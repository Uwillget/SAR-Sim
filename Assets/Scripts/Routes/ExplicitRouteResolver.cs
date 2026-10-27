using System;
using System.Collections.Generic;
using System.Linq;
using RoadNetworkEditor.Networking;

namespace RoadNetworkEditor.Routes
{
    public sealed class ExplicitRouteResolveResult
    {
        public readonly List<Lane> Lanes = new List<Lane>();
        public string Error;
        public bool Success => string.IsNullOrEmpty(Error);
    }

    /// <summary>
    /// Validates a user-supplied lane route and expands an omitted multilayer
    /// connector between two consecutive physical lanes when one exists.
    /// </summary>
    public static class ExplicitRouteResolver
    {
        public static ExplicitRouteResolveResult Resolve(
            IReadOnlyList<string> requestedLaneIds,
            IReadOnlyDictionary<string, Lane> lanes,
            IEnumerable<LaneConnectionCorridor> connectionCorridors,
            string expectedStartLaneId,
            string expectedEndLaneId,
            IReadOnlyDictionary<string, string> laneToEdge = null)
        {
            var result = new ExplicitRouteResolveResult();
            if (requestedLaneIds == null || requestedLaneIds.Count == 0)
            {
                result.Error = "Explicit route is empty.";
                return result;
            }

            if (!string.Equals(requestedLaneIds[0], expectedStartLaneId, StringComparison.Ordinal) ||
                !string.Equals(requestedLaneIds[requestedLaneIds.Count - 1], expectedEndLaneId,
                    StringComparison.Ordinal))
            {
                result.Error = $"Route endpoints must be {expectedStartLaneId} -> {expectedEndLaneId}, " +
                               $"but are {requestedLaneIds[0]} -> " +
                               $"{requestedLaneIds[requestedLaneIds.Count - 1]}.";
                return result;
            }

            var connectors = (connectionCorridors ?? Enumerable.Empty<LaneConnectionCorridor>())
                .Where(corridor => corridor != null && !string.IsNullOrWhiteSpace(corridor.CorridorId))
                .ToList();
            var connectorsById = connectors
                .GroupBy(corridor => corridor.CorridorId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            for (int i = 0; i < requestedLaneIds.Count; i++)
            {
                string currentId = requestedLaneIds[i];
                if (!lanes.TryGetValue(currentId ?? string.Empty, out Lane current))
                {
                    result.Error = $"Route references unknown lane or connector '{currentId}'.";
                    result.Lanes.Clear();
                    return result;
                }

                result.Lanes.Add(current);
                if (i == requestedLaneIds.Count - 1) continue;

                string nextId = requestedLaneIds[i + 1];
                if (!lanes.ContainsKey(nextId ?? string.Empty))
                {
                    result.Error = $"Route references unknown lane or connector '{nextId}'.";
                    result.Lanes.Clear();
                    return result;
                }

                if (HasDirectTransition(current, nextId) ||
                    LaneRoutingUtility.AreAdjacentLanesOnSameEdge(
                        current, lanes[nextId], lanes, laneToEdge) ||
                    IsConnectorTransition(currentId, nextId, connectorsById))
                {
                    continue;
                }

                LaneConnectionCorridor implicitConnector = connectors.FirstOrDefault(corridor =>
                    string.Equals(corridor.FromLaneId, currentId, StringComparison.Ordinal) &&
                    string.Equals(corridor.ToLaneId, nextId, StringComparison.Ordinal));
                if (implicitConnector != null &&
                    lanes.TryGetValue(implicitConnector.CorridorId, out Lane connectorLane))
                {
                    result.Lanes.Add(connectorLane);
                    continue;
                }

                result.Error = $"No directed connection from '{currentId}' to '{nextId}'.";
                result.Lanes.Clear();
                return result;
            }

            return result;
        }

        private static bool HasDirectTransition(Lane current, string nextLaneId)
        {
            return current.ToLanesId != null && current.ToLanesId.Contains(nextLaneId);
        }

        private static bool IsConnectorTransition(
            string currentId,
            string nextId,
            IReadOnlyDictionary<string, LaneConnectionCorridor> connectorsById)
        {
            if (connectorsById.TryGetValue(nextId, out LaneConnectionCorridor entering))
                return string.Equals(entering.FromLaneId, currentId, StringComparison.Ordinal);
            if (connectorsById.TryGetValue(currentId, out LaneConnectionCorridor exiting))
                return string.Equals(exiting.ToLaneId, nextId, StringComparison.Ordinal);
            return false;
        }
    }
}
