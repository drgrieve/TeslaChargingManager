using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;

namespace TeslaChargingManager.Tesla
{
    public class GenericResponse : BaseResponse
    {
        public GenericResult response { get; set; }
    }

    public class GenericResult
    {
        public string reason { get; set; }
        public bool result { get; set; }
    }

    public class BaseResponse
    {
        public string error { get; set; }
        public string error_description { get; set; }
    }

    public class VehicleModel
    {
        public long id { get; set; }
        public int vehicle_id { get; set; }
        public string vin { get; set; }
        public string display_name { get; set; }
        public string option_codes { get; set; }
        public object color { get; set; }
        public List<string> tokens { get; set; }
        public string state { get; set; }
        public bool in_service { get; set; }
        public string id_s { get; set; }
        public bool calendar_enabled { get; set; }
        public int api_version { get; set; }
        public object backseat_token { get; set; }
        public object backseat_token_updated_at { get; set; }
    }

    public class VehiclesModel
    {
        public List<VehicleModel> response { get; set; }
        public int count { get; set; }
    }

    public class VehicleDetailResponse : BaseResponse
    {
        public VehicleDetailModel response { get; set; }
    }

    public class VehicleDetailModel
    {
        public long id { get; set; }
        public long user_id { get; set; }
        public long vehicle_id { get; set; }
        public string vin { get; set; }
        public string display_name { get; set; }
        public object color { get; set; }
        public string access_type { get; set; }
        public IList<string> tokens { get; set; }
        public string state { get; set; }
        public bool in_service { get; set; }
        public string id_s { get; set; }
        public bool calendar_enabled { get; set; }
        public int api_version { get; set; }
        public object backseat_token { get; set; }
        public object backseat_token_updated_at { get; set; }
        public VehicleDriveStateModel drive_state { get; set; }
        //public ClimateState climate_state { get; set; }
        public ChargeStateModel charge_state { get; set; }
        //public GuiSettings gui_settings { get; set; }
        //public VehicleState vehicle_state { get; set; }
        //public VehicleConfig vehicle_config { get; set; }
    }

    public class ChargeStateModel
    {
        public bool battery_heater_on { get; set; }
        public int battery_level { get; set; }
        public double battery_range { get; set; }
        public int charge_current_request { get; set; }
        public int charge_current_request_max { get; set; }
        public bool charge_enable_request { get; set; }
        public double charge_energy_added { get; set; }
        public int charge_limit_soc { get; set; }
        public int charge_limit_soc_max { get; set; }
        public int charge_limit_soc_min { get; set; }
        public int charge_limit_soc_std { get; set; }
        public double charge_miles_added_ideal { get; set; }
        public double charge_miles_added_rated { get; set; }
        public object charge_port_cold_weather_mode { get; set; }
        public bool charge_port_door_open { get; set; }
        public string charge_port_latch { get; set; }
        public double charge_rate { get; set; }
        public bool charge_to_max_range { get; set; }
        public int charger_actual_current { get; set; }
        public int? charger_phases { get; set; }
        public int charger_pilot_current { get; set; }
        public int charger_power { get; set; }
        public int charger_voltage { get; set; }
        public ChargingState? charging_state { get; set; }
        public string conn_charge_cable { get; set; }
        public double est_battery_range { get; set; }
        public string fast_charger_brand { get; set; }
        public bool fast_charger_present { get; set; }
        public string fast_charger_type { get; set; }
        public double ideal_battery_range { get; set; }
        public bool managed_charging_active { get; set; }
        public object managed_charging_start_time { get; set; }
        public bool managed_charging_user_canceled { get; set; }
        public int max_range_charge_counter { get; set; }
        public int minutes_to_full_charge { get; set; }
        public bool not_enough_power_to_heat { get; set; }
        public bool scheduled_charging_pending { get; set; }
        public object scheduled_charging_start_time { get; set; }
        public double time_to_full_charge { get; set; }
        public long timestamp { get; set; }
        public bool trip_charging { get; set; }
        public int usable_battery_level { get; set; }
        public object user_charge_enable_request { get; set; }
    }

    public enum ChargingState
    {
        Charging = 1,
        Disconnected,
        Starting,
        Stopped,
        Complete
    }

    public class ChargeStateResponse : BaseResponse
    {
        public ChargeStateModel response { get; set; }
    }

    public class VehicleDriveStateModel
    {
        public int gps_as_of { get; set; }
        public int heading { get; set; }
        public double latitude { get; set; }
        public double longitude { get; set; }
        public double native_latitude { get; set; }
        public int native_location_supported { get; set; }
        public double native_longitude { get; set; }
        public string native_type { get; set; }
        public int power { get; set; }
        public object shift_state { get; set; }
        public int? speed { get; set; }
        public long timestamp { get; set; }

        public Location location => new Location { Latitude = latitude, Longitude = longitude };

    }

    public class VehicleDriveStateResponse : BaseResponse
    {
        public VehicleDriveStateModel response { get; set; }
    }



}
