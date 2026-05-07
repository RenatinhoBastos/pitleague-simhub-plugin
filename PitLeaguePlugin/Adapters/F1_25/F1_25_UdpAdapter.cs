using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PitLeague.SimHub.Adapters.F1_25.State;
using PitLeague.SimHub.Adapters.F1_25.Udp;

namespace PitLeague.SimHub.Adapters.F1_25
{
    /// <summary>
    /// F1 25 UDP telemetry adapter. Captures rich race data directly from F1 25's UDP stream.
    /// Covers F1, F2, F3 (same game engine, same UDP format).
    /// Default port: 20777 (configurable in F1 25 settings).
    /// </summary>
    public class F1_25_UdpAdapter : IGameTelemetryAdapter
    {
        public string AdapterId => "f125";
        public string SchemaVersion => "pitleague-2.1";
        public string[] RichDataAvailable => new[]
        {
            "weather", "tyreStints", "sectors", "incidents",
            "topSpeed", "gridPosition", "lapTimes", "pitStops"
        };

        private readonly int _port;
        private UdpClient _client;
        private CancellationTokenSource _cts;
        private Task _listenTask;

        // Accumulated state
        private SessionState _session = new SessionState();
        private ParticipantsMap _participants = new ParticipantsMap();
        private Dictionary<byte, LapBuffer> _lapBuffers = new Dictionary<byte, LapBuffer>();
        private Dictionary<byte, CarDamageBuffer> _damageBuffers = new Dictionary<byte, CarDamageBuffer>();
        private EventLog _events = new EventLog();
        private List<FinalClassificationEntry> _finalClassification;

        // Stats for diagnostics
        private Dictionary<byte, int> _packetCounts = new Dictionary<byte, int>();

        public bool HasFinalClassification => _finalClassification != null && _finalClassification.Count > 0;

        public F1_25_UdpAdapter(int port = 20777)
        {
            _port = port;
        }

        public bool IsAvailable()
        {
            try
            {
                using (var probe = new UdpClient(_port))
                {
                    probe.Close();
                }
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public void Start()
        {
            if (_client != null) return; // idempotent

            _cts = new CancellationTokenSource();
            _client = new UdpClient(_port);
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
            global::SimHub.Logging.Current.Info($"[PitLeague:F1_25] UDP listener started on port {_port}");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _client?.Close(); } catch { }
            try { _listenTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _client = null;
            _cts = null;
        }

        public void Reset()
        {
            _session.Reset();
            _participants.Clear();
            _lapBuffers.Clear();
            _damageBuffers.Clear();
            _events.Clear();
            _finalClassification = null;
            _packetCounts.Clear();
            global::SimHub.Logging.Current.Info("[PitLeague:F1_25] State reset for new session");
        }

        public void Dispose() => Stop();

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _client.ReceiveAsync().ConfigureAwait(false);
                    var bytes = result.Buffer;
                    if (bytes.Length < PacketHeader.SIZE) continue;

                    var header = HeaderParser.Parse(bytes);
                    if (header.PacketFormat != 2025) continue;

                    // Track packet counts for diagnostics
                    if (!_packetCounts.ContainsKey(header.PacketId))
                        _packetCounts[header.PacketId] = 0;
                    _packetCounts[header.PacketId]++;

                    DispatchPacket(header, bytes);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    global::SimHub.Logging.Current.Warn($"[PitLeague:F1_25] Listen loop error: {ex.Message}");
                }
            }
        }

        private void DispatchPacket(PacketHeader header, byte[] bytes)
        {
            switch (header.PacketId)
            {
                case PacketIds.Session:
                    SessionDataParser.Apply(_session, bytes);
                    break;

                case PacketIds.LapData:
                    LapDataParser.Apply(_lapBuffers, bytes);
                    break;

                case PacketIds.Event:
                    EventParser.Apply(_events, bytes);
                    break;

                case PacketIds.Participants:
                    ParticipantsParser.Apply(_participants, bytes);
                    break;

                case PacketIds.CarDamage:
                    CarDamageParser.Apply(_damageBuffers, bytes);
                    break;

                case PacketIds.FinalClassification:
                    if (bytes.Length != FinalClassificationParser.EXPECTED_PACKET_SIZE)
                    {
                        global::SimHub.Logging.Current.Warn(
                            $"[PitLeague:F1_25] FinalClassification size {bytes.Length} != {FinalClassificationParser.EXPECTED_PACKET_SIZE}. " +
                            "F1 25 may have been patched — validate offsets.");
                    }
                    _finalClassification = FinalClassificationParser.Parse(bytes);
                    if (_session.EndedAt == null) _session.EndedAt = DateTime.UtcNow;
                    global::SimHub.Logging.Current.Info(
                        $"[PitLeague:F1_25] FinalClassification received: {_finalClassification.Count} cars");
                    break;
            }
        }

        public RaceTelemetrySnapshot GetSnapshot()
        {
            if (_finalClassification == null || _finalClassification.Count == 0)
                throw new InvalidOperationException("Final classification not yet received");

            var snapshot = new RaceTelemetrySnapshot
            {
                SessionUID = $"{_session.Track}_{_session.StartedAt?.ToString("yyyyMMddHHmmss") ?? "unknown"}",
                Game = "F1_25",
                CapturedAt = DateTime.UtcNow,
                Session = new SessionInfo
                {
                    Type = _session.Type ?? "Race",
                    Track = _session.Track ?? "Unknown",
                    TotalLaps = _session.TotalLaps > 0 ? _session.TotalLaps : (int?)null,
                    StartedAt = _session.StartedAt,
                    EndedAt = _session.EndedAt,
                    Weather = new WeatherInfo
                    {
                        Condition = _session.WeatherCondition,
                        AirTempStart = _session.AirTempStart,
                        AirTempEnd = _session.AirTempEnd,
                        TrackTempStart = _session.TrackTempStart,
                        TrackTempEnd = _session.TrackTempEnd,
                        RainPercentageAvg = _session.RainPercentageAvg,
                        Changes = _session.WeatherChanges
                    }
                }
            };

            // Leader race time for pace gap calculation
            double leaderTime = _finalClassification
                .Where(d => d.ResultStatus == 3) // Finished
                .OrderBy(d => d.TotalRaceTime)
                .Select(d => d.TotalRaceTime)
                .FirstOrDefault();

            for (byte idx = 0; idx < _finalClassification.Count; idx++)
            {
                var fc = _finalClassification[idx];
                var participant = _participants.Get(idx);
                if (participant == null) continue;

                _lapBuffers.TryGetValue(idx, out var lapBuf);
                _damageBuffers.TryGetValue(idx, out var dmgBuf);
                var penalties = _events.PenaltiesFor(idx);

                snapshot.Drivers.Add(new Adapters.DriverResult
                {
                    Gamertag = participant.Name,
                    Position = fc.Position,
                    Status = TyreCompounds.MapResultStatus(fc.ResultStatus),
                    Team = participant.TeamId.ToString(),
                    Gap = "",
                    BestLapTime = FormatLapTime(fc.BestLapTimeInMS),
                    FastestLap = _events.FastestLapDriverIdx.HasValue && _events.FastestLapDriverIdx.Value == idx,
                    PolePosition = fc.GridPosition == 1,
                    PenaltySeconds = fc.PenaltiesTime,

                    GridPosition = fc.GridPosition,
                    TopSpeed = lapBuf?.MaxSpeedTrap,
                    RacePaceGapPct = (leaderTime > 0 && fc.ResultStatus == 3 && fc.TotalRaceTime > leaderTime)
                        ? Math.Round((fc.TotalRaceTime - leaderTime) / leaderTime * 100, 3)
                        : (double?)null,
                    NumPenaltiesAccumulated = fc.NumPenalties,

                    LapTimes = lapBuf?.GetLapTimes(),
                    PitStops = BuildPitStops(fc),
                    TyreStints = BuildStints(fc),
                    Incidents = new Adapters.DriverIncidents
                    {
                        Collisions = _events.CollisionsFor(idx),
                        TrackLimitsWarnings = lapBuf?.MaxWarnings ?? 0,
                        CornerCutting = lapBuf?.MaxCornerCutting ?? 0,
                        WingRepairs = dmgBuf?.WingRepairCount ?? 0,
                        Penalties = penalties
                    }
                });
            }

            return snapshot;
        }

        public Dictionary<string, int> GetPacketCounts() =>
            _packetCounts.ToDictionary(kv => $"packetId_{kv.Key}", kv => kv.Value);

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static string FormatLapTime(uint ms)
        {
            if (ms == 0) return null;
            var ts = TimeSpan.FromMilliseconds(ms);
            return ts.TotalMinutes >= 1
                ? $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
                : $"{ts.Seconds}.{ts.Milliseconds:D3}";
        }

        private static List<TyreStintEntry> BuildStints(FinalClassificationEntry fc)
        {
            if (fc.NumTyreStints == 0) return null;

            var stints = new List<TyreStintEntry>();
            int lapStart = 1;

            for (int i = 0; i < fc.NumTyreStints && i < 8; i++)
            {
                var (compound, visual) = TyreCompounds.Decode(fc.TyreStintsActual[i]);
                int lapEnd = i < fc.NumTyreStints - 1
                    ? fc.TyreStintsEndLaps[i]
                    : fc.NumLaps;

                if (lapEnd == 0) lapEnd = fc.NumLaps;

                stints.Add(new TyreStintEntry
                {
                    Compound = compound,
                    VisualCompound = visual,
                    LapStart = lapStart,
                    LapEnd = lapEnd
                });

                lapStart = lapEnd + 1;
            }

            return stints;
        }

        private static List<PitStopEntry> BuildPitStops(FinalClassificationEntry fc)
        {
            if (fc.NumPitStops == 0 || fc.NumTyreStints <= 1) return null;

            var stops = new List<PitStopEntry>();
            for (int i = 0; i < fc.NumTyreStints - 1 && i < 7; i++)
            {
                int pitLap = fc.TyreStintsEndLaps[i];
                if (pitLap <= 0) continue;

                var (fromCompound, fromVisual) = TyreCompounds.Decode(fc.TyreStintsActual[i]);
                var (toCompound, toVisual) = TyreCompounds.Decode(fc.TyreStintsActual[i + 1]);

                stops.Add(new PitStopEntry
                {
                    Lap = pitLap,
                    DurationSec = 0, // not available from FinalClassification
                    TyreFrom = fromVisual,
                    TyreTo = toVisual
                });
            }

            return stops;
        }
    }
}
