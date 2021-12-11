using HtmlAgilityPack;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using RestSharp;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace TeslaChargingManager
{
    class Program
    {
        static AppSettings appSettings = new AppSettings();
        static async Task Main(string[] args)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
            var configuration = builder.Build();
            ConfigurationBinder.Bind(configuration.GetSection("AppSettings"), appSettings);

            // See https://aka.ms/new-console-template for more information
            Console.WriteLine($"Welcome to Telsa Charging Manager{Environment.NewLine}");
            Console.WriteLine("Enter a command or /help for instructions");
            bool quitNow = false;
            while (!quitNow)
            {
                var command = Console.ReadLine();
                switch (command)
                {
                    case "/help":
                        Console.WriteLine("/login = Instructions to generate a new Tesla Access token");
                        Console.WriteLine("/charge = Charge vehicle with excess solar");
                        Console.WriteLine("/quit = Exit program");
                        break;

                    case "/login":
                        Login();
                        break;

                    case "/charge":
                        await Charge();
                        break;

                    case "/quit":
                        quitNow = true;
                        break;

                    default:
                        Console.WriteLine($"Unknown Command {command}. Try /help");
                        break;
                }
            }

        }

        private static CancellationTokenSource cts;
        private static bool isInChargingLoop;

        static async Task Charge()
        {
            Console.WriteLine("Charging Tesla from Solar");

            if (String.IsNullOrEmpty(appSettings.TeslaAccessToken))
            {
                Console.WriteLine("No Tesla token. Use /login");
                return;
            }
            if (String.IsNullOrEmpty(appSettings.PulseRefreshToken))
            {
                Console.WriteLine("No Pulse token.");
                return;
            }

            cts = new CancellationTokenSource();
            var token = cts.Token;

            await Task.Run(() => ChargeLoop(token));

            isInChargingLoop = true;
            while (!(Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) && isInChargingLoop)
            {
                Thread.Sleep(500);
            }
            cts.Cancel();
            Console.WriteLine("Charge monitoring has stopped");
        }

        static async void ChargeLoop(CancellationToken token)
        {
            TeslaService.Init(appSettings);
            PulseService.Init(appSettings);

            var pulseUser = PulseService.GetUser();
            var siteId = pulseUser.site_ids.First();
            var pulseSite = PulseService.GetSite(siteId);
            var pulseData = PulseService.GetLiveSummary(siteId);
            Console.WriteLine($"Current conditions is {pulseData.weather.temperature}c and {pulseData.weather.description}");
            Console.WriteLine($"Current solar production is {pulseData.solar}kw");

            if (pulseData.solar <= 0 || !pulseData.weather.daytime)
            {
                Console.Write($"Solar not currently producing {pulseData.solar} or is night time");
                isInChargingLoop = false;
                return;
            }

            var vehicles = await TeslaService.Vehicles();
            if (vehicles.count == 0)
            {
                Console.Write("No Tesla vehicles found");
                return;
            }

            Tesla.VehicleModel? vehicle = null;
            var distance = int.MaxValue;
            foreach(var v in vehicles.response)
            {
                var driveState = await TeslaService.DriveState(v.id);
                var d = CalculateDistance(driveState.location, pulseSite.location);
                if (d < 100)
                {
                    if (driveState.speed != null)
                    {
                        Console.Write($"Vehicle is moving at speed {driveState.speed}");
                        isInChargingLoop = false;
                        return;
                    }
                    vehicle = v;
                    break;
                }
                if (distance > d) distance = (int)d;
            }
            if (vehicle == null)
            {
                Console.WriteLine($"No Tesla vehicles found within 100m of {pulseSite.address}. Closest is {distance}m");
                isInChargingLoop = false;
                return;
            }

            Console.WriteLine($"Tesla vehicle found: {vehicle.display_name} and is {vehicle.state}");

            TeslaService.SetVehicleId(vehicle.id);

            Console.WriteLine("Press ESC to stop monitoring");

            var sustainedDrawDuration = 0;
            var notChargingDuration = 0;
            while (true)
            {
                if (token.IsCancellationRequested) break;

                pulseData = PulseService.GetLiveSummary(siteId);
                var grid = pulseData.grid;

                Console.WriteLine($"Grid ({DateTime.Now:s}): {grid}");

                var sustainedDraw = false;

                if (grid + appSettings.GridBuffer > 0)
                {
                    var reducedChargeBy = await TeslaService.ChargeDelta(grid + appSettings.GridBuffer);
                    if (Double.IsNaN(reducedChargeBy))
                    {
                        Console.WriteLine("Monitor stopped due to failure.");
                        isInChargingLoop = false;
                        break;
                    }
                    if (reducedChargeBy == 0)
                    {
                        if (grid > appSettings.GridMaxDraw && TeslaService.chargeState.charging_state == "Charging")
                        {
                            await TeslaService.StopCharging($"grid draw over grid draw max of {appSettings.GridMaxDraw}");
                        }
                        else if (grid > appSettings.GridMaxSustainedDraw && TeslaService.chargeState.charging_state == "Charging")
                        {
                            sustainedDraw = true;
                        }
                    }
                }
                else 
                {
                    var increaseChargeBy = await TeslaService.ChargeDelta(grid + appSettings.GridBuffer);
                    if (Double.IsNaN(increaseChargeBy))
                    {
                        Console.WriteLine("Monitor stopped due to failure.");
                        isInChargingLoop = false;
                        break;
                    }
                }

                sustainedDrawDuration = sustainedDraw ? sustainedDrawDuration += appSettings.LoopSleepDuration : 0;
                if (sustainedDrawDuration >= appSettings.SustainedDrawDuration)
                {
                    await TeslaService.StopCharging($"grid draw over grid sustained draw limit of {appSettings.GridMaxSustainedDraw} for {sustainedDrawDuration} seconds");
                }
                else if (sustainedDrawDuration > 0)
                {
                    Console.WriteLine($"Sustained draw {sustainedDrawDuration}/{appSettings.SustainedDrawDuration}");
                }

                notChargingDuration = TeslaService.chargeState.charging_state == "Charging" ? 0 : notChargingDuration += appSettings.LoopSleepDuration;
                if (notChargingDuration >= appSettings.NotChargingDuration)
                {
                    Console.WriteLine($"Not charging duration limit of {appSettings.NotChargingDuration} reached.");
                    isInChargingLoop = false;
                    break;
                }
                if (notChargingDuration > 0 && notChargingDuration % 300 == 0)
                {
                    Console.WriteLine($"Not charging duration {notChargingDuration}/{appSettings.NotChargingDuration}");
                }

                Thread.Sleep(appSettings.LoopSleepDuration * 1000);
            }
        }

        static void Login()
        {
            //Console.WriteLine($"To login you will need to enter your Tesla Email and Password.{Environment.NewLine}A token that is valid for 45 days will be displayed.{Environment.NewLine}Copy and enter this token into the appSettings.json file.");
            //Console.WriteLine("Enter email");
            //var email = Console.ReadLine() ?? "";
            //Console.WriteLine("Enter password");
            //var password = Console.ReadLine() ?? "";

            var codeVerifier = RandomAlpaNumericString(86);
            var codeChallenge = Convert.ToBase64String(Encoding.UTF8.GetBytes(ComputeSha256Hash(codeVerifier)));
            var state = "tcm";

            var client = new RestClient("https://auth.tesla.com");
            //var loginPageRequest = new RestRequest("oauth2/v3/authorize", Method.GET);
            //loginPageRequest.AddParameter("client_id", "ownerapi");
            //loginPageRequest.AddParameter("code_challenge", codeChallenge);
            //loginPageRequest.AddParameter("code_challenge_method", "S256");
            //loginPageRequest.AddParameter("redirect_uri", "https://auth.tesla.com/void/callback");
            //loginPageRequest.AddParameter("response_type", "code");
            //loginPageRequest.AddParameter("scope", "openid email offline_access");
            //loginPageRequest.AddParameter("state", state);
            //var loginPageResponse = client.Execute(loginPageRequest);
            //var cookies = loginPageResponse.Cookies;

            //var doc = new HtmlDocument();
            //doc.LoadHtml(loginPageResponse.Content);
            //var hiddenInputs = doc.DocumentNode.Descendants("form").First().Descendants("input").Where(x => x.GetAttributeValue<string>("type", "") == "hidden").ToList();

            //var loginRequest = new RestRequest("oauth2/v3/authorize", Method.POST);
            //foreach (var cookie in cookies) loginRequest.AddCookie(cookie.Name, cookie.Value);
            //loginRequest.AddQueryParameter("client_id", "ownerapi");
            //loginRequest.AddQueryParameter("code_challenge", codeChallenge);
            //loginRequest.AddQueryParameter("code_challenge_method", "S256");
            //loginRequest.AddQueryParameter("redirect_uri", "https://auth.tesla.com/void/callback");
            //loginRequest.AddQueryParameter("response_type", "code");
            //loginRequest.AddQueryParameter("scope", "openid email offline_access");
            //loginRequest.AddQueryParameter("state", state);

            //foreach(var hiddenInput in hiddenInputs) loginRequest.AddParameter(hiddenInput.GetAttributeValue<string>("name", ""), hiddenInput.GetAttributeValue<string>("value", ""));
            //loginRequest.AddParameter("identity", email);
            //loginRequest.AddParameter("credential", password);
            //var loginResponse = client.Execute(loginRequest);
            //var location = loginResponse.Headers.First(x => x.Name == "Location").Value.ToString();

            Console.WriteLine($"Copy the following link to your browser and login to Tesla using your email and password as normal.{Environment.NewLine}");

            var queryString = new Dictionary<string, string>()
            {
                {"client_id", "ownerapi" },
                {"code_challenge", codeChallenge },
                {"code_challenge_method","S256" },
                {"redirect_uri","https://auth.tesla.com/void/callback" },
                {"response_type","code" },
                {"scope","openid email offline_access" },
                {"state", state }
            };
            var loginPageUrl = QueryHelpers.AddQueryString("https://auth.tesla.com/oauth2/v3/authorize", queryString);
            Console.WriteLine(loginPageUrl);

            Console.WriteLine($"{Environment.NewLine}Copy and paste the response link here.{Environment.NewLine}");

            var location = Console.ReadLine() ?? "";
            var locationUri = new Uri(location);
            var locationData = HttpUtility.ParseQueryString(locationUri.Query);
            if (locationData["state"] != state)
            {
                Console.WriteLine($"State was '{locationData["state"]}'. Expected '{state}'.");
                return;
            }

            var code = locationData["code"];

            var tokenRequest = new RestRequest("oauth2/v3/token", Method.POST);
            tokenRequest.AddJsonBody(new
            {
                grant_type = "authorization_code",
                client_id = "ownerapi",
                code = code,
                code_verifier = codeVerifier,
                redirect_uri = "https://auth.tesla.com/void/callback"
            });
            var tokenResponse = client.Execute<TokenResponseModel>(tokenRequest);
            if (!tokenResponse.IsSuccessful)
            {
                Console.WriteLine($"Error: {tokenResponse.ErrorMessage}");
                return;
            }
            var token = tokenResponse.Data.access_token;

            client.BaseUrl = new Uri("https://owner-api.teslamotors.com");            
            var ownerTokenRequest = new RestRequest("oauth/token", Method.POST);
            ownerTokenRequest.AddHeader("Authorization", $"Bearer {token}");
            ownerTokenRequest.AddJsonBody(new
            {
                grant_type = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                client_id = "81527cff06843c8634fdc09e8ac0abefb46ac849f38fe1e431c2ef2106796384",
                client_secret = "c7257eb71a564034f9419ee651c7d0e5f7aa6bfbd18bafb5c5c033b093bb2fa3"
            });
            var ownerTokenResponse = client.Execute<TokenResponseModel>(ownerTokenRequest);
            if (!ownerTokenResponse.IsSuccessful)
            {
                Console.WriteLine($"Error: {ownerTokenResponse.ErrorMessage}");
                return;
            }
            var ownerToken = ownerTokenResponse.Data.access_token;

            Console.WriteLine($"{Environment.NewLine}Copy and enter this token into the appSettings.json file.{Environment.NewLine}{ownerToken}");
        }

        static string ComputeSha256Hash(string rawData)
        {
            // Create a SHA256   
            using (SHA256 sha256Hash = SHA256.Create())
            {
                // ComputeHash - returns byte array  
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));

                // Convert byte array to a string   
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        static string RandomAlpaNumericString(int length)
        {
            var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var stringChars = new char[length];
            var random = new Random();

            for (int i = 0; i < stringChars.Length; i++)
            {
                stringChars[i] = chars[random.Next(chars.Length)];
            }

            return new String(stringChars);
        }

        static double CalculateDistance(Location point1, Location point2)
        {
            var d1 = point1.Latitude * (Math.PI / 180.0);
            var num1 = point1.Longitude * (Math.PI / 180.0);
            var d2 = point2.Latitude * (Math.PI / 180.0);
            var num2 = point2.Longitude * (Math.PI / 180.0) - num1;
            var d3 = Math.Pow(Math.Sin((d2 - d1) / 2.0), 2.0) +
                     Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin(num2 / 2.0), 2.0);
            return 6376500.0 * (2.0 * Math.Atan2(Math.Sqrt(d3), Math.Sqrt(1.0 - d3)));
        }
    }


    public class TokenResponseModel
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public string expires_in { get; set; }
        public string state { get; set; }
        public string token_type { get; set; }
    }

    public class Location
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}

