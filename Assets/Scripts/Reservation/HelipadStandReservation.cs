using System.Collections.Generic;

namespace RoadNetworkEditor.Reservations
{
    /// <summary>
    /// Helpers for the provisional stand hold between landing and the next takeoff.
    /// </summary>
    public static class HelipadStandReservation
    {
        public static float CalculateEnd(float arrivalTime, float maxWait, float buffer)
        {
            return arrivalTime + System.Math.Max(0f, maxWait) + System.Math.Max(0f, buffer);
        }

        public static int Release(
            IList<OccupiedTimeWindow> windows,
            string aircraftId,
            float clearTime)
        {
            if (windows == null || string.IsNullOrEmpty(aircraftId)) return 0;

            int released = 0;
            for (int i = 0; i < windows.Count; i++)
            {
                OccupiedTimeWindow window = windows[i];
                if (window == null || !window.IsStandingReservation ||
                    window.OccupiedAircraftId != aircraftId)
                {
                    continue;
                }

                float clampedClearTime = System.Math.Max(window.StartTime, clearTime);
                if (clampedClearTime < window.EndTime)
                {
                    window.EndTime = clampedClearTime;
                    released++;
                }
            }
            return released;
        }
    }
}
