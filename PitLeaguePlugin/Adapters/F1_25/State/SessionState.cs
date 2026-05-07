using System;
using System.Collections.Generic;

namespace PitLeague.SimHub.Adapters.F1_25.State
{
    /// <summary>Accumulated session-level state from SessionData packets.</summary>
    public class SessionState
    {
        public string Type { get; set; }             // "Race", "Sprint", "Qualifying", "Other"
        public string Track { get; set; }
        public int TotalLaps { get; set; }
        public byte SessionTypeCode { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public int CurrentLap { get; set; }

        // Weather
        public string WeatherCondition { get; set; }  // "dry", "wet", "mixed"
        public double? AirTempStart { get; set; }
        public double? AirTempEnd { get; set; }
        public double? TrackTempStart { get; set; }
        public double? TrackTempEnd { get; set; }
        public int? RainPercentageAvg { get; set; }
        public List<WeatherChange> WeatherChanges { get; set; } = new List<WeatherChange>();

        // Internal tracking
        public bool HasInitialWeather { get; set; }
        public byte StartWeatherCode { get; set; }
        public byte EndWeatherCode { get; set; }
        public byte? LastWeatherCode { get; set; }

        public void Reset()
        {
            Type = null;
            Track = null;
            TotalLaps = 0;
            StartedAt = null;
            EndedAt = null;
            CurrentLap = 0;
            WeatherCondition = null;
            AirTempStart = null;
            AirTempEnd = null;
            TrackTempStart = null;
            TrackTempEnd = null;
            RainPercentageAvg = null;
            WeatherChanges.Clear();
            HasInitialWeather = false;
            LastWeatherCode = null;
        }
    }
}
