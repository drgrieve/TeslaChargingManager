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

        //Charge curve
        public string DefaultChargeCurve { get; set; }
        public List<ChargeCurve> ChargeCurves { get; set; }

        //Charging logic
        public int MinLoopSleepDuration { get; set; }
        public int MaxLoopSleepDuration { get; set; }
        public double GridMaxDraw { get; set; }
        public double GridMaxSustainedDraw { get; set; }
        public int SustainedDrawDuration { get; set; }
        public int NotChargingDuration { get; set; }
        public double RampUpPercentage { get; set; }
        public double RampDownPercentage { get; set; }
        public int MinimumChargingAmps { get; set; }
        public int MinimumStateOfCharge { get; set; }

    }

    public class ChargeCurve
    {
        public string Name { get; set; }
        public List<ChargePoint> Points { get; set; }
    }

    public class ChargePoint
    {
        public int SOC { get; set; }
        public double Buffer { get; set; }
    }
}
