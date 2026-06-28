using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using GameReaderCommon;
using Newtonsoft.Json;
using SimHub.Plugins;
using PitLeague.SimHub.Adapters;
using PitLeague.SimHub.Adapters.F1_25;
using PitLeague.SimHub.Adapters.Generic;
using PitLeague.SimHub.Capture;

namespace PitLeague.SimHub
{
    [PluginDescription("Envia resultados de corrida automaticamente para o PitLeague")]
    [PluginAuthor("PitLeague")]
    [PluginName("PitLeague")]
    public class PitLeaguePlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string VERSION = "2.8.5-rc6";

        // ─── SimHub interface ─────────────────────────────────────────────────
        public PluginManager PluginManager { get; set; }
        public ImageSource PictureIcon => null;
        public string LeftMenuTitle => "PitLeague Telemetry";

        // ─── Settings ─────────────────────────────────────────────────────────
        public PitLeaguePluginSettings Settings { get; private set; }

        // ─── Adapters ─────────────────────────────────────────────────────────
        private List<IGameTelemetryAdapter> _adapters;
        private IGameTelemetryAdapter _activeAdapter;
        private GenericSimHubAdapter _genericAdapter;
        private F1_25_UdpAdapter _f125Adapter;

        // ─── Internal state ───────────────────────────────────────────────────
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly HttpClient _heartbeatHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Milestone feed (piggybacks on heartbeat)
        private readonly List<object> _milestones = new List<object>();
        private readonly object _milestoneLock = new object();
        private const int MAX_MILESTONES = 8;

        private void AddMilestone(string code, string label, object data = null)
        {
            lock (_milestoneLock)
            {
                _milestones.Add(new { code, label, at = DateTime.UtcNow.ToString("o"), data });
                while (_milestones.Count > MAX_MILESTONES) _milestones.RemoveAt(0);
            }
        }

        private object[] GetMilestonesCopy()
        {
            lock (_milestoneLock) { return _milestones.ToArray(); }
        }

        private string _lastSessionType = "";
        private bool _wasInRace = false;
        private volatile bool _resultSentThisSession = false;
        private volatile bool _resultRejected = false;
        private volatile bool _sendingResult = false;
        private DateTime _lastSendAttempt = DateTime.MinValue;
        private int _sendAttempts = 0;
        private const int MAX_SEND_ATTEMPTS = 3;
        private const int SEND_RETRY_BACKOFF_SECONDS = 5;

        // Snapshot of opponents at end of race (for GenericSimHubAdapter)
        private List<OpponentSnapshot> _lastOpponents;
        private string _lastTrackName = "";
        private string _lastGameName = "";
        private int _lastTotalLaps = 0;
        private string _lastSessionTypeName = "";

        // Last received GameData (for ForceCaptureCurrentState)
        private bool _hasReceivedData = false;
        private GameData _lastReceivedData;

        // Logging helpers (avoid spam)
        private int _lastLoggedOpponentCount = -1;

        // Stall detection
        private DateTime _lastValidDataInRace = DateTime.MinValue;
        private bool _stallLogged = false;
        private const int STALL_TIMEOUT_SECONDS = 10;

        // Heartbeat
        private System.Threading.Timer _heartbeatTimer;

        // Live lap tracking
        private readonly Dictionary<string, int> _driverLapNumbers = new Dictionary<string, int>();
        private string _liveSessionKey = null;
        private static readonly HttpClient _liveLapHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private volatile bool _sendingLiveLap = false;

        // Atomic guard: ensures result dispatch fires exactly once per session
        private int _resultDispatchGuard = 0;
        // Debounce timer: waits for settle window after first FinalClassification
        private System.Threading.Timer _resultDebounceTimer;
        private const int RESULT_SETTLE_MS = 6000; // 6s settle window (wait for Session History rodízio)

        // Persisted JSON result file
        private string _resultJsonDir;
        private string _resultJsonPath;
        private bool _resultJsonHasFinalClassification = false;
        private DateTime _lastJsonWriteTime = DateTime.MinValue;
        private const int JSON_WRITE_INTERVAL_MS = 3000; // write every 3s max
        private string _currentSessionUID;

        // UI status
        public string LastStatusMessage { get; private set; } = "Aguardando corrida...";
        public bool IsConnected { get; private set; } = false;
        public bool ResultReadyToSend { get; private set; } = false;
        public bool IsSending => _sendingResult;
        public bool HasPersistedResult => _resultJsonPath != null && File.Exists(_resultJsonPath);
        public event EventHandler StatusChanged;

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 12) return "***";
            return key.Substring(0, 8) + "...";
        }

        // ─── Settings migration ──────────────────────────────────────────────

        private void MigrateUdpSettingsIfNeeded(PitLeaguePluginSettings settings)
        {
            if (settings.F1_25_UdpSettingsMigrated) return;

            if (settings.F1_25_UdpPort == 0 || settings.F1_25_UdpPort == 20777)
            {
                settings.F1_25_UdpListenPort = 20778;
                settings.F1_25_UdpForwardPort = 20777;
                settings.F1_25_UdpForwardEnabled = true;
                global::SimHub.Logging.Current.Info(
                    "[PitLeague] Migrated UDP config: listen=20778, forward=20777 (relay mode). " +
                    "Update F1 25: UDP Port = 20778.");
            }
            else
            {
                settings.F1_25_UdpListenPort = settings.F1_25_UdpPort;
                settings.F1_25_UdpForwardPort = 20777;
                settings.F1_25_UdpForwardEnabled = true;
                global::SimHub.Logging.Current.Info(
                    $"[PitLeague] Preserved custom UDP listen port {settings.F1_25_UdpPort}, " +
                    "added forward to :20777.");
            }

            settings.F1_25_UdpSettingsMigrated = true;
            this.SaveCommonSettings("PitLeagueSettings", settings);
        }

        // ─── Init ─────────────────────────────────────────────────────────────

        public void Init(PluginManager pluginManager)
        {
            Settings = this.ReadCommonSettings<PitLeaguePluginSettings>(
                "PitLeagueSettings",
                () => new PitLeaguePluginSettings()
            );

            pluginManager.AddProperty("PitLeague.Connected", this.GetType(), false);
            pluginManager.AddProperty("PitLeague.LastStatus", this.GetType(), "Aguardando...");
            pluginManager.AddProperty("PitLeague.LastSentAt", this.GetType(), "");
            pluginManager.AddProperty("PitLeague.ResultReadyToSend", this.GetType(), false);
            pluginManager.AddProperty("PitLeague.ActiveAdapter", this.GetType(), "");

            // Migrate UDP settings from v2.2.0 (single port) to v2.3.0 (listen + forward)
            MigrateUdpSettingsIfNeeded(Settings);

            // Initialize adapters: specific first, generic as fallback
            _f125Adapter = new F1_25_UdpAdapter(
                Settings.F1_25_UdpListenPort,
                Settings.F1_25_UdpForwardPort,
                Settings.F1_25_UdpForwardEnabled
            );
            _genericAdapter = new GenericSimHubAdapter(
                Settings.GameDisplayName.Length > 0 ? Settings.GameDisplayName : "Unknown"
            );

            _adapters = new List<IGameTelemetryAdapter> { _f125Adapter, _genericAdapter };

            // Try to activate the F1 25 adapter; fall back to generic
            _activeAdapter = _genericAdapter;
            if (_f125Adapter.Start())
            {
                _activeAdapter = _f125Adapter;
                global::SimHub.Logging.Current.Info(
                    $"[PitLeague] Adapter 'f125' ativado: listen={Settings.F1_25_UdpListenPort}, " +
                    $"forward={(_f125Adapter.ForwardEnabled ? Settings.F1_25_UdpForwardPort.ToString() : "OFF")}");
            }
            else
            {
                global::SimHub.Logging.Current.Warn(
                    "[PitLeague] F1 25 UDP adapter failed to start — using GenericSimHubAdapter fallback");
            }

            pluginManager.SetPropertyValue("PitLeague.ActiveAdapter", this.GetType(), _activeAdapter.AdapterId);

            // Initialize JSON result persistence directory
            _resultJsonDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PitLeague");
            try { Directory.CreateDirectory(_resultJsonDir); } catch { }
            global::SimHub.Logging.Current.Info($"[PitLeague] JSON result dir: {_resultJsonDir}");

            // Heartbeat timer: first in 5s, then every 30s
            _heartbeatTimer = new System.Threading.Timer(
                callback: _ => SendHeartbeatSafe(),
                state: null,
                dueTime: TimeSpan.FromSeconds(5),
                period: TimeSpan.FromSeconds(120)
            );

            UpdateStatus($"Plugin PitLeague v{VERSION} | Adapter: {_activeAdapter.AdapterId}");

            global::SimHub.Logging.Current.Info(
                $"[PitLeague] Plugin iniciado v{VERSION} | Adapter={_activeAdapter.AdapterId} | " +
                $"ApiUrl={Settings.ApiBaseUrl} | LeagueId={Settings.LeagueId ?? "(vazio)"} | " +
                $"ApiKey={MaskKey(Settings.ApiKey)} | AutoSend={Settings.AutoSendOnRaceEnd}");
        }

        public void End(PluginManager pluginManager)
        {
            global::SimHub.Logging.Current.Info("[PitLeague] Plugin encerrado");

            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _resultDebounceTimer?.Dispose();
            _resultDebounceTimer = null;

            foreach (var adapter in _adapters)
            {
                try { adapter.Stop(); } catch { }
                try { adapter.Dispose(); } catch { }
            }

            this.SaveCommonSettings("PitLeagueSettings", Settings);
        }

        // ─── DataUpdate — called every frame by SimHub ────────────────────────

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // ── Check if F1 25 UDP adapter has final classification ──────────
            // Use atomic guard to fire exactly once per session (prevents trigger storm)
            if (_activeAdapter is F1_25_UdpAdapter f125 && f125.HasFinalClassification && !_resultSentThisSession)
            {
                if (Interlocked.CompareExchange(ref _resultDispatchGuard, 1, 0) == 0)
                {
                    var isValidType = _lastSessionType.IndexOf("Race", StringComparison.OrdinalIgnoreCase) >= 0
                        || _lastSessionType.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0
                        || (Settings.SendQualifying
                            && _lastSessionType.IndexOf("Qualifying", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!isValidType)
                    {
                        global::SimHub.Logging.Current.Info(
                            $"[PitLeague] FC de '{_lastSessionType}' descartada — liberando para a próxima sessão.");
                        f125.DiscardFinalClassification();
                        Interlocked.Exchange(ref _resultDispatchGuard, 0);
                        return;
                    }

                    global::SimHub.Logging.Current.Info("[PitLeague] F1 25 UDP: FinalClassification recebida — aguardando settle window de 6s (Session History)");
                    AddMilestone("fc_received", "Bandeira! Processando resultado (6s)...", null);
                    // Debounce: wait 6s for Session History packets to complete all car laps
                    _resultDebounceTimer?.Dispose();
                    _resultDebounceTimer = new System.Threading.Timer(_ =>
                    {
                        global::SimHub.Logging.Current.Info("[PitLeague] Settle window concluída — enriquecendo JSON e disparando resultado");
                        AddMilestone("result_ready", "Resultado pronto", null);
                        PersistResultJson();
                        EnrichJsonWithFinalClassification();
                        TriggerResultReady("f125_final_classification");
                    }, null, RESULT_SETTLE_MS, System.Threading.Timeout.Infinite);
                    _wasInRace = false;
                }
                return;
            }

            // ── Stall detection: race data stopped arriving ──────────────────
            if (_wasInRace && (data.NewData == null || !data.GameRunning))
            {
                if (_lastValidDataInRace != DateTime.MinValue)
                {
                    var stalledSeconds = (DateTime.UtcNow - _lastValidDataInRace).TotalSeconds;

                    if (!_stallLogged)
                    {
                        global::SimHub.Logging.Current.Info($"[PitLeague] Primeiro tick invalido em Race detectado | stalledSeconds={stalledSeconds:F1}");
                        _stallLogged = true;
                    }

                    if (stalledSeconds >= STALL_TIMEOUT_SECONDS && !_resultSentThisSession)
                    {
                        global::SimHub.Logging.Current.Info($"[PitLeague] Stall detectado em Race por {stalledSeconds:F0}s — considerando corrida finalizada | opponents={_lastOpponents?.Count ?? 0}");
                        TriggerResultReady("stall_timeout");
                        _wasInRace = false;
                        _lastValidDataInRace = DateTime.MinValue;
                        _stallLogged = false;
                        return;
                    }
                }
                return;
            }

            // ── Original early return ────────────────────────────────────────
            if (data.NewData == null) return;

            // Store last received data for ForceCaptureCurrentState
            _lastReceivedData = data;
            _hasReceivedData = true;

            // Session type: UDP @35 is PRIMARY (correct offset confirmed by hexdump).
            // SimHub generic only as fallback when UDP is unknown (non-F1-25 games).
            var genericType = data.NewData.SessionTypeName ?? "";
            var udpType = (_activeAdapter is F1_25_UdpAdapter f125Udp) ? (f125Udp.GetSessionType() ?? "") : "";
            var udpIsKnown = udpType.Length > 0 && udpType != "Other" && !udpType.StartsWith("Unknown");
            var currentType = udpIsKnown ? udpType : genericType;

            var isRace = currentType.IndexOf("Race", StringComparison.OrdinalIgnoreCase) >= 0
                      || currentType.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0;
            var isInRace = isRace && data.GameRunning;

            // Log transitions; reset adapter only when ENTERING Race/Sprint
            if (currentType != _lastSessionType && _lastSessionType.Length > 0)
            {
                global::SimHub.Logging.Current.Info(
                    $"[PitLeague] Transicao session: '{_lastSessionType}' -> '{currentType}' " +
                    $"(generic='{genericType}' udp='{udpType}' src={(udpIsKnown ? "UDP" : "generic")}) | " +
                    $"isInRace={isInRace} | wasInRace={_wasInRace} | GameRunning={data.GameRunning}");

                if (isRace)
                {
                    foreach (var adapter in _adapters)
                    {
                        try { adapter.Reset(); } catch { }
                    }
                    Interlocked.Exchange(ref _resultDispatchGuard, 0);
                    _resultDebounceTimer?.Dispose();
                    _resultDebounceTimer = null;
                    _resultJsonHasFinalClassification = false;
                    _currentSessionUID = null;
                }
            }
            else if (currentType != _lastSessionType)
            {
                global::SimHub.Logging.Current.Info(
                    $"[PitLeague] Transicao session: '{_lastSessionType}' -> '{currentType}' " +
                    $"(generic='{genericType}' udp='{udpType}' src={(udpIsKnown ? "UDP" : "generic")}) | " +
                    $"isInRace={isInRace} | wasInRace={_wasInRace} | GameRunning={data.GameRunning}");
            }

            // While in race, keep snapshotting data (for GenericSimHubAdapter)
            if (isInRace)
            {
                SnapshotData(data);
                PersistResultJson();
                DetectAndSendLiveLaps(data);
                if (_lastValidDataInRace == DateTime.MinValue)
                    global::SimHub.Logging.Current.Info("[PitLeague] Race ativa, monitorando stall timeout");
                    AddMilestone("race_active", $"Corrida ativa ({_lastTotalLaps} voltas)", new { laps = _lastTotalLaps });
                _lastValidDataInRace = DateTime.UtcNow;
                _stallLogged = false;
            }

            // Detect transition: was in race → no longer in race
            if (_wasInRace && !isRace && !_resultSentThisSession)
            {
                TriggerResultReady("session_transition");
            }

            // Detect new race session (reset state)
            if (isRace && !_wasInRace)
            {
                _resultSentThisSession = false;
                _resultRejected = false;
                _sendingResult = false;
                _lastSendAttempt = DateTime.MinValue;
                _sendAttempts = 0;
                ResultReadyToSend = false;
                _lastOpponents = null;
                _lastLoggedOpponentCount = -1;
                _lastValidDataInRace = DateTime.MinValue;
                _stallLogged = false;
                _driverLapNumbers.Clear();
                _liveSessionKey = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                _sendingLiveLap = false;
                Interlocked.Exchange(ref _resultDispatchGuard, 0);
                _resultDebounceTimer?.Dispose();
                _resultDebounceTimer = null;
                _resultJsonHasFinalClassification = false;
                _lastJsonWriteTime = DateTime.MinValue;
                _currentSessionUID = null;

                // Reset all adapters for new session
                foreach (var adapter in _adapters)
                {
                    try { adapter.Reset(); } catch { }
                }

                global::SimHub.Logging.Current.Info("[PitLeague] Nova sessão de corrida detectada: " + currentType);
                AddMilestone("session_detected", "Corrida detectada: " + (_lastTrackName ?? "?"), new { track = _lastTrackName, session = currentType });
            }

            _wasInRace = isRace;
            _lastSessionType = currentType;
        }

        // ─── TriggerResultReady ──────────────────────────────────────────────

        private void TriggerResultReady(string reason)
        {
            if (_resultSentThisSession) return;
            if (_resultRejected) return;
            if (_sendingResult) return;
            if (_sendAttempts >= MAX_SEND_ATTEMPTS) return;
            if (_sendAttempts > 0 &&
                (DateTime.UtcNow - _lastSendAttempt).TotalSeconds < SEND_RETRY_BACKOFF_SECONDS)
                return;

            global::SimHub.Logging.Current.Info($"[PitLeague] TriggerResultReady: reason={reason} | adapter={_activeAdapter.AdapterId} | opponents={_lastOpponents?.Count ?? 0} | track={_lastTrackName ?? "null"} | attempt={_sendAttempts + 1}/{MAX_SEND_ATTEMPTS}");

            // If using generic adapter, capture from GameData snapshot
            if (_activeAdapter is GenericSimHubAdapter generic && !generic.HasFinalClassification)
            {
                if (_lastOpponents != null && _lastOpponents.Count > 0)
                {
                    generic.CaptureFromGameData(
                        _lastOpponents, _lastTrackName, _lastSessionTypeName,
                        _lastTotalLaps, _lastGameName
                    );
                }
            }

            if (!_activeAdapter.HasFinalClassification && !HasPersistedResult)
            {
                global::SimHub.Logging.Current.Warn("[PitLeague] TriggerResultReady mas adapter sem dados e sem JSON persistido.");
                UpdateStatus("Corrida finalizada mas sem dados suficientes.");
                return;
            }

            if (!_activeAdapter.HasFinalClassification && HasPersistedResult)
            {
                global::SimHub.Logging.Current.Info(
                    $"[PitLeague] Usando resultado reconstruído do estado ao vivo — FinalClassification não recebida. JSON: {_resultJsonPath}");
            }

            ResultReadyToSend = true;
            PluginManager?.SetPropertyValue("PitLeague.ResultReadyToSend", this.GetType(), true);

            if (Settings.AutoSendOnRaceEnd)
            {
                UpdateStatus("Corrida finalizada! Enviando automaticamente... / Auto-sending...");
                _ = Task.Run(async () =>
                {
                    if (_sendingResult) return;
                    _sendingResult = true;
                    try { await SendResultFromJson(); }
                    catch (Exception ex)
                    {
                        global::SimHub.Logging.Current.Error($"[PitLeague] Auto-send exception: {ex.Message}");
                    }
                    finally { _sendingResult = false; }
                });
            }
            else
            {
                UpdateStatus("Corrida finalizada! Clique 'Enviar Resultado'. / Race finished! Click 'Send Result'.");
            }
        }

        // ─── Force capture (for broadcasters/admins) ─────────────────────────

        public void ForceCaptureCurrentState()
        {
            global::SimHub.Logging.Current.Info("[PitLeague] [CaptureNow] ========== entrada ==========");

            // Path 1: adapter already has data (e.g. F1 25 FinalClassification received)
            if (_activeAdapter.HasFinalClassification)
            {
                global::SimHub.Logging.Current.Info($"[PitLeague] [CaptureNow] adapter '{_activeAdapter.AdapterId}' já tem dados");
                TriggerResultReady("manual_capture_existing_snapshot");
                return;
            }

            // Path 2: generic adapter — try capturing from last GameData
            if (_lastOpponents != null && _lastOpponents.Count > 0)
            {
                global::SimHub.Logging.Current.Info($"[PitLeague] [CaptureNow] snapshot existente com {_lastOpponents.Count} pilotos");
                TriggerResultReady("manual_capture_existing_opponents");
                return;
            }

            // Path 3: try live snapshot
            if (!_hasReceivedData || _lastReceivedData.NewData == null)
            {
                global::SimHub.Logging.Current.Warn("[PitLeague] [CaptureNow] _lastReceivedData.NewData é null");
                UpdateStatus("Sem dados de corrida disponíveis para capturar.\nInicie uma sessão no jogo primeiro.");
                return;
            }

            if (_lastReceivedData.NewData.Opponents == null || _lastReceivedData.NewData.Opponents.Count == 0)
            {
                global::SimHub.Logging.Current.Warn($"[PitLeague] [CaptureNow] Opponents vazio");
                UpdateStatus("Sem pilotos detectados no jogo.\nAguarde a sessão carregar completamente.");
                return;
            }

            global::SimHub.Logging.Current.Info($"[PitLeague] [CaptureNow] tentando SnapshotData com {_lastReceivedData.NewData.Opponents.Count} pilotos");

            try { SnapshotData(_lastReceivedData); }
            catch (Exception snapEx)
            {
                global::SimHub.Logging.Current.Error($"[PitLeague] [CaptureNow] SnapshotData exception: {snapEx.GetType().Name}: {snapEx.Message}");
                UpdateStatus($"Erro ao capturar snapshot: {snapEx.Message}");
                return;
            }

            if (_lastOpponents != null && _lastOpponents.Count > 0)
            {
                global::SimHub.Logging.Current.Info("[PitLeague] [CaptureNow] SnapshotData OK");
                TriggerResultReady("manual_capture_forced_snapshot");
            }
            else
            {
                global::SimHub.Logging.Current.Warn("[PitLeague] [CaptureNow] SnapshotData rodou mas sem pilotos");
                UpdateStatus("Captura falhou: não foi possível extrair lista de pilotos.");
            }
        }

        // ─── Snapshot opponents (safe copy from ref GameData) ─────────────────

        private void SnapshotData(GameData data)
        {
            try
            {
                _lastTrackName = (data.NewData.TrackName ?? "Unknown").Replace("\0", "").Trim();
                try { _lastTotalLaps = data.NewData.TotalLaps; } catch { _lastTotalLaps = 0; }
                _lastSessionTypeName = (data.NewData.SessionTypeName ?? "Race").Replace("\0", "").Trim();
                _lastGameName = Settings.GameDisplayName.Length > 0
                    ? Settings.GameDisplayName
                    : "Unknown";
                try { if (_lastGameName == "Unknown") _lastGameName = data.GameName ?? "Unknown"; } catch { }

                var opponents = data.NewData.Opponents;
                if (opponents == null || opponents.Count == 0) return;

                _lastOpponents = new List<OpponentSnapshot>();
                foreach (var o in opponents)
                {
                    try
                    {
                        _lastOpponents.Add(new OpponentSnapshot
                        {
                            Position = o.Position,
                            Name = (o.Name ?? "").Replace("\0", "").Trim(),
                            TeamName = (o.TeamName ?? "").Replace("\0", "").Trim(),
                            CarNumber = (o.CarNumber?.ToString() ?? "").Replace("\0", "").Trim(),
                            BestLapTime = o.BestLapTime,
                            IsPlayer = o.IsPlayer,
                        });
                    }
                    catch { }
                }

                if (_lastOpponents.Count != _lastLoggedOpponentCount)
                {
                    _lastLoggedOpponentCount = _lastOpponents.Count;
                    global::SimHub.Logging.Current.Info($"[PitLeague] Snapshot: {_lastOpponents.Count} pilotos | track={_lastTrackName} | session={_lastSessionTypeName} | laps={_lastTotalLaps}");
                }
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Warn("[PitLeague] Snapshot error: " + ex.Message);
            }
        }

        // ─── Persist result JSON locally ─────────────────────────────────────

        private void PersistResultJson()
        {
            try
            {
                if (string.IsNullOrEmpty(Settings.LeagueId)) return;

                // Throttle writes
                if ((DateTime.UtcNow - _lastJsonWriteTime).TotalMilliseconds < JSON_WRITE_INTERVAL_MS) return;
                _lastJsonWriteTime = DateTime.UtcNow;

                RaceTelemetrySnapshot snapshot;
                IGameTelemetryAdapter usedAdapter;
                Dictionary<string, int> udpStats = null;
                bool isRich = false;

                // Prefer f125 rich data when FinalClassification is available
                if (_activeAdapter is F1_25_UdpAdapter f125Rich && f125Rich.HasFinalClassification)
                {
                    snapshot = f125Rich.GetSnapshot();
                    usedAdapter = f125Rich;
                    udpStats = f125Rich.GetPacketCounts();
                    isRich = true;
                }
                else if (_lastOpponents != null && _lastOpponents.Count > 0)
                {
                    // Fallback: generic adapter from live state (no FinalClassification yet)
                    var tempAdapter = new GenericSimHubAdapter(
                        Settings.GameDisplayName.Length > 0 ? Settings.GameDisplayName : _lastGameName);
                    tempAdapter.CaptureFromGameData(
                        _lastOpponents, _lastTrackName, _lastSessionTypeName,
                        _lastTotalLaps, _lastGameName);
                    snapshot = tempAdapter.GetSnapshot();
                    usedAdapter = tempAdapter;
                }
                else
                {
                    return; // No data to persist
                }

                var json = PayloadBuilder.Build(
                    snapshot, usedAdapter, Settings.LeagueId, VERSION, udpStats);

                // Atomic write: temp file + rename
                var filePath = Path.Combine(_resultJsonDir, $"last_result_{Settings.LeagueId}.json");
                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json, Encoding.UTF8);
                if (File.Exists(filePath)) File.Delete(filePath);
                File.Move(tempPath, filePath);
                _resultJsonPath = filePath;
                _currentSessionUID = snapshot.SessionUID;

                if (Settings.DebugMode)
                {
                    global::SimHub.Logging.Current.Info(
                        $"[PitLeague] JSON atualizado: {snapshot.Drivers.Count} pilotos | " +
                        $"adapter={usedAdapter.AdapterId} | rich={isRich} | " +
                        $"track={snapshot.Session.Track} | session={snapshot.Session.Type}");
                }
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Warn($"[PitLeague] PersistResultJson error: {ex.Message}");
            }
        }

        private void EnrichJsonWithFinalClassification()
        {
            try
            {
                if (!_activeAdapter.HasFinalClassification) return;
                if (string.IsNullOrEmpty(Settings.LeagueId)) return;

                var filePath = Path.Combine(_resultJsonDir, $"last_result_{Settings.LeagueId}.json");
                if (!File.Exists(filePath))
                {
                    global::SimHub.Logging.Current.Warn("[PitLeague] EnrichJson: JSON file not found, skipping");
                    return;
                }

                // Read existing JSON (has correct session.type/track/sessionUID from live state)
                var existingJson = File.ReadAllText(filePath, Encoding.UTF8);
                var existingPayload = Newtonsoft.Json.Linq.JObject.Parse(existingJson);
                var existingSession = existingPayload["session"] as Newtonsoft.Json.Linq.JObject;

                if (existingSession == null)
                {
                    global::SimHub.Logging.Current.Warn("[PitLeague] EnrichJson: existing JSON has no session object");
                    return;
                }

                // Get FinalClassification snapshot for result data only
                var snapshot = _activeAdapter.GetSnapshot();

                // GUARD: if FinalClassification came with type != Race/Sprint, its session
                // metadata is polluted (game already left the race). Log and skip session fields.
                var fcType = snapshot.Session.Type ?? "Unknown";
                var isValidSession = fcType.IndexOf("Race", StringComparison.OrdinalIgnoreCase) >= 0
                    || fcType.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isValidSession)
                {
                    global::SimHub.Logging.Current.Info(
                        $"[PitLeague] FinalClassification tardia (type={fcType}) ignorada para campos de sessão — " +
                        $"mantendo estado ao vivo {existingSession["type"]}/{existingSession["track"]}");
                }

                // Build new results array from FinalClassification (official positions/status)
                var newResults = snapshot.Drivers.Select(d => new Dictionary<string, object>
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
                    ["gapToLeaderSec"] = d.GapToLeaderSec,
                    ["lapsBehind"] = d.LapsBehind,
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
                    } : (object)null,
                }).ToList();

                // Replace ONLY results in the existing JSON — preserve session.type/track/sessionUID
                existingSession["results"] = Newtonsoft.Json.Linq.JToken.FromObject(newResults,
                    JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Include }));

                // Update _pitleague envelope: adapter, richDataAvailable, udpStats
                if (existingPayload["_pitleague"] is Newtonsoft.Json.Linq.JObject pitleague)
                {
                    pitleague["adapter"] = _activeAdapter.AdapterId;
                    pitleague["richDataAvailable"] = Newtonsoft.Json.Linq.JToken.FromObject(_activeAdapter.RichDataAvailable);
                    pitleague["enrichedWithFinalClassification"] = true;

                    Dictionary<string, int> udpStats = null;
                    if (_activeAdapter is F1_25_UdpAdapter f125)
                        udpStats = f125.GetPacketCounts();
                    if (udpStats != null)
                        pitleague["udpPacketsReceived"] = Newtonsoft.Json.Linq.JToken.FromObject(udpStats);
                }

                // Update session.weather from f125 snapshot (preserving type/track/sessionUID)
                var w = snapshot.Session.Weather;
                bool hasWeather = w != null && !string.IsNullOrEmpty(w.Condition);
                if (hasWeather)
                {
                    existingSession["weather"] = Newtonsoft.Json.Linq.JToken.FromObject(
                        new Dictionary<string, object>
                        {
                            ["condition"] = w.Condition,
                            ["airTempStart"] = w.AirTempStart,
                            ["airTempEnd"] = w.AirTempEnd,
                            ["trackTempStart"] = w.TrackTempStart,
                            ["trackTempEnd"] = w.TrackTempEnd,
                            ["rainPercentageAvg"] = w.RainPercentageAvg,
                            ["changes"] = w.Changes,
                        },
                        JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Include }));
                }

                // Atomic write
                var enrichedJson = existingPayload.ToString(Formatting.None);
                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, enrichedJson, Encoding.UTF8);
                if (File.Exists(filePath)) File.Delete(filePath);
                File.Move(tempPath, filePath);
                _resultJsonPath = filePath;
                _resultJsonHasFinalClassification = true;

                global::SimHub.Logging.Current.Info(
                    $"[PitLeague] JSON enriquecido: adapter={_activeAdapter.AdapterId} | rich=true | " +
                    $"weather={( hasWeather ? "ok" : "null" )} | " +
                    $"session mantido type={existingSession["type"]} track={existingSession["track"]}");
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Warn($"[PitLeague] EnrichJson error: {ex.Message}");
            }
        }

        // ─── Discard FC after qualifying send (success or definitive rejection) ──
        private void DiscardIfQualifying()
        {
            if (_lastSessionType.IndexOf("Qualifying", StringComparison.OrdinalIgnoreCase) >= 0
                && _activeAdapter is F1_25_UdpAdapter f125Discard)
            {
                f125Discard.DiscardFinalClassification();
                global::SimHub.Logging.Current.Info("[PitLeague] FC de Qualifying descartada pós-envio (libera transição).");
            }
        }

        // ─── Send result from persisted JSON ────────────────────────────────

        public async Task<bool> SendResultFromJson()
        {
            if (string.IsNullOrEmpty(Settings.ApiKey))
            {
                UpdateStatus("API Key não configurada / API Key not configured");
                return false;
            }
            if (string.IsNullOrEmpty(Settings.LeagueId))
            {
                UpdateStatus("League ID não configurado / League ID not configured");
                return false;
            }

            var filePath = Path.Combine(_resultJsonDir ?? "", $"last_result_{Settings.LeagueId}.json");
            if (!File.Exists(filePath))
            {
                UpdateStatus("Nenhum resultado disponível para envio / No result available to send");
                global::SimHub.Logging.Current.Warn("[PitLeague] SendResultFromJson: JSON file not found");
                return false;
            }

            try
            {
                var json = File.ReadAllText(filePath, Encoding.UTF8);

                // Block cross-session resubmit
                try
                {
                    var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);
                    var jsonUID = parsed["sessionUID"]?.ToString();
                    if (!string.IsNullOrEmpty(jsonUID) && (_currentSessionUID == null || jsonUID != _currentSessionUID))
                    {
                        global::SimHub.Logging.Current.Warn(
                            $"[PitLeague] Reenvio bloqueado: JSON sessão '{jsonUID}', atual '{_currentSessionUID ?? "(null)"}'");
                        UpdateStatus("Sem corrida nova capturada — resultado anterior não reenviado. / No new race captured — previous result not resent.");
                        return false;
                    }
                }
                catch { }

                if (Settings.DebugMode)
                {
                    var preview = json.Length > 500 ? json.Substring(0, 500) + "..." : json;
                    global::SimHub.Logging.Current.Info(
                        $"[PitLeague] Lendo JSON persistido ({json.Length} chars): {preview}");
                }

                // Parse JSON to validate and log critical fields before sending
                int resultsCount = 0;
                string guardTrack = "";
                string guardUID = "";
                try
                {
                    var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);
                    var sess = parsed["session"];
                    resultsCount = (sess?["results"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
                    guardTrack = sess?["track"]?.ToString() ?? "";
                    guardUID = parsed["sessionUID"]?.ToString() ?? "";
                    global::SimHub.Logging.Current.Info(
                        $"[PitLeague] Enviando JSON: session.type={sess?["type"]} | track={sess?["track"]} | " +
                        $"sessionUID={parsed["sessionUID"]} | results={resultsCount}");
                }
                catch { }

                // Guard: block empty/invalid payloads (ghost sends after session reset)
                if (resultsCount == 0
                    || string.IsNullOrEmpty(guardTrack) || guardTrack == "Unknown"
                    || guardUID.Contains("Unknown"))
                {
                    global::SimHub.Logging.Current.Warn(
                        $"[PitLeague] Envio abortado: payload sem resultados válidos (results={resultsCount} track={guardTrack} uid={guardUID})");
                    UpdateStatus("Envio bloqueado: sem dados de corrida válidos. / Send blocked: no valid race data.");
                    return false;
                }

                var url = $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/simhub/result";
                global::SimHub.Logging.Current.Info(
                    $"[PitLeague] Enviando resultado do JSON persistido para {url}" +
                    (_resultJsonHasFinalClassification ? " (enriquecido com FinalClassification)" : " (reconstruído do estado ao vivo)"));

                UpdateStatus("Enviando resultado... / Sending result...");

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("Authorization", "Bearer " + Settings.ApiKey);
                request.Content = content;

                var response = await _http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonConvert.DeserializeObject<ApiResponse>(body);
                    var matchInfo = result?.Total > 0
                        ? $" ({result.Matched}/{result.Total} gamertags)"
                        : "";

                    Settings.LastSentAt = DateTime.UtcNow;
                    Settings.LastSendStatus = "Dados enviados com sucesso" + matchInfo;
                    _resultSentThisSession = true;
                    ResultReadyToSend = false;
                    IsConnected = true;
                    DiscardIfQualifying();

                    this.SaveCommonSettings("PitLeagueSettings", Settings);
                    UpdateStatus("Dados enviados com sucesso! / Data sent successfully!" + matchInfo);

                    PluginManager?.SetPropertyValue("PitLeague.Connected", this.GetType(), true);
                    PluginManager?.SetPropertyValue("PitLeague.LastSentAt", this.GetType(), DateTime.Now.ToString("dd/MM HH:mm"));
                    PluginManager?.SetPropertyValue("PitLeague.ResultReadyToSend", this.GetType(), false);

                    global::SimHub.Logging.Current.Info(
                        $"[PitLeague] Dados enviados com sucesso | HTTP {(int)response.StatusCode} | " +
                        $"matched={result?.Matched ?? 0}/{result?.Total ?? 0} | logId={result?.LogId ?? "null"}");

                    if (string.IsNullOrEmpty(result?.LogId))
                    {
                        global::SimHub.Logging.Current.Warn(
                            "[PitLeague] ATENÇÃO: 200 mas logId nulo — resultado pode não ter sido persistido / " +
                            "WARNING: 200 but logId is null — result may not have been persisted");
                    }

                    return true;
                }
                else
                {
                    var statusCode = (int)response.StatusCode;
                    var erro = $"Erro {statusCode}: {body.Substring(0, Math.Min(body.Length, 200))}";
                    Settings.LastSendStatus = erro;
                    IsConnected = false;
                    // Keep JSON for retry — do NOT delete
                    global::SimHub.Logging.Current.Warn(
                        $"[PitLeague] Envio falhou | HTTP {statusCode} | {body.Substring(0, Math.Min(500, body.Length))}");
                    UpdateStatus(erro);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Settings.LastSendStatus = "Exceção: " + ex.Message;
                IsConnected = false;
                UpdateStatus("Erro: " + ex.Message);
                global::SimHub.Logging.Current.Error($"[PitLeague] SendResultFromJson exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // ─── Send result using adapter pipeline (legacy — kept for auto-send) ──

        public async Task<bool> SendResultFromSnapshot()
        {
            if (string.IsNullOrEmpty(Settings.ApiKey))
            {
                UpdateStatus("API Key não configurada");
                return false;
            }
            if (string.IsNullOrEmpty(Settings.LeagueId))
            {
                UpdateStatus("League ID não configurado");
                return false;
            }
            if (!_activeAdapter.HasFinalClassification)
            {
                UpdateStatus("Nenhum dado de corrida capturado");
                return false;
            }

            try
            {
                UpdateStatus("Coletando dados...");

                var snapshot = _activeAdapter.GetSnapshot();

                global::SimHub.Logging.Current.Info(
                    $"[PitLeague] Snapshot congelado: {snapshot.Drivers.Count} pilotos | " +
                    $"session.type={snapshot.Session.Type} | track={snapshot.Session.Track}");

                if (snapshot.Drivers.Count < Settings.MinDriversToSend)
                {
                    UpdateStatus($"Apenas {snapshot.Drivers.Count} pilotos (mínimo: {Settings.MinDriversToSend})");
                    return false;
                }

                // Guard: block ghost payloads with invalid session metadata
                var snapTrack = snapshot.Session.Track ?? "";
                var snapUID = snapshot.SessionUID ?? "";
                if (snapshot.Drivers.Count == 0
                    || string.IsNullOrEmpty(snapTrack) || snapTrack == "Unknown"
                    || snapUID.Contains("Unknown"))
                {
                    global::SimHub.Logging.Current.Warn(
                        $"[PitLeague] Envio abortado: payload sem resultados válidos (drivers={snapshot.Drivers.Count} track={snapTrack} uid={snapUID})");
                    UpdateStatus("Envio bloqueado: sem dados de corrida válidos. / Send blocked: no valid race data.");
                    return false;
                }

                // Build JSON using PayloadBuilder (schema 2.1)
                Dictionary<string, int> udpStats = null;
                if (_activeAdapter is F1_25_UdpAdapter f125)
                    udpStats = f125.GetPacketCounts();

                var json = PayloadBuilder.Build(
                    snapshot, _activeAdapter, Settings.LeagueId, VERSION, udpStats
                );

                var url = $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/simhub/result";
                global::SimHub.Logging.Current.Info(
                    $"[PitLeague] Enviando resultado: {snapshot.Drivers.Count} pilotos | results={snapshot.Drivers.Count} | via {_activeAdapter.AdapterId} para {url}");

                UpdateStatus($"Enviando {snapshot.Drivers.Count} pilotos...");

                if (Settings.DebugMode)
                {
                    var preview = json.Length > 500 ? json.Substring(0, 500) + "..." : json;
                    global::SimHub.Logging.Current.Info($"[PitLeague] POST payload preview ({json.Length} chars): {preview}");
                }

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("Authorization", "Bearer " + Settings.ApiKey);
                request.Content = content;

                var response = await _http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonConvert.DeserializeObject<ApiResponse>(body);
                    var matchInfo = result?.Total > 0
                        ? $" ({result.Matched}/{result.Total} gamertags)"
                        : "";

                    Settings.LastSentAt = DateTime.UtcNow;
                    Settings.LastSendStatus = "Enviado via " + _activeAdapter.AdapterId + matchInfo;
                    _resultSentThisSession = true;
                    ResultReadyToSend = false;
                    IsConnected = true;
                    DiscardIfQualifying();

                    this.SaveCommonSettings("PitLeagueSettings", Settings);
                    UpdateStatus("Resultado enviado!" + matchInfo);

                    PluginManager?.SetPropertyValue("PitLeague.Connected", this.GetType(), true);
                    PluginManager?.SetPropertyValue("PitLeague.LastSentAt", this.GetType(), DateTime.Now.ToString("dd/MM HH:mm"));
                    PluginManager?.SetPropertyValue("PitLeague.ResultReadyToSend", this.GetType(), false);

                    global::SimHub.Logging.Current.Info($"[PitLeague] Resultado enviado OK | adapter={_activeAdapter.AdapterId} | HTTP {(int)response.StatusCode} | matched={result?.Matched ?? 0}/{result?.Total ?? 0}");
                    AddMilestone("result_sent", $"Resultado enviado ({result?.Matched ?? 0}/{result?.Total ?? 0} reconhecidos)", new { matched = result?.Matched ?? 0, total = result?.Total ?? 0 });
                    return true;
                }
                else
                {
                    var statusCode = (int)response.StatusCode;
                    var erro = $"Erro {statusCode}: {body.Substring(0, Math.Min(body.Length, 200))}";
                    Settings.LastSendStatus = erro;
                    IsConnected = false;

                    if (statusCode >= 400 && statusCode < 500)
                    {
                        _resultRejected = true;
                        DiscardIfQualifying();
                        global::SimHub.Logging.Current.Warn($"[PitLeague] Resultado REJEITADO pelo servidor (HTTP {statusCode}) — não será reenviado nesta sessão. Body: {body.Substring(0, Math.Min(500, body.Length))}");
                        AddMilestone("result_rejected", $"Falha no envio: HTTP {statusCode}", new { status = statusCode });
                        UpdateStatus($"Resultado rejeitado (HTTP {statusCode}). Corrija o problema e tente na próxima corrida.");
                    }
                    else
                    {
                        global::SimHub.Logging.Current.Warn($"[PitLeague] Falha transiente | HTTP {statusCode} | {body.Substring(0, Math.Min(200, body.Length))}");
                        UpdateStatus(erro);
                        if (_sendAttempts >= MAX_SEND_ATTEMPTS)
                            global::SimHub.Logging.Current.Warn($"[PitLeague] SendResult: desistindo após {MAX_SEND_ATTEMPTS} tentativas falhas nesta sessão");
                            AddMilestone("result_failed", "Desistiu após tentativas", null);
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Settings.LastSendStatus = "Exceção: " + ex.Message;
                IsConnected = false;
                UpdateStatus("Erro: " + ex.Message);
                global::SimHub.Logging.Current.Error($"[PitLeague] SendResult exception: {ex.GetType().Name}: {ex.Message}");
                if (_sendAttempts >= MAX_SEND_ATTEMPTS)
                    global::SimHub.Logging.Current.Warn($"[PitLeague] SendResult: desistindo após {MAX_SEND_ATTEMPTS} tentativas falhas nesta sessão");
                return false;
            }
        }

        // ─── Test connection ──────────────────────────────────────────────────

        public async Task<bool> TestConnection()
        {
            if (string.IsNullOrEmpty(Settings.ApiKey))
            {
                UpdateStatus("API Key não configurada");
                return false;
            }

            try
            {
                var url = $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/simhub/test";
                UpdateStatus("Testando conexão...");

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Authorization", "Bearer " + Settings.ApiKey);

                var response = await _http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(body);
                    string league = json["league"]?.ToString() ?? "";
                    IsConnected = true;
                    UpdateStatus(league.Length > 0 ? $"Conectado à liga: {league}" : "Conexão OK com o PitLeague");
                    return true;
                }
                else
                {
                    IsConnected = false;
                    UpdateStatus($"Erro {(int)response.StatusCode}: {body.Substring(0, Math.Min(body.Length, 150))}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                IsConnected = false;
                UpdateStatus("Sem conexão: " + ex.Message);
                return false;
            }
        }

        // ─── Heartbeat (fire-and-forget, fail-safe) ───────────────────────────

        private async void SendHeartbeatSafe()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Settings?.ApiKey) ||
                    string.IsNullOrWhiteSpace(Settings?.LeagueId) ||
                    string.IsNullOrWhiteSpace(Settings?.ApiBaseUrl))
                    return;

                var f125 = _activeAdapter as F1_25_UdpAdapter;
                var payload = new
                {
                    league_id = Settings.LeagueId,
                    hostname = System.Environment.MachineName,
                    plugin_version = VERSION,
                    game_name = _lastGameName.Length > 0 ? _lastGameName
                        : (Settings.GameDisplayName.Length > 0 ? Settings.GameDisplayName : "Unknown"),
                    udp_listening = f125?.IsListening ?? false,
                    active_adapter = _activeAdapter?.AdapterId ?? "none",
                    schema_version = _activeAdapter?.SchemaVersion ?? "unknown",
                    udp_listen_port = f125?.ListenPort ?? 0,
                    udp_forward_port = f125?.ForwardPort ?? 0,
                    udp_forward_enabled = f125?.ForwardEnabled ?? false,
                    udp_forward_packets_sent = f125?.ForwardPacketsSent ?? 0,
                    udp_forward_errors = f125?.ForwardErrors ?? 0,
                    metadata = new
                    {
                        os = System.Environment.OSVersion.VersionString,
                        captured_laps = _lastTotalLaps,
                        captured_track = _lastTrackName,
                        milestones = GetMilestonesCopy()
                    }
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/simhub/heartbeat"
                );
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Settings.ApiKey}");
                request.Content = content;

                var response = await _heartbeatHttpClient.SendAsync(request).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    global::SimHub.Logging.Current.Warn($"[PitLeague] Heartbeat falhou HTTP {(int)response.StatusCode}: {body.Substring(0, Math.Min(200, body.Length))}");
                }
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Warn($"[PitLeague] Heartbeat excecao: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ─── Live lap detection and sending ──────────────────────────────

        private void DetectAndSendLiveLaps(GameData data)
        {
            if (string.IsNullOrEmpty(_liveSessionKey)) return;
            if (data.NewData?.Opponents == null || data.NewData.Opponents.Count == 0) return;
            if (string.IsNullOrWhiteSpace(Settings?.ApiKey) || string.IsNullOrWhiteSpace(Settings?.LeagueId)) return;

            var completedLaps = new List<object>();

            foreach (var o in data.NewData.Opponents)
            {
                try
                {
                    var name = o.Name?.Trim() ?? "";
                    if (string.IsNullOrEmpty(name)) continue;

                    int currentLap = 0;
                    try { currentLap = o.CurrentLap ?? 0; } catch { continue; }

                    int prevLap;
                    _driverLapNumbers.TryGetValue(name, out prevLap);

                    if (currentLap > prevLap && prevLap > 0)
                    {
                        // Driver completed a lap (lap number increased)
                        int lapTimeMs = 0;
                        try
                        {
                            var lastLap = o.LastLapTime;
                            if (lastLap > TimeSpan.Zero)
                                lapTimeMs = (int)lastLap.TotalMilliseconds;
                        }
                        catch { }

                        if (lapTimeMs <= 0)
                        {
                            _driverLapNumbers[name] = currentLap;
                            continue; // Skip invalid laps
                        }

                        int s1 = 0, s2 = 0, s3 = 0;
                        try { var st = o.LastLapSectorTimes?.GetSectorSplit(1); if (st.HasValue) s1 = (int)st.Value.TotalMilliseconds; } catch { }
                        try { var st = o.LastLapSectorTimes?.GetSectorSplit(2); if (st.HasValue) s2 = (int)st.Value.TotalMilliseconds; } catch { }
                        if (s1 > 0 && s2 > 0 && lapTimeMs > s1 + s2)
                            s3 = lapTimeMs - s1 - s2;

                        int position = 0;
                        try { position = o.Position; } catch { }

                        bool isPit = false;
                        try { isPit = o.IsCarInPitLane; } catch { }

                        string compound = "UNKNOWN";
                        try
                        {
                            var tc = o.FrontTyreCompound;
                            if (!string.IsNullOrEmpty(tc))
                            {
                                var tcUpper = tc.ToUpperInvariant();
                                if (tcUpper.Contains("SOFT")) compound = "SOFT";
                                else if (tcUpper.Contains("MEDIUM")) compound = "MEDIUM";
                                else if (tcUpper.Contains("HARD")) compound = "HARD";
                                else if (tcUpper.Contains("INTER")) compound = "INTER";
                                else if (tcUpper.Contains("WET")) compound = "WET";
                                else compound = tcUpper;
                            }
                        }
                        catch { }

                        int gapMs = 0;
                        try
                        {
                            var gap = o.GaptoLeader;
                            if (gap > 0)
                                gapMs = (int)(gap * 1000);
                        }
                        catch { }

                        completedLaps.Add(new
                        {
                            driver_name = name,
                            lap_number = prevLap,
                            lap_time_ms = lapTimeMs,
                            s1_ms = s1 > 0 ? s1 : (int?)null,
                            s2_ms = s2 > 0 ? s2 : (int?)null,
                            s3_ms = s3 > 0 ? s3 : (int?)null,
                            tire_compound = compound,
                            position = position > 0 ? position : (int?)null,
                            gap_to_leader_ms = gapMs > 0 ? gapMs : (int?)null,
                            is_pit_lap = isPit,
                        });
                    }

                    _driverLapNumbers[name] = currentLap;
                }
                catch { /* Skip problematic opponents */ }
            }

            if (completedLaps.Count > 0 && !_sendingLiveLap)
            {
                string weather = "dry";
                int? airTemp = null;
                int? trackTemp = null;
                int? curLap = null;
                int? totLaps = null;
                try { airTemp = (int?)data.NewData.AirTemperature; } catch { }
                try { trackTemp = (int?)data.NewData.RoadTemperature; } catch { }
                try { totLaps = data.NewData.TotalLaps; } catch { }
                try { curLap = data.NewData.CurrentLap; } catch { }
                foreach (var o in data.NewData.Opponents)
                {
                    try
                    {
                        var tc = o.FrontTyreCompound;
                        if (!string.IsNullOrEmpty(tc))
                        {
                            var u = tc.ToUpperInvariant();
                            if (u.Contains("WET") || u.Contains("INTER")) { weather = "wet"; break; }
                        }
                    }
                    catch { }
                }

                _sendingLiveLap = true;
                _ = Task.Run(async () =>
                {
                    try { await SendLiveLapsSafe(completedLaps, weather, airTemp, trackTemp, curLap, totLaps).ConfigureAwait(false); }
                    finally { _sendingLiveLap = false; }
                });
            }
        }

        private async Task SendLiveLapsSafe(List<object> laps, string weather, int? airTemp, int? trackTemp, int? curLap, int? totLaps)
        {
            try
            {
                var payload = new
                {
                    session_key = _liveSessionKey,
                    laps = laps,
                    weather = weather,
                    air_temp_c = airTemp,
                    track_temp_c = trackTemp,
                    current_lap = curLap,
                    total_laps = totLaps,
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/simhub/live-lap"
                );
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Settings.ApiKey}");
                request.Content = content;

                var response = await _liveLapHttp.SendAsync(request).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    global::SimHub.Logging.Current.Warn($"[PitLeague] LiveLap send failed HTTP {(int)response.StatusCode}: {body.Substring(0, Math.Min(200, body.Length))}");
                }
                else
                {
                    global::SimHub.Logging.Current.Info($"[PitLeague] LiveLap sent: {laps.Count} laps");
                }
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Warn($"[PitLeague] LiveLap exception: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private void UpdateStatus(string message)
        {
            try
            {
                LastStatusMessage = message;
                PluginManager?.SetPropertyValue("PitLeague.LastStatus", this.GetType(), message);
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Warn($"[PitLeague] UpdateStatus falhou: {ex.Message}");
            }
        }

        // ─── WPF Settings ─────────────────────────────────────────────────────

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }
    }

    // ─── Snapshot class (safe copy from Opponent, no ref issues) ──────────────

    public class OpponentSnapshot
    {
        public int Position { get; set; }
        public string Name { get; set; }
        public string TeamName { get; set; }
        public string CarNumber { get; set; }
        public TimeSpan? BestLapTime { get; set; }
        public bool IsPlayer { get; set; }
    }
}
