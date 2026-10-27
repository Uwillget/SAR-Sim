using System;
using System.Collections.Generic;

namespace RoadNetworkEditor.Networking
{
    public static class LaneChangeDecisionUtility
    {
        public static bool HasSafeGap(
            float mergeProgress,
            IEnumerable<float> occupiedProgress,
            float laneLength,
            float requiredFrontGap,
            float requiredRearGap)
        {
            laneLength = Math.Max(laneLength, 0.001f);
            requiredFrontGap = Math.Max(0f, requiredFrontGap);
            requiredRearGap = Math.Max(0f, requiredRearGap);
            foreach (float occupied in occupiedProgress)
            {
                float distance = (occupied - mergeProgress) * laneLength;
                if (distance >= 0f && distance < requiredFrontGap) return false;
                if (distance < 0f && -distance < requiredRearGap) return false;
            }
            return true;
        }

        public static bool CandidateHasPriority(
            int candidatePriority,
            float candidateWaitStartedAt,
            string candidateId,
            int requesterPriority,
            float requesterWaitStartedAt,
            string requesterId)
        {
            if (candidatePriority != requesterPriority)
                return candidatePriority > requesterPriority;
            if (candidateWaitStartedAt >= 0f && requesterWaitStartedAt >= 0f &&
                Math.Abs(candidateWaitStartedAt - requesterWaitStartedAt) > 0.0001f)
                return candidateWaitStartedAt < requesterWaitStartedAt;
            return string.Compare(candidateId, requesterId, StringComparison.Ordinal) < 0;
        }
    }
}
