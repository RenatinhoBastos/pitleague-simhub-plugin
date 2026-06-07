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
        private int _pitStopCount;
        private bool _wasInPit;

        // Pit stop details: lap + duration captured by transition detection
        private readonly List<PitStopDetail> _pitStopDetails = new List<PitStopDetail>();
        private ushort _currentPitLaneTimeMS;
        private byte _pitEntryLap;
        private bool _pitDiagLogged;

        public double? MaxSpeedTrap => _maxSpeedTrap;
        public int MaxWarnings => _maxWarnings;
        public int MaxCornerCutting => _maxCornerCutting;
        public byte GridPosition => _gridPosition;
        public int PitStopCount => _pitStopCount;
        public List<PitStopDetail> PitStopDetails => _pitStopDetails;

        public void Update(
            byte lapNum, uint lastLapTimeMS,
            uint s1MS, uint s2MS,
            bool lapInvalid, float speedTrap,
            byte totalWarnings, byte cornerCutting,
            byte pitStatus, byte penalties,
            byte gridPosition,
            ushort pitLaneTimeMS = 0)
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

            // Detect pit stops by tracking pit status transitions
            bool inPit = pitStatus == 1 || pitStatus == 2;
            if (inPit && !_wasInPit)
            {
                _pitStopCount++;
                _pitEntryLap = lapNum;
                _currentPitLaneTimeMS = 0;
            }
            if (inPit && pitLaneTimeMS > _currentPitLaneTimeMS)
            {
                _currentPitLaneTimeMS = pitLaneTimeMS;
            }
            if (!inPit && _wasInPit)
            {
                // Exiting pit — record stop with lap + duration
                _pitStopDetails.Add(new PitStopDetail
                {
                    Lap = _pitEntryLap > 0 ? _pitEntryLap : lapNum,
                    DurationMs = _currentPitLaneTimeMS,
                });
                if (!_pitDiagLogged)
                {
                    _pitDiagLogged = true;
                    try
                    {
                        global::SimHub.Logging.Current.Info(
                            $"[PitLeague:F1_25] Pit stop detected: lap={_pitEntryLap} pitLaneTimeMS={_currentPitLaneTimeMS}");
                    }
                    catch { }
                }
                _currentPitLaneTimeMS = 0;
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
        }
    }
}
