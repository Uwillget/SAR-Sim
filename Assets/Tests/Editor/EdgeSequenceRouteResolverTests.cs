using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RoadNetworkEditor.Routes;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public class EdgeSequenceRouteResolverTests
    {
        [Test]
        public void Resolve_ChangesLaneAutomaticallyForSelectedAxisEdges()
        {
            Lane firstOuter = Lane("E1-O", 0f);
            Lane firstInner = Lane("E1-I", 4f);
            Lane second = Lane("E2-L", 4f);
            firstInner.ToLanesId.Add(second.LaneId);
            var edges = new[]
            {
                new Edge { EdgeId = "E1", Lanes = new List<Lane> { firstOuter, firstInner } },
                new Edge { EdgeId = "E2", Lanes = new List<Lane> { second } }
            };
            Dictionary<string, Lane> lanes = edges.SelectMany(edge => edge.Lanes)
                .ToDictionary(lane => lane.LaneId);
            Dictionary<string, string> map = edges.SelectMany(edge => edge.Lanes,
                    (edge, lane) => new { lane.LaneId, edge.EdgeId })
                .ToDictionary(item => item.LaneId, item => item.EdgeId);

            EdgeSequenceResolveResult result = EdgeSequenceRouteResolver.Resolve(
                new[] { "E1", "E2" }, firstOuter.LaneId,
                new HashSet<string> { second.LaneId }, lanes, edges,
                new List<LaneConnectionCorridor>(), map);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.Lanes.Select(lane => lane.LaneId),
                Is.EqualTo(new[] { "E1-O", "E1-I", "E2-L" }));
        }

        [Test]
        public void Resolve_RejectsAxisEdgeWithoutLaneLevelConnection()
        {
            Lane first = Lane("E1-L", 0f);
            Lane disconnected = Lane("E2-L", 20f);
            var edges = new[]
            {
                new Edge { EdgeId = "E1", Lanes = new List<Lane> { first } },
                new Edge { EdgeId = "E2", Lanes = new List<Lane> { disconnected } }
            };
            Dictionary<string, Lane> lanes = edges.SelectMany(edge => edge.Lanes)
                .ToDictionary(lane => lane.LaneId);
            var map = new Dictionary<string, string>
                { [first.LaneId] = "E1", [disconnected.LaneId] = "E2" };

            EdgeSequenceResolveResult result = EdgeSequenceRouteResolver.Resolve(
                new[] { "E1", "E2" }, first.LaneId, null, lanes, edges,
                new List<LaneConnectionCorridor>(), map);

            Assert.That(result.Success, Is.False);
            StringAssert.Contains("The locked first edge cannot connect to", result.Error);
        }

        [Test]
        public void Resolve_InsertsLayerConnectorBetweenSelectedEdges()
        {
            Lane lower = Lane("LOW", 0f);
            Lane connectorLane = Lane("LC", 0f);
            Lane upper = Lane("HIGH", 0f);
            var edges = new[]
            {
                new Edge { EdgeId = "LOW-E", Lanes = new List<Lane> { lower } },
                new Edge { EdgeId = "HIGH-E", Lanes = new List<Lane> { upper } }
            };
            var lanes = new[] { lower, connectorLane, upper }.ToDictionary(lane => lane.LaneId);
            var map = new Dictionary<string, string>
                { [lower.LaneId] = "LOW-E", [upper.LaneId] = "HIGH-E" };
            var connectors = new[]
            {
                new LaneConnectionCorridor
                    { CorridorId = "LC", FromLaneId = "LOW", ToLaneId = "HIGH" }
            };

            EdgeSequenceResolveResult result = EdgeSequenceRouteResolver.Resolve(
                new[] { "LOW-E", "HIGH-E" }, lower.LaneId,
                new HashSet<string> { upper.LaneId }, lanes, edges, connectors, map);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.Lanes.Select(lane => lane.LaneId),
                Is.EqualTo(new[] { "LOW", "LC", "HIGH" }));
        }

        [Test]
        public void Resolve_TraversesThreeLayersAndCalculatesEveryConnectorLane()
        {
            Lane lower = Lane("L0-LANE", 0f);
            Lane middle = Lane("L1-LANE", 10f);
            Lane upper = Lane("L2-LANE", 20f);
            Lane lowerToMiddle = Lane("LC-01", 5f);
            Lane middleToUpper = Lane("LC-12", 15f);
            var edges = new[]
            {
                new Edge { EdgeId = "E-L0", Lanes = new List<Lane> { lower } },
                new Edge { EdgeId = "E-L1", Lanes = new List<Lane> { middle } },
                new Edge { EdgeId = "E-L2", Lanes = new List<Lane> { upper } }
            };
            var lanes = new[] { lower, lowerToMiddle, middle, middleToUpper, upper }
                .ToDictionary(lane => lane.LaneId);
            var map = new Dictionary<string, string>
            {
                [lower.LaneId] = "E-L0",
                [middle.LaneId] = "E-L1",
                [upper.LaneId] = "E-L2"
            };
            var connectors = new[]
            {
                new LaneConnectionCorridor
                    { CorridorId = lowerToMiddle.LaneId, FromLaneId = lower.LaneId, ToLaneId = middle.LaneId },
                new LaneConnectionCorridor
                    { CorridorId = middleToUpper.LaneId, FromLaneId = middle.LaneId, ToLaneId = upper.LaneId }
            };

            EdgeSequenceResolveResult result = EdgeSequenceRouteResolver.Resolve(
                new[] { "E-L0", "E-L1", "E-L2" }, lower.LaneId,
                new HashSet<string> { upper.LaneId }, lanes, edges, connectors, map);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.Lanes.Select(lane => lane.LaneId),
                Is.EqualTo(new[] { "L0-LANE", "LC-01", "L1-LANE", "LC-12", "L2-LANE" }));
        }

        [Test]
        public void Resolve_RejectsLayerConnectorInTheWrongDirection()
        {
            Lane lower = Lane("LOW-D", 0f);
            Lane upper = Lane("HIGH-D", 10f);
            Lane connector = Lane("LC-UP", 5f);
            var edges = new[]
            {
                new Edge { EdgeId = "E-HIGH", Lanes = new List<Lane> { upper } },
                new Edge { EdgeId = "E-LOW", Lanes = new List<Lane> { lower } }
            };
            var lanes = new[] { lower, connector, upper }.ToDictionary(lane => lane.LaneId);
            var map = new Dictionary<string, string>
                { [lower.LaneId] = "E-LOW", [upper.LaneId] = "E-HIGH" };
            var connectors = new[]
            {
                new LaneConnectionCorridor
                    { CorridorId = connector.LaneId, FromLaneId = lower.LaneId, ToLaneId = upper.LaneId }
            };

            EdgeSequenceResolveResult result = EdgeSequenceRouteResolver.Resolve(
                new[] { "E-HIGH", "E-LOW" }, upper.LaneId, null, lanes, edges, connectors, map);

            Assert.That(result.Success, Is.False);
            StringAssert.Contains("The locked first edge cannot connect to", result.Error);
        }

        private static Lane Lane(string id, float z)
        {
            return new Lane
            {
                LaneId = id,
                Centerline = new List<Vector3>
                {
                    new Vector3(0f, 75f, z),
                    new Vector3(100f, 75f, z)
                },
                ToLanesId = new List<string>()
            };
        }
    }
}
