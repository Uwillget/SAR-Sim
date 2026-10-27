using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

namespace RoadNetworkEditor.Tests
{
    public class RuntimeSceneAndFacilityEditingTests
    {
        [Test]
        public void Navigator_ProvidesSixAxisViewsAndIndependentProjectionToggle()
        {
            var cameraObject = new GameObject("Navigator Camera");
            var navigatorObject = new GameObject("Navigator");
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                RuntimeSceneNavigator navigator = navigatorObject.AddComponent<RuntimeSceneNavigator>();
                navigator.Initialize(camera, null);

                AssertAxis(navigator, camera, SceneViewPreset.Top, Vector3.down);
                AssertAxis(navigator, camera, SceneViewPreset.Bottom, Vector3.up);
                AssertAxis(navigator, camera, SceneViewPreset.Front, Vector3.back);
                AssertAxis(navigator, camera, SceneViewPreset.Back, Vector3.forward);
                AssertAxis(navigator, camera, SceneViewPreset.Right, Vector3.left);
                AssertAxis(navigator, camera, SceneViewPreset.Left, Vector3.right);

                navigator.SetPreset(SceneViewPreset.Top);
                navigator.ToggleProjection();
                Assert.That(camera.orthographic, Is.False);
                Assert.That(navigator.CurrentPreset, Is.EqualTo(SceneViewPreset.Top));
            }
            finally
            {
                Object.DestroyImmediate(navigatorObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void OrientationCube_TracksControlledCameraRotation()
        {
            var cameraObject = new GameObject("Orientation Cube Camera");
            var navigatorObject = new GameObject("Orientation Cube Navigator");
            var cubeObject = new GameObject("Orientation Cube", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RuntimeOrientationCube));
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                RuntimeSceneNavigator navigator = navigatorObject.AddComponent<RuntimeSceneNavigator>();
                navigator.Initialize(camera, null);
                RuntimeOrientationCube cube = cubeObject.GetComponent<RuntimeOrientationCube>();
                int projectionToggles = 0;
                cube.Configure(navigator, _ => { }, () =>
                {
                    projectionToggles++;
                    navigator.ToggleProjection();
                });

                navigator.SetPreset(SceneViewPreset.Top);
                Quaternion topRotation = cube.VisualRotation;
                Assert.That(cube.DirectionConeCount, Is.EqualTo(6));
                Assert.That(cube.ProjectionScaleAtDepth(-0.8f),
                    Is.EqualTo(cube.ProjectionScaleAtDepth(0.8f)).Within(0.0001f));
                cube.ActivateCenter();
                Assert.That(projectionToggles, Is.EqualTo(1));
                Assert.That(camera.orthographic, Is.False);
                Assert.That(cube.ProjectionScaleAtDepth(-0.8f),
                    Is.GreaterThan(cube.ProjectionScaleAtDepth(0.8f)),
                    "Perspective projection should render nearer objects larger than farther objects.");
                navigator.SetPreset(SceneViewPreset.Right);
                Quaternion rightRotation = cube.VisualRotation;

                Assert.That(Quaternion.Angle(topRotation, rightRotation), Is.GreaterThan(45f));
                Assert.That(Quaternion.Angle(rightRotation,
                    Quaternion.Inverse(camera.transform.rotation)), Is.LessThan(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(cubeObject);
                Object.DestroyImmediate(navigatorObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void HoldingAnchorSelection_FallsBackToAuthoritativeVertiportData()
        {
            var root = new GameObject("Holding Anchor Cache Test");
            root.SetActive(false);
            try
            {
                SimulationManager simulation = root.AddComponent<SimulationManager>();
                VertiportManager vertiportManager = root.AddComponent<VertiportManager>();
                RuntimeAirspaceEditor editor = root.AddComponent<RuntimeAirspaceEditor>();
                var holding = new HoldingStructure
                {
                    HoldingStructureId = "VP1-HS1",
                    Position = new Vector3(4f, 75f, 9f),
                    Circles = new List<Circle>(), Vertices = new List<Vertex>(), Ramps = new List<Ramp>()
                };
                var vertiport = new Vertiport
                {
                    VertiportId = "VP1",
                    HoldingStructures = new List<HoldingStructure> { holding }
                };
                vertiportManager.Vertiports = new List<Vertiport> { vertiport };
                simulation.vertiportManager = vertiportManager;
                simulation.Vertiports[vertiport.VertiportId] = vertiport;

                typeof(RuntimeAirspaceEditor).GetField("manager",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(editor, simulation);
                typeof(RuntimeAirspaceEditor).GetField("<ActiveVertiportId>k__BackingField",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(editor, vertiport.VertiportId);

                Assert.That(simulation.HoldingStructures, Is.Empty,
                    "Simulate the interval before the topology cache refreshes.");
                Assert.That(editor.SelectHoldingAnchor(holding.HoldingStructureId), Is.True);
                Assert.That(editor.SelectedHoldingStructureId, Is.EqualTo(holding.HoldingStructureId));
                Assert.That(editor.EnumerateActiveHoldingStructures(), Does.Contain(holding));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void MoveHoldingStructure_KeepsLinkedApproachAnchorsAttached()
        {
            var managerObject = new GameObject("Holding Move Test");
            managerObject.SetActive(false);
            try
            {
                VertiportManager manager = managerObject.AddComponent<VertiportManager>();
                var vertex = new Vertex { VertexId = "V1", Position = new Vector3(5f, 30f, 0f) };
                var holding = new HoldingStructure
                {
                    HoldingStructureId = "HS1",
                    Position = new Vector3(0f, 30f, 0f),
                    Vertices = new List<Vertex> { vertex }
                };
                var linked = new ApproachNode
                {
                    NodeId = "A1", Position = vertex.Position, IsAnchor = true,
                    LinkHSId = "HS1", LinkVertexId = "V1"
                };
                var legacy = new ApproachNode
                {
                    NodeId = "A2", Position = holding.Position, IsAnchor = true, LinkHSId = "HS1"
                };
                var independent = new ApproachNode { NodeId = "A3", Position = new Vector3(2f, 0f, 2f) };
                manager.Vertiports = new List<Vertiport>
                {
                    new Vertiport
                    {
                        VertiportId = "VP1",
                        HoldingStructures = new List<HoldingStructure> { holding },
                        ApproachNet = new ApproachNetwork
                        {
                            Nodes = new List<ApproachNode> { linked, legacy, independent }
                        }
                    }
                };
                // Provide only the ownership containers needed by MoveHoldingStructure.
                // Keeping the manager inactive avoids editor visual lifecycle callbacks,
                // so this test isolates the data-link synchronization itself.
                Transform root = managerObject.transform.Find("Vertiports");
                var vertiportContainer = new GameObject("Vertiport_VP1");
                vertiportContainer.transform.SetParent(root, false);
                var holdingContainer = new GameObject("HoldingStructures");
                holdingContainer.transform.SetParent(vertiportContainer.transform, false);
                var structureContainer = new GameObject("HS_HS1");
                structureContainer.transform.SetParent(holdingContainer.transform, false);
                structureContainer.transform.position = holding.Position;

                Assert.That(manager.MoveHoldingStructure("HS1", new Vector3(10f, 30f, 8f)), Is.True);
                Assert.That(linked.Position, Is.EqualTo(new Vector3(15f, 30f, 8f)));
                Assert.That(legacy.Position, Is.EqualTo(new Vector3(10f, 30f, 8f)));
                Assert.That(independent.Position, Is.EqualTo(new Vector3(2f, 0f, 2f)));
            }
            finally
            {
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void FacilityVisualRebuildAndAwake_PreserveCorridorModelData()
        {
            var managerObject = new GameObject("Facility Visual Rebuild Test");
            managerObject.SetActive(false);
            try
            {
                VertiportManager manager = managerObject.AddComponent<VertiportManager>();
                var corridor = new NetConnectedCorridor
                {
                    CorridorId = "VP1-COR1", CorridorType = "Entry",
                    LinkLaneId = "L1", LinkRampId = "R1",
                    LaneLinkPoint = new Vector3(1f, 75f, 2f),
                    RampLinkPoint = new Vector3(3f, 0f, 4f)
                };
                var ramp = new Ramp
                {
                    RampId = "R1", FromVertexId = "V1", ToVertexId = "V2",
                    LinkCorridors = new List<(string, Vector3)>
                        { (corridor.CorridorId, corridor.RampLinkPoint) }
                };
                manager.Vertiports = new List<Vertiport>
                {
                    new Vertiport
                    {
                        VertiportId = "VP1",
                        Helipads = new List<Helipad>(),
                        HoldingStructures = new List<HoldingStructure>
                        {
                            new HoldingStructure
                            {
                                HoldingStructureId = "HS1",
                                Vertices = new List<Vertex>(), Circles = new List<Circle>(),
                                Ramps = new List<Ramp> { ramp }
                            }
                        },
                        ApproachNet = new ApproachNetwork
                        {
                            Nodes = new List<ApproachNode>(), Edges = new List<ApproachEdge>()
                        },
                        Corridors = new List<NetConnectedCorridor> { corridor }
                    }
                };

                manager.RebuildVisualsFromData();
                ramp.LinkCorridors.Clear();
                manager.RebuildVisualsFromData();
                typeof(VertiportManager).GetMethod("Awake",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);

                Assert.That(manager.Vertiports.Single().Corridors.Select(item => item.CorridorId),
                    Is.EqualTo(new[] { corridor.CorridorId }));
                Assert.That(manager.Vertiports.Single().HoldingStructures.Single().Ramps.Single()
                    .LinkCorridors.Select(item => item.Item1),
                    Is.EqualTo(new[] { corridor.CorridorId }));
            }
            finally
            {
                Object.DestroyImmediate(managerObject);
            }
        }

        private static void AssertAxis(RuntimeSceneNavigator navigator, Camera camera,
            SceneViewPreset preset, Vector3 expectedForward)
        {
            navigator.SetPreset(preset);
            Assert.That(camera.orthographic, Is.True, preset.ToString());
            Assert.That(Vector3.Dot(camera.transform.forward, expectedForward),
                Is.GreaterThan(0.999f), preset.ToString());
        }
    }
}
