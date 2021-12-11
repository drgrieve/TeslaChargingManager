using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeslaChargingManager.Pulse
{
    public class Appliance
    {
        public string assignment { get; set; }
        public string display_name { get; set; }
        public double power { get; set; }
    }

    public class Weather
    {
        public string datetime { get; set; }
        public string condition { get; set; }
        public string description { get; set; }
        public int temperature { get; set; }
        public string icon { get; set; }
        public int id { get; set; }
        public bool daytime { get; set; }
    }

    public class SummaryModel
    {
        public double grid { get; set; }
        public double consumption { get; set; }
        public List<Appliance> appliances { get; set; }
        public double tariff_rate { get; set; }
        public double self_powered_fraction { get; set; }
        public string last_updated { get; set; }
        public double solar { get; set; }
        public Weather weather { get; set; }
    }

    public class Device
    {
        public int id { get; set; }
        public string manufacturer { get; set; }
        public string model { get; set; }
        public string device_type { get; set; }
        public object operating_state { get; set; }
    }

    public class SiteModel
    {
        public string site_name { get; set; }
        public int tenant_id { get; set; }
        public int tariff_id { get; set; }
        public double lat { get; set; }
        public double lon { get; set; }
        public string city { get; set; }
        public string post_code { get; set; }
        public string state { get; set; }
        public string country { get; set; }
        public string address { get; set; }
        public string timezone { get; set; }
        public string known_tariff_date { get; set; }
        public int site_id { get; set; }
        public List<Device> devices { get; set; }
        public string monitoring_start { get; set; }
        public bool has_ac_battery { get; set; }
        public bool has_dc_battery { get; set; }

        public Location location => new Location { Latitude = lat, Longitude = lon };
    }

    public class UserModel
    {
        public int user_id { get; set; }
        public int tenant_id { get; set; }
        public string first_name { get; set; }
        public string last_name { get; set; }
        public string email { get; set; }
        public string phone { get; set; }
        public List<int> site_ids { get; set; }
        public string role { get; set; }
        public string send_bills_action { get; set; }
    }



    public class AuthParameters
    {
        public string REFRESH_TOKEN { get; set; }
    }

    public class RefreshTokenRequest
    {
        public string ClientId { get; set; }
        public string AuthFlow { get; set; } = "REFRESH_TOKEN_AUTH";
        public AuthParameters AuthParameters { get; set; }
    }

    public class AuthenticationResult
    {
        public string AccessToken { get; set; }
        public int ExpiresIn { get; set; }
        public string IdToken { get; set; }
        public string TokenType { get; set; }
    }

    public class ChallengeParameters
    {
    }

    public class RefreshTokenResponse
    {
        public AuthenticationResult AuthenticationResult { get; set; }
        public ChallengeParameters ChallengeParameters { get; set; }
    }
}
