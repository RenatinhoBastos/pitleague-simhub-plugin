using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using GameReaderCommon;
using Newtonsoft.Json;
using SimHub.Plugins;

namespace PitLeague.SimHub
{
    [PluginDescription("Envia resultados de corrida automaticamente para o PitLeague")]
    [PluginAuthor("PitLeague")]
    [PluginName("PitLeague")]
    public class PitLeaguePlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string VERSION = "2.0.0";

        // ─── SimHub interface ─────────────────────────────────────────────────
        public PluginManager PluginManager { get; set; }
        public ImageSource PictureIcon => null;
        public string LeftMenuTitle => "PitLeague Telemetry";

        // ─── Settings ─────────────────────────────────────────────────────────
        public PitLeaguePluginSettings Settings { get; private set; }

        // ─── Internal state ───────────────────────────────────────────────────
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly HttpClient _heartbeatHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private string _lastSessionType = "";
        private bool _wasInRace = false;
        private bool _resultSentThisSession = false;

        // Snapshot of opponents at end of race (copied from ref GameData)
        private List<OpponentSnapshot> _lastOpponents;
        private string _lastTrackName = "";
        private string _lastGameName = "";
        private int _lastTotalLaps = 0;
        private string _lastSessionTypeName = "";

        // Logging helpers (avoid spam)
        private int _lastLoggedOpponentCount = -1;

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

            // Heartbeat: primeiro envio em 5s, depois a cada 30s
            _heartbeatTimer = new System.Threading.Timer(
                callback: _ => SendHeartbeatSafe(),
                state: null,
                dueTime: TimeSpan.FromSeconds(5),
                period: TimeSpan.FromSeconds(30)
            );

            UpdateStatus("Plugin PitLeague v" + VERSION + " iniciado");

            global::SimHub.Logging.Current.Info($"[PitLeague] Plugin iniciado v{VERSION} | ApiUrl={Settings.ApiBaseUrl} | LeagueId={Settings.LeagueId ?? "(vazio)"} | ApiKey={MaskKey(Settings.ApiKey)} | AutoSend={Settings.AutoSendOnRaceEnd}");
            global::SimHub.Logging.Current.Info("[PitLeague] Heartbeat timer iniciado (30s interval)");
        }

        public void End(PluginManager pluginManager)
        {
            global::SimHub.Logging.Current.Info("[PitLeague] Plugin encerrado");

            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            global::SimHub.Logging.Current.Info("[PitLeague] Heartbeat timer encerrado");

            this.SaveCommonSettings("PitLeagueSettings", Settings);
        }

        // ─── DataUpdate — called every frame by SimHub ────────────────────────

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (data.NewData == null) return;

            var currentType = data.NewData.SessionTypeName ?? "";
            var isRace = currentType.IndexOf("Race", StringComparison.OrdinalIgnoreCase) >= 0
                      || currentType.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0;
            var isInRace = isRace && data.GameRunning;

            // Log session type transitions (not every tick)
            if (currentType != _lastSessionType)
            {
                global::SimHub.Logging.Current.Info($"[PitLeague] Transicao session: '{_lastSessionType}' -> '{currentType}' | isInRace={isInRace} | wasInRace={_wasInRace} | GameRunning={data.GameRunning}");
            }

            // While in race, keep snapshotting data (we need it when race ends)
            if (isInRace)
            {
                SnapshotData(data);
            }

            // Detect transition: was in race → no longer in race
            if (_wasInRace && !isRace && !_resultSentThisSession)
            {
                ResultReadyToSend = true;
                pluginManager.SetPropertyValue("PitLeague.ResultReadyToSend", this.GetType(), true);

                global::SimHub.Logging.Current.Info($"[PitLeague] Corrida finalizada detectada | opponents={_lastOpponents?.Count ?? 0} | track={_lastTrackName ?? "null"} | AutoSend={Settings.AutoSendOnRaceEnd}");

                if (Settings.AutoSendOnRaceEnd)
                {
                    UpdateStatus("Corrida finalizada! Enviando...");
                    Task.Run(() => SendResultFromSnapshot());
                }
                else
                {
                    UpdateStatus("Corrida finalizada! Pronto para enviar.");
                }
            }

            // Detect new race session (reset state)
            if (isRace && !_wasInRace)
            {
                _resultSentThisSession = false;
                ResultReadyToSend = false;
                _lastOpponents = null;
                _lastLoggedOpponentCount = -1;
                global::SimHub.Logging.Current.Info("[PitLeague] Nova sessão de corrida detectada: " + currentType);
            }

            _wasInRace = isRace;
            _lastSessionType = currentType;
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
                // Try to get game name from GameData (may not exist in all SDK versions)
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
                    catch { /* skip opponent if property access fails */ }
                }

                // Log when opponent count changes (avoid spam)
                if (_lastOpponents.Count != _lastLoggedOpponentCount)
                {
                    _lastLoggedOpponentCount = _lastOpponents.Count;
                    global::SimHub.Logging.Current.Info($"[PitLeague] Snapshot capturado: {_lastOpponents.Count} pilotos | track={_lastTrackName} | session={_lastSessionTypeName} | laps={_lastTotalLaps}");
                }
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Warn("[PitLeague] Snapshot error: " + ex.Message);
            }
        }

        // ─── Send result from last snapshot ───────────────────────────────────

        public async Task<bool> SendResultFromSnapshot()
        {
            if (string.IsNullOrEmpty(Settings.ApiKey))
            {
                UpdateStatus("API Key não configurada");
                global::SimHub.Logging.Current.Warn("[PitLeague] SendResult abortado: API Key não configurada");
                return false;
            }
            if (string.IsNullOrEmpty(Settings.LeagueId))
            {
                UpdateStatus("League ID não configurado");
                global::SimHub.Logging.Current.Warn("[PitLeague] SendResult abortado: League ID não configurado");
                return false;
            }
            if (_lastOpponents == null || _lastOpponents.Count == 0)
            {
                UpdateStatus("Nenhum dado de corrida capturado");
                global::SimHub.Logging.Current.Warn("[PitLeague] SendResult abortado: nenhum dado de corrida capturado");
                return false;
            }

            try
            {
                UpdateStatus("Coletando dados...");
                var payload = BuildPayloadFromSnapshot();

                if (payload.Session.Results.Count < Settings.MinDriversToSend)
                {
                    UpdateStatus($"Apenas {payload.Session.Results.Count} pilotos (mínimo: {Settings.MinDriversToSend})");
                    global::SimHub.Logging.Current.Warn($"[PitLeague] SendResult abortado: {payload.Session.Results.Count} pilotos < mínimo {Settings.MinDriversToSend}");
                    return false;
                }

                var url = $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/simhub/result";
                global::SimHub.Logging.Current.Info($"[PitLeague] Enviando resultado: {payload.Session.Results.Count} pilotos para {url}");

                UpdateStatus($"Enviando {payload.Session.Results.Count} pilotos...");

                var json = JsonConvert.SerializeObject(payload);
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
                        ? $" ({result.Matched}/{result.Total} gamertags vinculados)"
                        : "";

                    Settings.LastSentAt = DateTime.UtcNow;
                    Settings.LastSendStatus = "Enviado com sucesso" + matchInfo;
                    _resultSentThisSession = true;
                    ResultReadyToSend = false;
                    IsConnected = true;

                    this.SaveCommonSettings("PitLeagueSettings", Settings);
                    UpdateStatus("Resultado enviado!" + matchInfo);

                    PluginManager?.SetPropertyValue("PitLeague.Connected", this.GetType(), true);
                    PluginManager?.SetPropertyValue("PitLeague.LastSentAt", this.GetType(), DateTime.Now.ToString("dd/MM HH:mm"));
                    PluginManager?.SetPropertyValue("PitLeague.ResultReadyToSend", this.GetType(), false);

                    global::SimHub.Logging.Current.Info($"[PitLeague] Resultado enviado OK | HTTP {(int)response.StatusCode} | matched={result?.Matched ?? 0}/{result?.Total ?? 0}");
                    return true;
                }
                else
                {
                    var erro = $"Erro {(int)response.StatusCode}: {body.Substring(0, Math.Min(body.Length, 200))}";
                    Settings.LastSendStatus = erro;
                    IsConnected = false;
                    UpdateStatus(erro);
                    global::SimHub.Logging.Current.Warn($"[PitLeague] Falha no envio | HTTP {(int)response.StatusCode} | body={body.Substring(0, Math.Min(200, body.Length))}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Settings.LastSendStatus = "Exceção: " + ex.Message;
                IsConnected = false;
                UpdateStatus("Erro: " + ex.Message);
                global::SimHub.Logging.Current.Error($"[PitLeague] Excecao em SendResultFromSnapshot: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // ─── Test connection ──────────────────────────────────────────────────

        public async Task<bool> TestConnection()
        {
            if (string.IsNullOrEmpty(Settings.ApiKey))
            {
                UpdateStatus("API Key não configurada");
                global::SimHub.Logging.Current.Warn("[PitLeague] TestConnection: API Key não configurada");
                return false;
            }

            try
            {
                var url = $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/simhub/test";
                global::SimHub.Logging.Current.Info($"[PitLeague] TestConnection: GET {url}");
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
                    global::SimHub.Logging.Current.Info($"[PitLeague] TestConnection OK | liga={league}");
                    return true;
                }
                else
                {
                    IsConnected = false;
                    var snippet = body.Length > 150 ? body.Substring(0, 150) : body;
                    UpdateStatus($"Erro {(int)response.StatusCode}: {snippet}");
                    global::SimHub.Logging.Current.Warn($"[PitLeague] TestConnection falhou | HTTP {(int)response.StatusCode} | body={body.Substring(0, Math.Min(200, body.Length))}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                IsConnected = false;
                UpdateStatus("Sem conexão: " + ex.Message);
                global::SimHub.Logging.Current.Error($"[PitLeague] TestConnection excecao: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // ─── Heartbeat (fire-and-forget, fail-safe) ───────────────────────────

        private async void SendHeartbeatSafe()
        {
            try
            {
                // Se plugin não está configurado, pula silenciosamente
                if (string.IsNullOrWhiteSpace(Settings?.ApiKey) ||
                    string.IsNullOrWhiteSpace(Settings?.LeagueId) ||
                    string.IsNullOrWhiteSpace(Settings?.ApiBaseUrl))
                {
                    return;
                }

                var payload = new
                {
                    league_id = Settings.LeagueId,
                    hostname = System.Environment.MachineName,
                    plugin_version = VERSION,
                    game_name = _lastGameName.Length > 0 ? _lastGameName : (Settings.GameDisplayName.Length > 0 ? Settings.GameDisplayName : "Unknown"),
                    udp_listening = false,
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
                // NUNCA derrubar o plugin por causa de heartbeat
                global::SimHub.Logging.Current.Warn($"[PitLeague] Heartbeat excecao: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ─── Build payload from snapshot ──────────────────────────────────────

        private PitLeaguePayload BuildPayloadFromSnapshot()
        {
            var results = new List<DriverResult>();
            var fastestLapTime = TimeSpan.MaxValue;
            int fastestLapIdx = -1;

            // Find fastest lap
            for (int i = 0; i < _lastOpponents.Count; i++)
            {
                var bl = _lastOpponents[i].BestLapTime;
                if (bl.HasValue && bl.Value > TimeSpan.Zero && bl.Value < fastestLapTime)
                {
                    fastestLapTime = bl.Value;
                    fastestLapIdx = i;
                }
            }

            // Build results
            for (int i = 0; i < _lastOpponents.Count; i++)
            {
                var o = _lastOpponents[i];
                string bestLapStr = null;
                if (o.BestLapTime.HasValue && o.BestLapTime.Value > TimeSpan.Zero)
                {
                    var t = o.BestLapTime.Value;
                    bestLapStr = $"{(int)t.TotalMinutes}:{t.Seconds:D2}.{t.Milliseconds:D3}";
                }

                results.Add(new DriverResult
                {
                    Position = o.Position > 0 ? o.Position : (i + 1),
                    Gamertag = o.Name.Trim(),
                    Team = o.TeamName,
                    Status = "Finished",
                    BestLapTime = bestLapStr,
                    FastestLap = (i == fastestLapIdx),
                    PolePosition = false,
                    PenaltySeconds = 0,
                    Gap = o.Position == 1 ? "" : ""
                });
            }

            results.Sort((a, b) => a.Position.CompareTo(b.Position));

            // Determine session type for payload
            var sessionType = "Race";
            if (_lastSessionTypeName.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0)
                sessionType = "Sprint";

            return new PitLeaguePayload
            {
                Game = _lastGameName,
                LeagueId = Settings.LeagueId,
                SessionUID = $"{_lastTrackName}_{DateTime.UtcNow:yyyyMMddHHmmss}",
                CapturedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Session = new SessionData
                {
                    Type = sessionType,
                    Track = _lastTrackName,
                    TotalLaps = _lastTotalLaps,
                    Results = results
                }
            };
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private void UpdateStatus(string message)
        {
            LastStatusMessage = message;
            PluginManager?.SetPropertyValue("PitLeague.LastStatus", this.GetType(), message);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─── WPF Settings ─────────────────────────────────────────────────────

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }
    }

    // ─── Snapshot class (safe copy from Opponent, no ref issues) ──────────────

    internal class OpponentSnapshot
    {
        public int Position { get; set; }
        public string Name { get; set; }
        public string TeamName { get; set; }
        public string CarNumber { get; set; }
        public TimeSpan? BestLapTime { get; set; }
        public bool IsPlayer { get; set; }
    }
}
