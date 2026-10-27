using System;
using System.Collections.Generic;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Pure lane-boundary admission rule shared by runtime control and tests.
    /// </summary>
    public static class LaneEntryDecisionUtility
    {
        public static bool CanEnter(
            bool signalBlocked,
            bool capacityBlocked,
            IEnumerable<float> targetProgresses,
            float targetLaneLength,
            float safeInterval,
            bool enforceSourceControl = true)
        {
            if ((enforceSourceControl && (signalBlocked || capacityBlocked)) ||
                targetLaneLength <= 0f)
                return false;

            float requiredGap = Math.Max(0f, safeInterval);
            if (targetProgresses == null) return true;

            foreach (float progress in targetProgresses)
            {
                float distanceFromEntry = Math.Max(0f, progress) * targetLaneLength;
                if (distanceFromEntry < requiredGap)
                    return false;
            }
            return true;
        }
    }
}
