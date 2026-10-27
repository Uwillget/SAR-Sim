using UnityEngine;
using UnityEditor;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace RoadNetworkEditor.EditorScripts
{
    [CustomEditor(typeof(VertiportManager))]
    public class VertiportManagerEditor : Editor
    {
        private VertiportManager manager;

        private enum EditMode
        {
            Select,
            CreateVertiport,
            CreateHelipad,
            CreateHoldingStructure,
            CreateCircle,
            CreateVertex,
            CreateRamp,
            CreateNetwork,
            CreateCorridor
        }

        private static readonly string[] EditModeLabels =
        {
            "Select", "Vertiport", "Helipad",
            "Holding", "Ring", "Vertex",
            "Ramp", "Approach", "Corridor"
        };

        private enum ApprSubMode
        {
            ApprNode,
            ApprEdge
        }

        private EditMode currentMode = EditMode.Select;

        // Frozen position
        private bool freezeX = false;
        private bool freezeY = true;
        private bool freezeZ = false;
        private float frozenX = 0f;
        private float frozenY = 0f;
        private float frozenZ = 0f;

        // Preview position
        private Vector3 previewPos = Vector3.zero;

        // Create Vertiport / Helipad state
        private List<Vector3> polygonPoints = new List<Vector3>();
        private string selectedVertiportIdForHelipad = null;

        // Create Circle state
        private string selectedHSIdForCircle = null;
        private float newCircleRadius = 10f;
        private int newCircleVertexCount = 4;
        private float newCircleInitialAngle = 0f;

        // Create Ramp state
        private VertexVisual rampStartVertex = null;

        // Create Network state
        private ApprSubMode currentApprSubMode = ApprSubMode.ApprNode;
        private ApproachNodeVisual apprEdgeStartNode = null;
        private string selectedVertiportIdForApprNode = null;
        private int apprNodeVpDropdownIndex = 0;

        // Create Corridor state
        private enum CorridorPhase
        {
            SelectLane,
            SetLanePoint,
            SelectRamp,
            SetRampPoint
        }
        private CorridorPhase corridorPhase = CorridorPhase.SelectLane;
        private string corridorType = "Entry";
        private string selectedLaneId = null;
        private Vector3 laneLinkPoint = Vector3.zero;
        private string selectedRampId = null;
        private Vector3 rampLinkPoint = Vector3.zero;

        // Foldouts
        private bool showVertiportList = true;
        private Dictionary<string, bool> vpFoldouts = new Dictionary<string, bool>();
        private Dictionary<string, bool> hpFoldouts = new Dictionary<string, bool>();
        private Dictionary<string, bool> hsFoldouts = new Dictionary<string, bool>();
        private Dictionary<string, bool> apprNetFoldouts = new Dictionary<string, bool>();

        // File paths
        private string exportPath = "Assets/DataBase/VertiportData.json";
        private string importPath = "";

        private void OnEnable()
        {
            manager = (VertiportManager)target;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        public override void OnInspectorGUI()
        {
            if (manager == null) manager = (VertiportManager)target;

            serializedObject.Update();

            // Script field (read-only)
            GUI.enabled = false;
            EditorGUILayout.ObjectField("Script", MonoScript.FromMonoBehaviour(manager), typeof(VertiportManager), false);
            GUI.enabled = true;

            EditorGUILayout.Space(5);

            // Data list
            DrawVertiportDataList();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Visual Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("circleWidth"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("vertiportWidth"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("helipadWidth"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("vertexSize"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("circleSegments"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("showTextAnnotations"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("corridorTangentRatio"));

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Vertiport Editor", EditorStyles.boldLabel);

            DrawModeSelection();

            EditorGUILayout.Space(10);

            switch (currentMode)
            {
                case EditMode.Select:
                    DrawSelectModePanel();
                    break;
                case EditMode.CreateVertiport:
                    DrawCreateVertiportPanel();
                    break;
                case EditMode.CreateHelipad:
                    DrawCreateHelipadPanel();
                    break;
                case EditMode.CreateHoldingStructure:
                    DrawCreateHoldingStructurePanel();
                    break;
                case EditMode.CreateCircle:
                    DrawCreateCirclePanel();
                    break;
                case EditMode.CreateVertex:
                    DrawCreateVertexPanel();
                    break;
                case EditMode.CreateRamp:
                    DrawCreateRampPanel();
                    break;
                case EditMode.CreateNetwork:
                    DrawCreateNetworkPanel();
                    break;
                case EditMode.CreateCorridor:
                    DrawCreateCorridorPanel();
                    break;
            }

            EditorGUILayout.Space(10);

            if (currentMode == EditMode.Select)
            {
                DrawSelectionInfo();
                EditorGUILayout.Space(10);
            }

            DrawOperations();
            EditorGUILayout.Space(10);
            DrawFileOperations();
        }

        #region Data List

        private void DrawVertiportDataList()
        {
            EditorGUILayout.LabelField("Vertiport Data", EditorStyles.boldLabel);

            if (manager.Vertiports == null || manager.Vertiports.Count == 0)
            {
                EditorGUILayout.HelpBox("No vertiport data.", MessageType.Info);
                return;
            }

            showVertiportList = EditorGUILayout.Foldout(showVertiportList, $"Vertiports ({manager.Vertiports.Count})", true);
            if (!showVertiportList) return;

            EditorGUI.indentLevel++;

            for (int i = 0; i < manager.Vertiports.Count; i++)
            {
                Vertiport vp = manager.Vertiports[i];
                DrawVertiportItem(vp, i);
            }

            EditorGUI.indentLevel--;
        }

        private void DrawVertiportItem(Vertiport vp, int index)
        {
            string vpId = vp.VertiportId;
            if (!vpFoldouts.ContainsKey(vpId))
                vpFoldouts[vpId] = false;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            vpFoldouts[vpId] = EditorGUILayout.Foldout(vpFoldouts[vpId], vpId, true);

            if (GUILayout.Button("Focus", GUILayout.Width(50)))
            {
                VertiportVisual vis = manager.GetVertiportVisual(vpId);
                if (vis != null) { Selection.activeGameObject = vis.gameObject; SceneView.lastActiveSceneView.FrameSelected(); }
            }

            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("x", GUILayout.Width(25)))
            {
                if (EditorUtility.DisplayDialog("Delete Vertiport", $"Delete {vpId}?", "Yes", "No"))
                {
                    manager.DeleteVertiport(vpId);
                    EditorUtility.SetDirty(manager);
                    GUIUtility.ExitGUI();
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            if (vpFoldouts[vpId])
            {
                EditorGUI.indentLevel++;

                // VP boundary coordinates foldout (collapsed by default)
                string vpBoundKey = vpId + "_VPBoundary";
                if (!vpFoldouts.ContainsKey(vpBoundKey))
                    vpFoldouts[vpBoundKey] = false;
                vpFoldouts[vpBoundKey] = EditorGUILayout.Foldout(vpFoldouts[vpBoundKey], $"Boundary Points ({vp.Boundary.Count})", true);
                if (vpFoldouts[vpBoundKey])
                {
                    EditorGUI.indentLevel++;
                    VertiportVisual vpVisForEdit = manager.GetVertiportVisual(vpId);
                    for (int i = 0; i < vp.Boundary.Count; i++)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUI.BeginChangeCheck();
                        Vector3 newVal = EditorGUILayout.Vector3Field($"[{i}]", vp.Boundary[i]);
                        if (EditorGUI.EndChangeCheck())
                        {
                            vp.Boundary[i] = newVal;
                            if (vpVisForEdit != null) vpVisForEdit.UpdateVisual();
                            EditorUtility.SetDirty(manager);
                        }
                        GUI.backgroundColor = Color.red;
                        if (GUILayout.Button("x", GUILayout.Width(25)))
                        {
                            vp.Boundary.RemoveAt(i);
                            if (vpVisForEdit != null) vpVisForEdit.UpdateVisual();
                            EditorUtility.SetDirty(manager);
                            GUIUtility.ExitGUI();
                        }
                        GUI.backgroundColor = Color.white;
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }

                // ── Helipads group foldout ──
                string hpGroupKey = vpId + "_Helipads";
                if (!vpFoldouts.ContainsKey(hpGroupKey))
                    vpFoldouts[hpGroupKey] = false;
                vpFoldouts[hpGroupKey] = EditorGUILayout.Foldout(vpFoldouts[hpGroupKey], $"Helipads ({vp.Helipads.Count})", true);
                if (vpFoldouts[hpGroupKey])
                {
                    EditorGUI.indentLevel++;
                    foreach (var hp in vp.Helipads)
                    {
                        string hpKey = hp.HelipadId;
                        if (!hpFoldouts.ContainsKey(hpKey))
                            hpFoldouts[hpKey] = false;

                        EditorGUILayout.BeginHorizontal();
                        hpFoldouts[hpKey] = EditorGUILayout.Foldout(hpFoldouts[hpKey], hpKey, true);
                        if (GUILayout.Button("Focus", GUILayout.Width(50)))
                        {
                            HelipadVisual hVis = manager.GetHelipadVisual(hpKey);
                            if (hVis != null) { Selection.activeGameObject = hVis.gameObject; SceneView.lastActiveSceneView.FrameSelected(); }
                        }
                        GUI.backgroundColor = Color.red;
                        if (GUILayout.Button("x", GUILayout.Width(25)))
                        {
                            if (EditorUtility.DisplayDialog("Delete Helipad", $"Delete {hpKey}?", "Yes", "No"))
                            {
                                manager.DeleteHelipad(hpKey);
                                EditorUtility.SetDirty(manager);
                                GUIUtility.ExitGUI();
                            }
                        }
                        GUI.backgroundColor = Color.white;
                        EditorGUILayout.EndHorizontal();

                        if (hpFoldouts[hpKey])
                        {
                            EditorGUI.indentLevel++;

                            string hpBoundKey = hpKey + "_HPBoundary";
                            if (!hpFoldouts.ContainsKey(hpBoundKey))
                                hpFoldouts[hpBoundKey] = false;
                            hpFoldouts[hpBoundKey] = EditorGUILayout.Foldout(hpFoldouts[hpBoundKey], $"Boundary Points ({hp.Boundary.Count})", true);
                            if (hpFoldouts[hpBoundKey])
                            {
                                EditorGUI.indentLevel++;
                                HelipadVisual hVisForEdit = manager.GetHelipadVisual(hpKey);
                                for (int i = 0; i < hp.Boundary.Count; i++)
                                {
                                    EditorGUILayout.BeginHorizontal();
                                    EditorGUI.BeginChangeCheck();
                                    Vector3 newVal = EditorGUILayout.Vector3Field($"[{i}]", hp.Boundary[i]);
                                    if (EditorGUI.EndChangeCheck())
                                    {
                                        hp.Boundary[i] = newVal;
                                        if (hVisForEdit != null) hVisForEdit.UpdateVisual();
                                        manager.SyncApproachNodeToHelipad(hpKey);
                                        EditorUtility.SetDirty(manager);
                                    }
                                    GUI.backgroundColor = Color.red;
                                    if (GUILayout.Button("x", GUILayout.Width(25)))
                                    {
                                        hp.Boundary.RemoveAt(i);
                                        if (hVisForEdit != null) hVisForEdit.UpdateVisual();
                                        manager.SyncApproachNodeToHelipad(hpKey);
                                        EditorUtility.SetDirty(manager);
                                        GUIUtility.ExitGUI();
                                    }
                                    GUI.backgroundColor = Color.white;
                                    EditorGUILayout.EndHorizontal();
                                }
                                EditorGUI.indentLevel--;
                            }

                            // Reachable Corridors
                            if (hp.ReachableCorridorIds != null && hp.ReachableCorridorIds.Count > 0)
                                EditorGUILayout.LabelField("Reachable Corridors", string.Join(", ", hp.ReachableCorridorIds));
                            else
                                EditorGUILayout.LabelField("Reachable Corridors", "(none computed)");

                            EditorGUI.indentLevel--;
                        }
                    }
                    EditorGUI.indentLevel--;
                }

                // ── HoldingStructures group foldout ──
                string hsGroupKey = vpId + "_HS";
                if (!vpFoldouts.ContainsKey(hsGroupKey))
                    vpFoldouts[hsGroupKey] = false;
                vpFoldouts[hsGroupKey] = EditorGUILayout.Foldout(vpFoldouts[hsGroupKey], $"HoldingStructures ({vp.HoldingStructures.Count})", true);
                if (vpFoldouts[hsGroupKey])
                {
                    EditorGUI.indentLevel++;
                    foreach (var hs in vp.HoldingStructures)
                    {
                        string hsKey = hs.HoldingStructureId;
                        if (!hsFoldouts.ContainsKey(hsKey))
                            hsFoldouts[hsKey] = false;

                        EditorGUILayout.BeginHorizontal();
                        hsFoldouts[hsKey] = EditorGUILayout.Foldout(hsFoldouts[hsKey], hsKey, true);

                        GUI.backgroundColor = Color.red;
                        if (GUILayout.Button("x", GUILayout.Width(25)))
                        {
                            if (EditorUtility.DisplayDialog("Delete HS", $"Delete {hsKey}?", "Yes", "No"))
                            {
                                manager.DeleteHoldingStructure(hsKey);
                                EditorUtility.SetDirty(manager);
                                GUIUtility.ExitGUI();
                            }
                        }
                        GUI.backgroundColor = Color.white;
                        EditorGUILayout.EndHorizontal();

                        if (hsFoldouts[hsKey])
                        {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.LabelField("Position", hs.Position.ToString());
                            EditorGUILayout.LabelField($"Circles: {hs.Circles.Count}  Vertices: {hs.Vertices.Count}  Ramps: {hs.Ramps.Count}");

                            foreach (var c in hs.Circles)
                            {
                                EditorGUILayout.LabelField($"  {c.CircleId}", $"R={c.Radius:F1}  Verts={c.CoveredVertices.Count}");
                            }
                            foreach (var r in hs.Ramps)
                            {
                                string type = string.IsNullOrEmpty(r.LinkCircleId) ? "straight" : "arc";
                                EditorGUILayout.LabelField($"  {r.RampId}", $"{r.FromVertexId} -> {r.ToVertexId} ({type})");
                            }

                            EditorGUI.indentLevel--;
                        }
                    }
                    EditorGUI.indentLevel--;
                }

                // ── Approach Network foldout (expanded by default) ──
                if (vp.ApproachNet != null)
                {
                    string apprKey = vp.VertiportId + "_ApprNet";
                    if (!apprNetFoldouts.ContainsKey(apprKey))
                        apprNetFoldouts[apprKey] = false;

                    int nCount = vp.ApproachNet.Nodes.Count;
                    int eCount = vp.ApproachNet.Edges.Count;

                    EditorGUILayout.BeginHorizontal();
                    apprNetFoldouts[apprKey] = EditorGUILayout.Foldout(apprNetFoldouts[apprKey], $"Approach Network ({nCount} nodes, {eCount} edges)", true);
                    GUI.backgroundColor = Color.red;
                    if (GUILayout.Button("x", GUILayout.Width(25)))
                    {
                        if (EditorUtility.DisplayDialog("Clear Approach Network", $"Delete all nodes and edges in approach network of {vp.VertiportId}?", "Yes", "No"))
                        {
                            if (vp.ApproachNet != null)
                            {
                                foreach (var e in vp.ApproachNet.Edges.ToList())
                                    manager.DeleteApproachEdge(e.EdgeId);
                                foreach (var n in vp.ApproachNet.Nodes.ToList())
                                    manager.DeleteApproachNode(n.NodeId);
                            }
                            EditorUtility.SetDirty(manager);
                            GUIUtility.ExitGUI();
                        }
                    }
                    GUI.backgroundColor = Color.white;
                    EditorGUILayout.EndHorizontal();

                    if (apprNetFoldouts[apprKey])
                    {
                        EditorGUI.indentLevel++;

                        EditorGUILayout.LabelField("Nodes", EditorStyles.miniBoldLabel);
                        foreach (var node in vp.ApproachNet.Nodes.ToList())
                        {
                            EditorGUILayout.BeginHorizontal();
                            string anchorTag = node.IsAnchor ? " [Anchor]" : "";
                            EditorGUILayout.LabelField($"{node.NodeId}{anchorTag}", $"{node.ConnectedEdges.Count} edges");

                            if (GUILayout.Button("Focus", GUILayout.Width(50)))
                            {
                                ApproachNodeVisual nVis = manager.GetApproachNodeVisual(node.NodeId);
                                if (nVis != null) { Selection.activeGameObject = nVis.gameObject; SceneView.lastActiveSceneView.FrameSelected(); }
                            }

                            GUI.backgroundColor = Color.red;
                            if (GUILayout.Button("x", GUILayout.Width(25)))
                            {
                                manager.DeleteApproachNode(node.NodeId);
                                EditorUtility.SetDirty(manager);
                                GUIUtility.ExitGUI();
                            }
                            GUI.backgroundColor = Color.white;
                            EditorGUILayout.EndHorizontal();
                        }

                        EditorGUILayout.LabelField("Edges", EditorStyles.miniBoldLabel);
                        foreach (var edge in vp.ApproachNet.Edges.ToList())
                        {
                            EditorGUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField($"{edge.EdgeId}", $"{edge.FromNodeId} -> {edge.ToNodeId}");

                            if (GUILayout.Button("Focus", GUILayout.Width(50)))
                            {
                                ApproachEdgeVisual eVis = manager.GetApproachEdgeVisual(edge.EdgeId);
                                if (eVis != null) { Selection.activeGameObject = eVis.gameObject; SceneView.lastActiveSceneView.FrameSelected(); }
                            }

                            GUI.backgroundColor = Color.red;
                            if (GUILayout.Button("x", GUILayout.Width(25)))
                            {
                                manager.DeleteApproachEdge(edge.EdgeId);
                                EditorUtility.SetDirty(manager);
                                GUIUtility.ExitGUI();
                            }
                            GUI.backgroundColor = Color.white;
                            EditorGUILayout.EndHorizontal();
                        }

                        EditorGUI.indentLevel--;
                    }
                }

                // ── Corridors group foldout ──
                string corrGroupKey = vpId + "_Corridors";
                if (!vpFoldouts.ContainsKey(corrGroupKey))
                    vpFoldouts[corrGroupKey] = false;
                vpFoldouts[corrGroupKey] = EditorGUILayout.Foldout(vpFoldouts[corrGroupKey], $"Corridors ({vp.Corridors.Count})", true);
                if (vpFoldouts[corrGroupKey])
                {
                    EditorGUI.indentLevel++;
                    foreach (var corr in vp.Corridors.ToList())
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(corr.CorridorId, $"{corr.CorridorType}: Lane={corr.LinkLaneId} Ramp={corr.LinkRampId}");

                        if (GUILayout.Button("Focus", GUILayout.Width(50)))
                        {
                            CorridorVisual cVis = manager.GetCorridorVisual(corr.CorridorId);
                            if (cVis != null) { Selection.activeGameObject = cVis.gameObject; SceneView.lastActiveSceneView.FrameSelected(); }
                        }
                        GUI.backgroundColor = Color.red;
                        if (GUILayout.Button("x", GUILayout.Width(25)))
                        {
                            manager.DeleteCorridor(corr.CorridorId);
                            EditorUtility.SetDirty(manager);
                            GUIUtility.ExitGUI();
                        }
                        GUI.backgroundColor = Color.white;
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

    #endregion

        #region Mode Selection

        private void DrawModeSelection()
        {
            EditorGUILayout.LabelField("Edit Mode", EditorStyles.boldLabel);

            int cur = (int)currentMode;
            int selected = GUILayout.SelectionGrid(cur, EditModeLabels, 3,
                GUILayout.ExpandWidth(true));
            EditMode newMode = (EditMode)selected;

            if (newMode != currentMode)
            {
                currentMode = newMode;
                manager.ClearSelection();
                polygonPoints.Clear();
                rampStartVertex = null;
                selectedVertiportIdForApprNode = null;
                apprEdgeStartNode = null;
                currentApprSubMode = ApprSubMode.ApprNode;
                corridorPhase = CorridorPhase.SelectLane;
                selectedLaneId = null;
                selectedRampId = null;
                SceneView.RepaintAll();
            }
        }

        private void DrawSelectModePanel()
        {
            EditorGUILayout.HelpBox("Click objects in Scene view to select.\nDrag PositionHandle to move.", MessageType.Info);
        }

        private void DrawCreateVertiportPanel()
        {
            EditorGUILayout.HelpBox("Click in Scene to place boundary points.\nRight-click or press Enter to close polygon and create Vertiport.", MessageType.Info);
            EditorGUILayout.LabelField("Points placed", polygonPoints.Count.ToString());
            EditorGUILayout.LabelField("Preview", previewPos.ToString());
            EditorGUILayout.Space(5);
            DrawFrozenPosition();
            if (polygonPoints.Count > 0 && GUILayout.Button("Cancel"))
            {
                polygonPoints.Clear();
                SceneView.RepaintAll();
            }
        }

        private void DrawCreateHelipadPanel()
        {
            if (string.IsNullOrEmpty(selectedVertiportIdForHelipad))
            {
                EditorGUILayout.HelpBox("First, select a Vertiport in Select mode, then switch to this mode.\nOr enter Vertiport ID manually:", MessageType.Info);
                selectedVertiportIdForHelipad = EditorGUILayout.TextField("Vertiport ID", selectedVertiportIdForHelipad);
            }
            else
            {
                EditorGUILayout.LabelField("Parent Vertiport", selectedVertiportIdForHelipad);
                EditorGUILayout.HelpBox("Click in Scene to place helipad boundary points.\nRight-click to close and create.", MessageType.Info);
                EditorGUILayout.LabelField("Points placed", polygonPoints.Count.ToString());
            }
            EditorGUILayout.Space(5);
            DrawFrozenPosition();
            if (polygonPoints.Count > 0 && GUILayout.Button("Cancel"))
            {
                polygonPoints.Clear();
                selectedVertiportIdForHelipad = null;
                SceneView.RepaintAll();
            }
        }

        private void DrawCreateHoldingStructurePanel()
        {
            EditorGUILayout.HelpBox("Click in Scene to place a HoldingStructure position.", MessageType.Info);
            EditorGUILayout.LabelField("Preview", previewPos.ToString());
            EditorGUILayout.Space(5);
            DrawFrozenPosition();
        }

        private void DrawCreateCirclePanel()
        {
            // Auto-freeze XZ to selected HS position
            if (!string.IsNullOrEmpty(selectedHSIdForCircle))
            {
                HoldingStructure hs = FindHoldingStructureById(selectedHSIdForCircle);
                if (hs != null)
                {
                    freezeX = true;
                    freezeZ = true;
                    frozenX = hs.Position.x;
                    frozenZ = hs.Position.z;
                }
            }

            string helpText = "Select a HoldingStructure below or click any HS vertex/circle in Scene view.\nClick again to place the circle center.";
            if (!string.IsNullOrEmpty(selectedHSIdForCircle))
                helpText += "\n\nXZ auto-frozen to HS position. Uncheck to freely place.";
            EditorGUILayout.HelpBox(helpText, MessageType.Info);

            // Dropdown for HS selection
            var hsIds = GetAllHSIds();
            int selIdx = hsIds.IndexOf(selectedHSIdForCircle ?? "");
            int newIdx = EditorGUILayout.Popup("Selected HS", selIdx, hsIds.ToArray());
            if (newIdx >= 0 && newIdx < hsIds.Count && hsIds[newIdx] != selectedHSIdForCircle)
                selectedHSIdForCircle = hsIds[newIdx];
            newCircleRadius = EditorGUILayout.FloatField("Radius", newCircleRadius);
            newCircleVertexCount = EditorGUILayout.IntSlider("Vertex Count", newCircleVertexCount, 2, 32);
            newCircleInitialAngle = EditorGUILayout.Slider("Initial Angle", newCircleInitialAngle, 0f, 360f);
            EditorGUILayout.LabelField("Preview Center", previewPos.ToString());
            EditorGUILayout.Space(5);
            DrawFrozenPosition();

            if (!string.IsNullOrEmpty(selectedHSIdForCircle) && GUILayout.Button("Create Circle"))
            {
                Vector3 pos = ApplyFrozenAxes(previewPos);
                manager.CreateCircle(selectedHSIdForCircle, pos, newCircleRadius, newCircleVertexCount, newCircleInitialAngle);
                EditorUtility.SetDirty(manager);
                SceneView.RepaintAll();
            }
        }

        private void DrawCreateVertexPanel()
        {
            EditorGUILayout.HelpBox("Click on or near a Circle in Scene view to add a vertex at that position (projected onto the circle arc).", MessageType.Info);
            EditorGUILayout.LabelField("Preview", previewPos.ToString());
            EditorGUILayout.Space(5);
            DrawFrozenPosition();
        }

        private ArcDirection newRampDirection = ArcDirection.Shortest;

        private void DrawCreateRampPanel()
        {
            EditorGUILayout.HelpBox("Click first vertex (From), then click second vertex (To).\nSame circle = arc ramp, different circles = straight ramp.", MessageType.Info);

            newRampDirection = (ArcDirection)EditorGUILayout.EnumPopup("Arc Direction", newRampDirection);

            if (rampStartVertex != null)
                EditorGUILayout.LabelField("From Vertex", rampStartVertex.VertexId);
            else
                EditorGUILayout.LabelField("From Vertex", "None (click a vertex)");

            if (rampStartVertex != null && GUILayout.Button("Cancel"))
            {
                rampStartVertex = null;
                SceneView.RepaintAll();
            }
        }

        private void DrawCreateNetworkPanel()
        {
            // Sub-mode buttons
            EditorGUILayout.LabelField("Network Sub-Mode", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(currentApprSubMode == ApprSubMode.ApprNode, "Appr Node", "Button"))
                currentApprSubMode = ApprSubMode.ApprNode;
            if (GUILayout.Toggle(currentApprSubMode == ApprSubMode.ApprEdge, "Appr Edge", "Button"))
                currentApprSubMode = ApprSubMode.ApprEdge;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            if (currentApprSubMode == ApprSubMode.ApprNode)
            {
                if (string.IsNullOrEmpty(selectedVertiportIdForApprNode))
                {
                    // Try to inherit selection from Select mode
                    VertiportVisual sv = manager.GetSelectedVertiport();
                    if (sv != null) selectedVertiportIdForApprNode = sv.VertiportId;

                    // Auto-detect if only one VP exists
                    if (string.IsNullOrEmpty(selectedVertiportIdForApprNode) && manager.Vertiports.Count == 1)
                        selectedVertiportIdForApprNode = manager.Vertiports[0].VertiportId;

                    // Show dropdown if multiple VPs available
                    if (string.IsNullOrEmpty(selectedVertiportIdForApprNode) && manager.Vertiports.Count > 1)
                    {
                        EditorGUILayout.HelpBox("Select a target Vertiport:", MessageType.Info);
                        List<string> vpIds = manager.Vertiports.Select(v => v.VertiportId).ToList();
                        apprNodeVpDropdownIndex = EditorGUILayout.Popup("Vertiport", apprNodeVpDropdownIndex, vpIds.ToArray());
                        if (GUILayout.Button("Confirm Vertiport"))
                        {
                            selectedVertiportIdForApprNode = vpIds[apprNodeVpDropdownIndex];
                            SceneView.RepaintAll();
                        }
                    }

                    if (!string.IsNullOrEmpty(selectedVertiportIdForApprNode))
                    {
                        EditorGUILayout.LabelField("Using Vertiport", selectedVertiportIdForApprNode);
                        if (GUILayout.Button("Change")) selectedVertiportIdForApprNode = null;
                    }
                    else if (manager.Vertiports.Count == 0)
                    {
                        EditorGUILayout.HelpBox("No vertiports exist. Create a Vertiport first.", MessageType.Warning);
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("Parent Vertiport", selectedVertiportIdForApprNode);
                    EditorGUILayout.HelpBox("Click in Scene to place an approach node.\nClick on an HS anchor to create an anchor approach node.", MessageType.Info);
                    EditorGUILayout.LabelField("Preview", previewPos.ToString());
                    EditorGUILayout.Space(5);
                    DrawFrozenPosition();

                    if (GUILayout.Button("Cancel"))
                    {
                        selectedVertiportIdForApprNode = null;
                        SceneView.RepaintAll();
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Click first approach node (From), then click second node (To) to create a directed edge.", MessageType.Info);

                if (apprEdgeStartNode != null)
                    EditorGUILayout.LabelField("From Node", apprEdgeStartNode.NodeId);
                else
                    EditorGUILayout.LabelField("From Node", "None (click a node)");

                if (apprEdgeStartNode != null && GUILayout.Button("Cancel"))
                {
                    apprEdgeStartNode = null;
                    SceneView.RepaintAll();
                }
            }
        }

        private void DrawCreateCorridorPanel()
        {
            EditorGUILayout.LabelField("Corridor Type", EditorStyles.boldLabel);
            if (GUILayout.Toggle(corridorType == "Entry", "Entry", "Button"))
                corridorType = "Entry";
            if (GUILayout.Toggle(corridorType == "Exit", "Exit", "Button"))
                corridorType = "Exit";

            EditorGUILayout.Space(5);

            string phaseLabel = corridorPhase switch
            {
                CorridorPhase.SelectLane => "Phase 1/4: Click a lane to select it",
                CorridorPhase.SetLanePoint => "Phase 2/4: Shift+Click to set lane link point, Double-click to confirm",
                CorridorPhase.SelectRamp => "Phase 3/4: Click a ramp to select it",
                CorridorPhase.SetRampPoint => "Phase 4/4: Shift+Click to set ramp link point, Double-click to finish",
                _ => ""
            };
            EditorGUILayout.HelpBox(phaseLabel, MessageType.Info);

            if (!string.IsNullOrEmpty(selectedLaneId))
                EditorGUILayout.LabelField("Selected Lane", selectedLaneId);
            if (laneLinkPoint != Vector3.zero)
                EditorGUILayout.LabelField("Lane Link Point", laneLinkPoint.ToString());
            if (!string.IsNullOrEmpty(selectedRampId))
                EditorGUILayout.LabelField("Selected Ramp", selectedRampId);
            if (rampLinkPoint != Vector3.zero)
                EditorGUILayout.LabelField("Ramp Link Point", rampLinkPoint.ToString());

            if (corridorPhase != CorridorPhase.SelectLane && GUILayout.Button("Cancel / Reset"))
            {
                corridorPhase = CorridorPhase.SelectLane;
                selectedLaneId = null;
                laneLinkPoint = Vector3.zero;
                selectedRampId = null;
                rampLinkPoint = Vector3.zero;
                SceneView.RepaintAll();
            }
        }

        private void DrawFrozenPosition()
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

        private Vector3 ApplyFrozenAxes(Vector3 pos)
        {
            return new Vector3(
                freezeX ? frozenX : pos.x,
                freezeY ? frozenY : pos.y,
                freezeZ ? frozenZ : pos.z
            );
        }

        #endregion

        #region Selection Info

        private void DrawSelectionInfo()
        {
            EditorGUILayout.LabelField("Selection Info", EditorStyles.boldLabel);

            VertiportVisual sv = manager.GetSelectedVertiport();
            HelipadVisual sh = manager.GetSelectedHelipad();
            CircleVisual sc = manager.GetSelectedCircle();
            VertexVisual svx = manager.GetSelectedVertex();
            RampVisual sr = manager.GetSelectedRamp();
            ApproachNodeVisual san = manager.GetSelectedApproachNode();
            ApproachEdgeVisual sae = manager.GetSelectedApproachEdge();
            CorridorVisual sco = manager.GetSelectedCorridor();

            if (sv != null) DrawVertiportSelectionInfo(sv);
            else if (sh != null) DrawHelipadSelectionInfo(sh);
            else if (sc != null) DrawCircleSelectionInfo(sc);
            else if (svx != null) DrawVertexSelectionInfo(svx);
            else if (sr != null) DrawRampSelectionInfo(sr);
            else if (san != null) DrawApproachNodeSelectionInfo(san);
            else if (sae != null) DrawApproachEdgeSelectionInfo(sae);
            else if (sco != null) DrawCorridorSelectionInfo(sco);
            else EditorGUILayout.LabelField("No Selection", EditorStyles.miniLabel);
        }

        private void DrawVertiportSelectionInfo(VertiportVisual v)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Vertiport", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", v.VertiportId);
            if (v.VertiportData != null)
            {
                EditorGUILayout.LabelField("Boundary Points", v.VertiportData.Boundary.Count.ToString());
                EditorGUILayout.LabelField("Helipads", v.VertiportData.Helipads.Count.ToString());
                EditorGUILayout.LabelField("HoldingStructures", v.VertiportData.HoldingStructures.Count.ToString());
            }
            if (GUILayout.Button("Deselect")) manager.ClearSelection();
            EditorGUILayout.EndVertical();
        }

        private void DrawHelipadSelectionInfo(HelipadVisual h)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Helipad", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", h.HelipadId);
            EditorGUILayout.LabelField("Parent Vertiport", h.ParentVertiportId);
            if (h.HelipadData != null)
            {
                EditorGUILayout.LabelField("Boundary Points", h.HelipadData.Boundary.Count.ToString());
                string reachable = (h.HelipadData.ReachableCorridorIds != null && h.HelipadData.ReachableCorridorIds.Count > 0)
                    ? string.Join(", ", h.HelipadData.ReachableCorridorIds) : "(none)";
                EditorGUILayout.LabelField("Reachable Corridors", reachable);
            }
            if (GUILayout.Button("Deselect")) manager.ClearSelection();
            EditorGUILayout.EndVertical();
        }

        private void DrawCircleSelectionInfo(CircleVisual c)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Circle", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", c.CircleId);
            EditorGUILayout.LabelField("Parent HS", c.ParentHoldingStructureId);
            if (c.CircleData != null)
            {
                EditorGUILayout.LabelField("Center", c.CircleData.Center.ToString());
                EditorGUILayout.LabelField("Radius", c.CircleData.Radius.ToString("F2"));
                EditorGUILayout.LabelField("Vertices", c.CircleData.CoveredVertices.Count.ToString());

                EditorGUI.BeginChangeCheck();
                float newRadius = EditorGUILayout.FloatField("Radius", c.CircleData.Radius);
                float newAngle = EditorGUILayout.Slider("Initial Angle", c.CircleData.InitialAngle, 0f, 360f);
                if (EditorGUI.EndChangeCheck())
                    manager.UpdateCircle(c.CircleId, c.CircleData.Center, newRadius, c.CircleData.CoveredVertices.Count, newAngle);
            }
            if (GUILayout.Button("Deselect")) manager.ClearSelection();
            EditorGUILayout.EndVertical();
        }

        private void DrawVertexSelectionInfo(VertexVisual v)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Vertex", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", v.VertexId);
            EditorGUILayout.LabelField("Link Circle", v.LinkCircleId);
            if (v.VertexData != null)
            {
                EditorGUILayout.LabelField("Position", v.VertexData.Position.ToString());
                EditorGUILayout.LabelField("Connected Ramps", string.Join(", ", v.VertexData.ConnectedRamps));
            }
            if (GUILayout.Button("Deselect")) manager.ClearSelection();
            EditorGUILayout.EndVertical();
        }

        private void DrawRampSelectionInfo(RampVisual r)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Ramp", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", r.RampId);
            if (r.RampData != null)
            {
                EditorGUILayout.LabelField("From", r.RampData.FromVertexId);
                EditorGUILayout.LabelField("To", r.RampData.ToVertexId);
                string type = string.IsNullOrEmpty(r.LinkCircleId) ? "Straight (inter-circle)" : $"Arc (circle {r.LinkCircleId})";
                EditorGUILayout.LabelField("Type", type);
            }
            if (GUILayout.Button("Deselect")) manager.ClearSelection();
            EditorGUILayout.EndVertical();
        }

        private void DrawApproachNodeSelectionInfo(ApproachNodeVisual n)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Approach Node", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", n.NodeId);
            EditorGUILayout.LabelField("Parent Vertiport", n.ParentVertiportId);
            if (n.NodeData != null)
            {
                EditorGUILayout.LabelField("Position", n.NodeData.Position.ToString());
                EditorGUILayout.LabelField("Is Anchor", n.NodeData.IsAnchor.ToString());
                EditorGUILayout.LabelField("Linked HS", n.NodeData.LinkHSId ?? "null");
                EditorGUILayout.LabelField("Connected Edges", string.Join(", ", n.NodeData.ConnectedEdges));
            }
            if (GUILayout.Button("Deselect")) manager.ClearSelection();
            EditorGUILayout.EndVertical();
        }

        private void DrawApproachEdgeSelectionInfo(ApproachEdgeVisual e)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Approach Edge", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", e.EdgeId);
            if (e.EdgeData != null)
            {
                EditorGUILayout.LabelField("From", e.EdgeData.FromNodeId);
                EditorGUILayout.LabelField("To", e.EdgeData.ToNodeId);
            }
            if (GUILayout.Button("Deselect")) manager.ClearSelection();
            EditorGUILayout.EndVertical();
        }

        private void DrawCorridorSelectionInfo(CorridorVisual c)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Corridor", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", c.CorridorId);
            if (c.CorridorData != null)
            {
                EditorGUILayout.LabelField("Type", c.CorridorData.CorridorType);
                EditorGUILayout.LabelField("Link Lane", c.CorridorData.LinkLaneId);
                EditorGUILayout.LabelField("Link Ramp", c.CorridorData.LinkRampId);
                EditorGUILayout.LabelField("Lane Point", c.CorridorData.LaneLinkPoint.ToString());
                EditorGUILayout.LabelField("Ramp Point", c.CorridorData.RampLinkPoint.ToString());
            }
            if (GUILayout.Button("Deselect")) manager.ClearSelection();
            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Operations

        private void DrawOperations()
        {
            EditorGUILayout.LabelField("Operations", EditorStyles.boldLabel);

            if (GUILayout.Button("Clear All Data"))
            {
                if (EditorUtility.DisplayDialog("Clear All", "Delete all vertiport data?", "Yes", "No"))
                {
                    manager.ClearAllData();
                    vpFoldouts.Clear();
                    hpFoldouts.Clear();
                    hsFoldouts.Clear();
                    apprNetFoldouts.Clear();
                    EditorUtility.SetDirty(manager);
                }
            }

            if (GUILayout.Button("Rebuild Visuals from Data"))
            {
                manager.RebuildVisualsFromData();
                EditorUtility.SetDirty(manager);
            }
        }

        private void DrawFileOperations()
        {
            EditorGUILayout.LabelField("File Operations", EditorStyles.boldLabel);

            exportPath = EditorGUILayout.TextField("Export Path", exportPath);

            if (GUILayout.Button("Export to JSON"))
            {
                manager.ExportToJSON();
            }

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            importPath = EditorGUILayout.TextField("Import Path", importPath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string path = EditorUtility.OpenFilePanel("Import Vertiport Data", "Assets/DataBase", "json");
                if (!string.IsNullOrEmpty(path)) importPath = path;
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Import from JSON"))
            {
                if (EditorUtility.DisplayDialog("Import", "Replace current data?", "Yes", "No"))
                {
                    manager.ImportFromJSON(string.IsNullOrEmpty(importPath) ? null : importPath);
                    vpFoldouts.Clear();
                    hpFoldouts.Clear();
                    hsFoldouts.Clear();
                    EditorUtility.SetDirty(manager);
                }
            }
        }

        #endregion

        #region Scene GUI

        private void OnSceneGUI(SceneView sceneView)
        {
            if (manager == null) return;

            Event e = Event.current;

            // Draw PositionHandle for selected objects in Select mode
            if (currentMode == EditMode.Select)
            {
                DrawSelectionHandle();
            }

            switch (currentMode)
            {
                case EditMode.Select:
                    HandleSelectMode(e, sceneView);
                    break;
                case EditMode.CreateVertiport:
                    HandleCreateVertiportMode(e, sceneView);
                    break;
                case EditMode.CreateHelipad:
                    HandleCreateHelipadMode(e, sceneView);
                    break;
                case EditMode.CreateHoldingStructure:
                    HandleCreateHSMode(e, sceneView);
                    break;
                case EditMode.CreateCircle:
                    HandleCreateCircleMode(e, sceneView);
                    break;
                case EditMode.CreateVertex:
                    HandleCreateVertexMode(e, sceneView);
                    break;
                case EditMode.CreateRamp:
                    HandleCreateRampMode(e, sceneView);
                    break;
                case EditMode.CreateNetwork:
                    HandleCreateNetworkMode(e, sceneView);
                    break;
                case EditMode.CreateCorridor:
                    HandleCreateCorridorMode(e, sceneView);
                    break;
            }

            DrawSceneHelpers(sceneView);
        }

        private void DrawSelectionHandle()
        {
            VertiportVisual sv = manager.GetSelectedVertiport();
            HelipadVisual sh = manager.GetSelectedHelipad();
            VertexVisual svx = manager.GetSelectedVertex();

            // VP-level Position + Rotation handle (moves entire vertiport)
            if (sv != null && sv.VertiportData != null)
            {
                Vector3 vpCenter = Vector3.zero;
                var boundary = sv.VertiportData.Boundary;
                if (boundary.Count > 0)
                {
                    foreach (var p in boundary) vpCenter += p;
                    vpCenter /= boundary.Count;
                }

                EditorGUI.BeginChangeCheck();
                Vector3 newCenter = Handles.PositionHandle(vpCenter, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Vector3 delta = newCenter - vpCenter;
                    manager.OffsetVertiport(sv.VertiportId, delta);
                }

                EditorGUI.BeginChangeCheck();
                Quaternion newRot = Handles.RotationHandle(Quaternion.identity, vpCenter);
                if (EditorGUI.EndChangeCheck())
                {
                    float angle = Quaternion.Angle(Quaternion.identity, newRot);
                    Vector3 axis = Vector3.up;
                    manager.RotateVertiport(sv.VertiportId, vpCenter, angle);
                }
            }

            // VP boundary corner handles
            if (sv != null && sv.VertiportData != null && sv.VertiportData.Boundary.Count > 0)
            {
                var boundary = sv.VertiportData.Boundary;
                for (int i = 0; i < boundary.Count; i++)
                {
                    float size = HandleUtility.GetHandleSize(boundary[i]) * 0.06f;
                    Handles.color = Color.white;
                    Handles.SphereHandleCap(0, boundary[i], Quaternion.identity, size, EventType.Repaint);

                    EditorGUI.BeginChangeCheck();
                    Vector3 newPos = Handles.PositionHandle(boundary[i], Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        boundary[i] = newPos;
                        sv.UpdateVisual();
                        EditorUtility.SetDirty(manager);
                    }
                }
            }

            // HP boundary corner handles
            if (sh != null && sh.HelipadData != null && sh.HelipadData.Boundary.Count > 0)
            {
                var boundary = sh.HelipadData.Boundary;
                for (int i = 0; i < boundary.Count; i++)
                {
                    float size = HandleUtility.GetHandleSize(boundary[i]) * 0.05f;
                    Handles.color = Color.white;
                    Handles.SphereHandleCap(0, boundary[i], Quaternion.identity, size, EventType.Repaint);

                    EditorGUI.BeginChangeCheck();
                    Vector3 newPos = Handles.PositionHandle(boundary[i], Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        boundary[i] = newPos;
                        sh.UpdateVisual();
                        manager.SyncApproachNodeToHelipad(sh.HelipadId);
                        EditorUtility.SetDirty(manager);
                    }
                }
            }

            if (svx != null)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 newPos = Handles.PositionHandle(svx.transform.position, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    svx.UpdatePosition(newPos);
                    EditorUtility.SetDirty(manager);
                }
            }

            // Circle center handle
            CircleVisual sc = manager.GetSelectedCircle();
            if (sc != null && sc.CircleData != null)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 newCenter = Handles.PositionHandle(sc.CircleData.Center, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    manager.UpdateCircle(sc.CircleId, newCenter, sc.CircleData.Radius, sc.CircleData.CoveredVertices.Count);
                    EditorUtility.SetDirty(manager);
                }
            }

            // Approach node position handle
            ApproachNodeVisual san = manager.GetSelectedApproachNode();
            if (san != null)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 newPos = Handles.PositionHandle(san.transform.position, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    san.UpdatePosition(newPos);
                    EditorUtility.SetDirty(manager);
                }
            }

            // Corridor link point handles
            CorridorVisual sco = manager.GetSelectedCorridor();
            if (sco != null && sco.CorridorData != null)
            {
                NetConnectedCorridor cData = sco.CorridorData;

                // Lane link point (green, constrained to lane centerline)
                {
                    float size = HandleUtility.GetHandleSize(cData.LaneLinkPoint) * 0.07f;
                    Handles.color = Color.green;
                    Handles.SphereHandleCap(0, cData.LaneLinkPoint, Quaternion.identity, size, EventType.Repaint);
                    EditorGUI.BeginChangeCheck();
                    Vector3 newPos = Handles.PositionHandle(cData.LaneLinkPoint, Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        cData.LaneLinkPoint = SnapToLaneCenterline(cData.LinkLaneId, newPos);
                        sco.UpdateVisual();
                        // Sync back to Ramp.LinkCorridors
                        SyncCorridorLinkPointToRamp(cData);
                        EditorUtility.SetDirty(manager);
                    }
                }

                // Ramp link point (magenta, constrained to ramp line)
                {
                    float size = HandleUtility.GetHandleSize(cData.RampLinkPoint) * 0.07f;
                    Handles.color = Color.magenta;
                    Handles.SphereHandleCap(0, cData.RampLinkPoint, Quaternion.identity, size, EventType.Repaint);
                    EditorGUI.BeginChangeCheck();
                    Vector3 newPos = Handles.PositionHandle(cData.RampLinkPoint, Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        cData.RampLinkPoint = SnapToRampLine(cData.LinkRampId, newPos);
                        sco.UpdateVisual();
                        SyncCorridorLinkPointToRamp(cData);
                        EditorUtility.SetDirty(manager);
                    }
                }

                // Draw label near each point
                Handles.Label(cData.LaneLinkPoint + Vector3.up * 0.4f, "Lane");
                Handles.Label(cData.RampLinkPoint + Vector3.up * 0.4f, "Ramp");
            }
        }

        private void HandleSelectMode(Event e, SceneView sceneView)
        {
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(controlId);

            // Do not intercept events when a Handle (e.g. PositionHandle) is being dragged
            if (GUIUtility.hotControl != 0) return;

            // Handle Delete key for internally-selected objects
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace))
            {
                if (HandleDeleteSelected())
                {
                    e.Use();
                    sceneView.Repaint(); Repaint();
                    return;
                }
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

                // 1. Raycast for collider-based objects (vertices, approach nodes)
                if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity))
                {
                    VertexVisual vx = hit.collider.GetComponent<VertexVisual>();
                    if (vx != null)
                    {
                        manager.SelectVertex(vx);
                        e.Use();
                        sceneView.Repaint(); Repaint();
                        return;
                    }
                    ApproachNodeVisual anx = hit.collider.GetComponent<ApproachNodeVisual>();
                    if (anx != null)
                    {
                        manager.SelectApproachNode(anx);
                        e.Use();
                        sceneView.Repaint(); Repaint();
                        return;
                    }
                }

                // 2. Check for HS anchor spheres (parent name starts with "HS_")
                if (Physics.Raycast(ray, out RaycastHit hitHS, Mathf.Infinity))
                {
                    if (hitHS.collider.name == "Anchor" && hitHS.collider.transform.parent != null
                        && hitHS.collider.transform.parent.name.StartsWith("HS_"))
                    {
                        string hsId = hitHS.collider.transform.parent.name.Substring(3);
                        Debug.Log($"Clicked HS anchor: {hsId}");
                        CircleVisual cvInHS = FindCircleInHS(hsId);
                        if (cvInHS != null)
                        {
                            manager.SelectCircle(cvInHS);
                        }
                        e.Use();
                        sceneView.Repaint(); Repaint();
                        return;
                    }
                }

                // 3. Distance-based detection for approach edges (Gizmos-only, no colliders)
                ApproachEdgeVisual closestApprEdge = FindClosestApproachEdge(e);
                if (closestApprEdge != null)
                {
                    manager.SelectApproachEdge(closestApprEdge);
                    e.Use();
                    sceneView.Repaint(); Repaint();
                    return;
                }

                // 4. Distance-based detection for ramps (Gizmos-only)
                RampVisual closestRamp = FindClosestRamp(e);
                if (closestRamp != null)
                {
                    manager.SelectRamp(closestRamp);
                    e.Use();
                    sceneView.Repaint(); Repaint();
                    return;
                }

                // 5. Distance-based detection for LineRenderer objects (no colliders)
                // Check circles first
                CircleVisual closestCircle = FindClosestCircle(e);
                if (closestCircle != null)
                {
                    manager.SelectCircle(closestCircle);
                    e.Use();
                    sceneView.Repaint(); Repaint();
                    return;
                }

                // Check vertiports
                VertiportVisual closestVP = FindClosestVertiport(e);
                if (closestVP != null)
                {
                    manager.SelectVertiport(closestVP);
                    e.Use();
                    sceneView.Repaint(); Repaint();
                    return;
                }

                // Check helipads
                HelipadVisual closestHP = FindClosestHelipad(e);
                if (closestHP != null)
                {
                    manager.SelectHelipad(closestHP);
                    e.Use();
                    sceneView.Repaint(); Repaint();
                    return;
                }

                // 5. Distance-based detection for corridors (Gizmos-only)
                CorridorVisual closestCorr = FindClosestCorridor(e);
                if (closestCorr != null)
                {
                    manager.SelectCorridor(closestCorr);
                    e.Use();
                    sceneView.Repaint(); Repaint();
                    return;
                }

                // Clicked empty space
                manager.ClearSelection();
                e.Use();
                sceneView.Repaint(); Repaint();
            }

            // Consume MouseDrag to prevent Unity's default Transform tool from
            // moving the selected GameObject when our handles are active
            if (e.type == EventType.MouseDrag && e.button == 0)
            {
                e.Use();
            }
        }

        /// <summary>
        /// Deletes the currently-selected internal object (via Select mode).
        /// Returns true if something was deleted, false if nothing was selected.
        /// </summary>
        private bool HandleDeleteSelected()
        {
            // Check each type in order from most-specific to least-specific

            VertexVisual svx = manager.GetSelectedVertex();
            if (svx != null)
            {
                if (EditorUtility.DisplayDialog("Delete Vertex",
                    $"Delete vertex {svx.VertexId} and its connected ramps?", "Yes", "No"))
                {
                    manager.DeleteVertex(svx.VertexId);
                }
                return true;
            }

            RampVisual sr = manager.GetSelectedRamp();
            if (sr != null)
            {
                if (EditorUtility.DisplayDialog("Delete Ramp",
                    $"Delete ramp {sr.RampId}?", "Yes", "No"))
                {
                    manager.DeleteRamp(sr.RampId);
                }
                return true;
            }

            CircleVisual sc = manager.GetSelectedCircle();
            if (sc != null)
            {
                if (EditorUtility.DisplayDialog("Delete Circle",
                    $"Delete circle {sc.CircleId} and all its vertices/ramps?", "Yes", "No"))
                {
                    manager.DeleteCircle(sc.CircleId);
                }
                return true;
            }

            HelipadVisual sh = manager.GetSelectedHelipad();
            if (sh != null)
            {
                if (EditorUtility.DisplayDialog("Delete Helipad",
                    $"Delete helipad {sh.HelipadId}?", "Yes", "No"))
                {
                    manager.DeleteHelipad(sh.HelipadId);
                }
                return true;
            }

            ApproachNodeVisual san = manager.GetSelectedApproachNode();
            if (san != null)
            {
                if (EditorUtility.DisplayDialog("Delete Approach Node",
                    $"Delete approach node {san.NodeId} and its connected edges?", "Yes", "No"))
                {
                    manager.DeleteApproachNode(san.NodeId);
                }
                return true;
            }

            ApproachEdgeVisual sae = manager.GetSelectedApproachEdge();
            if (sae != null)
            {
                if (EditorUtility.DisplayDialog("Delete Approach Edge",
                    $"Delete approach edge {sae.EdgeId}?", "Yes", "No"))
                {
                    manager.DeleteApproachEdge(sae.EdgeId);
                }
                return true;
            }

            CorridorVisual sco = manager.GetSelectedCorridor();
            if (sco != null)
            {
                if (EditorUtility.DisplayDialog("Delete Corridor",
                    $"Delete corridor {sco.CorridorId}?", "Yes", "No"))
                {
                    manager.DeleteCorridor(sco.CorridorId);
                }
                return true;
            }

            VertiportVisual sv = manager.GetSelectedVertiport();
            if (sv != null)
            {
                if (EditorUtility.DisplayDialog("Delete Vertiport",
                    $"Delete entire vertiport {sv.VertiportId} and ALL its components?", "Yes", "No"))
                {
                    manager.DeleteVertiport(sv.VertiportId);
                }
                return true;
            }

            return false;
        }

        private void HandleCreateVertiportMode(Event e, SceneView sceneView)
        {
            if (e.type == EventType.MouseMove)
            {
                previewPos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));
                sceneView.Repaint(); Repaint();
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                Vector3 pos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));
                polygonPoints.Add(pos);
                e.Use();
                sceneView.Repaint(); Repaint();
            }

            if ((e.type == EventType.MouseDown && e.button == 1) || (e.type == EventType.KeyDown && e.keyCode == KeyCode.Return))
            {
                if (polygonPoints.Count >= 3)
                {
                    manager.CreateVertiport(polygonPoints);
                    EditorUtility.SetDirty(manager);
                }
                polygonPoints.Clear();
                e.Use();
                sceneView.Repaint(); Repaint();
            }

            // Draw preview
            DrawPolygonPreview();
        }

        private void HandleCreateHelipadMode(Event e, SceneView sceneView)
        {
            if (e.type == EventType.MouseMove)
            {
                previewPos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));
                sceneView.Repaint(); Repaint();
            }

            if (string.IsNullOrEmpty(selectedVertiportIdForHelipad) && e.type == EventType.MouseDown && e.button == 0)
            {
                Vector3 clickPos = GetMouseWorldPosition(e, sceneView);
                VertiportVisual vp = FindVertiportContainingPoint(clickPos);
                if (vp != null)
                {
                    selectedVertiportIdForHelipad = vp.VertiportId;
                    e.Use();
                    sceneView.Repaint(); Repaint();
                }
                else
                {
                    Debug.LogWarning("Click inside a Vertiport boundary to select it.");
                }
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && !string.IsNullOrEmpty(selectedVertiportIdForHelipad))
            {
                Vector3 pos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));

                // Validate point is within the selected VP boundary
                VertiportVisual vpVis = manager.GetVertiportVisual(selectedVertiportIdForHelipad);
                if (vpVis != null && vpVis.VertiportData != null &&
                    !IsPointInPolygon(pos, vpVis.VertiportData.Boundary))
                {
                    Debug.LogWarning("Helipad point is outside the Vertiport boundary. Place points inside the VP.");
                    return;
                }

                polygonPoints.Add(pos);
                e.Use();
                sceneView.Repaint(); Repaint();
            }

            if ((e.type == EventType.MouseDown && e.button == 1) || (e.type == EventType.KeyDown && e.keyCode == KeyCode.Return))
            {
                if (polygonPoints.Count >= 3)
                {
                    manager.CreateHelipad(selectedVertiportIdForHelipad, polygonPoints);
                    EditorUtility.SetDirty(manager);
                }
                polygonPoints.Clear();
                selectedVertiportIdForHelipad = null;
                e.Use();
                sceneView.Repaint(); Repaint();
            }

            DrawPolygonPreview();
        }

        private void HandleCreateHSMode(Event e, SceneView sceneView)
        {
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            if (e.type == EventType.MouseMove)
            {
                previewPos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));
                sceneView.Repaint(); Repaint();
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                // Need a selected vertiport first
                VertiportVisual sv = manager.GetSelectedVertiport();
                string vpId = sv != null ? sv.VertiportId : null;

                if (string.IsNullOrEmpty(vpId) && manager.Vertiports.Count == 1)
                    vpId = manager.Vertiports[0].VertiportId;

                if (string.IsNullOrEmpty(vpId))
                {
                    // Try to select a vertiport by clicking inside its boundary
                    Vector3 clickPos = GetMouseWorldPosition(e, sceneView);
                    VertiportVisual vp = FindVertiportContainingPoint(clickPos);
                    if (vp != null)
                    {
                        manager.SelectVertiport(vp);
                        e.Use();
                        sceneView.Repaint(); Repaint();
                        return;
                    }
                    Debug.LogWarning("Click inside a Vertiport boundary to select it.");
                    return;
                }

                Vector3 pos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));

                // Validate position is within the selected VP boundary
                VertiportVisual vpVis = manager.GetVertiportVisual(vpId);
                if (vpVis != null && vpVis.VertiportData != null &&
                    !IsPointInPolygon(pos, vpVis.VertiportData.Boundary))
                {
                    Debug.LogWarning("HoldingStructure must be placed inside the Vertiport boundary.");
                    return;
                }

                manager.CreateHoldingStructure(vpId, pos);
                EditorUtility.SetDirty(manager);
                e.Use();
                sceneView.Repaint(); Repaint();
            }

            Handles.color = Color.magenta;
            float hsPrevSize = HandleUtility.GetHandleSize(previewPos) * 0.1f;
            Handles.SphereHandleCap(0, previewPos, Quaternion.identity, hsPrevSize, EventType.Repaint);
        }

        private void HandleCreateCircleMode(Event e, SceneView sceneView)
        {
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            // Auto-freeze XZ to selected HS position
            if (!string.IsNullOrEmpty(selectedHSIdForCircle))
            {
                HoldingStructure hs = FindHoldingStructureById(selectedHSIdForCircle);
                if (hs != null)
                {
                    freezeX = true;
                    freezeZ = true;
                    frozenX = hs.Position.x;
                    frozenZ = hs.Position.z;
                }
            }

            if (e.type == EventType.MouseMove)
            {
                previewPos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));
                sceneView.Repaint(); Repaint();
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                // Use RaycastAll to handle overlapping colliders (e.g. ApprNode sphere occluding HS anchor)
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                RaycastHit[] hits = Physics.RaycastAll(ray, Mathf.Infinity);
                foreach (var hit in hits)
                {
                    Transform t = hit.collider.transform;
                    while (t != null)
                    {
                        if (t.name.StartsWith("HS_"))
                        {
                            string hsId = t.name.Substring(3);
                            selectedHSIdForCircle = hsId;
                            Debug.Log($"[CreateCircle] Selected HS: {hsId} (clicked: {hit.collider.name})");
                            e.Use();
                            sceneView.Repaint(); Repaint();
                            return;
                        }
                        t = t.parent;
                    }
                }

                // Also try distance-based circle click (for LineRenderer circles that raycast might miss)
                if (string.IsNullOrEmpty(selectedHSIdForCircle))
                {
                    CircleVisual cv = FindClosestCircle(e);
                    if (cv != null)
                    {
                        selectedHSIdForCircle = cv.ParentHoldingStructureId;
                        Debug.Log($"[CreateCircle] Selected HS from circle: {selectedHSIdForCircle}");
                        e.Use();
                        sceneView.Repaint(); Repaint();
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(selectedHSIdForCircle))
                {
                    Vector3 pos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));
                    manager.CreateCircle(selectedHSIdForCircle, pos, newCircleRadius, newCircleVertexCount, newCircleInitialAngle);
                    EditorUtility.SetDirty(manager);
                    e.Use();
                    sceneView.Repaint(); Repaint();
                }
            }

            // Preview circle
            Handles.color = Color.white;
            Handles.DrawWireDisc(previewPos, Vector3.up, newCircleRadius);
            float previewSize = HandleUtility.GetHandleSize(previewPos) * 0.1f;
            Handles.color = Color.green;
            Handles.SphereHandleCap(0, previewPos, Quaternion.identity, previewSize, EventType.Repaint);
        }

        private void HandleCreateVertexMode(Event e, SceneView sceneView)
        {
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            if (e.type == EventType.MouseMove)
            {
                previewPos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));
                sceneView.Repaint(); Repaint();
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                // Find closest circle and add vertex at click position
                CircleVisual closestCircle = FindClosestCircle(e);
                if (closestCircle != null && closestCircle.CircleData != null)
                {
                    Vector3 clickPos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));
                    Vector3 center = closestCircle.CircleData.Center;
                    float radius = closestCircle.CircleData.Radius;
                    Vector3 dir = clickPos - center;
                    dir.y = 0f;
                    if (dir.magnitude > 0.001f)
                        clickPos = center + dir.normalized * radius;

                    manager.CreateVertex(closestCircle.CircleId, clickPos);
                    EditorUtility.SetDirty(manager);
                    e.Use();
                    sceneView.Repaint(); Repaint();
                }
            }

            Handles.color = Color.cyan;
            float vxPrevSize = HandleUtility.GetHandleSize(previewPos) * 0.07f;
            Handles.SphereHandleCap(0, previewPos, Quaternion.identity, vxPrevSize, EventType.Repaint);
        }

        private void HandleCreateRampMode(Event e, SceneView sceneView)
        {
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            if (e.type == EventType.MouseMove)
            {
                previewPos = GetMouseWorldPosition(e, sceneView);
                sceneView.Repaint(); Repaint();
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                e.Use();

                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                // Use RaycastAll to find VertexVisual even when ApprNode sphere occludes the anchor
                RaycastHit[] hits = Physics.RaycastAll(ray, Mathf.Infinity);
                VertexVisual vx = null;
                foreach (var hit in hits)
                {
                    vx = hit.collider.GetComponent<VertexVisual>();
                    if (vx != null) break;
                }

                if (vx != null)
                {
                    if (rampStartVertex == null)
                    {
                        rampStartVertex = vx;
                        Debug.Log($"Ramp start: {rampStartVertex.VertexId}");
                    }
                    else if (vx != rampStartVertex)
                    {
                        manager.CreateRamp(rampStartVertex.VertexId, vx.VertexId, newRampDirection);
                        EditorUtility.SetDirty(manager);
                        rampStartVertex = null;
                        Debug.Log("Ramp created!");
                    }
                    SceneView.RepaintAll(); Repaint();
                }
                return;
            }

            if (rampStartVertex != null)
            {
                Handles.color = Color.yellow;
                Handles.DrawLine(rampStartVertex.transform.position, previewPos);
                float discSize = HandleUtility.GetHandleSize(previewPos) * 0.06f;
                Handles.DrawWireDisc(previewPos, Vector3.up, discSize);
                sceneView.Repaint();
            }
        }

        private void HandleCreateNetworkMode(Event e, SceneView sceneView)
        {
            if (currentApprSubMode == ApprSubMode.ApprNode)
                HandleApprNodeSubMode(e, sceneView);
            else
                HandleApprEdgeSubMode(e, sceneView);
        }

        private void HandleApprNodeSubMode(Event e, SceneView sceneView)
        {
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            if (string.IsNullOrEmpty(selectedVertiportIdForApprNode))
            {
                VertiportVisual sv = manager.GetSelectedVertiport();
                if (sv != null) selectedVertiportIdForApprNode = sv.VertiportId;
                if (string.IsNullOrEmpty(selectedVertiportIdForApprNode) && manager.Vertiports.Count == 1)
                    selectedVertiportIdForApprNode = manager.Vertiports[0].VertiportId;
                sceneView.Repaint(); Repaint();
                return;
            }

            if (e.type == EventType.MouseMove)
            {
                previewPos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));
                sceneView.Repaint(); Repaint();
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                e.Use();

                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity))
                {
                    // Check for HS vertex click (any circle vertex can be an anchor)
                    VertexVisual vVis = hit.collider.GetComponent<VertexVisual>();
                    if (vVis != null && vVis.VertexData != null
                        && !string.IsNullOrEmpty(vVis.VertexData.LinkCircleId))
                    {
                        // Find parent HS of this vertex
                        Transform t = hit.collider.transform;
                        while (t != null && !t.name.StartsWith("HS_")) t = t.parent;
                        if (t != null)
                        {
                            string hsId = t.name.Substring(3);
                            string vertexId = vVis.VertexId;
                            Vector3 pos = vVis.VertexData.Position;
                            manager.CreateApproachNode(selectedVertiportIdForApprNode, pos, isAnchor: true, linkHSId: hsId, linkVertexId: vertexId);
                            EditorUtility.SetDirty(manager);
                            Debug.Log($"[ApprNode] Created anchor node linked to HS {hsId} vertex {vertexId}");
                            sceneView.Repaint(); Repaint();
                            return;
                        }
                    }
                }

                Vector3 clickPos = ApplyFrozenAxes(GetMouseWorldPosition(e, sceneView));
                manager.CreateApproachNode(selectedVertiportIdForApprNode, clickPos);
                EditorUtility.SetDirty(manager);
                sceneView.Repaint(); Repaint();
            }

            Handles.color = Color.cyan;
            float prevSize = HandleUtility.GetHandleSize(previewPos) * 0.07f;
            Handles.SphereHandleCap(0, previewPos, Quaternion.identity, prevSize, EventType.Repaint);
        }

        private void HandleApprEdgeSubMode(Event e, SceneView sceneView)
        {
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            if (e.type == EventType.MouseMove)
            {
                previewPos = GetMouseWorldPosition(e, sceneView);
                sceneView.Repaint(); Repaint();
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                e.Use();

                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity))
                {
                    // 1. Check for ApproachNodeVisual directly
                    ApproachNodeVisual anx = hit.collider.GetComponent<ApproachNodeVisual>();
                    if (anx == null)
                    {
                        // 2a. Check for VertexVisual (circle vertex) — connect to specific vertex
                        VertexVisual vVis = hit.collider.GetComponent<VertexVisual>();
                        if (vVis != null && !string.IsNullOrEmpty(vVis.VertexData?.LinkCircleId))
                        {
                            Transform t = hit.collider.transform;
                            while (t != null && !t.name.StartsWith("HS_")) t = t.parent;
                            if (t != null)
                            {
                                string hsId = t.name.Substring(3);
                                string vId = vVis.VertexId;
                                anx = manager.FindOrCreateAnchorApprNode(hsId, vId);
                                if (anx != null)
                                    Debug.Log($"[ApprEdge] Using HS vertex anchor: {anx.NodeId} (HS {hsId}, vertex {vId})");
                            }
                        }
                        // 2b. Fallback — clicked HS center
                        if (anx == null)
                        {
                            Transform t = hit.collider.transform;
                            while (t != null)
                            {
                                if (t.name.StartsWith("HS_"))
                                {
                                    string hsId = t.name.Substring(3);
                                    anx = manager.FindOrCreateAnchorApprNode(hsId);
                                    if (anx != null)
                                        Debug.Log($"[ApprEdge] Using HS anchor node: {anx.NodeId} (HS {hsId})");
                                    break;
                                }
                                t = t.parent;
                            }
                        }
                    }

                    if (anx != null)
                    {
                        if (apprEdgeStartNode == null)
                        {
                            apprEdgeStartNode = anx;
                            Debug.Log($"ApprEdge start: {apprEdgeStartNode.NodeId}");
                        }
                        else if (anx != apprEdgeStartNode)
                        {
                            manager.CreateApproachEdge(apprEdgeStartNode.ParentVertiportId,
                                apprEdgeStartNode.NodeId, anx.NodeId);
                            EditorUtility.SetDirty(manager);
                            apprEdgeStartNode = null;
                            Debug.Log("Approach edge created!");
                        }
                        SceneView.RepaintAll(); Repaint();
                    }
                }
                return;
            }

            if (apprEdgeStartNode != null)
            {
                Handles.color = Color.yellow;
                Handles.DrawLine(apprEdgeStartNode.transform.position, previewPos);
                float discSize = HandleUtility.GetHandleSize(previewPos) * 0.06f;
                Handles.DrawWireDisc(previewPos, Vector3.up, discSize);
                sceneView.Repaint();
            }
        }

        private void HandleCreateCorridorMode(Event e, SceneView sceneView)
        {
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            // When a Handle (PositionHandle) is being dragged, skip mouse event processing
            // but still draw all handles so the drag can be tracked.
            bool handleActive = GUIUtility.hotControl != 0;

            if (!handleActive && e.type == EventType.MouseMove)
            {
                previewPos = GetMouseWorldPosition(e, sceneView);
                sceneView.Repaint(); Repaint();
            }

            switch (corridorPhase)
            {
                case CorridorPhase.SelectLane:
                    if (!handleActive && e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 1 && !e.shift)
                    {
                        e.Use();
                        var (laneId, closestPt, dist) = FindClosestLane(e);
                        if (!string.IsNullOrEmpty(laneId))
                        {
                            selectedLaneId = laneId;
                            laneLinkPoint = SnapToLaneCenterline(laneId, closestPt);
                            corridorPhase = CorridorPhase.SetLanePoint;
                            Debug.Log($"Corridor lane selected: {selectedLaneId}");
                        }
                        else
                        {
                            Debug.LogWarning("No lane found near click. Click closer to a lane centerline.");
                        }
                        sceneView.Repaint(); Repaint();
                    }

                    // Preview: highlight nearest lane (always draw)
                    {
                        var (previewLaneId, _, _) = FindClosestLane(e);
                        if (!string.IsNullOrEmpty(previewLaneId))
                        {
                            Handles.color = new Color(1f, 1f, 0f, 0.3f);
                            DrawLaneCenterlineHighlight(previewLaneId);
                        }
                    }
                    break;

                case CorridorPhase.SetLanePoint:
                    if (!handleActive)
                    {
                        if (e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 1 && e.shift)
                        {
                            e.Use();
                            Vector3 raw = GetMouseWorldPosition(e, sceneView);
                            laneLinkPoint = SnapToLaneCenterline(selectedLaneId, raw);
                            Debug.Log($"Lane link point set: {laneLinkPoint}");
                            sceneView.Repaint(); Repaint();
                        }
                        else if (e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 2)
                        {
                            e.Use();
                            corridorPhase = CorridorPhase.SelectRamp;
                            Debug.Log("Lane side confirmed (double-click). Now select a ramp.");
                            sceneView.Repaint(); Repaint();
                        }
                    }

                    // Lane link point PositionHandle (always draw, even during drag)
                    {
                        float size = HandleUtility.GetHandleSize(laneLinkPoint) * 0.08f;
                        Handles.color = Color.green;
                        Handles.SphereHandleCap(0, laneLinkPoint, Quaternion.identity, size, EventType.Repaint);
                        EditorGUI.BeginChangeCheck();
                        Vector3 newPos = Handles.PositionHandle(laneLinkPoint, Quaternion.identity);
                        if (EditorGUI.EndChangeCheck())
                        {
                            laneLinkPoint = SnapToLaneCenterline(selectedLaneId, newPos);
                            sceneView.Repaint(); Repaint();
                        }
                    }
                    break;

                case CorridorPhase.SelectRamp:
                    if (!handleActive && e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 1 && !e.shift)
                    {
                        e.Use();
                        RampVisual rv = null;

                        // 1. Try collider-based: click on Vertex colliders
                        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                        RaycastHit[] hits = Physics.RaycastAll(ray, Mathf.Infinity);
                        foreach (var hit in hits)
                        {
                            VertexVisual vx = hit.collider.GetComponent<VertexVisual>();
                            if (vx != null && vx.VertexData != null)
                            {
                                rv = FindRampByVertex(vx.VertexId);
                                if (rv != null) break;
                            }
                        }

                        // 2. Distance-based: find closest ramp by line segment
                        if (rv == null)
                        {
                            rv = FindClosestRampByDistance(e);
                        }

                        if (rv != null && rv.RampData != null)
                        {
                            selectedRampId = rv.RampId;
                            VertexVisual fromV = manager.GetVertexVisual(rv.RampData.FromVertexId);
                            VertexVisual toV = manager.GetVertexVisual(rv.RampData.ToVertexId);
                            if (fromV != null && toV != null)
                                rampLinkPoint = SnapToRampLine(selectedRampId, (fromV.transform.position + toV.transform.position) * 0.5f);
                            else
                                rampLinkPoint = rv.transform.position;

                            corridorPhase = CorridorPhase.SetRampPoint;
                            Debug.Log($"Corridor ramp selected: {selectedRampId}");
                        }
                        else
                        {
                            Debug.LogWarning("No ramp found near click. Click on a ramp line or its vertex.");
                        }
                        sceneView.Repaint(); Repaint();
                    }

                    // Preview: highlight nearest ramp by distance (always draw)
                    {
                        RampVisual nearestRv = FindClosestRampByDistance(e);
                        if (nearestRv != null && nearestRv.RampData != null)
                        {
                            VertexVisual fv = manager.GetVertexVisual(nearestRv.RampData.FromVertexId);
                            VertexVisual tv = manager.GetVertexVisual(nearestRv.RampData.ToVertexId);
                            if (fv != null && tv != null)
                            {
                                Handles.color = new Color(1f, 1f, 0f, 0.5f);
                                if (!string.IsNullOrEmpty(nearestRv.LinkCircleId))
                                    DrawRampArc(nearestRv, fv.transform.position, tv.transform.position, 3f);
                                else
                                    Handles.DrawLine(fv.transform.position, tv.transform.position, 3f);
                            }
                        }
                    }
                    break;

                case CorridorPhase.SetRampPoint:
                    if (!handleActive)
                    {
                        if (e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 1 && e.shift)
                        {
                            e.Use();
                            Vector3 raw = GetMouseWorldPosition(e, sceneView);
                            rampLinkPoint = SnapToRampLine(selectedRampId, raw);
                            Debug.Log($"Ramp link point set: {rampLinkPoint}");
                            sceneView.Repaint(); Repaint();
                        }
                        else if (e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 2)
                        {
                            e.Use();
                            RampVisual rVis = manager.GetRampVisual(selectedRampId);
                            if (rVis == null)
                            {
                                Debug.LogError($"Ramp {selectedRampId} not found.");
                                corridorPhase = CorridorPhase.SelectLane;
                                selectedLaneId = null;
                                selectedRampId = null;
                                sceneView.Repaint(); Repaint();
                                break;
                            }

                            // Find parent VP for this ramp
                            string vpId = null;
                            foreach (var vp in manager.Vertiports)
                            {
                                foreach (var hs in vp.HoldingStructures)
                                {
                                    if (hs.Ramps.Any(r => r.RampId == selectedRampId))
                                    {
                                        vpId = vp.VertiportId;
                                        break;
                                    }
                                }
                                if (vpId != null) break;
                            }

                            if (string.IsNullOrEmpty(vpId))
                            {
                                Debug.LogError("Could not determine parent Vertiport for ramp.");
                                corridorPhase = CorridorPhase.SelectLane;
                                selectedLaneId = null;
                                selectedRampId = null;
                                sceneView.Repaint(); Repaint();
                                break;
                            }

                            manager.CreateCorridor(vpId, corridorType, selectedLaneId, selectedRampId,
                                laneLinkPoint, rampLinkPoint);
                            EditorUtility.SetDirty(manager);

                            Debug.Log($"Corridor created: {corridorType} lane={selectedLaneId} ramp={selectedRampId}");

                            // Reset state
                            corridorPhase = CorridorPhase.SelectLane;
                            selectedLaneId = null;
                            laneLinkPoint = Vector3.zero;
                            selectedRampId = null;
                            rampLinkPoint = Vector3.zero;
                            sceneView.Repaint(); Repaint();
                        }
                    }

                    // Ramp link point PositionHandle (always draw, even during drag)
                    {
                        float size = HandleUtility.GetHandleSize(rampLinkPoint) * 0.08f;
                        Handles.color = Color.magenta;
                        Handles.SphereHandleCap(0, rampLinkPoint, Quaternion.identity, size, EventType.Repaint);
                        EditorGUI.BeginChangeCheck();
                        Vector3 newPos = Handles.PositionHandle(rampLinkPoint, Quaternion.identity);
                        if (EditorGUI.EndChangeCheck())
                        {
                            rampLinkPoint = SnapToRampLine(selectedRampId, newPos);
                            sceneView.Repaint(); Repaint();
                        }
                    }
                    break;
            }

            // Draw connecting preview line (always draw)
            if (corridorPhase == CorridorPhase.SetRampPoint ||
                (corridorPhase == CorridorPhase.SelectRamp && rampLinkPoint != Vector3.zero))
            {
                Handles.color = new Color(0.5f, 1f, 0.5f, 0.5f);
                Handles.DrawDottedLine(laneLinkPoint, rampLinkPoint, 4f);
            }
        }

        /// <summary>Project a point onto the nearest segment of a lane's centerline.</summary>
        private Vector3 SnapToLaneCenterline(string laneId, Vector3 point)
        {
            NetworkManager netMgr = Object.FindObjectOfType<NetworkManager>();
            if (netMgr == null || netMgr.FullNetwork == null) return point;

            foreach (var edge in netMgr.FullNetwork.Edges)
            {
                foreach (var lane in edge.Lanes)
                {
                    if (lane.LaneId != laneId || lane.Centerline == null || lane.Centerline.Count < 2) continue;
                    float bestDist = float.MaxValue;
                    Vector3 bestPt = point;
                    for (int i = 0; i < lane.Centerline.Count - 1; i++)
                    {
                        Vector3 proj = ProjectPointOnSegment(point, lane.Centerline[i], lane.Centerline[i + 1]);
                        float d = Vector3.Distance(point, proj);
                        if (d < bestDist) { bestDist = d; bestPt = proj; }
                    }
                    return bestPt;
                }
            }
            return point;
        }

        /// <summary>Project a point onto the ramp line segment (between from/to vertices).</summary>
        private Vector3 SnapToRampLine(string rampId, Vector3 point)
        {
            RampVisual rv = manager.GetRampVisual(rampId);
            if (rv == null || rv.RampData == null) return point;

            VertexVisual fv = manager.GetVertexVisual(rv.RampData.FromVertexId);
            VertexVisual tv = manager.GetVertexVisual(rv.RampData.ToVertexId);
            if (fv == null || tv == null) return point;

            if (!string.IsNullOrEmpty(rv.LinkCircleId))
            {
                CircleVisual cv = manager.GetCircleVisual(rv.LinkCircleId);
                if (cv != null && cv.CircleData != null)
                {
                    Vector3 center = cv.CircleData.Center;
                    float radius = cv.CircleData.Radius;
                    Vector3 fromPos = fv.transform.position;
                    Vector3 toPos = tv.transform.position;

                    Vector3 dir = point - center;
                    dir.y = 0;
                    if (dir.magnitude < 0.001f) dir = (fromPos - center).normalized;
                    Vector3 onCircle = center + dir.normalized * radius;

                    float pointAngle = Mathf.Atan2(onCircle.z - center.z, onCircle.x - center.x);
                    float fromAngle = Mathf.Atan2(fromPos.z - center.z, fromPos.x - center.x);
                    float toAngle = Mathf.Atan2(toPos.z - center.z, toPos.x - center.x);

                    float diff = toAngle - fromAngle;
                    while (diff > Mathf.PI) diff -= 2 * Mathf.PI;
                    while (diff < -Mathf.PI) diff += 2 * Mathf.PI;
                    float clampedAngle = Mathf.Lerp(fromAngle, fromAngle + diff,
                        Mathf.Clamp01((pointAngle - fromAngle) / diff));

                    Vector3 result = center + new Vector3(Mathf.Cos(clampedAngle), 0, Mathf.Sin(clampedAngle)) * radius;
                    result.y = point.y;
                    return result;
                }
            }

            return ProjectPointOnSegment(point, fv.transform.position, tv.transform.position);
        }

        /// <summary>Find a ramp by distance from the mouse ray to each ramp's segment.</summary>
        private RampVisual FindClosestRampByDistance(Event e)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            float bestDist = 1.5f;
            RampVisual best = null;

            foreach (var vp in manager.Vertiports)
            {
                foreach (var hs in vp.HoldingStructures)
                {
                    foreach (var r in hs.Ramps)
                    {
                        RampVisual rv = manager.GetRampVisual(r.RampId);
                        if (rv == null) continue;
                        VertexVisual fv = manager.GetVertexVisual(r.FromVertexId);
                        VertexVisual tv = manager.GetVertexVisual(r.ToVertexId);
                        if (fv == null || tv == null) continue;

                        float dist;
                        if (!string.IsNullOrEmpty(r.LinkCircleId))
                            dist = DistanceFromRayToRampArc(ray, rv);
                        else
                            dist = DistanceFromRayToSegment(ray, fv.transform.position, tv.transform.position);

                        if (dist < bestDist) { bestDist = dist; best = rv; }
                    }
                }
            }
            return best;
        }

        private (string laneId, Vector3 closestPoint, float distance) FindClosestLane(Event e)
        {
            NetworkManager netMgr = Object.FindObjectOfType<NetworkManager>();
            if (netMgr == null || netMgr.FullNetwork == null)
                return (null, Vector3.zero, float.MaxValue);

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            float bestDist = 2f;
            string bestId = null;
            Vector3 bestPoint = Vector3.zero;

            foreach (var edge in netMgr.FullNetwork.Edges)
            {
                foreach (var lane in edge.Lanes)
                {
                    if (lane.Centerline == null || lane.Centerline.Count < 2) continue;
                    for (int i = 0; i < lane.Centerline.Count - 1; i++)
                    {
                        float dist = DistanceFromRayToSegment(ray, lane.Centerline[i], lane.Centerline[i + 1]);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestId = lane.LaneId;
                            bestPoint = ProjectPointOnSegment(ray.origin, lane.Centerline[i], lane.Centerline[i + 1]);
                        }
                    }
                }
            }

            return (bestId, bestPoint, bestDist);
        }

        private Vector3 ProjectPointOnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            Vector3 ap = point - a;
            float t = Vector3.Dot(ap, ab) / Mathf.Max(ab.sqrMagnitude, 0.0001f);
            t = Mathf.Clamp01(t);
            return a + t * ab;
        }

        private void DrawLaneCenterlineHighlight(string laneId)
        {
            NetworkManager netMgr = Object.FindObjectOfType<NetworkManager>();
            if (netMgr == null || netMgr.FullNetwork == null) return;

            foreach (var edge in netMgr.FullNetwork.Edges)
            {
                foreach (var lane in edge.Lanes)
                {
                    if (lane.LaneId != laneId || lane.Centerline == null) continue;
                    for (int j = 0; j < lane.Centerline.Count - 1; j++)
                    {
                        Handles.DrawLine(lane.Centerline[j], lane.Centerline[j + 1], 3f);
                    }
                    return;
                }
            }
        }

        private RampVisual FindRampByVertex(string vertexId)
        {
            foreach (var vp in manager.Vertiports)
                foreach (var hs in vp.HoldingStructures)
                    foreach (var r in hs.Ramps)
                        if (r.FromVertexId == vertexId || r.ToVertexId == vertexId)
                            return manager.GetRampVisual(r.RampId);
            return null;
        }

        /// <summary>Sync corridor link point back to Ramp.LinkCorridors after user adjustment.</summary>
        private void SyncCorridorLinkPointToRamp(NetConnectedCorridor corridor)
        {
            foreach (var vp in manager.Vertiports)
            {
                foreach (var hs in vp.HoldingStructures)
                {
                    Ramp ramp = hs.Ramps.Find(r => r.RampId == corridor.LinkRampId);
                    if (ramp != null)
                    {
                        int idx = ramp.LinkCorridors.FindIndex(t => t.Item1 == corridor.CorridorId);
                        if (idx >= 0)
                            ramp.LinkCorridors[idx] = (corridor.CorridorId, corridor.RampLinkPoint);
                        return;
                    }
                }
            }
        }

        private void DrawPolygonPreview()
        {
            // Draw existing polygon edges
            if (polygonPoints.Count >= 2)
            {
                Handles.color = Color.green;
                for (int i = 0; i < polygonPoints.Count - 1; i++)
                    Handles.DrawLine(polygonPoints[i], polygonPoints[i + 1]);
                // Line from last point to preview
                Handles.DrawLine(polygonPoints[polygonPoints.Count - 1], previewPos);
            }

            // Draw closing line if >= 2 points
            if (polygonPoints.Count >= 3)
            {
                Handles.color = new Color(0, 1, 0, 0.3f);
                Handles.DrawLine(previewPos, polygonPoints[0]);
            }

            // Draw point markers — size scales with camera distance
            Handles.color = Color.white;
            foreach (var pt in polygonPoints)
            {
                float ptSize = HandleUtility.GetHandleSize(pt) * 0.08f;
                Handles.SphereHandleCap(0, pt, Quaternion.identity, ptSize, EventType.Repaint);
            }

            // Preview
            Handles.color = Color.green;
            float prevSize = HandleUtility.GetHandleSize(previewPos) * 0.1f;
            Handles.SphereHandleCap(0, previewPos, Quaternion.identity, prevSize, EventType.Repaint);
        }

        #endregion

        #region Distance-based Picking

        private VertiportVisual FindClosestVertiport(Event e)
        {
            Transform container = manager.GetVertiportsContainer();
            if (container == null) return null;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            float bestDist = 2f;
            VertiportVisual best = null;

            foreach (Transform child in container)
            {
                VertiportVisual v = child.GetComponent<VertiportVisual>();
                if (v == null || v.VertiportData == null || v.VertiportData.Boundary == null) continue;

                float dist = DistanceFromRayToPolygon(ray, v.VertiportData.Boundary);
                if (dist < bestDist) { bestDist = dist; best = v; }
            }
            return best;
        }

        private HelipadVisual FindClosestHelipad(Event e)
        {
            Transform container = manager.GetVertiportsContainer();
            if (container == null) return null;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            float bestDist = 2f;
            HelipadVisual best = null;

            foreach (Transform vpChild in container)
            {
                Transform hpContainer = vpChild.Find("Helipads");
                if (hpContainer == null) continue;
                foreach (Transform child in hpContainer)
                {
                    HelipadVisual h = child.GetComponent<HelipadVisual>();
                    if (h == null || h.HelipadData == null || h.HelipadData.Boundary == null) continue;

                    float dist = DistanceFromRayToPolygon(ray, h.HelipadData.Boundary);
                    if (dist < bestDist) { bestDist = dist; best = h; }
                }
            }
            return best;
        }

        private CircleVisual FindClosestCircle(Event e)
        {
            Transform container = manager.GetVertiportsContainer();
            if (container == null) return null;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            float bestDist = 2f;
            CircleVisual best = null;

            foreach (Transform vpChild in container)
            {
                Transform hsContainer = vpChild.Find("HoldingStructures");
                if (hsContainer == null) continue;
                foreach (Transform hsChild in hsContainer)
                {
                    foreach (Transform circleChild in hsChild)
                    {
                        CircleVisual c = circleChild.GetComponent<CircleVisual>();
                        if (c == null || c.CircleData == null) continue;

                        float dist = DistanceFromRayToCircle(ray, c.CircleData.Center, c.CircleData.Radius);
                        if (dist < bestDist) { bestDist = dist; best = c; }
                    }
                }
            }
            return best;
        }

        private RampVisual FindClosestRamp(Event e)
        {
            Transform container = manager.GetVertiportsContainer();
            if (container == null) return null;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            float bestDist = 1.5f;
            RampVisual best = null;

            foreach (Transform vpChild in container)
            {
                Transform hsContainer = vpChild.Find("HoldingStructures");
                if (hsContainer == null) continue;
                foreach (Transform hsChild in hsContainer)
                {
                    foreach (Transform rampChild in hsChild)
                    {
                        RampVisual r = rampChild.GetComponent<RampVisual>();
                        if (r == null || r.RampData == null) continue;

                        VertexVisual fromV = manager.GetVertexVisual(r.RampData.FromVertexId);
                        VertexVisual toV = manager.GetVertexVisual(r.RampData.ToVertexId);
                        if (fromV == null || toV == null) continue;

                        float dist;
                        if (!string.IsNullOrEmpty(r.LinkCircleId))
                            dist = DistanceFromRayToRampArc(ray, r);
                        else
                            dist = DistanceFromRayToSegment(ray, fromV.transform.position, toV.transform.position);

                        if (dist < bestDist) { bestDist = dist; best = r; }
                    }
                }
            }
            return best;
        }

        private CorridorVisual FindClosestCorridor(Event e)
        {
            Transform container = manager.GetVertiportsContainer();
            if (container == null) return null;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            float bestDist = 1.5f;
            CorridorVisual best = null;

            NetworkManager netMgr = Object.FindObjectOfType<NetworkManager>();

            foreach (Transform vpChild in container)
            {
                Transform corrContainer = vpChild.Find("Corridors");
                if (corrContainer == null) continue;
                foreach (Transform corrChild in corrContainer)
                {
                    CorridorVisual cv = corrChild.GetComponent<CorridorVisual>();
                    if (cv == null || cv.CorridorData == null) continue;

                    float dist = DistanceFromRayToCorridorBezier(ray, cv, netMgr);
                    if (dist < bestDist) { bestDist = dist; best = cv; }
                }
            }
            return best;
        }

        #endregion

        private ApproachEdgeVisual FindClosestApproachEdge(Event e)
        {
            Transform container = manager.GetVertiportsContainer();
            if (container == null) return null;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            float bestDist = 1.5f;
            ApproachEdgeVisual best = null;

            foreach (Transform vpChild in container)
            {
                Transform apprContainer = vpChild.Find("ApproachNet");
                if (apprContainer == null) continue;
                foreach (Transform edgeChild in apprContainer)
                {
                    ApproachEdgeVisual ev = edgeChild.GetComponent<ApproachEdgeVisual>();
                    if (ev == null || ev.EdgeData == null) continue;

                    ApproachNodeVisual fromN = manager.GetApproachNodeVisual(ev.EdgeData.FromNodeId);
                    ApproachNodeVisual toN = manager.GetApproachNodeVisual(ev.EdgeData.ToNodeId);
                    if (fromN == null || toN == null) continue;

                    float dist = DistanceFromRayToSegment(ray, fromN.transform.position, toN.transform.position);
                    if (dist < bestDist) { bestDist = dist; best = ev; }
                }
            }
            return best;
        }

        #region Data Lookup Helpers

        private HoldingStructure FindHoldingStructureById(string hsId)
        {
            foreach (var vp in manager.Vertiports)
                foreach (var hs in vp.HoldingStructures)
                    if (hs.HoldingStructureId == hsId)
                        return hs;
            return null;
        }

        private List<string> GetAllHSIds()
        {
            var ids = new List<string>();
            foreach (var vp in manager.Vertiports)
                foreach (var hs in vp.HoldingStructures)
                    ids.Add(hs.HoldingStructureId);
            return ids;
        }

        private CircleVisual FindCircleInHS(string hsId)
        {
            Transform container = manager.GetVertiportsContainer();
            if (container == null) return null;
            foreach (Transform vpChild in container)
            {
                Transform hsContainer = vpChild.Find("HoldingStructures");
                if (hsContainer == null) continue;
                Transform hsChild = hsContainer.Find($"HS_{hsId}");
                if (hsChild == null) continue;
                foreach (Transform t in hsChild)
                {
                    CircleVisual cv = t.GetComponent<CircleVisual>();
                    if (cv != null) return cv;
                }
            }
            return null;
        }

        #endregion

        #region Geometry Helpers

        private float DistanceFromRayToSegment(Ray ray, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            Vector3 aToOrigin = ray.origin - a;
            float abLenSq = ab.sqrMagnitude;
            Vector3 abNorm = abLenSq > 0.0001f ? ab / Mathf.Sqrt(abLenSq) : (b - a).normalized;

            Vector3 cross = Vector3.Cross(ray.direction, abNorm);
            float crossMag = cross.magnitude;

            if (crossMag < 0.0001f)
                return Mathf.Min(DistanceFromPointToRay(ray, a), DistanceFromPointToRay(ray, b));

            Vector3 crossNorm = cross / crossMag;
            float distance = Mathf.Abs(Vector3.Dot(aToOrigin, crossNorm));

            Vector3 aToClosest = aToOrigin - Vector3.Dot(aToOrigin, crossNorm) * crossNorm;
            float t = Vector3.Dot(aToClosest, abNorm) / Mathf.Sqrt(abLenSq);
            t = Mathf.Clamp01(t);

            if (t > 0 && t < 1) return distance;
            return Mathf.Min(DistanceFromPointToRay(ray, a), DistanceFromPointToRay(ray, b));
        }

        private float DistanceFromPointToRay(Ray ray, Vector3 point)
        {
            Vector3 originToPoint = point - ray.origin;
            float projection = Vector3.Dot(originToPoint, ray.direction);
            if (projection < 0) return originToPoint.magnitude;
            Vector3 closestOnRay = ray.origin + ray.direction * projection;
            return Vector3.Distance(point, closestOnRay);
        }

        private float DistanceFromRayToPolygon(Ray ray, List<Vector3> polygon)
        {
            if (polygon.Count < 2) return float.MaxValue;
            float minDist = float.MaxValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                int j = (i + 1) % polygon.Count;
                float d = DistanceFromRayToSegment(ray, polygon[i], polygon[j]);
                if (d < minDist) minDist = d;
            }
            return minDist;
        }

        private float DistanceFromRayToCircle(Ray ray, Vector3 center, float radius)
        {
            // Project ray to ground plane (y=center.y)
            Plane plane = new Plane(Vector3.up, center);
            if (!plane.Raycast(ray, out float dist)) return float.MaxValue;
            Vector3 hitPoint = ray.GetPoint(dist);
            float d = Vector3.Distance(hitPoint, center);
            return Mathf.Abs(d - radius);
        }

        private float DistanceFromRayToRampArc(Ray ray, RampVisual rv)
        {
            if (rv == null || rv.RampData == null) return float.MaxValue;

            CircleVisual cv = manager.GetCircleVisual(rv.LinkCircleId);
            if (cv == null || cv.CircleData == null) return float.MaxValue;

            VertexVisual fv = manager.GetVertexVisual(rv.RampData.FromVertexId);
            VertexVisual tv = manager.GetVertexVisual(rv.RampData.ToVertexId);
            if (fv == null || tv == null) return float.MaxValue;

            Vector3 center = cv.CircleData.Center;
            float radius = cv.CircleData.Radius;
            Vector3 fromPos = fv.transform.position;
            Vector3 toPos = tv.transform.position;

            float fromAngle = Mathf.Atan2(fromPos.z - center.z, fromPos.x - center.x);
            float toAngle = Mathf.Atan2(toPos.z - center.z, toPos.x - center.x);
            float diff = toAngle - fromAngle;
            while (diff > Mathf.PI) diff -= 2 * Mathf.PI;
            while (diff < -Mathf.PI) diff += 2 * Mathf.PI;

            int samples = 32;
            float minDist = float.MaxValue;
            for (int i = 0; i < samples; i++)
            {
                float t0 = i / (float)samples;
                float t1 = (i + 1) / (float)samples;
                float ang0 = fromAngle + diff * t0;
                float ang1 = fromAngle + diff * t1;
                Vector3 pt0 = center + new Vector3(Mathf.Cos(ang0), 0, Mathf.Sin(ang0)) * radius;
                Vector3 pt1 = center + new Vector3(Mathf.Cos(ang1), 0, Mathf.Sin(ang1)) * radius;
                float d = DistanceFromRayToSegment(ray, pt0, pt1);
                if (d < minDist) minDist = d;
            }
            return minDist;
        }

        private float DistanceFromRayToCorridorBezier(Ray ray, CorridorVisual cv, NetworkManager netMgr)
        {
            Vector3 lanePt = cv.CorridorData.LaneLinkPoint;
            Vector3 rampPt = cv.CorridorData.RampLinkPoint;
            float bezierDist = Vector3.Distance(lanePt, rampPt);
            float ratio = manager.corridorTangentRatio;
            float d = bezierDist * ratio;

            Vector3 laneDir = ComputeLaneDirectionForCorridor(cv, netMgr);
            Vector3 rampDir = ComputeRampDirectionForCorridor(cv);

            bool isEntry = cv.CorridorData.CorridorType == "Entry";
            Vector3 p0, p1, p2, p3;

            if (isEntry)
            {
                if (Vector3.Dot(rampDir, lanePt - rampPt) < 0) rampDir = -rampDir;
                p0 = rampPt; p3 = lanePt;
                p1 = p0 + rampDir * d;
                p2 = p3 - laneDir * d;
            }
            else
            {
                if (Vector3.Dot(laneDir, rampPt - lanePt) < 0) laneDir = -laneDir;
                p0 = lanePt; p3 = rampPt;
                p1 = p0 + laneDir * d;
                p2 = p3 - rampDir * d;
            }

            int segments = 32;
            float minDist = float.MaxValue;
            Vector3 prev = p0;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                float u = 1 - t;
                Vector3 pt = u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
                float segDist = DistanceFromRayToSegment(ray, prev, pt);
                if (segDist < minDist) minDist = segDist;
                prev = pt;
            }
            return minDist;
        }

        private Vector3 ComputeLaneDirectionForCorridor(CorridorVisual cv, NetworkManager netMgr)
        {
            if (netMgr == null || netMgr.FullNetwork == null) return Vector3.forward;

            foreach (var edge in netMgr.FullNetwork.Edges)
            {
                foreach (var lane in edge.Lanes)
                {
                    if (lane.LaneId != cv.CorridorData.LinkLaneId || lane.Centerline == null || lane.Centerline.Count < 2)
                        continue;

                    float bestDist = float.MaxValue;
                    Vector3 bestDir = Vector3.forward;
                    for (int i = 0; i < lane.Centerline.Count - 1; i++)
                    {
                        Vector3 proj = ProjectPointOnSegment(cv.CorridorData.LaneLinkPoint,
                            lane.Centerline[i], lane.Centerline[i + 1]);
                        float dist = Vector3.Distance(cv.CorridorData.LaneLinkPoint, proj);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestDir = (lane.Centerline[i + 1] - lane.Centerline[i]).normalized;
                        }
                    }
                    return bestDir;
                }
            }
            return Vector3.forward;
        }

        private Vector3 ComputeRampDirectionForCorridor(CorridorVisual cv)
        {
            RampVisual rv = manager.GetRampVisual(cv.CorridorData.LinkRampId);
            if (rv == null || rv.RampData == null) return Vector3.forward;

            VertexVisual fromV = manager.GetVertexVisual(rv.RampData.FromVertexId);
            VertexVisual toV = manager.GetVertexVisual(rv.RampData.ToVertexId);
            if (fromV == null || toV == null) return Vector3.forward;

            if (string.IsNullOrEmpty(rv.LinkCircleId))
            {
                return (toV.transform.position - fromV.transform.position).normalized;
            }
            else
            {
                CircleVisual circV = manager.GetCircleVisual(rv.LinkCircleId);
                if (circV == null || circV.CircleData == null) return Vector3.forward;

                Vector3 center = circV.CircleData.Center;
                Vector3 radial = cv.CorridorData.RampLinkPoint - center;
                radial.y = 0;
                if (radial.magnitude < 0.001f) return Vector3.forward;
                radial.Normalize();

                Vector3 fromDir = fromV.transform.position - center;
                Vector3 toDir = toV.transform.position - center;
                fromDir.y = 0; toDir.y = 0;
                float signedAngle = Mathf.Atan2(
                    Vector3.Dot(Vector3.up, Vector3.Cross(fromDir, toDir)),
                    Vector3.Dot(fromDir, toDir));

                Vector3 tangent = Vector3.Cross(Vector3.up, radial);
                if (signedAngle < 0) tangent = -tangent;
                return tangent.normalized;
            }
        }

        private void DrawRampArc(RampVisual rv, Vector3 fromPos, Vector3 toPos, float thickness)
        {
            CircleVisual cv = manager.GetCircleVisual(rv.LinkCircleId);
            if (cv == null || cv.CircleData == null) return;

            Vector3 center = cv.CircleData.Center;
            float radius = cv.CircleData.Radius;

            float fromAngle = Mathf.Atan2(fromPos.z - center.z, fromPos.x - center.x);
            float toAngle = Mathf.Atan2(toPos.z - center.z, toPos.x - center.x);
            float diff = toAngle - fromAngle;
            while (diff > Mathf.PI) diff -= 2 * Mathf.PI;
            while (diff < -Mathf.PI) diff += 2 * Mathf.PI;

            int segments = 48;
            Vector3 prev = fromPos;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = fromAngle + diff * t;
                Vector3 pos = center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                Handles.DrawLine(prev, pos, thickness);
                prev = pos;
            }
        }

        /// <summary>Ray-casting point-in-polygon test on the XZ plane.</summary>
        private bool IsPointInPolygon(Vector3 point, List<Vector3> polygon)
        {
            if (polygon == null || polygon.Count < 3) return false;
            int count = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                int j = (i + 1) % polygon.Count;
                Vector3 a = polygon[i];
                Vector3 b = polygon[j];
                if (((a.z > point.z) != (b.z > point.z)) &&
                    (point.x < (b.x - a.x) * (point.z - a.z) / (b.z - a.z) + a.x))
                    count++;
            }
            return count % 2 == 1;
        }

        /// <summary>Find a VertiportVisual whose boundary polygon contains the point (XZ plane).</summary>
        private VertiportVisual FindVertiportContainingPoint(Vector3 point)
        {
            Transform container = manager.GetVertiportsContainer();
            if (container == null) return null;
            foreach (Transform child in container)
            {
                VertiportVisual v = child.GetComponent<VertiportVisual>();
                if (v == null || v.VertiportData == null || v.VertiportData.Boundary == null) continue;
                if (IsPointInPolygon(point, v.VertiportData.Boundary))
                    return v;
            }
            return null;
        }

        #endregion

        #region Scene Helpers

        private void DrawSceneHelpers(SceneView sceneView)
        {
            Handles.BeginGUI();

            GUILayout.BeginArea(new Rect(10, 10, 300, 100));
            GUILayout.Box($"Mode: {currentMode}", GUILayout.Width(290));

            switch (currentMode)
            {
                case EditMode.Select:
                    VertiportVisual sv = manager.GetSelectedVertiport();
                    HelipadVisual sh = manager.GetSelectedHelipad();
                    CircleVisual sc = manager.GetSelectedCircle();
                    VertexVisual svx = manager.GetSelectedVertex();
                    RampVisual sr = manager.GetSelectedRamp();
                    ApproachNodeVisual san = manager.GetSelectedApproachNode();
                    ApproachEdgeVisual sae = manager.GetSelectedApproachEdge();
                    CorridorVisual sco2 = manager.GetSelectedCorridor();

                    if (sv != null) GUILayout.Box($"Selected: {sv.VertiportId}", GUILayout.Width(290));
                    else if (sh != null) GUILayout.Box($"Selected: {sh.HelipadId}", GUILayout.Width(290));
                    else if (sc != null) GUILayout.Box($"Selected: {sc.CircleId}", GUILayout.Width(290));
                    else if (svx != null) GUILayout.Box($"Selected: {svx.VertexId}", GUILayout.Width(290));
                    else if (sr != null) GUILayout.Box($"Selected: {sr.RampId}", GUILayout.Width(290));
                    else if (san != null) GUILayout.Box($"Selected: {san.NodeId}", GUILayout.Width(290));
                    else if (sae != null) GUILayout.Box($"Selected: {sae.EdgeId}", GUILayout.Width(290));
                    else if (sco2 != null) GUILayout.Box($"Selected: {sco2.CorridorId}", GUILayout.Width(290));
                    else GUILayout.Box("No selection", GUILayout.Width(290));
                    break;
                case EditMode.CreateVertiport:
                    GUILayout.Box($"Click to place VP boundary vertices\nRight-click/Enter to finish\nPoints: {polygonPoints.Count}", GUILayout.Width(290));
                    break;
                case EditMode.CreateHelipad:
                    GUILayout.Box($"Parent VP: {selectedVertiportIdForHelipad ?? "none"}\nClick to place HP boundary\nPoints: {polygonPoints.Count}", GUILayout.Width(290));
                    break;
                case EditMode.CreateHoldingStructure:
                    GUILayout.Box("Click to place HS position", GUILayout.Width(290));
                    break;
                case EditMode.CreateCircle:
                    GUILayout.Box($"HS: {selectedHSIdForCircle ?? "none"}\nClick to place circle center", GUILayout.Width(290));
                    break;
                case EditMode.CreateVertex:
                    GUILayout.Box("Click near a circle to add vertex", GUILayout.Width(290));
                    break;
                case EditMode.CreateRamp:
                    if (rampStartVertex == null)
                        GUILayout.Box("Click first vertex (From)", GUILayout.Width(290));
                    else
                        GUILayout.Box($"From: {rampStartVertex.VertexId}\nClick second vertex (To)", GUILayout.Width(290));
                    break;
                case EditMode.CreateNetwork:
                    if (currentApprSubMode == ApprSubMode.ApprNode)
                        GUILayout.Box($"Net: Appr Node\nVP: {selectedVertiportIdForApprNode ?? "none"}\nClick to place node", GUILayout.Width(290));
                    else if (apprEdgeStartNode == null)
                        GUILayout.Box("Net: Appr Edge\nClick first node (From)", GUILayout.Width(290));
                    else
                        GUILayout.Box($"Net: Appr Edge\nFrom: {apprEdgeStartNode.NodeId}\nClick second node (To)", GUILayout.Width(290));
                    break;
                case EditMode.CreateCorridor:
                    string corrInfo = $"Corridor: {corridorType}\nPhase: {corridorPhase}";
                    if (!string.IsNullOrEmpty(selectedLaneId))
                        corrInfo += $"\nLane: {selectedLaneId}";
                    if (!string.IsNullOrEmpty(selectedRampId))
                        corrInfo += $"\nRamp: {selectedRampId}";
                    GUILayout.Box(corrInfo, GUILayout.Width(290));
                    break;
            }

            GUILayout.EndArea();
            Handles.EndGUI();
        }

        private Vector3 GetMouseWorldPosition(Event e, SceneView sceneView)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            Plane plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(ray, out float dist))
                return ray.GetPoint(dist);
            return Vector3.zero;
        }

        #endregion
    }
}
