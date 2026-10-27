using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public sealed class SarSimulationHistoryTests
    {
        [Test]
        public void MetricsParser_UsesInvariantNumbersAndSkipsNonNumericRows()
        {
            const string csv =
                "Metrics,,\n" +
                "Simulation Duration (s),60.5,\n" +
                "\"Metric, with comma\",12.25,\n" +
                "Unavailable,N/A,\n" +
                "Not finite,NaN,\n";

            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Dictionary<string, double> metrics = SarSimulationHistory.ParseMetricsTableText(csv);
                Assert.That(metrics["Simulation Duration (s)"], Is.EqualTo(60.5d).Within(0.0001d));
                Assert.That(metrics["Metric, with comma"], Is.EqualTo(12.25d).Within(0.0001d));
                Assert.That(metrics.ContainsKey("Unavailable"), Is.False);
                Assert.That(metrics.ContainsKey("Not finite"), Is.False);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Test]
        public void CsvTableParser_PreservesHorizontalBatchGroupsAndBlankSpacerColumns()
        {
            const string csv =
                "Batch1,,,Batch2,,\n" +
                "Parameters,,,Parameters,,\n" +
                "signalPropagationDelay,0,,signalPropagationDelay,0.25,\n";
            SarCsvTable table = SarSimulationHistory.ParseCsvTableText(csv);
            Assert.That(table.Rows.Count, Is.EqualTo(3));
            Assert.That(table.ColumnCount, Is.EqualTo(6));
            Assert.That(table.Rows[0], Is.EqualTo(new[] { "Batch1", "", "", "Batch2", "", "" }));
            Assert.That(table.Rows[2][2], Is.Empty);
            Assert.That(table.Rows[2][5], Is.Empty);
        }

        [Test]
        public void Planner_FindsCommonMetricsVaryingFactorsAndNormalizesCounts()
        {
            var first = Run("one", 10d, "0", 10d, 5d,
                new KeyValuePair<string, double>("Only first", 1d));
            var second = Run("two", 20d, "1", 40d, 7d);
            var third = Run("three", 20d, "1", 60d, 9d);

            SarHistoryAnalysisPlan plan = SarHistoryAnalysisPlanner.CreatePlan(
                new[] { first, second, third });
            Assert.That(plan.CommonMetrics,
                Is.EquivalentTo(new[] { "Total Missions", "Average Mission Duration (s)" }));
            Assert.That(plan.NumericVaryingParameters, Is.EquivalentTo(new[] { "signalPropagationDelay" }));
            Assert.That(plan.HasDurationMismatch, Is.True);

            List<SarHistoryBarPoint> counts = SarHistoryAnalysisPlanner.BuildBarData(
                new[] { first, second, third }, "Total Missions");
            Assert.That(counts.ConvertAll(point => point.Value), Is.EqualTo(new[] { 1d, 2d, 3d }));
            Assert.That(counts.TrueForAll(point => point.IsPerSecond), Is.True);

            List<SarHistoryBarPoint> averages = SarHistoryAnalysisPlanner.BuildBarData(
                new[] { first, second, third }, "Average Mission Duration (s)");
            Assert.That(averages.ConvertAll(point => point.Value), Is.EqualTo(new[] { 5d, 7d, 9d }));
            Assert.That(averages.TrueForAll(point => !point.IsPerSecond), Is.True);
        }

        [Test]
        public void Planner_SensitivityGroupsRepeatedFactorValuesByMean()
        {
            var first = Run("one", 10d, "0", 10d, 5d);
            var second = Run("two", 20d, "1", 40d, 7d);
            var third = Run("three", 20d, "1", 60d, 9d);

            List<SarHistorySensitivityPoint> points = SarHistoryAnalysisPlanner.BuildSensitivityData(
                new[] { first, second, third }, "signalPropagationDelay", "Total Missions");
            Assert.That(points.Count, Is.EqualTo(2));
            Assert.That(points[0].FactorValue, Is.EqualTo(0d));
            Assert.That(points[0].MeanValue, Is.EqualTo(1d).Within(0.0001d));
            Assert.That(points[0].SampleCount, Is.EqualTo(1));
            Assert.That(points[1].FactorValue, Is.EqualTo(1d));
            Assert.That(points[1].MeanValue, Is.EqualTo(2.5d).Within(0.0001d));
            Assert.That(points[1].SampleCount, Is.EqualTo(2));
            Assert.That(points.TrueForAll(point => point.IsPerSecond), Is.True);
        }

        [Test]
        public void Planner_UsesOnlyCompletedLeafRunsForScientificComparisons()
        {
            SarSimulationRunManifest first = Run("complete-a", 10d, "0", 10d, 5d);
            SarSimulationRunManifest second = Run("complete-b", 20d, "1", 40d, 7d);
            SarSimulationRunManifest failed = Run("failed", 5d, "99", 999d, 99d);
            failed.Status = SarSimulationRunStatus.Failed;
            failed.Metrics.Remove("Average Mission Duration (s)");
            SarSimulationRunManifest stopped = Run("stopped", 5d, "100", 999d, 99d);
            stopped.Status = SarSimulationRunStatus.Stopped;
            var batch = new SarSimulationRunManifest
            {
                RunId = "batch",
                Kind = SarSimulationRunKind.Batch,
                Status = SarSimulationRunStatus.Completed
            };
            batch.Metrics["Batch-only metric"] = 1d;

            var selected = new[] { first, failed, batch, stopped, second };
            SarHistoryAnalysisPlan plan = SarHistoryAnalysisPlanner.CreatePlan(selected);
            Assert.That(plan.CommonMetrics,
                Is.EquivalentTo(new[] { "Total Missions", "Average Mission Duration (s)" }));
            Assert.That(plan.NumericVaryingParameters,
                Is.EquivalentTo(new[] { "signalPropagationDelay" }));

            List<SarHistoryBarPoint> bars = SarHistoryAnalysisPlanner.BuildBarData(
                selected, "Total Missions");
            Assert.That(bars.ConvertAll(point => point.RunId),
                Is.EqualTo(new[] { "complete-a", "complete-b" }));
            List<SarHistorySensitivityPoint> sensitivity =
                SarHistoryAnalysisPlanner.BuildSensitivityData(
                    selected, "signalPropagationDelay", "Total Missions");
            Assert.That(sensitivity.ConvertAll(point => point.FactorValue),
                Is.EqualTo(new[] { 0d, 1d }));
        }

        [Test]
        public void Manifest_RoundTripKeepsSchemaAndOmitsRuntimePaths()
        {
            var source = new SarSimulationRunManifest
            {
                RunId = "run-001",
                SceneId = "scene-a",
                SceneName = "Test Scene",
                Kind = SarSimulationRunKind.BatchMember,
                BatchId = "batch-001",
                BatchIndex = 2,
                BatchCount = 4,
                StartedUtc = "2026-08-27T00:00:00.0000000Z",
                CompletedUtc = "2026-08-27T00:01:00.0000000Z",
                Duration = 60d,
                Status = SarSimulationRunStatus.Completed,
                MetricsTableFile = "MetricsTable.csv",
                RecordingFile = "SimulationRecording.json.gz",
                TopologyFingerprint = "abc123",
                AxisNetworkSnapshotFile = "AxisNetworkSnapshot.json",
                FullNetworkSnapshotFile = "FullNetworkSnapshot.json",
                VertiportSnapshotFile = "VertiportsSnapshot.json",
                CorridorTangentRatio = 0.45f,
                FolderPath = "C:/must-not-be-serialized",
                ManifestPath = "C:/must-not-be-serialized/manifest.json"
            };
            source.Parameters["maxSpeed"] = "20";
            source.Metrics["Total Missions"] = 12d;
            source.ChildRunIds.Add("child-1");
            source.ArtifactErrors.Add("SimulationRecording.json: test failure");

            string json = SarSimulationHistory.ToJson(source);
            Assert.That(json, Does.Contain("\"SchemaVersion\": 2"));
            Assert.That(json, Does.Contain("\"BatchMember\""));
            Assert.That(json, Does.Not.Contain("must-not-be-serialized"));

            SarSimulationRunManifest restored = SarSimulationHistory.FromJson(json);
            Assert.That(restored.RunId, Is.EqualTo(source.RunId));
            Assert.That(restored.Kind, Is.EqualTo(SarSimulationRunKind.BatchMember));
            Assert.That(restored.Status, Is.EqualTo(SarSimulationRunStatus.Completed));
            Assert.That(restored.Parameters["maxSpeed"], Is.EqualTo("20"));
            Assert.That(restored.Metrics["Total Missions"], Is.EqualTo(12d));
            Assert.That(restored.TopologyFingerprint, Is.EqualTo("abc123"));
            Assert.That(restored.FullNetworkSnapshotFile, Is.EqualTo("FullNetworkSnapshot.json"));
            Assert.That(restored.CorridorTangentRatio, Is.EqualTo(0.45f));
            Assert.That(restored.ArtifactErrors,
                Is.EqualTo(new[] { "SimulationRecording.json: test failure" }));
            Assert.That(restored.FolderPath, Is.Null);
            Assert.That(restored.ManifestPath, Is.Null);

            SarSimulationRunManifest legacy = SarSimulationHistory.FromJson(
                "{\"SchemaVersion\":1,\"RunId\":\"legacy\",\"Status\":\"Completed\"}");
            Assert.That(legacy.ArtifactErrors, Is.Not.Null.And.Empty,
                "Schema-v1 manifests did not contain ArtifactErrors and must remain readable.");
            Assert.That(legacy.CorridorTangentRatio,
                Is.EqualTo(SarSimulationRunManifest.DefaultCorridorTangentRatio),
                "Legacy manifests must reproduce the historical 0.3 corridor geometry.");
        }

        [Test]
        public void BeginRun_ArchivesLiveVertiportCorridorTangentRatio()
        {
            var root = new GameObject("Corridor tangent archive test");
            root.SetActive(false);
            SimulationManager manager = root.AddComponent<SimulationManager>();
            manager.networkManager = root.AddComponent<NetworkManager>();
            manager.vertiportManager = root.AddComponent<VertiportManager>();
            manager.vertiportManager.corridorTangentRatio = 0.45f;
            SarSceneWorkspace workspace = root.AddComponent<SarSceneWorkspace>();
            workspace.Initialize(manager, null, false);
            SarSimulationHistory history = root.AddComponent<SarSimulationHistory>();
            history.Initialize(manager, workspace);
            string workspaceRoot = workspace.WorkspaceRoot;
            try
            {
                SarSimulationRunManifest manifest = history.BeginRun();
                Assert.That(manifest.CorridorTangentRatio, Is.EqualTo(0.45f));
                SarSimulationRunManifest archived = SarSimulationHistory.FromJson(
                    File.ReadAllText(manifest.ManifestPath));
                Assert.That(archived.CorridorTangentRatio, Is.EqualTo(0.45f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, true);
            }
        }

        [Test]
        public void FullNetworkSnapshotLoader_ReplacesConstructorDefaultAltitudeLayer()
        {
            string root = Path.Combine(Path.GetTempPath(), "sar-network-snapshot-" +
                                                           Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var historyRoot = new GameObject("Historical network snapshot loader");
            historyRoot.SetActive(false);
            try
            {
                const string snapshotFile = "FullNetworkSnapshot.json";
                File.WriteAllText(Path.Combine(root, snapshotFile),
                    "{\"SchemaVersion\":2,\"NetworkId\":\"archived\",\"Nodes\":[],\"Edges\":[]," +
                    "\"AltitudeLayers\":[" +
                    "{\"LayerId\":\"L0\",\"DisplayName\":\"Lower Network Layer\"," +
                    "\"NominalAltitude\":75.0,\"VerticalTolerance\":5.0,\"Enabled\":true," +
                    "\"Frozen\":false}," +
                    "{\"LayerId\":\"L1\",\"DisplayName\":\"Upper Network Layer\"," +
                    "\"NominalAltitude\":125.0,\"VerticalTolerance\":5.0,\"Enabled\":true," +
                    "\"Frozen\":false}],\"LaneConnectionCorridors\":[]}");
                SarSimulationHistory history = historyRoot.AddComponent<SarSimulationHistory>();
                var manifest = new SarSimulationRunManifest
                {
                    RunId = "archived-run",
                    FolderPath = root,
                    FullNetworkSnapshotFile = snapshotFile
                };

                Network loaded = history.LoadFullNetworkSnapshot(manifest);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.AltitudeLayers.Count, Is.EqualTo(2),
                    "The constructor's placeholder L0 must not be merged into an authoritative snapshot.");
                Assert.That(loaded.AltitudeLayers.Select(layer => layer.LayerId),
                    Is.EqualTo(new[] { "L0", "L1" }));
                Assert.That(loaded.AltitudeLayers.Select(layer => layer.DisplayName),
                    Is.EqualTo(new[] { "Lower Network Layer", "Upper Network Layer" }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(historyRoot);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void HistoryRecycle_MovesStandaloneRunAndRefreshSkipsTrash()
        {
            var root = new GameObject("History recycle test");
            root.SetActive(false);
            SarSceneWorkspace workspace = root.AddComponent<SarSceneWorkspace>();
            workspace.Initialize(null, null, false);
            SarSimulationHistory history = root.AddComponent<SarSimulationHistory>();
            history.Initialize(null, workspace);
            string workspaceRoot = workspace.WorkspaceRoot;
            try
            {
                SarSimulationRunManifest run = WriteHistoryManifest(history, "single-a",
                    SarSimulationRunKind.Single);
                history.LoadCurrentSceneManifests();
                Assert.That(history.Manifests.Select(item => item.RunId), Does.Contain(run.RunId));

                Assert.That(history.TryMoveToRecycleBin(run.RunId, out string message),
                    Is.True, message);
                Assert.That(Directory.Exists(run.FolderPath), Is.False);
                Assert.That(Directory.GetFiles(history.RecycleRoot,
                    SarSimulationHistory.ManifestFileName, SearchOption.AllDirectories), Has.Length.EqualTo(1));

                history.LoadCurrentSceneManifests();
                Assert.That(history.Manifests, Is.Empty,
                    "Recycled manifests must not be rediscovered by the recursive history scan.");

                string recycledManifest = Directory.GetFiles(history.RecycleRoot,
                    SarSimulationHistory.ManifestFileName, SearchOption.AllDirectories).Single();
                string recycledFolder = Path.GetDirectoryName(recycledManifest);
                Directory.Move(recycledFolder, run.FolderPath);
                history.LoadCurrentSceneManifests();
                Assert.That(history.Manifests.Select(item => item.RunId), Does.Contain(run.RunId),
                    "Moving the intact folder back from .Trash must restore the record on refresh.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, true);
            }
        }

        [Test]
        public void HistoryRecycle_RejectsMemberAloneAndMovesCompleteBatchTogether()
        {
            var root = new GameObject("Batch recycle test");
            root.SetActive(false);
            SarSceneWorkspace workspace = root.AddComponent<SarSceneWorkspace>();
            workspace.Initialize(null, null, false);
            SarSimulationHistory history = root.AddComponent<SarSimulationHistory>();
            history.Initialize(null, workspace);
            string workspaceRoot = workspace.WorkspaceRoot;
            try
            {
                SarSimulationRunManifest first = WriteHistoryManifest(history, "member-a",
                    SarSimulationRunKind.BatchMember, "batch-a");
                SarSimulationRunManifest second = WriteHistoryManifest(history, "member-b",
                    SarSimulationRunKind.BatchMember, "batch-a");
                SarSimulationRunManifest batch = WriteHistoryManifest(history, "batch-a",
                    SarSimulationRunKind.Batch);
                batch.BatchId = batch.RunId;
                batch.ChildRunIds = new List<string> { first.RunId, second.RunId };
                File.WriteAllText(batch.ManifestPath, SarSimulationHistory.ToJson(batch));
                history.LoadCurrentSceneManifests();

                Assert.That(history.TryMoveToRecycleBin(first.RunId, out string memberMessage),
                    Is.False);
                Assert.That(memberMessage, Does.Contain("batch member"));
                Assert.That(Directory.Exists(first.FolderPath), Is.True);

                Assert.That(history.TryMoveToRecycleBin(batch.RunId, out string batchMessage),
                    Is.True, batchMessage);
                Assert.That(Directory.Exists(batch.FolderPath), Is.False);
                Assert.That(Directory.Exists(first.FolderPath), Is.False);
                Assert.That(Directory.Exists(second.FolderPath), Is.False);
                Assert.That(Directory.GetFiles(history.RecycleRoot,
                    SarSimulationHistory.ManifestFileName, SearchOption.AllDirectories), Has.Length.EqualTo(3));
                history.LoadCurrentSceneManifests();
                Assert.That(history.Manifests, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, true);
            }
        }

        [Test]
        public void HistoryRecycle_CorruptDeclaredChildFailsAtomically()
        {
            var root = new GameObject("Corrupt batch-child recycle test");
            root.SetActive(false);
            SarSceneWorkspace workspace = root.AddComponent<SarSceneWorkspace>();
            workspace.Initialize(null, null, false);
            SarSimulationHistory history = root.AddComponent<SarSimulationHistory>();
            history.Initialize(null, workspace);
            string workspaceRoot = workspace.WorkspaceRoot;
            try
            {
                SarSimulationRunManifest unrelated = WriteHistoryManifest(history, "unrelated",
                    SarSimulationRunKind.Single);
                SarSimulationRunManifest batch = WriteHistoryManifest(history, "corrupt-batch",
                    SarSimulationRunKind.Batch);
                batch.BatchId = batch.RunId;
                batch.ChildRunIds = new List<string> { unrelated.RunId, "missing-member" };
                File.WriteAllText(batch.ManifestPath, SarSimulationHistory.ToJson(batch));
                history.LoadCurrentSceneManifests();

                Assert.That(history.TryMoveToRecycleBin(batch.RunId, out string message), Is.False);
                Assert.That(message, Does.Contain("incomplete or inconsistent"));
                Assert.That(Directory.Exists(batch.FolderPath), Is.True);
                Assert.That(Directory.Exists(unrelated.FolderPath), Is.True,
                    "An unrelated declared record must never be moved with a corrupt batch.");
                Assert.That(Directory.Exists(history.RecycleRoot) && Directory.GetFiles(history.RecycleRoot,
                    SarSimulationHistory.ManifestFileName, SearchOption.AllDirectories).Length > 0, Is.False,
                    "Validation must finish before any record folder is moved.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, true);
            }
        }

        [Test]
        public void HistoryRecycle_CorruptDiscoveredBackReferenceFailsAtomically()
        {
            var root = new GameObject("Corrupt batch back-reference recycle test");
            root.SetActive(false);
            SarSceneWorkspace workspace = root.AddComponent<SarSceneWorkspace>();
            workspace.Initialize(null, null, false);
            SarSimulationHistory history = root.AddComponent<SarSimulationHistory>();
            history.Initialize(null, workspace);
            string workspaceRoot = workspace.WorkspaceRoot;
            try
            {
                SarSimulationRunManifest batch = WriteHistoryManifest(history, "batch-with-rogue",
                    SarSimulationRunKind.Batch);
                batch.BatchId = batch.RunId;
                File.WriteAllText(batch.ManifestPath, SarSimulationHistory.ToJson(batch));
                SarSimulationRunManifest rogue = WriteHistoryManifest(history, "rogue-single",
                    SarSimulationRunKind.Single, batch.RunId);
                history.LoadCurrentSceneManifests();

                Assert.That(history.TryMoveToRecycleBin(batch.RunId, out string message), Is.False);
                Assert.That(message, Does.Contain("inconsistent"));
                Assert.That(Directory.Exists(batch.FolderPath), Is.True);
                Assert.That(Directory.Exists(rogue.FolderPath), Is.True);
                Assert.That(Directory.Exists(history.RecycleRoot), Is.False,
                    "Corrupt discovered membership must be rejected before creating the recycle folder.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, true);
            }
        }

        [Test]
        public void ParameterCapture_PreservesFloatRoundTripPrecisionWithInvariantText()
        {
            SimulationConfig config = ScriptableObject.CreateInstance<SimulationConfig>();
            try
            {
                config.signalPropagationDelay = 1.00000012f;
                Dictionary<string, string> captured = SarSimulationHistory.CaptureParameters(config);
                Assert.That(captured["signalPropagationDelay"],
                    Is.EqualTo(config.signalPropagationDelay.ToString("R", CultureInfo.InvariantCulture)));
                Assert.That(float.Parse(captured["signalPropagationDelay"], CultureInfo.InvariantCulture),
                    Is.EqualTo(config.signalPropagationDelay));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void ArtifactFreshness_RejectsMissingEmptyAndStaleOutputButAllowsClockTolerance()
        {
            string root = Path.Combine(Path.GetTempPath(), "sar-history-freshness-" +
                                                           Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string artifact = Path.Combine(root, "artifact.json");
            DateTime started = DateTime.UtcNow.AddMinutes(-1d);
            string startedUtc = started.ToString("o", CultureInfo.InvariantCulture);
            try
            {
                Assert.That(SarSimulationHistory.TryValidateArtifactSource(
                    Path.Combine(root, "missing.json"), startedUtc, out _), Is.False);

                File.WriteAllText(artifact, string.Empty);
                Assert.That(SarSimulationHistory.TryValidateArtifactSource(
                    artifact, startedUtc, out string emptyError), Is.False);
                Assert.That(emptyError, Does.Contain("empty"));

                File.WriteAllText(artifact, "{}");
                File.SetLastWriteTimeUtc(artifact, started.AddSeconds(-3d));
                Assert.That(SarSimulationHistory.TryValidateArtifactSource(
                    artifact, startedUtc, out string staleError), Is.False);
                Assert.That(staleError, Does.Contain("stale"));

                File.SetLastWriteTimeUtc(artifact, started.AddSeconds(-1d));
                Assert.That(SarSimulationHistory.TryValidateArtifactSource(
                    artifact, startedUtc, out string acceptedError), Is.True, acceptedError);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void CompletedRequest_FailsWhenEitherRequiredArtifactIsUnavailable()
        {
            Assert.That(SarSimulationHistory.ResolveFinalRunStatus(
                    SarSimulationRunStatus.Completed, true, true),
                Is.EqualTo(SarSimulationRunStatus.Completed));
            Assert.That(SarSimulationHistory.ResolveFinalRunStatus(
                    SarSimulationRunStatus.Completed, false, true),
                Is.EqualTo(SarSimulationRunStatus.Failed));
            Assert.That(SarSimulationHistory.ResolveFinalRunStatus(
                    SarSimulationRunStatus.Completed, true, false),
                Is.EqualTo(SarSimulationRunStatus.Failed));
            Assert.That(SarSimulationHistory.ResolveFinalRunStatus(
                    SarSimulationRunStatus.Cancelled, false, false),
                Is.EqualTo(SarSimulationRunStatus.Cancelled));
        }

        [Test]
        public void Recovery_InterruptsOnlyAbandonedRunningManifests()
        {
            const string completedUtc = "2026-08-27T00:01:00.0000000Z";
            var abandoned = new SarSimulationRunManifest
            {
                RunId = "abandoned",
                StartedUtc = "2026-08-27T00:00:00.0000000Z",
                Status = SarSimulationRunStatus.Running
            };
            Assert.That(SarSimulationHistory.RecoverAbandonedRun(
                abandoned, "active-run", "active-batch", completedUtc), Is.True);
            Assert.That(abandoned.Status, Is.EqualTo(SarSimulationRunStatus.Interrupted));
            Assert.That(abandoned.CompletedUtc, Is.EqualTo(completedUtc));
            Assert.That(abandoned.Duration, Is.EqualTo(60d).Within(0.001d));

            var activeRun = new SarSimulationRunManifest
                { RunId = "active-run", Status = SarSimulationRunStatus.Running };
            Assert.That(SarSimulationHistory.RecoverAbandonedRun(
                activeRun, "active-run", null, completedUtc), Is.False);
            Assert.That(activeRun.Status, Is.EqualTo(SarSimulationRunStatus.Running));

            var activeBatch = new SarSimulationRunManifest
                { RunId = "active-batch", Status = SarSimulationRunStatus.Running };
            Assert.That(SarSimulationHistory.RecoverAbandonedRun(
                activeBatch, null, "active-batch", completedUtc), Is.False);
            Assert.That(activeBatch.Status, Is.EqualTo(SarSimulationRunStatus.Running));
        }

        [Test]
        public void BatchCompletionProblems_RejectMissingOrNonCompletedChildren()
        {
            var batch = new SarSimulationRunManifest
            {
                RunId = "batch",
                Kind = SarSimulationRunKind.Batch,
                BatchCount = 3,
                ChildRunIds = new List<string> { "ok", "failed", "missing" }
            };
            SarSimulationRunManifest ok = Run("ok", 10d, "0", 1d, 1d);
            SarSimulationRunManifest failed = Run("failed", 10d, "0", 1d, 1d);
            failed.Status = SarSimulationRunStatus.Failed;

            List<string> problems = SarSimulationHistory.FindBatchCompletionProblems(
                batch, new[] { ok, failed });
            Assert.That(problems.Exists(item => item.Contains("failed is Failed")), Is.True);
            Assert.That(problems.Exists(item => item.Contains("missing is missing")), Is.True);

            SarSimulationRunManifest missing = Run("missing", 10d, "0", 1d, 1d);
            Assert.That(SarSimulationHistory.FindBatchCompletionProblems(
                batch, new[] { ok, missing, Run("failed", 10d, "0", 1d, 1d) }), Is.Empty);
        }

        [Test]
        public void TopologyFingerprint_IsStableAndChangesWithTopology()
        {
            var network = new Network { NetworkId = "axis" };
            network.Nodes.Add(new Node { NodeId = "n1", Position = new Vector3(1f, 2f, 3f) });
            var vertiports = new List<Vertiport> { new Vertiport { VertiportId = "v1" } };

            string first = SarSimulationHistory.ComputeTopologyFingerprint(network, vertiports);
            string second = SarSimulationHistory.ComputeTopologyFingerprint(network, vertiports);
            Assert.That(first, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(second, Is.EqualTo(first));

            network.Nodes[0].Position = new Vector3(1f, 2f, 4f);
            string changed = SarSimulationHistory.ComputeTopologyFingerprint(network, vertiports);
            Assert.That(changed, Is.Not.EqualTo(first));
        }

        [Test]
        public void RecordingArchive_GzipRoundTripLoadsRecordingWithoutChangingSource()
        {
            string root = Path.Combine(Path.GetTempPath(), "sar-history-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "SimulationRecording.json");
            string archive = Path.Combine(root, "SimulationRecording.json.gz");
            try
            {
                var recording = new SimulationRecording
                {
                    Stats = new SimulationRecordingStats { TotalSimTime = 12.5f, RecordedFrames = 7 },
                    AircraftTrajectories = new List<AircraftTrajectoryRecord>()
                };
                string sourceJson = JsonConvert.SerializeObject(recording);
                File.WriteAllText(source, sourceJson);

                Assert.That(SarSimulationHistory.TryCompressRecording(source, archive, out string error),
                    Is.True, error);
                Assert.That(File.Exists(source), Is.True, "Archiving must never move or delete Output files.");
                Assert.That(File.Exists(archive), Is.True);

                SimulationRecording restored = SarSimulationHistory.LoadRecordingFile(archive);
                Assert.That(restored, Is.Not.Null);
                Assert.That(restored.Stats.TotalSimTime, Is.EqualTo(12.5f).Within(0.001f));
                Assert.That(restored.Stats.RecordedFrames, Is.EqualTo(7));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void CompactHistoricalLoader_SkipsUnusedSeriesCountsEntriesAndDownsamplesTracks()
        {
            string root = Path.Combine(Path.GetTempPath(), "sar-compact-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "SimulationRecording.json");
            string archive = Path.Combine(root, "SimulationRecording.json.gz");
            try
            {
                var recording = new SimulationRecording
                {
                    LaneTimeSeries = new List<LaneTimeSeriesRecord>
                    {
                        new LaneTimeSeriesRecord
                        {
                            LaneId = "L1",
                            Snapshots = new List<LaneSnapshot>
                            {
                                new LaneSnapshot { Time = 0f, AircraftIds = new List<string> { "A" } },
                                new LaneSnapshot { Time = 1f, AircraftIds = new List<string>() },
                                new LaneSnapshot { Time = 2f, AircraftIds = new List<string> { "A", "B" } }
                            }
                        },
                        new LaneTimeSeriesRecord
                        {
                            LaneId = "L0",
                            Snapshots = new List<LaneSnapshot>
                            {
                                new LaneSnapshot { Time = 0f, AircraftIds = new List<string>() },
                                new LaneSnapshot { Time = 1f, AircraftIds = new List<string>() }
                            }
                        }
                    },
                    CorridorTimeSeries = new List<CorridorTimeSeriesRecord>
                    {
                        new CorridorTimeSeriesRecord { CorridorId = "unused", Snapshots = new List<CorridorSnapshot>() }
                    },
                    AircraftTrajectories = new List<AircraftTrajectoryRecord>
                    {
                        new AircraftTrajectoryRecord
                        {
                            AircraftId = "A",
                            MissionCompleted = true,
                            Positions = new List<Vector3>
                            {
                                Vector3.zero, Vector3.right, Vector3.right * 2f,
                                Vector3.right * 3f, Vector3.right * 4f
                            }
                        }
                    }
                };
                File.WriteAllText(source, JsonConvert.SerializeObject(recording, new Vector3Converter()));
                Assert.That(SarSimulationHistory.TryCompressRecording(source, archive, out string error),
                    Is.True, error);
                SarHistoricalVisualizationData compact =
                    SarSimulationHistory.LoadHistoricalVisualizationData(archive, null, 3);
                Assert.That(compact.LaneTraversals["L1"], Is.EqualTo(3f));
                Assert.That(compact.LaneTraversals.ContainsKey("L0"), Is.False);
                Assert.That(compact.SourceTrajectoryCount, Is.EqualTo(1));
                Assert.That(compact.CompletedTrajectoryCount, Is.EqualTo(1));
                Assert.That(compact.ExcludedIncompleteTrajectoryCount, Is.Zero);
                Assert.That(compact.Trajectories.Single().Count, Is.EqualTo(3));
                Assert.That(compact.Trajectories.Single().First(), Is.EqualTo(Vector3.zero));
                Assert.That(compact.Trajectories.Single().Last(), Is.EqualTo(Vector3.right * 4f));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void CompactHistoricalLoader_OnlyRetainsCompletedMissionTracks()
        {
            string root = Path.Combine(Path.GetTempPath(), "sar-complete-tracks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "SimulationRecording.json");
            try
            {
                List<Vector3> pathPoints = new List<Vector3> { Vector3.zero, Vector3.right };
                var recording = new SimulationRecording
                {
                    AircraftTrajectories = new List<AircraftTrajectoryRecord>
                    {
                        new AircraftTrajectoryRecord
                        {
                            AircraftId = "complete-new", MissionCompleted = true,
                            Positions = new List<Vector3>(pathPoints)
                        },
                        new AircraftTrajectoryRecord
                        {
                            AircraftId = "unfinished-new", MissionCompleted = false,
                            Positions = new List<Vector3>(pathPoints)
                        },
                        new AircraftTrajectoryRecord
                        {
                            AircraftId = "complete-legacy", MissionCompleted = null,
                            Positions = new List<Vector3>
                                { Vector3.zero, Vector3.right, Vector3.right * 2f },
                            Times = new List<float> { 0f, 1f, 2f }
                        },
                        new AircraftTrajectoryRecord
                        {
                            AircraftId = "unknown-legacy", MissionCompleted = null,
                            Positions = new List<Vector3>(pathPoints)
                        },
                        new AircraftTrajectoryRecord
                        {
                            AircraftId = "complete-legacy-without-times", MissionCompleted = null,
                            Positions = new List<Vector3>(pathPoints)
                        }
                    },
                    Events = new List<SimulationEvent>
                    {
                        new SimulationEvent
                        {
                            EventType = "PhaseChange",
                            AircraftId = "complete-legacy",
                            Description = "Mission SUCCESS",
                            Time = 1f
                        },
                        new SimulationEvent
                        {
                            EventType = "PhaseChange",
                            AircraftId = "complete-legacy-without-times",
                            Description = "Mission SUCCESS",
                            Time = 1f
                        }
                    }
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(recording, new Vector3Converter()));

                SarHistoricalVisualizationData compact =
                    SarSimulationHistory.LoadHistoricalVisualizationData(path, null, 8);

                Assert.That(compact.SourceTrajectoryCount, Is.EqualTo(5));
                Assert.That(compact.CompletedTrajectoryCount, Is.EqualTo(2));
                Assert.That(compact.ExcludedIncompleteTrajectoryCount, Is.EqualTo(2));
                Assert.That(compact.ExcludedUnverifiableTrajectoryCount, Is.EqualTo(1));
                Assert.That(compact.Trajectories.Count, Is.EqualTo(2));
                Assert.That(compact.RetainedTrajectoryPointCount, Is.EqualTo(4),
                    "The legacy trajectory must be cut at its last successful completion time.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static SarSimulationRunManifest Run(string id, double duration, string factor,
            double totalMissions, double averageDuration,
            params KeyValuePair<string, double>[] additionalMetrics)
        {
            var run = new SarSimulationRunManifest
            {
                RunId = id,
                SceneName = "Scene",
                Kind = SarSimulationRunKind.Single,
                Duration = duration,
                Status = SarSimulationRunStatus.Completed
            };
            run.Parameters["signalPropagationDelay"] = factor;
            run.Parameters["maxSpeed"] = "20";
            run.Metrics["Total Missions"] = totalMissions;
            run.Metrics["Average Mission Duration (s)"] = averageDuration;
            foreach (KeyValuePair<string, double> metric in additionalMetrics)
                run.Metrics[metric.Key] = metric.Value;
            return run;
        }

        private static SarSimulationRunManifest WriteHistoryManifest(SarSimulationHistory history,
            string runId, SarSimulationRunKind kind, string batchId = null)
        {
            string folder = Path.Combine(history.HistoryRoot, runId);
            Directory.CreateDirectory(folder);
            var manifest = new SarSimulationRunManifest
            {
                RunId = runId,
                SceneId = history.CurrentSceneId,
                SceneName = history.CurrentSceneName,
                Kind = kind,
                BatchId = batchId,
                StartedUtc = DateTime.UtcNow.AddMinutes(-1d).ToString("o", CultureInfo.InvariantCulture),
                CompletedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Status = SarSimulationRunStatus.Completed,
                FolderPath = folder,
                ManifestPath = Path.Combine(folder, SarSimulationHistory.ManifestFileName)
            };
            File.WriteAllText(manifest.ManifestPath, SarSimulationHistory.ToJson(manifest));
            return manifest;
        }
    }
}
