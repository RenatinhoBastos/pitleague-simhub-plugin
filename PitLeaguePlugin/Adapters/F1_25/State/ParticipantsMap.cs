using System.Collections.Generic;

namespace PitLeague.SimHub.Adapters.F1_25.State
{
    /// <summary>Maps carIdx → participant info from Participants packets.</summary>
    public class ParticipantsMap
    {
        private readonly Dictionary<byte, ParticipantInfo> _map = new Dictionary<byte, ParticipantInfo>();

        public void Set(byte carIdx, ParticipantInfo info)
        {
            _map[carIdx] = info;
        }

        public ParticipantInfo Get(byte carIdx)
        {
            return _map.TryGetValue(carIdx, out var info) ? info : null;
        }

        public void Clear() => _map.Clear();
    }

    public class ParticipantInfo
    {
        public string Name { get; set; }
        public byte TeamId { get; set; }
        public byte RaceNumber { get; set; }
        public bool IsAI { get; set; }
        public byte DriverId { get; set; }
        public byte NetworkId { get; set; }
    }
}
