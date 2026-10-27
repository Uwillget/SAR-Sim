using System.Collections.Generic;
using NUnit.Framework;
using RoadNetworkEditor.Networking;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public class LaneGeometryUtilityTests
    {
        [Test]
        public void ProjectProgress_UsesArcLengthInsteadOfStartPointDistance()
        {
            var polyline = new List<Vector3>
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(10f, 0f, 0f),
                new Vector3(10f, 0f, 10f)
            };

            float progress = LaneGeometryUtility.ProjectProgress(
                polyline,
                new Vector3(10f, 0f, 5f));

            Assert.That(progress, Is.EqualTo(0.75f).Within(0.0001f));
        }

        [Test]
        public void PointAtProgress_InterpolatesAcrossPolylineSegments()
        {
            var polyline = new List<Vector3>
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(10f, 0f, 0f),
                new Vector3(10f, 0f, 10f)
            };

            Vector3 point = LaneGeometryUtility.PointAtProgress(polyline, 0.75f);

            Assert.That(point.x, Is.EqualTo(10f).Within(0.0001f));
            Assert.That(point.z, Is.EqualTo(5f).Within(0.0001f));
        }

        [Test]
        public void ProjectProgress_ChoosesNearestSegmentOnBentLane()
        {
            var polyline = new List<Vector3>
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(10f, 0f, 0f),
                new Vector3(10f, 0f, 10f)
            };

            float progress = LaneGeometryUtility.ProjectProgress(
                polyline,
                new Vector3(8f, 0f, 6f));

            Assert.That(progress, Is.EqualTo(0.8f).Within(0.0001f));
        }

        [Test]
        public void BuildRouteTraversal_ClipsSourceAndDestinationAtLayerConnectorAttachments()
        {
            Lane source = TestLane("A", 0f, 0f, 100f);
            Lane destination = TestLane("B", 50f, 100f, 150f);
            var corridor = new LaneConnectionCorridor
            {
                CorridorId = "C",
                FromLaneId = source.LaneId,
                ToLaneId = destination.LaneId,
                FromPoint = new Vector3(40f, 0f, 0f),
                ToPoint = new Vector3(90f, 100f, 0f),
                Centerline = new List<Vector3>
                {
                    new Vector3(40f, 0f, 0f),
                    new Vector3(65f, 50f, 0f),
                    new Vector3(90f, 100f, 0f)
                }
            };
            Lane virtualConnector = MultilayerNetworkUtility.CreateVirtualLane(corridor);
            var route = new List<Lane> { source, virtualConnector, destination };
            var connectors = new Dictionary<string, LaneConnectionCorridor> { ["C"] = corridor };

            List<Vector3> sourceTraversal = LaneGeometryUtility.BuildRouteTraversal(route, 0, connectors);
            List<Vector3> connectorTraversal = LaneGeometryUtility.BuildRouteTraversal(route, 1, connectors);
            List<Vector3> destinationTraversal = LaneGeometryUtility.BuildRouteTraversal(route, 2, connectors);

            Assert.That(sourceTraversal[sourceTraversal.Count - 1], Is.EqualTo(corridor.FromPoint));
            Assert.That(sourceTraversal, Has.None.EqualTo(new Vector3(100f, 0f, 0f)),
                "The aircraft must not visit A's terminal node after reaching the connector attachment.");
            Assert.That(connectorTraversal, Is.EqualTo(corridor.Centerline));
            Assert.That(destinationTraversal[0], Is.EqualTo(corridor.ToPoint));
            Assert.That(destinationTraversal, Has.None.EqualTo(new Vector3(50f, 100f, 0f)),
                "The aircraft must not fly backward from the connector attachment to B's start.");
            Assert.That(destinationTraversal[destinationTraversal.Count - 1],
                Is.EqualTo(new Vector3(150f, 100f, 0f)));
        }

        [Test]
        public void BuildRouteTraversal_TwoConnectorsOnSameLane_UsesOnlyDownstreamInterval()
        {
            Lane first = TestLane("A", 0f, 0f, 100f);
            Lane middle = TestLane("B", 0f, 50f, 100f);
            Lane last = TestLane("D", 0f, 100f, 100f);
            LaneConnectionCorridor incoming = Connector("C1", "A", "B",
                new Vector3(30f, 0f, 0f), new Vector3(20f, 50f, 0f));
            LaneConnectionCorridor outgoing = Connector("C2", "B", "D",
                new Vector3(70f, 50f, 0f), new Vector3(80f, 100f, 0f));
            var route = new List<Lane>
            {
                first,
                MultilayerNetworkUtility.CreateVirtualLane(incoming),
                middle,
                MultilayerNetworkUtility.CreateVirtualLane(outgoing),
                last
            };
            var connectors = new Dictionary<string, LaneConnectionCorridor>
            {
                ["C1"] = incoming,
                ["C2"] = outgoing
            };

            List<Vector3> traversal = LaneGeometryUtility.BuildRouteTraversal(route, 2, connectors);

            Assert.That(traversal[0], Is.EqualTo(incoming.ToPoint));
            Assert.That(traversal[traversal.Count - 1], Is.EqualTo(outgoing.FromPoint));
            Assert.That(traversal, Has.None.EqualTo(new Vector3(0f, 50f, 0f)));
            Assert.That(traversal, Has.None.EqualTo(new Vector3(100f, 50f, 0f)));
        }

        [Test]
        public void BuildRouteTraversal_TwoConnectorsOnSameLane_RejectsBacktrackingInterval()
        {
            Lane middle = TestLane("B", 0f, 50f, 100f);
            LaneConnectionCorridor incoming = Connector("C1", "A", "B",
                new Vector3(30f, 0f, 0f), new Vector3(80f, 50f, 0f));
            LaneConnectionCorridor outgoing = Connector("C2", "B", "D",
                new Vector3(20f, 50f, 0f), new Vector3(80f, 100f, 0f));
            var route = new List<Lane>
            {
                TestLane("A", 0f, 0f, 100f),
                MultilayerNetworkUtility.CreateVirtualLane(incoming),
                middle,
                MultilayerNetworkUtility.CreateVirtualLane(outgoing),
                TestLane("D", 0f, 100f, 100f)
            };
            var connectors = new Dictionary<string, LaneConnectionCorridor>
            {
                ["C1"] = incoming,
                ["C2"] = outgoing
            };

            List<Vector3> traversal = LaneGeometryUtility.BuildRouteTraversal(route, 2, connectors);

            Assert.That(traversal, Is.Empty,
                "An outgoing connector upstream of the incoming attachment would require a U-turn.");
        }

        private static Lane TestLane(string id, float startX, float y, float endX)
        {
            return new Lane
            {
                LaneId = id,
                Centerline = new List<Vector3>
                {
                    new Vector3(startX, y, 0f),
                    new Vector3((startX + endX) * 0.5f, y, 0f),
                    new Vector3(endX, y, 0f)
                }
            };
        }

        private static LaneConnectionCorridor Connector(
            string id,
            string fromLane,
            string toLane,
            Vector3 fromPoint,
            Vector3 toPoint)
        {
            return new LaneConnectionCorridor
            {
                CorridorId = id,
                FromLaneId = fromLane,
                ToLaneId = toLane,
                FromPoint = fromPoint,
                ToPoint = toPoint,
                Centerline = new List<Vector3> { fromPoint, toPoint },
                Length = Vector3.Distance(fromPoint, toPoint)
            };
        }
    }
}
