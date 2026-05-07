using System;
using System.Runtime.InteropServices;

namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>F1 25 UDP packet header — 29 bytes, common to all packets.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PacketHeader
    {
        public ushort PacketFormat;              // 2025
        public byte GameYear;                    // 25
        public byte GameMajorVersion;
        public byte GameMinorVersion;
        public byte PacketVersion;
        public byte PacketId;                    // 0..14
        public ulong SessionUID;
        public float SessionTime;
        public uint FrameIdentifier;
        public uint OverallFrameIdentifier;
        public byte PlayerCarIndex;
        public byte SecondaryPlayerCarIndex;

        public const int SIZE = 29;
    }

    public static class PacketIds
    {
        public const byte Session = 1;
        public const byte LapData = 2;
        public const byte Event = 3;
        public const byte Participants = 4;
        public const byte CarStatus = 7;
        public const byte FinalClassification = 8;
        public const byte CarDamage = 10;
    }

    public static class HeaderParser
    {
        public static PacketHeader Parse(byte[] data)
        {
            if (data.Length < PacketHeader.SIZE)
                throw new ArgumentException($"Packet too short: {data.Length} < {PacketHeader.SIZE}");

            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<PacketHeader>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
