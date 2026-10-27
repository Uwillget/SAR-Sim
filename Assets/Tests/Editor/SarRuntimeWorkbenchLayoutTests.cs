using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RoadNetworkEditor.Tests
{
    public class SarRuntimeWorkbenchLayoutTests
    {
        [Test]
        public void DropdownAnchor_KeepsExpandedListFlushWithSourceField()
        {
            var canvasObject = new GameObject("Dropdown Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            var sourceObject = new GameObject("Dropdown Source", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(Dropdown), typeof(RuntimeDropdownAnchor));
            var listObject = new GameObject("Dropdown List", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image));
            try
            {
                Canvas canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                sourceObject.transform.SetParent(canvasObject.transform, false);
                RectTransform source = (RectTransform)sourceObject.transform;
                source.anchorMin = source.anchorMax = new Vector2(0.5f, 0.5f);
                source.sizeDelta = new Vector2(320f, 44f);
                listObject.transform.SetParent(source, false);
                Dropdown dropdown = sourceObject.GetComponent<Dropdown>();
                dropdown.options = Enumerable.Range(1, 12)
                    .Select(index => new Dropdown.OptionData("Option " + index)).ToList();
                RuntimeDropdownAnchor anchor = sourceObject.GetComponent<RuntimeDropdownAnchor>();
                anchor.Configure(dropdown);
                Canvas.ForceUpdateCanvases();

                Assert.That(anchor.AlignExpandedList(), Is.True);
                RectTransform list = (RectTransform)listObject.transform;
                Assert.That(list.anchoredPosition, Is.EqualTo(Vector2.zero));
                Assert.That(list.sizeDelta.y, Is.EqualTo(270f).Within(0.01f));
                Assert.That((list.anchorMin.y == 0f && list.pivot.y == 1f) ||
                            (list.anchorMin.y == 1f && list.pivot.y == 0f), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(canvasObject);
            }
        }

        [Test]
        public void Historical3DTarget_RequiresExactlyOneConcreteRecordedRun()
        {
            var root = new GameObject("Historical 3D Selection Test");
            root.SetActive(false);
            try
            {
                SarRuntimeWorkbench workbench = root.AddComponent<SarRuntimeWorkbench>();
                SarSimulationHistory history = root.AddComponent<SarSimulationHistory>();
                typeof(SarRuntimeWorkbench)
                    .GetField("simulationHistory", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(workbench, history);

                var manifests = (List<SarSimulationRunManifest>)typeof(SarSimulationHistory)
                    .GetField("manifests", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(history);
                var batch = new SarSimulationRunManifest
                {
                    RunId = "batch",
                    Kind = SarSimulationRunKind.Batch,
                    Status = SarSimulationRunStatus.Completed,
                    ChildRunIds = new List<string> { "member-a", "member-b" }
                };
                var memberA = new SarSimulationRunManifest
                {
                    RunId = "member-a",
                    Kind = SarSimulationRunKind.BatchMember,
                    Status = SarSimulationRunStatus.Completed,
                    RecordingFile = "member-a/SimulationRecording.json.gz"
                };
                var memberB = new SarSimulationRunManifest
                {
                    RunId = "member-b",
                    Kind = SarSimulationRunKind.BatchMember,
                    Status = SarSimulationRunStatus.Completed,
                    RecordingFile = "member-b/SimulationRecording.json.gz"
                };
                var memberWithoutRecording = new SarSimulationRunManifest
                {
                    RunId = "member-without-recording",
                    Kind = SarSimulationRunKind.BatchMember,
                    Status = SarSimulationRunStatus.Completed,
                    RecordingFile = string.Empty
                };
                manifests.AddRange(new[] { batch, memberA, memberB, memberWithoutRecording });

                var selectedIds = (HashSet<string>)typeof(SarRuntimeWorkbench)
                    .GetField("selectedHistoryIds", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                MethodInfo resolve = typeof(SarRuntimeWorkbench).GetMethod(
                    "ResolveSelectedConcrete3DRun", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(resolve, Is.Not.Null);

                SarSimulationRunManifest Resolve(params string[] runIds)
                {
                    selectedIds.Clear();
                    foreach (string runId in runIds) selectedIds.Add(runId);
                    return (SarSimulationRunManifest)resolve.Invoke(workbench, null);
                }

                Assert.That(Resolve(batch.RunId), Is.Null,
                    "A batch parent represents a statistical collection, not one 3D state.");
                Assert.That(Resolve(batch.RunId, memberA.RunId), Is.Null,
                    "Selecting a batch parent together with one member must remain ambiguous.");
                Assert.That(Resolve(memberA.RunId, memberB.RunId), Is.Null,
                    "Multiple concrete runs must never be collapsed to the last member.");
                Assert.That(Resolve(memberWithoutRecording.RunId), Is.Null,
                    "A concrete run without an archived recording cannot be loaded in 3D.");
                Assert.That(Resolve(memberA.RunId), Is.SameAs(memberA),
                    "Exactly one completed concrete run with a recording is the only valid 3D target.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void GeneratedInterface_HasVisibleNavigationAndControlledPageHeights()
        {
            EventSystem existingEventSystem = Object.FindObjectOfType<EventSystem>();
            SimulationManager[] sceneManagers = Object.FindObjectsOfType<SimulationManager>();
            NetworkManager[] sceneNetworkManagers = Object.FindObjectsOfType<NetworkManager>();
            VertiportManager[] sceneVertiportManagers = Object.FindObjectsOfType<VertiportManager>();
            string[] managerFingerprints = sceneManagers.Select(SimulationManagerFingerprint).ToArray();
            string[] networkFingerprints = sceneNetworkManagers.Select(NetworkManagerFingerprint).ToArray();
            string[] vertiportFingerprints = sceneVertiportManagers
                .Select(VertiportManagerFingerprint).ToArray();

            var simulationRoot = new GameObject("Runtime UI Layout Test Dependencies");
            simulationRoot.SetActive(false);
            NetworkManager isolatedNetworkManager = simulationRoot.AddComponent<NetworkManager>();
            VertiportManager isolatedVertiportManager = simulationRoot.AddComponent<VertiportManager>();
            SimulationManager isolatedManager = simulationRoot.AddComponent<SimulationManager>();
            ParameterSweepRunner isolatedSweepRunner = simulationRoot.AddComponent<ParameterSweepRunner>();
            isolatedSweepRunner.sim = isolatedManager;
            isolatedNetworkManager.AxisNetwork.AltitudeLayers[0].DisplayName = "Simulation";
            isolatedNetworkManager.FullNetwork.AltitudeLayers[0].DisplayName = "Simulation";
            SimulationConfig temporaryConfig = ScriptableObject.CreateInstance<SimulationConfig>();
            isolatedManager.config = temporaryConfig;
            isolatedManager.networkManager = isolatedNetworkManager;
            isolatedManager.vertiportManager = isolatedVertiportManager;
            // Workbench uses a non-persistent preview workspace in EditMode; the isolated
            // managers ensure every helper/history component also binds to test-owned data.
            var root = new GameObject("Runtime UI Layout Test");
            float originalTimeScale = Time.timeScale;

            try
            {
                SarRuntimeWorkbench workbench = root.AddComponent<SarRuntimeWorkbench>();
                typeof(SarRuntimeWorkbench)
                    .GetField("manager", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(workbench, isolatedManager);
                typeof(SarRuntimeWorkbench)
                    .GetField("sweepRunner", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(workbench, isolatedSweepRunner);
                typeof(SarRuntimeWorkbench)
                    .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(workbench, null);
                Canvas.ForceUpdateCanvases();

                Transform navigation = root.transform.Find("ModuleNavigation");
                Assert.That(navigation, Is.Not.Null);
                Transform visibilityToggle = root.transform.Find("UiVisibilityButton");
                Assert.That(visibilityToggle, Is.Not.Null);
                Assert.That(visibilityToggle.GetComponentInChildren<Text>(true).text, Does.Contain("F10"));
                RectTransform visibilityRect = (RectTransform)visibilityToggle;
                RectTransform settingsRect = (RectTransform)root.transform.Find("TopBar/Header/DisplaySettings");
                RectTransform sceneDataRect =
                    (RectTransform)root.transform.Find("TopBar/SceneTabBar/SceneData");
                Assert.That(settingsRect, Is.Not.Null);
                Assert.That(sceneDataRect, Is.Not.Null);
                Text status = root.transform.Find("TopBar/Header")
                    .GetComponentsInChildren<Text>(true).First(text => text.text.StartsWith("●"));

                const float headerControlHeight = 40f;
                const float visibilityControlWidth = 132f;
                const float settingsControlWidth = 68f;
                const float headerControlGap = 16f;
                const float sceneRowGap = 20f;
                Assert.That(visibilityRect.rect.height,
                    Is.EqualTo(headerControlHeight).Within(0.01f));
                Assert.That(settingsRect.rect.height,
                    Is.EqualTo(headerControlHeight).Within(0.01f));
                Assert.That(visibilityRect.rect.width,
                    Is.EqualTo(visibilityControlWidth).Within(0.01f));
                Assert.That(settingsRect.rect.width,
                    Is.EqualTo(settingsControlWidth).Within(0.01f));
                Assert.That(visibilityRect.offsetMin.y, Is.EqualTo(settingsRect.offsetMin.y).Within(0.01f));
                Assert.That(visibilityRect.offsetMax.y, Is.EqualTo(settingsRect.offsetMax.y).Within(0.01f));
                Assert.That(settingsRect.offsetMin.x - visibilityRect.offsetMax.x,
                    Is.EqualTo(headerControlGap).Within(0.01f));
                Assert.That(visibilityRect.offsetMin.x - status.rectTransform.offsetMax.x,
                    Is.EqualTo(headerControlGap).Within(0.01f),
                    "Status text must keep the designed gap from the UI toggle.");

                var visibilityCorners = new Vector3[4];
                var settingsCorners = new Vector3[4];
                var sceneDataCorners = new Vector3[4];
                visibilityRect.GetWorldCorners(visibilityCorners);
                settingsRect.GetWorldCorners(settingsCorners);
                sceneDataRect.GetWorldCorners(sceneDataCorners);
                Assert.That(visibilityCorners[0].y,
                    Is.EqualTo(settingsCorners[0].y).Within(0.01f),
                    "The UI toggle and Settings button must share the same visual bottom edge.");
                Assert.That(visibilityCorners[1].y,
                    Is.EqualTo(settingsCorners[1].y).Within(0.01f),
                    "The UI toggle and Settings button must share the same visual top edge.");

                float worldUnitsPerDesignPixel =
                    (visibilityCorners[1].y - visibilityCorners[0].y) / headerControlHeight;
                Assert.That(worldUnitsPerDesignPixel, Is.GreaterThan(0f));
                Assert.That(settingsCorners[0].x - visibilityCorners[3].x,
                    Is.EqualTo(headerControlGap * worldUnitsPerDesignPixel).Within(0.05f),
                    "The two header buttons must retain a 16 px visual gap after Canvas scaling.");
                float sceneDataVerticalGap = visibilityCorners[0].y - sceneDataCorners[1].y;
                Assert.That(sceneDataVerticalGap, Is.GreaterThan(0f),
                    "The UI toggle must not overlap the Scene Data button below it.");
                Assert.That(sceneDataVerticalGap,
                    Is.EqualTo(sceneRowGap * worldUnitsPerDesignPixel).Within(0.05f),
                    "The header controls and scene-tab row must retain the designed 20 px gap.");
                foreach (string tab in new[] { "Airspace Structure", "Routes & Flow", "Simulation", "Data Visualization" })
                {
                    RectTransform button = navigation.Find(tab) as RectTransform;
                    Assert.That(button, Is.Not.Null, tab);
                    Assert.That(button.rect.height, Is.GreaterThan(0f), tab);
                }

                Assert.That(navigation.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text == "Scene View"), Is.False);
                Transform viewGizmo = root.transform.Find("ViewOrientationControl");
                Assert.That(viewGizmo, Is.Not.Null);
                Assert.That(viewGizmo.GetComponent<EventTrigger>(), Is.Not.Null);
                Assert.That(viewGizmo.GetComponentInChildren<RuntimeOrientationCube>(true), Is.Not.Null);
                Assert.That(root.transform.Find("ViewOrientationMenu"), Is.Not.Null);
                Assert.That(root.transform.Find("DisplaySettingsOverlay"), Is.Not.Null);
                Assert.That(root.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text.Contains("(3D)")), Is.False);
                foreach (string removed in new[]
                         {
                             "Reset Top View", "Toggle Perspective View", "Apply Layer Name", "Apply Layer Altitude",
                             "Apply Helipad Parameters", "Apply Ring Parameters", "Update Height Threshold"
                         })
                    Assert.That(root.GetComponentsInChildren<Text>(true)
                        .Any(text => text.text == removed), Is.False, removed);
                Assert.That(root.GetComponentsInChildren<Text>(true)
                    .Count(text => text.text == "Focus Selected Vertiport"), Is.EqualTo(1));
                Assert.That(root.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text == "Edit Existing Vertiport"), Is.True);
                Assert.That(root.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text == "Create New Vertiport"), Is.True);
                foreach (string routeControl in new[]
                         {
                             "Focus Origin", "Focus Destination", "Automatic Shortest Path", "Edit Route Manually",
                             "Confirm Manual Route", "Export Missions and Traffic Flow", "Load and Replace Mission List",
                             "Overwrite Input File"
                         })
                    Assert.That(root.GetComponentsInChildren<Text>(true)
                        .Any(text => text.text == routeControl), Is.True, routeControl);
                Assert.That(root.GetComponentsInChildren<Transform>(true)
                    .Any(item => item.name == "PendingMissionList"), Is.True);
                Assert.That(root.GetComponentsInChildren<Transform>(true)
                    .Any(item => item.name == "EditExistingVertiportFlow"), Is.True);
                Assert.That(root.GetComponentsInChildren<Transform>(true)
                    .Any(item => item.name == "CreateVertiportFlow"), Is.True);
                Assert.That(root.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text == "Pause and focus automatically on collision"), Is.True);
                Toggle collisionPause = root.GetComponentsInChildren<Toggle>(true)
                    .FirstOrDefault(toggle => toggle.name == "Toggle");
                Assert.That(collisionPause, Is.Not.Null);
                Assert.That(root.GetComponentsInChildren<InputField>(true)
                    .Any(field => field.placeholder != null &&
                                  field.placeholder.GetComponent<Text>()?.text ==
                                  "Random-fleet aircraft count"), Is.True);
                Assert.That(root.GetComponentsInChildren<InputField>(true)
                    .Any(field => field.placeholder != null &&
                                  field.placeholder.GetComponent<Text>()?.text ==
                                  "Random-demand turnaround time after arrival (s)"), Is.True);
                Assert.That(root.GetComponentsInChildren<InputField>(true)
                    .Any(field => field.placeholder != null &&
                                  field.placeholder.GetComponent<Text>()?.text ==
                                  "Applies only to random-demand mode"), Is.True);
                foreach (string parameter in new[]
                         {
                             "maxSpeed", "safeInterval", "resourceBuffer", "greenSignalTime",
                             "signalPropagationDelay", "positioningErrorStdDev"
                         })
                    Assert.That(root.GetComponentsInChildren<InputField>(true)
                        .Any(field => field.name == "Parameter_" + parameter), Is.True, parameter);

                Transform parameterControls = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "ParameterControls");
                Transform parameterControlsBody = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "ParameterControlsBody");
                Button parameterControlsHeader = root.GetComponentsInChildren<Button>(true)
                    .Single(button => button.name == "ParameterControlsHeader");
                Assert.That(parameterControlsBody.IsChildOf(parameterControls), Is.True);
                Assert.That(parameterControlsBody.gameObject.activeSelf, Is.False,
                    "Parameter controls should be folded by default.");
                foreach (string groupName in new[]
                         {
                             "AircraftParameterGroup", "ReservationParameterGroup",
                             "TrafficControlParameterGroup", "InformationUncertaintyParameterGroup"
                         })
                {
                    Transform group = root.GetComponentsInChildren<Transform>(true)
                        .Single(item => item.name == groupName);
                    Assert.That(group.IsChildOf(parameterControlsBody), Is.True, groupName);
                    Assert.That(group.Find(groupName + "Body").gameObject.activeSelf, Is.False,
                        groupName + " should be folded by default.");
                }
                parameterControlsHeader.onClick.Invoke();
                Assert.That(parameterControlsBody.gameObject.activeSelf, Is.True);
                Button aircraftGroupHeader = root.GetComponentsInChildren<Button>(true)
                    .Single(button => button.name == "AircraftParameterGroupHeader");
                aircraftGroupHeader.onClick.Invoke();
                Assert.That(parameterControlsBody.Find("AircraftParameterGroup/AircraftParameterGroupBody")
                    .gameObject.activeSelf, Is.True);

                Toggle batchMode = root.GetComponentsInChildren<Toggle>(true)
                    .Single(toggle => toggle.GetComponentInChildren<Text>(true)?.text ==
                                      "Set up batch experiment");
                Transform batchSettings = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "BatchExperimentSettings");
                Transform batchControls = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "BatchExperimentControls");
                Transform singleControls = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "SingleSimulationControls");
                Transform batchVariables = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "BatchExperimentVariables");
                Assert.That(batchMode.isOn, Is.False);
                Assert.That(batchSettings.gameObject.activeSelf, Is.False,
                    "Batch inputs must be hidden in single-run mode.");
                Assert.That(batchControls.gameObject.activeSelf, Is.False,
                    "Batch start/stop must be hidden in single-run mode.");
                Assert.That(singleControls.gameObject.activeSelf, Is.True,
                    "Single-run start/stop must be visible in single-run mode.");
                Assert.That(batchVariables.IsChildOf(batchSettings), Is.True);
                batchMode.isOn = true;
                Assert.That(batchSettings.gameObject.activeSelf, Is.True);
                Assert.That(batchControls.gameObject.activeSelf, Is.True);
                Assert.That(singleControls.gameObject.activeSelf, Is.False,
                    "Single-run start/stop must be hidden in batch mode.");
                Assert.That(batchControls.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text == "Start Batch Experiment"), Is.True);
                Assert.That(batchSettings.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text == "Add Factor"), Is.True);
                batchMode.isOn = false;
                Assert.That(batchSettings.gameObject.activeSelf, Is.False);
                Assert.That(batchControls.gameObject.activeSelf, Is.False);
                Assert.That(singleControls.gameObject.activeSelf, Is.True);
                Transform routeFilePanel = root.GetComponentsInChildren<Transform>(true)
                    .First(item => item.name == "FileDemandParameters");
                Transform randomDemandPanel = root.GetComponentsInChildren<Transform>(true)
                    .First(item => item.name == "RandomDemandParameters");
                Button jsonMode = root.GetComponentsInChildren<Button>(true)
                    .First(button => button.GetComponentInChildren<Text>(true)?.text == "JSON / XML Routes");
                Button randomMode = root.GetComponentsInChildren<Button>(true)
                    .First(button => button.GetComponentInChildren<Text>(true)?.text == "Continuous Random Demand");
                jsonMode.onClick.Invoke();
                Assert.That(routeFilePanel.gameObject.activeSelf, Is.True);
                Assert.That(randomDemandPanel.gameObject.activeSelf, Is.False);
                randomMode.onClick.Invoke();
                Assert.That(routeFilePanel.gameObject.activeSelf, Is.False);
                Assert.That(randomDemandPanel.gameObject.activeSelf, Is.True);

                Assert.That(root.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text.StartsWith("Select Completed Trajectories (")), Is.True);
                Assert.That(root.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text.StartsWith("Select output metrics (")), Is.True);
                Assert.That(root.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text.StartsWith("Select input parameters (")), Is.True);
                Transform trafficLayerList = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "TrafficHeatmapAltitudeLayerList");
                Assert.That(trafficLayerList.GetComponentsInChildren<Toggle>(true).Length,
                    Is.GreaterThanOrEqualTo(1),
                    "Traffic heatmaps need an explicit per-altitude-layer visibility control.");
                Assert.That(root.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text == "Generate / Refresh 2D Traffic Heatmap"), Is.True);
                Button showAllTrafficLayers = (Button)typeof(SarRuntimeWorkbench)
                    .GetField("historyTrafficShowAllLayersButton",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                Assert.That(showAllTrafficLayers, Is.Not.Null,
                    "The Show All action must be retained so the busy interlock can disable it.");
                RuntimeAirspaceVisualizer trafficVisualizer =
                    (RuntimeAirspaceVisualizer)typeof(SarRuntimeWorkbench)
                    .GetField("visualizer", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                AltitudeLayer trafficLayer = trafficVisualizer.GetHeatmapAltitudeLayers().First();
                trafficVisualizer.SetHeatmapLayerVisible(trafficLayer.LayerId, false);
                typeof(SarRuntimeWorkbench)
                    .GetField("historyAnalysisBusy", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(workbench, true);
                typeof(SarRuntimeWorkbench)
                    .GetMethod("RefreshHistoryAnalysisAvailability",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(workbench, null);
                Assert.That(showAllTrafficLayers.interactable, Is.False);
                Assert.That(trafficLayerList.GetComponentsInChildren<Toggle>(true)
                    .All(toggle => !toggle.interactable), Is.True);
                typeof(SarRuntimeWorkbench)
                    .GetMethod("ResetTrafficHeatmapLayerVisibility",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(workbench, null);
                Assert.That(trafficVisualizer.IsHeatmapLayerVisible(trafficLayer.LayerId), Is.False,
                    "The method-level guard must reject a same-frame Show All race while plotting.");
                typeof(SarRuntimeWorkbench)
                    .GetField("historyAnalysisBusy", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(workbench, false);
                typeof(SarRuntimeWorkbench)
                    .GetMethod("ResetTrafficHeatmapLayerVisibility",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(workbench, null);
                Assert.That(trafficVisualizer.IsHeatmapLayerVisible(trafficLayer.LayerId), Is.True);

                SarSimulationHistory history = (SarSimulationHistory)typeof(SarRuntimeWorkbench)
                    .GetField("simulationHistory", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                var historyManifests = (List<SarSimulationRunManifest>)typeof(SarSimulationHistory)
                    .GetField("manifests", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(history);
                var cancelledEmptyBatch = new SarSimulationRunManifest
                {
                    RunId = "cancelled-empty-batch",
                    SceneId = "editor-layout-preview",
                    SceneName = "Editor Layout Preview",
                    Kind = SarSimulationRunKind.Batch,
                    Status = SarSimulationRunStatus.Cancelled,
                    BatchCount = 25,
                    StartedUtc = "2026-08-27T12:00:00.0000000Z",
                    BatchSummaryFile = string.Empty,
                    ChildRunIds = new List<string>()
                };
                historyManifests.Add(cancelledEmptyBatch);
                typeof(SarRuntimeWorkbench)
                    .GetMethod("RefreshHistoryList", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(workbench, new object[] { true });
                Transform cancelledBatchGroup = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "Batch_cancelled-empty-batch");
                Button cancelledBatchExpand = cancelledBatchGroup.GetComponentsInChildren<Button>(true)
                    .Single(button => button.GetComponentInChildren<Text>(true)?.text == "—");
                Toggle cancelledBatchSelection = cancelledBatchGroup.GetComponentInChildren<Toggle>(true);
                Assert.That(cancelledBatchExpand.interactable, Is.False,
                    "A cancelled batch without result artifacts must not expand.");
                Assert.That(cancelledBatchSelection.interactable, Is.False,
                    "A cancelled batch without result artifacts must not be selectable.");
                Assert.That(cancelledBatchGroup.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text.Contains(
                        "No result artifacts — cancelled before any member run was archived.")), Is.True);
                cancelledBatchExpand.onClick.Invoke();
                var expandedBatchIds = (HashSet<string>)typeof(SarRuntimeWorkbench)
                    .GetField("expandedHistoryBatchIds", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                var selectedHistoryIds = (HashSet<string>)typeof(SarRuntimeWorkbench)
                    .GetField("selectedHistoryIds", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                Assert.That(expandedBatchIds.Contains(cancelledEmptyBatch.RunId), Is.False);
                cancelledBatchSelection.onValueChanged.Invoke(true);
                Assert.That(selectedHistoryIds.Contains(cancelledEmptyBatch.RunId), Is.False);

                RectTransform analysisSurface = root.transform.Find("HistoryAnalysisSurface") as RectTransform;
                Assert.That(analysisSurface, Is.Not.Null);
                Assert.That(analysisSurface.offsetMin.x, Is.EqualTo(238f).Within(0.01f));
                Assert.That(analysisSurface.offsetMin.y, Is.EqualTo(34f).Within(0.01f));
                Assert.That(analysisSurface.offsetMax.x, Is.EqualTo(-438f).Within(0.01f));
                Assert.That(analysisSurface.offsetMax.y, Is.EqualTo(-118f).Within(0.01f));
                ScrollRect resultTableScroll = analysisSurface.Find("MetricsTableMode")
                    .GetComponent<ScrollRect>();
                Assert.That(resultTableScroll.horizontal, Is.True);
                Assert.That(resultTableScroll.vertical, Is.True);
                Assert.That(analysisSurface.Find("MetricsTableMode/TableViewerToolbar"), Is.Not.Null);
                Assert.That(analysisSurface.Find("HistoryChartMode/ChartViewerToolbar"), Is.Not.Null);
                Assert.That(analysisSurface.GetComponentsInChildren<SarZoomableViewport>(true).Length,
                    Is.EqualTo(2));
                Text chartCaption = (Text)typeof(SarRuntimeWorkbench)
                    .GetField("historyChartCaption", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                RectTransform chartViewport = analysisSurface.Find(
                    "HistoryChartMode/PythonChartViewport") as RectTransform;
                Assert.That(chartCaption, Is.Not.Null);
                Assert.That(chartViewport, Is.Not.Null);
                Assert.That(chartCaption.rectTransform.anchorMin.y, Is.EqualTo(1f));
                Assert.That(chartCaption.rectTransform.anchorMax.y, Is.EqualTo(1f));
                Assert.That(chartViewport.offsetMax.y,
                    Is.LessThan(chartCaption.rectTransform.offsetMin.y),
                    "The generated PNG viewport must start below the fixed run/dimension caption band.");
                foreach (string viewerControl in new[]
                         { "Previous Figure", "Figure 1/1", "Next Figure",
                             "Zoom Out", "Zoom In", "Reset View", "Export Image" })
                    Assert.That(analysisSurface.GetComponentsInChildren<Text>(true)
                        .Any(text => text.text == viewerControl), Is.True, viewerControl);
                MethodInfo clearChartPages = typeof(SarRuntimeWorkbench)
                    .GetMethod("ClearHistoryChartPages", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(clearChartPages, Is.Not.Null);
                Assert.DoesNotThrow(() => clearChartPages.Invoke(workbench, null));
                Assert.That((string)typeof(SarRuntimeWorkbench)
                    .GetField("historyChartSourcePath", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench), Is.Null);
                Assert.That((int)typeof(SarRuntimeWorkbench)
                    .GetField("historyChartPageIndex", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench), Is.Zero);
                foreach (string pageControl in new[] { "Previous Page", "Next Page", "Go" })
                    Assert.That(analysisSurface.Find("MetricsTableMode")
                        .GetComponentsInChildren<Text>(true)
                        .Any(text => text.text == pageControl), Is.True, pageControl);
                Assert.That(analysisSurface.GetComponent<RectMask2D>(), Is.Not.Null);
                Assert.That(analysisSurface.GetComponentsInChildren<RawImage>(true).Length,
                    Is.EqualTo(1), "Python figures should be displayed as one generated PNG, not UI-drawn chart pieces.");
                RectTransform heatLegend = root.transform.Find("HistoricalHeatmapLegend") as RectTransform;
                Assert.That(heatLegend, Is.Not.Null);
                Assert.That(heatLegend.rect.width, Is.EqualTo(360f).Within(0.01f));
                Assert.That(heatLegend.rect.height, Is.EqualTo(100f).Within(0.01f));
                Transform heatGradient = heatLegend.Find("HeatmapGradient");
                Assert.That(heatGradient, Is.Not.Null);
                Image[] heatSegments = heatGradient.Cast<Transform>()
                    .Select(item => item.GetComponent<Image>()).Where(item => item != null).ToArray();
                Assert.That(heatSegments.Length, Is.EqualTo(48));
                Assert.That(heatSegments[0].color.r, Is.EqualTo(0.43f).Within(0.01f),
                    "The legend's zero-load swatch must match the gray zero-traffic network.");
                Assert.That(heatSegments[1].color.g, Is.GreaterThan(heatSegments[1].color.r),
                    "Positive traffic should begin at the green end of the heat scale.");
                Text laneLegend = (Text)typeof(SarRuntimeWorkbench)
                    .GetField("historicalLaneLegendText", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                Text resourceLegend = (Text)typeof(SarRuntimeWorkbench)
                    .GetField("historicalResourceLegendText", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                Assert.That(resourceLegend.fontSize, Is.EqualTo(laneLegend.fontSize));
                Assert.That(resourceLegend.fontStyle, Is.EqualTo(laneLegend.fontStyle));
                Transform laneModePanel = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "RuntimeLaneColorMode");
                string[] laneModeLabels = laneModePanel.GetComponentsInChildren<Text>(true)
                    .Select(text => text.text).ToArray();
                Assert.That(laneModeLabels, Does.Contain("Lane-type colors (no live recoloring)"));
                Assert.That(laneModeLabels, Does.Contain("Live aircraft load (gray → green → red)"));
                Assert.That(laneModeLabels, Does.Contain("Lane passability (green = open, red = blocked)"));
                ToggleGroup laneModeGroup = laneModePanel.GetComponent<ToggleGroup>();
                Toggle[] laneModeToggles = laneModePanel.GetComponentsInChildren<Toggle>(true);
                Assert.That(laneModeGroup, Is.Not.Null);
                Assert.That(laneModeGroup.allowSwitchOff, Is.False);
                Assert.That(laneModeToggles.Length, Is.EqualTo(3));
                Assert.That(laneModeToggles.All(toggle => toggle.group == laneModeGroup), Is.True);
                Assert.That(laneModeToggles.Count(toggle => toggle.isOn), Is.EqualTo(1));
                Transform routeLayerList = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "RouteAltitudeLayerVisibility");
                Transform simulationLayerList = root.GetComponentsInChildren<Transform>(true)
                    .Single(item => item.name == "SimulationAltitudeLayerVisibility");
                Assert.That(routeLayerList.GetComponentsInChildren<Toggle>(true), Is.Not.Empty);
                Assert.That(simulationLayerList.GetComponentsInChildren<Toggle>(true), Is.Not.Empty);
                Assert.That(routeLayerList.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text.StartsWith("Simulation ·")), Is.True,
                    "A user-authored layer name must be rendered verbatim, even when it is also a UI term.");
                Transform recycleConfirmation = root.transform.Find("HistoryRecycleConfirmationOverlay");
                Assert.That(recycleConfirmation, Is.Not.Null);
                Assert.That(recycleConfirmation.gameObject.activeSelf, Is.False);
                Assert.That(recycleConfirmation.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text == "Move to Recycle Bin"), Is.True);
                Button recycleHistoryButton = (Button)typeof(SarRuntimeWorkbench)
                    .GetField("historyDeleteButton", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                Assert.That(recycleHistoryButton, Is.Not.Null);
                Assert.That(recycleHistoryButton.interactable, Is.False,
                    "History recycling requires one explicit non-member selection.");

                MethodInfo showPage = typeof(SarRuntimeWorkbench)
                    .GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic);
                RuntimeAirspaceEditor airspaceEditor = (RuntimeAirspaceEditor)typeof(SarRuntimeWorkbench)
                    .GetField("editor", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                Assert.That(airspaceEditor.SetLayerVisible("L0", false), Is.True);
                Assert.That(trafficVisualizer.IsPerLayerVisible("L0"), Is.False);
                Assert.That(isolatedNetworkManager.AxisNetwork.AltitudeLayers[0].Enabled, Is.True,
                    "The editor eye must not persist its temporary view choice.");
                showPage.Invoke(workbench, new object[] { 2 });
                Assert.That(trafficVisualizer.IsPerLayerVisible("L0"), Is.True,
                    "Simulation defaults to all layers visible instead of inheriting Edit-page state.");
                MethodInfo setPageLayerVisible = typeof(SarRuntimeWorkbench)
                    .GetMethod("SetPageAltitudeLayerVisible", BindingFlags.Instance | BindingFlags.NonPublic);
                setPageLayerVisible.Invoke(workbench, new object[] { 2, "L0", false });
                showPage.Invoke(workbench, new object[] { 1 });
                Assert.That(trafficVisualizer.IsPerLayerVisible("L0"), Is.True,
                    "Routes keeps an independent layer view.");
                showPage.Invoke(workbench, new object[] { 2 });
                Assert.That(trafficVisualizer.IsPerLayerVisible("L0"), Is.False,
                    "Returning to Simulation restores that page's session-local choice.");
                showPage.Invoke(workbench, new object[] { 0 });
                Assert.That(trafficVisualizer.IsPerLayerVisible("L0"), Is.False,
                    "Returning to Edit restores its separate session-local choice.");

                AltitudeLayer deletedLayer = airspaceEditor.AddLayer();
                string reusedLayerId = deletedLayer.LayerId;
                Assert.That(airspaceEditor.SetLayerVisible(reusedLayerId, false), Is.True);
                foreach (int pageIndex in new[] { 1, 2, 3 })
                    setPageLayerVisible.Invoke(workbench,
                        new object[] { pageIndex, reusedLayerId, false });
                Assert.That(airspaceEditor.DeleteActiveLayer(), Is.True);
                AltitudeLayer replacementLayer = airspaceEditor.AddLayer();
                Assert.That(replacementLayer.LayerId, Is.EqualTo(reusedLayerId),
                    "The regression requires AddAxisLayer to reuse the lowest free L# identifier.");
                Assert.That(replacementLayer.Enabled, Is.True,
                    "Temporary eyes must never mutate the authored visibility flag.");
                Assert.That(airspaceEditor.RenameActiveLayer("Reviewer Observation Level"), Is.True);
                Assert.That(trafficLayerList.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text.StartsWith("Reviewer Observation Level ·")), Is.True,
                    "The live analytics checklist must refresh a renamed layer with its authored name.");
                foreach (int pageIndex in new[] { 0, 1, 2, 3 })
                {
                    showPage.Invoke(workbench, new object[] { pageIndex });
                    Assert.That(trafficVisualizer.IsPerLayerVisible(reusedLayerId), Is.True,
                        $"A new layer reusing a deleted ID must default visible on page {pageIndex}.");
                    if (pageIndex == 3)
                        Assert.That(trafficVisualizer.IsHeatmapLayerVisible(reusedLayerId), Is.True,
                            "A reused ID must not inherit the deleted layer's analytics filter.");
                }

                setPageLayerVisible.Invoke(workbench, new object[] { 3, reusedLayerId, false });
                Assert.That(trafficVisualizer.IsPerLayerVisible(reusedLayerId), Is.False);
                Assert.That(trafficVisualizer.IsHeatmapLayerVisible(reusedLayerId), Is.False);
                showPage.Invoke(workbench, new object[] { 1 });
                Assert.That(trafficVisualizer.IsPerLayerVisible(reusedLayerId), Is.True,
                    "Routes must not inherit Data Visualization's hidden-layer choice.");
                showPage.Invoke(workbench, new object[] { 3 });
                Assert.That(trafficVisualizer.IsPerLayerVisible(reusedLayerId), Is.False,
                    "Returning to Data Visualization must restore that page's own display mask.");
                Assert.That(trafficVisualizer.IsHeatmapLayerVisible(reusedLayerId), Is.False,
                    "Returning to Data Visualization must restore its independent heatmap filter.");
                setPageLayerVisible.Invoke(workbench, new object[] { 3, reusedLayerId, true });

                const string authoredName = "Reviewer Observation Level";
                SarRuntimeLocalization runtimeLocalization = (SarRuntimeLocalization)typeof(SarRuntimeWorkbench)
                    .GetField("localization", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                var adversarialChineseCatalog = new SarRuntimeLocalizationCatalog
                {
                    Locale = SarRuntimeLocalization.ChineseLocale
                };
                adversarialChineseCatalog.Exact[authoredName] = "错误的硬编码层名";
                adversarialChineseCatalog.Phrases[authoredName] = "错误的硬编码层名";
                runtimeLocalization.ConfigureForTests(SarRuntimeLocalization.ChineseLocale,
                    adversarialChineseCatalog);
                typeof(SarRuntimeWorkbench).GetMethod("RefreshLayerDropdown",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(workbench, null);
                MethodInfo refreshPageLayers = typeof(SarRuntimeWorkbench).GetMethod(
                    "RefreshPageAltitudeLayerControls", BindingFlags.Instance | BindingFlags.NonPublic);
                refreshPageLayers.Invoke(workbench, new object[] { 1, true });
                refreshPageLayers.Invoke(workbench, new object[] { 2, true });
                typeof(SarRuntimeWorkbench).GetMethod("RefreshTrafficHeatmapLayerControls",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(workbench, new object[] { true });
                foreach (string containerName in new[]
                         {
                             "NetworkLayerStack", "RouteAltitudeLayerVisibility",
                             "SimulationAltitudeLayerVisibility", "TrafficHeatmapAltitudeLayerList"
                         })
                {
                    Transform layerContainer = root.GetComponentsInChildren<Transform>(true)
                        .Single(item => item.name == containerName);
                    Assert.That(layerContainer.GetComponentsInChildren<Text>(true)
                        .Any(text => text.text.Contains(authoredName)), Is.True,
                        $"{containerName} must show the authored DisplayName verbatim in Chinese mode.");
                    Assert.That(layerContainer.GetComponentsInChildren<Text>(true)
                        .Any(text => text.text.Contains("错误的硬编码层名")), Is.False,
                        $"{containerName} must never localize user-authored layer data.");
                }
                runtimeLocalization.ConfigureForTests(SarRuntimeLocalization.DefaultLocale, null);
                showPage.Invoke(workbench, new object[] { 0 });
                MethodInfo beginHistory = typeof(SarRuntimeWorkbench)
                    .GetMethod("BeginHistoryAnalysisContext", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo isHistoryCurrent = typeof(SarRuntimeWorkbench)
                    .GetMethod("IsHistoryAnalysisContextCurrent", BindingFlags.Instance | BindingFlags.NonPublic);
                showPage.Invoke(workbench, new object[] { 3 });
                int pendingGeneration = (int)beginHistory.Invoke(workbench, null);
                showPage.Invoke(workbench, new object[] { 0 });
                Assert.That((bool)isHistoryCurrent.Invoke(workbench,
                    new object[] { pendingGeneration, history.CurrentSceneId }), Is.False,
                    "Leaving Data Visualization must invalidate any late background result.");
                Assert.That((bool)typeof(SarRuntimeWorkbench)
                    .GetField("historyAnalysisBusy", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench), Is.False);
                Assert.That(root.GetComponentsInChildren<Text>(true)
                    .Count(text => text.text == "● City Buildings"), Is.EqualTo(1),
                    "Common visualization layers should have one synchronized global control.");
                Assert.That(navigation.GetComponentsInChildren<Transform>(true)
                    .Any(item => item.name == "Layer_City Buildings"), Is.True);

                Transform facilityCorridorFlow = root.GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(item => item.name == "VertiportCorridorFlow");
                Transform layerCorridorFlow = root.GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(item => item.name == "InterlayerCorridorFlow");
                Assert.That(facilityCorridorFlow, Is.Not.Null);
                Assert.That(layerCorridorFlow, Is.Not.Null);
                Assert.That(facilityCorridorFlow.gameObject.activeSelf, Is.True);
                Assert.That(layerCorridorFlow.gameObject.activeSelf, Is.False);
                Assert.That(facilityCorridorFlow.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text == "Arrival Corridor"), Is.True);
                Assert.That(layerCorridorFlow.GetComponentsInChildren<Text>(true)
                    .Any(text => text.text == "Active Vertiport" || text.text == "Arrival Corridor" ||
                                 text.text == "Departure Corridor" ||
                                 (text.text.Contains("Focus") && text.text.Contains("Vertiport"))), Is.False);

                Button layerCorridorMode = root.GetComponentsInChildren<Button>(true)
                    .First(button => button.GetComponentInChildren<Text>(true)?.text == "Interlayer Corridors");
                layerCorridorMode.onClick.Invoke();
                Assert.That(facilityCorridorFlow.gameObject.activeSelf, Is.False);
                Assert.That(layerCorridorFlow.gameObject.activeSelf, Is.True);
                Assert.That(root.GetComponent<RuntimeAirspaceEditor>().CorridorEditMode,
                    Is.EqualTo(RuntimeCorridorEditMode.Layer));

                Transform editPage = root.transform.Find("FunctionDrawer/AirspaceStructure");
                Assert.That(editPage, Is.Not.Null);
                Assert.That(editPage.GetComponent<ScrollRect>(), Is.Not.Null);
                Transform content = editPage.Find("Viewport/Content");
                Assert.That(content, Is.Not.Null);
                VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
                Assert.That(layout, Is.Not.Null);
                Assert.That(layout.childControlHeight, Is.True);

                Dropdown[] dropdowns = root.GetComponentsInChildren<Dropdown>(true);
                Assert.That(dropdowns.Length, Is.GreaterThan(0));
                foreach (Dropdown dropdown in dropdowns)
                {
                    Assert.That(dropdown.GetComponent<RuntimeDropdownAnchor>(), Is.Not.Null);
                    Assert.That(dropdown.template.GetComponent<CanvasGroup>(), Is.Not.Null);
                    ScrollRect dropdownScroll = dropdown.template.GetComponent<ScrollRect>();
                    Assert.That(dropdownScroll, Is.Not.Null);
                    Assert.That(dropdownScroll.vertical, Is.True);
                    Assert.That(dropdownScroll.verticalScrollbar, Is.Not.Null);
                    Assert.That(dropdownScroll.content.rect.height, Is.GreaterThan(0f));
                }

                SimulationManager manager = (SimulationManager)typeof(SarRuntimeWorkbench)
                    .GetField("manager", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                const string dynamicallyLoadedPad = "ZZ-SCENE-SWITCH-PAD";
                manager.Helipads[dynamicallyLoadedPad] = new Helipad { HelipadId = dynamicallyLoadedPad };
                try
                {
                    typeof(SarRuntimeWorkbench)
                        .GetMethod("RefreshVertiportDropdowns", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(workbench, null);
                    Dropdown origin = (Dropdown)typeof(SarRuntimeWorkbench)
                        .GetField("originDropdown", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(workbench);
                    Dropdown destination = (Dropdown)typeof(SarRuntimeWorkbench)
                        .GetField("destinationDropdown", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(workbench);
                    Assert.That(origin.options.Any(option => option.text == dynamicallyLoadedPad), Is.True,
                        "A scene switch must repopulate the origin endpoint list.");
                    Assert.That(destination.options.Any(option => option.text == dynamicallyLoadedPad), Is.True,
                        "A scene switch must repopulate the destination endpoint list.");
                }
                finally
                {
                    manager.Helipads.Remove(dynamicallyLoadedPad);
                }

                Slider timeScaleSlider = root.GetComponentsInChildren<Slider>(true)
                    .Single(slider => slider.name == "TimeScale");
                timeScaleSlider.value = 3f;
                Assert.That(Time.timeScale, Is.EqualTo(3f).Within(0.001f));
                Button resetTimeScale = root.GetComponentsInChildren<Button>(true)
                    .First(button => button.GetComponentInChildren<Text>(true)?.text == "Reset to 1×");
                resetTimeScale.onClick.Invoke();
                Assert.That(Time.timeScale, Is.EqualTo(1f).Within(0.001f));
                Assert.That(timeScaleSlider.value, Is.EqualTo(1f).Within(0.001f));

                RuntimeAirspaceVisualizer visualizer = (RuntimeAirspaceVisualizer)typeof(SarRuntimeWorkbench)
                    .GetField("visualizer", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(workbench);
                bool cityVisible = visualizer.IsVisible(AirspaceVisualLayer.City);
                Image gizmoBackground = viewGizmo.GetComponent<Image>();
                float originalAlpha = gizmoBackground.color.a;
                typeof(SarRuntimeWorkbench)
                    .GetMethod("ToggleViewGizmoBackground", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(workbench, null);
                Assert.That(gizmoBackground.color.a, Is.LessThan(originalAlpha));
                Assert.That(visualizer.IsVisible(AirspaceVisualLayer.City), Is.EqualTo(cityVisible));

                workbench.SetWorkbenchVisible(false);
                Assert.That(workbench.IsWorkbenchVisible, Is.False);
                Assert.That(navigation.gameObject.activeSelf, Is.False);
                Assert.That(visibilityToggle.gameObject.activeSelf, Is.True);
                Assert.That(visibilityToggle.GetComponentInChildren<Text>(true).text, Does.Contain("Show UI"));
                workbench.SetWorkbenchVisible(true);
                Assert.That(workbench.IsWorkbenchVisible, Is.True);
                Assert.That(navigation.gameObject.activeSelf, Is.True);
                Assert.That(root.transform.Find("DisplaySettingsOverlay").gameObject.activeSelf, Is.False);
                Assert.That(root.transform.Find("ViewOrientationMenu").gameObject.activeSelf, Is.False);
            }
            finally
            {
                Time.timeScale = originalTimeScale;
                Object.DestroyImmediate(root);
                try
                {
                    for (int index = 0; index < sceneManagers.Length; index++)
                        Assert.That(SimulationManagerFingerprint(sceneManagers[index]),
                            Is.EqualTo(managerFingerprints[index]),
                            "The layout test must not mutate the scene SimulationManager.");
                    for (int index = 0; index < sceneNetworkManagers.Length; index++)
                        Assert.That(NetworkManagerFingerprint(sceneNetworkManagers[index]),
                            Is.EqualTo(networkFingerprints[index]),
                            "The layout test must not mutate the scene NetworkManager.");
                    for (int index = 0; index < sceneVertiportManagers.Length; index++)
                        Assert.That(VertiportManagerFingerprint(sceneVertiportManagers[index]),
                            Is.EqualTo(vertiportFingerprints[index]),
                            "The layout test must not mutate the scene VertiportManager.");
                }
                finally
                {
                    Object.DestroyImmediate(simulationRoot);
                    Object.DestroyImmediate(temporaryConfig);
                    if (existingEventSystem == null)
                    {
                        EventSystem leftover = Object.FindObjectOfType<EventSystem>();
                        if (leftover != null) Object.DestroyImmediate(leftover.gameObject);
                    }
                }
            }
        }

        private static string SimulationManagerFingerprint(SimulationManager manager)
        {
            if (manager == null) return "<null>";
            return string.Join("|", new[]
            {
                SortedKeys(manager.Nodes), SortedKeys(manager.Edges), SortedKeys(manager.Lanes),
                SortedKeys(manager.LaneConnectionCorridors), SortedKeys(manager.Corridors),
                SortedKeys(manager.Vertiports), SortedKeys(manager.Helipads)
            });
        }

        private static string NetworkManagerFingerprint(NetworkManager manager)
        {
            if (manager == null) return "<null>";
            Network axis = manager.AxisNetwork;
            Network full = manager.FullNetwork;
            return string.Join("|", new[]
            {
                SortedIds(axis?.Nodes?.Select(item => item?.NodeId)),
                SortedIds(axis?.Edges?.Select(item => item?.EdgeId)),
                SortedIds(axis?.AltitudeLayers?.Select(item => item?.LayerId)),
                SortedIds(axis?.LaneConnectionCorridors?.Select(item => item?.CorridorId)),
                SortedIds(full?.Nodes?.Select(item => item?.NodeId)),
                SortedIds(full?.Edges?.Select(item => item?.EdgeId)),
                SortedIds(full?.AltitudeLayers?.Select(item => item?.LayerId)),
                SortedIds(full?.LaneConnectionCorridors?.Select(item => item?.CorridorId))
            });
        }

        private static string VertiportManagerFingerprint(VertiportManager manager)
        {
            if (manager == null) return "<null>";
            IEnumerable<Vertiport> vertiports = manager.Vertiports ?? new List<Vertiport>();
            return string.Join("|", new[]
            {
                SortedIds(vertiports.Select(item => item?.VertiportId)),
                SortedIds(vertiports.SelectMany(item => item?.Corridors ?? new List<NetConnectedCorridor>())
                    .Select(item => item?.CorridorId)),
                SortedIds(vertiports
                    .SelectMany(item => item?.HoldingStructures ?? new List<HoldingStructure>())
                    .SelectMany(item => item?.Ramps ?? new List<Ramp>())
                    .SelectMany(item => item?.LinkCorridors ?? new List<(string, Vector3)>())
                    .Select(item => item.Item1))
            });
        }

        private static string SortedKeys<T>(IDictionary<string, T> values) =>
            values == null ? "<null>" : string.Join(",", values.Keys.OrderBy(value => value));

        private static string SortedIds(IEnumerable<string> values) =>
            values == null
                ? "<null>"
                : string.Join(",", values.Where(value => !string.IsNullOrEmpty(value)).OrderBy(value => value));
    }
}
