using System;
using System.Collections.Generic;
using GameReaderCommon;
using SimHub.Plugins;

namespace PitLeague.SimHub.Adapters.Generic
{
    /// <summary>
    /// Generic fallback adapter using SimHub's DataPlugin GameData.
    /// Works with any SimHub-supported game but only provides basic data.
    /// All rich fields (weather, stints, sectors, incidents) are null.
    /// </summary>
    public class GenericSimHubAdapter : IGameTelemetryAdapter
    {
        public string AdapterId => "generic";
        public string SchemaVersion => "pitleague-2.1";
        public string[] RichDataAvailable => new[] { "basicResult" };

        private RaceTelemetrySnapshot _snapshot;
        private readonly string _gameName;

        public bool HasFinalClassification => _snapshot != null;

        public GenericSimHubAdapter(string gameName = "Unknown")
        {
            _gameName = gameName;
        }

        public bool IsAvailable() => true; // always available as fallback

        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public void Reset()
        {
            _snapshot = null;
        }

        /// <summary>
        /// Build snapshot from SimHub's Opponents data.
        /// Called by the main plugin when race ends (same logic as v2.1.3).
        /// </summary>
        public void CaptureFromGameData(
            List<OpponentSnapshot> opponents,
            string trackName,
            string sessionTypeName,
            int totalLaps,
            string gameName)
        {
            if (opponents == null || opponents.Count == 0) return;

            // Find fastest lap
            TimeSpan fastestTime = TimeSpan.MaxValue;
            int fastestIdx = -1;
            for (int i = 0; i < opponents.Count; i++)
            {
                var bl = opponents[i].BestLapTime;
                if (bl.HasValue && bl.Value > TimeSpan.Zero && bl.Value < fastestTime)
                {
                    fastestTime = bl.Value;
                    fastestIdx = i;
                }
            }

            var drivers = new List<DriverResult>();
            for (int i = 0; i < opponents.Count; i++)
            {
                var o = opponents[i];
                string bestLapStr = null;
                if (o.BestLapTime.HasValue && o.BestLapTime.Value > TimeSpan.Zero)
                {
                    var t = o.BestLapTime.Value;
                    bestLapStr = $"{(int)t.TotalMinutes}:{t.Seconds:D2}.{t.Milliseconds:D3}";
                }

                drivers.Add(new DriverResult
                {
                    Position = o.Position > 0 ? o.Position : (i + 1),
                    Gamertag = (o.Name ?? "").Trim(),
                    Team = o.TeamName ?? "",
                    Status = "Finished",
                    BestLapTime = bestLapStr,
                    FastestLap = (i == fastestIdx),
                    PolePosition = false,
                    PenaltySeconds = 0,
                    Gap = "",
                    // All rich fields null (generic adapter)
                    QualifyingPosition = null,
                    GridPosition = null,
                    TopSpeed = null,
                    RacePaceGapPct = null,
                    NumPenaltiesAccumulated = null,
                    LapTimes = null,
                    PitStops = null,
                    TyreStints = null,
                    Incidents = null,
                });
            }

            drivers.Sort((a, b) => a.Position.CompareTo(b.Position));

            string sessionType = "Race";
            if (sessionTypeName != null &&
                sessionTypeName.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0)
                sessionType = "Sprint";

            _snapshot = new RaceTelemetrySnapshot
            {
                SessionUID = $"{trackName}_{DateTime.UtcNow:yyyyMMddHHmmss}",
                Game = gameName ?? _gameName,
                CapturedAt = DateTime.UtcNow,
                Session = new SessionInfo
                {
                    Type = sessionType,
                    Track = trackName ?? "Unknown",
                    TotalLaps = totalLaps > 0 ? totalLaps : (int?)null,
                    StartedAt = null,
                    EndedAt = null,
                    Weather = new WeatherInfo() // all null
                },
                Drivers = drivers
            };
        }

        public RaceTelemetrySnapshot GetSnapshot()
        {
            if (_snapshot == null)
                throw new InvalidOperationException("No snapshot available — race data not captured");
            return _snapshot;
        }
    }
}
