using UnityEngine;
using UnityEditor;
using RoadNetworkEditor;
using RoadNetworkEditor.Networking;

using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace RoadNetworkEditor.EditorScripts
{
    [CustomEditor(typeof(NetworkManager))]
    public class NetworkManagerEditor : Editor
    {
        private NetworkManager manager;
        private Vector2 scrollPosition;

        // Edit mode.
        private enum EditMode
        {
            Select,
            CreateNode,
            CreateEdge,
            CreateWaypoint
        }

        private EditMode currentMode = EditMode.Select;

        // Frozen coordinates — Create Node mode.
        private bool freezeNodeX = false;
        private bool freezeNodeY = true;
        private bool freezeNodeZ = false;
        private float frozenNodeX = 0f;
        private float frozenNodeY = 0f;
        private float frozenNodeZ = 0f;

        // Frozen coordinates — Create Waypoint mode.
        private bool freezeWaypointX = false;
        private bool freezeWaypointY = true;
        private bool freezeWaypointZ = false;
        private float frozenWaypointX = 0f;
        private float frozenWaypointY = 0f;
        private float frozenWaypointZ = 0f;

        // File import / export.
        private string exportPath = "Assets/DataBase/AxisNetwork.json";
        private string importPath = "";

        // Temporary variables for edge creation.
        private NodeVisual edgeStartNode = null;
        private NodeVisual edgeEndNode = null;

        // Foldout state.
        private bool showNodesList = false;
        private bool showEdgesList = false;
        private Dictionary<string, bool> nodeFoldouts = new Dictionary<string, bool>();
        private Dictionary<string, bool> edgeFoldouts = new Dictionary<string, bool>();
        private bool showAltitudeLayers = true;
        private bool editBoundary;
        private int selectedBoundaryVertex = -1;
        private bool showLayerConnectors = true;
        private string newLayerId = "L1";
        private float newLayerAltitude = 125f;
        private int connectorFromLaneIndex = 0;
        private int connectorToLaneIndex = 0;
        private string newConnectorId = "";
        private string activeLayerId;
        private string layerStatusMessage;
        private MessageType layerStatusType = MessageType.Info;

        private void OnEnable()
        {
            manager = (NetworkManager)target;
            EnsureActiveLayer();
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        public override void OnInspectorGUI()
        {
            if (manager == null)
                manager = (NetworkManager)target;

            serializedObject.Update();

            // Script field (read-only).
            GUI.enabled = false;
            EditorGUILayout.ObjectField("Script", MonoScript.FromMonoBehaviour(manager), typeof(NetworkManager), false);
            GUI.enabled = true;

            // ========== Node and edge data lists ==========
            DrawNetworkDataLists();
            EditorGUILayout.Space(6);
            DrawMultilayerNetworkPanel();

            EditorGUILayout.Space(10);

            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("nodeSize"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("edgeWidth"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("waypointSize"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("showTextAnnotations"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("showFullNetwork"));

            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("nodePrefab"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("edgePrefab"));

            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("laneWidth"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("minLaneCount"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxLaneCount"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buildingLayerMask"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("collisionCheckHeight"));

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Road Network Editor", EditorStyles.boldLabel);

            // Mode selection.
            DrawModeSelection();

            EditorGUILayout.Space(10);

            // Display operation panel based on current mode.
            switch (currentMode)
            {
                case EditMode.Select:
                    DrawSelectModePanel();
                    break;
                case EditMode.CreateNode:
                    DrawCreateNodePanel();
                    break;
                case EditMode.CreateEdge:
                    DrawCreateEdgePanel();
                    break;
                case EditMode.CreateWaypoint:
                    DrawCreateWaypointPanel();
                    break;
            }

            EditorGUILayout.Space(10);

            // ========== Selection Info — only shown in Select mode, before Network Data ==========
            if (currentMode == EditMode.Select)
            {
                DrawSelectionInfo();
                EditorGUILayout.Space(10);
            }

            // Network operation buttons.
            DrawNetworkOperations();

            EditorGUILayout.Space(10);

            // File operations.
            DrawFileOperations();

            ApplyEditorLayerVisibility();

        }

        // ========== Draw network data lists ==========
        private void DrawNetworkDataLists()
        {
            EditorGUILayout.LabelField("Network Data", EditorStyles.boldLabel);

            if (manager.AxisNetwork == null)
            {
                EditorGUILayout.HelpBox("No network data loaded.", MessageType.Info);
                return;
            }

            // Node list.
            DrawNodesList();

            EditorGUILayout.Space(5);

            // Edge list.
            DrawEdgesList();
        }

        private void DrawNodesList()
        {
            showNodesList = EditorGUILayout.Foldout(showNodesList, $"Nodes ({manager.AxisNetwork.Nodes.Count})", true);

            if (!showNodesList)
                return;

            if (manager.AxisNetwork.Nodes.Count > 200)
            {
                EditorGUILayout.HelpBox(
                    manager.AxisNetwork.Nodes.Count + " nodes are loaded. The list is hidden so the Inspector stays responsive.",
                    MessageType.Info);
                return;
            }

            EditorGUI.indentLevel++;

            if (manager.AxisNetwork.Nodes.Count == 0)
            {
                EditorGUILayout.LabelField("No nodes", EditorStyles.miniLabel);
            }
            else
            {
                // Iterate List<Node> directly.
                for (int i = 0; i < manager.AxisNetwork.Nodes.Count; i++)
                {
                    Node node = manager.AxisNetwork.Nodes[i];
                    DrawNodeItem(node, i);
                }
            }

            EditorGUI.indentLevel--;
        }

        private void DrawNodeItem(Node node, int index)
        {
            string nodeId = node.NodeId;
            bool layerFrozen = IsLayerFrozen(node.LayerId);
            bool layerEditable = IsActiveEditableLayer(node.LayerId);

            // Ensure foldout state exists.
            if (!nodeFoldouts.ContainsKey(nodeId))
                nodeFoldouts[nodeId] = false;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Node title row.
            EditorGUILayout.BeginHorizontal();
            nodeFoldouts[nodeId] = EditorGUILayout.Foldout(nodeFoldouts[nodeId], nodeId, true);
            GUILayout.Label(LayerStateBadge(node.LayerId), EditorStyles.miniLabel,
                GUILayout.Width(layerFrozen ? 82f : 58f));

            // Focus button.
            if (GUILayout.Button("Focus", GUILayout.Width(50)))
            {
                NodeVisual nodeVisual = manager.GetNodeVisual(nodeId);
                if (nodeVisual != null)
                {
                    Selection.activeGameObject = nodeVisual.gameObject;
                    SceneView.lastActiveSceneView.FrameSelected();
                }
            }

            // Delete button.
            GUI.backgroundColor = Color.red;
            using (new EditorGUI.DisabledScope(!layerEditable))
            {
                if (GUILayout.Button("×", GUILayout.Width(25)))
                {
                    if (EditorUtility.DisplayDialog("Delete Node",
                        $"Delete node {nodeId}?",
                        "Yes", "No"))
                    {
                        Undo.RecordObject(manager, "Delete Network Node");
                        manager.DeleteNode(nodeId);
                        EditorUtility.SetDirty(manager);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndHorizontal();

            // Node detail info (foldout content).
            if (nodeFoldouts[nodeId])
            {
                EditorGUI.indentLevel++;

                // Position editing.
                using (new EditorGUI.DisabledScope(!layerEditable))
                {
                    Vector3 newPosition = EditorGUILayout.Vector3Field("Position", node.Position);
                    if (newPosition != node.Position)
                    {
                        Undo.RecordObject(manager, "Move Network Node");
                        manager.UpdateNodePosition(nodeId, newPosition);
                        EditorUtility.SetDirty(manager);
                        SceneView.RepaintAll();
                    }
                }
                if (!layerEditable)
                    EditorGUILayout.LabelField(LayerEditRestriction(node.LayerId), EditorStyles.miniLabel);

                // Show connected edges.
                EditorGUILayout.LabelField("Connected Edges:", EditorStyles.miniLabel);
                EditorGUI.indentLevel++;
                foreach (string edgeId in node.ConnectedEdges)
                {
                    EditorGUILayout.LabelField($"→ {edgeId}", EditorStyles.miniLabel);
                }
                EditorGUI.indentLevel--;

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawEdgesList()
        {
            showEdgesList = EditorGUILayout.Foldout(showEdgesList, $"Edges ({manager.AxisNetwork.Edges.Count})", true);

            if (!showEdgesList)
                return;

            if (manager.AxisNetwork.Edges.Count > 200)
            {
                EditorGUILayout.HelpBox(
                    manager.AxisNetwork.Edges.Count + " edges are loaded. The list is hidden so the Inspector stays responsive.",
                    MessageType.Info);
                return;
            }

            EditorGUI.indentLevel++;

            if (manager.AxisNetwork.Edges.Count == 0)
            {
                EditorGUILayout.LabelField("No edges", EditorStyles.miniLabel);
            }
            else
            {
                // Iterate List<Edge> directly.
                for (int i = 0; i < manager.AxisNetwork.Edges.Count; i++)
                {
                    Edge edge = manager.AxisNetwork.Edges[i];
                    DrawEdgeItem(edge, i);
                }
            }

            EditorGUI.indentLevel--;
        }

        private void DrawEdgeItem(Edge edge, int index)
        {
            string edgeId = edge.EdgeId;
            bool layerFrozen = IsLayerFrozen(edge.LayerId);
            bool layerEditable = IsActiveEditableLayer(edge.LayerId);

            if (!edgeFoldouts.ContainsKey(edgeId))
                edgeFoldouts[edgeId] = false;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Edge title row.
            EditorGUILayout.BeginHorizontal();
            edgeFoldouts[edgeId] = EditorGUILayout.Foldout(edgeFoldouts[edgeId], edgeId, true);
            GUILayout.Label(LayerStateBadge(edge.LayerId), EditorStyles.miniLabel,
                GUILayout.Width(layerFrozen ? 82f : 58f));

            // Focus button.
            if (GUILayout.Button("Focus", GUILayout.Width(50)))
            {
                EdgeVisual edgeVisual = manager.GetEdgeVisual(edgeId);
                if (edgeVisual != null)
                {
                    Selection.activeGameObject = edgeVisual.gameObject;
                    SceneView.lastActiveSceneView.FrameSelected();
                }
            }

            // Delete button.
            GUI.backgroundColor = Color.red;
            using (new EditorGUI.DisabledScope(!layerEditable))
            {
                if (GUILayout.Button("×", GUILayout.Width(25)))
                {
                    if (EditorUtility.DisplayDialog("Delete Edge",
                        $"Delete edge {edgeId}?",
                        "Yes", "No"))
                    {
                        Undo.RecordObject(manager, "Delete Network Edge");
                        manager.DeleteEdge(edgeId);
                        EditorUtility.SetDirty(manager);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndHorizontal();

            // Edge detail info.
            if (edgeFoldouts[edgeId])
            {
                EditorGUI.indentLevel++;

                // Start and end nodes.
                if (edge.LinkNodesId != null && edge.LinkNodesId.Count >= 2)
                {
                    EditorGUILayout.LabelField("Start Node", edge.LinkNodesId[0]);
                    EditorGUILayout.LabelField("End Node", edge.LinkNodesId[1]);
                }
                EditorGUILayout.LabelField("Length", edge.Length.ToString("F2"));

                EditorGUILayout.Space(3);

                // Centerline waypoints.
                if (edge.Centerline != null && edge.Centerline.Count > 0)
                {
                    EdgeVisual edgeVisual = manager.GetEdgeVisual(edgeId);

                    // Start Point (read-only display).
                    EditorGUILayout.BeginHorizontal();
                    GUI.enabled = false; // Disable editing.
                    EditorGUILayout.Vector3Field("Start", edge.Centerline[0]);
                    GUI.enabled = true;
                    EditorGUILayout.EndHorizontal();

                    // Waypoints (foldable).
                    int waypointCount = edge.Centerline.Count - 2;
                    if (waypointCount > 0)
                    {
                        // Create independent foldout state for each edge's waypoints.
                        string waypointFoldoutKey = $"{edgeId}_waypoints";
                        if (!nodeFoldouts.ContainsKey(waypointFoldoutKey))
                            nodeFoldouts[waypointFoldoutKey] = false;

                        nodeFoldouts[waypointFoldoutKey] = EditorGUILayout.Foldout(
                            nodeFoldouts[waypointFoldoutKey],
                            $"Waypoints ({waypointCount})",
                            true
                        );

                        if (nodeFoldouts[waypointFoldoutKey])
                        {
                            EditorGUI.indentLevel++;

                            for (int i = 1; i < edge.Centerline.Count - 1; i++)
                            {
                                EditorGUILayout.BeginHorizontal();

                                using (new EditorGUI.DisabledScope(!layerEditable))
                                {
                                    Vector3 newPos = EditorGUILayout.Vector3Field(
                                        $"Waypoint {i}", edge.Centerline[i]);

                                    // Route all mutations through NetworkManager's frozen-layer guard.
                                    if (newPos != edge.Centerline[i])
                                    {
                                        Undo.RecordObject(manager, "Move Network Waypoint");
                                        if (manager.UpdateAxisWaypoint(edgeId, i, newPos))
                                        {
                                            EditorUtility.SetDirty(manager);
                                            SceneView.RepaintAll();
                                        }
                                    }
                                }

                                // Delete waypoint button.
                                GUI.backgroundColor = Color.red;
                                using (new EditorGUI.DisabledScope(!layerEditable))
                                {
                                    if (GUILayout.Button("×", GUILayout.Width(25)))
                                    {
                                        Undo.RecordObject(manager, "Delete Network Waypoint");
                                        if (edgeVisual != null)
                                            manager.DeleteWaypoint(edgeVisual, i);
                                        else if (!IsLayerFrozen(edge.LayerId))
                                        {
                                            edge.Centerline.RemoveAt(i);
                                            edge.Length = MultilayerNetworkUtility.CalculatePolylineLength(
                                                edge.Centerline);
                                        }

                                        EditorUtility.SetDirty(manager);
                                        SceneView.RepaintAll();
                                        GUIUtility.ExitGUI();
                                    }
                                }
                                GUI.backgroundColor = Color.white;

                                EditorGUILayout.EndHorizontal();
                            }

                            EditorGUI.indentLevel--;
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField("Waypoints (0)", EditorStyles.miniLabel);
                    }

                    // End Point (read-only display).
                    EditorGUILayout.BeginHorizontal();
                    GUI.enabled = false; // Disable editing.
                    EditorGUILayout.Vector3Field("End", edge.Centerline[edge.Centerline.Count - 1]);
                    GUI.enabled = true;
                    EditorGUILayout.EndHorizontal();
                }

                if (!layerEditable)
                    EditorGUILayout.LabelField(LayerEditRestriction(edge.LayerId), EditorStyles.miniLabel);

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawMultilayerNetworkPanel()
        {
            if (manager.AxisNetwork == null) manager.AxisNetwork = new Network();
            if (manager.FullNetwork == null) manager.FullNetwork = new Network();
            EnsureActiveLayer();
            if (!AnyAxisLayerFrozen())
                MultilayerNetworkUtility.NormalizeAndValidate(manager.FullNetwork);

            List<AltitudeLayer> orderedLayers = OrderedAxisLayers();

            EditorGUILayout.LabelField("Multilayer Network", EditorStyles.boldLabel);
            showAltitudeLayers = EditorGUILayout.Foldout(
                showAltitudeLayers,
                $"Altitude Layers ({orderedLayers.Count})",
                true);
            if (showAltitudeLayers)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "Edit selects the layer used by Scene tools. Visible is combined with the " +
                    "global Show Full Network setting. Frozen layers remain selectable and visible, " +
                    "but their properties, geometry, connectors, deletion, and stack position are locked.",
                    MessageType.Info);

                for (int index = 0; index < orderedLayers.Count; index++)
                    DrawAltitudeLayerItem(orderedLayers[index], orderedLayers, index);

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Add Layer", EditorStyles.miniBoldLabel);
                newLayerId = EditorGUILayout.TextField("Layer ID", newLayerId);
                newLayerAltitude = EditorGUILayout.FloatField("Altitude", newLayerAltitude);
                if (GUILayout.Button("Add Altitude Layer"))
                    AddAxisLayerFromInspector();
                EditorGUILayout.EndVertical();

                if (!string.IsNullOrWhiteSpace(layerStatusMessage))
                    EditorGUILayout.HelpBox(layerStatusMessage, layerStatusType);
                EditorGUI.indentLevel--;
            }

            List<Lane> lanes = (manager.FullNetwork.Edges ?? new List<Edge>())
                .Where(edge => edge?.Lanes != null)
                .SelectMany(edge => edge.Lanes)
                .Where(lane => lane != null && !string.IsNullOrWhiteSpace(lane.LaneId))
                .OrderBy(lane => lane.LayerId)
                .ThenBy(lane => lane.LaneId)
                .ToList();
            string[] laneLabels = lanes
                .Select(lane => $"{lane.LaneId} [{lane.LayerId}]")
                .ToArray();

            showLayerConnectors = EditorGUILayout.Foldout(
                showLayerConnectors,
                $"Lane Connections ({manager.FullNetwork.LaneConnectionCorridors?.Count ?? 0})",
                true);
            if (!showLayerConnectors) return;

            EditorGUI.indentLevel++;
            for (int i = (manager.FullNetwork.LaneConnectionCorridors?.Count ?? 0) - 1;
                 i >= 0; i--)
            {
                LaneConnectionCorridor corridor = manager.FullNetwork.LaneConnectionCorridors[i];
                if (corridor == null) continue;
                bool corridorLocked = IsConnectorFrozen(corridor);

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    $"{corridor.CorridorId}: {corridor.FromLaneId} -> {corridor.ToLaneId}",
                    EditorStyles.boldLabel);
                GUI.backgroundColor = Color.red;
                using (new EditorGUI.DisabledScope(corridorLocked))
                {
                    if (GUILayout.Button("×", GUILayout.Width(25)))
                    {
                        Undo.RecordObject(manager, "Delete Layer Connector");
                        manager.DeleteLaneConnectionCorridor(corridor.CorridorId);
                        GUI.backgroundColor = Color.white;
                        GUIUtility.ExitGUI();
                    }
                }
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();

                using (new EditorGUI.DisabledScope(corridorLocked))
                {
                    EditorGUI.BeginChangeCheck();
                    float maxSpeed = Mathf.Max(0.1f,
                        EditorGUILayout.FloatField("Max Speed", corridor.MaxSpeed));
                    int priority = EditorGUILayout.IntField("Priority", corridor.Priority);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(manager, "Edit Layer Connector");
                        corridor.MaxSpeed = maxSpeed;
                        corridor.Priority = priority;
                        EditorUtility.SetDirty(manager);
                    }
                    if (!corridorLocked && corridor.Centerline == null)
                        corridor.Centerline = new List<Vector3>();
                    for (int pointIndex = 0;
                         corridor.Centerline != null && pointIndex < corridor.Centerline.Count;
                         pointIndex++)
                    {
                        Vector3 newPoint = EditorGUILayout.Vector3Field(
                            $"Point {pointIndex}",
                            corridor.Centerline[pointIndex]);
                        if (newPoint == corridor.Centerline[pointIndex]) continue;
                        Undo.RecordObject(manager, "Move Layer Connector Point");
                        corridor.Centerline[pointIndex] = newPoint;
                        corridor.FromPoint = corridor.Centerline[0];
                        corridor.ToPoint = corridor.Centerline[corridor.Centerline.Count - 1];
                        corridor.Length = MultilayerNetworkUtility.CalculatePolylineLength(corridor.Centerline);
                        EditorUtility.SetDirty(manager);
                        SceneView.RepaintAll();
                    }
                }
                if (corridorLocked)
                    EditorGUILayout.HelpBox(
                        "This connector touches a frozen layer and is read-only.", MessageType.Info);
                EditorGUILayout.LabelField("Length", corridor.Length.ToString("F2"));
                EditorGUILayout.EndVertical();
            }

            if (lanes.Count >= 2)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Create Directed Lane Connection", EditorStyles.miniBoldLabel);
                connectorFromLaneIndex = Mathf.Clamp(connectorFromLaneIndex, 0, lanes.Count - 1);
                connectorToLaneIndex = Mathf.Clamp(connectorToLaneIndex, 0, lanes.Count - 1);
                connectorFromLaneIndex = EditorGUILayout.Popup("From Lane", connectorFromLaneIndex, laneLabels);
                connectorToLaneIndex = EditorGUILayout.Popup("To Lane", connectorToLaneIndex, laneLabels);
                newConnectorId = EditorGUILayout.TextField("Connector ID", newConnectorId);
                bool canCreateConnector = connectorFromLaneIndex != connectorToLaneIndex &&
                                          !IsLayerFrozen(lanes[connectorFromLaneIndex].LayerId) &&
                                          !IsLayerFrozen(lanes[connectorToLaneIndex].LayerId);
                using (new EditorGUI.DisabledScope(!canCreateConnector))
                {
                    if (GUILayout.Button("Create Layer Connector"))
                    {
                        Undo.RecordObject(manager, "Create Layer Connector");
                        manager.CreateLaneConnectionCorridor(
                            lanes[connectorFromLaneIndex].LaneId,
                            lanes[connectorToLaneIndex].LaneId,
                            corridorId: string.IsNullOrWhiteSpace(newConnectorId) ? null : newConnectorId);
                        newConnectorId = "";
                        EditorUtility.SetDirty(manager);
                    }
                }
                EditorGUILayout.HelpBox(
                    "The connector is directed. Its default centerline joins the source lane end to the target lane start; edit points above for a climb/descent curve.",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
            }
            else
            {
                EditorGUILayout.HelpBox("Generate or load at least two lanes to create a connector.", MessageType.Info);
            }
            EditorGUI.indentLevel--;
        }

        private void DrawAltitudeLayerItem(AltitudeLayer layer, List<AltitudeLayer> orderedLayers,
            int index)
        {
            if (layer == null) return;
            bool isActive = layer.LayerId == activeLayerId;
            bool frozen = IsLayerFrozen(layer.LayerId);
            bool requestExitGui = false;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Row 1: active-layer selection and identity. Keep state controls on a
            // separate row so a narrow Inspector never pushes them off-screen.
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(isActive, new GUIContent("Edit", "Use this layer for Scene editing tools."),
                    "Button", GUILayout.Width(52f)) && !isActive)
            {
                activeLayerId = layer.LayerId;
                manager.ClearSelection();
                edgeStartNode = null;
                edgeEndNode = null;
                SetLayerStatus($"'{LayerDisplayName(layer)}' is now the active editing layer.",
                    MessageType.Info);
                isActive = true;
            }

            EditorGUILayout.LabelField(
                new GUIContent($"{LayerDisplayName(layer)}  [{layer.LayerId}]",
                    $"Layer ID: {layer.LayerId}"),
                EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();

            // Row 2: view/edit state. Both buttons share the available width.
            EditorGUILayout.BeginHorizontal();
            bool requestedVisible = GUILayout.Toggle(layer.Enabled,
                new GUIContent(layer.Enabled ? "Visible" : "Hidden",
                    "Per-layer visibility. Full-lane rendering also requires Show Full Network."),
                "Button", GUILayout.ExpandWidth(true));
            if (requestedVisible != layer.Enabled)
                SetAxisLayerVisible(layer.LayerId, requestedVisible);

            bool requestedFrozen = GUILayout.Toggle(frozen,
                new GUIContent(frozen ? "Frozen" : "Editable",
                    "Freeze or unfreeze this layer. Frozen geometry and stack position are locked."),
                "Button", GUILayout.ExpandWidth(true));
            if (requestedFrozen != frozen)
            {
                SetAxisLayerFrozen(layer.LayerId, requestedFrozen);
                frozen = requestedFrozen;
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(!isActive || frozen))
            {
                EditorGUI.BeginChangeCheck();
                string displayName = EditorGUILayout.TextField("Name", layer.DisplayName);
                float altitude = EditorGUILayout.FloatField("Altitude", layer.NominalAltitude);
                float tolerance = Mathf.Max(0f,
                    EditorGUILayout.FloatField("Tolerance", layer.VerticalTolerance));
                if (EditorGUI.EndChangeCheck())
                    UpdateAxisLayerProperties(layer, displayName, altitude, tolerance);
            }

            EditorGUILayout.BeginHorizontal();
            bool canMoveUp = CanReorderLayer(layer, orderedLayers, index - 1, isActive);
            bool canMoveDown = CanReorderLayer(layer, orderedLayers, index + 1, isActive);
            using (new EditorGUI.DisabledScope(!canMoveUp))
            {
                if (GUILayout.Button(new GUIContent("Move Up", "Move to the next higher altitude slot.")))
                    requestExitGui |= MoveAxisLayer(layer, orderedLayers, index - 1);
            }
            using (new EditorGUI.DisabledScope(!canMoveDown))
            {
                if (GUILayout.Button(new GUIContent("Move Down", "Move to the next lower altitude slot.")))
                    requestExitGui |= MoveAxisLayer(layer, orderedLayers, index + 1);
            }

            bool canDelete = isActive && !frozen && orderedLayers.Count > 1 &&
                             !HasFrozenConnectorDependency(layer.LayerId);
            GUI.backgroundColor = Color.red;
            using (new EditorGUI.DisabledScope(!canDelete))
            {
                if (GUILayout.Button(new GUIContent("Delete", frozen
                        ? "Unfreeze this layer before deleting it."
                        : "Delete this layer and all of its axis geometry."),
                        GUILayout.ExpandWidth(true)))
                    requestExitGui |= DeleteAxisLayerFromInspector(layer.LayerId);
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            if (frozen)
                EditorGUILayout.LabelField("Frozen: properties, geometry, connectors, deletion, and order are locked.",
                    EditorStyles.wordWrappedMiniLabel);
            else if (!isActive)
                EditorGUILayout.LabelField("Select Edit to modify this layer.", EditorStyles.miniLabel);
            else if (HasFrozenConnectorDependency(layer.LayerId))
                EditorGUILayout.LabelField(
                    "Deletion is blocked because a connector also belongs to a frozen layer.",
                    EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();

            if (requestExitGui) GUIUtility.ExitGUI();
        }

        private void AddAxisLayerFromInspector()
        {
            string layerId = (newLayerId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(layerId))
            {
                SetLayerStatus("Layer ID cannot be empty.", MessageType.Error);
                return;
            }
            if (MultilayerNetworkUtility.FindLayer(manager.AxisNetwork, layerId) != null)
            {
                SetLayerStatus($"Layer ID '{layerId}' already exists.", MessageType.Error);
                return;
            }

            Undo.RecordObject(manager, "Add Altitude Layer");
            manager.AxisNetwork.AltitudeLayers.Add(new AltitudeLayer
            {
                LayerId = layerId,
                DisplayName = layerId,
                NominalAltitude = newLayerAltitude,
                VerticalTolerance = 5f,
                Enabled = true,
                Frozen = false
            });
            activeLayerId = layerId;
            CommitAxisLayerGeometryChange();
            SetLayerStatus($"Added altitude layer '{layerId}'.", MessageType.Info);
            GUIUtility.ExitGUI();
        }

        private void UpdateAxisLayerProperties(AltitudeLayer layer, string displayName,
            float altitude, float tolerance)
        {
            if (layer == null || !IsActiveEditableLayer(layer.LayerId)) return;
            displayName = (displayName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                SetLayerStatus("Layer name cannot be empty.", MessageType.Error);
                return;
            }

            bool altitudeChanged = !Mathf.Approximately(layer.NominalAltitude, altitude);
            if (altitudeChanged && WouldCrossFrozenLayer(layer, altitude, out AltitudeLayer barrier))
            {
                SetLayerStatus(
                    $"Cannot move '{LayerDisplayName(layer)}' across frozen layer " +
                    $"'{LayerDisplayName(barrier)}'.", MessageType.Warning);
                return;
            }

            Undo.RecordObject(manager, "Edit Altitude Layer");
            layer.DisplayName = displayName;
            layer.VerticalTolerance = tolerance;
            if (altitudeChanged)
            {
                MultilayerNetworkUtility.SetAxisLayerAltitude(
                    manager.AxisNetwork, layer.LayerId, altitude);
                CommitAxisLayerGeometryChange();
            }
            else
            {
                SyncDerivedLayerMetadata(layer);
                EditorUtility.SetDirty(manager);
                SceneView.RepaintAll();
            }
            SetLayerStatus($"Updated layer '{LayerDisplayName(layer)}'.", MessageType.Info);
        }

        private bool MoveAxisLayer(AltitudeLayer layer, List<AltitudeLayer> orderedLayers,
            int targetIndex)
        {
            int sourceIndex = orderedLayers.IndexOf(layer);
            if (!CanReorderLayer(layer, orderedLayers, targetIndex, layer?.LayerId == activeLayerId))
            {
                SetLayerStatus("Frozen layers cannot move or be crossed during reordering.",
                    MessageType.Warning);
                return false;
            }

            Undo.RecordObject(manager, "Reorder Altitude Layer");
            if (!MultilayerNetworkUtility.ReorderAxisLayer(
                    manager.AxisNetwork, layer.LayerId, targetIndex)) return false;
            CommitAxisLayerGeometryChange();
            string direction = targetIndex < sourceIndex ? "up" : "down";
            SetLayerStatus($"Moved '{LayerDisplayName(layer)}' {direction} one altitude slot.",
                MessageType.Info);
            return true;
        }

        private bool DeleteAxisLayerFromInspector(string layerId)
        {
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(manager.AxisNetwork, layerId);
            if (layer == null || !IsActiveEditableLayer(layerId) ||
                manager.AxisNetwork.AltitudeLayers.Count <= 1 || HasFrozenConnectorDependency(layerId))
                return false;
            if (!EditorUtility.DisplayDialog("Delete Altitude Layer",
                    $"Delete '{LayerDisplayName(layer)}' and all of its nodes, edges, waypoints, and connectors?",
                    "Delete", "Cancel")) return false;

            Undo.RecordObject(manager, "Delete Altitude Layer");
            if (!MultilayerNetworkUtility.DeleteAxisLayer(manager.AxisNetwork, layerId)) return false;
            activeLayerId = null;
            EnsureActiveLayer();
            manager.ClearSelection();
            edgeStartNode = null;
            edgeEndNode = null;
            CommitAxisLayerGeometryChange();
            SetLayerStatus($"Deleted altitude layer '{layerId}'.", MessageType.Info);
            return true;
        }

        private void SetAxisLayerVisible(string layerId, bool visible)
        {
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(manager.AxisNetwork, layerId);
            if (layer == null) return;
            Undo.RecordObject(manager, "Change Layer Visibility");
            layer.Enabled = visible;
            SyncDerivedLayerMetadata(layer);
            if (!visible && activeLayerId == layerId)
            {
                currentMode = EditMode.Select;
                manager.ClearSelection();
                edgeStartNode = null;
                edgeEndNode = null;
            }
            EditorUtility.SetDirty(manager);
            ApplyEditorLayerVisibility();
            SceneView.RepaintAll();
            SetLayerStatus($"'{LayerDisplayName(layer)}' is now {(visible ? "visible" : "hidden")}.",
                MessageType.Info);
        }

        private void SetAxisLayerFrozen(string layerId, bool frozen)
        {
            AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(manager.AxisNetwork, layerId);
            if (layer == null) return;
            Undo.RecordObject(manager, "Change Layer Freeze State");
            layer.Frozen = frozen;
            SyncDerivedLayerMetadata(layer);
            if (frozen && activeLayerId == layerId)
            {
                currentMode = EditMode.Select;
                manager.ClearSelection();
                edgeStartNode = null;
                edgeEndNode = null;
            }
            EditorUtility.SetDirty(manager);
            SceneView.RepaintAll();
            SetLayerStatus($"'{LayerDisplayName(layer)}' is now {(frozen ? "frozen" : "editable")}.",
                MessageType.Info);
        }

        private bool CanReorderLayer(AltitudeLayer layer, List<AltitudeLayer> orderedLayers,
            int targetIndex, bool isActive)
        {
            return layer != null && isActive && !IsLayerFrozen(layer.LayerId) &&
                   targetIndex >= 0 && targetIndex < orderedLayers.Count &&
                   orderedLayers[targetIndex] != null &&
                   !IsLayerFrozen(orderedLayers[targetIndex].LayerId);
        }

        private bool WouldCrossFrozenLayer(AltitudeLayer layer, float requestedAltitude,
            out AltitudeLayer barrier)
        {
            barrier = null;
            if (layer == null) return false;
            foreach (AltitudeLayer candidate in manager.AxisNetwork.AltitudeLayers
                         .Where(candidate => candidate != null && candidate.LayerId != layer.LayerId &&
                                             IsLayerFrozen(candidate.LayerId)))
            {
                bool startsBelow = layer.NominalAltitude < candidate.NominalAltitude;
                bool crosses = startsBelow
                    ? requestedAltitude >= candidate.NominalAltitude
                    : requestedAltitude <= candidate.NominalAltitude;
                if (!crosses) continue;
                barrier = candidate;
                return true;
            }
            return false;
        }

        private bool HasFrozenConnectorDependency(string layerId)
        {
            return (manager.FullNetwork?.LaneConnectionCorridors ?? new List<LaneConnectionCorridor>())
                .Any(corridor => corridor != null &&
                                 (corridor.FromLayerId == layerId || corridor.ToLayerId == layerId) &&
                                 (IsLayerFrozen(corridor.FromLayerId) ||
                                  IsLayerFrozen(corridor.ToLayerId)));
        }

        private bool IsConnectorFrozen(LaneConnectionCorridor corridor)
        {
            return corridor != null &&
                   (IsLayerFrozen(corridor.FromLayerId) || IsLayerFrozen(corridor.ToLayerId));
        }

        private void CommitAxisLayerGeometryChange()
        {
            MultilayerNetworkUtility.NormalizeAxisLayerMembership(manager.AxisNetwork);
            manager.RebuildVisualsFromData();
            manager.GenerateFullNetworkFromAxis();
            EditorUtility.SetDirty(manager);
            ApplyEditorLayerVisibility();
            SceneView.RepaintAll();
        }

        private void SyncDerivedLayerMetadata(AltitudeLayer source)
        {
            if (source == null) return;
            AltitudeLayer derived = MultilayerNetworkUtility.FindLayer(
                manager.FullNetwork, source.LayerId);
            if (derived == null) return;
            derived.DisplayName = source.DisplayName;
            derived.NominalAltitude = source.NominalAltitude;
            derived.VerticalTolerance = source.VerticalTolerance;
            derived.Enabled = source.Enabled;
            derived.Frozen = source.Frozen;
        }

        private void EnsureActiveLayer()
        {
            if (manager == null) return;
            if (manager.AxisNetwork == null) manager.AxisNetwork = new Network();
            bool hasFrozenMetadata = manager.AxisNetwork.AltitudeLayers != null &&
                                     manager.AxisNetwork.AltitudeLayers.Any(layer =>
                                         layer != null && layer.Frozen);
            if (!hasFrozenMetadata)
                MultilayerNetworkUtility.NormalizeAxisLayerMembership(manager.AxisNetwork);
            if (MultilayerNetworkUtility.FindLayer(manager.AxisNetwork, activeLayerId) != null) return;
            activeLayerId = OrderedAxisLayers().FirstOrDefault()?.LayerId;
        }

        private List<AltitudeLayer> OrderedAxisLayers()
        {
            return (manager?.AxisNetwork?.AltitudeLayers ?? new List<AltitudeLayer>())
                .Where(layer => layer != null)
                .OrderByDescending(layer => layer.NominalAltitude)
                .ThenBy(layer => layer.LayerId)
                .ToList();
        }

        private bool IsLayerFrozen(string layerId)
        {
            if (string.IsNullOrWhiteSpace(layerId)) return false;
            return manager.IsAxisLayerFrozen(layerId) ||
                   MultilayerNetworkUtility.FindLayer(manager.FullNetwork, layerId)?.Frozen == true;
        }

        private bool IsLayerVisible(string layerId)
        {
            AltitudeLayer axisLayer = MultilayerNetworkUtility.FindLayer(manager.AxisNetwork, layerId);
            if (axisLayer != null) return axisLayer.Enabled;
            AltitudeLayer fullLayer = MultilayerNetworkUtility.FindLayer(manager.FullNetwork, layerId);
            return fullLayer == null || fullLayer.Enabled;
        }

        private bool IsActiveEditableLayer(string layerId)
        {
            return !string.IsNullOrEmpty(layerId) && layerId == activeLayerId &&
                   !IsLayerFrozen(layerId);
        }

        private bool IsActiveSceneEditableLayer(string layerId)
        {
            return IsActiveEditableLayer(layerId) && IsLayerVisible(layerId);
        }

        private string LayerStateBadge(string layerId)
        {
            if (IsLayerFrozen(layerId)) return layerId == activeLayerId ? "Active · Frozen" : "Frozen";
            return layerId == activeLayerId ? "Active" : layerId ?? "No Layer";
        }

        private string LayerEditRestriction(string layerId)
        {
            return IsLayerFrozen(layerId)
                ? "Read-only: this layer is frozen."
                : $"Read-only: select layer '{layerId}' as the active editing layer.";
        }

        private static string LayerDisplayName(AltitudeLayer layer)
        {
            return string.IsNullOrWhiteSpace(layer?.DisplayName) ? layer?.LayerId ?? "Layer" : layer.DisplayName;
        }

        private void SetLayerStatus(string message, MessageType type)
        {
            layerStatusMessage = message;
            layerStatusType = type;
            Repaint();
        }

        private void DrawActiveLayerToolState()
        {
            AltitudeLayer active = MultilayerNetworkUtility.FindLayer(manager.AxisNetwork, activeLayerId);
            if (active == null)
            {
                EditorGUILayout.HelpBox("Select an active altitude layer before editing geometry.",
                    MessageType.Warning);
                return;
            }
            EditorGUILayout.LabelField("Active Layer",
                $"{LayerDisplayName(active)} [{active.LayerId}]" +
                (active.Frozen ? " — Frozen" : !active.Enabled ? " — Hidden" : string.Empty));
            if (active.Frozen)
                EditorGUILayout.HelpBox("The active layer is frozen. Geometry tools are read-only.",
                    MessageType.Warning);
            else if (!active.Enabled)
                EditorGUILayout.HelpBox("The active layer is hidden. Make it visible before using Scene tools.",
                    MessageType.Warning);
        }

        private bool AnyAxisLayerFrozen()
        {
            return (manager?.AxisNetwork?.AltitudeLayers ?? new List<AltitudeLayer>())
                .Any(layer => layer != null && IsLayerFrozen(layer.LayerId));
        }

        private bool RequireEditableLayer(string layerId, string action)
        {
            if (IsActiveEditableLayer(layerId)) return true;
            string message = IsLayerFrozen(layerId)
                ? $"Layer '{layerId}' is frozen; cannot {action}."
                : $"Layer '{layerId}' is not the active editing layer; cannot {action}.";
            SetLayerStatus(message, MessageType.Warning);
            SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(message));
            return false;
        }

        private void ApplyEditorLayerVisibility()
        {
            if (manager == null) return;
            bool showAxis = !manager.showFullNetwork;
            foreach (NodeVisual node in manager.GetComponentsInChildren<NodeVisual>(true))
            {
                if (node == null || node.NodeData == null) continue;
                bool visible = showAxis && IsLayerVisible(node.NodeData.LayerId);
                if (node.gameObject.activeSelf != visible) node.gameObject.SetActive(visible);
            }
            foreach (EdgeVisual edge in manager.GetComponentsInChildren<EdgeVisual>(true))
            {
                if (edge == null || edge.EdgeData == null) continue;
                bool visible = showAxis && IsLayerVisible(edge.EdgeData.LayerId);
                if (edge.gameObject.activeSelf != visible) edge.gameObject.SetActive(visible);
            }
        }

        // ========== Other methods unchanged ==========

        private void DrawModeSelection()
        {
            EditorGUILayout.LabelField("Edit Mode", EditorStyles.boldLabel);

            EditMode newMode = (EditMode)GUILayout.Toolbar((int)currentMode, new string[] {
                "Select", "Create Node", "Create Edge", "Create Waypoint"
            });

            if (newMode != currentMode)
            {
                if (newMode != EditMode.Select && !IsActiveSceneEditableLayer(activeLayerId))
                {
                    currentMode = EditMode.Select;
                    SetLayerStatus(
                        "The active layer is frozen or hidden. Make it editable and visible before using geometry tools.",
                        MessageType.Warning);
                    return;
                }
                currentMode = newMode;
                manager.ClearSelection();
                edgeStartNode = null;
                edgeEndNode = null;
                SceneView.RepaintAll();
            }
        }

        private void DrawSelectModePanel()
        {
            EditorGUILayout.HelpBox("Select Mode:\n- Click nodes, edges, or waypoints to select\n- View and edit properties in Selection Info", MessageType.Info);
        }

        private void DrawCreateNodePanel()
        {
            EditorGUILayout.HelpBox("Create Node Mode:\n- Click in Scene view to place a new node\n- Frozen axes use fixed values, unfrozen axes follow click position", MessageType.Info);
            DrawActiveLayerToolState();
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Preview Position", nodePreviewPos.ToString());
            EditorGUILayout.Space(5);
            DrawFrozenPosition(ref freezeNodeX, ref freezeNodeY, ref freezeNodeZ,
                ref frozenNodeX, ref frozenNodeY, ref frozenNodeZ);
        }

        private void DrawCreateEdgePanel()
        {
            EditorGUILayout.HelpBox("Create Edge Mode:\n- Click first node (start)\n- Click second node (end)\n- Edge will be created automatically", MessageType.Info);
            DrawActiveLayerToolState();

            EditorGUILayout.Space(5);

            if (edgeStartNode != null)
            {
                EditorGUILayout.LabelField("Start Node", edgeStartNode.NodeId);
            }
            else
            {
                EditorGUILayout.LabelField("Start Node", "None (click a node)");
            }

            if (edgeEndNode != null)
            {
                EditorGUILayout.LabelField("End Node", edgeEndNode.NodeId);
            }
            else
            {
                EditorGUILayout.LabelField("End Node", "None");
            }

            if (edgeStartNode != null && GUILayout.Button("Cancel Edge Creation"))
            {
                edgeStartNode = null;
                edgeEndNode = null;
                SceneView.RepaintAll();
            }
        }

        private void DrawCreateWaypointPanel()
        {
            EdgeVisual selectedEdge = manager.GetSelectedEdge();

            DrawActiveLayerToolState();

            if (selectedEdge != null)
            {
                EditorGUILayout.LabelField("Selected Edge", selectedEdge.EdgeId);
                EditorGUILayout.LabelField("Waypoints", (selectedEdge.EdgeData.Centerline.Count - 2).ToString());
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox("Create Waypoint Mode:\n- Left-click an edge to select\n- Shift+Left-click to add waypoint\n- Right-click waypoint to delete", MessageType.Info);
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Preview Position", waypointPreviewPos.ToString());
                EditorGUILayout.Space(5);
                DrawFrozenPosition(ref freezeWaypointX, ref freezeWaypointY, ref freezeWaypointZ,
                    ref frozenWaypointX, ref frozenWaypointY, ref frozenWaypointZ);
            }
            else
            {
                EditorGUILayout.HelpBox("No edge selected.\n\nClick an edge in Scene view to select it.", MessageType.Info);
            }
        }

        private void DrawNetworkOperations()
        {
            EditorGUILayout.LabelField("Network Operations", EditorStyles.boldLabel);

            bool hasFrozenLayers = AnyAxisLayerFrozen();
            using (new EditorGUI.DisabledScope(hasFrozenLayers))
            {
                if (GUILayout.Button("Clear Axis Network"))
                {
                    if (EditorUtility.DisplayDialog("Clear Network",
                        "Are you sure you want to clear the entire axis network?",
                        "Yes", "No"))
                    {
                        Undo.RecordObject(manager, "Clear Axis Network");
                        manager.ClearAxisNetwork();
                        nodeFoldouts.Clear();
                        edgeFoldouts.Clear();
                        EditorUtility.SetDirty(manager);
                    }
                }
            }
            if (hasFrozenLayers)
                EditorGUILayout.LabelField(
                    "Clear, import, and network regeneration are disabled while any layer is frozen.",
                    EditorStyles.wordWrappedMiniLabel);

            if (GUILayout.Button("Rebuild Visuals from Data"))
            {
                manager.RebuildVisualsFromData();
                EditorUtility.SetDirty(manager);
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Axis Auto-Generation", EditorStyles.boldLabel);

            // Axis Generator selector — drag component here.
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("axisGenerator"),
                new GUIContent("Axis Generator",
                    "Drag AxisGenerator1 or AxisGenerator2 here, then click Generate."));

            // Common parameters for all generators.
            var gen = manager.axisGenerator;
            if (gen != null)
            {
                EditorGUILayout.LabelField("Common Params", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("axisDetectionHeight"),
                    new GUIContent("Detection Height", "Obstacle-check height and altitude of the generated axis."));
            }

            // Type-specific parameters.
            if (gen is AxisGenerator1)
            {
                EditorGUILayout.LabelField("Params (Skeleton Extraction)", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("axisVoxelSize"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("axisStraightLimit"));
            }
            else if (gen is AxisGenerator2)
            {
                EditorGUILayout.LabelField("Params (Quadtree Partition)", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("axis2MaxDepth"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("axis2MinCellSize"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("axis2PruneCellSize"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("axis2MinCorridorLength"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("axis2MinParallelSpacing"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("axis2MinDeadEndLength"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("axis2EnableOuterFrame"));
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Drag an AxisGenerator1 or AxisGenerator2 component to the slot above.\n\n" +
                    "Add Component → AxisGenerator1 (skeleton extraction) or\n" +
                    "Add Component → AxisGenerator2 (quadtree partition)\n" +
                    "to this GameObject, then drag it into the field.",
                    MessageType.Info);
            }

            if (serializedObject.ApplyModifiedProperties())
                EditorUtility.SetDirty(manager);

            DrawGenerationBoundaryControls();

            using (new EditorGUI.DisabledScope(gen == null || hasFrozenLayers))
            {
                if (GUILayout.Button("Generate Axis Network"))
                {
                    Undo.RecordObject(manager, "Generate Axis Network");
                    manager.RunAxisGenerator();
                }
            }

            EditorGUILayout.Space(5);
            using (new EditorGUI.DisabledScope(hasFrozenLayers))
            {
                if (GUILayout.Button("Generate Full Network"))
                {
                    Undo.RecordObject(manager, "Generate Full Network");
                    manager.GenerateFullNetworkFromAxis();
                    EditorUtility.SetDirty(manager);
                }
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                "Check Visual Settings > Show Full Network to view lane colors:\n" +
                "Straight=Green | Left=Blue | Right=Orange | StraightLeft=Cyan | StraightRight=YellowGreen | LeftRight=Purple | StraightLeftRight=Yellow",
                MessageType.Info);
        }

        private void DrawFileOperations()
        {
            EditorGUILayout.LabelField("File Operations", EditorStyles.boldLabel);

            exportPath = EditorGUILayout.TextField("Export Path", exportPath);

            if (GUILayout.Button("Export Axis Network to JSON"))
            {
                ExportNetworkToJSON();
            }

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            importPath = EditorGUILayout.TextField("Import Path", importPath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string path = EditorUtility.OpenFilePanel("Import Network", "Assets/DataBase", "json");
                if (!string.IsNullOrEmpty(path))
                {
                    importPath = path;
                }
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(AnyAxisLayerFrozen()))
            {
                if (GUILayout.Button("Import Axis Network from JSON"))
                    ImportNetworkFromJSON();
            }
        }

        private void DrawSelectionInfo()
        {
            EditorGUILayout.LabelField("Selection Info", EditorStyles.boldLabel);

            NodeVisual selectedNode = manager.GetSelectedNode();
            EdgeVisual selectedEdge = manager.GetSelectedEdge();
            WaypointVisual selectedWaypoint = manager.GetSelectedWaypoint();

            if (selectedNode != null)
            {
                DrawNodeSelectionInfo(selectedNode);
            }
            else if (selectedEdge != null)
            {
                DrawEdgeSelectionInfo(selectedEdge);
            }
            else if (selectedWaypoint != null)
            {
                DrawWaypointSelectionInfo(selectedWaypoint);
            }
            else
            {
                EditorGUILayout.LabelField("No Selection", EditorStyles.miniLabel);
            }
        }

        private void DrawNodeSelectionInfo(NodeVisual node)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Node", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", node.NodeId);

            if (node.NodeData != null)
            {
                EditorGUILayout.LabelField("Position", node.NodeData.Position.ToString());

                EditorGUILayout.LabelField("Connected Edges", node.NodeData.ConnectedEdges != null
                    ? string.Join(", ", node.NodeData.ConnectedEdges)
                    : "None");
                EditorGUILayout.LabelField("Neighbor Nodes", node.NodeData.NeighborNodes != null
                    ? string.Join(", ", node.NodeData.NeighborNodes)
                    : "None");
            }

            if (GUILayout.Button("Deselect"))
                manager.ClearSelection();

            EditorGUILayout.EndVertical();
        }

        private void DrawEdgeSelectionInfo(EdgeVisual edge)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Edge", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", edge.EdgeId);

            if (edge.EdgeData != null)
            {
                if (edge.EdgeData.LinkNodesId != null && edge.EdgeData.LinkNodesId.Count >= 2)
                {
                    EditorGUILayout.LabelField("Start Node", edge.EdgeData.LinkNodesId[0]);
                    EditorGUILayout.LabelField("End Node", edge.EdgeData.LinkNodesId[1]);
                }
                EditorGUILayout.LabelField("Length", edge.EdgeData.Length.ToString("F2"));

                if (edge.EdgeData.Centerline != null && edge.EdgeData.Centerline.Count > 0)
                {
                    EditorGUILayout.LabelField("Centerline Points", edge.EdgeData.Centerline.Count.ToString());
                    int wpCount = edge.EdgeData.Centerline.Count - 2;
                    EditorGUILayout.LabelField("  Waypoints", wpCount > 0 ? wpCount.ToString() : "0");
                }
            }

            if (GUILayout.Button("Deselect"))
                manager.ClearSelection();

            EditorGUILayout.EndVertical();
        }

        private void DrawWaypointSelectionInfo(WaypointVisual waypoint)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Waypoint", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Parent Edge",
                waypoint.ParentEdge != null ? waypoint.ParentEdge.EdgeId : "N/A");
            EditorGUILayout.LabelField("Index", waypoint.WaypointIndex.ToString());

            EditorGUILayout.LabelField("Position", waypoint.transform.position.ToString());

            if (GUILayout.Button("Deselect"))
                manager.ClearSelection();

            EditorGUILayout.EndVertical();
        }

        // ==================== Scene GUI interaction ====================

        private void OnSceneGUI(SceneView sceneView)
        {
            if (manager == null)
                return;

            Event e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace))
            {
                var selNode = manager.GetSelectedNode();
                var selEdge = manager.GetSelectedEdge();
                var selWaypoint = manager.GetSelectedWaypoint();

                if (selWaypoint != null)
                {
                    string layerId = selWaypoint.ParentEdge?.EdgeData?.LayerId;
                    if (RequireEditableLayer(layerId, "delete this waypoint"))
                    {
                        Undo.RecordObject(manager, "Delete Network Waypoint");
                        manager.DeleteWaypoint(selWaypoint.ParentEdge, selWaypoint.WaypointIndex);
                    }
                    e.Use();
                }
                else if (selEdge != null)
                {
                    if (RequireEditableLayer(selEdge.EdgeData?.LayerId, "delete this edge"))
                    {
                        Undo.RecordObject(manager, "Delete Network Edge");
                        manager.DeleteEdge(selEdge.EdgeId);
                    }
                    e.Use();
                }
                else if (selNode != null)
                {
                    if (RequireEditableLayer(selNode.NodeData?.LayerId, "delete this node"))
                    {
                        Undo.RecordObject(manager, "Delete Network Node");
                        manager.DeleteNode(selNode.NodeId);
                    }
                    e.Use();
                }
            }

            // Draw Position Handle for selected node.
            NodeVisual selectedNode = manager.GetSelectedNode();
            if (selectedNode != null && selectedNode.NodeData != null &&
                IsActiveSceneEditableLayer(selectedNode.NodeData.LayerId) &&
                IsLayerVisible(selectedNode.NodeData.LayerId))
            {
                EditorGUI.BeginChangeCheck();
                Vector3 newPosition = Handles.PositionHandle(selectedNode.transform.position, Quaternion.identity);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(manager, "Move Network Node");
                    manager.UpdateNodePosition(selectedNode.NodeId, newPosition);
                    EditorUtility.SetDirty(manager);
                    SceneView.RepaintAll();
                }
            }

            // Draw waypoint handles for selected edge.
            EdgeVisual selectedEdge = manager.GetSelectedEdge();
            if (selectedEdge != null && selectedEdge.EdgeData?.Centerline != null &&
                IsActiveSceneEditableLayer(selectedEdge.EdgeData.LayerId) &&
                IsLayerVisible(selectedEdge.EdgeData.LayerId))
            {
                for (int i = 1; i < selectedEdge.EdgeData.Centerline.Count - 1; i++)
                {
                    EditorGUI.BeginChangeCheck();
                    Vector3 newPosition = Handles.PositionHandle(selectedEdge.EdgeData.Centerline[i], Quaternion.identity);

                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(manager, "Move Network Waypoint");
                        if (manager.UpdateAxisWaypoint(selectedEdge.EdgeId, i, newPosition))
                        {
                            EditorUtility.SetDirty(manager);
                            SceneView.RepaintAll();
                        }
                    }
                }
            }

            // Draw handle for directly selected waypoint (when waypoint is individually selected in Select mode).
            WaypointVisual selectedWaypoint = manager.GetSelectedWaypoint();
            if (selectedWaypoint != null && selectedEdge == null && selectedWaypoint.ParentEdge != null &&
                selectedWaypoint.ParentEdge.EdgeData != null &&
                IsActiveSceneEditableLayer(selectedWaypoint.ParentEdge.EdgeData.LayerId) &&
                IsLayerVisible(selectedWaypoint.ParentEdge.EdgeData.LayerId))
            {
                EditorGUI.BeginChangeCheck();
                Vector3 newPosition = Handles.PositionHandle(selectedWaypoint.transform.position, Quaternion.identity);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(manager, "Move Network Waypoint");
                    if (manager.UpdateAxisWaypoint(selectedWaypoint.ParentEdge.EdgeId,
                            selectedWaypoint.WaypointIndex, newPosition))
                    {
                        EditorUtility.SetDirty(manager);
                        SceneView.RepaintAll();
                    }
                }
            }

            if (currentMode != EditMode.Select && !IsActiveSceneEditableLayer(activeLayerId))
            {
                currentMode = EditMode.Select;
                edgeStartNode = null;
                edgeEndNode = null;
            }

            if (editBoundary && DrawBoundaryEditor(e, sceneView))
                return;

            switch (currentMode)
            {
                case EditMode.Select:
                    HandleSelectMode(e, sceneView);
                    break;
                case EditMode.CreateNode:
                    HandleCreateNodeMode(e, sceneView);
                    break;
                case EditMode.CreateEdge:
                    HandleCreateEdgeMode(e, sceneView);
                    break;
                case EditMode.CreateWaypoint:
                    HandleCreateWaypointMode(e, sceneView);
                    break;
            }

            DrawSceneGUIHelpers(sceneView);
        }

        private void HandleSelectMode(Event e, SceneView sceneView)
        {
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            if (e.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(controlId);
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                // Consume event to prevent Unity from auto-selecting and switching Inspector
                HandleUtility.AddDefaultControl(controlId);
                e.Use();

                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

                // 1. Try raycast for collider-based objects (nodes, waypoints)
                if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity))
                {
                    NodeVisual node = hit.collider.GetComponent<NodeVisual>();
                    if (node != null)
                    {
                        manager.SelectNode(node);
                        sceneView.Repaint();
                        Repaint();
                        return;
                    }

                    WaypointVisual waypoint = hit.collider.GetComponent<WaypointVisual>();
                    if (waypoint != null)
                    {
                        manager.SelectWaypoint(waypoint);
                        sceneView.Repaint();
                        Repaint();
                        return;
                    }
                }

                // 2. Try distance-based edge detection (edges lack colliders)
                EdgeVisual closestEdge = FindClosestEdgeToClick(e);
                if (closestEdge != null)
                {
                    manager.SelectEdge(closestEdge);
                    sceneView.Repaint();
                    Repaint();
                    return;
                }

                // 3. Clicked empty space — deselect
                manager.ClearSelection();
                sceneView.Repaint();
                Repaint();
            }
        }

        private Vector3 nodePreviewPos = Vector3.zero;
        private Vector3 waypointPreviewPos = Vector3.zero;

        private void HandleCreateNodeMode(Event e, SceneView sceneView)
        {
            if (!IsActiveSceneEditableLayer(activeLayerId)) return;
            if (e.type == EventType.MouseMove)
            {
                nodePreviewPos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView),
                    freezeNodeX, freezeNodeY, freezeNodeZ, frozenNodeX, frozenNodeY, frozenNodeZ);
                sceneView.Repaint();
                Repaint();
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                Vector3 clickPos = GetMouseWorldPosition(e, sceneView);
                Vector3 position = ApplyFrozenAxes(clickPos,
                    freezeNodeX, freezeNodeY, freezeNodeZ, frozenNodeX, frozenNodeY, frozenNodeZ);
                Undo.RecordObject(manager, "Create Network Node");
                if (manager.CreateNode(position, activeLayerId) != null)
                    EditorUtility.SetDirty(manager);
                e.Use();
                sceneView.Repaint();
                Repaint();
            }

            Handles.color = Color.green;
            Handles.SphereHandleCap(0, nodePreviewPos, Quaternion.identity, 1f, EventType.Repaint);
        }

        private void HandleCreateEdgeMode(Event e, SceneView sceneView)
        {
            if (!IsActiveSceneEditableLayer(activeLayerId)) return;
            if (e.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                e.Use();

                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                RaycastHit hit;

                if (Physics.Raycast(ray, out hit, Mathf.Infinity))
                {
                    NodeVisual clickedNode = hit.collider.GetComponent<NodeVisual>();

                    if (clickedNode != null)
                    {
                        if (clickedNode.NodeData == null ||
                            clickedNode.NodeData.LayerId != activeLayerId ||
                            !IsLayerVisible(clickedNode.NodeData.LayerId))
                        {
                            SetLayerStatus("Select nodes from the active, visible layer when creating an edge.",
                                MessageType.Warning);
                            return;
                        }
                        if (edgeStartNode == null)
                        {
                            edgeStartNode = clickedNode;
                            Debug.Log($"Start: {edgeStartNode.NodeId}");
                        }
                        else if (clickedNode != edgeStartNode)
                        {
                            Undo.RecordObject(manager, "Create Network Edge");
                            if (manager.CreateEdge(edgeStartNode.NodeId, clickedNode.NodeId) != null)
                                EditorUtility.SetDirty(manager);
                            edgeStartNode = null;
                            Debug.Log("Edge created!");
                        }

                        SceneView.RepaintAll();
                        Repaint();
                    }
                }

                return;
            }

            if (edgeStartNode != null)
            {
                Handles.color = Color.yellow;
                Vector3 mousePos = GetMouseWorldPosition(Event.current, sceneView);
                Handles.DrawLine(edgeStartNode.transform.position, mousePos);
                Handles.DrawWireDisc(mousePos, Vector3.up, 0.5f);
                sceneView.Repaint();
            }
        }

        private void HandleCreateWaypointMode(Event e, SceneView sceneView)
        {
            if (!IsActiveSceneEditableLayer(activeLayerId)) return;
            if (e.type == EventType.MouseMove)
            {
                waypointPreviewPos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView),
                    freezeWaypointX, freezeWaypointY, freezeWaypointZ,
                    frozenWaypointX, frozenWaypointY, frozenWaypointZ);
                sceneView.Repaint();
                Repaint();
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                if (e.shift)
                {
                    // Shift+Left-click: add waypoint to selected edge (with frozen coordinates applied).
                    EdgeVisual selectedEdge = manager.GetSelectedEdge();
                    if (selectedEdge != null && selectedEdge.EdgeData != null &&
                        selectedEdge.EdgeData.LayerId == activeLayerId &&
                        IsLayerVisible(selectedEdge.EdgeData.LayerId))
                    {
                        Vector3 clickPos = GetMouseWorldPosition(e, sceneView);
                        Vector3 position = ApplyFrozenAxes(clickPos,
                            freezeWaypointX, freezeWaypointY, freezeWaypointZ,
                            frozenWaypointX, frozenWaypointY, frozenWaypointZ);
                        int insertIndex = selectedEdge.EdgeData.Centerline.Count - 1;
                        Undo.RecordObject(manager, "Create Network Waypoint");
                        if (manager.InsertAxisWaypoint(selectedEdge.EdgeId, position, insertIndex))
                            EditorUtility.SetDirty(manager);
                        e.Use();
                        sceneView.Repaint();
                        Repaint();
                    }
                }
                else
                {
                    // Left-click: select closest edge via distance detection.
                    EdgeVisual closestEdge = FindClosestEdgeToClick(e);
                    if (closestEdge != null && closestEdge.EdgeData != null &&
                        closestEdge.EdgeData.LayerId == activeLayerId &&
                        IsLayerVisible(closestEdge.EdgeData.LayerId))
                    {
                        manager.SelectEdge(closestEdge);
                    }
                    else
                    {
                        manager.ClearSelection();
                    }
                    e.Use();
                    sceneView.Repaint();
                    Repaint();
                }
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 1 && !e.alt)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                RaycastHit hit;

                if (Physics.Raycast(ray, out hit))
                {
                    WaypointVisual clickedWaypoint = hit.collider.GetComponent<WaypointVisual>();
                    if (clickedWaypoint != null && clickedWaypoint.ParentEdge != null &&
                        clickedWaypoint.ParentEdge.EdgeData != null &&
                        clickedWaypoint.ParentEdge.EdgeData.LayerId == activeLayerId &&
                        RequireEditableLayer(clickedWaypoint.ParentEdge.EdgeData.LayerId,
                            "delete this waypoint"))
                    {
                        Undo.RecordObject(manager, "Delete Network Waypoint");
                        manager.DeleteWaypoint(clickedWaypoint.ParentEdge, clickedWaypoint.WaypointIndex);
                        EditorUtility.SetDirty(manager);
                        e.Use();
                        sceneView.Repaint();
                        Repaint();
                    }
                }
            }

            // Preview sphere: shown at mouse hover position (after frozen coordinates applied).
            EdgeVisual selEdge = manager.GetSelectedEdge();
            if (selEdge != null)
            {
                Handles.color = Color.cyan;
                Handles.SphereHandleCap(0, waypointPreviewPos, Quaternion.identity, 0.6f, EventType.Repaint);
            }
        }

        /// <summary>
        /// Find the edge closest to the mouse click position via distance detection (edges have no Collider, cannot use Raycast).
        /// </summary>
        private EdgeVisual FindClosestEdgeToClick(Event e)
        {
            Transform edgesContainer = manager.transform.Find("Edges");
            if (edgesContainer == null)
                return null;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            float bestDistance = 1.0f;
            EdgeVisual bestEdge = null;

            foreach (Transform child in edgesContainer)
            {
                EdgeVisual edge = child.GetComponent<EdgeVisual>();
                if (edge == null || edge.EdgeData == null || edge.EdgeData.Centerline == null)
                    continue;

                float dist = GetDistanceFromRayToEdge(ray, edge);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestEdge = edge;
                }
            }

            return bestEdge;
        }

        private float GetDistanceFromRayToEdge(Ray ray, EdgeVisual edge)
        {
            float minDist = float.MaxValue;
            var points = edge.EdgeData.Centerline;

            for (int i = 0; i < points.Count - 1; i++)
            {
                float dist = DistanceFromRayToSegment(ray, points[i], points[i + 1]);
                if (dist < minDist)
                    minDist = dist;
            }

            return minDist;
        }

        private float DistanceFromRayToSegment(Ray ray, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            Vector3 aToOrigin = ray.origin - a;

            float abLenSq = ab.sqrMagnitude;
            Vector3 abNorm = abLenSq > 0.0001f ? ab / Mathf.Sqrt(abLenSq) : (b - a).normalized;

            Vector3 cross = Vector3.Cross(ray.direction, abNorm);
            float crossMag = cross.magnitude;

            if (crossMag < 0.0001f)
            {
                float d1 = DistanceFromPointToRay(ray, a);
                float d2 = DistanceFromPointToRay(ray, b);
                return Mathf.Min(d1, d2);
            }

            Vector3 crossNorm = cross / crossMag;
            float distance = Mathf.Abs(Vector3.Dot(aToOrigin, crossNorm));

            Vector3 aToClosest = aToOrigin - Vector3.Dot(aToOrigin, crossNorm) * crossNorm;
            float t = Vector3.Dot(aToClosest, abNorm) / Mathf.Sqrt(abLenSq);
            t = Mathf.Clamp01(t);

            if (t > 0 && t < 1)
                return distance;

            float dA = DistanceFromPointToRay(ray, a);
            float dB = DistanceFromPointToRay(ray, b);
            return Mathf.Min(dA, dB);
        }

        private float DistanceFromPointToRay(Ray ray, Vector3 point)
        {
            Vector3 originToPoint = point - ray.origin;
            float projection = Vector3.Dot(originToPoint, ray.direction);
            if (projection < 0)
                return originToPoint.magnitude;
            Vector3 closestOnRay = ray.origin + ray.direction * projection;
            return Vector3.Distance(point, closestOnRay);
        }

        private void DrawFrozenPosition(ref bool freezeX, ref bool freezeY, ref bool freezeZ,
            ref float frozenX, ref float frozenY, ref float frozenZ)
        {
            EditorGUILayout.LabelField("Frozen Position", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            freezeX = EditorGUILayout.Toggle(freezeX, GUILayout.Width(16));
            GUI.enabled = freezeX;
            frozenX = EditorGUILayout.FloatField("X", frozenX);
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            freezeY = EditorGUILayout.Toggle(freezeY, GUILayout.Width(16));
            GUI.enabled = freezeY;
            frozenY = EditorGUILayout.FloatField("Y", frozenY);
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            freezeZ = EditorGUILayout.Toggle(freezeZ, GUILayout.Width(16));
            GUI.enabled = freezeZ;
            frozenZ = EditorGUILayout.FloatField("Z", frozenZ);
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        private Vector3 ApplyFrozenAxes(Vector3 clickPos, bool freezeX, bool freezeY, bool freezeZ,
            float frozenX, float frozenY, float frozenZ)
        {
            return new Vector3(
                freezeX ? frozenX : clickPos.x,
                freezeY ? frozenY : clickPos.y,
                freezeZ ? frozenZ : clickPos.z
            );
        }

        private void DrawSceneGUIHelpers(SceneView sceneView)
        {
            Handles.BeginGUI();

            GUILayout.BeginArea(new Rect(10, 10, 300, 100));
            GUILayout.Box($"Mode: {currentMode}", GUILayout.Width(290));

            switch (currentMode)
            {
                case EditMode.Select:
                    NodeVisual selNode = manager.GetSelectedNode();
                    EdgeVisual selEdge = manager.GetSelectedEdge();
                    WaypointVisual selWaypoint = manager.GetSelectedWaypoint();
                    if (selNode != null)
                        GUILayout.Box($"Selected: Node {selNode.NodeId}", GUILayout.Width(290));
                    else if (selEdge != null)
                        GUILayout.Box($"Selected: Edge {selEdge.EdgeId}", GUILayout.Width(290));
                    else if (selWaypoint != null)
                        GUILayout.Box($"Selected: Waypoint {selWaypoint.WaypointIndex} of {(selWaypoint.ParentEdge != null ? selWaypoint.ParentEdge.EdgeId : "N/A")}", GUILayout.Width(290));
                    break;
                case EditMode.CreateNode:
                    GUILayout.Box("Click to place node", GUILayout.Width(290));
                    break;
                case EditMode.CreateEdge:
                    if (edgeStartNode == null)
                        GUILayout.Box("Click first node", GUILayout.Width(290));
                    else
                        GUILayout.Box($"Start: {edgeStartNode.NodeId}\nClick second node", GUILayout.Width(290));
                    break;
                case EditMode.CreateWaypoint:
                    EdgeVisual selectedEdge = manager.GetSelectedEdge();
                    if (selectedEdge != null)
                        GUILayout.Box($"Selected: {selectedEdge.EdgeId}\nShift+Click: Add waypoint\nRight-click: Delete waypoint", GUILayout.Width(290));
                    else
                        GUILayout.Box("Click an edge to select", GUILayout.Width(290));
                    break;
            }

            GUILayout.EndArea();

            Handles.EndGUI();
        }

        private Vector3 GetMouseWorldPosition(Event e, SceneView sceneView)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            Plane plane = new Plane(Vector3.up, Vector3.zero);
            float distance;
            if (plane.Raycast(ray, out distance))
            {
                return ray.GetPoint(distance);
            }
            return Vector3.zero;
        }

        private void ExportNetworkToJSON()
        {
            if (manager.AxisNetwork == null)
            {
                EditorUtility.DisplayDialog("Export Failed", "No axis network data to export.", "OK");
                return;
            }

            string directory = Path.GetDirectoryName(exportPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                Converters = new List<JsonConverter> { new Vector3Converter() }
            };

            string json = JsonConvert.SerializeObject(manager.AxisNetwork, settings);
            File.WriteAllText(exportPath, json);

            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Export Successful", $"Axis network exported to:\n{exportPath}", "OK");
            Debug.Log($"Axis network exported to: {exportPath}");
        }

        private void ImportNetworkFromJSON()
        {
            if (AnyAxisLayerFrozen())
            {
                EditorUtility.DisplayDialog("Import Blocked",
                    "Unfreeze every altitude layer before replacing the axis network.", "OK");
                return;
            }
            if (string.IsNullOrEmpty(importPath) || !File.Exists(importPath))
            {
                EditorUtility.DisplayDialog("Import Failed", "Invalid import path or file does not exist.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Import Network",
                "This will replace the current axis network. Continue?",
                "Yes", "No"))
            {
                return;
            }

            try
            {
                string json = File.ReadAllText(importPath);
                var settings = new JsonSerializerSettings
                {
                    ObjectCreationHandling = ObjectCreationHandling.Replace,
                    Converters = new List<JsonConverter> { new Vector3Converter() }
                };

                Network importedNetwork = JsonConvert.DeserializeObject<Network>(json, settings);

                if (importedNetwork != null)
                {
                    Undo.RecordObject(manager, "Import Axis Network");
                    manager.ClearAxisNetwork(false);
                    manager.AxisNetwork = importedNetwork;
                    manager.RebuildVisualsFromData();
                    activeLayerId = null;
                    EnsureActiveLayer();

                    // Clear foldout state.
                    nodeFoldouts.Clear();
                    edgeFoldouts.Clear();

                    EditorUtility.SetDirty(manager);

                    EditorUtility.DisplayDialog("Import Successful", $"Axis network imported from:\n{importPath}", "OK");
                    Debug.Log($"Axis network imported from: {importPath}");
                }
                else
                {
                    EditorUtility.DisplayDialog("Import Failed", "Failed to deserialize network data.", "OK");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Import Failed", $"Error: {ex.Message}", "OK");
                Debug.LogError($"Import failed: {ex.Message}");
            }
        }

        private void DrawGenerationBoundaryControls()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Generation Boundary", EditorStyles.boldLabel);
            GenerationBoundary boundary = manager.GetComponent<GenerationBoundary>();
            int count = boundary != null && boundary.vertices != null ? boundary.vertices.Count : 0;
            EditorGUILayout.HelpBox(
                count >= 3
                    ? "Polygon boundary is active (" + count + " vertices). The BoxCollider is only the sampling rectangle around it."
                    : "No polygon yet. Generation still uses the rectangular BoxCollider.",
                MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(count >= 3 ? "Reset Polygon From Box" : "Create Polygon From Box"))
                SeedBoundaryFromBox();
            editBoundary = GUILayout.Toggle(editBoundary, "Edit Polygon", "Button");
            EditorGUILayout.EndHorizontal();
            if (editBoundary)
                EditorGUILayout.HelpBox("Drag vertices in the Scene view. Shift+click an edge to insert a point. Delete removes the selected vertex.", MessageType.None);
        }

        private void SeedBoundaryFromBox()
        {
            BoxCollider box = manager.GetComponent<BoxCollider>();
            if (box == null) return;
            GenerationBoundary boundary = manager.GetComponent<GenerationBoundary>();
            if (boundary == null)
                boundary = Undo.AddComponent<GenerationBoundary>(manager.gameObject);
            Undo.RecordObject(boundary, "Seed Generation Polygon");
            boundary.SeedFromBox(box, 0f);
            EditorUtility.SetDirty(boundary);
            editBoundary = true;
            selectedBoundaryVertex = -1;
            SceneView.RepaintAll();
        }

        private bool DrawBoundaryEditor(Event e, SceneView sceneView)
        {
            GenerationBoundary boundary = manager.GetComponent<GenerationBoundary>();
            if (boundary == null || boundary.vertices == null || boundary.vertices.Count < 3)
                return false;

            Handles.color = new Color(0.95f, 0.55f, 0.1f, 1f);
            Vector3[] loop = new Vector3[boundary.vertices.Count + 1];
            for (int i = 0; i < boundary.vertices.Count; i++)
                loop[i] = boundary.vertices[i];
            loop[boundary.vertices.Count] = boundary.vertices[0];
            Handles.DrawAAPolyLine(3f, loop);

            for (int i = 0; i < boundary.vertices.Count; i++)
            {
                float size = HandleUtility.GetHandleSize(boundary.vertices[i]) * 0.08f;
                if (Handles.Button(boundary.vertices[i], Quaternion.identity, size, size, Handles.SphereHandleCap))
                    selectedBoundaryVertex = i;
            }

            if (selectedBoundaryVertex >= 0 && selectedBoundaryVertex < boundary.vertices.Count)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(boundary.vertices[selectedBoundaryVertex], Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(boundary, "Move Boundary Vertex");
                    moved.y = 0f;
                    boundary.vertices[selectedBoundaryVertex] = moved;
                    BoxCollider box = manager.GetComponent<BoxCollider>();
                    if (box != null)
                    {
                        Undo.RecordObject(box, "Fit Boundary Box");
                        boundary.FitBox(box);
                    }
                    EditorUtility.SetDirty(boundary);
                }
            }

            if (e.type == EventType.MouseDown && e.button == 0 && e.shift)
            {
                Vector3 hit = GetMouseWorldPosition(e, sceneView);
                hit.y = 0f;
                int edge = ClosestBoundaryEdge(boundary.vertices, hit, out float best);
                if (edge >= 0 && best < HandleUtility.GetHandleSize(hit) * 0.75f)
                {
                    Undo.RecordObject(boundary, "Insert Boundary Vertex");
                    boundary.vertices.Insert(edge + 1, hit);
                    selectedBoundaryVertex = edge + 1;
                    EditorUtility.SetDirty(boundary);
                    e.Use();
                }
            }

            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) &&
                selectedBoundaryVertex >= 0 && boundary.vertices.Count > 3)
            {
                Undo.RecordObject(boundary, "Delete Boundary Vertex");
                boundary.vertices.RemoveAt(selectedBoundaryVertex);
                selectedBoundaryVertex = -1;
                BoxCollider box = manager.GetComponent<BoxCollider>();
                if (box != null) boundary.FitBox(box);
                EditorUtility.SetDirty(boundary);
                e.Use();
                return true;
            }

            return editBoundary;
        }

        private static int ClosestBoundaryEdge(List<Vector3> vertices, Vector3 point, out float bestDistance)
        {
            bestDistance = float.MaxValue;
            int best = -1;
            for (int i = 0; i < vertices.Count; i++)
            {
                Vector3 a = vertices[i];
                Vector3 b = vertices[(i + 1) % vertices.Count];
                Vector3 ab = b - a;
                float denom = ab.sqrMagnitude;
                float t = denom < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(point - a, ab) / denom);
                float dist = Vector3.Distance(point, a + ab * t);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    best = i;
                }
            }
            return best;
        }
    }
}
