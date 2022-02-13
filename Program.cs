using HtmlAgilityPack;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using RestSharp;
using System.Diagnostics;
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
                var command = (Console.ReadLine() ?? String.Empty).ToLower();
                var options = command.Split(' ');
                if (options.Length > 0) command = options[0];
                switch (command)
                {
                    case "/help":
                        Console.WriteLine("/login = Instructions to generate a new Tesla Access token");
                        Console.WriteLine("/charge = Charge vehicle with excess solar");
                        Console.WriteLine("/trip = Charge vehicle for an upcoming trip");
                        Console.WriteLine("/quit = Exit program");
                        break;

                    case "/login":
                        Login();
                        break;

                    case "/charge":
                        var curve = options.Length == 1 ? appSettings.DefaultChargeCurve : options[1];
                        await Charge(curve);
                        break;

                    case "/trip":
                        if (options.Length != 3)
                        {
                            Console.WriteLine("Enter number of hours until departure and required SOC in percent. eg /trip 5 75");
                        }
                        else
                        {
                            var hours = Convert.ToInt32(options[1]);
                            var percentage = Convert.ToInt32(options[2]);
                            await Trip(hours, percentage);
                        }
                        break;

                    case "/limit":
                        if (options.Length != 2)
                        {
                            Console.WriteLine("Enter percentage limit. eg /limit 90");
                        }
                        else
                        {
                            var percentage = Convert.ToInt32(options[1]);
                            await Limit(percentage);
                        }
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
        private const double MilesToKilometres = 1.609344;
        private static async Task Trip(int hours, int percentage)
        {
            Console.WriteLine($"Charging Tesla from Solar for an upcoming trip in {hours} hours with required percentage SOC of {percentage}");

            if (!await Init()) return;

            var chargeCurve = appSettings.ChargeCurves.Where(x => x.Name == "Solar+").FirstOrDefault();
            if (chargeCurve == null)
            {
                Console.WriteLine($"Solar+ charge curve not found");
                return;
            }

            //Stage 1 => Charge with Solar+ curve
            //Stage 2 => Charge at max rate
            //Stage 3 => Charge with Solar curve

            await TeslaService.SetChargeLimitIfLower(percentage);

            isInChargingLoop = true;
            cts = new CancellationTokenSource();
            await Task.Run(() => ChargeLoop(chargeCurve, cts.Token, preventStopCharge: true));   //Stage 1

            var tripStart = DateTime.Now.AddHours(hours).AddMinutes(-5);
            var duration = 50000;
            while (!(Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) && isInChargingLoop)
            {
                if (duration >= 60000 && TeslaService.chargeState != null)
                {
                    if (TeslaService.chargeState.charging_state != Tesla.ChargingState.Charging && chargeCurve == null)
                        break;
                    if (chargeCurve?.Name == "Solar+")
                    {
                        var remainingTime = tripStart - DateTime.Now;
                        var remainingMinutes = remainingTime.TotalMinutes > 0 ? remainingTime.TotalMinutes : 0;
                        if (remainingMinutes > 0 && TeslaService.chargeState.charging_state == Tesla.ChargingState.Charging)
                        {
                            var minutesToFullCharge = 1.0 * TeslaService.chargeState.minutes_to_full_charge * percentage / TeslaService.chargeState.charge_limit_soc * TeslaService.chargeState.charge_current_request / TeslaService.chargeState.charge_current_request_max;
                            if (minutesToFullCharge > remainingMinutes) //Stage 2
                            {
                                cts.Cancel();
                                chargeCurve = null;
                                await TeslaService.SetVehicleChargingAmps(TeslaService.chargeState.charge_current_request_max);
                            }
                        }                          
                    }
                    //Stage 3
                    if (TeslaService.chargeState.battery_level >= percentage && chargeCurve?.Name != "Solar")
                    {
                        cts.Cancel();
                        chargeCurve = appSettings.ChargeCurves.Where(x => x.Name == "Solar").FirstOrDefault();
                        if (chargeCurve == null)
                        {
                            Console.WriteLine($"Solar charge curve not found");
                            return;
                        }
                        cts = new CancellationTokenSource();
                        await Task.Run(() => ChargeLoop(chargeCurve, cts.Token));
                    }
                    if (chargeCurve == null)
                    {
                        await TeslaService.ChargeState();
                        Console.WriteLine($"{DateTime.Now:s} Charging at maximum amps until SOC {TeslaService.chargeState.battery_level}% reaches {percentage}%");
                    }
                    duration = 0;
                }
                duration += 500;
                Thread.Sleep(500);
            }
            cts.Cancel();
            Console.WriteLine("Trip monitoring has stopped");
        }

        private static async Task Charge(string chargeCurveName)
        {
            Console.WriteLine("Charging Tesla from Solar");

            if (!await Init()) return;

            var chargeCurve = appSettings.ChargeCurves.FirstOrDefault(x => x.Name.Equals(chargeCurveName, StringComparison.OrdinalIgnoreCase));
            if (chargeCurve == null)
            {
                Console.WriteLine($"Charge curve {chargeCurveName} not found");
                return;
            }

            isInChargingLoop = true;
            cts = new CancellationTokenSource();
            var token = cts.Token;

            await Task.Run(() => ChargeLoop(chargeCurve, token));

            while (!(Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) && isInChargingLoop)
            {
                Thread.Sleep(500);
            }
            cts.Cancel();
            Console.WriteLine("Charge monitoring has stopped");
        }

        private static async Task<bool> Init()
        {
            if (String.IsNullOrEmpty(appSettings.TeslaAccessToken))
            {
                Console.WriteLine("No Tesla token. Use /login");
                return false;
            }
            if (String.IsNullOrEmpty(appSettings.PulseRefreshToken))
            {
                Console.WriteLine("No Pulse token.");
                return false;
            }

            TeslaService.Init(appSettings);
            PulseService.Init(appSettings);

            var pulseUser = PulseService.GetUser();
            if (pulseUser.user_id == 0)
            {
                Console.WriteLine("Pulse is not currently available");
                return false;
            }
            PulseService.SiteId = pulseUser.site_ids.First();
            var pulseSite = PulseService.GetSite();
            var pulseData = PulseService.GetLiveSummary();
            Console.WriteLine($"Current conditions is {pulseData.weather.temperature}c and {pulseData.weather.description}");
            Console.WriteLine($"Current solar production is {pulseData.solar}kW");

            if (pulseData.solar <= 0 || !pulseData.weather.daytime)
            {
                Console.Write($"Solar not currently producing {pulseData.solar} or is night time");
                return false;
            }

            var vehicles = await TeslaService.Vehicles();
            if (vehicles?.count == 0)
            {
                Console.Write("No Tesla vehicles found");
                return false;
            }

            Tesla.VehicleModel? vehicle = null;
            var distance = int.MaxValue;
            var asleep = false;
            foreach (var v in vehicles.response)
            {
                if (v.state == "asleep")
                {
                    asleep = true;
                    continue;
                }
                var driveState = await TeslaService.DriveState(v.id);
                var d = CalculateDistance(driveState.location, pulseSite.location);
                if (d < 100)
                {
                    if (driveState.speed != null && driveState.speed > 0)
                    {
                        Console.Write($"Vehicle is moving at speed {driveState.speed}");
                        return false;
                    }
                    vehicle = v;
                    break;
                }
                if (distance > d) distance = (int)d;
            }
            if (vehicle == null)
            {
                if (asleep == true && distance == int.MaxValue) Console.WriteLine($"No Tesla vehicles found awake.");
                else Console.WriteLine($"No Tesla vehicles found within 100m of {pulseSite.address}. Closest is {distance}m");
                return false;
            }

            Console.WriteLine($"Tesla vehicle found: {vehicle.display_name} and is {vehicle.state}");

            TeslaService.SetVehicleId(vehicle.id);

            return true;
        }
        private static async void ChargeLoop(ChargeCurve chargeCurve, CancellationToken token, bool preventStopCharge = false)
        {
            Console.WriteLine($"Using charge curve {chargeCurve.Name}. Press ESC to stop monitoring");

            var timer = new Stopwatch();
            var sustainedDrawDuration = 0;
            var notChargingDuration = 0;
            var statsDuration = 600;
            var loopDuration = appSettings.MinLoopSleepDuration;
            var stableDrawDuration = 0;
            var gridBuffer = await TeslaService.GetGridBuffer(chargeCurve);
            var sleep = 0;

            while (true)
            {
                if (token.IsCancellationRequested) break;

                var pulseData = PulseService.GetLiveSummary();
                if (pulseData == null || pulseData.grid == 0 && pulseData.consumption == 0 && pulseData.solar == 0)
                {
                    Console.WriteLine($"{DateTime.Now:s} Pulse data not available.");
                    Thread.Sleep(appSettings.MinLoopSleepDuration);
                    continue;
                }

                var grid = pulseData.grid;

                Console.WriteLine($"{DateTime.Now:s} Solar:{pulseData.solar:N2} Home:{pulseData.consumption:N2} Grid:{grid:N2} Buffer:{gridBuffer:N2}");

                var sustainedDraw = false;

                if (grid + gridBuffer > 0)
                {
                    var reducedChargeBy = await TeslaService.ChargeDelta(grid + gridBuffer, pulseData.consumption);
                    if (Double.IsNaN(reducedChargeBy))
                    {
                        Console.WriteLine("Monitor stopped due to failure.");
                        isInChargingLoop = false;
                        break;
                    }
                    if (reducedChargeBy == 0)
                    {
                        if (grid > appSettings.GridMaxDraw && TeslaService.chargeState.charging_state == Tesla.ChargingState.Charging && !preventStopCharge)
                        {
                            await TeslaService.StopCharging($"grid draw {grid} is over grid draw max of {appSettings.GridMaxDraw}");
                        }
                        else if (grid > appSettings.GridMaxSustainedDraw && TeslaService.chargeState.charging_state == Tesla.ChargingState.Charging)
                        {
                            sustainedDraw = true;
                        }
                    }
                    loopDuration = TeslaService.chargeState.charging_state == Tesla.ChargingState.Charging ? appSettings.MinLoopSleepDuration : appSettings.MaxLoopSleepDuration;
                    stableDrawDuration = 0;
                    gridBuffer = await TeslaService.GetGridBuffer(chargeCurve);
                }
                else 
                {
                    var increaseChargeBy = await TeslaService.ChargeDelta(grid + gridBuffer, pulseData.consumption);
                    if (Double.IsNaN(increaseChargeBy))
                    {
                        Console.WriteLine("Monitor stopped due to failure.");
                        isInChargingLoop = false;
                        break;
                    }
                    if (increaseChargeBy == 0)
                    {
                        stableDrawDuration += loopDuration;
                        if (stableDrawDuration > 60 && gridBuffer > await TeslaService.GetGridBuffer(chargeCurve) / 2)
                        {
                            stableDrawDuration = 0;
                            gridBuffer = Math.Round(gridBuffer > 0 ? gridBuffer - 0.05 : gridBuffer + 0.05, 2);
                        }
                        loopDuration += appSettings.MinLoopSleepDuration;
                        if (loopDuration > appSettings.MaxLoopSleepDuration) loopDuration = appSettings.MaxLoopSleepDuration;
                    }
                    else
                    { 
                        loopDuration = appSettings.MinLoopSleepDuration;
                        stableDrawDuration = 0;
                    }
                }

                var seconds = (int)Math.Round(timer.Elapsed.TotalSeconds);
                timer.Restart();
                sustainedDrawDuration = sustainedDraw ? sustainedDrawDuration += seconds : 0;
                if (sustainedDrawDuration >= appSettings.SustainedDrawDuration && !preventStopCharge)
                {
                    await TeslaService.StopCharging($"grid draw over grid sustained draw limit of {appSettings.GridMaxSustainedDraw} for {sustainedDrawDuration} seconds");
                }
                else if (sustainedDrawDuration > 0)
                {
                    Console.WriteLine($"Sustained draw {sustainedDrawDuration}/{appSettings.SustainedDrawDuration}");
                }

                notChargingDuration = TeslaService.chargeState.charging_state == Tesla.ChargingState.Charging ? 0 : notChargingDuration += seconds;
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

                if (statsDuration >= 600)
                {
                    Console.WriteLine($"Battery level {TeslaService.chargeState.battery_level}/{TeslaService.chargeState.charge_limit_soc}");
                    Console.WriteLine($"Range {Math.Round(MilesToKilometres * TeslaService.chargeState.ideal_battery_range)}/{Math.Round(MilesToKilometres * TeslaService.chargeState.ideal_battery_range * 100 / TeslaService.chargeState.battery_level)} km");
                    Console.WriteLine($"Charge added {TeslaService.chargeState.charge_energy_added}kWh");
                    Console.WriteLine($"Time to full charge is {TeslaService.chargeState.time_to_full_charge} hours");
                    statsDuration = 0;
                }
                else statsDuration += seconds;

                sleep = loopDuration - seconds < appSettings.MinLoopSleepDuration ? appSettings.MinLoopSleepDuration : loopDuration - seconds;
                Thread.Sleep((sleep) * 1000);
            }
        }


        private static async Task Limit(int percentage)
        {
            TeslaService.Init(appSettings);
            await TeslaService.SetChargeLimit(percentage);
            Console.WriteLine($"Charge limit set to {percentage}");
        }

        private static void Login()
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

        private static string ComputeSha256Hash(string rawData)
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

        private static string RandomAlpaNumericString(int length)
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

        private static double CalculateDistance(Location point1, Location point2)
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

