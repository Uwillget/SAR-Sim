using UnityEngine;

namespace RoadNetworkEditor
{
    public static class VertiportWaypointSafety
    {
        public static bool ConflictsWithAircraft(
            string requesterId,
            Vector3 requestedTarget,
            string otherAircraftId,
            Vector3 otherPosition,
            Vector3 otherTarget,
            bool otherIsActive,
            float clearance)
        {
            if (!otherIsActive || requesterId == otherAircraftId) return false;
            float minimum = Mathf.Max(0f, clearance);
            return Vector3.Distance(otherPosition, requestedTarget) < minimum ||
                   Vector3.Distance(otherTarget, requestedTarget) < minimum;
        }
    }
}
