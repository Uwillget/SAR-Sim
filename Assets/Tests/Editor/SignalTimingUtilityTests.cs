using NUnit.Framework;
using RoadNetworkEditor.Networking;

namespace RoadNetworkEditor.Tests.Editor
{
    public class SignalTimingUtilityTests
    {
        [Test]
        public void Evaluate_InsertsAllRedClearanceAfterEachGreen()
        {
            SignalPhaseState green = SignalTimingUtility.Evaluate(9.9f, 0f, 3, 10f, 2f);
            SignalPhaseState clearance = SignalTimingUtility.Evaluate(10.5f, 0f, 3, 10f, 2f);
            SignalPhaseState nextGreen = SignalTimingUtility.Evaluate(12.1f, 0f, 3, 10f, 2f);

            Assert.That(green.ActivePhaseIndex, Is.EqualTo(0));
            Assert.That(green.IsClearance, Is.False);
            Assert.That(clearance.ActivePhaseIndex, Is.EqualTo(0));
            Assert.That(clearance.IsClearance, Is.True);
            Assert.That(nextGreen.ActivePhaseIndex, Is.EqualTo(1));
            Assert.That(nextGreen.IsClearance, Is.False);
        }

        [Test]
        public void Evaluate_AppliesAndWrapsOffsetDeterministically()
        {
            SignalPhaseState state = SignalTimingUtility.Evaluate(1f, 13f, 2, 10f, 2f);

            Assert.That(state.ActivePhaseIndex, Is.EqualTo(1));
            Assert.That(state.IsClearance, Is.False);
            Assert.That(SignalTimingUtility.NormalizeOffset(26f, 24f), Is.EqualTo(2f));
        }
    }
}
