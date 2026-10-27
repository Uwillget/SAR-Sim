using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using RoadNetworkEditor;

/// <summary>
/// Rebuilds a short original-platform-style air corridor over locker 09 (Haifeng):
/// 75 m AGL Manhattan axis, 8 parallel 5 m lanes, ground vertiport + climb.
/// </summary>
public static class BuildShenzhenDemoAirspace
{
    const float East0 = 206.80f;
    const float North0 = 148.90f;
    const float CruiseY = 75f;
    const float PadY = 8f;
    const float Spacing = 150f;
    const float LaneWidth = 5f;
    const int HalfExtent = 1;
    const int LanesEachWay = 4;

    static readonly Color AxisAmber = new Color(0.83f, 0.62f, 0.18f, 0.95f);
    static readonly Color LaneStraight = new Color(0.16f, 0.62f, 0.35f, 0.92f);
    static readonly Color LaneLeft = new Color(0.20f, 0.42f, 0.82f, 0.95f);
    static readonly Color LaneRight = new Color(0.91f, 0.49f, 0.16f, 0.95f);
    static readonly Color LaneSLeft = new Color(0.10f, 0.58f, 0.58f, 0.95f);
    static readonly Color LaneSRight = new Color(0.48f, 0.65f, 0.13f, 0.95f);
    static readonly Color ConnectorGold = new Color(0.83f, 0.62f, 0.18f, 0.85f);
    static readonly Color ApproachBlue = new Color(0.13f, 0.48f, 0.72f, 1f);
    static readonly Color VertiportBrown = new Color(0.74f, 0.39f, 0.20f, 1f);
    static readonly Color HelipadTeal = new Color(0.10f, 0.55f, 0.47f, 1f);
    static readonly Color NodeWhite = new Color(0.92f, 0.93f, 0.90f, 1f);

    [MenuItem("ITSAC/Build Shenzhen Demo Airspace")]
    public static void Build()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.name != "ShenzhenSKPWhitebox")
            throw new System.Exception("Open ShenzhenSKPWhitebox first, current=" + scene.name);

        var old = GameObject.Find("ShenzhenDemoAirspace");
        if (old != null) Object.DestroyImmediate(old);

        Shader unlit = Shader.Find("Unlit/Color");
        if (unlit == null) unlit = Shader.Find("Sprites/Default");
        if (unlit == null) unlit = Shader.Find("Standard");

        var root = new GameObject("ShenzhenDemoAirspace");
        Undo.RegisterCreatedObjectUndo(root, "Build Shenzhen Demo Airspace");
        var axisRoot = Child(root.transform, "Axis");
        var laneRoot = Child(root.transform, "Lanes");
        var nodeRoot = Child(root.transform, "Nodes");
        var facilityRoot = Child(root.transform, "Facilities");

        int n = 2 * HalfExtent + 1;
        var nodes = new Vector3[n, n];
        for (int iz = 0; iz < n; iz++)
        {
            for (int ix = 0; ix < n; ix++)
            {
                float east = East0 + (ix - HalfExtent) * Spacing;
                float north = North0 + (iz - HalfExtent) * Spacing;
                nodes[ix, iz] = new Vector3(east, CruiseY, north);
                MakeNode(nodeRoot, "N_" + ix + "_" + iz, nodes[ix, iz], unlit);
            }
        }

        for (int iz = 0; iz < n; iz++)
        {
            for (int ix = 0; ix < n; ix++)
            {
                if (ix + 1 < n)
                    BuildPhysicalEdge(axisRoot, laneRoot, "E_x_" + ix + "_" + iz,
                        nodes[ix, iz], nodes[ix + 1, iz], unlit);
                if (iz + 1 < n)
                    BuildPhysicalEdge(axisRoot, laneRoot, "E_z_" + ix + "_" + iz,
                        nodes[ix, iz], nodes[ix, iz + 1], unlit);
            }
        }

        BuildFacility(facilityRoot, unlit);

        int loopFrom;
        Vector3[] route = BuildDemoRoute(nodes, out loopFrom);
        GameObject aircraft = SpawnAircraft(root.transform, route[0]);
        var flyer = aircraft.GetComponent<ShenzhenDemoFlyer>();
        if (flyer == null) flyer = aircraft.AddComponent<ShenzhenDemoFlyer>();
        flyer.waypoints = route;
        flyer.loopFromIndex = loopFrom;

        var fc = aircraft.GetComponent<FlightController>();
        if (fc == null) fc = aircraft.AddComponent<FlightController>();
        fc.maxSpeed = 18f;
        fc.moveSpeed = 18f;
        fc.rotationSpeed = 90f;
        fc.targetPosition = route[1];

        var pub = Object.FindObjectOfType<AirSimKinematicPublisher>();
        if (pub == null)
        {
            var pubGo = new GameObject("AirSimKinematicPublisher");
            pubGo.transform.SetParent(root.transform, false);
            pub = pubGo.AddComponent<AirSimKinematicPublisher>();
        }
        pub.explicitVehicles = new[] { aircraft.transform };
        pub.defaultAirSimVehicle = "Drone1";
        pub.sendHz = 50f;

        var cam = GameObject.Find("PreviewCamera");
        if (cam != null)
        {
            cam.transform.position = new Vector3(East0 - 220f, 160f, North0 - 280f);
            cam.transform.LookAt(new Vector3(East0, CruiseY, North0));
            var c = cam.GetComponent<Camera>();
            if (c != null)
            {
                c.farClipPlane = 80000f;
                c.nearClipPlane = 1f;
                c.fieldOfView = 55f;
            }
            var follow = cam.GetComponent<RoadNetworkEditor.ShenzhenDemoFollowCamera>();
            if (follow == null) follow = cam.AddComponent<RoadNetworkEditor.ShenzhenDemoFollowCamera>();
            follow.target = aircraft.transform;
            follow.offset = new Vector3(0f, 28f, -70f);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("[ITSAC] Shenzhen demo airspace: 3x3 nodes @75m, 8 lanes x 5m, route pts=" +
                  route.Length + " over locker 09 Haifeng");
    }

    static Vector3[] BuildDemoRoute(Vector3[,] nodes, out int loopFrom)
    {
        Vector3 pad = new Vector3(East0, PadY, North0);
        Vector3 cruise = new Vector3(East0, CruiseY, North0);
        Vector3 sw = nodes[0, 0];
        Vector3 se = nodes[2, 0];
        Vector3 ne = nodes[2, 2];
        Vector3 nw = nodes[0, 2];
        var pts = new List<Vector3>();
        Densify(pts, pad, cruise, 8);
        Densify(pts, cruise, se, 10);
        loopFrom = pts.Count;
        Densify(pts, se, ne, 12);
        Densify(pts, ne, nw, 12);
        Densify(pts, nw, sw, 12);
        Densify(pts, sw, se, 12);
        return pts.ToArray();
    }

    static void Densify(List<Vector3> dst, Vector3 a, Vector3 b, int steps)
    {
        if (dst.Count == 0) dst.Add(a);
        for (int i = 1; i <= steps; i++)
            dst.Add(Vector3.Lerp(a, b, i / (float)steps));
    }

    static void BuildPhysicalEdge(Transform axisRoot, Transform laneRoot, string id,
        Vector3 a, Vector3 b, Shader unlit)
    {
        MakeLine(axisRoot, "Axis_" + id, new[] { a, b }, AxisAmber, 1.6f, unlit);
        Vector3 dir = (b - a);
        dir.y = 0f;
        Vector3 right = Vector3.Cross(Vector3.up, dir.normalized);
        for (int k = 0; k < LanesEachWay; k++)
        {
            float offset = LaneWidth * (0.5f + k);
            Color cf = LaneColorForIndex(k, true);
            Color cb = LaneColorForIndex(k, false);
            Vector3 aF = a + right * offset;
            Vector3 bF = b + right * offset;
            Vector3 aB = a - right * offset;
            Vector3 bB = b - right * offset;
            MakeLine(laneRoot, id + "_fwd_" + k, new[] { aF, bF }, cf, 2.2f, unlit);
            MakeLine(laneRoot, id + "_bwd_" + k, new[] { bB, aB }, cb, 2.2f, unlit);
            AddArrow(laneRoot, id + "_fwd_" + k + "_arr", aF, bF, cf, unlit);
            AddArrow(laneRoot, id + "_bwd_" + k + "_arr", bB, aB, cb, unlit);
        }
        MakeLine(laneRoot, id + "_join", new[] { b, b + Vector3.up * 0.2f }, ConnectorGold, 0.8f, unlit);
    }

    static Color LaneColorForIndex(int k, bool fwd)
    {
        if (!fwd)
        {
            if (k == 0) return LaneRight;
            if (k == 1) return LaneSRight;
            if (k == 2) return LaneStraight;
            return LaneLeft;
        }
        if (k == 0) return LaneLeft;
        if (k == 1) return LaneSLeft;
        if (k == 2) return LaneStraight;
        return LaneSRight;
    }

    static void BuildFacility(Transform parent, Shader unlit)
    {
        const float half = 18f;
        Vector3 c = new Vector3(East0, 0.4f, North0);
        var vp = new[]
        {
            c + new Vector3(-half, 0f, -half),
            c + new Vector3(half, 0f, -half),
            c + new Vector3(half, 0f, half),
            c + new Vector3(-half, 0f, half),
            c + new Vector3(-half, 0f, -half)
        };
        MakeLine(parent, "Vertiport09", vp, VertiportBrown, 1.4f, unlit);

        const int segs = 24;
        const float r = 9f;
        var ring = new Vector3[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            float ang = i * Mathf.PI * 2f / segs;
            ring[i] = c + new Vector3(Mathf.Cos(ang) * r, 0.15f, Mathf.Sin(ang) * r);
        }
        MakeLine(parent, "Helipad09", ring, HelipadTeal, 1.1f, unlit);
        MakeLine(parent, "HelipadCrossE",
            new[] { c + new Vector3(-r, 0.2f, 0f), c + new Vector3(r, 0.2f, 0f) }, HelipadTeal, 0.8f, unlit);
        MakeLine(parent, "HelipadCrossN",
            new[] { c + new Vector3(0f, 0.2f, -r), c + new Vector3(0f, 0.2f, r) }, HelipadTeal, 0.8f, unlit);

        Vector3 pad = new Vector3(East0, PadY, North0);
        Vector3 top = new Vector3(East0, CruiseY, North0);
        MakeLine(parent, "Approach09", new[] { pad, top }, ApproachBlue, 2.0f, unlit);
        AddArrow(parent, "Approach09_arr", pad, top, ApproachBlue, unlit);
    }

    static void MakeNode(Transform parent, string name, Vector3 pos, Shader unlit)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * 8f;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = Mat(unlit, NodeWhite);
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    static GameObject SpawnAircraft(Transform parent, Vector3 pos)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Prefabs/Aircarft.prefab");
        GameObject go;
        if (prefab != null)
            go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.transform.localScale = new Vector3(2f, 1f, 2f);
        }
        go.name = "DemoAircraft_09";
        go.transform.SetParent(parent, true);
        go.transform.position = pos;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = Vector3.one * 6f;
        return go;
    }

    static void AddArrow(Transform parent, string name, Vector3 a, Vector3 b, Color color, Shader unlit)
    {
        Vector3 mid = Vector3.Lerp(a, b, 0.58f);
        Vector3 dir = (b - a);
        if (dir.sqrMagnitude < 0.01f) return;
        dir.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, dir);
        if (side.sqrMagnitude < 0.001f) side = Vector3.Cross(Vector3.right, dir);
        side.Normalize();
        float s = 6f;
        Vector3 tip = mid;
        Vector3 left = mid - dir * s + side * s * 0.55f;
        Vector3 right = mid - dir * s - side * s * 0.55f;
        MakeLine(parent, name, new[] { left, tip, right }, color, 1.4f, unlit);
    }

    static void MakeLine(Transform parent, string name, Vector3[] pts, Color color, float width, Shader unlit)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = pts.Length;
        lr.SetPositions(pts);
        lr.startWidth = width;
        lr.endWidth = width;
        lr.numCapVertices = 2;
        lr.numCornerVertices = 2;
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.alignment = LineAlignment.View;
        lr.textureMode = LineTextureMode.Stretch;
        lr.sharedMaterial = Mat(unlit, color);
        lr.startColor = color;
        lr.endColor = color;
    }

    static Material Mat(Shader shader, Color color)
    {
        var m = new Material(shader);
        m.color = color;
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        m.name = "DemoLane_" + ColorUtility.ToHtmlStringRGB(color);
        return m;
    }

    static Transform Child(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }
}
