using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using PitLeague.SimHub.Adapters;
using PitLeague.SimHub.Adapters.F1_25;

namespace PitLeague.SimHub.Capture
{
    /// <summary>
    /// Builds schema 2.1 JSON payload from a RaceTelemetrySnapshot.
    /// Used by both F1_25_UdpAdapter and GenericSimHubAdapter.
    /// </summary>
    public static class PayloadBuilder
    {
        public static string Build(
            RaceTelemetrySnapshot snapshot,
            IGameTelemetryAdapter adapter,
            string leagueId,
            string pluginVersion,
            Dictionary<string, int> udpStats = null)
        {
            var results = snapshot.Drivers.Select(d => new Dictionary<string, object>
            {
                ["gamertag"] = d.Gamertag,
                ["position"] = d.Position,
                ["status"] = d.Status,
                ["team"] = d.Team,
                ["gap"] = d.Gap,
                ["bestLapTime"] = d.BestLapTime,
                ["fastestLap"] = d.FastestLap,
                ["polePosition"] = d.PolePosition,
                ["penaltySeconds"] = d.PenaltySeconds,
                ["qualifyingPosition"] = d.QualifyingPosition,
                ["gridPosition"] = d.GridPosition,
                ["topSpeed"] = d.TopSpeed,
                ["racePaceGapPct"] = d.RacePaceGapPct,
                ["numPenaltiesAccumulated"] = d.NumPenaltiesAccumulated,
                ["lapTimes"] = d.LapTimes,
                ["pitStopDetails"] = d.PitStops,
                ["tyreStints"] = d.TyreStints,
                ["incidents"] = d.Incidents != null ? new Dictionary<string, object>
                {
                    ["collisions"] = d.Incidents.Collisions,
                    ["trackLimitsWarnings"] = d.Incidents.TrackLimitsWarnings,
                    ["cornerCutting"] = d.Incidents.CornerCutting,
                    ["wingRepairs"] = d.Incidents.WingRepairs,
                    ["penalties"] = d.Incidents.Penalties
                } : null,
            }).ToList();

            var payload = new Dictionary<string, object>
            {
                ["schemaVersion"] = adapter.SchemaVersion,
                ["pluginVersion"] = pluginVersion,
                ["source"] = "simhub-plugin",
                ["game"] = snapshot.Game,
                ["leagueId"] = leagueId,
                ["sessionUID"] = snapshot.SessionUID,
                ["capturedAt"] = snapshot.CapturedAt.ToString("O"),
                ["_pitleague"] = new Dictionary<string, object>
                {
                    ["adapter"] = adapter.AdapterId,
                    ["richDataAvailable"] = adapter.RichDataAvailable,
                    ["udpPacketsReceived"] = udpStats,
                },
                ["session"] = new Dictionary<string, object>
                {
                    ["type"] = snapshot.Session.Type,
                    ["track"] = snapshot.Session.Track,
                    ["totalLaps"] = snapshot.Session.TotalLaps,
                    ["startedAt"] = snapshot.Session.StartedAt?.ToString("O"),
                    ["endedAt"] = snapshot.Session.EndedAt?.ToString("O"),
                    ["weather"] = new Dictionary<string, object>
                    {
                        ["condition"] = snapshot.Session.Weather?.Condition,
                        ["airTempStart"] = snapshot.Session.Weather?.AirTempStart,
                        ["airTempEnd"] = snapshot.Session.Weather?.AirTempEnd,
                        ["trackTempStart"] = snapshot.Session.Weather?.TrackTempStart,
                        ["trackTempEnd"] = snapshot.Session.Weather?.TrackTempEnd,
                        ["rainPercentageAvg"] = snapshot.Session.Weather?.RainPercentageAvg,
                        ["changes"] = snapshot.Session.Weather?.Changes,
                    },
                    ["results"] = results,
                },
            };

            return JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Include,
                Formatting = Formatting.None,
            });
        }
    }
}
