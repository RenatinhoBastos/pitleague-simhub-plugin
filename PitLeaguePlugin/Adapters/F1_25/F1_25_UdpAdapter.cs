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
    /// F1 25 UDP telemetry adapter with relay support.
    /// Listens on a dedicated port (default 20778) and forwards packets to SimHub (default 20777).
    /// This avoids port conflicts with SimHub's own UDP listener.
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

        // Config
        private readonly int _listenPort;
        private readonly int _forwardPort;
        private readonly bool _forwardEnabled;

        // Sockets
        private UdpClient _udpClient;
        private UdpClient _forwardClient;
        private IPEndPoint _forwardEndpoint;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private volatile bool _running;

        // Forward stats
        private long _forwardPacketsSent;
        private long _forwardErrors;

        // Public properties for heartbeat
        public bool IsListening => _running && _udpClient != null;
        public int ListenPort => _listenPort;
        public int ForwardPort => _forwardPort;
        public bool ForwardEnabled => _forwardEnabled;
        public long ForwardPacketsSent => Interlocked.Read(ref _forwardPacketsSent);
        public long ForwardErrors => Interlocked.Read(ref _forwardErrors);

        // Accumulated state
        private SessionState _session = new SessionState();
        private ParticipantsMap _participants = new ParticipantsMap();
        private Dictionary<byte, LapBuffer> _lapBuffers = new Dictionary<byte, LapBuffer>();
        private Dictionary<byte, CarDamageBuffer> _damageBuffers = new Dictionary<byte, CarDamageBuffer>();
        private EventLog _events = new EventLog();
        private Dictionary<byte, SessionHistoryBuffer> _sessionHistoryBuffers = new Dictionary<byte, SessionHistoryBuffer>();
        private List<FinalClassificationEntry> _finalClassification;

        // Stats for diagnostics (guarded by _snapshotLock)
        private Dictionary<byte, int> _packetCounts = new Dictionary<byte, int>();

        // Session metadata frozen at FinalClassification time (before game switches to "Other")
        private string _frozenSessionType;
        private string _frozenSessionTrack;
        private int _frozenSessionTotalLaps;
        private DateTime? _frozenSessionStartedAt;
        private DateTime? _frozenSessionEndedAt;
        private string _frozenWeatherCondition;
        private double? _frozenAirTempStart;
        private double? _frozenAirTempEnd;
        private double? _frozenTrackTempStart;
        private double? _frozenTrackTempEnd;
        private int? _frozenRainPercentageAvg;
        private List<WeatherChange> _frozenWeatherChanges;

        // Lock for thread-safe snapshot capture
        private readonly object _snapshotLock = new object();

        private volatile bool _fcProcessed = false;

        public bool HasFinalClassification => _finalClassification != null && _finalClassification.Count > 0;

        /// <summary>Live session type from UDP SessionData @35 — updates every ~500ms.</summary>
        public string GetSessionType() => _session.Type;

        /// <summary>
        /// Discard the current FinalClassification without resetting _fcProcessed.
        /// Used when FC arrived for a non-race session (e.g. quali): clears HasFinalClassification
        /// so DataUpdate stops looping on the FC block, but keeps _fcProcessed=true to prevent
        /// re-capturing the same quali FC. The next adapter.Reset() (on race entry) will clear
        /// _fcProcessed to allow capturing the race FC.
        /// </summary>
        public void DiscardFinalClassification()
        {
            lock (_snapshotLock)
            {
                _finalClassification = null;
            }
        }

        public F1_25_UdpAdapter(int listenPort = 20778, int forwardPort = 20777, bool forwardEnabled = true)
        {
            _listenPort = listenPort;
            _forwardPort = forwardPort;
            _forwardEnabled = forwardEnabled;
        }

        public bool IsAvailable() => true; // real check happens in Start()

        public bool Start()
        {
            if (_running) return true; // idempotent

            try
            {
                // Listen socket — exclusive on listen port (we own it)
                _udpClient = new UdpClient();
                _udpClient.Client.ExclusiveAddressUse = true;
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _listenPort));

                // Forward socket — sends only, no bind
                if (_forwardEnabled)
                {
                    _forwardClient = new UdpClient();
                    _forwardEndpoint = new IPEndPoint(IPAddress.Loopback, _forwardPort);
                    global::SimHub.Logging.Current.Info(
                        $"[PitLeague:F1_25] UDP relay: listen={_listenPort}, forward=127.0.0.1:{_forwardPort}");
                }
                else
                {
                    global::SimHub.Logging.Current.Info(
                        $"[PitLeague:F1_25] UDP listen={_listenPort}, forward DISABLED");
                }

                _running = true;
                _cts = new CancellationTokenSource();
                _listenTask = Task.Run(() => ListenLoop(_cts.Token));
                return true;
            }
            catch (SocketException sx) when (sx.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                global::SimHub.Logging.Current.Error(
                    $"[PitLeague:F1_25] Port {_listenPort} already in use. " +
                    $"Check that no other plugin is bound to {_listenPort}. " +
                    $"Configure F1 25 UDP Port = {_listenPort}, SimHub Game Config = {_forwardPort}.");
                Cleanup();
                return false;
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Error(
                    $"[PitLeague:F1_25] Failed to start UDP listener: {ex.Message}");
                Cleanup();
                return false;
            }
        }

        // IGameTelemetryAdapter.Start() returns void — bridge
        void IGameTelemetryAdapter.Start() => Start();

        public void Stop()
        {
            _cts?.Cancel();
            Cleanup();
            try { _listenTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _listenTask = null;
            _cts = null;
        }

        private void Cleanup()
        {
            _running = false;
            try { _udpClient?.Close(); } catch { }
            try { _forwardClient?.Close(); } catch { }
            _udpClient = null;
            _forwardClient = null;
        }

        public void Reset()
        {
            _session.Reset();
            _participants.Clear();
            lock (_snapshotLock)
            {
                _lapBuffers.Clear();
                _damageBuffers.Clear();
                _sessionHistoryBuffers.Clear();
                _events.Clear();
                _finalClassification = null;
                _packetCounts.Clear();
            }
            _fcProcessed = false;
            // Reset frozen session metadata
            _frozenSessionType = null;
            _frozenSessionTrack = null;
            _frozenSessionTotalLaps = 0;
            _frozenSessionStartedAt = null;
            _frozenSessionEndedAt = null;
            _frozenWeatherCondition = null;
            _frozenAirTempStart = null;
            _frozenAirTempEnd = null;
            _frozenTrackTempStart = null;
            _frozenTrackTempEnd = null;
            _frozenRainPercentageAvg = null;
            _frozenWeatherChanges = null;
            global::SimHub.Logging.Current.Info("[PitLeague:F1_25] State reset for new session");
        }

        public void Dispose() => Stop();

        // ── Listen loop ──────────────────────────────────────────────────────────

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _running)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync().ConfigureAwait(false);
                    var buffer = result.Buffer;

                    // Forward FIRST — don't make SimHub wait for our parsing
                    ForwardPacket(buffer);

                    // Then parse for our own use
                    if (buffer.Length < PacketHeader.SIZE) continue;
                    try
                    {
                        var header = HeaderParser.Parse(buffer);
                        if (header.PacketFormat != 2025) continue;

                        lock (_snapshotLock)
                        {
                            if (!_packetCounts.ContainsKey(header.PacketId))
                                _packetCounts[header.PacketId] = 0;
                            _packetCounts[header.PacketId]++;
                        }

                        DispatchPacket(header, buffer);
                    }
                    catch (Exception ex)
                    {
                        global::SimHub.Logging.Current.Warn(
                            $"[PitLeague:F1_25] Packet parse error (continuing): {ex.Message}");
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    global::SimHub.Logging.Current.Warn(
                        $"[PitLeague:F1_25] UDP receive error: {ex.Message}");
                }
            }
        }

        private void ForwardPacket(byte[] buffer)
        {
            if (!_forwardEnabled || _forwardClient == null) return;
            try
            {
                _forwardClient.Send(buffer, buffer.Length, _forwardEndpoint);
                Interlocked.Increment(ref _forwardPacketsSent);
            }
            catch (Exception ex)
            {
                var errCount = Interlocked.Increment(ref _forwardErrors);
                if (errCount == 1 || errCount % 1000 == 0)
                {
                    global::SimHub.Logging.Current.Warn(
                        $"[PitLeague:F1_25] Forward to :{_forwardPort} failed (#{errCount}): {ex.Message}");
                }
            }
        }

        // ── Packet dispatch ──────────────────────────────────────────────────────

        private void DispatchPacket(PacketHeader header, byte[] bytes)
        {
            switch (header.PacketId)
            {
                case PacketIds.Session:
                    SessionDataParser.Apply(_session, bytes);
                    break;
                case PacketIds.LapData:
                    lock (_snapshotLock) { LapDataParser.Apply(_lapBuffers, bytes); }
                    break;
                case PacketIds.Event:
                    lock (_snapshotLock) { EventParser.Apply(_events, bytes); }
                    break;
                case PacketIds.Participants:
                    ParticipantsParser.Apply(_participants, bytes);
                    break;
                case PacketIds.CarDamage:
                    lock (_snapshotLock) { CarDamageParser.Apply(_damageBuffers, bytes); }
                    break;
                case PacketIds.SessionHistory:
                    try
                    {
                        lock (_snapshotLock) { SessionHistoryParser.Apply(_sessionHistoryBuffers, bytes); }
                    }
                    catch (Exception shEx)
                    {
                        global::SimHub.Logging.Current.Warn(
                            $"[PitLeague:F1_25] SessionHistory parse error (sectors will be null): {shEx.Message}");
                    }
                    break;
                case PacketIds.FinalClassification:
                    if (_fcProcessed) break;
                    _fcProcessed = true;

                    if (bytes.Length != FinalClassificationParser.EXPECTED_PACKET_SIZE)
                    {
                        global::SimHub.Logging.Current.Warn(
                            $"[PitLeague:F1_25] FinalClassification size {bytes.Length} != " +
                            $"{FinalClassificationParser.EXPECTED_PACKET_SIZE}. Validate offsets.");
                    }
                    lock (_snapshotLock)
                    {
                        _finalClassification = FinalClassificationParser.Parse(bytes);
                    }
                    // Freeze session metadata NOW — before the game switches to "Other"/next track
                    _frozenSessionType = _session.Type;
                    _frozenSessionTrack = _session.Track;
                    _frozenSessionTotalLaps = _session.TotalLaps;
                    _frozenSessionStartedAt = _session.StartedAt;
                    _frozenSessionEndedAt = _session.EndedAt ?? DateTime.UtcNow;
                    _frozenWeatherCondition = _session.WeatherCondition;
                    _frozenAirTempStart = _session.AirTempStart;
                    _frozenAirTempEnd = _session.AirTempEnd;
                    _frozenTrackTempStart = _session.TrackTempStart;
                    _frozenTrackTempEnd = _session.TrackTempEnd;
                    _frozenRainPercentageAvg = _session.RainPercentageAvg;
                    _frozenWeatherChanges = _session.WeatherChanges != null
                        ? new List<WeatherChange>(_session.WeatherChanges) : null;
                    global::SimHub.Logging.Current.Info(
                        $"[PitLeague:F1_25] FinalClassification received: {_finalClassification.Count} cars | " +
                        $"frozen session: type={_frozenSessionType} track={_frozenSessionTrack}");
                    break;
            }
        }

        // ── Snapshot ─────────────────────────────────────────────────────────────

        public RaceTelemetrySnapshot GetSnapshot()
        {
            // Freeze all mutable collections under lock to prevent
            // "Collection was modified" from concurrent UDP thread
            List<FinalClassificationEntry> frozenClassification;
            Dictionary<byte, LapBuffer> frozenLapBuffers;
            Dictionary<byte, CarDamageBuffer> frozenDamageBuffers;
            Dictionary<byte, SessionHistoryBuffer> frozenHistoryBuffers;
            byte? frozenFastestLapIdx;

            lock (_snapshotLock)
            {
                if (_finalClassification == null || _finalClassification.Count == 0)
                    throw new InvalidOperationException("Final classification not yet received");

                frozenClassification = new List<FinalClassificationEntry>(_finalClassification);
                frozenLapBuffers = new Dictionary<byte, LapBuffer>(_lapBuffers);
                frozenDamageBuffers = new Dictionary<byte, CarDamageBuffer>(_damageBuffers);
                frozenHistoryBuffers = new Dictionary<byte, SessionHistoryBuffer>(_sessionHistoryBuffers);
                frozenFastestLapIdx = _events.FastestLapDriverIdx;
            }

            // Use frozen session metadata (captured at FinalClassification time)
            // NOT the live _session which may have switched to "Other"/next track
            var sessionType = _frozenSessionType ?? _session.Type ?? "Race";
            var sessionTrack = _frozenSessionTrack ?? _session.Track ?? "Unknown";

            var snapshot = new RaceTelemetrySnapshot
            {
                SessionUID = $"{sessionTrack}_{(_frozenSessionStartedAt ?? _session.StartedAt)?.ToString("yyyyMMddHHmmss") ?? "unknown"}",
                Game = "F1_25",
                CapturedAt = DateTime.UtcNow,
                Session = new SessionInfo
                {
                    Type = sessionType,
                    Track = sessionTrack,
                    TotalLaps = _frozenSessionTotalLaps > 0 ? _frozenSessionTotalLaps : (_session.TotalLaps > 0 ? _session.TotalLaps : (int?)null),
                    StartedAt = _frozenSessionStartedAt ?? _session.StartedAt,
                    EndedAt = _frozenSessionEndedAt ?? _session.EndedAt,
                    Weather = new WeatherInfo
                    {
                        Condition = _frozenWeatherCondition ?? _session.WeatherCondition,
                        AirTempStart = _frozenAirTempStart ?? _session.AirTempStart,
                        AirTempEnd = _frozenAirTempEnd ?? _session.AirTempEnd,
                        TrackTempStart = _frozenTrackTempStart ?? _session.TrackTempStart,
                        TrackTempEnd = _frozenTrackTempEnd ?? _session.TrackTempEnd,
                        RainPercentageAvg = _frozenRainPercentageAvg ?? _session.RainPercentageAvg,
                        Changes = _frozenWeatherChanges ?? _session.WeatherChanges
                    }
                }
            };

            // Leader reference for gap calculations
            var leader = frozenClassification
                .Where(d => d.ResultStatus == 3)
                .OrderBy(d => d.TotalRaceTime)
                .FirstOrDefault();
            double leaderTime = leader?.TotalRaceTime ?? 0;
            byte leaderLaps = leader?.NumLaps ?? 0;

            for (byte idx = 0; idx < frozenClassification.Count; idx++)
            {
                var fc = frozenClassification[idx];
                var participant = _participants.Get(idx);
                if (participant == null) continue;

                frozenLapBuffers.TryGetValue(idx, out var lapBuf);
                frozenDamageBuffers.TryGetValue(idx, out var dmgBuf);
                frozenHistoryBuffers.TryGetValue(idx, out var histBuf);
                List<PenaltyEntry> penalties;
                int collisions;
                lock (_snapshotLock)
                {
                    penalties = _events.PenaltiesFor(idx);
                    collisions = _events.CollisionsFor(idx);
                }

                // Finalize pit detection before building snapshot
                // PitStopDetails is now idempotent (computed on-demand from _pitByLap)

                var driverResult = new Adapters.DriverResult
                {
                    Gamertag = participant.Name,
                    Position = fc.Position,
                    Status = TyreCompounds.MapResultStatus(fc.ResultStatus),
                    Team = participant.TeamId.ToString(),
                    Gap = "",
                    BestLapTime = FormatLapTime(fc.BestLapTimeInMS),
                    FastestLap = frozenFastestLapIdx.HasValue && frozenFastestLapIdx.Value == idx,
                    PolePosition = fc.GridPosition == 1,
                    PenaltySeconds = fc.PenaltiesTime,
                    GridPosition = fc.GridPosition,
                    TopSpeed = lapBuf?.MaxSpeedTrap,
                    RacePaceGapPct = (leaderTime > 0 && fc.ResultStatus == 3 && fc.TotalRaceTime > leaderTime)
                        ? Math.Round((fc.TotalRaceTime - leaderTime) / leaderTime * 100, 3)
                        : (double?)null,
                    GapToLeaderSec = (fc.ResultStatus == 3 && leaderTime > 0 && fc.Position != 1
                        && fc.NumLaps >= leaderLaps && fc.TotalRaceTime > leaderTime)
                        ? Math.Round(fc.TotalRaceTime - leaderTime, 3)
                        : (fc.ResultStatus == 3 && fc.Position == 1 ? 0 : (double?)null),
                    LapsBehind = (fc.ResultStatus == 3 && leaderLaps > 0 && fc.NumLaps < leaderLaps)
                        ? (int?)(leaderLaps - fc.NumLaps) : null,
                    NumPenaltiesAccumulated = fc.NumPenalties,
                    LapTimes = lapBuf?.GetLapTimes(),
                    PitStops = BuildPitStops(fc, lapBuf),
                    TyreStints = BuildStints(fc),
                    Incidents = new Adapters.DriverIncidents
                    {
                        Collisions = collisions,
                        TrackLimitsWarnings = lapBuf?.MaxWarnings ?? 0,
                        CornerCutting = lapBuf?.MaxCornerCutting ?? 0,
                        WingRepairs = dmgBuf?.WingRepairCount ?? 0,
                        Penalties = penalties
                    }
                };

                // ADDITIVE overlay: apply definitive sector times from Session History packet.
                // Isolated try/catch — failure leaves sectors as-is (null from LapBuffer).
                try
                {
                    if (histBuf != null && driverResult.LapTimes != null)
                    {
                        foreach (var lapEntry in driverResult.LapTimes)
                        {
                            var hist = histBuf.GetLap(lapEntry.Lap);
                            if (hist == null) continue;
                            if (hist.S1MS > 0) lapEntry.S1 = FormatSector(hist.S1MS);
                            if (hist.S2MS > 0) lapEntry.S2 = FormatSector(hist.S2MS);
                            if (hist.S3MS > 0) lapEntry.S3 = FormatSector(hist.S3MS);
                        }
                    }
                }
                catch (Exception sectorEx)
                {
                    global::SimHub.Logging.Current.Warn(
                        $"[PitLeague:F1_25] Sector overlay failed for car {idx} (sectors stay null): {sectorEx.Message}");
                }

                snapshot.Drivers.Add(driverResult);
            }

            // ── FIX: Fastest lap from FC (mark the classified driver with best lap time) ──
            {
                uint bestLapMs = uint.MaxValue;
                int bestLapIdx = -1;
                for (int fi = 0; fi < frozenClassification.Count; fi++)
                {
                    var fc = frozenClassification[fi];
                    if (fc.ResultStatus == 3 && fc.BestLapTimeInMS > 0 && fc.BestLapTimeInMS < bestLapMs)
                    {
                        bestLapMs = fc.BestLapTimeInMS;
                        bestLapIdx = fi;
                    }
                }
                if (bestLapIdx >= 0 && bestLapIdx < snapshot.Drivers.Count)
                {
                    // Clear any previous fastest lap flag, set the real one
                    foreach (var d in snapshot.Drivers) d.FastestLap = false;
                    snapshot.Drivers[bestLapIdx].FastestLap = true;
                    try
                    {
                        global::SimHub.Logging.Current.Info(
                            $"[PitLeague:F1_25] FastestLap from FC: P{frozenClassification[bestLapIdx].Position} {snapshot.Drivers[bestLapIdx].Gamertag} — {FormatLapTime(bestLapMs)}");
                    }
                    catch { }
                }
            }

            // ── FIX: Synthetic last lap (fallback when Session History missed the final lap) ──
            {
                int syntheticCount = 0;
                var syntheticCars = new List<string>();
                for (int di = 0; di < snapshot.Drivers.Count && di < frozenClassification.Count; di++)
                {
                    var r = snapshot.Drivers[di];
                    var fc = frozenClassification[di];
                    if (r.LapTimes == null || r.LapTimes.Count == 0) continue;
                    if (fc.NumLaps <= 0 || fc.TotalRaceTime <= 0) continue;
                    if (r.LapTimes.Count >= fc.NumLaps) continue; // all laps present

                    // Sum recorded lap times in ms
                    double sumMs = 0;
                    foreach (var lt in r.LapTimes)
                    {
                        try
                        {
                            if (lt.Time != null && lt.Time.Contains(":"))
                            {
                                var parts = lt.Time.Split(':');
                                sumMs += double.Parse(parts[0]) * 60000 + double.Parse(parts[1]) * 1000;
                            }
                            else if (lt.Time != null)
                            {
                                sumMs += double.Parse(lt.Time) * 1000;
                            }
                        }
                        catch { }
                    }

                    double totalMs = fc.TotalRaceTime * 1000; // TotalRaceTime is seconds (double)
                    double remainingMs = totalMs - sumMs;
                    int missingLaps = fc.NumLaps - r.LapTimes.Count;

                    if (remainingMs > 1000 && remainingMs < 600000 && missingLaps > 0) // sanity: 1s-10min
                    {
                        double perLapMs = remainingMs / missingLaps;
                        for (int m = 0; m < missingLaps; m++)
                        {
                            var ts = TimeSpan.FromMilliseconds(perLapMs);
                            string timeStr = ts.TotalMinutes >= 1
                                ? $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
                                : $"{ts.Seconds}.{ts.Milliseconds:D3}";
                            r.LapTimes.Add(new LapTimeEntry
                            {
                                Lap = r.LapTimes.Count + 1,
                                Time = timeStr,
                                S1 = null, S2 = null, S3 = null,
                                Valid = true,
                                Synthetic = true
                            });
                            syntheticCount++;
                        }
                        syntheticCars.Add(r.Gamertag);
                    }
                }
                if (syntheticCount > 0)
                {
                    try
                    {
                        global::SimHub.Logging.Current.Info(
                            $"[PitLeague:F1_25] Synthetic laps: {syntheticCount} voltas geradas (carros: {string.Join(", ", syntheticCars)})");
                    }
                    catch { }
                }
            }

            return snapshot;
        }

        public Dictionary<string, int> GetPacketCounts()
        {
            lock (_snapshotLock)
            {
                return _packetCounts.ToDictionary(kv => $"packetId_{kv.Key}", kv => kv.Value);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string FormatSector(uint ms)
        {
            if (ms == 0) return null;
            var sec = ms / 1000.0;
            return sec.ToString("F3");
        }

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
                int lapEnd = i < fc.NumTyreStints - 1 ? fc.TyreStintsEndLaps[i] : fc.NumLaps;
                if (lapEnd == 0) lapEnd = fc.NumLaps;
                stints.Add(new TyreStintEntry { Compound = compound, VisualCompound = visual, LapStart = lapStart, LapEnd = lapEnd });
                lapStart = lapEnd + 1;
            }
            return stints;
        }

        private static List<PitStopEntry> BuildPitStops(FinalClassificationEntry fc, LapBuffer lapBuf)
        {
            // Primary source: LapBuffer pit transition detection (reliable, counts actual pit entry/exit)
            if (lapBuf != null && lapBuf.PitStopCount > 0 && lapBuf.PitStopDetails.Count > 0)
            {
                var stops = new List<PitStopEntry>();
                for (int i = 0; i < lapBuf.PitStopDetails.Count; i++)
                {
                    var pit = lapBuf.PitStopDetails[i];
                    // Cross-reference tyre compound change from FC stints
                    string tyreFrom = "", tyreTo = "";
                    if (i < fc.NumTyreStints - 1)
                    {
                        var (_, fromVisual) = TyreCompounds.Decode(fc.TyreStintsActual[i]);
                        var (_, toVisual) = TyreCompounds.Decode(fc.TyreStintsActual[i + 1]);
                        tyreFrom = fromVisual;
                        tyreTo = toVisual;
                    }
                    stops.Add(new PitStopEntry
                    {
                        Lap = pit.Lap,
                        DurationSec = Math.Round(pit.DurationMs / 1000.0, 1),
                        StationarySec = Math.Round(pit.StationaryMs / 1000.0, 1),
                        TyreFrom = tyreFrom,
                        TyreTo = tyreTo,
                    });
                }
                return stops.Count > 0 ? stops : null;
            }

            // Fallback: FC data (fc.NumPitStops may be 0 even with real pit stops)
            if (fc.NumTyreStints > 1)
            {
                var stops = new List<PitStopEntry>();
                for (int i = 0; i < fc.NumTyreStints - 1 && i < 7; i++)
                {
                    int pitLap = fc.TyreStintsEndLaps[i];
                    if (pitLap <= 0) continue;
                    var (_, fromVisual) = TyreCompounds.Decode(fc.TyreStintsActual[i]);
                    var (_, toVisual) = TyreCompounds.Decode(fc.TyreStintsActual[i + 1]);
                    stops.Add(new PitStopEntry { Lap = pitLap, DurationSec = 0, TyreFrom = fromVisual, TyreTo = toVisual });
                }
                return stops.Count > 0 ? stops : null;
            }

            return null;
        }
    }
}
