using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeslaChargingManager
{
    public class AppSettings
    {
        //Setup
        public string TeslaAccessToken { get; set; }
        public string PulseClientId { get; set; }
        public string PulseRefreshToken { get; set; }
        public string PulseUrl { get; set; }

        //Charging logic
        public int LoopSleepDuration { get; set; }
        public double GridBuffer { get; set; }
        public double GridMaxDraw { get; set; }
        public double GridMaxSustainedDraw { get; set; }
        public int SustainedDrawDuration { get; set; }
        public int NotChargingDuration { get; set; }
        public double RampUpPercentage { get; set; }
        public double RampDownPercentage { get; set; }
        public int MinimumPowerToStartCharging { get; set; }
        public int MinimumChargingAmps { get; set; }
        public int MaximumChargingAmps { get; set; }
        public int MinimumStateOfCharge { get; set; }
        public int MaximumStateOfCharge { get; set; }

    }
}
