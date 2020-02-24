// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Agent.Kubernetes.IntegrationTest
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using k8s.Models;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Agent.Docker;
    using Microsoft.Azure.Devices.Edge.Agent.Docker.Models;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes.EdgeDeployment;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes.EdgeDeployment.Deployment;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes.EdgeDeployment.Pvc;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes.EdgeDeployment.Service;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes.EdgeDeployment.ServiceAccount;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes.IntegrationTest.Client;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Edge.Util.Test.Common;
    using Xunit;
    using DockerEmptyStruct = global::Docker.DotNet.Models.EmptyStruct;

    [Integration]
    [Kubernetes]
    public class EdgeDeploymentControllerTest : IClassFixture<KubernetesClusterFixture>, IAsyncLifetime
    {
        readonly KubernetesClient client;

        static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

        public EdgeDeploymentControllerTest(KubernetesClusterFixture fixture)
        {
            string deviceNamespace = $"device-{Guid.NewGuid()}";
            this.client = new KubernetesClient(deviceNamespace, fixture.Client);
        }

        public async Task InitializeAsync() => await this.client.AddNamespaceAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task CheckIfCreateDeploymentIsSuccessfulWithNoResources()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, string.Empty, string.Empty);
            KubernetesModule km1 = this.CreateDefaultKubernetesModule(moduleName);
            moduleLifeCycleManager.SetModules(moduleName);

            await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Empty);

            this.AssertNoMatchingDeployments(deviceSelector, moduleName);
            this.AssertNoMatchingServiceAccounts(deviceSelector, moduleName);
            this.AssertNoServicesExist(deviceSelector);
            this.AssertNoPvcsExist();
        }

        [Fact]
        public async Task CheckIfCreateDeploymentNoServiceWithPvcIsSuccessful()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var persistentVolumeName = "pvname";
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, persistentVolumeName, "storagename");
            KubernetesModule km1 = this.CreateKubernetesModuleWithHostConfig(moduleName, persistentVolumeName);
            var pvcName = $"{moduleName}-{persistentVolumeName}";
            var tokenSource = new CancellationTokenSource(DefaultTimeout * 3);
            moduleLifeCycleManager.SetModules(moduleName);

            await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Empty);
            await this.client.WaitUntilAnyPersistentVolumeClaimAsync(tokenSource.Token);

            this.AssertNoMatchingDeployments(deviceSelector, moduleName);
            this.AssertNoMatchingServiceAccounts(deviceSelector, moduleName);
            this.AssertNoServicesExist(deviceSelector);
            this.AssertNoMatchingPvcs(pvcName);
        }

        [Fact]
        public async Task CheckIfCreateDeploymentWithServiceNoPvcIsSuccessful()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, string.Empty, string.Empty);
            KubernetesModule km1 = this.CreateKubernetesModuleWithExposedPorts(moduleName);
            moduleLifeCycleManager.SetModules(moduleName);

            await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Empty);

            this.AssertNoMatchingDeployments(deviceSelector, moduleName);
            this.AssertNoMatchingServiceAccounts(deviceSelector, moduleName);
            this.AssertNoMatchingService(deviceSelector, moduleName);
            this.AssertNoPvcsExist();
        }

        [Fact]
        public async Task CheckIfDeleteDeploymentIsSuccessful()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var persistentVolumeName = "pvname";
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, persistentVolumeName, "storagename");
            KubernetesModule km1 = this.CreateKubernetesModuleWithHostconfigAndExposedPorts(moduleName, persistentVolumeName);
            var labels = this.CreateDefaultLabels(moduleName);
            moduleLifeCycleManager.SetModules(moduleName);

            await this.client.AddModuleDeploymentAsync(moduleName, labels, null);
            await controller.DeployModulesAsync(ModuleSet.Empty, ModuleSet.Create(km1));

            this.AssertNoDeploymentsExist(deviceSelector);
            this.AssertNoServiceAccountsExist(deviceSelector);
            this.AssertNoServicesExist(deviceSelector);
            this.AssertNoPvcsExist();
        }

        [Fact]
        public async Task CheckIfDeploymentIsHealthyWithDeploymentDeletion()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, string.Empty, string.Empty);
            KubernetesModule km1 = this.CreateDefaultKubernetesModule(moduleName);
            var labels = this.CreateDefaultLabels(moduleName);
            moduleLifeCycleManager.SetModules(moduleName);

            await this.client.AddModuleDeploymentAsync(moduleName, labels, null);
            // await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Create(km1));
            this.client.DeleteDeployment(moduleName);
            await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Create(km1));

            this.AssertNoMatchingDeployments(deviceSelector, moduleName);
            this.AssertNoMatchingServiceAccounts(deviceSelector, moduleName);
            this.AssertNoServicesExist(deviceSelector);
            this.AssertNoPvcsExist();
        }

        [Fact]
        public async Task CheckIfDeploymentIsHealthyWithPvcDeletion()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var persistentVolumeName = "pvname";
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, persistentVolumeName, "storagename");
            KubernetesModule km1 = this.CreateKubernetesModuleWithHostConfig(moduleName, persistentVolumeName);
            var labels = this.CreateDefaultLabels(moduleName);
            var tokenSource = new CancellationTokenSource(DefaultTimeout * 3);
            var pvcName = $"{moduleName}-{persistentVolumeName}";
            moduleLifeCycleManager.SetModules(moduleName);

            await this.client.AddModuleDeploymentAsync(moduleName, labels, null);
            // await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Create(km1));
            await this.client.WaitUntilAnyPersistentVolumeClaimAsync(tokenSource.Token);
            this.client.DeletePvc(pvcName);
            await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Create(km1));

            this.AssertNoMatchingDeployments(deviceSelector, moduleName);
            this.AssertNoMatchingServiceAccounts(deviceSelector, moduleName);
            this.AssertNoServicesExist(deviceSelector);
            this.AssertNoMatchingPvcs(pvcName);
        }

        [Fact]
        public async Task CheckIfDeploymentIsHealthyWithServiceAccountDeletion()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, string.Empty, string.Empty);
            KubernetesModule km1 = this.CreateDefaultKubernetesModule(moduleName);
            var labels = this.CreateDefaultLabels(moduleName);
            moduleLifeCycleManager.SetModules(moduleName);

            await this.client.AddModuleDeploymentAsync(moduleName, labels, null);
            // await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Create(km1));
            this.client.DeleteServiceAccount(moduleName);
            await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Create(km1));

            this.AssertNoMatchingDeployments(deviceSelector, moduleName);
            this.AssertNoMatchingServiceAccounts(deviceSelector, moduleName);
            this.AssertNoServicesExist(deviceSelector);
            this.AssertNoPvcsExist();
        }

        [Fact]
        public async Task CheckIfDeploymentIsHealthyWithServiceDeletion()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, string.Empty, string.Empty);
            KubernetesModule km1 = this.CreateKubernetesModuleWithExposedPorts(moduleName);
            var labels = this.CreateDefaultLabels(moduleName);
            moduleLifeCycleManager.SetModules(moduleName);

            await this.client.AddModuleDeploymentAsync(moduleName, labels, null);
            // await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Create(km1));
            this.client.DeleteService(moduleName);
            await controller.DeployModulesAsync(ModuleSet.Create(km1), ModuleSet.Create(km1));

            this.AssertNoMatchingDeployments(deviceSelector, moduleName);
            this.AssertNoMatchingServiceAccounts(deviceSelector, moduleName);
            this.AssertNoMatchingService(deviceSelector, moduleName);
            this.AssertNoPvcsExist();
        }

        [Fact]
        public async Task CheckIfUpdateDeploymentWithAddedPvcIsSuccessful()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var persistentVolumeName = "pvname";
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, persistentVolumeName, "storagename");
            KubernetesModule km1 = this.CreateDefaultKubernetesModule(moduleName);
            KubernetesModule km1updated = this.CreateKubernetesModuleWithHostConfig(moduleName, persistentVolumeName);
            var labels = this.CreateDefaultLabels(moduleName);
            var pvcName = $"{moduleName}-{persistentVolumeName}";
            var tokenSource = new CancellationTokenSource(DefaultTimeout * 3);
            moduleLifeCycleManager.SetModules(moduleName);

            await this.client.AddModuleDeploymentAsync(moduleName, labels, null);
            await controller.DeployModulesAsync(ModuleSet.Create(km1updated), ModuleSet.Create(km1));
            await this.client.WaitUntilAnyPersistentVolumeClaimAsync(tokenSource.Token);

            this.AssertNoMatchingDeployments(deviceSelector, moduleName);
            this.AssertNoMatchingServiceAccounts(deviceSelector, moduleName);
            this.AssertNoServicesExist(deviceSelector);
            this.AssertNoMatchingPvcs(pvcName);
        }

        [Fact]
        public async Task CheckIfUpdateDeploymentWithAddedServiceIsSuccessful()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, string.Empty, string.Empty);
            KubernetesModule km1 = this.CreateDefaultKubernetesModule(moduleName);
            KubernetesModule km1updated = this.CreateKubernetesModuleWithExposedPorts(moduleName);
            var labels = this.CreateDefaultLabels(moduleName);
            moduleLifeCycleManager.SetModules(moduleName);

            await this.client.AddModuleDeploymentAsync(moduleName, labels, null);
            await controller.DeployModulesAsync(ModuleSet.Create(km1updated), ModuleSet.Create(km1));

            this.AssertNoMatchingDeployments(deviceSelector, moduleName);
            this.AssertNoMatchingServiceAccounts(deviceSelector, moduleName);
            this.AssertNoMatchingService(deviceSelector, moduleName);
            this.AssertNoPvcsExist();
        }

        [Fact]
        public async Task CheckIfUpdateDeploymentWithImageUpdateIsSuccessful()
        {
            var moduleName = "module-a";
            var deviceSelector = $"{Kubernetes.Constants.K8sEdgeDeviceLabel}=deviceid,{Kubernetes.Constants.K8sEdgeHubNameLabel}=hostname";
            var moduleLifeCycleManager = this.CreateModuleLifeCycleManager(moduleName);
            var controller = this.CreateDeploymentController(deviceSelector, moduleLifeCycleManager, string.Empty, string.Empty);
            KubernetesModule km1 = this.CreateDefaultKubernetesModule(moduleName);
            string newImage = "test-image:2";
            KubernetesModule km1UpdaModule = this.CreateKubernetesModuleWithImageName(moduleName, newImage);
            var labels = this.CreateDefaultLabels(moduleName);
            moduleLifeCycleManager.SetModules(moduleName);

            await this.client.AddModuleDeploymentAsync(moduleName, labels, null);
            // await controller.DeployModulesAsync(ModuleSet.Create(km1UpdaModule), ModuleSet.Create(km1));
            await this.client.ReplaceModuleImageAsync(moduleName, newImage);
            await controller.DeployModulesAsync(ModuleSet.Create(km1UpdaModule), ModuleSet.Create(km1));

            this.AssertNoMatchingDeployments(deviceSelector, moduleName);
            this.AssertNoMatchingServiceAccounts(deviceSelector, moduleName);
            this.AssertNoServicesExist(deviceSelector);
            this.AssertNoPvcsExist();
        }

        private EdgeDeploymentController CreateDeploymentController(string deviceSelector, IModuleIdentityLifecycleManager moduleLifeCycleManager, string persistentVolumeName, string storageClassName)
        {
            var resourceName = new ResourceName("hostname", "deviceid");
            var kubernetesServiceMapper = new KubernetesServiceMapper(PortMapServiceType.ClusterIP);
            string proxyImagePullSecretName = null;
            IDictionary<string, bool> experimentalFeatures = null;
            var deploymentMapper = new KubernetesDeploymentMapper(
                this.client.DeviceNamespace,
                "edgehub",
                "proxy",
                Option.Maybe(proxyImagePullSecretName),
                "configPath",
                "config-volume",
                "configMapName",
                "trustBundlePath",
                "trust-bundle-volume",
                "trustBundleConfigMapName",
                PortMapServiceType.ClusterIP,
                persistentVolumeName,
                storageClassName,
                Option.Some<uint>(100),
                "apiVersion",
                new Uri("http://localhost:35001"),
                new Uri("http://localhost:35000"),
                false,
                false,
                experimentalFeatures == null ? new Dictionary<string, bool>() : experimentalFeatures);
            var pvcMapper = new KubernetesPvcMapper(persistentVolumeName, storageClassName, 100);
            var serviceAccountMapper = new KubernetesServiceAccountMapper();
            return new EdgeDeploymentController(
                resourceName,
                deviceSelector,
                this.client.DeviceNamespace,
                this.client.Kubernetes,
                moduleLifeCycleManager,
                kubernetesServiceMapper,
                deploymentMapper,
                pvcMapper,
                serviceAccountMapper);
        }

        private KubernetesModule CreateDefaultKubernetesModule(string moduleName)
        {
            var createOptions = CreatePodParameters.Create();
            KubernetesConfig config = new KubernetesConfig("image", createOptions, Option.None<AuthConfig>());
            IModule m1 = new DockerModule(moduleName, "v1", ModuleStatus.Running, RestartPolicy.Always, new DockerConfig("test-image:1"), ImagePullPolicy.OnCreate, Core.Constants.DefaultPriority, null, null);
            return new KubernetesModule(m1, config, new KubernetesModuleOwner("v1", "Deployment", "iotedged", "123"));
        }

        private KubernetesModule CreateKubernetesModuleWithHostConfig(string moduleName, string persistentVolumeName)
        {
            var hostConfig = new HostConfig
            {
                Mounts = new List<Mount>
                {
                    new Mount
                    {
                        Type = "volume",
                        ReadOnly = true,
                        Source = persistentVolumeName,
                        Target = "/tmp/volume"
                    }
                }
            };
            var createOptions = CreatePodParameters.Create(hostConfig: hostConfig);
            KubernetesConfig config = new KubernetesConfig("image", createOptions, Option.None<AuthConfig>());
            IModule m1 = new DockerModule(moduleName, "v1", ModuleStatus.Running, RestartPolicy.Always, new DockerConfig("test-image:1"), ImagePullPolicy.OnCreate, Core.Constants.DefaultPriority, null, null);
            return new KubernetesModule(m1, config, new KubernetesModuleOwner("v1", "Deployment", "iotedged", "123"));
        }

        private KubernetesModule CreateKubernetesModuleWithExposedPorts(string moduleName)
        {
            var exposedPorts = new Dictionary<string, DockerEmptyStruct>
            {
                ["80/tcp"] = default
            };
            var createOptions = CreatePodParameters.Create(exposedPorts: exposedPorts);
            KubernetesConfig config = new KubernetesConfig("image", createOptions, Option.None<AuthConfig>());
            IModule m1 = new DockerModule(moduleName, "v1", ModuleStatus.Running, RestartPolicy.Always, new DockerConfig("test-image:1"), ImagePullPolicy.OnCreate, Core.Constants.DefaultPriority, null, null);
            return new KubernetesModule(m1, config, new KubernetesModuleOwner("v1", "Deployment", "iotedged", "123"));
        }

        private KubernetesModule CreateKubernetesModuleWithHostconfigAndExposedPorts(string moduleName, string persistentVolumeName)
        {
            var hostConfig = new HostConfig
            {
                Mounts = new List<Mount>
                {
                    new Mount
                    {
                        Type = "volume",
                        ReadOnly = true,
                        Source = persistentVolumeName,
                        Target = "/tmp/volume"
                    }
                }
            };
            var exposedPorts = new Dictionary<string, DockerEmptyStruct>
            {
                ["80/tcp"] = default
            };
            var createOptions = CreatePodParameters.Create(hostConfig: hostConfig, exposedPorts: exposedPorts);
            KubernetesConfig config = new KubernetesConfig("image", createOptions, Option.None<AuthConfig>());
            IModule m1 = new DockerModule(moduleName, "v1", ModuleStatus.Running, RestartPolicy.Always, new DockerConfig("test-image:1"), ImagePullPolicy.OnCreate, Core.Constants.DefaultPriority, null, null);
            return new KubernetesModule(m1, config, new KubernetesModuleOwner("v1", "Deployment", "iotedged", "123"));
        }

        private KubernetesModule CreateKubernetesModuleWithImageName(string moduleName, string newImage)
        {
            var createOptions = CreatePodParameters.Create();
            KubernetesConfig config = new KubernetesConfig("image", createOptions, Option.None<AuthConfig>());
            IModule m1 = new DockerModule(moduleName, "v1", ModuleStatus.Running, RestartPolicy.Always, new DockerConfig(newImage), ImagePullPolicy.OnCreate, Core.Constants.DefaultPriority, null, null);
            return new KubernetesModule(m1, config, new KubernetesModuleOwner("v1", "Deployment", "iotedged", "123"));
        }

        private Dictionary<string, string> CreateDefaultLabels(string moduleName)
        {
            return new Dictionary<string, string>
            {
                ["net.azure-devices.edge.deviceid"] = "deviceid",
                ["net.azure-devices.edge.hub"] = "hostname",
                ["net.azure-devices.edge.module"] = moduleName
            };
        }

        private DummyModuleIdentityLifecycleManager CreateModuleLifeCycleManager(string moduleName) => new DummyModuleIdentityLifecycleManager(
                "hostname",
                "gatewayhostname",
                "deviceid",
                moduleName,
                new ConnectionStringCredentials("connectionString"));

        private async void AssertNoDeploymentsExist(string deviceSelector)
        {
            V1DeploymentList currentDeployments = await this.client.ListDeploymentsAsync(deviceSelector);
            Assert.Empty(currentDeployments.Items);
        }

        private async void AssertNoMatchingDeployments(string deviceSelector, string moduleName)
        {
            V1DeploymentList currentDeployments = await this.client.ListDeploymentsAsync(deviceSelector);
            Assert.Single(currentDeployments.Items, d => d.Metadata.Name == moduleName);
        }

        private async void AssertNoServiceAccountsExist(string deviceSelector)
        {
            V1ServiceAccountList currentServiceAccounts = await this.client.ListServiceAccountsAsync(deviceSelector);
            Assert.Empty(currentServiceAccounts.Items);
        }

        private async void AssertNoMatchingServiceAccounts(string deviceSelector, string moduleName)
        {
            V1ServiceAccountList currentServiceAccounts = await this.client.ListServiceAccountsAsync(deviceSelector);
            Assert.Single(currentServiceAccounts.Items, sa => sa.Metadata.Name == moduleName);
        }

        private async void AssertNoServicesExist(string deviceSelector)
        {
            V1ServiceList currentServices = await this.client.ListServicesAsync(deviceSelector);
            Assert.Empty(currentServices.Items);
        }

        private async void AssertNoMatchingService(string deviceSelector, string moduleName)
        {
            V1ServiceList currentServiceList = await this.client.ListServicesAsync(deviceSelector);
            Assert.Single(currentServiceList.Items, s => s.Metadata.Name == moduleName);
        }

        private async void AssertNoPvcsExist()
        {
            V1PersistentVolumeClaimList currentPvcList = await this.client.ListPeristentVolumeClaimsAsync();
            Assert.Empty(currentPvcList.Items);
        }

        private async void AssertNoMatchingPvcs(string pvcName)
        {
            V1PersistentVolumeClaimList currentPvcList = await this.client.ListPeristentVolumeClaimsAsync();
            Assert.Single(currentPvcList.Items, p => p.Metadata.Name == pvcName);
        }
    }
}
