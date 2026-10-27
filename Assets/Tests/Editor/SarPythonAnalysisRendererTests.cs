using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public sealed class SarPythonAnalysisRendererTests
    {
        [Test]
        public void RunComparisonRequest_UsesOrderedRunRecordsAsXAndKeepsMultipleYMetrics()
        {
            var batchMember = new SarSimulationRunManifest
            {
                RunId = "run-batch-member-0001",
                SceneName = "Research Scene",
                Kind = SarSimulationRunKind.BatchMember,
                Status = SarSimulationRunStatus.Completed,
                BatchId = "batch-001",
                BatchIndex = 2,
                BatchCount = 4,
                Duration = 100d,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["signalPropagationDelay"] = "0.2"
                },
                Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["Average Delay (s)"] = 4.5d,
                    ["Throughput (flights)"] = 80d
                }
            };
            var independent = new SarSimulationRunManifest
            {
                RunId = "run-independent-0002",
                SceneName = "Research Scene",
                Kind = SarSimulationRunKind.Single,
                Status = SarSimulationRunStatus.Completed,
                Duration = 100d,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["signalPropagationDelay"] = "9.9"
                },
                Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["Average Delay (s)"] = 7.25d,
                    ["Throughput (flights)"] = 125d
                }
            };

            SarPythonPlotRequestDto request = SarPythonAnalysisRenderer.CreateRunComparisonRequest(
                new[] { batchMember, independent },
                new[] { "Average Delay (s)", "Throughput (flights)", "Average Delay (s)" },
                "Selected records");
            JObject payload = JObject.Parse(SarPythonAnalysisRenderer.SerializeRequest(request));

            Assert.That((string)payload["kind"], Is.EqualTo(SarPythonPlotKinds.RunComparison));
            Assert.That(payload["selected_factors"], Is.Null,
                "A bar comparison must not reinterpret a sensitivity factor as its X axis.");
            Assert.That(payload["all_varying_factors"], Is.Null);
            Assert.That(payload["selected_metrics"]?.Values<string>(), Is.EqualTo(new[]
            {
                "Average Delay (s)", "Throughput (flights)"
            }));
            JArray records = (JArray)payload["runs"];
            Assert.That(records, Has.Count.EqualTo(2));
            Assert.That((string)records[0]["run_id"], Is.EqualTo(batchMember.RunId));
            Assert.That((string)records[1]["run_id"], Is.EqualTo(independent.RunId));
            Assert.That((string)records[0]["label"], Is.EqualTo("Batch 2/4"));
            Assert.That((string)records[1]["label"], Is.EqualTo("Run 2/2"));
            Assert.That((string)records[0]["label"], Does.Not.Contain(batchMember.RunId));
            Assert.That((string)records[1]["label"], Does.Not.Contain(independent.RunId));
            Assert.That((string)records[0]["parameters"]["signalPropagationDelay"], Is.EqualTo("0.2"),
                "Parameters remain metadata but do not define the categorical X axis.");
        }

        [Test]
        public void SensitivityRequest_PreservesMultipleYMetricsAndAllVaryingContexts()
        {
            var runs = new[]
            {
                CompletedRun("run-a", "0.1", "1", 4d, 40d),
                CompletedRun("run-b", "0.2", "2", 7d, 70d)
            };
            SarPythonPlotRequestDto request = SarPythonAnalysisRenderer.CreateSensitivityRequest(
                runs,
                new[] { "signalDelay" },
                new[] { "Delay (s)", "Throughput (flights)" },
                "Sensitivity");

            Assert.That(request.Kind, Is.EqualTo(SarPythonPlotKinds.Sensitivity));
            Assert.That(request.SelectedFactors, Is.EqualTo(new[] { "signalDelay" }));
            Assert.That(request.SelectedMetrics, Is.EqualTo(new[]
            {
                "Delay (s)", "Throughput (flights)"
            }));
            Assert.That(request.AllVaryingFactors, Is.EquivalentTo(new[]
            {
                "signalDelay", "positionError"
            }));
            Assert.That(request.Runs, Has.Count.EqualTo(2));
        }

        [Test]
        public void TrafficHeatmapRequest_PreservesExplicitLayerFilterAndSanitizesLoads()
        {
            var network = new Network
            {
                AltitudeLayers = new List<AltitudeLayer>
                {
                    new AltitudeLayer { LayerId = "L0", DisplayName = "Lower", NominalAltitude = 75f },
                    new AltitudeLayer { LayerId = "L1", DisplayName = "Upper", NominalAltitude = 125f }
                },
                LaneConnectionCorridors = new List<LaneConnectionCorridor>
                {
                    new LaneConnectionCorridor { CorridorId = "connector-is-not-a-lane" }
                }
            };
            SarPythonTrafficHeatmapRequestDto request =
                SarPythonAnalysisRenderer.CreateTrafficHeatmapRequest(
                    network,
                    new[] { new Vertiport { VertiportId = "VP0" } },
                    new Dictionary<string, float>
                    {
                        ["lane-a"] = 8f,
                        ["lane-b"] = -2f,
                        ["invalid"] = float.NaN
                    },
                    new Dictionary<string, float> { ["pad-a"] = 12.5f },
                    new[] { "L1", "L1", "L0" },
                    "Archived traffic",
                    0.45f);

            Assert.That(request.Kind, Is.EqualTo(SarPythonPlotKinds.TrafficHeatmap));
            Assert.That(request.Network, Is.SameAs(network));
            Assert.That(request.VisibleLayerIds, Is.EqualTo(new[] { "L1", "L0" }));
            Assert.That(request.LaneTraversals["lane-a"], Is.EqualTo(8f));
            Assert.That(request.LaneTraversals["lane-b"], Is.EqualTo(0f));
            Assert.That(request.LaneTraversals.ContainsKey("invalid"), Is.False);
            Assert.That(request.ResourceReservationSeconds["pad-a"], Is.EqualTo(12.5f));
            Assert.That(request.CorridorTangentRatio, Is.EqualTo(0.45f));
            JObject payload = JObject.Parse(SarPythonAnalysisRenderer.SerializeRequest(request));
            Assert.That((string)payload["kind"], Is.EqualTo("traffic-heatmap"));
            Assert.That(payload["visible_layer_ids"]?.Values<string>(), Is.EqualTo(new[] { "L1", "L0" }));
            Assert.That((float)payload["corridor_tangent_ratio"], Is.EqualTo(0.45f));
        }

        [Test]
        public void TrafficHeatmapResponse_DeserializesOrderedNetworkAndVertiportPages()
        {
            const string json =
                "{\"success\":true,\"output_path\":\"C:/plots/network.png\"," +
                "\"page_manifest_version\":1," +
                "\"page_output_schema\":\"sar.traffic-heatmap.pages/1\"," +
                "\"page_count\":2,\"network_page_count\":1,\"vertiport_page_count\":1," +
                "\"pages\":[" +
                "{\"page_id\":\"network-L0\",\"kind\":\"traffic-heatmap\"," +
                "\"page_type\":\"network-layer\",\"title\":\"Lower Network Layer\"," +
                "\"output_path\":\"C:/plots/network.png\",\"layer_id\":\"L0\"," +
                "\"visible_layer_ids\":[\"L0\"],\"lane_count\":640,\"resource_count\":0}," +
                "{\"page_id\":\"vertiport-VP1\",\"kind\":\"traffic-heatmap\"," +
                "\"page_type\":\"vertiport\",\"title\":\"Vertiport VP1\"," +
                "\"output_path\":\"C:/plots/vertiport.png\",\"vertiport_id\":\"VP1\"," +
                "\"display_name\":\"Vertiport VP1\",\"lane_count\":0,\"resource_count\":17}]}";

            SarSensitivityRenderResult result =
                JsonConvert.DeserializeObject<SarSensitivityRenderResult>(json);

            Assert.That(result.PageOutputSchema, Is.EqualTo("sar.traffic-heatmap.pages/1"));
            Assert.That(result.PageCount, Is.EqualTo(2));
            Assert.That(result.Pages, Has.Count.EqualTo(2));
            Assert.That(result.Pages[0].PageType, Is.EqualTo("network-layer"));
            Assert.That(result.Pages[0].VisibleLayerIds, Is.EqualTo(new[] { "L0" }));
            Assert.That(result.Pages[1].VertiportId, Is.EqualTo("VP1"));
            Assert.That(result.Pages[1].ResourceCount, Is.EqualTo(17));
        }

        [Test]
        public void PackagedWorker_ExtractsCurrentValidatedArchiveToShortCache()
        {
            string analyticsRoot = Path.Combine(Application.streamingAssetsPath, "SARAnalytics");
            MethodInfo prepare = typeof(SarPythonAnalysisRenderer).GetMethod(
                "PrepareArchivedWorker", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(prepare, Is.Not.Null);

            string executable;
            try
            {
                executable = (string)prepare.Invoke(null, new object[] { analyticsRoot });
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }

            Assert.That(File.Exists(executable), Is.True, executable);
            string cacheRoot = Directory.GetParent(Directory.GetParent(executable).FullName).FullName;
            string expectedToolRoot = Path.GetFullPath(SarRuntimePaths.ToolCacheRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.That(Path.GetFullPath(cacheRoot).StartsWith(expectedToolRoot,
                StringComparison.OrdinalIgnoreCase), Is.True);
            Assert.That(executable.Length, Is.LessThan(260),
                "The packaged Python runtime must remain usable without Windows long-path support.");

            JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(
                analyticsRoot, "Worker", "manifest.json")));
            string expectedHash = ((string)manifest["Sha256"])?.Trim().ToLowerInvariant();
            string markerPath = Path.Combine(cacheRoot, ".ready");
            Assert.That(File.Exists(markerPath), Is.True);
            Assert.That(File.ReadAllText(markerPath).Trim().ToLowerInvariant(), Is.EqualTo(expectedHash));
        }

        private static SarSimulationRunManifest CompletedRun(string id, string delay, string error,
            double metricA, double metricB)
        {
            return new SarSimulationRunManifest
            {
                RunId = id,
                Kind = SarSimulationRunKind.Single,
                Status = SarSimulationRunStatus.Completed,
                Duration = 100d,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["signalDelay"] = delay,
                    ["positionError"] = error
                },
                Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["Delay (s)"] = metricA,
                    ["Throughput (flights)"] = metricB
                }
            };
        }
    }
}
