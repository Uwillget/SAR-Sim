using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RoadNetworkEditor.Networking;
using RoadNetworkEditor.Reservations;
using RoadNetworkEditor.Routes;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

using Newtonsoft.Json;

namespace RoadNetworkEditor
{
    public class SimulationManager : MonoBehaviour
    {
        public static SimulationManager Instance { get; private set; }

        // ==================== Inspector References ====================
        [Header("====== References ======")]
        public GameObject aircraftPrefab;
        public Transform aircraftsParent;
        public NetworkManager networkManager;
        public VertiportManager vertiportManager;
        public SimulationConfig config;

        // Sub-modules
        public PathPlanner pathPlanner;

        // ==================== Data File Paths ====================
        [Header("====== Data File Paths ======")]
        [Tooltip("Data folder path relative to Assets (e.g. DataBase)")]
        public string dataFolder = "DataBase";
        private string dataBasePath;
        private string bundledDataPath;
        private string missionsFilePath;
        private string resultsFilePath;

        void EnsurePathsInitialized()
        {
            if (string.IsNullOrEmpty(dataBasePath))
            {
                if (config == null)
                    config = Resources.Load<SimulationConfig>("DefaultSimulationConfig");
                InitializeDataPaths();
            }
        }

        private void InitializeDataPaths()
        {
            dataBasePath = Application.isEditor
                ? Path.Combine(Application.dataPath, dataFolder)
                : SarRuntimePaths.SarDataRoot;
            bundledDataPath = Application.isEditor
                ? dataBasePath
                : Path.Combine(Application.streamingAssetsPath, "SARData");
            missionsFilePath = Path.Combine(dataBasePath,
                config != null ? config.missionsFileName : "Missions.json");
            resultsFilePath = Path.Combine(dataBasePath,
                config != null ? config.resultsFileName : "SimulationResults.json");
        }

        private string ResolveDefaultMissionPath()
        {
            EnsurePathsInitialized();
            if (File.Exists(missionsFilePath)) return missionsFilePath;
            string bundledPath = Path.Combine(
                bundledDataPath,
                config != null ? config.missionsFileName : "Missions.json");
            return File.Exists(bundledPath) ? bundledPath : missionsFilePath;
        }

        public string GetOutputDirectory()
        {
            string outputDirectory = Application.isEditor
                ? Path.GetFullPath(Path.Combine(Application.dataPath, "../Output"))
                : SarRuntimePaths.OutputRoot;
            Directory.CreateDirectory(outputDirectory);
            return outputDirectory;
        }

        public string GetBundledRouteExamplePath()
        {
            EnsurePathsInitialized();
            return Path.Combine(bundledDataPath, "RouteFile.example.json");
        }

        public string GetDefaultRouteExportPath()
        {
            return Path.Combine(GetOutputDirectory(), "RouteWorkspace.json");
        }

        // Play control
        [Header("====== Play Control ======")]
        [Tooltip("Auto-start simulation on enable")]
        public bool autoStart = false;
        [Tooltip("Max simulation duration in seconds (-1 = unlimited)")]
        public float maxSimDuration = -1f;
        [Tooltip("Results file path relative to project root")]
        public string resultsFilePath_Override = "../Output/MetricsTable.csv";
        [Tooltip("On collision, pause simulation and focus Scene view camera on collision point")]
        public bool pauseOnCollision = false;
        [Tooltip("Draw a bounded trail behind each active aircraft.")]
        public bool showAircraftTrails = false;
        [Range(5f, 10f)] public float aircraftTrailBodyLengths = 8f;
        private bool simulationRunning = false;
        private bool collisionPaused = false;
        public event System.Action<Vector3, string> CollisionPauseRequested;
        public event System.Action<AircraftState, Vector3> MissionCompleted;
        public event System.Action SimulationStarted;
        public event System.Action<bool> SimulationFinished;
        public Vector3 LastCollisionPoint { get; private set; }
        public string LastCollisionPair { get; private set; }
        public bool IsCollisionPaused => collisionPaused;
        private float previousMaximumDeltaTime;
        private bool timingSettingsCaptured;

        private const int MaxMissionDispatchPerFrame = 4;
        private const int MaxPendingSpawnChecksPerFrame = 8;
        private const int MaxPathRetryAttemptsPerFrame = 2;

        private int randomMissionCounter = 0;

        // ==================== Network Data (flat lookup) ====================
        [Header("====== Runtime State ======")]
        public Dictionary<string, Node> Nodes = new Dictionary<string, Node>();
        public Dictionary<string, Edge> Edges = new Dictionary<string, Edge>();
        public Dictionary<string, Lane> Lanes = new Dictionary<string, Lane>();
        public Dictionary<string, LaneConnectionCorridor> LaneConnectionCorridors =
            new Dictionary<string, LaneConnectionCorridor>();
        public Dictionary<string, string> LaneToEdgeMap = new Dictionary<string, string>();

        // ==================== Vertiport Data (flat lookup) ====================
        public Dictionary<string, Vertiport> Vertiports = new Dictionary<string, Vertiport>();
        public Dictionary<string, Helipad> Helipads = new Dictionary<string, Helipad>();
        public Dictionary<string, HoldingStructure> HoldingStructures = new Dictionary<string, HoldingStructure>();
        public Dictionary<string, Circle> Circles = new Dictionary<string, Circle>();
        public Dictionary<string, Vertex> Vertices = new Dictionary<string, Vertex>();
        public Dictionary<string, Ramp> Ramps = new Dictionary<string, Ramp>();
        public Dictionary<string, ApproachNode> ApproachNodes = new Dictionary<string, ApproachNode>();
        public Dictionary<string, ApproachEdge> ApproachEdges = new Dictionary<string, ApproachEdge>();
        public Dictionary<string, NetConnectedCorridor> Corridors = new Dictionary<string, NetConnectedCorridor>();

        // ==================== Parent Lookup Maps ====================
        public Dictionary<string, string> HelipadToVertiportMap = new Dictionary<string, string>();
        public Dictionary<string, string> HSToVertiportMap = new Dictionary<string, string>();
        public Dictionary<string, string> VertexToHSMap = new Dictionary<string, string>();
        public Dictionary<string, string> CorridorToVertiportMap = new Dictionary<string, string>();
        public Dictionary<string, string> ApproachNodeToVertiportMap = new Dictionary<string, string>();
        public Dictionary<string, string> CorridorToLaneMap = new Dictionary<string, string>();
        public Dictionary<string, string> RampToCorridorMap = new Dictionary<string, string>();

        // ==================== Runtime State Dictionaries ====================
        public Dictionary<string, AircraftState> AircraftStates = new Dictionary<string, AircraftState>();
        public Dictionary<string, GameObject> AircraftObjects = new Dictionary<string, GameObject>();
        public Dictionary<string, LaneState> LaneStates = new Dictionary<string, LaneState>();
        public Dictionary<string, HoldingStructureState> HSStates = new Dictionary<string, HoldingStructureState>();
        public Dictionary<string, HelipadState> HelipadStatesDict = new Dictionary<string, HelipadState>();
        public Dictionary<string, VertexState> VertexStatesDict = new Dictionary<string, VertexState>();
        public Dictionary<string, RampState> RampStatesDict = new Dictionary<string, RampState>();
        public Dictionary<string, CorridorState> CorridorStatesDict = new Dictionary<string, CorridorState>();
        public Dictionary<string, ApproachNodeState> ApproachNodeStates = new Dictionary<string, ApproachNodeState>();
        public Dictionary<string, ApproachEdgeState> ApproachEdgeStates = new Dictionary<string, ApproachEdgeState>();

        // ==================== Mission Queue ====================
        public Queue<MissionTask> MissionQueue = new Queue<MissionTask>();
        public SimulationMetrics Metrics = new SimulationMetrics();

        // ==================== RandomContinuous HP Tracking ====================
        public HashSet<string> OccupiedHelipads = new HashSet<string>();
        public Dictionary<string, string> AircraftOriginHP = new Dictionary<string, string>();   // aircraftId → HP
        public Dictionary<string, string> AircraftDestHP = new Dictionary<string, string>();     // aircraftId → HP
        private List<AircraftAgent> waitingAircraft = new List<AircraftAgent>();

        // Helipad retry: missions that failed initial planning, retrying at helipad
        private Dictionary<string, PendingMissionRetry> pendingMissions = new Dictionary<string, PendingMissionRetry>();
        private readonly Dictionary<string, AircraftState> pendingAircraftSpawns =
            new Dictionary<string, AircraftState>();
        // Helipad retry: chained aircraft waiting at helipad for replanning
        private Dictionary<string, AircraftAgent> helipadWaitingAgents = new Dictionary<string, AircraftAgent>();
        private readonly Dictionary<string, float> completedMissionAt =
            new Dictionary<string, float>(System.StringComparer.Ordinal);
        private int randomFleetTargetCount;
        public int RandomFleetTargetCount => randomFleetTargetCount;
        // Simulation data recorder
        private SimulationRecorder recorder;
        private SarSimulationHistory simulationHistory;

        // Traffic signal system
        private List<IntersectionController> _intersections = new List<IntersectionController>();
        private HashSet<string> _alwaysGreenRightLanes = new HashSet<string>();

        // Cumulative metrics (survive chaining overwrites)
        public int cumulSuccessCount = 0;
        public int cumulFailCount = 0;
        public int cumulTotalMissions = 0;
        public float cumulTotalWaitTime = 0f;
        public float cumulTotalFlightTime = 0f;
        public float cumulTotalFlightLength = 0f;     // total distance flown
        public float cumulTotalStraightDist = 0f;     // total straight-line distance between origin/dest
        public float cumulTotalHeadway = 0f;          // sum of average headway samples
        public int cumulHeadwaySamples = 0;           // number of headway samples
        // Periodic lane/corridor utilization sampling
        public float cumulLaneUtilSum = 0f;
        public float cumulCorrUtilSum = 0f;
        public float cumulLaneUtilSqSum = 0f;         // for variance
        public int cumulUtilSampleCount = 0;
        // Reservation-window utilization variance
        public float cumulResUtilSum = 0f;
        public float cumulResUtilSqSum = 0f;
        public int cumulResUtilSamples = 0;
        private float nextUtilSampleTime = 1f;
        // Signal delay buffer: aircraftId -> list of (time, progress) snapshots for delayed reading
        private Dictionary<string, List<(float time, float progress)>> _delayBuffer = new Dictionary<string, List<(float, float)>>();
        private Dictionary<string, float> _lastSnapshotTime = new Dictionary<string, float>();
        private const int MAX_DELAY_SAMPLES = 64;

        private float cumulSimTime = 0f;

        private class PendingMissionRetry
        {
            public MissionTask Task;
            public float FirstAttemptTime;
            public float LastRetryTime;
        }

        private sealed class RuntimeWaypointClaim
        {
            public string AircraftId;
            public Vector3 Target;
        }

        private readonly Dictionary<string, RuntimeWaypointClaim> runtimeWaypointClaims =
            new Dictionary<string, RuntimeWaypointClaim>();

        // Auto-start flag (survives Play mode transition)
        public static bool AutoStartRequested = false;

        // ==================== Unity Lifecycle ====================
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        void Start()
        {
            if (AutoStartRequested)
            {
                AutoStartRequested = false;
                StartSimulation();
            }
            else if (autoStart)
            {
                StartSimulation();
            }
        }

        void OnEnable()
        {
            System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            // Resolve references
            if (networkManager == null)
                networkManager = FindObjectOfType<NetworkManager>();
            if (vertiportManager == null)
                vertiportManager = FindObjectOfType<VertiportManager>();
            if (config == null)
                config = Resources.Load<SimulationConfig>("DefaultSimulationConfig");

            InitializeDataPaths();

            LoadNetworkData();
            LoadVertiportData();
            BuildAuxiliaryMaps();
            BuildLaneStates();
            BuildVertiportStates();
            BuildTrafficSignalSystem();

            pathPlanner = new PathPlanner(this);

            stopwatch.Stop();
            Debug.Log($"[SimulationManager] Initialization complete in {stopwatch.ElapsedMilliseconds} ms. " +
                      $"Nodes={Nodes.Count}, Edges={Edges.Count}, Lanes={Lanes.Count}, " +
                      $"Vertiports={Vertiports.Count}, Helipads={Helipads.Count}, Corridors={Corridors.Count}");
        }

        void Update()
        {
            if (!simulationRunning) return;

            cumulSimTime += Time.deltaTime;
            ProcessPendingAircraftSpawns();

            // The operator-controlled duration limit belongs to the endless random-demand
            // experiment only. File-driven demand always runs until its route file completes.
            if (HasReachedRandomDurationLimit())
            {
                Debug.Log($"[SimulationManager] Auto-stop: reached max duration {maxSimDuration}s");
                StopSimulation(true);
                return;
            }

            if (collisionPaused && Time.timeScale > 0.001f)
                collisionPaused = false;

            // Periodic utilization sampling
            if (Time.time >= nextUtilSampleTime)
            {
                nextUtilSampleTime = Time.time + 2f; // every 2 seconds
                SampleUtilization();
            }

            if (config != null && config.missionMode == MissionGenerationMode.RandomContinuous)
            {
                // Spawn aircraft from queue when departure time arrives
                ProcessMissionQueue();
                // Process completed missions → chain next
                ProcessCompletedMissions();
                // Check waiting aircraft
                ProcessWaitingAircraft();
                // Retry pending missions at helipad
                ProcessPendingMissions();
                // Retry chained aircraft waiting at helipad
                ProcessHelipadWaitingAgents();
            }
            else
            {
                // File-driven missions are one-shot: remove each aircraft as soon
                // as it reaches its destination and releases runtime occupancy.
                ProcessCompletedFileMissions();
                if (AircraftObjects.Count == 0 && MissionQueue.Count == 0 &&
                    pendingAircraftSpawns.Count == 0 && pendingMissions.Count == 0 &&
                    cumulTotalMissions > 0)
                {
                    CollectMetrics();
                    SaveResultsToFile();
                    StopSimulation(true);
                    Debug.Log("[SimulationManager] All missions complete. Simulation ended.");
                    return;
                }

                // Process mission queue for new tasks
                ProcessMissionQueue();
            }

            // Shared per-frame updates
            bool hasCruise = false;
            foreach (var aircraft in AircraftObjects.Values)
            {
                var agent = aircraft.GetComponent<AircraftAgent>();
                if (agent != null && agent.aircraftState.MissionPhase == "CRUISE")
                    hasCruise = true;
            }

            UpdateCorridorStates();

            // Signal phasing runs independently of cruise state
            UpdateTrafficSignals();

            if (hasCruise)
            {
                UpdateLaneStates();
                foreach (var aircraft in AircraftObjects.Values)
                {
                    var agent = aircraft.GetComponent<AircraftAgent>();
                    if (agent != null && agent.aircraftState.MissionPhase == "CRUISE" &&
                        !string.IsNullOrEmpty(agent.aircraftState.CurrentLaneId) &&
                        LaneStates.ContainsKey(agent.aircraftState.CurrentLaneId))
                    {
                        agent.controller.SetMoveSpeed(
                            agent.aircraftState.AircraftId,
                            LaneStates[agent.aircraftState.CurrentLaneId]
                        );
                    }
                }

                // Sample headway distances
                SampleHeadwayDistances();
            }

            CheckAircraftCollisions();
        }

        private void SampleUtilization()
        {
            cumulUtilSampleCount++;

            // Lane utilization: fraction of lanes with at least 1 aircraft
            int laneOccupied = 0;
            foreach (var state in LaneStates.Values)
                if (state.AircraftsInLane.Count > 0) laneOccupied++;
            float laneUtil = LaneStates.Count > 0 ? (float)laneOccupied / LaneStates.Count : 0f;
            cumulLaneUtilSum += laneUtil;
            cumulLaneUtilSqSum += laneUtil * laneUtil;

            // Corridor utilization
            int corrOccupied = 0;
            foreach (var state in CorridorStatesDict.Values)
                if (state.AircraftsInCorridor.Count > 0) corrOccupied++;
            float corrUtil = CorridorStatesDict.Count > 0 ? (float)corrOccupied / CorridorStatesDict.Count : 0f;
            cumulCorrUtilSum += corrUtil;

            // Reservation-window utilization per resource
            float totalResUtil = 0f;
            int resCount = 0;
            foreach (var kv in RampStatesDict)
            {
                float t = 0f;
                foreach (var w in kv.Value.OccupiedTimeWindows) t += w.EndTime - w.StartTime;
                float util = (cumulSimTime > 0 && kv.Value.OccupiedTimeWindows.Count > 0) ? t / cumulSimTime : 0f;
                totalResUtil += util; resCount++;
            }
            foreach (var kv in ApproachNodeStates)
            {
                float t = 0f;
                foreach (var w in kv.Value.OccupiedTimeWindows) t += w.EndTime - w.StartTime;
                float util = (cumulSimTime > 0 && kv.Value.OccupiedTimeWindows.Count > 0) ? t / cumulSimTime : 0f;
                totalResUtil += util; resCount++;
            }
            foreach (var kv in ApproachEdgeStates)
            {
                float t = 0f;
                foreach (var w in kv.Value.OccupiedTimeWindows) t += w.EndTime - w.StartTime;
                float util = (cumulSimTime > 0 && kv.Value.OccupiedTimeWindows.Count > 0) ? t / cumulSimTime : 0f;
                totalResUtil += util; resCount++;
            }
            foreach (var kv in VertexStatesDict)
            {
                float t = 0f;
                foreach (var w in kv.Value.OccupiedTimeWindows) t += w.EndTime - w.StartTime;
                float util = (cumulSimTime > 0 && kv.Value.OccupiedTimeWindows.Count > 0) ? t / cumulSimTime : 0f;
                totalResUtil += util; resCount++;
            }
            if (resCount > 0)
            {
                float avgResUtil = totalResUtil / resCount;
                cumulResUtilSum += avgResUtil;
                cumulResUtilSqSum += avgResUtil * avgResUtil;
                cumulResUtilSamples++;
            }
        }

        private void SampleHeadwayDistances()
        {
            // Lanes
            foreach (var laneKv in LaneStates)
            {
                var state = laneKv.Value;
                if (state.AircraftsInLane.Count < 2) continue;
                var sorted = new System.Collections.Generic.List<float>(state.AircraftsInLane.Values);
                sorted.Sort();
                float sum = 0f; int n = 0;
                for (int i = 1; i < sorted.Count; i++)
                {
                    float gap = (sorted[i] - sorted[i - 1]) * (Lanes.TryGetValue(laneKv.Key, out var ld) ? ld.Length : 1f);
                    if (gap > 0 && gap < 500f) { sum += gap; n++; }
                }
                if (n > 0) { cumulTotalHeadway += sum / n; cumulHeadwaySamples++; }
            }
            // Corridors
            foreach (var corKv in CorridorStatesDict)
            {
                var state = corKv.Value;
                if (state.AircraftsInCorridor.Count < 2) continue;
                var sorted = new System.Collections.Generic.List<float>(state.AircraftsInCorridor.Values);
                sorted.Sort();
                float sum = 0f; int n = 0;
                for (int i = 1; i < sorted.Count; i++)
                {
                    float gap = sorted[i] - sorted[i - 1]; // corridor progress is already normalized [0,1]
                    if (gap > 0.001f) { sum += gap; n++; }
                }
                if (n > 0) { cumulTotalHeadway += sum / n; cumulHeadwaySamples++; }
            }
        }

        // ==================== Collision Detection ====================
        private HashSet<string> _activeCollisions = new HashSet<string>();
        private Dictionary<string, CollisionStartInfo> _collisionStarts = new Dictionary<string, CollisionStartInfo>();
        public int TotalCollisionEvents { get; private set; }
        public int cumulLaneCollisions = 0;
        public int cumulApproachCollisions = 0;
        public int cumulRampCollisions = 0;
        public int cumulCorridorCollisions = 0;
        public int cumulHelipadCollisions = 0;
        public int cumulUnclassifiedCollisions = 0;

        /// <summary>
        /// Collision totals cover aircraft-aircraft overlap episodes only. Each continuous
        /// overlap of one unordered aircraft pair is assigned to exactly one category.
        /// </summary>
        public int CollisionBreakdownTotal =>
            cumulLaneCollisions + cumulApproachCollisions + cumulRampCollisions +
            cumulCorridorCollisions + cumulHelipadCollisions + cumulUnclassifiedCollisions;

        public int CollisionClassificationResidual =>
            TotalCollisionEvents - CollisionBreakdownTotal;

        private enum CollisionCategory
        {
            Lane,
            Approach,
            Ramp,
            Corridor,
            Helipad,
            Unclassified
        }

        private struct CollisionStartInfo
        {
            public float startTime;
            public string idA, idB, phaseA, phaseB, resA, resB;
            public Vector3 posA, posB;
        }

        void CheckAircraftCollisions()
        {
            var aircraftList = new List<(string id, Vector3 pos, GameObject obj)>();
            foreach (var kvp in AircraftObjects)
            {
                if (kvp.Value == null) continue;
                aircraftList.Add((kvp.Key, kvp.Value.transform.position, kvp.Value));
            }

            // Aircraft-Aircraft collision: distance-based check
            var currentPairs = new HashSet<string>();
            // Use the aircraft prefab's actual BoxCollider world size for collision detection
            float aircraftRadius = 2f; // fallback
            if (aircraftPrefab != null)
            {
                var col = aircraftPrefab.GetComponent<BoxCollider>();
                if (col == null) col = aircraftPrefab.GetComponentInChildren<BoxCollider>();
                if (col != null)
                {
                    Vector3 worldSize = Vector3.Scale(col.size, aircraftPrefab.transform.localScale);
                    aircraftRadius = Mathf.Min(worldSize.x, worldSize.z) * 0.5f;
                }
            }
            for (int i = 0; i < aircraftList.Count; i++)
            {
                for (int j = i + 1; j < aircraftList.Count; j++)
                {
                    float dist = Vector3.Distance(aircraftList[i].pos, aircraftList[j].pos);
                    string pairKey = string.Compare(aircraftList[i].id, aircraftList[j].id) < 0
                        ? $"{aircraftList[i].id}<->{aircraftList[j].id}"
                        : $"{aircraftList[j].id}<->{aircraftList[i].id}";

                    if (dist < aircraftRadius)
                    {
                        currentPairs.Add(pairKey);
                        if (_activeCollisions.Add(pairKey))
                        {
                            var agentI = aircraftList[i].obj.GetComponent<AircraftAgent>();
                            var agentJ = aircraftList[j].obj.GetComponent<AircraftAgent>();
                            _collisionStarts[pairKey] = new CollisionStartInfo
                            {
                                startTime = Time.time,
                                idA = aircraftList[i].id, idB = aircraftList[j].id,
                                phaseA = agentI?.aircraftState?.MissionPhase ?? "?",
                                phaseB = agentJ?.aircraftState?.MissionPhase ?? "?",
                                resA = GetCollisionResource(agentI), resB = GetCollisionResource(agentJ),
                                posA = aircraftList[i].pos, posB = aircraftList[j].pos
                            };

                            Debug.LogError(
                                $"[COLLISION START] {pairKey} | " +
                                $"{aircraftList[i].id}({_collisionStarts[pairKey].phaseA})@{_collisionStarts[pairKey].resA} " +
                                $"pos={aircraftList[i].pos} [{GetNodeInfo(agentI, aircraftList[i].pos)}] <-> " +
                                $"{aircraftList[j].id}({_collisionStarts[pairKey].phaseB})@{_collisionStarts[pairKey].resB} " +
                                $"pos={aircraftList[j].pos} [{GetNodeInfo(agentJ, aircraftList[j].pos)}] | " +
                                $"distance={dist:F3} t={Time.time:F2}");

                            // Pause and focus camera on collision
                            if (pauseOnCollision && !collisionPaused)
                            {
                                Vector3 mid = (aircraftList[i].pos + aircraftList[j].pos) * 0.5f;
                                PauseForCollision(pairKey, mid);
                            }
                        }
                    }
                }
            }

            // Log ended collisions (merged start+end)
            var ended = new List<string>();
            foreach (var key in _activeCollisions)
                if (!currentPairs.Contains(key)) ended.Add(key);

            foreach (var key in ended)
                FinalizeCollisionEvent(key, Time.time, "separated");

            // Aircraft-building overlaps remain diagnostic-only. They are sampled every
            // frame and do not yet have the unordered-pair episode tracking used by the
            // aircraft-aircraft total above, so including them would introduce duplicates.
            int buildingLayer = LayerMask.NameToLayer("Buildings");
            if (buildingLayer >= 0)
            {
                int buildingMask = 1 << buildingLayer;
                float buildingRadius = 2f;
                foreach (var ac in aircraftList)
                {
                    var hits = Physics.OverlapSphere(ac.pos, buildingRadius, buildingMask);
                    foreach (var hit in hits)
                    {
                        Debug.LogError($"[COLLISION] Aircraft-Building: {ac.id} hit '{hit.name}' " +
                                         $"at {ac.pos} (building: {hit.transform.position})");
                    }
                }
            }
        }

        private string GetCollisionResource(AircraftAgent agent)
        {
            if (agent?.aircraftState == null) return "none";
            var st = agent.aircraftState;
            if (!string.IsNullOrEmpty(st.CurrentLaneId)) return $"lane:{st.CurrentLaneId}";
            string phase = st.MissionPhase;
            Vector3 pos = agent.transform.position;
            // Check corridor
            foreach (var kv in CorridorStatesDict)
                if (kv.Value.AircraftsInCorridor.ContainsKey(st.AircraftId))
                    return $"corridor:{kv.Key}";
            string positionResource = MatchPositionToResource(pos, 2.5f);
            if (positionResource != "noMatch") return positionResource;

            // Fall back to a reservation that is active now. Do not report the
            // first historical booking, which can be far from the aircraft.
            foreach (var kv in ApproachNodeStates)
                foreach (var w in kv.Value.OccupiedTimeWindows)
                    if (w.OccupiedAircraftId == st.AircraftId &&
                        w.StartTime <= Time.time && Time.time < w.EndTime)
                        return $"approach:{kv.Key}";
            foreach (var kv in RampStatesDict)
                foreach (var w in kv.Value.OccupiedTimeWindows)
                    if (w.OccupiedAircraftId == st.AircraftId &&
                        w.StartTime <= Time.time && Time.time < w.EndTime)
                        return $"ramp:{kv.Key}";
            return $"pos:({pos.x:F0},{pos.z:F0})";
        }

        private static CollisionCategory ClassifyCollisionResource(string resource)
        {
            if (string.IsNullOrWhiteSpace(resource)) return CollisionCategory.Unclassified;
            if (resource.StartsWith("lane:", System.StringComparison.Ordinal))
                return CollisionCategory.Lane;
            if (resource.StartsWith("approach:", System.StringComparison.Ordinal) ||
                resource.StartsWith("apprNode=", System.StringComparison.Ordinal))
                return CollisionCategory.Approach;
            if (resource.StartsWith("ramp:", System.StringComparison.Ordinal) ||
                resource.StartsWith("vertex=", System.StringComparison.Ordinal))
                return CollisionCategory.Ramp;
            if (resource.StartsWith("corridor:", System.StringComparison.Ordinal))
                return CollisionCategory.Corridor;
            if (resource.StartsWith("helipad=", System.StringComparison.Ordinal))
                return CollisionCategory.Helipad;
            return CollisionCategory.Unclassified;
        }

        /// <summary>
        /// Resolve the event from both participants without an arbitrary resource-priority
        /// rule. One known resource plus an unavailable position sample is sufficient;
        /// conflicting known resource types are retained as unclassified/mixed instead of
        /// being silently assigned to whichever prefix was checked first.
        /// </summary>
        private static CollisionCategory ResolveCollisionCategory(string resourceA, string resourceB)
        {
            CollisionCategory categoryA = ClassifyCollisionResource(resourceA);
            CollisionCategory categoryB = ClassifyCollisionResource(resourceB);
            if (categoryA == categoryB) return categoryA;
            if (categoryA == CollisionCategory.Unclassified) return categoryB;
            if (categoryB == CollisionCategory.Unclassified) return categoryA;
            return CollisionCategory.Unclassified;
        }

        private void IncrementCollisionCategory(CollisionCategory category)
        {
            switch (category)
            {
                case CollisionCategory.Lane: cumulLaneCollisions++; break;
                case CollisionCategory.Approach: cumulApproachCollisions++; break;
                case CollisionCategory.Ramp: cumulRampCollisions++; break;
                case CollisionCategory.Corridor: cumulCorridorCollisions++; break;
                case CollisionCategory.Helipad: cumulHelipadCollisions++; break;
                default: cumulUnclassifiedCollisions++; break;
            }
        }

        private bool FinalizeCollisionEvent(string pairKey, float endTime, string endReason)
        {
            // The active-pair removal is the uniqueness gate: repeated separation checks or
            // shutdown paths cannot count the same continuous overlap episode twice.
            if (!_activeCollisions.Remove(pairKey)) return false;

            CollisionCategory category = CollisionCategory.Unclassified;
            bool hasStartInfo = _collisionStarts.TryGetValue(pairKey, out CollisionStartInfo cs);
            if (hasStartInfo)
                category = ResolveCollisionCategory(cs.resA, cs.resB);

            _collisionStarts.Remove(pairKey);
            IncrementCollisionCategory(category);
            TotalCollisionEvents++;

            if (hasStartInfo)
            {
                float duration = Mathf.Max(0f, endTime - cs.startTime);
                Vector3 midpoint = (cs.posA + cs.posB) * 0.5f;
                recorder?.RecordEvent(
                    "Collision",
                    pairKey,
                    $"{category}; {endReason}; duration={duration:F2}s; " +
                    $"{cs.idA}({cs.phaseA})@{cs.resA} <-> {cs.idB}({cs.phaseB})@{cs.resB}",
                    midpoint);
                Debug.LogError($"[COLLISION] {pairKey} | category={category} | " +
                               $"reason={endReason} | dur={duration:F2}s | " +
                               $"{cs.idA}({cs.phaseA})@{cs.resA} <-> " +
                               $"{cs.idB}({cs.phaseB})@{cs.resB}");
            }
            else
            {
                recorder?.RecordEvent(
                    "Collision",
                    pairKey,
                    $"{category}; {endReason}; start metadata unavailable",
                    Vector3.zero);
                Debug.LogError($"[COLLISION END] {pairKey} | category={category} | " +
                               $"reason={endReason} | start metadata unavailable | t={endTime:F2}");
            }
            return true;
        }

        private void FinalizeActiveCollisions(string endReason)
        {
            foreach (string pairKey in _activeCollisions.ToArray())
                FinalizeCollisionEvent(pairKey, Time.time, endReason);
        }

        private void ResetCollisionAccounting()
        {
            TotalCollisionEvents = 0;
            cumulLaneCollisions = 0;
            cumulApproachCollisions = 0;
            cumulRampCollisions = 0;
            cumulCorridorCollisions = 0;
            cumulHelipadCollisions = 0;
            cumulUnclassifiedCollisions = 0;
            _activeCollisions.Clear();
            _collisionStarts.Clear();
        }

        /// <summary>
        /// Returns info about what resource node/vertex an aircraft is at,
        /// for collision debugging.
        /// </summary>
        private string GetNodeInfo(AircraftAgent agent, Vector3 actualPos)
        {
            if (agent == null) return "agent=null";
            var st = agent.aircraftState;
            if (st == null) return "state=null";

            // Determine active trajectory based on phase
            List<RampPoint> traj = null;
            int curIdx = -1;
            if (st.MissionPhase == "TAKEOFF" || st.MissionPhase == "CRUISE")
            {
                traj = st.RampTrajectory;
                curIdx = agent.currentTakeoffpointIndex;
            }
            else if (st.MissionPhase == "LANDING")
            {
                traj = st.LandingTrajectory;
                curIdx = agent.currentLandingpointIndex;
            }

            // Find closest trajectory point
            if (traj != null && traj.Count > 0)
            {
                float minD = float.MaxValue;
                int closest = 0;
                for (int k = 0; k < traj.Count; k++)
                {
                    float d = Vector3.Distance(actualPos, traj[k].Point);
                    if (d < minD) { minD = d; closest = k; }
                }
                var pt = traj[closest];
                // Match trajectory position to known resources by position
                string resInfo = MatchPositionToResource(pt.Point);

                return $"curIdx={curIdx} closestTraj[{closest}/{traj.Count}] ptPos=({pt.Point.x:F1},{pt.Point.y:F1},{pt.Point.z:F1}) {resInfo} WaitTime={pt.WaitTime:F2} ReachTime={pt.ReachTime:F2} dist2actual={minD:F2}";
            }
            return "noTraj";
        }

        /// <summary>Match a position to the closest approach node or HS vertex (by position).</summary>
        private string MatchPositionToResource(Vector3 pos, float matchDistance = 0.5f)
        {
            float bestDist = matchDistance;
            string bestId = null;
            string bestType = null;

            foreach (var kv in ApproachNodes)
            {
                float d = Vector3.Distance(pos, kv.Value.Position);
                if (d < bestDist) { bestDist = d; bestId = kv.Key; bestType = "apprNode"; }
            }
            foreach (var kv in HoldingStructures)
            {
                foreach (var v in kv.Value.Vertices)
                {
                    float d = Vector3.Distance(pos, v.Position);
                    if (d < bestDist) { bestDist = d; bestId = v.VertexId; bestType = "vertex"; }
                }
            }
            foreach (var kv in Helipads)
            {
                float d = Vector3.Distance(pos, GetHelipadCenter(kv.Value));
                if (d < bestDist) { bestDist = d; bestId = kv.Key; bestType = "helipad"; }
            }
            if (bestId != null)
                return $"{bestType}={bestId} (matchDist={bestDist:F2})";
            return "noMatch";
        }

        // ==================== Data Loading ====================
        void LoadNetworkData()
        {
            // This method is also used after runtime topology edits. Replacing entries is
            // insufficient because an edited axis edge may become non-viable and therefore
            // disappear from FullNetwork entirely. Clear the previous snapshot first so
            // stale lanes/edges cannot remain visible or routable after regeneration.
            Nodes.Clear();
            Edges.Clear();
            Lanes.Clear();
            LaneConnectionCorridors.Clear();

            if (networkManager == null || networkManager.FullNetwork == null)
            {
                Debug.LogError("[SimulationManager] NetworkManager or FullNetwork not available.");
                return;
            }

            Network net = networkManager.FullNetwork;
            NetworkValidationResult validation = MultilayerNetworkUtility.NormalizeAndValidate(net);
            foreach (string warning in validation.Warnings)
                Debug.LogWarning($"[Network] {warning}");
            foreach (string error in validation.Errors)
                Debug.LogError($"[Network] {error}");

            foreach (var node in net.Nodes)
                Nodes[node.NodeId] = node;

            foreach (var edge in net.Edges)
            {
                Edges[edge.EdgeId] = edge;
                if (edge.Lanes != null)
                {
                    foreach (var lane in edge.Lanes)
                        Lanes[lane.LaneId] = lane;
                }
            }
            foreach (LaneConnectionCorridor corridor in net.LaneConnectionCorridors)
            {
                if (corridor == null ||
                    string.IsNullOrWhiteSpace(corridor.CorridorId) ||
                    !Lanes.ContainsKey(corridor.FromLaneId) ||
                    !Lanes.ContainsKey(corridor.ToLaneId))
                {
                    Debug.LogError($"[Network] Skipped invalid lane connection corridor " +
                                   $"'{corridor?.CorridorId ?? "<null>"}'.");
                    continue;
                }

                if (Lanes.ContainsKey(corridor.CorridorId))
                {
                    Debug.LogError($"[Network] Lane connection ID collides with a lane: {corridor.CorridorId}.");
                    continue;
                }

                LaneConnectionCorridors[corridor.CorridorId] = corridor;
                Lanes[corridor.CorridorId] = MultilayerNetworkUtility.CreateVirtualLane(corridor);
            }

            Debug.Log($"[SimulationManager] Network loaded: {Nodes.Count} nodes, {Edges.Count} edges, " +
                      $"{Lanes.Count} lanes ({LaneConnectionCorridors.Count} layer connectors).");
        }

        private bool HasReachedRandomDurationLimit()
        {
            return config != null && config.missionMode == MissionGenerationMode.RandomContinuous &&
                   maxSimDuration > 0f && cumulSimTime >= maxSimDuration;
        }

        public void NotifyMissionCompleted(AircraftState state, Vector3 finalPosition)
        {
            if (state == null) return;
            MissionCompleted?.Invoke(state, finalPosition);
        }

        void LoadVertiportData()
        {
            // Runtime CRUD may remove a facility resource. Always rebuild these lookup
            // tables from the authoritative VertiportManager data instead of retaining
            // objects that no longer exist.
            Vertiports.Clear();
            Helipads.Clear();
            HoldingStructures.Clear();
            Circles.Clear();
            Vertices.Clear();
            Ramps.Clear();
            ApproachNodes.Clear();
            ApproachEdges.Clear();
            Corridors.Clear();
            HelipadToVertiportMap.Clear();
            HSToVertiportMap.Clear();
            VertexToHSMap.Clear();
            CorridorToVertiportMap.Clear();
            ApproachNodeToVertiportMap.Clear();

            if (vertiportManager == null || vertiportManager.Vertiports == null)
            {
                Debug.LogError("[SimulationManager] VertiportManager or Vertiports data not available.");
                return;
            }

            foreach (var vp in vertiportManager.Vertiports)
            {
                Vertiports[vp.VertiportId] = vp;

                // Helipads
                if (vp.Helipads != null)
                {
                    foreach (var hp in vp.Helipads)
                    {
                        Helipads[hp.HelipadId] = hp;
                        HelipadToVertiportMap[hp.HelipadId] = vp.VertiportId;
                    }
                }

                // HoldingStructures
                if (vp.HoldingStructures != null)
                {
                    foreach (var hs in vp.HoldingStructures)
                    {
                        HoldingStructures[hs.HoldingStructureId] = hs;
                        HSToVertiportMap[hs.HoldingStructureId] = vp.VertiportId;

                        // Circles
                        if (hs.Circles != null)
                        {
                            foreach (var c in hs.Circles)
                                Circles[c.CircleId] = c;
                        }

                        // Vertices
                        if (hs.Vertices != null)
                        {
                            foreach (var v in hs.Vertices)
                            {
                                Vertices[v.VertexId] = v;
                                VertexToHSMap[v.VertexId] = hs.HoldingStructureId;
                            }
                        }

                        // Ramps
                        if (hs.Ramps != null)
                        {
                            foreach (var r in hs.Ramps)
                                Ramps[r.RampId] = r;
                        }
                    }
                }

                // ApproachNetwork
                if (vp.ApproachNet != null)
                {
                    if (vp.ApproachNet.Nodes != null)
                    {
                        foreach (var an in vp.ApproachNet.Nodes)
                        {
                            ApproachNodes[an.NodeId] = an;
                            ApproachNodeToVertiportMap[an.NodeId] = vp.VertiportId;
                        }
                    }
                    if (vp.ApproachNet.Edges != null)
                    {
                        foreach (var ae in vp.ApproachNet.Edges)
                            ApproachEdges[ae.EdgeId] = ae;
                    }
                }

                // Corridors
                if (vp.Corridors != null)
                {
                    foreach (var cor in vp.Corridors)
                    {
                        Corridors[cor.CorridorId] = cor;
                        CorridorToVertiportMap[cor.CorridorId] = vp.VertiportId;
                    }
                }
            }

            Debug.Log($"[SimulationManager] Vertiport data loaded: {Vertiports.Count} VPs, {Helipads.Count} HPs, " +
                      $"{HoldingStructures.Count} HSs, {Circles.Count} circles, {Vertices.Count} vertices, " +
                      $"{Ramps.Count} ramps, {ApproachNodes.Count} apprNodes, {ApproachEdges.Count} apprEdges, " +
                      $"{Corridors.Count} corridors.");
        }

        void BuildAuxiliaryMaps()
        {
            // LaneToEdgeMap
            LaneToEdgeMap.Clear();
            foreach (var edge in Edges.Values)
            {
                if (edge.Lanes == null) continue;
                foreach (var lane in edge.Lanes)
                    LaneToEdgeMap[lane.LaneId] = edge.EdgeId;
            }

            // CorridorToLaneMap
            CorridorToLaneMap.Clear();
            foreach (var cor in Corridors.Values)
            {
                if (!string.IsNullOrEmpty(cor.LinkLaneId))
                    CorridorToLaneMap[cor.CorridorId] = cor.LinkLaneId;
            }

            // RampToCorridorMap (via Ramp.LinkCorridors + Corridor.LinkRampId fallback)
            RampToCorridorMap.Clear();
            foreach (var ramp in Ramps.Values)
            {
                if (ramp.LinkCorridors != null)
                {
                    foreach (var (corridorId, _) in ramp.LinkCorridors)
                    {
                        if (!string.IsNullOrEmpty(corridorId))
                            RampToCorridorMap[ramp.RampId] = corridorId;
                    }
                }
            }
            // Fallback: also build from corridor side (LinkRampId → CorridorId)
            foreach (var cor in Corridors.Values)
            {
                if (!string.IsNullOrEmpty(cor.LinkRampId) && !RampToCorridorMap.ContainsKey(cor.LinkRampId))
                    RampToCorridorMap[cor.LinkRampId] = cor.CorridorId;
            }

            Debug.Log($"[SimulationManager] Auxiliary maps built: LaneToEdge={LaneToEdgeMap.Count}, " +
                      $"CorridorToLane={CorridorToLaneMap.Count}, RampToCorridor={RampToCorridorMap.Count}.");
        }

        void BuildLaneStates()
        {
            LaneStates.Clear();
            foreach (var lane in Lanes.Values)
            {
                float length = 0f;
                if (lane.Centerline != null && lane.Centerline.Count >= 2)
                {
                    for (int i = 1; i < lane.Centerline.Count; i++)
                        length += Vector3.Distance(lane.Centerline[i - 1], lane.Centerline[i]);
                }

                LaneStates[lane.LaneId] = new LaneState
                {
                    LaneId = lane.LaneId,
                    Length = length,
                    AircraftsInLane = new Dictionary<string, float>(),
                    SignalCheck = false,
                    CapabilityCheck = false,
                    EntryPermission = true,
                    ExitPermission = true
                };
            }
            Debug.Log($"[SimulationManager] {LaneStates.Count} LaneStates initialized.");
        }

        void BuildVertiportStates()
        {
            HSStates.Clear();
            HelipadStatesDict.Clear();
            VertexStatesDict.Clear();
            RampStatesDict.Clear();
            CorridorStatesDict.Clear();

            foreach (var hp in Helipads.Values)
                HelipadStatesDict[hp.HelipadId] = new HelipadState { HelipadId = hp.HelipadId };

            foreach (var hs in HoldingStructures.Values)
                HSStates[hs.HoldingStructureId] = new HoldingStructureState { HoldingStructureId = hs.HoldingStructureId };

            foreach (var v in Vertices.Values)
                VertexStatesDict[v.VertexId] = new VertexState { VertexId = v.VertexId };

            foreach (var r in Ramps.Values)
                RampStatesDict[r.RampId] = new RampState { RampId = r.RampId };

            foreach (var c in Corridors.Values)
                CorridorStatesDict[c.CorridorId] = new CorridorState { CorridorId = c.CorridorId };

            ApproachNodeStates.Clear();
            ApproachEdgeStates.Clear();
            foreach (var an in ApproachNodes.Values)
                ApproachNodeStates[an.NodeId] = new ApproachNodeState { NodeId = an.NodeId };
            foreach (var ae in ApproachEdges.Values)
                ApproachEdgeStates[ae.EdgeId] = new ApproachEdgeState { EdgeId = ae.EdgeId };

            BuildPositionMap();

            Debug.Log($"[SimulationManager] Vertiport states initialized: " +
                      $"{HelipadStatesDict.Count} HP, {HSStates.Count} HS, " +
                      $"{VertexStatesDict.Count} Vertex, {RampStatesDict.Count} Ramp, " +
                      $"{CorridorStatesDict.Count} Corridor, {ApproachNodeStates.Count} ApprNode, " +
                      $"{ApproachEdgeStates.Count} ApprEdge.");
        }

        // ==================== Simulation Control ====================
        public void StartSimulation()
        {
            if (simulationRunning)
            {
                Debug.LogWarning("[SimulationManager] Simulation already running.");
                return;
            }

            if (config == null)
            {
                Debug.LogError("[SimulationManager] Config not assigned.");
                return;
            }

            simulationRunning = true;
            if (!timingSettingsCaptured)
            {
                previousMaximumDeltaTime = Time.maximumDeltaTime;
                timingSettingsCaptured = true;
            }
            // Prevent a slow frame from triggering a large fixed-update catch-up burst.
            // Runtime movement uses Update/MoveTowards, so capping catch-up preserves
            // responsiveness without changing the configured simulation time scale.
            Time.maximumDeltaTime = Mathf.Min(previousMaximumDeltaTime, 0.1f);
            randomMissionCounter = 0;
            randomFleetTargetCount = 0;
            cumulSimTime = 0f;
            ResetCollisionAccounting();
            cumulSuccessCount = 0; cumulFailCount = 0; cumulTotalMissions = 0;
            cumulTotalWaitTime = 0; cumulTotalFlightTime = 0;
            cumulTotalFlightLength = 0; cumulTotalStraightDist = 0;
            cumulTotalHeadway = 0; cumulHeadwaySamples = 0;
            cumulLaneUtilSum = 0; cumulCorrUtilSum = 0; cumulLaneUtilSqSum = 0; cumulUtilSampleCount = 0;
            cumulResUtilSum = 0; cumulResUtilSqSum = 0; cumulResUtilSamples = 0;
            nextUtilSampleTime = 1f;
            _occupancyLog.Clear();

            // Start data recording
            if (recorder == null)
                recorder = FindObjectOfType<SimulationRecorder>();
            if (recorder == null)
            {
                var go = new GameObject("SimulationRecorder");
                recorder = go.AddComponent<SimulationRecorder>();
            }
            recorder.StartRecording(this);
            simulationHistory = SarSimulationHistory.Ensure(this);
            simulationHistory?.BeginRun(additionalParameters: new Dictionary<string, string>
            {
                ["missionMode"] = config.missionMode.ToString(),
                ["maxSimDuration"] = maxSimDuration.ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture)
            });
            Metrics = new SimulationMetrics();
            OccupiedHelipads.Clear();
            AircraftOriginHP.Clear();
            AircraftDestHP.Clear();
            waitingAircraft.Clear();
            pendingMissions.Clear();
            pendingAircraftSpawns.Clear();
            helipadWaitingAgents.Clear();
            completedMissionAt.Clear();
            runtimeWaypointClaims.Clear();

            // Clear all resource booking data between batches
            foreach (var s in RampStatesDict.Values) s.OccupiedTimeWindows.Clear();
            foreach (var s in ApproachNodeStates.Values) s.OccupiedTimeWindows.Clear();
            foreach (var s in ApproachEdgeStates.Values) s.OccupiedTimeWindows.Clear();
            foreach (var s in VertexStatesDict.Values) s.OccupiedTimeWindows.Clear();
            foreach (var s in HelipadStatesDict.Values) s.OccupiedTimeWindows.Clear();
            foreach (var s in CorridorStatesDict.Values) s.OccupiedTimeWindows.Clear();
            _delayBuffer.Clear();
            _lastSnapshotTime.Clear();

            if (config.missionMode == MissionGenerationMode.JSON)
            {
                if (MissionQueue.Count == 0)
                    LoadMissionsFromJSON();
            }
            else
            {
                InitRandomContinuous();
            }

            Debug.Log($"[SimulationManager] Simulation started. Mode={config.missionMode}, Aircraft={AircraftObjects.Count}");
            SimulationStarted?.Invoke();
#if UNITY_EDITOR
            if (enableJointSimulation)
                BeginJointSimulation();
#endif
        }

#if UNITY_EDITOR
        // Joint simulation is an Editor-only pilot. It is compiled out of the Windows
        // player entirely, because the bridge needs a Python interpreter, an Unreal
        // host and an AirSim RPC endpoint that the portable workbench does not carry.
        [Header("====== Joint AirSim ======")]
        [Tooltip("When enabled, starting a simulation launches the AirSim bridge and mirrors every aircraft pose.")]
        public bool enableJointSimulation = false;

        private AirSimJointBridge jointBridge;

        private void BeginJointSimulation()
        {
            if (jointBridge == null)
                jointBridge = GetComponent<AirSimJointBridge>();
            if (jointBridge == null)
                jointBridge = gameObject.AddComponent<AirSimJointBridge>();
            jointBridge.Begin(this);
        }

        private void EndJointSimulation()
        {
            if (jointBridge != null)
                jointBridge.End();
        }
#endif

        public void StopSimulation()
        {
            StopSimulation(false);
        }

        public void StopSimulation(bool completedNaturally)
        {
            if (!simulationRunning) return;
            simulationRunning = false;
#if UNITY_EDITOR
            EndJointSimulation();
#endif
            collisionPaused = false;
            if (timingSettingsCaptured)
                Time.maximumDeltaTime = previousMaximumDeltaTime;
            // A continuous overlap that reaches the run boundary still belongs to this run.
            // Finalize it before the recorder and metrics are written.
            FinalizeActiveCollisions("simulation stopped");
            try
            {
                if (recorder != null) recorder.StopRecording();
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"[SimulationManager] Recording finalization failed: {exception}");
            }
            try
            {
                SaveOccupancyLog();
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"[SimulationManager] Occupancy export failed: {exception}");
            }
            try
            {
                CollectMetricsCSV();
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"[SimulationManager] Metrics export failed: {exception}");
            }
            try
            {
                simulationHistory?.CompleteRun(completedNaturally
                    ? SarSimulationRunStatus.Completed
                    : SarSimulationRunStatus.Stopped, cumulSimTime);
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"[SimulationManager] History finalization failed: {exception}");
            }
            try
            {
                SimulationFinished?.Invoke(completedNaturally);
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"[SimulationManager] Simulation-finished listener failed: {exception}");
            }
            ClearAllAircrafts();
            Debug.Log("[SimulationManager] Simulation stopped.");
        }

        private void WriteCollisionMetrics(TextWriter writer)
        {
            writer.WriteLine($"Total Collisions,{TotalCollisionEvents},");
            writer.WriteLine($"Lane Collisions,{cumulLaneCollisions},");
            writer.WriteLine($"Approach Collisions,{cumulApproachCollisions},");
            writer.WriteLine($"Ramp Collisions,{cumulRampCollisions},");
            writer.WriteLine($"Corridor Collisions,{cumulCorridorCollisions},");
            writer.WriteLine($"Helipad Collisions,{cumulHelipadCollisions},");
            writer.WriteLine($"Unclassified / Mixed-Resource Collisions,{cumulUnclassifiedCollisions},");
            writer.WriteLine($"Collision Classification Residual,{CollisionClassificationResidual},");
        }

        private void CollectMetricsCSV()
        {
            float simDuration = cumulSimTime;
            int success = cumulSuccessCount;
            int fail = cumulFailCount;
            int totalMissions = success + fail;  // only completed missions
            float totalFlightTime = cumulTotalFlightTime;
            float totalWaitTime = cumulTotalWaitTime;
            float totalFlightLength = cumulTotalFlightLength;
            float totalStraightDist = cumulTotalStraightDist;

            // Reservation utilization
            int totalBookings = 0;
            float totalBookedTime = 0f;
            foreach (var kv in RampStatesDict)
                foreach (var w in kv.Value.OccupiedTimeWindows)
                { totalBookings++; totalBookedTime += w.EndTime - w.StartTime; }
            foreach (var kv in ApproachNodeStates)
                foreach (var w in kv.Value.OccupiedTimeWindows)
                { totalBookings++; totalBookedTime += w.EndTime - w.StartTime; }
            foreach (var kv in ApproachEdgeStates)
                foreach (var w in kv.Value.OccupiedTimeWindows)
                { totalBookings++; totalBookedTime += w.EndTime - w.StartTime; }
            foreach (var kv in VertexStatesDict)
                foreach (var w in kv.Value.OccupiedTimeWindows)
                { totalBookings++; totalBookedTime += w.EndTime - w.StartTime; }

            // Lane/corridor utilization: time-averaged from periodic samples
            float laneUtil = cumulUtilSampleCount > 0 ? cumulLaneUtilSum / cumulUtilSampleCount : 0f;
            float corrUtil = cumulUtilSampleCount > 0 ? cumulCorrUtilSum / cumulUtilSampleCount : 0f;
            float laneUtilVar = cumulUtilSampleCount > 1 ?
                (cumulLaneUtilSqSum / cumulUtilSampleCount) - (laneUtil * laneUtil) : 0f;
            // Reservation utilization variance (across resources)
            float avgResUtilPerResource = cumulResUtilSamples > 0 ? cumulResUtilSum / cumulResUtilSamples : 0f;
            float resUtilVar = cumulResUtilSamples > 1 ?
                (cumulResUtilSqSum / cumulResUtilSamples) - (avgResUtilPerResource * avgResUtilPerResource) : 0f;

            // Computed metrics
            float avgWait = success > 0 ? totalWaitTime / success : 0f;
            float avgMissionTime = success > 0 ? totalFlightTime / success : 0f;
            float avgTravelDist = success > 0 ? totalFlightLength / success : 0f;
            float avgStraightDist = success > 0 ? totalStraightDist / success : 0f;
            float detourRatio = totalStraightDist > 0 ? totalFlightLength / totalStraightDist : 0f;
            double throughputPerSec = simDuration > 0 ? (double)success / simDuration : 0.0;
            float avgHeadway = cumulHeadwaySamples > 0 ? cumulTotalHeadway / cumulHeadwaySamples : 0f;
            float resUtil = simDuration > 0 && totalBookings > 0 ? totalBookedTime / (totalBookings * simDuration) : 0f;

            string outDir = GetOutputDirectory();
            string csvPath = System.IO.Path.Combine(outDir, "MetricsTable.csv");
            using (var w = new System.IO.StreamWriter(csvPath))
            {
                // 3-column format: Label,Value, (spacer)
                w.WriteLine("Metrics,,");
                w.WriteLine(System.FormattableString.Invariant($"Simulation Duration (s),{simDuration:F1},"));
                w.WriteLine($"Throughput (successful missions),{success},");
                w.WriteLine(System.FormattableString.Invariant($"Throughput (missions/s),{throughputPerSec:F4},"));
                w.WriteLine(System.FormattableString.Invariant($"Average Travel Distance (m),{avgTravelDist:F1},"));
                w.WriteLine(System.FormattableString.Invariant($"Average Straight-line Distance (m),{avgStraightDist:F1},"));
                w.WriteLine(System.FormattableString.Invariant($"Detour Ratio,{detourRatio:F2},"));
                w.WriteLine(System.FormattableString.Invariant($"Average Mission Duration (s),{avgMissionTime:F1},"));
                w.WriteLine(System.FormattableString.Invariant($"Average Resource Wait Time (s),{avgWait:F2},"));
                w.WriteLine(System.FormattableString.Invariant($"Average Headway Distance (m),{avgHeadway:F1},"));
                w.WriteLine(System.FormattableString.Invariant($"Reservation-Window Utilization,{resUtil:F4},"));
                w.WriteLine(System.FormattableString.Invariant($"Lane Utilization (avg over time),{laneUtil:F4},"));
                w.WriteLine(System.FormattableString.Invariant($"Lane Utilization Variance,{laneUtilVar:F6},"));
                w.WriteLine(System.FormattableString.Invariant($"Corridor Utilization (avg over time),{corrUtil:F4},"));
                w.WriteLine(System.FormattableString.Invariant($"Reservation Utilization (avg per resource),{avgResUtilPerResource:F4},"));
                w.WriteLine(System.FormattableString.Invariant($"Reservation Utilization Variance,{resUtilVar:F6},"));
                w.WriteLine($"Utilization Sample Count,{cumulUtilSampleCount},");
                w.WriteLine($"Reservation Util Sample Count,{cumulResUtilSamples},");
                w.WriteLine($"Total Missions,{totalMissions},");
                w.WriteLine($"Successful,{success},");
                w.WriteLine($"Failed,{fail},");
                w.WriteLine(System.FormattableString.Invariant(
                    $"Failure Rate,{(totalMissions > 0 ? (float)fail / totalMissions : 0f):F4},"));
                w.WriteLine($"Total Resource Bookings,{totalBookings},");
                w.WriteLine(System.FormattableString.Invariant(
                    $"Total Booked Resource-Time (s),{totalBookedTime:F1},"));
                WriteCollisionMetrics(w);
                w.WriteLine($"Headway Samples,{cumulHeadwaySamples},");
            }
            Debug.Log($"[SimulationManager] Metrics CSV saved: {csvPath}");
        }

        public bool IsRunning() => simulationRunning;

        void OnDestroy()
        {
            if (simulationRunning)
            {
                simulationRunning = false;
                FinalizeActiveCollisions("simulation interrupted");
                try { if (recorder != null) recorder.StopRecording(); }
                catch (System.Exception exception) { Debug.LogError(exception); }
                try { SaveOccupancyLog(); }
                catch (System.Exception exception) { Debug.LogError(exception); }
                try { CollectMetricsCSV(); }
                catch (System.Exception exception) { Debug.LogError(exception); }
                try { simulationHistory?.CompleteRun(SarSimulationRunStatus.Interrupted, cumulSimTime); }
                catch (System.Exception exception) { Debug.LogError(exception); }
            }
            if (timingSettingsCaptured)
                Time.maximumDeltaTime = previousMaximumDeltaTime;
        }

        // ==================== Mission Management (Editor API) ====================
        public void AddMission(MissionTask task)
        {
            if (task == null) return;
            MissionQueue.Enqueue(task);
            Debug.Log($"[SimulationManager] Mission added: {task.TaskId} ({task.OriginVertiportId} -> {task.DestinationVertiportId})");
        }

        public List<MissionTask> GetPendingMissions()
        {
            return MissionQueue.ToList();
        }

        public void ClearMissionQueue()
        {
            MissionQueue.Clear();
            Debug.Log("[SimulationManager] Mission queue cleared.");
        }

        public bool ReplaceMission(string taskId, MissionTask replacement)
        {
            if (string.IsNullOrWhiteSpace(taskId) || replacement == null) return false;
            List<MissionTask> missions = MissionQueue.ToList();
            int index = missions.FindIndex(task => task != null && task.TaskId == taskId);
            if (index < 0) return false;
            replacement.TaskId = taskId;
            missions[index] = replacement;
            MissionQueue = new Queue<MissionTask>(missions.OrderBy(task => task.DepartureTime));
            Debug.Log($"[SimulationManager] Mission replaced: {taskId}");
            return true;
        }

        public bool RemoveMission(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return false;
            List<MissionTask> missions = MissionQueue
                .Where(task => task == null || task.TaskId != taskId)
                .ToList();
            if (missions.Count == MissionQueue.Count) return false;
            MissionQueue = new Queue<MissionTask>(missions);
            Debug.Log($"[SimulationManager] Mission removed: {taskId}");
            return true;
        }

        public void SaveMissionsToJSON()
        {
            EnsurePathsInitialized();
            SaveMissions(missionsFilePath);
        }

        public string SaveMissions(string filePath)
        {
            EnsurePathsInitialized();
            string path = string.IsNullOrWhiteSpace(filePath)
                ? GetDefaultRouteExportPath()
                : SarPortablePath.Resolve(filePath);
            RouteFileExporter.Save(path, MissionQueue);
            Debug.Log($"[SimulationManager] {MissionQueue.Count} missions saved to: {path}");
            return path;
        }

        /// <summary>
        /// Build a list of available Vertiport IDs (for editor dropdowns)
        /// </summary>
        public List<string> GetAvailableVertiportIds()
        {
            if (vertiportManager == null)
                vertiportManager = FindObjectOfType<VertiportManager>();
            if (vertiportManager != null && vertiportManager.Vertiports != null)
                return vertiportManager.Vertiports.Select(v => v.VertiportId).ToList();
            return new List<string>();
        }

        /// <summary>
        /// Rebuild the simulation lookup tables after a standalone topology edit.
        /// Live topology mutation is deliberately blocked while simulation is running.
        /// </summary>
        public bool RefreshRuntimeTopology()
        {
            if (simulationRunning)
            {
                Debug.LogWarning("[SimulationManager] Stop the simulation before refreshing topology.");
                return false;
            }

            LoadNetworkData();
            LoadVertiportData();
            BuildAuxiliaryMaps();
            BuildLaneStates();
            BuildVertiportStates();
            BuildTrafficSignalSystem();
            pathPlanner = new PathPlanner(this);
            Debug.Log($"[SimulationManager] Runtime topology refreshed: " +
                      $"Nodes={Nodes.Count}, Lanes={Lanes.Count}, Vertiports={Vertiports.Count}.");
            return true;
        }

        // ==================== Mission Processing ====================
        void ProcessMissionQueue()
        {
            int dispatched = 0;
            while (MissionQueue.Count > 0 && dispatched < MaxMissionDispatchPerFrame)
            {
                MissionTask task = MissionQueue.Peek();
                if (task.DepartureTime <= Time.time)
                {
                    MissionQueue.Dequeue();
                    CreateMission(task);
                    dispatched++;
                }
                else
                {
                    break;
                }
            }
        }

        void CreateMission(MissionTask task)
        {
            Debug.Log($"[SimulationManager] Creating mission {task.TaskId}: {task.OriginVertiportId} -> {task.DestinationVertiportId}");

            if (!Vertiports.ContainsKey(task.OriginVertiportId) ||
                !Vertiports.ContainsKey(task.DestinationVertiportId))
            {
                Debug.LogError($"[SimulationManager] Mission {task.TaskId}: Invalid vertiport IDs.");
                return;
            }

            AircraftState state = pathPlanner.PlanMission(
                task.OriginVertiportId, task.DestinationVertiportId, task.DepartureTime,
                task.OriginHelipadId, task.DestinationHelipadId, task.TaskId,
                task.RouteLaneIds, task.Priority);

            if (state == null)
            {
                // Instead of immediate fail, queue for retry at helipad.
                // The mission will be retried periodically until maxHelipadWaitTime.
                float maxHelipadWait = config != null ? config.maxHelipadWaitTime : 120f;
                Debug.LogWarning($"[SimulationManager] Mission {task.TaskId}: Initial planning failed. " +
                                 $"Queuing for helipad retry (max={maxHelipadWait}s).");

                pendingMissions[task.TaskId] = new PendingMissionRetry
                {
                    Task = task,
                    FirstAttemptTime = Time.time,
                    LastRetryTime = 0f
                };
                return;
            }

            // Override the auto-generated AircraftId with the task's ID
            state.AircraftId = task.TaskId;
            state.Mission.ExecutingAircraftId = task.TaskId;

            AircraftStates[task.TaskId] = state;
            cumulTotalMissions++;
            SpawnAircraft(state);
        }

        void SpawnAircraft(AircraftState state)
        {
            Vector3 spawnPos = state.MissionKeyNodes?.OriginHelipad ?? Vector3.zero;
            if (!TryClaimVertiportWaypoint(state.AircraftId, spawnPos))
            {
                pendingAircraftSpawns[state.AircraftId] = state;
                Debug.Log($"[RuntimeGate] Delaying spawn of {state.AircraftId}: origin resource occupied.");
                return;
            }

            SpawnAircraftImmediately(state, spawnPos);
        }

        private void SpawnAircraftImmediately(AircraftState state, Vector3 spawnPos)
        {
            if (aircraftPrefab == null)
            {
                Debug.LogError("[SimulationManager] Aircraft prefab is not assigned!");
                state.State = "FAIL";
                return;
            }

            GameObject aircraftObj = Instantiate(aircraftPrefab, spawnPos, Quaternion.identity, aircraftsParent);
            aircraftObj.name = "Aircraft_" + state.AircraftId;
            AircraftObjects[state.AircraftId] = aircraftObj;
            AircraftTrailController trail = aircraftObj.GetComponent<AircraftTrailController>();
            if (trail == null) trail = aircraftObj.AddComponent<AircraftTrailController>();
            trail.Configure(showAircraftTrails, state.AircraftId, aircraftTrailBodyLengths);

            AircraftAgent agent = aircraftObj.GetComponent<AircraftAgent>();
            if (agent != null)
            {
                agent.Initialize(state);
                Debug.Log($"[SimulationManager] Aircraft {state.AircraftId} spawned and initialized.");
            }
            else
            {
                Debug.LogError($"[SimulationManager] AircraftAgent component missing on prefab!");
            }
        }

        // ==================== Lane State Update ====================
        /// <summary>Public accessor for delayed progress (used by FlightController).</summary>
        public float GetDelayedProgressPublic(string aircraftId)
        {
            return GetDelayedProgress(aircraftId);
        }

        /// <summary>Get delayed progress for an aircraft (reads from delay buffer).</summary>
        private float GetDelayedProgress(string aircraftId)
        {
            float delay = config != null ? config.signalPropagationDelay : 0f;
            if (delay <= 0f) return -1f;

            if (!_delayBuffer.TryGetValue(aircraftId, out var snapshots) || snapshots.Count == 0)
                return -1f;

            float targetTime = Time.time - delay;
            // Find snapshot closest to targetTime
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < snapshots.Count; i++)
            {
                float dist = Mathf.Abs(snapshots[i].time - targetTime);
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return snapshots[best].progress;
        }

        /// <summary>Store a new position snapshot for delayed reading. Throttled — records at delay/MAX_SAMPLES intervals so 64 samples span the full delay window.</summary>
        private void StoreProgressSnapshot(string aircraftId, float progress)
        {
            float delay = config != null ? config.signalPropagationDelay : 0f;
            if (delay <= 0f) return;

            // Throttle: only record every delay/MAX_SAMPLES seconds
            float interval = delay / MAX_DELAY_SAMPLES;
            float now = Time.time;
            if (_lastSnapshotTime.TryGetValue(aircraftId, out float lastTime) && now - lastTime < interval)
                return;
            _lastSnapshotTime[aircraftId] = now;

            if (!_delayBuffer.TryGetValue(aircraftId, out var snapshots))
            {
                snapshots = new List<(float, float)>();
                _delayBuffer[aircraftId] = snapshots;
            }
            snapshots.Add((now, progress));
            // Trim old entries
            while (snapshots.Count > MAX_DELAY_SAMPLES)
                snapshots.RemoveAt(0);
            // Remove entries older than 2x delay
            float cutoff = now - delay * 2f;
            while (snapshots.Count > 1 && snapshots[0].time < cutoff)
                snapshots.RemoveAt(0);
        }

        void UpdateLaneStates()
        {
            foreach (var lane in LaneStates.Values)
            {
                lane.AircraftsInLane.Clear();
                lane.CapabilityCheck = false;
            }

            float delay = config != null ? config.signalPropagationDelay : 0f;

            foreach (var kvp in AircraftObjects)
            {
                var agent = kvp.Value.GetComponent<AircraftAgent>();
                if (agent == null) continue;

                string laneId = agent.aircraftState.CurrentLaneId;
                if (string.IsNullOrEmpty(laneId) || !LaneStates.ContainsKey(laneId))
                    continue;
                if (!Lanes.ContainsKey(laneId)) continue;

                float curProgress = LaneGeometryUtility.ProjectProgress(
                    Lanes[laneId].Centerline,
                    agent.transform.position);

                // Always store real-time snapshot + broadcast real-time position
                StoreProgressSnapshot(kvp.Key, curProgress);
                LaneStates[laneId].AircraftsInLane[kvp.Key] = curProgress;

                // A lateral manoeuvre occupies both source and target lanes until
                // the aircraft reaches the target lane centerline.
                string laneChangeTarget = agent.activeLaneChangeTargetLaneId;
                if (!string.IsNullOrEmpty(laneChangeTarget) &&
                    laneChangeTarget != laneId &&
                    LaneStates.ContainsKey(laneChangeTarget) &&
                    Lanes.TryGetValue(laneChangeTarget, out Lane targetLane))
                {
                    float targetProgress = LaneGeometryUtility.ProjectProgress(
                        targetLane.Centerline,
                        agent.transform.position);
                    LaneStates[laneChangeTarget].AircraftsInLane[kvp.Key] = targetProgress;
                }
            }

            // Capability check: if lead aircraft is too close to lane start, block upstream lanes
            float safeInterval = config != null ? config.safeInterval : 10f;
            foreach (var lane in LaneStates.Values)
            {
                bool isFull = CalculateLaneCapability(lane, safeInterval);
                if (isFull && LaneToEdgeMap.ContainsKey(lane.LaneId))
                {
                    string edgeId = LaneToEdgeMap[lane.LaneId];
                    if (!Edges.ContainsKey(edgeId)) continue;

                    Edge edge = Edges[edgeId];
                    Vector3 laneStart = Lanes[lane.LaneId].Centerline.First();

                    // Determine which node is at the lane start
                    string nearNodeId = null;
                    float minDist = float.MaxValue;
                    foreach (var nodeId in edge.LinkNodesId)
                    {
                        float d = Vector3.Distance(Nodes[nodeId].Position, laneStart);
                        if (d < minDist) { minDist = d; nearNodeId = nodeId; }
                    }

                    if (nearNodeId != null && Nodes.ContainsKey(nearNodeId))
                    {
                        foreach (var connectedEdgeId in Nodes[nearNodeId].ConnectedEdges)
                        {
                            if (connectedEdgeId == edgeId || !Edges.ContainsKey(connectedEdgeId)) continue;
                            foreach (var checkLane in Edges[connectedEdgeId].Lanes)
                            {
                                if (checkLane.ToLanesId.Contains(lane.LaneId))
                                    LaneStates[checkLane.LaneId].CapabilityCheck = true;
                            }
                        }
                    }
                }
            }

            // Add virtual vehicle at stop line for blocked lanes
            foreach (var lane in LaneStates.Values)
            {
                if (lane.SignalCheck || lane.CapabilityCheck)
                {
                    float stopLineDist = Mathf.Max(
                        0f,
                        config != null ? config.signalStopLineDistance : 10f);
                    float virtualProgress = 1.0f;
                    if (lane.Length > stopLineDist)
                        virtualProgress = (lane.Length - stopLineDist) / lane.Length;
                    lane.AircraftsInLane["__VIRTUAL__"] = virtualProgress;
                }
            }
        }

        bool CalculateLaneCapability(LaneState lane, float safeInterval)
        {
            if (lane.AircraftsInLane == null || lane.AircraftsInLane.Count == 0)
                return false;

            float minProgress = float.MaxValue;
            foreach (float progress in lane.AircraftsInLane.Values)
            {
                if (progress < minProgress)
                    minProgress = progress;
            }

            return (minProgress * lane.Length) < safeInterval;
        }

        // ==================== Corridor State Update ====================
        public void UpdateCorridorStates()
        {
            float delay = config != null ? config.signalPropagationDelay : 0f;
            foreach (var corState in CorridorStatesDict.Values)
                corState.AircraftsInCorridor.Clear();

            foreach (var kvp in AircraftObjects)
            {
                var agent = kvp.Value.GetComponent<AircraftAgent>();
                if (agent == null) continue;

                string corrId = agent.currentCorridorId;
                if (string.IsNullOrEmpty(corrId) || !CorridorStatesDict.ContainsKey(corrId))
                    continue;
                if (!Corridors.ContainsKey(corrId)) continue;

                var cor = Corridors[corrId];
                float corrLength = Vector3.Distance(cor.LaneLinkPoint, cor.RampLinkPoint);
                if (corrLength < 0.001f) continue;

                Vector3 corrStart;
                if (cor.CorridorType == "Entry")
                    corrStart = cor.RampLinkPoint;
                else
                    corrStart = cor.LaneLinkPoint;

                float distFromStart = Vector3.Distance(agent.aircraftState.Position, corrStart);
                float curProgress = Mathf.Clamp01(distFromStart / corrLength);
                StoreProgressSnapshot(kvp.Key, curProgress);
                CorridorStatesDict[corrId].AircraftsInCorridor[kvp.Key] = curProgress;
            }
        }

        public void ApplyCorridorFollowing(string aircraftId, string corridorId, FlightController controller)
        {
            if (!CorridorStatesDict.TryGetValue(corridorId, out var corState)) return;

            float myProgress;
            if (!corState.AircraftsInCorridor.TryGetValue(aircraftId, out myProgress)) return;

            float nextProgress = float.MaxValue;
            bool hasPredecessor = false;
            if (!Corridors.TryGetValue(corridorId, out var cor)) return;
            float corrLength = Vector3.Distance(cor.LaneLinkPoint, cor.RampLinkPoint);
            float delay = config != null ? config.signalPropagationDelay : 0f;
            float posErrStdDev = config != null ? config.positioningErrorStdDev : 0f;
            foreach (var kvp in corState.AircraftsInCorridor)
            {
                if (kvp.Key == aircraftId) continue;
                float otherProgress = kvp.Value;
                if (delay > 0f)
                {
                    float delayed = GetDelayedProgressPublic(kvp.Key);
                    if (delayed >= 0f) otherProgress = delayed;
                }
                if (posErrStdDev > 0f)
                {
                    float u1 = Random.value; float u2 = Random.value;
                    float noise = posErrStdDev * Mathf.Sqrt(-2f * Mathf.Log(Mathf.Max(u1, 0.0001f))) * Mathf.Cos(2f * Mathf.PI * u2);
                    if (corrLength > 0f) otherProgress += noise / corrLength;
                }
                if (otherProgress > myProgress && otherProgress < nextProgress)
                {
                    nextProgress = otherProgress;
                    hasPredecessor = true;
                }
            }

            if (!hasPredecessor)
            {
                if (controller.moveSpeed < controller.maxSpeed)
                    controller.moveSpeed = Mathf.Min(controller.moveSpeed + controller.acceleration * Time.deltaTime, controller.maxSpeed);
                return;
            }

            float distanceToNext = (nextProgress - myProgress) * corrLength;
            float safeInterval = config != null ? config.safeInterval : 10f;

            if (distanceToNext < safeInterval)
            {
                float decel = controller.acceleration * safeInterval / distanceToNext;
                controller.moveSpeed = Mathf.Max(controller.moveSpeed - decel * Time.deltaTime, 0f);
            }
            else
            {
                if (controller.moveSpeed < controller.maxSpeed)
                    controller.moveSpeed = Mathf.Min(controller.moveSpeed + controller.acceleration * Time.deltaTime, controller.maxSpeed);
            }


            controller.moveSpeed = Mathf.Min(
                controller.moveSpeed,
                FollowingSafetyUtility.CalculateMaximumSpeed(
                    distanceToNext,
                    safeInterval,
                    Mathf.Max(Time.deltaTime, 0.0001f)));
        }

        public void ConfigureAircraftTrails(bool enabled)
        {
            showAircraftTrails = enabled;
            foreach (var pair in AircraftObjects)
            {
                if (pair.Value == null) continue;
                AircraftTrailController trail = pair.Value.GetComponent<AircraftTrailController>();
                if (trail == null) trail = pair.Value.AddComponent<AircraftTrailController>();
                trail.Configure(showAircraftTrails, pair.Key, aircraftTrailBodyLengths);
            }
        }

        // ==================== Metrics & Output ====================
        void CollectMetrics()
        {
            float totalLength = 0f, totalTime = 0f, totalDelay = 0f;
            int success = 0, failed = 0;

            foreach (var kvp in AircraftStates)
            {
                var state = kvp.Value;
                if (state.State == "FAIL")
                {
                    failed++;
                    continue;
                }
                success++;
                totalLength += state.FlightLength;
                totalTime += state.FlightTime;
                if (state.RampTrajectory != null)
                {
                    foreach (var rp in state.RampTrajectory)
                        totalDelay += rp.WaitTime;
                }
            }

            Metrics.SystemCapacity = success;
            Metrics.AverageFlightLength = success > 0 ? totalLength / success : 0f;
            Metrics.AverageFlightTime = success > 0 ? totalTime / success : 0f;
            Metrics.TakeoffDelayTime = totalDelay;
            Metrics.SuccessfulFlight = success;
            Metrics.FailedFlight = failed;
            Metrics.FailureRate = (success + failed) > 0
                ? System.Math.Round((double)failed / (success + failed), 3)
                : 0.0;
            Metrics.ScheduleDelay = Time.time;

            Debug.Log($"[SimulationManager] Metrics: Capacity={Metrics.SystemCapacity}, " +
                      $"Success={Metrics.SuccessfulFlight}, Failed={Metrics.FailedFlight}, " +
                      $"Rate={Metrics.FailureRate}, AvgLen={Metrics.AverageFlightLength:F1}, " +
                      $"AvgTime={Metrics.AverageFlightTime:F1}");
        }

        void SaveResultsToFile()
        {
            if (!Directory.Exists(dataBasePath))
                Directory.CreateDirectory(dataBasePath);

            string json = JsonConvert.SerializeObject(Metrics, Formatting.Indented);
            File.WriteAllText(resultsFilePath, json);
            Debug.Log($"[SimulationManager] Results saved to: {resultsFilePath}");
        }

        void SaveOccupancyLog()
        {
            var export = new OccupancyExport();
            foreach (var kv in _occupancyLog)
                export.Resources.Add(kv.Value);

            string outDir = GetOutputDirectory();
            string filePath = Path.Combine(outDir, "OccupancyLog.json");
            string json = JsonConvert.SerializeObject(export, Formatting.Indented);
            File.WriteAllText(filePath, json);
            Debug.Log($"[SimulationManager] Occupancy log saved ({export.Resources.Count} resources) to: {filePath}");
        }

        void ClearAllAircrafts()
        {
            if (aircraftsParent != null)
            {
                foreach (Transform child in aircraftsParent)
                {
                    if (Application.isPlaying)
                        Destroy(child.gameObject);
                    else
                        DestroyImmediate(child.gameObject);
                }
            }

            AircraftObjects.Clear();
            AircraftStates.Clear();
            MissionQueue.Clear();
            pendingAircraftSpawns.Clear();
            runtimeWaypointClaims.Clear();
        }

        // ==================== Load Missions from JSON / XML ====================
        public RouteImportResult LoadRouteFile(string filePath = null, bool replaceQueue = false)
        {
            EnsurePathsInitialized();
            string path = SarPortablePath.Resolve(filePath ?? ResolveDefaultMissionPath());

            var laneIds = new HashSet<string>(Lanes.Keys, System.StringComparer.Ordinal);
            if (networkManager != null && networkManager.FullNetwork != null)
            {
                foreach (Edge edge in networkManager.FullNetwork.Edges ?? new List<Edge>())
                    foreach (Lane lane in edge.Lanes ?? new List<Lane>())
                        laneIds.Add(lane.LaneId);
                foreach (LaneConnectionCorridor corridor in
                         networkManager.FullNetwork.LaneConnectionCorridors ??
                         new List<LaneConnectionCorridor>())
                    laneIds.Add(corridor.CorridorId);
            }

            IEnumerable<Vertiport> sourceVertiports = Vertiports.Values;
            if (!sourceVertiports.Any() && vertiportManager != null)
                sourceVertiports = vertiportManager.Vertiports ?? new List<Vertiport>();
            List<Vertiport> vertiports = sourceVertiports.ToList();
            var vertiportIds = new HashSet<string>(
                vertiports.Select(vertiport => vertiport.VertiportId),
                System.StringComparer.Ordinal);
            var helipadIds = new HashSet<string>(
                vertiports.SelectMany(vertiport => vertiport.Helipads ?? new List<Helipad>())
                    .Select(helipad => helipad.HelipadId),
                System.StringComparer.Ordinal);

            RouteImportResult result = RouteFileImporter.Load(
                path,
                laneIds,
                vertiportIds,
                helipadIds);

            foreach (string warning in result.Warnings)
                Debug.LogWarning($"[RouteImport] {warning}");
            foreach (string error in result.Errors)
                Debug.LogError($"[RouteImport] {error}");

            if (replaceQueue && (result.Success || result.Missions.Count > 0))
                MissionQueue.Clear();
            foreach (MissionTask mission in result.Missions)
                MissionQueue.Enqueue(mission);

            Debug.Log($"[SimulationManager] Loaded {result.Missions.Count} valid mission(s) " +
                      $"from '{path}' with {result.Errors.Count} error(s) and " +
                      $"{result.Warnings.Count} warning(s).");
            return result;
        }

        public void LoadMissionsFromJSON(string filePath = null)
        {
            LoadRouteFile(filePath);
        }

        public void LoadMissionsFromXML(string filePath)
        {
            LoadRouteFile(filePath);
        }

        // ==================== RandomContinuous: HP-Exclusive Mission Chaining ====================

        /// <summary>
        /// Initialize RandomContinuous mode: spawn N aircraft at unique HPs across all VPs.
        /// Each aircraft gets a source and destination HP in different VPs.
        /// </summary>
        private void InitRandomContinuous()
        {
            if (Vertiports.Count < 2)
            {
                Debug.LogError("[SimulationManager] Need at least 2 vertiports for RandomContinuous mode.");
                return;
            }

            var allHPs = new List<HelipadEndpoint>();
            foreach (var vp in Vertiports.Values)
                foreach (var hp in vp.Helipads)
                    allHPs.Add(new HelipadEndpoint(vp.VertiportId, hp.HelipadId));

            int safeCapacity = RandomDemandAllocator.CalculateSafeFleetCapacity(allHPs);
            if (safeCapacity <= 0)
            {
                Debug.LogError("[SimulationManager] RandomContinuous has no safe cross-vertiport fleet capacity.");
                return;
            }
            int requested = config != null ? config.maxConcurrentAircraft : 10;
            int maxAircraft = Mathf.Clamp(requested, 1, safeCapacity);
            if (config != null && config.maxConcurrentAircraft != maxAircraft)
            {
                Debug.LogWarning($"[SimulationManager] Random fleet size clamped from {config.maxConcurrentAircraft} " +
                                 $"to safe capacity {maxAircraft} (helipads={allHPs.Count}).");
                config.maxConcurrentAircraft = maxAircraft;
            }

            System.Random rng = new System.Random();
            List<RandomMissionEndpoints> allocations =
                RandomDemandAllocator.Allocate(allHPs, maxAircraft, rng);
            randomFleetTargetCount = allocations.Count;

            for (int i = 0; i < allocations.Count; i++)
            {
                string originHP = allocations[i].Origin.HelipadId;
                string originVP = allocations[i].Origin.VertiportId;
                string destHP = allocations[i].Destination.HelipadId;
                string destVP = allocations[i].Destination.VertiportId;

                // Occupy both HPs
                OccupiedHelipads.Add(originHP);
                OccupiedHelipads.Add(destHP);

                string taskId = $"M{++randomMissionCounter:D04}";
                AircraftOriginHP[taskId] = originHP;
                AircraftDestHP[taskId] = destHP;

                MissionQueue.Enqueue(new MissionTask
                {
                    TaskId = taskId,
                    OriginVertiportId = originVP,
                    DestinationVertiportId = destVP,
                    OriginHelipadId = originHP,
                    DestinationHelipadId = destHP,
                    DepartureTime = Time.time + i * 0.5f,
                    Priority = 1
                });
            }

            Debug.Log($"[SimulationManager] RandomContinuous initialized: requested={maxAircraft} allocated={allocations.Count} " +
                      $"hpTotal={allHPs.Count} hpOccupied={OccupiedHelipads.Count} " +
                      $"pendingRetry={pendingMissions.Count} Time.time={Time.time:F0}");
        }

        /// <summary>
        /// Find an available HP in a VP different from the given originVP.
        /// Returns null if no HP is available.
        /// </summary>
        private string TryFindDestHP(string originVP)
        {
            var candidates = new List<string>();
            foreach (var vp in Vertiports.Values)
            {
                if (vp.VertiportId == originVP) continue;
                foreach (var hp in vp.Helipads)
                {
                    if (!OccupiedHelipads.Contains(hp.HelipadId))
                        candidates.Add(hp.HelipadId);
                }
            }
            if (candidates.Count == 0) return null;
            return candidates[Random.Range(0, candidates.Count)];
        }

        /// <summary>
        /// Called each frame in RandomContinuous mode: check for aircraft that have
        /// reached END phase and chain their next mission.
        /// </summary>
        private void ProcessCompletedMissions()
        {
            var completed = new List<AircraftAgent>();
            float turnaroundTime = config != null
                ? Mathf.Max(0f, config.randomTurnaroundTime)
                : 10f;

            foreach (var kvp in AircraftObjects)
            {
                if (kvp.Value == null) continue;
                var agent = kvp.Value.GetComponent<AircraftAgent>();
                if (agent?.aircraftState == null) continue;
                if (agent.aircraftState.MissionPhase == "END" && agent.hasMissionCompleted)
                {
                    string aircraftId = agent.aircraftState.AircraftId;
                    if (!completedMissionAt.TryGetValue(aircraftId, out float completedAt))
                    {
                        completedMissionAt[aircraftId] = Time.time;
                        completedAt = Time.time;
                        Debug.Log($"[SimulationManager] {aircraftId} parked for " +
                                  $"{turnaroundTime:F1}s before the next random mission.");
                    }

                    if (Time.time - completedAt >= turnaroundTime)
                        completed.Add(agent);
                }
            }

            foreach (var agent in completed)
            {
                agent.hasMissionCompleted = false;
                string acId = agent.aircraftState.AircraftId;
                completedMissionAt.Remove(acId);

                // Free old origin HP
                if (AircraftOriginHP.TryGetValue(acId, out string oldOriginHP))
                {
                    OccupiedHelipads.Remove(oldOriginHP);
                    AircraftOriginHP.Remove(acId);
                }

                // Current destination HP becomes new origin
                string newOriginHP = AircraftDestHP.TryGetValue(acId, out string destHP)
                    ? destHP : agent.aircraftState.DestinationHelipadId;
                string newOriginVP = HelipadToVertiportMap.TryGetValue(newOriginHP, out string vp)
                    ? vp : agent.aircraftState.Mission.DestinationVertiport;

                // Try to find destination HP in another VP
                string newDestHP = TryFindDestHP(newOriginVP);
                if (newDestHP != null)
                {
                    string newDestVP = HelipadToVertiportMap[newDestHP];
                    OccupiedHelipads.Add(newDestHP);
                    AircraftOriginHP[acId] = newOriginHP;
                    AircraftDestHP[acId] = newDestHP;

                    Debug.Log($"[SimulationManager] Chaining {acId}: {newOriginHP}({newOriginVP}) -> {newDestHP}({newDestVP})");

                    var newState = pathPlanner.PlanMission(
                        newOriginVP, newDestVP, Time.time,
                        newOriginHP, newDestHP, acId);

                    if (newState != null)
                    {
                        // Replace state on existing agent
                        agent.aircraftState = newState;
                        agent.ResetForNewMission();
                        AircraftStates[acId] = newState;
                        Debug.Log($"[SimulationManager] {acId} restarted: {newOriginHP} -> {newDestHP}");
                    }
                    else
                    {
                        // Queue for helipad retry instead of immediate fail
                        float maxHelipadWait = config != null ? config.maxHelipadWaitTime : 120f;
                        Debug.LogWarning($"[SimulationManager] Chaining failed for {acId}. Holding at helipad (max={maxHelipadWait}s).");
                        agent.aircraftState.MissionPhase = "WAITING";
                        agent.aircraftState.State = "ACTIVE";
                        agent.aircraftState.LandingRetryStartTime = Time.time;
                        agent.aircraftState.LastLandingRetryTime = 0f;
                        helipadWaitingAgents[acId] = agent;
                    }
                }
                else
                {
                    // No destination available — enter waiting
                    Debug.Log($"[SimulationManager] {acId} no destination available, entering WAITING at {newOriginHP}.");
                    agent.aircraftState.MissionPhase = "WAITING";
                    agent.aircraftState.State = "ACTIVE";
                    agent.aircraftState.LandingRetryStartTime = Time.time;
                    agent.aircraftState.LastLandingRetryTime = 0f;
                    AircraftOriginHP[acId] = newOriginHP;
                    waitingAircraft.Add(agent);
                }
            }
        }

        /// <summary>
        /// Check waiting aircraft each frame and try to assign them a destination.
        /// </summary>
        private void ProcessWaitingAircraft()
        {
            if (waitingAircraft.Count == 0) return;

            float destinationWaitLimit = config != null ? config.maxHelipadWaitTime : 120f;
            var unblocked = new List<AircraftAgent>();
            var timedOut = new List<AircraftAgent>();
            foreach (var agent in waitingAircraft)
            {
                string acId = agent.aircraftState.AircraftId;
                float waitStarted = agent.aircraftState.LandingRetryStartTime;
                if (waitStarted >= 0f && Time.time - waitStarted > destinationWaitLimit)
                {
                    timedOut.Add(agent);
                    continue;
                }

                string originVP = agent.aircraftState.Mission?.DestinationVertiport
                    ?? HelipadToVertiportMap.GetValueOrDefault(
                        AircraftOriginHP.GetValueOrDefault(acId, ""), "");

                string newDestHP = TryFindDestHP(originVP);
                if (newDestHP != null)
                {
                    string newDestVP = HelipadToVertiportMap[newDestHP];
                    string originHP = AircraftOriginHP.GetValueOrDefault(acId,
                        agent.aircraftState.DestinationHelipadId);

                    OccupiedHelipads.Add(newDestHP);
                    AircraftDestHP[acId] = newDestHP;

                    Debug.Log($"[SimulationManager] Unblocking {acId}: {originHP} -> {newDestHP}");

                    var newState = pathPlanner.PlanMission(
                        originVP, newDestVP, Time.time,
                        originHP, newDestHP, acId);

                    if (newState != null)
                    {
                        agent.aircraftState = newState;
                        agent.ResetForNewMission();
                        AircraftStates[acId] = newState;
                    }
                    else
                    {
                        // Hold at helipad for retry
                        float maxHelipadWait = config != null ? config.maxHelipadWaitTime : 120f;
                        Debug.LogWarning($"[SimulationManager] Unblock planning failed for {acId}. Holding at helipad (max={maxHelipadWait}s).");
                        agent.aircraftState.LandingRetryStartTime = Time.time;
                        agent.aircraftState.LastLandingRetryTime = 0f;
                        helipadWaitingAgents[acId] = agent;
                    }
                    unblocked.Add(agent);
                }
            }

            foreach (var agent in unblocked)
                waitingAircraft.Remove(agent);

            foreach (var agent in timedOut)
            {
                waitingAircraft.Remove(agent);
                string acId = agent.aircraftState.AircraftId;
                Debug.LogError($"[SimulationManager] {acId}: Destination wait timeout. Deregistering.");
                cumulFailCount++;
                if (AircraftOriginHP.TryGetValue(acId, out string originHP))
                {
                    ReleaseHelipadStandReservation(acId, originHP, Time.time);
                    OccupiedHelipads.Remove(originHP);
                    AircraftOriginHP.Remove(acId);
                }
                if (AircraftDestHP.TryGetValue(acId, out string destinationHP))
                {
                    OccupiedHelipads.Remove(destinationHP);
                    AircraftDestHP.Remove(acId);
                }
                agent.aircraftState.State = "FAIL";
                agent.aircraftState.MissionPhase = "END";
                SpawnReplacementAircraft(acId);
            }
        }

        public int GetRandomFleetCapacity()
        {
            var endpoints = new List<HelipadEndpoint>();
            foreach (Vertiport vertiport in Vertiports.Values)
                foreach (Helipad helipad in vertiport.Helipads ?? new List<Helipad>())
                    endpoints.Add(new HelipadEndpoint(vertiport.VertiportId, helipad.HelipadId));
            return RandomDemandAllocator.CalculateSafeFleetCapacity(endpoints);
        }

        /// <summary>
        /// Returns operator-facing aircraft counts. In random mode every configured
        /// fleet slot is either executing a mission or waiting at a stand/queue/retry.
        /// </summary>
        public void GetOperationalAircraftCounts(out int active, out int pending, out int total)
        {
            active = AircraftObjects.Values.Count(value =>
            {
                if (value == null) return false;
                AircraftAgent agent = value.GetComponent<AircraftAgent>();
                string phase = agent?.aircraftState?.MissionPhase;
                return !string.IsNullOrEmpty(phase) && phase != "WAITING" && phase != "END";
            });

            if (config != null && config.missionMode == MissionGenerationMode.RandomContinuous)
            {
                int capacity = GetRandomFleetCapacity();
                int configured = capacity > 0
                    ? Mathf.Clamp(config.maxConcurrentAircraft, 1, capacity)
                    : 0;
                total = randomFleetTargetCount > 0 ? randomFleetTargetCount : configured;
                active = Mathf.Clamp(active, 0, total);
                pending = Mathf.Max(0, total - active);
                return;
            }

            pending = MissionQueue.Count + pendingMissions.Count + pendingAircraftSpawns.Count;
            total = active + pending;
        }

        private void ProcessPendingAircraftSpawns()
        {
            if (pendingAircraftSpawns.Count == 0) return;
            foreach (var pair in pendingAircraftSpawns.Take(MaxPendingSpawnChecksPerFrame).ToList())
            {
                AircraftState state = pair.Value;
                Vector3 spawnPos = state.MissionKeyNodes?.OriginHelipad ?? Vector3.zero;
                if (!TryClaimVertiportWaypoint(pair.Key, spawnPos)) continue;
                pendingAircraftSpawns.Remove(pair.Key);
                SpawnAircraftImmediately(state, spawnPos);
                Debug.Log($"[RuntimeGate] Spawn released for {pair.Key} at t={Time.time:F2}.");
            }
        }

        /// <summary>
        /// Process missions that failed initial path planning and are retrying at helipad.
        /// Retry every 2 seconds until maxHelipadWaitTime expires.
        /// </summary>
        private void ProcessPendingMissions()
        {
            if (pendingMissions.Count == 0) return;

            float maxHelipadWait = config != null ? config.maxHelipadWaitTime : 120f;
            float retryInterval = 2f;
            var succeeded = new List<string>();
            var timedOut = new List<string>();

            int retries = 0;
            foreach (var kv in pendingMissions.ToList())
            {
                var pr = kv.Value;
                float elapsed = Time.time - pr.FirstAttemptTime;

                if (elapsed > maxHelipadWait)
                {
                    timedOut.Add(kv.Key);
                    continue;
                }

                if (Time.time - pr.LastRetryTime >= retryInterval || pr.LastRetryTime <= 0f)
                {
                    if (retries >= MaxPathRetryAttemptsPerFrame) continue;
                    retries++;
                    pr.LastRetryTime = Time.time;
                    Debug.Log($"[SimulationManager] Retrying mission {kv.Key} (elapsed={elapsed:F1}s/{maxHelipadWait}s)...");

                    var state = pathPlanner.PlanMission(
                        pr.Task.OriginVertiportId, pr.Task.DestinationVertiportId, Time.time,
                        pr.Task.OriginHelipadId, pr.Task.DestinationHelipadId, pr.Task.TaskId,
                        pr.Task.RouteLaneIds, pr.Task.Priority);

                    if (state != null)
                    {
                        state.AircraftId = pr.Task.TaskId;
                        state.Mission.ExecutingAircraftId = pr.Task.TaskId;
                        AircraftStates[pr.Task.TaskId] = state;
                        SpawnAircraft(state);
                        succeeded.Add(kv.Key);
                        Debug.Log($"[SimulationManager] Mission {kv.Key} planned after {elapsed:F1}s helipad wait.");
                    }
                }
            }

            foreach (var id in succeeded) pendingMissions.Remove(id);

            foreach (var id in timedOut)
            {
                var pr = pendingMissions[id];
                Debug.LogError($"[SimulationManager] Mission {id}: Helipad wait timeout ({maxHelipadWait}s). Deregistering.");
                cumulFailCount++;
                if (pr.Task.OriginHelipadId != null) OccupiedHelipads.Remove(pr.Task.OriginHelipadId);
                if (pr.Task.DestinationHelipadId != null) OccupiedHelipads.Remove(pr.Task.DestinationHelipadId);
                AircraftStates[id] = new AircraftState { AircraftId = id, State = "FAIL", MissionPhase = "END" };
                pendingMissions.Remove(id);
                // Replace with new mission to maintain fleet size
                SpawnReplacementAircraft(id);
            }
        }

        /// <summary>
        /// Process chained aircraft that failed path planning and are holding at helipad.
        /// </summary>
        private void ProcessHelipadWaitingAgents()
        {
            if (helipadWaitingAgents.Count == 0) return;

            float maxHelipadWait = config != null ? config.maxHelipadWaitTime : 120f;
            float retryInterval = 2f;
            var succeeded = new List<string>();
            var timedOut = new List<string>();

            int retries = 0;
            foreach (var kv in helipadWaitingAgents.ToList())
            {
                var agent = kv.Value;
                var st = agent.aircraftState;
                if (st == null) { timedOut.Add(kv.Key); continue; }

                float elapsed = Time.time - st.LandingRetryStartTime;

                if (elapsed > maxHelipadWait)
                {
                    timedOut.Add(kv.Key);
                    continue;
                }

                if (Time.time - st.LastLandingRetryTime >= retryInterval || st.LastLandingRetryTime <= 0f)
                {
                    if (retries >= MaxPathRetryAttemptsPerFrame) continue;
                    retries++;
                    st.LastLandingRetryTime = Time.time;
                    string originVP = HelipadToVertiportMap.TryGetValue(AircraftOriginHP[kv.Key], out string ovp)
                        ? ovp : st.Mission?.DestinationVertiport;
                    string destVP = HelipadToVertiportMap.TryGetValue(AircraftDestHP[kv.Key], out string dvp)
                        ? dvp : "";

                    Debug.Log($"[SimulationManager] Retrying chaining for {kv.Key} (elapsed={elapsed:F1}s)...");

                    var newState = pathPlanner.PlanMission(
                        originVP, destVP, Time.time,
                        AircraftOriginHP.GetValueOrDefault(kv.Key, ""),
                        AircraftDestHP.GetValueOrDefault(kv.Key, ""),
                        kv.Key);

                    if (newState != null)
                    {
                        agent.aircraftState = newState;
                        agent.ResetForNewMission();
                        AircraftStates[kv.Key] = newState;
                        succeeded.Add(kv.Key);
                        Debug.Log($"[SimulationManager] {kv.Key} chaining succeeded after {elapsed:F1}s helipad wait.");
                    }
                }
            }

            foreach (var id in succeeded) helipadWaitingAgents.Remove(id);

            foreach (var id in timedOut)
            {
                var agent = helipadWaitingAgents[id];
                Debug.LogError($"[SimulationManager] {id}: Helipad chaining wait timeout. Deregistering.");
                cumulFailCount++;
                if (AircraftOriginHP.TryGetValue(id, out string ohp))
                {
                    ReleaseHelipadStandReservation(id, ohp, Time.time);
                    OccupiedHelipads.Remove(ohp);
                }
                if (AircraftDestHP.TryGetValue(id, out string dhp)) OccupiedHelipads.Remove(dhp);
                agent.aircraftState.State = "FAIL";
                agent.aircraftState.MissionPhase = "END";
                helipadWaitingAgents.Remove(id);
                // Replace with new mission to maintain fleet size
                SpawnReplacementAircraft(id);
            }
        }

        /// <summary>Create a replacement mission at a free helipad to maintain fleet size.</summary>
        private void SpawnReplacementAircraft(string oldTaskId)
        {
            // Find a free origin HP
            var allHPs = new System.Collections.Generic.List<(string vpId, string hpId)>();
            foreach (var vp in Vertiports.Values)
                foreach (var hp in vp.Helipads)
                    if (!OccupiedHelipads.Contains(hp.HelipadId))
                        allHPs.Add((vp.VertiportId, hp.HelipadId));
            if (allHPs.Count < 2) return;

            var rng = new System.Random();
            var origin = allHPs[rng.Next(allHPs.Count)];
            var destCandidates = allHPs.Where(h => h.vpId != origin.vpId).ToList();
            if (destCandidates.Count == 0) return;
            var dest = destCandidates[rng.Next(destCandidates.Count)];

            OccupiedHelipads.Add(origin.hpId);
            OccupiedHelipads.Add(dest.hpId);
            string newId = $"R{++randomMissionCounter:D04}";
            AircraftOriginHP[newId] = origin.hpId;
            AircraftDestHP[newId] = dest.hpId;

            var task = new MissionTask
            {
                TaskId = newId,
                OriginVertiportId = origin.vpId,
                DestinationVertiportId = dest.vpId,
                OriginHelipadId = origin.hpId,
                DestinationHelipadId = dest.hpId,
                DepartureTime = Time.time,
                Priority = 1
            };
            MissionQueue.Enqueue(task);
            Debug.Log($"[SimulationManager] Spawned replacement mission {newId}: {origin.vpId}({origin.hpId}) -> {dest.vpId}({dest.hpId})");
        }

        // ==================== Phase 6: Resource Scheduling ====================
        #region Resource Scheduling

        /// <summary>
        /// Atomically validates and commits every resource interval for one path.
        /// No state is changed when any requested resource is unknown, invalid, or
        /// conflicts with another aircraft's existing reservation.
        /// </summary>
        public bool TryBookVertiportResources(
            string aircraftId,
            IReadOnlyList<ResourceReservationRequest> requests,
            out string failureReason)
        {
            failureReason = null;
            if (string.IsNullOrWhiteSpace(aircraftId))
            {
                failureReason = "Aircraft ID is required for resource booking.";
                return false;
            }

            if (requests == null || requests.Count == 0)
                return true;

            for (int i = 0; i < requests.Count; i++)
            {
                ResourceReservationRequest request = requests[i];
                if (string.IsNullOrWhiteSpace(request.ResourceId))
                {
                    failureReason = $"Booking request {i} has no resource ID.";
                    return false;
                }

                if (!IsFiniteReservationTime(request.StartTime) ||
                    !IsFiniteReservationTime(request.EndTime) ||
                    request.EndTime <= request.StartTime)
                {
                    failureReason = $"Invalid interval for {request.ResourceId}: " +
                                    $"[{request.StartTime:F3},{request.EndTime:F3}).";
                    return false;
                }

                if (!TryGetOccupiedWindows(request.ResourceId, out var occupiedWindows))
                {
                    failureReason = $"Unknown reservable resource: {request.ResourceId}.";
                    return false;
                }

                var reservationWindows = new List<ReservationWindow>(occupiedWindows.Count);
                foreach (var occupied in occupiedWindows)
                {
                    reservationWindows.Add(new ReservationWindow(
                        occupied.OccupiedAircraftId,
                        occupied.StartTime,
                        occupied.EndTime));
                }

                if (ReservationWindowSolver.HasConflict(
                        reservationWindows,
                        request.StartTime,
                        request.EndTime,
                        ignoredAircraftId: aircraftId))
                {
                    failureReason = $"Final booking conflict on {request.ResourceId} for {aircraftId}: " +
                                    $"[{request.StartTime:F3},{request.EndTime:F3}).";
                    return false;
                }
            }

            // Unity simulation state is single-threaded. Once all requests pass the
            // read-only phase, committing them in this loop is an atomic batch.
            for (int i = 0; i < requests.Count; i++)
            {
                ResourceReservationRequest request = requests[i];
                CommitVertiportResource(
                    request.ResourceId,
                    aircraftId,
                    request.StartTime,
                    request.EndTime,
                    request.IsStandingReservation);
            }

            Debug.Log($"[Reservation] Atomically booked {requests.Count} intervals for {aircraftId}.");
            return true;
        }

        private static bool IsFiniteReservationTime(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void CommitVertiportResource(
            string resourceId,
            string aircraftId,
            float startTime,
            float endTime,
            bool isStandingReservation)
        {
            var window = new OccupiedTimeWindow
            {
                OccupiedAircraftId = aircraftId,
                StartTime = startTime,
                EndTime = endTime,
                IsStandingReservation = isStandingReservation
            };

            if (HelipadStatesDict.TryGetValue(resourceId, out var hp))
                hp.OccupiedTimeWindows.Add(window);
            if (VertexStatesDict.TryGetValue(resourceId, out var v))
                v.OccupiedTimeWindows.Add(window);
            if (RampStatesDict.TryGetValue(resourceId, out var r))
                r.OccupiedTimeWindows.Add(window);
            if (CorridorStatesDict.TryGetValue(resourceId, out var c))
                c.OccupiedTimeWindows.Add(window);
            if (ApproachNodeStates.TryGetValue(resourceId, out var an))
                an.OccupiedTimeWindows.Add(window);
            if (ApproachEdgeStates.TryGetValue(resourceId, out var ae))
                ae.OccupiedTimeWindows.Add(window);
            // Propagate to all co-located resources across types (vertex ↔ approach anchor, etc.)
            BookToSamePositionResources(resourceId, window);

            // Record in global occupancy log for later export
            RecordOccupancyEntry(resourceId, aircraftId, startTime, endTime);
        }

        /// <summary>
        /// Truncates the provisional post-landing stand hold once the same aircraft
        /// has a committed takeoff slot. The window object is shared with every
        /// co-located resource, so the linked approach node is released atomically.
        /// </summary>
        public int ReleaseHelipadStandReservation(
            string aircraftId,
            string helipadId,
            float clearTime)
        {
            if (!TryGetOccupiedWindows(helipadId, out var windows)) return 0;
            return HelipadStandReservation.Release(windows, aircraftId, clearTime);
        }

        /// <summary>
        /// Record a booking into the global occupancy log (exported at simulation end).
        /// Groups entries by resource ID, then by aircraft ID, merging adjacent/overlapping
        /// windows from the same aircraft to keep the log compact.
        /// </summary>
        private void RecordOccupancyEntry(string resourceId, string aircraftId, float startTime, float endTime)
        {
            if (!_occupancyLog.TryGetValue(resourceId, out var resourceLog))
            {
                resourceLog = new ResourceOccupancyLog { ResourceId = resourceId };
                _occupancyLog[resourceId] = resourceLog;
            }

            // Find existing entry for this aircraft, or create one
            AircraftOccupancy acOcc = resourceLog.Occupancies.Find(o => o.AircraftId == aircraftId);
            if (acOcc == null)
            {
                acOcc = new AircraftOccupancy { AircraftId = aircraftId };
                resourceLog.Occupancies.Add(acOcc);
            }

            // Check if this new window overlaps or touches an existing one — merge if so
            bool merged = false;
            foreach (var tw in acOcc.TimeWindows)
            {
                // If windows overlap or touch (within 0.5s), extend the existing window
                if (startTime <= tw.EndTime + 0.5f && endTime >= tw.StartTime - 0.5f)
                {
                    tw.StartTime = Mathf.Min(tw.StartTime, startTime);
                    tw.EndTime = Mathf.Max(tw.EndTime, endTime);
                    merged = true;
                    break;
                }
            }
            if (!merged)
            {
                acOcc.TimeWindows.Add(new TimeWindowEntry { StartTime = startTime, EndTime = endTime });
            }
        }

        private Dictionary<string, List<string>> _posToResourceIds = new Dictionary<string, List<string>>();

        // Global occupancy log for export — records every booking per resource grouped by aircraft.
        private Dictionary<string, ResourceOccupancyLog> _occupancyLog = new Dictionary<string, ResourceOccupancyLog>();

        private string PosKey(Vector3 p) => $"{p.x:F1}_{p.y:F1}_{p.z:F1}";

        private void BuildPositionMap()
        {
            _posToResourceIds.Clear();
            void Add(string id, Vector3 pos)
            {
                string k = PosKey(pos);
                if (!_posToResourceIds.TryGetValue(k, out var l)) { l = new List<string>(); _posToResourceIds[k] = l; }
                l.Add(id);
            }
            foreach (var an in ApproachNodes.Values) Add(an.NodeId, an.Position);
            foreach (var v in Vertices.Values) Add(v.VertexId, v.Position);
            foreach (var hp in Helipads.Values) Add(hp.HelipadId, GetHelipadCenter(hp));
        }

        private static Vector3 GetHelipadCenter(Helipad helipad)
        {
            if (helipad?.Boundary == null || helipad.Boundary.Count == 0)
                return Vector3.zero;

            Vector3 sum = Vector3.zero;
            foreach (Vector3 point in helipad.Boundary)
                sum += point;
            return sum / helipad.Boundary.Count;
        }

        private void BookToSamePositionResources(string resourceId, OccupiedTimeWindow window)
        {
            Vector3? pos = null;
            if (ApproachNodes.TryGetValue(resourceId, out var an)) pos = an.Position;
            else if (Vertices.TryGetValue(resourceId, out var v)) pos = v.Position;
            else if (Helipads.TryGetValue(resourceId, out var hp)) pos = GetHelipadCenter(hp);
            if (pos == null) return;

            string k = PosKey(pos.Value);
            if (!_posToResourceIds.TryGetValue(k, out var siblings)) return;

            foreach (var sid in siblings)
            {
                if (sid == resourceId) continue;
                if (ApproachNodeStates.TryGetValue(sid, out var sib)) sib.OccupiedTimeWindows.Add(window);
                if (VertexStatesDict.TryGetValue(sid, out var sv)) sv.OccupiedTimeWindows.Add(window);
                if (HelipadStatesDict.TryGetValue(sid, out var sh)) sh.OccupiedTimeWindows.Add(window);
            }
        }

        /// <summary>
        /// Direct lookup of OccupiedTimeWindows for a resource across all state dictionaries.
        /// Returns true and the windows list if found, false otherwise.
        /// Used by PathPlanner to check time-window conflicts without reflection.
        /// </summary>
        public bool TryGetOccupiedWindows(string resourceId, out List<OccupiedTimeWindow> windows)
        {
            if (HelipadStatesDict.TryGetValue(resourceId, out var hp))
            { windows = hp.OccupiedTimeWindows; return true; }
            if (VertexStatesDict.TryGetValue(resourceId, out var v))
            { windows = v.OccupiedTimeWindows; return true; }
            if (RampStatesDict.TryGetValue(resourceId, out var r))
            { windows = r.OccupiedTimeWindows; return true; }
            if (CorridorStatesDict.TryGetValue(resourceId, out var c))
            { windows = c.OccupiedTimeWindows; return true; }
            if (ApproachNodeStates.TryGetValue(resourceId, out var an))
            { windows = an.OccupiedTimeWindows; return true; }
            if (ApproachEdgeStates.TryGetValue(resourceId, out var ae))
            { windows = ae.OccupiedTimeWindows; return true; }
            if (HSStates.TryGetValue(resourceId, out var hs))
            { windows = hs.OccupiedTimeWindows; return true; }
            windows = null;
            return false;
        }

        /// <summary>
        /// Execution-time gate for vertiport waypoints. Static reservations build a
        /// conflict-free plan; this rolling claim keeps a delayed aircraft's resource
        /// occupied until it has physically cleared the conflict area.
        /// </summary>
        public bool TryClaimVertiportWaypoint(string aircraftId, Vector3 target)
        {
            if (string.IsNullOrEmpty(aircraftId)) return false;
            float clearance = Mathf.Max(2.75f,
                config != null ? config.safeInterval * 0.35f : 3.5f);
            PruneRuntimeWaypointClaims(clearance);

            foreach (RuntimeWaypointClaim claim in runtimeWaypointClaims.Values)
            {
                if (claim.AircraftId != aircraftId &&
                    Vector3.Distance(claim.Target, target) < clearance)
                    return false;
            }

            foreach (var pair in AircraftObjects)
            {
                if (pair.Key == aircraftId || pair.Value == null) continue;
                AircraftAgent other = pair.Value.GetComponent<AircraftAgent>();
                if (other?.aircraftState == null ||
                    other.aircraftState.MissionPhase == "END" ||
                    other.aircraftState.State == "FAIL")
                    continue;

                Vector3 otherTarget = other.controller != null
                    ? other.controller.targetPosition
                    : pair.Value.transform.position;
                if (VertiportWaypointSafety.ConflictsWithAircraft(
                        aircraftId,
                        target,
                        pair.Key,
                        pair.Value.transform.position,
                        otherTarget,
                        true,
                        clearance))
                    return false;
            }

            runtimeWaypointClaims[RuntimeWaypointKey(target)] = new RuntimeWaypointClaim
            {
                AircraftId = aircraftId,
                Target = target
            };
            return true;
        }

        public void ReleaseRuntimeWaypointClaims(string aircraftId)
        {
            foreach (string key in runtimeWaypointClaims
                         .Where(pair => pair.Value.AircraftId == aircraftId)
                         .Select(pair => pair.Key).ToList())
                runtimeWaypointClaims.Remove(key);
        }

        public void ResumeAfterCollisionPause()
        {
            collisionPaused = false;
        }

        private void PauseForCollision(string pairKey, Vector3 point)
        {
            collisionPaused = true;
            LastCollisionPoint = point;
            LastCollisionPair = pairKey;
            Time.timeScale = 0f;
            CollisionPauseRequested?.Invoke(point, pairKey);
            Debug.LogWarning(
                $"[SimulationManager] PAUSED on collision: {pairKey} at {point}. Press Pause button to resume.");
        }

        private void PruneRuntimeWaypointClaims(float clearance)
        {
            foreach (var pair in runtimeWaypointClaims.ToList())
            {
                RuntimeWaypointClaim claim = pair.Value;
                if (!AircraftObjects.TryGetValue(claim.AircraftId, out GameObject aircraft) || aircraft == null)
                {
                    runtimeWaypointClaims.Remove(pair.Key);
                    continue;
                }

                AircraftAgent agent = aircraft.GetComponent<AircraftAgent>();
                bool active = agent?.aircraftState != null &&
                              agent.aircraftState.MissionPhase != "END" &&
                              agent.aircraftState.State != "FAIL";
                bool physicallyNear = Vector3.Distance(aircraft.transform.position, claim.Target) < clearance * 1.35f;
                bool headingThere = agent?.controller != null &&
                                    Vector3.Distance(agent.controller.targetPosition, claim.Target) < 0.6f;
                if (!active || (!physicallyNear && !headingThere))
                    runtimeWaypointClaims.Remove(pair.Key);
            }
        }

        private static string RuntimeWaypointKey(Vector3 point)
        {
            return $"{Mathf.Round(point.x * 2f) * 0.5f:F1}_" +
                   $"{Mathf.Round(point.y * 2f) * 0.5f:F1}_" +
                   $"{Mathf.Round(point.z * 2f) * 0.5f:F1}";
        }

        #endregion

        // ==================== Calling Interface (for AircraftAgent) ====================
        /// <summary>
        /// Atomically gates a transition into the next route lane. Signal and
        /// downstream-capacity flags stop the aircraft at the current lane end;
        /// a same-frame placeholder serializes simultaneous entry attempts.
        /// </summary>
        public bool TryReserveLaneEntry(
            string currentLaneId,
            string targetLaneId,
            string aircraftId)
        {
            if (string.IsNullOrEmpty(currentLaneId) ||
                string.IsNullOrEmpty(targetLaneId) ||
                string.IsNullOrEmpty(aircraftId) ||
                !LaneStates.TryGetValue(currentLaneId, out LaneState currentState) ||
                !LaneStates.TryGetValue(targetLaneId, out LaneState targetState))
            {
                return false;
            }

            float safeInterval = config != null ? config.safeInterval : 10f;
            bool enteringLayerConnector = LaneConnectionCorridors.ContainsKey(targetLaneId);
            IEnumerable<float> targetProgresses = targetState.AircraftsInLane
                .Where(pair => pair.Key != aircraftId &&
                               pair.Key != "__VIRTUAL__" &&
                               pair.Key != "__ENTRY_" + aircraftId)
                .Select(pair => pair.Value);
            if (!LaneEntryDecisionUtility.CanEnter(
                    currentState.SignalCheck,
                    currentState.CapabilityCheck,
                    targetProgresses,
                    targetState.Length,
                    safeInterval,
                    enforceSourceControl: !enteringLayerConnector))
            {
                return false;
            }

            targetState.AircraftsInLane["__ENTRY_" + aircraftId] = 0f;
            return true;
        }

        public Vector3 GetChangingPosition(string targetLaneId, string currentLaneId, string aircraftId)
        {
            if (!LaneStates.ContainsKey(targetLaneId) || !LaneStates.ContainsKey(currentLaneId))
                return Vector3.zero;

            var targetState = LaneStates[targetLaneId];
            var currentState = LaneStates[currentLaneId];

            if (!currentState.AircraftsInLane.ContainsKey(aircraftId))
                return Vector3.zero;

            if (!Lanes.ContainsKey(targetLaneId)) return Vector3.zero;

            float safeInterval = config != null ? config.safeInterval : 10f;
            float frontGap = safeInterval * (config != null ? config.laneChangeFrontGapFactor : 1f);
            float rearGap = safeInterval * (config != null ? config.laneChangeRearGapFactor : 1.5f);
            float laneChangeDist = config != null ? config.laneChangeDistance : 2f;
            GameObject aircraftObject = AircraftObjects.TryGetValue(aircraftId, out GameObject foundObject)
                ? foundObject : null;
            Vector3 aircraftPosition = aircraftObject != null
                ? aircraftObject.transform.position
                : GetPointOnLane(Lanes[currentLaneId], currentState.AircraftsInLane[aircraftId]);
            float targetProgress = LaneGeometryUtility.ProjectProgress(
                Lanes[targetLaneId].Centerline,
                aircraftPosition);
            float targetLength = Mathf.Max(targetState.Length, 0.001f);

            if (!LaneChangeDecisionUtility.HasSafeGap(
                    targetProgress,
                    targetState.AircraftsInLane
                        .Where(other => other.Key != aircraftId)
                        .Select(other => other.Value),
                    targetLength,
                    frontGap,
                    rearGap))
                return Vector3.zero;

            // Arbitrate requests for the same merge gap using mission priority,
            // waiting age, then stable aircraft ID.
            AircraftAgent requestingAgent = aircraftObject != null
                ? aircraftObject.GetComponent<AircraftAgent>() : null;
            foreach (GameObject otherObject in AircraftObjects.Values)
            {
                if (otherObject == null || otherObject == aircraftObject) continue;
                AircraftAgent otherAgent = otherObject.GetComponent<AircraftAgent>();
                if (otherAgent == null || !otherAgent.isWaitingForLaneChange ||
                    otherAgent.pendingTargetLaneId != targetLaneId)
                    continue;

                float otherProgress = LaneGeometryUtility.ProjectProgress(
                    Lanes[targetLaneId].Centerline,
                    otherObject.transform.position);
                float separation = Mathf.Abs(otherProgress - targetProgress) * targetLength;
                if (separation < Mathf.Max(frontGap, rearGap) &&
                    HasHigherLaneChangePriority(otherAgent, requestingAgent))
                    return Vector3.zero;
            }

            // Reserve the actual merge position for this frame. Unlike the old
            // 0.01 placeholder, this blocks only the contested longitudinal gap.
            targetState.AircraftsInLane["__LC_" + aircraftId] = targetProgress;
            float advancedProgress = targetProgress + laneChangeDist / targetLength;
            return GetPointOnLane(Lanes[targetLaneId], advancedProgress);
        }

        private bool HasHigherLaneChangePriority(AircraftAgent candidate, AircraftAgent requester)
        {
            if (candidate == null) return false;
            if (requester == null) return true;

            int candidatePriority = candidate.aircraftState?.Mission?.Priority ?? 1;
            int requesterPriority = requester.aircraftState?.Mission?.Priority ?? 1;
            return LaneChangeDecisionUtility.CandidateHasPriority(
                candidatePriority,
                candidate.laneChangeWaitStartedAt,
                candidate.aircraftState?.AircraftId,
                requesterPriority,
                requester.laneChangeWaitStartedAt,
                requester.aircraftState?.AircraftId);
        }

        public bool TryRerouteCruise(AircraftAgent agent, string blockedLaneId)
        {
            AircraftState state = agent?.aircraftState;
            if (state == null || state.MissionPhase != "CRUISE" ||
                string.IsNullOrEmpty(state.CurrentLaneId) ||
                string.IsNullOrEmpty(state.ExitCorridorId) ||
                state.LaneSequence == null || state.LaneSequence.Count == 0 ||
                !Corridors.TryGetValue(state.ExitCorridorId, out NetConnectedCorridor exitCorridor))
            {
                return false;
            }

            var forbidden = new HashSet<string>(System.StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(blockedLaneId)) forbidden.Add(blockedLaneId);
            float laneChangePenalty = config != null
                ? Mathf.Max(config.safeInterval, config.laneChangeDistance)
                : 10f;
            List<Lane> alternate = LaneRoutingUtility.FindShortestPath(
                Lanes,
                Edges.Values,
                LaneConnectionCorridors.Values,
                state.CurrentLaneId,
                exitCorridor.LinkLaneId,
                laneChangePenalty,
                forbidden);
            if (alternate == null || alternate.Count < 2 ||
                alternate[1].LaneId == blockedLaneId)
            {
                return false;
            }

            int currentIndex = Mathf.Clamp(agent.currentLaneSequenceIndex, 0,
                Mathf.Max(0, state.LaneSequence.Count - 1));
            var rerouted = state.LaneSequence.Take(currentIndex + 1).ToList();
            rerouted.AddRange(alternate.Skip(1));
            state.LaneSequence = rerouted;
            Debug.LogWarning($"[Deadlock] Rerouted {state.AircraftId} around '{blockedLaneId}'. " +
                             $"New remaining route has {alternate.Count} lane(s).");
            SimulationRecorder.Instance?.RecordEvent(
                "DeadlockReroute",
                state.AircraftId,
                $"Avoided lane {blockedLaneId}",
                agent.transform.position);
            return true;
        }

        public void RemoveDeadlockedAircraft(AircraftAgent agent, float waitDuration)
        {
            AircraftState state = agent?.aircraftState;
            if (state == null || state.State == "FAIL") return;

            string aircraftId = state.AircraftId;
            Debug.LogError($"[Deadlock] Removing {aircraftId} after {waitDuration:F1}s " +
                           "without a safe lane-change or alternate route.");
            state.State = "FAIL";
            state.MissionPhase = "END";
            state.CurrentLaneId = null;
            agent.activeLaneChangeTargetLaneId = null;
            agent.hasMissionCompleted = false;
            agent.controller.SetTargetPosition(agent.transform.position);
            cumulFailCount++;
            SimulationRecorder.Instance?.RecordEvent(
                "DeadlockRemoval",
                aircraftId,
                $"Removed after {waitDuration:F1}s",
                agent.transform.position);

            if (config == null || config.missionMode != MissionGenerationMode.RandomContinuous)
                return;

            if (AircraftOriginHP.TryGetValue(aircraftId, out string originHp))
                OccupiedHelipads.Remove(originHp);
            if (AircraftDestHP.TryGetValue(aircraftId, out string destinationHp))
                OccupiedHelipads.Remove(destinationHp);
            AircraftOriginHP.Remove(aircraftId);
            AircraftDestHP.Remove(aircraftId);
            AircraftObjects.Remove(aircraftId);
            Destroy(agent.gameObject);
            SpawnReplacementAircraft(aircraftId);
        }

        public Vector3 GetPointOnLane(Lane lane, float progress)
        {
            return LaneGeometryUtility.PointAtProgress(lane?.Centerline, progress);
        }

        public bool CheckEntryConflict(string corridorId)
        {
            if (!Corridors.ContainsKey(corridorId))
                return false;

            string connectedLaneId = Corridors[corridorId].LinkLaneId;
            if (string.IsNullOrEmpty(connectedLaneId) || !LaneStates.ContainsKey(connectedLaneId))
                return false;

            if (!Lanes.ContainsKey(connectedLaneId)) return false;

            LaneState laneState = LaneStates[connectedLaneId];
            Vector3 entryPoint = Corridors[corridorId].LaneLinkPoint;

            if (laneState.AircraftsInLane.Count == 0)
                return false;

            foreach (var kvp in laneState.AircraftsInLane)
            {
                if (kvp.Key == "__VIRTUAL__") continue;

                Vector3 currentPos = GetPointOnLane(Lanes[connectedLaneId], kvp.Value);
                float safeDist = config != null ? config.safeInterval : 10f;

                if (Vector3.Distance(currentPos, entryPoint) < safeDist)
                    return true;
            }

            return false;
        }

        // ==================== Visualization ====================
        // ==================== Traffic Signal System ====================

        private class EdgePair { public Edge EdgeA; public Edge EdgeB; }
        private struct PairResult { public EdgePair pair; public Edge single; }

        private class IntersectionController
        {
            public string NodeId;
            public int CurrentPhaseIndex;
            public float PhaseDuration;
            public List<List<string>> Phases = new List<List<string>>();
            public float GreenWaveOffset;
        }

        public void BuildTrafficSignalSystem()
        {
            _intersections.Clear();
            _alwaysGreenRightLanes.Clear();
            float greenSignalTime = config != null ? config.greenSignalTime : 10f;

            // Permissive right turns are opt-in. The safe default signals every
            // movement because no explicit conflict-area yield model is assumed.
            if (config != null && config.permissiveRightTurn)
            {
                foreach (var kv in Lanes)
                    if (kv.Value.LaneType == "Right")
                        _alwaysGreenRightLanes.Add(kv.Key);
            }

            foreach (var node in Nodes.Values)
            {
                string nodeId = node.NodeId;
                Vector3 intersectionPos = node.Position;
                List<Edge> allConnectedEdges = new List<Edge>();
                IntersectionController controller = new IntersectionController
                {
                    NodeId = nodeId,
                    CurrentPhaseIndex = 0,
                    PhaseDuration = greenSignalTime
                };

                foreach (var edgeId in node.ConnectedEdges)
                {
                    if (Edges.ContainsKey(edgeId))
                        allConnectedEdges.Add(Edges[edgeId]);
                }
                if (allConnectedEdges.Count <= 2) continue;

                Dictionary<Edge, List<Lane>> edgeToIncomingLanes = new Dictionary<Edge, List<Lane>>();
                List<Edge> validIncomingEdges = new List<Edge>();
                List<string> straightLeftRightLanes = new List<string>();

                foreach (var edge in allConnectedEdges)
                {
                    List<Lane> incoming = GetIncomingLanes(edge, intersectionPos);
                    if (incoming.Count > 0)
                    {
                        edgeToIncomingLanes[edge] = incoming;
                        validIncomingEdges.Add(edge);
                        foreach (var lane in incoming)
                        {
                            if (!string.IsNullOrEmpty(lane.LaneType) &&
                                lane.LaneType.Equals("StraightLeftRight", System.StringComparison.OrdinalIgnoreCase))
                            {
                                straightLeftRightLanes.Add(lane.LaneId);
                            }
                        }
                    }
                }

                if (validIncomingEdges.Count == 3)
                {
                    var result = FindBestOpposingPair(validIncomingEdges, edgeToIncomingLanes);
                    EdgePair mainPair = result.pair;
                    Edge sideEdge = result.single;
                    List<string> phase1 = GetLanesByDirectionType(mainPair, edgeToIncomingLanes, isLeftPhase: false);
                    if (phase1.Count > 0) controller.Phases.Add(phase1);
                    List<string> phase2 = GetLanesByDirectionType(mainPair, edgeToIncomingLanes, isLeftPhase: true);
                    if (phase2.Count > 0) controller.Phases.Add(phase2);
                    List<string> phase3 = new List<string>();
                    CollectLanes(phase3, edgeToIncomingLanes[sideEdge], checkType: false);
                    if (phase3.Count > 0) controller.Phases.Add(phase3);
                }
                else if (validIncomingEdges.Count == 4)
                {
                    var result1 = FindBestOpposingPair(validIncomingEdges, edgeToIncomingLanes);
                    EdgePair pair1 = result1.pair;
                    List<Edge> remaining = new List<Edge>();
                    foreach (var e in validIncomingEdges)
                        if (e != pair1.EdgeA && e != pair1.EdgeB) remaining.Add(e);
                    EdgePair pair2 = new EdgePair { EdgeA = remaining[0], EdgeB = remaining[1] };
                    List<string> p1 = GetLanesByDirectionType(pair1, edgeToIncomingLanes, isLeftPhase: false);
                    if (p1.Count > 0) controller.Phases.Add(p1);
                    List<string> p2 = GetLanesByDirectionType(pair1, edgeToIncomingLanes, isLeftPhase: true);
                    if (p2.Count > 0) controller.Phases.Add(p2);
                    List<string> p3 = GetLanesByDirectionType(pair2, edgeToIncomingLanes, isLeftPhase: false);
                    if (p3.Count > 0) controller.Phases.Add(p3);
                    List<string> p4 = GetLanesByDirectionType(pair2, edgeToIncomingLanes, isLeftPhase: true);
                    if (p4.Count > 0) controller.Phases.Add(p4);
                }
                else if (validIncomingEdges.Count >= 5)
                {
                    foreach (var edge in validIncomingEdges)
                    {
                        List<string> phase = new List<string>();
                        CollectLanes(phase, edgeToIncomingLanes[edge], checkType: false);
                        if (phase.Count > 0) controller.Phases.Add(phase);
                    }
                }

                if (straightLeftRightLanes.Count > 0)
                    controller.Phases.Add(straightLeftRightLanes);

                // Never leave an incoming lane uncontrolled because of an unknown
                // or newly introduced LaneType. Give every uncovered lane a safe,
                // exclusive fallback phase.
                var coveredLanes = new HashSet<string>(
                    controller.Phases.SelectMany(phase => phase),
                    System.StringComparer.Ordinal);
                foreach (Lane incomingLane in edgeToIncomingLanes.Values.SelectMany(value => value))
                {
                    if (!_alwaysGreenRightLanes.Contains(incomingLane.LaneId) &&
                        coveredLanes.Add(incomingLane.LaneId))
                    {
                        controller.Phases.Add(new List<string> { incomingLane.LaneId });
                    }
                }

                if (controller.Phases.Count > 1)
                    _intersections.Add(controller);
            }

            CalculateGreenWaveOffsets();
            Debug.Log($"[SimulationManager] Traffic signal system built: {_intersections.Count} intersections.");
        }

        private List<Lane> GetIncomingLanes(Edge edge, Vector3 nodePos)
        {
            List<Lane> result = new List<Lane>();
            foreach (var lane in edge.Lanes)
            {
                if (lane.Centerline == null || lane.Centerline.Count < 2) continue;
                float startDist = Vector3.Distance(lane.Centerline[0], nodePos);
                float endDist = Vector3.Distance(lane.Centerline[lane.Centerline.Count - 1], nodePos);
                if (endDist < startDist) result.Add(lane); // flows into node
            }
            return result;
        }

        private Vector3 GetDirectionAwayFromNode(Edge edge, Vector3 nodePos)
        {
            if (edge.Centerline == null || edge.Centerline.Count < 2) return Vector3.forward;
            Vector3 p1 = edge.Centerline[0];
            Vector3 p2 = edge.Centerline[edge.Centerline.Count - 1];
            if (Vector3.Distance(p1, nodePos) < Vector3.Distance(p2, nodePos))
                return (p2 - p1).normalized;
            else
                return (p1 - p2).normalized;
        }

        /// <summary>Average direction of incoming lanes (as flown, start→end).</summary>
        private Vector3 GetAvgDirection(List<Lane> lanes)
        {
            Vector3 sum = Vector3.zero;
            foreach (var lane in lanes)
            {
                if (lane.Centerline == null || lane.Centerline.Count < 2) continue;
                sum += (lane.Centerline[lane.Centerline.Count - 1] - lane.Centerline[0]).normalized;
            }
            return sum.normalized;
        }

        /// <summary>
        /// Find the two edges whose incoming-lane average directions are MOST OPPOSITE
        /// (smallest dot product). These form the main-road pair for signal phasing.
        /// </summary>
        private PairResult FindBestOpposingPair(List<Edge> edges, Dictionary<Edge, List<Lane>> laneInfo)
        {
            Edge bestA = null, bestB = null;
            float minDot = 1f; // most opposite = most negative dot
            for (int i = 0; i < edges.Count; i++)
            {
                for (int j = i + 1; j < edges.Count; j++)
                {
                    if (!laneInfo.ContainsKey(edges[i]) || !laneInfo.ContainsKey(edges[j])) continue;
                    Vector3 dirA = GetAvgDirection(laneInfo[edges[i]]);
                    Vector3 dirB = GetAvgDirection(laneInfo[edges[j]]);
                    float dot = Vector3.Dot(dirA, dirB);
                    if (dot < minDot) { minDot = dot; bestA = edges[i]; bestB = edges[j]; }
                }
            }
            Edge single = null;
            foreach (var e in edges)
                if (e != bestA && e != bestB) { single = e; break; }
            return new PairResult { pair = new EdgePair { EdgeA = bestA, EdgeB = bestB }, single = single };
        }

        private List<string> GetLanesByDirectionType(EdgePair pair, Dictionary<Edge, List<Lane>> laneInfo, bool isLeftPhase)
        {
            List<string> phaseLanes = new List<string>();
            if (laneInfo.ContainsKey(pair.EdgeA))
                CollectLanes(phaseLanes, laneInfo[pair.EdgeA], checkType: true, isLeftPhase: isLeftPhase);
            if (pair.EdgeB != null && laneInfo.ContainsKey(pair.EdgeB))
                CollectLanes(phaseLanes, laneInfo[pair.EdgeB], checkType: true, isLeftPhase: isLeftPhase);
            return phaseLanes;
        }

        private void CollectLanes(List<string> targetList, List<Lane> lanes, bool checkType, bool isLeftPhase = false)
        {
            foreach (var lane in lanes)
            {
                if (!LaneStates.ContainsKey(lane.LaneId)) continue;
                string type = lane.LaneType;
                if (string.IsNullOrEmpty(type)) type = "Straight";
                bool shouldAdd = false;
                if (!checkType)
                {
                    shouldAdd = true;
                }
                else
                {
                    if (!isLeftPhase)
                    {
                        // Straight phase: Straight, Right, StraightRight (NOT StraightLeft)
                        if (type.Equals("Straight", System.StringComparison.OrdinalIgnoreCase) ||
                            type.Equals("Right", System.StringComparison.OrdinalIgnoreCase) ||
                            type.Equals("StraightRight", System.StringComparison.OrdinalIgnoreCase))
                        {
                            shouldAdd = true;
                        }
                    }
                    else
                    {
                        // Left phase: Left, StraightLeft, LeftRight (NOT StraightRight)
                        if (type.Equals("Left", System.StringComparison.OrdinalIgnoreCase) ||
                            type.Equals("StraightLeft", System.StringComparison.OrdinalIgnoreCase) ||
                            type.Equals("LeftRight", System.StringComparison.OrdinalIgnoreCase))
                        {
                            shouldAdd = true;
                        }
                    }
                }
                if (shouldAdd && !targetList.Contains(lane.LaneId))
                    targetList.Add(lane.LaneId);
            }
        }

        private void CalculateGreenWaveOffsets()
        {
            if (_intersections.Count == 0) return;

            float greenWaveSpeed = Mathf.Max(
                config != null ? config.greenWaveSpeed : 15f,
                0.01f);
            float greenSignalTime = config != null ? config.greenSignalTime : 10f;
            float clearanceTime = config != null ? config.signalClearanceTime : 2f;
            string rootNodeId = _intersections
                .Select(controller => controller.NodeId)
                .Where(Nodes.ContainsKey)
                .OrderBy(id => id, System.StringComparer.Ordinal)
                .FirstOrDefault();
            if (rootNodeId == null) return;

            var distances = Nodes.Keys.ToDictionary(id => id, _ => float.MaxValue);
            var unvisited = new HashSet<string>(Nodes.Keys, System.StringComparer.Ordinal);
            distances[rootNodeId] = 0f;
            while (unvisited.Count > 0)
            {
                string current = unvisited
                    .OrderBy(id => distances[id])
                    .ThenBy(id => id, System.StringComparer.Ordinal)
                    .First();
                if (distances[current] == float.MaxValue) break;
                unvisited.Remove(current);

                foreach (string neighbor in Nodes[current].NeighborNodes)
                {
                    if (!unvisited.Contains(neighbor) || !Nodes.ContainsKey(neighbor)) continue;
                    float candidate = distances[current] + Vector3.Distance(
                        Nodes[current].Position,
                        Nodes[neighbor].Position);
                    if (candidate < distances[neighbor]) distances[neighbor] = candidate;
                }
            }

            foreach (IntersectionController controller in _intersections)
            {
                float travelTime = distances.GetValueOrDefault(controller.NodeId, 0f) / greenWaveSpeed;
                float cycleDuration = controller.Phases.Count *
                                      (Mathf.Max(0.01f, greenSignalTime) + Mathf.Max(0f, clearanceTime));
                controller.GreenWaveOffset = SignalTimingUtility.NormalizeOffset(
                    travelTime,
                    cycleDuration);
            }
        }

        public void UpdateTrafficSignals()
        {
            // Lane occupancy and downstream capacity are handled by UpdateLaneStates.
            foreach (var lane in LaneStates.Values)
                lane.SignalCheck = false;

            float greenSignalTime = config != null ? config.greenSignalTime : 10f;
            float clearanceTime = config != null ? config.signalClearanceTime : 2f;
            foreach (var controller in _intersections)
            {
                int phaseCount = controller.Phases.Count;
                if (phaseCount <= 0) continue;

                SignalPhaseState phaseState = SignalTimingUtility.Evaluate(
                    Time.time,
                    controller.GreenWaveOffset,
                    phaseCount,
                    greenSignalTime,
                    clearanceTime);
                controller.CurrentPhaseIndex = phaseState.ActivePhaseIndex;

                foreach (string laneId in controller.Phases.SelectMany(phase => phase).Distinct())
                {
                    if (LaneStates.TryGetValue(laneId, out LaneState laneState) &&
                        !_alwaysGreenRightLanes.Contains(laneId))
                        laneState.SignalCheck = true;
                }

                if (!phaseState.IsClearance)
                {
                    foreach (string laneId in controller.Phases[phaseState.ActivePhaseIndex])
                    {
                        if (LaneStates.TryGetValue(laneId, out LaneState laneState) &&
                            !_alwaysGreenRightLanes.Contains(laneId))
                            laneState.SignalCheck = false;
                    }
                }
            }
        }

        /// <summary>
        /// File-driven missions are one-shot. A completed aircraft is removed from
        /// the live scene immediately, while its AircraftState and recorder history
        /// remain available for metrics and export.
        /// </summary>
        private void ProcessCompletedFileMissions()
        {
            foreach (var pair in AircraftObjects.ToList())
            {
                GameObject aircraftObject = pair.Value;
                AircraftAgent agent = aircraftObject != null
                    ? aircraftObject.GetComponent<AircraftAgent>()
                    : null;
                if (agent?.aircraftState == null ||
                    agent.aircraftState.MissionPhase != "END" ||
                    !agent.hasMissionCompleted)
                    continue;

                DeregisterCompletedAircraft(pair.Key, agent);
            }
        }

        private void DeregisterCompletedAircraft(string aircraftId, AircraftAgent agent)
        {
            ReleaseAircraftOccupancy(aircraftId);

            if (AircraftOriginHP.TryGetValue(aircraftId, out string originHp))
                OccupiedHelipads.Remove(originHp);
            if (AircraftDestHP.TryGetValue(aircraftId, out string destinationHp))
                OccupiedHelipads.Remove(destinationHp);

            AircraftOriginHP.Remove(aircraftId);
            AircraftDestHP.Remove(aircraftId);
            AircraftObjects.Remove(aircraftId);
            pendingAircraftSpawns.Remove(aircraftId);
            pendingMissions.Remove(aircraftId);
            helipadWaitingAgents.Remove(aircraftId);
            completedMissionAt.Remove(aircraftId);
            waitingAircraft.Remove(agent);

            if (agent != null)
            {
                agent.hasMissionCompleted = false;
                if (Application.isPlaying)
                    Destroy(agent.gameObject);
                else
                    DestroyImmediate(agent.gameObject);
            }

            Debug.Log($"[SimulationManager] File mission {aircraftId} completed; " +
                      "aircraft instance removed and live occupancy released.");
        }

        private void ReleaseAircraftOccupancy(string aircraftId)
        {
            ReleaseRuntimeWaypointClaims(aircraftId);
            foreach (LaneState state in LaneStates.Values)
            {
                state.AircraftsInLane.Remove(aircraftId);
                state.AircraftsInLane.Remove("__ENTRY_" + aircraftId);
                state.AircraftsInLane.Remove("__LC_" + aircraftId);
            }
            foreach (CorridorState state in CorridorStatesDict.Values)
                state.AircraftsInCorridor.Remove(aircraftId);

            RemoveAircraftWindows(RampStatesDict.Values.Select(state => state.OccupiedTimeWindows), aircraftId);
            RemoveAircraftWindows(ApproachNodeStates.Values.Select(state => state.OccupiedTimeWindows), aircraftId);
            RemoveAircraftWindows(ApproachEdgeStates.Values.Select(state => state.OccupiedTimeWindows), aircraftId);
            RemoveAircraftWindows(VertexStatesDict.Values.Select(state => state.OccupiedTimeWindows), aircraftId);
            RemoveAircraftWindows(HelipadStatesDict.Values.Select(state => state.OccupiedTimeWindows), aircraftId);
            RemoveAircraftWindows(CorridorStatesDict.Values.Select(state => state.OccupiedTimeWindows), aircraftId);
            RemoveAircraftWindows(HSStates.Values.Select(state => state.OccupiedTimeWindows), aircraftId);
        }

        private static void RemoveAircraftWindows(
            IEnumerable<List<OccupiedTimeWindow>> windowLists,
            string aircraftId)
        {
            foreach (List<OccupiedTimeWindow> windows in windowLists)
                windows.RemoveAll(window => window.OccupiedAircraftId == aircraftId);
        }

        void OnDrawGizmos()
        {
            if (config == null || !config.showLaneStateGizmos || LaneStates == null || Lanes == null)
                return;

            foreach (var kvp in LaneStates)
            {
                string laneId = kvp.Key;
                LaneState state = kvp.Value;
                if (!Lanes.ContainsKey(laneId)) continue;

                var laneData = Lanes[laneId];
                if (laneData.Centerline == null || laneData.Centerline.Count == 0) continue;

                Vector3 endPos = laneData.Centerline[laneData.Centerline.Count - 1];
                Vector3 drawPos = endPos + Vector3.up * config.signalHeightOffset;

                // Signal light: red if blocked, green if free
                Gizmos.color = (state.SignalCheck || state.CapabilityCheck) ? Color.red : Color.green;
                Gizmos.DrawSphere(drawPos, config.signalGizmoSize);
                Gizmos.DrawLine(endPos, drawPos);
            }
        }

        private Color GetLaneTypeColor(string laneType)
        {
            switch (laneType)
            {
                case "Left":         return new Color(0.3f, 0.5f, 1.0f);  // blue
                case "Right":        return new Color(1.0f, 0.6f, 0.0f);  // orange
                case "Straight":     return new Color(0.0f, 0.8f, 0.2f);  // green
                case "StraightLeft": return new Color(0.0f, 0.7f, 0.7f);  // cyan
                case "StraightRight":return new Color(0.5f, 0.8f, 0.0f);  // yellow-green
                case "LeftRight":    return new Color(0.8f, 0.3f, 0.8f);  // purple
                default:             return Color.green;
            }
        }
    }
}
