using System.Collections.Generic;
using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Horizontal generation domain. The BoxCollider on NetworkManager stays as the
    /// sampling AABB; when this polygon has at least three vertices, generators
    /// keep roads only inside the polygon.
    /// </summary>
    public class GenerationBoundary : MonoBehaviour
    {
        public List<Vector3> vertices = new List<Vector3>();

        public bool HasPolygon => vertices != null && vertices.Count >= 3;

        public bool Contains(float x, float z)
        {
            if (!HasPolygon) return true;
            bool inside = false;
            int n = vertices.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float yi = vertices[i].z, yj = vertices[j].z;
                float xi = vertices[i].x, xj = vertices[j].x;
                if (((yi > z) != (yj > z)) &&
                    (x < (xj - xi) * (z - yi) / ((yj - yi) + 1e-12f) + xi))
                    inside = !inside;
            }
            return inside;
        }

        public PolygonRelation RelationToCell(float xMin, float zMin, float size)
        {
            if (!HasPolygon) return PolygonRelation.Inside;
            float xMax = xMin + size;
            float zMax = zMin + size;
            int inside = 0;
            Vector2[] corners =
            {
                new Vector2(xMin, zMin), new Vector2(xMax, zMin),
                new Vector2(xMax, zMax), new Vector2(xMin, zMax)
            };
            for (int i = 0; i < 4; i++)
                if (Contains(corners[i].x, corners[i].y)) inside++;
            bool vertexInsideCell = false;
            for (int i = 0; i < vertices.Count; i++)
            {
                Vector3 p = vertices[i];
                if (p.x >= xMin && p.x <= xMax && p.z >= zMin && p.z <= zMax)
                    vertexInsideCell = true;
            }
            if (inside == 4 && !vertexInsideCell) return PolygonRelation.Inside;
            if (inside == 0 && !vertexInsideCell && !EdgesCrossCell(xMin, zMin, xMax, zMax))
                return PolygonRelation.Outside;
            return PolygonRelation.Partial;
        }

        public void FitBox(BoxCollider box)
        {
            if (box == null || !HasPolygon) return;
            float minX = vertices[0].x, maxX = vertices[0].x;
            float minZ = vertices[0].z, maxZ = vertices[0].z;
            for (int i = 1; i < vertices.Count; i++)
            {
                Vector3 p = vertices[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }
            Vector3 center = box.center;
            Vector3 size = box.size;
            center.x = (minX + maxX) * 0.5f;
            center.z = (minZ + maxZ) * 0.5f;
            size.x = Mathf.Max(1f, maxX - minX);
            size.z = Mathf.Max(1f, maxZ - minZ);
            box.center = center;
            box.size = size;
        }

        public void SeedFromBox(BoxCollider box, float y)
        {
            if (box == null) return;
            Vector3 c = box.center;
            Vector3 s = box.size;
            float minX = c.x - s.x * 0.5f;
            float maxX = c.x + s.x * 0.5f;
            float minZ = c.z - s.z * 0.5f;
            float maxZ = c.z + s.z * 0.5f;
            vertices = new List<Vector3>
            {
                new Vector3(minX, y, minZ),
                new Vector3(maxX, y, minZ),
                new Vector3(maxX, y, maxZ),
                new Vector3(minX, y, maxZ)
            };
        }

        bool EdgesCrossCell(float xMin, float zMin, float xMax, float zMax)
        {
            int n = vertices.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if (SegmentCrossesAabb(vertices[j].x, vertices[j].z, vertices[i].x, vertices[i].z, xMin, zMin, xMax, zMax))
                    return true;
            }
            return false;
        }

        static bool SegmentCrossesAabb(float x0, float z0, float x1, float z1, float minX, float minZ, float maxX, float maxZ)
        {
            float dx = x1 - x0;
            float dz = z1 - z0;
            float t0 = 0f, t1 = 1f;
            if (!Clip(minX - x0, dx, ref t0, ref t1)) return false;
            if (!Clip(x0 - maxX, -dx, ref t0, ref t1)) return false;
            if (!Clip(minZ - z0, dz, ref t0, ref t1)) return false;
            if (!Clip(z0 - maxZ, -dz, ref t0, ref t1)) return false;
            return true;
        }

        static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (Mathf.Abs(q) < 1e-8f) return p <= 0f;
            float r = p / q;
            if (q < 0f)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }
    }

    public enum PolygonRelation
    {
        Inside,
        Outside,
        Partial
    }
}
