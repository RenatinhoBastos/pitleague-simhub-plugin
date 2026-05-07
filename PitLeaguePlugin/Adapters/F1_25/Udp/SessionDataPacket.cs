using System;

namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>
    /// Parser for F1 25 SessionData packet (PacketId=1).
    /// Extracts weather, temperatures, total laps, session type, track.
    /// Frequency: ~2/s.
    /// </summary>
    public static class SessionDataParser
    {
        // Key offsets after the 29-byte header
        private const int OFF_WEATHER = 29 + 4;          // byte: weather id
        private const int OFF_TRACK_TEMP = 29 + 5;       // int8: track temp °C
        private const int OFF_AIR_TEMP = 29 + 6;         // int8: air temp °C
        private const int OFF_TOTAL_LAPS = 29 + 7;       // uint8
        private const int OFF_TRACK_LENGTH = 29 + 8;     // uint16
        private const int OFF_SESSION_TYPE = 29 + 10;    // uint8
        private const int OFF_TRACK_ID = 29 + 11;        // int8: track id
        // Formula: 29 + 12 (uint8)
        // SessionTimeLeft: 29 + 13 (uint16)
        // SessionDuration: 29 + 15 (uint16)

        public static void Apply(State.SessionState state, byte[] data)
        {
            if (data.Length < 50) return;

            byte weatherCode = data[OFF_WEATHER];
            sbyte trackTemp = (sbyte)data[OFF_TRACK_TEMP];
            sbyte airTemp = (sbyte)data[OFF_AIR_TEMP];
            byte totalLaps = data[OFF_TOTAL_LAPS];
            byte sessionType = data[OFF_SESSION_TYPE];
            sbyte trackId = (sbyte)data[OFF_TRACK_ID];

            string weatherCondition = TyreCompounds.MapWeatherCode(weatherCode);

            // First update: capture start values
            if (!state.HasInitialWeather)
            {
                state.AirTempStart = airTemp;
                state.TrackTempStart = trackTemp;
                state.StartWeatherCode = weatherCode;
                state.HasInitialWeather = true;
                state.TotalLaps = totalLaps;
                state.SessionTypeCode = sessionType;
                state.Type = MapSessionType(sessionType);
                state.Track = MapTrackId(trackId);

                if (state.StartedAt == null)
                    state.StartedAt = DateTime.UtcNow;
            }

            // Always update "end" values (last seen)
            state.AirTempEnd = airTemp;
            state.TrackTempEnd = trackTemp;
            state.EndWeatherCode = weatherCode;

            // Detect weather changes
            if (state.LastWeatherCode.HasValue && state.LastWeatherCode.Value != weatherCode)
            {
                string fromCond = TyreCompounds.MapWeatherCode(state.LastWeatherCode.Value);
                string toCond = TyreCompounds.MapWeatherCode(weatherCode);
                if (fromCond != toCond && fromCond != null && toCond != null)
                {
                    state.WeatherChanges.Add(new WeatherChange
                    {
                        Lap = state.CurrentLap,
                        FromCondition = fromCond,
                        ToCondition = toCond,
                        CapturedAt = DateTime.UtcNow
                    });
                }
            }
            state.LastWeatherCode = weatherCode;

            // Compute weather condition: compare start vs end
            string startCond = TyreCompounds.MapWeatherCode(state.StartWeatherCode);
            string endCond = TyreCompounds.MapWeatherCode(state.EndWeatherCode);
            if (startCond != null && endCond != null)
            {
                state.WeatherCondition = (startCond == endCond) ? startCond : "mixed";
            }
            else
            {
                state.WeatherCondition = weatherCondition;
            }
        }

        private static string MapSessionType(byte code)
        {
            string name = TyreCompounds.MapSessionType(code);
            if (name.Contains("Race") || name == "Race 2" || name == "Race 3") return "Race";
            if (name.Contains("Sprint") && !name.Contains("Shootout")) return "Sprint";
            if (name.Contains("Qualifying") || name.Contains("Shootout")) return "Qualifying";
            return "Other";
        }

        private static string MapTrackId(sbyte id)
        {
            switch (id)
            {
                case 0: return "Melbourne";
                case 1: return "Paul Ricard";
                case 2: return "Shanghai";
                case 3: return "Bahrain";
                case 4: return "Catalunya";
                case 5: return "Monaco";
                case 6: return "Montreal";
                case 7: return "Silverstone";
                case 8: return "Hockenheim";
                case 9: return "Hungaroring";
                case 10: return "Spa";
                case 11: return "Monza";
                case 12: return "Singapore";
                case 13: return "Suzuka";
                case 14: return "Abu Dhabi";
                case 15: return "Texas";
                case 16: return "Brazil";
                case 17: return "Austria";
                case 18: return "Sochi";
                case 19: return "Mexico";
                case 20: return "Baku";
                case 21: return "Sakhir Short";
                case 22: return "Silverstone Short";
                case 23: return "Texas Short";
                case 24: return "Suzuka Short";
                case 25: return "Hanoi";
                case 26: return "Zandvoort";
                case 27: return "Imola";
                case 28: return "Portimao";
                case 29: return "Jeddah";
                case 30: return "Miami";
                case 31: return "Las Vegas";
                case 32: return "Losail";
                case 33: return "Shanghai"; // F1 25 may reuse
                default: return $"Track_{id}";
            }
        }
    }
}
