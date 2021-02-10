// Copyright (c) Microsoft. All rights reserved.
namespace IotEdgeQuickstart.Details
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Test.Common;
    using Microsoft.Azure.Devices.Edge.Util;

    public class HttpUris
    {
        const int ManagementPort = 15580;
        const int WorkloadPort = 15581;

        public HttpUris()
            : this(GetIpAddress())
        {
        }

        public HttpUris(string hostname)
        {
            this.ConnectManagement = $"http://{hostname}:{ManagementPort}";
            this.ConnectWorkload = $"http://{hostname}:{WorkloadPort}";
            this.ListenManagement = $"http://0.0.0.0:{ManagementPort}";
            this.ListenWorkload = $"http://0.0.0.0:{WorkloadPort}";
        }

        public string ConnectManagement { get; }

        public string ConnectWorkload { get; }

        public string ListenManagement { get; }

        public string ListenWorkload { get; }

        static string GetIpAddress()
        {
            // TODO: should use an internal IP address--e.g. docker0's address--instead
            //       of the public-facing address. The output of this command would be
            //       a good candidate:
            //       docker network inspect --format='{{(index .IPAM.Config 0).Gateway}}' bridge
            const string Server = "microsoft.com";
            const int Port = 443;

            IPHostEntry entry = Dns.GetHostEntry(Server);

            foreach (IPAddress address in entry.AddressList)
            {
                var endpoint = new IPEndPoint(address, Port);
                using (var s = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
                {
                    s.Connect(endpoint);
                    if (s.Connected)
                    {
                        return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
                    }
                }
            }

            return string.Empty;
        }
    }

    class IotedgedLinux : IBootstrapper
    {
        readonly string archivePath;
        readonly Option<RegistryCredentials> credentials;
        readonly Option<HttpUris> httpUris;
        readonly UriSocks uriSocks;
        readonly Option<string> proxy;
        readonly Option<UpstreamProtocolType> upstreamProtocol;
        readonly bool requireEdgeInstallation;
        readonly bool overwritePackages;

        public IotedgedLinux(string archivePath, Option<RegistryCredentials> credentials, Option<HttpUris> httpUris, UriSocks uriSocks, Option<string> proxy, Option<UpstreamProtocolType> upstreamProtocol, bool requireEdgeInstallation, bool overwritePackages)
        {
            this.archivePath = archivePath;
            this.credentials = credentials;
            this.httpUris = httpUris;
            this.uriSocks = uriSocks;
            this.proxy = proxy;
            this.upstreamProtocol = upstreamProtocol;
            this.requireEdgeInstallation = requireEdgeInstallation;
            this.overwritePackages = overwritePackages;
        }

        public async Task UpdatePackageState()
        {
            string[] packages = new string[] { "aziot-edge", "aziot-identity-service", "iotedge", "libiothsm-std" };

            foreach (string package in packages)
            {
                try
                {
                    await Process.RunAsync("bash", $"-c \"dpkg -l | grep {package}\"");

                    if (this.overwritePackages)
                    {
                        Console.WriteLine($"{package}: found. Removing package.");
                        await Process.RunAsync("apt", $"purge -y {package}");
                    }
                    else
                    {
                        throw new Exception($"{package}: found. Not overwriting existing packages.");
                    }
                }
                catch (Win32Exception)
                {
                    Console.WriteLine($"{package}: not found.");
                }
            }
        }

        public Task VerifyDependenciesAreInstalled() => Task.CompletedTask;

        public async Task VerifyModuleIsRunning(string name)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20))) // This long timeout is needed for resource constrained devices pulling the large tempFilterFunctions image
            {
                string errorMessage = null;

                try
                {
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

                        string options = this.httpUris.Match(uris => $"-H {uris.ConnectManagement} ", () => string.Empty);

                        try
                        {
                            string[] result = await Process.RunAsync(
                                "iotedge",
                                $"{options}list",
                                cts.Token);

                            string status = result
                                .Where(ln => ln.Split(null as char[], StringSplitOptions.RemoveEmptyEntries).First() == name)
                                .DefaultIfEmpty("name status")
                                .Single()
                                .Split(null as char[], StringSplitOptions.RemoveEmptyEntries)
                                .ElementAt(1); // second column is STATUS

                            if (status == "running")
                            {
                                break;
                            }

                            errorMessage = "Not found";
                        }
                        catch (Win32Exception e)
                        {
                            Console.WriteLine($"Error searching for {name} module: {e.Message}. Retrying.");
                        }
                    }
                }
                catch (OperationCanceledException e)
                {
                    throw new Exception($"Error searching for {name} module: {errorMessage ?? e.Message}");
                }
                catch (Exception e)
                {
                    throw new Exception($"Error searching for {name} module: {e.Message}");
                }
            }
        }

        public Task Install()
        {
            if (this.requireEdgeInstallation)
            {
                string[] packages = Directory.GetFiles(this.archivePath, "*.deb");

                foreach (string package in packages)
                {
                    Console.WriteLine($"Will install {package}");
                }

                string packageArguments = string.Join(" ", packages);

                return Process.RunAsync(
                    "apt-get",
                    $"install -y {packageArguments}",
                    300); // 5 min timeout because install can be slow on raspberry pi
            }
            else
            {
                Console.WriteLine("Skipping installation of aziot-edge and aziot-identity-service.");

                return Task.CompletedTask;
            }
        }

        private static IConfigDocument InitDocument(string template, bool toml)
        {
            string text = File.ReadAllText(template);

            if (toml)
            {
                return new TomlDocument(text);
            }

            return new YamlDocument(text);
        }

        public async Task Configure(
            DeviceProvisioningMethod method,
            Option<string> agentImage,
            string hostname,
            Option<string> parentHostname,
            string deviceCaCert,
            string deviceCaPk,
            string deviceCaCerts,
            LogLevel runtimeLogLevel)
        {
            agentImage.ForEach(
                image =>
                {
                    Console.WriteLine($"Setting up aziot-edged with agent image {image}");
                },
                () =>
                {
                    Console.WriteLine("Setting up aziot-edged with agent image 1.0");
                });

            const string KEYD = "/etc/aziot/keyd/config.toml";
            const string CERTD = "/etc/aziot/certd/config.toml";
            const string IDENTITYD = "/etc/aziot/identityd/config.toml";
            const string EDGED = "/etc/aziot/edged/config.yaml";

            // Initialize each service's config file.
            // The mapped values are:
            // - Path to the config file (/etc/aziot/[service_name]/config.[toml | yaml])
            // - User owning the config file
            // - Template used to generate the config file.
            Dictionary<string, (string owner, IConfigDocument document)> config = new Dictionary<string, (string, IConfigDocument)>();
            config.Add(KEYD, ("aziotks", InitDocument(KEYD + ".default", true)));
            config.Add(CERTD, ("aziotcs", InitDocument(CERTD + ".default", true)));
            config.Add(IDENTITYD, ("aziotid", InitDocument(IDENTITYD + ".default", true)));
            config.Add(EDGED, ("iotedge", InitDocument(EDGED + ".template", false)));

            // Directory for storing keys; create it if it doesn't exist.
            string keyDir = "/var/secrets/aziot/keyd/";
            Directory.CreateDirectory(keyDir);
            SetOwner(keyDir, config[KEYD].owner, "700");

            // Need to always reprovision so previous test runs don't affect this one.
            config[IDENTITYD].document.RemoveIfExists("provisioning");
            config[IDENTITYD].document.ReplaceOrAdd("provisioning.always_reprovision_on_startup", true);

            method.ManualConnectionString.Match(
                cs =>
                {
                    string keyPath = Path.Combine(keyDir, "device-id");
                    config[IDENTITYD].document.ReplaceOrAdd("provisioning.source", "manual");
                    config[IDENTITYD].document.ReplaceOrAdd("provisioning.authentication.method", "sas");
                    config[IDENTITYD].document.ReplaceOrAdd("provisioning.authentication.device_id_pk", "device-id");
                    config[KEYD].document.ReplaceOrAdd("preloaded_keys.device-id", $"file://{keyPath}");

                    string[] segments = cs.Split(";");

                    foreach (string s in segments)
                    {
                        string[] param = s.Split("=", 2);

                        switch (param[0])
                        {
                            case "HostName":
                                // replace IoTHub hostname with parent hostname for nested edge
                                config[IDENTITYD].document.ReplaceOrAdd("provisioning.iothub_hostname", parentHostname.GetOrElse(param[1]));
                                break;
                            case "SharedAccessKey":
                                File.WriteAllBytes(keyPath, Convert.FromBase64String(param[1]));
                                SetOwner(keyPath, config[KEYD].owner, "600");
                                break;
                            case "DeviceId":
                                config[IDENTITYD].document.ReplaceOrAdd("provisioning.device_id", param[1]);
                                break;
                            default:
                                break;
                        }
                    }

                    return string.Empty;
                },
                () =>
                {
                    config[IDENTITYD].document.RemoveIfExists("provisioning");
                    return string.Empty;
                });

            method.Dps.ForEach(
                dps =>
                {
                    config[IDENTITYD].document.ReplaceOrAdd("provisioning.source", "dps");
                    config[IDENTITYD].document.ReplaceOrAdd("provisioning.global_endpoint", dps.EndPoint);
                    config[IDENTITYD].document.ReplaceOrAdd("provisioning.scope_id", dps.ScopeId);
                    switch (dps.AttestationType)
                    {
                        case DPSAttestationType.SymmetricKey:
                            string dpsKeyPath = Path.Combine(keyDir, "device-id");
                            string dpsKey = dps.SymmetricKey.Expect(() => new ArgumentException("Expected symmetric key"));

                            File.WriteAllBytes(dpsKeyPath, Convert.FromBase64String(dpsKey));
                            SetOwner(dpsKeyPath, config[KEYD].owner, "600");

                            config[IDENTITYD].document.ReplaceOrAdd("provisioning.attestation.method", "symmetric_key");
                            config[IDENTITYD].document.ReplaceOrAdd("provisioning.attestation.symmetric_key", "device-id");

                            break;
                        case DPSAttestationType.X509:
                            string certPath = dps.DeviceIdentityCertificate.Expect(() => new ArgumentException("Expected path to identity certificate"));
                            string keyPath = dps.DeviceIdentityPrivateKey.Expect(() => new ArgumentException("Expected path to identity private key"));

                            SetOwner(certPath, config[CERTD].owner, "444");
                            SetOwner(keyPath, config[KEYD].owner, "400");

                            config[CERTD].document.ReplaceOrAdd("preloaded_certs.device-id", new Uri(certPath).AbsoluteUri);
                            config[KEYD].document.ReplaceOrAdd("preloaded_keys.device-id", new Uri(keyPath).AbsoluteUri);

                            config[IDENTITYD].document.ReplaceOrAdd("provisioning.attestation.method", "x509");
                            config[IDENTITYD].document.ReplaceOrAdd("provisioning.attestation.identity_cert", "device-id");
                            config[IDENTITYD].document.ReplaceOrAdd("provisioning.attestation.identity_pk", "device-id");
                            break;
                        default:
                            break;
                    }

                    dps.RegistrationId.ForEach(id => { config[IDENTITYD].document.ReplaceOrAdd("provisioning.attestation.registration_id", id); });
                });

            agentImage.ForEach(image =>
            {
                config[EDGED].document.ReplaceOrAdd("agent.config.image", image);
            });

            config[EDGED].document.ReplaceOrAdd("hostname", hostname);
            config[IDENTITYD].document.ReplaceOrAdd("hostname", hostname);

            parentHostname.ForEach(v => config[EDGED].document.ReplaceOrAdd("parent_hostname", v));

            foreach (RegistryCredentials c in this.credentials)
            {
                config[EDGED].document.ReplaceOrAdd("agent.config.auth.serveraddress", c.Address);
                config[EDGED].document.ReplaceOrAdd("agent.config.auth.username", c.User);
                config[EDGED].document.ReplaceOrAdd("agent.config.auth.password", c.Password);
            }

            config[EDGED].document.ReplaceOrAdd("agent.env.RuntimeLogLevel", runtimeLogLevel.ToString());

            if (this.httpUris.HasValue)
            {
                HttpUris uris = this.httpUris.OrDefault();
                config[EDGED].document.ReplaceOrAdd("connect.management_uri", uris.ConnectManagement);
                config[EDGED].document.ReplaceOrAdd("connect.workload_uri", uris.ConnectWorkload);
                config[EDGED].document.ReplaceOrAdd("listen.management_uri", uris.ListenManagement);
                config[EDGED].document.ReplaceOrAdd("listen.workload_uri", uris.ListenWorkload);
            }
            else
            {
                UriSocks socks = this.uriSocks;
                config[EDGED].document.ReplaceOrAdd("connect.management_uri", socks.ConnectManagement);
                config[EDGED].document.ReplaceOrAdd("connect.workload_uri", socks.ConnectWorkload);
                config[EDGED].document.ReplaceOrAdd("listen.management_uri", socks.ListenManagement);
                config[EDGED].document.ReplaceOrAdd("listen.workload_uri", socks.ListenWorkload);
            }

            // Clear any existing Identity Service principals.
            string principalsPath = "/etc/aziot/identityd/config.d";

            config[IDENTITYD].document.RemoveIfExists("principal");
            if (Directory.Exists(principalsPath))
            {
                Directory.Delete(principalsPath, true);
            }

            Directory.CreateDirectory(principalsPath);
            SetOwner(principalsPath, "aziotid", "755");

            // Add the principal entry for aziot-edge to Identity Service.
            // This is required so aziot-edge can communicate with Identity Service.
            uint iotedgeUid = await GetIotedgeUid();
            AddPrincipal("aziot-edge", iotedgeUid);

            foreach (string file in new string[] { deviceCaCert, deviceCaPk, deviceCaCerts })
            {
                if (string.IsNullOrEmpty(file))
                {
                    throw new ArgumentException("device_ca_cert, device_ca_pk, and trusted_ca_certs must all be provided.");
                }

                if (!File.Exists(file))
                {
                    throw new ArgumentException($"{file} does not exist.");
                }
            }

            // Files must be readable by KS and CS users.
            SetOwner(deviceCaCerts, config[CERTD].owner, "444");
            SetOwner(deviceCaCert, config[CERTD].owner, "444");
            SetOwner(deviceCaPk, config[KEYD].owner, "400");

            config[CERTD].document.ReplaceOrAdd("preloaded_certs.aziot-edged-trust-bundle", new Uri(deviceCaCerts).AbsoluteUri);
            config[CERTD].document.ReplaceOrAdd("preloaded_certs.aziot-edged-ca", new Uri(deviceCaCert).AbsoluteUri);
            config[KEYD].document.ReplaceOrAdd("preloaded_keys.aziot-edged-ca", new Uri(deviceCaPk).AbsoluteUri);

            this.proxy.ForEach(proxy => config[EDGED].document.ReplaceOrAdd("agent.env.https_proxy", proxy));

            this.upstreamProtocol.ForEach(upstreamProtocol => config[EDGED].document.ReplaceOrAdd("agent.env.UpstreamProtocol", upstreamProtocol.ToString()));

            foreach (KeyValuePair<string, (string owner, IConfigDocument document)> service in config)
            {
                string path = service.Key;
                string text = service.Value.document.ToString();

                await File.WriteAllTextAsync(path, text);
                SetOwner(path, service.Value.owner, "644");
                Console.WriteLine($"Created config {path}");
            }
        }

        public async Task Start()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            {
                await Process.RunAsync("systemctl", "restart aziot-keyd aziot-certd aziot-identityd aziot-edged", cts.Token);
                Console.WriteLine("Waiting for aziot-edged to start up.");

                // Waiting for the processes to enter the "Running" state doesn't guarantee that
                // they are fully started and ready to accept requests. Therefore, this function
                // must wait until a request can be processed.
                while (true)
                {
                    var processInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "iotedge",
                        Arguments = "list",
                        RedirectStandardOutput = true
                    };
                    var request = System.Diagnostics.Process.Start(processInfo);

                    if (request.WaitForExit(1000))
                    {
                        if (request.ExitCode == 0)
                        {
                            request.Close();
                            Console.WriteLine("aziot-edged ready for requests.");
                            break;
                        }
                    }
                    else
                    {
                        request.Kill(true);
                        request.WaitForExit();
                        request.Close();
                        Console.WriteLine("aziot-edged not yet ready.");
                    }
                }
            }
        }

        public async Task Stop()
        {
            await Process.RunAsync("systemctl", "stop aziot-edged", 60);
            await Process.RunAsync("systemctl", "stop aziot-identityd", 60);
            await Process.RunAsync("systemctl", "stop aziot-tpmd", 60);
            await Process.RunAsync("systemctl", "stop aziot-certd", 60);
            await Process.RunAsync("systemctl", "stop aziot-keyd", 60);
        }

        public Task Reset() => Task.CompletedTask;

        private static async Task<uint> GetIotedgeUid()
        {
            string[] output = await Process.RunAsync("id", "-u iotedge");
            string uid = output[0].Trim();

            return System.Convert.ToUInt32(uid, 10);
        }

        private static void SetOwner(string path, string owner, string permissions)
        {
            var chown = System.Diagnostics.Process.Start("chown", $"{owner}:{owner} {path}");
            chown.WaitForExit();
            chown.Close();

            var chmod = System.Diagnostics.Process.Start("chmod", $"{permissions} {path}");
            chmod.WaitForExit();
            chmod.Close();
        }

        private static void AddPrincipal(string name, uint uid, string[] type = null, Dictionary<string, string> opts = null)
        {
            string path = $"/etc/aziot/identityd/config.d/{name}-principal.toml";

            string principal = string.Join(
                "\n",
                "[[principal]]",
                $"uid = {uid}",
                $"name = \"{name}\"");

            if (type != null)
            {
                // Need to quote each type.
                for (int i = 0; i < type.Length; i++)
                {
                    type[i] = $"\"{type[i]}\"";
                }

                string types = string.Join(", ", type);
                principal = string.Join("\n", principal, $"idtype = [{types}]");
            }

            if (opts != null)
            {
                foreach (KeyValuePair<string, string> opt in opts)
                {
                    principal = string.Join("\n", principal, $"{opt.Key} = {opt.Value}");
                }
            }

            File.WriteAllText(path, principal);
            SetOwner(path, "aziotid", "644");
        }
    }
}
