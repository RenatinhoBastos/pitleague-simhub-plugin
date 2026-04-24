#!/usr/bin/env bash
set -euo pipefail

echo "═══════════════════════════════════════"
echo "  Fase 2B — Validação Automatizada"
echo "═══════════════════════════════════════"
echo ""

SSH_HOST="pitleague-vps"
LEAGUE_ID="47ebf6a3-1316-408d-bbc1-2d6645841ac2"

# 1. Copiar gerador UDP pro VPS
echo "→ Copiando udp-generator.py para o VPS..."
scp scripts/udp-generator.py "${SSH_HOST}:C:/pitleague/udp-generator.py"

# 2. Self-test do gerador no VPS
echo ""
echo "→ Self-test do gerador UDP no VPS..."
ssh "$SSH_HOST" "python C:\\pitleague\\udp-generator.py --self-test"

# 3. Orientar usuário
echo ""
echo "════════════════════════════════════════════════════════"
echo "  ATENÇÃO: Abra o RDP e confirme:"
echo "    1. SimHub está aberto"
echo "    2. Plugin PitLeague v2.1.0 configurado com API key"
echo "    3. SimHub está em 'F1 25' (não Assetto Corsa)"
echo "    4. Pronto para escutar em 0.0.0.0:20777"
echo "════════════════════════════════════════════════════════"
echo ""
read -p "Confirma que está pronto? (s/N): " ready
[[ "$ready" =~ ^[sS]$ ]] || { echo "Abortado."; exit 1; }

# 4. Timestamp antes do teste
BEFORE_TS=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
echo ""
echo "→ Timestamp antes do teste: $BEFORE_TS"

# 5. Disparar cenário race-crash no VPS
echo ""
echo "→ Disparando cenário --race-crash (20s de race depois corte abrupto)..."
ssh "$SSH_HOST" "python C:\\pitleague\\udp-generator.py --scenario race-crash --duration 20 --verbose"

# 6. Aguardar timeout do plugin (10s stall) + margem
echo ""
echo "→ Aguardando 15s para timeout do plugin disparar..."
sleep 15

# 7. Conferir log do SimHub
echo ""
echo "→ Tentando ler log do plugin..."
ssh "$SSH_HOST" '
$logPaths = @(
    "$env:APPDATA\SimHub\SimHub.log",
    "$env:LOCALAPPDATA\SimHub\SimHub.log"
)
# Also check Logs directory
$logDir = "C:\Program Files (x86)\SimHub\Logs"
if (Test-Path $logDir) {
    Get-ChildItem $logDir -Recurse -Filter "*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | ForEach-Object { $logPaths += $_.FullName }
}
$found = $false
foreach ($p in $logPaths) {
    if (Test-Path $p) {
        Write-Host "Log encontrado: $p" -ForegroundColor Green
        Get-Content $p -Tail 100 | Select-String "PitLeague|Stall|TriggerResultReady"
        $found = $true
        break
    }
}
if (-not $found) { Write-Host "Nenhum log encontrado nos paths conhecidos" -ForegroundColor Yellow }
' 2>/dev/null || echo "   (sem logs encontrados)"

echo ""
echo "═══════════════════════════════════════════════════"
echo "  Teste completo. Verificar:"
echo "═══════════════════════════════════════════════════"
echo ""
echo "  1. Log deve conter: 'Stall detectado em Race por ~10s'"
echo "  2. Log deve conter: 'TriggerResultReady: reason=stall_timeout'"
echo "  3. Se AutoSend ligado, webhook_logs deve ter novo registro"
echo ""
echo "  Query Supabase para conferir envio:"
echo "  SELECT * FROM webhook_logs"
echo "    WHERE league_id='${LEAGUE_ID}'"
echo "    AND created_at > '${BEFORE_TS}'"
echo "    ORDER BY created_at DESC LIMIT 5;"
echo ""
