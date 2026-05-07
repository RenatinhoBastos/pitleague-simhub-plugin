namespace PitLeague.SimHub.Adapters.F1_25.State
{
    /// <summary>Per-car damage tracking to detect wing repairs.</summary>
    public class CarDamageBuffer
    {
        private byte _lastFlWing;
        private byte _lastFrWing;
        private byte _lastRearWing;
        private bool _hasData;

        public int WingRepairCount { get; private set; }

        public void UpdateWingDamage(byte flWing, byte frWing, byte rearWing)
        {
            if (_hasData)
            {
                // Detect repair: damage goes from >0 to 0 (pit stop repair)
                if (_lastFlWing > 0 && flWing == 0) WingRepairCount++;
                if (_lastFrWing > 0 && frWing == 0) WingRepairCount++;
                if (_lastRearWing > 0 && rearWing == 0) WingRepairCount++;
            }

            _lastFlWing = flWing;
            _lastFrWing = frWing;
            _lastRearWing = rearWing;
            _hasData = true;
        }

        public void Clear()
        {
            WingRepairCount = 0;
            _hasData = false;
        }
    }
}
