using NUnit.Framework;

namespace RoadNetworkEditor.Tests
{
    public class FollowingSafetyUtilityTests
    {
        [TestCase(10f, 10f, 0.02f, 0f)]
        [TestCase(11f, 10f, 0.02f, 50f)]
        [TestCase(14f, 10f, 0.08f, 50f)]
        [TestCase(2f, 10f, 0.02f, 0f)]
        public void CalculateMaximumSpeed_DoesNotCrossMinimumGap(
            float gap,
            float minimumGap,
            float deltaTime,
            float expected)
        {
            float result = FollowingSafetyUtility.CalculateMaximumSpeed(
                gap,
                minimumGap,
                deltaTime);

            Assert.That(result, Is.EqualTo(expected).Within(0.0001f));
        }
    }
}
