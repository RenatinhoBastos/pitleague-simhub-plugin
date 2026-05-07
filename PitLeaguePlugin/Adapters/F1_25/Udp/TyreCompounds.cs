namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>F1 25 tyre compound code decoder.</summary>
    public static class TyreCompounds
    {
        public static (string compound, string visual) Decode(byte code)
        {
            switch (code)
            {
                case 16: return ("C5", "soft");
                case 17: return ("C4", "soft");
                case 18: return ("C3", "medium");
                case 19: return ("C2", "medium");
                case 20: return ("C1", "hard");
                case 21: return ("C0", "hard");
                case 22: return ("C6", "soft");
                case 7:  return ("inter", "intermediate");
                case 8:  return ("wet", "wet");
                default: return ($"unknown_{code}", "unknown");
            }
        }

        public static string MapWeatherCode(byte code)
        {
            switch (code)
            {
                case 0: // clear
                case 1: // light cloud
                case 2: // overcast
                    return "dry";
                case 3: // light rain
                case 4: // heavy rain
                case 5: // storm
                    return "wet";
                default:
                    return null;
            }
        }

        public static string MapResultStatus(byte code)
        {
            switch (code)
            {
                case 0: return "Invalid";
                case 1: return "Inactive";
                case 2: return "Active";
                case 3: return "Finished";
                case 4: return "DNF";
                case 5: return "DSQ";
                case 6: return "NC";
                case 7: return "Retired";
                default: return "Unknown";
            }
        }

        public static string MapSessionType(byte code)
        {
            switch (code)
            {
                case 0: return "Unknown";
                case 1: return "Practice 1";
                case 2: return "Practice 2";
                case 3: return "Practice 3";
                case 4: return "Short Practice";
                case 5: return "Qualifying 1";
                case 6: return "Qualifying 2";
                case 7: return "Qualifying 3";
                case 8: return "Short Qualifying";
                case 9: return "One Shot Qualifying";
                case 10: return "Race";
                case 11: return "Race 2";
                case 12: return "Race 3";
                case 13: return "Time Trial";
                case 14: return "Sprint Shootout 1";
                case 15: return "Sprint Shootout 2";
                case 16: return "Sprint Shootout 3";
                case 17: return "Short Sprint Shootout";
                case 18: return "One Shot Sprint Shootout";
                case 19: return "Sprint";
                default: return "Unknown";
            }
        }

        public static string MapPenaltyType(byte code)
        {
            switch (code)
            {
                case 0: return "drive_through";
                case 1: return "stop_go";
                case 2: return "grid_penalty";
                case 3: return "penalty_reminder";
                case 4: return "time_penalty";
                case 5: return "warning";
                case 6: return "disqualified";
                case 7: return "removed_from_results";
                case 10: return "lap_invalidated";
                case 16: return "retired";
                default: return $"unknown_{code}";
            }
        }
    }
}
