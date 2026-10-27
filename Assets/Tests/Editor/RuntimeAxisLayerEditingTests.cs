using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using NUnit.Framework;
using RoadNetworkEditor.Networking;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;
using UnityEngine.TestTools;

namespace RoadNetworkEditor.Tests
{
    public class RuntimeAxisLayerEditingTests
    {
        [Test]
        public void LegacyLayerJson_MissingVisibilityAndFreezeFields_UsesCompatibleDefaults()
        {
            const string json =
                "{\"NetworkId\":\"LEGACY\",\"AltitudeLayers\":[{\"LayerId\":\"L0\",\"NominalAltitude\":75.0}]}";

            Network network = JsonConvert.DeserializeObject<Network>(json,
                new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });

            Assert.That(network, Is.Not.Null);
            Assert.That(network.AltitudeLayers, Has.Count.EqualTo(1));
            Assert.That(network.AltitudeLayers[0].Enabled, Is.True,
                "Legacy layers without an Enabled field must remain visible.");
            Assert.That(network.AltitudeLayers[0].Frozen, Is.False,
                "Legacy layers without a Frozen field must remain editable.");
        }

        [Test]
        public void NormalizeAxisLayerMembership_AssignsNearestLayerAndLocksAltitude()
        {
            var network = new Network
            {
                AltitudeLayers = new List<AltitudeLayer>
                {
                    new AltitudeLayer { LayerId = "L0", NominalAltitude = 75f },
                    new AltitudeLayer { LayerId = "L1", NominalAltitude = 125f }
                },
                Nodes = new List<Node>
                {
                    new Node { NodeId = "N1", Position = new Vector3(10f, 123f, 20f) },
                    new Node { NodeId = "N2", LayerId = "L1", Position = new Vector3(30f, 127f, 40f) }
                },
                Edges = new List<Edge>
                {
                    new Edge
                    {
                        EdgeId = "E1",
                        LinkNodesId = new List<string> { "N1", "N2" },
                        Centerline = new List<Vector3>
                        {
                            new Vector3(10f, 123f, 20f), new Vector3(30f, 127f, 40f)
                        }
                    }
                }
            };

            MultilayerNetworkUtility.NormalizeAxisLayerMembership(network);

            Assert.That(network.Nodes[0].LayerId, Is.EqualTo("L1"));
            Assert.That(network.Edges[0].LayerId, Is.EqualTo("L1"));
            Assert.That(network.Nodes[0].Position, Is.EqualTo(new Vector3(10f, 125f, 20f)));
            Assert.That(network.Edges[0].Centerline[1], Is.EqualTo(new Vector3(30f, 125f, 40f)));
        }

        [Test]
        public void SetAxisLayerAltitude_PreservesPlanCoordinatesForNodesEdgesAndLanes()
        {
            Network network = BuildTwoLayerNetwork();

            Assert.That(MultilayerNetworkUtility.SetAxisLayerAltitude(network, "L0", 95f), Is.True);

            Assert.That(network.Nodes[0].Position, Is.EqualTo(new Vector3(10f, 95f, 20f)));
            Assert.That(network.Edges[0].Centerline[1], Is.EqualTo(new Vector3(15f, 95f, 25f)));
            Assert.That(network.Edges[0].Lanes[0].Centerline[0], Is.EqualTo(new Vector3(10f, 95f, 21f)));
            Assert.That(network.Edges[0].Lanes[0].LayerId, Is.EqualTo("L0"));
        }

        [Test]
        public void SwapAxisLayerOrder_ExchangesHeightsWithoutChangingPlanCoordinates()
        {
            Network network = BuildTwoLayerNetwork();

            Assert.That(MultilayerNetworkUtility.SwapAxisLayerOrder(network, "L0", 1), Is.True);

            Assert.That(MultilayerNetworkUtility.FindLayer(network, "L0").NominalAltitude, Is.EqualTo(125f));
            Assert.That(MultilayerNetworkUtility.FindLayer(network, "L1").NominalAltitude, Is.EqualTo(75f));
            Assert.That(network.Nodes[0].Position, Is.EqualTo(new Vector3(10f, 125f, 20f)));
            Assert.That(network.Nodes[1].Position, Is.EqualTo(new Vector3(50f, 75f, 60f)));
        }

        [Test]
        public void ReorderAxisLayer_InsertsAtStackIndexAndPreservesPlanCoordinates()
        {
            Network network = BuildTwoLayerNetwork();
            network.AltitudeLayers.Add(new AltitudeLayer { LayerId = "L2", NominalAltitude = 175f });

            Assert.That(MultilayerNetworkUtility.ReorderAxisLayer(network, "L0", 0), Is.True);

            Assert.That(MultilayerNetworkUtility.FindLayer(network, "L0").NominalAltitude, Is.EqualTo(175f));
            Assert.That(MultilayerNetworkUtility.FindLayer(network, "L2").NominalAltitude, Is.EqualTo(125f));
            Assert.That(MultilayerNetworkUtility.FindLayer(network, "L1").NominalAltitude, Is.EqualTo(75f));
            Assert.That(network.Nodes[0].Position, Is.EqualTo(new Vector3(10f, 175f, 20f)));
        }

        [Test]
        public void DuplicateAxisLayer_CopiesNodesEdgesAndIntermediateWaypoints()
        {
            Network network = BuildTwoLayerNetwork();
            network.Edges[0].Centerline.Insert(1, new Vector3(12f, 75f, 23f));
            AltitudeLayer source = MultilayerNetworkUtility.FindLayer(network, "L0");
            source.Enabled = false;
            source.Frozen = true;

            AltitudeLayer copy = MultilayerNetworkUtility.DuplicateAxisLayer(network, "L0", 175f);

            Assert.That(copy, Is.Not.Null);
            Assert.That(copy.Enabled, Is.False, "A duplicate inherits the source visibility.");
            Assert.That(copy.Frozen, Is.False, "A duplicate must be editable even if its source is frozen.");
            List<Node> copiedNodes = network.Nodes.FindAll(node => node.LayerId == copy.LayerId);
            List<Edge> copiedEdges = network.Edges.FindAll(edge => edge.LayerId == copy.LayerId);
            Assert.That(copiedNodes, Has.Count.EqualTo(1));
            Assert.That(copiedEdges, Has.Count.EqualTo(1));
            Assert.That(copiedEdges[0].Centerline, Has.Count.EqualTo(3));
            Assert.That(copiedEdges[0].Centerline[1], Is.EqualTo(new Vector3(12f, 175f, 23f)));
            Assert.That(copiedEdges[0].LinkNodesId[0], Is.EqualTo(copiedNodes[0].NodeId));
        }

        [Test]
        public void DeleteAxisLayer_CascadesNodesEdgesAndWaypoints()
        {
            Network network = BuildTwoLayerNetwork();
            network.Edges[0].Centerline.Insert(1, new Vector3(12f, 75f, 23f));
            network.LaneConnectionCorridors.Add(new LaneConnectionCorridor
            {
                CorridorId = "LC-1", FromLayerId = "L0", ToLayerId = "L1"
            });

            Assert.That(MultilayerNetworkUtility.DeleteAxisLayer(network, "L0"), Is.True);

            Assert.That(network.AltitudeLayers.Exists(layer => layer.LayerId == "L0"), Is.False);
            Assert.That(network.Nodes.Exists(node => node.LayerId == "L0"), Is.False);
            Assert.That(network.Edges.Exists(edge => edge.LayerId == "L0"), Is.False);
            Assert.That(network.Nodes, Has.Count.EqualTo(1));
            Assert.That(network.LaneConnectionCorridors, Is.Empty);
        }

        [Test]
        public void CloneAxisNetwork_PreservesIndependentLayerConnectorSnapshot()
        {
            Network network = BuildTwoLayerNetwork();
            network.AltitudeLayers[0].Enabled = false;
            network.AltitudeLayers[0].Frozen = true;
            network.LaneConnectionCorridors.Add(new LaneConnectionCorridor
            {
                CorridorId = "LC-1",
                FromLaneId = "E0_fwd_0",
                ToLaneId = "E1_fwd_0",
                FromLayerId = "L0",
                ToLayerId = "L1",
                Centerline = new List<Vector3>
                {
                    new Vector3(15f, 75f, 25f), new Vector3(50f, 125f, 60f)
                }
            });

            Network clone = MultilayerNetworkUtility.CloneAxisNetwork(network);

            Assert.That(clone.LaneConnectionCorridors, Has.Count.EqualTo(1));
            Assert.That(clone.LaneConnectionCorridors[0].CorridorId, Is.EqualTo("LC-1"));
            Assert.That(clone.LaneConnectionCorridors[0], Is.Not.SameAs(network.LaneConnectionCorridors[0]));
            Assert.That(clone.LaneConnectionCorridors[0].Centerline,
                Is.Not.SameAs(network.LaneConnectionCorridors[0].Centerline));
            Assert.That(clone.AltitudeLayers[0].Enabled, Is.False);
            Assert.That(clone.AltitudeLayers[0].Frozen, Is.True);
            Assert.That(clone.AltitudeLayers[0], Is.Not.SameAs(network.AltitudeLayers[0]));
        }

        [Test]
        public void FrozenActiveLayer_BlocksMutationToolsAndLayerMetadataChanges()
        {
            Network network = BuildTwoLayerNetwork();
            AltitudeLayer frozen = MultilayerNetworkUtility.FindLayer(network, "L0");
            frozen.DisplayName = "Frozen layer";
            frozen.Frozen = true;
            float originalAltitude = frozen.NominalAltitude;
            int originalLayerCount = network.AltitudeLayers.Count;

            GameObject root = CreateEditorHarness(network, out RuntimeAirspaceEditor editor);
            try
            {
                Assert.That(editor.SetActiveLayer("L0"), Is.True);

                RuntimeEditTool[] mutationTools =
                {
                    RuntimeEditTool.AddNetworkNode,
                    RuntimeEditTool.ConnectNetworkNodes,
                    RuntimeEditTool.AddNetworkWaypoint,
                    RuntimeEditTool.MoveNetworkNode,
                    RuntimeEditTool.MoveNetworkWaypoint
                };
                foreach (RuntimeEditTool tool in mutationTools)
                {
                    editor.SetTool(tool);
                    Assert.That(editor.Tool, Is.EqualTo(RuntimeEditTool.Inspect),
                        $"Frozen layers must reject {tool}.");
                }
                Assert.That(editor.SetActiveLayerAltitude(99f), Is.False);
                Assert.That(editor.RenameActiveLayer("Changed"), Is.False);
                Assert.That(editor.DeleteActiveLayer(), Is.False);
                Assert.That(editor.ReorderActiveLayer(0), Is.False);
                Assert.That(frozen.NominalAltitude, Is.EqualTo(originalAltitude));
                Assert.That(frozen.DisplayName, Is.EqualTo("Frozen layer"));
                Assert.That(network.AltitudeLayers, Has.Count.EqualTo(originalLayerCount));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ReorderActiveLayer_CannotCrossFrozenLayerAndPreservesEveryAltitude()
        {
            Network network = BuildTwoLayerNetwork();
            network.AltitudeLayers.Add(new AltitudeLayer
            {
                LayerId = "L2", DisplayName = "Top", NominalAltitude = 175f
            });
            MultilayerNetworkUtility.FindLayer(network, "L1").Frozen = true;
            Vector3 lowNodePosition = network.Nodes[0].Position;
            Vector3 middleNodePosition = network.Nodes[1].Position;

            GameObject root = CreateEditorHarness(network, out RuntimeAirspaceEditor editor);
            try
            {
                Assert.That(editor.SetActiveLayer("L0"), Is.True);

                Assert.That(editor.ReorderActiveLayer(0), Is.False);

                Assert.That(MultilayerNetworkUtility.FindLayer(network, "L0").NominalAltitude,
                    Is.EqualTo(75f));
                Assert.That(MultilayerNetworkUtility.FindLayer(network, "L1").NominalAltitude,
                    Is.EqualTo(125f));
                Assert.That(MultilayerNetworkUtility.FindLayer(network, "L2").NominalAltitude,
                    Is.EqualTo(175f));
                Assert.That(network.Nodes[0].Position, Is.EqualTo(lowNodePosition));
                Assert.That(network.Nodes[1].Position, Is.EqualTo(middleNodePosition));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void IsNetworkLayerRenderable_RequiresGlobalAndPerLayerVisibility()
        {
            Network network = BuildTwoLayerNetwork();
            GameObject root = new GameObject("Layer visibility test");
            root.SetActive(false);
            try
            {
                NetworkManager networkManager = root.AddComponent<NetworkManager>();
                networkManager.AxisNetwork = network;
                SimulationManager simulation = root.AddComponent<SimulationManager>();
                simulation.networkManager = networkManager;
                RuntimeAirspaceVisualizer visualizer = root.AddComponent<RuntimeAirspaceVisualizer>();
                typeof(RuntimeAirspaceVisualizer).GetField("manager",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(visualizer, simulation);
                AltitudeLayer layer = MultilayerNetworkUtility.FindLayer(network, "L0");

                layer.Enabled = false;
                visualizer.SetVisible(AirspaceVisualLayer.Network, true);
                Assert.That(visualizer.IsNetworkLayerRenderable("L0"), Is.True,
                    "legacy authored Enabled metadata must not hide a page-local view by itself");

                visualizer.SetVisible(AirspaceVisualLayer.Network, false);
                Assert.That(visualizer.IsNetworkLayerRenderable("L0"), Is.False,
                    "global off AND layer on");

                visualizer.SetDisplayLayerVisible("L0", false);
                visualizer.SetVisible(AirspaceVisualLayer.Network, true);
                Assert.That(visualizer.IsNetworkLayerRenderable("L0"), Is.False,
                    "global on AND page-local layer off");

                visualizer.SetVisible(AirspaceVisualLayer.Network, false);
                Assert.That(visualizer.IsNetworkLayerRenderable("L0"), Is.False,
                    "global off AND page-local layer off");
                Assert.That(layer.Enabled, Is.False,
                    "Display-only visibility must not mutate the authored metadata field.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RuntimeWorkspaceSwitch_KeepsLegacyAxisVisualizationSuppressed()
        {
            Network network = BuildTwoLayerNetwork();
            GameObject root = CreateEditorHarness(network, out RuntimeAirspaceEditor editor);
            try
            {
                NetworkManager networkManager = root.GetComponent<SimulationManager>().networkManager;
                networkManager.showFullNetwork = true;

                editor.SetWorkspace(AirspaceEditWorkspace.NetworkLayers, false);
                Assert.That(networkManager.showFullNetwork, Is.False);
                Assert.That(RuntimeOverlayIsActive(networkManager), Is.True);

                editor.SetWorkspace(AirspaceEditWorkspace.ConnectionCorridor, false);
                Assert.That(networkManager.showFullNetwork, Is.False,
                    "Changing edit sections must not restore legacy full-network gizmos.");
                Assert.That(RuntimeOverlayIsActive(networkManager), Is.True,
                    "Changing edit sections must not reactivate legacy Nodes/Edges visuals.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void LayerVisibility_IsTransientWhileFreezeSynchronizesDerivedMetadata()
        {
            Network axis = BuildTwoLayerNetwork();
            Network full = BuildTwoLayerNetwork();
            AltitudeLayer axisLayer = MultilayerNetworkUtility.FindLayer(axis, "L0");
            AltitudeLayer fullLayer = MultilayerNetworkUtility.FindLayer(full, "L0");

            GameObject root = CreateEditorHarness(axis, out RuntimeAirspaceEditor editor);
            try
            {
                root.GetComponent<SimulationManager>().networkManager.FullNetwork = full;

                Assert.That(editor.SetLayerVisible("L0", false), Is.True);
                Assert.That(editor.IsLayerVisible("L0"), Is.False);
                Assert.That(fullLayer.Enabled, Is.True);
                Assert.That(axisLayer.Enabled, Is.True,
                    "An eye toggle must not mutate authored scene metadata.");

                Assert.That(editor.SetLayerFrozen("L0", true), Is.True);
                Assert.That(axisLayer.Frozen, Is.True);
                Assert.That(fullLayer.Frozen, Is.True,
                    "Freeze remains an authored editing constraint and must synchronize.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void NetworkManagerMutationApi_RejectsFrozenLayerChanges()
        {
            Network network = BuildTwoLayerNetwork();
            MultilayerNetworkUtility.FindLayer(network, "L0").Frozen = true;
            network.Edges[0].Centerline.Insert(1, new Vector3(12f, 75f, 23f));
            GameObject root = new GameObject("Frozen NetworkManager guard test");
            root.SetActive(false);
            try
            {
                NetworkManager manager = root.AddComponent<NetworkManager>();
                manager.AxisNetwork = network;
                Vector3 nodePosition = network.Nodes[0].Position;
                int edgeCount = network.Edges.Count;
                int waypointCount = network.Edges[0].Centerline.Count;

                LogAssert.Expect(LogType.Warning, new Regex("Layer 'L0' is frozen; cannot move node"));
                manager.UpdateNodePosition("N0", new Vector3(99f, 75f, 99f));
                LogAssert.Expect(LogType.Warning, new Regex("Layer 'L0' is frozen; cannot add a waypoint"));
                Assert.That(manager.InsertAxisWaypoint("E0", new Vector3(13f, 75f, 24f), 1), Is.False);
                LogAssert.Expect(LogType.Warning, new Regex("Layer 'L0' is frozen; cannot delete edge"));
                manager.DeleteEdge("E0");
                LogAssert.Expect(LogType.Warning, new Regex("Layer 'L0' is frozen; cannot delete node"));
                manager.DeleteNode("N0");

                Assert.That(network.Nodes[0].Position, Is.EqualTo(nodePosition));
                Assert.That(network.Edges, Has.Count.EqualTo(edgeCount));
                Assert.That(network.Edges[0].Centerline, Has.Count.EqualTo(waypointCount));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static GameObject CreateEditorHarness(Network network, out RuntimeAirspaceEditor editor)
        {
            var root = new GameObject("Frozen layer editor test");
            root.SetActive(false);
            NetworkManager networkManager = root.AddComponent<NetworkManager>();
            networkManager.AxisNetwork = network;
            SimulationManager simulation = root.AddComponent<SimulationManager>();
            simulation.networkManager = networkManager;
            RuntimeAirspaceVisualizer visualizer = root.AddComponent<RuntimeAirspaceVisualizer>();
            visualizer.Initialize(simulation);
            editor = root.AddComponent<RuntimeAirspaceEditor>();
            typeof(RuntimeAirspaceEditor).GetField("manager",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(editor, simulation);
            typeof(RuntimeAirspaceEditor).GetField("visualizer",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(editor, visualizer);
            return root;
        }

        private static bool RuntimeOverlayIsActive(NetworkManager manager)
        {
            return (bool)typeof(NetworkManager).GetField("_runtimeAxisOverlayActive",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(manager);
        }

        private static Network BuildTwoLayerNetwork()
        {
            return new Network
            {
                AltitudeLayers = new List<AltitudeLayer>
                {
                    new AltitudeLayer { LayerId = "L0", NominalAltitude = 75f },
                    new AltitudeLayer { LayerId = "L1", NominalAltitude = 125f }
                },
                Nodes = new List<Node>
                {
                    new Node { NodeId = "N0", LayerId = "L0", Position = new Vector3(10f, 75f, 20f) },
                    new Node { NodeId = "N1", LayerId = "L1", Position = new Vector3(50f, 125f, 60f) }
                },
                Edges = new List<Edge>
                {
                    new Edge
                    {
                        EdgeId = "E0", LayerId = "L0",
                        LinkNodesId = new List<string> { "N0", "N0" },
                        Centerline = new List<Vector3>
                        {
                            new Vector3(10f, 75f, 20f), new Vector3(15f, 75f, 25f)
                        },
                        Lanes = new List<Lane>
                        {
                            new Lane
                            {
                                LaneId = "E0_fwd_0", LayerId = "L0",
                                Centerline = new List<Vector3>
                                {
                                    new Vector3(10f, 75f, 21f), new Vector3(15f, 75f, 26f)
                                }
                            }
                        }
                    }
                }
            };
        }
    }
}
