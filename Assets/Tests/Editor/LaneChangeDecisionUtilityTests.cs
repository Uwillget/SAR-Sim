using NUnit.Framework;
using RoadNetworkEditor.Networking;

namespace RoadNetworkEditor.Tests.Editor
{
    public class LaneChangeDecisionUtilityTests
    {
        [Test]
        public void HasSafeGap_UsesDifferentFrontAndRearRequirements()
        {
            Assert.That(LaneChangeDecisionUtility.HasSafeGap(
                0.5f, new[] { 0.62f, 0.3f }, 100f, 10f, 15f), Is.True);
            Assert.That(LaneChangeDecisionUtility.HasSafeGap(
                0.5f, new[] { 0.58f }, 100f, 10f, 15f), Is.False);
            Assert.That(LaneChangeDecisionUtility.HasSafeGap(
                0.5f, new[] { 0.36f }, 100f, 10f, 15f), Is.False);
        }

        [Test]
        public void CandidateHasPriority_UsesMissionPriorityThenAgeThenStableId()
        {
            Assert.That(LaneChangeDecisionUtility.CandidateHasPriority(
                2, 10f, "B", 1, 1f, "A"), Is.True);
            Assert.That(LaneChangeDecisionUtility.CandidateHasPriority(
                1, 5f, "B", 1, 10f, "A"), Is.True);
            Assert.That(LaneChangeDecisionUtility.CandidateHasPriority(
                1, 5f, "A", 1, 5f, "B"), Is.True);
        }
    }
}
