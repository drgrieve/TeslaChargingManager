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
        internal static ChargeStateModel? chargeState;
        private static DateTime chargeStateLastRefreshed;

        internal static void Init(AppSettings _appSettings)
        {
            appSettings = _appSettings;
            if (teslaClient == null)
            {
                teslaClient = new RestClient("https://owner-api.teslamotors.com");
                teslaClient.AddDefaultHeader("Authorization", $"Bearer {appSettings.TeslaAccessToken}");
            }
        }


        internal static async Task<double> ChargeDelta(double powerDelta, double consumption)
        {
            if (powerDelta < 0 && chargeState != null)
            {
                var amps = GetAmps(powerDelta * -1);
                if (amps == 0) return 0;
            }

            await ChargeState();

            if (chargeState == null)
            {
                Console.WriteLine($"Charger is: not available");
                return double.NaN;
            }
             
            if (chargeState.charging_state == ChargingState.Disconnected)
            {
                Console.WriteLine($"Charger is: {chargeState.charging_state}");
                return double.NaN;
            }

            if (chargeState.charging_state == ChargingState.Stopped)
            {
                Console.WriteLine($"Charger is: {chargeState.charging_state}.");

                if (chargeState.battery_level >= chargeState.charge_limit_soc - 1)
                {
                    Console.WriteLine($"Monitoring stopped as {chargeState.battery_level} has reached or exceeded {chargeState.charge_limit_soc - 1}");
                    return double.NaN;
                }

                var minimumPowerToStartCharging = (TeslaService.GetPowerPerAmp() ?? 0) * 1000 * appSettings.MinimumChargingAmps;
                if (powerDelta * 1000 + minimumPowerToStartCharging < 0)
                {
                    Console.WriteLine($"Starting charging as battery level {chargeState.battery_level} is less than {chargeState.charge_limit_soc - 1}");
                    var result = await TeslaService.StartCharging();
                    return result ? 0 : double.NaN;
                }
                else
                {
                    Console.WriteLine($"Charging not starting as available power {Math.Round(powerDelta * -1000)}w is less than minimum of {minimumPowerToStartCharging}w");
                    return 0;
                }
            }

            if (chargeState.charging_state != ChargingState.Charging)
            {
                Console.WriteLine($"Charger is: {chargeState.charging_state}");

                if (chargeState.charging_state == ChargingState.Complete) await SetVehicleChargingAmps(appSettings.MinimumChargingAmps);

                return chargeState.charging_state == ChargingState.Starting ? 0 : double.NaN;
            }

            Console.WriteLine($"Charging at: {chargeState.charger_actual_current} amps");

            var consumptionAmps = GetAmps(consumption);
            if (consumptionAmps < chargeState.charger_actual_current)
            {
                Console.WriteLine($"Consumption in amps: {consumptionAmps} indicates charging is not ramped to indicated charge rate.");
                if (powerDelta < 0) return 0;
            }

            if (powerDelta > 0)
            {
                if (chargeState.charger_actual_current > appSettings.MinimumChargingAmps)
                {
                    var amps = GetAmps(powerDelta);
                    if (amps == 0) amps = 1;
                    amps = AdjustAmps(amps, appSettings.RampDownPercentage);
                    amps = chargeState.charger_actual_current - amps;
                    if (amps < appSettings.MinimumChargingAmps) amps = appSettings.MinimumChargingAmps;
                    await SetVehicleChargingAmps(amps);
                    return amps;
                }
            }
            else
            {
                if (chargeState.charger_actual_current < chargeState.charge_current_request_max)
                {
                    var amps = GetAmps(powerDelta * -1);
                    if (amps > 0)
                    {
                        amps = AdjustAmps(amps, appSettings.RampUpPercentage);
                        amps = chargeState.charger_actual_current + amps;
                        if (amps > chargeState.charge_current_request_max) amps = chargeState.charge_current_request_max;
                        else if (amps < appSettings.MinimumChargingAmps) amps = appSettings.MinimumChargingAmps;
                        await SetVehicleChargingAmps(amps);
                        return amps;
                    }
                }
            }
            return 0;
        }

        private static double? powerPerAmp;
        private static double? GetPowerPerAmp()
        {
            if (chargeState.charger_voltage == 0 || chargeState.charger_phases == null) return powerPerAmp;
            powerPerAmp = chargeState.charger_voltage * chargeState.charger_phases.GetValueOrDefault(1) / 1000.0;
            return powerPerAmp;
        }
        private static int GetAmps(double power)
        {
            var powerPerAmp = GetPowerPerAmp().GetValueOrDefault(1);
            return (int)Math.Floor(power / powerPerAmp);
        }

        private static int AdjustAmps(int amps, double adjustmentPercentage)
        {
            var newAmps = (int)Math.Round(amps * adjustmentPercentage);
            return newAmps == 0 ? amps : newAmps;
        }

        internal static async Task ChargeState()
        {
            //If charging is stopped don't bother to check for charge state too often, as you can assume the car is not charging
            if (chargeState?.charging_state == ChargingState.Stopped && chargeStateLastRefreshed.AddMinutes(10) < DateTime.UtcNow)
            {
                return;
            }
            var response = await teslaClient.ExecuteGetAsync<ChargeStateResponse>(new RestRequest($"api/1/vehicles/{vehicleId}/data_request/charge_state"));
            chargeState = response.Data.response;
            chargeStateLastRefreshed = DateTime.UtcNow;
        }

        internal static async Task SetVehicleChargingAmps(int amps)
        {
            Console.WriteLine($"Charging changed to: {amps} amps");
            var request = new RestRequest($"/api/1/vehicles/{vehicleId}/command/set_charging_amps");
            request.AddJsonBody(new { charging_amps = amps });
            var response = await teslaClient.ExecutePostAsync<GenericResponse>(request);
            if (response.Data == null) Console.WriteLine($"Failed to set charging amps: {response.Content}");
            if (response.Data.error != null) Console.WriteLine($"Failed to set charging amps: {response.Data.error}");
            else if (!response.Data.response.result) Console.WriteLine($"Failed to set charging amps: {response.Data.response.reason}");
        }

        internal static async Task StopCharging(string reason)
        {
            Console.WriteLine($"Stopping charging due to {reason}");
            if (chargeState.charging_state != ChargingState.Charging)
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

        internal static async Task<double> GetGridBuffer(ChargeCurve chargeCurve)
        {
            if (chargeState == null) await ChargeState();

            var point = chargeCurve.Points.First(x => x.SOC > chargeState.battery_level);

            return point.Buffer;
        }

        internal static async Task<bool> StartCharging()
        {
            var request = new RestRequest($"/api/1/vehicles/{vehicleId}/command/charge_start");
            var response = await teslaClient.ExecutePostAsync<GenericResponse>(request);
            if (!response.Data.response.result) Console.WriteLine($"Failed to start charging as: {response.Data.response.reason}");
            chargeState = null;
            return response.Data.response.result;
        }

        internal static async Task<VehiclesModel> Vehicles()
        {
            var response = await teslaClient.ExecuteGetAsync<VehiclesModel>(new RestRequest("/api/1/vehicles"));
            return response.Data;
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

        internal static async Task SetChargeLimitIfLower(int percentage)
        {
            if (chargeState == null) await ChargeState();

            if (percentage > chargeState.charge_limit_soc)
            {
                await SetChargeLimit(percentage);
            }
        }

        internal static async Task SetChargeLimit(int percentage)
        {
            var request = new RestRequest($"/api/1/vehicles/{vehicleId}/command/set_charge_limit", Method.POST);
            request.AddJsonBody(new { percent = percentage });
            var response = await teslaClient.ExecutePostAsync<GenericResponse>(request);
        }
    }
}
