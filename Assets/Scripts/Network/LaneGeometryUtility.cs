using System.Collections.Generic;
using UnityEngine;

namespace RoadNetworkEditor.Networking
{
    public static class LaneGeometryUtility
    {
        public static float PolylineLength(IReadOnlyList<Vector3> points)
        {
            if (points == null || points.Count < 2) return 0f;
            float length = 0f;
            for (int i = 1; i < points.Count; i++)
                length += Vector3.Distance(points[i - 1], points[i]);
            return length;
        }

        public static float ProjectDistance(IReadOnlyList<Vector3> points, Vector3 position)
        {
            if (points == null || points.Count < 2) return 0f;

            float bestSquaredDistance = float.MaxValue;
            float bestDistanceAlong = 0f;
            float accumulated = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 start = points[i - 1];
                Vector3 segment = points[i] - start;
                float length = segment.magnitude;
                if (length <= 0.0001f) continue;

                float t = Mathf.Clamp01(Vector3.Dot(position - start, segment) /
                                        (length * length));
                Vector3 projected = start + segment * t;
                float squaredDistance = (position - projected).sqrMagnitude;
                if (squaredDistance < bestSquaredDistance)
                {
                    bestSquaredDistance = squaredDistance;
                    bestDistanceAlong = accumulated + length * t;
                }
                accumulated += length;
            }
            return bestDistanceAlong;
        }

        public static float ProjectProgress(IReadOnlyList<Vector3> points, Vector3 position)
        {
            float length = PolylineLength(points);
            return length <= 0.0001f
                ? 0f
                : Mathf.Clamp01(ProjectDistance(points, position) / length);
        }

        public static Vector3 PointAtProgress(IReadOnlyList<Vector3> points, float progress)
        {
            if (points == null || points.Count == 0) return Vector3.zero;
            if (points.Count == 1) return points[0];

            float totalLength = PolylineLength(points);
            if (totalLength <= 0.0001f) return points[0];
            float targetDistance = Mathf.Clamp01(progress) * totalLength;
            float accumulated = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                float segmentLength = Vector3.Distance(points[i - 1], points[i]);
                if (segmentLength <= 0.0001f) continue;
                if (accumulated + segmentLength >= targetDistance)
                {
                    float t = (targetDistance - accumulated) / segmentLength;
                    return Vector3.Lerp(points[i - 1], points[i], t);
                }
                accumulated += segmentLength;
            }
            return points[points.Count - 1];
        }

        /// <summary>
        /// Builds the portion of a routed lane that the aircraft must actually fly.
        /// A physical lane is clipped at the attachment point of an adjacent layer
        /// connector: the source lane ends at FromPoint and the destination lane starts
        /// at ToPoint. The shared Lane.Centerline is never mutated.
        /// </summary>
        public static List<Vector3> BuildRouteTraversal(
            IReadOnlyList<Lane> route,
            int laneIndex,
            IReadOnlyDictionary<string, LaneConnectionCorridor> layerConnectors)
        {
            var result = new List<Vector3>();
            if (route == null || laneIndex < 0 || laneIndex >= route.Count) return result;

            Lane lane = route[laneIndex];
            if (lane?.Centerline == null || lane.Centerline.Count == 0) return result;

            // Connector virtual lanes already contain the directed Bezier centerline.
            if (TryGetConnector(layerConnectors, lane.LaneId, out LaneConnectionCorridor currentConnector))
            {
                IReadOnlyList<Vector3> connectorPoints = currentConnector.Centerline != null &&
                                                        currentConnector.Centerline.Count > 0
                    ? currentConnector.Centerline
                    : lane.Centerline;
                result.AddRange(connectorPoints);
                return result;
            }

            float totalLength = PolylineLength(lane.Centerline);
            if (totalLength <= 0.0001f)
            {
                result.Add(lane.Centerline[0]);
                return result;
            }

            float startDistance = 0f;
            float endDistance = totalLength;

            if (laneIndex > 0 &&
                TryGetConnector(layerConnectors, route[laneIndex - 1]?.LaneId,
                    out LaneConnectionCorridor incomingConnector) &&
                incomingConnector.ToLaneId == lane.LaneId)
            {
                startDistance = ProjectDistance(lane.Centerline, incomingConnector.ToPoint);
            }

            if (laneIndex + 1 < route.Count &&
                TryGetConnector(layerConnectors, route[laneIndex + 1]?.LaneId,
                    out LaneConnectionCorridor outgoingConnector) &&
                outgoingConnector.FromLaneId == lane.LaneId)
            {
                endDistance = ProjectDistance(lane.Centerline, outgoingConnector.FromPoint);
            }

            // A later connector must be downstream of the connector used to enter this
            // lane. Returning no geometry makes an invalid route explicit instead of
            // silently commanding a U-turn along the lane.
            if (endDistance + 0.01f < startDistance) return result;

            return SliceByDistance(lane.Centerline, startDistance, endDistance);
        }

        public static List<Vector3> SliceByDistance(
            IReadOnlyList<Vector3> points,
            float startDistance,
            float endDistance)
        {
            var result = new List<Vector3>();
            if (points == null || points.Count == 0) return result;
            if (points.Count == 1)
            {
                result.Add(points[0]);
                return result;
            }

            float totalLength = PolylineLength(points);
            startDistance = Mathf.Clamp(startDistance, 0f, totalLength);
            endDistance = Mathf.Clamp(endDistance, startDistance, totalLength);
            AddIfDistinct(result, PointAtDistance(points, startDistance));

            float accumulated = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                accumulated += Vector3.Distance(points[i - 1], points[i]);
                if (accumulated > startDistance + 0.0001f &&
                    accumulated < endDistance - 0.0001f)
                {
                    AddIfDistinct(result, points[i]);
                }
            }

            AddIfDistinct(result, PointAtDistance(points, endDistance));
            return result;
        }

        private static Vector3 PointAtDistance(IReadOnlyList<Vector3> points, float distance)
        {
            if (points == null || points.Count == 0) return Vector3.zero;
            if (points.Count == 1) return points[0];

            float target = Mathf.Clamp(distance, 0f, PolylineLength(points));
            float accumulated = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                float segmentLength = Vector3.Distance(points[i - 1], points[i]);
                if (segmentLength <= 0.0001f) continue;
                if (accumulated + segmentLength >= target)
                {
                    float t = (target - accumulated) / segmentLength;
                    return Vector3.Lerp(points[i - 1], points[i], t);
                }
                accumulated += segmentLength;
            }
            return points[points.Count - 1];
        }

        private static bool TryGetConnector(
            IReadOnlyDictionary<string, LaneConnectionCorridor> connectors,
            string laneId,
            out LaneConnectionCorridor connector)
        {
            connector = null;
            return connectors != null &&
                   !string.IsNullOrEmpty(laneId) &&
                   connectors.TryGetValue(laneId, out connector) &&
                   connector != null;
        }

        private static void AddIfDistinct(List<Vector3> points, Vector3 point)
        {
            if (points.Count == 0 || Vector3.Distance(points[points.Count - 1], point) > 0.0001f)
                points.Add(point);
        }
    }
}
