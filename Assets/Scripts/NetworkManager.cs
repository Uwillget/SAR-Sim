using System.Collections.Generic;
using System.IO;
using System.Linq;
using RoadNetworkEditor.Networking;
using UnityEngine;

using Newtonsoft.Json;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RoadNetworkEditor
{
    /// <summary>
    /// Road network manager, attached to an empty GameObject in the scene.
    /// Responsible for creating, editing, and managing the axis network and full road network.
    /// </summary>
    [ExecuteAlways]
    public class NetworkManager : MonoBehaviour
    {
        [Header("Network Data")]
        public Network AxisNetwork = new Network(); // Axis network.
        public Network FullNetwork = new Network();  // Full road network (with lanes).

        [Header("Visual Settings")]
        public float nodeSize = 0.5f;
        public float edgeWidth = 0.2f;
        public float waypointSize = 0.3f;
        public bool showTextAnnotations = true;
        public bool showFullNetwork = false;

        [Header("Prefabs")]
        public GameObject nodePrefab;
        public GameObject edgePrefab;

        [Header("Lane Generation")]
        [Tooltip("Standard lane width in meters.")]
        public float laneWidth = 5f;
        [Tooltip("Minimum lane count (even).")]
        [Range(1, 10)] public int minLaneCount = 2;
        [Tooltip("Maximum lane count (even).")]
        [Range(1, 10)] public int maxLaneCount = 8;
        [Tooltip("Building collision layer.")]
        public LayerMask buildingLayerMask = 1 << 6;

        [Tooltip("Half-height for collision check (Y-axis range).")]
        public float collisionCheckHeight = 10f;

        [Header("Axis Auto-Generation")]
        [Tooltip("Drag AxisGenerator1 or AxisGenerator2 component here.")]
        public AxisGeneratorBase axisGenerator;

        [Header("Axis Gen 1: Skeleton Extraction")]
        [Tooltip("Voxel grid size; smaller yields finer results but is slower.")]
        public float axisVoxelSize = 5f;
        [Tooltip("Obstacle-check height and altitude of the generated axis, in metres.")]
        public float axisDetectionHeight = 50f;
        [Tooltip("Collinearity straightening threshold.")]
        public float axisStraightLimit = 8f;

        [Header("Axis Gen 2: Quadtree Partition")]
        [Tooltip("Maximum subdivision depth")]
        public int axis2MaxDepth = 6;
        [Tooltip("Minimum cell edge length")]
        public float axis2MinCellSize = 2f;
        [Tooltip("Free cells smaller than this are pruned")]
        public float axis2PruneCellSize = 4f;
        [Tooltip("Minimum corridor length to keep")]
        public float axis2MinCorridorLength = 18f;
        [Tooltip("Minimum parallel corridor spacing")]
        public float axis2MinParallelSpacing = 12f;
        [Tooltip("Dead-end branches shorter than this removed")]
        public float axis2MinDeadEndLength = 18f;
        [Tooltip("Add boundary ring around domain")]
        public bool axis2EnableOuterFrame = true;

        // Runtime data.
        private Dictionary<string, NodeVisual> nodeVisuals = new Dictionary<string, NodeVisual>();
        private Dictionary<string, EdgeVisual> edgeVisuals = new Dictionary<string, EdgeVisual>();

        private NodeVisual selectedNode;
        private EdgeVisual selectedEdge;
        private WaypointVisual selectedWaypoint;
        private Transform nodesContainer;
        private Transform edgesContainer;

        [SerializeField] private bool _manuallyCleared = false;

        private void Awake()
        {
            if (AxisNetwork == null)
                AxisNetwork = new Network { NetworkId = "AxisNetwork" };

            if (FullNetwork == null)
                FullNetwork = new Network { NetworkId = "FullNetwork" };

            MultilayerNetworkUtility.NormalizeAxisLayerMembership(AxisNetwork);
            MultilayerNetworkUtility.NormalizeAndValidate(FullNetwork);

            LoadPrefabs();

            // Destroy old persistent containers; visual objects exist only when data is non-empty.
            Transform oldNodes = transform.Find("Nodes");
            if (oldNodes != null) DestroyImmediate(oldNodes.gameObject);
            Transform oldEdges = transform.Find("Edges");
            if (oldEdges != null) DestroyImmediate(oldEdges.gameObject);

            nodeVisuals.Clear();
            edgeVisuals.Clear();

            InitializeContainers();

            // If data exists (preserved by Unity serialization), rebuild visuals immediately.
            // Otherwise wait for OnEnable to attempt auto-load from JSON.
            if (AxisNetwork.Nodes.Count > 0 || AxisNetwork.Edges.Count > 0)
            {
                RebuildVisualsFromData();
            }
        }

        private const string DEFAULT_JSON_PATH = "Assets/DataBase/AxisNetwork.json";

        private void OnEnable()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // Attempt immediate sync (handles initialization after scene load).
                if (nodesContainer == null || edgesContainer == null)
                {
                    InitializeContainers();
                }

                if (ShouldAutoLoadFromJSON())
                {
                    Debug.Log("[NetworkManager] Data is empty, auto-loading from JSON...");
                    AutoLoadFromJSON();
                }
                else
                {
                    EnsureVisualsSyncWithData();
                }

                // Delayed re-sync (handles DontSave container rebuild after domain reload).
                EditorApplication.delayCall += () =>
                {
                    if (this == null) return;

                    if (nodesContainer == null || edgesContainer == null)
                    {
                        InitializeContainers();
                    }

                    if (ShouldAutoLoadFromJSON())
                    {
                        Debug.Log("[NetworkManager] Data is empty, auto-loading from JSON...");
                        AutoLoadFromJSON();
                    }
                    else
                    {
                        EnsureVisualsSyncWithData();
                    }
                };
            }
#endif
        }

        private void Start()
        {
            // Final safety net: ensure visuals are synced with data.
            // Start runs after all Awake/OnEnable and before the first frame.
            EnsureVisualsSyncWithData();
        }

        /// <summary>
        /// Ensure visual objects are synced with data; rebuild if data exists but visuals are missing.
        /// </summary>
        private void EnsureVisualsSyncWithData()
        {
            if (AxisNetwork == null)
                return;

            bool hasData = AxisNetwork.Nodes.Count > 0 || AxisNetwork.Edges.Count > 0;
            if (!hasData)
                return;

            bool missingNodeVisual = AxisNetwork.Nodes.Any(node => node != null &&
                (!nodeVisuals.TryGetValue(node.NodeId, out NodeVisual visual) || visual == null));
            bool missingEdgeVisual = AxisNetwork.Edges.Any(edge => edge != null &&
                (!edgeVisuals.TryGetValue(edge.EdgeId, out EdgeVisual visual) || visual == null));
            bool missingContainers = nodesContainer == null || edgesContainer == null;

            if (missingContainers || missingNodeVisual || missingEdgeVisual)
            {
                Debug.Log("[NetworkManager] Data exists but visuals are missing, auto-rebuilding visuals...");
                RebuildVisualsFromData();
            }
        }

        /// <summary>
        /// Player-build safety net for runtime editing. DontSave editor visuals are discarded when
        /// entering Play mode, so dictionary counts alone cannot prove that legacy data is editable.
        /// </summary>
        public void EnsureRuntimeEditingReady()
        {
            if (AxisNetwork == null)
                AxisNetwork = new Network { NetworkId = "AxisNetwork" };
            MultilayerNetworkUtility.NormalizeAxisLayerMembership(AxisNetwork);
            LoadPrefabs();
            if (nodesContainer == null || edgesContainer == null) InitializeContainers();
            EnsureVisualsSyncWithData();
        }

        private bool ShouldAutoLoadFromJSON()
        {
            if (_manuallyCleared)
                return false;

            bool dataIsEmpty = (AxisNetwork == null ||
                               (AxisNetwork.Nodes.Count == 0 && AxisNetwork.Edges.Count == 0));

            if (!dataIsEmpty)
                return false;

            return File.Exists(DEFAULT_JSON_PATH);
        }

        /// <summary>
        /// Automatically load data from a JSON file.
        /// </summary>
        private void AutoLoadFromJSON()
        {
            if (!File.Exists(DEFAULT_JSON_PATH))
            {
                Debug.LogWarning($"[NetworkManager] JSON file not found at {DEFAULT_JSON_PATH}");
                return;
            }

            try
            {
                // Read JSON file.
                string json = File.ReadAllText(DEFAULT_JSON_PATH);

                // Deserialize using Newtonsoft.Json.
                JsonSerializerSettings settings = new JsonSerializerSettings
                {
                    ObjectCreationHandling = ObjectCreationHandling.Replace,
                    Converters = new List<JsonConverter> { new Vector3Converter() }
                };

                Network importedNetwork = JsonConvert.DeserializeObject<Network>(json, settings);

                if (importedNetwork == null)
                {
                    Debug.LogError("[NetworkManager] Failed to parse JSON file.");
                    return;
                }
                if (!TryConsolidateAltitudeLayers(importedNetwork, out string layerError))
                {
                    Debug.LogError($"[NetworkManager] {layerError}");
                    return;
                }

                ClearAxisNetwork();

                AxisNetwork = importedNetwork;
                _manuallyCleared = false;

                RebuildVisualsFromData();

#if UNITY_EDITOR
                EditorUtility.SetDirty(this);
#endif

                Debug.Log($"[NetworkManager] Auto-loaded network from JSON: {importedNetwork.Nodes.Count} nodes, {importedNetwork.Edges.Count} edges");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[NetworkManager] Auto-load from JSON failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void InitializeContainers()
        {
            Transform nodesTransform = transform.Find("Nodes");
            if (nodesTransform == null)
            {
                GameObject nodesObj = new GameObject("Nodes");
                nodesObj.hideFlags = HideFlags.DontSave;
                nodesObj.transform.SetParent(transform);
                nodesObj.transform.localPosition = Vector3.zero;
                nodesContainer = nodesObj.transform;
            }
            else
            {
                nodesContainer = nodesTransform;
            }

            Transform edgesTransform = transform.Find("Edges");
            if (edgesTransform == null)
            {
                GameObject edgesObj = new GameObject("Edges");
                edgesObj.hideFlags = HideFlags.DontSave;
                edgesObj.transform.SetParent(transform);
                edgesObj.transform.localPosition = Vector3.zero;
                edgesContainer = edgesObj.transform;
            }
            else
            {
                edgesContainer = edgesTransform;
            }

            ApplyFullNetworkVisibility();
        }

        private void LoadPrefabs()
        {
            if (nodePrefab == null)
                nodePrefab = Resources.Load<GameObject>("Prefabs/NodePrefab");

            if (edgePrefab == null)
                edgePrefab = Resources.Load<GameObject>("Prefabs/EdgePrefab");
        }

        #region Node Operations

        /// <summary>
        /// Create a new node.
        /// </summary>
        public NodeVisual CreateNode(Vector3 position, string layerId = null)
        {
            if (nodePrefab == null)
            {
                Debug.LogError("Node prefab is not assigned!");
                return null;
            }

            if (nodesContainer == null)
                InitializeContainers();

            MultilayerNetworkUtility.NormalizeAxisLayerMembership(AxisNetwork);
            string resolvedLayerId = string.IsNullOrWhiteSpace(layerId)
                ? MultilayerNetworkUtility.FindNearestLayerId(AxisNetwork.AltitudeLayers, position.y)
                : layerId;
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(AxisNetwork, resolvedLayerId);
            if (layer == null)
            {
                Debug.LogError($"Unknown altitude layer '{resolvedLayerId}'.");
                return null;
            }
            if (RejectFrozenAxisMutation(layer.LayerId, "create a node"))
                return null;
            position.y = layer.NominalAltitude;

            // Create data.
            Node node = new Node
            {
                NodeId = GenerateNodeId(),
                LayerId = layer.LayerId,
                Position = position,
                ConnectedEdges = new List<string>(),
                NeighborNodes = new List<string>()
            };

            AxisNetwork.Nodes.Add(node);
            _manuallyCleared = false;

            GameObject nodeObj = Instantiate(nodePrefab, position, Quaternion.identity, nodesContainer);
            NodeVisual nodeVisual = nodeObj.GetComponent<NodeVisual>();

            if (nodeVisual == null)
                nodeVisual = nodeObj.AddComponent<NodeVisual>();

            nodeVisual.Initialize(node);
            nodeVisuals[node.NodeId] = nodeVisual;

            Debug.Log($"Created node {node.NodeId} under Nodes container");

            return nodeVisual;
        }

        /// <summary>
        /// Delete a node.
        /// </summary>
        public void DeleteNode(string nodeId, bool destroyGameObject = true)
        {
            Node node = AxisNetwork.Nodes.Find(n => n.NodeId == nodeId);
            if (node == null)
                return;
            if (RejectFrozenAxisMutation(node.LayerId, $"delete node {nodeId}"))
                return;

            // Delete all connected edges.
            List<string> edgesToDelete = new List<string>(node.ConnectedEdges);
            foreach (string edgeId in edgesToDelete)
            {
                DeleteEdge(edgeId);
            }

            // Delete visual object (only when needed).
            nodeVisuals.TryGetValue(nodeId, out NodeVisual nodeVisual);
            if (destroyGameObject && nodeVisual != null)
            {
                if (Application.isPlaying) Destroy(nodeVisual.gameObject);
                else DestroyImmediate(nodeVisual.gameObject);
            }

            // Remove from dictionary and data.
            nodeVisuals.Remove(nodeId);
            AxisNetwork.Nodes.Remove(node);

            if (selectedNode == nodeVisual)
                selectedNode = null;
        }

        /// <summary>
        /// Update node position.
        /// </summary>
        public void UpdateNodePosition(string nodeId, Vector3 newPosition)
        {
            Node node = AxisNetwork.Nodes.Find(n => n.NodeId == nodeId);
            if (node == null) return;
            if (RejectFrozenAxisMutation(node.LayerId, $"move node {nodeId}"))
                return;
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(AxisNetwork, node?.LayerId);
            if (layer != null) newPosition.y = layer.NominalAltitude;
            node.Position = newPosition;

            if (nodeVisuals.TryGetValue(nodeId, out NodeVisual nodeVisual) && nodeVisual != null)
                nodeVisual.UpdatePosition(newPosition);

            // Update all connected edges.
            foreach (string edgeId in node.ConnectedEdges ?? new List<string>())
            {
                UpdateEdgeEndpoints(edgeId);
            }
        }

        /// <summary>
        /// Replace the editable axis network from an arbitrary JSON file. Parsing is
        /// completed before the live data is changed so a malformed external file
        /// cannot destroy the currently opened scene.
        /// </summary>
        public bool TryImportAxisNetwork(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "The network file path is empty.";
                return false;
            }

            string importPath;
            try { importPath = Path.GetFullPath(path); }
            catch (System.Exception ex)
            {
                error = $"The network file path is invalid: {ex.Message}";
                return false;
            }

            if (!File.Exists(importPath))
            {
                error = $"The network file does not exist: {importPath}";
                return false;
            }

            Network previous = MultilayerNetworkUtility.CloneAxisNetwork(AxisNetwork);
            bool liveDataChanged = false;
            try
            {
                var settings = new JsonSerializerSettings
                {
                    ObjectCreationHandling = ObjectCreationHandling.Replace,
                    Converters = new List<JsonConverter> { new Vector3Converter() }
                };
                Network imported = JsonConvert.DeserializeObject<Network>(File.ReadAllText(importPath), settings);
                if (imported == null)
                {
                    error = "The network JSON is empty or malformed.";
                    return false;
                }
                if (!TryConsolidateAltitudeLayers(imported, out error)) return false;

                imported.Nodes = imported.Nodes ?? new List<Node>();
                imported.Edges = imported.Edges ?? new List<Edge>();
                MultilayerNetworkUtility.NormalizeAxisLayerMembership(imported);
                if (!TryPrepareImportedAxisNetwork(imported, out error)) return false;
                var expectedConnectorIds = new HashSet<string>(
                    (imported.LaneConnectionCorridors ?? new List<LaneConnectionCorridor>())
                    .Select(corridor => corridor.CorridorId), System.StringComparer.Ordinal);

                AxisNetwork = imported;
                liveDataChanged = true;
                _manuallyCleared = false;
                GenerateFullNetworkFromAxis();
                var generatedConnectorIds = new HashSet<string>(
                    (FullNetwork.LaneConnectionCorridors ?? new List<LaneConnectionCorridor>())
                    .Where(corridor => corridor != null)
                    .Select(corridor => corridor.CorridorId), System.StringComparer.Ordinal);
                if (!expectedConnectorIds.SetEquals(generatedConnectorIds))
                    throw new InvalidDataException("An interlayer corridor references a missing lane or two lanes on the same layer.");
                RebuildVisualsFromData();
#if UNITY_EDITOR
                EditorUtility.SetDirty(this);
#endif
                Debug.Log($"[NetworkManager] Imported axis network from {importPath}: " +
                          $"{AxisNetwork.Nodes.Count} node(s), {AxisNetwork.Edges.Count} edge(s).");
                return true;
            }
            catch (System.Exception ex)
            {
                if (liveDataChanged && previous != null)
                {
                    try
                    {
                        AxisNetwork = previous;
                        GenerateFullNetworkFromAxis();
                        RebuildVisualsFromData();
                    }
                    catch (System.Exception rollbackException)
                    {
                        Debug.LogError($"[NetworkManager] Axis network rollback failed: {rollbackException}");
                    }
                }
                error = $"Failed to import the network: {ex.Message}";
                Debug.LogError($"[NetworkManager] {error}\n{ex.StackTrace}");
                return false;
            }
        }

        private static bool TryPrepareImportedAxisNetwork(Network network, out string error)
        {
            error = null;
            var nodeIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (Node node in network.Nodes)
            {
                if (node == null || string.IsNullOrWhiteSpace(node.NodeId))
                {
                    error = "The network contains a null node or an empty node ID.";
                    return false;
                }
                if (!nodeIds.Add(node.NodeId))
                {
                    error = $"Duplicate node ID: {node.NodeId}.";
                    return false;
                }
                node.ConnectedEdges = new List<string>();
                node.NeighborNodes = new List<string>();
            }

            var edgeIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (Edge edge in network.Edges)
            {
                if (edge == null || string.IsNullOrWhiteSpace(edge.EdgeId))
                {
                    error = "The network contains a null edge or an empty edge ID.";
                    return false;
                }
                if (!edgeIds.Add(edge.EdgeId))
                {
                    error = $"Duplicate edge ID: {edge.EdgeId}.";
                    return false;
                }
                if (edge.LinkNodesId == null || edge.LinkNodesId.Count < 2 ||
                    !nodeIds.Contains(edge.LinkNodesId[0]) ||
                    !nodeIds.Contains(edge.LinkNodesId[edge.LinkNodesId.Count - 1]))
                {
                    error = $"Edge {edge.EdgeId} references a missing start or end node.";
                    return false;
                }
                if (edge.Centerline == null || edge.Centerline.Count < 2)
                {
                    error = $"Edge {edge.EdgeId} requires at least two centerline points.";
                    return false;
                }
                Node from = network.Nodes.First(node => node.NodeId == edge.LinkNodesId[0]);
                Node to = network.Nodes.First(node => node.NodeId == edge.LinkNodesId[edge.LinkNodesId.Count - 1]);
                if (!string.Equals(from.LayerId, to.LayerId, System.StringComparison.Ordinal))
                {
                    error = $"Edge {edge.EdgeId} crosses altitude layers; use an interlayer corridor instead.";
                    return false;
                }
                from.ConnectedEdges.Add(edge.EdgeId);
                to.ConnectedEdges.Add(edge.EdgeId);
                if (!from.NeighborNodes.Contains(to.NodeId)) from.NeighborNodes.Add(to.NodeId);
                if (!to.NeighborNodes.Contains(from.NodeId)) to.NeighborNodes.Add(from.NodeId);
                edge.Length = MultilayerNetworkUtility.CalculatePolylineLength(edge.Centerline);
            }

            var connectorIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (LaneConnectionCorridor corridor in
                     network.LaneConnectionCorridors ?? new List<LaneConnectionCorridor>())
            {
                if (corridor == null || string.IsNullOrWhiteSpace(corridor.CorridorId) ||
                    string.IsNullOrWhiteSpace(corridor.FromLaneId) ||
                    string.IsNullOrWhiteSpace(corridor.ToLaneId))
                {
                    error = "An interlayer corridor is missing its ID or connected lane.";
                    return false;
                }
                if (!connectorIds.Add(corridor.CorridorId))
                {
                    error = $"Duplicate interlayer-corridor ID: {corridor.CorridorId}.";
                    return false;
                }
            }
            return true;
        }

        #endregion

        #region Edge Operations

        /// <summary>
        /// Create a new edge.
        /// </summary>
        public EdgeVisual CreateEdge(string startNodeId, string endNodeId)
        {
            if (!nodeVisuals.ContainsKey(startNodeId) || !nodeVisuals.ContainsKey(endNodeId))
            {
                Debug.LogError("Start or end node not found!");
                return null;
            }

            if (edgePrefab == null)
            {
                Debug.LogError("Edge prefab is not assigned!");
                return null;
            }

            if (edgesContainer == null)
                InitializeContainers();

            NodeVisual startNode = nodeVisuals[startNodeId];
            NodeVisual endNode = nodeVisuals[endNodeId];
            if (startNode.NodeData == null || endNode.NodeData == null ||
                startNode.NodeData.LayerId != endNode.NodeData.LayerId)
            {
                Debug.LogError("Ordinary axis edges must connect nodes on the same altitude layer. Use a layer connector between layers.");
                return null;
            }
            if (RejectFrozenAxisMutation(startNode.NodeData.LayerId, "create an edge"))
                return null;

            // Create data.
            Edge edge = new Edge
            {
                EdgeId = GenerateEdgeId(),
                LayerId = startNode.NodeData.LayerId,
                LinkNodesId = new List<string> { startNodeId, endNodeId },
                Centerline = new List<Vector3> { startNode.transform.position, endNode.transform.position },
                Length = Vector3.Distance(startNode.transform.position, endNode.transform.position),
                Lanes = new List<Lane>()
            };

            AxisNetwork.Edges.Add(edge);
            _manuallyCleared = false;

            startNode.NodeData.ConnectedEdges.Add(edge.EdgeId);
            startNode.NodeData.NeighborNodes.Add(endNodeId);
            endNode.NodeData.ConnectedEdges.Add(edge.EdgeId);
            endNode.NodeData.NeighborNodes.Add(startNodeId);

            // Create visual object.
            GameObject edgeObj = Instantiate(edgePrefab, Vector3.zero, Quaternion.identity, edgesContainer);
            EdgeVisual edgeVisual = edgeObj.GetComponent<EdgeVisual>();

            if (edgeVisual == null)
                edgeVisual = edgeObj.AddComponent<EdgeVisual>();

            edgeVisual.Initialize(edge, startNode, endNode);
            edgeVisuals[edge.EdgeId] = edgeVisual;

            Debug.Log($"Created edge {edge.EdgeId} under Edges container");

            return edgeVisual;
        }

        /// <summary>
        /// Delete an edge.
        /// </summary>
        public void DeleteEdge(string edgeId)
        {
            // Find edge from list.
            Edge edge = AxisNetwork.Edges.Find(e => e.EdgeId == edgeId);
            if (edge == null)
                return;
            if (RejectFrozenAxisMutation(edge.LayerId, $"delete edge {edgeId}"))
                return;

            // Remove from the start/end node connection lists.
            if (edge.LinkNodesId != null && edge.LinkNodesId.Count >= 2)
            {
                string startNodeId = edge.LinkNodesId[0];
                string endNodeId = edge.LinkNodesId[1];

                Node startNode = AxisNetwork.Nodes.Find(n => n.NodeId == startNodeId);
                if (startNode != null)
                {
                    startNode.ConnectedEdges.Remove(edgeId);
                    startNode.NeighborNodes.Remove(endNodeId);
                }

                Node endNode = AxisNetwork.Nodes.Find(n => n.NodeId == endNodeId);
                if (endNode != null)
                {
                    endNode.ConnectedEdges.Remove(edgeId);
                    endNode.NeighborNodes.Remove(startNodeId);
                }
            }

            // Remove edge from list.
            AxisNetwork.Edges.Remove(edge);

            // Delete visual object.
            if (edgeVisuals.ContainsKey(edgeId))
            {
                EdgeVisual edgeVisual = edgeVisuals[edgeId];
                if (edgeVisual != null)
                {
                    if (Application.isPlaying) Destroy(edgeVisual.gameObject);
                    else DestroyImmediate(edgeVisual.gameObject);
                }
                edgeVisuals.Remove(edgeId);
            }

            Debug.Log($"Deleted edge: {edgeId}");
        }

        /// <summary>
        /// Update edge endpoints (called when a node is moved).
        /// </summary>
        private void UpdateEdgeEndpoints(string edgeId)
        {
            Edge edge = AxisNetwork.Edges.Find(item => item != null && item.EdgeId == edgeId);
            if (edge?.Centerline == null || edge.Centerline.Count < 2 ||
                edge.LinkNodesId == null || edge.LinkNodesId.Count < 2) return;

            string startNodeId = edge.LinkNodesId[0];
            string endNodeId = edge.LinkNodesId[1];
            Node startNode = AxisNetwork.Nodes.Find(node => node != null && node.NodeId == startNodeId);
            Node endNode = AxisNetwork.Nodes.Find(node => node != null && node.NodeId == endNodeId);
            if (startNode == null || endNode == null) return;

            edge.Centerline[0] = startNode.Position;
            edge.Centerline[edge.Centerline.Count - 1] = endNode.Position;
            edge.Length = MultilayerNetworkUtility.CalculatePolylineLength(edge.Centerline);

            if (edgeVisuals.TryGetValue(edgeId, out EdgeVisual edgeVisual) && edgeVisual != null)
                edgeVisual.UpdateVisual();
        }

        public bool InsertAxisWaypoint(string edgeId, Vector3 position, int insertIndex)
        {
            Edge edge = AxisNetwork?.Edges?.Find(item => item != null && item.EdgeId == edgeId);
            if (edge?.Centerline == null || edge.Centerline.Count < 2) return false;
            if (RejectFrozenAxisMutation(edge.LayerId, $"add a waypoint to edge {edgeId}"))
                return false;
            insertIndex = Mathf.Clamp(insertIndex, 1, edge.Centerline.Count - 1);
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(AxisNetwork, edge.LayerId);
            if (layer != null) position.y = layer.NominalAltitude;

            if (edgeVisuals.TryGetValue(edgeId, out EdgeVisual visual) && visual != null)
                visual.AddWaypoint(position, insertIndex);
            else
                edge.Centerline.Insert(insertIndex, position);
            edge.Length = MultilayerNetworkUtility.CalculatePolylineLength(edge.Centerline);
            return true;
        }

        public bool UpdateAxisWaypoint(string edgeId, int waypointIndex, Vector3 position)
        {
            Edge edge = AxisNetwork?.Edges?.Find(item => item != null && item.EdgeId == edgeId);
            if (edge?.Centerline == null || waypointIndex <= 0 || waypointIndex >= edge.Centerline.Count - 1)
                return false;
            if (RejectFrozenAxisMutation(edge.LayerId, $"move a waypoint on edge {edgeId}"))
                return false;
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(AxisNetwork, edge.LayerId);
            if (layer != null) position.y = layer.NominalAltitude;
            edge.Centerline[waypointIndex] = position;
            edge.Length = MultilayerNetworkUtility.CalculatePolylineLength(edge.Centerline);
            if (edgeVisuals.TryGetValue(edgeId, out EdgeVisual visual) && visual != null)
                visual.UpdateWaypoint(waypointIndex, position);
            return true;
        }

        #endregion

        #region Selection

        public void SelectNode(NodeVisual node)
        {
            if (selectedNode != null)
                selectedNode.SetSelected(false);

            if (selectedEdge != null)
            {
                selectedEdge.SetSelected(false);
                selectedEdge = null;
            }

            if (selectedWaypoint != null)
            {
                selectedWaypoint.SetSelected(false);
                selectedWaypoint = null;
            }

            selectedNode = node;
            if (selectedNode != null)
                selectedNode.SetSelected(true);
        }

        public void SelectEdge(EdgeVisual edge)
        {
            if (selectedNode != null)
            {
                selectedNode.SetSelected(false);
                selectedNode = null;
            }

            if (selectedEdge != null)
                selectedEdge.SetSelected(false);

            if (selectedWaypoint != null)
            {
                selectedWaypoint.SetSelected(false);
                selectedWaypoint = null;
            }

            selectedEdge = edge;
            if (selectedEdge != null)
                selectedEdge.SetSelected(true);
        }

        public void SelectWaypoint(WaypointVisual waypoint)
        {
            if (selectedNode != null)
            {
                selectedNode.SetSelected(false);
                selectedNode = null;
            }

            if (selectedEdge != null)
            {
                selectedEdge.SetSelected(false);
                selectedEdge = null;
            }

            if (selectedWaypoint != null)
                selectedWaypoint.SetSelected(false);

            selectedWaypoint = waypoint;
            if (selectedWaypoint != null)
                selectedWaypoint.SetSelected(true);
        }

        public void ClearSelection()
        {
            if (selectedNode != null)
            {
                selectedNode.SetSelected(false);
                selectedNode = null;
            }

            if (selectedEdge != null)
            {
                selectedEdge.SetSelected(false);
                selectedEdge = null;
            }

            if (selectedWaypoint != null)
            {
                selectedWaypoint.SetSelected(false);
                selectedWaypoint = null;
            }
        }

        public NodeVisual GetSelectedNode() => selectedNode;
        public EdgeVisual GetSelectedEdge() => selectedEdge;
        public WaypointVisual GetSelectedWaypoint() => selectedWaypoint;

        public void DeleteWaypoint(EdgeVisual edge, int waypointIndex)
        {
            if (edge == null || edge.EdgeData?.Centerline == null) return;
            if (waypointIndex <= 0 || waypointIndex >= edge.EdgeData.Centerline.Count - 1) return;
            if (RejectFrozenAxisMutation(edge.EdgeData.LayerId,
                    $"delete a waypoint from edge {edge.EdgeData.EdgeId}")) return;
            edge.RemoveWaypoint(waypointIndex);
            ClearSelection();
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        #endregion

        #region Axis Generator Trigger

        /// <summary>Trigger whichever AxisGenerator component is assigned.</summary>
        public void RunAxisGenerator()
        {
            if (axisGenerator == null)
            {
                Debug.LogError("[NetworkManager] No AxisGenerator assigned. Drag AxisGenerator1 or AxisGenerator2 to the slot.");
                return;
            }

            // Configure common parameters from NetworkManager.
            GenerationBoundary boundary = GetComponent<GenerationBoundary>();
            axisGenerator.boundary = boundary;
            if (boundary != null && boundary.HasPolygon)
                boundary.FitBox(GetComponent<BoxCollider>());
            axisGenerator.detectionArea = GetComponent<BoxCollider>();
            if (axisGenerator.detectionArea == null)
            {
                axisGenerator.detectionArea = gameObject.AddComponent<BoxCollider>();
                axisGenerator.detectionArea.size = new Vector3(2500, 200, 2500);
                axisGenerator.detectionArea.center = new Vector3(0, 0, 0);
            }
            axisGenerator.laneWidth = laneWidth;
            axisGenerator.minLaneCount = minLaneCount;
            axisGenerator.maxLaneCount = maxLaneCount;
            axisGenerator.obstacleLayerMask = buildingLayerMask;
            axisGenerator.detectionHeight = axisDetectionHeight;
            axisGenerator.outputPath = "Assets/DataBase/AxisNetwork.json";

            // Configure type-specific parameters.
            if (axisGenerator is AxisGenerator1 gen1)
            {
                gen1.voxelSize = axisVoxelSize;
                gen1.straightLimit = axisStraightLimit;
            }
            else if (axisGenerator is AxisGenerator2 gen2)
            {
                gen2.maxDepth = axis2MaxDepth;
                gen2.minCellSize = axis2MinCellSize;
                gen2.pruneCellSize = axis2PruneCellSize;
                gen2.minCorridorLength = axis2MinCorridorLength;
                gen2.minParallelSpacing = axis2MinParallelSpacing;
                gen2.minDeadEndLength = axis2MinDeadEndLength;
                gen2.enableOuterFrame = axis2EnableOuterFrame;
            }
            else
            {
                Debug.LogError($"[NetworkManager] Unsupported generator type: {axisGenerator.GetType().Name}. Use AxisGenerator1 or AxisGenerator2.");
                return;
            }

            // Run the algorithm.
            axisGenerator.GenerateAxisNetwork();

            // Load generated JSON.
            string jsonPath = System.IO.Path.Combine(Application.dataPath, "../", "Assets/DataBase/AxisNetwork.json");
            if (System.IO.File.Exists(jsonPath))
            {
                string json = System.IO.File.ReadAllText(jsonPath);
                var settings = new JsonSerializerSettings
                {
                    ObjectCreationHandling = ObjectCreationHandling.Replace,
                    Converters = new List<JsonConverter> { new Vector3Converter() }
                };
                var net = JsonConvert.DeserializeObject<Network>(json, settings);
                if (net != null)
                {
                    if (!TryConsolidateAltitudeLayers(net, out string layerError))
                    {
                        Debug.LogError($"[NetworkManager] {layerError}");
                        return;
                    }
                    ClearAxisNetwork();
                    AxisNetwork = net;
                    RebuildVisualsFromData();
                    Debug.Log($"[NetworkManager] Auto-loaded generated axis network: {net.Nodes.Count} nodes, {net.Edges.Count} edges");
                }
            }
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        #endregion

        #region Network Generation

        /// <summary>
        /// Generate the full road network from the axis network (including lane centerlines, collision checks, and lane connections).
        /// Process: compute widths, compute intersection trims, generate lanes from trimmed endpoints (matching reference implementation).
        /// </summary>
        public void GenerateFullNetworkFromAxis()
        {
            Debug.Log("[NetworkManager] Starting full road network generation...");

            // Layer connectors are authored together with the axis network, while their
            // lane-attached Bezier geometry belongs to the generated full network. Keep a
            // snapshot across regeneration so editing an axis edge never silently deletes
            // valid inter-layer traffic links.
            List<LaneConnectionCorridor> retainedLayerConnectors =
                (AxisNetwork?.LaneConnectionCorridors != null && AxisNetwork.LaneConnectionCorridors.Count > 0
                    ? AxisNetwork.LaneConnectionCorridors
                    : FullNetwork?.LaneConnectionCorridors ?? new List<LaneConnectionCorridor>())
                .Where(corridor => corridor != null)
                .Select(MultilayerNetworkUtility.CloneLaneConnectionCorridor)
                .ToList();
            ClearFullNetwork();
            FullNetwork.NetworkId = "FullNetwork";
            MultilayerNetworkUtility.NormalizeAxisLayerMembership(AxisNetwork);
            foreach (Edge axisEdge in AxisNetwork.Edges.Where(edge => edge?.Centerline != null))
                axisEdge.Length = MultilayerNetworkUtility.CalculatePolylineLength(axisEdge.Centerline);
            FullNetwork.AltitudeLayers = AxisNetwork.AltitudeLayers
                .Where(layer => layer != null)
                .Select(layer => new AltitudeLayer
                {
                    LayerId = layer.LayerId,
                    DisplayName = layer.DisplayName,
                    NominalAltitude = layer.NominalAltitude,
                    VerticalTolerance = layer.VerticalTolerance,
                    Enabled = layer.Enabled,
                    Frozen = layer.Frozen
                }).ToList();

            if (AxisNetwork == null || AxisNetwork.Edges.Count == 0)
            {
                Debug.LogWarning("[NetworkManager] Axis network is empty, cannot generate full road network.");
                return;
            }

            // 1. Copy nodes.
            foreach (Node axisNode in AxisNetwork.Nodes)
            {
                Node newNode = new Node
                {
                    NodeId = axisNode.NodeId,
                    LayerId = axisNode.LayerId,
                    Position = axisNode.Position,
                    ConnectedEdges = new List<string>(axisNode.ConnectedEdges),
                    NeighborNodes = new List<string>(axisNode.NeighborNodes)
                };
                FullNetwork.Nodes.Add(newNode);
            }

            // 2. Collision check: compute lane count and total width for each edge; create edge shells (no lanes).
            var edgeWidthMap = new Dictionary<string, float>();
            var edgeLaneCount = new Dictionary<string, int>();

            foreach (Edge axisEdge in AxisNetwork.Edges)
            {
                if (axisEdge.Centerline == null || axisEdge.Centerline.Count < 2) continue;

                int laneCount = CalculateMaxLaneCount(axisEdge.Centerline);
                if (laneCount <= 0)
                {
                    Debug.LogWarning($"[NetworkManager] Edge {axisEdge.EdgeId} has no viable lanes after collision check, skipping.");
                    continue;
                }

                edgeWidthMap[axisEdge.EdgeId] = laneCount * laneWidth;
                edgeLaneCount[axisEdge.EdgeId] = laneCount;

                // Create edge shells first (no lanes) for CalculateIntersectionTrims lookup.
                FullNetwork.Edges.Add(new Edge
                {
                    EdgeId = axisEdge.EdgeId,
                    LayerId = axisEdge.LayerId,
                    LinkNodesId = new List<string>(axisEdge.LinkNodesId),
                    Centerline = new List<Vector3>(axisEdge.Centerline),
                    Length = axisEdge.Length,
                    Lanes = new List<Lane>()
                });
            }

            int removedDeadEnds = DeadEndPruner.Prune(FullNetwork);
            if (removedDeadEnds > 0)
                Debug.Log($"[NetworkManager] Dropped {removedDeadEnds} dead-end edge(s) before lane generation.");

            // 3. Compute intersection trim distances (requires FullNetwork.Edges to be populated).
            var edgeTrimMap = new Dictionary<string, float>();
            CalculateIntersectionTrims(edgeWidthMap, edgeTrimMap);

            // 4. Generate lane centerlines using trimmed endpoints.
            foreach (Edge fullEdge in FullNetwork.Edges)
            {
                if (!edgeLaneCount.TryGetValue(fullEdge.EdgeId, out int laneCount))
                    continue;

                float startTrim = 0f, endTrim = 0f;
                string startKey = $"{fullEdge.LinkNodesId[0]}_{fullEdge.EdgeId}";
                string endKey = $"{fullEdge.LinkNodesId[fullEdge.LinkNodesId.Count - 1]}_{fullEdge.EdgeId}";
                if (edgeTrimMap.ContainsKey(startKey)) startTrim = edgeTrimMap[startKey];
                if (edgeTrimMap.ContainsKey(endKey)) endTrim = edgeTrimMap[endKey];

                GenerateLaneCenterlines(fullEdge, laneCount, startTrim, endTrim);
            }

            // 5. Build lane connection relationships.
            BuildLaneConnections();

            // 5.5 Auto-assign lane types (Straight/Left/Right) based on intersection geometry.
            AssignLaneTypes();

            // 5.6 Keep explicit inter-layer links in sync with regenerated lane geometry.
            // Connectors whose owning lane disappeared are removed; surviving links are
            // rebuilt as tangent cubic Bezier curves.
            FullNetwork.LaneConnectionCorridors = retainedLayerConnectors;
            SynchronizeLaneConnectionCorridors();

            // 6. Export full road network.
            ExportFullNetworkToJSON();

            Debug.Log($"[NetworkManager] Full road network generation complete: {FullNetwork.Nodes.Count} nodes, {FullNetwork.Edges.Count} edges");
        }

        /// <summary>
        /// Sample collision checks along the axis centerline; return the maximum number of lanes this edge can accommodate.
        /// </summary>
        private int CalculateMaxLaneCount(List<Vector3> centerline)
        {
            int bestLaneCount = 0;

            for (int laneCount = 0; laneCount <= maxLaneCount; laneCount += 2)
            {
                if (IsRoadBlockedAlongCenterline(centerline, laneCount))
                    break;
                bestLaneCount = laneCount;
            }

            if (bestLaneCount < minLaneCount)
                return 0;
            return bestLaneCount;
        }

        /// <summary>
        /// Sample along the centerline: check whether the road width for a given lane count collides with buildings.
        /// </summary>
        private bool IsRoadBlockedAlongCenterline(List<Vector3> centerline, int laneCount)
        {
            if (laneCount <= 0) return false;

            float totalWidth = laneCount * laneWidth;
            float halfWidth = totalWidth * 0.5f;
            float halfHeight = collisionCheckHeight;

            for (int i = 0; i < centerline.Count - 1; i++)
            {
                Vector3 segStart = centerline[i];
                Vector3 segEnd = centerline[i + 1];
                Vector3 segDir = segEnd - segStart;
                float segLength = segDir.magnitude;
                if (segLength < 0.1f) continue;

                Vector3 segCenter = (segStart + segEnd) * 0.5f;
                Quaternion orientation = Quaternion.LookRotation(segDir);

                bool hit = Physics.CheckBox(
                    segCenter,
                    new Vector3(halfWidth, halfHeight, segLength * 0.5f),
                    orientation,
                    buildingLayerMask,
                    QueryTriggerInteraction.Ignore
                );

                if (hit) return true;
            }

            return false;
        }

        /// <summary>
        /// Generate offset lane centerlines for a single edge.
        /// Matches reference implementation: clip the raw centerline first, then offset.
        /// </summary>
        private void GenerateLaneCenterlines(Edge edge, int laneCount, float startTrim, float endTrim)
        {
            var centerline = edge.Centerline;
            if (centerline.Count < 2) return;

            // 1. Clip the raw centerline.
            List<Vector3> clippedRaw = ClipCenterlineByTrim(centerline, startTrim, endTrim);

            int lanesPerDirection = laneCount / 2;

            for (int i = 0; i < lanesPerDirection; i++)
            {
                float offset = (i + 0.5f) * laneWidth;

                // Forward lanes: start to end, offset to the right.
                Lane fwdLane = new Lane
                {
                    LaneId = $"{edge.EdgeId}_fwd_{i}",
                    LayerId = edge.LayerId,
                    Centerline = OffsetCenterline(clippedRaw, offset),
                    FromLanesId = new List<string>(),
                    ToLanesId = new List<string>(),
                    Length = 0f
                };
                fwdLane.Length = CalculateCenterlineLength(fwdLane.Centerline);
                edge.Lanes.Add(fwdLane);

                // Backward lanes: end to start, offset to the left then reverse point order.
                List<Vector3> bwdPoints = OffsetCenterline(clippedRaw, -offset);
                bwdPoints.Reverse();
                Lane bwdLane = new Lane
                {
                    LaneId = $"{edge.EdgeId}_bwd_{i}",
                    LayerId = edge.LayerId,
                    Centerline = bwdPoints,
                    FromLanesId = new List<string>(),
                    ToLanesId = new List<string>(),
                    Length = 0f
                };
                bwdLane.Length = CalculateCenterlineLength(bwdLane.Centerline);
                edge.Lanes.Add(bwdLane);
            }
        }

        /// <summary>
        /// Clip the raw centerline by trim distances, using the local direction at the node.
        /// visualStart = P0 + startDir*startTrim, visualEnd = Pn + endDir*endTrim
        /// </summary>
        private List<Vector3> ClipCenterlineByTrim(List<Vector3> centerline, float startTrim, float endTrim)
        {
            Vector3 startPos = centerline[0];
            Vector3 endPos = centerline[centerline.Count - 1];

            // Local direction: use the first segment for start, reverse of the last segment for end (direction away from node).
            Vector3 startDir = (centerline[1] - centerline[0]).normalized;
            Vector3 endDir = (centerline[centerline.Count - 2] - centerline[centerline.Count - 1]).normalized;

            // Degeneracy check: use straight-line distance between endpoints as an approximate upper bound.
            float straightLen = Vector3.Distance(startPos, endPos);
            if (startTrim + endTrim >= straightLen)
            {
                Vector3 mid = (startPos + endPos) * 0.5f;
                return new List<Vector3> { mid, mid };
            }

            List<Vector3> result = new List<Vector3>();

            // Trim from start.
            result.Add(startPos + startDir * startTrim);

            // Keep intermediate waypoints: use cumulative arc length to test if outside trim range.
            float[] cumDist = new float[centerline.Count];
            cumDist[0] = 0f;
            for (int i = 1; i < centerline.Count; i++)
                cumDist[i] = cumDist[i - 1] + Vector3.Distance(centerline[i - 1], centerline[i]);
            float totalArcLen = cumDist[centerline.Count - 1];

            float endArcCut = totalArcLen - endTrim;
            for (int i = 1; i < centerline.Count - 1; i++)
            {
                if (cumDist[i] > startTrim && cumDist[i] < endArcCut)
                    result.Add(centerline[i]);
            }

            // Trim from end.
            result.Add(endPos + endDir * endTrim);
            // Note: endDir = (Pn-1 - Pn), so endPos + endDir*endTrim = Pn + (Pn-1-Pn).normalized * endTrim.
            // i.e. move from Pn toward Pn-1 by endTrim.

            return result;
        }

        /// <summary>
        /// Offset the centerline perpendicularly by a given distance to produce a new polyline.
        /// </summary>
        private List<Vector3> OffsetCenterline(List<Vector3> points, float offset)
        {
            List<Vector3> result = new List<Vector3>();

            for (int i = 0; i < points.Count; i++)
            {
                Vector3 dir = GetDirectionAtPoint(points, i);
                Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
                result.Add(points[i] + right * offset);
            }

            return result;
        }

        /// <summary>
        /// Get the tangent direction at a point along the centerline.
        /// </summary>
        private Vector3 GetDirectionAtPoint(List<Vector3> points, int index)
        {
            if (points.Count == 1) return Vector3.forward;

            if (index == 0)
                return (points[1] - points[0]).normalized;

            if (index == points.Count - 1)
                return (points[points.Count - 1] - points[points.Count - 2]).normalized;

            Vector3 incoming = (points[index] - points[index - 1]).normalized;
            Vector3 outgoing = (points[index + 1] - points[index]).normalized;
            Vector3 avg = (incoming + outgoing).normalized;

            return avg.sqrMagnitude > 0.0001f ? avg : incoming;
        }

        private float CalculateCenterlineLength(List<Vector3> points)
        {
            float length = 0f;
            for (int i = 0; i < points.Count - 1; i++)
                length += Vector3.Distance(points[i], points[i + 1]);
            return length;
        }

        /// <summary>
        /// Compute intersection trim distances (prevents overlapping of adjacent-edge lanes at intersections).
        /// </summary>
        private void CalculateIntersectionTrims(Dictionary<string, float> edgeWidthMap,Dictionary<string, float> edgeTrimMap)
{
    _intersectionPolygons.Clear();

    foreach (var node in FullNetwork.Nodes)
    {
        if (node.ConnectedEdges.Count < 2) continue;

        // Get edges connected to this node that passed collision checks, sorted by azimuth.
        var sortedEdges = node.ConnectedEdges
            .Select(edgeId =>
            {
                Edge edge = FullNetwork.Edges.Find(e => e.EdgeId == edgeId);
                if (edge == null || !edgeWidthMap.ContainsKey(edgeId))
                    return null;
                Vector3 dir = GetLocalDirectionFromNode(edge, node.Position);
                Vector3 otherEnd = GetOtherEndPoint(edge, node.Position);
                float halfWidth = edgeWidthMap[edge.EdgeId] * 0.5f;
                return new { Edge = edge, Dir = dir, HalfWidth = halfWidth, OtherPos = otherEnd };
            })
            .Where(x => x != null)
            .OrderBy(x => -Mathf.Atan2(x.Dir.z, x.Dir.x))
            .ToList();

        if (sortedEdges.Count < 2) continue;

        int count = sortedEdges.Count;
        List<Vector3> thisCrossingCorners = new List<Vector3>();
        float[] finalTrimDists = new float[count];

        for (int i = 0; i < count; i++)
        {
            var current = sortedEdges[i];
            var next = sortedEdges[(i + 1) % count];
            var prev = sortedEdges[(i - 1 + count) % count];

            float roadLen = Vector3.Distance(node.Position, current.OtherPos);

            Vector3 currRightDir = Quaternion.Euler(0, 90, 0) * current.Dir;
            Vector3 currentRightEdgeOrigin = node.Position + currRightDir * Mathf.Max(current.HalfWidth, next.HalfWidth);
            Vector3 nextLeftDir = Quaternion.Euler(0, -90, 0) * next.Dir;
            Vector3 nextLeftEdgeOrigin = node.Position + nextLeftDir * Mathf.Max(current.HalfWidth, next.HalfWidth);

            Vector3 rightIntersection;
            bool hasRightInt = GetLineIntersection(
                currentRightEdgeOrigin, current.Dir,
                nextLeftEdgeOrigin, next.Dir,
                out rightIntersection
            );

            Vector3 currLeftDir = Quaternion.Euler(0, -90, 0) * current.Dir;
            Vector3 currentLeftEdgeOrigin = node.Position + currLeftDir * Mathf.Max(current.HalfWidth, prev.HalfWidth);
            Vector3 prevRightDir = Quaternion.Euler(0, 90, 0) * prev.Dir;
            Vector3 prevRightEdgeOrigin = node.Position + prevRightDir * Mathf.Max(current.HalfWidth, prev.HalfWidth);

            Vector3 leftIntersection;
            bool hasLeftInt = GetLineIntersection(
                currentLeftEdgeOrigin, current.Dir,
                prevRightEdgeOrigin, prev.Dir,
                out leftIntersection
            );

            float trimDist = 0f;
            if (hasRightInt)
            {
                float dist = Vector3.Dot(rightIntersection - node.Position, current.Dir);
                if (dist > 0 && dist < roadLen)
                {
                    trimDist = Mathf.Max(trimDist, dist);
                }
            }
            if (hasLeftInt)
            {
                float dist = Vector3.Dot(leftIntersection - node.Position, current.Dir);
                if (dist > 0 && dist < roadLen)
                {
                    trimDist = Mathf.Max(trimDist, dist);
                }
            }

            trimDist = Mathf.Min(trimDist, roadLen * 0.45f);
            finalTrimDists[i] = trimDist;
            edgeTrimMap[$"{node.NodeId}_{current.Edge.EdgeId}"] = trimDist;
        }

        // Collect intersection polygon vertices (two vertices per adjacent edge pair).
        for (int i = 0; i < count; i++)
        {
            var current = sortedEdges[i];
            int nextIndex = (i + 1) % count;
            var next = sortedEdges[nextIndex];

            float currentDist = finalTrimDists[i];
            float nextDist = finalTrimDists[nextIndex];

            Vector3 currRightDir = Quaternion.Euler(0, 90, 0) * current.Dir;
            Vector3 currentCutRightPoint = node.Position
                                         + current.Dir * currentDist
                                         + currRightDir * current.HalfWidth;
            thisCrossingCorners.Add(currentCutRightPoint);

            Vector3 nextLeftDir = Quaternion.Euler(0, -90, 0) * next.Dir;
            Vector3 nextCutLeftPoint = node.Position
                                     + next.Dir * nextDist
                                     + nextLeftDir * next.HalfWidth;
            thisCrossingCorners.Add(nextCutLeftPoint);
        }

        if (thisCrossingCorners.Count >= 3)
            _intersectionPolygons.Add(thisCrossingCorners);
    }
}

        /// <summary>
        /// Build lane-level connection relationships (FromLanesId / ToLanesId).
        /// </summary>
        private void BuildLaneConnections()
        {
            var edgeMap = new Dictionary<string, Edge>();
            foreach (var edge in FullNetwork.Edges)
                edgeMap[edge.EdgeId] = edge;

            var nodeDict = new Dictionary<string, Node>();
            foreach (var node in FullNetwork.Nodes)
                nodeDict[node.NodeId] = node;

            foreach (var node in FullNetwork.Nodes)
            {
                if (node.ConnectedEdges.Count < 2) continue;

                // Get edges connected to this node (present in FullNetwork and having lanes).
                var connectedEdges = new List<Edge>();
                foreach (string edgeId in node.ConnectedEdges)
                {
                    if (edgeMap.TryGetValue(edgeId, out Edge e) && e.Lanes.Count > 0)
                        connectedEdges.Add(e);
                }

                if (connectedEdges.Count < 2) continue;

                // Sort by azimuth (clockwise).
                connectedEdges.Sort((a, b) =>
                {
                    Vector3 dirA = GetDirAwayFromNode(a, node.Position);
                    Vector3 dirB = GetDirAwayFromNode(b, node.Position);
                    float angA = Vector3.SignedAngle(Vector3.forward, dirA, Vector3.up);
                    if (angA < 0) angA += 360f;
                    float angB = Vector3.SignedAngle(Vector3.forward, dirB, Vector3.up);
                    if (angB < 0) angB += 360f;
                    return angB.CompareTo(angA);
                });

                int edgeCount = connectedEdges.Count;

                for (int i = 0; i < edgeCount; i++)
                {
                    Edge currEdge = connectedEdges[i];
                    List<Lane> incoming = GetSortedLanes(currEdge, node.Position, isIncoming: true);
                    if (incoming.Count == 0) continue;

                    if (edgeCount == 2)
                    {
                        Edge otherEdge = connectedEdges[(i + 1) % edgeCount];
                        List<Lane> outgoing = GetSortedLanes(otherEdge, node.Position, isIncoming: false);
                        ConnectSequence(incoming, outgoing, false, false);
                    }
                    else if (edgeCount == 3)
                    {
                        Handle3WayConnection(connectedEdges, i, incoming, node.Position);
                    }
                    else
                    {
                        // 4+ edges: straight + left + right
                        // Dedicated right-turn when 3+ incoming lanes; shared with straight when 2
                        // Dedicated left-turn when 3+ incoming lanes; shared with straight when 2
                        bool dedicatedRight = incoming.Count >= 3;
                        bool dedicatedLeft  = incoming.Count >= 3;

                        // Straight: edges from (i+2) to (i+edgeCount-2)
                        for (int k = 2; k < edgeCount - 1; k++)
                        {
                            Edge straightEdge = connectedEdges[(i + k) % edgeCount];
                            List<Lane> straightOut = GetSortedLanes(straightEdge, node.Position, isIncoming: false);
                            ConnectSequence(incoming, straightOut, dedicatedLeft, dedicatedRight);
                        }

                        // Left: only the leftmost lane (dedicated) unless forced to share
                        Edge leftEdge = connectedEdges[(i - 1 + edgeCount) % edgeCount];
                        List<Lane> leftOut = GetSortedLanes(leftEdge, node.Position, isIncoming: false);
                        if (leftOut.Count > 0 && incoming.Count > 0)
                        {
                            if (dedicatedLeft)
                                ConnectOneToOne(incoming[0], leftOut[0]);
                            else
                                for (int j = 0; j < incoming.Count; j++)
                                {
                                    if (j == incoming.Count - 1 && j != 0) break;
                                    int target = Mathf.Min(j, leftOut.Count - 1);
                                    ConnectOneToOne(incoming[j], leftOut[target]);
                                }
                        }

                        // Right: only the rightmost lane (dedicated) unless forced to share
                        Edge rightEdge = connectedEdges[(i + 1) % edgeCount];
                        List<Lane> rightOut = GetSortedLanes(rightEdge, node.Position, isIncoming: false);
                        if (incoming.Count > 0 && rightOut.Count > 0)
                            ConnectOneToOne(incoming[incoming.Count - 1], rightOut[rightOut.Count - 1]);
                    }
                }
            }
        }

        /// <summary>
        /// Assign LaneType (Straight/Left/Right) based on polar-angle edge ordering at intersections.
        /// Uses the tangent direction at the node (not the full edge direction).
        /// Matches NetworkGenerator.BuildLaneConnections logic.
        /// </summary>
        private void AssignLaneTypes()
        {
            // Default all to Straight
            foreach (var lane in FullNetwork.Edges.SelectMany(e => e.Lanes))
                lane.LaneType = "Straight";

            var edgeMap = FullNetwork.Edges.ToDictionary(e => e.EdgeId, e => e);

            foreach (var node in FullNetwork.Nodes)
            {
                var connectedEdges = node.ConnectedEdges
                    .Select(eid => edgeMap.GetValueOrDefault(eid))
                    .Where(e => e != null).ToList();
                if (connectedEdges.Count < 2) continue;

                Vector3 pivotPos = node.Position;

                // Sort edges clockwise by tangent direction AT the node
                connectedEdges.Sort((edgeA, edgeB) =>
                {
                    Vector3 dirA = GetDirAwayFromNode(edgeA, pivotPos);
                    Vector3 dirB = GetDirAwayFromNode(edgeB, pivotPos);
                    float angleA = Vector3.SignedAngle(Vector3.forward, dirA, Vector3.up);
                    if (angleA < 0) angleA += 360f;
                    float angleB = Vector3.SignedAngle(Vector3.forward, dirB, Vector3.up);
                    if (angleB < 0) angleB += 360f;
                    return angleB.CompareTo(angleA); // clockwise
                });

                int edgeCount = connectedEdges.Count;

                for (int i = 0; i < edgeCount; i++)
                {
                    Edge inEdge = connectedEdges[i];
                    var incoming = GetSortedLanes(inEdge, pivotPos, isIncoming: true);
                    if (incoming.Count == 0) continue;

                    if (edgeCount == 2)
                    {
                        // Straight road: all lanes go straight to the opposite edge
                        int si = (i + 1) % edgeCount;
                        Edge straightEdge = connectedEdges[si];
                        var straightOut = GetSortedLanes(straightEdge, pivotPos, isIncoming: false);
                        AssignLaneTypeToLanes(incoming, straightOut, "Straight");
                        continue;
                    }

                    if (edgeCount == 3)
                    {
                        int si = (i + 1) % edgeCount;
                        Edge e1 = connectedEdges[si];
                        var out1 = GetSortedLanes(e1, pivotPos, isIncoming: false);
                        if (out1.Count == 0) continue;

                        Vector3 inDir = GetAvgLaneDirection(incoming);
                        Vector3 dir1 = GetAvgLaneDirection(out1);

                        if (Vector3.Dot(inDir, dir1) >= 0.9f)
                        {
                            // e1 goes same compass direction → straight road
                            AssignLaneTypeToLanes(incoming, out1, "Straight");
                            int li = (i + 2) % edgeCount;
                            Edge leftEdge = connectedEdges[li];
                            var leftOut = GetSortedLanes(leftEdge, pivotPos, isIncoming: false);
                            AssignLaneTypeToLanes(incoming, leftOut, "Left");
                        }
                        else
                        {
                            // e1 is right neighbor (clockwise)
                            AssignLaneTypeToLanes(incoming, out1, "Right");
                            si = (i + 2) % edgeCount;
                            Edge e2 = connectedEdges[si];
                            var out2 = GetSortedLanes(e2, pivotPos, isIncoming: false);
                            if (out2.Count == 0) continue;
                            Vector3 dir2 = GetDirAwayFromNode(e2, pivotPos);
                            if (Vector3.Dot(inDir, dir2) >= 0.9f)
                                AssignLaneTypeToLanes(incoming, out2, "Straight");
                            else
                                AssignLaneTypeToLanes(incoming, out2, "Left");
                        }
                        continue;
                    }

                    // 4+ edges
                    if (edgeCount >= 4)
                    {
                        // Right neighbor: (i + 1)
                        int ri = (i + 1) % edgeCount;
                        Edge rightEdge = connectedEdges[ri];
                        var rightOut = GetSortedLanes(rightEdge, pivotPos, isIncoming: false);
                        AssignLaneTypeToLanes(incoming, rightOut, "Right");

                        // Left neighbor: (i - 1)
                        int li = (i - 1 + edgeCount) % edgeCount;
                        Edge leftEdge = connectedEdges[li];
                        var leftOut = GetSortedLanes(leftEdge, pivotPos, isIncoming: false);
                        AssignLaneTypeToLanes(incoming, leftOut, "Left");

                        // Straight: edges from (i+2) to (i+edgeCount-2)
                        for (int k = 2; k < edgeCount - 1; k++)
                        {
                            int si = (i + k) % edgeCount;
                            Edge straightEdge = connectedEdges[si];
                            var straightOut = GetSortedLanes(straightEdge, pivotPos, isIncoming: false);
                            AssignLaneTypeToLanes(incoming, straightOut, "Straight");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Assign LaneType to incoming lanes that connect to specific outgoing lanes.
        /// Uses AddLaneType (cumulative: Straight+Left=StraightLeft, etc.)
        /// </summary>
        private void AssignLaneTypeToLanes(List<Lane> incoming, List<Lane> outgoing, string connectionType)
        {
            if (outgoing.Count == 0) return;
            foreach (var inLane in incoming)
            {
                if (inLane.ToLanesId == null) continue;
                foreach (var toId in inLane.ToLanesId)
                {
                    if (outgoing.Any(o => o.LaneId == toId))
                    {
                        AddLaneType(inLane, connectionType);
                        break;
                    }
                }
            }
        }

        private void AddLaneType(Lane lane, string newType)
        {
            if (string.IsNullOrEmpty(lane.LaneType) || lane.LaneType == "Straight")
            {
                lane.LaneType = newType;
                return;
            }
            if (lane.LaneType.Contains(newType)) return;
            if (newType == "Straight")
                lane.LaneType = "Straight" + lane.LaneType;
            else
                lane.LaneType = lane.LaneType + newType;
        }

        private void Handle3WayConnection(List<Edge> edges, int i, List<Lane> incoming, Vector3 nodePos)
        {
            int edgeCount = edges.Count;

            Edge edge1 = edges[(i + 1) % edgeCount];
            List<Lane> out1 = GetSortedLanes(edge1, nodePos, isIncoming: false);

            if (out1.Count == 0) return;

            Vector3 inDir = (incoming[0].Centerline[incoming[0].Centerline.Count - 1]
                - incoming[0].Centerline[0]).normalized;
            Vector3 dir1 = (out1[0].Centerline[out1[0].Centerline.Count - 1]
                - out1[0].Centerline[0]).normalized;

            bool hasDedicated = incoming.Count >= 3;

            if (Vector3.Dot(inDir, dir1) >= 0.9f)
            {
                // e1 is opposite → straight + left. No separate right-turn edge exists.
                ConnectSequence(incoming, out1, hasDedicated, false);
                Edge edge2 = edges[(i + 2) % edgeCount];
                List<Lane> out2 = GetSortedLanes(edge2, nodePos, isIncoming: false);
                if (out2.Count > 0 && incoming.Count > 0)
                    ConnectOneToOne(incoming[0], out2[0]);
            }
            else
            {
                // e1 is right neighbor → right-turn edge exists
                if (incoming.Count > 0 && out1.Count > 0)
                    ConnectOneToOne(incoming[incoming.Count - 1], out1[out1.Count - 1]);

                Edge edge2 = edges[(i + 2) % edgeCount];
                List<Lane> out2 = GetSortedLanes(edge2, nodePos, isIncoming: false);
                if (out2.Count == 0) return;
                Vector3 dir2 = (out2[0].Centerline[out2[0].Centerline.Count - 1]
                    - out2[0].Centerline[0]).normalized;

                if (Vector3.Dot(inDir, dir2) >= 0.9f)
                {
                    // e2 is straight → no separate left edge. Reserve rightmost only.
                    ConnectSequence(incoming, out2, false, hasDedicated);
                }
                else
                {
                    // e2 is left → both left and right edges exist
                    ConnectSequence(incoming, out2, hasDedicated, hasDedicated);
                    if (out2.Count > 0 && incoming.Count > 0)
                        ConnectOneToOne(incoming[0], out2[0]);
                }
            }
        }

        #region Lane Connection Helpers

        /// <summary>Average direction of the given lanes (start→end), matching reference script.</summary>
        private Vector3 GetAvgLaneDirection(List<Lane> lanes)
        {
            Vector3 sum = Vector3.zero;
            foreach (var lane in lanes)
            {
                if (lane.Centerline == null || lane.Centerline.Count < 2) continue;
                sum += (lane.Centerline[lane.Centerline.Count - 1] - lane.Centerline[0]).normalized;
            }
            return sum.normalized;
        }

        private Vector3 GetDirAwayFromNode(Edge edge, Vector3 nodePos)
        {
            return GetLocalDirectionFromNode(edge, nodePos);
        }

        /// <summary>
        /// Get the local direction of an edge at a given node (direction away from the node along the adjacent centerline segment).
        private Vector3 GetLocalDirectionFromNode(Edge edge, Vector3 nodePos)
        {
            var cl = edge.Centerline;
            if (cl.Count < 2) return Vector3.forward;

            float dFirst = Vector3.Distance(cl[0], nodePos);
            float dLast = Vector3.Distance(cl[cl.Count - 1], nodePos);

            if (dFirst <= dLast)
                return (cl[1] - cl[0]).normalized;
            else
                return (cl[cl.Count - 2] - cl[cl.Count - 1]).normalized;
        }

        private Vector3 GetOtherEndPoint(Edge edge, Vector3 nodePos)
        {
            Vector3 p1 = edge.Centerline[0];
            Vector3 p2 = edge.Centerline[edge.Centerline.Count - 1];
            return Vector3.Distance(p1, nodePos) < Vector3.Distance(p2, nodePos) ? p2 : p1;
        }

        private List<Lane> GetSortedLanes(Edge edge, Vector3 nodePos, bool isIncoming)
        {
            List<Lane> result = new List<Lane>();
            foreach (var lane in edge.Lanes)
            {
                if (lane.Centerline.Count < 2) continue;
                float dStart = Vector3.Distance(lane.Centerline[0], nodePos);
                float dEnd = Vector3.Distance(lane.Centerline[lane.Centerline.Count - 1], nodePos);
                bool flowsIntoNode = dEnd < dStart;

                if (isIncoming && flowsIntoNode)
                    result.Add(lane);
                else if (!isIncoming && !flowsIntoNode)
                    result.Add(lane);
            }
            return result;
        }

        private void ConnectOneToOne(Lane from, Lane to)
        {
            if (!from.ToLanesId.Contains(to.LaneId))
                from.ToLanesId.Add(to.LaneId);
            if (!to.FromLanesId.Contains(from.LaneId))
                to.FromLanesId.Add(from.LaneId);
        }

        private void ConnectSequence(List<Lane> fromLanes, List<Lane> toLanes,
            bool skipFirst, bool skipLast)
        {
            if (toLanes.Count == 0) return;

            for (int i = 0; i < fromLanes.Count; i++)
            {
                if (i == 0 && fromLanes.Count != 1 && skipFirst) continue;
                if (i == fromLanes.Count - 1 && fromLanes.Count != 1 && skipLast) continue;
                int target = Mathf.Min(i, toLanes.Count - 1);
                ConnectOneToOne(fromLanes[i], toLanes[target]);
            }
        }

        private bool GetLineIntersection(Vector3 p1, Vector3 d1, Vector3 p2, Vector3 d2,
            out Vector3 intersection)
        {
            intersection = Vector3.zero;
            double det = d1.x * d2.z - d1.z * d2.x;
            if (System.Math.Abs(det) < 1e-5) return false;
            double t = ((p2.x - p1.x) * d2.z - (p2.z - p1.z) * d2.x) / det;
            intersection = p1 + d1 * (float)t;
            intersection.y = p1.y;
            return true;
        }

        #endregion

        /// <summary>Persist the editable multilayer axis network, including layers and edge waypoints.</summary>
        public void ExportAxisNetworkToJSON()
        {
            MultilayerNetworkUtility.NormalizeAxisLayerMembership(AxisNetwork);
            string outputPath = DEFAULT_JSON_PATH;
#if !UNITY_EDITOR
            outputPath = Path.Combine(SarRuntimePaths.NetworkRoot, "AxisNetwork.json");
#endif
            string directory = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                Converters = new List<JsonConverter> { new Vector3Converter() }
            };
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(AxisNetwork, settings));
#if UNITY_EDITOR
            AssetDatabase.Refresh();
#endif
            Debug.Log($"[NetworkManager] Axis network exported to: {outputPath}");
        }

        /// <summary>Export the editable axis network to a caller-selected path.</summary>
        public string ExportAxisNetworkToJSON(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new System.ArgumentException("Output path is required.", nameof(outputPath));
            string path = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            MultilayerNetworkUtility.NormalizeAxisLayerMembership(AxisNetwork);
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                Converters = new List<JsonConverter> { new Vector3Converter() }
            };
            File.WriteAllText(path, JsonConvert.SerializeObject(AxisNetwork, settings));
            Debug.Log($"[NetworkManager] Axis network exported to: {path}");
            return path;
        }

        /// <summary>
        /// Export the full road network to a JSON file.
        /// </summary>
        public void ExportFullNetworkToJSON()
        {
            NetworkValidationResult validation =
                MultilayerNetworkUtility.NormalizeAndValidate(FullNetwork);
            if (!validation.IsValid)
            {
                foreach (string error in validation.Errors)
                    Debug.LogError($"[NetworkManager] Export validation: {error}");
                Debug.LogError("[NetworkManager] Full network export cancelled because validation failed.");
                return;
            }

            string outputPath = "Assets/DataBase/FullNetwork.json";
#if !UNITY_EDITOR
            outputPath = Path.Combine(SarRuntimePaths.NetworkRoot, "FullNetwork.json");
#endif
            string directory = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                Converters = new List<JsonConverter> { new Vector3Converter() }
            };

            string json = JsonConvert.SerializeObject(FullNetwork, settings);
            File.WriteAllText(outputPath, json);

#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
            Debug.Log($"[NetworkManager] Full road network exported to: {outputPath}");
        }

        private void ClearFullNetwork()
        {
            FullNetwork.Nodes.Clear();
            FullNetwork.Edges.Clear();
            FullNetwork.AltitudeLayers.Clear();
            FullNetwork.LaneConnectionCorridors.Clear();
        }

        private static bool TryConsolidateAltitudeLayers(Network network, out string error)
        {
            error = null;
            if (network == null) { error = "The network data is empty."; return false; }
            network.AltitudeLayers = network.AltitudeLayers ?? new List<AltitudeLayer>();
            var consolidated = new List<AltitudeLayer>();
            foreach (IGrouping<string, AltitudeLayer> group in network.AltitudeLayers
                         .Where(layer => layer != null)
                         .GroupBy(layer => string.IsNullOrWhiteSpace(layer.LayerId) ? string.Empty : layer.LayerId))
            {
                List<AltitudeLayer> values = group.ToList();
                AltitudeLayer first = values[0];
                if (values.Count > 1 && values.Any(layer =>
                        !Mathf.Approximately(layer.NominalAltitude, first.NominalAltitude) ||
                        !Mathf.Approximately(layer.VerticalTolerance, first.VerticalTolerance) ||
                        !string.Equals(layer.DisplayName, first.DisplayName, System.StringComparison.Ordinal) ||
                        layer.Enabled != first.Enabled || layer.Frozen != first.Frozen))
                {
                    error = $"Altitude-layer ID \"{group.Key}\" is duplicated with inconsistent parameters.";
                    return false;
                }
                consolidated.Add(first);
            }
            network.AltitudeLayers = consolidated;
            return true;
        }

        #endregion

        #region Utility

        public AltitudeLayer AddAltitudeLayer(
            string layerId,
            float nominalAltitude,
            float verticalTolerance = 5f)
        {
            if (FullNetwork == null) FullNetwork = new Network();
            MultilayerNetworkUtility.NormalizeAndValidate(FullNetwork);
            if (string.IsNullOrWhiteSpace(layerId) ||
                FullNetwork.AltitudeLayers.Any(layer => layer.LayerId == layerId))
            {
                Debug.LogError($"[NetworkManager] Invalid or duplicate altitude layer ID: '{layerId}'.");
                return null;
            }

            var layer = new AltitudeLayer
            {
                LayerId = layerId,
                DisplayName = layerId,
                NominalAltitude = nominalAltitude,
                VerticalTolerance = Mathf.Max(0f, verticalTolerance),
                Enabled = true,
                Frozen = false
            };
            FullNetwork.AltitudeLayers.Add(layer);
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
            return layer;
        }

        public LaneConnectionCorridor CreateLaneConnectionCorridor(
            string fromLaneId,
            string toLaneId,
            List<Vector3> centerline = null,
            string corridorId = null)
        {
            if (FullNetwork == null) FullNetwork = new Network();
            MultilayerNetworkUtility.NormalizeAndValidate(FullNetwork);

            Lane fromLane = FindFullNetworkLane(fromLaneId);
            Lane toLane = FindFullNetworkLane(toLaneId);
            if (fromLane == null || toLane == null || fromLaneId == toLaneId)
            {
                Debug.LogError($"[NetworkManager] Invalid lane connection: {fromLaneId} -> {toLaneId}.");
                return null;
            }
            if (fromLane.LayerId == toLane.LayerId)
            {
                Debug.LogError($"[NetworkManager] Layer connector endpoints must be on different layers: " +
                               $"{fromLaneId} and {toLaneId} are both on {fromLane.LayerId}.");
                return null;
            }

            var usedIds = new HashSet<string>(FullNetwork.Edges
                .Where(edge => edge?.Lanes != null)
                .SelectMany(edge => edge.Lanes)
                .Where(lane => lane != null)
                .Select(lane => lane.LaneId));
            foreach (LaneConnectionCorridor existing in FullNetwork.LaneConnectionCorridors)
                if (existing != null) usedIds.Add(existing.CorridorId);

            if (string.IsNullOrWhiteSpace(corridorId))
            {
                int index = 1;
                do corridorId = $"LC-{index++:D4}";
                while (usedIds.Contains(corridorId));
            }
            else if (usedIds.Contains(corridorId))
            {
                Debug.LogError($"[NetworkManager] Duplicate lane connection ID: {corridorId}.");
                return null;
            }

            List<Vector3> points = centerline != null
                ? new List<Vector3>(centerline)
                : new List<Vector3>();
            if (points.Count < 2)
            {
                if (fromLane.Centerline == null || fromLane.Centerline.Count == 0 ||
                    toLane.Centerline == null || toLane.Centerline.Count == 0)
                {
                    Debug.LogError("[NetworkManager] Both lanes need centerlines to create a connector.");
                    return null;
                }
                points = new List<Vector3>
                {
                    fromLane.Centerline[fromLane.Centerline.Count - 1],
                    toLane.Centerline[0]
                };
            }
            points = MultilayerNetworkUtility.BuildLaneConnectorBezier(
                fromLane, toLane, points[0], points[points.Count - 1]);
            if (points.Count < 2)
            {
                Debug.LogError("[NetworkManager] Failed to generate Bezier lane connector geometry.");
                return null;
            }

            var corridor = new LaneConnectionCorridor
            {
                CorridorId = corridorId,
                FromLaneId = fromLaneId,
                ToLaneId = toLaneId,
                FromLayerId = fromLane.LayerId,
                ToLayerId = toLane.LayerId,
                FromPoint = points[0],
                ToPoint = points[points.Count - 1],
                Centerline = points,
                Length = MultilayerNetworkUtility.CalculatePolylineLength(points),
                MaxSpeed = 20f,
                Priority = 1
            };

            FullNetwork.LaneConnectionCorridors.Add(corridor);
            NetworkValidationResult validation =
                MultilayerNetworkUtility.NormalizeAndValidate(FullNetwork);
            if (!validation.IsValid)
            {
                FullNetwork.LaneConnectionCorridors.Remove(corridor);
                Debug.LogError($"[NetworkManager] Lane connection rejected: " +
                               string.Join("; ", validation.Errors));
                return null;
            }
            UpsertAxisLayerConnector(corridor);

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
            SceneView.RepaintAll();
#endif
            Debug.Log($"[NetworkManager] Created layer connector {corridorId}: " +
                      $"{fromLaneId}({fromLane.LayerId}) -> {toLaneId}({toLane.LayerId}).");
            return corridor;
        }

        public bool UpdateLaneConnectionCorridorLink(
            string corridorId,
            bool moveFromPoint,
            Vector3 attachmentCandidate)
        {
            LaneConnectionCorridor corridor = FullNetwork?.LaneConnectionCorridors?
                .FirstOrDefault(item => item != null && item.CorridorId == corridorId);
            if (corridor == null) return false;
            Lane fromLane = FindFullNetworkLane(corridor.FromLaneId);
            Lane toLane = FindFullNetworkLane(corridor.ToLaneId);
            if (fromLane == null || toLane == null) return false;

            Vector3 fromPoint = moveFromPoint ? attachmentCandidate : corridor.FromPoint;
            Vector3 toPoint = moveFromPoint ? corridor.ToPoint : attachmentCandidate;
            List<Vector3> curve = MultilayerNetworkUtility.BuildLaneConnectorBezier(
                fromLane, toLane, fromPoint, toPoint);
            if (curve.Count < 2) return false;
            corridor.Centerline = curve;
            corridor.FromPoint = curve[0];
            corridor.ToPoint = curve[curve.Count - 1];
            corridor.Length = MultilayerNetworkUtility.CalculatePolylineLength(curve);
            UpsertAxisLayerConnector(corridor);
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
            SceneView.RepaintAll();
#endif
            return true;
        }

        public bool DeleteLaneConnectionCorridor(string corridorId)
        {
            if (FullNetwork?.LaneConnectionCorridors == null) return false;
            int removed = FullNetwork.LaneConnectionCorridors.RemoveAll(
                corridor => corridor != null && corridor.CorridorId == corridorId);
            if (AxisNetwork?.LaneConnectionCorridors != null)
                AxisNetwork.LaneConnectionCorridors.RemoveAll(
                    corridor => corridor != null && corridor.CorridorId == corridorId);
#if UNITY_EDITOR
            if (removed > 0)
            {
                EditorUtility.SetDirty(this);
                SceneView.RepaintAll();
            }
#endif
            return removed > 0;
        }

        private Lane FindFullNetworkLane(string laneId)
        {
            if (FullNetwork?.Edges == null) return null;
            foreach (Edge edge in FullNetwork.Edges)
            {
                Lane lane = edge?.Lanes?.FirstOrDefault(candidate => candidate.LaneId == laneId);
                if (lane != null) return lane;
            }
            return null;
        }

        private void SynchronizeLaneConnectionCorridors()
        {
            if (FullNetwork?.LaneConnectionCorridors == null) return;
            var lanes = FullNetwork.Edges
                .Where(edge => edge?.Lanes != null)
                .SelectMany(edge => edge.Lanes)
                .Where(lane => lane != null && !string.IsNullOrEmpty(lane.LaneId))
                .ToDictionary(lane => lane.LaneId, lane => lane);
            int removed = FullNetwork.LaneConnectionCorridors.RemoveAll(corridor =>
                corridor == null || !lanes.ContainsKey(corridor.FromLaneId ?? string.Empty) ||
                !lanes.ContainsKey(corridor.ToLaneId ?? string.Empty) ||
                lanes[corridor.FromLaneId].LayerId == lanes[corridor.ToLaneId].LayerId);
            foreach (LaneConnectionCorridor corridor in FullNetwork.LaneConnectionCorridors)
            {
                Lane fromLane = lanes[corridor.FromLaneId];
                Lane toLane = lanes[corridor.ToLaneId];
                Vector3 fromCandidate = corridor.Centerline != null && corridor.Centerline.Count > 1
                    ? corridor.Centerline[0] : corridor.FromPoint;
                Vector3 toCandidate = corridor.Centerline != null && corridor.Centerline.Count > 1
                    ? corridor.Centerline[corridor.Centerline.Count - 1] : corridor.ToPoint;
                List<Vector3> curve = MultilayerNetworkUtility.BuildLaneConnectorBezier(
                    fromLane, toLane, fromCandidate, toCandidate);
                if (curve.Count < 2) continue;
                corridor.FromLayerId = fromLane.LayerId;
                corridor.ToLayerId = toLane.LayerId;
                corridor.Centerline = curve;
                corridor.FromPoint = curve[0];
                corridor.ToPoint = curve[curve.Count - 1];
                corridor.Length = MultilayerNetworkUtility.CalculatePolylineLength(curve);
            }
            if (AxisNetwork != null)
            {
                AxisNetwork.LaneConnectionCorridors = FullNetwork.LaneConnectionCorridors
                    .Where(corridor => corridor != null)
                    .Select(MultilayerNetworkUtility.CloneLaneConnectionCorridor)
                    .ToList();
            }
            if (removed > 0)
                Debug.LogWarning($"[NetworkManager] Removed {removed} invalid layer connector(s) after lane regeneration.");
        }

        private void UpsertAxisLayerConnector(LaneConnectionCorridor corridor)
        {
            if (AxisNetwork == null || corridor == null) return;
            if (AxisNetwork.LaneConnectionCorridors == null)
                AxisNetwork.LaneConnectionCorridors = new List<LaneConnectionCorridor>();
            int index = AxisNetwork.LaneConnectionCorridors.FindIndex(item =>
                item != null && item.CorridorId == corridor.CorridorId);
            LaneConnectionCorridor copy = MultilayerNetworkUtility.CloneLaneConnectionCorridor(corridor);
            if (index >= 0) AxisNetwork.LaneConnectionCorridors[index] = copy;
            else AxisNetwork.LaneConnectionCorridors.Add(copy);
        }

        public void ClearAxisNetwork(bool isManualClear = true)
        {
            // Snapshot and clear dictionaries first to prevent DestroyImmediate from triggering OnDestroy callbacks that modify the collections.
            var oldNodeVisuals = new List<NodeVisual>(nodeVisuals.Values);
            var oldEdgeVisuals = new List<EdgeVisual>(edgeVisuals.Values);
            nodeVisuals.Clear();
            edgeVisuals.Clear();

            foreach (var nodeVisual in oldNodeVisuals)
            {
                if (nodeVisual != null)
                    DestroyImmediate(nodeVisual.gameObject);
            }

            foreach (var edgeVisual in oldEdgeVisuals)
            {
                if (edgeVisual != null)
                    DestroyImmediate(edgeVisual.gameObject);
            }

            AxisNetwork.Nodes.Clear();
            AxisNetwork.Edges.Clear();

            if (isManualClear)
                _manuallyCleared = true;

            ClearSelection();
        }

        public void RebuildVisualsFromData()
        {
            MultilayerNetworkUtility.NormalizeAxisLayerMembership(AxisNetwork);
            LoadPrefabs();
            if (nodesContainer == null || edgesContainer == null) InitializeContainers();
            // Snapshot and clear dictionaries first to prevent DestroyImmediate from triggering OnDestroy callbacks that modify the collections.
            var oldNodeVisuals = new List<NodeVisual>(nodeVisuals.Values);
            var oldEdgeVisuals = new List<EdgeVisual>(edgeVisuals.Values);
            nodeVisuals.Clear();
            edgeVisuals.Clear();

            // Clear existing visuals.
            foreach (var nodeVisual in oldNodeVisuals)
            {
                if (nodeVisual != null)
                    DestroyImmediate(nodeVisual.gameObject);
            }
            foreach (var edgeVisual in oldEdgeVisuals)
            {
                if (edgeVisual != null)
                    DestroyImmediate(edgeVisual.gameObject);
            }

            // Destroy all child objects under containers (keep the container itself to avoid null-reference errors in the Editor Inspector).
            if (nodesContainer != null)
            {
                for (int i = nodesContainer.childCount - 1; i >= 0; i--)
                    DestroyImmediate(nodesContainer.GetChild(i).gameObject);
            }
            if (edgesContainer != null)
            {
                for (int i = edgesContainer.childCount - 1; i >= 0; i--)
                    DestroyImmediate(edgesContainer.GetChild(i).gameObject);
            }

            if (AxisNetwork == null)
                return;

            // Rebuild node visuals.
            foreach (Node node in AxisNetwork.Nodes)
            {
                GameObject nodeObj = Instantiate(nodePrefab, node.Position, Quaternion.identity, nodesContainer);
                NodeVisual nodeVisual = nodeObj.GetComponent<NodeVisual>();
                if (nodeVisual == null)
                    nodeVisual = nodeObj.AddComponent<NodeVisual>();

                nodeVisual.Initialize(node);
                nodeVisuals[node.NodeId] = nodeVisual;
            }

            // Rebuild edge visuals.
            foreach (Edge edge in AxisNetwork.Edges)
            {
                if (edge.LinkNodesId != null && edge.LinkNodesId.Count >= 2)
                {
                    string startNodeId = edge.LinkNodesId[0];
                    string endNodeId = edge.LinkNodesId[1];

                    if (nodeVisuals.ContainsKey(startNodeId) && nodeVisuals.ContainsKey(endNodeId))
                    {
                        GameObject edgeObj = Instantiate(edgePrefab, Vector3.zero, Quaternion.identity, edgesContainer);
                        EdgeVisual edgeVisual = edgeObj.GetComponent<EdgeVisual>();
                        if (edgeVisual == null)
                            edgeVisual = edgeObj.AddComponent<EdgeVisual>();

                        edgeVisual.Initialize(edge, nodeVisuals[startNodeId], nodeVisuals[endNodeId]);
                        edgeVisuals[edge.EdgeId] = edgeVisual;
                    }
                }
            }

            Debug.Log($"Rebuilt visuals: {nodeVisuals.Count} nodes, {edgeVisuals.Count} edges");
        }

        private string GenerateNodeId()
        {
            int maxId = 0;
            foreach (Node node in AxisNetwork.Nodes)
            {
                if (node.NodeId.StartsWith("N"))
                {
                    if (int.TryParse(node.NodeId.Substring(1), out int id))
                    {
                        maxId = Mathf.Max(maxId, id);
                    }
                }
            }
            return $"N{maxId + 1}";
        }

        private string GenerateEdgeId()
        {
            int maxId = 0;
            foreach (Edge edge in AxisNetwork.Edges)
            {
                if (edge.EdgeId.StartsWith("E"))
                {
                    if (int.TryParse(edge.EdgeId.Substring(1), out int id))
                    {
                        maxId = Mathf.Max(maxId, id);
                    }
                }
            }
            return $"E{maxId + 1}";
        }

        /// <summary>
        /// Get the node visual object by node ID.
        /// </summary>
        public NodeVisual GetNodeVisual(string nodeId)
        {
            if (nodesContainer == null)
                return null;

            foreach (Transform child in nodesContainer)
            {
                NodeVisual nodeVisual = child.GetComponent<NodeVisual>();
                if (nodeVisual != null && nodeVisual.NodeId == nodeId)
                {
                    return nodeVisual;
                }
            }
            return null;
        }

        /// <summary>
        /// Get the edge visual object by edge ID.
        /// </summary>
        public EdgeVisual GetEdgeVisual(string edgeId)
        {
            if (edgesContainer == null)
                return null;

            foreach (Transform child in edgesContainer)
            {
                EdgeVisual edgeVisual = child.GetComponent<EdgeVisual>();
                if (edgeVisual != null && edgeVisual.EdgeId == edgeId)
                {
                    return edgeVisual;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns whether direct axis mutations are locked for the specified layer.
        /// Runtime and custom-editor callers share this guard so a frozen layer cannot
        /// be changed merely by entering through a lower-level NetworkManager API.
        /// </summary>
        public bool IsAxisLayerFrozen(string layerId)
        {
            return MultilayerNetworkUtility.FindLayer(AxisNetwork, layerId)?.Frozen == true;
        }

        private bool RejectFrozenAxisMutation(string layerId, string operation)
        {
            if (!IsAxisLayerFrozen(layerId)) return false;
            Debug.LogWarning($"[NetworkManager] Layer '{layerId}' is frozen; cannot {operation}.");
            return true;
        }

        #endregion

        #region Full Network Visualization

        // Intersection polygon vertices (for Gizmos drawing).
        private List<List<Vector3>> _intersectionPolygons = new List<List<Vector3>>();

        private bool _lastShowFullNetwork = false;
        private bool _runtimeAxisOverlayActive;
        private bool _lastRuntimeAxisOverlayActive;

        public void SetRuntimeAxisOverlayActive(bool active)
        {
            _runtimeAxisOverlayActive = active;
            ApplyFullNetworkVisibility();
            _lastRuntimeAxisOverlayActive = active;
        }

        private void Update()
        {
            if (showFullNetwork != _lastShowFullNetwork ||
                _runtimeAxisOverlayActive != _lastRuntimeAxisOverlayActive)
            {
                ApplyFullNetworkVisibility();
                _lastShowFullNetwork = showFullNetwork;
                _lastRuntimeAxisOverlayActive = _runtimeAxisOverlayActive;
            }
        }

        private void ApplyFullNetworkVisibility()
        {
            if (nodesContainer != null)
                nodesContainer.gameObject.SetActive(!showFullNetwork && !_runtimeAxisOverlayActive);
            if (edgesContainer != null)
                edgesContainer.gameObject.SetActive(!showFullNetwork && !_runtimeAxisOverlayActive);
        }

        private Color GetLaneGizmoColor(string laneType)
        {
            if (string.IsNullOrEmpty(laneType)) return Color.green;
            switch (laneType)
            {
                case "Straight":      return new Color(0.0f, 0.8f, 0.2f);  // green
                case "Left":          return new Color(0.3f, 0.5f, 1.0f);  // blue
                case "Right":         return new Color(1.0f, 0.6f, 0.0f);  // orange
                case "StraightLeft":  return new Color(0.0f, 0.7f, 0.7f);  // cyan
                case "StraightRight": return new Color(0.5f, 0.8f, 0.0f);  // yellow-green
                case "LeftRight":     return new Color(0.8f, 0.3f, 0.8f);  // purple
                case "StraightLeftRight": return new Color(0.8f, 0.8f, 0.2f); // yellow
                default:              return Color.green;
            }
        }

        private void OnDrawGizmos()
        {
            if (_runtimeAxisOverlayActive || !showFullNetwork || FullNetwork == null || FullNetwork.Edges.Count == 0)
                return;

            // Per-layer visibility is combined with the global showFullNetwork switch.
            // Missing layer metadata is treated as visible for legacy network files; only an
            // explicit Enabled=false layer suppresses its lanes and attached connections.
            var hiddenLayerIds = new HashSet<string>(
                (FullNetwork.AltitudeLayers ?? new List<AltitudeLayer>())
                .Where(layer => layer != null && !layer.Enabled &&
                                !string.IsNullOrWhiteSpace(layer.LayerId))
                .Select(layer => layer.LayerId),
                System.StringComparer.Ordinal);
            var laneDict = new Dictionary<string, Lane>();
            var laneVisibility = new Dictionary<string, bool>(System.StringComparer.Ordinal);
            foreach (var edge in FullNetwork.Edges)
            {
                if (edge?.Lanes == null) continue;
                foreach (var lane in edge.Lanes)
                {
                    if (lane == null || string.IsNullOrWhiteSpace(lane.LaneId)) continue;
                    bool visible = !hiddenLayerIds.Contains(edge.LayerId ?? string.Empty) &&
                                   !hiddenLayerIds.Contains(lane.LayerId ?? string.Empty);
                    laneVisibility[lane.LaneId] = visible;
                    if (visible) laneDict[lane.LaneId] = lane;
                }
            }

            // 1. Draw lane centerlines (polyline segments; not smoothed for now).
            foreach (var edge in FullNetwork.Edges)
            {
                if (edge?.Lanes == null || hiddenLayerIds.Contains(edge.LayerId ?? string.Empty))
                    continue;

                for (int i = 0; i < edge.Lanes.Count; i++)
                {
                    var lane = edge.Lanes[i];
                    if (lane == null || hiddenLayerIds.Contains(lane.LayerId ?? string.Empty) ||
                        lane.Centerline == null || lane.Centerline.Count < 2) continue;

                    Gizmos.color = GetLaneGizmoColor(lane.LaneType);

                    for (int j = 0; j < lane.Centerline.Count - 1; j++)
                        Gizmos.DrawLine(lane.Centerline[j], lane.Centerline[j + 1]);

                    // Draw direction arrow at lane endpoint.
                    Vector3 tip = lane.Centerline[lane.Centerline.Count - 1];
                    Vector3 from = lane.Centerline[lane.Centerline.Count - 2];
                    Vector3 laneDir = (tip - from).normalized;
                    Vector3 right = Vector3.Cross(Vector3.up, laneDir).normalized;
                    float arrowLen = 2f;
                    Gizmos.DrawLine(tip, tip - laneDir * arrowLen + right * arrowLen * 0.5f);
                    Gizmos.DrawLine(tip, tip - laneDir * arrowLen - right * arrowLen * 0.5f);
                }
            }

            // 2. Draw lane connection lines.
            Gizmos.color = Color.yellow;
            foreach (var edge in FullNetwork.Edges)
            {
                if (edge?.Lanes == null || hiddenLayerIds.Contains(edge.LayerId ?? string.Empty))
                    continue;
                foreach (var lane in edge.Lanes)
                {
                    if (lane == null || hiddenLayerIds.Contains(lane.LayerId ?? string.Empty) ||
                        lane.Centerline == null || lane.Centerline.Count < 2) continue;
                    Vector3 laneEnd = lane.Centerline[lane.Centerline.Count - 1];
                    foreach (string toLaneId in lane.ToLanesId ?? new List<string>())
                    {
                        if (laneDict.TryGetValue(toLaneId, out Lane toLane)
                            && toLane.Centerline.Count > 0)
                        {
                            Gizmos.DrawLine(laneEnd, toLane.Centerline[0]);
                        }
                    }
                }
            }

            // 3. Draw explicit lane-to-lane layer connectors as magenta 3D polylines.
            if (FullNetwork.LaneConnectionCorridors != null)
            {
                Gizmos.color = new Color(1f, 0.2f, 0.85f, 1f);
                foreach (LaneConnectionCorridor corridor in FullNetwork.LaneConnectionCorridors)
                {
                    if (corridor?.Centerline == null || corridor.Centerline.Count < 2) continue;
                    bool fromLaneVisible = !laneVisibility.TryGetValue(
                        corridor.FromLaneId ?? string.Empty, out bool fromVisible) || fromVisible;
                    bool toLaneVisible = !laneVisibility.TryGetValue(
                        corridor.ToLaneId ?? string.Empty, out bool toVisible) || toVisible;
                    if (hiddenLayerIds.Contains(corridor.FromLayerId ?? string.Empty) ||
                        hiddenLayerIds.Contains(corridor.ToLayerId ?? string.Empty) ||
                        !fromLaneVisible || !toLaneVisible) continue;
                    for (int i = 1; i < corridor.Centerline.Count; i++)
                        Gizmos.DrawLine(corridor.Centerline[i - 1], corridor.Centerline[i]);

                    Vector3 tip = corridor.Centerline[corridor.Centerline.Count - 1];
                    Vector3 direction = (tip - corridor.Centerline[corridor.Centerline.Count - 2]).normalized;
                    Vector3 side = Vector3.Cross(Vector3.up, direction).normalized;
                    if (side.sqrMagnitude < 0.001f) side = Vector3.right;
                    const float arrowLength = 2f;
                    Gizmos.DrawLine(tip, tip - direction * arrowLength + side * arrowLength * 0.5f);
                    Gizmos.DrawLine(tip, tip - direction * arrowLength - side * arrowLength * 0.5f);
                    Gizmos.DrawWireSphere(corridor.FromPoint, 0.5f);
                    Gizmos.DrawWireSphere(corridor.ToPoint, 0.5f);
                }
            }

            // 4. Draw intersection polygons (enclosed by 4 road-edge intersection points).
            Gizmos.color = new Color(0f, 1f, 1f, 0.3f);
            foreach (var polygon in _intersectionPolygons)
            {
                for (int i = 0; i < polygon.Count; i++)
                {
                    int j = (i + 1) % polygon.Count;
                    Gizmos.DrawLine(polygon[i], polygon[j]);
                }
            }
        }

        #endregion
    }
}
