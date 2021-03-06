// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Hub.CloudProxy
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Exceptions;
    using Microsoft.Azure.Devices.Edge.Util;

    public class ModuleAuthentication : ModuleAuthenticationWithTokenRefresh
    {
        readonly ITokenProvider tokenProvider;

        public ModuleAuthentication(ITokenProvider tokenProvider, string deviceId, string moduleId)
            : base(deviceId, moduleId)
        {
            this.tokenProvider = Preconditions.CheckNotNull(tokenProvider);
        }

        protected override Task<string> SafeCreateNewToken(string iotHub, int suggestedTimeToLive)
        {
            try
            {
                return this.tokenProvider.GetTokenAsync(Option.Some(TimeSpan.FromSeconds(suggestedTimeToLive)));
            }
            catch (TokenProviderException ex)
            {
                // ModuleAuthentication plugs into the device SDK, and we don't
                // want to leak our internal exceptions into the device SDK
                throw new IotHubCommunicationException(ex.Message, ex);
            }
        }
    }
}
