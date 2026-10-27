using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RoadNetworkEditor.RuntimeUI
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SarSimulationRunKind
    {
        Single,
        BatchMember,
        Batch
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum SarSimulationRunStatus
    {
        Running,
        Completed,
        Stopped,
        Failed,
        Cancelled,
        Interrupted
    }

    /// <summary>
    /// Versioned, portable description of one simulation run or one parameter-sweep batch.
    /// File names are always relative to the manifest folder; absolute runtime paths are
    /// deliberately excluded from JSON so a scene folder can be moved as one unit.
    /// </summary>
    [Serializable]
    public sealed class SarSimulationRunManifest
    {
        public const int CurrentSchemaVersion = 2;
        public const float DefaultCorridorTangentRatio = 0.3f;

        public int SchemaVersion = CurrentSchemaVersion;
        public string RunId;
        public string SceneId;
        public string SceneName;
        public SarSimulationRunKind Kind;
        public string BatchId;
        public int BatchIndex;
        public int BatchCount;
        public string StartedUtc;
        public string CompletedUtc;
        public double Duration;
        public SarSimulationRunStatus Status = SarSimulationRunStatus.Running;
        public Dictionary<string, string> Parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, double> Metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        public string MetricsTableFile;
        public string RecordingFile;
        public string OccupancyLogFile;
        public string BatchSummaryFile;
        public string TopologyFingerprint;
        public string AxisNetworkSnapshotFile;
        public string FullNetworkSnapshotFile;
        public string VertiportSnapshotFile;
        /// <summary>
        /// Tangent-handle ratio used by the live VertiportManager when the topology
        /// snapshot was captured.  The initializer preserves the geometry of legacy
        /// manifests that predate this field.
        /// </summary>
        public float CorridorTangentRatio = DefaultCorridorTangentRatio;
        public List<string> ChildRunIds = new List<string>();
        public List<string> ArtifactErrors = new List<string>();

        [JsonIgnore] public string FolderPath;
        [JsonIgnore] public string ManifestPath;
    }

    public sealed class SarCsvTable
    {
        public string SourcePath;
        public List<List<string>> Rows = new List<List<string>>();
        public int ColumnCount;
    }

    /// <summary>
    /// Compact data required by historical 3D review. It deliberately excludes the
    /// millions of corridor/ramp/approach snapshots and events stored in a full recording.
    /// </summary>
    public sealed class SarHistoricalVisualizationData
    {
        public Dictionary<string, float> LaneTraversals =
            new Dictionary<string, float>(StringComparer.Ordinal);
        public Dictionary<string, float> ResourceReservationSeconds =
            new Dictionary<string, float>(StringComparer.Ordinal);
        public List<List<Vector3>> Trajectories = new List<List<Vector3>>();
        public int SourceTrajectoryCount;
        public int CompletedTrajectoryCount;
        public int ExcludedIncompleteTrajectoryCount;
        public int ExcludedUnverifiableTrajectoryCount;
        public int RetainedTrajectoryPointCount;
    }

    /// <summary>
    /// Scene-scoped persistent simulation history. The service never moves or deletes the
    /// files produced in Output: it takes independent snapshots into the active scene.
    /// </summary>
    public sealed class SarSimulationHistory : MonoBehaviour
    {
        public const string HistoryFolderName = "SimulationHistory";
        public const string RecycleFolderName = ".Trash";
        public const string ManifestFileName = "manifest.json";
        public const string ArchivedMetricsFileName = "MetricsTable.csv";
        public const string ArchivedRecordingFileName = "SimulationRecording.json.gz";
        public const string ArchivedOccupancyFileName = "OccupancyLog.json";
        public const string ArchivedBatchSummaryFileName = "BatchExperimentRunSummaryTable.csv";
        public const string AxisNetworkSnapshotFileName = "AxisNetworkSnapshot.json";
        public const string FullNetworkSnapshotFileName = "FullNetworkSnapshot.json";
        public const string VertiportSnapshotFileName = "VertiportsSnapshot.json";

        private static readonly TimeSpan ArtifactTimestampTolerance = TimeSpan.FromSeconds(2d);

        private static readonly JsonSerializerSettings ManifestJsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            Converters = new List<JsonConverter> { new StringEnumConverter() }
        };

        private static readonly JsonSerializerSettings SnapshotJsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Include,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            // Snapshot DTOs initialise their collection fields so a newly-authored scene is
            // immediately usable (Network, for example, starts with a legacy L0 placeholder).
            // Json.NET populates those existing collections by default, which used to append
            // the archived altitude layers after that placeholder.  A snapshot containing the
            // real "Lower Network Layer" therefore loaded as both "Default Layer" and "Lower
            // Network Layer", even though both entries addressed the same L0 geometry.  A
            // historical snapshot is authoritative, so replace initialised collections rather
            // than merging them with constructor defaults.
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Culture = CultureInfo.InvariantCulture,
            Converters = new List<JsonConverter> { new Vector3Converter() }
        };

        private SimulationManager manager;
        private SarSceneWorkspace workspace;
        private SarSimulationRunManifest activeRun;
        private SarSimulationRunManifest activeBatch;
        private string pendingBatchId;
        private int pendingBatchIndex;
        private int pendingBatchCount;
        private readonly List<SarSimulationRunManifest> manifests = new List<SarSimulationRunManifest>();

        public event Action HistoryChanged;

        public IReadOnlyList<SarSimulationRunManifest> Manifests => manifests;
        public SarSimulationRunManifest ActiveRun => activeRun;
        public SarSimulationRunManifest ActiveBatch => activeBatch;
        public string CurrentSceneId => ResolveSceneIdentity().Id;
        public string CurrentSceneName => ResolveSceneIdentity().Name;
        public string CurrentSceneFolder => ResolveSceneIdentity().Folder;
        public string HistoryRoot => Path.Combine(CurrentSceneFolder, HistoryFolderName);
        public string RecycleRoot => Path.Combine(HistoryRoot, RecycleFolderName);
        public string CurrentTopologyFingerprint => ComputeTopologyFingerprint(
            manager?.networkManager?.AxisNetwork,
            manager?.vertiportManager?.Vertiports);

        public static SarSimulationHistory Ensure(SimulationManager simulationManager)
        {
            if (simulationManager == null) return null;
            SarSimulationHistory history = simulationManager.GetComponent<SarSimulationHistory>();
            if (history == null) history = simulationManager.gameObject.AddComponent<SarSimulationHistory>();
            history.Initialize(simulationManager, FindObjectOfType<SarSceneWorkspace>());
            return history;
        }

        public void Initialize(SimulationManager simulationManager, SarSceneWorkspace sceneWorkspace)
        {
            if (workspace != null) workspace.ActiveSceneChanged -= HandleActiveSceneChanged;
            manager = simulationManager;
            workspace = sceneWorkspace;
            if (workspace != null) workspace.ActiveSceneChanged += HandleActiveSceneChanged;
            LoadCurrentSceneManifests();
        }

        private void OnDestroy()
        {
            if (workspace != null) workspace.ActiveSceneChanged -= HandleActiveSceneChanged;
        }

        private void HandleActiveSceneChanged()
        {
            LoadCurrentSceneManifests();
        }

        public SarSimulationRunManifest BeginRun(
            SarSimulationRunKind kind = SarSimulationRunKind.Single,
            IDictionary<string, string> additionalParameters = null)
        {
            SceneIdentity scene = ResolveSceneIdentity();
            bool hasBatchContext = !string.IsNullOrWhiteSpace(pendingBatchId) && activeBatch != null &&
                                   string.Equals(activeBatch.RunId, pendingBatchId, StringComparison.Ordinal) &&
                                   string.Equals(activeBatch.SceneId, scene.Id, StringComparison.Ordinal);
            if (hasBatchContext) kind = SarSimulationRunKind.BatchMember;

            var manifest = NewManifest(scene, kind, "run");
            if (hasBatchContext)
            {
                manifest.BatchId = pendingBatchId;
                manifest.BatchIndex = Math.Max(1, pendingBatchIndex);
                manifest.BatchCount = Math.Max(manifest.BatchIndex, pendingBatchCount);
            }
            manifest.Parameters = CaptureParameters(manager != null ? manager.config : null);
            MergeParameters(manifest.Parameters, additionalParameters);

            CreateManifestFolder(manifest);
            CaptureTopologyArtifacts(manifest);
            SaveManifest(manifest);
            activeRun = manifest;

            if (kind == SarSimulationRunKind.BatchMember)
            {
                SarSimulationRunManifest batch = activeBatch != null && activeBatch.RunId == manifest.BatchId
                    ? activeBatch
                    : FindManifest(manifest.BatchId);
                if (batch != null && !batch.ChildRunIds.Contains(manifest.RunId))
                {
                    batch.ChildRunIds.Add(manifest.RunId);
                    SaveManifest(batch);
                }
            }

            AddOrReplace(manifest);
            HistoryChanged?.Invoke();
            return manifest;
        }

        public SarSimulationRunManifest CompleteRun(
            SarSimulationRunStatus status = SarSimulationRunStatus.Completed,
            double duration = -1d)
        {
            return CompleteRun(activeRun, status, duration);
        }

        public SarSimulationRunManifest CompleteRun(
            SarSimulationRunManifest manifest,
            SarSimulationRunStatus status = SarSimulationRunStatus.Completed,
            double duration = -1d)
        {
            if (manifest == null) return null;
            EnsureRuntimePaths(manifest);
            NormalizeManifestCollections(manifest);
            string output = manager != null
                ? manager.GetOutputDirectory()
                : (Application.isEditor
                    ? Path.GetFullPath(Path.Combine(Application.dataPath, "../Output"))
                    : SarRuntimePaths.OutputRoot);

            string metricsSource = Path.Combine(output, ArchivedMetricsFileName);
            RemoveArtifactErrors(manifest, ArchivedMetricsFileName);
            manifest.MetricsTableFile = null;
            manifest.Metrics = new Dictionary<string, double>(StringComparer.Ordinal);
            string metricsArchiveError = null;
            bool metricsArchived = TryValidateArtifactSource(metricsSource, manifest.StartedUtc,
                out string metricsValidationError) &&
                TryCopyArchive(metricsSource, Path.Combine(manifest.FolderPath, ArchivedMetricsFileName),
                    out metricsArchiveError);
            if (metricsArchived)
            {
                manifest.MetricsTableFile = ArchivedMetricsFileName;
                manifest.Metrics = ParseMetricsTable(Path.Combine(manifest.FolderPath, ArchivedMetricsFileName));
                if (manifest.Metrics.Count == 0)
                {
                    metricsArchived = false;
                    RecordArtifactError(manifest, ArchivedMetricsFileName,
                        "the archived table contains no finite numeric metrics");
                }
            }
            else
                RecordArtifactError(manifest, ArchivedMetricsFileName,
                    metricsValidationError ?? metricsArchiveError ?? "the artifact could not be archived");

            string recordingSource = Path.Combine(output, "SimulationRecording.json");
            RemoveArtifactErrors(manifest, "SimulationRecording.json");
            manifest.RecordingFile = null;
            string recordingArchiveError = null;
            bool recordingArchived = TryValidateArtifactSource(recordingSource, manifest.StartedUtc,
                out string recordingValidationError) &&
                TryCompressRecording(recordingSource,
                    Path.Combine(manifest.FolderPath, ArchivedRecordingFileName), out recordingArchiveError);
            if (recordingArchived)
            {
                manifest.RecordingFile = ArchivedRecordingFileName;
            }
            else
            {
                string reason = recordingValidationError ?? recordingArchiveError ??
                                "the artifact could not be archived";
                RecordArtifactError(manifest, "SimulationRecording.json", reason);
                Debug.LogWarning($"[SarSimulationHistory] Recording was not archived: {reason}");
            }

            string occupancySource = Path.Combine(output, ArchivedOccupancyFileName);
            RemoveArtifactErrors(manifest, ArchivedOccupancyFileName);
            manifest.OccupancyLogFile = null;
            if (File.Exists(occupancySource))
            {
                string occupancyArchiveError = null;
                if (TryValidateArtifactSource(occupancySource, manifest.StartedUtc,
                        out string occupancyValidationError) &&
                    TryCopyArchive(occupancySource,
                        Path.Combine(manifest.FolderPath, ArchivedOccupancyFileName),
                        out occupancyArchiveError))
                    manifest.OccupancyLogFile = ArchivedOccupancyFileName;
                else
                    RecordArtifactError(manifest, ArchivedOccupancyFileName,
                        occupancyValidationError ?? occupancyArchiveError ??
                        "the optional artifact could not be archived");
            }

            manifest.CompletedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            manifest.Status = ResolveFinalRunStatus(status, metricsArchived, recordingArchived);
            manifest.Duration = ResolveDuration(manifest, duration);
            SaveManifest(manifest);
            AddOrReplace(manifest);
            if (ReferenceEquals(activeRun, manifest) || activeRun?.RunId == manifest.RunId) activeRun = null;
            HistoryChanged?.Invoke();
            return manifest;
        }

        public SarSimulationRunManifest BeginBatch(int memberCount,
            IDictionary<string, string> additionalParameters = null)
        {
            SceneIdentity scene = ResolveSceneIdentity();
            var manifest = NewManifest(scene, SarSimulationRunKind.Batch, "batch");
            manifest.BatchId = manifest.RunId;
            manifest.BatchCount = Math.Max(0, memberCount);
            manifest.Parameters = CaptureParameters(manager != null ? manager.config : null);
            MergeParameters(manifest.Parameters, additionalParameters);
            CreateManifestFolder(manifest);
            SaveManifest(manifest);
            activeBatch = manifest;
            pendingBatchId = manifest.RunId;
            pendingBatchIndex = 0;
            pendingBatchCount = manifest.BatchCount;
            AddOrReplace(manifest);
            HistoryChanged?.Invoke();
            return manifest;
        }

        public void SetBatchMemberContext(string batchId, int index, int count)
        {
            pendingBatchId = batchId ?? string.Empty;
            pendingBatchIndex = Math.Max(1, index);
            pendingBatchCount = Math.Max(pendingBatchIndex, count);
            if (activeBatch == null || !string.Equals(activeBatch.RunId, pendingBatchId, StringComparison.Ordinal))
                activeBatch = FindManifest(pendingBatchId);
        }

        public void SetBatchMemberContext(int index, int count)
        {
            SetBatchMemberContext(activeBatch?.RunId, index, count);
        }

        public SarSimulationRunManifest CompleteBatch(
            string summaryFilePath = null,
            SarSimulationRunStatus status = SarSimulationRunStatus.Completed,
            double duration = -1d)
        {
            SarSimulationRunManifest manifest = activeBatch;
            if (manifest == null && !string.IsNullOrEmpty(pendingBatchId))
                manifest = FindManifest(pendingBatchId);
            if (manifest == null) return null;

            EnsureRuntimePaths(manifest);
            NormalizeManifestCollections(manifest);
            if (!string.IsNullOrWhiteSpace(summaryFilePath))
            {
                string destination = Path.Combine(manifest.FolderPath, ArchivedBatchSummaryFileName);
                if (TryCopyArchive(summaryFilePath, destination))
                    manifest.BatchSummaryFile = ArchivedBatchSummaryFileName;
            }

            manifest.CompletedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            RemoveArtifactErrors(manifest, "BatchChildren");
            if (status == SarSimulationRunStatus.Completed)
            {
                List<string> childProblems = ValidateBatchChildren(manifest);
                if (childProblems.Count > 0)
                {
                    RecordArtifactError(manifest, "BatchChildren", string.Join("; ", childProblems));
                    status = SarSimulationRunStatus.Failed;
                }
            }
            manifest.Status = status;
            manifest.Duration = ResolveDuration(manifest, duration);
            SaveManifest(manifest);
            AddOrReplace(manifest);
            activeBatch = null;
            pendingBatchId = null;
            pendingBatchIndex = 0;
            pendingBatchCount = 0;
            HistoryChanged?.Invoke();
            return manifest;
        }

        private List<string> ValidateBatchChildren(SarSimulationRunManifest batch)
        {
            return ValidateBatchChildren(batch, FindManifest);
        }

        public static List<string> FindBatchCompletionProblems(SarSimulationRunManifest batch,
            IEnumerable<SarSimulationRunManifest> candidateRuns)
        {
            Dictionary<string, SarSimulationRunManifest> byId =
                (candidateRuns ?? Enumerable.Empty<SarSimulationRunManifest>())
                .Where(run => run != null && !string.IsNullOrWhiteSpace(run.RunId))
                .GroupBy(run => run.RunId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            return ValidateBatchChildren(batch,
                id => byId.TryGetValue(id, out SarSimulationRunManifest run) ? run : null);
        }

        private static List<string> ValidateBatchChildren(SarSimulationRunManifest batch,
            Func<string, SarSimulationRunManifest> resolveChild)
        {
            var problems = new List<string>();
            List<string> childIds = (batch?.ChildRunIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal).ToList();
            int expected = Math.Max(0, batch?.BatchCount ?? 0);
            if (childIds.Count < expected)
                problems.Add($"expected {expected} member runs but only {childIds.Count} are registered");

            foreach (string childId in childIds)
            {
                SarSimulationRunManifest child = resolveChild?.Invoke(childId);
                if (child == null)
                {
                    problems.Add($"{childId} is missing");
                    continue;
                }
                if (child.Kind == SarSimulationRunKind.Batch)
                {
                    problems.Add($"{childId} is not a member run");
                    continue;
                }
                if (child.Status != SarSimulationRunStatus.Completed)
                    problems.Add($"{childId} is {child.Status}");
            }
            return problems;
        }

        public IReadOnlyList<SarSimulationRunManifest> LoadCurrentSceneManifests()
        {
            manifests.Clear();
            string historyRoot = HistoryRoot;
            if (Directory.Exists(historyRoot))
            {
                string[] paths;
                try
                {
                    paths = Directory.GetFiles(historyRoot, ManifestFileName, SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SarSimulationHistory] History folder could not be scanned: {ex.Message}");
                    paths = Array.Empty<string>();
                }
                foreach (string path in paths)
                {
                    try
                    {
                        if (IsPathInside(path, RecycleRoot)) continue;
                        SarSimulationRunManifest manifest = FromJson(File.ReadAllText(path));
                        if (manifest == null || string.IsNullOrWhiteSpace(manifest.RunId)) continue;
                        manifest.ManifestPath = Path.GetFullPath(path);
                        manifest.FolderPath = Path.GetDirectoryName(manifest.ManifestPath);
                        NormalizeManifestCollections(manifest);
                        if (RecoverAbandonedRun(manifest, activeRun?.RunId, activeBatch?.RunId))
                            SaveManifest(manifest);
                        manifests.Add(manifest);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[SarSimulationHistory] Ignored unreadable manifest '{path}': {ex.Message}");
                    }
                }
            }

            manifests.Sort((left, right) => CompareStartedDescending(left?.StartedUtc, right?.StartedUtc));
            HistoryChanged?.Invoke();
            return manifests;
        }

        public SarSimulationRunManifest FindManifest(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return null;
            SarSimulationRunManifest known = manifests.FirstOrDefault(item => item.RunId == runId);
            if (known != null) return known;
            string path = Path.Combine(HistoryRoot, SafeSegment(runId, string.Empty), ManifestFileName);
            if (!File.Exists(path)) return null;
            try
            {
                SarSimulationRunManifest loaded = FromJson(File.ReadAllText(path));
                if (loaded == null || loaded.RunId != runId) return null;
                loaded.ManifestPath = Path.GetFullPath(path);
                loaded.FolderPath = Path.GetDirectoryName(loaded.ManifestPath);
                NormalizeManifestCollections(loaded);
                AddOrReplace(loaded);
                return loaded;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SarSimulationHistory] Manifest '{runId}' could not be loaded: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Moves one standalone run, or a batch parent together with every registered
        /// member, into the current scene's history recycle folder. Nothing is erased.
        /// Batch members cannot be recycled independently because that would leave the
        /// parent summary/parameter grid internally inconsistent.
        /// </summary>
        public bool TryMoveToRecycleBin(string runId, out string message)
        {
            message = null;
            SarSimulationRunManifest selected = FindManifest(runId);
            if (selected == null)
            {
                message = "The selected history record no longer exists.";
                return false;
            }
            if (selected.Kind == SarSimulationRunKind.BatchMember)
            {
                message = "A batch member cannot be recycled independently. Select its batch parent to recycle the complete batch.";
                return false;
            }
            if (!string.Equals(selected.SceneId, CurrentSceneId, StringComparison.Ordinal))
            {
                message = "The selected history record does not belong to the active scene.";
                return false;
            }

            var targets = new List<SarSimulationRunManifest> { selected };
            if (selected.Kind == SarSimulationRunKind.Batch)
            {
                var childIds = new HashSet<string>(selected.ChildRunIds ?? new List<string>(),
                    StringComparer.Ordinal);
                // Also discover children by their back-reference so a stale parent list cannot
                // orphan member folders.  Treat any malformed back-reference as corruption and
                // stop before creating/moving anything.
                foreach (SarSimulationRunManifest child in manifests.Where(item => item != null &&
                             !string.Equals(item.RunId, selected.RunId, StringComparison.Ordinal) &&
                             string.Equals(item.BatchId, selected.RunId, StringComparison.Ordinal)))
                {
                    if (child.Kind != SarSimulationRunKind.BatchMember ||
                        !string.Equals(child.SceneId, CurrentSceneId, StringComparison.Ordinal))
                    {
                        message = "The batch membership metadata is inconsistent; no files were moved.";
                        return false;
                    }
                    childIds.Add(child.RunId);
                }
                foreach (string childId in childIds)
                {
                    SarSimulationRunManifest child = FindManifest(childId);
                    if (child == null || child.Kind != SarSimulationRunKind.BatchMember ||
                        !string.Equals(child.BatchId, selected.RunId, StringComparison.Ordinal) ||
                        !string.Equals(child.SceneId, CurrentSceneId, StringComparison.Ordinal))
                    {
                        message = "The batch membership metadata is incomplete or inconsistent; no files were moved.";
                        return false;
                    }
                    if (!targets.Any(item => item.RunId == child.RunId)) targets.Add(child);
                }
            }

            if (targets.Any(IsActiveOrRunning))
            {
                message = "An active or running simulation record cannot be recycled.";
                return false;
            }

            string historyRoot;
            string recycleRoot;
            try
            {
                historyRoot = Path.GetFullPath(HistoryRoot);
                recycleRoot = Path.GetFullPath(RecycleRoot);
            }
            catch (Exception ex)
            {
                message = "The scene history path is invalid: " + ex.Message;
                return false;
            }

            var sourceFolders = new List<Tuple<SarSimulationRunManifest, string>>();
            foreach (SarSimulationRunManifest target in targets)
            {
                if (!string.Equals(target.SceneId, CurrentSceneId, StringComparison.Ordinal))
                {
                    message = "The batch contains a member from another scene; no files were moved.";
                    return false;
                }
                string folder;
                string manifestPath;
                FileAttributes folderAttributes;
                try
                {
                    folder = Path.GetFullPath(target.FolderPath ?? string.Empty);
                    folderAttributes = Directory.Exists(folder)
                        ? File.GetAttributes(folder)
                        : 0;
                    manifestPath = Path.GetFullPath(target.ManifestPath ??
                        Path.Combine(folder, ManifestFileName));
                }
                catch (Exception ex)
                {
                    message = "A history-record path is invalid: " + ex.Message;
                    return false;
                }
                if (!IsDirectChildDirectory(folder, historyRoot) ||
                    IsPathInside(folder, recycleRoot) || !Directory.Exists(folder) ||
                    (folderAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    message = "A history-record folder is outside the active scene history root; no files were moved.";
                    return false;
                }
                if (!IsPathInside(manifestPath, folder) || !File.Exists(manifestPath))
                {
                    message = "A selected history manifest is missing or outside its record folder; no files were moved.";
                    return false;
                }
                sourceFolders.Add(Tuple.Create(target, folder));
            }

            string operationName = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) +
                                   "-" + SafeSegment(selected.RunId, "history") + "-" +
                                   Guid.NewGuid().ToString("N").Substring(0, 6);
            string operationRoot = Path.Combine(recycleRoot, operationName);
            var moved = new List<Tuple<string, string>>();
            try
            {
                Directory.CreateDirectory(operationRoot);
                File.WriteAllText(Path.Combine(operationRoot, "recycle-info.json"),
                    JsonConvert.SerializeObject(new
                    {
                        schemaVersion = 1,
                        recycledUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        sceneId = CurrentSceneId,
                        sceneName = CurrentSceneName,
                        selectedRunId = selected.RunId,
                        runIds = targets.Select(item => item.RunId).ToList()
                    }, Formatting.Indented));
                foreach (Tuple<SarSimulationRunManifest, string> source in sourceFolders)
                {
                    string destination = Path.Combine(operationRoot, Path.GetFileName(source.Item2));
                    if (Directory.Exists(destination) || File.Exists(destination))
                        throw new IOException("A recycle destination already exists.");
                    Directory.Move(source.Item2, destination);
                    moved.Add(Tuple.Create(source.Item2, destination));
                }
            }
            catch (Exception ex)
            {
                for (int index = moved.Count - 1; index >= 0; index--)
                {
                    Tuple<string, string> pair = moved[index];
                    try
                    {
                        if (Directory.Exists(pair.Item2) && !Directory.Exists(pair.Item1))
                            Directory.Move(pair.Item2, pair.Item1);
                    }
                    catch (Exception rollbackError)
                    {
                        Debug.LogError("[SarSimulationHistory] History recycle rollback failed: " +
                                       rollbackError.Message);
                    }
                }
                message = "The history record could not be moved to the recycle bin: " + ex.Message;
                return false;
            }

            var removedIds = new HashSet<string>(targets.Select(item => item.RunId), StringComparer.Ordinal);
            manifests.RemoveAll(item => item != null && removedIds.Contains(item.RunId));
            message = selected.Kind == SarSimulationRunKind.Batch
                ? $"The batch and {Math.Max(0, targets.Count - 1)} member run(s) were moved to {RecycleFolderName}."
                : $"The simulation record was moved to {RecycleFolderName}.";
            HistoryChanged?.Invoke();
            return true;
        }

        private bool IsActiveOrRunning(SarSimulationRunManifest manifest)
        {
            if (manifest == null) return true;
            return manifest.Status == SarSimulationRunStatus.Running ||
                   string.Equals(manifest.RunId, activeRun?.RunId, StringComparison.Ordinal) ||
                   string.Equals(manifest.RunId, activeBatch?.RunId, StringComparison.Ordinal) ||
                   (!string.IsNullOrWhiteSpace(pendingBatchId) &&
                    (string.Equals(manifest.RunId, pendingBatchId, StringComparison.Ordinal) ||
                     string.Equals(manifest.BatchId, pendingBatchId, StringComparison.Ordinal)));
        }

        public SimulationRecording LoadRecording(SarSimulationRunManifest manifest)
        {
            if (manifest == null) return null;
            return LoadRecordingFile(ResolveRecordingPath(manifest));
        }

        public string ResolveRecordingPath(SarSimulationRunManifest manifest)
        {
            if (manifest == null) return string.Empty;
            EnsureRuntimePaths(manifest);
            string path = ResolveRelativeFile(manifest, manifest.RecordingFile);
            if (File.Exists(path)) return path;
            string gzFallback = Path.Combine(manifest.FolderPath, ArchivedRecordingFileName);
            string jsonFallback = Path.Combine(manifest.FolderPath, "SimulationRecording.json");
            return File.Exists(gzFallback) ? gzFallback : File.Exists(jsonFallback) ? jsonFallback : string.Empty;
        }

        public string ResolveOccupancyPath(SarSimulationRunManifest manifest)
        {
            if (manifest == null) return string.Empty;
            EnsureRuntimePaths(manifest);
            string path = ResolveRelativeFile(manifest, manifest.OccupancyLogFile);
            if (File.Exists(path)) return path;
            string fallback = Path.Combine(manifest.FolderPath, ArchivedOccupancyFileName);
            return File.Exists(fallback) ? fallback : string.Empty;
        }

        public SarCsvTable LoadResultTable(SarSimulationRunManifest manifest)
        {
            if (manifest == null) return new SarCsvTable();
            EnsureRuntimePaths(manifest);
            bool batch = manifest.Kind == SarSimulationRunKind.Batch;
            string relative = batch ? manifest.BatchSummaryFile : manifest.MetricsTableFile;
            string path = ResolveRelativeFile(manifest, relative);
            if (!File.Exists(path))
            {
                string fallback = Path.Combine(manifest.FolderPath,
                    batch ? ArchivedBatchSummaryFileName : ArchivedMetricsFileName);
                path = File.Exists(fallback) ? fallback : string.Empty;
            }
            return LoadCsvTableFile(path);
        }

        /// <summary>
        /// Loads the generated lane-level network captured at run start.  Historical
        /// visualization uses this copy without mutating the currently open scene.
        /// </summary>
        public Network LoadFullNetworkSnapshot(SarSimulationRunManifest manifest)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.FullNetworkSnapshotFile))
                return null;
            try
            {
                EnsureRuntimePaths(manifest);
                string path = ResolveRelativeFile(manifest, manifest.FullNetworkSnapshotFile);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<Network>(File.ReadAllText(path), SnapshotJsonSettings);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SarSimulationHistory] Full-network snapshot could not be loaded: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Loads the ground-facility geometry captured with a historical run.  This data is
        /// kept separate from the active scene so a review never borrows approach/ramp
        /// geometry from a newer, potentially incompatible scene revision.
        /// </summary>
        public List<Vertiport> LoadVertiportsSnapshot(SarSimulationRunManifest manifest)
        {
            if (manifest == null) return null;
            try
            {
                EnsureRuntimePaths(manifest);
                string path = ResolveRelativeFile(manifest, manifest.VertiportSnapshotFile);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    string fallback = Path.Combine(manifest.FolderPath, VertiportSnapshotFileName);
                    path = File.Exists(fallback) ? fallback : string.Empty;
                }
                if (string.IsNullOrWhiteSpace(path)) return null;
                return JsonConvert.DeserializeObject<List<Vertiport>>(File.ReadAllText(path),
                           SnapshotJsonSettings) ?? new List<Vertiport>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SarSimulationHistory] Vertiport snapshot could not be loaded: {ex.Message}");
                return null;
            }
        }

        public static SimulationRecording LoadRecordingFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Stream content = file;
                    GZipStream gzip = null;
                    if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                    {
                        gzip = new GZipStream(file, CompressionMode.Decompress, false);
                        content = gzip;
                    }
                    try
                    {
                        using (var text = new StreamReader(content))
                        using (var json = new JsonTextReader(text))
                        {
                            var serializer = new JsonSerializer
                            {
                                NullValueHandling = NullValueHandling.Ignore,
                                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                            };
                            serializer.Converters.Add(new Vector3Converter());
                            return serializer.Deserialize<SimulationRecording>(json);
                        }
                    }
                    finally
                    {
                        if (gzip != null) gzip.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SarSimulationHistory] Recording '{path}' could not be loaded: {ex.Message}");
                return null;
            }
        }

        public static SarHistoricalVisualizationData LoadHistoricalVisualizationData(
            string recordingPath, string occupancyPath = null, int maximumPointsPerTrajectory = 320)
        {
            if (string.IsNullOrWhiteSpace(recordingPath) || !File.Exists(recordingPath))
                throw new FileNotFoundException("The historical recording is missing.", recordingPath);

            var result = new SarHistoricalVisualizationData();
            using (var file = new FileStream(recordingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Stream content = file;
                GZipStream gzip = null;
                if (recordingPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    gzip = new GZipStream(file, CompressionMode.Decompress, false);
                    content = gzip;
                }
                try
                {
                    using (var text = new StreamReader(content))
                    using (var json = new JsonTextReader(text))
                    {
                        var serializer = new JsonSerializer
                        {
                            NullValueHandling = NullValueHandling.Ignore,
                            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                        };
                        serializer.Converters.Add(new Vector3Converter());
                        ReadCompactRecording(json, serializer, result, maximumPointsPerTrajectory);
                    }
                }
                finally
                {
                    if (gzip != null) gzip.Dispose();
                }
            }

            if (!string.IsNullOrWhiteSpace(occupancyPath) && File.Exists(occupancyPath))
                result.ResourceReservationSeconds = LoadResourceReservationSeconds(occupancyPath);
            return result;
        }

        private static void ReadCompactRecording(JsonTextReader json, JsonSerializer serializer,
            SarHistoricalVisualizationData result, int maximumPointsPerTrajectory)
        {
            var trajectoryCandidates = new List<AircraftTrajectoryRecord>();
            var legacyCompletionTimes = new Dictionary<string, float>(StringComparer.Ordinal);
            if (!json.Read() || json.TokenType != JsonToken.StartObject)
                throw new JsonException("The recording root is not a JSON object.");
            while (json.Read())
            {
                if (json.TokenType == JsonToken.EndObject) break;
                if (json.TokenType != JsonToken.PropertyName) continue;
                string property = Convert.ToString(json.Value, CultureInfo.InvariantCulture);
                if (!json.Read()) break;
                if (string.Equals(property, "LaneTimeSeries", StringComparison.Ordinal) &&
                    json.TokenType == JsonToken.StartArray)
                {
                    while (json.Read() && json.TokenType != JsonToken.EndArray)
                    {
                        if (json.TokenType == JsonToken.Null) continue;
                        LaneTimeSeriesRecord lane = serializer.Deserialize<LaneTimeSeriesRecord>(json);
                        if (lane == null || string.IsNullOrWhiteSpace(lane.LaneId)) continue;
                        float count = CountLaneTraversals(lane.Snapshots);
                        if (!IsFinite(count) || count <= 0f) continue;
                        if (result.LaneTraversals.ContainsKey(lane.LaneId))
                            result.LaneTraversals[lane.LaneId] += count;
                        else
                            result.LaneTraversals[lane.LaneId] = count;
                    }
                }
                else if (string.Equals(property, "AircraftTrajectories", StringComparison.Ordinal) &&
                         json.TokenType == JsonToken.StartArray)
                {
                    while (json.Read() && json.TokenType != JsonToken.EndArray)
                    {
                        if (json.TokenType == JsonToken.Null) continue;
                        AircraftTrajectoryRecord trajectory = serializer.Deserialize<AircraftTrajectoryRecord>(json);
                        result.SourceTrajectoryCount++;
                        if (trajectory != null) trajectoryCandidates.Add(trajectory);
                    }
                }
                else if (string.Equals(property, "Events", StringComparison.Ordinal) &&
                         json.TokenType == JsonToken.StartArray)
                {
                    while (json.Read() && json.TokenType != JsonToken.EndArray)
                    {
                        if (json.TokenType == JsonToken.Null) continue;
                        SimulationEvent recordedEvent = serializer.Deserialize<SimulationEvent>(json);
                        if (recordedEvent == null || string.IsNullOrWhiteSpace(recordedEvent.AircraftId))
                            continue;
                        bool isSuccessfulCompletion =
                            string.Equals(recordedEvent.EventType, "MissionCompleted", StringComparison.Ordinal) ||
                            (string.Equals(recordedEvent.EventType, "PhaseChange", StringComparison.Ordinal) &&
                             (recordedEvent.Description ?? string.Empty)
                             .IndexOf("Mission SUCCESS", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (isSuccessfulCompletion)
                        {
                            if (!legacyCompletionTimes.TryGetValue(recordedEvent.AircraftId,
                                    out float previousTime) || recordedEvent.Time > previousTime)
                                legacyCompletionTimes[recordedEvent.AircraftId] = recordedEvent.Time;
                        }
                    }
                }
                else
                {
                    json.Skip();
                }
            }

            foreach (AircraftTrajectoryRecord trajectory in trajectoryCandidates)
            {
                bool completed = trajectory?.MissionCompleted == true ||
                                 (trajectory?.MissionCompleted == null &&
                                  !string.IsNullOrWhiteSpace(trajectory.AircraftId) &&
                                  legacyCompletionTimes.ContainsKey(trajectory.AircraftId));
                if (!completed)
                {
                    result.ExcludedIncompleteTrajectoryCount++;
                    continue;
                }
                IReadOnlyList<Vector3> completedPositions = trajectory.Positions;
                if (trajectory.MissionCompleted == null &&
                    legacyCompletionTimes.TryGetValue(trajectory.AircraftId, out float completedAt))
                {
                    if (!TryTrimLegacyTrajectoryAtCompletion(trajectory, completedAt,
                            out completedPositions))
                    {
                        // Never display an unbounded legacy tail: without aligned sample
                        // times there is no evidence that points after SUCCESS are absent.
                        result.ExcludedUnverifiableTrajectoryCount++;
                        continue;
                    }
                }
                List<Vector3> reduced = DownsampleTrajectory(completedPositions,
                    Math.Max(2, maximumPointsPerTrajectory));
                if (reduced.Count < 2) continue;
                result.Trajectories.Add(reduced);
                result.CompletedTrajectoryCount++;
                result.RetainedTrajectoryPointCount += reduced.Count;
            }
        }

        private static bool TryTrimLegacyTrajectoryAtCompletion(
            AircraftTrajectoryRecord trajectory, float completedAt,
            out IReadOnlyList<Vector3> positions)
        {
            if (trajectory?.Positions == null || trajectory.Times == null ||
                trajectory.Times.Count != trajectory.Positions.Count)
            {
                positions = Array.Empty<Vector3>();
                return false;
            }
            int retained = 0;
            while (retained < trajectory.Times.Count &&
                   trajectory.Times[retained] <= completedAt + 0.001f)
                retained++;
            positions = retained <= 0
                ? (IReadOnlyList<Vector3>)Array.Empty<Vector3>()
                : trajectory.Positions.Take(retained).ToList();
            return retained > 0;
        }

        public static float CountLaneTraversals(IEnumerable<LaneSnapshot> snapshots)
        {
            List<LaneSnapshot> ordered = (snapshots ?? Enumerable.Empty<LaneSnapshot>())
                .Where(item => item != null).OrderBy(item => item.Time).ToList();
            var previousIds = new HashSet<string>(StringComparer.Ordinal);
            int previousCount = 0;
            float entries = 0f;
            foreach (LaneSnapshot snapshot in ordered)
            {
                if (snapshot.AircraftIds != null)
                {
                    var currentIds = new HashSet<string>(snapshot.AircraftIds
                        .Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
                    entries += currentIds.Count(id => !previousIds.Contains(id));
                    previousIds = currentIds;
                    previousCount = currentIds.Count;
                }
                else
                {
                    int currentCount = Math.Max(0, snapshot.AircraftCount);
                    entries += Math.Max(0, currentCount - previousCount);
                    previousCount = currentCount;
                    previousIds.Clear();
                }
            }
            return entries;
        }

        private static List<Vector3> DownsampleTrajectory(IReadOnlyList<Vector3> source, int maximumPoints)
        {
            if (source == null || source.Count == 0) return new List<Vector3>();
            if (source.Count <= maximumPoints) return new List<Vector3>(source);
            var result = new List<Vector3>(maximumPoints);
            for (int index = 0; index < maximumPoints; index++)
            {
                int sourceIndex = (int)Math.Round(index * (source.Count - 1d) / (maximumPoints - 1d));
                result.Add(source[sourceIndex]);
            }
            return result;
        }

        public static Dictionary<string, float> LoadResourceReservationSeconds(string path)
        {
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;
            OccupancyExport export = JsonConvert.DeserializeObject<OccupancyExport>(File.ReadAllText(path));
            foreach (ResourceOccupancyLog resource in export?.Resources ?? new List<ResourceOccupancyLog>())
            {
                if (resource == null || string.IsNullOrWhiteSpace(resource.ResourceId)) continue;
                float total = 0f;
                foreach (AircraftOccupancy occupancy in resource.Occupancies ?? new List<AircraftOccupancy>())
                    foreach (TimeWindowEntry window in occupancy?.TimeWindows ?? new List<TimeWindowEntry>())
                        if (window != null && window.EndTime > window.StartTime)
                        {
                            float duration = window.EndTime - window.StartTime;
                            if (IsFinite(duration) && duration > 0f) total += duration;
                        }
                if (IsFinite(total) && total > 0f) result[resource.ResourceId] = total;
            }
            return result;
        }

        public static SarCsvTable LoadCsvTableFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new SarCsvTable();
            SarCsvTable table = ParseCsvTableText(File.ReadAllText(path));
            table.SourcePath = Path.GetFullPath(path);
            return table;
        }

        public static SarCsvTable ParseCsvTableText(string csv)
        {
            var table = new SarCsvTable();
            if (string.IsNullOrEmpty(csv)) return table;
            using (var reader = new StringReader(csv))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    List<string> cells = ParseCsvLine(line);
                    if (table.Rows.Count == 0 && cells.Count > 0)
                        cells[0] = (cells[0] ?? string.Empty).TrimStart('\ufeff');
                    table.Rows.Add(cells);
                    table.ColumnCount = Math.Max(table.ColumnCount, cells.Count);
                }
            }
            return table;
        }

        public static Dictionary<string, double> ParseMetricsTable(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new Dictionary<string, double>(StringComparer.Ordinal);
            try
            {
                return ParseMetricsTableText(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SarSimulationHistory] Metrics table '{path}' could not be read: {ex.Message}");
                return new Dictionary<string, double>(StringComparer.Ordinal);
            }
        }

        public static Dictionary<string, double> ParseMetricsTableText(string csv)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(csv)) return result;
            using (var reader = new StringReader(csv))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    List<string> cells = ParseCsvLine(line);
                    if (cells.Count < 2) continue;
                    string key = (cells[0] ?? string.Empty).Trim().TrimStart('\ufeff');
                    string value = (cells[1] ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(key) || string.Equals(key, "Metrics", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands,
                            CultureInfo.InvariantCulture, out double number) ||
                        double.IsNaN(number) || double.IsInfinity(number))
                        continue;
                    result[key] = number;
                }
            }
            return result;
        }

        public static string ToJson(SarSimulationRunManifest manifest)
        {
            return JsonConvert.SerializeObject(manifest, ManifestJsonSettings);
        }

        public static SarSimulationRunManifest FromJson(string json)
        {
            SarSimulationRunManifest manifest = JsonConvert.DeserializeObject<SarSimulationRunManifest>(json,
                ManifestJsonSettings);
            NormalizeManifestCollections(manifest);
            return manifest;
        }

        /// <summary>
        /// Verifies that an Output artifact belongs to this run instead of being an older file
        /// left by a previous process. A two-second tolerance accommodates coarse file-system clocks.
        /// </summary>
        public static bool TryValidateArtifactSource(string sourcePath, string startedUtc, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "the source file is missing";
                return false;
            }

            if (!DateTime.TryParse(startedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                    out DateTime started))
            {
                error = "the run start timestamp is invalid, so artifact freshness cannot be verified";
                return false;
            }

            try
            {
                var source = new FileInfo(sourcePath);
                source.Refresh();
                if (source.Length <= 0L)
                {
                    error = "the source file is empty";
                    return false;
                }

                DateTime threshold = started.ToUniversalTime().Subtract(ArtifactTimestampTolerance);
                if (source.LastWriteTimeUtc < threshold)
                {
                    error = "the source file is stale (last write " +
                            source.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture) +
                            " predates this run)";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "the source file could not be inspected: " + ex.Message;
                return false;
            }
        }

        public static SarSimulationRunStatus ResolveFinalRunStatus(SarSimulationRunStatus requested,
            bool metricsArchived, bool recordingArchived)
        {
            return requested == SarSimulationRunStatus.Completed && (!metricsArchived || !recordingArchived)
                ? SarSimulationRunStatus.Failed
                : requested;
        }

        /// <summary>
        /// Converts a persisted Running record into Interrupted unless it is the service's
        /// currently active run or batch. Returns true when the manifest was changed.
        /// </summary>
        public static bool RecoverAbandonedRun(SarSimulationRunManifest manifest, string activeRunId,
            string activeBatchId, string completedUtc = null)
        {
            if (manifest == null || manifest.Status != SarSimulationRunStatus.Running) return false;
            if ((!string.IsNullOrWhiteSpace(activeRunId) && manifest.RunId == activeRunId) ||
                (!string.IsNullOrWhiteSpace(activeBatchId) && manifest.RunId == activeBatchId))
                return false;

            DateTime completed;
            if (!DateTime.TryParse(completedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                    out completed))
                completed = DateTime.UtcNow;
            completed = completed.ToUniversalTime();
            manifest.CompletedUtc = completed.ToString("o", CultureInfo.InvariantCulture);
            if ((!IsFinite(manifest.Duration) || manifest.Duration <= 0d) &&
                DateTime.TryParse(manifest.StartedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime started))
                manifest.Duration = Math.Max(0d, (completed - started.ToUniversalTime()).TotalSeconds);
            manifest.Status = SarSimulationRunStatus.Interrupted;
            return true;
        }

        /// <summary>
        /// Produces a deterministic SHA-256 over canonical JSON for the in-memory axis network
        /// and vertiports. An empty value means the current topology is unavailable.
        /// </summary>
        public static string ComputeTopologyFingerprint(Network axisNetwork,
            IEnumerable<Vertiport> vertiports)
        {
            if (axisNetwork == null || vertiports == null) return string.Empty;
            try
            {
                string axisJson = SerializeSnapshot(axisNetwork, Formatting.None);
                string vertiportJson = SerializeSnapshot(vertiports.ToList(), Formatting.None);
                return ComputeTopologyFingerprintFromJson(axisJson, vertiportJson);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SarSimulationHistory] Topology fingerprint could not be computed: {ex.Message}");
                return string.Empty;
            }
        }

        public static bool TryCompressRecording(string sourcePath, string destinationPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "the source file is missing";
                return false;
            }
            string temporary = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                string folder = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var gzip = new GZipStream(target, System.IO.Compression.CompressionLevel.Optimal, false))
                    source.CopyTo(gzip, 1024 * 1024);
                ReplaceArchiveFile(temporary, destinationPath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                TryDelete(temporary);
                return false;
            }
        }

        private void CaptureTopologyArtifacts(SarSimulationRunManifest manifest)
        {
            if (manifest == null) return;
            NormalizeManifestCollections(manifest);
            Network axisNetwork = manager?.networkManager?.AxisNetwork;
            Network fullNetwork = manager?.networkManager?.FullNetwork;
            IEnumerable<Vertiport> vertiports = manager?.vertiportManager?.Vertiports;
            float liveTangentRatio = manager?.vertiportManager != null
                ? manager.vertiportManager.corridorTangentRatio
                : SarSimulationRunManifest.DefaultCorridorTangentRatio;
            manifest.CorridorTangentRatio = float.IsNaN(liveTangentRatio) ||
                                            float.IsInfinity(liveTangentRatio)
                ? SarSimulationRunManifest.DefaultCorridorTangentRatio
                : liveTangentRatio;
            RemoveArtifactErrors(manifest, AxisNetworkSnapshotFileName);
            RemoveArtifactErrors(manifest, FullNetworkSnapshotFileName);
            RemoveArtifactErrors(manifest, VertiportSnapshotFileName);
            manifest.AxisNetworkSnapshotFile = null;
            manifest.FullNetworkSnapshotFile = null;
            manifest.VertiportSnapshotFile = null;
            manifest.TopologyFingerprint = null;

            if (axisNetwork == null)
            {
                RecordArtifactError(manifest, AxisNetworkSnapshotFileName,
                    "the in-memory axis network is unavailable");
                return;
            }
            if (vertiports == null)
            {
                RecordArtifactError(manifest, VertiportSnapshotFileName,
                    "the in-memory vertiport collection is unavailable");
                return;
            }

            try
            {
                List<Vertiport> vertiportList = vertiports.ToList();
                string axisCompact = SerializeSnapshot(axisNetwork, Formatting.None);
                string fullCompact = fullNetwork != null
                    ? SerializeSnapshot(fullNetwork, Formatting.None)
                    : null;
                string vertiportCompact = SerializeSnapshot(vertiportList, Formatting.None);
                string axisDestination = Path.Combine(manifest.FolderPath, AxisNetworkSnapshotFileName);
                string fullDestination = Path.Combine(manifest.FolderPath, FullNetworkSnapshotFileName);
                string vertiportDestination = Path.Combine(manifest.FolderPath, VertiportSnapshotFileName);

                bool axisSaved = TryWriteSnapshot(axisDestination,
                    JToken.Parse(axisCompact).ToString(Formatting.Indented), out string axisError);
                bool vertiportsSaved = TryWriteSnapshot(vertiportDestination,
                    JToken.Parse(vertiportCompact).ToString(Formatting.Indented), out string vertiportError);
                string fullError = null;
                bool fullSaved = fullCompact != null && TryWriteSnapshot(fullDestination,
                    JToken.Parse(fullCompact).ToString(Formatting.Indented), out fullError);
                if (axisSaved) manifest.AxisNetworkSnapshotFile = AxisNetworkSnapshotFileName;
                else RecordArtifactError(manifest, AxisNetworkSnapshotFileName, axisError);
                if (fullSaved) manifest.FullNetworkSnapshotFile = FullNetworkSnapshotFileName;
                else RecordArtifactError(manifest, FullNetworkSnapshotFileName,
                    fullNetwork == null ? "the generated lane-level network is unavailable" : fullError);
                if (vertiportsSaved) manifest.VertiportSnapshotFile = VertiportSnapshotFileName;
                else RecordArtifactError(manifest, VertiportSnapshotFileName, vertiportError);
                if (axisSaved && vertiportsSaved)
                    manifest.TopologyFingerprint = ComputeTopologyFingerprintFromJson(
                        axisCompact, vertiportCompact);
            }
            catch (Exception ex)
            {
                RecordArtifactError(manifest, "TopologySnapshot", ex.Message);
            }
        }

        private SarSimulationRunManifest NewManifest(SceneIdentity scene, SarSimulationRunKind kind, string prefix)
        {
            return new SarSimulationRunManifest
            {
                RunId = prefix + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) +
                        "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
                SceneId = scene.Id,
                SceneName = scene.Name,
                Kind = kind,
                StartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Status = SarSimulationRunStatus.Running
            };
        }

        public static Dictionary<string, string> CaptureParameters(SimulationConfig config)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (config == null) return result;
            foreach (SimulationParameterDescriptor descriptor in SimulationParameterCatalog.All)
            {
                object value = SimulationParameterCatalog.ReadValue(config, descriptor.FieldName);
                if (value is float single)
                    result[descriptor.FieldName] = single.ToString("R", CultureInfo.InvariantCulture);
                else if (value is double precision)
                    result[descriptor.FieldName] = precision.ToString("R", CultureInfo.InvariantCulture);
                else
                    result[descriptor.FieldName] = Convert.ToString(value, CultureInfo.InvariantCulture) ??
                                                   string.Empty;
            }
            return result;
        }

        private static void MergeParameters(Dictionary<string, string> target,
            IDictionary<string, string> additional)
        {
            if (additional == null) return;
            foreach (KeyValuePair<string, string> pair in additional)
                if (!string.IsNullOrWhiteSpace(pair.Key)) target[pair.Key] = pair.Value ?? string.Empty;
        }

        private void CreateManifestFolder(SarSimulationRunManifest manifest)
        {
            string historyRoot = HistoryRootFor(manifest);
            Directory.CreateDirectory(historyRoot);
            manifest.FolderPath = Path.Combine(historyRoot, SafeSegment(manifest.RunId, "run"));
            Directory.CreateDirectory(manifest.FolderPath);
            manifest.ManifestPath = Path.Combine(manifest.FolderPath, ManifestFileName);
        }

        private void EnsureRuntimePaths(SarSimulationRunManifest manifest)
        {
            if (string.IsNullOrWhiteSpace(manifest.FolderPath))
                manifest.FolderPath = Path.Combine(HistoryRootFor(manifest), SafeSegment(manifest.RunId, "run"));
            if (string.IsNullOrWhiteSpace(manifest.ManifestPath))
                manifest.ManifestPath = Path.Combine(manifest.FolderPath, ManifestFileName);
            Directory.CreateDirectory(manifest.FolderPath);
        }

        private string HistoryRootFor(SarSimulationRunManifest manifest)
        {
            string id = SafeSegment(manifest?.SceneId, CurrentSceneId);
            string root = workspace != null ? workspace.WorkspaceRoot : SarRuntimePaths.SceneRoot;
            return Path.Combine(root, id, HistoryFolderName);
        }

        private static void SaveManifest(SarSimulationRunManifest manifest)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.ManifestPath)) return;
            string temporary = manifest.ManifestPath + ".tmp";
            try
            {
                Directory.CreateDirectory(manifest.FolderPath);
                File.WriteAllText(temporary, ToJson(manifest));
                ReplaceArchiveFile(temporary, manifest.ManifestPath);
            }
            catch (Exception ex)
            {
                TryDelete(temporary);
                Debug.LogWarning($"[SarSimulationHistory] Manifest could not be saved: {ex.Message}");
            }
        }

        private static bool TryCopyArchive(string sourcePath, string destinationPath)
        {
            return TryCopyArchive(sourcePath, destinationPath, out _);
        }

        private static bool TryCopyArchive(string sourcePath, string destinationPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "the source file is missing";
                return false;
            }
            string temporary = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                string folder = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.Copy(sourcePath, temporary, false);
                ReplaceArchiveFile(temporary, destinationPath);
                return true;
            }
            catch (Exception ex)
            {
                TryDelete(temporary);
                error = ex.Message;
                Debug.LogWarning($"[SarSimulationHistory] '{sourcePath}' could not be archived: {ex.Message}");
                return false;
            }
        }

        private static bool TryWriteSnapshot(string destinationPath, string json, out string error)
        {
            error = null;
            string temporary = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                string folder = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(temporary, json ?? string.Empty, new UTF8Encoding(false));
                ReplaceArchiveFile(temporary, destinationPath);
                return true;
            }
            catch (Exception ex)
            {
                TryDelete(temporary);
                error = ex.Message;
                return false;
            }
        }

        private static string SerializeSnapshot(object value, Formatting formatting)
        {
            JsonSerializer serializer = JsonSerializer.Create(SnapshotJsonSettings);
            JToken token = CanonicalizeToken(JToken.FromObject(value, serializer));
            return token.ToString(formatting);
        }

        private static JToken CanonicalizeToken(JToken token)
        {
            if (token is JObject obj)
            {
                return new JObject(obj.Properties()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => new JProperty(property.Name,
                        CanonicalizeToken(property.Value))));
            }
            if (token is JArray array)
                return new JArray(array.Select(CanonicalizeToken));
            return token?.DeepClone() ?? JValue.CreateNull();
        }

        private static string ComputeTopologyFingerprintFromJson(string axisJson, string vertiportJson)
        {
            string payload = "axis-network\n" + axisJson + "\nvertiports\n" + vertiportJson;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
                return BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void RecordArtifactError(SarSimulationRunManifest manifest, string artifact,
            string reason)
        {
            if (manifest == null) return;
            NormalizeManifestCollections(manifest);
            string message = (string.IsNullOrWhiteSpace(artifact) ? "Artifact" : artifact.Trim()) + ": " +
                             (string.IsNullOrWhiteSpace(reason) ? "unknown error" : reason.Trim());
            if (!manifest.ArtifactErrors.Contains(message)) manifest.ArtifactErrors.Add(message);
        }

        private static void RemoveArtifactErrors(SarSimulationRunManifest manifest, string artifact)
        {
            if (manifest?.ArtifactErrors == null || string.IsNullOrWhiteSpace(artifact)) return;
            string prefix = artifact.Trim() + ":";
            manifest.ArtifactErrors.RemoveAll(item =>
                item != null && item.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static void ReplaceArchiveFile(string temporary, string destination)
        {
            if (!File.Exists(destination))
            {
                File.Move(temporary, destination);
                return;
            }

            try
            {
                File.Replace(temporary, destination, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(temporary, destination, true);
                TryDelete(temporary);
            }
            catch (IOException)
            {
                File.Copy(temporary, destination, true);
                TryDelete(temporary);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // A failed cleanup is harmless; the source Output file is never touched.
            }
        }

        private static double ResolveDuration(SarSimulationRunManifest manifest, double requested)
        {
            if (IsFinite(requested) && requested >= 0d) return requested;
            if (manifest.Metrics != null &&
                manifest.Metrics.TryGetValue("Simulation Duration (s)", out double measured) &&
                IsFinite(measured) && measured >= 0d)
                return measured;
            if (DateTime.TryParse(manifest.StartedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime started))
                return Math.Max(0d, (DateTime.UtcNow - started.ToUniversalTime()).TotalSeconds);
            return 0d;
        }

        private string ResolveRelativeFile(SarSimulationRunManifest manifest, string relativeFile)
        {
            if (string.IsNullOrWhiteSpace(relativeFile)) return string.Empty;
            string folder = Path.GetFullPath(manifest.FolderPath);
            string candidate = Path.GetFullPath(Path.Combine(folder, relativeFile));
            string prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : string.Empty;
        }

        private void AddOrReplace(SarSimulationRunManifest manifest)
        {
            int index = manifests.FindIndex(item => item.RunId == manifest.RunId);
            if (index >= 0) manifests[index] = manifest;
            else manifests.Add(manifest);
            manifests.Sort((left, right) => CompareStartedDescending(left?.StartedUtc, right?.StartedUtc));
        }

        private static int CompareStartedDescending(string left, string right)
        {
            DateTime.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime leftDate);
            DateTime.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime rightDate);
            return rightDate.CompareTo(leftDate);
        }

        private SceneIdentity ResolveSceneIdentity()
        {
            SarSceneProfile profile = workspace != null ? workspace.ActiveProfile : null;
            if (profile != null && !string.IsNullOrWhiteSpace(profile.Id))
            {
                string id = SafeSegment(profile.Id, "default");
                return new SceneIdentity(id,
                    string.IsNullOrWhiteSpace(profile.DisplayName) ? id : profile.DisplayName,
                    Path.Combine(workspace.WorkspaceRoot, id));
            }

            string sceneRoot = SarRuntimePaths.SceneRoot;
            string registryPath = Path.Combine(sceneRoot, "scenes.json");
            try
            {
                if (File.Exists(registryPath))
                {
                    SarSceneRegistry saved = JsonConvert.DeserializeObject<SarSceneRegistry>(
                        File.ReadAllText(registryPath), new Vector3Converter());
                    string id = SafeSegment(saved?.ActiveSceneId, string.Empty);
                    if (!string.IsNullOrEmpty(id))
                    {
                        SarSceneProfile savedProfile = saved.Scenes?.FirstOrDefault(item => item?.Id == id);
                        string name = string.IsNullOrWhiteSpace(savedProfile?.DisplayName) ? id : savedProfile.DisplayName;
                        return new SceneIdentity(id, name, Path.Combine(sceneRoot, id));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SarSimulationHistory] Active scene registry could not be read: {ex.Message}");
            }

            return new SceneIdentity("default", "Default Scene", Path.Combine(sceneRoot, "default"));
        }

        private static string SafeSegment(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            string trimmed = value.Trim();
            if (trimmed == "." || trimmed == ".." || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                trimmed.Contains("/") || trimmed.Contains("\\"))
                return fallback;
            return trimmed;
        }

        private static bool IsPathInside(string candidatePath, string parentDirectory)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(parentDirectory))
                return false;
            try
            {
                string candidate = Path.GetFullPath(candidatePath);
                string parent = Path.GetFullPath(parentDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
                return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool IsDirectChildDirectory(string candidateDirectory, string parentDirectory)
        {
            if (!IsPathInside(candidateDirectory, parentDirectory)) return false;
            try
            {
                string candidateParent = Path.GetDirectoryName(Path.GetFullPath(candidateDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string expected = Path.GetFullPath(parentDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(candidateParent, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void NormalizeManifestCollections(SarSimulationRunManifest manifest)
        {
            if (manifest == null) return;
            manifest.Parameters = manifest.Parameters == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(manifest.Parameters, StringComparer.Ordinal);
            manifest.Metrics = manifest.Metrics == null
                ? new Dictionary<string, double>(StringComparer.Ordinal)
                : new Dictionary<string, double>(manifest.Metrics, StringComparer.Ordinal);
            if (manifest.ChildRunIds == null) manifest.ChildRunIds = new List<string>();
            if (manifest.ArtifactErrors == null) manifest.ArtifactErrors = new List<string>();
        }

        private static List<string> ParseCsvLine(string line)
        {
            var values = new List<string>();
            if (line == null) return values;
            var current = new System.Text.StringBuilder();
            bool quoted = false;
            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (character == '"')
                {
                    if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        current.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else if (character == ',' && !quoted)
                {
                    values.Add(current.ToString());
                    current.Length = 0;
                }
                else
                {
                    current.Append(character);
                }
            }
            values.Add(current.ToString());
            return values;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private readonly struct SceneIdentity
        {
            public readonly string Id;
            public readonly string Name;
            public readonly string Folder;

            public SceneIdentity(string id, string name, string folder)
            {
                Id = id;
                Name = name;
                Folder = folder;
            }
        }
    }

    public sealed class SarHistoryAnalysisPlan
    {
        public List<string> CommonMetrics = new List<string>();
        public List<string> NumericVaryingParameters = new List<string>();
        public bool HasDurationMismatch;
    }

    public sealed class SarHistoryBarPoint
    {
        public string RunId;
        public string Label;
        public double Value;
        public bool IsPerSecond;
    }

    public sealed class SarHistorySensitivityPoint
    {
        public double FactorValue;
        public double MeanValue;
        public int SampleCount;
        public bool IsPerSecond;
    }

    /// <summary>
    /// Pure comparison planner used by both runtime UI and edit-mode tests. Batch manifests
    /// intentionally remain opaque here: the UI expands ChildRunIds before asking for a plan.
    /// </summary>
    public static class SarHistoryAnalysisPlanner
    {
        public static SarHistoryAnalysisPlan CreatePlan(IEnumerable<SarSimulationRunManifest> selectedRuns)
        {
            List<SarSimulationRunManifest> runs = UsableRuns(selectedRuns);
            var plan = new SarHistoryAnalysisPlan();
            if (runs.Count == 0) return plan;

            IEnumerable<string> common = runs[0].Metrics?.Keys ?? Enumerable.Empty<string>();
            for (int index = 1; index < runs.Count; index++)
                common = common.Intersect(runs[index].Metrics?.Keys ?? Enumerable.Empty<string>(),
                    StringComparer.Ordinal);
            plan.CommonMetrics = common
                .Where(metric => runs.All(run => IsFiniteMetric(run, metric)))
                .OrderBy(metric => metric, StringComparer.Ordinal).ToList();

            IEnumerable<string> commonParameters = runs[0].Parameters?.Keys ?? Enumerable.Empty<string>();
            for (int index = 1; index < runs.Count; index++)
                commonParameters = commonParameters.Intersect(
                    runs[index].Parameters?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (string parameter in commonParameters.OrderBy(item => item, StringComparer.Ordinal))
            {
                var values = new List<double>();
                bool numeric = true;
                foreach (SarSimulationRunManifest run in runs)
                {
                    if (run.Parameters == null ||
                        !run.Parameters.TryGetValue(parameter, out string text) ||
                        !TryParseFinite(text, out double value))
                    {
                        numeric = false;
                        break;
                    }
                    values.Add(value);
                }
                if (numeric && values.Distinct().Count() > 1)
                    plan.NumericVaryingParameters.Add(parameter);
            }

            plan.HasDurationMismatch = DurationsDiffer(runs);
            return plan;
        }

        public static List<SarHistoryBarPoint> BuildBarData(
            IEnumerable<SarSimulationRunManifest> selectedRuns, string metric)
        {
            List<SarSimulationRunManifest> runs = UsableRuns(selectedRuns);
            bool normalize = DurationsDiffer(runs) && IsCumulativeMetric(metric);
            var result = new List<SarHistoryBarPoint>();
            foreach (SarSimulationRunManifest run in runs)
            {
                if (!IsFiniteMetric(run, metric)) continue;
                if (normalize && (!IsFinite(run.Duration) || run.Duration <= 0d)) continue;
                result.Add(new SarHistoryBarPoint
                {
                    RunId = run.RunId,
                    Label = string.IsNullOrWhiteSpace(run.SceneName) ? run.RunId : run.SceneName + " / " + run.RunId,
                    Value = normalize ? run.Metrics[metric] / run.Duration : run.Metrics[metric],
                    IsPerSecond = normalize
                });
            }
            return result;
        }

        public static List<SarHistorySensitivityPoint> BuildSensitivityData(
            IEnumerable<SarSimulationRunManifest> selectedRuns, string factor, string metric)
        {
            List<SarSimulationRunManifest> runs = UsableRuns(selectedRuns);
            bool normalize = DurationsDiffer(runs) && IsCumulativeMetric(metric);
            var samples = new List<Tuple<double, double>>();
            foreach (SarSimulationRunManifest run in runs)
            {
                if (run.Parameters == null ||
                    !run.Parameters.TryGetValue(factor ?? string.Empty, out string text) ||
                    !TryParseFinite(text, out double factorValue) || !IsFiniteMetric(run, metric))
                    continue;
                if (normalize && (!IsFinite(run.Duration) || run.Duration <= 0d)) continue;
                double metricValue = normalize ? run.Metrics[metric] / run.Duration : run.Metrics[metric];
                samples.Add(Tuple.Create(factorValue, metricValue));
            }

            return samples.GroupBy(sample => sample.Item1)
                .OrderBy(group => group.Key)
                .Select(group => new SarHistorySensitivityPoint
                {
                    FactorValue = group.Key,
                    MeanValue = group.Average(sample => sample.Item2),
                    SampleCount = group.Count(),
                    IsPerSecond = normalize
                }).ToList();
        }

        public static bool IsCumulativeMetric(string metric)
        {
            if (string.IsNullOrWhiteSpace(metric)) return false;
            string value = metric.ToLowerInvariant();
            string[] notCounts =
            {
                "average", " avg", "avg ", "mean", "rate", "ratio",
                "duration", "per second", "/s", "variance", "proportion", "percent", "%"
            };
            if (notCounts.Any(value.Contains)) return false;
            string[] cumulative =
            {
                "total", "count", "collision", "booking", "sample", "successful", "failed",
                "flight", "mission", "throughput"
            };
            if (cumulative.Any(value.Contains)) return true;
            if (value.Contains("utilization") || value.Contains("occupancy")) return false;
            return false;
        }

        private static List<SarSimulationRunManifest> UsableRuns(
            IEnumerable<SarSimulationRunManifest> selectedRuns)
        {
            return (selectedRuns ?? Enumerable.Empty<SarSimulationRunManifest>())
                .Where(run => run != null && run.Kind != SarSimulationRunKind.Batch &&
                              run.Status == SarSimulationRunStatus.Completed).ToList();
        }

        private static bool DurationsDiffer(IReadOnlyList<SarSimulationRunManifest> runs)
        {
            if (runs.Count < 2) return false;
            List<double> durations = runs.Select(run => run.Duration)
                .Where(duration => IsFinite(duration) && duration > 0d).ToList();
            if (durations.Count == 0) return false;
            if (durations.Count != runs.Count) return true;
            double minimum = durations.Min();
            double maximum = durations.Max();
            // Frame-boundary overshoot of a few tenths of a second is not a different
            // experimental horizon. Normalize only when durations differ materially.
            return maximum - minimum > Math.Max(1d, maximum * 0.01d);
        }

        private static bool IsFiniteMetric(SarSimulationRunManifest run, string metric)
        {
            return run.Metrics != null && !string.IsNullOrWhiteSpace(metric) &&
                   run.Metrics.TryGetValue(metric, out double value) && IsFinite(value);
        }

        private static bool TryParseFinite(string text, out double value)
        {
            return double.TryParse(text?.Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
                       CultureInfo.InvariantCulture, out value) &&
                   IsFinite(value);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
