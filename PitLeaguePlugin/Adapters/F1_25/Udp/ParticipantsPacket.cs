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
        // F1 25 entry structure (57 bytes):
        //   aiControlled (1), driverId (1), networkId (1), teamId (1), myTeam (1), raceNumber (1),
        //   nationality (1), name (32), yourTelemetry (1), showOnlineNames (1), techLevel (1),
        //   platform (1), + additional F1 25 fields (18 trailing bytes total)
        private const int ENTRY_SIZE = 57;
        private const int NAME_OFFSET = 7;   // after nationality at +6
        private const int NAME_LENGTH = 32;  // F1 25: m_name[32] (was 48 in F1 24)

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
                byte nationality = data[offset + 6];
                byte raceNumber = data[offset + 5];

                // Name: 32 bytes at offset+7, null-terminated UTF-8
                // Strip ALL C0 control chars (\u0000-\u001F) — not just \0
                string rawName = Encoding.UTF8.GetString(data, offset + NAME_OFFSET, NAME_LENGTH);
                string name = StripControlChars(rawName).Trim();

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
