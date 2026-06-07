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

        // Pit stop accumulation per episode
        private readonly List<PitStopDetail> _pitStopDetails = new List<PitStopDetail>();
        private bool _pitSeenThisLap;
        private byte _pitEntryLap;
        private ushort _maxPitLaneTimeMS;
        private ushort _maxPitStopTimerMS;
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

            // Pit stop accumulation: track max times while in pit
            bool inPit = pitStatus == 1 || pitStatus == 2;
            if (inPit)
            {
                if (!_pitSeenThisLap) { _pitSeenThisLap = true; _pitEntryLap = lapNum; }
                if (pitLaneTimeMS > _maxPitLaneTimeMS) _maxPitLaneTimeMS = pitLaneTimeMS;
                if (pitStopTimerMS > _maxPitStopTimerMS) _maxPitStopTimerMS = pitStopTimerMS;
            }
            _wasInPit = inPit;

            // Record completed lap when lapNum advances — also closes any pit from that lap
            if (lapNum > _lastLapNum && lastLapTimeMS > 0)
            {
                // Close pit episode from the lap that just ended (if any)
                ClosePit();

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

        /// <summary>Close the last pit episode (call before snapshot, handles end-of-race).</summary>
        public void FinalizePit()
        {
            if (_pitSeenThisLap) ClosePit();
        }

        private void ClosePit()
        {
            const int PIT_MIN_MS = 3000; // real pit lane never < ~14s; kills grid (0ms) and noise
            if (_pitSeenThisLap && _maxPitLaneTimeMS >= PIT_MIN_MS)
            {
                _pitStopDetails.Add(new PitStopDetail
                {
                    Lap = _pitEntryLap > 0 ? _pitEntryLap : _lastLapNum,
                    DurationMs = _maxPitLaneTimeMS,
                    StationaryMs = _maxPitStopTimerMS,
                });
                _pitStopCount++;
                if (!_pitDiagLogged)
                {
                    _pitDiagLogged = true;
                    try
                    {
                        global::SimHub.Logging.Current.Info(
                            $"[PitLeague:F1_25] Pit VALIDO: lap={_pitEntryLap} inLane={_maxPitLaneTimeMS}ms box={_maxPitStopTimerMS}ms");
                    }
                    catch { }
                }
            }
            _pitSeenThisLap = false;
            _maxPitLaneTimeMS = 0;
            _maxPitStopTimerMS = 0;
            _pitEntryLap = 0;
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
