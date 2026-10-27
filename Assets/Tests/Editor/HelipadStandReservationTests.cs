using System.Collections.Generic;
using NUnit.Framework;
using RoadNetworkEditor.Reservations;

namespace RoadNetworkEditor.Tests
{
    public class HelipadStandReservationTests
    {
        [Test]
        public void CalculateEnd_CoversEntireRetryPeriodAndBuffer()
        {
            Assert.That(
                HelipadStandReservation.CalculateEnd(100f, 120f, 2f),
                Is.EqualTo(222f));
        }

        [Test]
        public void Release_TruncatesOnlyMatchingStandingWindow()
        {
            var ownerStand = new OccupiedTimeWindow
            {
                OccupiedAircraftId = "M1",
                StartTime = 100f,
                EndTime = 222f,
                IsStandingReservation = true
            };
            var ownerMovement = new OccupiedTimeWindow
            {
                OccupiedAircraftId = "M1",
                StartTime = 105f,
                EndTime = 110f,
                IsStandingReservation = false
            };
            var otherStand = new OccupiedTimeWindow
            {
                OccupiedAircraftId = "M2",
                StartTime = 100f,
                EndTime = 222f,
                IsStandingReservation = true
            };
            var windows = new List<OccupiedTimeWindow>
            {
                ownerStand,
                ownerMovement,
                otherStand
            };

            int released = HelipadStandReservation.Release(windows, "M1", 108f);

            Assert.That(released, Is.EqualTo(1));
            Assert.That(ownerStand.EndTime, Is.EqualTo(108f));
            Assert.That(ownerMovement.EndTime, Is.EqualTo(110f));
            Assert.That(otherStand.EndTime, Is.EqualTo(222f));
        }
    }
}
