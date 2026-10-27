using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RoadNetworkEditor.EditorScripts
{
    /// <summary>
    /// Import OSM hull whitebox (meters, X=east Y=up Z=north) into a new scene.
    /// Uses non-convex MeshColliders on static geometry so buildings are solid.
    /// Does NOT call ProcessBuildingModel (convex 255-tri hulls clip city tiles).
    /// </summary>
    public static class ImportHullOsmWhitebox
    {
        const string ScenePath = "Assets/Resources/Scenes/ShenzhenHullWhitebox.unity";
        const string AssetFolder = "Assets/ImportedCityModels/OSM_Hull";

        [MenuItem("ITSAC/Import Hull OSM Whitebox")]
        public static void Import()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var light = new GameObject("Directional Light");
            var sun = light.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            light.transform.rotation = Quaternion.Euler(50f, 30f, 0f);

            var camGo = new GameObject("PreviewCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            camGo.transform.position = new Vector3(8000f, 800f, 4000f);
            camGo.transform.LookAt(new Vector3(8000f, 0f, 10000f));

            var root = new GameObject("ShenzhenHull");
            var buildings = new GameObject("Buildings");
            var roads = new GameObject("Roads");
            var groundParent = new GameObject("Ground");
            var lockers = new GameObject("Lockers");
            buildings.transform.SetParent(root.transform);
            roads.transform.SetParent(root.transform);
            groundParent.transform.SetParent(root.transform);
            lockers.transform.SetParent(root.transform);

            int nB = 0, nR = 0, nG = 0, nL = 0;
            string[] guids = AssetDatabase.FindAssets("t:Model", new[] { AssetFolder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null)
                {
                    var goPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (goPrefab == null) continue;
                    InstantiateImported(goPrefab, path, buildings, roads, groundParent, lockers,
                        ref nB, ref nR, ref nG, ref nL);
                    continue;
                }
            }

            // OBJ imports as GameObject hierarchy, not a raw Mesh asset.
            string[] goGuids = AssetDatabase.FindAssets("t:GameObject", new[] { AssetFolder });
            foreach (string guid in goGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                InstantiateImported(prefab, path, buildings, roads, groundParent, lockers,
                    ref nB, ref nR, ref nG, ref nL);
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[ImportHullOsmWhitebox] saved {ScenePath} buildings={nB} roads={nR} ground={nG} lockers={nL}");
        }

        static void InstantiateImported(
            GameObject prefab, string path,
            GameObject buildings, GameObject roads, GameObject ground, GameObject lockers,
            ref int nB, ref int nR, ref int nG, ref int nL)
        {
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            GameObject parent;
            bool solid;
            if (name.StartsWith("buildings_"))
            {
                parent = buildings;
                solid = true;
                nB++;
            }
            else if (name.StartsWith("roads_"))
            {
                parent = roads;
                solid = false;
                nR++;
            }
            else if (name == "ground")
            {
                parent = ground;
                solid = true;
                nG++;
            }
            else if (name == "lockers")
            {
                parent = lockers;
                solid = true;
                nL++;
            }
            else
            {
                return;
            }

            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (inst == null)
                inst = Object.Instantiate(prefab);
            inst.name = Path.GetFileNameWithoutExtension(path);
            inst.transform.SetParent(parent.transform, false);
            inst.transform.position = Vector3.zero;
            inst.transform.rotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one;
            MakeStaticSolid(inst, solid);
        }

        static void MakeStaticSolid(GameObject go, bool addCollider)
        {
            go.isStatic = true;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var child = mf.gameObject;
                child.isStatic = true;
                var rb = child.GetComponent<Rigidbody>();
                if (rb != null) Object.DestroyImmediate(rb);
                var col = child.GetComponent<MeshCollider>();
                if (!addCollider)
                {
                    if (col != null) Object.DestroyImmediate(col);
                    continue;
                }
                if (col == null) col = child.AddComponent<MeshCollider>();
                col.sharedMesh = mf.sharedMesh;
                col.convex = false;
                col.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation
                    | MeshColliderCookingOptions.EnableMeshCleaning
                    | MeshColliderCookingOptions.WeldColocatedVertices;
                col.isTrigger = false;
            }
        }
    }
}
