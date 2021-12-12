using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TeslaChargingManager.Tesla;

namespace TeslaChargingManager
{
    internal static class TeslaService
    {
        private static AppSettings appSettings;
        private static RestClient teslaClient;
        private static long vehicleId;
        public static ChargeStateModel chargeState;

        internal static void Init(AppSettings _appSettings)
        {
            appSettings = _appSettings;
            teslaClient = new RestClient("https://owner-api.teslamotors.com");
            teslaClient.AddDefaultHeader("Authorization", $"Bearer {appSettings.TeslaAccessToken}");
        }


        internal static async Task<double> ChargeDelta(double powerDelta)
        {
            chargeState = await ChargeState();

            if (chargeState == null)
            {
                Console.WriteLine($"Charger is: not available");
                return double.NaN;
            }

            if (chargeState.charging_state == "Disconnected")
            {
                Console.WriteLine($"Charger is: {chargeState.charging_state}");
                return double.NaN;
            }

            if (chargeState.charging_state == "Stopped")
            {
                Console.WriteLine($"Charger is: {chargeState.charging_state}.");
                if (powerDelta * 1000 + appSettings.MinimumPowerToStartCharging < 0 && chargeState.battery_level < appSettings.MaximumStateOfCharge)
                {
                    Console.WriteLine($"Starting charging as battery level {chargeState.battery_level} is less than {appSettings.MaximumStateOfCharge}");
                    var result = await TeslaService.StartCharging();
                    return result ? 0 : double.NaN;
                }
                else
                {
                    Console.WriteLine($"Charging not starting as available power {Math.Round(powerDelta * -1000)}w is less than minimum of {appSettings.MinimumPowerToStartCharging}w");
                    return 0;
                }
            }

            if (chargeState.charging_state != "Charging")
            {
                Console.WriteLine($"Charger is: {chargeState.charging_state}");
                return double.NaN;
            }

            Console.WriteLine($"Charging at: {chargeState.charger_actual_current} amps");

            if (powerDelta > 0)
            {
                if (chargeState.charger_actual_current > appSettings.MinimumChargingAmps)
                {
                    var amps = GetAmps(powerDelta, chargeState);
                    if (amps > 0)
                    {
                        amps = AdjustAmps(amps, appSettings.RampDownPercentage);
                        amps = chargeState.charger_actual_current - amps;
                        if (amps < appSettings.MinimumChargingAmps) amps = appSettings.MinimumChargingAmps;
                        await SetVehicleChargingAmps(amps);
                        return amps;
                    }
                }
            }
            else
            {
                if (chargeState.charger_actual_current < appSettings.MaximumChargingAmps)
                {
                    var amps = GetAmps(powerDelta * -1, chargeState);
                    if (amps > 0)
                    {
                        amps = AdjustAmps(amps, appSettings.RampUpPercentage);
                        amps = chargeState.charger_actual_current + amps;
                        if (amps > appSettings.MaximumChargingAmps) amps = appSettings.MaximumChargingAmps;
                        else if (amps < appSettings.MinimumChargingAmps) amps = appSettings.MinimumChargingAmps;
                        await SetVehicleChargingAmps(amps);
                        return amps;
                    }
                }
            }
            return 0;
        }

        private static int GetAmps(double power, ChargeStateModel chargeState)
        {
            var powerPerAmp = chargeState.charger_voltage * chargeState.charger_phases.GetValueOrDefault(1) / 1000.0;
            return (int)Math.Floor(power / powerPerAmp);
        }

        private static int AdjustAmps(int amps, double adjustmentPercentage)
        {
            var newAmps = (int)Math.Round(amps * adjustmentPercentage);
            return newAmps == 0 ? amps : newAmps;
        }

        private static async Task<ChargeStateModel> ChargeState()
        {
            var response = await teslaClient.GetAsync<ChargeStateResponse>(new RestRequest($"api/1/vehicles/{vehicleId}/data_request/charge_state"));
            return response.response;
        }

        private static async Task SetVehicleChargingAmps(int amps)
        {
            Console.WriteLine($"Charging changed to: {amps} amps");
            var request = new RestRequest($"/api/1/vehicles/{vehicleId}/command/set_charging_amps");
            request.AddJsonBody(new { charging_amps = amps });
            var response = await teslaClient.ExecutePostAsync<GenericResponse>(request);
            if (!response.Data.response.result) Console.WriteLine($"Failed to set charging amps: {response.Data.response.reason}");
        }

        internal static async Task StopCharging(string reason)
        {
            Console.WriteLine($"Stopping charging due to {reason}");
            if (chargeState.charging_state != "Charging")
            {
                Console.WriteLine($"Charging not stopped as vehicle is currently in state {chargeState.charging_state}.");
            }
            else if (chargeState.battery_level < appSettings.MinimumStateOfCharge)
            {
                Console.WriteLine($"Battery SOC {chargeState.battery_level} is less than minimum of {appSettings.MinimumStateOfCharge}. Charging not stopped.");
            }
            else
            {
                var request = new RestRequest($"/api/1/vehicles/{vehicleId}/command/charge_stop");
                await teslaClient.PostAsync<GenericResponse>(request);
            }
        }

        internal static async Task<bool> StartCharging()
        {
            var request = new RestRequest($"/api/1/vehicles/{vehicleId}/command/charge_start");
            var response = await teslaClient.ExecutePostAsync<GenericResponse>(request);
            if (!response.Data.response.result) Console.WriteLine($"Failed to start charging as: {response.Data.response.reason}");
            return response.Data.response.result;
        }

        internal static async Task<VehiclesModel> Vehicles()
        {
            var vehicles = await teslaClient.GetAsync<Tesla.VehiclesModel>(new RestRequest("/api/1/vehicles"));
            return vehicles;
        }

        internal static void SetVehicleId(long id)
        {
            vehicleId = id;
        }

        internal static async Task<VehicleDriveStateModel> DriveState(long id)
        {
            var state = await teslaClient.GetAsync<Tesla.VehicleDriveStateResponse>(new RestRequest($"/api/1/vehicles/{id}/data_request/drive_state"));
            return state.response;
        }


    }
}
