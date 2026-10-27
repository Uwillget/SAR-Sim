using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RoadNetworkEditor.Routes;

namespace RoadNetworkEditor.Tests.Editor
{
    public class ExplicitRouteResolverTests
    {
        [Test]
        public void Resolve_InsertsOmittedMultilayerConnector()
        {
            var lanes = BuildLanes();
            var connectors = BuildConnectors();

            ExplicitRouteResolveResult result = ExplicitRouteResolver.Resolve(
                new[] { "L-A", "L-B" }, lanes, connectors, "L-A", "L-B");

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.Lanes.ConvertAll(lane => lane.LaneId),
                Is.EqualTo(new[] { "L-A", "LC-1", "L-B" }));
        }

        [Test]
        public void Resolve_AcceptsExplicitConnectorSequence()
        {
            ExplicitRouteResolveResult result = ExplicitRouteResolver.Resolve(
                new[] { "L-A", "LC-1", "L-B" },
                BuildLanes(), BuildConnectors(), "L-A", "L-B");

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.Lanes, Has.Count.EqualTo(3));
        }

        [Test]
        public void Resolve_ExpandsOmittedConnectorsAcrossThreeLayersForExecution()
        {
            var lanes = new Dictionary<string, Lane>
            {
                ["L0"] = new Lane { LaneId = "L0", ToLanesId = new List<string>() },
                ["C01"] = new Lane { LaneId = "C01", LaneType = "LayerConnector" },
                ["L1"] = new Lane { LaneId = "L1", ToLanesId = new List<string>() },
                ["C12"] = new Lane { LaneId = "C12", LaneType = "LayerConnector" },
                ["L2"] = new Lane { LaneId = "L2", ToLanesId = new List<string>() }
            };
            var connectors = new List<LaneConnectionCorridor>
            {
                new LaneConnectionCorridor { CorridorId = "C01", FromLaneId = "L0", ToLaneId = "L1" },
                new LaneConnectionCorridor { CorridorId = "C12", FromLaneId = "L1", ToLaneId = "L2" }
            };

            ExplicitRouteResolveResult result = ExplicitRouteResolver.Resolve(
                new[] { "L0", "L1", "L2" }, lanes, connectors, "L0", "L2");

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.Lanes.Select(lane => lane.LaneId),
                Is.EqualTo(new[] { "L0", "C01", "L1", "C12", "L2" }));
        }

        [Test]
        public void Resolve_RejectsDisconnectedLanePair()
        {
            var lanes = BuildLanes();

            ExplicitRouteResolveResult result = ExplicitRouteResolver.Resolve(
                new[] { "L-A", "L-B" }, lanes,
                new List<LaneConnectionCorridor>(), "L-A", "L-B");

            Assert.That(result.Success, Is.False);
            StringAssert.Contains("No directed connection", result.Error);
        }

        [Test]
        public void Resolve_AcceptsOnlyAdjacentSameDirectionLaneChanges()
        {
            Lane lane0 = HorizontalLane("L0", 0f);
            Lane lane1 = HorizontalLane("L1", 4f);
            Lane lane2 = HorizontalLane("L2", 8f);
            var lanes = new[] { lane0, lane1, lane2 }
                .ToDictionary(lane => lane.LaneId);
            var laneToEdge = lanes.Keys.ToDictionary(id => id, _ => "E1");

            ExplicitRouteResolveResult adjacent = ExplicitRouteResolver.Resolve(
                new[] { "L0", "L1" }, lanes,
                new List<LaneConnectionCorridor>(), "L0", "L1", laneToEdge);
            ExplicitRouteResolveResult skipped = ExplicitRouteResolver.Resolve(
                new[] { "L0", "L2" }, lanes,
                new List<LaneConnectionCorridor>(), "L0", "L2", laneToEdge);

            Assert.That(adjacent.Success, Is.True, adjacent.Error);
            Assert.That(skipped.Success, Is.False);
        }

        private static Dictionary<string, Lane> BuildLanes()
        {
            return new Dictionary<string, Lane>
            {
                ["L-A"] = new Lane
                {
                    LaneId = "L-A",
                    ToLanesId = new List<string>()
                },
                ["LC-1"] = new Lane
                {
                    LaneId = "LC-1",
                    LaneType = "LayerConnector",
                    FromLanesId = new List<string> { "L-A" },
                    ToLanesId = new List<string> { "L-B" }
                },
                ["L-B"] = new Lane
                {
                    LaneId = "L-B",
                    FromLanesId = new List<string>(),
                    ToLanesId = new List<string>()
                }
            };
        }

        private static List<LaneConnectionCorridor> BuildConnectors()
        {
            return new List<LaneConnectionCorridor>
            {
                new LaneConnectionCorridor
                {
                    CorridorId = "LC-1",
                    FromLaneId = "L-A",
                    ToLaneId = "L-B"
                }
            };
        }

        private static Lane HorizontalLane(string id, float z)
        {
            return new Lane
            {
                LaneId = id,
                Centerline = new List<UnityEngine.Vector3>
                {
                    new UnityEngine.Vector3(0f, 75f, z),
                    new UnityEngine.Vector3(100f, 75f, z)
                }
            };
        }
    }
}
