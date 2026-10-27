using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public sealed class SarHistoryVisualizationTests
    {
        [Test]
        public void HistoricalHeatmap_CountsEntriesAndRepeatedReentryPerRun()
        {
            var managerRoot = new GameObject("Historical heatmap manager");
            var visualizerRoot = new GameObject("Historical heatmap visualizer");
            managerRoot.SetActive(false);
            visualizerRoot.SetActive(false);
            try
            {
                SimulationManager manager = managerRoot.AddComponent<SimulationManager>();
                manager.Lanes["L1"] = new Lane { LaneId = "L1", LayerId = "Layer-A" };
                RuntimeAirspaceVisualizer visualizer = visualizerRoot.AddComponent<RuntimeAirspaceVisualizer>();
                typeof(RuntimeAirspaceVisualizer)
                    .GetField("manager", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(visualizer, manager);
                visualizer.SetHistoricalTopology(new Network
                {
                    Edges = new List<Edge>
                    {
                        new Edge
                        {
                            EdgeId = "E1",
                            LayerId = "Layer-A",
                            Lanes = new List<Lane>
                            {
                                new Lane
                                {
                                    LaneId = "L1",
                                    LayerId = "Layer-A",
                                    Centerline = new List<Vector3> { Vector3.zero, Vector3.right }
                                }
                            }
                        }
                    }
                });

                SimulationRecording firstRun = Recording("L1",
                    new[] { "A", "B" }, new[] { "A" }, new[] { "A", "C" },
                    Array.Empty<string>(), new[] { "A" });
                SimulationRecording secondRun = Recording("L1",
                    new[] { "D" }, Array.Empty<string>(), new[] { "D" });
                visualizer.SetHistoricalRecordings(new[] { firstRun, secondRun });

                var loads = (Dictionary<string, float>)typeof(RuntimeAirspaceVisualizer)
                    .GetField("historicalLaneLoads", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(visualizer);
                // First run: A+B, C, then A re-entry = 4. Second run: D twice = 2.
                Assert.That(loads["L1"], Is.EqualTo(3f).Within(0.0001f));
                Assert.That(visualizer.HistoricalRecordedLaneCount, Is.EqualTo(1));
                Assert.That(visualizer.HistoricalMatchedLaneCount, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(visualizerRoot);
                UnityEngine.Object.DestroyImmediate(managerRoot);
            }
        }

        [Test]
        public void LegacySnapshotsWithoutAircraftIds_UsePositiveOccupancyIncrements()
        {
            var snapshots = new[]
            {
                new LaneSnapshot { Time = 0f, AircraftCount = 0, AircraftIds = null },
                new LaneSnapshot { Time = 1f, AircraftCount = 2, AircraftIds = null },
                new LaneSnapshot { Time = 2f, AircraftCount = 1, AircraftIds = null },
                new LaneSnapshot { Time = 3f, AircraftCount = 3, AircraftIds = null }
            };
            Assert.That(SarSimulationHistory.CountLaneTraversals(snapshots), Is.EqualTo(4f));
        }

        [Test]
        public void ResourceHeat_UsesSumOfEveryReservationWindow()
        {
            string path = Path.Combine(Path.GetTempPath(), "sar-occupancy-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var export = new OccupancyExport
                {
                    Resources = new List<ResourceOccupancyLog>
                    {
                        new ResourceOccupancyLog
                        {
                            ResourceId = "Ramp-1",
                            Occupancies = new List<AircraftOccupancy>
                            {
                                new AircraftOccupancy
                                {
                                    AircraftId = "A",
                                    TimeWindows = new List<TimeWindowEntry>
                                    {
                                        new TimeWindowEntry { StartTime = 2f, EndTime = 7f },
                                        new TimeWindowEntry { StartTime = 9f, EndTime = 10.5f }
                                    }
                                }
                            }
                        },
                        new ResourceOccupancyLog
                        {
                            ResourceId = "Unused-Resource",
                            Occupancies = new List<AircraftOccupancy>()
                        }
                    }
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(export));
                Dictionary<string, float> totals = SarSimulationHistory.LoadResourceReservationSeconds(path);
                Assert.That(totals["Ramp-1"], Is.EqualTo(6.5f).Within(0.0001f));
                Assert.That(totals.ContainsKey("Unused-Resource"), Is.False);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void HistoricalMode_RemainsActiveWhenHeatValuesAreEmptyAndDropsZeroValues()
        {
            var visualizerRoot = new GameObject("Empty historical heatmap visualizer");
            visualizerRoot.SetActive(false);
            try
            {
                RuntimeAirspaceVisualizer visualizer = visualizerRoot.AddComponent<RuntimeAirspaceVisualizer>();
                visualizer.SetHistoricalVisualizationData(new SarHistoricalVisualizationData
                {
                    LaneTraversals = new Dictionary<string, float> { ["L0"] = 0f },
                    ResourceReservationSeconds = new Dictionary<string, float> { ["R0"] = 0f }
                });

                var laneLoads = (Dictionary<string, float>)typeof(RuntimeAirspaceVisualizer)
                    .GetField("historicalLaneLoads", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(visualizer);
                var resourceLoads = (Dictionary<string, float>)typeof(RuntimeAirspaceVisualizer)
                    .GetField("historicalResourceLoads", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(visualizer);
                Assert.That(visualizer.HistoricalModeActive, Is.True);
                Assert.That(visualizer.HasHistoricalAnalysis, Is.True);
                Assert.That(laneLoads, Is.Empty);
                Assert.That(resourceLoads, Is.Empty);

                visualizer.ClearHistoricalAnalysis();
                Assert.That(visualizer.HistoricalModeActive, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(visualizerRoot);
            }
        }

        [Test]
        public void HistoricalResourcePeak_UsesOnlyDrawableArchivedApproachEdgesAndRamps()
        {
            var visualizerRoot = new GameObject("Historical ground heatmap visualizer");
            visualizerRoot.SetActive(false);
            try
            {
                RuntimeAirspaceVisualizer visualizer = visualizerRoot.AddComponent<RuntimeAirspaceVisualizer>();
                visualizer.SetHistoricalGroundGeometry(new[]
                {
                    new Vertiport
                    {
                        VertiportId = "VP1",
                        ApproachNet = new ApproachNetwork
                        {
                            Nodes = new List<ApproachNode>
                            {
                                new ApproachNode { NodeId = "A", Position = Vector3.zero },
                                new ApproachNode { NodeId = "B", Position = Vector3.right }
                            },
                            Edges = new List<ApproachEdge>
                            {
                                new ApproachEdge { EdgeId = "Approach-Valid", FromNodeId = "A", ToNodeId = "B" },
                                new ApproachEdge { EdgeId = "Approach-Broken", FromNodeId = "A", ToNodeId = "Missing" }
                            }
                        },
                        HoldingStructures = new List<HoldingStructure>
                        {
                            new HoldingStructure
                            {
                                HoldingStructureId = "HS1",
                                Vertices = new List<Vertex>
                                {
                                    new Vertex { VertexId = "V1", Position = Vector3.zero },
                                    new Vertex { VertexId = "V2", Position = Vector3.forward }
                                },
                                Ramps = new List<Ramp>
                                {
                                    new Ramp { RampId = "Ramp-Valid", FromVertexId = "V1", ToVertexId = "V2" }
                                }
                            }
                        }
                    }
                });
                visualizer.SetHistoricalVisualizationData(new SarHistoricalVisualizationData
                {
                    ResourceReservationSeconds = new Dictionary<string, float>
                    {
                        ["Approach-Valid"] = 5f,
                        ["Ramp-Valid"] = 3f,
                        ["Approach-Broken"] = 500f,
                        ["Invisible-Resource"] = 1000f
                    }
                });

                float peak = (float)typeof(RuntimeAirspaceVisualizer)
                    .GetField("historicalPeakResourceLoad", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(visualizer);
                int drawableCount = (int)typeof(RuntimeAirspaceVisualizer)
                    .GetField("historicalDrawableResourceCount", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(visualizer);
                Assert.That(peak, Is.EqualTo(5f));
                Assert.That(drawableCount, Is.EqualTo(2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(visualizerRoot);
            }
        }

        [Test]
        public void HistoricalHeatColor_UsesGreenYellowRedStops()
        {
            MethodInfo method = typeof(RuntimeAirspaceVisualizer)
                .GetMethod("HeatColor", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var low = (Color)method.Invoke(null, new object[] { 0f });
            var middle = (Color)method.Invoke(null, new object[] { 0.5f });
            var high = (Color)method.Invoke(null, new object[] { 1f });

            Assert.That(low.g, Is.GreaterThan(low.r));
            Assert.That(middle.r, Is.GreaterThan(0.9f));
            Assert.That(middle.g, Is.GreaterThan(0.7f));
            Assert.That(high.r, Is.GreaterThan(high.g));
        }

        [Test]
        public void HistoricalPeakLaneLoad_UsesPageLocalVisibilityWithoutMutatingLegacySnapshot()
        {
            var visualizerRoot = new GameObject("Visible historical peak visualizer");
            visualizerRoot.SetActive(false);
            try
            {
                RuntimeAirspaceVisualizer visualizer = visualizerRoot.AddComponent<RuntimeAirspaceVisualizer>();
                visualizer.SetHistoricalTopology(new Network
                {
                    AltitudeLayers = new List<AltitudeLayer>
                    {
                        new AltitudeLayer { LayerId = "Visible", Enabled = true },
                        new AltitudeLayer { LayerId = "Hidden", Enabled = false }
                    },
                    Edges = new List<Edge>
                    {
                        new Edge
                        {
                            EdgeId = "EV", LayerId = "Visible", Lanes = new List<Lane>
                            {
                                new Lane { LaneId = "LV", LayerId = "Visible",
                                    Centerline = new List<Vector3> { Vector3.zero, Vector3.right } }
                            }
                        },
                        new Edge
                        {
                            EdgeId = "EH", LayerId = "Hidden", Lanes = new List<Lane>
                            {
                                new Lane { LaneId = "LH", LayerId = "Hidden",
                                    Centerline = new List<Vector3> { Vector3.zero, Vector3.forward } }
                            }
                        }
                    }
                });
                visualizer.SetHistoricalVisualizationData(new SarHistoricalVisualizationData
                {
                    LaneTraversals = new Dictionary<string, float>
                    {
                        ["LV"] = 2f,
                        ["LH"] = 100f
                    }
                });

                Assert.That(visualizer.HistoricalPeakLaneLoad, Is.EqualTo(100f),
                    "A legacy Enabled=false snapshot must not permanently hide an archived layer.");
                visualizer.SetDisplayLayerVisible("Hidden", false);
                Assert.That(visualizer.HistoricalPeakLaneLoad, Is.EqualTo(2f));
                visualizer.SetVisible(AirspaceVisualLayer.Network, false);
                Assert.That(visualizer.HistoricalPeakLaneLoad, Is.Zero);
                visualizer.SetVisible(AirspaceVisualLayer.Network, true);
                Assert.That(visualizer.HistoricalPeakLaneLoad, Is.EqualTo(2f));
                Assert.That(visualizer.GetHeatmapAltitudeLayers().Single(layer =>
                    layer.LayerId == "Hidden").Enabled, Is.False,
                    "The page-local overlay must not rewrite archived authoring metadata.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(visualizerRoot);
            }
        }

        [Test]
        public void HeatmapLayerFilter_IsTransientAndControlsHistoricalPeak()
        {
            var visualizerRoot = new GameObject("Transient heatmap layer filter");
            visualizerRoot.SetActive(false);
            try
            {
                var layer = new AltitudeLayer
                    { LayerId = "Upper", DisplayName = "Upper", NominalAltitude = 150f, Enabled = true };
                RuntimeAirspaceVisualizer visualizer = visualizerRoot.AddComponent<RuntimeAirspaceVisualizer>();
                visualizer.SetHistoricalTopology(new Network
                {
                    AltitudeLayers = new List<AltitudeLayer> { layer },
                    Edges = new List<Edge>
                    {
                        new Edge
                        {
                            EdgeId = "EU", LayerId = layer.LayerId,
                            Lanes = new List<Lane>
                            {
                                new Lane
                                {
                                    LaneId = "LU", LayerId = layer.LayerId,
                                    Centerline = new List<Vector3> { Vector3.zero, Vector3.right }
                                }
                            }
                        }
                    }
                });
                visualizer.SetHistoricalVisualizationData(new SarHistoricalVisualizationData
                {
                    LaneTraversals = new Dictionary<string, float> { ["LU"] = 12f }
                });

                Assert.That(visualizer.HistoricalPeakLaneLoad, Is.EqualTo(12f));
                Assert.That(visualizer.GetHeatmapAltitudeLayers().Select(item => item.LayerId),
                    Is.EqualTo(new[] { "Upper" }));
                visualizer.SetHeatmapLayerVisible("Upper", false);
                Assert.That(visualizer.IsHeatmapLayerVisible("Upper"), Is.False);
                Assert.That(visualizer.HistoricalPeakLaneLoad, Is.Zero);
                Assert.That(layer.Enabled, Is.True,
                    "A temporary analytics filter must never change persisted layer visibility.");

                visualizer.ResetHeatmapLayerVisibility();
                Assert.That(visualizer.IsHeatmapLayerVisible("Upper"), Is.True);
                Assert.That(visualizer.HistoricalPeakLaneLoad, Is.EqualTo(12f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(visualizerRoot);
            }
        }

        [Test]
        public void HeatmapLayerFilter_LegacyDisabledSnapshotLayerRemainsSelectableWithoutMutation()
        {
            var visualizerRoot = new GameObject("Authored heatmap visibility");
            visualizerRoot.SetActive(false);
            try
            {
                RuntimeAirspaceVisualizer visualizer =
                    visualizerRoot.AddComponent<RuntimeAirspaceVisualizer>();
                visualizer.SetHistoricalTopology(new Network
                {
                    AltitudeLayers = new List<AltitudeLayer>
                    {
                        new AltitudeLayer { LayerId = "Enabled", Enabled = true },
                        new AltitudeLayer { LayerId = "Disabled", Enabled = false }
                    }
                });

                visualizer.SetHeatmapLayerVisible("Disabled", true);

                Assert.That(visualizer.IsHeatmapLayerVisible("Disabled"), Is.True,
                    "Archived Enabled is legacy authoring metadata, not a permanent analytics filter.");
                Assert.That(visualizer.GetHeatmapAltitudeLayers().Select(item => item.LayerId),
                    Is.EquivalentTo(new[] { "Enabled", "Disabled" }),
                    "Every archived layer must remain available to the page-local visibility checklist.");
                Assert.That(visualizer.GetHeatmapAltitudeLayers().Single(item =>
                    item.LayerId == "Disabled").Enabled, Is.False,
                    "Presentational selection must not rewrite the archived snapshot field.");

                visualizer.SetDisplayLayerVisible("Disabled", false);
                Assert.That(visualizer.IsPerLayerVisible("Disabled"), Is.False);
                Assert.That(visualizer.GetHeatmapAltitudeLayers().Single(item =>
                    item.LayerId == "Disabled").Enabled, Is.False,
                    "The page-local display overlay must remain transient.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(visualizerRoot);
            }
        }

        [Test]
        public void LiveHeatmapLayers_LegacyAxisPlaceholderFallsBackToCompleteFullNetwork()
        {
            var root = new GameObject("Legacy live heatmap layers");
            root.SetActive(false);
            try
            {
                SimulationManager simulation = root.AddComponent<SimulationManager>();
                NetworkManager networkManager = root.AddComponent<NetworkManager>();
                networkManager.AxisNetwork = new Network
                {
                    AltitudeLayers = new List<AltitudeLayer>
                    {
                        new AltitudeLayer
                        {
                            LayerId = "L0", DisplayName = "Default Layer", NominalAltitude = 75f
                        }
                    }
                };
                networkManager.FullNetwork = new Network
                {
                    AltitudeLayers = new List<AltitudeLayer>
                    {
                        new AltitudeLayer
                        {
                            LayerId = "L0", DisplayName = "Authored Low Level", NominalAltitude = 75f
                        },
                        new AltitudeLayer
                        {
                            LayerId = "L1", DisplayName = "Reviewer Observation Level", NominalAltitude = 125f
                        }
                    }
                };
                simulation.networkManager = networkManager;

                RuntimeAirspaceVisualizer visualizer = root.AddComponent<RuntimeAirspaceVisualizer>();
                typeof(RuntimeAirspaceVisualizer)
                    .GetField("manager", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(visualizer, simulation);

                IReadOnlyList<AltitudeLayer> layers = visualizer.GetHeatmapAltitudeLayers();
                Assert.That(layers.Select(layer => layer.LayerId), Is.EqualTo(new[] { "L0", "L1" }));
                Assert.That(layers[0].DisplayName, Is.EqualTo("Authored Low Level"),
                    "The constructor-only Axis L0 must not replace the authored FullNetwork metadata.");
                Assert.That(layers[1].DisplayName, Is.EqualTo("Reviewer Observation Level"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void HistoricalLoadData_DoesNotInjectTrajectoryPreviews()
        {
            var visualizerRoot = new GameObject("Load-only historical visualization");
            visualizerRoot.SetActive(false);
            try
            {
                RuntimeAirspaceVisualizer visualizer =
                    visualizerRoot.AddComponent<RuntimeAirspaceVisualizer>();
                visualizer.SetHistoricalTopology(NetworkWithLane("Layer", "Lane", Vector3.right));

                visualizer.SetHistoricalLoadData(new SarHistoricalVisualizationData
                {
                    LaneTraversals = new Dictionary<string, float> { ["Lane"] = 3f },
                    Trajectories = new List<List<Vector3>>
                    {
                        new List<Vector3> { Vector3.zero, Vector3.one }
                    }
                });

                var previews = (List<List<Vector3>>)typeof(RuntimeAirspaceVisualizer)
                    .GetField("routePreviews", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(visualizer);
                Assert.That(previews, Is.Empty,
                    "The Python heat-map path must load traffic values without unlisted 3D tracks.");
                Assert.That(visualizer.HistoricalPeakLaneLoad, Is.EqualTo(3f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(visualizerRoot);
            }
        }

        [Test]
        public void WorkbenchSceneSwitchAndExplicitClear_ResetTransientHeatmapLayerState()
        {
            var root = new GameObject("Heatmap filter lifecycle");
            root.SetActive(false);
            try
            {
                SarRuntimeWorkbench workbench = root.AddComponent<SarRuntimeWorkbench>();
                RuntimeAirspaceVisualizer visualizer = root.AddComponent<RuntimeAirspaceVisualizer>();
                typeof(SarRuntimeWorkbench)
                    .GetField("visualizer", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(workbench, visualizer);
                Network topology = NetworkWithLane("Shared-Layer", "Lane", Vector3.right);
                visualizer.SetHistoricalTopology(topology);
                visualizer.SetHeatmapLayerVisible("Shared-Layer", false);

                typeof(SarRuntimeWorkbench)
                    .GetMethod("HandleActiveSceneChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(workbench, null);
                visualizer.SetHistoricalTopology(topology);
                Assert.That(visualizer.IsHeatmapLayerVisible("Shared-Layer"), Is.True,
                    "A layer ID reused by another scene must not inherit the old scene's filter.");

                visualizer.SetHeatmapLayerVisible("Shared-Layer", false);
                typeof(SarRuntimeWorkbench)
                    .GetMethod("ClearLoadedHistoricalResult", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(workbench, new object[] { true });
                visualizer.SetHistoricalTopology(topology);
                Assert.That(visualizer.IsHeatmapLayerVisible("Shared-Layer"), Is.True,
                    "An explicit historical-result clear must restore the default layer filter.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ConsecutiveHistoricalResults_SecondRunFullyReplacesFirstRunHeatAndRoutes()
        {
            var visualizerRoot = new GameObject("Sequential historical result replacement");
            visualizerRoot.SetActive(false);
            try
            {
                RuntimeAirspaceVisualizer visualizer = visualizerRoot.AddComponent<RuntimeAirspaceVisualizer>();
                visualizer.SetHistoricalTopology(NetworkWithLane("Layer-A", "Lane-A", Vector3.right));
                visualizer.SetHistoricalVisualizationData(new SarHistoricalVisualizationData
                {
                    LaneTraversals = new Dictionary<string, float> { ["Lane-A"] = 99f },
                    Trajectories = new List<List<Vector3>>
                    {
                        new List<Vector3> { Vector3.zero, Vector3.right }
                    }
                });

                visualizer.SetHistoricalTopology(NetworkWithLane("Layer-B", "Lane-B", Vector3.forward));
                visualizer.SetHistoricalVisualizationData(new SarHistoricalVisualizationData
                {
                    LaneTraversals = new Dictionary<string, float> { ["Lane-B"] = 7f },
                    Trajectories = new List<List<Vector3>>
                    {
                        new List<Vector3> { Vector3.one, Vector3.one + Vector3.forward },
                        new List<Vector3> { Vector3.one, Vector3.one + Vector3.up }
                    }
                });

                var laneLoads = (Dictionary<string, float>)typeof(RuntimeAirspaceVisualizer)
                    .GetField("historicalLaneLoads", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(visualizer);
                var lanes = (Dictionary<string, Lane>)typeof(RuntimeAirspaceVisualizer)
                    .GetField("historicalLanes", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(visualizer);
                var previews = (List<List<Vector3>>)typeof(RuntimeAirspaceVisualizer)
                    .GetField("routePreviews", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(visualizer);

                Assert.That(laneLoads.Keys, Is.EqualTo(new[] { "Lane-B" }));
                Assert.That(lanes.Keys, Is.EqualTo(new[] { "Lane-B" }));
                Assert.That(previews.Count, Is.EqualTo(2));
                Assert.That(visualizer.HistoricalPeakLaneLoad, Is.EqualTo(7f));
                Assert.That(visualizer.HistoricalMatchedLaneCount, Is.EqualTo(1));
                Assert.That(visualizer.HistoricalRecordedLaneCount, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(visualizerRoot);
            }
        }

        [Test]
        public void VertiportSnapshotLoader_UsesArchivedFallbackFile()
        {
            string root = Path.Combine(Path.GetTempPath(), "sar-vertiport-snapshot-" +
                                                           Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var historyRoot = new GameObject("Historical snapshot loader");
            historyRoot.SetActive(false);
            try
            {
                var snapshot = new List<Vertiport>
                {
                    new Vertiport
                    {
                        VertiportId = "VP-Archived",
                        Boundary = new List<Vector3> { Vector3.zero, Vector3.right, Vector3.forward }
                    }
                };
                File.WriteAllText(Path.Combine(root, SarSimulationHistory.VertiportSnapshotFileName),
                    JsonConvert.SerializeObject(snapshot, new Vector3Converter()));
                var manifest = new SarSimulationRunManifest
                {
                    RunId = "run",
                    FolderPath = root,
                    VertiportSnapshotFile = null
                };
                SarSimulationHistory history = historyRoot.AddComponent<SarSimulationHistory>();

                List<Vertiport> loaded = history.LoadVertiportsSnapshot(manifest);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.Count, Is.EqualTo(1));
                Assert.That(loaded[0].VertiportId, Is.EqualTo("VP-Archived"));
                Assert.That(loaded[0].Boundary[1], Is.EqualTo(Vector3.right));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(historyRoot);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static SimulationRecording Recording(string laneId, params string[][] aircraftIds)
        {
            var snapshots = new List<LaneSnapshot>();
            for (int index = 0; index < aircraftIds.Length; index++)
                snapshots.Add(new LaneSnapshot
                {
                    Time = index,
                    AircraftCount = aircraftIds[index].Length,
                    AircraftIds = new List<string>(aircraftIds[index])
                });
            return new SimulationRecording
            {
                LaneTimeSeries = new List<LaneTimeSeriesRecord>
                {
                    new LaneTimeSeriesRecord { LaneId = laneId, Snapshots = snapshots }
                },
                AircraftTrajectories = new List<AircraftTrajectoryRecord>()
            };
        }

        private static Network NetworkWithLane(string layerId, string laneId, Vector3 end)
        {
            return new Network
            {
                AltitudeLayers = new List<AltitudeLayer>
                {
                    new AltitudeLayer { LayerId = layerId, Enabled = true }
                },
                Edges = new List<Edge>
                {
                    new Edge
                    {
                        EdgeId = "Edge-" + laneId,
                        LayerId = layerId,
                        Lanes = new List<Lane>
                        {
                            new Lane
                            {
                                LaneId = laneId,
                                LayerId = layerId,
                                Centerline = new List<Vector3> { Vector3.zero, end }
                            }
                        }
                    }
                }
            };
        }
    }
}
