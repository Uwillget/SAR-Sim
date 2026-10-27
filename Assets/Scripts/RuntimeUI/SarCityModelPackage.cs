using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace RoadNetworkEditor.RuntimeUI
{
    internal sealed class SarRuntimeCityMeshOwner : MonoBehaviour
    {
        public Mesh OwnedMesh;
        public Material[] OwnedMaterials;

        private void OnDestroy()
        {
            if (OwnedMesh != null)
            {
                if (Application.isPlaying) Destroy(OwnedMesh);
                else DestroyImmediate(OwnedMesh);
            }
            foreach (Material material in OwnedMaterials ?? Array.Empty<Material>())
            {
                if (material == null) continue;
                if (Application.isPlaying) Destroy(material);
                else DestroyImmediate(material);
            }
        }
    }

    /// <summary>
    /// Portable city-model format used to bridge Unity's editor-only SketchUp importer
    /// and the standalone player. The package contains baked mesh geometry and material
    /// colours, so it has no dependency on AssetDatabase at runtime.
    /// </summary>
    public static class SarCityModelPackage
    {
        public const string Extension = ".sarcity";
        private const string Magic = "SARCITY";
        private const int Version = 1;

        private sealed class MeshRecord
        {
            public string Name;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] Uv;
            public int[][] SubMeshes;
            public Color[] Colours;
        }

        public static int Export(Transform root, string outputPath)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is required.", nameof(outputPath));

            string path = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            List<MeshRecord> records = Capture(root);

            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(records.Count);
                foreach (MeshRecord record in records) WriteRecord(writer, record);
            }
            return records.Count;
        }

        public static bool TryLoad(string path, Transform targetRoot, out string error, out int meshCount)
        {
            error = null;
            meshCount = 0;
            if (targetRoot == null)
            {
                error = "The city-model container does not exist.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = $"City-model file not found: {path}";
                return false;
            }

            try
            {
                List<MeshRecord> records;
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == Extension) records = ReadPackage(path);
                else if (extension == ".obj") records = ReadObj(path);
                else if (extension == ".dxf") records = ReadDxf(path);
                else
                {
                    error = $"Unsupported city-model format: {extension}. Use {Extension}, .obj, or .dxf.";
                    return false;
                }

                ReplaceChildren(targetRoot, records);
                meshCount = records.Count;
                return true;
            }
            catch (Exception ex)
            {
                error = $"City-model import failed: {ex.Message}";
                Debug.LogError($"[SarCityModelPackage] {error}\n{ex.StackTrace}");
                return false;
            }
        }

        private static List<MeshRecord> Capture(Transform root)
        {
            Matrix4x4 toRoot = root.worldToLocalMatrix;
            var records = new List<MeshRecord>();
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) continue;
                Matrix4x4 matrix = toRoot * filter.transform.localToWorldMatrix;
                Vector3[] vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
                Vector3[] normals = mesh.normals;
                for (int i = 0; i < normals.Length; i++) normals[i] = matrix.MultiplyVector(normals[i]).normalized;
                int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                var subMeshes = new int[subMeshCount][];
                for (int i = 0; i < subMeshCount; i++) subMeshes[i] = mesh.GetTriangles(i);

                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                Material[] materials = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
                var colours = new Color[subMeshCount];
                for (int i = 0; i < colours.Length; i++) colours[i] = MaterialColour(materials, i);
                records.Add(new MeshRecord
                {
                    Name = filter.gameObject.name,
                    Vertices = vertices,
                    Normals = normals,
                    Uv = mesh.uv,
                    SubMeshes = subMeshes,
                    Colours = colours
                });
            }
            return records;
        }

        private static Color MaterialColour(Material[] materials, int index)
        {
            if (materials != null && materials.Length > 0)
            {
                Material material = materials[Mathf.Min(index, materials.Length - 1)];
                if (material != null)
                {
                    if (material.HasProperty("_BaseColor")) return material.GetColor("_BaseColor");
                    if (material.HasProperty("_Color")) return material.GetColor("_Color");
                }
            }
            return new Color(0.68f, 0.7f, 0.72f, 1f);
        }

        private static void WriteRecord(BinaryWriter writer, MeshRecord record)
        {
            writer.Write(record.Name ?? "Building");
            WriteVectors(writer, record.Vertices);
            WriteVectors(writer, record.Normals);
            writer.Write(record.Uv?.Length ?? 0);
            foreach (Vector2 value in record.Uv ?? Array.Empty<Vector2>())
            {
                writer.Write(value.x);
                writer.Write(value.y);
            }
            writer.Write(record.SubMeshes?.Length ?? 0);
            foreach (int[] triangles in record.SubMeshes ?? Array.Empty<int[]>())
            {
                writer.Write(triangles?.Length ?? 0);
                foreach (int index in triangles ?? Array.Empty<int>()) writer.Write(index);
            }
            writer.Write(record.Colours?.Length ?? 0);
            foreach (Color colour in record.Colours ?? Array.Empty<Color>())
            {
                writer.Write(colour.r); writer.Write(colour.g); writer.Write(colour.b); writer.Write(colour.a);
            }
        }

        private static void WriteVectors(BinaryWriter writer, Vector3[] values)
        {
            writer.Write(values?.Length ?? 0);
            foreach (Vector3 value in values ?? Array.Empty<Vector3>())
            {
                writer.Write(value.x); writer.Write(value.y); writer.Write(value.z);
            }
        }

        private static List<MeshRecord> ReadPackage(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.ReadString() != Magic) throw new InvalidDataException("This is not a valid SAR city-model package. ");
                int version = reader.ReadInt32();
                if (version != Version) throw new InvalidDataException($"Unsupported city-model package version: {version}. ");
                int count = ReadCount(reader, "mesh", 100000);
                var records = new List<MeshRecord>(count);
                for (int i = 0; i < count; i++)
                {
                    var record = new MeshRecord { Name = reader.ReadString() };
                    record.Vertices = ReadVectors(reader, 20000000);
                    record.Normals = ReadVectors(reader, 20000000);
                    int uvCount = ReadCount(reader, "uv", 20000000);
                    record.Uv = new Vector2[uvCount];
                    for (int j = 0; j < uvCount; j++) record.Uv[j] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    int subMeshCount = ReadCount(reader, "submesh", 4096);
                    record.SubMeshes = new int[subMeshCount][];
                    for (int j = 0; j < subMeshCount; j++)
                    {
                        int indexCount = ReadCount(reader, "index", 60000000);
                        record.SubMeshes[j] = new int[indexCount];
                        for (int k = 0; k < indexCount; k++) record.SubMeshes[j][k] = reader.ReadInt32();
                    }
                    int colourCount = ReadCount(reader, "colour", 4096);
                    record.Colours = new Color[colourCount];
                    for (int j = 0; j < colourCount; j++)
                        record.Colours[j] = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    Validate(record);
                    records.Add(record);
                }
                return records;
            }
        }

        private static int ReadCount(BinaryReader reader, string label, int maximum)
        {
            int value = reader.ReadInt32();
            if (value < 0 || value > maximum) throw new InvalidDataException($"Invalid {label} count: {value}. ");
            return value;
        }

        private static Vector3[] ReadVectors(BinaryReader reader, int maximum)
        {
            int count = ReadCount(reader, "vector", maximum);
            var values = new Vector3[count];
            for (int i = 0; i < count; i++) values[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return values;
        }

        private static void Validate(MeshRecord record)
        {
            if (record.Vertices == null || record.Vertices.Length == 0)
                throw new InvalidDataException("The city model contains an empty mesh. ");
            foreach (int index in record.SubMeshes.SelectMany(value => value))
                if (index < 0 || index >= record.Vertices.Length)
                    throw new InvalidDataException("The city model contains an out-of-range triangle index. ");
        }

        private static void ReplaceChildren(Transform targetRoot, IEnumerable<MeshRecord> records)
        {
            var created = new List<GameObject>();
            int buildingLayer = LayerMask.NameToLayer("Buildings");
            if (buildingLayer < 0)
                buildingLayer = targetRoot.gameObject.layer;
            else
                targetRoot.gameObject.layer = buildingLayer;
            try
            {
                foreach (MeshRecord record in records)
                {
                    var child = new GameObject(string.IsNullOrWhiteSpace(record.Name) ? "Building" : record.Name);
                    child.layer = buildingLayer;
                    child.SetActive(false);
                    created.Add(child);
                    child.transform.SetParent(targetRoot, false);
                    Mesh mesh = BuildMesh(record);
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer renderer = child.AddComponent<MeshRenderer>();
                    Material[] materials = BuildMaterials(record);
                    renderer.sharedMaterials = materials;
                    MeshCollider collider = child.AddComponent<MeshCollider>();
                    collider.sharedMesh = mesh;
                    collider.convex = false;
                    SarRuntimeCityMeshOwner owner = child.AddComponent<SarRuntimeCityMeshOwner>();
                    owner.OwnedMesh = mesh;
                    owner.OwnedMaterials = materials;
                }

                for (int i = targetRoot.childCount - 1; i >= 0; i--)
                {
                    GameObject old = targetRoot.GetChild(i).gameObject;
                    if (created.Contains(old)) continue;
                    // Destroy is deferred in a player. Detach immediately so an import
                    // followed by an export in the same frame cannot capture geometry
                    // from the previously active scene.
                    old.SetActive(false);
                    old.transform.SetParent(null, false);
                    if (Application.isPlaying) UnityEngine.Object.Destroy(old);
                    else UnityEngine.Object.DestroyImmediate(old);
                }
                foreach (GameObject child in created) child.SetActive(true);
            }
            catch
            {
                foreach (GameObject child in created)
                {
                    if (child == null) continue;
                    if (Application.isPlaying) UnityEngine.Object.Destroy(child);
                    else UnityEngine.Object.DestroyImmediate(child);
                }
                throw;
            }
        }

        private static Mesh BuildMesh(MeshRecord record)
        {
            var mesh = new Mesh { name = record.Name + "_Mesh", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = record.Vertices;
            bool validNormals = record.Normals != null && record.Normals.Length == record.Vertices.Length &&
                                record.Normals.Any(normal => normal.sqrMagnitude > 0.01f);
            if (validNormals) mesh.normals = record.Normals;
            if (record.Uv != null && record.Uv.Length == record.Vertices.Length) mesh.uv = record.Uv;
            mesh.subMeshCount = Mathf.Max(1, record.SubMeshes.Length);
            for (int i = 0; i < record.SubMeshes.Length; i++) mesh.SetTriangles(record.SubMeshes[i], i, false);
            if (!validNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material[] BuildMaterials(MeshRecord record)
        {
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse") ?? Shader.Find("Sprites/Default");
            int count = Mathf.Max(1, record.SubMeshes.Length);
            var materials = new Material[count];
            for (int i = 0; i < count; i++)
            {
                var material = new Material(shader) { name = record.Name + "_Material" };
                Color colour = record.Colours != null && record.Colours.Length > 0
                    ? record.Colours[Mathf.Min(i, record.Colours.Length - 1)]
                    : new Color(0.68f, 0.7f, 0.72f, 1f);
                if (material.HasProperty("_Color")) material.color = colour;
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
                materials[i] = material;
            }
            return materials;
        }

        private static List<MeshRecord> ReadObj(string path)
        {
            var sourceVertices = new List<Vector3>();
            var sourceNormals = new List<Vector3>();
            var sourceUv = new List<Vector2>();
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uv = new List<Vector2>();
            var triangles = new List<int>();
            var map = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (parts[0] == "v" && parts.Length >= 4)
                    sourceVertices.Add(new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
                else if (parts[0] == "vn" && parts.Length >= 4)
                    sourceNormals.Add(new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])).normalized);
                else if (parts[0] == "vt" && parts.Length >= 3)
                    sourceUv.Add(new Vector2(Parse(parts[1]), Parse(parts[2])));
                else if (parts[0] == "f" && parts.Length >= 4)
                {
                    int first = ResolveObjVertex(parts[1], sourceVertices, sourceUv, sourceNormals, vertices, uv, normals, map);
                    int previous = ResolveObjVertex(parts[2], sourceVertices, sourceUv, sourceNormals, vertices, uv, normals, map);
                    for (int i = 3; i < parts.Length; i++)
                    {
                        int current = ResolveObjVertex(parts[i], sourceVertices, sourceUv, sourceNormals, vertices, uv, normals, map);
                        triangles.Add(first); triangles.Add(previous); triangles.Add(current);
                        previous = current;
                    }
                }
            }
            if (vertices.Count == 0 || triangles.Count == 0) throw new InvalidDataException("The OBJ file contains no usable triangle mesh. ");
            return new List<MeshRecord>
            {
                new MeshRecord
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    Vertices = vertices.ToArray(), Normals = normals.ToArray(), Uv = uv.ToArray(),
                    SubMeshes = new[] { triangles.ToArray() },
                    Colours = new[] { new Color(0.68f, 0.7f, 0.72f, 1f) }
                }
            };
        }

        private static int ResolveObjVertex(string token, IList<Vector3> sourceVertices, IList<Vector2> sourceUv,
            IList<Vector3> sourceNormals, IList<Vector3> vertices, IList<Vector2> uv, IList<Vector3> normals,
            IDictionary<string, int> map)
        {
            if (map.TryGetValue(token, out int existing)) return existing;
            string[] indices = token.Split('/');
            int vi = ObjIndex(indices[0], sourceVertices.Count);
            int ti = indices.Length > 1 && indices[1].Length > 0 ? ObjIndex(indices[1], sourceUv.Count) : -1;
            int ni = indices.Length > 2 && indices[2].Length > 0 ? ObjIndex(indices[2], sourceNormals.Count) : -1;
            if (vi < 0 || vi >= sourceVertices.Count) throw new InvalidDataException($"Invalid OBJ vertex index: {token}. ");
            int index = vertices.Count;
            vertices.Add(sourceVertices[vi]);
            uv.Add(ti >= 0 && ti < sourceUv.Count ? sourceUv[ti] : Vector2.zero);
            normals.Add(ni >= 0 && ni < sourceNormals.Count ? sourceNormals[ni] : Vector3.zero);
            map[token] = index;
            return index;
        }

        private static int ObjIndex(string value, int count)
        {
            int parsed = int.Parse(value, CultureInfo.InvariantCulture);
            return parsed > 0 ? parsed - 1 : count + parsed;
        }

        private static float Parse(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

        private readonly struct DxfPair
        {
            public readonly int Code;
            public readonly string Value;

            public DxfPair(int code, string value)
            {
                Code = code;
                Value = value;
            }
        }

        /// <summary>
        /// Reads the geometry-bearing subset used by many CAD city exports. DXF is
        /// a broad interchange format; 3DFACE/SOLID entities are imported directly,
        /// while closed LWPOLYLINE footprints require a non-zero thickness. A pure
        /// 2D road-line DXF is deliberately rejected because it contains no building
        /// volume to display.
        /// </summary>
        private static List<MeshRecord> ReadDxf(string path)
        {
            List<DxfPair> pairs = ReadDxfPairs(path);
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            int entityCount = 0;

            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Code != 0) continue;
                string entity = pairs[i].Value.Trim().ToUpperInvariant();
                int end = i + 1;
                while (end < pairs.Count && pairs[end].Code != 0) end++;
                if (entity == "3DFACE" || entity == "SOLID")
                {
                    if (AppendDxfFace(pairs, i + 1, end, vertices, triangles)) entityCount++;
                }
                else if (entity == "LWPOLYLINE")
                {
                    if (AppendDxfExtrudedPolyline(pairs, i + 1, end, vertices, triangles)) entityCount++;
                }
                i = end - 1;
            }

            if (vertices.Count == 0 || triangles.Count == 0)
                throw new InvalidDataException(
                    "The DXF contains no usable 3D building faces. Export a DXF with 3DFACE or SOLID entities from the CAD application, " +
                    "or use closed LWPOLYLINE entities with thickness. Pure 2D roads or contours cannot form a city model. ");

            return new List<MeshRecord>
            {
                new MeshRecord
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    Vertices = vertices.ToArray(),
                    Normals = Array.Empty<Vector3>(),
                    Uv = Array.Empty<Vector2>(),
                    SubMeshes = new[] { triangles.ToArray() },
                    Colours = new[] { new Color(0.68f, 0.7f, 0.72f, 1f) }
                }
            };
        }

        private static List<DxfPair> ReadDxfPairs(string path)
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2 || lines.Length % 2 != 0)
                throw new InvalidDataException("The DXF is not a valid ASCII group-code file. ");
            var pairs = new List<DxfPair>(lines.Length / 2);
            for (int i = 0; i < lines.Length; i += 2)
            {
                if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                    throw new InvalidDataException($"Invalid DXF group code at line {i + 1}. ");
                pairs.Add(new DxfPair(code, lines[i + 1]));
            }
            return pairs;
        }

        private static bool AppendDxfFace(IReadOnlyList<DxfPair> pairs, int start, int end,
            ICollection<Vector3> vertices, ICollection<int> triangles)
        {
            var points = new Vector3[4];
            var present = new bool[4, 3];
            for (int i = start; i < end; i++)
            {
                int axis;
                int point;
                int code = pairs[i].Code;
                if (code >= 10 && code <= 13) { axis = 0; point = code - 10; }
                else if (code >= 20 && code <= 23) { axis = 1; point = code - 20; }
                else if (code >= 30 && code <= 33) { axis = 2; point = code - 30; }
                else continue;
                float value = Parse(pairs[i].Value);
                if (axis == 0) points[point].x = value;
                else if (axis == 1) points[point].z = value;
                else points[point].y = value; // DXF Z-up -> Unity Y-up.
                present[point, axis] = true;
            }
            if (!PointPresent(present, 0) || !PointPresent(present, 1) || !PointPresent(present, 2)) return false;

            int offset = vertices.Count;
            vertices.Add(points[0]); vertices.Add(points[1]); vertices.Add(points[2]);
            triangles.Add(offset); triangles.Add(offset + 1); triangles.Add(offset + 2);
            if (PointPresent(present, 3) && (points[3] - points[2]).sqrMagnitude > 0.000001f)
            {
                vertices.Add(points[3]);
                triangles.Add(offset); triangles.Add(offset + 2); triangles.Add(offset + 3);
            }
            return true;
        }

        private static bool PointPresent(bool[,] present, int point) =>
            present[point, 0] && present[point, 1] && present[point, 2];

        private static bool AppendDxfExtrudedPolyline(IReadOnlyList<DxfPair> pairs, int start, int end,
            ICollection<Vector3> vertices, ICollection<int> triangles)
        {
            var footprint = new List<Vector2>();
            float elevation = 0f;
            float thickness = 0f;
            int flags = 0;
            for (int i = start; i < end; i++)
            {
                switch (pairs[i].Code)
                {
                    case 10:
                        footprint.Add(new Vector2(Parse(pairs[i].Value), 0f));
                        break;
                    case 20:
                        if (footprint.Count > 0)
                        {
                            Vector2 point = footprint[footprint.Count - 1];
                            point.y = Parse(pairs[i].Value);
                            footprint[footprint.Count - 1] = point;
                        }
                        break;
                    case 38: elevation = Parse(pairs[i].Value); break;
                    case 39: thickness = Parse(pairs[i].Value); break;
                    case 70: int.TryParse(pairs[i].Value.Trim(), out flags); break;
                }
            }
            bool closed = (flags & 1) != 0;
            if (!closed || footprint.Count < 3 || Mathf.Abs(thickness) < 0.0001f) return false;

            int offset = vertices.Count;
            float top = elevation + thickness;
            for (int i = 0; i < footprint.Count; i++)
                vertices.Add(new Vector3(footprint[i].x, elevation, footprint[i].y));
            for (int i = 0; i < footprint.Count; i++)
                vertices.Add(new Vector3(footprint[i].x, top, footprint[i].y));

            bool clockwise = SignedArea(footprint) < 0f;
            for (int i = 1; i < footprint.Count - 1; i++)
            {
                AddTriangle(triangles, offset, offset + i, offset + i + 1, !clockwise);
                AddTriangle(triangles, offset + footprint.Count, offset + footprint.Count + i,
                    offset + footprint.Count + i + 1, clockwise);
            }
            for (int i = 0; i < footprint.Count; i++)
            {
                int next = (i + 1) % footprint.Count;
                int b0 = offset + i;
                int b1 = offset + next;
                int t0 = offset + footprint.Count + i;
                int t1 = offset + footprint.Count + next;
                AddTriangle(triangles, b0, b1, t1, clockwise);
                AddTriangle(triangles, b0, t1, t0, clockwise);
            }
            return true;
        }

        private static float SignedArea(IReadOnlyList<Vector2> points)
        {
            float area = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 current = points[i];
                Vector2 next = points[(i + 1) % points.Count];
                area += current.x * next.y - next.x * current.y;
            }
            return area * 0.5f;
        }

        private static void AddTriangle(ICollection<int> triangles, int a, int b, int c, bool reverse)
        {
            triangles.Add(a);
            triangles.Add(reverse ? c : b);
            triangles.Add(reverse ? b : c);
        }
    }
}
