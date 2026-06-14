using System;
using System.Collections.Generic;
using System.Linq;

namespace PitLeague.SimHub.Adapters.F1_25.State
{
    /// <summary>Per-car accumulated lap data from LapData packets.</summary>
    public class LapBuffer
    {
        private readonly Dictionary<int, LapRecord> _laps = new Dictionary<int, LapRecord>();
        private byte _lastLapNum;
        private double? _maxSpeedTrap;
        private byte _maxWarnings;
        private byte _maxCornerCutting;
        private byte _gridPosition;
        private bool _wasInPit;

        // Pit stop accumulation — keyed by entry lap, idempotent (no destructive close)
        private readonly Dictionary<int, PitStopDetail> _pitByLap = new Dictionary<int, PitStopDetail>();
        private int _currentPitEntryLap = 0;
        private bool _pitDiagLogged;
        private ushort _lastPitLaneAccumulated = 0;
        private ushort _currentPitBaseline = 0;
        private const int PIT_MIN_MS = 3000;

        public double? MaxSpeedTrap => _maxSpeedTrap;
        public int MaxWarnings => _maxWarnings;
        public int MaxCornerCutting => _maxCornerCutting;
        public byte GridPosition => _gridPosition;

        /// <summary>Valid pit stops (>3s lane time), ordered by lap. Idempotent — safe to call repeatedly.</summary>
        public List<PitStopDetail> PitStopDetails =>
            _pitByLap.Values.Where(d => d.DurationMs >= PIT_MIN_MS).OrderBy(d => d.Lap).ToList();

        /// <summary>Count of valid pit stops.</summary>
        public int PitStopCount => _pitByLap.Values.Count(d => d.DurationMs >= PIT_MIN_MS);

        public void Update(
            byte lapNum, uint lastLapTimeMS,
            uint s1MS, uint s2MS,
            bool lapInvalid, float speedTrap,
            byte totalWarnings, byte cornerCutting,
            byte pitStatus, byte penalties,
            byte gridPosition,
            ushort pitLaneTimeMS = 0,
            ushort pitStopTimerMS = 0)
        {
            // Track max speed across all laps
            if (speedTrap > 0 && (!_maxSpeedTrap.HasValue || speedTrap > _maxSpeedTrap.Value))
                _maxSpeedTrap = speedTrap;

            // Track max warnings (cumulative from game)
            if (totalWarnings > _maxWarnings)
                _maxWarnings = totalWarnings;
            if (cornerCutting > _maxCornerCutting)
                _maxCornerCutting = cornerCutting;

            if (gridPosition > 0)
                _gridPosition = gridPosition;

            // Pit stop accumulation — idempotent, delta-based duration
            bool inPit = pitStatus == 1 || pitStatus == 2;
            if (inPit)
            {
                if (!_wasInPit)
                {
                    _currentPitEntryLap = lapNum;
                    _currentPitBaseline = _lastPitLaneAccumulated;
                }
                if (pitLaneTimeMS > 0 && _currentPitEntryLap > 0)
                {
                    PitStopDetail d;
                    if (!_pitByLap.TryGetValue(_currentPitEntryLap, out d))
                    {
                        d = new PitStopDetail { Lap = _currentPitEntryLap };
                        _pitByLap[_currentPitEntryLap] = d;
                    }
                    // Delta from baseline (not raw cumulative)
                    ushort delta = (ushort)(pitLaneTimeMS > _currentPitBaseline ? pitLaneTimeMS - _currentPitBaseline : pitLaneTimeMS);
                    if (delta > d.DurationMs)
                    {
                        bool wasBelowMin = d.DurationMs < PIT_MIN_MS;
                        d.DurationMs = delta;
                        if (wasBelowMin && d.DurationMs >= PIT_MIN_MS && !_pitDiagLogged)
                        {
                            _pitDiagLogged = true;
                            try
                            {
                                global::SimHub.Logging.Current.Info(
                                    $"[PitLeague:F1_25] Pit VALIDO: lap={d.Lap} delta={delta}ms raw={pitLaneTimeMS}ms baseline={_currentPitBaseline}ms box={d.StationaryMs}ms");
                            }
                            catch { }
                        }
                    }
                    if (pitStopTimerMS > d.StationaryMs) d.StationaryMs = pitStopTimerMS;
                    _lastPitLaneAccumulated = pitLaneTimeMS;
                }
            }
            _wasInPit = inPit;

            // Record completed lap when lapNum advances
            if (lapNum > _lastLapNum && lastLapTimeMS > 0)
            {
                int completedLap = _lastLapNum;
                if (completedLap > 0)
                {
                    uint s3MS = lastLapTimeMS > (s1MS + s2MS) ? lastLapTimeMS - s1MS - s2MS : 0;
                    _laps[completedLap] = new LapRecord
                    {
                        Lap = completedLap,
                        TimeMS = lastLapTimeMS,
                        S1MS = s1MS,
                        S2MS = s2MS,
                        S3MS = s3MS,
                        Valid = !lapInvalid
                    };
                }
            }
            _lastLapNum = lapNum;
        }

        public List<Adapters.LapTimeEntry> GetLapTimes()
        {
            if (_laps.Count == 0) return null;
            return _laps.Values
                .OrderBy(l => l.Lap)
                .Select(l => new Adapters.LapTimeEntry
                {
                    Lap = l.Lap,
                    Time = FormatTime(l.TimeMS),
                    S1 = FormatSector(l.S1MS),
                    S2 = FormatSector(l.S2MS),
                    S3 = FormatSector(l.S3MS),
                    Valid = l.Valid
                })
                .ToList();
        }

        private static string FormatTime(uint ms)
        {
            if (ms == 0) return null;
            var ts = TimeSpan.FromMilliseconds(ms);
            return ts.TotalMinutes >= 1
                ? $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
                : $"{ts.Seconds}.{ts.Milliseconds:D3}";
        }

        private static string FormatSector(uint ms)
        {
            if (ms == 0) return null;
            var sec = ms / 1000.0;
            return sec.ToString("F3");
        }

        private class LapRecord
        {
            public int Lap;
            public uint TimeMS;
            public uint S1MS;
            public uint S2MS;
            public uint S3MS;
            public bool Valid;
        }

        public class PitStopDetail
        {
            public int Lap;
            public ushort DurationMs;
            public ushort StationaryMs;
        }
    }
}
