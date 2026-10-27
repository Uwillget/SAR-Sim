using System.Collections.Generic;
using NUnit.Framework;
using RoadNetworkEditor.Routes;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public class RouteDemandAuthoringTests
    {
        [Test]
        public void ManualCenterlineClick_AppendsRejectsAndRollsBackWithSharedRules()
        {
            GameObject simulationObject = new GameObject("RouteDemandAuthoring test");
            simulationObject.SetActive(false);
            try
            {
                SimulationManager manager = simulationObject.AddComponent<SimulationManager>();
                ConfigureSimpleRoute(manager);
                var selectedEdges = new List<string> { "E1" };

                bool appended = RouteDemandAuthoring.TryApplyManualEdgeClick(manager,
                    "HP-ORIGIN", "HP-DESTINATION", selectedEdges, "E2",
                    out List<string> route, out bool connectsDestination, out string appendError);

                Assert.That(appended, Is.True, appendError);
                Assert.That(connectsDestination, Is.True);
                Assert.That(selectedEdges, Is.EqualTo(new[] { "E1", "E2" }));
                Assert.That(route, Is.EqualTo(new[] { "L1", "L2" }));

                bool rejected = RouteDemandAuthoring.TryApplyManualEdgeClick(manager,
                    "HP-ORIGIN", "HP-DESTINATION", selectedEdges, "E3",
                    out _, out _, out string rejectionError);

                Assert.That(rejected, Is.False);
                StringAssert.Contains("does not continue", rejectionError);
                Assert.That(selectedEdges, Is.EqualTo(new[] { "E1", "E2" }),
                    "A rejected Scene-view click must not mutate the selected route.");

                bool rolledBack = RouteDemandAuthoring.TryApplyManualEdgeClick(manager,
                    "HP-ORIGIN", "HP-DESTINATION", selectedEdges, "E1",
                    out List<string> prefix, out connectsDestination, out string rollbackError);

                Assert.That(rolledBack, Is.True, rollbackError);
                Assert.That(connectsDestination, Is.False);
                Assert.That(selectedEdges, Is.EqualTo(new[] { "E1" }));
                Assert.That(prefix, Is.EqualTo(new[] { "L1" }));
            }
            finally
            {
                Object.DestroyImmediate(simulationObject);
            }
        }

        private static void ConfigureSimpleRoute(SimulationManager manager)
        {
            Lane first = Lane("L1", 0f);
            Lane second = Lane("L2", 10f);
            Lane disconnected = Lane("L3", 20f);
            first.ToLanesId.Add(second.LaneId);

            Edge firstEdge = Edge("E1", first);
            Edge secondEdge = Edge("E2", second);
            Edge disconnectedEdge = Edge("E3", disconnected);
            manager.Edges[firstEdge.EdgeId] = firstEdge;
            manager.Edges[secondEdge.EdgeId] = secondEdge;
            manager.Edges[disconnectedEdge.EdgeId] = disconnectedEdge;
            manager.Lanes[first.LaneId] = first;
            manager.Lanes[second.LaneId] = second;
            manager.Lanes[disconnected.LaneId] = disconnected;
            manager.LaneToEdgeMap[first.LaneId] = firstEdge.EdgeId;
            manager.LaneToEdgeMap[second.LaneId] = secondEdge.EdgeId;
            manager.LaneToEdgeMap[disconnected.LaneId] = disconnectedEdge.EdgeId;

            var originPad = new Helipad
            {
                HelipadId = "HP-ORIGIN",
                ReachableCorridorIds = new List<string> { "ENTRY" }
            };
            var destinationPad = new Helipad
            {
                HelipadId = "HP-DESTINATION",
                ReachableCorridorIds = new List<string> { "EXIT" }
            };
            var originVertiport = new Vertiport
            {
                VertiportId = "VP-ORIGIN",
                Helipads = new List<Helipad> { originPad },
                Corridors = new List<NetConnectedCorridor>
                {
                    new NetConnectedCorridor
                        { CorridorId = "ENTRY", CorridorType = "Entry", LinkLaneId = first.LaneId }
                }
            };
            var destinationVertiport = new Vertiport
            {
                VertiportId = "VP-DESTINATION",
                Helipads = new List<Helipad> { destinationPad },
                Corridors = new List<NetConnectedCorridor>
                {
                    new NetConnectedCorridor
                        { CorridorId = "EXIT", CorridorType = "Exit", LinkLaneId = second.LaneId }
                }
            };
            manager.Vertiports[originVertiport.VertiportId] = originVertiport;
            manager.Vertiports[destinationVertiport.VertiportId] = destinationVertiport;
            manager.Helipads[originPad.HelipadId] = originPad;
            manager.Helipads[destinationPad.HelipadId] = destinationPad;
            manager.HelipadToVertiportMap[originPad.HelipadId] = originVertiport.VertiportId;
            manager.HelipadToVertiportMap[destinationPad.HelipadId] = destinationVertiport.VertiportId;
            manager.pathPlanner = new PathPlanner(manager);
        }

        private static Edge Edge(string id, Lane lane)
        {
            return new Edge
            {
                EdgeId = id,
                Centerline = new List<Vector3>(lane.Centerline),
                Lanes = new List<Lane> { lane }
            };
        }

        private static Lane Lane(string id, float z)
        {
            return new Lane
            {
                LaneId = id,
                Length = 100f,
                Centerline = new List<Vector3>
                {
                    new Vector3(0f, 75f, z),
                    new Vector3(100f, 75f, z)
                },
                FromLanesId = new List<string>(),
                ToLanesId = new List<string>()
            };
        }
    }
}
