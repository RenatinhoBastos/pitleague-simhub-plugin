using System;
using System.Text;

namespace PitLeague.SimHub.Adapters.F1_25.Udp
{
    /// <summary>
    /// Parser for F1 25 Event packet (PacketId=3).
    /// Event-driven: penalties, fastest lap, retirements, safety car.
    /// Header (29) + eventStringCode (4) + event-specific data.
    /// </summary>
    public static class EventParser
    {
        public static void Apply(State.EventLog log, byte[] data)
        {
            if (data.Length < PacketHeader.SIZE + 4) return;

            int offset = PacketHeader.SIZE;
            string code = Encoding.ASCII.GetString(data, offset, 4);
            offset += 4;

            switch (code)
            {
                case "FTLP": // Fastest lap
                    if (offset + 5 <= data.Length)
                    {
                        byte vehicleIdx = data[offset];
                        uint lapTimeMS = BitConverter.ToUInt32(data, offset + 1);
                        log.SetFastestLap(vehicleIdx, lapTimeMS);
                    }
                    break;

                case "PENA": // Penalty issued
                    if (offset + 7 <= data.Length)
                    {
                        byte penaltyType = data[offset];
                        byte infringementType = data[offset + 1];
                        byte vehicleIdx = data[offset + 2];
                        byte otherVehicleIdx = data[offset + 3];
                        byte time = data[offset + 4];
                        byte lapNum = data[offset + 5];
                        byte placesGained = data[offset + 6];

                        log.AddPenalty(vehicleIdx, penaltyType, infringementType, time, lapNum);
                    }
                    break;

                case "RTMT": // Retirement
                    if (offset + 1 <= data.Length)
                    {
                        byte vehicleIdx = data[offset];
                        log.AddRetirement(vehicleIdx);
                    }
                    break;

                case "SCAR": // Safety car
                    if (offset + 1 <= data.Length)
                    {
                        byte scType = data[offset]; // 0=full, 1=virtual, 2=formation
                        log.AddSafetyCarEvent(scType);
                    }
                    break;

                case "RCWN": // Race winner
                    if (offset + 1 <= data.Length)
                    {
                        byte vehicleIdx = data[offset];
                        log.SetRaceWinner(vehicleIdx);
                    }
                    break;

                case "COLL": // Collision
                    if (offset + 2 <= data.Length)
                    {
                        byte vehicle1 = data[offset];
                        byte vehicle2 = data[offset + 1];
                        log.AddCollision(vehicle1, vehicle2);
                    }
                    break;
            }
        }
    }
}
