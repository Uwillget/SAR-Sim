using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace RoadNetworkEditor.RuntimeUI
{
    public sealed class SarSensitivityRenderResult
    {
        [JsonProperty("success")] public bool Success;
        [JsonProperty("message")] public string Message;
        [JsonProperty("error")] public string Error;
        [JsonProperty("caption")] public string Caption;
        [JsonProperty("warnings")] public List<string> Warnings = new List<string>();
        [JsonProperty("output_path")] public string OutputPath;
        [JsonProperty("page_manifest_version")] public int PageManifestVersion;
        [JsonProperty("page_output_schema")] public string PageOutputSchema;
        [JsonProperty("page_count")] public int PageCount;
        [JsonProperty("network_page_count")] public int NetworkPageCount;
        [JsonProperty("vertiport_page_count")] public int VertiportPageCount;
        [JsonProperty("pages")] public List<SarRenderedImagePage> Pages = new List<SarRenderedImagePage>();
    }

    [Serializable]
    public sealed class SarRenderedImagePage
    {
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("page_id")] public string PageId;
        [JsonProperty("page_number")] public int PageNumber;
        [JsonProperty("page_count")] public int PageCount;
        [JsonProperty("page_type")] public string PageType;
        [JsonProperty("title")] public string Title;
        [JsonProperty("output_path")] public string OutputPath;
        [JsonProperty("vertiport_id")] public string VertiportId;
        [JsonProperty("display_name")] public string DisplayName;
        [JsonProperty("layer_id")] public string LayerId;
        [JsonProperty("visible_layer_ids")] public List<string> VisibleLayerIds = new List<string>();
        [JsonProperty("lane_count")] public int LaneCount;
        [JsonProperty("resource_count")] public int ResourceCount;
        [JsonProperty("suggested_columns")] public int SuggestedColumns = 1;
    }

    /// <summary>
    /// Stable request-kind identifiers shared with the packaged Python worker.
    /// Run comparison deliberately has no selected_factors field: its categorical
    /// X axis is the ordered list of selected simulation records.
    /// </summary>
    public static class SarPythonPlotKinds
    {
        public const string Sensitivity = "tested-grid-empirical-response";
        public const string RunComparison = "run-metric-bar-comparison";
        public const string TrafficHeatmap = "traffic-heatmap";
    }

    [Serializable]
    public sealed class SarPythonPlotRunDto
    {
        [JsonProperty("run_id")] public string RunId;
        [JsonProperty("label")] public string Label;
        [JsonProperty("scene_name")] public string SceneName;
        [JsonProperty("run_kind")] public string RunKind;
        [JsonProperty("batch_id")] public string BatchId;
        [JsonProperty("batch_index")] public int BatchIndex;
        [JsonProperty("batch_count")] public int BatchCount;
        [JsonProperty("duration")] public double Duration;
        [JsonProperty("parameters")] public Dictionary<string, string> Parameters =
            new Dictionary<string, string>(StringComparer.Ordinal);
        [JsonProperty("metrics")] public Dictionary<string, double> Metrics =
            new Dictionary<string, double>(StringComparer.Ordinal);
    }

    [Serializable]
    public sealed class SarPythonPlotRequestDto
    {
        [JsonProperty("schema_version")] public int SchemaVersion = 1;
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("title")] public string Title;
        [JsonProperty("selected_factors", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> SelectedFactors;
        [JsonProperty("selected_metrics")] public List<string> SelectedMetrics = new List<string>();
        [JsonProperty("all_varying_factors", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> AllVaryingFactors;
        [JsonProperty("runs")] public List<SarPythonPlotRunDto> Runs = new List<SarPythonPlotRunDto>();
    }

    /// <summary>
    /// Immutable analysis snapshot consumed by the headless traffic-heatmap renderer.
    /// The renderer reads authored lane centerlines and deliberately ignores
    /// LaneConnectionCorridors. An empty VisibleLayerIds list intentionally hides
    /// all road layers while retaining the vertiport/resource panel.
    /// </summary>
    [Serializable]
    public sealed class SarPythonTrafficHeatmapRequestDto
    {
        [JsonProperty("schema_version")] public int SchemaVersion = 1;
        [JsonProperty("kind")] public string Kind = SarPythonPlotKinds.TrafficHeatmap;
        [JsonProperty("title")] public string Title;
        [JsonProperty("visible_layer_ids")] public List<string> VisibleLayerIds = new List<string>();
        [JsonProperty("network")] public Network Network;
        [JsonProperty("vertiports")] public List<Vertiport> Vertiports = new List<Vertiport>();
        [JsonProperty("lane_traversals")] public Dictionary<string, float> LaneTraversals =
            new Dictionary<string, float>(StringComparer.Ordinal);
        [JsonProperty("resource_reservation_seconds")] public Dictionary<string, float> ResourceReservationSeconds =
            new Dictionary<string, float>(StringComparer.Ordinal);
        [JsonProperty("corridor_tangent_ratio")]
        public float CorridorTangentRatio = SarSimulationRunManifest.DefaultCorridorTangentRatio;
        [JsonProperty("dpi")] public int Dpi = 300;
    }

    internal sealed class SarPlotWorkerManifest
    {
        public int SchemaVersion = 1;
        public string Version;
        public string ArchiveFile;
        public string Sha256;
        public string SourceSha256;
        public string TrafficHeatmapSourceSha256;
        public string ExecutableRelativePath;
    }

    /// <summary>
    /// Runs a fixed, headless Pandas/NumPy/Matplotlib worker. Unity sends canonical manifest
    /// records and only displays the resulting PNG; arbitrary user executables are never run.
    /// </summary>
    public static class SarPythonAnalysisRenderer
    {
        private const int TimeoutMilliseconds = 180000;
        private const string AnalyticsFolder = "SARAnalytics";
        private static readonly object WorkerExtractionLock = new object();
        private static readonly JsonSerializerSettings PlotJsonSettings = new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Converters = new List<JsonConverter> { new Vector3Converter() }
        };

        public static async Task<SarSensitivityRenderResult> RenderSensitivityAsync(
            IReadOnlyList<SarSimulationRunManifest> runs,
            IReadOnlyList<string> factors,
            IReadOnlyList<string> metrics,
            string outputDirectory,
            string title)
        {
            List<SarSimulationRunManifest> usableRuns = UsableRuns(runs);
            if (usableRuns.Count == 0)
                return Failure("No completed run is available for sensitivity plotting.");
            List<string> selectedFactors = DistinctNames(factors);
            if (selectedFactors.Count < 1 || selectedFactors.Count > 3)
                return Failure("Select between one and three input parameters (X).");
            List<string> selectedMetrics = DistinctNames(metrics);
            if (selectedMetrics.Count == 0)
                return Failure("Select at least one output metric (Y).");
            if (selectedMetrics.Count > 12)
                return Failure("Select at most 12 output metrics (Y) for one figure.");
            string missingMetric = selectedMetrics.FirstOrDefault(metric =>
                usableRuns.Any(run => !HasFiniteMetric(run, metric)));
            if (missingMetric != null)
                return Failure($"Every selected run must contain a finite value for Y metric '{missingMetric}'.");

            SarPythonPlotRequestDto request = CreateSensitivityRequest(
                usableRuns, selectedFactors, selectedMetrics, title);
            return await RenderAsync(request, outputDirectory, "Sensitivity");
        }

        /// <summary>
        /// Generates one categorical bar-chart panel per selected Y metric. The X axis
        /// preserves the supplied run order and identifies batch members/independent
        /// records; simulation parameters remain metadata and never become X values.
        /// </summary>
        public static async Task<SarSensitivityRenderResult> RenderRunComparisonAsync(
            IReadOnlyList<SarSimulationRunManifest> runs,
            IReadOnlyList<string> metrics,
            string outputDirectory,
            string title)
        {
            List<SarSimulationRunManifest> usableRuns = UsableRuns(runs);
            if (usableRuns.Count < 2)
                return Failure("Select at least two completed simulation records for comparison.");
            List<string> selectedMetrics = DistinctNames(metrics);
            if (selectedMetrics.Count == 0)
                return Failure("Select at least one output metric (Y).");
            if (selectedMetrics.Count > 12)
                return Failure("Select at most 12 output metrics (Y) for one figure.");
            string missingMetric = selectedMetrics.FirstOrDefault(metric =>
                usableRuns.Any(run => !HasFiniteMetric(run, metric)));
            if (missingMetric != null)
                return Failure($"Every selected run must contain a finite value for Y metric '{missingMetric}'.");

            SarPythonPlotRequestDto request = CreateRunComparisonRequest(
                usableRuns, selectedMetrics, title);
            return await RenderAsync(request, outputDirectory, "Comparison");
        }

        public static async Task<SarSensitivityRenderResult> RenderTrafficHeatmapAsync(
            Network network,
            IReadOnlyList<Vertiport> vertiports,
            IReadOnlyDictionary<string, float> laneTraversals,
            IReadOnlyDictionary<string, float> resourceReservationSeconds,
            IReadOnlyList<string> visibleLayerIds,
            string outputDirectory,
            string title,
            float corridorTangentRatio = SarSimulationRunManifest.DefaultCorridorTangentRatio)
        {
            if (network == null)
                return Failure("The archived network snapshot is unavailable for traffic-heatmap plotting.");
            SarPythonTrafficHeatmapRequestDto request = CreateTrafficHeatmapRequest(
                network, vertiports, laneTraversals, resourceReservationSeconds,
                visibleLayerIds, title, corridorTangentRatio);
            return await RenderAsync(request, outputDirectory, "TrafficHeatmap");
        }

        public static SarPythonPlotRequestDto CreateSensitivityRequest(
            IReadOnlyList<SarSimulationRunManifest> runs,
            IReadOnlyList<string> factors,
            IReadOnlyList<string> metrics,
            string title)
        {
            List<SarSimulationRunManifest> usableRuns = UsableRuns(runs);
            SarHistoryAnalysisPlan plan = SarHistoryAnalysisPlanner.CreatePlan(usableRuns);
            return new SarPythonPlotRequestDto
            {
                Kind = SarPythonPlotKinds.Sensitivity,
                Title = string.IsNullOrWhiteSpace(title) ? "Parameter Sensitivity Analysis" : title.Trim(),
                SelectedFactors = DistinctNames(factors),
                SelectedMetrics = DistinctNames(metrics),
                AllVaryingFactors = new List<string>(plan.NumericVaryingParameters),
                Runs = CreateRunDtos(usableRuns)
            };
        }

        public static SarPythonPlotRequestDto CreateRunComparisonRequest(
            IReadOnlyList<SarSimulationRunManifest> runs,
            IReadOnlyList<string> metrics,
            string title)
        {
            List<SarSimulationRunManifest> usableRuns = UsableRuns(runs);
            return new SarPythonPlotRequestDto
            {
                Kind = SarPythonPlotKinds.RunComparison,
                Title = string.IsNullOrWhiteSpace(title)
                    ? "Selected Simulation Run Comparison"
                    : title.Trim(),
                // Null is intentional: comparison X is Runs, never a parameter vector.
                SelectedFactors = null,
                AllVaryingFactors = null,
                SelectedMetrics = DistinctNames(metrics),
                Runs = CreateRunDtos(usableRuns)
            };
        }

        public static SarPythonTrafficHeatmapRequestDto CreateTrafficHeatmapRequest(
            Network network,
            IReadOnlyList<Vertiport> vertiports,
            IReadOnlyDictionary<string, float> laneTraversals,
            IReadOnlyDictionary<string, float> resourceReservationSeconds,
            IReadOnlyList<string> visibleLayerIds,
            string title,
            float corridorTangentRatio = SarSimulationRunManifest.DefaultCorridorTangentRatio)
        {
            return new SarPythonTrafficHeatmapRequestDto
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Traffic Load Heatmap" : title.Trim(),
                Network = network,
                Vertiports = (vertiports ?? Array.Empty<Vertiport>()).Where(item => item != null).ToList(),
                LaneTraversals = CopyNonNegativeValues(laneTraversals),
                ResourceReservationSeconds = CopyNonNegativeValues(resourceReservationSeconds),
                VisibleLayerIds = DistinctNames(visibleLayerIds),
                CorridorTangentRatio = float.IsNaN(corridorTangentRatio) ||
                                       float.IsInfinity(corridorTangentRatio)
                    ? SarSimulationRunManifest.DefaultCorridorTangentRatio
                    : corridorTangentRatio
            };
        }

        private static async Task<SarSensitivityRenderResult> RenderAsync(
            object request,
            string outputDirectory,
            string filePrefix)
        {
            if (request == null) return Failure("The plotting request is empty.");
            if (string.IsNullOrWhiteSpace(outputDirectory))
                return Failure("The plotting output directory is empty.");
            Directory.CreateDirectory(outputDirectory);
            string jobId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" +
                           Guid.NewGuid().ToString("N").Substring(0, 8);
            string prefix = string.IsNullOrWhiteSpace(filePrefix) ? "Analysis" : filePrefix.Trim();
            string requestPath = Path.Combine(outputDirectory, prefix + "-" + jobId + ".request.json");
            string outputPath = Path.Combine(outputDirectory, prefix + "-" + jobId + ".png");
            string responsePath = Path.Combine(outputDirectory, prefix + "-" + jobId + ".response.json");
            File.WriteAllText(requestPath, SerializeRequest(request),
                new UTF8Encoding(false));

            try
            {
                Invocation invocation = await ResolveInvocationAsync();
                string arguments = invocation.PrefixArguments +
                    " --request " + Quote(requestPath) +
                    " --output " + Quote(outputPath) +
                    " --response " + Quote(responsePath);
                var start = new ProcessStartInfo
                {
                    FileName = invocation.Executable,
                    Arguments = arguments.Trim(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = invocation.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.EnvironmentVariables["MPLBACKEND"] = "Agg";
                start.EnvironmentVariables["MPLCONFIGDIR"] = Path.Combine(outputDirectory, ".matplotlib");
                start.EnvironmentVariables["PYTHONUTF8"] = "1";
                if (string.IsNullOrWhiteSpace(start.EnvironmentVariables["WINDIR"]))
                    start.EnvironmentVariables["WINDIR"] = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                ProcessResult process = await Task.Run(() => RunProcess(start, TimeoutMilliseconds));
                if (!File.Exists(responsePath))
                    return Failure(process.TimedOut
                        ? "Python plotting exceeded three minutes and was terminated."
                        : $"The plotting worker returned no response (exit {process.ExitCode}). {process.Error}".Trim());
                SarSensitivityRenderResult result = JsonConvert.DeserializeObject<SarSensitivityRenderResult>(
                    File.ReadAllText(responsePath)) ?? Failure("The plotting worker returned an unreadable response.");
                if (process.TimedOut) return Failure("Python plotting exceeded three minutes and was terminated.");
                if (!result.Success || process.ExitCode != 0)
                {
                    if (string.IsNullOrWhiteSpace(result.Message))
                        result.Message = string.IsNullOrWhiteSpace(result.Error) ? process.Error : result.Error;
                    return result;
                }
                if (!File.Exists(result.OutputPath) && File.Exists(outputPath)) result.OutputPath = outputPath;
                if (string.IsNullOrWhiteSpace(result.OutputPath) || !File.Exists(result.OutputPath))
                    return Failure("The plotting worker completed without producing a PNG image.");
                result.Pages = result.Pages ?? new List<SarRenderedImagePage>();
                for (int index = result.Pages.Count - 1; index >= 0; index--)
                {
                    SarRenderedImagePage page = result.Pages[index];
                    if (page == null || string.IsNullOrWhiteSpace(page.OutputPath) ||
                        !File.Exists(page.OutputPath))
                        result.Pages.RemoveAt(index);
                }
                return result;
            }
            catch (Exception ex)
            {
                return Failure(ex.Message);
            }
        }

        private static List<SarPythonPlotRunDto> CreateRunDtos(
            IReadOnlyList<SarSimulationRunManifest> runs)
        {
            var result = new List<SarPythonPlotRunDto>();
            if (runs == null) return result;
            for (int index = 0; index < runs.Count; index++)
            {
                SarSimulationRunManifest run = runs[index];
                if (run == null) continue;
                result.Add(new SarPythonPlotRunDto
                {
                    RunId = run.RunId ?? string.Empty,
                    Label = RunComparisonLabel(run, index, runs.Count),
                    SceneName = run.SceneName ?? string.Empty,
                    RunKind = run.Kind.ToString(),
                    BatchId = run.BatchId ?? string.Empty,
                    BatchIndex = run.BatchIndex,
                    BatchCount = run.BatchCount,
                    Duration = run.Duration,
                    Parameters = run.Parameters == null
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : new Dictionary<string, string>(run.Parameters, StringComparer.Ordinal),
                    Metrics = run.Metrics == null
                        ? new Dictionary<string, double>(StringComparer.Ordinal)
                        : new Dictionary<string, double>(run.Metrics, StringComparer.Ordinal)
                });
            }
            return result;
        }

        private static string RunComparisonLabel(SarSimulationRunManifest run, int index, int totalCount)
        {
            if (run != null && run.Kind == SarSimulationRunKind.BatchMember)
            {
                int memberIndex = Math.Max(1, run.BatchIndex);
                int memberCount = Math.Max(memberIndex, run.BatchCount);
                return $"Batch {memberIndex}/{memberCount}";
            }
            return $"Run {index + 1}/{Math.Max(1, totalCount)}";
        }

        private static List<SarSimulationRunManifest> UsableRuns(
            IEnumerable<SarSimulationRunManifest> runs)
        {
            return (runs ?? Enumerable.Empty<SarSimulationRunManifest>())
                .Where(run => run != null && run.Kind != SarSimulationRunKind.Batch &&
                              run.Status == SarSimulationRunStatus.Completed)
                .ToList();
        }

        private static List<string> DistinctNames(IEnumerable<string> values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return (values ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Where(seen.Add)
                .ToList();
        }

        private static Dictionary<string, float> CopyNonNegativeValues(
            IReadOnlyDictionary<string, float> values)
        {
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            if (values == null) return result;
            foreach (KeyValuePair<string, float> pair in values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || float.IsNaN(pair.Value) ||
                    float.IsInfinity(pair.Value)) continue;
                result[pair.Key.Trim()] = Math.Max(0f, pair.Value);
            }
            return result;
        }

        private static bool HasFiniteMetric(SarSimulationRunManifest run, string metric)
        {
            return run?.Metrics != null && run.Metrics.TryGetValue(metric ?? string.Empty, out double value) &&
                   !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static async Task<Invocation> ResolveInvocationAsync()
        {
            string analyticsRoot = Path.Combine(Application.streamingAssetsPath, AnalyticsFolder);
            string scriptPath = Path.Combine(analyticsRoot, "SensitivityAnalysis.py");
#if UNITY_EDITOR_WIN
            string projectPython = Path.GetFullPath(Path.Combine(Application.dataPath,
                "..", "Output", "Visualization", ".venv", "Scripts", "python.exe"));
            if (File.Exists(projectPython) && File.Exists(scriptPath))
            {
                ValidateSourceMatchesWorker(analyticsRoot, scriptPath);
                return new Invocation(projectPython, Quote(scriptPath), analyticsRoot);
            }
#endif
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            string directWorker = Path.Combine(analyticsRoot, "Worker", "SarSensitivityRenderer.exe");
            if (File.Exists(directWorker))
                return new Invocation(directWorker, string.Empty, Path.GetDirectoryName(directWorker));
            string worker = await Task.Run(() => PrepareArchivedWorker(analyticsRoot));
            if (!string.IsNullOrWhiteSpace(worker) && File.Exists(worker))
                return new Invocation(worker, string.Empty, Path.GetDirectoryName(worker));
#endif
            throw new FileNotFoundException(
                "The packaged SAR Python plotting worker is unavailable. Rebuild the release with the validated SARAnalytics worker archive.");
        }

        private static string PrepareArchivedWorker(string analyticsRoot)
        {
            string manifestPath = Path.Combine(analyticsRoot, "Worker", "manifest.json");
            if (!File.Exists(manifestPath)) return string.Empty;
            SarPlotWorkerManifest manifest = JsonConvert.DeserializeObject<SarPlotWorkerManifest>(
                File.ReadAllText(manifestPath));
            if (manifest == null || manifest.SchemaVersion != 1 ||
                string.IsNullOrWhiteSpace(manifest.ArchiveFile) ||
                string.IsNullOrWhiteSpace(manifest.Sha256) ||
                string.IsNullOrWhiteSpace(manifest.SourceSha256) ||
                string.IsNullOrWhiteSpace(manifest.TrafficHeatmapSourceSha256) ||
                string.IsNullOrWhiteSpace(manifest.ExecutableRelativePath))
                throw new InvalidDataException("The SAR plotting-worker manifest is invalid.");

            string archivePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath),
                manifest.ArchiveFile));
            string workerRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath), "."));
            if (!archivePath.StartsWith(workerRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(archivePath))
                throw new InvalidDataException("The SAR plotting-worker archive path is invalid.");
            string actualHash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(archivePath))
                actualHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actualHash, manifest.Sha256.Trim().ToLowerInvariant(),
                    StringComparison.Ordinal))
                throw new InvalidDataException("The SAR plotting-worker archive failed SHA-256 validation.");

            string cacheRoot = Path.Combine(SarRuntimePaths.ToolCacheRoot, "SarSensitivityRenderer", actualHash);
            string executable = SafeChildPath(cacheRoot, manifest.ExecutableRelativePath);
            string readyMarker = SafeChildPath(cacheRoot, ".ready");
            lock (WorkerExtractionLock)
            {
                // The executable can appear before the remaining DLLs have been extracted.
                // Only a completion marker therefore makes a cache reusable. If a previous
                // launch was interrupted, every archive member is overwritten and repaired.
                if (File.Exists(executable) && File.Exists(readyMarker) &&
                    string.Equals(File.ReadAllText(readyMarker).Trim(), actualHash,
                        StringComparison.OrdinalIgnoreCase))
                    return executable;

                Directory.CreateDirectory(cacheRoot);
                using (ZipArchive archive = ZipFile.OpenRead(archivePath))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string target = SafeChildPath(cacheRoot, entry.FullName);
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                    }
                }
                if (!File.Exists(executable))
                    throw new FileNotFoundException(
                        "The validated plotting-worker archive does not contain its executable.", executable);
                File.WriteAllText(readyMarker, actualHash, new UTF8Encoding(false));
                return executable;
            }
        }

        private static void ValidateSourceMatchesWorker(string analyticsRoot, string sourcePath)
        {
            string manifestPath = Path.Combine(analyticsRoot, "Worker", "manifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException(
                    "The plotting-worker manifest is missing; rebuild the bundled worker before testing the Python source.",
                    manifestPath);
            SarPlotWorkerManifest manifest = JsonConvert.DeserializeObject<SarPlotWorkerManifest>(
                File.ReadAllText(manifestPath));
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.SourceSha256) ||
                string.IsNullOrWhiteSpace(manifest.TrafficHeatmapSourceSha256))
                throw new InvalidDataException(
                    "The plotting-worker manifest does not identify the source revision; rebuild the bundled worker.");
            ValidateSourceHash(sourcePath, manifest.SourceSha256, "SensitivityAnalysis.py");
            ValidateSourceHash(Path.Combine(analyticsRoot, "TrafficHeatmapAnalysis.py"),
                manifest.TrafficHeatmapSourceSha256, "TrafficHeatmapAnalysis.py");
        }

        private static void ValidateSourceHash(string sourcePath, string expectedHash, string displayName)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"The plotting source is missing: {displayName}", sourcePath);
            string sourceHash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(sourcePath))
                sourceHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(sourceHash, expectedHash.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"{displayName} differs from the packaged worker. Run Tools/SARPlotWorker/build_worker.ps1 before testing or building.");
        }

        private static string SafeChildPath(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                                  Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(fullRoot, relative ?? string.Empty));
            if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The plotting-worker archive contains an unsafe path.");
            return target;
        }

        private static ProcessResult RunProcess(ProcessStartInfo start, int timeoutMilliseconds)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            using (Process process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += (_, args) => { if (args.Data != null) output.AppendLine(args.Data); };
                process.ErrorDataReceived += (_, args) => { if (args.Data != null) error.AppendLine(args.Data); };
                if (!process.Start()) throw new InvalidOperationException("The plotting worker could not be started.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                bool exited = process.WaitForExit(timeoutMilliseconds);
                if (!exited)
                {
                    try { process.Kill(); }
                    catch { }
                    return new ProcessResult(-1, output.ToString(), error.ToString(), true);
                }
                process.WaitForExit();
                return new ProcessResult(process.ExitCode, output.ToString(), error.ToString(), false);
            }
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        /// <summary>
        /// Serializes every plotting request with the same Unity-vector contract used by
        /// network and vertiport snapshots. Calling JsonConvert directly on Vector3 would
        /// traverse computed properties such as normalized and create a self-reference.
        /// </summary>
        public static string SerializeRequest(object request) =>
            JsonConvert.SerializeObject(request, Formatting.Indented, PlotJsonSettings);

        private static SarSensitivityRenderResult Failure(string message) => new SarSensitivityRenderResult
        {
            Success = false,
            Message = string.IsNullOrWhiteSpace(message) ? "Python analysis plotting failed." : message
        };

        private readonly struct Invocation
        {
            public readonly string Executable;
            public readonly string PrefixArguments;
            public readonly string WorkingDirectory;

            public Invocation(string executable, string prefixArguments, string workingDirectory)
            {
                Executable = executable;
                PrefixArguments = prefixArguments ?? string.Empty;
                WorkingDirectory = workingDirectory;
            }
        }

        private readonly struct ProcessResult
        {
            public readonly int ExitCode;
            public readonly string Output;
            public readonly string Error;
            public readonly bool TimedOut;

            public ProcessResult(int exitCode, string output, string error, bool timedOut)
            {
                ExitCode = exitCode;
                Output = output;
                Error = error;
                TimedOut = timedOut;
            }
        }
    }
}
