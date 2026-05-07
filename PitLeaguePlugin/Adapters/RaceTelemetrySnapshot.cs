using System;
using System.Collections.Generic;

namespace PitLeague.SimHub.Adapters
{
    public class RaceTelemetrySnapshot
    {
        public string SessionUID { get; set; }
        public string Game { get; set; }
        public DateTime CapturedAt { get; set; }
        public SessionInfo Session { get; set; } = new SessionInfo();
        public List<DriverResult> Drivers { get; set; } = new List<DriverResult>();
    }

    public class SessionInfo
    {
        public string Type { get; set; }       // "Race", "Sprint", "Qualifying"
        public string Track { get; set; }
        public int? TotalLaps { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public WeatherInfo Weather { get; set; } = new WeatherInfo();
    }

    public class WeatherInfo
    {
        public string Condition { get; set; }   // "dry" | "wet" | "mixed" | null
        public double? AirTempStart { get; set; }
        public double? AirTempEnd { get; set; }
        public double? TrackTempStart { get; set; }
        public double? TrackTempEnd { get; set; }
        public int? RainPercentageAvg { get; set; }
        public List<WeatherChange> Changes { get; set; } = new List<WeatherChange>();
    }

    public class WeatherChange
    {
        public int Lap { get; set; }
        public string FromCondition { get; set; }
        public string ToCondition { get; set; }
        public DateTime CapturedAt { get; set; }
    }

    public class DriverResult
    {
        // Always populated
        public string Gamertag { get; set; }
        public int Position { get; set; }
        public string Status { get; set; }       // "Finished", "DNF", "DSQ", "DNS", "NC"
        public string Team { get; set; }
        public string Gap { get; set; }
        public string BestLapTime { get; set; }
        public bool FastestLap { get; set; }
        public bool PolePosition { get; set; }
        public int PenaltySeconds { get; set; }

        // Populated by rich adapters (F1_25). Generic leaves null.
        public int? QualifyingPosition { get; set; }
        public int? GridPosition { get; set; }
        public double? TopSpeed { get; set; }
        public double? RacePaceGapPct { get; set; }
        public int? NumPenaltiesAccumulated { get; set; }
        public List<LapTimeEntry> LapTimes { get; set; }
        public List<PitStopEntry> PitStops { get; set; }
        public List<TyreStintEntry> TyreStints { get; set; }
        public DriverIncidents Incidents { get; set; }
    }

    public class LapTimeEntry
    {
        public int Lap { get; set; }
        public string Time { get; set; }
        public string S1 { get; set; }
        public string S2 { get; set; }
        public string S3 { get; set; }
        public bool Valid { get; set; }
    }

    public class PitStopEntry
    {
        public int Lap { get; set; }
        public double DurationSec { get; set; }
        public string TyreFrom { get; set; }
        public string TyreTo { get; set; }
    }

    public class TyreStintEntry
    {
        public string Compound { get; set; }
        public string VisualCompound { get; set; }
        public int LapStart { get; set; }
        public int LapEnd { get; set; }
    }

    public class DriverIncidents
    {
        public int Collisions { get; set; }
        public int TrackLimitsWarnings { get; set; }
        public int CornerCutting { get; set; }
        public int WingRepairs { get; set; }
        public List<PenaltyEntry> Penalties { get; set; } = new List<PenaltyEntry>();
    }

    public class PenaltyEntry
    {
        public string Type { get; set; }
        public int Lap { get; set; }
        public int Seconds { get; set; }
    }
}
