using NUnit.Framework;

namespace RoadNetworkEditor.Tests
{
    public class LaneEntryDecisionUtilityTests
    {
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void CanEnter_BlockedSourceLane_ReturnsFalse(
            bool signalBlocked,
            bool capacityBlocked)
        {
            Assert.That(
                LaneEntryDecisionUtility.CanEnter(
                    signalBlocked,
                    capacityBlocked,
                    new[] { 0.8f },
                    100f,
                    10f),
                Is.False);
        }

        [Test]
        public void CanEnter_AircraftInsideEntryGap_ReturnsFalse()
        {
            Assert.That(
                LaneEntryDecisionUtility.CanEnter(
                    false,
                    false,
                    new[] { 0.05f },
                    100f,
                    10f),
                Is.False);
        }

        [Test]
        public void CanEnter_ClearEntry_ReturnsTrue()
        {
            Assert.That(
                LaneEntryDecisionUtility.CanEnter(
                    false,
                    false,
                    new[] { 0.2f },
                    100f,
                    10f),
                Is.True);
        }

        [Test]
        public void CanEnter_LayerConnector_IgnoresSourceEndpointControl()
        {
            Assert.That(
                LaneEntryDecisionUtility.CanEnter(
                    true,
                    true,
                    new[] { 0.2f },
                    100f,
                    10f,
                    enforceSourceControl: false),
                Is.True);
        }

        [Test]
        public void CanEnter_LayerConnector_StillBlocksOccupiedEntryGap()
        {
            Assert.That(
                LaneEntryDecisionUtility.CanEnter(
                    true,
                    true,
                    new[] { 0.05f },
                    100f,
                    10f,
                    enforceSourceControl: false),
                Is.False);
        }
    }
}
