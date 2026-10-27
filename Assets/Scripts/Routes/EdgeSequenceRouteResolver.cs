using System;
using System.Collections.Generic;
using System.Linq;
using RoadNetworkEditor.Networking;

namespace RoadNetworkEditor.Routes
{
    public sealed class EdgeSequenceResolveResult
    {
        public readonly List<Lane> Lanes = new List<Lane>();
        public string Error;
        public bool Success => string.IsNullOrEmpty(Error);
    }

    /// <summary>
    /// Resolves a user-selected axis-edge sequence into one directed lane sequence.
    /// The resolver may change lanes on the same edge and traverse explicit layer
    /// connector lanes, but it may never skip or reorder a selected axis edge.
    /// </summary>
    public static class EdgeSequenceRouteResolver
    {
        private readonly struct State : IEquatable<State>
        {
            public readonly string LaneId;
            public readonly int EdgeIndex;

            public State(string laneId, int edgeIndex)
            {
                LaneId = laneId;
                EdgeIndex = edgeIndex;
            }

            public bool Equals(State other) => EdgeIndex == other.EdgeIndex && LaneId == other.LaneId;
            public override bool Equals(object obj) => obj is State other && Equals(other);
            public override int GetHashCode() => ((LaneId != null ? LaneId.GetHashCode() : 0) * 397) ^ EdgeIndex;
        }

        public static EdgeSequenceResolveResult Resolve(
            IReadOnlyList<string> edgeIds,
            string startLaneId,
            ISet<string> requiredEndLaneIds,
            IReadOnlyDictionary<string, Lane> lanes,
            IEnumerable<Edge> edges,
            IEnumerable<LaneConnectionCorridor> connectionCorridors,
            IReadOnlyDictionary<string, string> laneToEdge)
        {
            var result = new EdgeSequenceResolveResult();
            if (edgeIds == null || edgeIds.Count == 0)
                return Fail(result, "The centerline-edge sequence is empty.");
            if (lanes == null || !lanes.ContainsKey(startLaneId ?? string.Empty))
                return Fail(result, $"Start lane {startLaneId ?? "(empty)"} does not exist.");
            if (laneToEdge == null || !laneToEdge.TryGetValue(startLaneId, out string startEdge) ||
                !string.Equals(startEdge, edgeIds[0], StringComparison.Ordinal))
                return Fail(result, $"The start lane does not belong to the locked first edge {edgeIds[0]}.");

            HashSet<string> knownEdges = new HashSet<string>(
                (edges ?? Enumerable.Empty<Edge>()).Where(edge => edge != null).Select(edge => edge.EdgeId),
                StringComparer.Ordinal);
            string unknown = edgeIds.FirstOrDefault(id => !knownEdges.Contains(id));
            if (!string.IsNullOrEmpty(unknown))
                return Fail(result, $"Centerline edge {unknown} does not exist.");

            List<LaneConnectionCorridor> connectors =
                (connectionCorridors ?? Enumerable.Empty<LaneConnectionCorridor>())
                .Where(corridor => corridor != null).ToList();
            Dictionary<string, LaneConnectionCorridor> connectorById = connectors
                .Where(corridor => !string.IsNullOrEmpty(corridor.CorridorId))
                .GroupBy(corridor => corridor.CorridorId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            Dictionary<string, List<string>> adjacency = BuildAdjacency(lanes, edges, connectors, laneToEdge);

            var start = new State(startLaneId, 0);
            var queue = new Queue<State>();
            var visited = new HashSet<State> { start };
            var previous = new Dictionary<State, State>();
            queue.Enqueue(start);
            State? finish = null;

            while (queue.Count > 0)
            {
                State current = queue.Dequeue();
                if (current.EdgeIndex == edgeIds.Count - 1 &&
                    (requiredEndLaneIds == null || requiredEndLaneIds.Contains(current.LaneId)))
                {
                    finish = current;
                    break;
                }

                if (!adjacency.TryGetValue(current.LaneId, out List<string> nextIds)) continue;
                foreach (string nextId in nextIds)
                {
                    int nextEdgeIndex = GetNextEdgeIndex(current, nextId, edgeIds, laneToEdge,
                        connectorById);
                    if (nextEdgeIndex < 0) continue;
                    var next = new State(nextId, nextEdgeIndex);
                    if (!visited.Add(next)) continue;
                    previous[next] = current;
                    queue.Enqueue(next);
                }
            }

            if (!finish.HasValue)
            {
                string suffix = requiredEndLaneIds == null
                    ? $"selected edge {edgeIds[edgeIds.Count - 1]}"
                    : "the destination outbound corridor";
                return Fail(result, $"The locked first edge cannot connect to {suffix} in the selected order.");
            }

            var routeIds = new List<string>();
            State cursor = finish.Value;
            routeIds.Add(cursor.LaneId);
            while (!cursor.Equals(start))
            {
                cursor = previous[cursor];
                routeIds.Add(cursor.LaneId);
            }
            routeIds.Reverse();
            result.Lanes.AddRange(routeIds.Select(id => lanes[id]));
            return result;
        }

        private static int GetNextEdgeIndex(
            State current,
            string nextLaneId,
            IReadOnlyList<string> edgeIds,
            IReadOnlyDictionary<string, string> laneToEdge,
            IReadOnlyDictionary<string, LaneConnectionCorridor> connectorById)
        {
            if (laneToEdge.TryGetValue(nextLaneId, out string nextEdge))
            {
                if (string.Equals(nextEdge, edgeIds[current.EdgeIndex], StringComparison.Ordinal))
                    return current.EdgeIndex;
                if (current.EdgeIndex + 1 < edgeIds.Count &&
                    string.Equals(nextEdge, edgeIds[current.EdgeIndex + 1], StringComparison.Ordinal))
                    return current.EdgeIndex + 1;
                return -1;
            }

            if (!connectorById.TryGetValue(nextLaneId, out LaneConnectionCorridor connector)) return -1;
            if (!laneToEdge.TryGetValue(connector.FromLaneId ?? string.Empty, out string fromEdge) ||
                !string.Equals(fromEdge, edgeIds[current.EdgeIndex], StringComparison.Ordinal))
                return -1;
            if (!laneToEdge.TryGetValue(connector.ToLaneId ?? string.Empty, out string toEdge)) return -1;
            bool stays = string.Equals(toEdge, edgeIds[current.EdgeIndex], StringComparison.Ordinal);
            bool advances = current.EdgeIndex + 1 < edgeIds.Count &&
                            string.Equals(toEdge, edgeIds[current.EdgeIndex + 1], StringComparison.Ordinal);
            return stays || advances ? current.EdgeIndex : -1;
        }

        private static Dictionary<string, List<string>> BuildAdjacency(
            IReadOnlyDictionary<string, Lane> lanes,
            IEnumerable<Edge> edges,
            IEnumerable<LaneConnectionCorridor> connectors,
            IReadOnlyDictionary<string, string> laneToEdge)
        {
            var adjacency = lanes.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
            foreach (Lane lane in lanes.Values)
            {
                foreach (string nextId in lane?.ToLanesId ?? Enumerable.Empty<string>())
                    AddArc(adjacency, lane.LaneId, nextId);
            }

            foreach (Edge edge in edges ?? Enumerable.Empty<Edge>())
            {
                List<Lane> edgeLanes = (edge?.Lanes ?? new List<Lane>())
                    .Where(lane => lane != null && lanes.ContainsKey(lane.LaneId)).ToList();
                for (int i = 0; i < edgeLanes.Count; i++)
                for (int j = i + 1; j < edgeLanes.Count; j++)
                {
                    if (!LaneRoutingUtility.AreAdjacentLanesOnSameEdge(
                            edgeLanes[i], edgeLanes[j], lanes, laneToEdge)) continue;
                    AddArc(adjacency, edgeLanes[i].LaneId, edgeLanes[j].LaneId);
                    AddArc(adjacency, edgeLanes[j].LaneId, edgeLanes[i].LaneId);
                }
            }

            foreach (LaneConnectionCorridor connector in connectors)
            {
                AddArc(adjacency, connector.FromLaneId, connector.CorridorId);
                AddArc(adjacency, connector.CorridorId, connector.ToLaneId);
            }
            return adjacency;
        }

        private static void AddArc(Dictionary<string, List<string>> adjacency, string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) ||
                !adjacency.TryGetValue(from, out List<string> arcs) || !adjacency.ContainsKey(to) ||
                arcs.Contains(to)) return;
            arcs.Add(to);
        }

        private static EdgeSequenceResolveResult Fail(EdgeSequenceResolveResult result, string error)
        {
            result.Error = error;
            result.Lanes.Clear();
            return result;
        }
    }
}
