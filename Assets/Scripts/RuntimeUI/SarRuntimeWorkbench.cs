using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RoadNetworkEditor.RuntimeUI
{
    [DefaultExecutionOrder(1000)]
    public sealed class SarRuntimeWorkbench : MonoBehaviour
    {
#if UNITY_EDITOR
        private static string EditorUiPreferenceKey =>
            "RoadNetworkEditor.SarRuntimeWorkbench.EditorPlayModeUiEnabled.v2." +
            Application.dataPath.Replace('\\', '/').ToLowerInvariant();

        /// <summary>
        /// Editor-only preference. The packaged Windows application always creates
        /// the runtime workbench; this switch controls only Unity Play Mode.
        /// </summary>
        public static bool EditorPlayModeUiEnabled
        {
            get => UnityEditor.EditorPrefs.GetBool(EditorUiPreferenceKey, false);
            set => UnityEditor.EditorPrefs.SetBool(EditorUiPreferenceKey, value);
        }
#endif

        private enum GroundFacilityEditFlow
        {
            ModifyExisting,
            CreateNew
        }

        private sealed class HistoryTablePage
        {
            public SarSimulationRunManifest Run;
            public SarCsvTable Table;
            public int ColumnStart;
            public int SourcePageNumber;
            public int SourcePageCount;
        }

        private sealed class HistoryTableSection
        {
            public string Name;
            public readonly List<KeyValuePair<string, string>> Entries =
                new List<KeyValuePair<string, string>>();
        }

        private static readonly Color Transparent = new Color(0f, 0f, 0f, 0f);
        private static readonly Color Paper = Hex("F7F5F2");
        private static readonly Color Surface = Hex("FFFCF8");
        private static readonly Color SurfaceSoft = Hex("EEEAE4");
        private static readonly Color Ink = Hex("292724");
        private static readonly Color InkSoft = Hex("494641");
        private static readonly Color Muted = Hex("77716A");
        private static readonly Color Accent = Hex("C76846");
        private static readonly Color AccentSoft = Hex("E9C7B9");
        private static readonly Color Teal = Hex("287A70");
        private static readonly Color Green = Hex("397A4A");
        private static readonly Color Red = Hex("B6473B");
        private static readonly Color Border = Hex("DDD7CF");
        private const int MaximumHistoryOutputMetrics = 12;

        private SimulationManager manager;
        private RuntimeSceneNavigator navigator;
        private RuntimeAirspaceVisualizer visualizer;
        private RuntimeAirspaceEditor editor;
        private RuntimeRouteDesigner routeDesigner;
        private RuntimeAnalytics analytics;
        private SarRuntimeLocalization localization;
        private SarSimulationHistory simulationHistory;
        private SarSceneWorkspace sceneWorkspace;
        private EventSystem generatedEventSystem;
        private Font uiFont;
        private Camera overviewCamera;
        private Camera followCamera;

        private GameObject[] pages;
        private Button[] navigationButtons;
        private Text statusText;
        private Text footerText;
        private Text editStats;
        private Text routeStats;
        private Text simulationStats;
        private Text analyticsStats;
        private Text routeSummary;
        private Text speedText;
        private Slider timeScaleSlider;
        private InputField randomFleetSizeInput;
        private InputField randomTurnaroundInput;
        private InputField randomMaxDurationInput;
        private readonly Dictionary<string, InputField> simulationParameterInputs =
            new Dictionary<string, InputField>(StringComparer.Ordinal);
        private ParameterSweepRunner sweepRunner;
        private RectTransform sweepVariableContainer;
        private Text sweepStatusText;
        private InputField sweepDurationInput;
        private InputField sweepOutputPathInput;
        private Toggle batchExperimentModeToggle;
        private GameObject batchExperimentSettingsPanel;
        private GameObject singleSimulationControlsPanel;
        private GameObject batchExperimentControlsPanel;
        private GameObject routeFileSettingsPanel;
        private GameObject randomDemandSettingsPanel;
        private Toggle collisionPauseToggle;
        private Toggle aircraftTrailToggle;
        private Toggle laneTypeColorModeToggle;
        private Toggle liveTrafficHeatmapToggle;
        private Toggle lanePassabilityColorModeToggle;
        private Toggle permissiveRightTurnToggle;
        private RectTransform sceneTabsContainer;
        private Button sceneDataButton;
        private Button newSceneButton;
        private readonly List<Button> sceneTabActionButtons = new List<Button>();
        private readonly List<Selectable> simulationConfigurationControls = new List<Selectable>();
        private Button singleSimulationStartButton;
        private Button singleSimulationStopButton;
        private Button batchExperimentStartButton;
        private Button batchExperimentStopButton;
        private GameObject sceneDataOverlay;
        private InputField sceneNameInput;
        private InputField sceneNetworkPathInput;
        private InputField sceneVertiportPathInput;
        private InputField sceneCityPathInput;
        private InputField cityPositionXInput;
        private InputField cityPositionYInput;
        private InputField cityPositionZInput;
        private InputField cityRotationXInput;
        private InputField cityRotationYInput;
        private InputField cityRotationZInput;
        private RectTransform cityDropTarget;
        private Text cityDropStatusText;
        private SarWindowsFileDrop windowsFileDrop;
        private GameObject sceneCloseConfirmOverlay;
        private Text sceneCloseConfirmText;
        private string pendingCloseSceneId;
        private Dropdown closedSceneDropdown;
        private Button reopenSceneButton;
        private Text sceneDataSummary;
        private Text layerStatusText;
        private Text networkSelectionDetails;
        private Text holdingSelectionDetails;
        private Text corridorSelectionDetails;
        private InputField routePathInput;
        private InputField routeReadPathInput;
        private InputField routeOutputPathInput;
        private InputField departureInput;
        private InputField flowCountInput;
        private InputField flowIntervalInput;
        private InputField flowStartInput;
        private InputField altitudeInput;
        private InputField layerNameInput;
        private InputField buildingHeightInput;
        private InputField holdingAltitudeInput;
        private InputField holdingRadiusInput;
        private InputField holdingVertexCountInput;
        private InputField holdingInitialAngleInput;
        private ArcDirection holdingRampDirection = ArcDirection.CounterClockwise;
        private InputField helipadWidthInput;
        private InputField helipadDepthInput;
        private InputField helipadRadiusInput;
        private InputField helipadRotationInput;
        private Dropdown groundHelipadDropdown;
        private Dropdown helipadShapeDropdown;
        private readonly List<Dropdown> activeVertiportDropdowns = new List<Dropdown>();
        private Dropdown originDropdown;
        private Dropdown destinationDropdown;
        private Dropdown aircraftDropdown;
        private Button trajectoryDropdownButton;
        private RectTransform trajectoryChecklistPanel;
        private readonly HashSet<string> selectedTrajectoryIds = new HashSet<string>(StringComparer.Ordinal);
        private string trajectoryChecklistSignature;
        private string loadedHistoricalRunId;
        private RectTransform historyListContainer;
        private Text historySelectionSummary;
        private Button historyMetricSelectorButton;
        private RectTransform historyMetricChecklistPanel;
        private Button historyFactorSelectorButton;
        private RectTransform historyFactorChecklistPanel;
        private Button historyLoad3DButton;
        private Button historyTableButton;
        private Button historyBarButton;
        private Button historySensitivityButton;
        private Button historyTrafficHeatmapButton;
        private Button historyTrafficShowAllLayersButton;
        private RectTransform historyTrafficLayerContainer;
        private string historyTrafficLayerSignature;
        private RectTransform routeAltitudeLayerContainer;
        private RectTransform simulationAltitudeLayerContainer;
        private readonly Dictionary<int, Dictionary<string, bool>> pageAltitudeLayerVisibility =
            new Dictionary<int, Dictionary<string, bool>>();
        private readonly Dictionary<int, string> pageAltitudeLayerSignatures =
            new Dictionary<int, string>();
        private Button historyDeleteButton;
        private GameObject historyDeleteConfirmOverlay;
        private Text historyDeleteConfirmText;
        private string pendingHistoryRecycleRunId;
        private Network loadedHistoricalNetwork;
        private List<Vertiport> loadedHistoricalVertiports;
        private SarHistoricalVisualizationData loadedHistoricalVisualizationData;
        private readonly HashSet<string> selectedHistoryIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> expandedHistoryBatchIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> selectedHistoryMetrics = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> selectedHistoryFactors = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> expandedHistoryMetricGroups =
            new HashSet<string>(new[] { "Mission Outcomes" }, StringComparer.Ordinal);
        private string historyListSignature;
        private string historySelectorSignature;
        private bool historyAnalysisBusy;
        private int historyAnalysisGeneration;
        private GameObject historyAnalysisSurface;
        private Text historyAnalysisTitle;
        private Text historyAnalysisSubtitle;
        private GameObject historyTablePanel;
        private RectTransform historyTableContent;
        private RectTransform historyTableViewport;
        private readonly List<HistoryTablePage> historyTablePages = new List<HistoryTablePage>();
        private int historyTablePageIndex;
        private InputField historyTablePageInput;
        private Text historyTablePageTotalLabel;
        private Button historyTablePreviousButton;
        private Button historyTableNextButton;
        private Text historyTableZoomLabel;
        private SarZoomableViewport historyTableZoom;
        private GameObject historyChartPanel;
        private RawImage historyChartImage;
        private Text historyChartCaption;
        private Texture2D historyChartTexture;
        private string historyChartSourcePath;
        private readonly List<SarRenderedImagePage> historyChartPages = new List<SarRenderedImagePage>();
        private int historyChartPageIndex;
        private string historyChartPageCaption;
        private Button historyChartPreviousButton;
        private Button historyChartNextButton;
        private Text historyChartPageLabel;
        private Text historyChartZoomLabel;
        private SarZoomableViewport historyChartZoom;
        private GameObject historicalHeatmapLegend;
        private Text historicalLaneLegendText;
        private Text historicalResourceLegendText;
        private GameObject[] groundWorkflowPanels;
        private GroundFacilityEditFlow groundFacilityEditFlow;
        private Button modifyVertiportButton;
        private Button createVertiportButton;
        private RectTransform layerStackContainer;
        private readonly List<RectTransform> layerStackRows = new List<RectTransform>();
        private readonly List<Button> layerStackButtons = new List<Button>();
        private readonly List<Button> layerVisibilityButtons = new List<Button>();
        private readonly List<Button> layerFreezeButtons = new List<Button>();
        private bool refreshingLayerDropdown;
        private RectTransform holdingHierarchyContainer;
        private readonly List<Button> holdingHierarchyButtons = new List<Button>();
        private readonly HashSet<string> expandedHoldingStructures = new HashSet<string>();
        private readonly HashSet<string> expandedHoldingCircles = new HashSet<string>();
        private GameObject[] editWorkspacePanels;
        private Button routeLaneSelectionButton;
        private Button automaticRouteButton;
        private Button manualRouteButton;
        private RectTransform missionQueueContainer;
        private string missionQueueSignature;
        private Button tallBuildingHighlightButton;
        private Button entryCorridorButton;
        private Button exitCorridorButton;
        private GameObject[] corridorModePanels;
        private Button facilityCorridorModeButton;
        private Button layerCorridorModeButton;
        private readonly Dictionary<AirspaceVisualLayer, List<Button>> layerButtons =
            new Dictionary<AirspaceVisualLayer, List<Button>>();
        private readonly HashSet<Button> sidebarLayerButtons = new HashSet<Button>();
        private readonly Dictionary<AirspaceEditWorkspace, Button> workspaceButtons =
            new Dictionary<AirspaceEditWorkspace, Button>();
        private readonly Dictionary<RuntimeEditTool, List<Button>> toolButtons =
            new Dictionary<RuntimeEditTool, List<Button>>();
        private readonly Dictionary<SceneViewPreset, Button> viewMenuButtons =
            new Dictionary<SceneViewPreset, Button>();
        private readonly Dictionary<MissionGenerationMode, Button> missionModeButtons =
            new Dictionary<MissionGenerationMode, Button>();
        private readonly Dictionary<Dropdown, List<string>> dropdownSourceOptions =
            new Dictionary<Dropdown, List<string>>();
        private readonly Dictionary<Dropdown, bool> dropdownLocalizeOptions =
            new Dictionary<Dropdown, bool>();

        private RectTransform viewGizmo;
        private GameObject viewGizmoMenu;
        private Text viewGizmoStatusText;
        private RuntimeOrientationCube orientationCube;
        private Image viewGizmoBackground;
        private bool viewGizmoBackgroundVisible = true;
        private Button projectionMenuButton;
        private Button backgroundMenuButton;
        private GameObject displaySettingsOverlay;
        private Text displayModeText;
        private Button windowedModeButton;
        private Button fullScreenModeButton;
        private SarDisplayPreferences displayPreferences;
        private RectTransform visibilityToggleRoot;
        private Text visibilityToggleText;
        private bool workbenchVisible = true;
        private int currentPageIndex;
        private bool dataVisualizationHeatmapEnabled;
        private RuntimeLaneColorMode runtimeLaneColorMode = RuntimeLaneColorMode.LaneType;
        private bool? lastSimulationInterlockState;
        private readonly Dictionary<GameObject, bool> visibilityState =
            new Dictionary<GameObject, bool>();

        private float nextRefresh;
        private SceneViewPreset lastDisplayedViewPreset = (SceneViewPreset)(-1);
        private bool? lastDisplayedProjection;
        private string notice;
        private float noticeUntil;
        private float resumeTimeScale = 1f;

        public bool IsWorkbenchVisible => workbenchVisible;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindObjectOfType<SarRuntimeWorkbench>() != null) return;
            SimulationManager simulationManager = FindObjectOfType<SimulationManager>();
            if (simulationManager == null) return;
#if UNITY_EDITOR
            if (!EditorPlayModeUiEnabled) return;
#endif
            CreateWorkbench(simulationManager);
        }

        private static SarRuntimeWorkbench CreateWorkbench(SimulationManager simulationManager)
        {
            SarRuntimeWorkbench existing = FindObjectOfType<SarRuntimeWorkbench>();
            if (existing != null) return existing;
            if (simulationManager == null) return null;
            var root = new GameObject("Structured Air-Route Simulation Workbench");
            SarRuntimeWorkbench workbench = root.AddComponent<SarRuntimeWorkbench>();
            workbench.manager = simulationManager;
            return workbench;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Applies the persistent Editor Play Mode preference immediately. Disabling
        /// destroys the complete generated workbench and its owned EventSystem.
        /// </summary>
        public static void ApplyEditorPlayModeUiPreference(SimulationManager simulationManager)
        {
            if (!Application.isPlaying) return;
            if (EditorPlayModeUiEnabled)
            {
                CreateWorkbench(simulationManager != null
                    ? simulationManager
                    : FindObjectOfType<SimulationManager>());
                return;
            }

            SarRuntimeWorkbench existing = FindObjectOfType<SarRuntimeWorkbench>();
            if (existing != null) DestroyRuntimeObject(existing.gameObject);
        }
#endif

        private void Awake()
        {
            if (manager == null) manager = FindObjectOfType<SimulationManager>();
            // Resolve the release locale before constructing any controls. Static labels and every
            // later dynamic assignment are localized at their source, so the Canvas never contains
            // an intermediate English frame in the zh-CN player.
            localization = SarRuntimeLocalization.Ensure(gameObject);
            uiFont = CreateUiFont();
            EnsureEventSystem();
            ApplySavedDisplayMode();
            UseOverviewCamera(false);

            visualizer = gameObject.AddComponent<RuntimeAirspaceVisualizer>();
            visualizer.Initialize(manager);
            sceneWorkspace = gameObject.AddComponent<SarSceneWorkspace>();
            // Edit-mode layout tests never touch the saved workspace. Editor play also
            // keeps the open Unity scene instead of restoring the saved default profile.
            sceneWorkspace.Initialize(manager, visualizer, Application.isPlaying);
            navigator = gameObject.AddComponent<RuntimeSceneNavigator>();
            navigator.Initialize(overviewCamera, manager);
            editor = gameObject.AddComponent<RuntimeAirspaceEditor>();
            editor.Initialize(manager, navigator, visualizer);
            routeDesigner = gameObject.AddComponent<RuntimeRouteDesigner>();
            routeDesigner.Initialize(manager, navigator, visualizer);
            analytics = gameObject.AddComponent<RuntimeAnalytics>();
            analytics.Initialize(manager, visualizer);
            simulationHistory = SarSimulationHistory.Ensure(manager);
            simulationHistory?.Initialize(manager, sceneWorkspace);
            if (simulationHistory != null) simulationHistory.HistoryChanged += HandleHistoryChanged;
            if (sweepRunner == null) sweepRunner = FindObjectOfType<ParameterSweepRunner>();
            if (sweepRunner == null) sweepRunner = gameObject.AddComponent<ParameterSweepRunner>();
            sweepRunner.sim = manager;
            if (sweepRunner.variables.Count == 0)
            {
                sweepRunner.variables.Add(new ParameterSweepRunner.SweepVariable
                    { configField = "signalPropagationDelay", startValue = 0f, step = 0.2f, count = 5 });
                sweepRunner.variables.Add(new ParameterSweepRunner.SweepVariable
                    { configField = "positioningErrorStdDev", startValue = 0f, step = 2.5f, count = 5 });
            }
            sweepRunner.ResolveGrid();
            editor.StatusChanged += Notice;
            editor.StateChanged += HandleEditorStateChanged;
            routeDesigner.StatusChanged += Notice;
            routeDesigner.StateChanged += RefreshRouteDesignerState;
            manager.CollisionPauseRequested += HandleCollisionPause;
            sceneWorkspace.StatusChanged += Notice;
            sceneWorkspace.ProfilesChanged += RefreshSceneTabs;
            sceneWorkspace.ActiveSceneChanged += HandleActiveSceneChanged;
            windowsFileDrop = gameObject.AddComponent<SarWindowsFileDrop>();
            windowsFileDrop.FilesDropped += HandleFilesDropped;
            BuildInterface();
        }

        private void OnDestroy()
        {
            InvalidateHistoryAnalysisContext();
            if (editor != null) editor.StatusChanged -= Notice;
            if (editor != null) editor.StateChanged -= HandleEditorStateChanged;
            if (routeDesigner != null) routeDesigner.StatusChanged -= Notice;
            if (routeDesigner != null) routeDesigner.StateChanged -= RefreshRouteDesignerState;
            if (manager != null) manager.CollisionPauseRequested -= HandleCollisionPause;
            if (simulationHistory != null) simulationHistory.HistoryChanged -= HandleHistoryChanged;
            if (windowsFileDrop != null) windowsFileDrop.FilesDropped -= HandleFilesDropped;
            if (sceneWorkspace != null)
            {
                sceneWorkspace.StatusChanged -= Notice;
                sceneWorkspace.ProfilesChanged -= RefreshSceneTabs;
                sceneWorkspace.ActiveSceneChanged -= HandleActiveSceneChanged;
                if (!sceneWorkspace.PreserveOpenUnityScene)
                    sceneWorkspace.SaveActiveScene();
            }
            if (generatedEventSystem != null)
                DestroyRuntimeObject(generatedEventSystem.gameObject);
            if (historyChartTexture != null)
                DestroyRuntimeObject(historyChartTexture);
        }

        private void Start()
        {
            // Network data is loaded in SimulationManager.OnEnable, which runs after this
            // object's Awake. Frame once the nodes actually exist, otherwise the navigator
            // stays pivoted at the origin and the city (kilometres away) is off screen.
            navigator?.FrameAll(manager, false);
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.F10)) SetWorkbenchVisible(!workbenchVisible);
            if (navigator != null && (lastDisplayedViewPreset != navigator.CurrentPreset ||
                                      lastDisplayedProjection != navigator.IsOrthographic))
                RefreshSceneViewButtons();
            if (manager == null || Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.25f;
            RefreshStatus();
        }

        private void BuildInterface()
        {
            Canvas canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.45f;
            gameObject.AddComponent<GraphicRaycaster>();

            RectTransform top = Rect("TopBar", transform, new Vector2(0f, 1f), Vector2.one,
                new Vector2(0f, -118f), Vector2.zero, Surface);
            Rect("TopDivider", top, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 1f), Border);
            RectTransform header = Rect("Header", top, new Vector2(0f, 1f), Vector2.one,
                new Vector2(0f, -76f), Vector2.zero, Transparent);
            Text("SAR · Structured Air-Route Simulation Platform", header, 23, FontStyle.Bold, TextAnchor.MiddleLeft,
                Ink, new Vector2(30f, 0f), new Vector2(-560f, 0f));
            Text("Structured Airspace · Traffic Demand · Operations · Analytics", header, 12, FontStyle.Normal,
                TextAnchor.MiddleLeft, Muted, new Vector2(600f, 0f), new Vector2(-620f, 0f));
            statusText = Text("● System Ready", header, 13, FontStyle.Bold, TextAnchor.MiddleRight,
                Teal, new Vector2(-430f, 0f), new Vector2(-252f, 0f));
            statusText.rectTransform.anchorMin = new Vector2(1f, 0f);
            statusText.rectTransform.anchorMax = Vector2.one;
            RectTransform settingsRect = Rect("DisplaySettings", header, Vector2.one, Vector2.one,
                new Vector2(-88f, -58f), new Vector2(-20f, -18f), SurfaceSoft);
            MakeButton(settingsRect, ToggleDisplaySettings, SurfaceSoft);
            Text("Settings", settingsRect, 12, FontStyle.Bold, TextAnchor.MiddleCenter, Ink,
                Vector2.zero, Vector2.zero);
            BuildSceneTabBar(top);

            RectTransform sidebar = Rect("ModuleNavigation", transform, Vector2.zero, new Vector2(0f, 1f),
                new Vector2(0f, 34f), new Vector2(238f, -118f), Ink);
            TopText(sidebar, "WORKSPACE", -48f, -16f, 12, new Color(1f, 1f, 1f, 0.52f));
            string[] modules = { "Airspace Structure", "Routes & Flow", "Simulation", "Data Visualization" };
            string[] hints = { "Network · Vertiports · Corridors", "Missions · Periodic Flow", "Demand · Playback · Tracking", "Trajectories · Heatmaps · Metrics" };
            navigationButtons = new Button[modules.Length];
            for (int i = 0; i < modules.Length; i++)
            {
                int index = i;
                navigationButtons[i] = NavigationButton(sidebar, modules[i], hints[i],
                    -70f - i * 74f, () => ShowPage(index));
            }
            BuildGlobalLayerControls(sidebar);

            RectTransform drawer = Rect("FunctionDrawer", transform, new Vector2(1f, 0f), Vector2.one,
                new Vector2(-438f, 34f), new Vector2(0f, -118f), Paper);
            Rect("DrawerBorder", drawer, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(1f, 0f), Border);
            pages = new GameObject[4];
            pages[0] = BuildEditPage(drawer).gameObject;
            pages[1] = BuildRoutePage(drawer).gameObject;
            pages[2] = BuildSimulationPage(drawer).gameObject;
            pages[3] = BuildAnalyticsPage(drawer).gameObject;
            BuildHistoryAnalysisSurface();
            BuildHistoricalHeatmapLegend();
            BuildViewGizmo();
            BuildDisplaySettings();
            BuildSceneDataOverlay();
            BuildSceneCloseConfirmation();
            BuildHistoryDeleteConfirmation();
            BuildVisibilityToggle();
            RefreshSceneViewButtons();

            RectTransform footer = Rect("StatusBar", transform, Vector2.zero, new Vector2(1f, 0f),
                Vector2.zero, new Vector2(0f, 34f), Surface);
            Rect("StatusBarDivider", footer, new Vector2(0f, 1f), Vector2.one,
                new Vector2(0f, -1f), Vector2.zero, Border);
            footerText = Text("Initializing...", footer, 12, FontStyle.Normal,
                TextAnchor.MiddleLeft, Muted, new Vector2(18f, 0f), new Vector2(-18f, 0f));
            ShowPage(0);
            RefreshStatus();
        }

        private void BuildVisibilityToggle()
        {
            visibilityToggleRoot = Rect("UiVisibilityButton", transform, Vector2.one, Vector2.one,
                new Vector2(-236f, -58f), new Vector2(-104f, -18f), InkSoft);
            MakeButton(visibilityToggleRoot, () => SetWorkbenchVisible(!workbenchVisible), InkSoft);
            visibilityToggleText = Text("Hide UI  F10", visibilityToggleRoot, 11, FontStyle.Bold,
                TextAnchor.MiddleCenter, Color.white, new Vector2(4f, 0f), new Vector2(-4f, 0f));
            visibilityToggleRoot.SetAsLastSibling();
        }

        public void SetWorkbenchVisible(bool visible)
        {
            if (workbenchVisible == visible) return;
            workbenchVisible = visible;
            if (!visible)
            {
                visibilityState.Clear();
                foreach (Transform child in transform)
                {
                    if (child == visibilityToggleRoot || !(child is RectTransform)) continue;
                    visibilityState[child.gameObject] = child.gameObject.activeSelf;
                    child.gameObject.SetActive(false);
                }
                routeDesigner.LaneSelectionEnabled = false;
                visualizer.EditorOverlayVisible = false;
            }
            else
            {
                foreach (KeyValuePair<GameObject, bool> pair in visibilityState)
                    if (pair.Key != null) pair.Key.SetActive(pair.Value);
                visibilityState.Clear();
                visualizer.EditorOverlayVisible = currentPageIndex == 0;
            }
            if (visibilityToggleRoot != null)
            {
                visibilityToggleRoot.gameObject.SetActive(true);
                visibilityToggleRoot.SetAsLastSibling();
            }
            if (visibilityToggleText != null)
                SetLocalizedText(visibilityToggleText, visible ? "Hide UI  F10" : "Show UI  F10");
            RefreshHistoricalHeatmapLegend();
        }

        private RectTransform BuildEditPage(Transform parent)
        {
            RectTransform root;
            RectTransform content = Page("AirspaceStructure", parent, "Airspace Structure", "Four editing workspaces use top view by default; free 3D navigation is always available", out root);
            editStats = Label(content, "Loading airspace structure...", 14, InkSoft, 94f);
            Section(content, "Editing Workspace", "Select one workspace; only the active workspace remains highlighted");
            WorkspaceRow(content, AirspaceEditWorkspace.NetworkLayers, AirspaceEditWorkspace.GroundFacility);
            WorkspaceRow(content, AirspaceEditWorkspace.HoldingStructure, AirspaceEditWorkspace.ConnectionCorridor);

            editWorkspacePanels = new GameObject[4];

            RectTransform networkPanel = EditorPanel(content, "NetworkLayerPanel");
            editWorkspacePanels[(int)AirspaceEditWorkspace.NetworkLayers] = networkPanel.gameObject;
            Section(networkPanel, "Network Layers", "Drag a layer row to reorder it; objects remain locked to their layer altitude");
            layerStatusText = Label(networkPanel, "Loading layers...", 12, InkSoft, 46f);
            layerStackContainer = EditorPanel(networkPanel, "NetworkLayerStack");
            Label(networkPanel, "Active Layer Name", 12, Muted, 24f, FontStyle.Bold);
            layerNameInput = Input(networkPanel, editor.ActiveLayer?.DisplayName ?? "", "Layer name",
                _ => ApplyNetworkLayerName());
            Label(networkPanel, "Active Layer Altitude (m)", 12, Muted, 24f, FontStyle.Bold);
            altitudeInput = Input(networkPanel, editor.EditingAltitude.ToString("F0"), "Altitude (m)",
                _ => ApplyNetworkLayerAltitude());
            RowButtons(networkPanel,
                ("New Layer", AddNetworkLayer, SurfaceSoft),
                ("Duplicate Layer", DuplicateNetworkLayer, SurfaceSoft));
            FullButton(networkPanel, "Delete Active Layer", DeleteNetworkLayer, Red);

            Section(networkPanel, "Building-Height Aid", "Buildings above the specified height threshold use an accent material");
            buildingHeightInput = Input(networkPanel,
                visualizer.TallBuildingHeightThreshold.ToString("F0"), "Building-height threshold (m)",
                _ => ApplyTallBuildingThreshold());
            tallBuildingHighlightButton = FullButton(networkPanel, "Highlight Tall Buildings",
                ToggleTallBuildingHighlight, SurfaceSoft);

            Section(networkPanel, "Centerline Editing Tools", "Centerlines, nodes, and waypoints are emphasized on the editing plane and support snapping");
            ToolRow(networkPanel, RuntimeEditTool.Inspect, RuntimeEditTool.AddNetworkNode);
            ToolRow(networkPanel, RuntimeEditTool.ConnectNetworkNodes, RuntimeEditTool.AddNetworkWaypoint);
            ToolRow(networkPanel, RuntimeEditTool.MoveNetworkNode, RuntimeEditTool.MoveNetworkWaypoint);
            RowButtons(networkPanel,
                ("Rebuild Derived Lanes", editor.RebuildDerivedNetwork, SurfaceSoft),
                ("Clear Selection", () => editor.ClearNetworkSelection(), SurfaceSoft));
            Section(networkPanel, "Selected Object", "In View mode, click a node, waypoint, or centerline edge to inspect its properties");
            networkSelectionDetails = Label(networkPanel, "No object selected.", 12, InkSoft, 132f);
            RowButtons(networkPanel,
                ("Delete Selected", () => editor.DeleteSelectedNetworkElement(), Red),
                ("Clear Selection", () => editor.ClearNetworkSelection(), SurfaceSoft));
            Hint(networkPanel, "The eye controls per-layer visibility, and the snowflake freezes layer geometry and order. A layer is shown only when both global Air Network visibility and its eye are enabled. Deleting a node also deletes its connected edges and waypoints. ");

            RectTransform groundPanel = EditorPanel(content, "VertiportGroundStructurePanel");
            editWorkspacePanels[(int)AirspaceEditWorkspace.GroundFacility] = groundPanel.gameObject;
            Section(groundPanel, "Ground Structure · 0 m", "Vertiport boundaries, helipads, and approach networks are edited in a top-view projection");
            Section(groundPanel, "Editing Task", "Editing an existing vertiport and creating a new one use separate workflows");
            RowButtons(groundPanel,
                ("Edit Existing Vertiport", () => ShowGroundFacilityFlow(GroundFacilityEditFlow.ModifyExisting), SurfaceSoft),
                ("Create New Vertiport", () => ShowGroundFacilityFlow(GroundFacilityEditFlow.CreateNew), SurfaceSoft),
                (left, right) => { modifyVertiportButton = left; createVertiportButton = right; });
            groundWorkflowPanels = new GameObject[2];

            RectTransform modifyGroundPanel = EditorPanel(groundPanel, "EditExistingVertiportFlow");
            groundWorkflowPanels[(int)GroundFacilityEditFlow.ModifyExisting] = modifyGroundPanel.gameObject;
            Section(modifyGroundPanel, "Step 1 · Select an Existing Vertiport", "Choose a target from the menu; use the focus button to center its boundary when needed");
            ActiveVertiportDropdown(modifyGroundPanel);
            FullButton(modifyGroundPanel, "Focus Selected Vertiport", editor.FocusActiveVertiport, SurfaceSoft);
            Section(modifyGroundPanel, "Boundary Editing", "Inspect the boundary, insert points on existing segments, or move boundary points");
            ToolRow(modifyGroundPanel, RuntimeEditTool.Inspect, RuntimeEditTool.AddVertiportBoundaryPoint);
            ToolButton(modifyGroundPanel, RuntimeEditTool.MoveVertiportBoundaryPoint);

            Section(modifyGroundPanel, "Helipad Design", "Focus an existing helipad or place a new one inside the active boundary using shape parameters");
            groundHelipadDropdown = Dropdown(modifyGroundPanel, Enumerable.Empty<string>(), value =>
                editor.SetActiveHelipad(value));
            FullButton(modifyGroundPanel, "Focus Active Helipad", editor.FocusActiveHelipad, SurfaceSoft);
            helipadShapeDropdown = Dropdown(modifyGroundPanel, new[] { "Rectangle", "Circle", "Freehand Polygon" }, value =>
            {
                RuntimeHelipadShape shape = value == "Circle" ? RuntimeHelipadShape.Circle :
                    value == "Freehand Polygon" ? RuntimeHelipadShape.Polygon : RuntimeHelipadShape.Rectangle;
                ApplyHelipadSettings(shape);
            }, true);
            Label(modifyGroundPanel, "Rectangle width: total helipad size along local X (m)", 11, Muted, 22f);
            helipadWidthInput = Input(modifyGroundPanel, "8", "Rectangle width (m)",
                _ => ApplyHelipadSettings(editor.HelipadShape));
            Label(modifyGroundPanel, "Rectangle depth: total helipad size along local Z (m)", 11, Muted, 22f);
            helipadDepthInput = Input(modifyGroundPanel, "8", "Rectangle depth (m)",
                _ => ApplyHelipadSettings(editor.HelipadShape));
            Label(modifyGroundPanel, "Circle radius: distance from center to helipad boundary (m)", 11, Muted, 22f);
            helipadRadiusInput = Input(modifyGroundPanel, "4", "Circle radius (m)",
                _ => ApplyHelipadSettings(editor.HelipadShape));
            Label(modifyGroundPanel, "Rotation: counterclockwise about the helipad centroid in top view; circles are unaffected (deg)", 11, Muted, 34f);
            helipadRotationInput = Input(modifyGroundPanel, "0", "Centroid rotation (deg)",
                _ => ApplyHelipadSettings(editor.HelipadShape));
            ToolActionRow(modifyGroundPanel, RuntimeEditTool.AddHelipad, "Finish Polygon",
                () => editor.FinishHelipadPolygon());
            FullButton(modifyGroundPanel, "Cancel Freehand Drawing", editor.CancelHelipadPolygon, SurfaceSoft);

            Section(modifyGroundPanel, "Ground Approach Network", "Nodes use solid markers; a temporary edge follows the pointer while connecting");
            ToolRow(modifyGroundPanel, RuntimeEditTool.AddApproachNode, RuntimeEditTool.MoveApproachNode);
            ToolButton(modifyGroundPanel, RuntimeEditTool.ConnectApproachNodes);
            Hint(modifyGroundPanel, "Click an existing boundary segment to insert a point. Boundary points and approach nodes can be moved. The entire helipad boundary must remain inside the active vertiport. ");

            RectTransform createGroundPanel = EditorPanel(groundPanel, "CreateVertiportFlow");
            groundWorkflowPanels[(int)GroundFacilityEditFlow.CreateNew] = createGroundPanel.gameObject;
            Section(createGroundPanel, "Step 1 · Draw the Vertiport Boundary", "Place boundary points sequentially on the 0 m top-view plane; internal tools appear after completion");
            ToolButton(createGroundPanel, RuntimeEditTool.DrawVertiportBoundary);
            RowButtons(createGroundPanel,
                ("Finish Boundary and Continue", FinishNewVertiportBoundary, Accent),
                ("Cancel and Redraw", editor.CancelVertiportBoundary, SurfaceSoft));
            Hint(createGroundPanel, "Place at least three non-collinear boundary points. The system then creates and focuses the vertiport and switches to Edit Existing Vertiport for helipad and approach-network design. ");
            ShowGroundFacilityFlow(GroundFacilityEditFlow.ModifyExisting, false);

            RectTransform holdingPanel = EditorPanel(content, "HoldingStructurePanel");
            editWorkspacePanels[(int)AirspaceEditWorkspace.HoldingStructure] = holdingPanel.gameObject;
            Section(holdingPanel, "Holding-Structure Editing", "Top view is the default; switch to free 3D navigation to inspect altitude relationships");
            ActiveVertiportDropdown(holdingPanel);
            ToolRow(holdingPanel, RuntimeEditTool.Inspect, RuntimeEditTool.AddHoldingStructure);
            ToolButton(holdingPanel, RuntimeEditTool.MoveHoldingStructure);
            Label(holdingPanel, "Placement Altitude (m)", 12, Muted, 24f, FontStyle.Bold);
            holdingAltitudeInput = Input(holdingPanel, "75", "Holding-structure altitude",
                _ => ApplyHoldingSettings());
            Label(holdingPanel, "Holding-Ring Radius (m)", 12, Muted, 24f, FontStyle.Bold);
            holdingRadiusInput = Input(holdingPanel, "8", "Horizontal radius about the selected anchor",
                _ => ApplyHoldingSettings());
            Label(holdingPanel, "Holding-Ring Vertex Count", 12, Muted, 24f, FontStyle.Bold);
            holdingVertexCountInput = Input(holdingPanel, "8", "Number of evenly spaced vertices around the circle",
                _ => ApplyHoldingSettings());
            Label(holdingPanel, "Initial Angle (deg)", 12, Muted, 24f, FontStyle.Bold);
            holdingInitialAngleInput = Input(holdingPanel, "0", "Angle from the X axis to the first holding vertex",
                _ => ApplyHoldingSettings());
            Dropdown(holdingPanel, new[] { "Counterclockwise Arc", "Clockwise Arc", "Shortest Arc" }, value =>
            {
                holdingRampDirection = value == "Clockwise Arc" ? ArcDirection.Clockwise :
                    value == "Shortest Arc" ? ArcDirection.Shortest : ArcDirection.CounterClockwise;
                ApplyHoldingSettings();
            }, true);
            FullButton(holdingPanel, "Generate Holding Ring",
                () => { if (ApplyHoldingSettings()) editor.CreateHoldingCircle(); }, SurfaceSoft);
            ToolButton(holdingPanel, RuntimeEditTool.ConnectHoldingVertices);
            RowButtons(holdingPanel,
                ("Update Selected Ring", () => { if (ApplyHoldingSettings()) editor.UpdateSelectedHoldingCircle(); }, SurfaceSoft),
                ("Delete Selected", () => editor.DeleteSelectedHoldingElement(), Red));
            Section(holdingPanel, "Holding-Structure Hierarchy", "Expand anchors and rings to select ring vertices and one-way ramps directly");
            holdingHierarchyContainer = EditorPanel(holdingPanel, "HoldingStructureHierarchy");
            holdingSelectionDetails = Label(holdingPanel, "No holding-structure object selected.", 12, InkSoft, 104f);
            Hint(holdingPanel, "Place and select an anchor, then set altitude, radius, initial angle, and vertex count to generate a ring. Connect vertices manually to create one-way ramps. In-ring connections are arcs; inter-ring connections are straight. ");

            RectTransform corridorPanel = EditorPanel(content, "ConnectionCorridorPanel");
            editWorkspacePanels[(int)AirspaceEditWorkspace.ConnectionCorridor] = corridorPanel.gameObject;
            Section(corridorPanel, "Connection-Corridor Editing", "Vertiport and interlayer corridors use independent tools and parameters");
            RowButtons(corridorPanel,
                ("Vertiport Corridors", () => ShowCorridorEditMode(RuntimeCorridorEditMode.Facility), SurfaceSoft),
                ("Interlayer Corridors", () => ShowCorridorEditMode(RuntimeCorridorEditMode.Layer), SurfaceSoft),
                (left, right) => { facilityCorridorModeButton = left; layerCorridorModeButton = right; });

            corridorModePanels = new GameObject[2];
            RectTransform facilityCorridorPanel = EditorPanel(corridorPanel, "VertiportCorridorFlow");
            corridorModePanels[(int)RuntimeCorridorEditMode.Facility] = facilityCorridorPanel.gameObject;
            Section(facilityCorridorPanel, "Vertiport Corridors", "Connect holding ramps in the active vertiport to air lanes");
            ActiveVertiportDropdown(facilityCorridorPanel);
            Section(facilityCorridorPanel, "Vertiport-Corridor Direction", "Arrival and departure determine the operating direction of a Bézier corridor");
            RowButtons(facilityCorridorPanel,
                ("Arrival Corridor", () => SetCorridorType("Entry"), SurfaceSoft),
                ("Departure Corridor", () => SetCorridorType("Exit"), SurfaceSoft),
                (left, right) => { entryCorridorButton = left; exitCorridorButton = right; });
            ToolRow(facilityCorridorPanel, RuntimeEditTool.Inspect, RuntimeEditTool.AddFacilityCorridor);
            ToolButton(facilityCorridorPanel, RuntimeEditTool.MoveFacilityCorridorLink);
            RowButtons(facilityCorridorPanel,
                ("Delete Selected Corridor", () => editor.DeleteSelectedFacilityCorridor(), Red),
                ("Clear Selection", editor.ClearCorridorSelection, SurfaceSoft));
            Hint(facilityCorridorPanel, "Click an air lane, then a holding-structure ramp in the active vertiport. Access points can be dragged along their lane or ramp. ");

            RectTransform layerCorridorPanel = EditorPanel(corridorPanel, "InterlayerCorridorFlow");
            corridorModePanels[(int)RuntimeCorridorEditMode.Layer] = layerCorridorPanel.gameObject;
            Section(layerCorridorPanel, "Interlayer Corridors", "Directly connect two lanes on different altitude layers without vertiport or arrival/departure attributes");
            ToolRow(layerCorridorPanel, RuntimeEditTool.Inspect, RuntimeEditTool.AddLayerConnector);
            ToolButton(layerCorridorPanel, RuntimeEditTool.MoveFacilityCorridorLink);
            RowButtons(layerCorridorPanel,
                ("Delete Selected Corridor", () => editor.DeleteSelectedFacilityCorridor(), Red),
                ("Clear Selection", editor.ClearCorridorSelection, SurfaceSoft));
            Hint(layerCorridorPanel, "Click lanes on different altitude layers in sequence to create a Bézier connector. Each tangent point can be dragged along its lane. ");

            corridorSelectionDetails = Label(corridorPanel, "No connection corridor selected.", 12, InkSoft, 150f);
            ShowCorridorEditMode(RuntimeCorridorEditMode.Facility, false);

            Section(content, "Edit History", "Editing is available only while the simulation is stopped");
            RowButtons(content, ("Undo", editor.Undo, SurfaceSoft), ("Validate and Export", editor.Export, Teal));
            ShowEditorWorkspace(AirspaceEditWorkspace.NetworkLayers, false);
            RefreshLayerDropdown();
            RefreshVertiportDropdowns();
            return root;
        }

        private void BuildSceneTabBar(Transform top)
        {
            RectTransform bar = Rect("SceneTabBar", top, Vector2.zero, new Vector2(1f, 0f),
                new Vector2(18f, 7f), new Vector2(-18f, 41f), Surface);
            RectTransform dataButtonRect = Rect("SceneData", bar, new Vector2(1f, 0f), Vector2.one,
                new Vector2(-106f, 1f), new Vector2(0f, -1f), SurfaceSoft);
            sceneDataButton = MakeButton(dataButtonRect, () =>
            {
                if (RejectWorkspaceMutationWhileSimulationRuns("opening scene data")) return;
                ToggleSceneDataOverlay();
            }, SurfaceSoft);
            Text("Scene Data", dataButtonRect, 11, FontStyle.Bold, TextAnchor.MiddleCenter, Ink,
                Vector2.zero, Vector2.zero);
            RectTransform newButtonRect = Rect("NewScene", bar, new Vector2(1f, 0f), Vector2.one,
                new Vector2(-214f, 1f), new Vector2(-112f, -1f), SurfaceSoft);
            newSceneButton = MakeButton(newButtonRect, () =>
            {
                if (RejectWorkspaceMutationWhileSimulationRuns("creating a scene")) return;
                sceneWorkspace.CreateScene("New Scene");
            }, SurfaceSoft);
            Text("＋ New Scene", newButtonRect, 11, FontStyle.Bold, TextAnchor.MiddleCenter, Ink,
                Vector2.zero, Vector2.zero);

            RectTransform viewport = Rect("SceneTabViewport", bar, Vector2.zero, Vector2.one,
                Vector2.zero, new Vector2(-224f, 0f), Surface);
            viewport.gameObject.AddComponent<RectMask2D>();
            ScrollRect scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 32f;
            sceneTabsContainer = Rect("SceneTabContent", viewport, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, Transparent);
            sceneTabsContainer.anchorMin = Vector2.zero;
            sceneTabsContainer.anchorMax = new Vector2(0f, 1f);
            sceneTabsContainer.pivot = new Vector2(0f, 0.5f);
            sceneTabsContainer.sizeDelta = new Vector2(1f, 0f);
            HorizontalLayoutGroup layout = sceneTabsContainer.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = false;
            ContentSizeFitter fitter = sceneTabsContainer.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport;
            scroll.content = sceneTabsContainer;
            RefreshSceneTabs();
        }

        private void RefreshSceneTabs()
        {
            if (sceneTabsContainer == null || sceneWorkspace == null) return;
            sceneTabActionButtons.Clear();
            for (int i = sceneTabsContainer.childCount - 1; i >= 0; i--)
            {
                GameObject oldTab = sceneTabsContainer.GetChild(i).gameObject;
                oldTab.SetActive(false);
                Destroy(oldTab);
            }
            string activeId = sceneWorkspace.ActiveProfile?.Id;
            foreach (SarSceneProfile profile in sceneWorkspace.OpenProfiles)
            {
                SarSceneProfile captured = profile;
                RectTransform tab = Rect("Scene_" + profile.Id, sceneTabsContainer, Vector2.zero, Vector2.one,
                    Vector2.zero, Vector2.zero, profile.Id == activeId ? Accent : SurfaceSoft);
                LayoutElement element = tab.gameObject.AddComponent<LayoutElement>();
                element.preferredWidth = Mathf.Clamp(72f + profile.DisplayName.Length * 12f, 132f, 220f);
                element.preferredHeight = 32f;
                Button activateButton = MakeButton(tab, () =>
                {
                    if (RejectWorkspaceMutationWhileSimulationRuns("switching scene tabs")) return;
                    if (!sceneWorkspace.Activate(captured.Id, out string error)) Notice(error);
                    RefreshSceneTabs();
                }, profile.Id == activeId ? Accent : SurfaceSoft);
                sceneTabActionButtons.Add(activateButton);
                Text(profile.DisplayName, tab, 11, FontStyle.Bold, TextAnchor.MiddleLeft,
                    profile.Id == activeId ? Color.white : Ink, new Vector2(12f, 0f), new Vector2(-34f, 0f));
                RectTransform close = Rect("CloseTab", tab, new Vector2(1f, 0f), Vector2.one,
                    new Vector2(-31f, 2f), new Vector2(-3f, -2f), Transparent);
                Button closeButton = MakeButton(close, () =>
                {
                    if (RejectWorkspaceMutationWhileSimulationRuns("closing a scene tab")) return;
                    RequestCloseScene(captured.Id);
                }, Transparent);
                sceneTabActionButtons.Add(closeButton);
                Text("×", close, 16, FontStyle.Normal, TextAnchor.MiddleCenter,
                    profile.Id == activeId ? Color.white : Muted, Vector2.zero, Vector2.zero);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(sceneTabsContainer);
            RefreshSceneDataFields();
            ApplySimulationInterlock(true);
        }

        private void HandleActiveSceneChanged()
        {
            InvalidateHistoryAnalysisContext();
            pageAltitudeLayerVisibility.Clear();
            pageAltitudeLayerSignatures.Clear();
            visualizer?.ResetDisplayLayerVisibility();
            editor?.HandleSceneDataReloaded();
            routeDesigner?.Initialize(manager, navigator, visualizer);
            analytics?.ClearTracks();
            visualizer?.ClearHistoricalAnalysis();
            visualizer?.ResetHeatmapLayerVisibility();
            loadedHistoricalRunId = null;
            loadedHistoricalNetwork = null;
            loadedHistoricalVertiports = null;
            loadedHistoricalVisualizationData = null;
            historyTrafficLayerSignature = null;
            selectedHistoryIds.Clear();
            historyListSignature = null;
            if (historyAnalysisSurface != null) historyAnalysisSurface.SetActive(false);
            simulationHistory?.LoadCurrentSceneManifests();
            manager?.ClearMissionQueue();
            RefreshTrafficHeatmapLayerControls(true);
            RefreshPageAltitudeLayerControls(1, true);
            RefreshPageAltitudeLayerControls(2, true);
            RefreshSceneTabs();
            RefreshVertiportDropdowns();
            RefreshRouteDesignerState();
            navigator?.FrameAll(manager, true);
        }

        private void HandleHistoryChanged()
        {
            InvalidateHistoryAnalysisContext();
            historyListSignature = null;
            RefreshHistoryList(true);
        }

        private RectTransform BuildRoutePage(Transform parent)
        {
            RectTransform root;
            RectTransform content = Page("RouteSettings", parent, "Routes & Flow", "Visual route configuration without hand-editing rou or route files", out root);
            routeStats = Label(content, "Loading helipads...", 14, InkSoft, 64f);
            Section(content, "Network Altitude-Layer Visibility",
                "This page keeps its own temporary layer visibility; authored scene data is not modified");
            routeAltitudeLayerContainer = EditorPanel(content, "RouteAltitudeLayerVisibility");
            Section(content, "Mission Endpoints", "Select origin and destination helipads");
            IEnumerable<string> pads = manager.Helipads.Keys.OrderBy(id => id).ToList();
            originDropdown = Dropdown(content, pads, routeDesigner.SetOrigin);
            destinationDropdown = Dropdown(content, pads, routeDesigner.SetDestination);
            SetDropdownValue(originDropdown, routeDesigner.OriginHelipadId);
            SetDropdownValue(destinationDropdown, routeDesigner.DestinationHelipadId);
            RowButtons(content,
                ("Focus Origin", routeDesigner.FocusOrigin, SurfaceSoft),
                ("Focus Destination", routeDesigner.FocusDestination, SurfaceSoft));

            Section(content, "Routing Method", "Approach networks and connection corridors are fixed; manual mode selects only network centerline edges");
            RowButtons(content,
                ("Automatic Shortest Path", () => routeDesigner.SetSelectionMode(RouteSelectionMode.AutomaticShortest), SurfaceSoft),
                ("Edit Route Manually", () => routeDesigner.SetSelectionMode(RouteSelectionMode.Manual), SurfaceSoft),
                (left, right) => { automaticRouteButton = left; manualRouteButton = right; });
            RowButtons(content,
                ("Recalculate Shortest Path", () => routeDesigner.BuildShortestRoute(), SurfaceSoft),
                ("Confirm Manual Route", () => routeDesigner.ConfirmManualRoute(), Accent));
            RowButtons(content,
                ("Continue Selecting Edges", ContinueManualLaneSelection, SurfaceSoft),
                ("Return to Locked First Edge", routeDesigner.ClearRoute, SurfaceSoft),
                (left, _) => routeLaneSelectionButton = left);
            routeSummary = Label(content, "Current route: Automatic shortest path", 13, InkSoft, 38f);
            Section(content, "Single-Aircraft Mission", "Set a departure time and add the mission to the demand queue");
            departureInput = Input(content, "0", "Departure time (s)");
            RowButtons(content,
                ("Add Single Mission", () => routeDesigner.AddSingle(ParseFloat(departureInput, 0f)), Accent),
                ("Update Selected Mission", () => routeDesigner.UpdateSelectedMission(ParseFloat(departureInput, 0f)), SurfaceSoft));
            Section(content, "Fixed-Interval Traffic Flow", "SUMO-style flow defined by count, interval, and start time");
            flowCountInput = Input(content, "20", "Aircraft count");
            flowIntervalInput = Input(content, "8", "Dispatch interval (s)");
            flowStartInput = Input(content, "0", "Start time (s)");
            RowButtons(content,
                ("Generate Traffic Flow", () => routeDesigner.AddFlow(ParseInt(flowCountInput, 20),
                    ParseFloat(flowIntervalInput, 8f), ParseFloat(flowStartInput, 0f)), Accent),
                ("Clear Demand Queue", () => { manager.ClearMissionQueue(); Notice("Demand queue cleared. "); }, SurfaceSoft));

            Section(content, "Mission and Traffic-Flow List", "Click a pending mission to load and edit its endpoints and route");
            missionQueueContainer = Rect("PendingMissionList", content, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, Transparent);
            VerticalLayoutGroup queueLayout = missionQueueContainer.gameObject.AddComponent<VerticalLayoutGroup>();
            queueLayout.spacing = 5f;
            queueLayout.childControlHeight = true;
            queueLayout.childForceExpandHeight = false;
            ContentSizeFitter queueFitter = missionQueueContainer.gameObject.AddComponent<ContentSizeFitter>();
            queueFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            RowButtons(content,
                ("Delete Selected Mission", DeleteSelectedMission, Red),
                ("Clear Selection", ClearSelectedMission, SurfaceSoft));

            Section(content, "Route-File Workspace", "Load JSON or XML, edit the queue, then export or overwrite the source file");
            routeReadPathInput = PathInput(content, SarPortablePath.ToPortable(manager.GetBundledRouteExamplePath()),
                "Input route file (.json / .xml)", PickRouteReadPath);
            RowButtons(content,
                ("Load and Replace Mission List", ImportRouteWorkspace, Accent),
                ("Overwrite Input File", OverwriteRouteWorkspace, Red));
            routeOutputPathInput = PathInput(content, SarPortablePath.ToPortable(manager.GetDefaultRouteExportPath()),
                "Output path (.json / .xml)", PickRouteOutputPath);
            FullButton(content, "Export Missions and Traffic Flow", ExportRouteWorkspace, Teal);
            Hint(content, "The first centerline edge reached by the departure corridor is locked automatically. Lane-level connectivity is validated after every appended edge; confirm the route once it reaches an edge connected to the destination exit corridor. ");
            RefreshPageAltitudeLayerControls(1, true);
            RefreshRouteDesignerState();
            return root;
        }

        private RectTransform BuildSimulationPage(Transform parent)
        {
            RectTransform root;
            RectTransform content = Page("Simulation", parent, "Simulation", "Parameter configuration, playback controls, and aircraft tracking", out root);
            simulationStats = Label(content, "Loading simulation status...", 14, InkSoft, 130f);
            Section(content, "Demand Source", "Load JSON or XML, or generate continuous random demand");
            RowButtons(content,
                ("JSON / XML Routes", () => SetMissionMode(MissionGenerationMode.JSON), SurfaceSoft),
                ("Continuous Random Demand", () => SetMissionMode(MissionGenerationMode.RandomContinuous), SurfaceSoft),
                (left, right) =>
                {
                    missionModeButtons[MissionGenerationMode.JSON] = left;
                    missionModeButtons[MissionGenerationMode.RandomContinuous] = right;
                });
            RectTransform routeFilePanel = EditorPanel(content, "FileDemandParameters");
            routeFileSettingsPanel = routeFilePanel.gameObject;
            routePathInput = PathInput(routeFilePanel, SarPortablePath.ToPortable(manager.GetBundledRouteExamplePath()),
                "Route file (.json / .xml)", PickSimulationRoutePath);
            RowButtons(routeFilePanel, ("Load and Replace Queue", LoadRouteFile, Accent),
                ("Clear Queue", () => { manager.ClearMissionQueue(); Notice("Demand queue cleared. "); }, SurfaceSoft));

            RectTransform randomPanel = EditorPanel(content, "RandomDemandParameters");
            randomDemandSettingsPanel = randomPanel.gameObject;
            int safeFleetCapacity = manager != null ? manager.GetRandomFleetCapacity() : 0;
            Label(randomPanel, $"Aircraft Count (safe limit: {safeFleetCapacity})", 11, Muted, 22f);
            randomFleetSizeInput = Input(randomPanel,
                (manager?.config != null ? manager.config.maxConcurrentAircraft : 50).ToString(CultureInfo.InvariantCulture),
                "Random-fleet aircraft count", ApplyRandomFleetSize);
            Label(randomPanel, "Turnaround Time After Arrival (s)", 11, Muted, 22f);
            randomTurnaroundInput = Input(randomPanel,
                (manager.config != null ? manager.config.randomTurnaroundTime : 10f)
                .ToString("0.###", CultureInfo.InvariantCulture),
                "Random-demand turnaround time after arrival (s)", ApplyRandomTurnaroundTime);
            Label(randomPanel, "Maximum Run Duration (s; 0 = unlimited)", 11, Muted, 22f);
            randomMaxDurationInput = Input(randomPanel,
                (manager.maxSimDuration > 0f ? manager.maxSimDuration : 0f)
                .ToString("0.###", CultureInfo.InvariantCulture),
                "Applies only to random-demand mode", ApplyRandomMaxDuration);

            Section(content, "Runtime Parameters", "Expand only the parameter category needed for the current experiment");
            RectTransform parameterControls = CollapsibleSection(content, "ParameterControls",
                "Parameter Controls", false);
            RectTransform aircraftParameters = CollapsibleSection(parameterControls,
                "AircraftParameterGroup", "Aircraft Dynamics", false);
            BuildSimulationParameterGroup(aircraftParameters, SimulationParameterGroup.Aircraft);
            RectTransform reservationParameters = CollapsibleSection(parameterControls,
                "ReservationParameterGroup", "Reservation System", false);
            BuildSimulationParameterGroup(reservationParameters, SimulationParameterGroup.Reservation);
            RectTransform trafficControlParameters = CollapsibleSection(parameterControls,
                "TrafficControlParameterGroup", "Traffic Control", false);
            BuildSimulationParameterGroup(trafficControlParameters, SimulationParameterGroup.TrafficControl);
            permissiveRightTurnToggle = ToggleField(trafficControlParameters,
                "Allow dedicated right-turn lanes to proceed on red",
                manager.config != null && manager.config.permissiveRightTurn,
                _ => { });
            RectTransform uncertaintyParameters = CollapsibleSection(parameterControls,
                "InformationUncertaintyParameterGroup", "Information Uncertainty", false);
            BuildSimulationParameterGroup(uncertaintyParameters,
                SimulationParameterGroup.InformationUncertainty);
            RowButtons(parameterControls,
                ("Apply All Parameters", ApplySimulationParameters, Accent),
                ("Reload Current Values", RefreshSimulationParameterInputs, SurfaceSoft));

            Section(content, "Experiment Mode", "Use a single run by default, or enable a Cartesian batch experiment");
            batchExperimentModeToggle = ToggleField(content, "Set up batch experiment", false,
                SetBatchExperimentMode);
            RectTransform batchSettings = EditorPanel(content, "BatchExperimentSettings");
            batchExperimentSettingsPanel = batchSettings.gameObject;
            sweepStatusText = Label(batchSettings, "Loading batch-experiment configuration...", 12, InkSoft, 50f);
            Label(batchSettings, "Duration per Batch (s)", 11, Muted, 22f);
            sweepDurationInput = Input(batchSettings,
                sweepRunner.batchDuration.ToString("0.###", CultureInfo.InvariantCulture),
                "Duration per batch (s)");
            Label(batchSettings, "Results-Table Output Path", 11, Muted, 22f);
            sweepOutputPathInput = Input(batchSettings,
                SarPortablePath.ToPortable(sweepRunner.OutputFullPath),
                "app://Output/BatchExperimentRunSummaryTable.csv");
            sweepVariableContainer = EditorPanel(batchSettings, "BatchExperimentVariables");
            RebuildSweepVariableControls();
            RowButtons(batchSettings,
                ("Add Factor", AddSweepVariable, SurfaceSoft),
                ("Remove Last", RemoveLastSweepVariable, SurfaceSoft));
            // Everything constructed above this point changes demand, parameters, or
            // experiment configuration. Keep a stable list so it can be frozen for the
            // complete lifetime of a single or batch run, including gaps between batches.
            simulationConfigurationControls.Clear();
            simulationConfigurationControls.AddRange(
                content.GetComponentsInChildren<Selectable>(true));
            Section(content, "Run Controls", "Start, pause, stop, and adjust time scale");
            RectTransform singleControls = EditorPanel(content, "SingleSimulationControls");
            singleSimulationControlsPanel = singleControls.gameObject;
            RowButtons(singleControls, ("Start Simulation", StartSimulation, Green),
                ("Stop Simulation", StopSingleSimulation, Red),
                (start, stop) =>
                {
                    singleSimulationStartButton = start;
                    singleSimulationStopButton = stop;
                });
            RectTransform batchControls = EditorPanel(content, "BatchExperimentControls");
            batchExperimentControlsPanel = batchControls.gameObject;
            RowButtons(batchControls,
                ("Start Batch Experiment", StartParameterSweep, Green),
                ("Stop Batch Experiment", StopParameterSweep, Red),
                (start, stop) =>
                {
                    batchExperimentStartButton = start;
                    batchExperimentStopButton = stop;
                });
            RowButtons(content, ("Pause / Resume", TogglePause, SurfaceSoft),
                ("Reset to 1×", () => SetTimeScale(1f), SurfaceSoft));
            timeScaleSlider = Slider(content, 0.25f, 4f, 1f, SetTimeScale);
            speedText = Label(content, "Time Scale  1.00×", 12, Muted, 28f);
            collisionPauseToggle = ToggleField(content, "Pause and focus automatically on collision",
                manager.pauseOnCollision, value => manager.pauseOnCollision = value);
            Section(content, "Aircraft Trail", "Show the most recent portion of each aircraft trajectory in overview or follow view");
            aircraftTrailToggle = ToggleField(content, "Show aircraft trails",
                manager.showAircraftTrails, value =>
                {
                    manager.ConfigureAircraftTrails(value);
                    Notice($"Aircraft trails {(value ? "enabled" : "disabled")}. ");
                });
            Hint(content, "Trail length is maintained at approximately eight aircraft body lengths; it is wider near the aircraft and narrows and fades toward the tail. ");
            Section(content, "Runtime Lane Coloring",
                "Choose exactly one lane-line color source; intersection-signal markers remain a separate visual layer");
            RectTransform laneColorModePanel = EditorPanel(content, "RuntimeLaneColorMode");
            ToggleGroup laneColorModeGroup = laneColorModePanel.gameObject.AddComponent<ToggleGroup>();
            laneColorModeGroup.allowSwitchOff = false;
            laneTypeColorModeToggle = ToggleField(laneColorModePanel,
                "Lane-type colors (no live recoloring)", true,
                enabled => SetRuntimeLaneColorMode(RuntimeLaneColorMode.LaneType, enabled));
            liveTrafficHeatmapToggle = ToggleField(laneColorModePanel,
                "Live aircraft load (gray → green → red)", false,
                enabled => SetRuntimeLaneColorMode(RuntimeLaneColorMode.LiveAircraftLoad, enabled));
            lanePassabilityColorModeToggle = ToggleField(laneColorModePanel,
                "Lane passability (green = open, red = blocked)", false,
                enabled => SetRuntimeLaneColorMode(RuntimeLaneColorMode.Passability, enabled));
            laneTypeColorModeToggle.group = laneColorModeGroup;
            liveTrafficHeatmapToggle.group = laneColorModeGroup;
            lanePassabilityColorModeToggle.group = laneColorModeGroup;
            Section(content, "Network Altitude-Layer Visibility",
                "Temporarily hide layers while observing a run; this does not modify the airspace scene or archived topology");
            simulationAltitudeLayerContainer = EditorPanel(content, "SimulationAltitudeLayerVisibility");
            Section(content, "Aircraft Tracking", "Switch between scene overview and aircraft-follow views");
            aircraftDropdown = Dropdown(content, Enumerable.Empty<string>(), _ => { });
            RowButtons(content, ("Follow Selected Aircraft", FollowSelectedAircraft, SurfaceSoft),
                ("Return to Overview", () => UseOverviewCamera(true), SurfaceSoft));
            Hint(content, "For random demand, verify takeoff at 1× before increasing to 4× for long runs. Production runs are limited to 4× to preserve reservation-window accuracy. ");
            SetBatchExperimentMode(false);
            RefreshMissionModeButtons();
            RefreshPageAltitudeLayerControls(2, true);
            ApplySimulationInterlock(true);
            return root;
        }

        private void SetBatchExperimentMode(bool enabled)
        {
            if (sweepRunner != null && sweepRunner.isRunning && !enabled)
            {
                batchExperimentModeToggle?.SetIsOnWithoutNotify(true);
                Notice("Stop the current batch experiment before returning to single-run mode. ");
                return;
            }
            if (manager != null && manager.IsRunning() && enabled)
            {
                batchExperimentModeToggle?.SetIsOnWithoutNotify(false);
                Notice("Stop the current single simulation before configuring a batch experiment. ");
                return;
            }

            batchExperimentModeToggle?.SetIsOnWithoutNotify(enabled);
            if (batchExperimentSettingsPanel != null)
                batchExperimentSettingsPanel.SetActive(enabled);
            if (batchExperimentControlsPanel != null)
                batchExperimentControlsPanel.SetActive(enabled);
            if (singleSimulationControlsPanel != null)
                singleSimulationControlsPanel.SetActive(!enabled);
        }

        private void BuildSimulationParameterGroup(Transform parent, SimulationParameterGroup group)
        {
            if (manager?.config == null) return;
            foreach (SimulationParameterDescriptor descriptor in SimulationParameterCatalog.All
                         .Where(item => item.Group == group))
            {
                Label(parent, descriptor.DisplayName, 11, Muted, 21f);
                InputField input = Input(parent,
                    SimulationParameterCatalog.FormatValue(manager.config, descriptor),
                    descriptor.FieldName);
                input.gameObject.name = "Parameter_" + descriptor.FieldName;
                simulationParameterInputs[descriptor.FieldName] = input;
            }
        }

        private void ApplySimulationParameters()
        {
            if (manager == null || manager.config == null)
            {
                Notice("Simulation configuration is unavailable. ");
                return;
            }
            if (manager.IsRunning() || (sweepRunner != null && sweepRunner.isRunning))
            {
                Notice("Stop the current simulation or batch experiment before changing parameters. ");
                return;
            }

            var errors = new List<string>();
            var previousValues = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, InputField> pair in simulationParameterInputs)
            {
                if (!SimulationParameterCatalog.TryGet(pair.Key, out SimulationParameterDescriptor descriptor))
                    continue;
                previousValues[pair.Key] = SimulationParameterCatalog.ReadValue(manager.config, pair.Key);
                if (!SimulationParameterCatalog.TrySetFromText(manager.config, descriptor,
                        pair.Value.text, out string error)) errors.Add(error);
            }
            if (manager.config.laneChangeRemovalTimeout <= manager.config.laneChangeRerouteTimeout)
                errors.Add("The lane-change deadlock-removal wait must exceed the lane-change replanning wait.");
            if (errors.Count > 0)
            {
                foreach (KeyValuePair<string, object> pair in previousValues)
                    SimulationParameterCatalog.RestoreValue(manager.config, pair.Key, pair.Value);
                RefreshSimulationParameterInputs();
                Notice("Parameters were not applied: " + string.Join("; ", errors.Take(3)) + " ");
                return;
            }
            manager.config.permissiveRightTurn = permissiveRightTurnToggle != null &&
                                                 permissiveRightTurnToggle.isOn;
            manager.BuildTrafficSignalSystem();
            Notice("Aircraft, reservation, traffic-control, and experiment parameters applied. ");
        }

        private void RefreshSimulationParameterInputs()
        {
            if (manager?.config == null) return;
            foreach (KeyValuePair<string, InputField> pair in simulationParameterInputs)
                if (SimulationParameterCatalog.TryGet(pair.Key, out SimulationParameterDescriptor descriptor))
                    pair.Value.SetTextWithoutNotify(
                        SimulationParameterCatalog.FormatValue(manager.config, descriptor));
            permissiveRightTurnToggle?.SetIsOnWithoutNotify(manager.config.permissiveRightTurn);
        }

        private void RebuildSweepVariableControls()
        {
            if (sweepVariableContainer == null || sweepRunner == null) return;
            foreach (Transform child in sweepVariableContainer.Cast<Transform>().ToList())
            {
                child.gameObject.SetActive(false);
                DestroyUiObject(child.gameObject);
            }

            for (int index = 0; index < sweepRunner.variables.Count; index++)
            {
                ParameterSweepRunner.SweepVariable variable = sweepRunner.variables[index];
                RectTransform panel = EditorPanel(sweepVariableContainer, $"Factor_{index + 1}");
                Label(panel, $"Factor {index + 1}", 12, Ink, 24f, FontStyle.Bold);
                ToggleField(panel, "Enabled", variable.enabled, value =>
                {
                    variable.enabled = value;
                    sweepRunner.ResolveGrid();
                    RefreshSweepStatus();
                });
                Dropdown field = Dropdown(panel, ParameterSweepRunner.SweepableConfigFields, value =>
                {
                    variable.configField = value;
                    sweepRunner.ResolveGrid();
                    RefreshSweepStatus();
                }, true);
                SetDropdownValue(field, variable.configField);
                Label(panel, "Start / Step / Number of Values", 11, Muted, 21f);
                Input(panel, variable.startValue.ToString("0.###", CultureInfo.InvariantCulture), "Start value",
                    value =>
                    {
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                            variable.startValue = number;
                        sweepRunner.ResolveGrid();
                        RefreshSweepStatus();
                    });
                Input(panel, variable.step.ToString("0.###", CultureInfo.InvariantCulture), "Step",
                    value =>
                    {
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                            variable.step = number;
                        sweepRunner.ResolveGrid();
                        RefreshSweepStatus();
                    });
                Input(panel, variable.count.ToString(CultureInfo.InvariantCulture), "Number of values",
                    value =>
                    {
                        if (int.TryParse(value, out int count)) variable.count = Mathf.Clamp(count, 1, 10000);
                        sweepRunner.ResolveGrid();
                        RefreshSweepStatus();
                    });
            }
            RefreshSweepStatus();
        }

        private void AddSweepVariable()
        {
            if (sweepRunner == null || sweepRunner.isRunning) return;
            sweepRunner.variables.Add(new ParameterSweepRunner.SweepVariable());
            sweepRunner.ResolveGrid();
            RebuildSweepVariableControls();
        }

        private void RemoveLastSweepVariable()
        {
            if (sweepRunner == null || sweepRunner.isRunning || sweepRunner.variables.Count == 0) return;
            sweepRunner.variables.RemoveAt(sweepRunner.variables.Count - 1);
            sweepRunner.ResolveGrid();
            RebuildSweepVariableControls();
        }

        private bool ApplySweepInputs()
        {
            if (sweepRunner == null) return false;
            if (!TryReadFloat(sweepDurationInput, "duration per batch", 0.1f, 10000000f,
                    sweepRunner.batchDuration, out float duration)) return false;
            string output = sweepOutputPathInput?.text?.Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                Notice("Set the CSV output path for the batch experiment. ");
                return false;
            }
            sweepRunner.batchDuration = duration;
            sweepRunner.outputPath = output;
            sweepRunner.ResolveGrid();
            return true;
        }

        private void StartParameterSweep()
        {
            if (IsSimulationInteractionLocked())
            {
                Notice("Stop the current single or batch simulation before starting another batch experiment. ");
                return;
            }
            if (!ApplySweepInputs()) return;
            if (sweepRunner.StartSweep())
                Notice($"Batch experiment started: {sweepRunner.totalBatches} batches; output: {sweepRunner.OutputFullPath}. ");
            else Notice("Batch experiment did not start: " + sweepRunner.lastError + " ");
            RefreshSweepStatus();
            ApplySimulationInterlock(true);
        }

        private void StopParameterSweep()
        {
            if (sweepRunner == null || !sweepRunner.isRunning)
            {
                Notice("No batch experiment is currently running. ");
                return;
            }
            sweepRunner.StopSweepInternal();
            Notice("Batch experiment stopped; original configuration values restored. ");
            RefreshSweepStatus();
            ApplySimulationInterlock(true);
        }

        private void RefreshSweepStatus()
        {
            if (sweepStatusText == null || sweepRunner == null) return;
            if (!sweepRunner.isRunning) sweepRunner.ResolveGrid();
            SetLocalizedText(sweepStatusText, sweepRunner.isRunning
                ? $"Running batch {sweepRunner.currentBatch} / {sweepRunner.totalBatches} · Phase {sweepRunner.batchPhase}"
                : $"Grid: {sweepRunner.totalBatches} batches · Output {SarPortablePath.ToPortable(sweepRunner.OutputFullPath)}");
        }

        private RectTransform BuildAnalyticsPage(Transform parent)
        {
            RectTransform root;
            RectTransform content = Page("DataVisualization", parent, "Simulation Data Visualization", "Scene history, 3D replay, metrics tables, and comparison charts", out root);
            analyticsStats = Label(content, "No statistics available", 14, InkSoft, 170f);

            Section(content, "Simulation History for the Current Scene", "Records are stored with each scene; a batch experiment groups all member runs under one parent record");
            historySelectionSummary = Label(content, "Loading current-scene history...", 12, InkSoft, 60f);
            RowButtons(content,
                ("Refresh Records", () => { simulationHistory?.LoadCurrentSceneManifests(); RefreshHistoryList(true); }, SurfaceSoft),
                ("Open History Folder", OpenHistoryFolder, Teal));
            RowButtons(content,
                ("Move Selected Record to Recycle Bin", RequestRecycleSelectedHistory, Red),
                ("Refresh After External Restore", () =>
                {
                    simulationHistory?.LoadCurrentSceneManifests();
                    RefreshHistoryList(true);
                }, SurfaceSoft),
                (left, _) => historyDeleteButton = left);
            historyListContainer = EditorPanel(content, "SceneSimulationHistoryList");

            Section(content, "Analysis Variables", "Select one or more output metrics (Y); input parameters (X) apply only to parameter sensitivity");
            Label(content, "Output Metrics (Y)", 11, Muted, 21f);
            historyMetricSelectorButton = FullButton(content, "Select output metrics (0) ▾",
                ToggleHistoryMetricChecklist, SurfaceSoft);
            historyMetricChecklistPanel = EditorPanel(content, "HistoryMetricChecklist");
            historyMetricChecklistPanel.gameObject.SetActive(false);
            Label(content, "Input Parameters (X)", 11, Muted, 21f);
            historyFactorSelectorButton = FullButton(content, "Select input parameters (0/3) ▾",
                ToggleHistoryFactorChecklist, SurfaceSoft);
            historyFactorChecklistPanel = EditorPanel(content, "HistoryFactorChecklist");
            historyFactorChecklistPanel.gameObject.SetActive(false);
            RowButtons(content,
                ("Load 3D Results", LoadSelectedHistoryInto3D, Accent),
                ("Review Result Table", ShowSelectedHistoryTable, SurfaceSoft),
                (left, right) => { historyLoad3DButton = left; historyTableButton = right; });
            RowButtons(content,
                ("Metric Bar Chart", ShowSelectedHistoryBars, SurfaceSoft),
                ("Parameter Sensitivity Plot", ShowSelectedHistorySensitivity, SurfaceSoft),
                (left, right) => { historyBarButton = left; historySensitivityButton = right; });
            Hint(content, "Expand a batch to inspect or load one member in 3D. Batch parents expand only for statistical analysis; they never load every recording into the 3D view. Bar comparisons use selected records as X and accept multiple Y metrics; sensitivity uses one to three selected X parameters. Both figures are generated by the bundled Python scientific-plotting tool. ");

            Section(content, "Analysis Layers", "General airspace layers are controlled from the sidebar; only analysis-result layers appear here");
            LayerRow(content, (AirspaceVisualLayer.Heatmap, "Traffic Heatmap"),
                (AirspaceVisualLayer.RoutePreview, "Routes / Trajectories"));

            Section(content, "Traffic-Heatmap Altitude Layers",
                "Choose which authored road-network layers appear in the 3D historical heatmap and the Python 2D traffic figure");
            historyTrafficLayerContainer = EditorPanel(content, "TrafficHeatmapAltitudeLayerList");
            RowButtons(content,
                ("Generate / Refresh 2D Traffic Heatmap", ShowSelectedHistoryTrafficHeatmap, Accent),
                ("Show All Altitude Layers", ResetTrafficHeatmapLayerVisibility, SurfaceSoft),
                (left, right) =>
                {
                    historyTrafficHeatmapButton = left;
                    historyTrafficShowAllLayersButton = right;
                });
            Hint(content, "Select exactly one single run or batch member. The Python figure separates visible altitude layers into square map panels, includes vertiport reservation resources, and deliberately excludes lane-to-lane connector curves. ");

            Section(content, "Complete Mission Trajectories", "Expand the list to select multiple completed mission trajectories");
            trajectoryDropdownButton = FullButton(content, "Select Completed Trajectories (0) ▾",
                ToggleTrajectoryChecklist, SurfaceSoft);
            trajectoryChecklistPanel = EditorPanel(content, "TrajectoryChecklist");
            trajectoryChecklistPanel.gameObject.SetActive(false);
            RowButtons(content, ("Draw Selected Trajectories", DrawSelectedTracks, Accent),
                ("Clear Trajectory Display", () => { analytics.ClearTrackDisplay(); Notice("Trajectory display cleared; records retained. "); }, SurfaceSoft));
            RowButtons(content, ("Select All Complete Trajectories", SelectAllTrajectories, SurfaceSoft),
                ("Delete All Trajectory Records", ClearTrajectoryRecords, Red));

            Section(content, "Current Session and Files", "Current-session trajectories remain available immediately; use scene history above for persistent results");
            RowButtons(content, ("Save Demand Queue", SaveQueue, SurfaceSoft), ("Open Output Folder", OpenOutputFolder, Teal));
            Hint(content, "Current-session trajectories retain only complete missions. Persistent history restores one selected run at a time; its traffic heatmap uses lane-entry counts and ground-resource reservation time. ");
            RefreshTrajectoryChecklist(true);
            RefreshHistoryList(true);
            RefreshTrafficHeatmapLayerControls(true);
            return root;
        }

        private IReadOnlyList<AltitudeLayer> CurrentTrafficHeatmapLayers()
        {
            IEnumerable<AltitudeLayer> layers = loadedHistoricalNetwork?.AltitudeLayers;
            if (layers == null)
                return visualizer?.GetHeatmapAltitudeLayers() ?? Array.Empty<AltitudeLayer>();
            return layers.Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId))
                .OrderBy(layer => layer.NominalAltitude)
                .ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
                .ToList();
        }

        private void RefreshTrafficHeatmapLayerControls(bool force = false)
        {
            if (historyTrafficLayerContainer == null || visualizer == null) return;
            IReadOnlyList<AltitudeLayer> layers = CurrentTrafficHeatmapLayers();
            string signature = (historyAnalysisBusy ? "busy|" : "ready|") + string.Join("|",
                layers.Select(layer =>
                    $"{layer.LayerId}:{layer.DisplayName}:{layer.NominalAltitude:R}:" +
                    (visualizer.IsHeatmapLayerVisible(layer.LayerId) ? "1" : "0")));
            if (!force && string.Equals(signature, historyTrafficLayerSignature, StringComparison.Ordinal))
                return;
            historyTrafficLayerSignature = signature;
            for (int index = historyTrafficLayerContainer.childCount - 1; index >= 0; index--)
                DestroyRuntimeObject(historyTrafficLayerContainer.GetChild(index).gameObject);

            if (layers.Count == 0)
            {
                Label(historyTrafficLayerContainer, "No altitude layers are available", 11, Muted, 32f);
                return;
            }

            foreach (AltitudeLayer layer in layers)
            {
                AltitudeLayer captured = layer;
                string displayName = RawLayerName(captured);
                Toggle toggle = ToggleField(historyTrafficLayerContainer,
                    $"{displayName} · {captured.NominalAltitude:0.##} m",
                    IsPageAltitudeLayerVisible(3, captured.LayerId), value =>
                    {
                        if (!SetPageAltitudeLayerVisible(3, captured.LayerId, value)) return;
                        historyTrafficLayerSignature = null;
                        RefreshHistoricalHeatmapLegend();
                        NoticeRawLayer(displayName, "Traffic-heatmap layer ",
                            (value ? ": Visible. " : ": Hidden. ") +
                            "Use Generate / Refresh 2D Traffic Heatmap to update the Python figure. ");
                    });
                SetToggleRawLabel(toggle, $"{displayName} · {captured.NominalAltitude:0.##} m");
                toggle.interactable = !historyAnalysisBusy;
            }
        }

        private void ResetTrafficHeatmapLayerVisibility()
        {
            if (visualizer == null || historyAnalysisBusy) return;
            visualizer.ResetHeatmapLayerVisibility();
            ResetPageAltitudeLayerVisibility(3);
            historyTrafficLayerSignature = null;
            RefreshTrafficHeatmapLayerControls(true);
            RefreshHistoricalHeatmapLegend();
            Notice("All traffic-heatmap altitude layers are visible. Refresh the 2D figure if it is open. ");
        }

        private IReadOnlyList<AltitudeLayer> CurrentLiveAltitudeLayers()
        {
            List<AltitudeLayer> axis = manager?.networkManager?.AxisNetwork?.AltitudeLayers?
                .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId)).ToList()
                ?? new List<AltitudeLayer>();
            List<AltitudeLayer> full = manager?.networkManager?.FullNetwork?.AltitudeLayers?
                .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId)).ToList()
                ?? new List<AltitudeLayer>();
            // AxisNetwork is the authored source. Legacy axis files, however, acquire
            // only the constructor's L0 placeholder while the generated network owns
            // the complete layer list; prefer the source that actually has more IDs.
            IEnumerable<AltitudeLayer> source = full.Select(layer => layer.LayerId).Distinct().Count() >
                                                axis.Select(layer => layer.LayerId).Distinct().Count()
                ? full
                : axis;
            return source.GroupBy(layer => layer.LayerId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(layer => layer.NominalAltitude)
                .ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
                .ToList();
        }

        private Dictionary<string, bool> PageAltitudeLayerState(int pageIndex)
        {
            if (!pageAltitudeLayerVisibility.TryGetValue(pageIndex, out Dictionary<string, bool> state))
            {
                state = new Dictionary<string, bool>(StringComparer.Ordinal);
                pageAltitudeLayerVisibility[pageIndex] = state;
            }
            return state;
        }

        private bool IsPageAltitudeLayerVisible(int pageIndex, string layerId)
        {
            if (string.IsNullOrWhiteSpace(layerId)) return true;
            Dictionary<string, bool> state = PageAltitudeLayerState(pageIndex);
            return !state.TryGetValue(layerId, out bool visible) || visible;
        }

        private bool SetPageAltitudeLayerVisible(int pageIndex, string layerId, bool visible)
        {
            if (string.IsNullOrWhiteSpace(layerId)) return false;
            // Unity UI invokes Toggle.onValueChanged once from Toggle.Rebuild even when the
            // value did not change. Rebuilding this same checklist from that callback creates
            // an endless Canvas rebuild/destroy loop, so treat an equal value as a strict no-op.
            if (IsPageAltitudeLayerVisible(pageIndex, layerId) == visible) return false;
            PageAltitudeLayerState(pageIndex)[layerId] = visible;
            pageAltitudeLayerSignatures.Remove(pageIndex);
            if (currentPageIndex == pageIndex)
                visualizer?.SetDisplayLayerVisible(layerId, visible);
            if (pageIndex == 3)
                visualizer?.SetHeatmapLayerVisible(layerId, visible);
            // The toggled control already displays the new value. A future forced/topology
            // refresh will rebuild the list; never destroy it from inside its own callback.
            return true;
        }

        private void ResetPageAltitudeLayerVisibility(int pageIndex)
        {
            PageAltitudeLayerState(pageIndex).Clear();
            pageAltitudeLayerSignatures.Remove(pageIndex);
            if (currentPageIndex == pageIndex)
            {
                visualizer?.ResetDisplayLayerVisibility();
                ApplyPageAltitudeLayerVisibility(pageIndex);
            }
        }

        private void ApplyPageAltitudeLayerVisibility(int pageIndex)
        {
            if (visualizer == null) return;
            visualizer.ResetDisplayLayerVisibility();
            foreach (KeyValuePair<string, bool> pair in PageAltitudeLayerState(pageIndex))
                visualizer.SetDisplayLayerVisible(pair.Key, pair.Value);
            if (pageIndex == 3)
            {
                visualizer.ResetHeatmapLayerVisibility();
                foreach (AltitudeLayer layer in CurrentTrafficHeatmapLayers())
                    visualizer.SetHeatmapLayerVisible(layer.LayerId,
                        IsPageAltitudeLayerVisible(3, layer.LayerId));
            }
            RefreshPageAltitudeLayerControls(pageIndex, true);
            if (pageIndex == 0) RefreshLayerDropdown();
        }

        private void RefreshPageAltitudeLayerControls(int pageIndex, bool force = false)
        {
            RectTransform container = pageIndex == 1
                ? routeAltitudeLayerContainer
                : pageIndex == 2 ? simulationAltitudeLayerContainer : null;
            if (container == null) return;
            IReadOnlyList<AltitudeLayer> layers = CurrentLiveAltitudeLayers();
            string signature = string.Join("|", layers.Select(layer =>
                $"{layer.LayerId}:{RawLayerName(layer)}:{layer.NominalAltitude:R}:" +
                (IsPageAltitudeLayerVisible(pageIndex, layer.LayerId) ? "1" : "0")));
            if (!force && pageAltitudeLayerSignatures.TryGetValue(pageIndex, out string previous) &&
                string.Equals(previous, signature, StringComparison.Ordinal)) return;
            pageAltitudeLayerSignatures[pageIndex] = signature;
            DestroyUiChildren(container);
            if (layers.Count == 0)
            {
                Label(container, "No altitude layers are available", 11, Muted, 32f);
                return;
            }
            foreach (AltitudeLayer layer in layers)
            {
                AltitudeLayer captured = layer;
                string rawName = RawLayerName(captured);
                string rawLabel = $"{rawName} · {captured.NominalAltitude:0.##} m";
                Toggle toggle = ToggleField(container, rawLabel,
                    IsPageAltitudeLayerVisible(pageIndex, captured.LayerId),
                    visible =>
                    {
                        if (!SetPageAltitudeLayerVisible(pageIndex, captured.LayerId, visible)) return;
                        NoticeRawLayer(rawName, "Network altitude layer ",
                            visible ? ": Visible. " : ": Hidden. ");
                    });
                SetToggleRawLabel(toggle, rawLabel);
            }
        }

        private static string RawLayerName(AltitudeLayer layer) =>
            layer == null ? string.Empty :
            (string.IsNullOrWhiteSpace(layer.DisplayName) ? layer.LayerId : layer.DisplayName);

        private static void SetToggleRawLabel(Toggle toggle, string value)
        {
            if (toggle == null) return;
            Text label = toggle.GetComponentsInChildren<Text>(true).FirstOrDefault();
            if (label != null) label.text = value ?? string.Empty;
        }

        private void NoticeRawLayer(string rawLayerName, string localizedPrefix,
            string localizedSuffix)
        {
            // Translate only the fixed UI fragments. RefreshStatus treats the complete notice
            // as an already-rendered literal, so an authored name such as "Lower Network Layer"
            // or "Simulation" can never be reinterpreted by phrase replacement.
            notice = L(localizedPrefix) + (rawLayerName ?? string.Empty) + L(localizedSuffix);
            noticeUntil = Time.unscaledTime + 4.5f;
            if (footerText != null) RefreshStatus();
        }

        private void BuildHistoryAnalysisSurface()
        {
            RectTransform surface = Rect("HistoryAnalysisSurface", transform, Vector2.zero, Vector2.one,
                new Vector2(238f, 34f), new Vector2(-438f, -118f), Paper);
            historyAnalysisSurface = surface.gameObject;
            surface.gameObject.AddComponent<RectMask2D>();
            Outline outline = surface.gameObject.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);

            RectTransform header = Rect("AnalysisHeader", surface, new Vector2(0f, 1f), Vector2.one,
                new Vector2(0f, -76f), Vector2.zero, Surface);
            historyAnalysisTitle = Text("Historical Results Analysis", header, 22, FontStyle.Bold,
                TextAnchor.UpperLeft, Ink, new Vector2(24f, 13f), new Vector2(-190f, -8f));
            historyAnalysisSubtitle = Text("", header, 11, FontStyle.Normal,
                TextAnchor.LowerLeft, Muted, new Vector2(24f, 8f), new Vector2(-190f, -39f));
            RectTransform close = Rect("ReturnTo3DScene", header, Vector2.one, Vector2.one,
                new Vector2(-164f, -56f), new Vector2(-20f, -18f), InkSoft);
            MakeButton(close, HideHistoryAnalysisSurface, InkSoft);
            Text("Return to 3D Scene", close, 11, FontStyle.Bold, TextAnchor.MiddleCenter,
                Color.white, Vector2.zero, Vector2.zero);

            RectTransform tableRoot = Rect("MetricsTableMode", surface, Vector2.zero, Vector2.one,
                new Vector2(22f, 20f), new Vector2(-22f, -94f), Surface);
            historyTablePanel = tableRoot.gameObject;
            ScrollRect tableScroll = tableRoot.gameObject.AddComponent<ScrollRect>();
            tableScroll.horizontal = true;
            tableScroll.vertical = true;
            tableScroll.movementType = ScrollRect.MovementType.Clamped;
            tableScroll.scrollSensitivity = 32f;
            RectTransform tableToolbar = AnalysisToolbar(tableRoot, "TableViewerToolbar");
            historyTablePreviousButton = ToolbarButton(tableToolbar, "Previous Page",
                () => SetHistoryTablePage(historyTablePageIndex - 1), 104f);
            historyTablePageInput = Input(tableToolbar, "1/1", "Page");
            SetToolbarWidth(historyTablePageInput.gameObject, 72f);
            historyTablePageInput.onEndEdit.AddListener(_ => JumpToHistoryTablePage());
            historyTablePageTotalLabel = Label(tableToolbar, "/ 1", 11, Muted, 38f);
            SetToolbarWidth(historyTablePageTotalLabel.gameObject, 112f);
            ToolbarButton(tableToolbar, "Go", JumpToHistoryTablePage, 42f);
            historyTableNextButton = ToolbarButton(tableToolbar, "Next Page",
                () => SetHistoryTablePage(historyTablePageIndex + 1), 92f);
            ToolbarSpacer(tableToolbar);
            ToolbarButton(tableToolbar, "Zoom Out", () => ChangeTableZoom(-0.25f), 78f);
            historyTableZoomLabel = Label(tableToolbar, "100%", 11, InkSoft, 38f);
            SetToolbarWidth(historyTableZoomLabel.gameObject, 48f);
            ToolbarButton(tableToolbar, "Zoom In", () => ChangeTableZoom(0.25f), 72f);
            ToolbarButton(tableToolbar, "Reset View", ResetTableView, 86f);
            ToolbarButton(tableToolbar, "Export Image", ExportCurrentAnalysisImage, 104f);

            RectTransform viewport = Rect("MetricsViewport", tableRoot, Vector2.zero, Vector2.one,
                new Vector2(12f, 12f), new Vector2(-12f, -54f), Surface);
            historyTableViewport = viewport;
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            historyTableContent = Rect("ResultTableGrid", viewport, new Vector2(0f, 1f),
                new Vector2(0f, 1f), Vector2.zero, Vector2.zero, Transparent);
            historyTableContent.pivot = new Vector2(0f, 1f);
            tableScroll.viewport = viewport;
            tableScroll.content = historyTableContent;
            historyTableZoom = viewport.gameObject.AddComponent<SarZoomableViewport>();
            historyTableZoom.Configure(historyTableContent, tableScroll, new Vector2(0f, 1f), 1f, 4f, 0.25f);
            historyTableZoom.ZoomChanged += percent =>
                SetLocalizedText(historyTableZoomLabel, $"{percent}%");

            RectTransform chartRoot = Rect("HistoryChartMode", surface, Vector2.zero, Vector2.one,
                new Vector2(22f, 20f), new Vector2(-22f, -94f), Surface);
            historyChartPanel = chartRoot.gameObject;
            ScrollRect chartScroll = chartRoot.gameObject.AddComponent<ScrollRect>();
            chartScroll.horizontal = true;
            chartScroll.vertical = true;
            chartScroll.movementType = ScrollRect.MovementType.Clamped;
            chartScroll.scrollSensitivity = 30f;
            RectTransform chartToolbar = AnalysisToolbar(chartRoot, "ChartViewerToolbar");
            historyChartPreviousButton = ToolbarButton(chartToolbar, "Previous Figure",
                () => SetHistoryChartPage(historyChartPageIndex - 1), 104f);
            historyChartPageLabel = Label(chartToolbar, "Figure 1/1", 11, InkSoft, 72f);
            SetToolbarWidth(historyChartPageLabel.gameObject, 84f);
            historyChartNextButton = ToolbarButton(chartToolbar, "Next Figure",
                () => SetHistoryChartPage(historyChartPageIndex + 1), 92f);
            ToolbarSpacer(chartToolbar);
            ToolbarButton(chartToolbar, "Zoom Out", () => ChangeChartZoom(-0.25f), 78f);
            historyChartZoomLabel = Label(chartToolbar, "100%", 11, InkSoft, 38f);
            SetToolbarWidth(historyChartZoomLabel.gameObject, 48f);
            ToolbarButton(chartToolbar, "Zoom In", () => ChangeChartZoom(0.25f), 72f);
            ToolbarButton(chartToolbar, "Reset View", ResetChartView, 86f);
            ToolbarButton(chartToolbar, "Export Image", ExportCurrentAnalysisImage, 104f);
            historyChartCaption = Text("", chartRoot, 12, FontStyle.Normal, TextAnchor.UpperLeft,
                InkSoft, new Vector2(20f, -108f), new Vector2(-20f, -52f));
            // Keep the run/dimension caption in its own fixed band. A generated PNG can
            // now fill or zoom within the viewport without ever painting over this text.
            historyChartCaption.rectTransform.anchorMin = new Vector2(0f, 1f);
            historyChartCaption.rectTransform.anchorMax = Vector2.one;
            historyChartCaption.horizontalOverflow = HorizontalWrapMode.Wrap;
            historyChartCaption.verticalOverflow = VerticalWrapMode.Truncate;
            RectTransform generatedImageViewport = Rect("PythonChartViewport", chartRoot,
                Vector2.zero, Vector2.one, new Vector2(20f, 20f), new Vector2(-20f, -120f),
                Transparent);
            generatedImageViewport.gameObject.AddComponent<RectMask2D>();
            GameObject generatedImage = new GameObject("PythonGeneratedChart", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RawImage));
            generatedImage.transform.SetParent(generatedImageViewport, false);
            RectTransform generatedImageRect = (RectTransform)generatedImage.transform;
            generatedImageRect.anchorMin = new Vector2(0.5f, 0.5f);
            generatedImageRect.anchorMax = new Vector2(0.5f, 0.5f);
            generatedImageRect.anchoredPosition = Vector2.zero;
            generatedImageRect.sizeDelta = Vector2.zero;
            historyChartImage = generatedImage.GetComponent<RawImage>();
            historyChartImage.color = Color.white;
            historyChartImage.raycastTarget = true;
            generatedImage.AddComponent<AspectRatioFitter>().aspectMode =
                AspectRatioFitter.AspectMode.FitInParent;
            chartScroll.viewport = generatedImageViewport;
            chartScroll.content = generatedImageRect;
            historyChartZoom = generatedImageViewport.gameObject.AddComponent<SarZoomableViewport>();
            historyChartZoom.Configure(generatedImageRect, chartScroll,
                new Vector2(0.5f, 0.5f), 1f, 5f, 0.25f);
            historyChartZoom.ZoomChanged += percent =>
                SetLocalizedText(historyChartZoomLabel, $"{percent}%");
            generatedImage.SetActive(false);

            historyChartPanel.SetActive(false);
            historyAnalysisSurface.SetActive(false);
        }

        private void BuildHistoricalHeatmapLegend()
        {
            RectTransform panel = Rect("HistoricalHeatmapLegend", transform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(254f, -232f), new Vector2(614f, -132f),
                new Color(0.08f, 0.08f, 0.08f, 0.88f));
            historicalHeatmapLegend = panel.gameObject;
            Outline outline = panel.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.18f);
            outline.effectDistance = new Vector2(1f, -1f);
            historicalLaneLegendText = Text("Lane traversals  0 – 0", panel, 14,
                FontStyle.Bold, TextAnchor.MiddleLeft, Color.white,
                new Vector2(14f, 62f), new Vector2(-14f, -8f));
            historicalResourceLegendText = Text("Reservation time (s)  0 – 0", panel, 14,
                FontStyle.Bold, TextAnchor.MiddleLeft, Color.white,
                new Vector2(14f, 38f), new Vector2(-14f, -34f));

            RectTransform gradient = Rect("HeatmapGradient", panel, Vector2.zero, Vector2.one,
                new Vector2(14f, 12f), new Vector2(-14f, -76f), Transparent);
            HorizontalLayoutGroup layout = gradient.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 0f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = true;
            for (int index = 0; index < 48; index++)
            {
                RectTransform segment = Rect("GradientSegment", gradient, Vector2.zero, Vector2.one,
                    Vector2.zero, Vector2.zero,
                    index == 0
                        ? new Color(0.43f, 0.45f, 0.47f, 0.82f)
                        : RuntimeAirspaceVisualizer.SampleHeatmapColor((index - 1f) / 46f));
                segment.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
                segment.GetComponent<Image>().raycastTarget = false;
            }
            panel.gameObject.SetActive(false);
        }

        private void RefreshHistoricalHeatmapLegend()
        {
            if (historicalHeatmapLegend == null || visualizer == null) return;
            bool visible = workbenchVisible && currentPageIndex == 3 &&
                           (historyAnalysisSurface == null || !historyAnalysisSurface.activeSelf) &&
                           visualizer.HistoricalModeActive &&
                           visualizer.IsVisible(AirspaceVisualLayer.Heatmap);
            historicalHeatmapLegend.SetActive(visible);
            if (!visible) return;
            SetLocalizedText(historicalLaneLegendText,
                $"Lane traversals  0 – {visualizer.HistoricalPeakLaneLoad:0.##}");
            SetLocalizedText(historicalResourceLegendText,
                $"Reservation time (s)  0 – {visualizer.HistoricalPeakResourceLoad:0.##}");
            historicalResourceLegendText.gameObject.SetActive(
                visualizer.HistoricalDrawableResourceCount > 0);
        }

        private RectTransform Page(string name, Transform parent, string title, string subtitle, out RectTransform root)
        {
            root = Rect(name, parent, Vector2.zero, Vector2.one, new Vector2(1f, 0f), Vector2.zero, Paper);
            ScrollRect scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.scrollSensitivity = 28f;
            RectTransform viewport = Rect("Viewport", root, Vector2.zero, Vector2.one,
                new Vector2(18f, 16f), new Vector2(-12f, -14f), Paper);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            RectTransform content = Rect("Content", viewport, new Vector2(0f, 1f), Vector2.one,
                Vector2.zero, Vector2.zero, Transparent);
            content.pivot = new Vector2(0.5f, 1f);
            VerticalLayoutGroup layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 14, 14, 24);
            layout.spacing = 9f;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport;
            scroll.content = content;
            Label(content, title, 25, Ink, 38f, FontStyle.Bold);
            Label(content, subtitle, 12, Muted, 26f);
            RectTransform divider = Rect("Divider", content, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Border);
            divider.gameObject.AddComponent<LayoutElement>().preferredHeight = 1f;
            return content;
        }

        private void Section(Transform parent, string title, string subtitle)
        {
            RectTransform spacer = Rect("SectionSpacing", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Transparent);
            spacer.gameObject.AddComponent<LayoutElement>().preferredHeight = 5f;
            Label(parent, title, 14, Ink, 26f, FontStyle.Bold);
            Label(parent, subtitle, 11, Muted, 23f);
        }

        private void LayerRow(Transform parent,
            (AirspaceVisualLayer layer, string label) left,
            (AirspaceVisualLayer layer, string label) right)
        {
            Button leftButton = null;
            Button rightButton = null;
            RowButtons(parent,
                ("● " + left.label, () => ToggleLayer(left.layer), visualizer.IsVisible(left.layer) ? AccentSoft : SurfaceSoft),
                ("● " + right.label, () => ToggleLayer(right.layer), visualizer.IsVisible(right.layer) ? AccentSoft : SurfaceSoft),
                (a, b) => { leftButton = a; rightButton = b; });
            RegisterLayerButton(left.layer, leftButton);
            RegisterLayerButton(right.layer, rightButton);
        }

        private void RegisterLayerButton(AirspaceVisualLayer layer, Button button)
        {
            if (!layerButtons.TryGetValue(layer, out List<Button> buttons))
                layerButtons[layer] = buttons = new List<Button>();
            buttons.Add(button);
        }

        private void BuildGlobalLayerControls(Transform sidebar)
        {
            TopText(sidebar, "VISUAL LAYERS", -390f, -364f, 12, new Color(1f, 1f, 1f, 0.52f));
            SidebarLayerRow(sidebar, -402f,
                (AirspaceVisualLayer.City, "City Buildings"), (AirspaceVisualLayer.Network, "Air Network"));
            SidebarLayerRow(sidebar, -440f,
                (AirspaceVisualLayer.Approach, "Ground Approach"), (AirspaceVisualLayer.Holding, "Holding Structures"));
            SidebarLayerRow(sidebar, -478f,
                (AirspaceVisualLayer.Facilities, "Vertiports"), (AirspaceVisualLayer.Corridors, "Vertiport Corridors"));
            SidebarLayerRow(sidebar, -516f,
                (AirspaceVisualLayer.LayerConnectors, "Interlayer Corridors"), (AirspaceVisualLayer.Signals, "Intersection Signals"));
        }

        private void SidebarLayerRow(Transform parent, float top,
            (AirspaceVisualLayer layer, string label) left,
            (AirspaceVisualLayer layer, string label) right)
        {
            SidebarLayerButton(parent, top, false, left.layer, left.label);
            SidebarLayerButton(parent, top, true, right.layer, right.label);
        }

        private void SidebarLayerButton(Transform parent, float top, bool right,
            AirspaceVisualLayer layer, string label)
        {
            Vector2 anchorMin = right ? new Vector2(0.5f, 1f) : new Vector2(0f, 1f);
            Vector2 anchorMax = right ? Vector2.one : new Vector2(0.5f, 1f);
            Vector2 offsetMin = new Vector2(right ? 4f : 14f, top - 32f);
            Vector2 offsetMax = new Vector2(right ? -14f : -4f, top);
            Color color = visualizer.IsVisible(layer) ? Accent : InkSoft;
            RectTransform rect = Rect("Layer_" + label, parent, anchorMin, anchorMax, offsetMin, offsetMax, color);
            Button button = MakeButton(rect, () => ToggleLayer(layer), color);
            Text("● " + label, rect, 10, FontStyle.Normal, TextAnchor.MiddleCenter, Color.white,
                new Vector2(4f, 0f), new Vector2(-4f, 0f));
            RegisterLayerButton(layer, button);
            sidebarLayerButtons.Add(button);
        }

        private void ToolRow(Transform parent, RuntimeEditTool left, RuntimeEditTool right)
        {
            Button leftButton = null;
            Button rightButton = null;
            RowButtons(parent,
                (RuntimeAirspaceEditor.ToolName(left), () => SetEditorTool(left), SurfaceSoft),
                (RuntimeAirspaceEditor.ToolName(right), () => SetEditorTool(right), SurfaceSoft),
                (a, b) => { leftButton = a; rightButton = b; });
            RegisterToolButton(left, leftButton);
            RegisterToolButton(right, rightButton);
        }

        private Button ToolButton(Transform parent, RuntimeEditTool tool)
        {
            Button button = FullButton(parent, RuntimeAirspaceEditor.ToolName(tool),
                () => SetEditorTool(tool), SurfaceSoft);
            RegisterToolButton(tool, button);
            return button;
        }

        private void ToolActionRow(Transform parent, RuntimeEditTool tool, string actionLabel, Action action)
        {
            Button toolButton = null;
            RowButtons(parent,
                (RuntimeAirspaceEditor.ToolName(tool), () => SetEditorTool(tool), SurfaceSoft),
                (actionLabel, action, SurfaceSoft),
                (left, _) => toolButton = left);
            RegisterToolButton(tool, toolButton);
        }

        private void WorkspaceRow(Transform parent, AirspaceEditWorkspace left, AirspaceEditWorkspace right)
        {
            Button leftButton = null;
            Button rightButton = null;
            RowButtons(parent,
                (RuntimeAirspaceEditor.WorkspaceName(left), () => ShowEditorWorkspace(left), SurfaceSoft),
                (RuntimeAirspaceEditor.WorkspaceName(right), () => ShowEditorWorkspace(right), SurfaceSoft),
                (a, b) => { leftButton = a; rightButton = b; });
            workspaceButtons[left] = leftButton;
            workspaceButtons[right] = rightButton;
        }

        private RectTransform EditorPanel(Transform parent, string name)
        {
            RectTransform panel = Rect(name, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Transparent);
            VerticalLayoutGroup layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 9f;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            ContentSizeFitter fitter = panel.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return panel;
        }

        private RectTransform CollapsibleSection(Transform parent, string name, string title,
            bool expandedByDefault)
        {
            RectTransform section = EditorPanel(parent, name);
            Button header = FullButton(section, string.Empty, null, SurfaceSoft);
            header.gameObject.name = name + "Header";
            Text headerLabel = header.GetComponentInChildren<Text>(true);
            RectTransform body = EditorPanel(section, name + "Body");
            bool expanded = expandedByDefault;

            void ApplyState()
            {
                body.gameObject.SetActive(expanded);
                SetLocalizedText(headerLabel, (expanded ? "▾  " : "▸  ") + title);
                if (section.parent is RectTransform parentRect)
                    LayoutRebuilder.MarkLayoutForRebuild(parentRect);
            }

            header.onClick.AddListener(() =>
            {
                expanded = !expanded;
                ApplyState();
            });
            ApplyState();
            return body;
        }

        private void ActiveVertiportDropdown(Transform parent)
        {
            Label(parent, "Active Vertiport", 12, Muted, 24f, FontStyle.Bold);
            Dropdown dropdown = Dropdown(parent, manager.Vertiports.Keys.OrderBy(id => id), value =>
            {
                editor.SetActiveVertiport(value);
                RefreshVertiportDropdowns();
            });
            activeVertiportDropdowns.Add(dropdown);
            SetDropdownValue(dropdown, editor.ActiveVertiportId);
        }

        private void ShowGroundFacilityFlow(GroundFacilityEditFlow flow, bool notify = true)
        {
            groundFacilityEditFlow = flow;
            if (groundWorkflowPanels != null)
                for (int i = 0; i < groundWorkflowPanels.Length; i++)
                    if (groundWorkflowPanels[i] != null)
                        groundWorkflowPanels[i].SetActive(i == (int)flow);
            SetButtonColor(modifyVertiportButton,
                flow == GroundFacilityEditFlow.ModifyExisting ? Accent : SurfaceSoft);
            SetButtonColor(createVertiportButton,
                flow == GroundFacilityEditFlow.CreateNew ? Accent : SurfaceSoft);
            if (editor != null && editor.Workspace == AirspaceEditWorkspace.GroundFacility)
                editor.SetTool(flow == GroundFacilityEditFlow.CreateNew
                    ? RuntimeEditTool.DrawVertiportBoundary
                    : RuntimeEditTool.Inspect, false);
            if (notify)
                Notice(flow == GroundFacilityEditFlow.CreateNew
                    ? "Create New Vertiport workflow opened. Draw the boundary first. "
                    : "Edit Existing Vertiport workflow opened. ");
        }

        private void FinishNewVertiportBoundary()
        {
            if (!editor.FinishVertiportBoundary()) return;
            RefreshVertiportDropdowns();
            ShowGroundFacilityFlow(GroundFacilityEditFlow.ModifyExisting, false);
            Notice($"Vertiport {editor.ActiveVertiportId} created. Continue with helipad and approach-network design. ");
        }

        private void RefreshVertiportDropdowns()
        {
            if (editor == null || manager == null) return;
            List<string> vertiports = manager.Vertiports.Keys.OrderBy(id => id).ToList();
            foreach (Dropdown dropdown in activeVertiportDropdowns)
            {
                SetDropdownOptions(dropdown, vertiports);
                SetDropdownValue(dropdown, editor.ActiveVertiportId);
            }

            if (groundHelipadDropdown != null)
            {
                List<string> pads = manager.Vertiports.TryGetValue(editor.ActiveVertiportId ?? string.Empty,
                        out Vertiport vertiport)
                    ? (vertiport.Helipads ?? new List<Helipad>()).Where(pad => pad != null)
                        .Select(pad => pad.HelipadId).OrderBy(id => id).ToList()
                    : new List<string>();
                SetDropdownOptions(groundHelipadDropdown, pads);
                if (!pads.Contains(editor.SelectedHelipadId) && pads.Count > 0)
                    editor.SetActiveHelipad(pads[0]);
                SetDropdownValue(groundHelipadDropdown, editor.SelectedHelipadId);
            }
            RefreshRouteEndpointDropdowns();
            if (helipadShapeDropdown != null)
                SetDropdownValue(helipadShapeDropdown,
                    editor.HelipadShape == RuntimeHelipadShape.Circle ? "Circle" :
                    editor.HelipadShape == RuntimeHelipadShape.Polygon ? "Freehand Polygon" : "Rectangle");
        }

        private void RefreshRouteEndpointDropdowns()
        {
            if (manager == null) return;
            List<string> pads = manager.Helipads.Keys.OrderBy(id => id).ToList();
            SetDropdownOptions(originDropdown, pads);
            SetDropdownOptions(destinationDropdown, pads);
            if (originDropdown != null)
            {
                originDropdown.interactable = pads.Count > 0;
                SetDropdownValue(originDropdown, routeDesigner?.OriginHelipadId);
            }
            if (destinationDropdown != null)
            {
                destinationDropdown.interactable = pads.Count > 0;
                SetDropdownValue(destinationDropdown, routeDesigner?.DestinationHelipadId);
            }
        }

        private void RefreshHoldingHierarchy()
        {
            if (holdingHierarchyContainer == null || editor == null || manager == null) return;
            foreach (Button old in holdingHierarchyButtons)
                if (old != null)
                {
                    old.gameObject.SetActive(false);
                    DestroyUiObject(old.gameObject);
                }
            holdingHierarchyButtons.Clear();

            if (!manager.Vertiports.TryGetValue(editor.ActiveVertiportId ?? string.Empty, out Vertiport vertiport) ||
                vertiport?.HoldingStructures == null || vertiport.HoldingStructures.Count == 0)
            {
                AddHoldingHierarchyButton("No holding-structure anchor has been placed", 0, null, false);
                return;
            }

            foreach (HoldingStructure holding in vertiport.HoldingStructures.Where(item => item != null))
            {
                string holdingId = holding.HoldingStructureId;
                bool holdingExpanded = expandedHoldingStructures.Contains(holdingId);
                AddHoldingHierarchyButton($"{(holdingExpanded ? "▼" : "▶")}  Anchor  {holdingId}", 0, () =>
                {
                    if (!expandedHoldingStructures.Add(holdingId)) expandedHoldingStructures.Remove(holdingId);
                    editor.SelectHoldingAnchor(holdingId);
                }, editor.HoldingSelectionKind == RuntimeHoldingSelectionKind.Anchor &&
                   editor.SelectedHoldingStructureId == holdingId);
                if (!holdingExpanded) continue;

                foreach (Circle circle in (holding.Circles ?? new List<Circle>()).Where(item => item != null))
                {
                    string circleId = circle.CircleId;
                    bool circleExpanded = expandedHoldingCircles.Contains(circleId);
                    AddHoldingHierarchyButton($"{(circleExpanded ? "▼" : "▶")}  Holding Ring  {circleId}", 1, () =>
                    {
                        if (!expandedHoldingCircles.Add(circleId)) expandedHoldingCircles.Remove(circleId);
                        editor.SelectHoldingCircle(circleId);
                    }, editor.HoldingSelectionKind == RuntimeHoldingSelectionKind.Circle &&
                       editor.SelectedHoldingCircleId == circleId);
                    if (!circleExpanded) continue;

                    foreach (string vertexId in circle.CoveredVertices ?? new List<string>())
                    {
                        string id = vertexId;
                        AddHoldingHierarchyButton($"●  Vertex  {id}", 2,
                            () => editor.SelectHoldingVertex(id),
                            editor.HoldingSelectionKind == RuntimeHoldingSelectionKind.Vertex &&
                            editor.SelectedHoldingVertexId == id);
                    }
                }

                foreach (Ramp ramp in (holding.Ramps ?? new List<Ramp>()).Where(item => item != null))
                {
                    string rampId = ramp.RampId;
                    AddHoldingHierarchyButton($"→  One-Way Ramp  {ramp.FromVertexId} → {ramp.ToVertexId}", 1,
                        () => editor.SelectHoldingRamp(rampId),
                        editor.HoldingSelectionKind == RuntimeHoldingSelectionKind.Ramp &&
                        editor.SelectedHoldingRampId == rampId);
                }
            }
        }

        private void AddHoldingHierarchyButton(string label, int depth, Action action, bool selected)
        {
            Button button = Button(label, holdingHierarchyContainer, action, selected ? AccentSoft : SurfaceSoft);
            button.interactable = action != null;
            button.gameObject.AddComponent<LayoutElement>().preferredHeight = 36f;
            Text text = button.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                text.alignment = TextAnchor.MiddleLeft;
                text.fontStyle = selected ? FontStyle.Bold : FontStyle.Normal;
                text.rectTransform.offsetMin = new Vector2(12f + depth * 18f, 0f);
                text.rectTransform.offsetMax = new Vector2(-8f, 0f);
            }
            holdingHierarchyButtons.Add(button);
        }

        private void ShowEditorWorkspace(AirspaceEditWorkspace workspace, bool notify = true)
        {
            if (editWorkspacePanels != null)
                for (int i = 0; i < editWorkspacePanels.Length; i++)
                    if (editWorkspacePanels[i] != null) editWorkspacePanels[i].SetActive(i == (int)workspace);
            editor.SetWorkspace(workspace, notify);
            if (workspace == AirspaceEditWorkspace.GroundFacility &&
                groundFacilityEditFlow == GroundFacilityEditFlow.CreateNew)
                editor.SetTool(RuntimeEditTool.DrawVertiportBoundary, false);
            visualizer.EditorOverlayVisible = true;
            RefreshEditorState();
        }

        private void SetEditorTool(RuntimeEditTool tool)
        {
            editor.SetTool(tool);
            RefreshEditorState();
        }

        private void RegisterToolButton(RuntimeEditTool tool, Button button)
        {
            if (button == null) return;
            if (!toolButtons.TryGetValue(tool, out List<Button> buttons))
            {
                buttons = new List<Button>();
                toolButtons[tool] = buttons;
            }
            buttons.Add(button);
        }

        private void RefreshEditorState()
        {
            bool networkLocked = editor.Workspace == AirspaceEditWorkspace.NetworkLayers &&
                                 editor.ActiveLayerIsFrozen;
            foreach (var pair in workspaceButtons)
                SetButtonColor(pair.Value, pair.Key == editor.Workspace ? Accent : SurfaceSoft);
            foreach (var pair in toolButtons)
                foreach (Button button in pair.Value)
                {
                    button.interactable = !(networkLocked && IsNetworkMutationTool(pair.Key));
                    SetButtonColor(button, pair.Key == editor.Tool ? Accent : SurfaceSoft);
                }
            if (layerNameInput != null) layerNameInput.interactable = !networkLocked;
            if (altitudeInput != null) altitudeInput.interactable = !networkLocked;
            RefreshLayerDropdown();
            RefreshVertiportDropdowns();
            RefreshHoldingHierarchy();
            RefreshSceneViewButtons();
            if (networkSelectionDetails != null)
                SetLocalizedText(networkSelectionDetails, editor.GetSelectionDescription());
            if (holdingSelectionDetails != null)
                SetLocalizedText(holdingSelectionDetails, editor.GetHoldingSelectionDescription());
            if (corridorSelectionDetails != null)
                SetLocalizedText(corridorSelectionDetails, editor.GetCorridorSelectionDescription());
            if (tallBuildingHighlightButton != null)
                SetButtonColor(tallBuildingHighlightButton,
                    visualizer.TallBuildingHighlightEnabled ? Accent : SurfaceSoft);
            RefreshCorridorModeButtons();
            RefreshCorridorTypeButtons();
        }

        private static bool IsNetworkMutationTool(RuntimeEditTool tool)
        {
            return tool == RuntimeEditTool.AddNetworkNode ||
                   tool == RuntimeEditTool.ConnectNetworkNodes ||
                   tool == RuntimeEditTool.AddNetworkWaypoint ||
                   tool == RuntimeEditTool.MoveNetworkNode ||
                   tool == RuntimeEditTool.MoveNetworkWaypoint;
        }

        private void HandleEditorStateChanged()
        {
            if (currentPageIndex == 0 && editor != null && visualizer != null)
            {
                Dictionary<string, bool> state = PageAltitudeLayerState(0);
                foreach (AltitudeLayer layer in editor.Layers)
                    if (layer != null && !string.IsNullOrWhiteSpace(layer.LayerId))
                        state[layer.LayerId] = visualizer.IsPerLayerVisible(layer.LayerId);
            }
            PruneTransientAltitudeLayerVisibility();
            pageAltitudeLayerSignatures.Clear();
            RefreshPageAltitudeLayerControls(1, true);
            RefreshPageAltitudeLayerControls(2, true);
            RefreshTrafficHeatmapLayerControls(true);
            RefreshEditorState();
        }

        private void PruneTransientAltitudeLayerVisibility()
        {
            var liveLayerIds = new HashSet<string>((editor?.Layers ?? Array.Empty<AltitudeLayer>())
                .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId))
                .Select(layer => layer.LayerId), StringComparer.Ordinal);
            var trafficLayerIds = new HashSet<string>(CurrentTrafficHeatmapLayers()
                .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId))
                .Select(layer => layer.LayerId), StringComparer.Ordinal);

            foreach (KeyValuePair<int, Dictionary<string, bool>> page in pageAltitudeLayerVisibility)
            {
                HashSet<string> validIds = page.Key == 3 ? trafficLayerIds : liveLayerIds;
                foreach (string staleId in page.Value.Keys.Where(id => !validIds.Contains(id)).ToList())
                    page.Value.Remove(staleId);
            }

            if (visualizer == null) return;
            visualizer.ResetDisplayLayerVisibility();
            HashSet<string> currentIds = currentPageIndex == 3 ? trafficLayerIds : liveLayerIds;
            foreach (KeyValuePair<string, bool> pair in PageAltitudeLayerState(currentPageIndex))
                if (currentIds.Contains(pair.Key))
                    visualizer.SetDisplayLayerVisible(pair.Key, pair.Value);

            // Analytics has a second filter for heat values.  Keep it independent from
            // the display mask and from live editing: when an archived result is loaded,
            // CurrentTrafficHeatmapLayers returns the archived IDs instead of editor IDs.
            visualizer.ResetHeatmapLayerVisibility();
            foreach (KeyValuePair<string, bool> pair in PageAltitudeLayerState(3))
                if (trafficLayerIds.Contains(pair.Key))
                    visualizer.SetHeatmapLayerVisible(pair.Key, pair.Value);
        }

        private void ShowCorridorEditMode(RuntimeCorridorEditMode mode, bool notify = true)
        {
            if (corridorModePanels != null)
                for (int index = 0; index < corridorModePanels.Length; index++)
                    if (corridorModePanels[index] != null)
                        corridorModePanels[index].SetActive(index == (int)mode);
            editor.SetCorridorEditMode(mode, notify);
            RefreshCorridorModeButtons();
            RefreshCorridorTypeButtons();
        }

        private void RefreshCorridorModeButtons()
        {
            if (editor == null) return;
            SetButtonColor(facilityCorridorModeButton,
                editor.CorridorEditMode == RuntimeCorridorEditMode.Facility ? Accent : SurfaceSoft);
            SetButtonColor(layerCorridorModeButton,
                editor.CorridorEditMode == RuntimeCorridorEditMode.Layer ? Accent : SurfaceSoft);
        }

        private void SetCorridorType(string value)
        {
            editor.CorridorType = value == "Exit" ? "Exit" : "Entry";
            RefreshCorridorTypeButtons();
            Notice($"Vertiport-corridor direction: {L(editor.CorridorType == "Exit" ? "Departure" : "Arrival")}. ");
        }

        private void RefreshCorridorTypeButtons()
        {
            if (editor == null) return;
            if (entryCorridorButton != null)
                SetButtonColor(entryCorridorButton, editor.CorridorType == "Entry" ? Accent : SurfaceSoft);
            if (exitCorridorButton != null)
                SetButtonColor(exitCorridorButton, editor.CorridorType == "Exit" ? Accent : SurfaceSoft);
        }

        private void RefreshLayerDropdown()
        {
            if (layerStackContainer == null || editor == null || refreshingLayerDropdown) return;
            refreshingLayerDropdown = true;
            try
            {
                List<AltitudeLayer> layers = editor.Layers.ToList();
                bool rebuild = layerStackRows.Count != layers.Count || layerStackButtons.Count != layers.Count;
                if (!rebuild)
                    for (int i = 0; i < layers.Count; i++)
                        if (layerStackButtons[i] == null || layerStackButtons[i].gameObject.name != layers[i].LayerId)
                            rebuild = true;
                if (rebuild)
                {
                    // Rebuilding from the hierarchy, rather than only from the cached lists,
                    // also removes rows left by an interrupted or re-entrant refresh.
                    for (int i = layerStackContainer.childCount - 1; i >= 0; i--)
                    {
                        GameObject old = layerStackContainer.GetChild(i).gameObject;
                        old.SetActive(false);
                        DestroyUiObject(old);
                    }
                    layerStackRows.Clear();
                    layerStackButtons.Clear();
                    layerVisibilityButtons.Clear();
                    layerFreezeButtons.Clear();
                    foreach (AltitudeLayer layer in layers)
                    {
                        string id = layer.LayerId;
                        RectTransform row = Rect(id + "_row", layerStackContainer, Vector2.zero, Vector2.one,
                            Vector2.zero, Vector2.zero, Transparent);
                        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 38f;

                        RectTransform selectRect = Rect(id, row, Vector2.zero, Vector2.one,
                            Vector2.zero, new Vector2(-82f, 0f), SurfaceSoft);
                        Button button = MakeButton(selectRect, () => editor.SetActiveLayer(id), SurfaceSoft);
                        button.gameObject.name = id;
                        Text("", selectRect, 12, FontStyle.Bold, TextAnchor.MiddleLeft, Ink,
                            new Vector2(10f, 0f), new Vector2(-8f, 0f));
                        button.gameObject.AddComponent<SarLayerDragItem>().Configure(
                            () => editor.SetActiveLayer(id), ResolveLayerDropIndex,
                            targetIndex =>
                            {
                                if (editor.ActiveLayerId != id) editor.SetActiveLayer(id);
                                editor.ReorderActiveLayer(targetIndex);
                                RefreshLayerDropdown();
                            },
                            () => !editor.IsLayerFrozen(id), row);

                        RectTransform visibilityRect = Rect("LayerVisibility_" + id, row, Vector2.one, Vector2.one,
                            new Vector2(-78f, -34f), new Vector2(-44f, 0f), SurfaceSoft);
                        Button visibilityButton = MakeButton(visibilityRect, () =>
                        {
                            bool visible = !editor.IsLayerVisible(id);
                            if (editor.SetLayerVisible(id, visible))
                                PageAltitudeLayerState(0)[id] = visible;
                            RefreshLayerDropdown();
                        }, SurfaceSoft);
                        Text("◎", visibilityRect, 18, FontStyle.Bold, TextAnchor.MiddleCenter, Ink,
                            Vector2.zero, Vector2.zero);

                        RectTransform freezeRect = Rect("LayerFreeze_" + id, row, Vector2.one, Vector2.one,
                            new Vector2(-38f, -34f), new Vector2(-4f, 0f), SurfaceSoft);
                        Button freezeButton = MakeButton(freezeRect, () =>
                        {
                            editor.SetLayerFrozen(id, !editor.IsLayerFrozen(id));
                            RefreshLayerDropdown();
                        }, SurfaceSoft);
                        Text("❄", freezeRect, 17, FontStyle.Bold, TextAnchor.MiddleCenter, Ink,
                            Vector2.zero, Vector2.zero);

                        layerStackRows.Add(row);
                        layerStackButtons.Add(button);
                        layerVisibilityButtons.Add(visibilityButton);
                        layerFreezeButtons.Add(freezeButton);
                    }
                }
                for (int i = 0; i < layers.Count; i++)
                {
                    AltitudeLayer layer = layers[i];
                    Button button = layerStackButtons[i];
                    Text label = button.GetComponentInChildren<Text>(true);
                    if (label != null)
                        label.text = $"≡  {(layer.LayerId == editor.ActiveLayerId ? "●" : "○")}  " +
                                     RuntimeAirspaceEditor.LayerLabel(layer);
                    SetButtonColor(button, layer.LayerId == editor.ActiveLayerId ? Accent : SurfaceSoft);

                    Button visibilityButton = layerVisibilityButtons[i];
                    Text visibilityLabel = visibilityButton.GetComponentInChildren<Text>(true);
                    bool layerVisible = editor.IsLayerVisible(layer.LayerId);
                    if (visibilityLabel != null)
                    {
                        SetLocalizedText(visibilityLabel, layerVisible ? "◎" : "—");
                        visibilityLabel.color = layerVisible ? Color.white : Ink;
                    }
                    SetButtonColor(visibilityButton, layerVisible ? Teal : SurfaceSoft);

                    Button freezeButton = layerFreezeButtons[i];
                    SetButtonColor(freezeButton, layer.Frozen ? Accent : SurfaceSoft);
                    Text freezeLabel = freezeButton.GetComponentInChildren<Text>(true);
                    if (freezeLabel != null)
                    {
                        SetLocalizedText(freezeLabel, "❄");
                        freezeLabel.color = layer.Frozen ? Color.white : Ink;
                    }
                }
                if (altitudeInput != null && editor.ActiveLayer != null)
                    altitudeInput.text = editor.ActiveLayer.NominalAltitude.ToString("F0");
                if (layerNameInput != null && editor.ActiveLayer != null)
                    layerNameInput.text = editor.ActiveLayer.DisplayName ?? editor.ActiveLayer.LayerId;
                if (layerStatusText != null)
                    layerStatusText.text = L("Active: ") + RuntimeAirspaceEditor.LayerLabel(editor.ActiveLayer) +
                                           "\n" + L("Layers: ") + layers.Count + " · " +
                                           L(editor.ActiveLayerIsVisible ? "Visible" : "Hidden") + " · " +
                                           L(editor.ActiveLayerIsFrozen ? "Frozen" : "Editable");
            }
            finally
            {
                refreshingLayerDropdown = false;
            }
        }

        private int ResolveLayerDropIndex(Vector2 screenPosition, RectTransform draggedRow)
        {
            List<RectTransform> stationaryRows = layerStackRows
                .Where(row => row != null && row != draggedRow).ToList();
            if (stationaryRows.Count == 0) return 0;
            for (int i = 0; i < stationaryRows.Count; i++)
            {
                RectTransform row = stationaryRows[i];
                float centerY = RectTransformUtility.WorldToScreenPoint(null,
                    row.position).y;
                if (screenPosition.y > centerY) return i;
            }
            return stationaryRows.Count;
        }

        private void AddNetworkLayer()
        {
            editor.AddLayer();
            RefreshLayerDropdown();
        }

        private void DuplicateNetworkLayer()
        {
            editor.DuplicateActiveLayer();
            RefreshLayerDropdown();
        }

        private void DeleteNetworkLayer()
        {
            editor.DeleteActiveLayer();
            RefreshLayerDropdown();
        }

        private void ApplyNetworkLayerName()
        {
            if (string.IsNullOrWhiteSpace(layerNameInput?.text))
            {
                Notice("Layer name cannot be empty; the previous name was restored. ");
                if (layerNameInput != null)
                    layerNameInput.SetTextWithoutNotify(editor.ActiveLayer?.DisplayName ?? string.Empty);
                return;
            }
            editor.RenameActiveLayer(layerNameInput?.text);
            RefreshLayerDropdown();
        }

        private void ApplyNetworkLayerAltitude()
        {
            float fallback = editor.ActiveLayer?.NominalAltitude ?? 75f;
            if (!TryReadFloat(altitudeInput, "layer altitude", 0f, 10000f, fallback, out float altitude)) return;
            editor.SetActiveLayerAltitude(altitude);
            RefreshLayerDropdown();
        }

        private void MoveNetworkLayer(int direction)
        {
            editor.MoveActiveLayer(direction);
            RefreshLayerDropdown();
        }

        private void ToggleTallBuildingHighlight()
        {
            bool enabled = !visualizer.TallBuildingHighlightEnabled;
            if (!TryReadFloat(buildingHeightInput, "building-height threshold", 0f, 10000f,
                    visualizer.TallBuildingHeightThreshold, out float threshold)) return;
            int count = visualizer.SetTallBuildingHighlight(threshold, enabled);
            SetButtonColor(tallBuildingHighlightButton, enabled ? Accent : SurfaceSoft);
            Notice(enabled
                ? $"Highlighted {count} buildings taller than {threshold:F0} m. "
                : "Tall-building highlighting disabled. ");
        }

        private void ApplyTallBuildingThreshold()
        {
            float fallback = visualizer.TallBuildingHeightThreshold;
            if (!TryReadFloat(buildingHeightInput, "building-height threshold", 0f, 10000f, fallback,
                    out float threshold)) return;
            int count = visualizer.SetTallBuildingHighlight(threshold, visualizer.TallBuildingHighlightEnabled);
            Notice(visualizer.TallBuildingHighlightEnabled
                ? $"Height threshold updated to {threshold:F0} m; {count} buildings are highlighted. "
                : $"Height threshold updated to {threshold:F0} m; enable highlighting to apply it. ");
        }

        private bool ApplyHoldingAltitude()
        {
            if (!TryReadFloat(holdingAltitudeInput, "holding-structure altitude", 0f, 10000f,
                    editor.EditingAltitude, out float altitude)) return false;
            editor.SetHoldingAltitude(altitude);
            return true;
        }

        private bool ApplyHoldingSettings()
        {
            if (!ApplyHoldingAltitude()) return false;
            if (!TryReadFloat(holdingRadiusInput, "holding-ring radius", 0.5f, 10000f,
                    editor.HoldingRingRadius, out float radius) ||
                !TryReadInt(holdingVertexCountInput, "holding-ring vertex count", 2, 64,
                    editor.HoldingRingVertexCount, out int vertexCount) ||
                !TryReadFloat(holdingInitialAngleInput, "holding-ring initial angle", -36000f, 36000f,
                    editor.HoldingRingInitialAngle, out float initialAngle)) return false;
            editor.ConfigureHoldingRing(
                radius,
                vertexCount,
                initialAngle,
                holdingRampDirection);
            Notice("Holding-ring parameters updated automatically. ");
            return true;
        }

        private bool ApplyHelipadSettings(RuntimeHelipadShape shape)
        {
            if (!TryReadFloat(helipadWidthInput, "helipad rectangle width", 1f, 10000f,
                    editor.HelipadWidth, out float width) ||
                !TryReadFloat(helipadDepthInput, "helipad rectangle depth", 1f, 10000f,
                    editor.HelipadDepth, out float depth) ||
                !TryReadFloat(helipadRadiusInput, "helipad circle radius", 0.5f, 10000f,
                    editor.HelipadRadius, out float radius) ||
                !TryReadFloat(helipadRotationInput, "helipad rotation angle", -36000f, 36000f,
                    editor.HelipadRotationDegrees, out float rotation)) return false;
            editor.ConfigureHelipad(shape,
                width, depth, radius, rotation);
            Notice($"Helipad parameters updated automatically: {L(shape == RuntimeHelipadShape.Rectangle ? "Rectangle" : shape == RuntimeHelipadShape.Circle ? "Circle" : "Freehand Polygon")}. ");
            return true;
        }

        private void ToggleLayer(AirspaceVisualLayer layer)
        {
            if (layer == AirspaceVisualLayer.Heatmap)
            {
                // The Heatmap visual layer is reserved for archived analysis. Live
                // lane lines use the explicit three-way mode on the Simulation page.
                if (currentPageIndex == 3)
                    SetDataVisualizationHeatmapEnabled(!dataVisualizationHeatmapEnabled, true);
                else if (currentPageIndex == 2)
                    Notice("Use Runtime Lane Coloring to choose lane-type, live-load, or passability colors. ");
                return;
            }
            bool value = visualizer.Toggle(layer);
            RefreshLayerButtonVisual(layer, value);
            Notice($"{L(LayerName(layer))}: {(value ? "Visible" : "Hidden")}. ");
        }

        private void RefreshLayerButtonVisual(AirspaceVisualLayer layer, bool value)
        {
            if (layerButtons.TryGetValue(layer, out List<Button> buttons))
                foreach (Button button in buttons)
                    SetButtonColor(button, sidebarLayerButtons.Contains(button)
                        ? (value ? Accent : InkSoft)
                        : (value ? AccentSoft : SurfaceSoft));
        }

        private void BuildViewGizmo()
        {
            viewGizmo = Rect("ViewOrientationControl", transform, Vector2.one, Vector2.one,
                new Vector2(-616f, -292f), new Vector2(-458f, -130f), new Color(0.17f, 0.16f, 0.15f, 0.88f));
            viewGizmoBackground = viewGizmo.GetComponent<Image>();
            RegisterViewContextClick(viewGizmo.gameObject);

            var cubeObject = new GameObject("OrientationCubeAndCones", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RuntimeOrientationCube));
            cubeObject.transform.SetParent(viewGizmo, false);
            RectTransform cubeRect = (RectTransform)cubeObject.transform;
            cubeRect.anchorMin = Vector2.zero;
            cubeRect.anchorMax = Vector2.one;
            cubeRect.offsetMin = new Vector2(10f, 34f);
            cubeRect.offsetMax = new Vector2(-10f, -6f);
            orientationCube = cubeObject.GetComponent<RuntimeOrientationCube>();
            orientationCube.Configure(navigator, SetView, ToggleProjection, uiFont);
            RegisterViewContextClick(cubeObject);

            RectTransform projection = Rect("ProjectionStatus", viewGizmo, Vector2.zero, new Vector2(1f, 0f),
                new Vector2(8f, 6f), new Vector2(-8f, 32f), Transparent);
            RegisterViewContextClick(projection.gameObject);
            viewGizmoStatusText = Text("Top · Orthographic", projection, 11, FontStyle.Bold,
                TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.88f), Vector2.zero, Vector2.zero);
            Outline statusOutline = viewGizmoStatusText.gameObject.AddComponent<Outline>();
            statusOutline.effectColor = new Color(0f, 0f, 0f, 0.72f);
            statusOutline.effectDistance = new Vector2(1f, -1f);
            BuildViewGizmoMenu();
        }

        private void GizmoAxisLine(Transform parent, Vector2 from, Vector2 to, Color color)
        {
            Vector2 direction = to - from;
            RectTransform line = Rect("OrientationAxis", parent, Vector2.zero, Vector2.zero,
                Vector2.zero, new Vector2(direction.magnitude, 3f), color);
            line.pivot = new Vector2(0f, 0.5f);
            line.anchoredPosition = from;
            line.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
            line.GetComponent<Image>().raycastTarget = false;
        }

        private void GizmoAxisButton(Transform parent, string label, Vector2 min, Vector2 max,
            SceneViewPreset preset, Color color)
        {
            RectTransform rect = Rect("Orientation_" + ViewName(preset), parent, Vector2.zero, Vector2.zero,
                min, max, new Color(color.r, color.g, color.b, 0.12f));
            MakeButton(rect, () => SetView(preset), new Color(color.r, color.g, color.b, 0.12f));
            RegisterViewContextClick(rect.gameObject);
            Text(string.IsNullOrEmpty(label) ? "●" : label + "\n●", rect, string.IsNullOrEmpty(label) ? 15 : 12,
                FontStyle.Bold, TextAnchor.MiddleCenter, color, Vector2.zero, Vector2.zero);
        }

        private void BuildViewGizmoMenu()
        {
            RectTransform menu = Rect("ViewOrientationMenu", transform, Vector2.one, Vector2.one,
                new Vector2(-704f, -528f), new Vector2(-458f, -138f), Surface);
            viewGizmoMenu = menu.gameObject;
            float top = -12f;
            foreach (SceneViewPreset preset in new[]
                     {
                         SceneViewPreset.Perspective, SceneViewPreset.Right, SceneViewPreset.Top,
                         SceneViewPreset.Front, SceneViewPreset.Left, SceneViewPreset.Bottom, SceneViewPreset.Back
                     })
            {
                SceneViewPreset value = preset;
                viewMenuButtons[preset] = ViewMenuButton(menu, ViewName(preset), top,
                    () => { SetView(value); viewGizmoMenu.SetActive(false); });
                top -= 40f;
            }
            Rect("MenuDivider", menu, new Vector2(0f, 1f), Vector2.one,
                new Vector2(12f, top - 1f), new Vector2(-12f, top), Border);
            top -= 10f;
            projectionMenuButton = ViewMenuButton(menu, "Perspective Projection", top, () =>
            {
                ToggleProjection();
                viewGizmoMenu.SetActive(false);
            });
            top -= 40f;
            backgroundMenuButton = ViewMenuButton(menu, "Show Background", top, () =>
            {
                ToggleViewGizmoBackground();
                RefreshSceneViewButtons();
                viewGizmoMenu.SetActive(false);
            });
            viewGizmoMenu.SetActive(false);
        }

        private Button ViewMenuButton(Transform parent, string label, float top, Action action)
        {
            RectTransform rect = Rect(label, parent, new Vector2(0f, 1f), Vector2.one,
                new Vector2(8f, top - 36f), new Vector2(-8f, top), Surface);
            Button button = MakeButton(rect, action, Surface);
            Text(label, rect, 13, FontStyle.Normal, TextAnchor.MiddleLeft, Ink,
                new Vector2(14f, 0f), new Vector2(-8f, 0f));
            return button;
        }

        private void RegisterViewContextClick(GameObject target)
        {
            EventTrigger trigger = target.GetComponent<EventTrigger>() ?? target.AddComponent<EventTrigger>();
            if (trigger.triggers == null) trigger.triggers = new List<EventTrigger.Entry>();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            entry.callback.AddListener(data =>
            {
                if (data is PointerEventData pointer && pointer.button == PointerEventData.InputButton.Right)
                    ToggleViewGizmoMenu();
            });
            trigger.triggers.Add(entry);
        }

        private void ToggleViewGizmoMenu()
        {
            if (viewGizmoMenu == null) return;
            viewGizmoMenu.SetActive(!viewGizmoMenu.activeSelf);
            RefreshSceneViewButtons();
        }

        private void ToggleViewGizmoBackground()
        {
            viewGizmoBackgroundVisible = !viewGizmoBackgroundVisible;
            if (viewGizmoBackground != null)
                viewGizmoBackground.color = viewGizmoBackgroundVisible
                    ? new Color(0.17f, 0.16f, 0.15f, 0.88f)
                    : Transparent;
            Notice($"Orientation-control background: {(viewGizmoBackgroundVisible ? "Visible" : "Hidden")}. ");
        }

        private void BuildDisplaySettings()
        {
            RectTransform overlay = Rect("DisplaySettingsOverlay", transform, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, new Color(0.08f, 0.07f, 0.06f, 0.55f));
            displaySettingsOverlay = overlay.gameObject;
            Button dismiss = overlay.gameObject.AddComponent<Button>();
            dismiss.transition = Selectable.Transition.None;
            dismiss.targetGraphic = overlay.GetComponent<Image>();
            dismiss.onClick.AddListener(() => displaySettingsOverlay.SetActive(false));

            RectTransform card = Rect("DisplaySettingsPanel", overlay, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-270f, -165f), new Vector2(270f, 165f), Surface);
            Text("Display Settings", card, 22, FontStyle.Bold, TextAnchor.UpperLeft, Ink,
                new Vector2(28f, 24f), new Vector2(-28f, -22f));
            Text("Choose the application window mode. This setting persists across launches.", card, 12,
                FontStyle.Normal, TextAnchor.UpperLeft, Muted,
                new Vector2(28f, 66f), new Vector2(-28f, -58f));
            displayModeText = Text("Loading display state...", card, 13, FontStyle.Bold,
                TextAnchor.UpperLeft, InkSoft, new Vector2(28f, 110f), new Vector2(-28f, -100f));

            RectTransform windowedRect = Rect("Windowed", card, new Vector2(0f, 0f), new Vector2(0.5f, 0f),
                new Vector2(28f, 78f), new Vector2(-6f, 126f), SurfaceSoft);
            windowedModeButton = MakeButton(windowedRect, () => ApplyDisplayMode(false), SurfaceSoft);
            Text("Windowed", windowedRect, 13, FontStyle.Bold, TextAnchor.MiddleCenter, Ink,
                Vector2.zero, Vector2.zero);
            RectTransform fullScreenRect = Rect("FullScreen", card, new Vector2(0.5f, 0f), new Vector2(1f, 0f),
                new Vector2(6f, 78f), new Vector2(-28f, 126f), SurfaceSoft);
            fullScreenModeButton = MakeButton(fullScreenRect, () => ApplyDisplayMode(true), SurfaceSoft);
            Text("Borderless Full Screen", fullScreenRect, 13, FontStyle.Bold, TextAnchor.MiddleCenter, Ink,
                Vector2.zero, Vector2.zero);

            RectTransform closeRect = Rect("CloseDisplaySettings", card, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(-90f, 22f), new Vector2(90f, 62f), InkSoft);
            MakeButton(closeRect, () => displaySettingsOverlay.SetActive(false), InkSoft);
            Text("Close", closeRect, 12, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white,
                Vector2.zero, Vector2.zero);
            displaySettingsOverlay.SetActive(false);
        }

        private void BuildSceneDataOverlay()
        {
            RectTransform overlay = Rect("SceneDataOverlay", transform, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, new Color(0.08f, 0.07f, 0.06f, 0.55f));
            sceneDataOverlay = overlay.gameObject;
            Button dismiss = overlay.gameObject.AddComponent<Button>();
            dismiss.transition = Selectable.Transition.None;
            dismiss.targetGraphic = overlay.GetComponent<Image>();
            dismiss.onClick.AddListener(() => sceneDataOverlay.SetActive(false));

            RectTransform card = Rect("SceneDataPanel", overlay, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-410f, -440f), new Vector2(410f, 440f), Surface);
            Button absorber = card.gameObject.AddComponent<Button>();
            absorber.transition = Selectable.Transition.None;
            absorber.targetGraphic = card.GetComponent<Image>();

            RectTransform viewport = Rect("SceneDataViewport", card, Vector2.zero, Vector2.one,
                new Vector2(24f, 24f), new Vector2(-30f, -24f), Surface);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            ScrollRect scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.scrollSensitivity = 30f;
            RectTransform content = EditorPanel(viewport, "SceneDataContent");
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            scroll.viewport = viewport;
            scroll.content = content;
            RectTransform scrollbarRoot = Rect("SceneDataScrollbar", card, new Vector2(1f, 0f), Vector2.one,
                new Vector2(-22f, 28f), new Vector2(-12f, -28f), SurfaceSoft);
            Scrollbar scrollbar = scrollbarRoot.gameObject.AddComponent<Scrollbar>();
            RectTransform slidingArea = Rect("SlidingArea", scrollbarRoot, Vector2.zero, Vector2.one,
                new Vector2(2f, 2f), new Vector2(-2f, -2f), Transparent);
            RectTransform handle = Rect("ScrollbarHandle", slidingArea, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, AccentSoft);
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handle.GetComponent<Image>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            Label(content, "Scene Workspace", 22, Ink, 42f, FontStyle.Bold);
            sceneDataSummary = Label(content, "Loading scene data...", 12, Muted, 66f);
            Section(content, "Scene Identity", "Data is independent between tabs; current edits are saved automatically before switching");
            sceneNameInput = Input(content, string.Empty, "Scene name", value =>
            {
                if (!sceneWorkspace.RenameActiveScene(value, out string error)) Notice(error);
                RefreshSceneTabs();
            });
            RowButtons(content,
                ("Save Current Scene", () => { sceneWorkspace.SaveActiveScene(); Notice("Current scene saved. "); }, Accent),
                ("Duplicate as New Scene", () => sceneWorkspace.DuplicateActiveScene(), SurfaceSoft));
            FullButton(content, "Move Current Scene to Recycle Bin", () =>
            {
                if (!sceneWorkspace.RecycleActiveScene(out string error)) Notice(error);
            }, Red);
            Section(content, "Closed Tabs", "Closing a tab does not delete scene files; reopen it here");
            closedSceneDropdown = Dropdown(content, Enumerable.Empty<string>(), _ => { }, true);
            reopenSceneButton = FullButton(content, "Reopen Selected Scene", ReopenSelectedScene, SurfaceSoft);

            Section(content, "Network Data Source", "Browse or drag a centerline-network JSON into the path field; import copies it into the current scene and rebuilds the network");
            sceneNetworkPathInput = PathInput(content, string.Empty, "External AxisNetwork.json path",
                PickSceneNetworkPath, ImportSceneNetworkPath);

            Section(content, "Vertiport-Structure Data Source", "Browse or drag a vertiport JSON into the path field to import vertiports, helipads, approach networks, holding structures, and corridors");
            sceneVertiportPathInput = PathInput(content, string.Empty, "External VertiportData.json path",
                PickSceneVertiportPath, ImportSceneVertiportPath);

            Section(content, "City Building Model", "The application reads .sarcity, .obj, or .dxf directly; the Unity Editor can convert and load CADMapper .skp files automatically");
            sceneCityPathInput = PathInput(content, string.Empty, "City-model path (.sarcity / .obj / .dxf / .skp)",
                PickSceneCityPath, ImportSceneCityPath);
            cityDropTarget = Rect("CityModelDropZone", content, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, Hex("F0E5DD"));
            cityDropTarget.gameObject.AddComponent<LayoutElement>().preferredHeight = 74f;
            Outline dropOutline = cityDropTarget.gameObject.AddComponent<Outline>();
            dropOutline.effectColor = AccentSoft;
            dropOutline.effectDistance = new Vector2(1f, -1f);
            cityDropStatusText = Text("Drop a city model here\n.sarcity / .obj / .dxf / .skp", cityDropTarget, 13, FontStyle.Bold,
                TextAnchor.MiddleCenter, InkSoft, new Vector2(12f, 6f), new Vector2(-12f, -6f));
            FullButton(content, "Open CADMapper", () => sceneWorkspace.OpenCadmapper(), Teal);
            Hint(content, "DXF files must contain 3DFACE or SOLID surfaces, or closed contours with thickness; pure 2D linework cannot form city buildings. Direct .skp import in the application requires an available converter. ");

            Section(content, "City-Model Transform", "Move or rotate the entire city model immediately after editing a field");
            Label(content, "Position X / Y / Z (m)", 11, Muted, 22f);
            cityPositionXInput = Input(content, "0", "Position X", _ => ApplyCityTransformFields());
            cityPositionYInput = Input(content, "0", "Position Y", _ => ApplyCityTransformFields());
            cityPositionZInput = Input(content, "0", "Position Z", _ => ApplyCityTransformFields());
            Label(content, "Rotation X / Y / Z (deg)", 11, Muted, 22f);
            cityRotationXInput = Input(content, "0", "Rotation X", _ => ApplyCityTransformFields());
            cityRotationYInput = Input(content, "0", "Rotation Y", _ => ApplyCityTransformFields());
            cityRotationZInput = Input(content, "0", "Rotation Z", _ => ApplyCityTransformFields());
            RowButtons(content,
                ("Open Scene Storage Folder", OpenSceneWorkspaceFolder, SurfaceSoft),
                ("Close", () => sceneDataOverlay.SetActive(false), InkSoft));
            sceneDataOverlay.SetActive(false);
            RefreshSceneDataFields();
        }

        private void BuildSceneCloseConfirmation()
        {
            RectTransform overlay = Rect("CloseSceneConfirmationOverlay", transform, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, new Color(0.08f, 0.07f, 0.06f, 0.66f));
            sceneCloseConfirmOverlay = overlay.gameObject;
            RectTransform card = Rect("CloseSceneConfirmation", overlay, new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-300f, -128f), new Vector2(300f, 128f), Surface);
            Text("Close Scene Tab", card, 20, FontStyle.Bold, TextAnchor.UpperLeft, Ink,
                new Vector2(28f, 24f), new Vector2(-28f, -18f));
            sceneCloseConfirmText = Text("The scene contains unsaved changes.", card, 13, FontStyle.Normal,
                TextAnchor.UpperLeft, InkSoft, new Vector2(28f, 70f), new Vector2(-28f, -64f));
            RectTransform buttons = Rect("CloseOptions", card, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(28f, 24f), new Vector2(-28f, 72f), Transparent);
            HorizontalLayoutGroup layout = buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childForceExpandWidth = true;
            Button("Save and Close", buttons, () => ConfirmCloseScene(true), Accent)
                .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            Button("Discard Changes", buttons, () => ConfirmCloseScene(false), Red)
                .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            Button("Cancel", buttons, CancelCloseScene, SurfaceSoft)
                .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            sceneCloseConfirmOverlay.SetActive(false);
        }

        private void RequestCloseScene(string sceneId)
        {
            SarSceneProfile profile = sceneWorkspace.Profiles.FirstOrDefault(scene => scene.Id == sceneId);
            if (profile == null) return;
            if (!sceneWorkspace.IsDirty(sceneId))
            {
                if (!sceneWorkspace.CloseScene(sceneId, false, out string error)) Notice(error);
                return;
            }
            pendingCloseSceneId = sceneId;
            if (sceneCloseConfirmText != null)
                SetLocalizedText(sceneCloseConfirmText,
                    $"Scene \"{profile.DisplayName}\" contains unsaved airspace edits. Save the scene files before closing?");
            sceneCloseConfirmOverlay.SetActive(true);
        }

        private void ConfirmCloseScene(bool saveChanges)
        {
            string sceneId = pendingCloseSceneId;
            CancelCloseScene();
            if (!sceneWorkspace.CloseScene(sceneId, saveChanges, out string error)) Notice(error);
        }

        private void CancelCloseScene()
        {
            pendingCloseSceneId = null;
            if (sceneCloseConfirmOverlay != null) sceneCloseConfirmOverlay.SetActive(false);
        }

        private void BuildHistoryDeleteConfirmation()
        {
            RectTransform overlay = Rect("HistoryRecycleConfirmationOverlay", transform,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                new Color(0.08f, 0.07f, 0.06f, 0.66f));
            historyDeleteConfirmOverlay = overlay.gameObject;
            RectTransform card = Rect("HistoryRecycleConfirmation", overlay,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-330f, -130f), new Vector2(330f, 130f), Surface);
            Text("Recycle Simulation History", card, 20, FontStyle.Bold,
                TextAnchor.UpperLeft, Ink, new Vector2(28f, 24f), new Vector2(-28f, -18f));
            historyDeleteConfirmText = Text("The record will be moved to the scene recycle folder.",
                card, 13, FontStyle.Normal, TextAnchor.UpperLeft, InkSoft,
                new Vector2(28f, 70f), new Vector2(-28f, -64f));
            RectTransform buttons = Rect("HistoryRecycleOptions", card,
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(28f, 24f), new Vector2(-28f, 72f), Transparent);
            HorizontalLayoutGroup layout = buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childForceExpandWidth = true;
            Button("Move to Recycle Bin", buttons, ConfirmRecycleSelectedHistory, Red)
                .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            Button("Cancel", buttons, CancelRecycleSelectedHistory, SurfaceSoft)
                .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            historyDeleteConfirmOverlay.SetActive(false);
        }

        private void RequestRecycleSelectedHistory()
        {
            if (historyAnalysisBusy || IsSimulationInteractionLocked())
            {
                Notice("Wait for the current simulation or analysis operation to finish before recycling history. ");
                return;
            }
            List<SarSimulationRunManifest> selected = GetExplicitSelectedHistoryItems();
            if (selected.Count != 1)
            {
                Notice("Select exactly one standalone run or one batch parent to recycle. ");
                return;
            }
            SarSimulationRunManifest manifest = selected[0];
            if (manifest.Kind == SarSimulationRunKind.BatchMember)
            {
                Notice("A batch member cannot be recycled independently. Select its batch parent to recycle the complete batch. ");
                return;
            }
            pendingHistoryRecycleRunId = manifest.RunId;
            string rawRecord = SarRuntimeLocalization.Literal(HistoryEntryLabel(manifest));
            string detail = manifest.Kind == SarSimulationRunKind.Batch
                ? "This moves the batch parent and every member run to the current scene's SimulationHistory/.Trash folder. Continue?"
                : "This moves the selected run to the current scene's SimulationHistory/.Trash folder. Continue?";
            SetLocalizedText(historyDeleteConfirmText, rawRecord + "\n" + detail);
            historyDeleteConfirmOverlay.SetActive(true);
            historyDeleteConfirmOverlay.transform.SetAsLastSibling();
        }

        private void ConfirmRecycleSelectedHistory()
        {
            string runId = pendingHistoryRecycleRunId;
            CancelRecycleSelectedHistory();
            if (historyAnalysisBusy || IsSimulationInteractionLocked())
            {
                Notice("The record was not recycled because a simulation or analysis operation is active. ");
                return;
            }
            string result = null;
            if (simulationHistory == null ||
                !simulationHistory.TryMoveToRecycleBin(runId, out result))
            {
                Notice(string.IsNullOrWhiteSpace(result)
                    ? "The selected history record could not be recycled. "
                    : result + " ");
                return;
            }

            InvalidateHistoryAnalysisContext();
            selectedHistoryIds.Clear();
            expandedHistoryBatchIds.Clear();
            selectedHistoryMetrics.Clear();
            selectedHistoryFactors.Clear();
            HideHistoryAnalysisSurface();
            ClearLoadedHistoricalResult();
            historyListSignature = null;
            historySelectorSignature = null;
            RefreshHistoryList(true);
            Notice(result + " It can be restored manually from the scene recycle folder. ");
        }

        private void CancelRecycleSelectedHistory()
        {
            pendingHistoryRecycleRunId = null;
            if (historyDeleteConfirmOverlay != null) historyDeleteConfirmOverlay.SetActive(false);
        }

        private void ToggleSceneDataOverlay()
        {
            if (sceneDataOverlay == null) return;
            bool show = !sceneDataOverlay.activeSelf;
            sceneDataOverlay.SetActive(show);
            if (show) RefreshSceneDataFields();
        }

        private void RefreshSceneDataFields()
        {
            if (sceneWorkspace == null) return;
            SarSceneProfile profile = sceneWorkspace.ActiveProfile;
            if (profile == null) return;
            if (sceneNameInput != null) sceneNameInput.SetTextWithoutNotify(profile.DisplayName ?? string.Empty);
            if (sceneNetworkPathInput != null) sceneNetworkPathInput.SetTextWithoutNotify(profile.NetworkSourcePath ?? profile.AxisNetworkPath);
            if (sceneVertiportPathInput != null) sceneVertiportPathInput.SetTextWithoutNotify(profile.VertiportSourcePath ?? profile.VertiportPath);
            if (sceneCityPathInput != null) sceneCityPathInput.SetTextWithoutNotify(profile.CitySourcePath ?? profile.CityModelPath);
            SetTransformText(cityPositionXInput, profile.CityPosition.x);
            SetTransformText(cityPositionYInput, profile.CityPosition.y);
            SetTransformText(cityPositionZInput, profile.CityPosition.z);
            SetTransformText(cityRotationXInput, profile.CityEulerAngles.x);
            SetTransformText(cityRotationYInput, profile.CityEulerAngles.y);
            SetTransformText(cityRotationZInput, profile.CityEulerAngles.z);
            if (closedSceneDropdown != null)
                SetDropdownOptions(closedSceneDropdown, sceneWorkspace.ClosedProfiles.Select(scene => scene.DisplayName));
            if (reopenSceneButton != null)
            {
                bool anyClosed = sceneWorkspace.ClosedProfiles.Count > 0;
                reopenSceneButton.interactable = anyClosed;
                SetButtonColor(reopenSceneButton, anyClosed ? SurfaceSoft : Border);
            }
            if (sceneDataSummary != null)
                SetLocalizedText(sceneDataSummary, $"Active: {profile.DisplayName}\n" +
                    $"Network: {manager.Nodes.Count} nodes / {manager.Edges.Count} edges · " +
                    $"Vertiports: {manager.Vertiports.Count} · City package: {Path.GetFileName(profile.CityModelPath)}\n" +
                    $"Scene directory: user://SARScenes/{profile.Id}/");
        }

        private void PickSceneNetworkPath()
        {
            SarNativeFileDialog.OpenFileAsync(L("Select Centerline-Network JSON"), "JSON files|*.json|All files|*.*",
                ResolveScenePath(sceneNetworkPathInput?.text), path =>
                {
                    if (!string.IsNullOrWhiteSpace(path)) ImportSceneNetworkPath(path);
                });
        }

        private string ResolveScenePath(string path)
        {
            return sceneWorkspace != null
                ? sceneWorkspace.ResolveActivePath(path)
                : SarPortablePath.Resolve(path);
        }

        private void PickSceneVertiportPath()
        {
            SarNativeFileDialog.OpenFileAsync(L("Select Vertiport-Structure JSON"), "JSON files|*.json|All files|*.*",
                ResolveScenePath(sceneVertiportPathInput?.text), path =>
                {
                    if (!string.IsNullOrWhiteSpace(path)) ImportSceneVertiportPath(path);
                });
        }

        private void PickSceneCityPath()
        {
            SarNativeFileDialog.OpenFileAsync(L("Select City Model"),
                "City models|*.sarcity;*.obj;*.dxf;*.skp|SAR city packages|*.sarcity|OBJ models|*.obj|DXF models|*.dxf|SketchUp|*.skp",
                ResolveScenePath(sceneCityPathInput?.text), path =>
                {
                    if (!string.IsNullOrWhiteSpace(path)) ImportSceneCityPath(path);
                });
        }

        private void ImportSceneNetworkPath(string path)
        {
            if (!sceneWorkspace.ImportAxisNetwork(path, out string error)) { Notice(error); return; }
            HandleActiveSceneChanged();
            RefreshSceneDataFields();
        }

        private void ImportSceneVertiportPath(string path)
        {
            if (!sceneWorkspace.ImportVertiports(path, out string error)) { Notice(error); return; }
            HandleActiveSceneChanged();
            RefreshSceneDataFields();
        }

        private void ImportSceneCityPath(string path)
        {
            if (cityDropStatusText != null)
                SetLocalizedText(cityDropStatusText, $"Importing {Path.GetFileName(path)}...");
            Vector3 viewCenter = navigator != null ? navigator.Pivot : Vector3.zero;
            if (!sceneWorkspace.ImportCityModel(path, viewCenter, out string error, out Bounds cityBounds))
            {
                if (cityDropStatusText != null)
                    SetLocalizedText(cityDropStatusText, "Import failed: " + error);
                Notice(error);
                return;
            }
            if (cityDropStatusText != null)
                SetLocalizedText(cityDropStatusText,
                    $"Loaded: {Path.GetFileName(path)}\nDrop another file here to replace it");
            navigator?.FrameBounds(cityBounds, 1.12f, true);
            RefreshSceneDataFields();
        }

        private void HandleFilesDropped(IReadOnlyList<string> paths, Vector2 screenPoint)
        {
            if (sceneDataOverlay == null || !sceneDataOverlay.activeInHierarchy) return;
            string jsonPath = paths?.FirstOrDefault(value =>
                string.Equals(Path.GetExtension(value), ".json", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(jsonPath) && IsDropInside(sceneNetworkPathInput, screenPoint))
            {
                ImportSceneNetworkPath(jsonPath);
                return;
            }
            if (!string.IsNullOrWhiteSpace(jsonPath) && IsDropInside(sceneVertiportPathInput, screenPoint))
            {
                ImportSceneVertiportPath(jsonPath);
                return;
            }
            if (cityDropTarget == null ||
                !RectTransformUtility.RectangleContainsScreenPoint(cityDropTarget, screenPoint, null))
            {
                Notice("Drop network or vertiport JSON into its corresponding path field, and city models into the city-model drop zone. ");
                return;
            }
            string path = paths?.FirstOrDefault(value =>
            {
                string extension = Path.GetExtension(value ?? string.Empty).ToLowerInvariant();
                return extension == SarCityModelPackage.Extension || extension == ".obj" ||
                       extension == ".dxf" || extension == ".skp";
            });
            if (string.IsNullOrWhiteSpace(path))
            {
                const string unsupported = "The drop zone accepts only .sarcity, .obj, .dxf, or .skp city models. ";
                if (cityDropStatusText != null) SetLocalizedText(cityDropStatusText, unsupported);
                Notice(unsupported);
                return;
            }
            sceneCityPathInput?.SetTextWithoutNotify(path);
            ImportSceneCityPath(path);
        }

        private static bool IsDropInside(Component component, Vector2 screenPoint)
        {
            RectTransform rectangle = component != null ? component.transform as RectTransform : null;
            return rectangle != null &&
                   RectTransformUtility.RectangleContainsScreenPoint(rectangle, screenPoint, null);
        }

        private void ApplyCityTransformFields()
        {
            SarSceneProfile profile = sceneWorkspace?.ActiveProfile;
            if (profile == null) return;
            Vector3 position = new Vector3(
                ParseFloat(cityPositionXInput, profile.CityPosition.x),
                ParseFloat(cityPositionYInput, profile.CityPosition.y),
                ParseFloat(cityPositionZInput, profile.CityPosition.z));
            Vector3 rotation = new Vector3(
                ParseFloat(cityRotationXInput, profile.CityEulerAngles.x),
                ParseFloat(cityRotationYInput, profile.CityEulerAngles.y),
                ParseFloat(cityRotationZInput, profile.CityEulerAngles.z));
            sceneWorkspace.SetActiveCityTransform(position, rotation);
            RefreshSceneDataFields();
            navigator?.FrameAll(manager, true);
        }

        private static void SetTransformText(InputField input, float value)
        {
            input?.SetTextWithoutNotify(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private void ReopenSelectedScene()
        {
            if (closedSceneDropdown == null || closedSceneDropdown.options.Count == 0)
            {
                Notice("There are no closed scene tabs. ");
                return;
            }
            string displayName = DropdownSourceValue(closedSceneDropdown);
            SarSceneProfile profile = sceneWorkspace.ClosedProfiles.FirstOrDefault(scene => scene.DisplayName == displayName);
            if (profile == null)
            {
                Notice("The scene could not be reopened because its record no longer exists. ");
                return;
            }
            if (!sceneWorkspace.ReopenScene(profile.Id, true, out string error))
                Notice(error ?? "The scene could not be reopened. ");
        }

        private void OpenSceneWorkspaceFolder()
        {
            Directory.CreateDirectory(sceneWorkspace.WorkspaceRoot);
            Application.OpenURL("file:///" + sceneWorkspace.WorkspaceRoot.Replace('\\', '/'));
        }

        private void ToggleDisplaySettings()
        {
            if (displaySettingsOverlay == null) return;
            bool show = !displaySettingsOverlay.activeSelf;
            displaySettingsOverlay.SetActive(show);
            if (show) RefreshDisplaySettings();
        }

        private void ApplySavedDisplayMode()
        {
            displayPreferences = SarDisplayPreferencesStore.Load();
            if (Application.isEditor || displayPreferences == null || !displayPreferences.Initialized) return;
            ApplyDisplayMode(displayPreferences.FullScreen, false);
        }

        private void ApplyDisplayMode(bool fullScreen, bool notify = true)
        {
            if (displayPreferences == null) displayPreferences = SarDisplayPreferencesStore.Load();
            displayPreferences.Initialized = true;
            displayPreferences.FullScreen = fullScreen;
            if (!Application.isEditor)
            {
                if (fullScreen)
                {
                    if (Screen.fullScreenMode == FullScreenMode.Windowed)
                    {
                        displayPreferences.WindowWidth = Screen.width;
                        displayPreferences.WindowHeight = Screen.height;
                    }
                    Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight,
                        FullScreenMode.FullScreenWindow);
                }
                else
                {
                    int defaultWidth = Mathf.Clamp(Display.main.systemWidth - 160, 960, 1600);
                    int defaultHeight = Mathf.Clamp(Mathf.RoundToInt(defaultWidth * 9f / 16f), 640,
                        Mathf.Max(640, Display.main.systemHeight - 120));
                    int width = displayPreferences.WindowWidth > 0 ? displayPreferences.WindowWidth : defaultWidth;
                    int height = displayPreferences.WindowHeight > 0 ? displayPreferences.WindowHeight : defaultHeight;
                    Screen.SetResolution(width, height, FullScreenMode.Windowed);
                }
            }
            SarDisplayPreferencesStore.Save(displayPreferences);
            RefreshDisplaySettings(fullScreen);
            if (notify) Notice($"Display mode changed to {(fullScreen ? "Borderless Full Screen" : "Windowed")}. ");
        }

        private void RefreshDisplaySettings(bool? requestedFullScreen = null)
        {
            bool fullScreen = requestedFullScreen ?? (!Application.isEditor
                ? Screen.fullScreenMode != FullScreenMode.Windowed
                : displayPreferences?.FullScreen == true);
            if (displayModeText != null)
                SetLocalizedText(displayModeText,
                    $"Current: {(fullScreen ? "Borderless Full Screen" : "Windowed")}  ·  {Screen.width} × {Screen.height}");
            SetButtonColor(windowedModeButton, fullScreen ? SurfaceSoft : AccentSoft);
            SetButtonColor(fullScreenModeButton, fullScreen ? AccentSoft : SurfaceSoft);
        }

        private void SetView(SceneViewPreset preset)
        {
            UseOverviewCamera(false);
            navigator.SetPreset(preset);
            RefreshSceneViewButtons();
            Notice($"View: {L(ViewName(preset))}. ");
        }

        private void ToggleProjection()
        {
            navigator.ToggleProjection();
            RefreshSceneViewButtons();
            Notice($"Switched to {(navigator.IsOrthographic ? "orthographic" : "perspective")} projection. ");
        }

        private void RefreshSceneViewButtons()
        {
            if (navigator == null) return;
            lastDisplayedViewPreset = navigator.CurrentPreset;
            lastDisplayedProjection = navigator.IsOrthographic;
            if (viewGizmoStatusText != null)
                SetLocalizedText(viewGizmoStatusText, $"{L(ViewName(navigator.CurrentPreset))} · " +
                                                       L(navigator.IsOrthographic ? "Orthographic" : "Perspective"));
            foreach (var pair in viewMenuButtons)
            {
                Text label = pair.Value != null ? pair.Value.GetComponentInChildren<Text>(true) : null;
                SetLocalizedText(label, (pair.Key == navigator.CurrentPreset ? "✓  " : "    ") +
                                        L(ViewName(pair.Key)));
                SetButtonColor(pair.Value, pair.Key == navigator.CurrentPreset ? AccentSoft : Surface);
            }
            if (projectionMenuButton != null)
            {
                Text label = projectionMenuButton.GetComponentInChildren<Text>(true);
                SetLocalizedText(label, (navigator.IsOrthographic ? "    " : "✓  ") +
                                        L("Perspective Projection"));
            }
            if (backgroundMenuButton != null)
            {
                Text label = backgroundMenuButton.GetComponentInChildren<Text>(true);
                SetLocalizedText(label, (viewGizmoBackgroundVisible ? "✓  " : "    ") +
                                        L("Show Background"));
            }
        }

        private Button NavigationButton(Transform parent, string title, string hint, float top, Action action)
        {
            RectTransform rect = Rect(title, parent, new Vector2(0f, 1f), Vector2.one,
                new Vector2(14f, top - 62f), new Vector2(-14f, top), InkSoft);
            Button button = MakeButton(rect, action, InkSoft);
            Text(title, rect, 15, FontStyle.Bold, TextAnchor.UpperLeft, Color.white,
                new Vector2(18f, 13f), new Vector2(-12f, -9f));
            Text(hint, rect, 10, FontStyle.Normal, TextAnchor.LowerLeft, new Color(1f, 1f, 1f, 0.55f),
                new Vector2(18f, 8f), new Vector2(-12f, -31f));
            return button;
        }

        private Button SceneButton(Transform parent, string label, float top, Action action)
        {
            Color color = new Color(1f, 1f, 1f, 0.06f);
            RectTransform rect = Rect(label, parent, new Vector2(0f, 1f), Vector2.one,
                new Vector2(20f, top - 34f), new Vector2(-20f, top), color);
            Button button = MakeButton(rect, action, color);
            Text(label, rect, 12, FontStyle.Normal, TextAnchor.MiddleLeft, new Color(1f, 1f, 1f, 0.85f),
                new Vector2(14f, 0f), new Vector2(-8f, 0f));
            return button;
        }

        private void TopText(Transform parent, string value, float minY, float maxY, int size, Color color)
        {
            Text text = Text(value, parent, size, FontStyle.Bold, TextAnchor.MiddleLeft, color,
                new Vector2(24f, minY), new Vector2(-20f, maxY));
            text.rectTransform.anchorMin = new Vector2(0f, 1f);
            text.rectTransform.anchorMax = Vector2.one;
        }

        private bool IsSimulationInteractionLocked() =>
            (manager != null && manager.IsRunning()) ||
            (sweepRunner != null && sweepRunner.isRunning);

        private bool RejectWorkspaceMutationWhileSimulationRuns(string action)
        {
            if (!IsSimulationInteractionLocked()) return false;
            Notice($"Stop the current single or batch simulation before {action}. ");
            return true;
        }

        /// <summary>
        /// Freezes every workspace that can mutate or replace simulation-owned data.
        /// The batch runner remains locked between members even when SimulationManager
        /// briefly reports stopped, because sweepRunner.isRunning remains true.
        /// </summary>
        private void ApplySimulationInterlock(bool force = false)
        {
            bool locked = IsSimulationInteractionLocked();
            if (!force && lastSimulationInterlockState == locked)
            {
                ApplyHeatmapVisibilityForCurrentPage();
                return;
            }
            lastSimulationInterlockState = locked;

            if (locked && pages != null && pages.Length > 2 && currentPageIndex != 2)
                ShowPage(2);

            if (navigationButtons != null)
                for (int index = 0; index < navigationButtons.Length; index++)
                    if (navigationButtons[index] != null)
                        navigationButtons[index].interactable = !locked || index == 2;

            if (sceneDataButton != null) sceneDataButton.interactable = !locked;
            if (newSceneButton != null) newSceneButton.interactable = !locked;
            foreach (Button button in sceneTabActionButtons)
                if (button != null) button.interactable = !locked;
            foreach (Selectable selectable in simulationConfigurationControls)
                if (selectable != null) selectable.interactable = !locked;

            if (singleSimulationStartButton != null)
                singleSimulationStartButton.interactable = !locked;
            if (batchExperimentStartButton != null)
                batchExperimentStartButton.interactable = !locked;
            if (singleSimulationStopButton != null)
                singleSimulationStopButton.interactable = manager != null && manager.IsRunning();
            if (batchExperimentStopButton != null)
                batchExperimentStopButton.interactable = sweepRunner != null && sweepRunner.isRunning;

            if (locked)
            {
                if (sceneDataOverlay != null) sceneDataOverlay.SetActive(false);
                if (sceneCloseConfirmOverlay != null) sceneCloseConfirmOverlay.SetActive(false);
                if (historyDeleteConfirmOverlay != null) historyDeleteConfirmOverlay.SetActive(false);
                pendingCloseSceneId = null;
                pendingHistoryRecycleRunId = null;
            }
            ApplyHeatmapVisibilityForCurrentPage();
        }

        private void SetLiveTrafficHeatmapEnabled(bool enabled)
        {
            // Compatibility entry point retained for older editor tests and serialized
            // UnityEvents. It now selects one explicit lane mode instead of toggling the
            // historical Heatmap visual layer.
            SetRuntimeLaneColorMode(enabled
                ? RuntimeLaneColorMode.LiveAircraftLoad
                : RuntimeLaneColorMode.LaneType, true);
        }

        private void SetRuntimeLaneColorMode(RuntimeLaneColorMode mode, bool enabled)
        {
            // ToggleGroup invokes the deselected toggle before/alongside the newly
            // selected one. Only the positive edge owns the mode transition.
            if (!enabled) return;
            runtimeLaneColorMode = mode;
            laneTypeColorModeToggle?.SetIsOnWithoutNotify(mode == RuntimeLaneColorMode.LaneType);
            liveTrafficHeatmapToggle?.SetIsOnWithoutNotify(mode == RuntimeLaneColorMode.LiveAircraftLoad);
            lanePassabilityColorModeToggle?.SetIsOnWithoutNotify(mode == RuntimeLaneColorMode.Passability);
            ApplyHeatmapVisibilityForCurrentPage();
            switch (mode)
            {
                case RuntimeLaneColorMode.LiveAircraftLoad:
                    Notice("Lane display mode: live aircraft load. ");
                    break;
                case RuntimeLaneColorMode.Passability:
                    Notice("Lane display mode: passability. ");
                    break;
                default:
                    Notice("Lane display mode: lane-type colors. ");
                    break;
            }
        }

        private void SetDataVisualizationHeatmapEnabled(bool enabled, bool notify)
        {
            dataVisualizationHeatmapEnabled = enabled;
            ApplyHeatmapVisibilityForCurrentPage();
            RefreshLayerButtonVisual(AirspaceVisualLayer.Heatmap, enabled);
            if (notify)
                Notice($"{L(LayerName(AirspaceVisualLayer.Heatmap))}: {(enabled ? "Visible" : "Hidden")}. ");
        }

        private void ApplyHeatmapVisibilityForCurrentPage()
        {
            if (visualizer == null) return;
            bool visible = currentPageIndex == 3 && dataVisualizationHeatmapEnabled;
            visualizer.SetVisible(AirspaceVisualLayer.Heatmap, visible);
            visualizer.SetLaneColorMode(currentPageIndex == 2
                ? runtimeLaneColorMode
                : RuntimeLaneColorMode.LaneType);
            RefreshHistoricalHeatmapLegend();
        }

        private void ShowPage(int index)
        {
            int selected = Mathf.Clamp(index, 0, pages.Length - 1);
            if (selected != 2 && IsSimulationInteractionLocked())
            {
                Notice("Airspace editing, Routes & Flow, Data Visualization, and scene switching are locked until the current simulation ends. ");
                ApplySimulationInterlock(true);
                return;
            }
            currentPageIndex = selected;
            // Page-local eyes are a presentation concern. Replace the complete
            // temporary overlay on every page transition so a hidden layer in the
            // editor cannot leak into simulation, route authoring, or analytics.
            ApplyPageAltitudeLayerVisibility(selected);
            for (int i = 0; i < pages.Length; i++) pages[i].SetActive(i == selected);
            for (int i = 0; i < navigationButtons.Length; i++)
                SetButtonColor(navigationButtons[i], i == selected ? Accent : InkSoft);
            if (selected != 1) routeDesigner.LaneSelectionEnabled = false;
            RefreshRouteModeButtons();
            visualizer.EditorOverlayVisible = selected == 0;
            if (selected != 0) editor.SetTool(RuntimeEditTool.Inspect, false);
            if (selected != 3)
            {
                // A history load/plot may still be running on a worker thread. Invalidate it
                // before clearing the visible state so a late completion cannot inject an
                // archived topology or trajectory catalogue into another workspace.
                InvalidateHistoryAnalysisContext();
                HideHistoryAnalysisSurface();
                if (!string.IsNullOrWhiteSpace(loadedHistoricalRunId) ||
                    loadedHistoricalNetwork != null || loadedHistoricalVisualizationData != null)
                    ClearLoadedHistoricalResult();
                else
                {
                    visualizer.ClearHistoricalAnalysis();
                    visualizer.ResetHeatmapLayerVisibility();
                }
            }
            if (selected == 1)
                routeDesigner.UpdatePreview();
            else if (selected == 3)
                DrawSelectedTracks(false);
            else
                visualizer.SetRoutePreview(null);
            ApplyHeatmapVisibilityForCurrentPage();
        }

        private void RefreshStatus()
        {
            bool running = manager.IsRunning();
            ApplySimulationInterlock();
            bool operationActive = IsSimulationInteractionLocked();
            manager.GetOperationalAircraftCounts(out int active, out int pending, out _);
            int queuedMissions = manager.MissionQueue.Count;
            SetLocalizedText(statusText, operationActive ? "● Running" : "● System Ready");
            statusText.color = operationActive ? Green : Teal;
            bool hasTransientNotice = !string.IsNullOrEmpty(notice) && Time.unscaledTime < noticeUntil;
            string transient = hasTransientNotice
                ? notice
                : L("Wheel: Zoom · Middle button: Pan · Right button: Orbit");
            SetLocalizedText(footerText,
                SarRuntimeLocalization.Literal(transient) +
                $"    |    Active {active}    Pending {pending}    " +
                $"Succeeded {manager.cumulSuccessCount}    Failed {manager.cumulFailCount}    Collisions {manager.TotalCollisionEvents}");

            Network network = manager.networkManager != null ? manager.networkManager.FullNetwork : null;
            SetLocalizedText(editStats,
                $"Network nodes / edges       {manager.Nodes.Count} / {manager.Edges.Count}\n" +
                $"Lanes / altitude layers     {manager.Lanes.Count} / {network?.AltitudeLayers?.Count ?? 0}\n" +
                $"Interlayer corridors        {manager.LaneConnectionCorridors.Count}\n" +
                $"Holding structures / approach nodes {manager.HoldingStructures.Count} / {manager.ApproachNodes.Count}\n" +
                $"Vertiports / helipads       {manager.Vertiports.Count} / {manager.Helipads.Count}");
            SetLocalizedText(routeStats,
                $"Helipads {manager.Helipads.Count} · Queued missions {queuedMissions} · {L(routeDesigner.RouteSummary)}");
            SetLocalizedText(routeSummary, $"Current route: {L(routeDesigner.RouteSummary)}");
            RefreshMissionQueuePanel();
            SetLocalizedText(simulationStats,
                $"Status              {(operationActive ? "Running" : "Ready")}\n" +
                $"Demand mode         {L(MissionModeName(manager.config?.missionMode))}\n" +
                $"Active / Pending    {active} / {pending}\n" +
                $"Succeeded / Failed  {manager.cumulSuccessCount} / {manager.cumulFailCount}\n" +
                $"Collision events    {manager.TotalCollisionEvents}");
            SetLocalizedText(analyticsStats, analytics.BuildStatistics());
            if (speedText != null) SetLocalizedText(speedText, $"Time Scale  {Time.timeScale:F2}×");
            RefreshSweepStatus();
            RefreshAircraftDropdown();
            RefreshTrajectoryChecklist();
            RefreshHistoricalHeatmapLegend();
        }

        private void RefreshAircraftDropdown()
        {
            if (aircraftDropdown == null) return;
            List<string> ids = manager.AircraftObjects.Where(pair => pair.Value != null).Select(pair => pair.Key)
                .Distinct().OrderBy(id => id).ToList();
            string selected = DropdownSourceValue(aircraftDropdown);
            if (dropdownSourceOptions.TryGetValue(aircraftDropdown, out List<string> existing) &&
                existing.SequenceEqual(ids)) return;
            SetDropdownOptions(aircraftDropdown, ids);
            aircraftDropdown.SetValueWithoutNotify(Mathf.Max(0, ids.IndexOf(selected)));
            aircraftDropdown.RefreshShownValue();
        }

        private void ContinueManualLaneSelection()
        {
            if (routeDesigner.SelectionMode != RouteSelectionMode.Manual)
            {
                routeDesigner.SetSelectionMode(RouteSelectionMode.Manual);
                return;
            }
            routeDesigner.LaneSelectionEnabled = true;
            RefreshRouteModeButtons();
            Notice("Manual routing enabled. Continue selecting lane-connected centerline edges from the current route endpoint. ");
        }

        private void RefreshRouteDesignerState()
        {
            RefreshRouteEndpointDropdowns();
            if (originDropdown != null) SetDropdownValue(originDropdown, routeDesigner.OriginHelipadId);
            if (destinationDropdown != null) SetDropdownValue(destinationDropdown, routeDesigner.DestinationHelipadId);
            if (routeSummary != null)
                SetLocalizedText(routeSummary, $"Current route: {L(routeDesigner.RouteSummary)}");
            RefreshRouteModeButtons();
            missionQueueSignature = null;
            RefreshMissionQueuePanel();
        }

        private void RefreshRouteModeButtons()
        {
            if (routeDesigner == null) return;
            SetButtonColor(automaticRouteButton,
                routeDesigner.SelectionMode == RouteSelectionMode.AutomaticShortest ? Accent : SurfaceSoft);
            SetButtonColor(manualRouteButton,
                routeDesigner.SelectionMode == RouteSelectionMode.Manual ? Accent : SurfaceSoft);
            SetButtonColor(routeLaneSelectionButton,
                routeDesigner.SelectionMode == RouteSelectionMode.Manual && routeDesigner.LaneSelectionEnabled
                    ? Accent : SurfaceSoft);
        }

        private void RefreshMissionQueuePanel()
        {
            if (missionQueueContainer == null || manager == null) return;
            List<MissionTask> tasks = manager.GetPendingMissions();
            string signature = string.Join("|", tasks.Select(task =>
                $"{task.TaskId}:{task.DepartureTime:F3}:{task.OriginHelipadId}:{task.DestinationHelipadId}:" +
                $"{task.RouteLaneIds?.Count ?? 0}"));
            if (signature == missionQueueSignature) return;
            missionQueueSignature = signature;
            DestroyUiChildren(missionQueueContainer);

            if (tasks.Count == 0)
            {
                Label(missionQueueContainer, "There are no pending missions.", 12, Muted, 34f);
                return;
            }

            const int displayLimit = 60;
            foreach (MissionTask task in tasks.Take(displayLimit))
            {
                string routeType = task.RouteLaneIds != null && task.RouteLaneIds.Count > 0
                    ? $"Manual · {task.RouteLaneIds.Count} edges"
                    : "Automatic shortest";
                string selected = task.TaskId == routeDesigner.SelectedMissionId ? "● " : string.Empty;
                Button button = FullButton(missionQueueContainer,
                    $"{selected}{task.TaskId}  t={task.DepartureTime:F1}s  " +
                    $"{task.OriginHelipadId} → {task.DestinationHelipadId}  · {routeType}",
                    () => SelectMissionForEditing(task.TaskId),
                    task.TaskId == routeDesigner.SelectedMissionId ? AccentSoft : SurfaceSoft);
                button.GetComponent<LayoutElement>().preferredHeight = 36f;
            }
            if (tasks.Count > displayLimit)
                Label(missionQueueContainer, $"{tasks.Count - displayLimit} additional missions are not shown.", 11, Muted, 28f);
        }

        private void SelectMissionForEditing(string taskId)
        {
            MissionTask task = manager.GetPendingMissions().FirstOrDefault(item => item.TaskId == taskId);
            if (task == null || !routeDesigner.LoadMissionForEdit(taskId)) return;
            if (departureInput != null)
                departureInput.text = task.DepartureTime.ToString("0.###", CultureInfo.InvariantCulture);
            RefreshRouteDesignerState();
        }

        private void DeleteSelectedMission()
        {
            string taskId = routeDesigner.SelectedMissionId;
            if (string.IsNullOrEmpty(taskId)) { Notice("Select a record from the mission list first. "); return; }
            if (!manager.RemoveMission(taskId)) { Notice("The mission is no longer in the pending queue. "); return; }
            ClearSelectedMission();
            Notice($"Mission {taskId} deleted. ");
        }

        private void ClearSelectedMission()
        {
            routeDesigner.SetSelectionMode(RouteSelectionMode.AutomaticShortest);
            missionQueueSignature = null;
            RefreshMissionQueuePanel();
        }

        private void ImportRouteWorkspace()
        {
            string path = SarPortablePath.Resolve(routeReadPathInput?.text?.Trim());
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            { Notice("The input file was not found. Check the path. "); return; }
            var result = manager.LoadRouteFile(path, true);
            missionQueueSignature = null;
            MissionTask first = manager.GetPendingMissions().FirstOrDefault();
            if (first != null) SelectMissionForEditing(first.TaskId);
            Notice($"Loaded {result.Missions.Count} missions; {result.Errors.Count} records contain errors. ");
        }

        private void PickRouteReadPath()
        {
            SarNativeFileDialog.OpenFileAsync(L("Select Routes and Traffic-Flow File"),
                "Route files|*.json;*.xml|JSON files|*.json|XML files|*.xml|All files|*.*",
                SarPortablePath.Resolve(routeReadPathInput?.text), path =>
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        routeReadPathInput?.SetTextWithoutNotify(SarPortablePath.ToPortable(path));
                });
        }

        private void PickSimulationRoutePath()
        {
            SarNativeFileDialog.OpenFileAsync(L("Select Simulation-Demand File"),
                "Route files|*.json;*.xml|JSON files|*.json|XML files|*.xml|All files|*.*",
                SarPortablePath.Resolve(routePathInput?.text), path =>
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        routePathInput?.SetTextWithoutNotify(SarPortablePath.ToPortable(path));
                });
        }

        private void PickRouteOutputPath()
        {
            string current = routeOutputPathInput?.text?.Trim();
            string extension = string.Equals(Path.GetExtension(current), ".xml", StringComparison.OrdinalIgnoreCase)
                ? "xml" : "json";
            SarNativeFileDialog.SaveFileAsync(L("Export Routes and Traffic-Flow File"),
                "JSON files|*.json|XML files|*.xml|All files|*.*",
                SarPortablePath.Resolve(current), extension, path =>
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        routeOutputPathInput?.SetTextWithoutNotify(SarPortablePath.ToPortable(path));
                });
        }

        private void ExportRouteWorkspace()
        {
            ExportRouteWorkspaceTo(routeOutputPathInput?.text?.Trim());
        }

        private void OverwriteRouteWorkspace()
        {
            ExportRouteWorkspaceTo(routeReadPathInput?.text?.Trim());
        }

        private void ExportRouteWorkspaceTo(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) { Notice("Output path cannot be empty. "); return; }
            try
            {
                string saved = manager.SaveMissions(SarPortablePath.Resolve(path));
                Notice($"Exported {manager.MissionQueue.Count} missions to {saved}. ");
            }
            catch (Exception exception)
            {
                Notice("Export failed: " + exception.Message + " ");
            }
        }

        private static void DestroyUiChildren(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
        }

        private void StartSimulation()
        {
            if (IsSimulationInteractionLocked())
            {
                Notice("A single or batch simulation is already running. ");
                return;
            }
            routeDesigner.LaneSelectionEnabled = false;
            SetEditorTool(RuntimeEditTool.Inspect);
            if (manager.config != null && manager.config.missionMode == MissionGenerationMode.JSON && manager.MissionQueue.Count == 0)
                LoadRouteFile();
            if (manager.config != null && manager.config.missionMode == MissionGenerationMode.JSON && manager.MissionQueue.Count == 0)
            { Notice("There is no executable route demand. "); return; }
            Time.timeScale = Mathf.Max(Time.timeScale, 0.25f);
            manager.StartSimulation();
            Notice("Simulation started. ");
            ApplySimulationInterlock(true);
        }

        private void StopSingleSimulation()
        {
            if (manager == null || !manager.IsRunning())
            {
                Notice("No single simulation is currently running. ");
                return;
            }
            manager.StopSimulation();
            Notice("Simulation stopped. ");
            ApplySimulationInterlock(true);
        }

        private void LoadRouteFile()
        {
            string path = SarPortablePath.Resolve(routePathInput?.text?.Trim());
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            { Notice("The route file was not found. Check its location. "); return; }
            var result = manager.LoadRouteFile(path, true);
            Notice($"Loaded {result.Missions.Count} missions; {result.Errors.Count} records contain errors. ");
        }

        private void SetMissionMode(MissionGenerationMode mode)
        {
            if (IsSimulationInteractionLocked())
            { Notice("Stop the single or batch simulation before changing demand mode. "); return; }
            if (manager.config != null) manager.config.missionMode = mode;
            RefreshMissionModeButtons();
            Notice($"Demand mode: {L(MissionModeName(mode))}. ");
        }

        private void RefreshMissionModeButtons()
        {
            MissionGenerationMode current = manager?.config != null
                ? manager.config.missionMode : MissionGenerationMode.JSON;
            foreach (var pair in missionModeButtons)
                SetButtonColor(pair.Value, pair.Key == current ? Accent : SurfaceSoft);
            if (routeFileSettingsPanel != null)
                routeFileSettingsPanel.SetActive(current == MissionGenerationMode.JSON);
            if (randomDemandSettingsPanel != null)
                randomDemandSettingsPanel.SetActive(current == MissionGenerationMode.RandomContinuous);
        }

        private void TogglePause()
        {
            if (Time.timeScale <= 0.001f)
            {
                Time.timeScale = Mathf.Clamp(resumeTimeScale, 0.25f, 4f);
                manager?.ResumeAfterCollisionPause();
            }
            else
            {
                resumeTimeScale = Time.timeScale;
                Time.timeScale = 0f;
            }
            Notice(Time.timeScale <= 0.001f ? "Simulation paused. " : "Simulation resumed. ");
        }

        private void SetTimeScale(float value)
        {
            float clamped = Mathf.Clamp(value, 0.25f, 4f);
            resumeTimeScale = clamped;
            Time.timeScale = clamped;
            manager?.ResumeAfterCollisionPause();
            if (timeScaleSlider != null && !Mathf.Approximately(timeScaleSlider.value, clamped))
                timeScaleSlider.SetValueWithoutNotify(clamped);
            if (speedText != null) SetLocalizedText(speedText, $"Time Scale  {clamped:F2}×");
        }

        private void ApplyRandomTurnaroundTime(string value)
        {
            if (manager?.config == null) return;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
                parsed < 0f || parsed > 3600f)
            {
                randomTurnaroundInput.SetTextWithoutNotify(
                    manager.config.randomTurnaroundTime.ToString("0.###", CultureInfo.InvariantCulture));
                Notice("Invalid random-demand turnaround time. Enter a value from 0 to 3600 seconds. ");
                return;
            }

            manager.config.randomTurnaroundTime = parsed;
            randomTurnaroundInput.SetTextWithoutNotify(parsed.ToString("0.###", CultureInfo.InvariantCulture));
            Notice($"Random-demand aircraft will remain for {parsed:0.###} seconds after arrival before departing. ");
        }

        private void ApplyRandomFleetSize(string value)
        {
            if (manager?.config == null) return;
            int safeCapacity = manager.GetRandomFleetCapacity();
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ||
                parsed < 1 || parsed > safeCapacity)
            {
                randomFleetSizeInput.SetTextWithoutNotify(
                    manager.config.maxConcurrentAircraft.ToString(CultureInfo.InvariantCulture));
                Notice($"Invalid random-fleet size. Enter 1–{safeCapacity} aircraft. Each initial mission requires exclusive origin and destination helipads. ");
                return;
            }

            manager.config.maxConcurrentAircraft = parsed;
            randomFleetSizeInput.SetTextWithoutNotify(parsed.ToString(CultureInfo.InvariantCulture));
            Notice(manager.IsRunning()
                ? $"Random-fleet size set to {parsed}; it will take effect at the next simulation start. "
                : $"Random-fleet size set to {parsed}. ");
            RefreshStatus();
        }

        private void ApplyRandomMaxDuration(string value)
        {
            if (manager == null) return;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
                parsed < 0f || parsed > 604800f)
            {
                randomMaxDurationInput.SetTextWithoutNotify(
                    (manager.maxSimDuration > 0f ? manager.maxSimDuration : 0f)
                    .ToString("0.###", CultureInfo.InvariantCulture));
                Notice("Invalid maximum random-demand duration. Enter 0–604800 seconds; 0 means unlimited. ");
                return;
            }
            manager.maxSimDuration = parsed <= 0f ? -1f : parsed;
            randomMaxDurationInput.SetTextWithoutNotify(parsed.ToString("0.###", CultureInfo.InvariantCulture));
            Notice(parsed <= 0f ? "Random-demand duration set to unlimited. " : $"Random demand will stop automatically after {parsed:0.###} seconds. ");
        }

        private void HandleCollisionPause(Vector3 point, string aircraftPair)
        {
            UseOverviewCamera(false);
            navigator?.FocusPoint(point, 35f);
            RefreshStatus();
            Notice($"Collision detected between {aircraftPair}; the simulation was paused and focused on the collision point. ");
        }

        private void FollowSelectedAircraft()
        {
            if (aircraftDropdown == null || aircraftDropdown.options.Count == 0)
            { Notice("No aircraft is currently available to track. "); return; }
            string id = DropdownSourceValue(aircraftDropdown);
            DroneFollowCamera controller = FindObjectOfType<DroneFollowCamera>(true);
            if (controller == null || !manager.AircraftObjects.TryGetValue(id, out GameObject aircraft) || aircraft == null)
            { Notice("The selected aircraft is not currently in the scene. "); return; }
            controller.FollowAircraft(id);
            followCamera = controller.GetComponent<Camera>();
            if (overviewCamera != null) overviewCamera.gameObject.SetActive(false);
            followCamera.gameObject.SetActive(true);
            followCamera.enabled = true;
            followCamera.targetDisplay = 0;
            Notice($"Tracking aircraft {id}. ");
        }

        private void RefreshHistoryList(bool force = false)
        {
            if (historyListContainer == null || simulationHistory == null) return;
            List<SarSimulationRunManifest> entries = simulationHistory.Manifests
                .Where(item => item != null && item.Kind != SarSimulationRunKind.BatchMember)
                .OrderByDescending(item => item.StartedUtc, StringComparer.Ordinal).ToList();
            var allIds = new HashSet<string>(simulationHistory.Manifests
                .Where(item => item != null).Select(item => item.RunId), StringComparer.Ordinal);
            selectedHistoryIds.RemoveWhere(id => !allIds.Contains(id));
            expandedHistoryBatchIds.RemoveWhere(id => !allIds.Contains(id));
            var noResultBatchIds = new HashSet<string>(entries
                .Where(BatchWasCancelledWithoutResults)
                .Select(item => item.RunId), StringComparer.Ordinal);
            selectedHistoryIds.RemoveWhere(noResultBatchIds.Contains);
            expandedHistoryBatchIds.RemoveWhere(noResultBatchIds.Contains);
            string signature = simulationHistory.CurrentSceneId + "|" + string.Join("|", entries.Select(item =>
                item.RunId + ":" + item.Status + ":" + (item.ChildRunIds?.Count ?? 0) + ":" +
                (item.BatchSummaryFile ?? string.Empty) + ":" +
                (expandedHistoryBatchIds.Contains(item.RunId) ? "open" : "closed") + ":" +
                string.Join(",", (item.ChildRunIds ?? new List<string>()).Select(childId =>
                {
                    SarSimulationRunManifest child = simulationHistory.FindManifest(childId);
                    return child == null ? childId : child.RunId + "-" + child.Status + "-" + child.BatchIndex;
                }))));

            if (force || signature != historyListSignature)
            {
                historyListSignature = signature;
                foreach (Transform child in historyListContainer.Cast<Transform>().ToList())
                {
                    child.gameObject.SetActive(false);
                    DestroyUiObject(child.gameObject);
                }

                if (entries.Count == 0)
                    Label(historyListContainer, "The current scene has no simulation history. Records appear automatically after a single or batch experiment completes.",
                        12, Muted, 48f);
                foreach (SarSimulationRunManifest entry in entries)
                {
                    if (entry.Kind != SarSimulationRunKind.Batch)
                    {
                        AddHistorySelectionToggle(historyListContainer, entry, HistoryEntryLabel(entry), 0f);
                        continue;
                    }

                    RectTransform batchGroup = EditorPanel(historyListContainer, "Batch_" + entry.RunId);
                    bool cancelledWithoutResults = BatchWasCancelledWithoutResults(entry);
                    RectTransform header = Rect("BatchHeader", batchGroup, Vector2.zero, Vector2.one,
                        Vector2.zero, Vector2.zero, Transparent);
                    float headerHeight = cancelledWithoutResults ? 76f : 58f;
                    header.gameObject.AddComponent<LayoutElement>().preferredHeight = headerHeight;
                    HorizontalLayoutGroup row = header.gameObject.AddComponent<HorizontalLayoutGroup>();
                    row.spacing = 6f;
                    row.childControlHeight = true;
                    row.childForceExpandHeight = true;
                    row.childControlWidth = true;
                    row.childForceExpandWidth = false;

                    bool expanded = expandedHistoryBatchIds.Contains(entry.RunId);
                    SarSimulationRunManifest capturedBatch = entry;
                    Button expandButton = Button(cancelledWithoutResults ? "—" : expanded ? "▾" : "▸", header, () =>
                    {
                        if (BatchWasCancelledWithoutResults(capturedBatch)) return;
                        if (!expandedHistoryBatchIds.Add(capturedBatch.RunId))
                            expandedHistoryBatchIds.Remove(capturedBatch.RunId);
                        historyListSignature = null;
                        RefreshHistoryList(true);
                    }, SurfaceSoft);
                    LayoutElement expandLayout = expandButton.gameObject.AddComponent<LayoutElement>();
                    expandLayout.preferredWidth = 38f;
                    expandLayout.minWidth = 38f;
                    expandButton.interactable = !cancelledWithoutResults;
                    string batchLabel = HistoryEntryLabel(entry);
                    if (cancelledWithoutResults)
                        batchLabel += "\nNo result artifacts — cancelled before any member run was archived.";
                    Toggle batchToggle = HistoryToggleField(header, batchLabel,
                        !cancelledWithoutResults && selectedHistoryIds.Contains(entry.RunId), enabled =>
                        {
                            if (BatchWasCancelledWithoutResults(capturedBatch)) return;
                            SetHistorySelection(capturedBatch.RunId, enabled);
                        }, headerHeight, 0f);
                    batchToggle.interactable = !cancelledWithoutResults;
                    batchToggle.GetComponent<LayoutElement>().flexibleWidth = 1f;

                    if (cancelledWithoutResults || !expanded) continue;
                    List<SarSimulationRunManifest> members = (entry.ChildRunIds ?? new List<string>())
                        .Select(simulationHistory.FindManifest).Where(item => item != null)
                        .OrderBy(item => item.BatchIndex).ThenBy(item => item.StartedUtc, StringComparer.Ordinal)
                        .ToList();
                    if (members.Count == 0)
                    {
                        Label(batchGroup, "No archived member runs are available for this batch.", 11, Muted, 38f);
                        continue;
                    }
                    List<string> factorNames = BatchFactorNames(entry, members);
                    foreach (SarSimulationRunManifest member in members)
                        AddHistorySelectionToggle(batchGroup, member,
                            BatchMemberEntryLabel(member, factorNames), 28f);
                }
            }
            RefreshHistoryAnalysisAvailability();
        }

        private static bool BatchWasCancelledWithoutResults(SarSimulationRunManifest manifest)
        {
            return manifest != null && manifest.Kind == SarSimulationRunKind.Batch &&
                   manifest.Status == SarSimulationRunStatus.Cancelled &&
                   (manifest.ChildRunIds == null || manifest.ChildRunIds.Count == 0) &&
                   string.IsNullOrWhiteSpace(manifest.BatchSummaryFile);
        }

        private void AddHistorySelectionToggle(Transform parent, SarSimulationRunManifest manifest,
            string label, float indent)
        {
            SarSimulationRunManifest captured = manifest;
            HistoryToggleField(parent, label, selectedHistoryIds.Contains(manifest.RunId),
                enabled => SetHistorySelection(captured.RunId, enabled),
                manifest.Kind == SarSimulationRunKind.BatchMember ? 64f : 48f, indent);
        }

        private void SetHistorySelection(string runId, bool enabled)
        {
            SarSimulationRunManifest manifest = simulationHistory?.FindManifest(runId);
            if (enabled)
            {
                selectedHistoryIds.Add(runId);
                // A batch parent already represents every member for statistics. Keeping
                // a parent and one stale child selected at the same time previously made
                // the 3D action appear to load the batch while it actually loaded that
                // lone child (often the last member clicked).
                if (manifest?.Kind == SarSimulationRunKind.Batch)
                    foreach (string childId in manifest.ChildRunIds ?? new List<string>())
                        selectedHistoryIds.Remove(childId);
                else if (manifest?.Kind == SarSimulationRunKind.BatchMember &&
                         !string.IsNullOrWhiteSpace(manifest.BatchId))
                    selectedHistoryIds.Remove(manifest.BatchId);
            }
            else
            {
                selectedHistoryIds.Remove(runId);
            }
            historyListSignature = null;
            RefreshHistoryList(true);
        }

        private Toggle HistoryToggleField(Transform parent, string label, bool value,
            Action<bool> changed, float height, float indent)
        {
            RectTransform root = Rect("HistorySelection", parent, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, Transparent);
            root.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            Toggle toggle = root.gameObject.AddComponent<Toggle>();
            RectTransform box = Rect("Checkbox", root, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(indent, -11f), new Vector2(indent + 22f, 11f), Surface);
            Outline outline = box.gameObject.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);
            RectTransform mark = Rect("Checkmark", box, Vector2.zero, Vector2.one,
                new Vector2(5f, 5f), new Vector2(-5f, -5f), Accent);
            Text labelText = Text(label, root, 11, FontStyle.Normal, TextAnchor.MiddleLeft,
                Ink, new Vector2(indent + 34f, 4f), new Vector2(-6f, -4f));
            labelText.horizontalOverflow = HorizontalWrapMode.Wrap;
            labelText.verticalOverflow = VerticalWrapMode.Truncate;
            toggle.targetGraphic = box.GetComponent<Image>();
            toggle.graphic = mark.GetComponent<Image>();
            toggle.isOn = value;
            toggle.onValueChanged.AddListener(enabled => changed?.Invoke(enabled));
            return toggle;
        }

        private string HistoryEntryLabel(SarSimulationRunManifest manifest)
        {
            DateTime.TryParse(manifest.StartedUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime started);
            string time = started == default(DateTime)
                ? "Unknown time"
                : started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (manifest.Kind == SarSimulationRunKind.Batch)
                return $"[Batch] {time} · {manifest.ChildRunIds.Count}/{manifest.BatchCount} member runs · " +
                       HistoryStatusLabel(manifest.Status) + ArtifactErrorSuffix(manifest);
            if (manifest.Kind == SarSimulationRunKind.BatchMember)
                return $"Run {Math.Max(1, manifest.BatchIndex):00}/{Math.Max(manifest.BatchIndex, manifest.BatchCount):00} · " +
                       $"{manifest.Duration:0.##} s · {HistoryStatusLabel(manifest.Status)}" +
                       ArtifactErrorSuffix(manifest);
            return $"[Single] {time} · {manifest.Duration:0.##} s · {HistoryStatusLabel(manifest.Status)}" +
                   ArtifactErrorSuffix(manifest);
        }

        private static List<string> BatchFactorNames(SarSimulationRunManifest batch,
            IReadOnlyList<SarSimulationRunManifest> members)
        {
            var names = new List<string>();
            if (batch?.Parameters != null)
            {
                foreach (KeyValuePair<string, string> pair in batch.Parameters
                             .Where(pair => pair.Key.StartsWith("factor", StringComparison.Ordinal) &&
                                            pair.Key.IndexOf('.', StringComparison.Ordinal) < 0)
                             .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    if (!string.IsNullOrWhiteSpace(pair.Value) && !names.Contains(pair.Value))
                        names.Add(pair.Value);
            }
            if (names.Count == 0)
                names.AddRange(SarHistoryAnalysisPlanner.CreatePlan(members).NumericVaryingParameters);
            return names;
        }

        private string BatchMemberEntryLabel(SarSimulationRunManifest member,
            IReadOnlyList<string> factorNames)
        {
            string factors = string.Join(" · ", (factorNames ?? Array.Empty<string>())
                .Where(name => member.Parameters != null && member.Parameters.ContainsKey(name))
                .Select(name => L(name) + " = " + member.Parameters[name]));
            string firstLine = $"Run {Math.Max(1, member.BatchIndex):00}/{Math.Max(member.BatchIndex, member.BatchCount):00} · " +
                               $"{member.Duration:0.##} s · {HistoryStatusLabel(member.Status)}";
            return string.IsNullOrWhiteSpace(factors) ? firstLine : firstLine + "\n" + factors;
        }

        private static string ArtifactErrorSuffix(SarSimulationRunManifest manifest)
        {
            int count = manifest?.ArtifactErrors?.Count ?? 0;
            return count > 0 ? $" · {count} archive warnings" : string.Empty;
        }

        private static string HistoryStatusLabel(SarSimulationRunStatus status)
        {
            switch (status)
            {
                case SarSimulationRunStatus.Completed: return "Completed";
                case SarSimulationRunStatus.Stopped: return "Stopped Manually";
                case SarSimulationRunStatus.Cancelled: return "Cancelled";
                case SarSimulationRunStatus.Failed: return "Failed";
                case SarSimulationRunStatus.Interrupted: return "Interrupted";
                default: return "Running";
            }
        }

        private List<SarSimulationRunManifest> GetExplicitSelectedHistoryItems()
        {
            if (simulationHistory == null) return new List<SarSimulationRunManifest>();
            return simulationHistory.Manifests
                .Where(item => item != null && selectedHistoryIds.Contains(item.RunId))
                .OrderBy(item => item.StartedUtc, StringComparer.Ordinal).ToList();
        }

        private void InvalidateHistoryAnalysisContext()
        {
            unchecked { historyAnalysisGeneration++; }
            historyAnalysisBusy = false;
        }

        private int BeginHistoryAnalysisContext()
        {
            unchecked { historyAnalysisGeneration++; }
            historyAnalysisBusy = true;
            return historyAnalysisGeneration;
        }

        private bool IsHistoryAnalysisContextCurrent(int generation, string sceneId)
        {
            return this != null && simulationHistory != null &&
                   currentPageIndex == 3 &&
                   generation == historyAnalysisGeneration &&
                   string.Equals(sceneId, simulationHistory.CurrentSceneId, StringComparison.Ordinal);
        }

        private bool IsSensitivityContextCurrent(int generation, string sceneId,
            IReadOnlyList<string> runIds, IReadOnlyList<string> factors, IReadOnlyList<string> metrics)
        {
            if (!IsHistoryAnalysisContextCurrent(generation, sceneId)) return false;
            List<SarSimulationRunManifest> currentRuns = ResolveSelectedAnalysisRuns()
                .Where(item => item.Status == SarSimulationRunStatus.Completed).ToList();
            List<string> currentRunIds = currentRuns.Select(item => item.RunId)
                .OrderBy(item => item, StringComparer.Ordinal).ToList();
            if (!currentRunIds.SequenceEqual(runIds ?? Array.Empty<string>(), StringComparer.Ordinal))
                return false;

            SarHistoryAnalysisPlan currentPlan = SarHistoryAnalysisPlanner.CreatePlan(currentRuns);
            List<string> currentFactors = currentPlan.NumericVaryingParameters
                .Where(selectedHistoryFactors.Contains).ToList();
            List<string> currentMetrics = currentPlan.CommonMetrics
                .Where(selectedHistoryMetrics.Contains).ToList();
            return currentFactors.SequenceEqual(factors ?? Array.Empty<string>(), StringComparer.Ordinal) &&
                   currentMetrics.SequenceEqual(metrics ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        private bool IsRunComparisonContextCurrent(int generation, string sceneId,
            IReadOnlyList<string> runIds, IReadOnlyList<string> metrics)
        {
            if (!IsHistoryAnalysisContextCurrent(generation, sceneId)) return false;
            List<SarSimulationRunManifest> currentRuns = ResolveSelectedAnalysisRuns()
                .Where(item => item.Status == SarSimulationRunStatus.Completed).ToList();
            List<string> currentRunIds = currentRuns.Select(item => item.RunId)
                .OrderBy(item => item, StringComparer.Ordinal).ToList();
            if (!currentRunIds.SequenceEqual(runIds ?? Array.Empty<string>(), StringComparer.Ordinal))
                return false;
            SarHistoryAnalysisPlan currentPlan = SarHistoryAnalysisPlanner.CreatePlan(currentRuns);
            List<string> currentMetrics = currentPlan.CommonMetrics
                .Where(selectedHistoryMetrics.Contains).ToList();
            return currentMetrics.SequenceEqual(metrics ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        private List<SarSimulationRunManifest> ResolveSelectedAnalysisRuns()
        {
            if (simulationHistory == null) return new List<SarSimulationRunManifest>();
            var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (SarSimulationRunManifest selected in GetExplicitSelectedHistoryItems())
            {
                if (selected.Kind == SarSimulationRunKind.Batch)
                {
                    foreach (string childId in selected.ChildRunIds ?? new List<string>())
                        if (!string.IsNullOrWhiteSpace(childId)) resolvedIds.Add(childId);
                }
                else
                {
                    resolvedIds.Add(selected.RunId);
                }
            }
            return resolvedIds.Select(simulationHistory.FindManifest)
                .Where(item => item != null && item.Kind != SarSimulationRunKind.Batch &&
                               item.Status != SarSimulationRunStatus.Running)
                .OrderBy(item => item.StartedUtc, StringComparer.Ordinal).ToList();
        }

        private SarSimulationRunManifest ResolveSelectedConcrete3DRun()
        {
            List<SarSimulationRunManifest> explicitItems = GetExplicitSelectedHistoryItems();
            if (explicitItems.Count != 1) return null;
            SarSimulationRunManifest selected = explicitItems[0];
            return selected.Kind != SarSimulationRunKind.Batch &&
                   selected.Status != SarSimulationRunStatus.Running &&
                   !string.IsNullOrWhiteSpace(selected.RecordingFile)
                ? selected
                : null;
        }

        private void RefreshHistoryAnalysisAvailability()
        {
            if (historySelectionSummary == null) return;
            List<SarSimulationRunManifest> explicitItems = GetExplicitSelectedHistoryItems();
            List<SarSimulationRunManifest> runs = ResolveSelectedAnalysisRuns();
            SarHistoryAnalysisPlan plan = SarHistoryAnalysisPlanner.CreatePlan(runs);
            selectedHistoryMetrics.RemoveWhere(metric => !plan.CommonMetrics.Contains(metric));
            selectedHistoryFactors.RemoveWhere(factor => !plan.NumericVaryingParameters.Contains(factor));
            if (selectedHistoryMetrics.Count == 0 && plan.CommonMetrics.Count > 0)
                selectedHistoryMetrics.Add(plan.CommonMetrics[0]);
            if (selectedHistoryFactors.Count == 0)
                foreach (string factor in plan.NumericVaryingParameters.Take(3))
                    selectedHistoryFactors.Add(factor);
            RefreshHistoryDimensionSelectors(plan);

            SarSimulationRunManifest concrete3DRun = ResolveSelectedConcrete3DRun();
            bool hasResultTable = explicitItems.Any(item =>
            {
                SarCsvTable table = simulationHistory.LoadResultTable(item);
                return table != null && table.Rows.Count > 0;
            });
            int completedRunCount = runs.Count(item => item.Status == SarSimulationRunStatus.Completed);
            bool simulationActive = manager != null && manager.IsRunning();
            bool batchActive = sweepRunner != null && sweepRunner.isRunning;
            if (historyLoad3DButton != null)
                historyLoad3DButton.interactable = !historyAnalysisBusy && !simulationActive &&
                                                   !batchActive && explicitItems.Count > 0;
            if (historyTableButton != null)
                historyTableButton.interactable = !historyAnalysisBusy && hasResultTable;
            if (historyBarButton != null)
                historyBarButton.interactable = !historyAnalysisBusy && completedRunCount >= 2 &&
                                               selectedHistoryMetrics.Count > 0;
            if (historySensitivityButton != null)
                historySensitivityButton.interactable = !historyAnalysisBusy && completedRunCount >= 2 &&
                    selectedHistoryMetrics.Count > 0 && selectedHistoryFactors.Count > 0 &&
                    selectedHistoryFactors.Count <= 3;
            if (historyTrafficHeatmapButton != null)
                historyTrafficHeatmapButton.interactable = !historyAnalysisBusy && !simulationActive &&
                    !batchActive && concrete3DRun != null;
            if (historyTrafficShowAllLayersButton != null)
                historyTrafficShowAllLayersButton.interactable = !historyAnalysisBusy &&
                    !simulationActive && !batchActive;
            if (historyDeleteButton != null)
                historyDeleteButton.interactable = !historyAnalysisBusy && !simulationActive &&
                    !batchActive && explicitItems.Count == 1 &&
                    explicitItems[0].Kind != SarSimulationRunKind.BatchMember;
            RefreshTrafficHeatmapLayerControls();

            int parentSelections = selectedHistoryIds.Count;
            string durationNote = plan.HasDurationMismatch
                ? " · Different durations: cumulative metrics normalized per second"
                : string.Empty;
            SetLocalizedText(historySelectionSummary,
                $"Scene: {simulationHistory.CurrentSceneName}\n" +
                $"Records: {simulationHistory.Manifests.Count(item => item.Kind != SarSimulationRunKind.BatchMember)} · " +
                $"Selected: {parentSelections} explicit items / {runs.Count} analysis runs · " +
                $"Comparable: {completedRunCount} runs{durationNote}\n" +
                (concrete3DRun != null
                    ? $"3D target: {HistoryEntryLabel(concrete3DRun)}"
                    : "3D target: expand a batch and select exactly one member (or select one single run)"));
        }

        private void ToggleHistoryMetricChecklist()
        {
            if (historyMetricChecklistPanel == null) return;
            historyMetricChecklistPanel.gameObject.SetActive(!historyMetricChecklistPanel.gameObject.activeSelf);
            historySelectorSignature = null;
            RefreshHistoryAnalysisAvailability();
        }

        private void ToggleHistoryFactorChecklist()
        {
            if (historyFactorChecklistPanel == null) return;
            historyFactorChecklistPanel.gameObject.SetActive(!historyFactorChecklistPanel.gameObject.activeSelf);
            historySelectorSignature = null;
            RefreshHistoryAnalysisAvailability();
        }

        private void RefreshHistoryDimensionSelectors(SarHistoryAnalysisPlan plan)
        {
            if (historyMetricChecklistPanel == null || historyFactorChecklistPanel == null) return;
            string signature = string.Join("|", plan.CommonMetrics) + "::" +
                               string.Join("|", plan.NumericVaryingParameters) + "::" +
                               string.Join("|", selectedHistoryMetrics.OrderBy(item => item)) + "::" +
                               string.Join("|", selectedHistoryFactors.OrderBy(item => item)) + "::" +
                               historyMetricChecklistPanel.gameObject.activeSelf + "::" +
                               historyFactorChecklistPanel.gameObject.activeSelf;
            if (signature != historySelectorSignature)
            {
                historySelectorSignature = signature;
                ClearUiChildren(historyMetricChecklistPanel);
                if (plan.CommonMetrics.Count == 0)
                    Label(historyMetricChecklistPanel, "No finite metric is shared by the selected runs.",
                        11, Muted, 38f);
                foreach (IGrouping<string, string> metricGroup in plan.CommonMetrics
                             .GroupBy(HistoryMetricGroupName)
                             .OrderBy(group => HistoryMetricGroupOrder(group.Key)))
                {
                    RectTransform metricGroupBody = HistoryMetricGroupSection(
                        historyMetricChecklistPanel, metricGroup.Key);
                    foreach (string metric in metricGroup)
                    {
                        string captured = metric;
                        ToggleField(metricGroupBody, metric,
                            selectedHistoryMetrics.Contains(metric), enabled =>
                        {
                            if (enabled && !selectedHistoryMetrics.Contains(captured) &&
                                selectedHistoryMetrics.Count >= MaximumHistoryOutputMetrics)
                            {
                                Notice($"At most {MaximumHistoryOutputMetrics} output metrics can be shown in one figure. " +
                                       "Unselect one metric before adding another; multi-metric analysis remains available within this limit.");
                                historySelectorSignature = null;
                                RefreshHistoryAnalysisAvailability();
                                return;
                            }
                            if (enabled) selectedHistoryMetrics.Add(captured);
                            else selectedHistoryMetrics.Remove(captured);
                            historySelectorSignature = null;
                            RefreshHistoryAnalysisAvailability();
                        });
                    }
                }

                ClearUiChildren(historyFactorChecklistPanel);
                if (plan.NumericVaryingParameters.Count == 0)
                    Label(historyFactorChecklistPanel, "No numeric input parameter varies across the selected runs.",
                        11, Muted, 38f);
                foreach (string factor in plan.NumericVaryingParameters)
                {
                    string captured = factor;
                    ToggleField(historyFactorChecklistPanel, factor,
                        selectedHistoryFactors.Contains(factor), enabled =>
                        {
                            if (enabled && !selectedHistoryFactors.Contains(captured) &&
                                selectedHistoryFactors.Count >= 3)
                            {
                                Notice("At most three input parameters can be mapped to spatial axes. " +
                                       "Unselect one parameter first; remaining parameters are kept as explicit contexts rather than silently averaged.");
                                historySelectorSignature = null;
                                RefreshHistoryAnalysisAvailability();
                                return;
                            }
                            if (enabled) selectedHistoryFactors.Add(captured);
                            else selectedHistoryFactors.Remove(captured);
                            historySelectorSignature = null;
                            RefreshHistoryAnalysisAvailability();
                        });
                }
            }

            Text metricLabel = historyMetricSelectorButton != null
                ? historyMetricSelectorButton.GetComponentInChildren<Text>(true) : null;
            if (metricLabel != null)
                SetLocalizedText(metricLabel,
                    $"Select output metrics ({selectedHistoryMetrics.Count}/{plan.CommonMetrics.Count}) " +
                    (historyMetricChecklistPanel.gameObject.activeSelf ? "▴" : "▾"));
            Text factorLabel = historyFactorSelectorButton != null
                ? historyFactorSelectorButton.GetComponentInChildren<Text>(true) : null;
            if (factorLabel != null)
                SetLocalizedText(factorLabel,
                    $"Select input parameters ({selectedHistoryFactors.Count}/3) " +
                    (historyFactorChecklistPanel.gameObject.activeSelf ? "▴" : "▾"));
        }

        private RectTransform HistoryMetricGroupSection(Transform parent, string title)
        {
            RectTransform section = EditorPanel(parent, "MetricGroup_" + title.Replace(" ", string.Empty));
            bool expanded = expandedHistoryMetricGroups.Contains(title);
            Button header = FullButton(section, string.Empty, null, SurfaceSoft);
            Text headerLabel = header.GetComponentInChildren<Text>(true);
            RectTransform body = EditorPanel(section, "MetricGroupBody");

            void ApplyState()
            {
                body.gameObject.SetActive(expanded);
                SetLocalizedText(headerLabel, (expanded ? "▾  " : "▸  ") + title);
            }

            header.onClick.AddListener(() =>
            {
                expanded = !expanded;
                if (expanded) expandedHistoryMetricGroups.Add(title);
                else expandedHistoryMetricGroups.Remove(title);
                ApplyState();
                if (section.parent is RectTransform parentRect)
                    LayoutRebuilder.MarkLayoutForRebuild(parentRect);
            });
            ApplyState();
            return body;
        }

        private static string HistoryMetricGroupName(string metric)
        {
            string value = metric ?? string.Empty;
            if (value.IndexOf("Collision", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Safety and Collisions";
            if (value.IndexOf("Utilization", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Booking", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Booked Resource", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Resource Wait", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Resource Utilization";
            if (value.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Detour", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Mission Duration", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Headway Distance", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Mobility Efficiency";
            if (value.IndexOf("Throughput", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Mission", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(value, "Successful", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Failed", StringComparison.OrdinalIgnoreCase) ||
                value.IndexOf("Failure Rate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Simulation Duration", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Mission Outcomes";
            return "Diagnostics and Samples";
        }

        private static int HistoryMetricGroupOrder(string group)
        {
            switch (group)
            {
                case "Mission Outcomes": return 0;
                case "Mobility Efficiency": return 1;
                case "Resource Utilization": return 2;
                case "Safety and Collisions": return 3;
                default: return 4;
            }
        }

        private void OpenHistoryFolder()
        {
            if (simulationHistory == null) return;
            Directory.CreateDirectory(simulationHistory.HistoryRoot);
            Application.OpenURL("file:///" + simulationHistory.HistoryRoot.Replace('\\', '/'));
        }

        private async void LoadSelectedHistoryInto3D()
        {
            if ((manager != null && manager.IsRunning()) ||
                (sweepRunner != null && sweepRunner.isRunning))
            {
                Notice("Stop the current single or batch simulation before loading an archived 3D result. ");
                return;
            }

            SarSimulationRunManifest run = ResolveSelectedConcrete3DRun();
            if (run == null)
            {
                int leafSelections = GetExplicitSelectedHistoryItems()
                    .Count(item => item.Kind != SarSimulationRunKind.Batch);
                Notice(leafSelections == 0
                    ? "A batch parent has no single 3D state. Expand the batch and select one member run with the desired parameter values. "
                    : "Select exactly one single run or one batch member before loading a 3D result. Statistical charts may still combine multiple runs. ");
                return;
            }

            SarSimulationHistory history = simulationHistory;
            string sceneId = history.CurrentSceneId;
            string runId = run.RunId;
            string recordingPath = history.ResolveRecordingPath(run);
            if (string.IsNullOrWhiteSpace(recordingPath) || !File.Exists(recordingPath))
            {
                Notice("The selected run contains no loadable recording. Wait for it to finish or select another member. ");
                return;
            }

            string occupancyPath = history.ResolveOccupancyPath(run);
            int generation = BeginHistoryAnalysisContext();
            RefreshHistoryAnalysisAvailability();
            Notice($"Loading run {Math.Max(1, run.BatchIndex)} in the background; only lane entries and downsampled completed-mission trajectories are retained. ");
            try
            {
                Tuple<Network, List<Vertiport>, SarHistoricalVisualizationData> loaded = await Task.Run(() => Tuple.Create(
                    history.LoadFullNetworkSnapshot(run),
                    history.LoadVertiportsSnapshot(run),
                    SarSimulationHistory.LoadHistoricalVisualizationData(recordingPath, occupancyPath, 320)));
                SarSimulationRunManifest currentRun = ResolveSelectedConcrete3DRun();
                if (!IsHistoryAnalysisContextCurrent(generation, sceneId) || visualizer == null ||
                    currentRun == null || !string.Equals(currentRun.RunId, runId, StringComparison.Ordinal))
                    return;

                Network historicalTopology = loaded.Item1;
                List<Vertiport> historicalVertiports = loaded.Item2;
                SarHistoricalVisualizationData data = loaded.Item3;
                // Apply only after every archive artifact has loaded successfully. This makes
                // replacement atomic: a corrupt/new selection never destroys the last valid
                // visualization or the current trajectory catalogue.
                ClearLoadedHistoricalResult();
                loadedHistoricalNetwork = historicalTopology;
                loadedHistoricalVertiports = historicalVertiports ?? new List<Vertiport>();
                loadedHistoricalVisualizationData = data;
                visualizer.SetHistoricalTopology(historicalTopology);
                visualizer.SetHistoricalGroundGeometry(historicalVertiports);
                visualizer.SetHistoricalVisualizationData(data);
                ApplyPageAltitudeLayerVisibility(3);
                IReadOnlyList<string> historicalTrackIds = analytics.ReplaceWithHistoricalTracks(
                    data.Trajectories, HistoryEntryLabel(run));
                selectedTrajectoryIds.Clear();
                foreach (string id in historicalTrackIds) selectedTrajectoryIds.Add(id);
                loadedHistoricalRunId = runId;
                historyTrafficLayerSignature = null;
                RefreshTrafficHeatmapLayerControls(true);
                trajectoryChecklistSignature = null;
                RefreshTrajectoryChecklist(true);
                if (!visualizer.IsVisible(AirspaceVisualLayer.Network)) ToggleLayer(AirspaceVisualLayer.Network);
                SetDataVisualizationHeatmapEnabled(true, false);
                if (data.Trajectories.Count > 0 && !visualizer.IsVisible(AirspaceVisualLayer.RoutePreview))
                    ToggleLayer(AirspaceVisualLayer.RoutePreview);
                HideHistoryAnalysisSurface();
                int matched = visualizer.HistoricalMatchedLaneCount;
                int total = visualizer.HistoricalRecordedLaneCount;
                string topologyNote = historicalTopology != null
                    ? "using the archived lane topology"
                    : "archived lane topology unavailable; lane heat cannot be rendered";
                string groundNote = historicalVertiports != null && historicalVertiports.Count > 0
                    ? "using the archived ground snapshot"
                    : "archived ground snapshot unavailable";
                Notice($"Loaded one concrete run: matched {matched}/{total} lanes; heat is lane traversals and resource-reservation seconds; " +
                       $"retained {data.RetainedTrajectoryPointCount} points from {data.CompletedTrajectoryCount} completed trajectories " +
                       $"and excluded {data.ExcludedIncompleteTrajectoryCount} unfinished plus " +
                       $"{data.ExcludedUnverifiableTrajectoryCount} unverifiable legacy trajectories ({data.SourceTrajectoryCount} recorded); " +
                       $"{topologyNote}; {groundNote}. ");
            }
            catch (Exception ex)
            {
                SarSimulationRunManifest currentRun = ResolveSelectedConcrete3DRun();
                if (!IsHistoryAnalysisContextCurrent(generation, sceneId) || currentRun == null ||
                    !string.Equals(currentRun.RunId, runId, StringComparison.Ordinal)) return;
                Debug.LogError($"[SAR History] Compact 3D load failed: {ex}");
                Notice("The selected 3D result could not be loaded: " + ex.Message);
            }
            finally
            {
                if (this != null && generation == historyAnalysisGeneration)
                {
                    historyAnalysisBusy = false;
                    RefreshHistoryAnalysisAvailability();
                }
            }
        }

        private void ClearLoadedHistoricalResult(bool resetHeatmapLayerVisibility = true)
        {
            loadedHistoricalRunId = null;
            loadedHistoricalNetwork = null;
            loadedHistoricalVertiports = null;
            loadedHistoricalVisualizationData = null;
            historyTrafficLayerSignature = null;
            visualizer?.ClearHistoricalAnalysis();
            if (resetHeatmapLayerVisibility)
                visualizer?.ResetHeatmapLayerVisibility();
            analytics?.ClearTracks();
            selectedTrajectoryIds.Clear();
            trajectoryChecklistSignature = null;
            RefreshTrajectoryChecklist(true);
            RefreshTrafficHeatmapLayerControls(true);
        }

        private static SimulationRecording MergeRecordings(IEnumerable<SimulationRecording> recordings)
        {
            var merged = new SimulationRecording
            {
                LaneTimeSeries = new List<LaneTimeSeriesRecord>(),
                CorridorTimeSeries = new List<CorridorTimeSeriesRecord>(),
                RampTimeSeries = new List<RampTimeSeriesRecord>(),
                ApproachEdgeTimeSeries = new List<ApproachEdgeTimeSeriesRecord>(),
                AircraftTrajectories = new List<AircraftTrajectoryRecord>(),
                Events = new List<SimulationEvent>()
            };
            foreach (SimulationRecording recording in recordings ?? Enumerable.Empty<SimulationRecording>())
            {
                if (recording == null) continue;
                merged.LaneTimeSeries.AddRange(recording.LaneTimeSeries ?? new List<LaneTimeSeriesRecord>());
                merged.CorridorTimeSeries.AddRange(recording.CorridorTimeSeries ?? new List<CorridorTimeSeriesRecord>());
                merged.RampTimeSeries.AddRange(recording.RampTimeSeries ?? new List<RampTimeSeriesRecord>());
                merged.ApproachEdgeTimeSeries.AddRange(recording.ApproachEdgeTimeSeries ?? new List<ApproachEdgeTimeSeriesRecord>());
                merged.AircraftTrajectories.AddRange(recording.AircraftTrajectories ?? new List<AircraftTrajectoryRecord>());
                merged.Events.AddRange(recording.Events ?? new List<SimulationEvent>());
            }
            return merged;
        }

        private void ShowSelectedHistoryTable()
        {
            List<Tuple<SarSimulationRunManifest, SarCsvTable>> tables = GetExplicitSelectedHistoryItems()
                .Select(item => Tuple.Create(item, simulationHistory.LoadResultTable(item)))
                .Where(item => item.Item2 != null && item.Item2.Rows.Count > 0).ToList();
            if (tables.Count == 0)
            {
                Notice("The selected records contain no archived result table. Select a batch parent for its BatchExperimentRunSummaryTable, or a concrete run for its MetricsTable. ");
                return;
            }
            historyTablePages.Clear();
            foreach (Tuple<SarSimulationRunManifest, SarCsvTable> item in tables)
            {
                SarSimulationRunManifest run = item.Item1;
                SarCsvTable table = item.Item2;
                int pageCount = run.Kind == SarSimulationRunKind.Batch
                    ? Math.Max(1, (table.ColumnCount + 2) / 3)
                    : 1;
                for (int page = 0; page < pageCount; page++)
                    historyTablePages.Add(new HistoryTablePage
                    {
                        Run = run,
                        Table = table,
                        ColumnStart = run.Kind == SarSimulationRunKind.Batch ? page * 3 : 0,
                        SourcePageNumber = page + 1,
                        SourcePageCount = pageCount
                    });
            }
            int batchCount = tables.Count(item => item.Item1.Kind == SarSimulationRunKind.Batch);
            ShowHistoryAnalysisSurface("Archived Result Tables",
                $"{historyTablePages.Count} page(s) · {batchCount} batch summary table(s) · one experiment per page", false);
            SetHistoryTablePage(0);
        }

        private void SetHistoryTablePage(int pageIndex)
        {
            if (historyTablePages.Count == 0 || historyTableContent == null) return;
            historyTablePageIndex = Mathf.Clamp(pageIndex, 0, historyTablePages.Count - 1);
            HistoryTablePage page = historyTablePages[historyTablePageIndex];
            ClearUiChildren(historyTableContent);
            Canvas.ForceUpdateCanvases();
            float contentWidth = Mathf.Max(780f, (historyTableViewport != null
                ? historyTableViewport.rect.width : 1120f) - 8f);
            const float titleHeight = 42f;
            float y = 8f;
            string tableName = page.Run.Kind == SarSimulationRunKind.Batch
                ? "BatchExperimentRunSummaryTable.csv"
                : "MetricsTable.csv";
            string experimentLabel = page.Run.Kind == SarSimulationRunKind.Batch
                ? $"Batch {page.SourcePageNumber}/{page.SourcePageCount}"
                : HistoryEntryLabel(page.Run);
            AddHistoryGridText($"{experimentLabel}  ·  {tableName}", 8f, y,
                contentWidth - 16f, titleHeight, 14, FontStyle.Bold,
                TextAnchor.MiddleLeft, Ink, Transparent);
            y += titleHeight;
            if (page.Run.ArtifactErrors != null && page.Run.ArtifactErrors.Count > 0)
            {
                AddHistoryGridText("Archive warnings: " + string.Join("; ", page.Run.ArtifactErrors),
                    8f, y, contentWidth - 16f, 38f, 10, FontStyle.Normal,
                    TextAnchor.MiddleLeft, Red, Hex("F4DFDA"));
                y += 42f;
            }

            foreach (HistoryTableSection section in ExtractHistoryTableSections(page))
                RenderHistoryTableSection(section, contentWidth, ref y);

            historyTableContent.sizeDelta = new Vector2(contentWidth, Mathf.Max(1f, y + 12f));
            historyTablePageInput?.SetTextWithoutNotify(
                $"{historyTablePageIndex + 1}/{historyTablePages.Count}");
            SetLocalizedText(historyTablePageTotalLabel,
                page.Run.Kind == SarSimulationRunKind.Batch
                    ? $"Batch {page.SourcePageNumber}/{page.SourcePageCount}"
                    : $"Page {historyTablePageIndex + 1}/{historyTablePages.Count}");
            if (historyTablePreviousButton != null)
                historyTablePreviousButton.interactable = historyTablePageIndex > 0;
            if (historyTableNextButton != null)
                historyTableNextButton.interactable = historyTablePageIndex + 1 < historyTablePages.Count;
            ResetTableView();
            SetLocalizedText(historyAnalysisSubtitle,
                $"Page {historyTablePageIndex + 1}/{historyTablePages.Count} · {experimentLabel} · one experiment per page");
        }

        private static List<HistoryTableSection> ExtractHistoryTableSections(HistoryTablePage page)
        {
            var sections = new List<HistoryTableSection>();
            HistoryTableSection current = null;
            int keyColumn = page.ColumnStart;
            int valueColumn = page.ColumnStart + 1;
            foreach (List<string> row in page.Table.Rows ?? new List<List<string>>())
            {
                string key = keyColumn < (row?.Count ?? 0) ? row[keyColumn]?.Trim() : string.Empty;
                string value = valueColumn < (row?.Count ?? 0) ? row[valueColumn]?.Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (key.StartsWith("Batch", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(value)) continue;
                if (string.Equals(key, "Parameters", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "Metrics", StringComparison.OrdinalIgnoreCase))
                {
                    current = new HistoryTableSection { Name = key };
                    sections.Add(current);
                    continue;
                }
                if (current == null)
                {
                    current = new HistoryTableSection { Name = "Metrics" };
                    sections.Add(current);
                }
                current.Entries.Add(new KeyValuePair<string, string>(key, value));
            }
            return sections;
        }

        private void RenderHistoryTableSection(HistoryTableSection section, float contentWidth,
            ref float y)
        {
            if (section == null || section.Entries.Count == 0) return;
            const float sectionHeight = 31f;
            const float rowHeight = 28f;
            const float gap = 18f;
            AddHistoryGridText(section.Name, 8f, y, contentWidth - 16f, sectionHeight,
                10, FontStyle.Bold, TextAnchor.MiddleLeft, Color.white, InkSoft);
            y += sectionHeight + 4f;

            float columnWidth = (contentWidth - 16f - gap) * 0.5f;
            int rowsPerColumn = (section.Entries.Count + 1) / 2;
            for (int rowIndex = 0; rowIndex < rowsPerColumn; rowIndex++)
            {
                RenderHistoryKeyValue(section.Entries[rowIndex], 8f, y + rowIndex * rowHeight,
                    columnWidth, rowIndex, rowHeight);
                int rightIndex = rowIndex + rowsPerColumn;
                if (rightIndex < section.Entries.Count)
                    RenderHistoryKeyValue(section.Entries[rightIndex], 8f + columnWidth + gap,
                        y + rowIndex * rowHeight, columnWidth, rowIndex, rowHeight);
            }
            y += rowsPerColumn * rowHeight + 12f;
        }

        private void RenderHistoryKeyValue(KeyValuePair<string, string> entry, float x, float y,
            float width, int rowIndex, float rowHeight)
        {
            float keyWidth = width * 0.72f;
            Color background = rowIndex % 2 == 0 ? Surface : SurfaceSoft;
            AddHistoryGridText(entry.Key, x, y, keyWidth, rowHeight, 9, FontStyle.Normal,
                TextAnchor.MiddleLeft, Ink, background);
            AddHistoryGridText(entry.Value, x + keyWidth, y, width - keyWidth, rowHeight, 9,
                FontStyle.Normal, TextAnchor.MiddleRight, Ink, background);
        }

        private void JumpToHistoryTablePage()
        {
            string text = historyTablePageInput?.text?.Trim() ?? string.Empty;
            int separator = text.IndexOf('/');
            if (separator >= 0) text = text.Substring(0, separator).Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int page) &&
                page >= 1 && page <= historyTablePages.Count)
            {
                SetHistoryTablePage(page - 1);
                return;
            }
            Notice($"Enter a page number from 1 to {Math.Max(1, historyTablePages.Count)}. ");
            historyTablePageInput?.SetTextWithoutNotify(
                $"{historyTablePageIndex + 1}/{Math.Max(1, historyTablePages.Count)}");
        }

        private static string MetricSortKey(string metric)
        {
            if (metric == "Simulation Duration (s)") return "0000";
            if (metric != null && metric.StartsWith("Throughput", StringComparison.Ordinal)) return "0001" + metric;
            return "1000" + metric;
        }

        private static float HistoryTableColumnWidth(int column)
        {
            switch (column % 3)
            {
                case 0: return 220f;
                case 1: return 118f;
                default: return 16f;
            }
        }

        private static float TableWidth(int columns)
        {
            float width = 0f;
            for (int column = 0; column < columns; column++) width += HistoryTableColumnWidth(column);
            return width;
        }

        private void AddHistoryGridText(string value, float x, float y, float width, float height,
            int fontSize, FontStyle style, TextAnchor alignment, Color foreground, Color background)
        {
            RectTransform cell = Rect("CsvCell", historyTableContent, new Vector2(0f, 1f),
                new Vector2(0f, 1f), Vector2.zero, Vector2.zero, background);
            cell.pivot = new Vector2(0f, 1f);
            cell.anchoredPosition = new Vector2(x, -y);
            cell.sizeDelta = new Vector2(Mathf.Max(1f, width - 1f), Mathf.Max(1f, height - 1f));
            Image cellImage = cell.GetComponent<Image>();
            if (cellImage != null) cellImage.raycastTarget = false;
            Text text = Text(value, cell, fontSize, style, alignment, foreground,
                new Vector2(6f, 2f), new Vector2(-6f, -2f));
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
        }

        private RectTransform AnalysisToolbar(Transform parent, string name)
        {
            RectTransform toolbar = Rect(name, parent, new Vector2(0f, 1f), Vector2.one,
                new Vector2(12f, -44f), new Vector2(-12f, -4f), SurfaceSoft);
            HorizontalLayoutGroup layout = toolbar.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 3, 3);
            layout.spacing = 5f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = true;
            return toolbar;
        }

        private Button ToolbarButton(Transform parent, string label, Action action, float width)
        {
            Button button = Button(label, parent, action, Surface);
            SetToolbarWidth(button.gameObject, width);
            return button;
        }

        private static void SetToolbarWidth(GameObject item, float width)
        {
            if (item == null) return;
            LayoutElement layout = item.GetComponent<LayoutElement>() ?? item.AddComponent<LayoutElement>();
            layout.minWidth = width;
            layout.preferredWidth = width;
            layout.flexibleWidth = 0f;
        }

        private void ToolbarSpacer(Transform parent)
        {
            RectTransform spacer = Rect("ToolbarSpacer", parent, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, Transparent);
            LayoutElement layout = spacer.gameObject.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;
            layout.minWidth = 8f;
        }

        private void ChangeTableZoom(float delta)
        {
            if (historyTableZoom == null) return;
            historyTableZoom.SetZoom(historyTableZoom.Zoom + delta);
            SetLocalizedText(historyTableZoomLabel, $"{historyTableZoom.ZoomPercent}%");
        }

        private void ResetTableView()
        {
            historyTableZoom?.ResetView();
            SetLocalizedText(historyTableZoomLabel, "100%");
        }

        private void ChangeChartZoom(float delta)
        {
            if (historyChartZoom == null) return;
            historyChartZoom.SetZoom(historyChartZoom.Zoom + delta);
            SetLocalizedText(historyChartZoomLabel, $"{historyChartZoom.ZoomPercent}%");
        }

        private void ResetChartView()
        {
            historyChartZoom?.ResetView();
            SetLocalizedText(historyChartZoomLabel, "100%");
        }

        private void ClearHistoryChartPages()
        {
            historyChartPages.Clear();
            historyChartPageIndex = 0;
            historyChartPageCaption = null;
            historyChartSourcePath = null;
            if (historyChartPreviousButton != null) historyChartPreviousButton.interactable = false;
            if (historyChartNextButton != null) historyChartNextButton.interactable = false;
            SetLocalizedText(historyChartPageLabel, "Figure 1/1");
        }

        private void ConfigureHistoryChartPages(SarSensitivityRenderResult result, string caption)
        {
            ClearHistoryChartPages();
            historyChartPageCaption = caption ?? string.Empty;
            if (result?.Pages != null)
                historyChartPages.AddRange(result.Pages.Where(page => page != null &&
                    !string.IsNullOrWhiteSpace(page.OutputPath) && File.Exists(page.OutputPath)));
            if (historyChartPages.Count == 0 && result != null &&
                !string.IsNullOrWhiteSpace(result.OutputPath) && File.Exists(result.OutputPath))
            {
                historyChartPages.Add(new SarRenderedImagePage
                {
                    PageId = "figure-1",
                    PageType = "single",
                    Title = "Analysis Figure",
                    OutputPath = result.OutputPath
                });
            }
            if (historyChartPages.Count == 0)
                throw new InvalidDataException("The plotting worker returned no readable figure page.");
            SetHistoryChartPage(0);
        }

        private void SetHistoryChartPage(int pageIndex)
        {
            if (historyChartPages.Count == 0) return;
            historyChartPageIndex = Mathf.Clamp(pageIndex, 0, historyChartPages.Count - 1);
            SarRenderedImagePage page = historyChartPages[historyChartPageIndex];
            if (page == null || string.IsNullOrWhiteSpace(page.OutputPath) || !File.Exists(page.OutputPath))
            {
                Notice("The selected figure page is unavailable. Regenerate the analysis. ");
                return;
            }

            byte[] png = File.ReadAllBytes(page.OutputPath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(png, true))
            {
                DestroyRuntimeObject(texture);
                throw new InvalidDataException("The generated PNG could not be decoded by Unity.");
            }
            if (historyChartTexture != null) DestroyRuntimeObject(historyChartTexture);
            historyChartTexture = texture;
            historyChartImage.texture = texture;
            historyChartSourcePath = page.OutputPath;
            AspectRatioFitter fitter = historyChartImage.GetComponent<AspectRatioFitter>();
            if (fitter != null) fitter.aspectRatio = texture.width / (float)Math.Max(1, texture.height);
            historyChartImage.gameObject.SetActive(true);
            ResetChartView();

            int pageNumber = historyChartPageIndex + 1;
            int pageCount = historyChartPages.Count;
            SetLocalizedText(historyChartPageLabel, $"Figure {pageNumber}/{pageCount}");
            if (historyChartPreviousButton != null)
                historyChartPreviousButton.interactable = historyChartPageIndex > 0;
            if (historyChartNextButton != null)
                historyChartNextButton.interactable = historyChartPageIndex + 1 < pageCount;
            string title = string.IsNullOrWhiteSpace(page.Title)
                ? $"Figure {pageNumber}/{pageCount}"
                : page.Title;
            string pageSummary = pageCount > 1
                ? L($"Figure {pageNumber}/{pageCount}: ") + title
                : title;
            if (historyChartCaption != null)
                historyChartCaption.text = string.IsNullOrWhiteSpace(historyChartPageCaption)
                    ? pageSummary
                    : pageSummary + " · " + L(historyChartPageCaption);
        }

        private void ExportCurrentAnalysisImage()
        {
            bool chart = historyChartPanel != null && historyChartPanel.activeSelf;
            bool table = historyTablePanel != null && historyTablePanel.activeSelf;
            if (!chart && !table)
            {
                Notice("Open a chart or result-table page before exporting an image. ");
                return;
            }

            string defaultName = chart
                ? Path.GetFileName(string.IsNullOrWhiteSpace(historyChartSourcePath)
                    ? "SAR-Analysis.png" : historyChartSourcePath)
                : $"SAR-Result-Table-Page-{historyTablePageIndex + 1}.png";
            string initial = Path.Combine(simulationHistory?.HistoryRoot ?? Application.persistentDataPath,
                "AnalysisExports", defaultName);
            SarNativeFileDialog.SaveFileAsync(L("Export Analysis Image"),
                "PNG image|*.png", initial, "png", path =>
                {
                    if (string.IsNullOrWhiteSpace(path)) return;
                    if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                        if (chart && !string.IsNullOrWhiteSpace(historyChartSourcePath) &&
                            File.Exists(historyChartSourcePath))
                        {
                            if (!string.Equals(Path.GetFullPath(historyChartSourcePath),
                                    Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                                File.Copy(historyChartSourcePath, path, true);
                            Notice("Analysis image exported: " + path + " ");
                            return;
                        }
                        if (table && historyTableViewport != null)
                        {
                            StartCoroutine(CaptureRectToPng(historyTableViewport, path));
                            return;
                        }
                        Notice("No generated analysis image is available to export. ");
                    }
                    catch (Exception ex)
                    {
                        Notice("Image export failed: " + ex.Message + " ");
                    }
                });
        }

        private IEnumerator CaptureRectToPng(RectTransform target, string path)
        {
            yield return new WaitForEndOfFrame();
            Texture2D screen = null;
            Texture2D crop = null;
            try
            {
                screen = ScreenCapture.CaptureScreenshotAsTexture();
                var corners = new Vector3[4];
                target.GetWorldCorners(corners);
                Canvas canvas = target.GetComponentInParent<Canvas>();
                Camera canvasCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? canvas.worldCamera
                    : null;
                Vector2[] screenCorners = corners
                    .Select(point => RectTransformUtility.WorldToScreenPoint(canvasCamera, point))
                    .ToArray();
                int xMin = Mathf.Clamp(Mathf.FloorToInt(screenCorners.Min(point => point.x)), 0,
                    Math.Max(0, screen.width - 1));
                int xMax = Mathf.Clamp(Mathf.CeilToInt(screenCorners.Max(point => point.x)), xMin + 1,
                    screen.width);
                int yMin = Mathf.Clamp(Mathf.FloorToInt(screenCorners.Min(point => point.y)), 0,
                    Math.Max(0, screen.height - 1));
                int yMax = Mathf.Clamp(Mathf.CeilToInt(screenCorners.Max(point => point.y)), yMin + 1,
                    screen.height);
                crop = new Texture2D(xMax - xMin, yMax - yMin, TextureFormat.RGBA32, false);
                crop.SetPixels(screen.GetPixels(xMin, yMin, xMax - xMin, yMax - yMin));
                crop.Apply(false, false);
                File.WriteAllBytes(path, crop.EncodeToPNG());
                Notice("Result-table image exported: " + path + " ");
            }
            catch (Exception ex)
            {
                Notice("Image export failed: " + ex.Message + " ");
            }
            finally
            {
                if (crop != null) DestroyRuntimeObject(crop);
                if (screen != null) DestroyRuntimeObject(screen);
            }
        }

        private async void ShowSelectedHistoryTrafficHeatmap()
        {
            // Buttons are disabled as soon as generation starts, but this guard also
            // closes the same-frame double-click/reflection race before Begin... runs.
            if (historyAnalysisBusy) return;
            if (IsSimulationInteractionLocked())
            {
                Notice("Stop the current single or batch simulation before generating an archived traffic heatmap. ");
                return;
            }

            SarSimulationRunManifest run = ResolveSelectedConcrete3DRun();
            if (run == null)
            {
                Notice("Select exactly one single run or one batch member before generating a traffic heatmap. ");
                return;
            }
            SarSimulationHistory history = simulationHistory;
            string sceneId = history.CurrentSceneId;
            string runId = run.RunId;
            string recordingPath = history.ResolveRecordingPath(run);
            if (string.IsNullOrWhiteSpace(recordingPath) || !File.Exists(recordingPath))
            {
                Notice("The selected run contains no loadable recording for a traffic heatmap. ");
                return;
            }

            string occupancyPath = history.ResolveOccupancyPath(run);
            int generation = BeginHistoryAnalysisContext();
            RefreshHistoryAnalysisAvailability();
            ShowHistoryAnalysisSurface("Generating 2D Traffic Heatmap",
                "Python / Matplotlib · archived lane traversals and ground-resource reservation time", true);
            ClearHistoryChartPages();
            historyChartImage.gameObject.SetActive(false);
            ResetChartView();
            SetLocalizedText(historyChartCaption,
                "Loading the archived topology and generating paged network-layer and full-size vertiport figures...");
            try
            {
                Tuple<Network, List<Vertiport>, SarHistoricalVisualizationData> loaded = await Task.Run(() =>
                    Tuple.Create(
                        history.LoadFullNetworkSnapshot(run),
                        history.LoadVertiportsSnapshot(run),
                        SarSimulationHistory.LoadHistoricalVisualizationData(
                            recordingPath, occupancyPath, 320)));
                SarSimulationRunManifest currentRun = ResolveSelectedConcrete3DRun();
                if (!IsHistoryAnalysisContextCurrent(generation, sceneId) || currentRun == null ||
                    !string.Equals(currentRun.RunId, runId, StringComparison.Ordinal)) return;

                // This is a visualization replacement, not a user-requested clear.
                // Preserve the current scene's transient layer choices while removing
                // every old route/trajectory catalogue entry atomically.
                ClearLoadedHistoricalResult(false);
                loadedHistoricalNetwork = loaded.Item1;
                loadedHistoricalVertiports = loaded.Item2 ?? new List<Vertiport>();
                SarHistoricalVisualizationData sourceData = loaded.Item3 ??
                                                            new SarHistoricalVisualizationData();
                loadedHistoricalVisualizationData = new SarHistoricalVisualizationData
                {
                    LaneTraversals = new Dictionary<string, float>(
                        sourceData.LaneTraversals ?? new Dictionary<string, float>(),
                        StringComparer.Ordinal),
                    ResourceReservationSeconds = new Dictionary<string, float>(
                        sourceData.ResourceReservationSeconds ?? new Dictionary<string, float>(),
                        StringComparer.Ordinal)
                };
                loadedHistoricalRunId = runId;
                visualizer.SetHistoricalTopology(loadedHistoricalNetwork);
                visualizer.SetHistoricalGroundGeometry(loadedHistoricalVertiports);
                visualizer.SetHistoricalLoadData(loadedHistoricalVisualizationData);
                ApplyPageAltitudeLayerVisibility(3);
                SetDataVisualizationHeatmapEnabled(true, false);
                historyTrafficLayerSignature = null;
                RefreshTrafficHeatmapLayerControls(true);

                List<string> visibleLayerIds = (loadedHistoricalNetwork?.AltitudeLayers ??
                                                new List<AltitudeLayer>())
                    .Where(layer => layer != null && !string.IsNullOrWhiteSpace(layer.LayerId) &&
                                    visualizer.IsHeatmapLayerVisible(layer.LayerId))
                    .OrderBy(layer => layer.NominalAltitude)
                    .ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
                    .Select(layer => layer.LayerId)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                string outputDirectory = Path.Combine(history.HistoryRoot, "AnalysisExports");
                SarSensitivityRenderResult result = await SarPythonAnalysisRenderer.RenderTrafficHeatmapAsync(
                    loadedHistoricalNetwork,
                    loadedHistoricalVertiports,
                    loadedHistoricalVisualizationData.LaneTraversals,
                    loadedHistoricalVisualizationData.ResourceReservationSeconds,
                    visibleLayerIds,
                    outputDirectory,
                    L($"{history.CurrentSceneName} — Archived Traffic Load"),
                    run.CorridorTangentRatio);
                currentRun = ResolveSelectedConcrete3DRun();
                if (!IsHistoryAnalysisContextCurrent(generation, sceneId) || currentRun == null ||
                    !string.Equals(currentRun.RunId, runId, StringComparison.Ordinal)) return;
                if (!result.Success)
                {
                    SetLocalizedText(historyChartCaption, "Plotting failed: " + result.Message);
                    Notice("Traffic-heatmap plotting failed: " + result.Message);
                    return;
                }

                string chartCaption = result.Caption +
                    (result.Warnings != null && result.Warnings.Count > 0
                        ? " · " + string.Join("; ", result.Warnings)
                        : string.Empty);
                ConfigureHistoryChartPages(result, chartCaption);
                SetLocalizedText(historyAnalysisTitle, "Archived Traffic Heatmap");
                SetLocalizedText(historyAnalysisSubtitle,
                    $"{HistoryEntryLabel(run)} · {historyChartPages.Count} figure page(s) · " +
                    $"{visibleLayerIds.Count} visible altitude layer(s)");
                Notice("Python traffic heatmap generated. Use Previous Figure / Next Figure to review " +
                       "network-layer overviews and full-size vertiport pages. Lane-to-lane connectors are excluded. ");
            }
            catch (Exception ex)
            {
                SarSimulationRunManifest currentRun = ResolveSelectedConcrete3DRun();
                if (!IsHistoryAnalysisContextCurrent(generation, sceneId) || currentRun == null ||
                    !string.Equals(currentRun.RunId, runId, StringComparison.Ordinal)) return;
                Debug.LogError($"[SAR Analytics] Traffic-heatmap plotting failed: {ex}");
                SetLocalizedText(historyChartCaption, "Plotting failed: " + ex.Message);
                Notice("Traffic-heatmap plotting failed: " + ex.Message);
            }
            finally
            {
                if (this != null && generation == historyAnalysisGeneration)
                {
                    historyAnalysisBusy = false;
                    RefreshHistoryAnalysisAvailability();
                }
            }
        }

        private async void ShowSelectedHistoryBars()
        {
            List<SarSimulationRunManifest> runs = ResolveSelectedAnalysisRuns()
                .Where(item => item.Status == SarSimulationRunStatus.Completed).ToList();
            SarHistoryAnalysisPlan plan = SarHistoryAnalysisPlanner.CreatePlan(runs);
            List<string> metrics = plan.CommonMetrics.Where(selectedHistoryMetrics.Contains).ToList();
            if (metrics.Count == 0)
            {
                Notice("Select at least one shared output metric (Y) for the run comparison. Input parameters (X) are not used by this chart. ");
                return;
            }
            if (runs.Count < 2)
            {
                Notice("A bar-chart comparison requires at least two completed experiment records. ");
                return;
            }

            SarSimulationHistory history = simulationHistory;
            string sceneId = history.CurrentSceneId;
            List<string> runIds = runs.Select(item => item.RunId)
                .OrderBy(item => item, StringComparer.Ordinal).ToList();
            int generation = BeginHistoryAnalysisContext();
            RefreshHistoryAnalysisAvailability();
            ShowHistoryAnalysisSurface("Generating Run-Comparison Figure",
                $"Python / Pandas / NumPy / Matplotlib · {runs.Count} record(s) · {metrics.Count} Y metric(s)", true);
            ClearHistoryChartPages();
            historyChartImage.gameObject.SetActive(false);
            ResetChartView();
            SetLocalizedText(historyChartCaption,
                "Generating one independently scaled bar-chart panel per selected output metric...");
            try
            {
                string outputDirectory = Path.Combine(history.HistoryRoot, "AnalysisExports");
                SarSensitivityRenderResult result = await SarPythonAnalysisRenderer.RenderRunComparisonAsync(
                    runs, metrics, outputDirectory,
                    L($"{history.CurrentSceneName} — Selected Experiment Record Comparison"));
                if (!IsRunComparisonContextCurrent(generation, sceneId, runIds, metrics)) return;
                if (!result.Success)
                {
                    SetLocalizedText(historyChartCaption, "Plotting failed: " + result.Message);
                    Notice("Run-comparison plotting failed: " + result.Message);
                    return;
                }

                byte[] png = File.ReadAllBytes(result.OutputPath);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(png, true))
                {
                    DestroyRuntimeObject(texture);
                    throw new InvalidDataException("The generated PNG could not be decoded by Unity.");
                }
                if (historyChartTexture != null) DestroyRuntimeObject(historyChartTexture);
                historyChartTexture = texture;
                historyChartImage.texture = texture;
                historyChartSourcePath = result.OutputPath;
                AspectRatioFitter fitter = historyChartImage.GetComponent<AspectRatioFitter>();
                if (fitter != null) fitter.aspectRatio = texture.width / (float)Math.Max(1, texture.height);
                historyChartImage.gameObject.SetActive(true);
                ResetChartView();
                SetLocalizedText(historyAnalysisTitle, "Run-Metric Bar Comparison");
                SetLocalizedText(historyAnalysisSubtitle,
                    $"{runs.Count} selected records → {string.Join(", ", metrics.Select(L))}");
                SetLocalizedText(historyChartCaption, result.Caption +
                    (result.Warnings != null && result.Warnings.Count > 0
                        ? " · " + string.Join("; ", result.Warnings)
                        : string.Empty));
                Notice("Python multi-metric run comparison generated and stored with the current scene history. ");
            }
            catch (Exception ex)
            {
                if (!IsRunComparisonContextCurrent(generation, sceneId, runIds, metrics)) return;
                Debug.LogError($"[SAR Analytics] Run-comparison plotting failed: {ex}");
                SetLocalizedText(historyChartCaption, "Plotting failed: " + ex.Message);
                Notice("Run-comparison plotting failed: " + ex.Message);
            }
            finally
            {
                if (this != null && generation == historyAnalysisGeneration)
                {
                    historyAnalysisBusy = false;
                    RefreshHistoryAnalysisAvailability();
                }
            }
        }

        private async void ShowSelectedHistorySensitivity()
        {
            List<SarSimulationRunManifest> runs = ResolveSelectedAnalysisRuns()
                .Where(item => item.Status == SarSimulationRunStatus.Completed).ToList();
            SarHistoryAnalysisPlan plan = SarHistoryAnalysisPlanner.CreatePlan(runs);
            List<string> factors = plan.NumericVaryingParameters
                .Where(selectedHistoryFactors.Contains).ToList();
            List<string> metrics = plan.CommonMetrics.Where(selectedHistoryMetrics.Contains).ToList();
            if (factors.Count < 1 || factors.Count > 3 || metrics.Count == 0)
            {
                Notice("Select one to three varying input parameters (X) and at least one shared output metric (Y). ");
                return;
            }
            if (runs.Count < 2)
            {
                Notice("An empirical parameter-response plot requires at least two completed runs. ");
                return;
            }

            SarSimulationHistory history = simulationHistory;
            string sceneId = history.CurrentSceneId;
            List<string> runIds = runs.Select(item => item.RunId)
                .OrderBy(item => item, StringComparer.Ordinal).ToList();
            int generation = BeginHistoryAnalysisContext();
            RefreshHistoryAnalysisAvailability();
            ShowHistoryAnalysisSurface("Generating Parameter-Response Figure",
                $"Python / NumPy / Matplotlib · {factors.Count} X dimension(s) · {metrics.Count} Y metric(s)", true);
            ClearHistoryChartPages();
            historyChartImage.gameObject.SetActive(false);
            ResetChartView();
            SetLocalizedText(historyChartCaption,
                "Generating a headless scientific figure; exact repeated combinations use real sample statistics only...");
            try
            {
                string outputDirectory = Path.Combine(history.HistoryRoot, "AnalysisExports");
                SarSensitivityRenderResult result = await SarPythonAnalysisRenderer.RenderSensitivityAsync(
                    runs, factors, metrics, outputDirectory,
                    L($"{history.CurrentSceneName} — Empirical Parameter Response"));
                if (!IsSensitivityContextCurrent(generation, sceneId, runIds, factors, metrics)) return;
                if (!result.Success)
                {
                    SetLocalizedText(historyChartCaption, "Plotting failed: " + result.Message);
                    Notice("Parameter-response plotting failed: " + result.Message);
                    return;
                }
                byte[] png = File.ReadAllBytes(result.OutputPath);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(png, true))
                {
                    DestroyRuntimeObject(texture);
                    throw new InvalidDataException("The generated PNG could not be decoded by Unity.");
                }
                if (historyChartTexture != null) DestroyRuntimeObject(historyChartTexture);
                historyChartTexture = texture;
                historyChartImage.texture = texture;
                historyChartSourcePath = result.OutputPath;
                AspectRatioFitter fitter = historyChartImage.GetComponent<AspectRatioFitter>();
                if (fitter != null) fitter.aspectRatio = texture.width / (float)Math.Max(1, texture.height);
                historyChartImage.gameObject.SetActive(true);
                ResetChartView();
                SetLocalizedText(historyAnalysisTitle, "Empirical Parameter-Response Analysis");
                SetLocalizedText(historyAnalysisSubtitle, string.Join(" × ", factors.Select(L)) + " → " +
                                                          string.Join(", ", metrics.Select(L)));
                SetLocalizedText(historyChartCaption, result.Caption +
                    (result.Warnings != null && result.Warnings.Count > 0
                        ? " · " + string.Join("; ", result.Warnings)
                        : string.Empty));
                Notice("Python sensitivity figure generated and stored with the current scene history. ");
            }
            catch (Exception ex)
            {
                if (!IsSensitivityContextCurrent(generation, sceneId, runIds, factors, metrics)) return;
                Debug.LogError($"[SAR Analytics] Sensitivity plotting failed: {ex}");
                SetLocalizedText(historyChartCaption, "Plotting failed: " + ex.Message);
                Notice("Parameter-response plotting failed: " + ex.Message);
            }
            finally
            {
                if (this != null && generation == historyAnalysisGeneration)
                {
                    historyAnalysisBusy = false;
                    RefreshHistoryAnalysisAvailability();
                }
            }
        }

        private void ShowHistoryAnalysisSurface(string title, string subtitle, bool chart)
        {
            if (historyAnalysisSurface == null) return;
            historyAnalysisSurface.SetActive(true);
            historyAnalysisSurface.transform.SetAsLastSibling();
            if (viewGizmo != null) viewGizmo.gameObject.SetActive(false);
            SetLocalizedText(historyAnalysisTitle, title);
            SetLocalizedText(historyAnalysisSubtitle, subtitle);
            historyTablePanel.SetActive(!chart);
            historyChartPanel.SetActive(chart);
            RefreshHistoricalHeatmapLegend();
        }

        private void HideHistoryAnalysisSurface()
        {
            if (historyAnalysisSurface != null) historyAnalysisSurface.SetActive(false);
            if (viewGizmo != null && workbenchVisible) viewGizmo.gameObject.SetActive(true);
            RefreshHistoricalHeatmapLegend();
        }

        private static void ClearUiChildren(Transform parent)
        {
            if (parent == null) return;
            foreach (Transform child in parent.Cast<Transform>().ToList())
            {
                child.gameObject.SetActive(false);
                DestroyUiObject(child.gameObject);
            }
        }

        private void ToggleTrajectoryChecklist()
        {
            if (trajectoryChecklistPanel == null) return;
            trajectoryChecklistPanel.gameObject.SetActive(!trajectoryChecklistPanel.gameObject.activeSelf);
            RefreshTrajectoryChecklist(true);
        }

        private void RefreshTrajectoryChecklist(bool force = false)
        {
            if (trajectoryChecklistPanel == null || analytics == null) return;
            List<string> ids = analytics.CompletedTrackIds.ToList();
            selectedTrajectoryIds.RemoveWhere(id => !ids.Contains(id));
            string signature = string.Join("|", ids) + "::" +
                               string.Join("|", selectedTrajectoryIds.OrderBy(id => id));
            if (!force && signature == trajectoryChecklistSignature) return;
            trajectoryChecklistSignature = signature;
            DestroyUiChildren(trajectoryChecklistPanel);
            if (ids.Count == 0)
            {
                Label(trajectoryChecklistPanel, "No completed mission trajectories are available yet.", 12, Muted, 36f);
            }
            else
            {
                foreach (string id in ids)
                {
                    string capturedId = id;
                    ToggleField(trajectoryChecklistPanel, analytics.GetTrackLabel(capturedId),
                        selectedTrajectoryIds.Contains(capturedId), enabled =>
                        {
                            if (enabled) selectedTrajectoryIds.Add(capturedId);
                            else selectedTrajectoryIds.Remove(capturedId);
                            trajectoryChecklistSignature = null;
                            DrawSelectedTracks(false);
                        });
                }
            }
            Text label = trajectoryDropdownButton != null
                ? trajectoryDropdownButton.GetComponentInChildren<Text>(true) : null;
            if (label != null)
                SetLocalizedText(label,
                    $"Select Completed Trajectories ({selectedTrajectoryIds.Count}/{ids.Count}) " +
                    (trajectoryChecklistPanel.gameObject.activeSelf ? "▴" : "▾"));
        }

        private void DrawSelectedTracks() => DrawSelectedTracks(true);

        private void DrawSelectedTracks(bool notify)
        {
            if (selectedTrajectoryIds.Count == 0 || !analytics.ShowTracks(selectedTrajectoryIds))
            {
                analytics.ClearTrackDisplay();
                if (notify) Notice("Select at least one completed mission trajectory first. ");
                return;
            }
            visualizer.SetVisible(AirspaceVisualLayer.RoutePreview, true);
            if (notify) Notice($"Drew {selectedTrajectoryIds.Count} complete mission trajectories together. ");
        }

        private void SelectAllTrajectories()
        {
            selectedTrajectoryIds.Clear();
            foreach (string id in analytics.CompletedTrackIds) selectedTrajectoryIds.Add(id);
            trajectoryChecklistSignature = null;
            RefreshTrajectoryChecklist(true);
            DrawSelectedTracks();
        }

        private void ClearTrajectoryRecords()
        {
            analytics.ClearTracks();
            selectedTrajectoryIds.Clear();
            trajectoryChecklistSignature = null;
            RefreshTrajectoryChecklist(true);
            Notice("All trajectory records cleared. ");
        }

        private const string RuntimeOverviewCameraName = "RuntimeOverviewCamera";

        private void UseOverviewCamera(bool showNotice)
        {
            Camera[] cameras = FindObjectsOfType<Camera>(true);
            followCamera = cameras.FirstOrDefault(camera => camera.GetComponent<DroneFollowCamera>() != null);
            // RuntimeOverviewCamera replaces Camera2 and uses that camera's settings.
            // Older scenes that still contain Camera2 keep using it.
            overviewCamera = cameras.FirstOrDefault(camera => camera.name == RuntimeOverviewCameraName)
                ?? cameras.FirstOrDefault(camera => camera.name == "Camera2" && camera.targetTexture == null)
                ?? cameras.FirstOrDefault(camera =>
                    camera.GetComponent<DroneFollowCamera>() == null && camera.targetTexture == null);
            if (overviewCamera == null)
                overviewCamera = CreateRuntimeOverviewCamera();

            if (followCamera != null && followCamera != overviewCamera)
                followCamera.targetDisplay = 3;
            overviewCamera.gameObject.SetActive(true);
            overviewCamera.enabled = true;
            overviewCamera.targetDisplay = 0;
            if (navigator != null && navigator.ControlledCamera != overviewCamera)
                navigator.Initialize(overviewCamera, manager);
            if (showNotice) Notice("Returned to scene overview. ");
        }

        /// <summary>
        /// Same camera setup as Camera2 in Assets/Resources/Scenes/1.unity.
        /// That object starts inactive; the workbench enables it and moves it to display 0.
        /// Its saved scale is zero, which is not copied.
        /// </summary>
        private static Camera CreateRuntimeOverviewCamera()
        {
            GameObject cameraObject = new GameObject(RuntimeOverviewCameraName);
            cameraObject.SetActive(false);
            Camera camera = cameraObject.AddComponent<Camera>();
            ApplyOriginalCamera2Settings(camera);
            if (cameraObject.GetComponent<AudioListener>() == null)
                cameraObject.AddComponent<AudioListener>();
            cameraObject.transform.SetPositionAndRotation(
                new Vector3(141.20944f, 500f, 40.277733f),
                Quaternion.Euler(90f, 90f, 0f));
            return camera;
        }

        private static void ApplyOriginalCamera2Settings(Camera camera)
        {
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.white;
            camera.orthographic = true;
            camera.orthographicSize = 1200f;
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 1000f;
            camera.depth = -1f;
            camera.cullingMask = -1;
            camera.renderingPath = RenderingPath.UsePlayerSettings;
            camera.targetDisplay = 1;
            camera.allowHDR = true;
            camera.allowMSAA = true;
            camera.allowDynamicResolution = false;
            camera.useOcclusionCulling = true;
            camera.stereoConvergence = 10f;
            camera.stereoSeparation = 0.022f;
        }

        private void SaveQueue() { manager.SaveMissionsToJSON(); Notice("Demand queue saved as JSON. "); }

        private void OpenOutputFolder()
        {
            Application.OpenURL("file:///" + manager.GetOutputDirectory().Replace('\\', '/'));
            Notice("Output directory opened. ");
        }

        private void Notice(string message)
        {
            // Localize the notice before it is embedded into the footer's live counters. This lets
            // exact catalog entries match and prevents an otherwise translated status line from
            // retaining an English notice prefix.
            notice = L(message);
            noticeUntil = Time.unscaledTime + 4.5f;
            if (footerText != null) RefreshStatus();
        }

        private void RowButtons(Transform parent,
            (string label, Action action, Color color) left,
            (string label, Action action, Color color) right,
            Action<Button, Button> created = null)
        {
            RectTransform row = Rect("ButtonRow", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Transparent);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childForceExpandWidth = true;
            Button leftButton = Button(left.label, row, left.action, left.color);
            leftButton.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            Button rightButton = Button(right.label, row, right.action, right.color);
            rightButton.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            created?.Invoke(leftButton, rightButton);
        }

        private Button FullButton(Transform parent, string label, Action action, Color color)
        {
            Button button = Button(label, parent, action, color);
            button.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;
            return button;
        }

        private Button Button(string label, Transform parent, Action action, Color color)
        {
            RectTransform rect = Rect(label, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, color);
            Button button = MakeButton(rect, action, color);
            bool white = UsesLightButtonText(color);
            Text(label, rect, 12, FontStyle.Bold, TextAnchor.MiddleCenter, white ? Color.white : Ink,
                new Vector2(7f, 0f), new Vector2(-7f, 0f));
            return button;
        }

        private Button MakeButton(RectTransform rect, Action action, Color color)
        {
            Button button = rect.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = rect.GetComponent<Image>();
            button.onClick.AddListener(() => action?.Invoke());
            rect.gameObject.AddComponent<SarUiButtonMotion>().Configure(color);
            return button;
        }

        private void SetButtonColor(Button button, Color color)
        {
            if (button == null) return;
            button.targetGraphic.color = color;
            button.GetComponent<SarUiButtonMotion>()?.Configure(color);
            bool light = UsesLightButtonText(color);
            foreach (Text label in button.GetComponentsInChildren<Text>(true))
            {
                float alpha = label.color.a;
                Color textColor = light ? Color.white : Ink;
                textColor.a = alpha;
                label.color = textColor;
            }
        }

        private static bool UsesLightButtonText(Color color)
        {
            return color == Ink || color == InkSoft || color == Accent || color == Teal ||
                   color == Green || color == Red;
        }

        private InputField Input(Transform parent, string value, string placeholderText,
            Action<string> committed = null)
        {
            RectTransform box = Rect("InputField", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Surface);
            box.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;
            Outline outline = box.gameObject.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);
            InputField field = box.gameObject.AddComponent<InputField>();
            Text text = Text(value, box, 12, FontStyle.Normal, TextAnchor.MiddleLeft,
                Ink, new Vector2(12f, 0f), new Vector2(-12f, 0f));
            Text placeholder = Text(placeholderText, box, 12, FontStyle.Italic, TextAnchor.MiddleLeft,
                Muted, new Vector2(12f, 0f), new Vector2(-12f, 0f));
            field.textComponent = text;
            field.placeholder = placeholder;
            field.text = value;
            if (committed != null) field.onEndEdit.AddListener(textValue => committed(textValue));
            return field;
        }

        private InputField PathInput(Transform parent, string value, string placeholderText,
            Action browse, Action<string> committed = null)
        {
            RectTransform row = Rect("PathInputRow", parent, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, Transparent);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = false;

            InputField field = Input(row, value, placeholderText, committed);
            LayoutElement fieldLayout = field.GetComponent<LayoutElement>();
            fieldLayout.flexibleWidth = 1f;
            fieldLayout.preferredWidth = 0f;
            Button browseButton = Button("Browse...", row, browse, SurfaceSoft);
            LayoutElement browseLayout = browseButton.gameObject.AddComponent<LayoutElement>();
            browseLayout.preferredWidth = 82f;
            browseLayout.minWidth = 82f;
            return field;
        }

        private Dropdown Dropdown(Transform parent, IEnumerable<string> options, Action<string> changed,
            bool localizeOptions = false)
        {
            RectTransform root = Rect("Dropdown", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Surface);
            root.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;
            Outline outline = root.gameObject.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);
            Dropdown dropdown = root.gameObject.AddComponent<Dropdown>();
            dropdown.targetGraphic = root.GetComponent<Image>();
            Text caption = Text("", root, 12, FontStyle.Normal, TextAnchor.MiddleLeft,
                Ink, new Vector2(12f, 0f), new Vector2(-38f, 0f));
            Text arrow = Text("▼", root, 13, FontStyle.Bold, TextAnchor.MiddleCenter,
                Muted, Vector2.zero, Vector2.zero);
            arrow.rectTransform.anchorMin = new Vector2(1f, 0f);
            arrow.rectTransform.anchorMax = Vector2.one;
            arrow.rectTransform.offsetMin = new Vector2(-36f, 0f);
            arrow.rectTransform.offsetMax = new Vector2(-8f, 0f);
            RectTransform template = Rect("Template", root, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, -270f), Vector2.zero, Surface);
            template.pivot = new Vector2(0.5f, 1f);
            Canvas templateCanvas = template.gameObject.AddComponent<Canvas>();
            templateCanvas.overrideSorting = true;
            templateCanvas.sortingOrder = 700;
            // Dropdown.Show animates the cloned list through its CanvasGroup.
            // Omitting this component makes visible dropdowns throw inside AlphaFadeList.
            template.gameObject.AddComponent<CanvasGroup>();
            template.gameObject.AddComponent<GraphicRaycaster>();
            ScrollRect scroll = template.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 34f;
            RectTransform viewport = Rect("Viewport", template, Vector2.zero, Vector2.one,
                new Vector2(2f, 2f), new Vector2(-20f, -2f), Surface);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            RectTransform content = Rect("Content", viewport, new Vector2(0f, 1f), Vector2.one,
                new Vector2(0f, -38f), Vector2.zero, Transparent);
            content.pivot = new Vector2(0.5f, 1f);
            Toggle item = Rect("Item", content, new Vector2(0f, 1f), Vector2.one,
                new Vector2(0f, -38f), Vector2.zero, Surface).gameObject.AddComponent<Toggle>();
            item.targetGraphic = item.GetComponent<Image>();
            Text itemText = Text("Option", item.transform, 12, FontStyle.Normal, TextAnchor.MiddleLeft,
                Ink, new Vector2(10f, 0f), new Vector2(-8f, 0f));

            RectTransform scrollbarRoot = Rect("Scrollbar Vertical", template,
                new Vector2(1f, 0f), Vector2.one, new Vector2(-18f, 2f), new Vector2(-2f, -2f),
                SurfaceSoft);
            Scrollbar scrollbar = scrollbarRoot.gameObject.AddComponent<Scrollbar>();
            RectTransform slidingArea = Rect("Sliding Area", scrollbarRoot, Vector2.zero, Vector2.one,
                new Vector2(3f, 3f), new Vector2(-3f, -3f), Transparent);
            RectTransform handle = Rect("Handle", slidingArea, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, AccentSoft);
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handle.GetComponent<Image>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            scroll.verticalScrollbarSpacing = 2f;
            dropdown.template = template;
            dropdown.captionText = caption;
            dropdown.itemText = itemText;
            dropdownLocalizeOptions[dropdown] = localizeOptions;
            ApplyDropdownOptions(dropdown, options, localizeOptions);
            // Runtime-created Dropdown components can retain an empty serialized
            // event object after the template is initialized. Replace it explicitly
            // so user selection always reaches the workbench callback.
            dropdown.onValueChanged = new Dropdown.DropdownEvent();
            dropdown.onValueChanged.AddListener(index =>
            {
                if (index < 0) return;
                if (dropdownSourceOptions.TryGetValue(dropdown, out List<string> sources) &&
                    index < sources.Count)
                    changed?.Invoke(sources[index]);
                else if (index < dropdown.options.Count)
                    changed?.Invoke(dropdown.options[index].text);
            });
            root.gameObject.AddComponent<RuntimeDropdownAnchor>().Configure(dropdown);
            template.gameObject.SetActive(false);
            dropdown.RefreshShownValue();
            return dropdown;
        }

        private Slider Slider(Transform parent, float min, float max, float value, Action<float> changed)
        {
            RectTransform root = Rect("TimeScale", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Transparent);
            root.gameObject.AddComponent<LayoutElement>().preferredHeight = 28f;
            Slider slider = root.gameObject.AddComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = value;
            Rect("Track", root, new Vector2(0f, 0.43f), new Vector2(1f, 0.57f), Vector2.zero, Vector2.zero, Border);
            RectTransform fillArea = Rect("FillArea", root, new Vector2(0f, 0.36f), new Vector2(1f, 0.64f),
                Vector2.zero, Vector2.zero, Transparent);
            RectTransform fill = Rect("Fill", fillArea, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Accent);
            RectTransform handleArea = Rect("HandleArea", root, Vector2.zero, Vector2.one,
                new Vector2(6f, 0f), new Vector2(-6f, 0f), Transparent);
            RectTransform handle = Rect("Handle", handleArea, new Vector2(0f, 0.18f), new Vector2(0f, 0.82f),
                new Vector2(-7f, 0f), new Vector2(7f, 0f), Surface);
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.onValueChanged.AddListener(number => changed(number));
            return slider;
        }

        private Toggle ToggleField(Transform parent, string label, bool value, Action<bool> changed)
        {
            RectTransform root = Rect("Toggle", parent, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, Transparent);
            root.gameObject.AddComponent<LayoutElement>().preferredHeight = 38f;
            Toggle toggle = root.gameObject.AddComponent<Toggle>();
            RectTransform box = Rect("Checkbox", root, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0f, -11f), new Vector2(22f, 11f), Surface);
            Outline outline = box.gameObject.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);
            RectTransform mark = Rect("Checkmark", box, Vector2.zero, Vector2.one,
                new Vector2(5f, 5f), new Vector2(-5f, -5f), Accent);
            Text(label, root, 12, FontStyle.Normal, TextAnchor.MiddleLeft,
                Ink, new Vector2(34f, 0f), Vector2.zero);
            toggle.targetGraphic = box.GetComponent<Image>();
            toggle.graphic = mark.GetComponent<Image>();
            toggle.isOn = value;
            toggle.onValueChanged.AddListener(enabled => changed?.Invoke(enabled));
            return toggle;
        }

        private Text Label(Transform parent, string value, int size, Color color, float height,
            FontStyle style = FontStyle.Normal)
        {
            Text label = Text(value, parent, size, style, TextAnchor.MiddleLeft, color, Vector2.zero, Vector2.zero);
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            return label;
        }

        private void Hint(Transform parent, string value)
        {
            RectTransform card = Rect("Hint", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Hex("F0E5DD"));
            card.gameObject.AddComponent<LayoutElement>().preferredHeight = 78f;
            Text("TIP  " + value, card, 11, FontStyle.Normal, TextAnchor.MiddleLeft,
                InkSoft, new Vector2(12f, 8f), new Vector2(-12f, -8f));
        }

        private Text Text(string value, Transform parent, int size, FontStyle style,
            TextAnchor alignment, Color color, Vector2 offsetMin, Vector2 offsetMax)
        {
            GameObject obj = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            obj.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)obj.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            Text text = obj.GetComponent<Text>();
            text.font = uiFont;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = color;
            text.text = L(value);
            text.raycastTarget = false;
            return text;
        }

        private string L(string source) => localization != null ? localization.Translate(source) : source;

        private void SetLocalizedText(Text target, string source)
        {
            if (target != null) target.text = L(source);
        }

        private RectTransform Rect(string name, Transform parent, Vector2 anchorMin,
            Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            obj.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)obj.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            rect.GetComponent<Image>().color = color;
            return rect;
        }

        private void SetDropdownValue(Dropdown dropdown, string value)
        {
            if (dropdown == null) return;
            int index = dropdownSourceOptions.TryGetValue(dropdown, out List<string> sources)
                ? sources.FindIndex(option => option == value)
                : dropdown.options.FindIndex(option => option.text == value);
            if (index >= 0) dropdown.SetValueWithoutNotify(index);
            dropdown.RefreshShownValue();
        }

        private void SetDropdownOptions(Dropdown dropdown, IEnumerable<string> values)
        {
            if (dropdown == null) return;
            List<string> options = values?.Where(value => !string.IsNullOrEmpty(value)).Distinct().ToList()
                                   ?? new List<string>();
            bool unchanged = dropdownSourceOptions.TryGetValue(dropdown, out List<string> existing) &&
                             existing.SequenceEqual(options);
            if (unchanged) return;
            bool localizeOptions = dropdownLocalizeOptions.TryGetValue(dropdown, out bool localize) && localize;
            ApplyDropdownOptions(dropdown, options, localizeOptions);
            dropdown.SetValueWithoutNotify(0);
            dropdown.RefreshShownValue();
        }

        private void ApplyDropdownOptions(Dropdown dropdown, IEnumerable<string> values, bool localizeOptions)
        {
            if (dropdown == null) return;
            List<string> sources = values?.Where(value => value != null).ToList() ?? new List<string>();
            dropdownSourceOptions[dropdown] = sources;
            dropdown.options = sources.Select(value =>
                new Dropdown.OptionData(localizeOptions ? L(value) : value)).ToList();
        }

        private string DropdownSourceValue(Dropdown dropdown)
        {
            if (dropdown == null || dropdown.value < 0) return null;
            if (dropdownSourceOptions.TryGetValue(dropdown, out List<string> sources) &&
                dropdown.value < sources.Count)
                return sources[dropdown.value];
            return dropdown.value < dropdown.options.Count ? dropdown.options[dropdown.value].text : null;
        }

        private Font CreateUiFont()
        {
            try
            {
                return Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "Segoe UI" }, 18);
            }
            catch { return Resources.GetBuiltinResource<Font>("Arial.ttf"); }
        }

        private void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null) return;
            GameObject eventSystem = new GameObject("EventSystem");
            generatedEventSystem = eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();
        }

        private static void DestroyRuntimeObject(UnityEngine.Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        private static float ParseFloat(InputField field, float fallback) =>
            float.TryParse(field?.text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;

        private static int ParseInt(InputField field, int fallback) =>
            int.TryParse(field?.text, out int value) ? value : fallback;

        private bool TryReadFloat(InputField field, string label, float minimum, float maximum,
            float fallback, out float value)
        {
            string textValue = field?.text?.Trim();
            bool parsed = float.TryParse(textValue, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                          float.TryParse(textValue, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
            if (parsed && !float.IsNaN(value) && !float.IsInfinity(value) &&
                value >= minimum && value <= maximum) return true;
            value = fallback;
            field?.SetTextWithoutNotify(fallback.ToString("0.###", CultureInfo.InvariantCulture));
            Notice($"Invalid {label}. Enter a number from {minimum:0.###} to {maximum:0.###}. ");
            return false;
        }

        private bool TryReadInt(InputField field, string label, int minimum, int maximum,
            int fallback, out int value)
        {
            if (int.TryParse(field?.text?.Trim(), out value) && value >= minimum && value <= maximum)
                return true;
            value = fallback;
            field?.SetTextWithoutNotify(fallback.ToString(CultureInfo.InvariantCulture));
            Notice($"Invalid {label}. Enter an integer from {minimum} to {maximum}. ");
            return false;
        }

        private static string MissionModeName(MissionGenerationMode? mode) =>
            mode == MissionGenerationMode.RandomContinuous ? "Continuous Random Demand" : "JSON / XML Route File";

        private static string ViewName(SceneViewPreset preset)
        {
            switch (preset)
            {
                case SceneViewPreset.Top: return "Top";
                case SceneViewPreset.Front: return "Front";
                case SceneViewPreset.Right: return "Right";
                case SceneViewPreset.Left: return "Left";
                case SceneViewPreset.Bottom: return "Bottom";
                case SceneViewPreset.Back: return "Back";
                default: return "Free View";
            }
        }

        private static string LayerName(AirspaceVisualLayer layer)
        {
            switch (layer)
            {
                case AirspaceVisualLayer.City: return "City Buildings";
                case AirspaceVisualLayer.Network: return "Air Network";
                case AirspaceVisualLayer.Approach: return "Ground Approach Network";
                case AirspaceVisualLayer.Holding: return "Holding Structures";
                case AirspaceVisualLayer.Facilities: return "Vertiports and Helipads";
                case AirspaceVisualLayer.Corridors: return "Vertiport Connection Corridors";
                case AirspaceVisualLayer.LayerConnectors: return "Interlayer Connection Corridors";
                case AirspaceVisualLayer.Signals: return "Intersection Signals";
                case AirspaceVisualLayer.RoutePreview: return "Routes and Trajectories";
                case AirspaceVisualLayer.Heatmap: return "Traffic Heatmap";
                default: return layer.ToString();
            }
        }

        private static void DestroyUiObject(UnityEngine.Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        private static Color Hex(string value)
        {
            ColorUtility.TryParseHtmlString("#" + value, out Color color);
            return color;
        }
    }
}
