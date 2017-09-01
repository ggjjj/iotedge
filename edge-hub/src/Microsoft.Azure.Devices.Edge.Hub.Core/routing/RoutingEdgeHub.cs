﻿// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Hub.Core.Routing
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Cloud;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Device;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Routing.Core;
    using Microsoft.Extensions.Logging;
    using static System.FormattableString;
    using IIdentity = Microsoft.Azure.Devices.Edge.Hub.Core.IIdentity;
    using IMessage = Microsoft.Azure.Devices.Edge.Hub.Core.IMessage;
    using IRoutingMessage = Microsoft.Azure.Devices.Routing.Core.IMessage;

    public class RoutingEdgeHub : IEdgeHub
    {
        readonly Router router;
        readonly Core.IMessageConverter<IRoutingMessage> messageConverter;
        readonly IConnectionManager connectionManager;

        static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

        public RoutingEdgeHub(Router router, Core.IMessageConverter<IRoutingMessage> messageConverter, IConnectionManager connectionManager)
        {
            this.router = Preconditions.CheckNotNull(router, nameof(router));
            this.messageConverter = Preconditions.CheckNotNull(messageConverter, nameof(messageConverter));
            this.connectionManager = Preconditions.CheckNotNull(connectionManager, nameof(connectionManager));
        }

        public Task ProcessDeviceMessage(IIdentity identity, IMessage message)
        {
            message.SystemProperties[Edge.Hub.Core.SystemProperties.EdgeMessageId] = Guid.NewGuid().ToString();
            IRoutingMessage routingMessage = this.messageConverter.FromMessage(Preconditions.CheckNotNull(message, nameof(message)));
            return this.router.RouteAsync(routingMessage);
        }

        public Task ProcessDeviceMessageBatch(IIdentity identity, IEnumerable<IMessage> messages)
        {
            IEnumerable<IRoutingMessage> routingMessages = Preconditions.CheckNotNull(messages)
                .Select(m => this.messageConverter.FromMessage(m));
            return this.router.RouteAsync(routingMessages);
        }

        public Task<DirectMethodResponse> InvokeMethodAsync(IIdentity identity, DirectMethodRequest methodRequest)
        {
            Preconditions.CheckNotNull(methodRequest, nameof(methodRequest));

            Events.MethodCallReceived(identity, methodRequest.Id, methodRequest.CorrelationId);
            Option<IDeviceProxy> deviceProxy = this.connectionManager.GetDeviceConnection(methodRequest.Id);
            return deviceProxy.Match(
                dp => dp.InvokeMethodAsync(methodRequest),
                () => Task.FromResult(new DirectMethodResponse(null, null, (int)HttpStatusCode.NotFound)));
        }

        public Task UpdateReportedPropertiesAsync(IIdentity identity, IMessage reportedPropertiesMessage)
        {
            Preconditions.CheckNotNull(identity, nameof(identity));
            Preconditions.CheckNotNull(reportedPropertiesMessage, nameof(reportedPropertiesMessage));

            Option<ICloudProxy> cloudProxy = this.connectionManager.GetCloudConnection(identity.Id);
            Task cloudSendMessageTask = cloudProxy.Match(cp => cp.UpdateReportedPropertiesAsync(reportedPropertiesMessage), () => Task.CompletedTask);

            IRoutingMessage routingMessage = this.messageConverter.FromMessage(reportedPropertiesMessage);
            Task routingSendMessageTask = this.router.RouteAsync(routingMessage);

            return Task.WhenAll(cloudSendMessageTask, routingSendMessageTask);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.router?.Dispose();
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        static class Events
        {
            static readonly ILogger Log = Logger.Factory.CreateLogger<RoutingEdgeHub>();
            const int IdStart = HubCoreEventIds.RoutingEdgeHub;

            enum EventIds
            {
                MethodReceived = IdStart,                
            }

            public static void MethodCallReceived(IIdentity identity, string id, string correlationId)
            {
                Log.LogDebug((int)EventIds.MethodReceived, Invariant($"Received method invoke call from device/module {identity.Id} for {id} with correlation ID {correlationId}"));
            }
        }
    }
}