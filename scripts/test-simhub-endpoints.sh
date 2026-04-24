#!/usr/bin/env bash
set -euo pipefail

# ═══════════════════════════════════════════════════════════════════
# PitLeague — Testar endpoints SimHub da API
#
# Env vars necessárias:
#   PITLEAGUE_API_KEY     — API key da liga (sk_live_xxx)
#   PITLEAGUE_LEAGUE_ID   — UUID da liga
#   PITLEAGUE_BASE_URL    — (opcional, default: https://app.pitleague.com.br)
#
# Uso:
#   export PITLEAGUE_API_KEY=sk_live_xxx
#   export PITLEAGUE_LEAGUE_ID=uuid-da-liga
#   ./scripts/test-simhub-endpoints.sh
# ═══════════════════════════════════════════════════════════════════

API_KEY="${PITLEAGUE_API_KEY:?Defina PITLEAGUE_API_KEY (ex: sk_live_xxx)}"
LEAGUE_ID="${PITLEAGUE_LEAGUE_ID:?Defina PITLEAGUE_LEAGUE_ID (UUID da liga)}"
BASE_URL="${PITLEAGUE_BASE_URL:-https://app.pitleague.com.br}"

echo "═══════════════════════════════════════════"
echo "  PitLeague — Teste de Endpoints SimHub"
echo "═══════════════════════════════════════════"
echo "  Base URL : $BASE_URL"
echo "  API Key  : ${API_KEY:0:12}****"
echo "  Liga     : $LEAGUE_ID"
echo "═══════════════════════════════════════════"
echo ""

PASS=0
FAIL=0

run_test() {
    local name="$1"
    local method="$2"
    local url="$3"
    local data="${4:-}"

    echo "── Teste: $name ──"

    if [[ "$method" == "GET" ]]; then
        RESPONSE=$(curl -s -w "\n%{http_code}" -X GET "$url" \
            -H "Authorization: Bearer $API_KEY" \
            --max-time 15 2>&1)
    else
        RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$url" \
            -H "Authorization: Bearer $API_KEY" \
            -H "Content-Type: application/json" \
            -d "$data" \
            --max-time 15 2>&1)
    fi

    HTTP_CODE=$(echo "$RESPONSE" | tail -1)
    BODY=$(echo "$RESPONSE" | sed '$d')

    if [[ "$HTTP_CODE" =~ ^2 ]]; then
        echo "   ✅ HTTP $HTTP_CODE"
        PASS=$((PASS + 1))
    else
        echo "   ❌ HTTP $HTTP_CODE"
        FAIL=$((FAIL + 1))
    fi

    echo "   Body: ${BODY:0:300}"
    echo ""
}

# ── Teste 1: Health check (API keys endpoint) ──────────────────────
run_test "API Keys (auth check)" "GET" "$BASE_URL/api/integrations/api-keys"

# ── Teste 2: SimHub test endpoint ──────────────────────────────────
run_test "SimHub Test" "POST" "$BASE_URL/api/integrations/simhub/test" \
    "{\"leagueId\": \"$LEAGUE_ID\"}"

# ── Teste 3: SimHub result (payload mínimo simulado) ───────────────
PAYLOAD=$(cat <<EOF
{
  "schemaVersion": "pitleague-2.0",
  "source": "test-script",
  "pluginVersion": "1.0.0",
  "game": "F1 25",
  "leagueId": "$LEAGUE_ID",
  "sessionUID": "test-$(date +%s)",
  "capturedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "session": {
    "type": "Race",
    "track": "Monaco",
    "totalLaps": 5,
    "results": [
      {
        "position": 1,
        "gamertag": "TestDriver1",
        "team": "Test Team A",
        "status": "Finished",
        "bestLapTime": "1:12.345",
        "fastestLap": true,
        "polePosition": true,
        "penaltySeconds": 0,
        "gap": "0.000"
      },
      {
        "position": 2,
        "gamertag": "TestDriver2",
        "team": "Test Team B",
        "status": "Finished",
        "bestLapTime": "1:13.001",
        "fastestLap": false,
        "polePosition": false,
        "penaltySeconds": 0,
        "gap": "+1.234"
      }
    ]
  }
}
EOF
)

run_test "SimHub Result (2 pilotos teste)" "POST" \
    "$BASE_URL/api/integrations/simhub/result" "$PAYLOAD"

# ── Resumo ─────────────────────────────────────────────────────────
echo "═══════════════════════════════════════════"
echo "  Resultado: $PASS passou, $FAIL falhou"
echo "═══════════════════════════════════════════"

if [[ $FAIL -gt 0 ]]; then
    echo "⚠  Verifique API key e league ID."
    exit 1
fi
