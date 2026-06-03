using System.Collections.Generic;

namespace PitLeague.SimHub.Adapters.F1_25.State
{
    /// <summary>
    /// Per-car session history from SessionHistory packets (packetId 11).
    /// Stores per-lap sector times (S1/S2/S3) from the definitive source.
    /// </summary>
    public class SessionHistoryBuffer
    {
        /// <summary>Sector times for a completed lap, keyed by 1-based lap number.</summary>
        private readonly Dictionary<int, LapSectors> _sectors = new Dictionary<int, LapSectors>();

        /// <summary>Number of completed laps reported by the packet.</summary>
        public int NumLaps { get; private set; }

        /// <summary>
        /// Updates sector data for a given lap. Called from the packet parser.
        /// Only stores laps where at least one sector has a non-zero time.
        /// </summary>
        public void SetLap(int lapNum, uint s1MS, uint s2MS, uint s3MS, uint lapTimeMS, bool valid)
        {
            if (lapNum <= 0) return;
            if (s1MS == 0 && s2MS == 0 && s3MS == 0 && lapTimeMS == 0) return;

            _sectors[lapNum] = new LapSectors
            {
                S1MS = s1MS,
                S2MS = s2MS,
                S3MS = s3MS,
                LapTimeMS = lapTimeMS,
                Valid = valid
            };
        }

        public void SetNumLaps(int numLaps)
        {
            NumLaps = numLaps;
        }

        /// <summary>
        /// Returns sector data for the given lap, or null if not available.
        /// </summary>
        public LapSectors GetLap(int lapNum)
        {
            LapSectors result;
            return _sectors.TryGetValue(lapNum, out result) ? result : null;
        }

        /// <summary>All lap numbers that have sector data.</summary>
        public IEnumerable<int> LapNumbers => _sectors.Keys;

        public class LapSectors
        {
            public uint S1MS;
            public uint S2MS;
            public uint S3MS;
            public uint LapTimeMS;
            public bool Valid;
        }
    }
}
