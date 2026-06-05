using System;

namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>
    /// Parser for F1 25 SessionData packet (PacketId=1). ~2/s.
    ///
    /// F1 25 body (offsets from byte 0 of raw datagram):
    ///   @29  m_weather, @30 m_trackTemp, @31 m_airTemp, @32 m_totalLaps,
    ///   @33-34 m_trackLength(u16), @35 m_sessionType, @36 m_trackId
    ///
    /// F1 25 session codes (CHANGED from F1 24 — Race moved to 15):
    ///   0=Unknown, 1-4=Practice, 5-9=Qualifying(9=OSQ),
    ///   10-14=Sprint Shootout, 15=Race, 16=Race2, 17=Race3,
    ///   18=TimeTrial, 19=Sprint
    /// </summary>
    public static class SessionDataParser
    {
        private const int OFF_WEATHER      = 29;
        private const int OFF_TRACK_TEMP   = 30;
        private const int OFF_AIR_TEMP     = 31;
        private const int OFF_TOTAL_LAPS   = 32;
        private const int OFF_SESSION_TYPE = 35;  // confirmed by hexdump rc8
        private const int OFF_TRACK_ID     = 36;  // confirmed by hexdump rc8

        // Throttled hexdump every ~20s (covers quali + race in same session)
        private static DateTime _lastDiagTime = DateTime.MinValue;

        public static void Apply(State.SessionState state, byte[] data)
        {
            if (data.Length < 42) return;

            // Throttled hexdump for offset verification
            if ((DateTime.UtcNow - _lastDiagTime).TotalSeconds >= 20)
            {
                _lastDiagTime = DateTime.UtcNow;
                try
                {
                    int n = Math.Min(20, data.Length - 29);
                    var hex = BitConverter.ToString(data, 29, n).Replace("-", " ");
                    global::SimHub.Logging.Current.Info(
                        $"[PitLeague:F1_25] SessionData hex[29..{29+n-1}]: {hex} | " +
                        $"@35={data[35]}(sType) @36={(sbyte)data[36]}(trkId) @32={data[32]}(laps)");
                }
                catch { }
            }

            byte weatherCode = data[OFF_WEATHER];
            sbyte trackTemp = (sbyte)data[OFF_TRACK_TEMP];
            sbyte airTemp = (sbyte)data[OFF_AIR_TEMP];
            byte totalLaps = data[OFF_TOTAL_LAPS];
            byte sessionType = data[OFF_SESSION_TYPE];
            sbyte trackId = (sbyte)data[OFF_TRACK_ID];

            // ALWAYS update type/track/laps — F1 25 changes mid-weekend (OSQ→Race)
            string newType = MapSessionType(sessionType);
            string newTrack = MapTrackId(trackId);

            // Log when type actually changes (passo 3: defensive log at the source)
            if (state.Type != null && state.Type != newType)
            {
                global::SimHub.Logging.Current.Info(
                    $"[PitLeague:F1_25:parser] session type changed: '{state.Type}' -> '{newType}' " +
                    $"(byte={sessionType}) track='{newTrack}' laps={totalLaps}");
            }

            state.SessionTypeCode = sessionType;
            state.Type = newType;
            state.Track = newTrack;
            state.TotalLaps = totalLaps;

            string weatherCondition = TyreCompounds.MapWeatherCode(weatherCode);

            // First update only: capture weather START values
            if (!state.HasInitialWeather)
            {
                state.AirTempStart = airTemp;
                state.TrackTempStart = trackTemp;
                state.StartWeatherCode = weatherCode;
                state.HasInitialWeather = true;

                if (state.StartedAt == null)
                    state.StartedAt = DateTime.UtcNow;
            }

            // Always update "end" values
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

        /// <summary>F1 25 session type → normalized category. 15=Race (was 10 in F1 24).</summary>
        private static string MapSessionType(byte code)
        {
            switch (code)
            {
                case 0: return "Other";
                case 1: case 2: case 3: case 4: return "Other";         // Practice
                case 5: case 6: case 7: case 8: case 9: return "Qualifying";  // Q1-Q3, ShortQ, OSQ
                case 10: case 11: case 12: case 13: case 14: return "Qualifying"; // Sprint Shootout
                case 15: return "Race";
                case 16: return "Race";   // Race 2
                case 17: return "Race";   // Race 3
                case 18: return "Other";  // Time Trial
                case 19: return "Sprint";
                default:
                    global::SimHub.Logging.Current.Warn($"[PitLeague:F1_25] Unknown sessionType byte={code}");
                    return "Other";
            }
        }

        private static string MapTrackId(sbyte id)
        {
            switch (id)
            {
                case 0: return "Melbourne";    case 1: return "Paul Ricard";
                case 2: return "Shanghai";     case 3: return "Bahrain";
                case 4: return "Catalunya";    case 5: return "Monaco";
                case 6: return "Montreal";     case 7: return "Silverstone";
                case 8: return "Hockenheim";   case 9: return "Hungaroring";
                case 10: return "Spa";         case 11: return "Monza";
                case 12: return "Singapore";   case 13: return "Suzuka";
                case 14: return "Abu Dhabi";   case 15: return "Texas";
                case 16: return "Brazil";      case 17: return "Austria";
                case 18: return "Sochi";       case 19: return "Mexico";
                case 20: return "Baku";        case 21: return "Sakhir Short";
                case 22: return "Silverstone Short"; case 23: return "Texas Short";
                case 24: return "Suzuka Short"; case 25: return "Hanoi";
                case 26: return "Zandvoort";   case 27: return "Imola";
                case 28: return "Portimao";    case 29: return "Jeddah";
                case 30: return "Miami";       case 31: return "Las Vegas";
                case 32: return "Losail";      case 33: return "Shanghai";
                default: return $"Track_{id}";
            }
        }
    }
}
