# F1 25 UDP Telemetry Setup

## Prerequisites
- F1 25 installed on the same PC as SimHub (or on a PC in the same local network)
- SimHub installed with PitLeague plugin v2.2.0+
- Plugin configured with API Key and League ID in SimHub settings

## F1 25 Configuration

1. Open **F1 25**
2. Go to **Settings > Telemetry**
3. Configure:

| Setting | Value |
|---------|-------|
| **UDP Telemetry** | ON |
| **UDP Broadcast Mode** | OFF |
| **UDP IP Address** | `127.0.0.1` (same PC) or SimHub PC's IP (network) |
| **UDP Port** | `20777` (default) |
| **UDP Send Rate** | `60Hz` (recommended, minimum 20Hz) |
| **UDP Format** | `2025` |
| **Your Telemetry** | **Public** (important — otherwise only your own data is sent) |

## What Gets Captured

When using the F1 25 UDP adapter, the plugin captures rich telemetry data:

| Data | Description |
|------|-------------|
| **Weather** | Condition (dry/wet/mixed), air + track temps (start and end), weather changes during race |
| **Tyre Stints** | Compound (C1-C6, inter, wet), visual compound, lap start/end per stint |
| **Lap Times** | Individual lap times with S1, S2, S3 sectors and lap validity |
| **Pit Stops** | Lap number, tyre change (from → to) |
| **Incidents** | Collisions, track limits warnings, corner cutting, wing repairs |
| **Penalties** | Type (drive through, time penalty, etc.), lap, seconds |
| **Top Speed** | Maximum speed trap reading across all laps |
| **Grid Position** | Starting grid position |
| **Race Pace** | Gap percentage to leader |

## Troubleshooting

### Plugin shows "generic" adapter instead of "f125"
- F1 25 must be running BEFORE SimHub starts, or the UDP port may be occupied
- Check that UDP port 20777 is not used by another application
- Restart SimHub after starting F1 25

### No data received
- Verify "UDP Telemetry" is ON in F1 25 settings
- Verify "Your Telemetry" is set to **Public**
- Check firewall is not blocking UDP port 20777
- If F1 25 is on a different PC, use that PC's IP instead of 127.0.0.1

### Partial data (some fields null)
- "UDP Send Rate" too low — set to 60Hz
- Race ended before FinalClassification packet was sent (rare)
- Short race with < 3 laps may not have enough data
