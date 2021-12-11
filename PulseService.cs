using Newtonsoft.Json;
using RestSharp;
using RestSharp.Serializers.NewtonsoftJson;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TeslaChargingManager.Pulse;

namespace TeslaChargingManager
{
    internal static class PulseService
    {
        private static DateTime accessTokenExpiresOn = DateTime.MinValue;
        private static string accessToken;
        private static AppSettings appSettings;
        private static RestClient client;

        internal static void Init(AppSettings _appSettings)
        {
            appSettings = _appSettings;
            client = new RestClient(appSettings.PulseUrl);
            RefreshAccessToken();
        }

        private static void RefreshAccessToken()
        {
            if (DateTime.UtcNow > accessTokenExpiresOn)
            {
                var refreshClient = new RestClient("https://cognito-idp.ap-southeast-2.amazonaws.com");
                refreshClient.AddDefaultHeader("X-Amz-Target", "AWSCognitoIdentityProviderService.InitiateAuth");
                refreshClient.AddDefaultHeader("Content-Type", "application/x-amz-json-1.1");
                var refreshRequest = new RestRequest(Method.POST);
                refreshRequest.AddJsonBody(new RefreshTokenRequest
                {
                    ClientId = appSettings.PulseClientId,
                    AuthParameters = new AuthParameters { REFRESH_TOKEN = appSettings.PulseRefreshToken }
                });
                var refreshResponse = refreshClient.Execute(refreshRequest);
                if (refreshResponse.IsSuccessful)
                {
                    var data = JsonConvert.DeserializeObject<RefreshTokenResponse>(refreshResponse.Content);
                    accessTokenExpiresOn = DateTime.UtcNow.AddSeconds(data.AuthenticationResult.ExpiresIn);
                    accessToken = data.AuthenticationResult.IdToken;                    
                    client.AddOrUpdateDefaultParameter(new Parameter("Authorization", $"Bearer {accessToken}", ParameterType.HttpHeader));
                }
            }
        }

        internal static UserModel GetUser()
        {
            RefreshAccessToken();
            var pulseRequest = new RestRequest($"prod/v1/user");
            var pulseResponse = client.Get<UserModel>(pulseRequest);
            return pulseResponse.Data;
        }

        internal static SiteModel GetSite(int siteId)
        {
            RefreshAccessToken();
            var pulseRequest = new RestRequest($"prod/v1/sites/{siteId}");
            var pulseResponse = client.Get<SiteModel>(pulseRequest);
            return pulseResponse.Data;
        }

        internal static SummaryModel GetLiveSummary(int siteId)
        {
            RefreshAccessToken();
            var pulseRequest = new RestRequest($"prod/v1/sites/{siteId}/live_data_summary");
            var pulseResponse = client.Get<SummaryModel>(pulseRequest);
            return pulseResponse.Data;
        }

    }
}
