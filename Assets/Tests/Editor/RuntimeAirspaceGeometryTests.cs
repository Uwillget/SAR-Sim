using System.Collections.Generic;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

namespace RoadNetworkEditor.Tests
{
    public class RuntimeAirspaceGeometryTests
    {
        [Test]
        public void CircularRamp_IsSampledOnCircleInsteadOfChord()
        {
            var vertices = new Dictionary<string, Vertex>
            {
                ["A"] = new Vertex { VertexId = "A", Position = new Vector3(10f, 20f, 0f) },
                ["B"] = new Vertex { VertexId = "B", Position = new Vector3(0f, 20f, 10f) }
            };
            var circles = new Dictionary<string, Circle>
            {
                ["C"] = new Circle { CircleId = "C", Center = new Vector3(0f, 20f, 0f), Radius = 10f }
            };
            var ramp = new Ramp
            {
                FromVertexId = "A", ToVertexId = "B", LinkCircleId = "C",
                Direction = ArcDirection.CounterClockwise
            };

            List<Vector3> points = RuntimeAirspaceGeometry.BuildRampPolyline(ramp, vertices, circles, 12);

            Assert.That(points.Count, Is.GreaterThan(2));
            foreach (Vector3 point in points)
                Assert.That(Vector2.Distance(Vector2.zero, new Vector2(point.x, point.z)), Is.EqualTo(10f).Within(0.001f));
            Assert.That(points[points.Count / 2].x, Is.GreaterThan(6f));
            Assert.That(points[points.Count / 2].z, Is.GreaterThan(6f));
        }

        [Test]
        public void CrossLayerRamp_RemainsStraight()
        {
            var vertices = new Dictionary<string, Vertex>
            {
                ["A"] = new Vertex { Position = new Vector3(0f, 20f, 0f) },
                ["B"] = new Vertex { Position = new Vector3(10f, 40f, 5f) }
            };
            var ramp = new Ramp { FromVertexId = "A", ToVertexId = "B", LinkCircleId = null };

            List<Vector3> points = RuntimeAirspaceGeometry.BuildRampPolyline(ramp, vertices,
                new Dictionary<string, Circle>());

            Assert.That(points, Has.Count.EqualTo(2));
            Assert.That(points[0], Is.EqualTo(vertices["A"].Position));
            Assert.That(points[1], Is.EqualTo(vertices["B"].Position));
        }

        [Test]
        public void FacilityCorridor_BezierKeepsBothAttachmentPoints()
        {
            var lane = new Lane
            {
                Centerline = new List<Vector3> { Vector3.zero, new Vector3(10f, 0f, 0f) }
            };
            var vertices = new Dictionary<string, Vertex>
            {
                ["A"] = new Vertex { Position = new Vector3(10f, 10f, 10f) },
                ["B"] = new Vertex { Position = new Vector3(20f, 10f, 10f) }
            };
            var ramp = new Ramp { FromVertexId = "A", ToVertexId = "B" };
            var corridor = new NetConnectedCorridor
            {
                CorridorType = "Exit", LaneLinkPoint = new Vector3(5f, 0f, 0f),
                RampLinkPoint = new Vector3(15f, 10f, 10f)
            };

            List<Vector3> curve = RuntimeAirspaceGeometry.BuildCorridorPolyline(corridor, lane, ramp,
                vertices, new Dictionary<string, Circle>(), 0.3f, 16);

            Assert.That(curve[0], Is.EqualTo(corridor.LaneLinkPoint));
            Assert.That(curve[curve.Count - 1], Is.EqualTo(corridor.RampLinkPoint));
            Assert.That(curve.Count, Is.EqualTo(17));
        }

        [Test]
        public void FacilityCorridor_EntryDirectionRunsFromHoldingRampToLane()
        {
            var lane = new Lane
            {
                Centerline = new List<Vector3> { Vector3.zero, new Vector3(10f, 0f, 0f) }
            };
            var vertices = new Dictionary<string, Vertex>
            {
                ["A"] = new Vertex { Position = new Vector3(10f, 10f, 10f) },
                ["B"] = new Vertex { Position = new Vector3(20f, 10f, 10f) }
            };
            var ramp = new Ramp { FromVertexId = "A", ToVertexId = "B" };
            var corridor = new NetConnectedCorridor
            {
                CorridorType = "Entry", LaneLinkPoint = new Vector3(5f, 0f, 0f),
                RampLinkPoint = new Vector3(15f, 10f, 10f)
            };

            List<Vector3> curve = RuntimeAirspaceGeometry.BuildCorridorPolyline(corridor, lane, ramp,
                vertices, new Dictionary<string, Circle>(), 0.3f, 16);

            Assert.That(curve[0], Is.EqualTo(corridor.RampLinkPoint));
            Assert.That(curve[curve.Count - 1], Is.EqualTo(corridor.LaneLinkPoint));
        }

        [Test]
        public void PointInPolygonXZ_RejectsPointOutsideVertiport()
        {
            var polygon = new List<Vector3>
            {
                new Vector3(-5f, 0f, -5f), new Vector3(5f, 0f, -5f),
                new Vector3(5f, 0f, 5f), new Vector3(-5f, 0f, 5f)
            };

            Assert.That(RuntimeAirspaceEditor.PointInPolygonXZ(Vector3.zero, polygon), Is.True);
            Assert.That(RuntimeAirspaceEditor.PointInPolygonXZ(new Vector3(8f, 0f, 0f), polygon), Is.False);
        }
    }
}
