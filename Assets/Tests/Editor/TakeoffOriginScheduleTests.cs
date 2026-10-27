using NUnit.Framework;
using RoadNetworkEditor.Reservations;
using UnityEngine;

namespace RoadNetworkEditor.Tests
{
    public class TakeoffOriginScheduleTests
    {
        [Test]
        public void CalculateHold_MovesFirstNodeDelayBackToHelipad()
        {
            float hold = TakeoffOriginSchedule.CalculateHold(
                10f,
                Vector3.zero,
                new Vector3(20f, 0f, 0f),
                16f,
                20f);

            Assert.That(hold, Is.EqualTo(5f).Within(0.0001f));
        }

        [Test]
        public void CalculateHold_CoLocatedNodeHoldsUntilReservedArrival()
        {
            float hold = TakeoffOriginSchedule.CalculateHold(
                34.149f,
                new Vector3(715.44f, 0f, -530.87f),
                new Vector3(715.44f, 0f, -530.87f),
                40.024f,
                20f);

            Assert.That(hold, Is.EqualTo(5.875f).Within(0.0001f));
        }
    }
}
