using System;
using System.Text;

namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>
    /// Parser for F1 25 Participants packet (PacketId=4).
    /// Each entry: 57 bytes (name[48] + other fields). 22 cars max.
    /// Provides gamertag → carIdx mapping.
    /// </summary>
    public static class ParticipantsParser
    {
        // Entry structure (57 bytes):
        //   aiControlled (1), driverId (1), networkId (1), teamId (1), myTeam (1), raceNumber (1),
        //   nationality (1), name (48), yourTelemetry (1), showOnlineNames (1), techLevel (1), platform (1)
        private const int ENTRY_SIZE = 57;
        private const int NAME_LENGTH = 48;

        public static void Apply(State.ParticipantsMap map, byte[] data)
        {
            if (data.Length < PacketHeader.SIZE + 1) return;

            int offset = PacketHeader.SIZE;
            byte numActiveCars = data[offset];
            offset += 1;

            for (byte carIdx = 0; carIdx < numActiveCars && offset + ENTRY_SIZE <= data.Length; carIdx++)
            {
                byte aiControlled = data[offset];
                byte driverId = data[offset + 1];
                byte networkId = data[offset + 2];
                byte teamId = data[offset + 3];
                byte raceNumber = data[offset + 5];

                // Name: 48 bytes, null-terminated UTF-8
                string name = Encoding.UTF8.GetString(data, offset + 6, NAME_LENGTH).TrimEnd('\0').Trim();

                map.Set(carIdx, new State.ParticipantInfo
                {
                    Name = name,
                    TeamId = teamId,
                    RaceNumber = raceNumber,
                    IsAI = aiControlled == 1,
                    DriverId = driverId,
                    NetworkId = networkId
                });

                offset += ENTRY_SIZE;
            }
        }
    }
}
