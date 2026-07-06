using System;
using System.Text;

namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>
    /// Parser for F1 25 Participants packet (PacketId=4).
    /// Each entry: 57 bytes (name[32] + other fields). 22 cars max.
    /// Provides gamertag → carIdx mapping.
    /// </summary>
    public static class ParticipantsParser
    {
        // F1 2025 entry structure (57 bytes):
        //   +0 aiControlled (1), +1 driverId (1), +2 networkId (1), +3 teamId (1),
        //   +4 myTeam (1), +5 raceNumber (1), +6 nationality (1),
        //   +7 name (32), ... trailing fields
        // F1 2026 entry structure (60 bytes):
        //   +0 aiControlled (1), +1-2 driverId (uint16), +3-4 networkId (uint16),
        //   +5-6 teamId (uint16), +7 myTeam (1), +8 raceNumber (1), +9 nationality (1),
        //   +10 name (32), ... trailing fields
        private const int ENTRY_SIZE_2025 = 57;
        private const int NAME_OFFSET_2025 = 7;
        private const int ENTRY_SIZE_2026 = 60;
        private const int NAME_OFFSET_2026 = 10;
        private const int NAME_LENGTH = 32;

        public static void Apply(State.ParticipantsMap map, byte[] data, bool is2026 = false)
        {
            if (data.Length < PacketHeader.SIZE + 1) return;

            int entrySize = is2026 ? ENTRY_SIZE_2026 : ENTRY_SIZE_2025;
            int nameOffset = is2026 ? NAME_OFFSET_2026 : NAME_OFFSET_2025;

            int offset = PacketHeader.SIZE;
            byte numActiveCars = data[offset];
            offset += 1;

            for (byte carIdx = 0; carIdx < numActiveCars && offset + entrySize <= data.Length; carIdx++)
            {
                byte aiControlled = data[offset];
                byte teamId, raceNumber, nationality;
                if (is2026)
                {
                    // 2026: driverId/networkId/teamId are uint16
                    teamId = data[offset + 5]; // low byte of uint16 teamId
                    raceNumber = data[offset + 8];
                    nationality = data[offset + 9];
                }
                else
                {
                    teamId = data[offset + 3];
                    raceNumber = data[offset + 5];
                    nationality = data[offset + 6];
                }

                // Name: 32 bytes at nameOffset, null-terminated UTF-8
                string rawName = Encoding.UTF8.GetString(data, offset + nameOffset, NAME_LENGTH);
                string name = StripControlChars(rawName).Trim();

                map.Set(carIdx, new State.ParticipantInfo
                {
                    Name = name,
                    TeamId = teamId,
                    RaceNumber = raceNumber,
                    IsAI = aiControlled == 1,
                    DriverId = is2026 ? data[offset + 1] : data[offset + 1],
                    NetworkId = is2026 ? data[offset + 3] : data[offset + 2]
                });

                offset += entrySize;
            }
        }

        /// <summary>Remove all C0 control characters (\u0000-\u001F) from a string.</summary>
        private static string StripControlChars(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= ' ') // \u0020 = space, first printable ASCII
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
