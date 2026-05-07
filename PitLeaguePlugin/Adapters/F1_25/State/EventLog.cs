using System.Collections.Generic;
using System.Linq;
using PitLeague.SimHub.Adapters.F1_25.Udp;

namespace PitLeague.SimHub.Adapters.F1_25.State
{
    /// <summary>Accumulated events from Event packets (penalties, fastest lap, collisions).</summary>
    public class EventLog
    {
        public byte? FastestLapDriverIdx { get; private set; }
        public uint FastestLapTimeMS { get; private set; }
        public byte? RaceWinnerIdx { get; private set; }

        private readonly List<PenaltyRecord> _penalties = new List<PenaltyRecord>();
        private readonly Dictionary<byte, int> _collisionCounts = new Dictionary<byte, int>();
        private readonly List<byte> _retirements = new List<byte>();
        private readonly List<byte> _safetyCarEvents = new List<byte>();

        public void SetFastestLap(byte vehicleIdx, uint lapTimeMS)
        {
            FastestLapDriverIdx = vehicleIdx;
            FastestLapTimeMS = lapTimeMS;
        }

        public void SetRaceWinner(byte vehicleIdx) => RaceWinnerIdx = vehicleIdx;

        public void AddPenalty(byte vehicleIdx, byte penaltyType, byte infringementType, byte time, byte lapNum)
        {
            _penalties.Add(new PenaltyRecord
            {
                VehicleIdx = vehicleIdx,
                PenaltyType = penaltyType,
                InfringementType = infringementType,
                Seconds = time,
                Lap = lapNum
            });
        }

        public void AddRetirement(byte vehicleIdx) => _retirements.Add(vehicleIdx);

        public void AddSafetyCarEvent(byte scType) => _safetyCarEvents.Add(scType);

        public void AddCollision(byte vehicle1, byte vehicle2)
        {
            if (!_collisionCounts.ContainsKey(vehicle1)) _collisionCounts[vehicle1] = 0;
            if (!_collisionCounts.ContainsKey(vehicle2)) _collisionCounts[vehicle2] = 0;
            _collisionCounts[vehicle1]++;
            _collisionCounts[vehicle2]++;
        }

        public int CollisionsFor(byte vehicleIdx) =>
            _collisionCounts.TryGetValue(vehicleIdx, out var c) ? c : 0;

        public List<Adapters.PenaltyEntry> PenaltiesFor(byte vehicleIdx)
        {
            return _penalties
                .Where(p => p.VehicleIdx == vehicleIdx)
                .Select(p => new Adapters.PenaltyEntry
                {
                    Type = TyreCompounds.MapPenaltyType(p.PenaltyType),
                    Lap = p.Lap,
                    Seconds = p.Seconds
                })
                .ToList();
        }

        public void Clear()
        {
            FastestLapDriverIdx = null;
            FastestLapTimeMS = 0;
            RaceWinnerIdx = null;
            _penalties.Clear();
            _collisionCounts.Clear();
            _retirements.Clear();
            _safetyCarEvents.Clear();
        }

        private class PenaltyRecord
        {
            public byte VehicleIdx;
            public byte PenaltyType;
            public byte InfringementType;
            public byte Seconds;
            public byte Lap;
        }
    }
}
