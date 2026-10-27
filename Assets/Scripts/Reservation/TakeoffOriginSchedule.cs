using UnityEngine;

namespace RoadNetworkEditor.Reservations
{
    /// <summary>
    /// Timing helpers for the part of a takeoff before the first approach node.
    /// Waiting for a delayed first-node slot must happen on the origin helipad,
    /// not after the aircraft has already entered that node.
    /// </summary>
    public static class TakeoffOriginSchedule
    {
        public static float CalculateHold(
            float requestedDeparture,
            Vector3 originPosition,
            Vector3 firstNodePosition,
            float firstNodeArrival,
            float speed)
        {
            if (speed <= 0f)
                return 0f;

            float travelTime = Vector3.Distance(originPosition, firstNodePosition) / speed;
            float releaseTime = firstNodeArrival - travelTime;
            return Mathf.Max(0f, releaseTime - requestedDeparture);
        }
    }
}
