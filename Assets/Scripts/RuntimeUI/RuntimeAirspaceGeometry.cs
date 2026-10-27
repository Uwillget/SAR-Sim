using System.Collections.Generic;
using UnityEngine;

namespace RoadNetworkEditor.RuntimeUI
{
    /// <summary>Shared geometry for runtime holding ramps and facility corridors.</summary>
    public static class RuntimeAirspaceGeometry
    {
        public static List<Vector3> BuildRampPolyline(Ramp ramp,
            IReadOnlyDictionary<string, Vertex> vertices,
            IReadOnlyDictionary<string, Circle> circles, int segments = 32)
        {
            var points = new List<Vector3>();
            if (ramp == null || vertices == null ||
                !vertices.TryGetValue(ramp.FromVertexId, out Vertex from) ||
                !vertices.TryGetValue(ramp.ToVertexId, out Vertex to)) return points;

            if (string.IsNullOrEmpty(ramp.LinkCircleId) || circles == null ||
                !circles.TryGetValue(ramp.LinkCircleId, out Circle circle))
            {
                points.Add(from.Position);
                points.Add(to.Position);
                return points;
            }

            float start = Mathf.Atan2(from.Position.z - circle.Center.z, from.Position.x - circle.Center.x);
            float end = Mathf.Atan2(to.Position.z - circle.Center.z, to.Position.x - circle.Center.x);
            float delta = ResolveArcDelta(start, end, ramp.Direction);
            int count = Mathf.Max(4, segments);
            for (int i = 0; i <= count; i++)
            {
                float t = i / (float)count;
                float angle = start + delta * t;
                Vector3 point = circle.Center +
                    new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * circle.Radius;
                point.y = Mathf.Lerp(from.Position.y, to.Position.y, t);
                points.Add(point);
            }
            return points;
        }

        public static List<Vector3> BuildCorridorPolyline(NetConnectedCorridor corridor, Lane lane, Ramp ramp,
            IReadOnlyDictionary<string, Vertex> vertices,
            IReadOnlyDictionary<string, Circle> circles,
            float tangentRatio = 0.3f, int segments = 32)
        {
            var result = new List<Vector3>();
            if (corridor == null) return result;
            Vector3 laneTangent = TangentOnPolyline(lane?.Centerline, corridor.LaneLinkPoint);
            List<Vector3> rampLine = BuildRampPolyline(ramp, vertices, circles, 48);
            Vector3 rampTangent = TangentOnPolyline(rampLine, corridor.RampLinkPoint);
            if (laneTangent.sqrMagnitude < 0.0001f) laneTangent = Vector3.forward;
            if (rampTangent.sqrMagnitude < 0.0001f) rampTangent = Vector3.forward;

            bool entry = corridor.CorridorType == "Entry";
            Vector3 p0 = entry ? corridor.RampLinkPoint : corridor.LaneLinkPoint;
            Vector3 p3 = entry ? corridor.LaneLinkPoint : corridor.RampLinkPoint;
            float handle = Vector3.Distance(p0, p3) * Mathf.Clamp(tangentRatio, 0.05f, 0.75f);
            Vector3 startTangent = entry ? rampTangent : laneTangent;
            Vector3 endTangent = entry ? laneTangent : rampTangent;
            if (Vector3.Dot(startTangent, p3 - p0) < 0f) startTangent = -startTangent;
            if (Vector3.Dot(endTangent, p3 - p0) < 0f) endTangent = -endTangent;
            Vector3 p1 = p0 + startTangent.normalized * handle;
            Vector3 p2 = p3 - endTangent.normalized * handle;

            int count = Mathf.Max(8, segments);
            for (int i = 0; i <= count; i++)
                result.Add(EvaluateBezier(p0, p1, p2, p3, i / (float)count));
            return result;
        }

        public static Vector3 ClosestPointOnPolyline(IList<Vector3> points, Vector3 target)
        {
            if (points == null || points.Count == 0) return target;
            if (points.Count == 1) return points[0];
            Vector3 bestPoint = points[0];
            float best = float.MaxValue;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 point = ProjectPointOnSegment(target, points[i - 1], points[i]);
                float distance = (target - point).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                bestPoint = point;
            }
            return bestPoint;
        }

        public static Vector3 TangentOnPolyline(IList<Vector3> points, Vector3 target)
        {
            if (points == null || points.Count < 2) return Vector3.zero;
            Vector3 tangent = points[1] - points[0];
            float best = float.MaxValue;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 point = ProjectPointOnSegment(target, points[i - 1], points[i]);
                float distance = (target - point).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                tangent = points[i] - points[i - 1];
            }
            return tangent.normalized;
        }

        public static float ResolveArcDelta(float start, float end, ArcDirection direction)
        {
            float counterClockwise = Mathf.Repeat(end - start, Mathf.PI * 2f);
            if (direction == ArcDirection.CounterClockwise) return counterClockwise;
            if (direction == ArcDirection.Clockwise)
                return Mathf.Approximately(counterClockwise, 0f) ? 0f : counterClockwise - Mathf.PI * 2f;
            return counterClockwise > Mathf.PI ? counterClockwise - Mathf.PI * 2f : counterClockwise;
        }

        public static Vector3 ProjectPointOnSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 segment = end - start;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared < 0.000001f) return start;
            float t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / lengthSquared);
            return start + segment * t;
        }

        public static Vector3 EvaluateBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }
    }
}
