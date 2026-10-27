using UnityEngine;

namespace RoadNetworkEditor.Networking
{
    public readonly struct SignalPhaseState
    {
        public readonly int ActivePhaseIndex;
        public readonly bool IsClearance;

        public SignalPhaseState(int activePhaseIndex, bool isClearance)
        {
            ActivePhaseIndex = activePhaseIndex;
            IsClearance = isClearance;
        }
    }

    public static class SignalTimingUtility
    {
        public static SignalPhaseState Evaluate(
            float simulationTime,
            float offset,
            int phaseCount,
            float greenDuration,
            float clearanceDuration)
        {
            if (phaseCount <= 0) return new SignalPhaseState(-1, true);

            greenDuration = Mathf.Max(0.01f, greenDuration);
            clearanceDuration = Mathf.Max(0f, clearanceDuration);
            float slotDuration = greenDuration + clearanceDuration;
            float cycleDuration = phaseCount * slotDuration;
            float localTime = (simulationTime - offset) % cycleDuration;
            if (localTime < 0f) localTime += cycleDuration;
            int phaseIndex = Mathf.Clamp(
                Mathf.FloorToInt(localTime / slotDuration),
                0,
                phaseCount - 1);
            float withinSlot = localTime - phaseIndex * slotDuration;
            return new SignalPhaseState(phaseIndex, withinSlot >= greenDuration);
        }

        public static float NormalizeOffset(float offset, float cycleDuration)
        {
            if (cycleDuration <= 0f) return 0f;
            float normalized = offset % cycleDuration;
            return normalized < 0f ? normalized + cycleDuration : normalized;
        }
    }
}
