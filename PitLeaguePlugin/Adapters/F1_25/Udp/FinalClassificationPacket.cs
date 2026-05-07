using System;
using System.Collections.Generic;

namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>
    /// F1 25 FinalClassificationData — sent once at end of session.
    /// Total packet: 1042 bytes (header 29 + 1 byte numCars + 22 * 46 bytes entries).
    /// Entry size: 46 bytes (F1 25 adds ResultReason byte vs F1 24's 45).
    /// </summary>
    public class FinalClassificationEntry
    {
        public byte Position;
        public byte NumLaps;
        public byte GridPosition;
        public byte Points;
        public byte NumPitStops;
        public byte ResultStatus;        // 3=finished, 4=DNF, 5=DSQ, 6=NC, 7=retired
        public byte ResultReason;        // New in F1 25
        public uint BestLapTimeInMS;
        public double TotalRaceTime;
        public byte PenaltiesTime;
        public byte NumPenalties;
        public byte NumTyreStints;
        public byte[] TyreStintsActual;  // 8 bytes
        public byte[] TyreStintsVisual;  // 8 bytes
        public byte[] TyreStintsEndLaps; // 8 bytes

        public const int ENTRY_SIZE = 46;
    }

    public static class FinalClassificationParser
    {
        public const int EXPECTED_PACKET_SIZE = 1042;

        public static List<FinalClassificationEntry> Parse(byte[] data)
        {
            var entries = new List<FinalClassificationEntry>();
            int offset = PacketHeader.SIZE; // skip header

            if (data.Length < offset + 1) return entries;

            byte numCars = data[offset];
            offset += 1;

            for (int i = 0; i < numCars && offset + FinalClassificationEntry.ENTRY_SIZE <= data.Length; i++)
            {
                var entry = new FinalClassificationEntry
                {
                    Position = data[offset + 0],
                    NumLaps = data[offset + 1],
                    GridPosition = data[offset + 2],
                    Points = data[offset + 3],
                    NumPitStops = data[offset + 4],
                    ResultStatus = data[offset + 5],
                    ResultReason = data[offset + 6],
                    BestLapTimeInMS = BitConverter.ToUInt32(data, offset + 7),
                    TotalRaceTime = BitConverter.ToDouble(data, offset + 11),
                    PenaltiesTime = data[offset + 19],
                    NumPenalties = data[offset + 20],
                    NumTyreStints = data[offset + 21],
                    TyreStintsActual = new byte[8],
                    TyreStintsVisual = new byte[8],
                    TyreStintsEndLaps = new byte[8]
                };

                Buffer.BlockCopy(data, offset + 22, entry.TyreStintsActual, 0, 8);
                Buffer.BlockCopy(data, offset + 30, entry.TyreStintsVisual, 0, 8);
                Buffer.BlockCopy(data, offset + 38, entry.TyreStintsEndLaps, 0, 8);

                entries.Add(entry);
                offset += FinalClassificationEntry.ENTRY_SIZE;
            }

            return entries;
        }
    }
}
