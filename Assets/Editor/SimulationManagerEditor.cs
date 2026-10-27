using UnityEngine;
using UnityEditor;

using System.Collections.Generic;
using System.Linq;
using RoadNetworkEditor.Routes;
using RoadNetworkEditor.RuntimeUI;

namespace RoadNetworkEditor.EditorScripts
{
    [CustomEditor(typeof(SimulationManager))]
    public class SimulationManagerEditor : Editor
    {
        private SimulationManager manager;

        private enum InspectorRouteMode
        {
            AutomaticShortest,
            ManualSceneSelection
        }

        // Route and demand authoring fields. These intentionally live in the
        // SimulationManager inspector so the Scene view remains the workspace.
        private int routeOriginHelipadIndex;
        private int routeDestinationHelipadIndex = 1;
        private InspectorRouteMode routeMode;
        private readonly List<string> selectedRouteLaneIds = new List<string>();
        private readonly List<string> selectedRouteEdgeIds = new List<string>();
        private string lockedFirstRouteEdgeId;
        private string hoveredRouteEdgeId;
        private bool routeSceneSelectionEnabled;
        private bool routeConfirmed;
        private bool routeConnectsDestination;
        private string selectedMissionId;
        private string routeAuthoringStatus = "Refresh topology, choose endpoints, then generate a route.";
        private MessageType routeAuthoringStatusType = MessageType.Info;
        private int editorTaskSequence;
        private float newDepartureTime = 0f;
        private int newPriority = 1;
        private string newTaskIdPrefix = "M";
        private int newFlowCount = 20;
        private float newFlowInterval = 8f;
        private float newFlowStartTime;

        // UI state
        private Vector2 missionScrollPos;
        private Vector2 mainScrollPos;
        private bool showMissionCreator = true;
        private bool showRuntimeInfo = false;

        // Auto-refresh
        private double lastRefreshTime = 0;
        private const double refreshInterval = 0.5;

        private void OnEnable()
        {
            manager = target as SimulationManager;
            if (manager != null)
                SceneView.duringSceneGui += DrawRouteSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DrawRouteSceneGUI;
            hoveredRouteEdgeId = null;
        }

        public override void OnInspectorGUI()
        {
            if (manager == null)
                manager = target as SimulationManager;
            if (manager == null)
            {
                EditorGUILayout.HelpBox("The SimulationManager target is no longer available.",
                    MessageType.Info);
                return;
            }

            serializedObject.Update();

            // Script reference (read-only)
            GUI.enabled = false;
            EditorGUILayout.ObjectField("Script", MonoScript.FromMonoBehaviour(manager), typeof(SimulationManager), false);
            GUI.enabled = true;

            EditorGUILayout.Space(10);

            mainScrollPos = EditorGUILayout.BeginScrollView(mainScrollPos);

            DrawReferencesSection();
            DrawDataPathsSection();
            DrawPlayControlSection();
            EditorGUILayout.Space(10);
            DrawMissionModeSection();
            EditorGUILayout.Space(10);
            DrawMissionCreatorSection();
            EditorGUILayout.Space(10);
            DrawMissionQueueSection();
            EditorGUILayout.Space(10);
            DrawSimulationControlSection();
            EditorGUILayout.Space(10);
            DrawRuntimeInfoSection();

            EditorGUILayout.EndScrollView();

            serializedObject.ApplyModifiedProperties();

            // Auto-repaint during simulation
            if (manager.IsRunning() && EditorApplication.timeSinceStartup - lastRefreshTime > refreshInterval)
            {
                lastRefreshTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        // ================================================================
        // REFERENCES
        // ================================================================
        void DrawReferencesSection()
        {
            EditorGUILayout.LabelField("References", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("aircraftPrefab"), new GUIContent("Aircraft Prefab"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("aircraftsParent"), new GUIContent("Aircrafts Parent"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("networkManager"), new GUIContent("Network Manager"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("vertiportManager"), new GUIContent("Vertiport Manager"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("config"), new GUIContent("Simulation Config"));

            if (manager.config == null)
            {
                EditorGUILayout.HelpBox("SimulationConfig not assigned. Drag a config asset or create one.", MessageType.Warning);
                if (GUILayout.Button("Create Default Config"))
                {
                    CreateDefaultConfig();
                }
            }
        }

        void DrawDataPathsSection()
        {
            EditorGUILayout.LabelField("Data Paths", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("dataFolder"), new GUIContent("Data Folder"));
        }

        void DrawPlayControlSection()
        {
            EditorGUILayout.LabelField("Play Control", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("autoStart"), new GUIContent("Auto Start"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enableJointSimulation"), new GUIContent("Start Joint AirSim Simulation"));
            if (manager.enableJointSimulation)
            {
                EditorGUILayout.HelpBox(
                    "The bridge starts when this simulation starts, not when Play Mode is entered. " +
                    "One AirSim vehicle is created per Unity aircraft and teleported to the Unity " +
                    "pose every frame; the Unreal camera follows the selected aircraft.\n\n" +
                    "Bridge hotkeys (active while the Unreal window has focus): " +
                    "H toggles follow/free-flight, N cycles the followed aircraft, " +
                    "J/L move the camera closer or farther, U/O raise or lower it.\n\n" +
                    "Editor only. The bridge runs outside the Unity project, so set the interpreter " +
                    "and the folder that contains airsim_bridge on the AirSimJointBridge component, " +
                    "or through the SAR_BRIDGE_PYTHON and SAR_BRIDGE_WORKDIR environment variables.",
                    MessageType.Info);
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("pauseOnCollision"), new GUIContent("Pause on Collision"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxSimDuration"), new GUIContent("Max Duration (s)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("resultsFilePath_Override"), new GUIContent("Results File"));

            EditorGUILayout.Space(4f);
            bool editorUiEnabled = SarRuntimeWorkbench.EditorPlayModeUiEnabled;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Editor Play Mode Runtime UI", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(editorUiEnabled
                    ? "Enabled — Play Mode uses the packaged-software workbench."
                    : "Disabled — Play Mode uses Inspectors and the Scene view only.",
                EditorStyles.wordWrappedMiniLabel);
            Color previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = editorUiEnabled
                ? new Color(0.86f, 0.48f, 0.36f)
                : new Color(0.45f, 0.72f, 0.55f);
            if (GUILayout.Button(editorUiEnabled
                    ? "Disable Runtime UI in Editor Play Mode"
                    : "Enable Runtime UI in Editor Play Mode", GUILayout.Height(28f)))
            {
                SarRuntimeWorkbench.EditorPlayModeUiEnabled = !editorUiEnabled;
                SarRuntimeWorkbench.ApplyEditorPlayModeUiPreference(manager);
                Repaint();
            }
            GUI.backgroundColor = previousBackground;
            EditorGUILayout.HelpBox(
                "This setting is persistent and affects only Unity Editor Play Mode. " +
                "Disabling it removes the generated Canvas, EventSystem, runtime navigation, " +
                "editing, route, analytics, and file-drop components. Windows builds always keep the UI.",
                MessageType.None);
            EditorGUILayout.EndVertical();
        }

        // ================================================================
        // MISSION MODE
        // ================================================================
        void DrawMissionModeSection()
        {
            EditorGUILayout.LabelField("Mission Mode", EditorStyles.boldLabel);

            if (manager.config == null) return;

            MissionGenerationMode currentMode = manager.config.missionMode;
            bool wasJSON = currentMode == MissionGenerationMode.JSON;
            bool wasRandom = currentMode == MissionGenerationMode.RandomContinuous;

            EditorGUILayout.BeginHorizontal();
            bool isJSON = GUILayout.Toggle(wasJSON, " JSON File", EditorStyles.miniButtonLeft);
            bool isRandom = GUILayout.Toggle(wasRandom, " Random Continuous", EditorStyles.miniButtonRight);
            EditorGUILayout.EndHorizontal();

            // Only the clicked toggle changes state — apply the one that actually flipped
            if (isJSON != wasJSON || isRandom != wasRandom)
            {
                Undo.RecordObject(manager.config, "Change Mission Mode");
                if (isJSON && !wasJSON)
                    manager.config.missionMode = MissionGenerationMode.JSON;
                else if (isRandom && !wasRandom)
                    manager.config.missionMode = MissionGenerationMode.RandomContinuous;
                EditorUtility.SetDirty(manager.config);
                currentMode = manager.config.missionMode;
            }

            // Config parameters based on mode
            if (currentMode == MissionGenerationMode.JSON)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Load Default Route File", GUILayout.Height(25)))
                {
                    manager.LoadRouteFile(null, true);
                    EditorUtility.SetDirty(manager);
                }
                if (GUILayout.Button("Import JSON / XML...", GUILayout.Height(25)))
                {
                    string path = EditorUtility.OpenFilePanel(
                        "Import Route File",
                        Application.dataPath,
                        string.Empty);
                    if (!string.IsNullOrEmpty(path))
                    {
                        manager.LoadRouteFile(path, true);
                        EditorUtility.SetDirty(manager);
                    }
                }
                EditorGUILayout.EndHorizontal();
                if (GUILayout.Button("Save Current Queue to JSON", GUILayout.Height(23)))
                {
                    manager.SaveMissionsToJSON();
                }
                EditorGUILayout.HelpBox(
                    "Supports legacy Missions.json plus SUMO-style named routes, vehicles, trips, and flows in JSON or XML.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Random Continuous mode generates missions on the fly during simulation. Missions are not saved to file.", MessageType.Info);

                EditorGUI.BeginChangeCheck();
                if (EditorGUI.EndChangeCheck())
                {
                    EditorUtility.SetDirty(manager.config);
                }
            }
        }

        // ================================================================
        // ROUTES AND TRAFFIC FLOW
        // ================================================================
        void DrawMissionCreatorSection()
        {
            showMissionCreator = EditorGUILayout.Foldout(showMissionCreator,
                "Routes & Traffic Flow (Inspector + Scene view)", true);
            if (!showMissionCreator) return;

            if (manager.config == null || manager.config.missionMode != MissionGenerationMode.JSON)
            {
                EditorGUILayout.HelpBox("Switch to JSON File mode to author routes and traffic flows.",
                    MessageType.Info);
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUI.BeginDisabledGroup(manager.IsRunning());

            if (GUILayout.Button("Refresh route-authoring topology", GUILayout.Height(26f)))
                PrepareRouteAuthoring(true);

            List<string> helipadIds = GetRouteHelipadIds();
            if (helipadIds.Count < 2)
            {
                EditorGUILayout.HelpBox(
                    "At least two helipads are required. Build the facilities, then refresh topology.",
                    MessageType.Warning);
                EditorGUI.EndDisabledGroup();
                EditorGUI.indentLevel--;
                return;
            }

            routeOriginHelipadIndex = Mathf.Clamp(routeOriginHelipadIndex, 0, helipadIds.Count - 1);
            routeDestinationHelipadIndex = Mathf.Clamp(routeDestinationHelipadIndex, 0, helipadIds.Count - 1);
            if (routeDestinationHelipadIndex == routeOriginHelipadIndex)
                routeDestinationHelipadIndex = (routeOriginHelipadIndex + 1) % helipadIds.Count;

            EditorGUI.BeginChangeCheck();
            routeOriginHelipadIndex = EditorGUILayout.Popup("Origin helipad",
                routeOriginHelipadIndex, helipadIds.ToArray());
            routeDestinationHelipadIndex = EditorGUILayout.Popup("Destination helipad",
                routeDestinationHelipadIndex, helipadIds.ToArray());
            if (EditorGUI.EndChangeCheck())
                HandleRouteEndpointsChanged(helipadIds);

            InspectorRouteMode nextMode = (InspectorRouteMode)GUILayout.Toolbar((int)routeMode,
                new[] { "Automatic shortest", "Manual: click centerlines" }, GUILayout.Height(25f));
            if (nextMode != routeMode)
            {
                routeMode = nextMode;
                selectedMissionId = null;
                if (routeMode == InspectorRouteMode.AutomaticShortest)
                    BuildShortestRoute();
                else
                    InitializeManualRoute();
            }

            if (routeMode == InspectorRouteMode.AutomaticShortest)
            {
                if (GUILayout.Button("Generate / refresh shortest route", GUILayout.Height(26f)))
                    BuildShortestRoute();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Click an axis centerline in the Scene view to append it. Clicking an already " +
                    "selected centerline rolls the route back to that position. The first departure " +
                    "edge is locked, matching the packaged application.", MessageType.None);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(routeSceneSelectionEnabled
                        ? "Stop Scene selection (Esc)"
                        : "Select centerlines in Scene view", GUILayout.Height(28f)))
                {
                    if (selectedRouteEdgeIds.Count == 0 && !InitializeManualRoute())
                        routeSceneSelectionEnabled = false;
                    else
                        routeSceneSelectionEnabled = !routeSceneSelectionEnabled;
                    hoveredRouteEdgeId = null;
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Reset to locked first edge", GUILayout.Height(28f)))
                    InitializeManualRoute();
                EditorGUILayout.EndHorizontal();

                GUI.enabled = routeConnectsDestination && !routeConfirmed;
                if (GUILayout.Button("Confirm manual route", GUILayout.Height(28f)))
                    ConfirmManualRoute();
                GUI.enabled = !manager.IsRunning();
            }

            string routeSummary = selectedRouteEdgeIds.Count == 0
                ? "No route preview."
                : $"{selectedRouteEdgeIds.Count} axis edge(s), {selectedRouteLaneIds.Count} " +
                  $"lane/corridor segment(s)\n{string.Join(" → ", selectedRouteEdgeIds)}";
            EditorGUILayout.HelpBox(routeSummary, MessageType.None);
            EditorGUILayout.HelpBox(routeAuthoringStatus, routeAuthoringStatusType);

            newTaskIdPrefix = EditorGUILayout.TextField("Task ID Prefix", newTaskIdPrefix);
            newDepartureTime = EditorGUILayout.FloatField("Departure Time (s)", newDepartureTime);
            newPriority = EditorGUILayout.IntSlider("Priority", newPriority, 1, 10);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add single mission", GUILayout.Height(27f)))
                AddSingleAuthoredMission();
            GUI.enabled = !string.IsNullOrEmpty(selectedMissionId) && !manager.IsRunning();
            if (GUILayout.Button("Update selected mission", GUILayout.Height(27f)))
                UpdateSelectedMission();
            GUI.enabled = !manager.IsRunning();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Periodic traffic flow", EditorStyles.boldLabel);
            newFlowCount = Mathf.Max(1, EditorGUILayout.IntField("Aircraft count", newFlowCount));
            newFlowInterval = EditorGUILayout.FloatField("Interval (s)", newFlowInterval);
            newFlowStartTime = EditorGUILayout.FloatField("Start time (s)", newFlowStartTime);
            if (GUILayout.Button("Add periodic flow", GUILayout.Height(27f)))
                AddAuthoredFlow();

            EditorGUI.EndDisabledGroup();
            EditorGUI.indentLevel--;
        }

        // ================================================================
        // MISSION QUEUE
        // ================================================================
        void DrawMissionQueueSection()
        {
            List<MissionTask> missions = manager.GetPendingMissions();
            EditorGUILayout.LabelField($"Mission Queue ({missions.Count})", EditorStyles.boldLabel);

            if (missions.Count == 0)
            {
                EditorGUILayout.HelpBox("No missions in queue.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear Queue", GUILayout.Width(100)))
            {
                manager.ClearMissionQueue();
                selectedMissionId = null;
                EditorUtility.SetDirty(manager);
            }
            if (manager.config != null && manager.config.missionMode == MissionGenerationMode.JSON)
            {
                if (GUILayout.Button("Save to JSON", GUILayout.Width(100)))
                {
                    manager.SaveMissionsToJSON();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Scrollable mission list
            missionScrollPos = EditorGUILayout.BeginScrollView(missionScrollPos, GUILayout.Height(150));

            for (int i = 0; i < missions.Count; i++)
            {
                var m = missions[i];
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                EditorGUILayout.LabelField($"{i + 1}.", GUILayout.Width(25));
                EditorGUILayout.LabelField(m.TaskId, GUILayout.Width(70));

                string originStr = m.OriginVertiportId;
                if (!string.IsNullOrEmpty(m.OriginHelipadId))
                    originStr += "/" + m.OriginHelipadId;
                string destStr = m.DestinationVertiportId;
                if (!string.IsNullOrEmpty(m.DestinationHelipadId))
                    destStr += "/" + m.DestinationHelipadId;
                EditorGUILayout.LabelField($"{originStr} -> {destStr}", GUILayout.Width(175));
                EditorGUILayout.LabelField($"T+{m.DepartureTime:F0}s", GUILayout.Width(55));
                EditorGUILayout.LabelField($"P{m.Priority}", GUILayout.Width(25));
                if (m.RouteLaneIds != null && m.RouteLaneIds.Count > 0)
                    EditorGUILayout.LabelField($"Route:{m.RouteLaneIds.Count}", GUILayout.Width(65));

                if (GUILayout.Button("Edit", GUILayout.Width(38)))
                {
                    LoadMissionForEditing(m);
                    break;
                }

                // Remove button
                if (GUILayout.Button("X", GUILayout.Width(25)))
                {
                    // Dequeue and re-enqueue all except this one
                    var temp = new List<MissionTask>();
                    for (int j = 0; j < missions.Count; j++)
                    {
                        if (j != i) temp.Add(missions[j]);
                    }
                    manager.ClearMissionQueue();
                    foreach (var t in temp) manager.AddMission(t);
                    if (selectedMissionId == m.TaskId) selectedMissionId = null;
                    EditorUtility.SetDirty(manager);
                    break;
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        // ================================================================
        // SIMULATION CONTROL
        // ================================================================
        void DrawSimulationControlSection()
        {
            EditorGUILayout.LabelField("Simulation Control", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Simulation requires Play Mode. Click below to enter Play Mode with auto-start.", MessageType.Info);
                if (GUILayout.Button("Enter Play Mode & Start", GUILayout.Height(30)))
                {
                    // Save missions to JSON so they survive the Play mode transition
                    if (manager.config != null && manager.config.missionMode == MissionGenerationMode.JSON
                        && manager.GetPendingMissions().Count > 0)
                    {
                        manager.SaveMissionsToJSON();
                    }
                    SimulationManager.AutoStartRequested = true;
                    EditorApplication.EnterPlaymode();
                }
                return;
            }

            bool running = manager.IsRunning();

            EditorGUILayout.BeginHorizontal();

            GUI.enabled = !running;
            GUI.backgroundColor = Color.green;
            if (GUILayout.Button("Start Simulation", GUILayout.Height(35)))
            {
                if (manager.config == null)
                {
                    EditorUtility.DisplayDialog("Missing Config", "Please assign a SimulationConfig first.", "OK");
                }
                else if (manager.config.missionMode == MissionGenerationMode.JSON && manager.GetPendingMissions().Count == 0)
                {
                    bool load = EditorUtility.DisplayDialog("Empty Queue",
                        "Mission queue is empty. Load the configured JSON/XML route file?",
                        "Load Route File", "Cancel");
                    if (load)
                    {
                        manager.LoadRouteFile(null, true);
                        if (manager.GetPendingMissions().Count > 0)
                        {
                            manager.StartSimulation();
                        }
                    }
                }
                else
                {
                    manager.StartSimulation();
                }
            }
            GUI.backgroundColor = Color.white;

            GUI.enabled = running;
            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("Stop Simulation", GUILayout.Height(35)))
            {
                manager.StopSimulation();
            }
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            if (running)
            {
                EditorGUILayout.HelpBox("Simulation is RUNNING...", MessageType.Info);
            }
        }

        // ================================================================
        // RUNTIME INFO
        // ================================================================
        void DrawRuntimeInfoSection()
        {
            showRuntimeInfo = EditorGUILayout.Foldout(showRuntimeInfo, "Runtime State", true);
            if (!showRuntimeInfo) return;

            EditorGUI.indentLevel++;

            EditorGUILayout.LabelField($"Simulation Running: {(manager.IsRunning() ? "Yes" : "No")}");
            EditorGUILayout.LabelField($"Aircraft Active: {manager.AircraftObjects.Count}");
            EditorGUILayout.LabelField($"Aircraft States: {manager.AircraftStates.Count}");
            EditorGUILayout.LabelField($"Mission Queue: {manager.GetPendingMissions().Count}");
            EditorGUILayout.LabelField($"Lane States: {manager.LaneStates.Count}");

            EditorGUILayout.Space(5);

            // Metrics display
            var m = manager.Metrics;
            if (m != null && (m.SuccessfulFlight > 0 || m.FailedFlight > 0))
            {
                EditorGUILayout.LabelField("Metrics", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"  System Capacity: {m.SystemCapacity}");
                EditorGUILayout.LabelField($"  Successful: {m.SuccessfulFlight}  Failed: {m.FailedFlight}");
                EditorGUILayout.LabelField($"  Failure Rate: {m.FailureRate:P1}");
                EditorGUILayout.LabelField($"  Avg Flight Length: {m.AverageFlightLength:F1}");
                EditorGUILayout.LabelField($"  Avg Flight Time: {m.AverageFlightTime:F1}s");
                EditorGUILayout.LabelField($"  Takeoff Delay: {m.TakeoffDelayTime:F1}s");
                EditorGUILayout.LabelField($"  Schedule Delay: {m.ScheduleDelay:F1}s");
            }

            EditorGUI.indentLevel--;
        }

        // ================================================================
        // HELPERS
        // ================================================================
        private List<string> GetRouteHelipadIds()
        {
            IEnumerable<string> runtimeIds = manager?.Helipads?.Keys ?? Enumerable.Empty<string>();
            IEnumerable<string> sceneIds = (manager?.vertiportManager?.Vertiports ?? new List<Vertiport>())
                .Where(vertiport => vertiport != null)
                .SelectMany(vertiport => vertiport.Helipads ?? new List<Helipad>())
                .Where(helipad => helipad != null)
                .Select(helipad => helipad.HelipadId);
            return runtimeIds.Concat(sceneIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct().OrderBy(id => id).ToList();
        }

        private bool TryGetRouteEndpoints(out string originHelipadId,
            out string destinationHelipadId)
        {
            List<string> helipads = GetRouteHelipadIds();
            originHelipadId = null;
            destinationHelipadId = null;
            if (helipads.Count < 2) return false;
            routeOriginHelipadIndex = Mathf.Clamp(routeOriginHelipadIndex, 0, helipads.Count - 1);
            routeDestinationHelipadIndex = Mathf.Clamp(routeDestinationHelipadIndex, 0, helipads.Count - 1);
            if (routeDestinationHelipadIndex == routeOriginHelipadIndex)
                routeDestinationHelipadIndex = (routeOriginHelipadIndex + 1) % helipads.Count;
            originHelipadId = helipads[routeOriginHelipadIndex];
            destinationHelipadId = helipads[routeDestinationHelipadIndex];
            return true;
        }

        private void HandleRouteEndpointsChanged(List<string> helipadIds)
        {
            if (helipadIds == null || helipadIds.Count < 2) return;
            if (routeDestinationHelipadIndex == routeOriginHelipadIndex)
                routeDestinationHelipadIndex = (routeOriginHelipadIndex + 1) % helipadIds.Count;
            selectedMissionId = null;
            if (routeMode == InspectorRouteMode.AutomaticShortest)
                BuildShortestRoute();
            else
                InitializeManualRoute();
        }

        private bool PrepareRouteAuthoring(bool reportSuccess)
        {
            if (manager == null)
            {
                SetRouteStatus("SimulationManager is unavailable.", MessageType.Error);
                return false;
            }
            if (!manager.RefreshRuntimeTopology())
            {
                SetRouteStatus("Stop the simulation before editing route demand.", MessageType.Error);
                return false;
            }
            if (manager.Lanes.Count == 0 || manager.Helipads.Count < 2)
            {
                SetRouteStatus("The active topology does not contain routable lanes and two helipads.",
                    MessageType.Error);
                return false;
            }
            if (reportSuccess)
                SetRouteStatus($"Topology ready: {manager.Edges.Count} axis edges, " +
                               $"{manager.Lanes.Count} lanes/corridors, {manager.Helipads.Count} helipads.",
                    MessageType.Info);
            Repaint();
            SceneView.RepaintAll();
            return true;
        }

        private bool BuildShortestRoute()
        {
            routeSceneSelectionEnabled = false;
            hoveredRouteEdgeId = null;
            selectedRouteLaneIds.Clear();
            selectedRouteEdgeIds.Clear();
            lockedFirstRouteEdgeId = null;
            routeConfirmed = false;
            routeConnectsDestination = false;
            if (!PrepareRouteAuthoring(false) ||
                !TryGetRouteEndpoints(out string origin, out string destination))
            {
                SceneView.RepaintAll();
                return false;
            }
            if (!RouteDemandAuthoring.TryBuildShortestLaneRoute(manager, origin, destination,
                    out List<string> laneIds, out string error))
            {
                SetRouteStatus("Shortest-route generation failed: " + error, MessageType.Error);
                SceneView.RepaintAll();
                return false;
            }

            selectedRouteLaneIds.AddRange(laneIds);
            selectedRouteEdgeIds.AddRange(RouteDemandAuthoring.CollapseLaneEdges(manager, laneIds));
            routeConfirmed = true;
            routeConnectsDestination = true;
            SetRouteStatus($"Shortest route ready: {selectedRouteEdgeIds.Count} highlighted axis edges.",
                MessageType.Info);
            SceneView.RepaintAll();
            Repaint();
            return true;
        }

        private bool InitializeManualRoute()
        {
            selectedRouteLaneIds.Clear();
            selectedRouteEdgeIds.Clear();
            lockedFirstRouteEdgeId = null;
            hoveredRouteEdgeId = null;
            routeConfirmed = false;
            routeConnectsDestination = false;
            routeSceneSelectionEnabled = true;
            if (!PrepareRouteAuthoring(false) ||
                !TryGetRouteEndpoints(out string origin, out string destination))
            {
                routeSceneSelectionEnabled = false;
                SceneView.RepaintAll();
                return false;
            }
            if (!RouteDemandAuthoring.TryGetManualRouteStart(manager, origin, destination,
                    out _, out string firstEdgeId, out string error))
            {
                routeSceneSelectionEnabled = false;
                SetRouteStatus("Manual route initialization failed: " + error, MessageType.Error);
                SceneView.RepaintAll();
                return false;
            }

            lockedFirstRouteEdgeId = firstEdgeId;
            selectedRouteEdgeIds.Add(firstEdgeId);
            routeConnectsDestination = RouteDemandAuthoring.TryResolveManualEdges(manager,
                origin, destination, selectedRouteEdgeIds, true,
                out List<string> initialLanes, out _);
            if (!routeConnectsDestination)
                RouteDemandAuthoring.TryResolveManualEdges(manager, origin, destination,
                    selectedRouteEdgeIds, false, out initialLanes, out _);
            selectedRouteLaneIds.AddRange(initialLanes);
            SetRouteStatus(routeConnectsDestination
                    ? $"Locked departure edge '{firstEdgeId}' already reaches the destination. " +
                      "Confirm the manual route."
                    : $"Departure edge '{firstEdgeId}' is locked. Click the next axis " +
                      "centerline in the Scene view.",
                MessageType.Info);
            SceneView.RepaintAll();
            Repaint();
            return true;
        }

        private bool ConfirmManualRoute()
        {
            if (!TryGetRouteEndpoints(out string origin, out string destination))
            {
                routeConfirmed = false;
                SetRouteStatus("Select two valid helipad endpoints.", MessageType.Error);
                return false;
            }
            if (!RouteDemandAuthoring.TryResolveManualEdges(manager, origin, destination,
                    selectedRouteEdgeIds, true, out List<string> laneIds, out string error))
            {
                routeConfirmed = false;
                SetRouteStatus("Manual route is incomplete: " + error, MessageType.Error);
                return false;
            }
            selectedRouteLaneIds.Clear();
            selectedRouteLaneIds.AddRange(laneIds);
            routeConfirmed = true;
            routeConnectsDestination = true;
            routeSceneSelectionEnabled = false;
            hoveredRouteEdgeId = null;
            SetRouteStatus($"Manual route confirmed: {selectedRouteEdgeIds.Count} axis edges, " +
                           $"{selectedRouteLaneIds.Count} lane/corridor segments.", MessageType.Info);
            SceneView.RepaintAll();
            Repaint();
            return true;
        }

        private bool EnsureAuthoredRoute()
        {
            if (routeMode == InspectorRouteMode.AutomaticShortest)
                return BuildShortestRoute();
            if (!routeConfirmed)
            {
                SetRouteStatus(routeConnectsDestination
                    ? "The selected centerlines reach the destination; confirm the manual route first."
                    : "Continue selecting connected centerlines until the destination is reached.",
                    MessageType.Warning);
                return false;
            }
            return ConfirmManualRoute();
        }

        private MissionTask CreateAuthoredMission(float departureTime)
        {
            TryGetRouteEndpoints(out string origin, out string destination);
            int sequence = ++editorTaskSequence;
            string prefix = string.IsNullOrWhiteSpace(newTaskIdPrefix) ? "M" : newTaskIdPrefix.Trim();
            string taskId = $"{prefix}-{System.DateTime.Now:HHmmss}-{sequence:D4}";
            return RouteDemandAuthoring.CreateMission(manager, origin, destination,
                routeMode == InspectorRouteMode.ManualSceneSelection
                    ? selectedRouteLaneIds : Enumerable.Empty<string>(),
                Mathf.Max(0f, departureTime), newPriority, taskId, $"R-{taskId}");
        }

        private void AddSingleAuthoredMission()
        {
            if (!EnsureAuthoredRoute()) return;
            MissionTask mission = CreateAuthoredMission(newDepartureTime);
            manager.AddMission(mission);
            selectedMissionId = null;
            EditorUtility.SetDirty(manager);
            SetRouteStatus($"Mission '{mission.TaskId}' was added to the queue.", MessageType.Info);
        }

        private void AddAuthoredFlow()
        {
            if (newFlowCount < 1 || newFlowCount > 10000 || newFlowInterval < 0.1f ||
                float.IsNaN(newFlowInterval) || float.IsInfinity(newFlowInterval) ||
                newFlowStartTime < 0f || float.IsNaN(newFlowStartTime) ||
                float.IsInfinity(newFlowStartTime))
            {
                SetRouteStatus("Flow count must be 1–10000, start time non-negative, " +
                               "and interval at least 0.1 s.", MessageType.Error);
                return;
            }
            if (!EnsureAuthoredRoute()) return;
            for (int index = 0; index < newFlowCount; index++)
                manager.AddMission(CreateAuthoredMission(newFlowStartTime + index * newFlowInterval));
            selectedMissionId = null;
            EditorUtility.SetDirty(manager);
            SetRouteStatus($"Added a periodic flow of {newFlowCount} aircraft at " +
                           $"{newFlowInterval:0.###} s intervals.", MessageType.Info);
        }

        private void UpdateSelectedMission()
        {
            if (string.IsNullOrEmpty(selectedMissionId) || !EnsureAuthoredRoute()) return;
            MissionTask replacement = CreateAuthoredMission(newDepartureTime);
            replacement.TaskId = selectedMissionId;
            replacement.RouteId = routeMode == InspectorRouteMode.ManualSceneSelection
                ? $"R-{selectedMissionId}" : null;
            if (!manager.ReplaceMission(selectedMissionId, replacement))
            {
                SetRouteStatus("The selected mission is no longer in the pending queue.",
                    MessageType.Error);
                selectedMissionId = null;
                return;
            }
            EditorUtility.SetDirty(manager);
            SetRouteStatus($"Mission '{selectedMissionId}' was updated.", MessageType.Info);
        }

        private void LoadMissionForEditing(MissionTask mission)
        {
            if (mission == null || !PrepareRouteAuthoring(false)) return;
            List<string> helipads = GetRouteHelipadIds();
            string origin = ResolveMissionHelipad(mission.OriginHelipadId,
                mission.OriginVertiportId, helipads);
            string destination = ResolveMissionHelipad(mission.DestinationHelipadId,
                mission.DestinationVertiportId, helipads);
            int originIndex = helipads.IndexOf(origin);
            int destinationIndex = helipads.IndexOf(destination);
            if (originIndex < 0 || destinationIndex < 0 || originIndex == destinationIndex)
            {
                SetRouteStatus("The mission endpoints cannot be mapped to two current helipads.",
                    MessageType.Error);
                return;
            }

            selectedMissionId = mission.TaskId;
            routeOriginHelipadIndex = originIndex;
            routeDestinationHelipadIndex = destinationIndex;
            newDepartureTime = mission.DepartureTime;
            newPriority = Mathf.Clamp(mission.Priority, 1, 10);
            selectedRouteLaneIds.Clear();
            selectedRouteEdgeIds.Clear();
            if (mission.RouteLaneIds != null && mission.RouteLaneIds.Count > 0)
            {
                routeMode = InspectorRouteMode.ManualSceneSelection;
                selectedRouteLaneIds.AddRange(mission.RouteLaneIds);
                selectedRouteEdgeIds.AddRange(
                    RouteDemandAuthoring.CollapseLaneEdges(manager, mission.RouteLaneIds));
                lockedFirstRouteEdgeId = selectedRouteEdgeIds.FirstOrDefault();
                routeConfirmed = true;
                routeConnectsDestination = true;
                routeSceneSelectionEnabled = false;
                SetRouteStatus($"Loaded mission '{mission.TaskId}' for editing.", MessageType.Info);
            }
            else
            {
                routeMode = InspectorRouteMode.AutomaticShortest;
                BuildShortestRoute();
                SetRouteStatus($"Loaded automatic mission '{mission.TaskId}' for editing.",
                    MessageType.Info);
            }
            SceneView.RepaintAll();
            Repaint();
        }

        private string ResolveMissionHelipad(string explicitHelipadId, string vertiportId,
            List<string> availableHelipads)
        {
            if (!string.IsNullOrEmpty(explicitHelipadId) &&
                availableHelipads.Contains(explicitHelipadId)) return explicitHelipadId;
            return GetHelipadIdsForVP(vertiportId).FirstOrDefault(availableHelipads.Contains);
        }

        private void SetRouteStatus(string message, MessageType type)
        {
            routeAuthoringStatus = string.IsNullOrWhiteSpace(message) ? "Ready." : message;
            routeAuthoringStatusType = type;
            Repaint();
        }

        private IEnumerable<Edge> GetAxisEdges()
        {
            IEnumerable<Edge> edges = manager?.networkManager?.AxisNetwork?.Edges;
            return edges ?? manager?.Edges?.Values ?? Enumerable.Empty<Edge>();
        }

        private void DrawRouteSceneGUI(SceneView sceneView)
        {
            if (manager == null || sceneView == null) return;
            if (manager.IsRunning())
            {
                routeSceneSelectionEnabled = false;
                hoveredRouteEdgeId = null;
            }

            List<Edge> axisEdges = GetAxisEdges()
                .Where(edge => edge?.Centerline != null && edge.Centerline.Count > 1)
                .ToList();
            Event current = Event.current;
            if (routeMode == InspectorRouteMode.ManualSceneSelection &&
                routeSceneSelectionEnabled && !manager.IsRunning())
            {
                int controlId = GUIUtility.GetControlID("SAR route centerline selection".GetHashCode(),
                    FocusType.Passive);
                if (current.type == EventType.Layout)
                    HandleUtility.AddDefaultControl(controlId);

                Edge nearest = FindNearestAxisEdge(axisEdges, current.mousePosition,
                    sceneView.camera, 30f);
                string nextHovered = nearest?.EdgeId;
                if (nextHovered != hoveredRouteEdgeId)
                {
                    hoveredRouteEdgeId = nextHovered;
                    sceneView.Repaint();
                    Repaint();
                }

                if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
                {
                    routeSceneSelectionEnabled = false;
                    hoveredRouteEdgeId = null;
                    SetRouteStatus("Scene centerline selection paused.", MessageType.Info);
                    current.Use();
                    SceneView.RepaintAll();
                }
                else if (current.type == EventType.MouseDown && current.button == 0 &&
                         !current.alt && nearest != null &&
                         TryGetRouteEndpoints(out string origin, out string destination))
                {
                    if (RouteDemandAuthoring.TryApplyManualEdgeClick(manager, origin, destination,
                            selectedRouteEdgeIds, nearest.EdgeId, out List<string> laneIds,
                            out bool connectsDestination, out string error))
                    {
                        selectedRouteLaneIds.Clear();
                        selectedRouteLaneIds.AddRange(laneIds);
                        routeConnectsDestination = connectsDestination;
                        routeConfirmed = false;
                        SetRouteStatus(connectsDestination
                                ? "The highlighted centerlines reach the destination. Confirm the manual route."
                                : $"Added '{nearest.EdgeId}'. Continue with the next connected centerline.",
                            MessageType.Info);
                    }
                    else
                    {
                        SetRouteStatus($"Cannot select '{nearest.EdgeId}': {error}", MessageType.Warning);
                    }
                    current.Use();
                    SceneView.RepaintAll();
                    Repaint();
                }
            }

            if (current.type == EventType.Repaint &&
                (routeSceneSelectionEnabled || selectedRouteEdgeIds.Count > 0))
                DrawAxisRouteOverlay(axisEdges);
        }

        private void DrawAxisRouteOverlay(List<Edge> axisEdges)
        {
            UnityEngine.Rendering.CompareFunction previousZTest = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            var selected = new HashSet<string>(selectedRouteEdgeIds);
            foreach (Edge edge in axisEdges)
            {
                bool hovered = edge.EdgeId == hoveredRouteEdgeId;
                bool included = selected.Contains(edge.EdgeId);
                if (!routeSceneSelectionEnabled && !included) continue;
                Handles.color = hovered
                    ? new Color(1f, 0.62f, 0.08f, 1f)
                    : included
                        ? new Color(0.84f, 0.24f, 0.10f, 1f)
                        : new Color(0.25f, 0.22f, 0.19f, 0.72f);
                float width = hovered ? 6f : included ? 4.5f : 1.5f;
                Vector3[] lifted = edge.Centerline
                    .Select(point => point + Vector3.up * 0.48f).ToArray();
                Handles.DrawAAPolyLine(width, lifted);

                int order = selectedRouteEdgeIds.IndexOf(edge.EdgeId);
                if (order >= 0)
                {
                    var labelStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 12,
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = Color.white }
                    };
                    Handles.Label(lifted[lifted.Length / 2], (order + 1).ToString(), labelStyle);
                }
            }
            Handles.zTest = previousZTest;

            if (!routeSceneSelectionEnabled) return;
            Handles.BeginGUI();
            Rect panel = new Rect(10f, 10f, 410f, 72f);
            GUI.Box(panel, GUIContent.none, EditorStyles.helpBox);
            GUI.Label(new Rect(20f, 18f, 390f, 20f),
                "Manual route: click axis centerlines in order", EditorStyles.boldLabel);
            GUI.Label(new Rect(20f, 42f, 390f, 20f),
                $"Locked first edge: {lockedFirstRouteEdgeId ?? "none"}   " +
                $"Selected: {selectedRouteEdgeIds.Count}   Esc: pause selection");
            Handles.EndGUI();
        }

        private static Edge FindNearestAxisEdge(IEnumerable<Edge> edges, Vector2 mousePosition,
            Camera sceneCamera, float maxPixels)
        {
            Edge best = null;
            float bestDistance = maxPixels;
            foreach (Edge edge in edges ?? Enumerable.Empty<Edge>())
            {
                for (int index = 1; index < edge.Centerline.Count; index++)
                {
                    Vector3 previousViewport = sceneCamera.WorldToViewportPoint(edge.Centerline[index - 1]);
                    Vector3 currentViewport = sceneCamera.WorldToViewportPoint(edge.Centerline[index]);
                    if (previousViewport.z <= 0f && currentViewport.z <= 0f) continue;
                    Vector2 from = HandleUtility.WorldToGUIPoint(edge.Centerline[index - 1]);
                    Vector2 to = HandleUtility.WorldToGUIPoint(edge.Centerline[index]);
                    float distance = DistanceToSegment(mousePosition, from, to);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = edge;
                }
            }
            return best;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 from, Vector2 to)
        {
            Vector2 line = to - from;
            float lengthSquared = line.sqrMagnitude;
            if (lengthSquared <= 0.001f) return Vector2.Distance(point, from);
            float ratio = Mathf.Clamp01(Vector2.Dot(point - from, line) / lengthSquared);
            return Vector2.Distance(point, from + line * ratio);
        }

        List<string> GetHelipadIdsForVP(string vpId)
        {
            List<string> ids = new List<string>();
            if (manager.vertiportManager == null) return ids;

            var vp = manager.vertiportManager.Vertiports?.Find(v => v.VertiportId == vpId);
            if (vp?.Helipads == null) return ids;

            foreach (var hp in vp.Helipads)
                ids.Add(hp.HelipadId);
            return ids;
        }
        void CreateDefaultConfig()
        {
            string path = "Assets/DataBase/DefaultSimulationConfig.asset";

            // Check if already exists
            var existing = AssetDatabase.LoadAssetAtPath<SimulationConfig>(path);
            if (existing != null)
            {
                manager.config = existing;
                EditorUtility.SetDirty(manager);
                Debug.Log("[Editor] Using existing SimulationConfig.");
                return;
            }

            var config = ScriptableObject.CreateInstance<SimulationConfig>();
            AssetDatabase.CreateAsset(config, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            manager.config = config;
            EditorUtility.SetDirty(manager);
            Debug.Log($"[Editor] Created SimulationConfig at: {path}");
        }
    }
}
