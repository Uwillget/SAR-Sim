using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RoadNetworkEditor.Networking
{
    /// <summary>
    /// Builds the routable lane graph from junction movements, adjacent same-direction
    /// lane changes, and explicit multilayer connectors.
    /// </summary>
    public static class LaneRoutingUtility
    {
        private sealed class Arc
        {
            public string ToLaneId;
            public float Cost;
        }

        public static List<Lane> FindShortestPath(
            IReadOnlyDictionary<string, Lane> lanes,
            IEnumerable<Edge> edges,
            IEnumerable<LaneConnectionCorridor> connectionCorridors,
            string startLaneId,
            string targetLaneId,
            float laneChangePenalty = 30f,
            ISet<string> forbiddenLaneIds = null)
        {
            if (lanes == null ||
                !lanes.ContainsKey(startLaneId ?? string.Empty) ||
                !lanes.ContainsKey(targetLaneId ?? string.Empty))
            {
                return null;
            }

            Dictionary<string, List<Arc>> adjacency = BuildAdjacency(
                lanes,
                edges,
                connectionCorridors,
                Mathf.Max(0.001f, laneChangePenalty));

            var distances = lanes.Keys.ToDictionary(id => id, _ => float.MaxValue);
            var previous = new Dictionary<string, string>(StringComparer.Ordinal);
            var frontier = new List<string> { startLaneId };
            distances[startLaneId] = 0f;

            while (frontier.Count > 0)
            {
                string current = frontier
                    .OrderBy(id => distances.GetValueOrDefault(id, float.MaxValue))
                    .ThenBy(id => id, StringComparer.Ordinal)
                    .First();
                frontier.Remove(current);
                if (current == targetLaneId) break;

                foreach (Arc arc in adjacency[current])
                {
                    if (forbiddenLaneIds != null &&
                        forbiddenLaneIds.Contains(arc.ToLaneId) &&
                        arc.ToLaneId != targetLaneId)
                    {
                        continue;
                    }
                    float candidate = distances[current] + arc.Cost;
                    if (candidate >= distances.GetValueOrDefault(arc.ToLaneId, float.MaxValue))
                        continue;

                    distances[arc.ToLaneId] = candidate;
                    previous[arc.ToLaneId] = current;
                    if (!frontier.Contains(arc.ToLaneId)) frontier.Add(arc.ToLaneId);
                }
            }

            if (startLaneId != targetLaneId && !previous.ContainsKey(targetLaneId))
                return null;

            var laneIds = new List<string> { targetLaneId };
            string cursor = targetLaneId;
            while (cursor != startLaneId)
            {
                if (!previous.TryGetValue(cursor, out cursor)) return null;
                laneIds.Add(cursor);
            }
            laneIds.Reverse();
            return laneIds.Select(id => lanes[id]).ToList();
        }

        public static bool AreAdjacentLanesOnSameEdge(
            Lane first,
            Lane second,
            IReadOnlyDictionary<string, Lane> lanes,
            IReadOnlyDictionary<string, string> laneToEdge)
        {
            if (first == null || second == null || lanes == null || laneToEdge == null ||
                !laneToEdge.TryGetValue(first.LaneId, out string firstEdge) ||
                !laneToEdge.TryGetValue(second.LaneId, out string secondEdge) ||
                !string.Equals(firstEdge, secondEdge, StringComparison.Ordinal) ||
                !SameDirection(first, second))
            {
                return false;
            }

            List<Lane> directedLanes = lanes.Values
                .Where(lane => lane != null &&
                               laneToEdge.TryGetValue(lane.LaneId, out string edgeId) &&
                               string.Equals(edgeId, firstEdge, StringComparison.Ordinal) &&
                               SameDirection(first, lane))
                .OrderBy(lane => LateralOffset(lane, first))
                .ThenBy(lane => lane.LaneId, StringComparer.Ordinal)
                .ToList();
            int firstIndex = directedLanes.FindIndex(lane => lane.LaneId == first.LaneId);
            int secondIndex = directedLanes.FindIndex(lane => lane.LaneId == second.LaneId);
            return firstIndex >= 0 && secondIndex >= 0 && Mathf.Abs(firstIndex - secondIndex) == 1;
        }

        private static Dictionary<string, List<Arc>> BuildAdjacency(
            IReadOnlyDictionary<string, Lane> lanes,
            IEnumerable<Edge> edges,
            IEnumerable<LaneConnectionCorridor> connectionCorridors,
            float laneChangePenalty)
        {
            var adjacency = lanes.Keys.ToDictionary(
                id => id,
                _ => new List<Arc>(),
                StringComparer.Ordinal);

            foreach (Lane lane in lanes.Values)
            {
                if (lane?.ToLanesId == null) continue;
                foreach (string nextId in lane.ToLanesId)
                {
                    if (lanes.TryGetValue(nextId ?? string.Empty, out Lane next))
                        AddArc(adjacency[lane.LaneId], nextId, Mathf.Max(next.Length, 0.001f));
                }
            }

            foreach (Edge edge in edges ?? Enumerable.Empty<Edge>())
            {
                if (edge?.Lanes == null) continue;
                foreach (List<Lane> directionGroup in BuildDirectionGroups(edge.Lanes))
                {
                    Lane reference = directionGroup[0];
                    List<Lane> ordered = directionGroup
                        .OrderBy(lane => LateralOffset(lane, reference))
                        .ThenBy(lane => lane.LaneId, StringComparer.Ordinal)
                        .ToList();
                    for (int i = 1; i < ordered.Count; i++)
                    {
                        AddArc(adjacency[ordered[i - 1].LaneId], ordered[i].LaneId, laneChangePenalty);
                        AddArc(adjacency[ordered[i].LaneId], ordered[i - 1].LaneId, laneChangePenalty);
                    }
                }
            }

            foreach (LaneConnectionCorridor corridor in
                     connectionCorridors ?? Enumerable.Empty<LaneConnectionCorridor>())
            {
                if (corridor == null ||
                    !lanes.ContainsKey(corridor.FromLaneId ?? string.Empty) ||
                    !lanes.ContainsKey(corridor.ToLaneId ?? string.Empty) ||
                    !lanes.TryGetValue(corridor.CorridorId ?? string.Empty, out Lane connectorLane))
                {
                    continue;
                }

                AddArc(adjacency[corridor.FromLaneId], corridor.CorridorId,
                    Mathf.Max(connectorLane.Length, 0.001f));
                AddArc(adjacency[corridor.CorridorId], corridor.ToLaneId,
                    Mathf.Max(lanes[corridor.ToLaneId].Length, 0.001f));
            }

            return adjacency;
        }

        private static List<List<Lane>> BuildDirectionGroups(IEnumerable<Lane> source)
        {
            var groups = new List<List<Lane>>();
            foreach (Lane lane in source.Where(IsUsableLane))
            {
                List<Lane> group = groups.FirstOrDefault(existing => SameDirection(existing[0], lane));
                if (group == null)
                {
                    group = new List<Lane>();
                    groups.Add(group);
                }
                group.Add(lane);
            }
            return groups;
        }

        private static void AddArc(List<Arc> arcs, string targetId, float cost)
        {
            Arc existing = arcs.FirstOrDefault(arc => arc.ToLaneId == targetId);
            if (existing == null)
                arcs.Add(new Arc { ToLaneId = targetId, Cost = cost });
            else
                existing.Cost = Mathf.Min(existing.Cost, cost);
        }

        private static bool IsUsableLane(Lane lane)
        {
            return lane != null && lane.Centerline != null && lane.Centerline.Count >= 2;
        }

        private static bool SameDirection(Lane first, Lane second)
        {
            if (!IsUsableLane(first) || !IsUsableLane(second)) return false;
            Vector3 firstDirection =
                (first.Centerline[first.Centerline.Count - 1] - first.Centerline[0]).normalized;
            Vector3 secondDirection =
                (second.Centerline[second.Centerline.Count - 1] - second.Centerline[0]).normalized;
            return Vector3.Dot(firstDirection, secondDirection) > 0.5f;
        }

        private static float LateralOffset(Lane lane, Lane directionReference)
        {
            Vector3 direction =
                (directionReference.Centerline[directionReference.Centerline.Count - 1] -
                 directionReference.Centerline[0]).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, direction).normalized;
            return Vector3.Dot(lane.Centerline[0], right);
        }
    }
}
