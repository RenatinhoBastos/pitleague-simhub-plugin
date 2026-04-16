using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PitLeague.SimHub
{
    // ─── Payload enviado para a API ───────────────────────────────────────────

    public class PitLeaguePayload
    {
        [JsonProperty("schemaVersion")]
        public string SchemaVersion { get; set; } = "pitleague-2.0";

        [JsonProperty("source")]
        public string Source { get; set; } = "simhub-plugin";

        [JsonProperty("pluginVersion")]
        public string PluginVersion { get; set; } = PitLeaguePlugin.VERSION;

        [JsonProperty("game")]
        public string Game { get; set; }

        [JsonProperty("leagueId")]
        public string LeagueId { get; set; }

        [JsonProperty("sessionUID")]
        public string SessionUID { get; set; }

        [JsonProperty("capturedAt")]
        public string CapturedAt { get; set; }

        [JsonProperty("session")]
        public SessionData Session { get; set; }
    }

    public class SessionData
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("track")]
        public string Track { get; set; }

        [JsonProperty("totalLaps")]
        public int TotalLaps { get; set; }

        [JsonProperty("results")]
        public List<DriverResult> Results { get; set; } = new List<DriverResult>();
    }

    public class DriverResult
    {
        [JsonProperty("position")]
        public int Position { get; set; }

        [JsonProperty("gamertag")]
        public string Gamertag { get; set; }

        [JsonProperty("team")]
        public string Team { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("bestLapTime")]
        public string BestLapTime { get; set; }

        [JsonProperty("fastestLap")]
        public bool FastestLap { get; set; }

        [JsonProperty("polePosition")]
        public bool PolePosition { get; set; }

        [JsonProperty("penaltySeconds")]
        public int PenaltySeconds { get; set; }

        [JsonProperty("gap")]
        public string Gap { get; set; }
    }

    // ─── Resposta da API ──────────────────────────────────────────────────────

    public class ApiResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("matched")]
        public int Matched { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("logId")]
        public string LogId { get; set; }
    }
}
