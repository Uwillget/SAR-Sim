using System.IO;
using System.Reflection;
using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;

namespace RoadNetworkEditor.Tests.Editor
{
    public sealed class SarSceneWorkspaceTests
    {
        [Test]
        public void NativeFileDialog_NormalizesWindowsFilter()
        {
            string value = SarNativeFileDialog.NormalizeFilter("City model|*.sarcity;*.obj|All files|*.*");
            Assert.That(value, Does.Contain("City model\0*.sarcity;*.obj\0"));
            Assert.That(value.EndsWith("\0"), Is.True);
        }

        [Test]
        public void NativeFileDialog_BlocksNestedDialogEntry()
        {
            MethodInfo enter = typeof(SarNativeFileDialog).GetMethod("TryEnterDialog",
                BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo exit = typeof(SarNativeFileDialog).GetMethod("ExitDialog",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(enter, Is.Not.Null);
            Assert.That(exit, Is.Not.Null);
            try
            {
                Assert.That(enter.Invoke(null, null), Is.EqualTo(true));
                Assert.That(SarNativeFileDialog.IsDialogOpen, Is.True);
                Assert.That(enter.Invoke(null, null), Is.EqualTo(false),
                    "Nested Windows message dispatch must not open a second dialog.");
            }
            finally
            {
                exit.Invoke(null, null);
            }
            Assert.That(SarNativeFileDialog.IsDialogOpen, Is.False);
        }

#if UNITY_EDITOR_WIN
        [Test]
        public void NativeFileDialog_ModernWindowsInteropCanBeConfigured()
        {
            Assert.That(SarNativeFileDialog.ValidateWindowsInterop(out string error), Is.True, error);
        }
#endif

        [Test]
        public void PortablePath_RoundTripsApplicationUserAndSceneLocations()
        {
            string applicationFile = Path.Combine(SarPortablePath.ApplicationRoot, "DataBase", "Route.json");
            string applicationToken = SarPortablePath.ToPortable(applicationFile);
            Assert.That(applicationToken, Does.StartWith(SarPortablePath.ApplicationPrefix));
            Assert.That(Path.GetFullPath(SarPortablePath.Resolve(applicationToken)),
                Is.EqualTo(Path.GetFullPath(applicationFile)).IgnoreCase);

            string userFile = Path.Combine(SarRuntimePaths.DataRoot, "Output", "Metrics.csv");
            string userToken = SarPortablePath.ToPortable(userFile);
            Assert.That(userToken, Does.StartWith(SarPortablePath.UserPrefix));
            Assert.That(Path.GetFullPath(SarPortablePath.Resolve(userToken)),
                Is.EqualTo(Path.GetFullPath(userFile)).IgnoreCase);

            string sceneFolder = Path.Combine(SarRuntimePaths.SceneRoot, "scene-test");
            string sceneFile = Path.Combine(sceneFolder, "AxisNetwork.json");
            string sceneToken = SarPortablePath.ToPortable(sceneFile, sceneFolder);
            Assert.That(sceneToken, Is.EqualTo("scene://AxisNetwork.json"));
            Assert.That(Path.GetFullPath(SarPortablePath.Resolve(sceneToken, sceneFolder)),
                Is.EqualTo(Path.GetFullPath(sceneFile)).IgnoreCase);
        }

        [Test]
        public void CityPlacement_CentresPlanOnViewAndGroundsLowestPoint()
        {
            var bounds = new Bounds(new Vector3(100f, 50f, -25f), new Vector3(40f, 60f, 20f));
            Vector3 target = new Vector3(5f, 999f, 7f);
            Vector3 delta = SarSceneWorkspace.ComputeCityPlacementDelta(bounds, target);
            bounds.center += delta;
            Assert.That(bounds.center.x, Is.EqualTo(target.x).Within(0.001f));
            Assert.That(bounds.center.z, Is.EqualTo(target.z).Within(0.001f));
            Assert.That(bounds.min.y, Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void CityPackage_RoundTripsMeshAndReplacesOldChildren()
        {
            string path = Path.Combine(Path.GetTempPath(), "sar-city-test-" + System.Guid.NewGuid() + ".sarcity");
            var source = new GameObject("SourceCity");
            var building = GameObject.CreatePrimitive(PrimitiveType.Cube);
            building.name = "TestBuilding";
            building.transform.SetParent(source.transform, false);
            building.transform.localPosition = new Vector3(4f, 6f, 8f);
            var target = new GameObject("TargetCity");
            new GameObject("OldBuilding").transform.SetParent(target.transform, false);
            try
            {
                int exported = SarCityModelPackage.Export(source.transform, path);
                Assert.That(exported, Is.EqualTo(1));
                Assert.That(SarCityModelPackage.TryLoad(path, target.transform, out string error, out int loaded),
                    Is.True, error);
                Assert.That(loaded, Is.EqualTo(1));
                Assert.That(target.transform.Find("TestBuilding"), Is.Not.Null);
                Assert.That(target.transform.Find("OldBuilding"), Is.Null);
                int buildingLayer = LayerMask.NameToLayer("Buildings");
                if (buildingLayer >= 0)
                {
                    Assert.That(target.layer, Is.EqualTo(buildingLayer));
                    Assert.That(target.transform.Find("TestBuilding").gameObject.layer,
                        Is.EqualTo(buildingLayer),
                        "Runtime-imported city meshes must remain visible to lane-clearance collision queries.");
                }
                Mesh mesh = target.GetComponentInChildren<MeshFilter>().sharedMesh;
                Assert.That(mesh.vertexCount, Is.GreaterThan(0));
                Assert.That(mesh.bounds.center.x, Is.EqualTo(4f).Within(0.01f));
                Assert.That(mesh.bounds.center.y, Is.EqualTo(6f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(target);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void ImportedCityMesh_BlocksMinimumLaneGeneration()
        {
            int buildingLayer = LayerMask.NameToLayer("Buildings");
            if (buildingLayer < 0) Assert.Ignore("Buildings layer is not configured.");

            string path = Path.Combine(Path.GetTempPath(), "sar-city-lane-test-" + System.Guid.NewGuid() + ".sarcity");
            var source = new GameObject("SourceCity");
            var building = GameObject.CreatePrimitive(PrimitiveType.Cube);
            building.transform.SetParent(source.transform, false);
            building.transform.localPosition = new Vector3(0f, 75f, 0f);
            building.transform.localScale = new Vector3(20f, 20f, 20f);
            var target = new GameObject("TargetCity");
            var managerObject = new GameObject("NetworkManager lane-clearance test");
            managerObject.SetActive(false);
            try
            {
                SarCityModelPackage.Export(source.transform, path);
                Assert.That(SarCityModelPackage.TryLoad(path, target.transform,
                    out string error, out _), Is.True, error);
                Physics.SyncTransforms();

                NetworkManager manager = managerObject.AddComponent<NetworkManager>();
                manager.buildingLayerMask = 1 << buildingLayer;
                manager.laneWidth = 5f;
                manager.minLaneCount = 2;
                manager.maxLaneCount = 8;
                manager.collisionCheckHeight = 5f;
                MethodInfo calculate = typeof(NetworkManager).GetMethod("CalculateMaxLaneCount",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(calculate, Is.Not.Null);
                int laneCount = (int)calculate.Invoke(manager, new object[]
                {
                    new System.Collections.Generic.List<Vector3>
                    {
                        new Vector3(-30f, 75f, 0f),
                        new Vector3(30f, 75f, 0f)
                    }
                });
                Assert.That(laneCount, Is.Zero,
                    "If even two lanes intersect an imported building, the edge must have no generated lanes.");
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(managerObject);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void CityPackage_LoadsExternalObjWithComputedNormals()
        {
            string path = Path.Combine(Path.GetTempPath(), "sar-city-test-" + System.Guid.NewGuid() + ".obj");
            File.WriteAllText(path,
                "v 0 0 0\n" +
                "v 10 0 0\n" +
                "v 0 12 0\n" +
                "f 1 2 3\n");
            var target = new GameObject("ObjCity");
            try
            {
                Assert.That(SarCityModelPackage.TryLoad(path, target.transform,
                    out string error, out int loaded), Is.True, error);
                Assert.That(loaded, Is.EqualTo(1));
                Mesh mesh = target.GetComponentInChildren<MeshFilter>().sharedMesh;
                Assert.That(mesh.vertexCount, Is.EqualTo(3));
                Assert.That(mesh.normals, Has.Length.EqualTo(3));
                Assert.That(mesh.normals[0].sqrMagnitude, Is.GreaterThan(0.9f));
            }
            finally
            {
                Object.DestroyImmediate(target);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void CityPackage_LoadsDxfThreeDimensionalFace()
        {
            string path = Path.Combine(Path.GetTempPath(), "sar-city-test-" + System.Guid.NewGuid() + ".dxf");
            File.WriteAllText(path,
                "0\nSECTION\n2\nENTITIES\n0\n3DFACE\n8\nBuildings\n" +
                "10\n0\n20\n0\n30\n0\n" +
                "11\n10\n21\n0\n31\n0\n" +
                "12\n0\n22\n10\n32\n12\n" +
                "13\n0\n23\n10\n33\n12\n" +
                "0\nENDSEC\n0\nEOF\n");
            var target = new GameObject("DxfCity");
            try
            {
                Assert.That(SarCityModelPackage.TryLoad(path, target.transform,
                    out string error, out int loaded), Is.True, error);
                Assert.That(loaded, Is.EqualTo(1));
                Mesh mesh = target.GetComponentInChildren<MeshFilter>().sharedMesh;
                Assert.That(mesh.triangles, Has.Length.EqualTo(3));
                Assert.That(mesh.bounds.max.y, Is.EqualTo(12f).Within(0.01f),
                    "DXF Z coordinates must become Unity Y height.");
            }
            finally
            {
                Object.DestroyImmediate(target);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void CityPackage_LoadsThickClosedDxfFootprint()
        {
            string path = Path.Combine(Path.GetTempPath(), "sar-city-test-" + System.Guid.NewGuid() + ".dxf");
            File.WriteAllText(path,
                "0\nSECTION\n2\nENTITIES\n0\nLWPOLYLINE\n70\n1\n38\n2\n39\n8\n" +
                "10\n0\n20\n0\n10\n10\n20\n0\n10\n10\n20\n10\n10\n0\n20\n10\n" +
                "0\nENDSEC\n0\nEOF\n");
            var target = new GameObject("DxfExtrusion");
            try
            {
                Assert.That(SarCityModelPackage.TryLoad(path, target.transform,
                    out string error, out int loaded), Is.True, error);
                Assert.That(loaded, Is.EqualTo(1));
                Mesh mesh = target.GetComponentInChildren<MeshFilter>().sharedMesh;
                Assert.That(mesh.triangles.Length, Is.GreaterThanOrEqualTo(30));
                Assert.That(mesh.bounds.min.y, Is.EqualTo(2f).Within(0.01f));
                Assert.That(mesh.bounds.max.y, Is.EqualTo(10f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(target);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void AircraftTrail_CanBeEnabledAndCleared()
        {
            var aircraft = new GameObject("Aircraft_Test");
            try
            {
                var controller = aircraft.AddComponent<AircraftTrailController>();
                controller.Configure(true, "M001", 8f);
                TrailRenderer trail = aircraft.GetComponent<TrailRenderer>();
                Assert.That(trail, Is.Not.Null);
                Assert.That(trail.enabled, Is.True);
                Assert.That(trail.emitting, Is.True);
                Assert.That(controller.TargetLength, Is.EqualTo(20f).Within(0.01f));
                Assert.That(trail.time, Is.LessThanOrEqualTo(6f));
                Assert.That(trail.widthCurve.Evaluate(0f), Is.GreaterThan(trail.widthCurve.Evaluate(1f)));
                Assert.That(trail.colorGradient.Evaluate(0f).a,
                    Is.GreaterThan(trail.colorGradient.Evaluate(1f).a));
                controller.Configure(false, "M001", 8f);
                Assert.That(trail.enabled, Is.False);
                Assert.That(trail.emitting, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(aircraft);
            }
        }
    }
}
