# PitLeague SimHub Plugin — Schema JSON v2.0

Formato unificado enviado pelo plugin (e pelo emulador Python no Mac).
Compatível com `/api/integrations/simhub/result`.

---

## Payload completo

```json
{
  "schemaVersion": "pitleague-2.0",
  "source": "simhub-plugin",
  "pluginVersion": "1.0.0",
  "game": "F1 25",
  "leagueId": "uuid-da-liga-aqui",
  "sessionUID": "abc123-unico-por-sessao",
  "capturedAt": "2026-04-14T20:00:00Z",
  "session": {
    "type": "Race",
    "track": "Monaco",
    "totalLaps": 50,
    "results": [
      {
        "position": 1,
        "gamertag": "RRT_Renato",
        "team": "Red Bull",
        "status": "Finished",
        "bestLapTime": "1:12.345",
        "fastestLap": true,
        "polePosition": true,
        "penaltySeconds": 0,
        "gap": "0.000"
      },
      {
        "position": 2,
        "gamertag": "MAR_Marcos",
        "team": "Ferrari",
        "status": "Finished",
        "bestLapTime": "1:12.901",
        "fastestLap": false,
        "polePosition": false,
        "penaltySeconds": 5,
        "gap": "+3.421"
      },
      {
        "position": 20,
        "gamertag": "ALV_Pedro",
        "team": "Haas",
        "status": "DNF",
        "bestLapTime": null,
        "fastestLap": false,
        "polePosition": false,
        "penaltySeconds": 0,
        "gap": "DNF"
      }
    ]
  }
}
```

---

## Campos obrigatórios vs opcionais

| Campo | Obrigatório | Descrição |
|-------|-------------|-----------|
| `schemaVersion` | ✅ | Sempre "pitleague-2.0" |
| `source` | ✅ | "simhub-plugin" ou "python-emulator" |
| `game` | ✅ | "F1 25", "ACC", "iRacing", "AMS2", etc. |
| `leagueId` | ✅ | UUID da liga AV no PitLeague |
| `sessionUID` | ✅ | ID único da sessão (evita duplicatas) |
| `capturedAt` | ✅ | ISO 8601 UTC |
| `session.type` | ✅ | "Race", "Qualifying", "Practice" |
| `session.track` | ✅ | Nome da pista como o SimHub retorna |
| `session.totalLaps` | ❌ | Total de voltas da corrida |
| `results[].position` | ✅ | Posição final (1-based) |
| `results[].gamertag` | ✅ | Gamertag exato do piloto |
| `results[].team` | ❌ | Nome da equipe no jogo |
| `results[].status` | ✅ | "Finished", "DNF", "DSQ", "DNS" |
| `results[].bestLapTime` | ❌ | Formato "m:ss.mmm" |
| `results[].fastestLap` | ❌ | true para o piloto com a volta mais rápida |
| `results[].polePosition` | ❌ | true para quem largou em P1 |
| `results[].penaltySeconds` | ❌ | Segundos de penalidade aplicados |
| `results[].gap` | ❌ | Gap para o líder ("+3.421") ou "DNF" |
| `pluginVersion` | ❌ | Versão do plugin para debug |

---

## Status de corrida

| status | Quando usar |
|--------|-------------|
| `Finished` | Completou a corrida normalmente |
| `DNF` | Não completou (acidente, abandono) |
| `DSQ` | Desqualificado |
| `DNS` | Não largou |

---

## Compatibilidade v1.0

A API aceita v1.0 (formato antigo do Python) e v2.0 (plugin).
Novo código deve sempre enviar v2.0.
