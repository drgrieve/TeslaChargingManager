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
            Console.WriteLine("Enter a command or /? for instructions");
            bool quitNow = false;
            while (!quitNow)
            {
                var command = (Console.ReadLine() ?? String.Empty).ToLower();
                var options = command.Split(' ');
                if (options.Length > 0) command = options[0];
                switch (command)
                {
                    case "/help":
                    case "/?":
                        Console.WriteLine("/login = Instructions to generate a new Tesla Access token");
                        Console.WriteLine("/charge = Charge vehicle with excess solar");
                        Console.WriteLine("/trip = Charge vehicle for an upcoming trip");
                        Console.WriteLine("/limit = Set max charge limit");
                        Console.WriteLine("/amps = Set current charging amps");
                        Console.WriteLine("/stats = See current stats");
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
                            var hours = Convert.ToDouble(options[1]);
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
                    case "/amps":
                        if (options.Length != 2)
                        {
                            Console.WriteLine("Enter amps. eg /amps 3");
                        }
                        else
                        {
                            var amps = Convert.ToInt32(options[1]);
                            await Amps(amps);
                        }
                        break;
                    case "/stats":
                        await Stats();
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
        private static async Task Trip(double hours, int percentage)
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
            if (String.IsNullOrEmpty(appSettings.TeslaRefreshToken))
            {
                Console.WriteLine("No Tesla token. Use /login");
                return false;
            }
            //if (String.IsNullOrEmpty(appSettings.PulseRefreshToken))
            //{
            //    Console.WriteLine("No Pulse token.");
            //    return false;
            //}

            TeslaService.Init(appSettings);

            var products = await TeslaService.Products();
            foreach (var product in products.response)
            {
                Console.WriteLine();
                if (product.ResourceType == "battery")
                {
                    Console.WriteLine($"Powerwall: {product.Id} found");

                    TeslaService.SetPowerwallId(product.Id);
                    TeslaService.SetSiteId(product.EnergySiteId.Value);
                }
            }


            //PulseService.Init(appSettings);

            //var pulseUser = PulseService.GetUser();
            //if (pulseUser.user_id == 0)
            //{
            //    Console.WriteLine("Pulse is not currently available");
            //    return false;
            //}

            //Pulse.SiteModel? pulseSite = null;
            //if (pulseUser.site_ids == null)
            //{
            //    var sites = PulseService.GetSites();
            //    pulseSite = sites.data.First();
            //    PulseService.SiteId = pulseSite.site_id;
            //}
            //else
            //{
            //    PulseService.SiteId = pulseUser.site_ids.First();
            //    pulseSite = PulseService.GetSite();
            //}
            //var pulseData = PulseService.GetLiveSummary();
            //if (pulseData?.weather == null)
            //{
            //    Console.WriteLine("Pulse live data is not currently available");
            //    return false;
            //}
            //Console.WriteLine($"Current conditions is {pulseData.weather.temperature}c and {pulseData.weather.description}");
            //Console.WriteLine($"Current solar production is {pulseData.solar}kW");

            //if (pulseData.solar <= 0 || !pulseData.weather.daytime)
            //{
            //    Console.Write($"Solar not currently producing {pulseData.solar} or is night time");
            //    return false;
            //}

            var vehicles = await TeslaService.Vehicles();
            if (vehicles == null || vehicles.count == 0)
            {
                Console.Write("No Tesla vehicles found");
                return false;
            }

            Tesla.VehicleModel? vehicle = null;
            var asleep = false;
            foreach (var v in vehicles.response)
            {
                if (v.state == "asleep")
                {
                    asleep = true;
                    continue;
                }
                asleep = false;

                var vehicleDetail = await TeslaService.VehicleDetail(v.id);
                if (vehicleDetail == null)
                {
                    continue;
                }

                var driveState = vehicleDetail.drive_state;
                if (driveState == null)
                {
                    continue;
                }
                if (driveState.speed != null && driveState.speed > 0)
                {
                    Console.Write($"Vehicle is moving at speed {driveState.speed}");
                    continue;
                }
                vehicle = v;
                //var d = CalculateDistance(driveState.location, pulseSite.location);
                //if (d < 100)
                //{
                //    if (driveState.speed != null && driveState.speed > 0)
                //    {
                //        Console.Write($"Vehicle is moving at speed {driveState.speed}");
                //        return false;
                //    }
                //    vehicle = v;
                //    break;
                //}
                //if (distance > d) distance = (int)d;
            }
            if (vehicle == null)
            {
                if (asleep == true) Console.WriteLine($"No Tesla vehicles found awake.");
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

            while (true)
            {
                if (token.IsCancellationRequested) break;

                //var pulseData = PulseService.GetLiveSummary();
                //if (pulseData == null || pulseData.grid == 0 && pulseData.consumption == 0 && pulseData.solar == 0)
                //{
                //    Console.WriteLine($"{DateTime.Now:s} Pulse data not available.");
                //    Thread.Sleep(appSettings.MinLoopSleepDuration);
                //    continue;
                //}
                var siteData = await TeslaService.SiteStatus();

                //var grid = pulseData.grid;
                var grid = (siteData.BatteryPower + siteData.GridPower) / 1000.0;
                var solar = siteData.SolarPower / 1000.0;
                var load = siteData.LoadPower / 1000.0;

                //Console.WriteLine($"{DateTime.Now:s} Solar:{pulseData.solar:N2} Home:{pulseData.consumption:N2} Grid:{grid:N2} Buffer:{gridBuffer:N2}");
                Console.WriteLine($"{DateTime.Now:s} Solar:{solar:N2} Home:{load:N2} Grid:{siteData.GridPower / 1000.0:N2} Battery:{siteData.BatteryPower / 1000.0} Buffer:{gridBuffer:N2}");

                var sustainedDraw = false;

                if (grid + gridBuffer > 0)
                {
                    var reducedChargeBy = await TeslaService.ChargeDelta(grid + gridBuffer, load);
                    if (Double.IsNaN(reducedChargeBy))
                    {
                        Console.WriteLine("Monitor stopped due to failure.");
                        isInChargingLoop = false;
                        break;
                    }
                    if (reducedChargeBy == 0)
                    {
                        if (grid > chargeCurve.GridMaxDraw && TeslaService.chargeState.charging_state == Tesla.ChargingState.Charging && !preventStopCharge)
                        {
                            await TeslaService.StopCharging($"grid draw {grid} is over grid draw max of {chargeCurve.GridMaxDraw}");
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
                    var increaseChargeBy = await TeslaService.ChargeDelta(grid + gridBuffer, load);
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

                if (TeslaService.chargeState != null)
                {
                    notChargingDuration = TeslaService.chargeState.charging_state == Tesla.ChargingState.Charging ? 0 : notChargingDuration += seconds;
                }
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

                if (statsDuration >= 600 && TeslaService.chargeState != null)
                {
                    Console.WriteLine($"Battery level {TeslaService.chargeState.battery_level}/{TeslaService.chargeState.charge_limit_soc}");
                    Console.WriteLine($"Range {Math.Round(MilesToKilometres * TeslaService.chargeState.ideal_battery_range)}/{Math.Round(MilesToKilometres * TeslaService.chargeState.ideal_battery_range * 100 / TeslaService.chargeState.battery_level)} km");
                    Console.WriteLine($"Charge added {TeslaService.chargeState.charge_energy_added}kWh");
                    Console.WriteLine($"Time to full charge is {TeslaService.chargeState.time_to_full_charge} hours");
                    Console.WriteLine($"Powerwall SOC is {siteData.PercentageCharged:N1}");
                    statsDuration = 0;
                }
                else statsDuration += seconds;

                var sleep = loopDuration - seconds < appSettings.MinLoopSleepDuration ? appSettings.MinLoopSleepDuration : loopDuration - seconds;
                Thread.Sleep((sleep) * 1000);
            }
        }


        private static async Task Limit(int percentage)
        {
            TeslaService.Init(appSettings);
            await TeslaService.SetChargeLimit(percentage);
            Console.WriteLine($"Charge limit set to {percentage}");
        }

        private static async Task Amps(int amps)
        {
            TeslaService.Init(appSettings);
            var vehicles = await TeslaService.Vehicles();
            TeslaService.SetVehicleId(vehicles.response.First().id);
            await TeslaService.SetVehicleChargingAmps(amps);
            Console.WriteLine($"Charging amps set to {amps}");
        }

        private static async Task Stats()
        {
            TeslaService.Init(appSettings);

            var products = await TeslaService.Products();

            if ((products?.count ?? 0) == 0) 
            {
                Console.WriteLine("No products found");
                return;
            }

            foreach (var product in products.response)
            {
                Console.WriteLine();
                if (product.VehicleId != 0)
                {
                    Console.WriteLine($"Vehicle: {product.DisplayName}");
                    Console.WriteLine($"State: {product.State}");

                    if (product.State == "online")
                    {
                        Console.WriteLine("Fetching vehicle details");
                        var details = await TeslaService.VehicleDetail(long.Parse(product.Id));
                        if (details?.charge_state != null)
                        {
                            Console.WriteLine($"Charging: {details.charge_state.charging_state}");
                            Console.WriteLine($"Battery level {details.charge_state.battery_level}/{details.charge_state.charge_limit_soc}");
                            Console.WriteLine($"Range {Math.Round(MilesToKilometres * details.charge_state.ideal_battery_range)}/{Math.Round(MilesToKilometres * details.charge_state.ideal_battery_range * 100 / details.charge_state.battery_level)} km");
                        }
                    }
                }
                else if (product.ResourceType == "battery")
                {
                    Console.WriteLine($"Powerwall: {product.Id}");
                    Console.WriteLine($"Site: {product.SiteName}");
                    if (product.BatteryPower != null)
                    {
                        Console.WriteLine("Fetching powerwall details");

                        var details = await TeslaService.PowerwallDetail(product.Id);

                        Console.WriteLine($"State: {(details.BatteryPower < 0 ? "Discharging" : details.BatteryPower == 0 ? "Standby" : "Charging")} {Math.Abs(details.BatteryPower) / 1000.0:N1} kW");
                        Console.WriteLine($"Actual: {details.EnergyLeft / details.TotalPackEnergy:P2} = {details.EnergyLeft / 1000.0:N1} kW / {details.TotalPackEnergy / 1000.0:N1} kW");
                        Console.WriteLine($"Usable: {details.PercentageCharged / 100:P2} = {details.PercentageCharged / 100 * 13500 / 1000:N1} kW / {13500 / 1000.0:N1} kW");
                    }

                    if (product.EnergySiteId != null)
                    {
                        Console.WriteLine("Fetching site details");

                        var details = await TeslaService.SiteStatus(product.EnergySiteId);

                        Console.WriteLine($"Load: {details.LoadPower / 1000:N1} kW");
                        Console.WriteLine($"Solar: {details.SolarPower / 1000:N1} kW");
                        Console.WriteLine($"Battery: {details.BatteryPower / 1000:N1} kW");
                        Console.WriteLine($"Grid: {details.GridPower / 1000:N1} kW");
                    }
                }
            }

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

