using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class ExportWhiteboxToUE
{
    // Override with SAR_UE_IMPORT_DIR; otherwise the export lands next to Assets.
    static string OutDir
    {
        get
        {
            string fromEnvironment = System.Environment.GetEnvironmentVariable("SAR_UE_IMPORT_DIR");
            if (!string.IsNullOrEmpty(fromEnvironment)) return fromEnvironment;
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ue_import"));
        }
    }

    [MenuItem("ITSAC/Export Whitebox To UE")]
    public static void Export()
    {
        Directory.CreateDirectory(OutDir);
        Directory.CreateDirectory(Path.Combine(OutDir, "city"));
        Directory.CreateDirectory(Path.Combine(OutDir, "lockers"));

        ExportGo("ShenzhenSKP/Nanshan", Path.Combine(OutDir, "city", "Nanshan.obj"));
        ExportGo("ShenzhenSKP/Futian", Path.Combine(OutDir, "city", "Futian.obj"));
        ExportGo("ShenzhenSKP/Longhua", Path.Combine(OutDir, "city", "Longhua.obj"));
        ExportCombinedPhysicalLockers(Path.Combine(OutDir, "lockers", "PhysicalLockers.obj"));
        ExportCombinedBeacons(Path.Combine(OutDir, "lockers", "LockerBeacons.obj"));
        WriteAirSimSettings(Path.Combine(OutDir, "settings.json"));
        WriteReadme(Path.Combine(OutDir, "README_IMPORT.txt"));
        AssetDatabase.Refresh();
        Debug.Log("[UE Export] wrote " + OutDir);
    }

    static void ExportGo(string path, string outPath)
    {
        var go = GameObject.Find(path);
        if (go == null) throw new System.Exception("missing " + path);
        var mf = go.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) throw new System.Exception("no mesh " + path);
        WriteMeshObj(mf.sharedMesh, go.transform.localToWorldMatrix, go.name, outPath);
    }

    static void ExportCombinedPhysicalLockers(string outPath)
    {
        var lockers = GameObject.Find("ShenzhenSKP/Lockers");
        var sb = new StringBuilder(1 << 20);
        sb.AppendLine("# Physical 8m lockers, UE cm, X=north Y=east Z=up");
        int vBase = 1;
        int count = 0;
        for (int i = 0; i < lockers.transform.childCount; i++)
        {
            var phys = lockers.transform.GetChild(i).Find("PhysicalLocker");
            if (phys == null) continue;
            var mf = phys.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            vBase = AppendMesh(sb, mf.sharedMesh, phys.localToWorldMatrix, vBase);
            count++;
        }
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Debug.Log("[UE Export] physical lockers " + count + " -> " + outPath);
    }

    static void ExportCombinedBeacons(string outPath)
    {
        var lockers = GameObject.Find("ShenzhenSKP/Lockers");
        var sb = new StringBuilder(1 << 22);
        sb.AppendLine("# Visual beacons only, UE cm. Import with no collision.");
        int vBase = 1;
        string[] parts = { "Pad", "Mast", "Head" };
        for (int i = 0; i < lockers.transform.childCount; i++)
        {
            var root = lockers.transform.GetChild(i);
            for (int p = 0; p < parts.Length; p++)
            {
                var t = root.Find(parts[p]);
                if (t == null) continue;
                var mf = t.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                vBase = AppendMesh(sb, mf.sharedMesh, t.localToWorldMatrix, vBase);
            }
        }
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Debug.Log("[UE Export] beacons -> " + outPath);
    }

    static void WriteMeshObj(Mesh mesh, Matrix4x4 localToWorld, string name, string outPath)
    {
        Debug.Log("[UE Export] " + name + " verts=" + mesh.vertexCount);
        using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(false), 1 << 20))
        {
            sw.WriteLine("# " + name + " Unity Y-up meters -> UE cm Xnorth Yeast Zup");
            sw.WriteLine("o " + name);
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            var inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = localToWorld.MultiplyPoint3x4(verts[i]);
                // Unity (east, up, north) m -> UE (north, east, up) cm
                float x = w.z * 100f;
                float y = w.x * 100f;
                float z = w.y * 100f;
                sw.Write("v ");
                sw.Write(x.ToString("F4", inv));
                sw.Write(" ");
                sw.Write(y.ToString("F4", inv));
                sw.Write(" ");
                sw.WriteLine(z.ToString("F4", inv));
            }
            for (int i = 0; i < tris.Length; i += 3)
            {
                int a = tris[i] + 1;
                int b = tris[i + 1] + 1;
                int c = tris[i + 2] + 1;
                sw.Write("f ");
                sw.Write(a);
                sw.Write(" ");
                sw.Write(c);
                sw.Write(" ");
                sw.WriteLine(b);
            }
        }
        Debug.Log("[UE Export] wrote " + outPath);
    }

    static int AppendMesh(StringBuilder sb, Mesh mesh, Matrix4x4 localToWorld, int vBase)
    {
        var verts = mesh.vertices;
        var tris = mesh.triangles;
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 w = localToWorld.MultiplyPoint3x4(verts[i]);
            sb.Append("v ");
            sb.Append((w.z * 100f).ToString("F4", inv));
            sb.Append(" ");
            sb.Append((w.x * 100f).ToString("F4", inv));
            sb.Append(" ");
            sb.Append((w.y * 100f).ToString("F4", inv));
            sb.AppendLine();
        }
        for (int i = 0; i < tris.Length; i += 3)
        {
            sb.Append("f ");
            sb.Append(tris[i] + vBase);
            sb.Append(" ");
            sb.Append(tris[i + 2] + vBase);
            sb.Append(" ");
            sb.Append(tris[i + 1] + vBase);
            sb.AppendLine();
        }
        return vBase + verts.Length;
    }

    static void WriteAirSimSettings(string path)
    {
        const string json = @"{
  ""SeeDocsAt"": ""https://github.com/Microsoft/AirSim/blob/master/docs/settings.md"",
  ""SettingsVersion"": 1.2,
  ""SimMode"": ""Multirotor"",
  ""OriginGeopoint"": {
    ""Latitude"": 22.48486839226679,
    ""Longitude"": 113.94358909787222,
    ""Altitude"": 0
  },
  ""Vehicles"": {
    ""Drone1"": {
      ""VehicleType"": ""SimpleFlight"",
      ""X"": 125.74,
      ""Y"": 94.23,
      ""Z"": -5
    }
  }
}
";
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    static void WriteReadme(string path)
    {
        File.WriteAllText(path, string.Join("\n", new string[] {
            "AirSim / UE4.27 import",
            "Coordinate: Unreal X=North(cm), Y=East(cm), Z=Up(cm). Origin = WGS84 113.943589E, 22.484868N.",
            "1) Import city/*.obj with Generate Lightmap UVs off, Combine Meshes on.",
            "2) Static Mesh: Collision Complexity = Use Complex Collision As Simple. Double-sided if needed.",
            "3) Place at (0,0,0), scale 1. Do not rotate.",
            "4) Import lockers/PhysicalLockers.obj with collision. LockerBeacons.obj: NoCollision.",
            "5) GameMode = AirSimGameMode. Copy settings.json to Documents/AirSim/settings.json.",
            "6) Vehicle NED in settings: X north km-scale meters, Y east, Z down. Example spawn is site 26 converted: N=12574m, E=9423m."
        }), new UTF8Encoding(false));
    }
}
