using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RoadNetworkEditor.Networking
{
    public sealed class NetworkValidationResult
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>
    /// Compatibility and validation boundary for schema-v2 multilayer networks.
    /// Legacy single-layer JSON is upgraded in memory without rewriting the file.
    /// </summary>
    public static class MultilayerNetworkUtility
    {
        public const int CurrentSchemaVersion = 2;
        public const string DefaultLayerId = "L0";

        public static NetworkValidationResult NormalizeAndValidate(Network network)
        {
            var result = new NetworkValidationResult();
            if (network == null)
            {
                result.Errors.Add("Network is null.");
                return result;
            }

            network.SchemaVersion = CurrentSchemaVersion;
            network.Nodes = network.Nodes ?? new List<Node>();
            network.Edges = network.Edges ?? new List<Edge>();
            network.AltitudeLayers = network.AltitudeLayers ?? new List<AltitudeLayer>();
            network.LaneConnectionCorridors =
                network.LaneConnectionCorridors ?? new List<LaneConnectionCorridor>();

            List<Lane> lanes = network.Edges
                .Where(edge => edge != null && edge.Lanes != null)
                .SelectMany(edge => edge.Lanes)
                .Where(lane => lane != null)
                .ToList();

            EnsureLayers(network, lanes);
            NormalizeAxisLayerMembership(network);
            foreach (var duplicateLayer in network.AltitudeLayers
                         .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId))
                         .GroupBy(layer => layer.LayerId)
                         .Where(group => group.Count() > 1))
            {
                result.Errors.Add($"Duplicate altitude layer ID: {duplicateLayer.Key}.");
            }
            var layersById = network.AltitudeLayers
                .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId))
                .GroupBy(layer => layer.LayerId)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (Lane lane in lanes)
            {
                lane.FromLanesId = lane.FromLanesId ?? new List<string>();
                lane.ToLanesId = lane.ToLanesId ?? new List<string>();
                lane.Centerline = lane.Centerline ?? new List<Vector3>();
                lane.LinkCorridors = lane.LinkCorridors ?? new List<(string, Vector3)>();

                if (string.IsNullOrWhiteSpace(lane.LayerId) || !layersById.ContainsKey(lane.LayerId))
                {
                    lane.LayerId = FindNearestLayerId(network.AltitudeLayers, MeanAltitude(lane.Centerline));
                }

                AltitudeLayer laneLayer = layersById[lane.LayerId];
                ClampPolylineToAltitude(lane.Centerline, laneLayer.NominalAltitude);

                lane.Length = CalculatePolylineLength(lane.Centerline);
            }

            var lanesById = lanes
                .Where(lane => !string.IsNullOrWhiteSpace(lane.LaneId))
                .GroupBy(lane => lane.LaneId)
                .ToDictionary(group => group.Key, group => group.First());

            var knownIds = new HashSet<string>(lanesById.Keys);
            for (int i = 0; i < network.LaneConnectionCorridors.Count; i++)
            {
                LaneConnectionCorridor corridor = network.LaneConnectionCorridors[i];
                if (corridor == null)
                {
                    result.Errors.Add($"Lane connection at index {i} is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(corridor.CorridorId))
                    corridor.CorridorId = NextConnectorId(knownIds);
                if (!knownIds.Add(corridor.CorridorId))
                    result.Errors.Add($"Duplicate lane or connector ID: {corridor.CorridorId}.");

                if (!lanesById.TryGetValue(corridor.FromLaneId ?? string.Empty, out Lane fromLane))
                    result.Errors.Add($"{corridor.CorridorId}: unknown FromLaneId '{corridor.FromLaneId}'.");
                if (!lanesById.TryGetValue(corridor.ToLaneId ?? string.Empty, out Lane toLane))
                    result.Errors.Add($"{corridor.CorridorId}: unknown ToLaneId '{corridor.ToLaneId}'.");
                if (corridor.FromLaneId == corridor.ToLaneId)
                    result.Errors.Add($"{corridor.CorridorId}: source and target lanes must differ.");

                if (string.IsNullOrWhiteSpace(corridor.FromLayerId) && fromLane != null)
                    corridor.FromLayerId = fromLane.LayerId;
                if (string.IsNullOrWhiteSpace(corridor.ToLayerId) && toLane != null)
                    corridor.ToLayerId = toLane.LayerId;

                if (!layersById.ContainsKey(corridor.FromLayerId ?? string.Empty))
                    result.Errors.Add($"{corridor.CorridorId}: unknown source layer '{corridor.FromLayerId}'.");
                if (!layersById.ContainsKey(corridor.ToLayerId ?? string.Empty))
                    result.Errors.Add($"{corridor.CorridorId}: unknown target layer '{corridor.ToLayerId}'.");
                if (!string.IsNullOrEmpty(corridor.FromLayerId) &&
                    corridor.FromLayerId == corridor.ToLayerId)
                    result.Errors.Add($"{corridor.CorridorId}: layer connector endpoints must use different layers.");

                NormalizeCenterline(corridor, fromLane, toLane);
                corridor.Length = CalculatePolylineLength(corridor.Centerline);
                if (corridor.Length <= 0.001f)
                    result.Errors.Add($"{corridor.CorridorId}: centerline length must be positive.");
                if (corridor.MaxSpeed <= 0f)
                {
                    corridor.MaxSpeed = 20f;
                    result.Warnings.Add($"{corridor.CorridorId}: MaxSpeed reset to 20.");
                }
            }

            return result;
        }

        /// <summary>
        /// Upgrades legacy axis data and locks every axis node/edge to one altitude layer.
        /// Unity uses Y as the vertical axis; X/Z plan coordinates remain unchanged.
        /// </summary>
        public static void NormalizeAxisLayerMembership(Network network)
        {
            if (network == null) return;
            network.Nodes = network.Nodes ?? new List<Node>();
            network.Edges = network.Edges ?? new List<Edge>();
            network.AltitudeLayers = network.AltitudeLayers ?? new List<AltitudeLayer>();
            EnsureLayers(network, network.Edges
                .Where(edge => edge?.Lanes != null)
                .SelectMany(edge => edge.Lanes)
                .Where(lane => lane != null)
                .ToList());

            var layers = network.AltitudeLayers
                .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId))
                .GroupBy(layer => layer.LayerId)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (Node node in network.Nodes.Where(node => node != null))
            {
                string nearestLayerId = FindNearestLayerId(network.AltitudeLayers, node.Position.y);
                if (string.IsNullOrWhiteSpace(node.LayerId) || !layers.ContainsKey(node.LayerId) ||
                    IsLegacyDefaultLayerMismatch(node.LayerId, nearestLayerId, node.Position.y, layers))
                    node.LayerId = nearestLayerId;
                node.Position = WithAltitude(node.Position, layers[node.LayerId].NominalAltitude);
            }

            foreach (Edge edge in network.Edges.Where(edge => edge != null))
            {
                edge.Centerline = edge.Centerline ?? new List<Vector3>();
                string endpointLayer = null;
                if (edge.LinkNodesId != null && edge.LinkNodesId.Count > 0)
                    endpointLayer = network.Nodes.FirstOrDefault(node => node.NodeId == edge.LinkNodesId[0])?.LayerId;
                float meanAltitude = MeanAltitude(edge.Centerline);
                string nearestLayerId = FindNearestLayerId(network.AltitudeLayers, meanAltitude);
                if (string.IsNullOrWhiteSpace(edge.LayerId) || !layers.ContainsKey(edge.LayerId) ||
                    IsLegacyDefaultLayerMismatch(edge.LayerId, nearestLayerId, meanAltitude, layers))
                    edge.LayerId = !string.IsNullOrWhiteSpace(endpointLayer) && layers.ContainsKey(endpointLayer)
                        ? endpointLayer
                        : nearestLayerId;
                ClampPolylineToAltitude(edge.Centerline, layers[edge.LayerId].NominalAltitude);
            }
        }

        public static AltitudeLayer AddAxisLayer(Network network, float altitude, string displayName = null)
        {
            if (network == null) throw new ArgumentNullException(nameof(network));
            NormalizeAxisLayerMembership(network);
            var used = new HashSet<string>(network.AltitudeLayers.Select(layer => layer.LayerId));
            int index = 0;
            string id;
            do id = $"L{index++}"; while (used.Contains(id));
            var layer = new AltitudeLayer
            {
                LayerId = id,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? $"Altitude Layer {id}" : displayName,
                NominalAltitude = altitude,
                VerticalTolerance = 5f,
                Enabled = true
            };
            network.AltitudeLayers.Add(layer);
            return layer;
        }

        public static bool SetAxisLayerAltitude(Network network, string layerId, float altitude)
        {
            if (network == null || string.IsNullOrWhiteSpace(layerId)) return false;
            NormalizeAxisLayerMembership(network);
            AltitudeLayer layer = network.AltitudeLayers.FirstOrDefault(item => item.LayerId == layerId);
            if (layer == null) return false;
            layer.NominalAltitude = altitude;
            ApplyLayerAltitudes(network);
            return true;
        }

        public static bool RenameAxisLayer(Network network, string layerId, string displayName)
        {
            AltitudeLayer layer = FindLayer(network, layerId);
            if (layer == null || string.IsNullOrWhiteSpace(displayName)) return false;
            layer.DisplayName = displayName.Trim();
            return true;
        }

        /// <summary>
        /// Moves a layer to a new position in the visible, high-to-low stack. Existing altitude
        /// slots are reassigned to the reordered layers so every node/waypoint keeps its X/Z plan
        /// coordinate while the complete layer moves vertically.
        /// </summary>
        public static bool ReorderAxisLayer(Network network, string layerId, int targetDescendingIndex)
        {
            if (network == null || string.IsNullOrWhiteSpace(layerId)) return false;
            NormalizeAxisLayerMembership(network);
            List<AltitudeLayer> ordered = network.AltitudeLayers
                .Where(layer => layer != null)
                .OrderByDescending(layer => layer.NominalAltitude)
                .ThenBy(layer => layer.LayerId)
                .ToList();
            int sourceIndex = ordered.FindIndex(layer => layer.LayerId == layerId);
            if (sourceIndex < 0) return false;
            targetDescendingIndex = Mathf.Clamp(targetDescendingIndex, 0, ordered.Count - 1);
            if (sourceIndex == targetDescendingIndex) return true;

            List<float> altitudeSlots = ordered.Select(layer => layer.NominalAltitude).ToList();
            AltitudeLayer moved = ordered[sourceIndex];
            ordered.RemoveAt(sourceIndex);
            ordered.Insert(targetDescendingIndex, moved);
            for (int i = 0; i < ordered.Count; i++) ordered[i].NominalAltitude = altitudeSlots[i];
            ApplyLayerAltitudes(network);
            return true;
        }

        /// <summary>Duplicates a complete axis layer, including its nodes, edges and waypoints.</summary>
        public static AltitudeLayer DuplicateAxisLayer(Network network, string sourceLayerId, float targetAltitude)
        {
            if (network == null) return null;
            NormalizeAxisLayerMembership(network);
            AltitudeLayer source = FindLayer(network, sourceLayerId);
            if (source == null) return null;

            string sourceName = string.IsNullOrWhiteSpace(source.DisplayName) ? source.LayerId : source.DisplayName;
            AltitudeLayer copy = AddAxisLayer(network, targetAltitude, sourceName + " Copy");
            copy.VerticalTolerance = source.VerticalTolerance;
            copy.Enabled = source.Enabled;
            // A copied layer is intentionally editable even when its source was frozen.
            copy.Frozen = false;

            var nodeMap = new Dictionary<string, string>();
            var usedNodeIds = new HashSet<string>(network.Nodes.Where(node => node != null)
                .Select(node => node.NodeId));
            foreach (Node node in network.Nodes.Where(node => node != null && node.LayerId == sourceLayerId).ToList())
            {
                string nodeId = NextCopyId(node.NodeId, copy.LayerId, usedNodeIds);
                usedNodeIds.Add(nodeId);
                nodeMap[node.NodeId] = nodeId;
                network.Nodes.Add(new Node
                {
                    NodeId = nodeId,
                    LayerId = copy.LayerId,
                    Position = WithAltitude(node.Position, copy.NominalAltitude),
                    ConnectedEdges = new List<string>(),
                    NeighborNodes = new List<string>()
                });
            }

            var usedEdgeIds = new HashSet<string>(network.Edges.Where(edge => edge != null)
                .Select(edge => edge.EdgeId));
            foreach (Edge edge in network.Edges.Where(edge => edge != null && edge.LayerId == sourceLayerId).ToList())
            {
                if (edge.LinkNodesId == null || edge.LinkNodesId.Count < 2 ||
                    !nodeMap.TryGetValue(edge.LinkNodesId[0], out string fromId) ||
                    !nodeMap.TryGetValue(edge.LinkNodesId[edge.LinkNodesId.Count - 1], out string toId))
                    continue;
                string edgeId = NextCopyId(edge.EdgeId, copy.LayerId, usedEdgeIds);
                usedEdgeIds.Add(edgeId);
                var centerline = (edge.Centerline ?? new List<Vector3>())
                    .Select(point => WithAltitude(point, copy.NominalAltitude)).ToList();
                network.Edges.Add(new Edge
                {
                    EdgeId = edgeId,
                    LayerId = copy.LayerId,
                    LinkNodesId = new List<string> { fromId, toId },
                    Centerline = centerline,
                    Length = CalculatePolylineLength(centerline),
                    Lanes = new List<Lane>()
                });
                Node from = network.Nodes.First(node => node.NodeId == fromId);
                Node to = network.Nodes.First(node => node.NodeId == toId);
                from.ConnectedEdges.Add(edgeId);
                from.NeighborNodes.Add(toId);
                to.ConnectedEdges.Add(edgeId);
                to.NeighborNodes.Add(fromId);
            }
            return copy;
        }

        /// <summary>Deletes a layer and all axis nodes, edges and intermediate waypoints owned by it.</summary>
        public static bool DeleteAxisLayer(Network network, string layerId)
        {
            if (network == null || network.AltitudeLayers == null || network.AltitudeLayers.Count <= 1)
                return false;
            AltitudeLayer layer = FindLayer(network, layerId);
            if (layer == null) return false;
            var nodeIds = new HashSet<string>(network.Nodes
                .Where(node => node != null && node.LayerId == layerId).Select(node => node.NodeId));
            var edgeIds = new HashSet<string>(network.Edges
                .Where(edge => edge != null && (edge.LayerId == layerId ||
                       (edge.LinkNodesId ?? new List<string>()).Any(nodeIds.Contains)))
                .Select(edge => edge.EdgeId));
            network.Edges.RemoveAll(edge => edge != null && edgeIds.Contains(edge.EdgeId));
            network.Nodes.RemoveAll(node => node != null && nodeIds.Contains(node.NodeId));
            foreach (Node node in network.Nodes.Where(node => node != null))
            {
                node.ConnectedEdges?.RemoveAll(edgeIds.Contains);
                node.NeighborNodes?.RemoveAll(nodeIds.Contains);
            }
            network.LaneConnectionCorridors?.RemoveAll(corridor => corridor != null &&
                (corridor.FromLayerId == layerId || corridor.ToLayerId == layerId));
            network.AltitudeLayers.Remove(layer);
            return true;
        }

        /// <summary>Creates an independent snapshot suitable for runtime undo/redo transactions.</summary>
        public static Network CloneAxisNetwork(Network source)
        {
            if (source == null) return null;
            return new Network
            {
                SchemaVersion = source.SchemaVersion,
                NetworkId = source.NetworkId,
                AltitudeLayers = (source.AltitudeLayers ?? new List<AltitudeLayer>()).Where(layer => layer != null)
                    .Select(layer => new AltitudeLayer
                    {
                        LayerId = layer.LayerId,
                        DisplayName = layer.DisplayName,
                        NominalAltitude = layer.NominalAltitude,
                        VerticalTolerance = layer.VerticalTolerance,
                        Enabled = layer.Enabled,
                        Frozen = layer.Frozen
                    }).ToList(),
                Nodes = (source.Nodes ?? new List<Node>()).Where(node => node != null).Select(node => new Node
                {
                    NodeId = node.NodeId,
                    LayerId = node.LayerId,
                    Position = node.Position,
                    ConnectedEdges = new List<string>(node.ConnectedEdges ?? new List<string>()),
                    NeighborNodes = new List<string>(node.NeighborNodes ?? new List<string>())
                }).ToList(),
                Edges = (source.Edges ?? new List<Edge>()).Where(edge => edge != null).Select(edge => new Edge
                {
                    EdgeId = edge.EdgeId,
                    LayerId = edge.LayerId,
                    LinkNodesId = new List<string>(edge.LinkNodesId ?? new List<string>()),
                    Centerline = new List<Vector3>(edge.Centerline ?? new List<Vector3>()),
                    Length = edge.Length,
                    Lanes = new List<Lane>()
                }).ToList(),
                LaneConnectionCorridors = (source.LaneConnectionCorridors ?? new List<LaneConnectionCorridor>())
                    .Where(corridor => corridor != null)
                    .Select(CloneLaneConnectionCorridor)
                    .ToList()
            };
        }

        /// <summary>
        /// Creates a value-copy of a layer connector. Axis-network scene files keep these
        /// descriptors as their source of truth even though their lane geometry is rebuilt.
        /// </summary>
        public static LaneConnectionCorridor CloneLaneConnectionCorridor(LaneConnectionCorridor source)
        {
            if (source == null) return null;
            return new LaneConnectionCorridor
            {
                CorridorId = source.CorridorId,
                FromLaneId = source.FromLaneId,
                ToLaneId = source.ToLaneId,
                FromLayerId = source.FromLayerId,
                ToLayerId = source.ToLayerId,
                FromPoint = source.FromPoint,
                ToPoint = source.ToPoint,
                Centerline = new List<Vector3>(source.Centerline ?? new List<Vector3>()),
                Length = source.Length,
                MaxSpeed = source.MaxSpeed,
                Priority = source.Priority
            };
        }

        /// <summary>Swaps adjacent physical heights while preserving every point's X/Z plan coordinate.</summary>
        public static bool SwapAxisLayerOrder(Network network, string layerId, int direction)
        {
            if (network == null || direction == 0) return false;
            NormalizeAxisLayerMembership(network);
            List<AltitudeLayer> ordered = network.AltitudeLayers
                .Where(layer => layer != null)
                .OrderBy(layer => layer.NominalAltitude)
                .ThenBy(layer => layer.LayerId)
                .ToList();
            int index = ordered.FindIndex(layer => layer.LayerId == layerId);
            int target = index + Math.Sign(direction);
            if (index < 0 || target < 0 || target >= ordered.Count) return false;
            float altitude = ordered[index].NominalAltitude;
            ordered[index].NominalAltitude = ordered[target].NominalAltitude;
            ordered[target].NominalAltitude = altitude;
            ApplyLayerAltitudes(network);
            return true;
        }

        public static AltitudeLayer FindLayer(Network network, string layerId)
        {
            return network?.AltitudeLayers?.FirstOrDefault(layer => layer != null && layer.LayerId == layerId);
        }

        public static Lane CreateVirtualLane(LaneConnectionCorridor corridor)
        {
            if (corridor == null) throw new ArgumentNullException(nameof(corridor));

            return new Lane
            {
                LaneId = corridor.CorridorId,
                LayerId = corridor.ToLayerId,
                LaneType = "LayerConnector",
                FromLanesId = new List<string> { corridor.FromLaneId },
                ToLanesId = new List<string> { corridor.ToLaneId },
                Centerline = new List<Vector3>(corridor.Centerline ?? new List<Vector3>()),
                Length = corridor.Length,
                LinkCorridors = new List<(string, Vector3)>()
            };
        }

        public static float CalculatePolylineLength(IReadOnlyList<Vector3> points)
        {
            if (points == null || points.Count < 2) return 0f;

            float length = 0f;
            for (int i = 1; i < points.Count; i++)
                length += Vector3.Distance(points[i - 1], points[i]);
            return length;
        }

        /// <summary>
        /// Builds a directed cubic Bezier connector tangent to both attached lanes.
        /// FromPoint/ToPoint are editable attachment candidates and are projected onto
        /// their owning lane centerlines before the curve is sampled.
        /// </summary>
        public static List<Vector3> BuildLaneConnectorBezier(
            Lane fromLane,
            Lane toLane,
            Vector3 fromPoint,
            Vector3 toPoint,
            int segments = 32,
            float tangentRatio = 0.32f)
        {
            var result = new List<Vector3>();
            if (fromLane?.Centerline == null || fromLane.Centerline.Count < 2 ||
                toLane?.Centerline == null || toLane.Centerline.Count < 2)
                return result;

            Vector3 p0 = ClosestPointOnPolyline(fromLane.Centerline, fromPoint);
            Vector3 p3 = ClosestPointOnPolyline(toLane.Centerline, toPoint);
            Vector3 fromTangent = TangentOnPolyline(fromLane.Centerline, p0);
            Vector3 toTangent = TangentOnPolyline(toLane.Centerline, p3);
            if (fromTangent.sqrMagnitude < 0.0001f) fromTangent = (p3 - p0).normalized;
            if (toTangent.sqrMagnitude < 0.0001f) toTangent = (p3 - p0).normalized;

            float handle = Mathf.Max(1f, Vector3.Distance(p0, p3) *
                Mathf.Clamp(tangentRatio, 0.08f, 0.7f));
            Vector3 p1 = p0 + fromTangent.normalized * handle;
            Vector3 p2 = p3 - toTangent.normalized * handle;
            int count = Mathf.Max(8, segments);
            for (int i = 0; i <= count; i++)
            {
                float t = i / (float)count;
                float u = 1f - t;
                result.Add(u * u * u * p0 + 3f * u * u * t * p1 +
                           3f * u * t * t * p2 + t * t * t * p3);
            }
            return result;
        }

        private static void EnsureLayers(Network network, IReadOnlyList<Lane> lanes)
        {
            network.AltitudeLayers.RemoveAll(layer => layer == null);
            if (network.AltitudeLayers.Count == 0)
            {
                float altitude = lanes.Count > 0
                    ? lanes.Average(lane => MeanAltitude(lane.Centerline))
                    : 75f;
                network.AltitudeLayers.Add(new AltitudeLayer
                {
                    LayerId = DefaultLayerId,
                    DisplayName = "Default Layer",
                    NominalAltitude = altitude,
                    VerticalTolerance = 5f,
                    Enabled = true,
                    Frozen = false
                });
            }

            var duplicateLayers = network.AltitudeLayers
                .Where(layer => string.IsNullOrWhiteSpace(layer.LayerId))
                .ToList();
            for (int i = 0; i < duplicateLayers.Count; i++)
                duplicateLayers[i].LayerId = $"L{network.AltitudeLayers.IndexOf(duplicateLayers[i])}";
        }

        public static string FindNearestLayerId(IEnumerable<AltitudeLayer> layers, float altitude)
        {
            AltitudeLayer nearest = layers
                .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId))
                .OrderBy(layer => Mathf.Abs(layer.NominalAltitude - altitude))
                .FirstOrDefault();
            return nearest != null ? nearest.LayerId : DefaultLayerId;
        }

        private static float MeanAltitude(IReadOnlyList<Vector3> points)
        {
            if (points == null || points.Count == 0) return 75f;
            float sum = 0f;
            for (int i = 0; i < points.Count; i++) sum += points[i].y;
            return sum / points.Count;
        }

        private static void ApplyLayerAltitudes(Network network)
        {
            var layers = network.AltitudeLayers
                .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId))
                .ToDictionary(layer => layer.LayerId, layer => layer.NominalAltitude);
            foreach (Node node in network.Nodes.Where(node => node != null && layers.ContainsKey(node.LayerId)))
                node.Position = WithAltitude(node.Position, layers[node.LayerId]);
            foreach (Edge edge in network.Edges.Where(edge => edge != null && layers.ContainsKey(edge.LayerId)))
            {
                ClampPolylineToAltitude(edge.Centerline, layers[edge.LayerId]);
                if (edge.Lanes == null) continue;
                foreach (Lane lane in edge.Lanes.Where(lane => lane != null))
                {
                    lane.LayerId = edge.LayerId;
                    ClampPolylineToAltitude(lane.Centerline, layers[edge.LayerId]);
                }
            }
        }

        private static void ClampPolylineToAltitude(IList<Vector3> points, float altitude)
        {
            if (points == null) return;
            for (int i = 0; i < points.Count; i++) points[i] = WithAltitude(points[i], altitude);
        }

        private static Vector3 WithAltitude(Vector3 point, float altitude)
        {
            point.y = altitude;
            return point;
        }

        private static bool IsLegacyDefaultLayerMismatch(string currentLayerId, string nearestLayerId,
            float sourceAltitude, IReadOnlyDictionary<string, AltitudeLayer> layers)
        {
            if (currentLayerId != DefaultLayerId || nearestLayerId == currentLayerId ||
                !layers.TryGetValue(currentLayerId, out AltitudeLayer current) ||
                !layers.TryGetValue(nearestLayerId, out AltitudeLayer nearest)) return false;
            return Mathf.Abs(sourceAltitude - nearest.NominalAltitude) + 0.01f <
                   Mathf.Abs(sourceAltitude - current.NominalAltitude);
        }

        private static void NormalizeCenterline(
            LaneConnectionCorridor corridor,
            Lane fromLane,
            Lane toLane)
        {
            corridor.Centerline = corridor.Centerline ?? new List<Vector3>();
            if (fromLane?.Centerline != null && fromLane.Centerline.Count > 1 &&
                toLane?.Centerline != null && toLane.Centerline.Count > 1)
            {
                Vector3 start = corridor.Centerline.Count > 1
                    ? corridor.Centerline[0]
                    : fromLane.Centerline[fromLane.Centerline.Count - 1];
                Vector3 end = corridor.Centerline.Count > 1
                    ? corridor.Centerline[corridor.Centerline.Count - 1]
                    : toLane.Centerline[0];
                corridor.Centerline = BuildLaneConnectorBezier(fromLane, toLane, start, end);
            }
            else if (corridor.Centerline.Count < 2)
            {
                corridor.Centerline = new List<Vector3> { corridor.FromPoint, corridor.ToPoint };
            }

            corridor.FromPoint = corridor.Centerline[0];
            corridor.ToPoint = corridor.Centerline[corridor.Centerline.Count - 1];
        }

        private static Vector3 ClosestPointOnPolyline(IList<Vector3> points, Vector3 target)
        {
            Vector3 bestPoint = points[0];
            float bestDistance = float.MaxValue;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 segment = points[i] - points[i - 1];
                float lengthSquared = segment.sqrMagnitude;
                float t = lengthSquared <= 0.000001f
                    ? 0f
                    : Mathf.Clamp01(Vector3.Dot(target - points[i - 1], segment) / lengthSquared);
                Vector3 projected = points[i - 1] + segment * t;
                float distance = (target - projected).sqrMagnitude;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestPoint = projected;
            }
            return bestPoint;
        }

        private static Vector3 TangentOnPolyline(IList<Vector3> points, Vector3 target)
        {
            Vector3 tangent = points[1] - points[0];
            float bestDistance = float.MaxValue;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 segment = points[i] - points[i - 1];
                float lengthSquared = segment.sqrMagnitude;
                float t = lengthSquared <= 0.000001f
                    ? 0f
                    : Mathf.Clamp01(Vector3.Dot(target - points[i - 1], segment) / lengthSquared);
                Vector3 projected = points[i - 1] + segment * t;
                float distance = (target - projected).sqrMagnitude;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                tangent = segment;
            }
            return tangent.normalized;
        }

        private static string NextConnectorId(ISet<string> knownIds)
        {
            int index = 1;
            string candidate;
            do
            {
                candidate = $"LC-{index:D4}";
                index++;
            }
            while (knownIds.Contains(candidate));
            return candidate;
        }

        private static string NextCopyId(string sourceId, string layerId, ISet<string> knownIds)
        {
            string stem = $"{sourceId}_{layerId}";
            string candidate = stem;
            int suffix = 2;
            while (knownIds.Contains(candidate)) candidate = $"{stem}_{suffix++}";
            return candidate;
        }
    }
}
