using NUnit.Framework;
using UnityEngine;

namespace RoadNetworkEditor.Tests
{
    public class VertiportWaypointSafetyTests
    {
        [Test]
        public void ConflictsWithAircraft_BlocksPhysicalOccupant()
        {
            Assert.That(VertiportWaypointSafety.ConflictsWithAircraft(
                "M1", Vector3.zero,
                "M2", new Vector3(1f, 0f, 0f), new Vector3(20f, 0f, 0f),
                true, 3.5f), Is.True);
        }

        [Test]
        public void ConflictsWithAircraft_BlocksAircraftHeadingToSamePoint()
        {
            Assert.That(VertiportWaypointSafety.ConflictsWithAircraft(
                "M1", Vector3.zero,
                "M2", new Vector3(20f, 0f, 0f), new Vector3(2f, 0f, 0f),
                true, 3.5f), Is.True);
        }

        [Test]
        public void ConflictsWithAircraft_IgnoresSelfAndInactiveAircraft()
        {
            Assert.That(VertiportWaypointSafety.ConflictsWithAircraft(
                "M1", Vector3.zero,
                "M1", Vector3.zero, Vector3.zero,
                true, 3.5f), Is.False);
            Assert.That(VertiportWaypointSafety.ConflictsWithAircraft(
                "M1", Vector3.zero,
                "M2", Vector3.zero, Vector3.zero,
                false, 3.5f), Is.False);
        }
    }
}
