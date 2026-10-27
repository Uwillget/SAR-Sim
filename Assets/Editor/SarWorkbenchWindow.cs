using System.Collections.Generic;
using System.IO;
using System.Linq;
using RoadNetworkEditor.Networking;
using RoadNetworkEditor.Routes;
using UnityEditor;
using UnityEngine;

namespace RoadNetworkEditor.EditorTools
{
    /// <summary>
    /// A compact entry point for the complete authoring workflow. Detailed
    /// net-edit operations remain in the mature manager inspectors and Scene view.
    /// </summary>
    public sealed class SarWorkbenchWindow : EditorWindow
    {
        private static readonly string[] Tabs =
        {
            "Overview", "Simulation", "Network", "Facilities", "Build"
        };

        private SimulationManager simulationManager;
        private NetworkManager networkManager;
        private VertiportManager vertiportManager;
        private ParameterSweepRunner parameterSweepRunner;
        private int selectedTab;
        private Vector2 scrollPosition;
        private string routeFilePath = string.Empty;
        private string lastMessage = "Ready.";
        private MessageType lastMessageType = MessageType.Info;

        [MenuItem("Tools/SAR Workbench")]
        public static void Open()
        {
            SarWorkbenchWindow window = GetWindow<SarWorkbenchWindow>();
            window.titleContent = new GUIContent("SAR Workbench");
            window.minSize = new Vector2(620f, 560f);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshManagers();
            EditorApplication.hierarchyChanged += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= Repaint;
        }

        private void OnGUI()
        {
            DrawHeader();
            selectedTab = GUILayout.Toolbar(selectedTab, Tabs, GUILayout.Height(28f));
            EditorGUILayout.Space(8f);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            DrawManagerPicker();
            EditorGUILayout.Space(8f);

            switch (selectedTab)
            {
                case 0: DrawOverview(); break;
                case 1: DrawSimulation(); break;
                case 2: DrawNetwork(); break;
                case 3: DrawFacilities(); break;
                case 4: DrawBuild(); break;
            }

            EditorGUILayout.Space(12f);
            EditorGUILayout.HelpBox(lastMessage, lastMessageType);
            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 64f);
            EditorGUI.DrawRect(rect, new Color(0.04f, 0.09f, 0.12f));
            Rect titleRect = new Rect(rect.x + 18f, rect.y + 8f, rect.width - 36f, 28f);
            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 20,
                normal = { textColor = new Color(0.25f, 0.9f, 0.92f) }
            };
            EditorGUI.LabelField(titleRect, "STRUCTURED AIR ROUTE WORKBENCH", titleStyle);
            Rect subtitleRect = new Rect(rect.x + 19f, rect.y + 37f, rect.width - 38f, 18f);
            GUIStyle subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.72f, 0.8f, 0.84f) }
            };
            EditorGUI.LabelField(subtitleRect,
                "Network authoring  •  reservation simulation  •  reproducible build", subtitleStyle);
        }

        private void DrawManagerPicker()
        {
            BeginSection("SCENE CONTEXT");
            simulationManager = (SimulationManager)EditorGUILayout.ObjectField(
                "Simulation", simulationManager, typeof(SimulationManager), true);
            networkManager = (NetworkManager)EditorGUILayout.ObjectField(
                "Air Network", networkManager, typeof(NetworkManager), true);
            vertiportManager = (VertiportManager)EditorGUILayout.ObjectField(
                "Facilities", vertiportManager, typeof(VertiportManager), true);
            if (GUILayout.Button("Refresh from open scene", GUILayout.Height(24f)))
                RefreshManagers();
            EndSection();
        }

        private void DrawOverview()
        {
            BeginSection("SYSTEM MODEL");
            EditorGUILayout.HelpBox(
                "The simulation separates three operating domains: the structured air-route " +
                "network, vertiport approach/holding resources, and a time-window reservation " +
                "layer. Runtime presentation stays read-only while topology changes remain " +
                "explicit, validated editor operations.", MessageType.None);
            EndSection();

            BeginSection("CURRENT DATASET");
            DrawNetworkCounts();
            DrawFacilityCounts();
            if (simulationManager != null)
            {
                EditorGUILayout.LabelField("Demand queue", simulationManager.MissionQueue.Count.ToString());
                EditorGUILayout.LabelField("Aircraft in scene", simulationManager.AircraftObjects.Count.ToString());
            }
            EndSection();

            BeginSection("RECOMMENDED WORKFLOW");
            EditorGUILayout.LabelField("1. Edit axis network and facilities in Scene view.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("2. Generate lanes; add altitude layers and lane connectors.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("3. Validate topology and export authoritative JSON.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("4. Select SimulationManager; author routes in its Inspector and Scene view.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("5. Validate and build the Windows x64 package.", EditorStyles.wordWrappedLabel);
            EndSection();
        }

        private void DrawSimulation()
        {
            BeginSection("DEMAND AND PLAYBACK");
            if (simulationManager == null)
            {
                EditorGUILayout.HelpBox("Add or select a SimulationManager.", MessageType.Warning);
                EndSection();
                return;
            }

            EditorGUILayout.LabelField("State", simulationManager.IsRunning() ? "RUNNING" : "STOPPED");
            EditorGUILayout.LabelField("Queued missions", simulationManager.MissionQueue.Count.ToString());
            EditorGUILayout.LabelField("Active aircraft", simulationManager.AircraftObjects.Count.ToString());

            EditorGUILayout.BeginHorizontal();
            routeFilePath = EditorGUILayout.TextField("Route file", routeFilePath);
            if (GUILayout.Button("Browse…", GUILayout.Width(84f)))
            {
                string picked = EditorUtility.OpenFilePanel("Select JSON or XML route file", "", "");
                if (!string.IsNullOrEmpty(picked)) routeFilePath = picked;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Load & replace queue", GUILayout.Height(28f)))
                LoadRouteFile();
            if (GUILayout.Button("Clear queue", GUILayout.Height(28f)))
            {
                simulationManager.ClearMissionQueue();
                lastMessage = "Mission queue cleared.";
                lastMessageType = MessageType.Info;
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Start", GUILayout.Height(28f))) simulationManager.StartSimulation();
                if (GUILayout.Button("Stop", GUILayout.Height(28f))) simulationManager.StopSimulation();
                EditorGUILayout.EndHorizontal();
                Time.timeScale = EditorGUILayout.Slider("Time scale", Time.timeScale, 0f, 4f);
            }

            if (!EditorApplication.isPlaying)
                EditorGUILayout.HelpBox("Playback controls become active in Play Mode.", MessageType.Info);
            if (GUILayout.Button("Open full SimulationManager inspector"))
                SelectObject(simulationManager);
            EndSection();

            BeginSection("PARAMETERS AND BATCH EXPERIMENTS");
            if (simulationManager.config != null)
            {
                EditorGUILayout.LabelField("Simulation configuration", simulationManager.config.name);
                if (GUILayout.Button("Open complete SimulationConfig inspector", GUILayout.Height(26f)))
                    SelectObject(simulationManager.config);
            }
            else EditorGUILayout.HelpBox("Assign a SimulationConfig before editing parameters.", MessageType.Warning);
            if (parameterSweepRunner != null)
            {
                parameterSweepRunner.ResolveGrid();
                EditorGUILayout.LabelField("Batch grid", $"{parameterSweepRunner.totalBatches} run(s)");
                if (GUILayout.Button("Open batch experiment controls", GUILayout.Height(26f)))
                    SelectObject(parameterSweepRunner);
            }
            else EditorGUILayout.HelpBox("Add a ParameterSweepRunner to run parameter grids.", MessageType.Warning);
            EndSection();
        }

        private void DrawNetwork()
        {
            BeginSection("AIR-ROUTE NETEDIT");
            if (networkManager == null)
            {
                EditorGUILayout.HelpBox("Add or select a NetworkManager.", MessageType.Warning);
                EndSection();
                return;
            }

            DrawNetworkCounts();
            EditorGUILayout.HelpBox(
                "Use the NetworkManager inspector with Scene view for nodes, edges and waypoints. " +
                "Its Layer & Lane Connector section creates lane-to-lane vertical corridors. " +
                "This workbench supplies the safe generate/validate/export lifecycle.", MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open detailed net editor", GUILayout.Height(30f)))
                SelectObject(networkManager);
            if (GUILayout.Button("Validate schema", GUILayout.Height(30f)))
                ValidateNetwork();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Generate full lane network", GUILayout.Height(30f)))
                GenerateNetwork();
            if (GUILayout.Button("Export FullNetwork.json", GUILayout.Height(30f)))
            {
                networkManager.ExportFullNetworkToJSON();
                lastMessage = "Full lane network exported.";
                lastMessageType = MessageType.Info;
            }
            EditorGUILayout.EndHorizontal();
            EndSection();
        }

        private void DrawFacilities()
        {
            BeginSection("VERTIPORT NETEDIT");
            if (vertiportManager == null)
            {
                EditorGUILayout.HelpBox("Add or select a VertiportManager.", MessageType.Warning);
                EndSection();
                return;
            }

            DrawFacilityCounts();
            EditorGUILayout.HelpBox(
                "The detailed inspector edits helipads, holding circles/vertices/ramps, " +
                "approach nodes/edges and route-to-facility corridors. These resources " +
                "participate in atomic reservation scheduling.", MessageType.None);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open facility editor", GUILayout.Height(30f)))
                SelectObject(vertiportManager);
            if (GUILayout.Button("Export VertiportData.json", GUILayout.Height(30f)))
            {
                vertiportManager.ExportToJSON();
                lastMessage = "Vertiport data exported.";
                lastMessageType = MessageType.Info;
            }
            EditorGUILayout.EndHorizontal();
            EndSection();
        }

        private void DrawBuild()
        {
            BeginSection("WINDOWS PACKAGE");
            EditorGUILayout.LabelField("Release output", SarWindowsBuild.ReleaseRootDirectory,
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("Executable", SarWindowsBuild.ExecutableName);
            EditorGUILayout.HelpBox(
                "Build validation is read-only. Packaged route synchronization is a separate, " +
                "visible asset edit so Git can record every input change. One validated player is " +
                "published as separate en-US and zh-CN releases.", MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Validate", GUILayout.Height(30f))) ValidateBuild();
            if (GUILayout.Button("Sync packaged route data", GUILayout.Height(30f)))
            {
                SarWindowsBuild.SyncPackagedRouteData();
                lastMessage = "Packaged route data synchronized; review Git changes before committing.";
                lastMessageType = MessageType.Warning;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Build Windows x64", GUILayout.Height(34f)))
                SarWindowsBuild.BuildWindows64();
            if (GUILayout.Button("Open build folder", GUILayout.Height(34f)))
                SarWindowsBuild.OpenBuildFolder();
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Republish locales from validated Windows build", GUILayout.Height(28f)))
                SarWindowsBuild.PublishBilingualReleasesFromExistingBuild();

            string guide = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "OperationGuide"));
            if (GUILayout.Button("Open operation guides")) EditorUtility.RevealInFinder(guide);
            EndSection();
        }

        private void DrawNetworkCounts()
        {
            Network network = networkManager != null ? networkManager.FullNetwork : null;
            int edgeCount = network?.Edges?.Count ?? 0;
            int laneCount = network?.Edges?.Where(edge => edge != null && edge.Lanes != null)
                .Sum(edge => edge.Lanes.Count) ?? 0;
            EditorGUILayout.LabelField("Network nodes / edges", $"{network?.Nodes?.Count ?? 0} / {edgeCount}");
            EditorGUILayout.LabelField("Lanes / altitude layers", $"{laneCount} / {network?.AltitudeLayers?.Count ?? 0}");
            EditorGUILayout.LabelField("Layer connectors", (network?.LaneConnectionCorridors?.Count ?? 0).ToString());
        }

        private void DrawFacilityCounts()
        {
            List<Vertiport> vertiports = vertiportManager?.Vertiports;
            int helipads = vertiports?.Where(vp => vp != null && vp.Helipads != null)
                .Sum(vp => vp.Helipads.Count) ?? 0;
            int holdingStructures = vertiports?.Where(vp => vp != null && vp.HoldingStructures != null)
                .Sum(vp => vp.HoldingStructures.Count) ?? 0;
            int approachNodes = vertiports?.Where(vp => vp?.ApproachNet?.Nodes != null)
                .Sum(vp => vp.ApproachNet.Nodes.Count) ?? 0;
            EditorGUILayout.LabelField("Vertiports / helipads", $"{vertiports?.Count ?? 0} / {helipads}");
            EditorGUILayout.LabelField("Holding structures / approach nodes", $"{holdingStructures} / {approachNodes}");
        }

        private void LoadRouteFile()
        {
            if (string.IsNullOrWhiteSpace(routeFilePath))
                routeFilePath = simulationManager.GetBundledRouteExamplePath();
            RouteImportResult result = simulationManager.LoadRouteFile(routeFilePath, true);
            lastMessage = result.Success
                ? $"Loaded {result.Missions.Count} mission(s), {result.Warnings.Count} warning(s)."
                : string.Join("\n", result.Errors);
            lastMessageType = result.Success ? MessageType.Info : MessageType.Error;
        }

        private void ValidateNetwork()
        {
            Undo.RecordObject(networkManager, "Normalize and validate air network");
            NetworkValidationResult result = MultilayerNetworkUtility.NormalizeAndValidate(networkManager.FullNetwork);
            EditorUtility.SetDirty(networkManager);
            if (result.IsValid)
            {
                lastMessage = $"Network schema is valid ({result.Warnings.Count} warning(s)).";
                if (result.Warnings.Count > 0) lastMessage += "\n" + string.Join("\n", result.Warnings);
                lastMessageType = result.Warnings.Count > 0 ? MessageType.Warning : MessageType.Info;
            }
            else
            {
                lastMessage = string.Join("\n", result.Errors);
                lastMessageType = MessageType.Error;
            }
        }

        private void GenerateNetwork()
        {
            if (!EditorUtility.DisplayDialog(
                    "Generate full lane network",
                    "Regenerate the full lane network from the current axis network? Existing generated lanes are replaced.",
                    "Generate", "Cancel"))
                return;

            Undo.RecordObject(networkManager, "Generate full lane network");
            networkManager.GenerateFullNetworkFromAxis();
            EditorUtility.SetDirty(networkManager);
            lastMessage = "Full lane network regenerated. Validate it before export.";
            lastMessageType = MessageType.Warning;
        }

        private void ValidateBuild()
        {
            List<string> issues = SarWindowsBuild.CollectValidationIssues();
            lastMessage = issues.Count == 0 ? "Build validation passed." : string.Join("\n", issues);
            lastMessageType = issues.Count == 0 ? MessageType.Info : MessageType.Error;
        }

        private void RefreshManagers()
        {
            if (simulationManager == null) simulationManager = FindObjectOfType<SimulationManager>();
            if (networkManager == null)
                networkManager = simulationManager != null && simulationManager.networkManager != null
                    ? simulationManager.networkManager
                    : FindObjectOfType<NetworkManager>();
            if (vertiportManager == null)
                vertiportManager = simulationManager != null && simulationManager.vertiportManager != null
                    ? simulationManager.vertiportManager
                    : FindObjectOfType<VertiportManager>();
            if (parameterSweepRunner == null) parameterSweepRunner = FindObjectOfType<ParameterSweepRunner>();
            Repaint();
        }

        private static void SelectObject(UnityEngine.Object target)
        {
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        private static void BeginSection(string title)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.Space(3f);
        }

        private static void EndSection()
        {
            EditorGUILayout.Space(3f);
            EditorGUILayout.EndVertical();
        }
    }
}
