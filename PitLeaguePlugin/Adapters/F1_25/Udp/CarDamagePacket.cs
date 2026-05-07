using System;
using System.Collections.Generic;

namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>
    /// Parser for F1 25 CarDamage packet (PacketId=10).
    /// Tracks wing damage per car to detect wing repairs.
    /// Each entry ~42 bytes. 22 cars max.
    /// </summary>
    public static class CarDamageParser
    {
        // Entry offsets (within each car's data block):
        //   tyresWear[4] (4 floats = 16 bytes)
        //   tyresDamage[4] (4 bytes)
        //   brakesDamage[4] (4 bytes)
        //   frontLeftWingDamage (1 byte) - offset 24
        //   frontRightWingDamage (1 byte) - offset 25
        //   rearWingDamage (1 byte) - offset 26
        // Plus more fields after. Approx 42 bytes per car.
        private const int ENTRY_SIZE = 42;

        public static void Apply(Dictionary<byte, State.CarDamageBuffer> buffers, byte[] data)
        {
            if (data.Length < PacketHeader.SIZE + 10) return;

            int offset = PacketHeader.SIZE;

            for (byte carIdx = 0; carIdx < 22 && offset + ENTRY_SIZE <= data.Length; carIdx++)
            {
                // Wing damage values at offset+24, +25, +26 within entry
                byte flWing = data[offset + 24];
                byte frWing = data[offset + 25];
                byte rearWing = data[offset + 26];

                if (!buffers.ContainsKey(carIdx))
                    buffers[carIdx] = new State.CarDamageBuffer();

                buffers[carIdx].UpdateWingDamage(flWing, frWing, rearWing);

                offset += ENTRY_SIZE;
            }
        }
    }
}
