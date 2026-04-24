#!/usr/bin/env bash
set -euo pipefail

# ═══════════════════════════════════════════════════════════════════
# PitLeague — Deploy plugin DLL para VPS Windows via SSH
#
# Uso:
#   ./scripts/deploy-to-vps.sh              # baixa última release do GitHub
#   ./scripts/deploy-to-vps.sh --local PATH # usa DLL local
#   ./scripts/deploy-to-vps.sh --pull       # git pull no VPS + compila lá (futuro)
# ═══════════════════════════════════════════════════════════════════

SSH_HOST="pitleague-vps"
PLUGIN_NAME="PitLeaguePlugin.dll"
VPS_PLUGIN_DIR='C:\Program Files (x86)\SimHub'
VPS_TEMP="C:\\temp"
GITHUB_REPO="RenatinhoBastos/pitleague-simhub-plugin"

echo "═══════════════════════════════════════════"
echo "  PitLeague — Deploy Plugin to VPS"
echo "═══════════════════════════════════════════"

# Garantir pasta temp no VPS
ssh "$SSH_HOST" "New-Item -ItemType Directory -Path '$VPS_TEMP' -Force | Out-Null"

if [[ "${1:-}" == "--local" ]]; then
    LOCAL_DLL="${2:?Uso: --local /caminho/para/PitLeaguePlugin.dll}"
    [[ -f "$LOCAL_DLL" ]] || { echo "❌ DLL não encontrada: $LOCAL_DLL"; exit 1; }
    echo "→ Enviando DLL local para VPS..."
    scp "$LOCAL_DLL" "${SSH_HOST}:C:/temp/${PLUGIN_NAME}"

elif [[ "${1:-}" == "--pull" ]]; then
    echo "→ Git pull no VPS (modo futuro — requer MSBuild)..."
    ssh "$SSH_HOST" "cd C:\\pitleague\\pitleague-simhub-plugin; & 'C:\\Program Files\\Git\\cmd\\git.exe' pull 2>&1"
    echo "⚠  Compilação remota ainda não implementada. Use GitHub Actions + --local ou modo padrão."
    exit 0

else
    echo "→ Baixando última release do GitHub..."
    DLL_URL=$(curl -s "https://api.github.com/repos/${GITHUB_REPO}/releases/latest" \
        | grep "browser_download_url.*\\.dll" \
        | head -1 \
        | cut -d '"' -f 4)

    if [[ -z "$DLL_URL" ]]; then
        echo "❌ Nenhuma DLL encontrada na última release."
        echo "   Verifique: https://github.com/${GITHUB_REPO}/releases"
        exit 1
    fi

    echo "   URL: $DLL_URL"
    curl -sL "$DLL_URL" -o "/tmp/${PLUGIN_NAME}"
    echo "   Tamanho: $(du -h /tmp/${PLUGIN_NAME} | cut -f1)"
    scp "/tmp/${PLUGIN_NAME}" "${SSH_HOST}:C:/temp/${PLUGIN_NAME}"
fi

echo ""
echo "→ Parando SimHub no VPS..."
ssh "$SSH_HOST" 'Get-Process SimHubWPF -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2' 2>/dev/null || true

echo "→ Copiando DLL para pasta de plugins..."
ssh "$SSH_HOST" "Copy-Item -Force 'C:\\temp\\${PLUGIN_NAME}' '${VPS_PLUGIN_DIR}\\${PLUGIN_NAME}'"

echo "→ Verificando DLL copiada..."
ssh "$SSH_HOST" "Get-Item '${VPS_PLUGIN_DIR}\\${PLUGIN_NAME}' | Select-Object Name, @{N='SizeKB';E={[math]::Round(\$_.Length/1KB,1)}}, LastWriteTime | Format-Table -AutoSize"

echo "→ Iniciando SimHub..."
ssh "$SSH_HOST" 'Start-Process "C:\Program Files (x86)\SimHub\SimHubWPF.exe"' 2>/dev/null || true

echo "→ Aguardando 8s para plugin inicializar..."
sleep 8

echo "→ Checando log do SimHub (últimas linhas com PitLeague)..."
ssh "$SSH_HOST" '
$logPaths = @(
    "$env:APPDATA\SimHub\SimHub.log",
    "C:\Program Files (x86)\SimHub\Logs\SimHub.log"
)
foreach ($p in $logPaths) {
    if (Test-Path $p) {
        Get-Content $p -Tail 30 | Select-String "PitLeague|Error|Warn|plugin"
        break
    }
}
' 2>/dev/null || echo "   (log não encontrado — SimHub pode não ter gravado ainda)"

echo ""
echo "✅ Deploy concluído!"
echo "   DLL: ${VPS_PLUGIN_DIR}\\${PLUGIN_NAME}"
echo "   Para verificar manualmente: ssh pitleague-vps"
