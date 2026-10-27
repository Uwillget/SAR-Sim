using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace RoadNetworkEditor.Tests
{
    public class RandomDemandAllocatorTests
    {
        [Test]
        public void Allocate_NeverReusesAHelipadAcrossOriginsAndDestinations()
        {
            var endpoints = new List<HelipadEndpoint>();
            for (int vp = 1; vp <= 4; vp++)
                for (int hp = 1; hp <= 6; hp++)
                    endpoints.Add(new HelipadEndpoint($"VP{vp}", $"VP{vp}-HP{hp}"));

            List<RandomMissionEndpoints> allocations =
                RandomDemandAllocator.Allocate(endpoints, 10, new Random(12345));

            Assert.That(allocations, Has.Count.EqualTo(10));
            Assert.That(allocations.All(item =>
                item.Origin.VertiportId != item.Destination.VertiportId), Is.True);

            string[] assigned = allocations
                .SelectMany(item => new[]
                {
                    item.Origin.HelipadId,
                    item.Destination.HelipadId
                })
                .ToArray();
            Assert.That(assigned.Distinct().Count(), Is.EqualTo(assigned.Length));
        }

        [Test]
        public void Allocate_StopsWhenNoCrossVertiportPairRemains()
        {
            var endpoints = new List<HelipadEndpoint>
            {
                new HelipadEndpoint("VP1", "HP1"),
                new HelipadEndpoint("VP1", "HP2"),
                new HelipadEndpoint("VP2", "HP3")
            };

            List<RandomMissionEndpoints> allocations =
                RandomDemandAllocator.Allocate(endpoints, 3, new Random(7));

            Assert.That(allocations, Has.Count.EqualTo(1));
        }

        [Test]
        public void Capacity_ReservesTwoUniquePadsAndAccountsForDominantVertiport()
        {
            var endpoints = new List<HelipadEndpoint>();
            for (int i = 0; i < 8; i++) endpoints.Add(new HelipadEndpoint("VP1", "A" + i));
            for (int i = 0; i < 3; i++) endpoints.Add(new HelipadEndpoint("VP2", "B" + i));
            endpoints.Add(new HelipadEndpoint("VP3", "C0"));

            Assert.That(RandomDemandAllocator.CalculateSafeFleetCapacity(endpoints), Is.EqualTo(4));
            Assert.That(RandomDemandAllocator.Allocate(endpoints, 20, new Random(3)), Has.Count.EqualTo(4));
        }
    }
}
