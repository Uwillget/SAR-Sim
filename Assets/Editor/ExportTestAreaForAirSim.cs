using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Writes TestArea meshes in the same OBJ frame the existing Shenzhen import uses:
    /// Unity metres, Y-up, X east, Z north -> OBJ centimetres, X north, Y east, Z up.
    /// </summary>
    public static class ExportTestAreaForAirSim
    {
        // Override with SAR_UE_IMPORT_DIR; otherwise the export lands in
        // <project>/ue_import/testarea.
        public static string OutputDirectory
        {
            get
            {
                string fromEnvironment = System.Environment.GetEnvironmentVariable("SAR_UE_IMPORT_DIR");
                string root = string.IsNullOrEmpty(fromEnvironment)
                    ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ue_import"))
                    : fromEnvironment;
                return Path.Combine(root, "testarea");
            }
        }

        [MenuItem("ITSAC/Export TestArea OBJ for AirSim")]
        public static void ExportFromMenu()
        {
            string summary = Export();
            Debug.Log(summary);
            EditorUtility.DisplayDialog("Export TestArea", summary, "OK");
        }

        public static string Export()
        {
            GameObject testArea = GameObject.Find("TestArea");
            if (testArea == null)
                return "TestArea was not found in the open scene.";

            Directory.CreateDirectory(OutputDirectory);
            string objPath = Path.Combine(OutputDirectory, "TestArea.obj");
            string mtlPath = Path.Combine(OutputDirectory, "TestArea.mtl");
            MeshFilter[] filters = testArea.GetComponentsInChildren<MeshFilter>(true);

            int vertexCount = 0;
            int faceCount = 0;
            bool hasBounds = false;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;
            var culture = CultureInfo.InvariantCulture;

            using (var writer = new StreamWriter(objPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("# TestArea Unity Y-up meters -> UE cm Xnorth Yeast Zup");
                writer.WriteLine("mtllib TestArea.mtl");
                writer.WriteLine("usemtl BuildingGray");
                foreach (MeshFilter filter in filters)
                {
                    Mesh mesh = filter.sharedMesh;
                    if (mesh == null) continue;
                    Vector3[] vertices = mesh.vertices;
                    int[] triangles = mesh.triangles;
                    int baseIndex = vertexCount;
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Vector3 world = filter.transform.TransformPoint(vertices[i]);
                        if (!hasBounds)
                        {
                            min = max = world;
                            hasBounds = true;
                        }
                        else
                        {
                            min = Vector3.Min(min, world);
                            max = Vector3.Max(max, world);
                        }
                        writer.Write("v ");
                        writer.Write((world.z * 100f).ToString("F3", culture));
                        writer.Write(' ');
                        writer.Write((world.x * 100f).ToString("F3", culture));
                        writer.Write(' ');
                        writer.WriteLine((world.y * 100f).ToString("F3", culture));
                        vertexCount++;
                    }
                    for (int i = 0; i + 2 < triangles.Length; i += 3)
                    {
                        writer.Write("f ");
                        writer.Write(baseIndex + triangles[i] + 1);
                        writer.Write(' ');
                        writer.Write(baseIndex + triangles[i + 1] + 1);
                        writer.Write(' ');
                        writer.WriteLine(baseIndex + triangles[i + 2] + 1);
                        faceCount++;
                    }
                }
            }

            File.WriteAllText(mtlPath,
                "newmtl BuildingGray\nKd 0.784 0.784 0.784\nKs 0.05 0.05 0.05\nNs 30\nillum 2\n",
                new UTF8Encoding(false));

            string boundsPath = Path.Combine(OutputDirectory, "TestArea.bounds.txt");
            File.WriteAllText(boundsPath,
                "unity_min_east_up_north " + min.ToString("F3") + "\n" +
                "unity_max_east_up_north " + max.ToString("F3") + "\n" +
                "vertices " + vertexCount + "\nfaces " + faceCount + "\n" +
                "material Mat_Building_Gray sRGB 0.784 smoothness 0.3\n",
                new UTF8Encoding(false));

            return "Exported " + vertexCount + " vertices, " + faceCount + " faces to " + objPath;
        }
    }
}
