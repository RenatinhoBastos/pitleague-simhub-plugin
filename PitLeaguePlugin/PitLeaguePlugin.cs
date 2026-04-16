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
        public const string VERSION = "1.0.0";

        // ─── SimHub interface ─────────────────────────────────────────────────
        public PluginManager PluginManager { get; set; }
        public ImageSource PictureIcon => null;
        public string LeftMenuTitle => "PitLeague Telemetry";

        // ─── Settings ─────────────────────────────────────────────────────────
        public PitLeaguePluginSettings Settings { get; private set; }

        // ─── Internal state ───────────────────────────────────────────────────
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private string _lastSessionType = "";
        private bool _wasInRace = false;
        private bool _resultSentThisSession = false;

        // Snapshot of opponents at end of race (copied from ref GameData)
        private List<OpponentSnapshot> _lastOpponents;
        private string _lastTrackName = "";
        private string _lastGameName = "";
        private int _lastTotalLaps = 0;
        private string _lastSessionTypeName = "";

        // UI status
        public string LastStatusMessage { get; private set; } = "Aguardando corrida...";
        public bool IsConnected { get; private set; } = false;
        public bool ResultReadyToSend { get; private set; } = false;
        public event EventHandler StatusChanged;

        // ─── Init ─────────────────────────────────────────────────────────────

        public void Init(PluginManager pluginManager)
        {
            System.Diagnostics.Debug.WriteLine("[PitLeague] Plugin iniciado v" + VERSION);

            Settings = this.ReadCommonSettings<PitLeaguePluginSettings>(
                "PitLeagueSettings",
                () => new PitLeaguePluginSettings()
            );

            pluginManager.AddProperty("PitLeague.Connected", this.GetType(), false);
            pluginManager.AddProperty("PitLeague.LastStatus", this.GetType(), "Aguardando...");
            pluginManager.AddProperty("PitLeague.LastSentAt", this.GetType(), "");
            pluginManager.AddProperty("PitLeague.ResultReadyToSend", this.GetType(), false);

            UpdateStatus("Plugin PitLeague v" + VERSION + " iniciado");
        }

        public void End(PluginManager pluginManager)
        {
            this.SaveCommonSettings("PitLeagueSettings", Settings);
            System.Diagnostics.Debug.WriteLine("[PitLeague] Plugin encerrado");
        }

        // ─── DataUpdate — called every frame by SimHub ────────────────────────

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (data.NewData == null) return;

            var sessionType = data.NewData.SessionTypeName ?? "";
            var isRace = sessionType.IndexOf("Race", StringComparison.OrdinalIgnoreCase) >= 0
                      || sessionType.IndexOf("Sprint", StringComparison.OrdinalIgnoreCase) >= 0;

            // While in race, keep snapshotting data (we need it when race ends)
            if (isRace && data.GameRunning)
            {
                SnapshotData(data);
            }

            // Detect transition: was in race → no longer in race
            if (_wasInRace && !isRace && !_resultSentThisSession)
            {
                ResultReadyToSend = true;
                pluginManager.SetPropertyValue("PitLeague.ResultReadyToSend", this.GetType(), true);

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
                if (Settings.DebugMode)
                    System.Diagnostics.Debug.WriteLine("[PitLeague] Nova sessão de corrida detectada: " + sessionType);
            }

            _wasInRace = isRace;
            _lastSessionType = sessionType;
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
            }
            catch (Exception ex)
            {
                if (Settings.DebugMode)
                    System.Diagnostics.Debug.WriteLine("[PitLeague] Snapshot error: " + ex.Message);
            }
        }

        // ─── Send result from last snapshot ───────────────────────────────────

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
            if (_lastOpponents == null || _lastOpponents.Count == 0)
            {
                UpdateStatus("Nenhum dado de corrida capturado");
                return false;
            }

            try
            {
                UpdateStatus("Coletando dados...");
                var payload = BuildPayloadFromSnapshot();

                if (payload.Session.Results.Count < Settings.MinDriversToSend)
                {
                    UpdateStatus($"Apenas {payload.Session.Results.Count} pilotos (mínimo: {Settings.MinDriversToSend})");
                    return false;
                }

                UpdateStatus($"Enviando {payload.Session.Results.Count} pilotos...");

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var url = $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/simhub/result";

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

                    System.Diagnostics.Debug.WriteLine("[PitLeague] Resultado enviado" + matchInfo);
                    return true;
                }
                else
                {
                    var erro = $"Erro {(int)response.StatusCode}: {body.Substring(0, Math.Min(body.Length, 200))}";
                    Settings.LastSendStatus = erro;
                    IsConnected = false;
                    UpdateStatus(erro);
                    System.Diagnostics.Debug.WriteLine("[PitLeague] Falha: " + body);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Settings.LastSendStatus = "Exceção: " + ex.Message;
                IsConnected = false;
                UpdateStatus("Erro: " + ex.Message);
                System.Diagnostics.Debug.WriteLine("[PitLeague] Exceção: " + ex);
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
                UpdateStatus("Testando conexão...");
                var url = $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/simhub/test";

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
                    var snippet = body.Length > 150 ? body.Substring(0, 150) : body;
                    UpdateStatus($"Erro {(int)response.StatusCode}: {snippet}");
                    System.Diagnostics.Debug.WriteLine($"[PitLeague] Test failed: {(int)response.StatusCode} {body}");
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
