using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RoadNetworkEditor.Routes
{
    /// <summary>
    /// Shared route-demand authoring service used by the Unity Inspector/Scene
    /// workflow and the packaged runtime UI.
    /// </summary>
    public static class RouteDemandAuthoring
    {
        public static bool TryBuildShortestLaneRoute(SimulationManager manager,
            string originHelipadId, string destinationHelipadId,
            out List<string> laneIds, out string error)
        {
            laneIds = new List<string>();
            if (!ValidateEndpoints(manager, originHelipadId, destinationHelipadId, out error)) return false;
            if (manager.pathPlanner == null)
            {
                error = "The route planner is not initialized. Refresh the active topology first.";
                return false;
            }

            List<string> starts = GetEndpointLaneIds(manager, originHelipadId, "Entry");
            List<string> ends = GetEndpointLaneIds(manager, destinationHelipadId, "Exit");
            List<Lane> best = null;
            float bestCost = float.PositiveInfinity;
            foreach (string start in starts)
            foreach (string end in ends)
            {
                List<Lane> candidate = manager.pathPlanner.FindLaneSequenceWithLayerConnections(start, end);
                if (candidate == null || candidate.Count == 0) continue;
                float cost = candidate.Sum(LaneCost);
                if (cost >= bestCost) continue;
                best = candidate;
                bestCost = cost;
            }

            if (best == null)
            {
                error = "No connected lane route exists between the selected helipads.";
                return false;
            }
            laneIds.AddRange(best.Select(lane => lane.LaneId));
            error = null;
            return true;
        }

        public static bool TryGetManualRouteStart(SimulationManager manager,
            string originHelipadId, string destinationHelipadId,
            out string startLaneId, out string firstEdgeId, out string error)
        {
            startLaneId = null;
            firstEdgeId = null;
            if (!TryBuildShortestLaneRoute(manager, originHelipadId, destinationHelipadId,
                    out List<string> shortest, out error)) return false;
            startLaneId = shortest.FirstOrDefault(id => manager.LaneToEdgeMap.ContainsKey(id));
            if (string.IsNullOrEmpty(startLaneId))
            {
                error = "The departure corridor has no usable axis-network entry edge.";
                return false;
            }
            firstEdgeId = manager.LaneToEdgeMap[startLaneId];
            return true;
        }

        public static bool TryResolveManualEdges(SimulationManager manager,
            string originHelipadId, string destinationHelipadId,
            IEnumerable<string> edgeIds, bool requireDestination,
            out List<string> laneIds, out string error)
        {
            laneIds = new List<string>();
            if (!TryGetManualRouteStart(manager, originHelipadId, destinationHelipadId,
                    out string startLaneId, out string firstEdgeId, out error)) return false;
            List<string> selectedEdges = (edgeIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList();
            if (selectedEdges.Count == 0 ||
                !string.Equals(selectedEdges[0], firstEdgeId, StringComparison.Ordinal))
            {
                error = $"The manual route must begin with locked departure edge '{firstEdgeId}'.";
                return false;
            }

            HashSet<string> destinationLanes = requireDestination
                ? new HashSet<string>(GetEndpointLaneIds(manager, destinationHelipadId, "Exit"),
                    StringComparer.Ordinal)
                : null;
            EdgeSequenceResolveResult result = EdgeSequenceRouteResolver.Resolve(
                selectedEdges, startLaneId, destinationLanes, manager.Lanes, manager.Edges.Values,
                manager.LaneConnectionCorridors.Values, manager.LaneToEdgeMap);
            if (!result.Success)
            {
                error = result.Error;
                return false;
            }
            laneIds.AddRange(result.Lanes.Select(lane => lane.LaneId));
            error = null;
            return true;
        }

        /// <summary>
        /// Applies one axis-centerline click using the same ordered-selection rules in
        /// the Unity Scene view and the packaged application. Clicking an already
        /// selected edge rolls the route back to that edge; the locked departure edge
        /// cannot be removed. A newly appended edge is accepted only when a lane-level
        /// connection exists from the current prefix.
        /// </summary>
        public static bool TryApplyManualEdgeClick(SimulationManager manager,
            string originHelipadId, string destinationHelipadId,
            List<string> selectedEdgeIds, string clickedEdgeId,
            out List<string> laneIds, out bool connectsDestination, out string error)
        {
            laneIds = new List<string>();
            connectsDestination = false;
            if (manager == null || selectedEdgeIds == null)
            {
                error = "The route-authoring state is unavailable.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(clickedEdgeId) || !manager.Edges.ContainsKey(clickedEdgeId))
            {
                error = "The selected centerline is not part of the active routable network.";
                return false;
            }

            int existingIndex = selectedEdgeIds.IndexOf(clickedEdgeId);
            if (existingIndex >= 0)
            {
                if (existingIndex == 0 && selectedEdgeIds.Count == 1)
                {
                    error = $"'{clickedEdgeId}' is the locked departure edge and cannot be removed.";
                    return false;
                }
                selectedEdgeIds.RemoveRange(existingIndex + 1,
                    selectedEdgeIds.Count - existingIndex - 1);
            }
            else
            {
                selectedEdgeIds.Add(clickedEdgeId);
                if (!TryResolveManualEdges(manager, originHelipadId, destinationHelipadId,
                        selectedEdgeIds, false, out _, out string appendError))
                {
                    selectedEdgeIds.RemoveAt(selectedEdgeIds.Count - 1);
                    error = $"The centerline does not continue the current lane-level route. {appendError}";
                    return false;
                }
            }

            connectsDestination = TryResolveManualEdges(manager, originHelipadId,
                destinationHelipadId, selectedEdgeIds, true, out List<string> completeRoute, out _);
            if (connectsDestination)
            {
                laneIds.AddRange(completeRoute);
                error = null;
                return true;
            }

            if (!TryResolveManualEdges(manager, originHelipadId, destinationHelipadId,
                    selectedEdgeIds, false, out List<string> prefixRoute, out error))
                return false;
            laneIds.AddRange(prefixRoute);
            error = null;
            return true;
        }

        public static MissionTask CreateMission(SimulationManager manager,
            string originHelipadId, string destinationHelipadId,
            IEnumerable<string> explicitLaneIds, float departureTime, int priority,
            string taskId, string routeId)
        {
            List<string> lanes = (explicitLaneIds ?? Enumerable.Empty<string>()).ToList();
            return new MissionTask
            {
                TaskId = taskId,
                RouteId = lanes.Count > 0 ? routeId : null,
                OriginVertiportId = manager.HelipadToVertiportMap[originHelipadId],
                DestinationVertiportId = manager.HelipadToVertiportMap[destinationHelipadId],
                OriginHelipadId = originHelipadId,
                DestinationHelipadId = destinationHelipadId,
                RouteLaneIds = lanes,
                DepartureTime = Mathf.Max(0f, departureTime),
                Priority = priority
            };
        }

        public static List<string> CollapseLaneEdges(SimulationManager manager,
            IEnumerable<string> laneIds)
        {
            var result = new List<string>();
            foreach (string laneId in laneIds ?? Enumerable.Empty<string>())
            {
                if (!manager.LaneToEdgeMap.TryGetValue(laneId, out string edgeId)) continue;
                if (result.Count == 0 || result[result.Count - 1] != edgeId) result.Add(edgeId);
            }
            return result;
        }

        public static bool ValidateEndpoints(SimulationManager manager,
            string originHelipadId, string destinationHelipadId, out string error)
        {
            if (string.IsNullOrEmpty(originHelipadId) || string.IsNullOrEmpty(destinationHelipadId))
            {
                error = "Select both origin and destination helipads.";
                return false;
            }
            if (originHelipadId == destinationHelipadId)
            {
                error = "Origin and destination helipads must be different.";
                return false;
            }
            if (manager == null || !manager.HelipadToVertiportMap.ContainsKey(originHelipadId) ||
                !manager.HelipadToVertiportMap.ContainsKey(destinationHelipadId))
            {
                error = "The selected endpoint is not part of a valid vertiport.";
                return false;
            }
            error = null;
            return true;
        }

        public static List<string> GetEndpointLaneIds(SimulationManager manager,
            string helipadId, string corridorType)
        {
            if (manager == null || string.IsNullOrEmpty(helipadId) ||
                !manager.Helipads.TryGetValue(helipadId, out Helipad pad) ||
                !manager.HelipadToVertiportMap.TryGetValue(helipadId, out string vertiportId) ||
                !manager.Vertiports.TryGetValue(vertiportId, out Vertiport vertiport))
                return new List<string>();
            HashSet<string> reachable = pad.ReachableCorridorIds != null
                ? new HashSet<string>(pad.ReachableCorridorIds)
                : new HashSet<string>();
            return (vertiport.Corridors ?? new List<NetConnectedCorridor>())
                .Where(corridor => corridor != null && corridor.CorridorType == corridorType &&
                                   (reachable.Count == 0 || reachable.Contains(corridor.CorridorId)) &&
                                   manager.Lanes.ContainsKey(corridor.LinkLaneId))
                .Select(corridor => corridor.LinkLaneId)
                .Distinct().OrderBy(id => id).ToList();
        }

        private static float LaneCost(Lane lane)
        {
            if (lane == null) return float.PositiveInfinity;
            if (lane.Length > 0.01f) return lane.Length;
            if (lane.Centerline == null || lane.Centerline.Count < 2) return 1f;
            float length = 0f;
            for (int index = 1; index < lane.Centerline.Count; index++)
                length += Vector3.Distance(lane.Centerline[index - 1], lane.Centerline[index]);
            return Mathf.Max(1f, length);
        }
    }
}
