using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Batch MeshColliders for the Shenzhen combined whitebox.
/// The original CityModelController "Process Building Model" button adds convex
/// hulls and snaps bases to Y=0 — that is for many small building meshes, not
/// these multi-million-triangle district OBJs.
/// </summary>
public static class AddWhiteboxColliders
{
    const string BuildingsLayer = "Buildings";
    static readonly string[] DistrictNames = { "Nanshan", "Futian", "Longhua" };

    static readonly MeshColliderCookingOptions CookFlags =
        MeshColliderCookingOptions.CookForFasterSimulation
        | MeshColliderCookingOptions.EnableMeshCleaning
        | MeshColliderCookingOptions.WeldColocatedVertices;

    [MenuItem("ITSAC/Add Whitebox Colliders")]
    public static void AddToShenzhenWhitebox()
    {
        var skp = GameObject.Find("ShenzhenSKP");
        if (skp == null)
            throw new System.Exception("Open ShenzhenSKPWhitebox and keep ShenzhenSKP in the scene.");

        int buildingLayer = LayerMask.NameToLayer(BuildingsLayer);
        if (buildingLayer < 0)
            throw new System.Exception("Missing layer '" + BuildingsLayer + "'.");

        int added = 0, refreshed = 0, layered = 0, lockers = 0;

        foreach (string name in DistrictNames)
        {
            var t = skp.transform.Find(name);
            if (t == null) continue;
            ApplyMeshColliders(t.gameObject, buildingLayer, true, ref added, ref refreshed, ref layered);
        }

        var lockerRoot = skp.transform.Find("Lockers");
        if (lockerRoot != null)
            ApplyMeshColliders(lockerRoot.gameObject, lockerRoot.gameObject.layer, false, ref lockers, ref refreshed, ref layered);

        WireNetworkBuildingMask(buildingLayer);

        var scene = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("[AddWhiteboxColliders] districts added=" + added
            + " refreshed=" + refreshed
            + " layer=" + layered
            + " lockerColliders=" + lockers
            + " Buildings layer=" + buildingLayer);
    }

    [MenuItem("ITSAC/Add Mesh Colliders To Selection")]
    public static void AddToSelection()
    {
        var selected = Selection.gameObjects;
        if (selected == null || selected.Length == 0)
            throw new System.Exception("Select a parent (or meshes) in the Hierarchy first.");

        int added = 0, refreshed = 0, layered = 0;
        foreach (var go in selected)
            ApplyMeshColliders(go, go.layer, false, ref added, ref refreshed, ref layered);

        var scene = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("[AddWhiteboxColliders] selection added=" + added + " refreshed=" + refreshed);
    }

    static void ApplyMeshColliders(
        GameObject root, int layer, bool setLayer,
        ref int added, ref int refreshed, ref int layered)
    {
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            if (IsOverlay(mf.gameObject)) continue;

            var go = mf.gameObject;
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null)
                Object.DestroyImmediate(rb);

            var col = go.GetComponent<MeshCollider>();
            bool created = col == null;
            if (created)
                col = Undo.AddComponent<MeshCollider>(go);

            bool needsCook = created
                || col.sharedMesh != mf.sharedMesh
                || col.convex
                || col.isTrigger;
            if (needsCook)
            {
                Undo.RecordObject(col, "Add Whitebox Collider");
                col.sharedMesh = mf.sharedMesh;
                col.convex = false;
                col.cookingOptions = CookFlags;
                col.isTrigger = false;
            }
            if (created) added++;
            else if (needsCook) refreshed++;

            if (setLayer && go.layer != layer)
            {
                Undo.RecordObject(go, "Set Buildings Layer");
                go.layer = layer;
                layered++;
            }
        }
    }

    static bool IsOverlay(GameObject go)
    {
        Transform t = go.transform;
        while (t != null)
        {
            if (t.name == "DistrictBasemap" || t.name == "DistrictOutlines")
                return true;
            t = t.parent;
        }
        return false;
    }

    static void WireNetworkBuildingMask(int buildingLayer)
    {
        var nmGo = GameObject.Find("NetworkManager");
        if (nmGo == null) return;
        var nm = nmGo.GetComponent<RoadNetworkEditor.NetworkManager>();
        if (nm == null) return;
        Undo.RecordObject(nm, "Wire building layer mask");
        nm.buildingLayerMask |= 1 << buildingLayer;
        EditorUtility.SetDirty(nm);
    }
}
