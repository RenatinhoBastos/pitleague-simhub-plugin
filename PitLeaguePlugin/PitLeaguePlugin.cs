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
    public class PitLeaguePlugin : IPlugin, IWPFSettings, IWPFSettingsV2
    {
        public const string VERSION = "1.0.0";

        // ─── SimHub Interface ─────────────────────────────────────────────────
        public PluginManager PluginManager { get; set; }
        public System.Windows.Media.ImageSource PictureIcon => null;
        public string LeftMenuTitle => "PitLeague Telemetry";

        // ─── Estado interno ───────────────────────────────────────────────────
        public PitLeaguePluginSettings Settings { get; private set; }

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // Controle de sessão
        private string  _currentSessionUID    = "";
        private string  _lastSessionPhase     = "";
        private bool    _resultReadyToSend    = false;
        private bool    _resultSentThisSession = false;
        private int     _polePositionIdx      = -1; // índice do piloto que fez pole

        // Para UI — último status legível
        public string LastStatusMessage { get; private set; } = "Aguardando corrida...";
        public bool   IsConnected       { get; private set; } = false;

        // Evento para atualizar a UI
        public event EventHandler StatusChanged;

        // ─── Init / End ───────────────────────────────────────────────────────

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[PitLeague] Plugin iniciado v" + VERSION);

            Settings = this.ReadCommonSettings<PitLeaguePluginSettings>(
                "PitLeagueSettings",
                () => new PitLeaguePluginSettings()
            );

            // Expor propriedades que aparecem no SimHub Dashboard
            pluginManager.AddProperty("PitLeague.Connected",        this.GetType(), false);
            pluginManager.AddProperty("PitLeague.LastStatus",        this.GetType(), "Aguardando...");
            pluginManager.AddProperty("PitLeague.LastSentAt",        this.GetType(), "");
            pluginManager.AddProperty("PitLeague.ResultReadyToSend", this.GetType(), false);

            UpdateStatus("Plugin PitLeague v" + VERSION + " iniciado ✓");
        }

        public void End(PluginManager pluginManager)
        {
            this.SaveCommonSettings("PitLeagueSettings", Settings);
            SimHub.Logging.Current.Info("[PitLeague] Plugin encerrado");
        }

        // ─── DataUpdate — chamado a cada frame pelo SimHub ────────────────────

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Jogo não está rodando
            if (!data.GameRunning)
            {
                _resultReadyToSend = false;
                return;
            }

            // Só processar sessões do tipo Race (quando RaceOnlyMode = true)
            var sessionType = data.GameData?.SessionTypeName ?? "";
            if (Settings.RaceOnlyMode && !sessionType.Contains("Race"))
                return;

            // Detectar mudança de sessão (novo UUID = nova corrida)
            var sessionUID = GetSessionUID(pluginManager, data);
            if (sessionUID != _currentSessionUID)
            {
                _currentSessionUID     = sessionUID;
                _resultSentThisSession = false;
                _resultReadyToSend     = false;
                _lastSessionPhase      = "";
                _polePositionIdx       = -1;

                if (Settings.DebugMode)
                    SimHub.Logging.Current.Info("[PitLeague] Nova sessão detectada: " + sessionUID);
            }

            // Detectar fase da sessão
            var phase = data.GameData?.SessionPhase ?? "";
            if (phase == _lastSessionPhase) return; // nada mudou
            _lastSessionPhase = phase;

            if (Settings.DebugMode)
                SimHub.Logging.Current.Info("[PitLeague] Fase: " + phase + " | Track: " + data.GameData?.TrackName);

            // Capturar pole position durante Qualifying/Formation
            if (phase == "Qualifying" || phase == "Formation")
            {
                _polePositionIdx = 0; // SimHub já ordena por posição
            }

            // ✅ Corrida terminou
            if (phase == "Checkered" || phase == "PostSession")
            {
                if (!_resultSentThisSession)
                {
                    _resultReadyToSend = true;
                    pluginManager.SetPropertyValue("PitLeague.ResultReadyToSend", this.GetType(), true);
                    UpdateStatus("Corrida finalizada! " + (Settings.AutoSendOnRaceEnd ? "Enviando..." : "Pronto para enviar."));

                    if (Settings.AutoSendOnRaceEnd)
                    {
                        Task.Run(() => SendResult(pluginManager, data));
                    }
                }
            }
        }

        // ─── Envio do resultado ───────────────────────────────────────────────

        /// <summary>Chamado pelo botão "Enviar Resultado" na UI ou automaticamente</summary>
        public async Task<bool> SendResult(PluginManager pluginManager, GameData data)
        {
            if (string.IsNullOrEmpty(Settings.ApiKey))
            {
                UpdateStatus("❌ API Key não configurada");
                return false;
            }

            if (string.IsNullOrEmpty(Settings.LeagueId))
            {
                UpdateStatus("❌ League ID não configurado");
                return false;
            }

            try
            {
                UpdateStatus("Coletando dados...");
                var payload = BuildPayload(pluginManager, data);

                if (payload.Session.Results.Count < Settings.MinDriversToSend)
                {
                    UpdateStatus($"❌ Apenas {payload.Session.Results.Count} pilotos (mínimo: {Settings.MinDriversToSend})");
                    return false;
                }

                UpdateStatus($"Enviando {payload.Session.Results.Count} pilotos...");

                var json    = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var url     = $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/simhub/result";

                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + Settings.ApiKey);

                var response = await _http.PostAsync(url, content);
                var body     = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonConvert.DeserializeObject<ApiResponse>(body);
                    var matchInfo = result?.Total > 0
                        ? $" ({result.Matched}/{result.Total} gamertags vinculados)"
                        : "";

                    Settings.LastSentSessionUID = _currentSessionUID;
                    Settings.LastSentAt         = DateTime.UtcNow;
                    Settings.LastSendStatus     = "✅ Enviado com sucesso" + matchInfo;

                    _resultSentThisSession = true;
                    _resultReadyToSend     = false;
                    IsConnected            = true;

                    this.SaveCommonSettings("PitLeagueSettings", Settings);
                    UpdateStatus("✅ Resultado enviado!" + matchInfo);

                    pluginManager.SetPropertyValue("PitLeague.Connected",    this.GetType(), true);
                    pluginManager.SetPropertyValue("PitLeague.LastSentAt",    this.GetType(), DateTime.Now.ToString("dd/MM HH:mm"));
                    pluginManager.SetPropertyValue("PitLeague.ResultReadyToSend", this.GetType(), false);

                    SimHub.Logging.Current.Info("[PitLeague] Resultado enviado ✓" + matchInfo);
                    return true;
                }
                else
                {
                    var erro = $"❌ Erro {(int)response.StatusCode}: {body.Substring(0, Math.Min(body.Length, 200))}";
                    Settings.LastSendStatus = erro;
                    IsConnected = false;
                    UpdateStatus(erro);
                    SimHub.Logging.Current.Error("[PitLeague] Falha no envio: " + body);
                    return false;
                }
            }
            catch (Exception ex)
            {
                var erro = "❌ Exceção: " + ex.Message;
                Settings.LastSendStatus = erro;
                IsConnected = false;
                UpdateStatus(erro);
                SimHub.Logging.Current.Error("[PitLeague] Exceção: " + ex);
                return false;
            }
        }

        /// <summary>Testar conexão com a API (botão de teste na UI)</summary>
        public async Task<bool> TestConnection()
        {
            if (string.IsNullOrEmpty(Settings.ApiKey))
            {
                UpdateStatus("❌ API Key não configurada");
                return false;
            }

            try
            {
                UpdateStatus("Testando conexão...");
                var url = $"{Settings.ApiBaseUrl.TrimEnd('/')}/api/integrations/api-keys";

                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + Settings.ApiKey);

                var response = await _http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    IsConnected = true;
                    UpdateStatus("✅ Conexão OK com o PitLeague");
                    return true;
                }
                else
                {
                    IsConnected = false;
                    UpdateStatus($"❌ API retornou {(int)response.StatusCode} — verifique a API Key");
                    return false;
                }
            }
            catch (Exception ex)
            {
                IsConnected = false;
                UpdateStatus("❌ Sem conexão: " + ex.Message);
                return false;
            }
        }

        // ─── Construção do payload ────────────────────────────────────────────

        private PitLeaguePayload BuildPayload(PluginManager pluginManager, GameData data)
        {
            var gameName  = Settings.GameDisplayName.Length > 0
                ? Settings.GameDisplayName
                : data.GameData?.GameName ?? "Unknown";
            var trackName = data.GameData?.TrackName ?? "Unknown";
            var totalLaps = (int)(data.GameData?.CompletedLaps ?? 0);

            var results = BuildResults(pluginManager, data);

            return new PitLeaguePayload
            {
                Game       = gameName,
                LeagueId   = Settings.LeagueId,
                SessionUID = _currentSessionUID,
                CapturedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Session    = new SessionData
                {
                    Type      = data.GameData?.SessionTypeName ?? "Race",
                    Track     = trackName,
                    TotalLaps = totalLaps,
                    Results   = results
                }
            };
        }

        private List<DriverResult> BuildResults(PluginManager pluginManager, GameData data)
        {
            var results = new List<DriverResult>();
            var fastestLapTime = TimeSpan.MaxValue;
            int fastestLapIdx  = -1;

            // Descobrir a volta mais rápida para marcar FastestLap
            var opponentsCount = data.OpponentsCount;
            for (int i = 0; i < opponentsCount; i++)
            {
                var bestLap = GetOpponentProperty<TimeSpan>(pluginManager, i, "BestLapTime");
                if (bestLap > TimeSpan.Zero && bestLap < fastestLapTime)
                {
                    fastestLapTime = bestLap;
                    fastestLapIdx  = i;
                }
            }

            // Construir lista de resultados
            for (int i = 0; i < opponentsCount; i++)
            {
                var position  = GetOpponentProperty<int>(pluginManager, i, "Position");
                var carName   = GetOpponentProperty<string>(pluginManager, i, "CarName")
                             ?? GetOpponentProperty<string>(pluginManager, i, "DriverName")
                             ?? $"Driver{i + 1}";
                var teamName  = GetOpponentProperty<string>(pluginManager, i, "TeamName") ?? "";
                var isRetired = GetOpponentProperty<bool>(pluginManager, i, "IsRetired");
                var bestLap   = GetOpponentProperty<TimeSpan>(pluginManager, i, "BestLapTime");
                var gap       = GetOpponentProperty<string>(pluginManager, i, "GapToLeader") ?? "";
                var penalty   = GetOpponentProperty<double>(pluginManager, i, "PenaltyTime");

                // Status
                var status = "Finished";
                if (isRetired)   status = "DNF";

                // BestLapTime formatado
                string bestLapStr = null;
                if (bestLap > TimeSpan.Zero)
                    bestLapStr = $"{(int)bestLap.TotalMinutes}:{bestLap.Seconds:D2}.{bestLap.Milliseconds:D3}";

                results.Add(new DriverResult
                {
                    Position        = position > 0 ? position : (i + 1),
                    Gamertag        = carName.Trim(),
                    Team            = teamName,
                    Status          = status,
                    BestLapTime     = bestLapStr,
                    FastestLap      = (i == fastestLapIdx),
                    PolePosition    = (i == _polePositionIdx),
                    PenaltySeconds  = (int)penalty,
                    Gap             = status == "DNF" ? "DNF" : gap
                });
            }

            // Ordenar por posição final
            results.Sort((a, b) => a.Position.CompareTo(b.Position));
            return results;
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private T GetOpponentProperty<T>(PluginManager pm, int index, string property)
        {
            try
            {
                var value = pm.GetPropertyValue($"DataCorePlugin.Opponents[{index}].{property}");
                if (value is T typed) return typed;
                if (value != null)   return (T)Convert.ChangeType(value, typeof(T));
            }
            catch { }
            return default;
        }

        private string GetSessionUID(PluginManager pm, GameData data)
        {
            // Tentar UID nativo do jogo (F1 25 tem sessionUID no pacote)
            try
            {
                var uid = pm.GetPropertyValue("DataCorePlugin.GameRawData.SessionUID")?.ToString();
                if (!string.IsNullOrEmpty(uid) && uid != "0") return uid;
            }
            catch { }

            // Fallback: track + data como UID sintético
            var track = data.GameData?.TrackName ?? "unknown";
            var date  = DateTime.UtcNow.ToString("yyyyMMddHH");
            return $"{track}_{date}";
        }

        private void UpdateStatus(string message)
        {
            LastStatusMessage = message;
            PluginManager?.SetPropertyValue("PitLeague.LastStatus", this.GetType(), message);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─── WPF Settings UI ──────────────────────────────────────────────────

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        public System.Windows.Controls.Control GetWPFSettingsControlV2(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }
    }
}
