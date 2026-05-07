using System;
using System.Collections.Generic;

namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>
    /// Parser for F1 25 LapData packet (PacketId=2).
    /// Contains per-car lap data: sectors, warnings, penalties, grid position, speed trap.
    /// Each car entry is ~50 bytes. 22 cars max.
    /// </summary>
    public struct LapDataEntry
    {
        public uint LastLapTimeInMS;
        public uint CurrentLapTimeInMS;
        public ushort Sector1TimeMSPart;
        public byte Sector1TimeMinutesPart;
        public ushort Sector2TimeMSPart;
        public byte Sector2TimeMinutesPart;
        public float LapDistance;
        public float TotalDistance;
        public byte CarPosition;
        public byte CurrentLapNum;
        public byte PitStatus;               // 0=none, 1=pitting, 2=in pit area
        public byte NumPitStops;
        public byte Sector;                  // 0=S1, 1=S2, 2=S3
        public byte CurrentLapInvalid;
        public byte Penalties;               // seconds of pending penalty
        public byte TotalWarnings;
        public byte CornerCuttingWarnings;
        public byte GridPosition;
        public byte DriverStatus;
        public byte ResultStatus;
        public float SpeedTrap;              // top speed this lap (km/h)

        public const int ENTRY_SIZE = 50;    // approximate — read with BinaryReader for safety
    }

    public static class LapDataParser
    {
        public static void Apply(Dictionary<byte, State.LapBuffer> buffers, byte[] data)
        {
            if (data.Length < PacketHeader.SIZE + 10) return;

            int offset = PacketHeader.SIZE;
            // LapData packet: 22 entries after header
            for (byte carIdx = 0; carIdx < 22 && offset + 43 <= data.Length; carIdx++)
            {
                try
                {
                    uint lastLapTimeMS = BitConverter.ToUInt32(data, offset);
                    uint currentLapTimeMS = BitConverter.ToUInt32(data, offset + 4);
                    ushort s1MSPart = BitConverter.ToUInt16(data, offset + 8);
                    byte s1MinPart = data[offset + 10];
                    ushort s2MSPart = BitConverter.ToUInt16(data, offset + 11);
                    byte s2MinPart = data[offset + 13];
                    // Skip delta fields (6 bytes)
                    // LapDistance (float, 4 bytes) at offset+20
                    // TotalDistance (float, 4 bytes) at offset+24
                    // SafetyCarDelta (float, 4 bytes) at offset+28
                    byte carPosition = data[offset + 32];
                    byte currentLapNum = data[offset + 33];
                    byte pitStatus = data[offset + 34];
                    byte numPitStops = data[offset + 35];
                    byte sector = data[offset + 36];
                    byte lapInvalid = data[offset + 37];
                    byte penalties = data[offset + 38];
                    byte totalWarnings = data[offset + 39];
                    byte cornerCutting = data[offset + 40];
                    // NumUnservedDriveThrough + StopGo: 2 bytes at offset+41,42
                    byte gridPosition = data[offset + 43];
                    // DriverStatus + ResultStatus + PitLaneTimer stuff
                    float speedTrap = 0;
                    if (offset + 49 + 4 <= data.Length)
                    {
                        speedTrap = BitConverter.ToSingle(data, offset + 49);
                    }

                    if (!buffers.ContainsKey(carIdx))
                        buffers[carIdx] = new State.LapBuffer();

                    var buf = buffers[carIdx];
                    buf.Update(
                        currentLapNum, lastLapTimeMS,
                        s1MSPart + s1MinPart * 60000u,
                        s2MSPart + s2MinPart * 60000u,
                        lapInvalid != 0,
                        speedTrap,
                        totalWarnings, cornerCutting,
                        pitStatus, penalties,
                        gridPosition
                    );
                }
                catch { /* skip malformed entry */ }

                offset += LapDataEntry.ENTRY_SIZE;
            }
        }
    }
}
