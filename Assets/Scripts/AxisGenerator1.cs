using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Axis Generator Method 1: Skeleton extraction + graph simplification.
    /// Uses voxel-based EDT + Zhang-Suen thinning + Douglas-Peucker simplification.
    /// Attach to the same GameObject as NetworkManager or use standalone.
    /// </summary>
    public class AxisGenerator1 : AxisGeneratorBase
    {
        [Header("Generation Parameters")]
        [Tooltip("Voxel grid cell size. Smaller = finer detail, slower.")]
        public float voxelSize = 5f;
        [Tooltip("Collinear straightening: max deviation before a waypoint is kept.")]
        public float straightLimit = 8f;
        [Tooltip("Dead-end pruning: max length of leaf branches to remove.")]
        public float clusterLimit = 50f;

        // ============================================================
        // PUBLIC API
        // ============================================================

        public override void GenerateAxisNetwork()
        {
            Debug.Log("[AxisGenerator1] Starting axis network generation...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (detectionArea == null)
                detectionArea = GetComponent<BoxCollider>();
            if (detectionArea == null)
            {
                Debug.LogError("[AxisGenerator1] No BoxCollider assigned. Please set detectionArea or attach a BoxCollider.");
                return;
            }

            // 1. Create occupancy grid from building colliders
            int[,] grid = CreateGridData(out Vector3 origin);
            int rows = grid.GetLength(0), cols = grid.GetLength(1);
            int passable = 0, blocked = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                { if (grid[r, c] == 1) passable++; else blocked++; }
            Debug.Log($"[AxisGenerator1] Grid: {rows}x{cols}, passable={passable}, blocked={blocked}");

            // 2. EDT + Zhang-Suen thinning → skeleton
            float[,] distMap = ComputeEDT(grid, rows, cols);
            int[,] skeleton = Thinning(grid, rows, cols);
            int skelCount = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (skeleton[r, c] == 1) skelCount++;
            Debug.Log($"[AxisGenerator1] Skeleton pixels: {skelCount}");

            // 3. Build graph from skeleton
            var rawGraph = BuildGraphFromSkeleton(skeleton, distMap, rows, cols, origin);
            Debug.Log($"[AxisGenerator1] Raw graph nodes: {rawGraph.Count}");

            // 3b. Keep only the largest connected component
            rawGraph = KeepLargestComponent(rawGraph);
            Debug.Log($"[AxisGenerator1] Largest component: {rawGraph.Count} nodes");

            // 4. Prune dead-end branches
            var pruned = PruneGraph(rawGraph);
            Debug.Log($"[AxisGenerator1] After pruning: {pruned.Count} nodes");

            // 5. Simplify: merge degree-2 collinear chains.
            //    Keep the pre-simplification graph for path walking between feature nodes.
            var preSimplify = CloneGraph(pruned);
            var simplified = SimplifyGraph(pruned);
            Debug.Log($"[AxisGenerator1] After simplification: {simplified.Count} nodes");


            // 6. Build edge centerlines + save.
            //    Walk the ORIGINAL (pre-simplify) graph to find paths between feature nodes.
            var network = BuildNetworkData(simplified, preSimplify);
            DeadEndPruner.Prune(network);
            StampRoadAltitude(network);
            SaveNetwork(network);

            sw.Stop();
            Debug.Log($"[AxisGenerator1] Done in {sw.ElapsedMilliseconds}ms. Output: {outputPath}");
        }

        // ============================================================
        // 1. GRID CREATION
        // ============================================================

        private int[,] CreateGridData(out Vector3 origin)
        {
            Vector3 size = detectionArea.size;
            Vector3 center = detectionArea.center;
            origin = center - new Vector3(size.x / 2, 0, size.z / 2);
            origin.y = detectionHeight;
            int gw = Mathf.CeilToInt(size.x / voxelSize);
            int gh = Mathf.CeilToInt(size.z / voxelSize);
            int[,] grid = new int[gh, gw];
            const float halfY = 10f;
            Vector3 halfExtents = new Vector3(voxelSize / 2f, halfY, voxelSize / 2f);

            for (int z = 0; z < gh; z++)
            {
                for (int x = 0; x < gw; x++)
                {
                    Vector3 pos = new Vector3(
                        origin.x + x * voxelSize + voxelSize / 2f,
                        detectionHeight,
                        origin.z + z * voxelSize + voxelSize / 2f);
                    bool blocked = OutsideBoundary(pos.x, pos.z) ||
                        Physics.CheckBox(pos, halfExtents, Quaternion.identity, obstacleLayerMask);
                    grid[z, x] = blocked ? 0 : 1; // 0=blocked, 1=passable
                }
            }
            return grid;
        }

        // ============================================================
        // 2. EUCLIDEAN DISTANCE TRANSFORM (2-pass)
        // ============================================================

        private float[,] ComputeEDT(int[,] grid, int rows, int cols)
        {
            float INF = 1e9f;
            float[,] dist = new float[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    dist[r, c] = grid[r, c] == 1 ? INF : 0f;

            // Forward pass
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    if (dist[r, c] == 0) continue;
                    float up = r > 0 ? dist[r - 1, c] : INF;
                    float left = c > 0 ? dist[r, c - 1] : INF;
                    dist[r, c] = Mathf.Min(dist[r, c], Mathf.Min(up + 1, left + 1));
                }
            // Backward pass
            for (int r = rows - 1; r >= 0; r--)
                for (int c = cols - 1; c >= 0; c--)
                {
                    if (dist[r, c] == 0) continue;
                    float down = r < rows - 1 ? dist[r + 1, c] : INF;
                    float right = c < cols - 1 ? dist[r, c + 1] : INF;
                    dist[r, c] = Mathf.Min(dist[r, c], Mathf.Min(down + 1, right + 1));
                }
            return dist;
        }

        // ============================================================
        // 3. ZHANG-SUEN THINNING
        // ============================================================

        private static int[,] Thinning(int[,] grid, int rows, int cols)
        {
            int[,] s = (int[,])grid.Clone();
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int step = 0; step < 2; step++)
                {
                    var toClear = new List<Vector2Int>();
                    for (int r = 1; r < rows - 1; r++)
                    {
                        for (int c = 1; c < cols - 1; c++)
                        {
                            if (s[r, c] == 0) continue;
                            int p2 = s[r - 1, c], p3 = s[r - 1, c + 1], p4 = s[r, c + 1],
                                p5 = s[r + 1, c + 1], p6 = s[r + 1, c], p7 = s[r + 1, c - 1],
                                p8 = s[r, c - 1], p9 = s[r - 1, c - 1];
                            int A = (p2 == 0 && p3 == 1 ? 1 : 0) + (p3 == 0 && p4 == 1 ? 1 : 0) +
                                    (p4 == 0 && p5 == 1 ? 1 : 0) + (p5 == 0 && p6 == 1 ? 1 : 0) +
                                    (p6 == 0 && p7 == 1 ? 1 : 0) + (p7 == 0 && p8 == 1 ? 1 : 0) +
                                    (p8 == 0 && p9 == 1 ? 1 : 0) + (p9 == 0 && p2 == 1 ? 1 : 0);
                            int B = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                            int m1 = step == 0 ? (p2 * p4 * p6) : (p2 * p4 * p8);
                            int m2 = step == 0 ? (p4 * p6 * p8) : (p2 * p6 * p8);
                            if (A == 1 && B >= 2 && B <= 6 && m1 == 0 && m2 == 0)
                                toClear.Add(new Vector2Int(r, c));
                        }
                    }
                    if (toClear.Count > 0)
                    {
                        foreach (var p in toClear) s[p.x, p.y] = 0;
                        changed = true;
                    }
                }
            }
            return s;
        }

        // ============================================================
        // 4. BUILD GRAPH FROM SKELETON
        // ============================================================

        private class PathNode
        {
            public int id;
            public Vector3 pos;
            public List<int> neighbors = new List<int>();
        }

        private List<PathNode> BuildGraphFromSkeleton(int[,] skeleton, float[,] distMap, int rows, int cols, Vector3 origin)
        {
            var nodes = new List<PathNode>();
            var coordToId = new Dictionary<string, int>();
            int id = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (skeleton[r, c] == 1)
                    {
                        coordToId[$"{r},{c}"] = id;
                        nodes.Add(new PathNode
                        {
                            id = id,
                            pos = new Vector3(origin.x + c * voxelSize + voxelSize / 2, detectionHeight,
                                              origin.z + r * voxelSize + voxelSize / 2)
                        });
                        id++;
                    }

            int[] dr = { -1, -1, -1, 0, 0, 1, 1, 1 };
            int[] dc = { -1, 0, 1, -1, 1, -1, 0, 1 };
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    if (skeleton[r, c] == 0) continue;
                    int myId = coordToId[$"{r},{c}"];
                    for (int k = 0; k < 8; k++)
                    {
                        int nr = r + dr[k], nc = c + dc[k];
                        string nk = $"{nr},{nc}";
                        if (!coordToId.ContainsKey(nk)) continue;
                        if (dr[k] != 0 && dc[k] != 0)
                        {
                            bool horiz = coordToId.ContainsKey($"{r},{nc}");
                            bool vert = coordToId.ContainsKey($"{nr},{c}");
                            if (horiz || vert) continue; // skip diagonal if orthogonal exists
                        }
                        if (!nodes[myId].neighbors.Contains(coordToId[nk]))
                            nodes[myId].neighbors.Add(coordToId[nk]);
                    }
                }
            return nodes;
        }

        // ============================================================
        // 4b. KEEP LARGEST CONNECTED COMPONENT
        // ============================================================

        private List<PathNode> KeepLargestComponent(List<PathNode> nodes)
        {
            if (nodes.Count == 0) return nodes;
            var visited = new bool[nodes.Count];
            var components = new List<List<int>>();

            for (int i = 0; i < nodes.Count; i++)
            {
                if (visited[i]) continue;
                var comp = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(i); visited[i] = true;
                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    comp.Add(cur);
                    foreach (int nb in nodes[cur].neighbors)
                        if (!visited[nb]) { visited[nb] = true; queue.Enqueue(nb); }
                }
                components.Add(comp);
            }

            if (components.Count <= 1) return nodes;
            components.Sort((a, b) => b.Count.CompareTo(a.Count));
            var keep = new HashSet<int>(components[0]);
            Debug.Log($"[AxisGenerator1] {components.Count} components, keeping largest: {components[0].Count} nodes (others: {string.Join(",", components.Skip(1).Select(c=>c.Count))})");

            var result = new List<PathNode>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!keep.Contains(i)) continue;
                // neighbors store original node ids. Drop links into removed components
                // but do not renumber ids; later stages look nodes up by id.
                nodes[i].neighbors.RemoveAll(nb => !keep.Contains(nb));
                result.Add(nodes[i]);
            }
            return result;
        }

        // ============================================================
        // 5. PRUNE DEAD-END BRANCHES
        // ============================================================

        private List<PathNode> PruneGraph(List<PathNode> nodes)
        {
            var byId = new Dictionary<int, PathNode>();
            foreach (var node in nodes) byId[node.id] = node;

            var degree = new Dictionary<int, int>();
            var toDelete = new HashSet<int>();
            var queue = new Queue<int>();
            var inQueue = new HashSet<int>();

            foreach (var node in nodes)
            {
                int d = 0;
                foreach (int nb in node.neighbors)
                    if (byId.ContainsKey(nb)) d++;
                degree[node.id] = d;
                if (d == 0) toDelete.Add(node.id);
                else if (d == 1) { queue.Enqueue(node.id); inQueue.Add(node.id); }
            }

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                toDelete.Add(cur);
                if (!byId.TryGetValue(cur, out var curNode)) continue;
                foreach (int nb in curNode.neighbors)
                {
                    if (!byId.ContainsKey(nb) || toDelete.Contains(nb)) continue;
                    degree[nb]--;
                    if (degree[nb] <= 1 && inQueue.Add(nb))
                        queue.Enqueue(nb);
                }
            }

            var result = new List<PathNode>();
            foreach (var node in nodes)
            {
                if (toDelete.Contains(node.id)) continue;
                node.neighbors.RemoveAll(nb => !byId.ContainsKey(nb) || toDelete.Contains(nb));
                result.Add(node);
            }
            return result;
        }

        // ============================================================
        private List<PathNode> CloneGraph(List<PathNode> nodes)
        {
            var clone = new List<PathNode>();
            foreach (var n in nodes) clone.Add(new PathNode { id = n.id, pos = n.pos, neighbors = new List<int>(n.neighbors) });
            return clone;
        }

        // 6. SIMPLIFY: Douglas-Peucker + feature node detection + clustering
        //    Matches ReferenceScripts/NetworkGenerator.cs SimpliflyGraph
        // ============================================================

        private Dictionary<int, PathNode> featureNodes;
        private Dictionary<int, PathNode> allNodesById;

        private bool IsCornerNode(PathNode node)
        {
            if (node.neighbors.Count != 2) return false;
            // The reference uses GridPos, but we don't store it in PathNode.
            // Instead check if the two neighbors form a non-straight angle
            Vector3 pos = node.pos;
            var a = allNodesById[node.neighbors[0]];
            var b = allNodesById[node.neighbors[1]];
            float dot = Vector3.Dot((a.pos - pos).normalized, (b.pos - pos).normalized);
            return dot > -0.7f && dot < 0.7f; // not collinear
        }

        private List<Vector3> SimplifyClearCenterline(List<Vector3> raw)
        {
            var result = new List<Vector3>();
            if (raw == null || raw.Count == 0) return result;
            var keep = new List<int> { 0 };
            AppendClearShortcut(raw, 0, raw.Count - 1, keep);
            if (raw.Count > 1) keep.Add(raw.Count - 1);
            keep.Sort();
            int last = -1;
            for (int i = 0; i < keep.Count; i++)
            {
                if (keep[i] == last) continue;
                Vector3 p = raw[keep[i]];
                p.y = detectionHeight;
                result.Add(p);
                last = keep[i];
            }
            return result;
        }

        private void AppendClearShortcut(List<Vector3> raw, int a, int b, List<int> keep)
        {
            if (b <= a + 1) return;
            int farthest = a + 1;
            float maxDist = 0f;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistancePointToLine(raw[i], raw[a], raw[b]);
                if (d > maxDist) { maxDist = d; farthest = i; }
            }
            bool clear = !SegmentHitsBuilding(raw[a], raw[b]);
            if (clear && maxDist <= straightLimit) return;
            if (farthest <= a || farthest >= b) return;
            AppendClearShortcut(raw, a, farthest, keep);
            keep.Add(farthest);
            AppendClearShortcut(raw, farthest, b, keep);
        }

        private bool SegmentHitsBuilding(Vector3 a, Vector3 b)
        {
            Vector3 dir = b - a;
            dir.y = 0f;
            float len = dir.magnitude;
            if (len < 0.05f) return false;
            const float halfY = 10f;
            Vector3 center = (a + b) * 0.5f;
            center.y = detectionHeight;
            return Physics.CheckBox(
                center,
                new Vector3(Mathf.Max(0.5f, voxelSize * 0.45f), halfY, len * 0.5f),
                Quaternion.LookRotation(dir),
                obstacleLayerMask,
                QueryTriggerInteraction.Ignore);
        }

        private static float PolylineLength(List<Vector3> pts)
        {
            float length = 0f;
            for (int i = 1; i < pts.Count; i++)
                length += Vector3.Distance(pts[i - 1], pts[i]);
            return length;
        }

        private float DistancePointToLine(Vector3 point, Vector3 lineStart, Vector3 lineEnd)
        {
            Vector3 line = lineEnd - lineStart;
            if (line.sqrMagnitude < 0.0001f) return Vector3.Distance(point, lineStart);
            return Vector3.Cross(point - lineStart, line).magnitude / line.magnitude;
        }

        private void ApplyDouglasPeucker(int startIdx, int endIdx, List<int> path)
        {
            if (startIdx >= endIdx - 1) return;
            Vector3 pStart = allNodesById[path[startIdx]].pos;
            Vector3 pEnd = allNodesById[path[endIdx]].pos;
            float maxDist = 0f;
            int farthest = -1;
            for (int i = startIdx + 1; i < endIdx; i++)
            {
                float d = DistancePointToLine(allNodesById[path[i]].pos, pStart, pEnd);
                if (d > maxDist) { maxDist = d; farthest = i; }
            }
            if (maxDist < straightLimit)
            {
                for (int i = startIdx + 1; i < endIdx; i++)
                    featureNodes.Remove(path[i]);
            }
            else
            {
                ApplyDouglasPeucker(startIdx, farthest, path);
                ApplyDouglasPeucker(farthest, endIdx, path);
            }
        }

        private List<PathNode> SimplifyGraph(List<PathNode> nodes)
        {
            allNodesById = new Dictionary<int, PathNode>();
            foreach (var n in nodes) allNodesById[n.id] = n;

            // Step 1: Identify feature nodes (degree != 2 or corner)
            featureNodes = new Dictionary<int, PathNode>();
            foreach (var n in nodes)
                if (n.neighbors.Count != 2 || IsCornerNode(n))
                    featureNodes[n.id] = n;

            Debug.Log($"[AxisGenerator1] Feature nodes: {featureNodes.Count}");

            // Step 2: Walk degree-2 chains, apply Douglas-Peucker
            var visitedSegments = new HashSet<string>();
            var currentFeatures = new List<PathNode>(featureNodes.Values);
            foreach (var startNode in currentFeatures)
            {
                if (!featureNodes.ContainsKey(startNode.id)) continue;
                foreach (int neighbor in startNode.neighbors)
                {
                    int a = startNode.id, b = neighbor;
                    string segKey = a < b ? $"{a}_{b}" : $"{b}_{a}";
                    if (visitedSegments.Contains(segKey)) continue;
                    visitedSegments.Add(segKey);

                    int prev = a, cur = b;
                    var path = new List<int> { a };
                    int finalNodeId = -1;
                    while (true)
                    {
                        visitedSegments.Add(prev < cur ? $"{prev}_{cur}" : $"{cur}_{prev}");
                        if (featureNodes.ContainsKey(cur)) path.Add(cur);
                        if (!allNodesById.TryGetValue(cur, out var curNode)) break;
                        if (curNode.neighbors.Count != 2) { finalNodeId = cur; break; }
                        int next = curNode.neighbors[0] == prev ? curNode.neighbors[1] : curNode.neighbors[0];
                        prev = cur; cur = next;
                        if (cur == a) { finalNodeId = cur; break; }
                    }

                    if (finalNodeId >= 0 && path.Count > 1)
                    {
                        var fullPath = new List<int> { a };
                        foreach (int pid in path)
                            if (pid != a && pid != finalNodeId) fullPath.Add(pid);
                        fullPath.Add(finalNodeId);
                        if ((allNodesById[a].pos - allNodesById[finalNodeId].pos).sqrMagnitude > 0.01f)
                            ApplyDouglasPeucker(0, fullPath.Count - 1, fullPath);
                    }
                }
            }

            // Step 3: Cluster — remove degree-2 feature nodes adjacent to higher-degree nodes
            foreach (var node in new List<PathNode>(featureNodes.Values))
            {
                if (!featureNodes.ContainsKey(node.id)) continue;
                foreach (int nb in node.neighbors)
                {
                    if (!featureNodes.TryGetValue(nb, out var nbNode)) continue;
                    if (node.neighbors.Count > nbNode.neighbors.Count && nbNode.neighbors.Count == 2)
                        { featureNodes.Remove(nb); continue; }
                    if (node.neighbors.Count < nbNode.neighbors.Count && node.neighbors.Count == 2)
                        { featureNodes.Remove(node.id); break; }
                }
            }

            Debug.Log($"[AxisGenerator1] After simplification: {featureNodes.Count} nodes");
            // Keep original IDs — DO NOT reassign. Chain walking in BuildNetworkData
            // relies on feature node IDs matching the original graph.
            return new List<PathNode>(featureNodes.Values);
        }

        // ============================================================
        // 7. BUILD NETWORK DATA + SAVE
        //    Matches ReferenceScripts/NetworkGenerator.cs GenerateRoadNetwork
        // ============================================================

        /// <param name="featureNodesList">Simplified feature nodes</param>
        /// <param name="originalGraph">Pre-simplification full graph for chain walking</param>
        private Network BuildNetworkData(List<PathNode> featureNodesList, List<PathNode> originalGraph)
        {
            // Build lookup from the ORIGINAL full graph
            var originalById = new Dictionary<int, PathNode>();
            foreach (var n in originalGraph) originalById[n.id] = n;
            var featureSet = new HashSet<int>();
            foreach (var n in featureNodesList) featureSet.Add(n.id);

            // Build dicts
            var featureById = new Dictionary<int, PathNode>();
            foreach (var fn in featureNodesList) featureById[fn.id] = fn;

            var net = new Network { NetworkId = "AxisNetwork" };
            float roadY = detectionHeight;
            var nodeIdToIndex = new Dictionary<int, int>(); // feature id -> index in net.Nodes

            foreach (var fn in featureNodesList)
            {
                Vector3 pos = fn.pos; pos.y = roadY;
                nodeIdToIndex[fn.id] = net.Nodes.Count;
                net.Nodes.Add(new Node { NodeId = $"N{fn.id}", Position = pos,
                    ConnectedEdges = new List<string>(), NeighborNodes = new List<string>() });
            }

            // Walk from each feature node through ORIGINAL graph along degree-2 chains
            var createdEdges = new HashSet<string>();

            foreach (var startNode in featureNodesList)
            {
                if (!originalById.TryGetValue(startNode.id, out var origStart)) continue;
                foreach (int rawNeighborId in origStart.neighbors)
                {
                    int curId = rawNeighborId, prevId = startNode.id;
                    int targetId = -1;
                    var chain = new List<Vector3> { origStart.pos };
                    while (true)
                    {
                        if (!originalById.TryGetValue(curId, out var curNode)) break;
                        chain.Add(curNode.pos);
                        if (featureSet.Contains(curId)) { targetId = curId; break; }
                        if (curNode.neighbors.Count != 2) break;
                        int nextId = curNode.neighbors[0] == prevId ? curNode.neighbors[1] : curNode.neighbors[0];
                        prevId = curId; curId = nextId;
                        if (curId == startNode.id) break;
                    }
                    if (targetId < 0 || targetId == startNode.id) continue;

                    string eKey = startNode.id < targetId ? $"{startNode.id}_{targetId}" : $"{targetId}_{startNode.id}";
                    if (!createdEdges.Add(eKey)) continue;

                    string fromNodeId = $"N{startNode.id}", toNodeId = $"N{targetId}";
                    List<Vector3> centerline = SimplifyClearCenterline(chain);

                    string eid = $"{startNode.id}_{targetId}";
                    var edge = new Edge { EdgeId = eid,
                        LinkNodesId = new List<string> { fromNodeId, toNodeId },
                        Centerline = centerline,
                        Length = PolylineLength(centerline),
                        Lanes = new List<Lane>() };
                    net.Edges.Add(edge);

                    int fi = nodeIdToIndex[startNode.id];
                    int ti = nodeIdToIndex[targetId];
                    net.Nodes[fi].ConnectedEdges.Add(eid);
                    net.Nodes[fi].NeighborNodes.Add(toNodeId);
                    net.Nodes[ti].ConnectedEdges.Add(eid);
                    net.Nodes[ti].NeighborNodes.Add(fromNodeId);
                }
            }

            return net;
        }

        private void SaveNetwork(Network net)
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                Converters = new List<JsonConverter> { new Vector3Converter() }
            };
            string json = JsonConvert.SerializeObject(net, settings);
            string fullPath = Path.Combine(Application.dataPath, "../", outputPath);
            File.WriteAllText(fullPath, json);
            Debug.Log($"[AxisGenerator1] Saved {net.Nodes.Count} nodes, {net.Edges.Count} edges to {outputPath}");
        }
    }
}
