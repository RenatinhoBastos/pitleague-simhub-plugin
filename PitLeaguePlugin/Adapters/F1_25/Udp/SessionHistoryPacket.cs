using System;
using System.Collections.Generic;

namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>
    /// Parser for F1 25 Session History packet (PacketId=11).
    /// Sent per-car ~every second. Contains definitive per-lap sector times.
    ///
    /// Layout after header (29 bytes):
    ///   +0  m_carIdx              (uint8)
    ///   +1  m_numLaps             (uint8)
    ///   +2  m_numTyreStints       (uint8)
    ///   +3  m_bestLapTimeLapNum   (uint8)
    ///   +4  m_bestSector1LapNum   (uint8)
    ///   +5  m_bestSector2LapNum   (uint8)
    ///   +6  m_bestSector3LapNum   (uint8)
    ///   +7  m_lapHistoryData[100] — each 14 bytes:
    ///        +0  m_lapTimeInMS          (uint32)
    ///        +4  m_sector1TimeInMS      (uint16)
    ///        +6  m_sector1TimeMinutes   (uint8)
    ///        +7  m_sector2TimeInMS      (uint16)
    ///        +9  m_sector2TimeMinutes   (uint8)
    ///       +10  m_sector3TimeInMS      (uint16)
    ///       +12  m_sector3TimeMinutes   (uint8)
    ///       +13  m_lapValidBitFlags     (uint8) — bit0=lap, bit1=s1, bit2=s2, bit3=s3
    ///   +1407 m_tyreStintsHistoryData[8] — each 3 bytes (not used here)
    ///
    /// Total: 29 + 7 + 1400 + 24 = 1460 bytes
    /// </summary>
    public static class SessionHistoryParser
    {
        private const int FIELDS_OFFSET = PacketHeader.SIZE; // 29
        private const int LAP_HISTORY_OFFSET = FIELDS_OFFSET + 7; // 36
        private const int LAP_ENTRY_SIZE = 14;
        private const int MAX_LAPS = 100;
        public const int EXPECTED_PACKET_SIZE = 1460;

        /// <summary>
        /// Applies session history data for a single car into the buffers dictionary.
        /// Must be called under _snapshotLock.
        /// </summary>
        public static void Apply(Dictionary<byte, State.SessionHistoryBuffer> buffers, byte[] data)
        {
            // Minimum: header + 7 fields + at least 1 lap entry
            if (data.Length < LAP_HISTORY_OFFSET + LAP_ENTRY_SIZE) return;

            byte carIdx = data[FIELDS_OFFSET];       // +0: m_carIdx
            byte numLaps = data[FIELDS_OFFSET + 1];  // +1: m_numLaps

            if (carIdx > 21) return; // F1 25 max 22 cars (0-21)
            if (numLaps == 0) return;

            if (!buffers.ContainsKey(carIdx))
                buffers[carIdx] = new State.SessionHistoryBuffer();

            var buf = buffers[carIdx];
            buf.SetNumLaps(numLaps);

            int lapCount = Math.Min((int)numLaps, MAX_LAPS);

            for (int i = 0; i < lapCount; i++)
            {
                int entryOffset = LAP_HISTORY_OFFSET + (i * LAP_ENTRY_SIZE);
                if (entryOffset + LAP_ENTRY_SIZE > data.Length) break;

                uint lapTimeMS = BitConverter.ToUInt32(data, entryOffset);
                ushort s1MSPart = BitConverter.ToUInt16(data, entryOffset + 4);
                byte s1Min = data[entryOffset + 6];
                ushort s2MSPart = BitConverter.ToUInt16(data, entryOffset + 7);
                byte s2Min = data[entryOffset + 9];
                ushort s3MSPart = BitConverter.ToUInt16(data, entryOffset + 10);
                byte s3Min = data[entryOffset + 12];
                byte validFlags = data[entryOffset + 13];

                uint s1MS = (uint)(s1MSPart + s1Min * 60000);
                uint s2MS = (uint)(s2MSPart + s2Min * 60000);
                uint s3MS = (uint)(s3MSPart + s3Min * 60000);

                // If S3 is 0 in packet but S1 and S2 are valid, derive S3
                if (s3MS == 0 && s1MS > 0 && s2MS > 0 && lapTimeMS > (s1MS + s2MS))
                    s3MS = lapTimeMS - s1MS - s2MS;

                bool lapValid = (validFlags & 0x01) != 0;

                // Lap numbers are 1-based
                buf.SetLap(i + 1, s1MS, s2MS, s3MS, lapTimeMS, lapValid);
            }
        }
    }
}
