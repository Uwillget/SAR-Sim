using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class PlaceLockerBeacons
{
    [MenuItem("ITSAC/Place Locker Beacons")]
    public static void Place()
    {
        var skp = GameObject.Find("ShenzhenSKP");
        var lockers = GameObject.Find("ShenzhenSKP/Lockers");
        var overlayRoot = GameObject.Find("ShenzhenSKP/DistrictBasemap");
        if (skp == null || lockers == null || overlayRoot == null)
            throw new Exception("Missing ShenzhenSKP / Lockers / DistrictBasemap");

        var unlit = Shader.Find("Unlit/Color");
        if (unlit == null) unlit = Shader.Find("Standard");

        var nMesh = MeshOf(overlayRoot.transform.Find("Nanshan"));
        var fMesh = MeshOf(overlayRoot.transform.Find("Futian"));
        var lMesh = MeshOf(overlayRoot.transform.Find("Longhua"));

        int nChild = lockers.transform.childCount;
        var names = new string[nChild];
        var poss = new Vector3[nChild];
        for (int i = 0; i < nChild; i++)
        {
            names[i] = lockers.transform.GetChild(i).name;
            poss[i] = lockers.transform.GetChild(i).position;
        }
        for (int i = nChild - 1; i >= 0; i--)
            UnityEngine.Object.DestroyImmediate(lockers.transform.GetChild(i).gameObject);

        var oldOutline = skp.transform.Find("DistrictOutlines");
        if (oldOutline != null) UnityEngine.Object.DestroyImmediate(oldOutline.gameObject);
        var outlineRoot = new GameObject("DistrictOutlines");
        outlineRoot.transform.SetParent(skp.transform, false);

        AddBoundary("Nanshan", nMesh, new Color(0.15f, 0.55f, 1f, 1f), outlineRoot.transform, unlit);
        AddBoundary("Futian", fMesh, new Color(0.15f, 0.95f, 0.28f, 1f), outlineRoot.transform, unlit);
        AddBoundary("Longhua", lMesh, new Color(1f, 0.45f, 0.08f, 1f), outlineRoot.transform, unlit);

        var colNS = new Color(0.10f, 0.55f, 1.00f, 1f);
        var colFT = new Color(0.10f, 0.95f, 0.25f, 1f);
        var colLH = new Color(1.00f, 0.40f, 0.05f, 1f);
        var colPhys = new Color(1f, 1f, 0.15f, 1f);
        var font = Font.CreateDynamicFontFromOSFont("Arial", 64);

        int inside = 0;
        for (int i = 0; i < nChild; i++)
        {
            int id;
            int.TryParse(names[i].Substring(0, 2), out id);
            string district = id <= 9 ? "Nanshan" : (id <= 16 ? "Futian" : "Longhua");
            Color col = district == "Nanshan" ? colNS : (district == "Futian" ? colFT : colLH);
            Mesh dmesh = district == "Nanshan" ? nMesh : (district == "Futian" ? fMesh : lMesh);
            bool pip = PointIn(dmesh, poss[i].x, poss[i].z);
            if (pip) inside++;
            Debug.Log("[Locker] " + names[i] + " " + district + " xz=(" +
                      poss[i].x.ToString("F1") + "," + poss[i].z.ToString("F1") + ") inOverlay=" + pip);

            var root = new GameObject(names[i]);
            root.transform.SetParent(lockers.transform, false);
            root.transform.position = new Vector3(poss[i].x, 0f, poss[i].z);

            var tag = root.AddComponent<LockerBeaconTag>();
            tag.color = col;
            tag.label = names[i].Substring(0, 2);

            MakePrim(PrimitiveType.Cylinder, "Pad", root.transform, new Vector3(0, 6f, 0),
                new Vector3(240f, 6f, 240f), col, unlit, false);
            MakePrim(PrimitiveType.Cylinder, "Mast", root.transform, new Vector3(0, 260f, 0),
                new Vector3(50f, 260f, 50f), col, unlit, false);
            MakePrim(PrimitiveType.Sphere, "Head", root.transform, new Vector3(0, 580f, 0),
                new Vector3(280f, 280f, 280f), col, unlit, false);

            var pin = new GameObject("Pin");
            pin.transform.SetParent(root.transform, false);
            var lr = pin.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = false;
            lr.widthMultiplier = 90f;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = MakeMat(unlit, col);
            lr.positionCount = 2;
            lr.SetPosition(0, Vector3.zero);
            lr.SetPosition(1, new Vector3(0, 2200f, 0));

            MakePrim(PrimitiveType.Cube, "PhysicalLocker", root.transform, new Vector3(0, 4f, 0),
                new Vector3(8f, 8f, 8f), colPhys, unlit, true);

            if (font != null)
            {
                for (int k = 0; k < 4; k++)
                {
                    var lab = new GameObject("Label" + k);
                    lab.transform.SetParent(root.transform, false);
                    lab.transform.localPosition = new Vector3(0, 1250f, 0);
                    lab.transform.localRotation = Quaternion.Euler(0, k * 90f, 0);
                    var tm = lab.AddComponent<TextMesh>();
                    tm.text = names[i].Substring(0, 2);
                    tm.font = font;
                    tm.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                    tm.fontSize = 160;
                    tm.characterSize = 18f;
                    tm.anchor = TextAnchor.MiddleCenter;
                    tm.alignment = TextAlignment.Center;
                    tm.color = Color.white;
                    tm.fontStyle = FontStyle.Bold;
                }
            }
        }

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        var sv = SceneView.lastActiveSceneView;
        if (sv != null)
        {
            sv.cameraSettings.dynamicClip = false;
            sv.cameraSettings.farClip = 90000f;
            sv.LookAt(new Vector3(5000f, 0f, 14000f), Quaternion.Euler(90f, 0f, 0f), 22000f);
            sv.Repaint();
        }

        Debug.Log("[Locker] placed " + lockers.transform.childCount + " beacons, insideOverlay=" +
                  inside + "/" + nChild + " outlines=" + outlineRoot.transform.childCount);
    }

    static Mesh MeshOf(Transform t)
    {
        if (t == null) return null;
        var mf = t.GetComponent<MeshFilter>();
        return mf != null ? mf.sharedMesh : null;
    }

    static Material MakeMat(Shader unlit, Color c)
    {
        var m = new Material(unlit);
        m.color = c;
        m.name = "MarkerUnlit";
        return m;
    }

    static void MakePrim(PrimitiveType type, string name, Transform parent, Vector3 localPos,
        Vector3 localScale, Color col, Shader unlit, bool keepCollider)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        if (!keepCollider)
        {
            var colr = go.GetComponent<Collider>();
            if (colr != null) UnityEngine.Object.DestroyImmediate(colr);
        }
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = MakeMat(unlit, col);
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    static bool InTri(float px, float pz, Vector3 a, Vector3 b, Vector3 c)
    {
        float s = (a.x - c.x) * (pz - c.z) - (a.z - c.z) * (px - c.x);
        float t = (b.x - a.x) * (pz - a.z) - (b.z - a.z) * (px - a.x);
        if ((s < 0) != (t < 0) && s != 0 && t != 0) return false;
        float d = (c.x - b.x) * (pz - b.z) - (c.z - b.z) * (px - b.x);
        return d == 0 || (d < 0) == (s + t <= 0);
    }

    static bool PointIn(Mesh mesh, float x, float z)
    {
        if (mesh == null) return false;
        var v = mesh.vertices;
        var tri = mesh.triangles;
        for (int i = 0; i < tri.Length; i += 3)
        {
            if (InTri(x, z, v[tri[i]], v[tri[i + 1]], v[tri[i + 2]])) return true;
        }
        return false;
    }

    static void AddBoundary(string name, Mesh mesh, Color col, Transform parent, Shader unlit)
    {
        if (mesh == null) return;
        var v = mesh.vertices;
        var tri = mesh.triangles;
        var count = new Dictionary<long, int>();
        var aOf = new Dictionary<long, int>();
        var bOf = new Dictionary<long, int>();
        for (int i = 0; i < tri.Length; i += 3)
        {
            int ia0 = tri[i], ib0 = tri[i + 1], ic0 = tri[i + 2];
            AddEdge(ia0, ib0, count, aOf, bOf);
            AddEdge(ib0, ic0, count, aOf, bOf);
            AddEdge(ic0, ia0, count, aOf, bOf);
        }
        var adj = new Dictionary<int, List<int>>();
        foreach (var kv in count)
        {
            if (kv.Value != 1) continue;
            int ia = aOf[kv.Key];
            int ib = bOf[kv.Key];
            if (!adj.ContainsKey(ia)) adj[ia] = new List<int>();
            if (!adj.ContainsKey(ib)) adj[ib] = new List<int>();
            adj[ia].Add(ib);
            adj[ib].Add(ia);
        }
        var used = new HashSet<int>();
        int loopI = 0;
        foreach (var startKv in adj)
        {
            int start = startKv.Key;
            if (used.Contains(start)) continue;
            var loop = new List<int>();
            int cur = start;
            int prev = -1;
            bool closed = false;
            for (int step = 0; step < adj.Count + 2; step++)
            {
                loop.Add(cur);
                used.Add(cur);
                var nbs = adj[cur];
                int nxt = -1;
                for (int k = 0; k < nbs.Count; k++)
                {
                    if (nbs[k] != prev) { nxt = nbs[k]; break; }
                }
                if (nxt < 0) break;
                if (nxt == start) { closed = true; break; }
                prev = cur;
                cur = nxt;
            }
            if (loop.Count < 3) continue;
            var go = new GameObject(name + "_loop" + loopI);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = closed;
            lr.widthMultiplier = 120f;
            lr.numCornerVertices = 2;
            lr.numCapVertices = 2;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = MakeMat(unlit, col);
            lr.positionCount = loop.Count;
            for (int p = 0; p < loop.Count; p++)
            {
                Vector3 vv = v[loop[p]];
                lr.SetPosition(p, new Vector3(vv.x, 12f, vv.z));
            }
            loopI++;
        }
    }

    [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
    static void DrawLockerGizmo(LockerBeaconTag tag, GizmoType gizmoType)
    {
        if (tag == null) return;
        Vector3 ground = tag.transform.position;
        Vector3 p = ground + Vector3.up * 30f;
        float s = HandleUtility.GetHandleSize(p) * 0.42f;
        Handles.color = new Color(tag.color.r, tag.color.g, tag.color.b, 0.95f);
        Handles.SphereHandleCap(0, p + Vector3.up * s, Quaternion.identity, s, EventType.Repaint);
        Handles.DrawAAPolyLine(8f, new Vector3[] { ground, p + Vector3.up * s * 2.2f });
        var style = new GUIStyle(EditorStyles.whiteBoldLabel);
        style.fontSize = 13;
        style.alignment = TextAnchor.MiddleCenter;
        style.normal.textColor = Color.white;
        Handles.Label(p + Vector3.up * s * 1.85f, tag.label, style);
    }

    static void AddEdge(int ia, int ib, Dictionary<long, int> count, Dictionary<long, int> aOf,
        Dictionary<long, int> bOf)
    {
        int lo = ia < ib ? ia : ib;
        int hi = ia < ib ? ib : ia;
        long key = ((long)lo << 32) | (uint)hi;
        if (!count.ContainsKey(key))
        {
            count[key] = 0;
            aOf[key] = ia;
            bOf[key] = ib;
        }
        count[key] = count[key] + 1;
    }
}
