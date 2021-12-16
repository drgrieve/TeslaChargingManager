﻿using HtmlAgilityPack;
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
            if (pulseUser.user_id == 0)
            {
                Console.WriteLine("Pulse is not currently available");
                isInChargingLoop = false;
                return;
            }
            var siteId = pulseUser.site_ids.First();
            var pulseSite = PulseService.GetSite(siteId);
            var pulseData = PulseService.GetLiveSummary(siteId);
            Console.WriteLine($"Current conditions is {pulseData.weather.temperature}c and {pulseData.weather.description}");
            Console.WriteLine($"Current solar production is {pulseData.solar}kW");

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
            var asleep = false;
            foreach(var v in vehicles.response)
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
                if (asleep == true && distance == int.MaxValue) Console.WriteLine($"No Tesla vehicles found awake.");
                else Console.WriteLine($"No Tesla vehicles found within 100m of {pulseSite.address}. Closest is {distance}m");
                isInChargingLoop = false;
                return;
            }

            Console.WriteLine($"Tesla vehicle found: {vehicle.display_name} and is {vehicle.state}");

            TeslaService.SetVehicleId(vehicle.id);

            Console.WriteLine("Press ESC to stop monitoring");

            var timer = new Stopwatch();
            var sustainedDrawDuration = 0;
            var notChargingDuration = 0;
            var statsDuration = 600;
            var loopDuration = appSettings.MinLoopSleepDuration;
            var stableDrawDuration = 0;
            var gridBuffer = appSettings.GridBuffer;
            while (true)
            {
                if (token.IsCancellationRequested) break;

                pulseData = PulseService.GetLiveSummary(siteId);
                if (pulseData.grid == 0 && pulseData.consumption == 0 && pulseData.solar == 0)
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
                        if (grid > appSettings.GridMaxDraw && TeslaService.chargeState.charging_state == "Charging")
                        {
                            await TeslaService.StopCharging($"grid draw {grid} is over grid draw max of {appSettings.GridMaxDraw}");
                        }
                        else if (grid > appSettings.GridMaxSustainedDraw && TeslaService.chargeState.charging_state == "Charging")
                        {
                            sustainedDraw = true;
                        }
                    }
                    loopDuration = TeslaService.chargeState.charging_state == "Charging" ? appSettings.MinLoopSleepDuration : appSettings.MaxLoopSleepDuration;
                    stableDrawDuration = 0;
                    gridBuffer = appSettings.GridBuffer;
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
                        if (stableDrawDuration > 60 && gridBuffer > appSettings.GridMinBuffer)
                        {
                            stableDrawDuration = 0;
                            gridBuffer = Math.Round(gridBuffer - 0.05, 2);
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
                if (sustainedDrawDuration >= appSettings.SustainedDrawDuration)
                {
                    await TeslaService.StopCharging($"grid draw over grid sustained draw limit of {appSettings.GridMaxSustainedDraw} for {sustainedDrawDuration} seconds");
                }
                else if (sustainedDrawDuration > 0)
                {
                    Console.WriteLine($"Sustained draw {sustainedDrawDuration}/{appSettings.SustainedDrawDuration}");
                }

                notChargingDuration = TeslaService.chargeState.charging_state == "Charging" ? 0 : notChargingDuration += seconds;
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
                    Console.WriteLine($"Range {Math.Round(1.609344 * TeslaService.chargeState.ideal_battery_range)}/{Math.Round(1.609344 * TeslaService.chargeState.ideal_battery_range * 100 / TeslaService.chargeState.battery_level)}");
                    Console.WriteLine($"Charge added {TeslaService.chargeState.charge_energy_added}kWh");
                    Console.WriteLine($"Time to full charge is {TeslaService.chargeState.time_to_full_charge} hours");
                    statsDuration = 0;
                }
                else statsDuration += seconds;

                var sleep = loopDuration - seconds < appSettings.MinLoopSleepDuration ? appSettings.MinLoopSleepDuration : loopDuration - seconds;
                Thread.Sleep((sleep) * 1000);
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

