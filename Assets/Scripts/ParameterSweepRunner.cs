using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

namespace RoadNetworkEditor
{
    public class ParameterSweepRunner : MonoBehaviour
    {
        [Header("Batch Duration (overrides SimulationManager)")]
        public float batchDuration = 300f;

        [Header("Independent Variables")]
        public List<SweepVariable> variables = new List<SweepVariable>();

        [Header("Output")]
        public string outputPath = "Output/BatchExperimentRunSummaryTable.csv";

        // Runtime (non-serialized — must reset on domain reload)
        [System.NonSerialized] public bool isRunning;
        [System.NonSerialized] public int currentBatch, totalBatches;
        [System.NonSerialized] public float batchStartTime;
        [System.NonSerialized] public int batchPhase;
        [System.NonSerialized] public string lastError;

        public SimulationManager sim;
        private List<int> varIndices;
        private List<List<float>> varValues;
        private List<Dictionary<string, string>> allBatchData;
        private List<string> metricNames;
        private float savedMaxDur;
        private bool savedAutoStart;
        private bool gridTooLarge;
        private float collectStartTime;
        private int frameWait;
        private string pendingBatchVars; // saved before indices advance
        private readonly Dictionary<string, object> savedConfigValues = new Dictionary<string, object>();
        private SarSimulationHistory history;
        private SimulationManager subscribedSimulationManager;
        private bool stopSweepInProgress;

        public static IReadOnlyList<string> SweepableConfigFields =>
            SimulationParameterCatalog.SweepableFieldNames;

        public string OutputFullPath => ResolveOutputPath();

        void Awake() { sim = FindObjectOfType<SimulationManager>(); }

        public void ResolveGrid()
        {
            varValues = new List<List<float>>();
            gridTooLarge = false;
            foreach (var v in variables)
            {
                if (!v.enabled) continue;
                if (!SimulationParameterCatalog.TryGet(v.configField, out SimulationParameterDescriptor descriptor) ||
                    !descriptor.CanSweep || v.count <= 0) continue;
                var vals = new List<float>();
                for (int i = 0; i < v.count; i++) vals.Add(v.startValue + i * v.step);
                varValues.Add(vals);
            }
            totalBatches = 1;
            foreach (var vv in varValues)
            {
                long next = (long)totalBatches * vv.Count;
                if (next > 100000)
                {
                    totalBatches = 100001;
                    gridTooLarge = true;
                    break;
                }
                totalBatches = (int)next;
            }
            if (varValues.Count == 0) totalBatches = 0;
        }

        [Serializable]
        public class SweepVariable
        {
            public bool enabled = true;
            public string configField = "signalPropagationDelay";
            public float startValue = 0f;
            public float step = 0.2f;
            public int count = 5;
        }

        // ===== Public API (called from Editor) =====

        public bool StartSweep()
        {
            if (sim == null) sim = FindObjectOfType<SimulationManager>();
            if (isRunning) return false;

            ResolveGrid();
            if (sim == null || sim.config == null)
                return Fail("A SimulationManager with a SimulationConfig is required.");
            if (sim.IsRunning())
                return Fail("Stop the current simulation before starting a parameter sweep.");
            if (totalBatches <= 0)
                return Fail("Enable at least one valid sweep variable with a positive count.");
            if (gridTooLarge)
                return Fail("The parameter grid exceeds the safety limit of 100,000 batches.");
            if (batchDuration <= 0f)
                return Fail("Batch duration must be greater than zero.");
            string duplicate = variables.Where(item => item.enabled)
                .GroupBy(item => item.configField, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key).FirstOrDefault();
            if (!string.IsNullOrEmpty(duplicate))
                return Fail($"Sweep variable '{duplicate}' is enabled more than once.");
            foreach (SweepVariable variable in variables.Where(item => item.enabled))
            {
                if (!SimulationParameterCatalog.TryGet(variable.configField,
                        out SimulationParameterDescriptor descriptor) || !descriptor.CanSweep)
                    return Fail($"Unsupported sweep variable '{variable.configField}'.");
                float lastValue = variable.startValue + (Mathf.Max(1, variable.count) - 1) * variable.step;
                float minimum = Mathf.Min(variable.startValue, lastValue);
                float maximum = Mathf.Max(variable.startValue, lastValue);
                if (variable.count <= 0 || minimum < descriptor.Minimum || maximum > descriptor.Maximum)
                    return Fail($"Sweep range for '{variable.configField}' must remain within " +
                                $"{descriptor.Minimum:0.###}–{descriptor.Maximum:0.###}.");
            }

            allBatchData = new List<Dictionary<string, string>>();
            metricNames = new List<string>();
            varIndices = new List<int>();
            for (int i = 0; i < varValues.Count; i++) varIndices.Add(0);

            currentBatch = 1;
            isRunning = true;
            batchPhase = 0;
            batchStartTime = 0f;
            frameWait = 0;
            savedMaxDur = sim.maxSimDuration;
            savedAutoStart = sim.autoStart;
            savedConfigValues.Clear();
            foreach (SweepVariable variable in variables.Where(item => item.enabled))
                if (!savedConfigValues.ContainsKey(variable.configField))
                    savedConfigValues[variable.configField] =
                        SimulationParameterCatalog.ReadValue(sim.config, variable.configField);
            sim.autoStart = false;  // prevent sim from starting before we're ready
            lastError = null;
            try
            {
                history = SarSimulationHistory.Ensure(sim);
                history?.BeginBatch(totalBatches, BuildSweepDefinition());
                AttachSimulationFinishedHandler();
            }
            catch (Exception exception)
            {
                isRunning = false;
                DetachSimulationFinishedHandler();
                RestoreSimulationSettings();
                try
                {
                    history?.CompleteBatch(null, SarSimulationRunStatus.Cancelled);
                }
                catch (Exception finalizationException)
                {
                    Debug.LogError($"[Sweep] Failed to roll back batch history: {finalizationException}");
                }
                history = null;
                return Fail($"Failed to initialize batch history: {exception.Message}");
            }

            Debug.Log($"[Sweep] Starting {totalBatches} batches ({(totalBatches * batchDuration / 60f):F0} min)...");
            return true;
        }

        public void StopSweepInternal()
        {
            StopSweepInternal(false);
        }

        private void StopSweepInternal(bool completed)
        {
            if (!isRunning || stopSweepInProgress) return;

            stopSweepInProgress = true;
            isRunning = false;
            DetachSimulationFinishedHandler();
            try
            {
                if (sim != null && sim.IsRunning()) sim.StopSimulation();
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Sweep] Failed to stop the active member: {exception}");
            }

            try
            {
                RestoreSimulationSettings();
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Sweep] Failed to restore simulation settings: {exception}");
            }

            try
            {
                string summaryPath = allBatchData != null && allBatchData.Count > 0
                    ? OutputFullPath
                    : null;
                history?.CompleteBatch(summaryPath, completed
                    ? SarSimulationRunStatus.Completed
                    : SarSimulationRunStatus.Cancelled);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Sweep] Failed to finalize batch history: {exception}");
            }
            finally
            {
                history = null;
                stopSweepInProgress = false;
            }
            Debug.Log(completed ? "[Sweep] Completed." : "[Sweep] Cancelled.");
        }

        private void AttachSimulationFinishedHandler()
        {
            DetachSimulationFinishedHandler();
            if (sim == null) return;
            subscribedSimulationManager = sim;
            subscribedSimulationManager.SimulationFinished += HandleSimulationFinished;
        }

        private void DetachSimulationFinishedHandler()
        {
            if (subscribedSimulationManager != null)
                subscribedSimulationManager.SimulationFinished -= HandleSimulationFinished;
            subscribedSimulationManager = null;
        }

        private void HandleSimulationFinished(bool completedNaturally)
        {
            if (!isRunning || stopSweepInProgress) return;

            if (!completedNaturally)
            {
                lastError = $"Batch {currentBatch} was stopped before natural completion; the sweep was cancelled.";
                Debug.LogWarning($"[Sweep] {lastError}");
                StopSweepInternal(false);
                return;
            }

            if (batchPhase != 1)
            {
                lastError = $"Batch {currentBatch} finished in an unexpected sweep phase ({batchPhase}); " +
                            "the sweep was cancelled to prevent results from crossing batches or scenes.";
                Debug.LogWarning($"[Sweep] {lastError}");
                StopSweepInternal(false);
                return;
            }

            BeginCollectionPhase();
        }

        private void BeginCollectionPhase()
        {
            batchPhase = 2;
            collectStartTime = Time.time;
            frameWait = 3; // wait a few frames for file I/O and aircraft cleanup
        }

        private void RestoreSimulationSettings()
        {
            if (sim == null) return;
            sim.maxSimDuration = savedMaxDur;
            sim.autoStart = savedAutoStart;
            if (sim.config == null) return;
            foreach (KeyValuePair<string, object> pair in savedConfigValues)
                SimulationParameterCatalog.RestoreValue(sim.config, pair.Key, pair.Value);
        }

        private void OnDisable()
        {
            if (isRunning) StopSweepInternal(false);
            else DetachSimulationFinishedHandler();
        }

        private void OnDestroy()
        {
            if (isRunning) StopSweepInternal(false);
            else DetachSimulationFinishedHandler();
        }

        // ===== Main loop in Update =====

        void Update()
        {
            if (!isRunning) return;
            if (sim == null) { StopSweepInternal(); return; }

            switch (batchPhase)
            {
                case 0: // Start next batch
                    if (AreAllDone())
                    {
                        StopSweepInternal(true);
                        Debug.Log($"[Sweep] ALL {totalBatches} COMPLETE → {outputPath}");
                        return;
                    }

                    ApplyCurrentVars();
                    pendingBatchVars = GetVarStr(); // save before batch starts
                    history?.SetBatchMemberContext(currentBatch, totalBatches);
                    sim.maxSimDuration = batchDuration;
                    batchPhase = 1;
                    try
                    {
                        sim.StartSimulation();
                    }
                    catch (Exception exception)
                    {
                        lastError = $"Batch {currentBatch} failed to start: {exception.Message}";
                        Debug.LogError($"[Sweep] {lastError}\n{exception}");
                        StopSweepInternal(false);
                        return;
                    }
                    if (!sim.IsRunning())
                    {
                        lastError = $"Batch {currentBatch} did not enter the running state; the sweep was cancelled.";
                        Debug.LogError($"[Sweep] {lastError}");
                        StopSweepInternal(false);
                        return;
                    }
                    batchStartTime = Time.time;
                    Debug.Log($"[Sweep] Batch {currentBatch}/{totalBatches}: {pendingBatchVars}  started.");
                    break;

                case 1: // Sim running — check if done
                    if (!sim.IsRunning())
                    {
                        // Every legitimate stop is reported through SimulationFinished.
                        // Reaching this state means the member ended abnormally or its
                        // lifecycle callback was lost, so continuing would contaminate
                        // the following batch.
                        lastError = $"Batch {currentBatch} stopped without a completion event; " +
                                    "the sweep was cancelled.";
                        Debug.LogWarning($"[Sweep] {lastError}");
                        StopSweepInternal(false);
                        return;
                    }
                    break;

                case 2: // Collecting — wait for file I/O then collect
                    frameWait--;
                    if (frameWait > 0) break;

                    pendingBatchVars = GetVarStr();
                    CollectData();
                    AppendBatchToCSV();
                    AdvanceIndices();
                    currentBatch++;
                    Debug.Log($"[Sweep] Batch {currentBatch - 1} done.");

                    if (currentBatch > totalBatches)
                    {
                        StopSweepInternal(true);
                        Debug.Log($"[Sweep] ALL {totalBatches} COMPLETE → {OutputFullPath}");
                        return;
                    }

                    batchPhase = 0;
                    break;
            }
        }

        // ===== Helpers =====

        bool AreAllDone()
        {
            return currentBatch > totalBatches;
        }

        void ApplyCurrentVars()
        {
            int vi = 0;
            for (int i = 0; i < variables.Count; i++)
            {
                var v = variables[i];
                if (!v.enabled) continue;
                float val = varValues[vi][varIndices[vi]];
                if (!SimulationParameterCatalog.TrySetNumeric(sim.config, v.configField, val,
                        out string error))
                    Debug.LogError($"[Sweep] {error}");
                vi++;
            }
        }

        string GetVarStr()
        {
            var p = new List<string>();
            int vi = 0;
            for (int i = 0; i < variables.Count; i++)
            {
                if (!variables[i].enabled) continue;
                p.Add($"{variables[i].configField}={varValues[vi][varIndices[vi]]}");
                vi++;
            }
            return string.Join(", ", p);
        }

        private Dictionary<string, string> BuildSweepDefinition()
        {
            var definition = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["batchDuration"] = batchDuration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["batchCount"] = totalBatches.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            int index = 1;
            foreach (SweepVariable variable in variables.Where(item => item.enabled))
            {
                definition[$"factor{index}"] = variable.configField;
                definition[$"factor{index}.start"] = variable.startValue.ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture);
                definition[$"factor{index}.step"] = variable.step.ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture);
                definition[$"factor{index}.count"] = variable.count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                index++;
            }
            return definition;
        }

        void AdvanceIndices()
        {
            if (varIndices == null || varIndices.Count == 0) return;
            int dim = varIndices.Count - 1;
            varIndices[dim]++;
            while (dim >= 0 && varIndices[dim] >= varValues[dim].Count)
            {
                varIndices[dim] = 0;
                dim--;
                if (dim >= 0) varIndices[dim]++;
            }
        }

        void CollectData()
        {
            string metricsPath = Path.Combine(sim.GetOutputDirectory(), "MetricsTable.csv");
            if (!File.Exists(metricsPath))
            {
                Debug.LogWarning($"[Sweep] Batch {currentBatch}: metrics file missing.");
                allBatchData.Add(new Dictionary<string, string>());
                return;
            }

            var batchData = new Dictionary<string, string>();
            // Split Vars into individual parameter columns
            string varsStr = pendingBatchVars ?? GetVarStr();
            if (!string.IsNullOrEmpty(varsStr))
            {
                foreach (var part in varsStr.Split(new[] { ", " }, StringSplitOptions.None))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0)
                        batchData[part.Substring(0, eq)] = part.Substring(eq + 1);
                }
            }
            var lines = File.ReadAllLines(metricsPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd(',');
                int ci = line.IndexOf(',');
                if (ci > 0)
                {
                    string key = line.Substring(0, ci).Trim();
                    string val = line.Substring(ci + 1).Trim();
                    if (string.IsNullOrEmpty(key) || key == "Metrics") continue;
                    batchData[key] = val;
                    if (currentBatch == 1 && !metricNames.Contains(key))
                        metricNames.Add(key);
                }
            }
            allBatchData.Add(batchData);
            Debug.Log($"[Sweep] Batch {currentBatch}/{totalBatches} collected ({batchData.Count} values).");
        }

        /// <summary>Write CSV in 3-columns-per-batch layout.</summary>
        void AppendBatchToCSV()
        {
            if (allBatchData.Count == 0) return;
            string path = ResolveOutputPath();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Collect param keys and metric keys from all batches
            var paramKeys = new List<string>();
            foreach (var bd in allBatchData)
                foreach (var k in bd.Keys)
                    if (k != "Batch" && metricNames != null && !metricNames.Contains(k) && !paramKeys.Contains(k))
                        paramKeys.Add(k);

            // Build rows: each row is a List<string> where position 0,3,6,... = label, 1,4,7,... = value, 2,5,8,... = empty spacer
            int colsPerBatch = 3;
            var rows = new List<List<string>>();

            // Row 0: Batch headers
            var hdrRow = NewRow(allBatchData.Count);
            for (int b = 0; b < allBatchData.Count; b++)
                hdrRow[b * colsPerBatch] = $"Batch{b + 1}";
            rows.Add(hdrRow);

            // Row 1: "Parameters" label
            var paramHdr = NewRow(allBatchData.Count);
            for (int b = 0; b < allBatchData.Count; b++)
                paramHdr[b * colsPerBatch] = "Parameters";
            rows.Add(paramHdr);

            // Parameter rows
            foreach (string pk in paramKeys)
            {
                var prow = NewRow(allBatchData.Count);
                for (int b = 0; b < allBatchData.Count; b++)
                {
                    prow[b * colsPerBatch] = pk;
                    prow[b * colsPerBatch + 1] = allBatchData[b].ContainsKey(pk) ? allBatchData[b][pk] : "";
                }
                rows.Add(prow);
            }

            // Blank row
            rows.Add(NewRow(allBatchData.Count));

            // "Metrics" header
            var metHdr = NewRow(allBatchData.Count);
            for (int b = 0; b < allBatchData.Count; b++)
                metHdr[b * colsPerBatch] = "Metrics";
            rows.Add(metHdr);

            // Metric rows
            foreach (string mk in metricNames)
            {
                var mrow = NewRow(allBatchData.Count);
                for (int b = 0; b < allBatchData.Count; b++)
                {
                    mrow[b * colsPerBatch] = mk;
                    mrow[b * colsPerBatch + 1] = allBatchData[b].ContainsKey(mk) ? allBatchData[b][mk] : "";
                }
                rows.Add(mrow);
            }

            // Write CSV
            using (var w = new StreamWriter(path))
            {
                foreach (var row in rows)
                    w.WriteLine(string.Join(",", row));
            }

            Debug.Log($"[Sweep] CSV table updated: batch {allBatchData.Count} ({new FileInfo(path).Length} bytes)");
        }

        List<string> NewRow(int batchCount)
        {
            var r = new List<string>(batchCount * 3);
            for (int i = 0; i < batchCount * 3; i++) r.Add("");
            return r;
        }

        private string ResolveOutputPath()
        {
            string value = string.IsNullOrWhiteSpace(outputPath)
                ? "user://Output/BatchExperimentRunSummaryTable.csv"
                : outputPath.Trim();
            string path = SarPortablePath.Resolve(value);
            if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
                path += ".csv";
            return path;
        }

        private bool Fail(string error)
        {
            lastError = error;
            Debug.LogError($"[Sweep] {error}");
            return false;
        }
    }
}
