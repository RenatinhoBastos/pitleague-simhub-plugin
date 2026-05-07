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

        /// <summary>Enviar apenas sessões do tipo Race (ignorar Practice/Qualifying)</summary>
        public bool RaceOnlyMode { get; set; } = true;

        /// <summary>Mínimo de pilotos para considerar o resultado válido (evita corridas solo)</summary>
        public int MinDriversToSend { get; set; } = 2;

        // ─── Jogo ─────────────────────────────────────────────────────────────

        /// <summary>Nome do jogo para exibição no histórico (ex: "F1 25", "ACC", "iRacing")</summary>
        public string GameDisplayName { get; set; } = "";

        // ─── Debug ────────────────────────────────────────────────────────────

        /// <summary>Mostrar logs detalhados no SimHub</summary>
        public bool DebugMode { get; set; } = false;

        // ─── F1 25 UDP ───────────────────────────────────────────────────────

        /// <summary>Porta UDP para receber telemetria do F1 25 (padrão: 20777)</summary>
        public int F1_25_UdpPort { get; set; } = 20777;

        // ─── Estado interno (não configurável pelo usuário) ────────────────────

        /// <summary>SessionUID da última sessão enviada (evita duplicatas)</summary>
        public string LastSentSessionUID { get; set; } = "";

        /// <summary>Timestamp do último envio bem-sucedido</summary>
        public DateTime LastSentAt { get; set; } = DateTime.MinValue;

        /// <summary>Status do último envio</summary>
        public string LastSendStatus { get; set; } = "Nunca enviado";
    }
}
