#!/usr/bin/env python3
"""
PitLeague SimHub Emulator — Mac/Linux
======================================
Emula exatamente o que o plugin C# enviaria para a API do PitLeague.
Útil para testar o endpoint sem precisar do SimHub (Windows-only).

Dois modos:
  1. AUTO  — Tenta capturar telemetria UDP do F1 25 (PS5 → Mac)
  2. MANUAL — Você digita os resultados no terminal

Uso:
  python3 simhub_emulator.py              # modo auto (UDP)
  python3 simhub_emulator.py --manual    # modo manual
  python3 simhub_emulator.py --test      # testa conexão com a API
"""

import socket
import struct
import json
import uuid
import datetime
import argparse
import requests
import sys
import os

# ─── Configuração ──────────────────────────────────────────────────────────────
API_KEY    = os.environ.get("PITLEAGUE_API_KEY", "sk_live_sua_chave_aqui")
LEAGUE_ID  = os.environ.get("PITLEAGUE_LEAGUE_ID", "uuid-da-sua-liga")
API_URL    = os.environ.get("PITLEAGUE_URL", "https://app.pitleague.com.br")
UDP_PORT   = int(os.environ.get("PITLEAGUE_UDP_PORT", "20777"))  # F1 25 default
GAME_NAME  = os.environ.get("PITLEAGUE_GAME", "F1 25")


# ─── Schema v2.0 ───────────────────────────────────────────────────────────────
def build_payload(results: list, track: str, session_type: str = "Race",
                  total_laps: int = 0, session_uid: str = None) -> dict:
    """Monta o payload no formato pitleague-2.0 (idêntico ao plugin C#)"""
    return {
        "schemaVersion": "pitleague-2.0",
        "source":        "python-emulator",
        "pluginVersion": "1.0.0",
        "game":          GAME_NAME,
        "leagueId":      LEAGUE_ID,
        "sessionUID":    session_uid or str(uuid.uuid4()),
        "capturedAt":    datetime.datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ"),
        "session": {
            "type":      session_type,
            "track":     track,
            "totalLaps": total_laps,
            "results":   results
        }
    }


def build_driver(position: int, gamertag: str, team: str = "",
                 status: str = "Finished", best_lap: str = None,
                 fastest_lap: bool = False, pole: bool = False,
                 penalty: int = 0, gap: str = "") -> dict:
    return {
        "position":       position,
        "gamertag":       gamertag,
        "team":           team,
        "status":         status,
        "bestLapTime":    best_lap,
        "fastestLap":     fastest_lap,
        "polePosition":   pole,
        "penaltySeconds": penalty,
        "gap":            gap if status != "DNF" else "DNF"
    }


# ─── Envio para API ────────────────────────────────────────────────────────────
def send_to_api(payload: dict) -> bool:
    url     = f"{API_URL.rstrip('/')}/api/integrations/simhub/result"
    headers = {
        "Authorization": f"Bearer {API_KEY}",
        "Content-Type":  "application/json"
    }

    print(f"\n📡 Enviando para: {url}")
    print(f"📦 Payload ({len(payload['session']['results'])} pilotos):")
    print(json.dumps(payload, indent=2, ensure_ascii=False))

    try:
        r = requests.post(url, json=payload, headers=headers, timeout=15)
        print(f"\n→ Status HTTP: {r.status_code}")
        try:
            data = r.json()
            print(f"→ Resposta: {json.dumps(data, indent=2, ensure_ascii=False)}")
            if data.get("success"):
                print(f"\n✅ Sucesso! {data.get('matched', 0)}/{data.get('total', 0)} gamertags vinculados.")
                return True
            else:
                print(f"\n❌ API retornou erro: {data.get('message', 'sem mensagem')}")
        except Exception:
            print(f"→ Body raw: {r.text[:500]}")
        return r.ok
    except requests.exceptions.RequestException as e:
        print(f"\n❌ Erro de conexão: {e}")
        return False


def test_connection() -> bool:
    url     = f"{API_URL.rstrip('/')}/api/integrations/api-keys"
    headers = {"Authorization": f"Bearer {API_KEY}"}
    print(f"🔌 Testando conexão com {API_URL}...")
    try:
        r = requests.get(url, headers=headers, timeout=10)
        if r.ok:
            print("✅ Conexão OK com o PitLeague!")
            return True
        else:
            print(f"❌ API retornou {r.status_code} — verifique a API Key e o League ID")
            return False
    except Exception as e:
        print(f"❌ Sem conexão: {e}")
        return False


# ─── Modo MANUAL ───────────────────────────────────────────────────────────────
def run_manual():
    print("═" * 55)
    print("  PitLeague SimHub Emulator — Modo Manual")
    print("═" * 55)
    print(f"  API Key : {API_KEY[:12]}****")
    print(f"  Liga    : {LEAGUE_ID}")
    print(f"  Jogo    : {GAME_NAME}")
    print("═" * 55)

    track       = input("\nNome da pista (ex: Monaco): ").strip() or "Unknown"
    total_laps  = input("Total de voltas (Enter para 0): ").strip()
    total_laps  = int(total_laps) if total_laps.isdigit() else 0
    session_uid = f"{track.replace(' ', '_')}_{datetime.datetime.utcnow().strftime('%Y%m%d%H%M')}"

    print(f"\nDigite os pilotos em ordem de chegada.")
    print("Deixe o gamertag em branco para parar.\n")

    results    = []
    fastest_lap_pos = None

    # Perguntar pole
    pole_tag = input("Gamertag de quem fez POLE (Enter para pular): ").strip()

    position = 1
    while True:
        print(f"\n--- P{position} ---")
        tag = input("  Gamertag: ").strip()
        if not tag:
            break

        team   = input("  Equipe (Enter para vazio): ").strip()
        status_input = input("  Status [F=Finished/D=DNF/Q=DSQ] (Enter=F): ").strip().upper()
        status = {"D": "DNF", "Q": "DSQ", "F": "Finished"}.get(status_input, "Finished")

        lap_input = input("  Melhor volta (ex: 1:12.345, Enter para pular): ").strip()
        penalty   = input("  Penalidade em segundos (Enter=0): ").strip()
        penalty   = int(penalty) if penalty.isdigit() else 0
        gap_input = input("  Gap para líder (ex: +3.421, Enter para vazio): ").strip()

        is_fl = input("  Volta mais rápida? [s/n]: ").strip().lower() == "s"
        if is_fl:
            fastest_lap_pos = position

        results.append(build_driver(
            position    = position,
            gamertag    = tag,
            team        = team,
            status      = status,
            best_lap    = lap_input or None,
            fastest_lap = is_fl,
            pole        = (tag == pole_tag),
            penalty     = penalty,
            gap         = gap_input if position > 1 else "0.000"
        ))
        position += 1

    if not results:
        print("Nenhum piloto inserido. Abortando.")
        return

    payload = build_payload(
        results      = results,
        track        = track,
        session_type = "Race",
        total_laps   = total_laps,
        session_uid  = session_uid
    )

    send_to_api(payload)


# ─── Modo AUTO (UDP F1 25) ─────────────────────────────────────────────────────
# Estrutura dos pacotes F1 25 (estrutura simplificada)
# Tamanhos e offsets baseados no F1 25 UDP Specification
PACKET_HEADER_SIZE = 29  # bytes

def parse_header(data: bytes) -> dict:
    """Parseia o header do pacote UDP do F1 25"""
    try:
        fmt = "<HBBBBQfIIBB"
        size = struct.calcsize(fmt)
        if len(data) < size:
            return None
        fields = struct.unpack_from(fmt, data, 0)
        return {
            "packet_format":  fields[0],  # 2025
            "packet_id":      fields[9],  # tipo do pacote
            "session_uid":    fields[5],  # ID único da sessão
            "frame_id":       fields[7],
        }
    except Exception:
        return None

# IDs dos pacotes F1 25
PACKET_MOTION          = 0
PACKET_SESSION         = 1
PACKET_LAP_DATA        = 2
PACKET_EVENT           = 3
PACKET_PARTICIPANTS    = 4
PACKET_CAR_SETUPS      = 5
PACKET_CAR_TELEMETRY   = 6
PACKET_CAR_STATUS      = 7
PACKET_FINAL_CLASS     = 8
PACKET_SESSION_HISTORY = 11

def run_auto():
    print("═" * 55)
    print("  PitLeague SimHub Emulator — Modo Auto (UDP)")
    print("═" * 55)
    print(f"  Ouvindo UDP na porta {UDP_PORT}")
    print(f"  Configure o F1 25: Settings → Telemetry → Port {UDP_PORT}")
    print(f"  Ctrl+C para parar")
    print("═" * 55)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", UDP_PORT))
    sock.settimeout(1.0)

    # Estado da sessão atual
    session_uid    = None
    track_id       = None
    participants   = {}  # car_idx → name
    final_results  = []
    result_ready   = False

    track_names = {
        0: "Melbourne", 1: "Paul Ricard", 2: "Shanghai", 3: "Sakhir (Bahrain)",
        4: "Catalunya", 5: "Monaco", 6: "Montreal", 7: "Silverstone",
        8: "Hockenheim", 9: "Hungaroring", 10: "Spa", 11: "Monza",
        12: "Singapore", 13: "Suzuka", 14: "Abu Dhabi", 15: "Texas",
        16: "Brazil", 17: "Austria", 18: "Sochi", 19: "Mexico",
        20: "Baku (Azerbaijan)", 21: "Sakhir Short", 22: "Silverstone Short",
        23: "Texas Short", 24: "Suzuka Short", 25: "Hanoi", 26: "Zandvoort",
        27: "Imola", 28: "Portimao", 29: "Jeddah", 30: "Miami",
        31: "Las Vegas", 32: "Losail"
    }

    team_names = {
        0: "Mercedes", 1: "Ferrari", 2: "Red Bull", 3: "Williams",
        4: "Aston Martin", 5: "Alpine", 6: "RB", 7: "Haas",
        8: "McLaren", 9: "Sauber", 255: ""
    }

    print("\n⏳ Aguardando pacotes do F1 25...")

    try:
        while True:
            try:
                data, _ = sock.recvfrom(4096)
            except socket.timeout:
                if result_ready and final_results:
                    print("\n📊 Resultado pronto para envio!")
                    _send_auto_result(final_results, track_id, track_names, session_uid)
                    result_ready = False
                    final_results = []
                continue

            header = parse_header(data)
            if not header or header["packet_format"] < 2024:
                continue

            uid = str(header["session_uid"])
            if uid != session_uid:
                if uid != "0":
                    print(f"\n🔄 Nova sessão detectada: {uid}")
                    session_uid  = uid
                    participants = {}
                    final_results = []
                    result_ready  = False

            pkt = header["packet_id"]

            # ── Pacote de participantes (nomes dos pilotos) ──────────────────
            if pkt == PACKET_PARTICIPANTS:
                try:
                    num_cars = struct.unpack_from("B", data, PACKET_HEADER_SIZE)[0]
                    offset   = PACKET_HEADER_SIZE + 1
                    for i in range(min(num_cars, 22)):
                        # Cada entry: ai_controlled(1) + driver_id(1) + network_id(1) + team_id(1) +
                        #             my_team(1) + race_number(1) + nationality(1) + name(48) + ...
                        entry_offset = offset + i * 56
                        team_id = struct.unpack_from("B", data, entry_offset + 3)[0]
                        name_bytes = data[entry_offset + 7 : entry_offset + 7 + 48]
                        name = name_bytes.split(b'\x00')[0].decode('utf-8', errors='ignore').strip()
                        if name:
                            participants[i] = {
                                "name": name,
                                "team": team_names.get(team_id, "")
                            }
                    if participants:
                        print(f"  👥 {len(participants)} pilotos detectados: " +
                              ", ".join(p["name"] for p in list(participants.values())[:5]) +
                              ("..." if len(participants) > 5 else ""))
                except Exception as e:
                    if "--debug" in sys.argv:
                        print(f"  ⚠ Participants parse error: {e}")

            # ── Pacote de classificação final ────────────────────────────────
            elif pkt == PACKET_FINAL_CLASS:
                try:
                    num_cars = struct.unpack_from("B", data, PACKET_HEADER_SIZE)[0]
                    offset   = PACKET_HEADER_SIZE + 1
                    results  = []
                    for i in range(min(num_cars, 22)):
                        # Final classification entry: position(1) + num_laps(1) + grid_position(1) +
                        #   points(1) + num_pit_stops(1) + result_status(1) + ...
                        entry_offset = offset + i * 45
                        pos     = struct.unpack_from("B", data, entry_offset)[0]
                        status  = struct.unpack_from("B", data, entry_offset + 5)[0]
                        fl_time_raw = struct.unpack_from("I", data, entry_offset + 7)[0]
                        fl_ms   = fl_time_raw  # milliseconds

                        status_map = {
                            3: "Finished", 4: "DNF", 5: "DNS", 7: "DSQ"}
                        status_str = status_map.get(status, "Finished")

                        driver = participants.get(i, {"name": f"Driver{i+1}", "team": ""})
                        results.append({
                            "idx":    i,
                            "pos":    pos,
                            "name":   driver["name"],
                            "team":   driver["team"],
                            "status": status_str,
                            "fl_ms":  fl_ms
                        })

                    results.sort(key=lambda x: x["pos"])
                    final_results = results
                    result_ready  = True
                    print(f"\n🏁 Corrida finalizada! {num_cars} pilotos classificados.")
                    print(f"   P1: {results[0]['name']} | P2: {results[1]['name'] if len(results)>1 else '?'}")

                except Exception as e:
                    if "--debug" in sys.argv:
                        print(f"  ⚠ Final class parse error: {e}")

            # ── Pacote de sessão (pista) ────────────────────────────────────
            elif pkt == PACKET_SESSION:
                try:
                    tid = struct.unpack_from("B", data, PACKET_HEADER_SIZE)[0]
                    if track_id != tid:
                        track_id = tid
                        track_name = track_names.get(tid, f"Track_{tid}")
                        print(f"  🗺  Pista: {track_name} (id={tid})")
                except Exception:
                    pass

    except KeyboardInterrupt:
        print("\n\nEmulador encerrado.")
    finally:
        sock.close()


def _send_auto_result(raw_results, track_id, track_names, session_uid):
    """Monta e envia o resultado capturado automaticamente"""
    track = track_names.get(track_id, f"Track_{track_id}") if track_id is not None else "Unknown"

    # Encontrar volta mais rápida
    min_fl   = float("inf")
    fl_idx   = -1
    for r in raw_results:
        if r["fl_ms"] > 0 and r["fl_ms"] < min_fl:
            min_fl = r["fl_ms"]
            fl_idx = r["idx"]

    def fmt_laptime(ms):
        if ms <= 0: return None
        total_s = ms / 1000
        m  = int(total_s // 60)
        s  = int(total_s % 60)
        ms = int((total_s - int(total_s)) * 1000)
        return f"{m}:{s:02d}.{ms:03d}"

    results = []
    for r in raw_results:
        results.append(build_driver(
            position    = r["pos"],
            gamertag    = r["name"],
            team        = r["team"],
            status      = r["status"],
            best_lap    = fmt_laptime(r["fl_ms"]),
            fastest_lap = (r["idx"] == fl_idx),
            pole        = False,  # F1 25 não envia pole via UDP final class
            penalty     = 0,
            gap         = "" if r["pos"] == 1 else "?"
        ))

    payload = build_payload(
        results      = results,
        track        = track,
        session_type = "Race",
        session_uid  = session_uid or str(uuid.uuid4())
    )

    # Perguntar antes de enviar
    resp = input(f"\n📤 Enviar resultado de {track} ({len(results)} pilotos) para o PitLeague? [s/n]: ").strip().lower()
    if resp == "s":
        send_to_api(payload)
    else:
        # Salvar JSON localmente para inspeção
        fname = f"resultado_{track}_{datetime.datetime.now().strftime('%H%M%S')}.json"
        with open(fname, "w") as f:
            json.dump(payload, f, indent=2, ensure_ascii=False)
        print(f"💾 Payload salvo em: {fname}")


# ─── Entry point ──────────────────────────────────────────────────────────────
if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="PitLeague SimHub Emulator")
    parser.add_argument("--manual", action="store_true", help="Modo manual (digitar resultados)")
    parser.add_argument("--test",   action="store_true", help="Testar conexão com a API")
    parser.add_argument("--debug",  action="store_true", help="Logs detalhados")
    args = parser.parse_args()

    print("\n🏁 PitLeague SimHub Emulator v1.0.0\n")

    if not API_KEY or API_KEY == "sk_live_sua_chave_aqui":
        print("⚠  AVISO: API Key não configurada.")
        print("   Configure com: export PITLEAGUE_API_KEY=sk_live_xxx\n")

    if args.test:
        test_connection()
    elif args.manual:
        run_manual()
    else:
        run_auto()
