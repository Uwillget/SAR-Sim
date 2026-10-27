using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Splits combined district whitebox meshes into per-building children,
/// matching the original SKP import (CityModel / building_001 / ...).
/// Uses welded-position + triangle connectivity so CADMapper boxes come apart.
/// </summary>
public static class SplitWhiteboxBuildings
{
    const float WeldMeters = 0.03f;
    const int MinTriangles = 8;
    const float MinHeight = 1.25f;
    const string AssetRoot = "Assets/ImportedCityModels/ShenzhenSplit";

    [MenuItem("ITSAC/Analyze Whitebox Buildings")]
    public static void Analyze()
    {
        var city = FindCityModel();
        var sb = new System.Text.StringBuilder();
        try
        {
            foreach (var district in Districts(city))
            {
                var mesh = district.GetComponent<MeshFilter>().sharedMesh;
                var groups = BuildGroups(mesh, district.name);
                int kept = 0;
                int discarded = 0;
                int maxTris = 0;
                foreach (var g in groups)
                {
                    if (KeepGroup(mesh, g))
                    {
                        kept++;
                        if (g.triCount > maxTris) maxTris = g.triCount;
                    }
                    else discarded++;
                }
                sb.AppendLine(district.name + " components=" + groups.Count
                    + " buildings=" + kept + " discarded=" + discarded
                    + " largestTris=" + maxTris);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
        Debug.Log("[SplitWhitebox] " + sb.ToString());
    }

    [MenuItem("ITSAC/Split Whitebox Into Buildings")]
    public static void Split()
    {
        var city = FindCityModel();
        DirectoryEnsure(AssetRoot);
        int total = 0;
        try
        {
            foreach (var district in Districts(city))
                total += SplitDistrict(district);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (city.GetComponent<RoadNetworkEditor.CityModelController>() == null)
            city.gameObject.AddComponent<RoadNetworkEditor.CityModelController>();

        var scene = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[SplitWhitebox] created " + total + " building children under " + city.name);
    }

    static Transform FindCityModel()
    {
        var t = GameObject.Find("ShenzhenEnv/CityModel");
        if (t == null) t = GameObject.Find("CityModel");
        if (t == null) t = GameObject.Find("ShenzhenSKP");
        if (t == null)
            throw new System.Exception("CityModel parent not found (expected ShenzhenEnv/CityModel).");
        return t.transform;
    }

    static List<Transform> Districts(Transform city)
    {
        var list = new List<Transform>();
        foreach (string name in new[] { "Nanshan", "Futian", "Longhua" })
        {
            var child = city.Find(name);
            if (child == null) continue;
            var mf = child.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 1000)
                list.Add(child);
        }
        if (list.Count == 0)
            throw new System.Exception("No combined district meshes found under " + city.name);
        return list;
    }

    static int SplitDistrict(Transform district)
    {
        var mf = district.GetComponent<MeshFilter>();
        var src = mf.sharedMesh;
        var groups = BuildGroups(src, district.name);

        for (int i = district.childCount - 1; i >= 0; i--)
        {
            var ch = district.GetChild(i);
            if (ch.name.StartsWith("B_"))
                Object.DestroyImmediate(ch.gameObject);
        }

        string folder = AssetRoot + "/" + district.name;
        DirectoryEnsure(folder);
        string containerPath = folder + "/" + district.name + "_buildings.asset";
        var existing = AssetDatabase.LoadAssetAtPath<Object>(containerPath);
        if (existing != null)
            AssetDatabase.DeleteAsset(containerPath);

        var container = ScriptableObject.CreateInstance<SplitMeshContainer>();
        AssetDatabase.CreateAsset(container, containerPath);

        var srcVerts = src.vertices;
        var srcNorms = src.normals;
        var srcUv = src.uv;
        var srcTris = src.triangles;
        var mat = district.GetComponent<MeshRenderer>() != null
            ? district.GetComponent<MeshRenderer>().sharedMaterial
            : null;
        int layer = district.gameObject.layer;
        int created = 0;
        int serial = 1;

        AssetDatabase.StartAssetEditing();
        try
        {
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                if (!KeepGroup(src, g)) continue;
                if (gi % 50 == 0)
                    EditorUtility.DisplayProgressBar("Split " + district.name,
                        "Building " + serial, (float)gi / groups.Count);

                var mesh = MakeMesh(srcVerts, srcNorms, srcUv, srcTris, g, district.name + "_B" + serial.ToString("D4"));
                AssetDatabase.AddObjectToAsset(mesh, container);

                var go = new GameObject("B_" + serial.ToString("D4"));
                go.transform.SetParent(district, false);
                go.layer = layer;
                go.isStatic = true;
                var childMf = go.AddComponent<MeshFilter>();
                childMf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                created++;
                serial++;
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }

        EditorUtility.SetDirty(container);

        var srcMr = district.GetComponent<MeshRenderer>();
        if (srcMr != null) Object.DestroyImmediate(srcMr);
        var srcCol = district.GetComponent<MeshCollider>();
        if (srcCol != null) Object.DestroyImmediate(srcCol);
        if (mf != null) Object.DestroyImmediate(mf);

        Debug.Log("[SplitWhitebox] " + district.name + " -> " + created + " buildings");
        return created;
    }

    class Group
    {
        public int root;
        public List<int> triStarts = new List<int>();
        public int triCount;
        public Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        public Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
    }

    static List<Group> BuildGroups(Mesh mesh, string label)
    {
        var verts = mesh.vertices;
        var tris = mesh.triangles;
        int n = verts.Length;
        var parent = new int[n];
        var rank = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        EditorUtility.DisplayProgressBar("Split " + label, "Weld vertices", 0.05f);
        var grid = new Dictionary<long, int>(n / 2);
        float inv = 1f / WeldMeters;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = verts[i];
            int ix = Mathf.RoundToInt(p.x * inv);
            int iy = Mathf.RoundToInt(p.y * inv);
            int iz = Mathf.RoundToInt(p.z * inv);
            long key = Key(ix, iy, iz);
            int found;
            if (grid.TryGetValue(key, out found))
                Union(parent, rank, i, found);
            else
                grid[key] = i;
        }

        EditorUtility.DisplayProgressBar("Split " + label, "Connect triangles", 0.35f);
        for (int t = 0; t < tris.Length; t += 3)
        {
            Union(parent, rank, tris[t], tris[t + 1]);
            Union(parent, rank, tris[t + 1], tris[t + 2]);
        }

        EditorUtility.DisplayProgressBar("Split " + label, "Collect buildings", 0.7f);
        var map = new Dictionary<int, Group>();
        for (int t = 0; t < tris.Length; t += 3)
        {
            int root = Find(parent, tris[t]);
            Group g;
            if (!map.TryGetValue(root, out g))
            {
                g = new Group { root = root };
                map[root] = g;
            }
            g.triStarts.Add(t);
            g.triCount++;
            Encapsulate(g, verts[tris[t]]);
            Encapsulate(g, verts[tris[t + 1]]);
            Encapsulate(g, verts[tris[t + 2]]);
        }
        return new List<Group>(map.Values);
    }

    static bool KeepGroup(Mesh mesh, Group g)
    {
        if (g.triCount < MinTriangles) return false;
        float h = g.max.y - g.min.y;
        float footprint = Mathf.Max(g.max.x - g.min.x, g.max.z - g.min.z);
        if (h < MinHeight && footprint < 4f) return false;
        return true;
    }

    static Mesh MakeMesh(Vector3[] verts, Vector3[] norms, Vector2[] uv, int[] tris, Group g, string name)
    {
        var used = new Dictionary<int, int>();
        var newVerts = new List<Vector3>(g.triCount * 2);
        var newNorms = new List<Vector3>(g.triCount * 2);
        var newUv = new List<Vector2>(g.triCount * 2);
        var newTris = new List<int>(g.triCount * 3);
        bool hasN = norms != null && norms.Length == verts.Length;
        bool hasUv = uv != null && uv.Length == verts.Length;

        for (int i = 0; i < g.triStarts.Count; i++)
        {
            int t = g.triStarts[i];
            for (int k = 0; k < 3; k++)
            {
                int old = tris[t + k];
                int mapped;
                if (!used.TryGetValue(old, out mapped))
                {
                    mapped = newVerts.Count;
                    used[old] = mapped;
                    newVerts.Add(verts[old]);
                    if (hasN) newNorms.Add(norms[old]);
                    if (hasUv) newUv.Add(uv[old]);
                }
                newTris.Add(mapped);
            }
        }

        var mesh = new Mesh();
        mesh.name = name;
        if (newVerts.Count > 65535)
            mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(newVerts);
        if (hasN) mesh.SetNormals(newNorms);
        if (hasUv) mesh.SetUVs(0, newUv);
        mesh.SetTriangles(newTris, 0, true);
        mesh.RecalculateBounds();
        if (!hasN) mesh.RecalculateNormals();
        return mesh;
    }

    static void Encapsulate(Group g, Vector3 p)
    {
        if (p.x < g.min.x) g.min.x = p.x;
        if (p.y < g.min.y) g.min.y = p.y;
        if (p.z < g.min.z) g.min.z = p.z;
        if (p.x > g.max.x) g.max.x = p.x;
        if (p.y > g.max.y) g.max.y = p.y;
        if (p.z > g.max.z) g.max.z = p.z;
    }

    static long Key(int x, int y, int z)
    {
        unchecked
        {
            return ((long)(x + 1000000) & 0x1FFFFF)
                | (((long)(y + 1000000) & 0x1FFFFF) << 21)
                | (((long)(z + 1000000) & 0x1FFFFF) << 42);
        }
    }

    static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }
        return x;
    }

    static void Union(int[] parent, int[] rank, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a == b) return;
        if (rank[a] < rank[b]) parent[a] = b;
        else if (rank[a] > rank[b]) parent[b] = a;
        else
        {
            parent[b] = a;
            rank[a]++;
        }
    }

    static void DirectoryEnsure(string assetFolder)
    {
        if (AssetDatabase.IsValidFolder(assetFolder)) return;
        string[] parts = assetFolder.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }

    class SplitMeshContainer : ScriptableObject { }
}
