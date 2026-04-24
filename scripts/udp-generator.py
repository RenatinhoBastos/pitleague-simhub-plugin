#!/usr/bin/env python3
"""
PitLeague — F1 25 UDP Packet Generator
=======================================
Generates synthetic F1 25 UDP packets to test the SimHub plugin
without needing the actual game running.

Scenarios:
  --race-normal   : 20s of Race data, then clean transition to Practice
  --race-crash    : 20s of Race data, then abrupt stop (Liin case)
  --spectator     : Same as race-crash but logged as spectator mode

Usage:
  python3 udp-generator.py --scenario race-crash --host 127.0.0.1 --port 20777
  python3 udp-generator.py --self-test

No external dependencies — pure Python stdlib.
"""

import socket
import struct
import time
import argparse
import sys
import threading

# ─── F1 25 UDP Protocol Constants ─────────────────────────────────────────────

PACKET_FORMAT = 2025
GAME_YEAR = 25

# Packet IDs
PACKET_SESSION = 1
PACKET_LAP_DATA = 2

# Session types (F1 25 enum)
SESSION_UNKNOWN = 0
SESSION_P1 = 1
SESSION_P2 = 2
SESSION_P3 = 3
SESSION_SHORT_P = 4
SESSION_Q1 = 5
SESSION_Q2 = 6
SESSION_Q3 = 7
SESSION_SHORT_Q = 8
SESSION_OSQ = 9
SESSION_RACE = 10
SESSION_RACE2 = 11
SESSION_RACE3 = 12
SESSION_TIME_TRIAL = 13
SESSION_SPRINT = 14

# Track IDs
TRACK_BAHRAIN = 3
TRACK_MONACO = 5
TRACK_MONZA = 11


# ─── Packet Builders ─────────────────────────────────────────────────────────

def build_header(packet_id: int, session_uid: int = 123456789,
                 session_time: float = 0.0, frame_id: int = 0) -> bytes:
    """
    F1 25 PacketHeader — 29 bytes
    Based on the official F1 25 UDP specification.
    """
    return struct.pack(
        "<HBBBBBQfIIBB",
        PACKET_FORMAT,      # m_packetFormat (uint16) = 2025
        GAME_YEAR,          # m_gameYear (uint8) = 25
        1,                  # m_gameMajorVersion (uint8)
        0,                  # m_gameMinorVersion (uint8)
        1,                  # m_packetVersion (uint8)
        packet_id,          # m_packetId (uint8)
        session_uid,        # m_sessionUID (uint64)
        session_time,       # m_sessionTime (float)
        frame_id,           # m_frameIdentifier (uint32)
        frame_id,           # m_overallFrameIdentifier (uint32)
        0,                  # m_playerCarIndex (uint8)
        255,                # m_secondaryPlayerCarIndex (uint8)
    )


def build_session_packet(session_type: int = SESSION_RACE,
                         track_id: int = TRACK_BAHRAIN,
                         total_laps: int = 5,
                         session_uid: int = 123456789,
                         session_time: float = 0.0,
                         frame_id: int = 0) -> bytes:
    """
    PacketSessionData (id=1).
    Full packet is ~644 bytes, we fill the first critical fields
    and zero-pad the rest.
    """
    header = build_header(PACKET_SESSION, session_uid, session_time, frame_id)

    # Session data body (first ~30 bytes matter, rest zero-padded)
    # Offset 0: m_weather (uint8)
    # Offset 1: m_trackTemperature (int8)
    # Offset 2: m_airTemperature (int8)
    # Offset 3: m_totalLaps (uint8)
    # Offset 4: m_trackLength (uint16)
    # Offset 6: m_sessionType (uint8)
    # Offset 7: m_trackId (int8)
    # ...rest we don't need
    session_body = struct.pack(
        "<BbbBHBb",
        0,              # m_weather (clear)
        30,             # m_trackTemperature
        25,             # m_airTemperature
        total_laps,     # m_totalLaps
        5543,           # m_trackLength (meters, ~Bahrain)
        session_type,   # m_sessionType
        track_id,       # m_trackId
    )

    # Pad to realistic size (~644 bytes total)
    pad_size = 644 - len(header) - len(session_body)
    return header + session_body + (b'\x00' * max(0, pad_size))


def build_lap_data_packet(session_uid: int = 123456789,
                          session_time: float = 0.0,
                          frame_id: int = 0) -> bytes:
    """
    PacketLapData (id=2).
    Minimal: just header + enough zeros to look valid.
    Full packet is ~1131 bytes for 22 cars.
    """
    header = build_header(PACKET_LAP_DATA, session_uid, session_time, frame_id)
    # Pad to realistic size
    pad_size = 1131 - len(header)
    return header + (b'\x00' * max(0, pad_size))


# ─── Scenarios ────────────────────────────────────────────────────────────────

def run_scenario(scenario: str, host: str, port: int, duration: int,
                 verbose: bool):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    session_uid = int(time.time()) & 0xFFFFFFFFFFFFFFFF
    frame = 0

    print(f"═══════════════════════════════════════════")
    print(f"  PitLeague UDP Generator — F1 25")
    print(f"═══════════════════════════════════════════")
    print(f"  Scenario : {scenario}")
    print(f"  Target   : {host}:{port}")
    print(f"  Duration : {duration}s")
    print(f"  Session  : {session_uid}")
    print(f"═══════════════════════════════════════════")
    print()

    if scenario == "spectator":
        print("  ⚠ SPECTATOR MODE — simula broadcaster sem controle do jogo")
        print()

    try:
        # Phase 1: Send initial Session packet (Race)
        pkt = build_session_packet(
            session_type=SESSION_RACE,
            track_id=TRACK_BAHRAIN,
            total_laps=5,
            session_uid=session_uid,
            frame_id=frame,
        )
        sock.sendto(pkt, (host, port))
        frame += 1
        print(f"[{0:5.1f}s] Sent SessionData: type=Race(10), track=Bahrain(3)")

        # Phase 2: Send LapData at ~60Hz for duration
        hz = 60
        total_ticks = duration * hz
        log_interval = hz * 2  # log every 2s

        print(f"[{0:5.1f}s] Sending LapData at {hz}Hz for {duration}s...")

        for tick in range(total_ticks):
            elapsed = tick / hz
            pkt = build_lap_data_packet(
                session_uid=session_uid,
                session_time=elapsed,
                frame_id=frame,
            )
            sock.sendto(pkt, (host, port))
            frame += 1

            if verbose and tick % log_interval == 0 and tick > 0:
                print(f"[{elapsed:5.1f}s] LapData tick={tick}, frame={frame}")

            time.sleep(1.0 / hz)

        elapsed = duration

        # Phase 3: scenario-specific ending
        if scenario == "race-normal":
            print(f"[{elapsed:5.1f}s] Transitioning to Practice (clean end)...")

            # Send Session packet with Practice type
            pkt = build_session_packet(
                session_type=SESSION_P1,
                track_id=TRACK_BAHRAIN,
                session_uid=session_uid,
                session_time=elapsed,
                frame_id=frame,
            )
            sock.sendto(pkt, (host, port))
            frame += 1
            print(f"[{elapsed:5.1f}s] Sent SessionData: type=Practice(1)")

            # Send a few more LapData ticks in Practice
            for tick in range(hz * 3):
                pkt = build_lap_data_packet(
                    session_uid=session_uid,
                    session_time=elapsed + tick / hz,
                    frame_id=frame,
                )
                sock.sendto(pkt, (host, port))
                frame += 1
                time.sleep(1.0 / hz)

            print(f"[{elapsed + 3:5.1f}s] Done. Clean transition complete.")

        elif scenario in ("race-crash", "spectator"):
            label = "SPECTATOR" if scenario == "spectator" else "CRASH"
            print(f"[{elapsed:5.1f}s] *** {label}: ABRUPT STOP — no transition packet ***")
            print(f"[{elapsed:5.1f}s] Plugin should detect stall after ~10s timeout")

        print()
        print(f"✅ Scenario '{scenario}' complete. {frame} packets sent.")

    finally:
        sock.close()


# ─── Self-test ────────────────────────────────────────────────────────────────

def run_self_test():
    """Send packets to localhost and verify they parse correctly."""
    print("═══════════════════════════════════════════")
    print("  PitLeague UDP Generator — Self-Test")
    print("═══════════════════════════════════════════")
    print()

    test_port = 20999  # use non-standard port to avoid conflicts
    received = []
    errors = []

    # Listener thread
    def listener():
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("127.0.0.1", test_port))
        sock.settimeout(3.0)
        try:
            while True:
                try:
                    data, _ = sock.recvfrom(4096)
                    received.append(data)
                except socket.timeout:
                    break
        finally:
            sock.close()

    t = threading.Thread(target=listener, daemon=True)
    t.start()
    time.sleep(0.2)  # let listener bind

    # Send test packets
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        # Test 1: Session packet
        pkt = build_session_packet(session_type=SESSION_RACE, track_id=TRACK_BAHRAIN)
        sock.sendto(pkt, ("127.0.0.1", test_port))
        print("  Sent: SessionData (Race, Bahrain)")

        # Test 2: LapData packet
        pkt = build_lap_data_packet()
        sock.sendto(pkt, ("127.0.0.1", test_port))
        print("  Sent: LapData")

        time.sleep(0.5)
    finally:
        sock.close()

    t.join(timeout=5)

    # Validate received packets
    print()
    if len(received) < 2:
        print(f"  ❌ Expected 2 packets, received {len(received)}")
        sys.exit(1)

    for i, data in enumerate(received):
        if len(data) < 29:
            errors.append(f"Packet {i}: too short ({len(data)} bytes)")
            continue

        fmt = struct.unpack_from("<HBBBBBQfIIBB", data, 0)
        pkt_format = fmt[0]
        game_year = fmt[1]
        pkt_id = fmt[6]

        if pkt_format != 2025:
            errors.append(f"Packet {i}: m_packetFormat={pkt_format}, expected 2025")
        if game_year != 25:
            errors.append(f"Packet {i}: m_gameYear={game_year}, expected 25")

        pkt_name = {1: "SessionData", 2: "LapData"}.get(pkt_id, f"Unknown({pkt_id})")
        print(f"  Packet {i}: {pkt_name} | format={pkt_format} | year={game_year} | size={len(data)}B ✅")

    if errors:
        print()
        for e in errors:
            print(f"  ❌ {e}")
        sys.exit(1)

    # Validate Session packet content
    session_pkt = received[0]
    header_size = 29
    session_type = struct.unpack_from("B", session_pkt, header_size + 6)[0]
    track_id = struct.unpack_from("b", session_pkt, header_size + 7)[0]

    if session_type != SESSION_RACE:
        errors.append(f"SessionData: sessionType={session_type}, expected {SESSION_RACE}")
    else:
        print(f"  SessionData content: sessionType={session_type}(Race) trackId={track_id}(Bahrain) ✅")

    print()
    if errors:
        for e in errors:
            print(f"  ❌ {e}")
        sys.exit(1)
    else:
        print("  ✅ All self-tests passed!")
        print()


# ─── Entry point ──────────────────────────────────────────────────────────────

if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="PitLeague F1 25 UDP Packet Generator"
    )
    parser.add_argument("--scenario",
                        choices=["race-normal", "race-crash", "spectator"],
                        help="Scenario to simulate")
    parser.add_argument("--host", default="127.0.0.1",
                        help="Target host (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=20777,
                        help="Target port (default: 20777)")
    parser.add_argument("--duration", type=int, default=20,
                        help="Race duration in seconds (default: 20)")
    parser.add_argument("--verbose", action="store_true",
                        help="Log every packet sent")
    parser.add_argument("--self-test", action="store_true",
                        help="Run self-test (send + receive + validate)")

    args = parser.parse_args()

    if args.self_test:
        run_self_test()
    elif args.scenario:
        run_scenario(args.scenario, args.host, args.port, args.duration,
                     args.verbose)
    else:
        parser.print_help()
        print("\nExemplos:")
        print("  python3 udp-generator.py --self-test")
        print("  python3 udp-generator.py --scenario race-crash")
        print("  python3 udp-generator.py --scenario race-normal --duration 10")
        sys.exit(1)
