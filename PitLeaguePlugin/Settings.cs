using System;

namespace PitLeague.SimHub
{
    /// <summary>
    /// Configurações do plugin PitLeague — salvas automaticamente pelo SimHub
    /// </summary>
    public class PitLeaguePluginSettings
    {
        // ─── Conexão com a API ────────────────────────────────────────────────
        
        /// <summary>API Key gerada em Admin → Integrações → SimHub</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>UUID da liga AV no PitLeague</summary>
        public string LeagueId { get; set; } = "";

        /// <summary>URL base da API (não alterar em produção)</summary>
        public string ApiBaseUrl { get; set; } = "https://app.pitleague.com.br";

        // ─── Comportamento ────────────────────────────────────────────────────

        /// <summary>Enviar resultado automaticamente ao fim da corrida</summary>
        public bool AutoSendOnRaceEnd { get; set; } = false;

        /// <summary>Quando true, envia FC de sessões de Qualifying (seletiva). Default false.</summary>
        public bool SendQualifying { get; set; } = false;

        /// <summary>Mínimo de pilotos para considerar o resultado válido (evita corridas solo)</summary>
        public int MinDriversToSend { get; set; } = 2;

        // ─── Jogo ─────────────────────────────────────────────────────────────

        /// <summary>Nome do jogo para exibição no histórico (ex: "F1 25", "ACC", "iRacing")</summary>
        public string GameDisplayName { get; set; } = "";

        // ─── Debug ────────────────────────────────────────────────────────────

        /// <summary>Mostrar logs detalhados no SimHub</summary>
        public bool DebugMode { get; set; } = false;

        // ─── F1 25 UDP ───────────────────────────────────────────────────────

        // LEGACY — kept for migration logic, ignored after first run
        public int F1_25_UdpPort { get; set; } = 20777;

        /// <summary>Porta onde o plugin escuta UDP do jogo (default 20778, livre de SimHub)</summary>
        public int F1_25_UdpListenPort { get; set; } = 20778;

        /// <summary>Porta para onde o plugin reencaminha pacotes (default 20777, onde SimHub escuta)</summary>
        public int F1_25_UdpForwardPort { get; set; } = 20777;

        /// <summary>Habilita forward para SimHub. Default true.</summary>
        public bool F1_25_UdpForwardEnabled { get; set; } = true;

        /// <summary>Flag one-shot: migration de settings antigas já rodou</summary>
        public bool F1_25_UdpSettingsMigrated { get; set; } = false;

        // ─── Estado interno (não configurável pelo usuário) ────────────────────

        /// <summary>SessionUID da última sessão enviada (evita duplicatas)</summary>
        public string LastSentSessionUID { get; set; } = "";

        /// <summary>Timestamp do último envio bem-sucedido</summary>
        public DateTime LastSentAt { get; set; } = DateTime.MinValue;

        /// <summary>Status do último envio</summary>
        public string LastSendStatus { get; set; } = "Nunca enviado";
    }
}
