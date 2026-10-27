using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// A final, frame-discrete safety bound for car-following movement.
    /// It assumes the predecessor may stop during the current frame and limits
    /// follower travel so the configured centre-to-centre gap is not crossed.
    /// </summary>
    public static class FollowingSafetyUtility
    {
        public static float CalculateMaximumSpeed(
            float gap,
            float minimumGap,
            float deltaTime)
        {
            if (deltaTime <= 0f)
                return 0f;

            float availableTravel = Mathf.Max(0f, gap - Mathf.Max(0f, minimumGap));
            return availableTravel / deltaTime;
        }
    }
}
