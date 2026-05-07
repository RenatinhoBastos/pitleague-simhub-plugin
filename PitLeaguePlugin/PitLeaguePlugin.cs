using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
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
        public const string VERSION = "2.2.0";

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

        private string _lastSessionType = "";
        private bool _wasInRace = false;
        private bool _resultSentThisSession = false;

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

        // UI status
        public string LastStatusMessage { get; private set; } = "Aguardando corrida...";
        public bool IsConnected { get; private set; } = false;
        public bool ResultReadyToSend { get; private set; } = false;
        public event EventHandler StatusChanged;

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 12) return "***";
            return key.Substring(0, 8) + "...";
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

            // Initialize adapters: specific first, generic as fallback
            _f125Adapter = new F1_25_UdpAdapter(Settings.F1_25_UdpPort);
            _genericAdapter = new GenericSimHubAdapter(
                Settings.GameDisplayName.Length > 0 ? Settings.GameDisplayName : "Unknown"
            );

            _adapters = new List<IGameTelemetryAdapter> { _f125Adapter, _genericAdapter };

            // Try to activate the first available specific adapter
            _activeAdapter = _genericAdapter; // fallback default
            foreach (var adapter in _adapters)
            {
                if (adapter == _genericAdapter) continue; // skip fallback in priority loop
                try
                {
                    if (adapter.IsAvailable())
                    {
                        adapter.Start();
                        _activeAdapter = adapter;
                        global::SimHub.Logging.Current.Info(
                            $"[PitLeague] Adapter '{adapter.AdapterId}' ativado com sucesso");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    global::SimHub.Logging.Current.Warn(
                        $"[PitLeague] Adapter '{adapter.AdapterId}' falhou ao iniciar: {ex.Message}");
                }
            }

            pluginManager.SetPropertyValue("PitLeague.ActiveAdapter", this.GetType(), _activeAdapter.AdapterId);

            // Heartbeat timer: first in 5s, then every 30s
            _heartbeatTimer = new System.Threading.Timer(
                callback: _ => SendHeartbeatSafe(),
                state: null,
                dueTime: TimeSpan.FromSeconds(5),
                period: TimeSpan.FromSeconds(30)
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
            if (_activeAdapter is F1_25_UdpAdapter f125 && f125.HasFinalClassification && !_resultSentThisSession)
            {
                global::SimHub.Logging.Current.Info("[PitLeague] F1 25 UDP: FinalClassification recebida — disparando resultado");
                TriggerResultReady("f125_final_classification");
                _wasInRace = false;
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

            var currentType = data.NewData.SessionTypeName ?? "";
            var isRace = currentType.IndexOf("Race", StringComparison.OrdinalIgnoreCase) >= 0
                      || currentType.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0;
            var isInRace = isRace && data.GameRunning;

            // Log session type transitions
            if (currentType != _lastSessionType)
            {
                global::SimHub.Logging.Current.Info($"[PitLeague] Transicao session: '{_lastSessionType}' -> '{currentType}' | isInRace={isInRace} | wasInRace={_wasInRace} | GameRunning={data.GameRunning}");
            }

            // While in race, keep snapshotting data (for GenericSimHubAdapter)
            if (isInRace)
            {
                SnapshotData(data);
                if (_lastValidDataInRace == DateTime.MinValue)
                    global::SimHub.Logging.Current.Info("[PitLeague] Race ativa, monitorando stall timeout");
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
                ResultReadyToSend = false;
                _lastOpponents = null;
                _lastLoggedOpponentCount = -1;
                _lastValidDataInRace = DateTime.MinValue;
                _stallLogged = false;

                // Reset all adapters for new session
                foreach (var adapter in _adapters)
                {
                    try { adapter.Reset(); } catch { }
                }

                global::SimHub.Logging.Current.Info("[PitLeague] Nova sessão de corrida detectada: " + currentType);
            }

            _wasInRace = isRace;
            _lastSessionType = currentType;
        }

        // ─── TriggerResultReady ──────────────────────────────────────────────

        private void TriggerResultReady(string reason)
        {
            if (_resultSentThisSession) return;

            global::SimHub.Logging.Current.Info($"[PitLeague] TriggerResultReady: reason={reason} | adapter={_activeAdapter.AdapterId} | opponents={_lastOpponents?.Count ?? 0} | track={_lastTrackName ?? "null"}");

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

            if (!_activeAdapter.HasFinalClassification)
            {
                global::SimHub.Logging.Current.Warn("[PitLeague] TriggerResultReady mas adapter sem dados.");
                UpdateStatus("Corrida finalizada mas sem dados suficientes.");
                return;
            }

            ResultReadyToSend = true;
            PluginManager?.SetPropertyValue("PitLeague.ResultReadyToSend", this.GetType(), true);

            if (Settings?.AutoSendOnRaceEnd == true)
            {
                UpdateStatus("Corrida finalizada! Enviando...");
                _ = Task.Run(async () => await SendResultFromSnapshot().ConfigureAwait(false));
            }
            else
            {
                UpdateStatus("Corrida finalizada! Pronto para enviar.");
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
                _lastTrackName = data.NewData.TrackName ?? "Unknown";
                try { _lastTotalLaps = data.NewData.TotalLaps; } catch { _lastTotalLaps = 0; }
                _lastSessionTypeName = data.NewData.SessionTypeName ?? "Race";
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
                            Name = o.Name ?? "",
                            TeamName = o.TeamName ?? "",
                            CarNumber = o.CarNumber?.ToString() ?? "",
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

        // ─── Send result using adapter pipeline ──────────────────────────────

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

                if (snapshot.Drivers.Count < Settings.MinDriversToSend)
                {
                    UpdateStatus($"Apenas {snapshot.Drivers.Count} pilotos (mínimo: {Settings.MinDriversToSend})");
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
                    $"[PitLeague] Enviando resultado: {snapshot.Drivers.Count} pilotos via {_activeAdapter.AdapterId} para {url}");

                UpdateStatus($"Enviando {snapshot.Drivers.Count} pilotos...");

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

                    this.SaveCommonSettings("PitLeagueSettings", Settings);
                    UpdateStatus("Resultado enviado!" + matchInfo);

                    PluginManager?.SetPropertyValue("PitLeague.Connected", this.GetType(), true);
                    PluginManager?.SetPropertyValue("PitLeague.LastSentAt", this.GetType(), DateTime.Now.ToString("dd/MM HH:mm"));
                    PluginManager?.SetPropertyValue("PitLeague.ResultReadyToSend", this.GetType(), false);

                    global::SimHub.Logging.Current.Info($"[PitLeague] Resultado enviado OK | adapter={_activeAdapter.AdapterId} | HTTP {(int)response.StatusCode} | matched={result?.Matched ?? 0}/{result?.Total ?? 0}");
                    return true;
                }
                else
                {
                    var erro = $"Erro {(int)response.StatusCode}: {body.Substring(0, Math.Min(body.Length, 200))}";
                    Settings.LastSendStatus = erro;
                    IsConnected = false;
                    UpdateStatus(erro);
                    global::SimHub.Logging.Current.Warn($"[PitLeague] Falha | HTTP {(int)response.StatusCode} | {body.Substring(0, Math.Min(200, body.Length))}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Settings.LastSendStatus = "Exceção: " + ex.Message;
                IsConnected = false;
                UpdateStatus("Erro: " + ex.Message);
                global::SimHub.Logging.Current.Error($"[PitLeague] SendResult exception: {ex.GetType().Name}: {ex.Message}");
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

                var payload = new
                {
                    league_id = Settings.LeagueId,
                    hostname = System.Environment.MachineName,
                    plugin_version = VERSION,
                    game_name = _lastGameName.Length > 0 ? _lastGameName
                        : (Settings.GameDisplayName.Length > 0 ? Settings.GameDisplayName : "Unknown"),
                    udp_listening = _activeAdapter is F1_25_UdpAdapter,
                    active_adapter = _activeAdapter?.AdapterId ?? "none",
                    schema_version = _activeAdapter?.SchemaVersion ?? "unknown",
                    metadata = new
                    {
                        os = System.Environment.OSVersion.VersionString,
                        captured_laps = _lastTotalLaps,
                        captured_track = _lastTrackName
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
