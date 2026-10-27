using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Axis Generator Method 2: Quadtree partitioning + orthogonal corridor extraction.
    ///
    /// Algorithm (ported from QuadTreeNetwork.py):
    ///   1. Build quadtree over the detection area, classifying cells by obstacle overlap.
    ///   2. Prune small free cells and apply scanline concavity pruning.
    ///   3. Extract long horizontal / vertical corridor segments from kept free cells.
    ///   4. Select non-redundant parallel segments, optionally add outer frame.
    ///   5. Build graph by intersecting H and V segments, then simplify (dead-end
    ///      pruning + collinear node merging).
    ///
    /// Attach to the same GameObject as NetworkManager or use standalone.
    /// </summary>
    public class AxisGenerator2 : AxisGeneratorBase
    {
        // ============================================================
        // PUBLIC PARAMETERS
        // ============================================================

        [Header("Quadtree Parameters")]
        [Tooltip("Maximum subdivision depth.")]
        public int maxDepth = 6;
        [Tooltip("Minimum cell edge length. Stops subdivision when reached.")]
        public float minCellSize = 2f;
        [Tooltip("Free cells smaller than this are pruned.")]
        public float pruneCellSize = 4f;

        [Header("Scanline Concavity Pruning")]
        [Tooltip("Fill concave pockets inside occupied-region boundaries.")]
        public bool enableScanlinePruning = true;
        [Tooltip("Max scanline pruning iterations.")]
        public int scanlineMaxIter = 5;

        [Header("Corridor Extraction")]
        [Tooltip("Minimum corridor length to keep (shorter candidates discarded).")]
        public float minCorridorLength = 18f;
        [Tooltip("Minimum spacing between parallel corridors (closer ones merged).")]
        public float minParallelSpacing = 12f;

        [Header("Dead-End Pruning")]
        [Tooltip("Dead-end branches shorter than this are removed.")]
        public float minDeadEndLength = 18f;

        [Header("Outer Frame")]
        [Tooltip("Add boundary ring around the domain.")]
        public bool enableOuterFrame = true;
        [Tooltip("Offset of the frame from the domain boundary.")]
        public float outerFrameOffset = 0f;

        // ============================================================
        // CONSTANTS & INTERNAL STATE
        // ============================================================

        private const float SNAP_EPS = 1e-7f;
        private const float EPS = 1e-9f;

        // BoxCollider bounds (set in GenerateAxisNetwork).
        private float bboxXMin, bboxXMax, bboxZMin, bboxZMax;

        // ============================================================
        // INNER TYPES
        // ============================================================

        private class Cell
        {
            public float xMin, zMin, size;
            public int depth;
            public enum Status { Free, Occupied, Mixed }
            public Status status;

            public float xMax => xMin + size;
            public float zMax => zMin + size;
            public float cx => xMin + 0.5f * size;
            public float cz => zMin + 0.5f * size;

            public Cell(float x, float z, float s, int d)
            { xMin = x; zMin = z; size = s; depth = d; status = Status.Mixed; }

            public string Key => $"{F9(xMin)},{F9(zMin)},{F9(size)}";
        }

        private struct Seg
        {
            public char orient; // 'H' or 'V'
            public float constant; // y for H, x for V
            public float a, b;  // interval endpoints
            public float score;
            public float Length => Mathf.Abs(b - a);
            public (float, float) p1 => orient == 'H' ? (a, constant) : (constant, a);
            public (float, float) p2 => orient == 'H' ? (b, constant) : (constant, b);

            public Seg(char o, float c, float a0, float b0, float s = 0)
            { orient = o; constant = c; a = Mathf.Min(a0, b0); b = Mathf.Max(a0, b0); score = s; }
        }

        // ============================================================
        // PUBLIC API
        // ============================================================

        public override void GenerateAxisNetwork()
        {
            Debug.Log("[AxisGenerator2] Starting quadtree axis network generation...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (detectionArea == null)
                detectionArea = GetComponent<BoxCollider>();
            if (detectionArea == null)
            {
                Debug.LogError("[AxisGenerator2] No BoxCollider assigned.");
                return;
            }

            // Store actual BoxCollider bounds (used for clipping & outer frame).
            Vector3 c = detectionArea.center;
            Vector3 s = detectionArea.size;
            bboxXMin = c.x - s.x * 0.5f;
            bboxXMax = c.x + s.x * 0.5f;
            bboxZMin = c.z - s.z * 0.5f;
            bboxZMax = c.z + s.z * 0.5f;

            // Quadtree needs a square root cell that covers the whole bbox.
            float span = Mathf.Max(bboxXMax - bboxXMin, bboxZMax - bboxZMin);
            float xMid = (bboxXMin + bboxXMax) * 0.5f;
            float zMid = (bboxZMin + bboxZMax) * 0.5f;
            float qXMin = xMid - span * 0.5f;
            float qZMin = zMid - span * 0.5f;

            Debug.Log($"[AxisGenerator2] BBox: [{bboxXMin:F0},{bboxXMax:F0}] × [{bboxZMin:F0},{bboxZMax:F0}], quadtree span={span:F0}");

            // 1. Build quadtree.
            var leaves = new List<Cell>();
            var root = new Cell(qXMin, qZMin, span, 0);
            BuildQuadtree(root, leaves);
            Debug.Log($"[AxisGenerator2] Quadtree leaves: {leaves.Count}");

            // 2. Pruning.
            var prunedKeys = BuildFinalPrunedSet(leaves);
            var keptCells = leaves.Where(c => !prunedKeys.Contains(c.Key)).ToList();
            Debug.Log($"[AxisGenerator2] Kept cells: {keptCells.Count}, pruned: {prunedKeys.Count}");

            // --- DIAGNOSTIC: analyze north zone (Z > 0, X < 200) ---
            DiagnoseZone(leaves, keptCells, prunedKeys, bboxZMin, bboxXMax, "North-West (Z>0, X<200)",
                (c) => c.cz > 0f && c.cx < 200f);
            DiagnoseZone(leaves, keptCells, prunedKeys, bboxZMin, bboxXMax, "North (Z>500)",
                (c) => c.cz > 500f);

            // 3. Extract corridor segments, then clip to BoxCollider bounds.
            var segments = ExtractMainCorridorSegments(keptCells, bboxXMin, bboxXMax, bboxZMin, bboxZMax);
            segments = ClipSegmentsToBounds(segments, bboxXMin, bboxXMax, bboxZMin, bboxZMax);
            Debug.Log($"[AxisGenerator2] Corridor segments: {segments.Count}");
            // --- DIAGNOSTIC: corridor coverage in north ---
            var hNorth = segments.Where(s => s.orient == 'H' && s.constant > 500f).ToList();
            var vNorth = segments.Where(s => s.orient == 'V' && s.a < 200f && s.b > 500f).ToList();
            Debug.Log($"[AxisGenerator2] North (Z>500) corridors: H={hNorth.Count}, V crossing north={vNorth.Count}");

            // 4. Build graph from segments.
            var (nodes, edges) = BuildGraphFromSegments(segments);
            Debug.Log($"[AxisGenerator2] Raw graph: {nodes.Count} nodes, {edges.Count} edges");

            // 5. Simplify: prune short dead ends, merge collinear degree-2 nodes.
            (nodes, edges) = SimplifyGraph(nodes, edges);
            Debug.Log($"[AxisGenerator2] Simplified graph: {nodes.Count} nodes, {edges.Count} edges");

            // 6. Build Network data and save.
            float roadY = detectionHeight;
            var network = BuildNetworkData(nodes, edges, roadY);
            DeadEndPruner.Prune(network);
            StampRoadAltitude(network);
            SaveNetwork(network);

            sw.Stop();
            Debug.Log($"[AxisGenerator2] Done in {sw.ElapsedMilliseconds}ms. Output: {outputPath}");
        }

        // ============================================================
        // 1. QUADTREE
        // ============================================================

        private void BuildQuadtree(Cell cell, List<Cell> leaves)
        {
            ClassifyCell(cell);

            if (cell.status == Cell.Status.Free || cell.status == Cell.Status.Occupied)
            {
                leaves.Add(cell);
                return;
            }

            // Mixed cell — subdivide if possible.
            if (cell.depth >= maxDepth || cell.size <= minCellSize)
            {
                // Conservative: treat as occupied.
                cell.status = Cell.Status.Occupied;
                leaves.Add(cell);
                return;
            }

            float half = cell.size * 0.5f;
            int d = cell.depth + 1;
            BuildQuadtree(new Cell(cell.xMin, cell.zMin, half, d), leaves);
            BuildQuadtree(new Cell(cell.xMin + half, cell.zMin, half, d), leaves);
            BuildQuadtree(new Cell(cell.xMin, cell.zMin + half, half, d), leaves);
            BuildQuadtree(new Cell(cell.xMin + half, cell.zMin + half, half, d), leaves);
        }

        private void ClassifyCell(Cell cell)
        {
            if (boundary != null && boundary.HasPolygon)
            {
                PolygonRelation relation = boundary.RelationToCell(cell.xMin, cell.zMin, cell.size);
                if (relation == PolygonRelation.Outside)
                {
                    cell.status = Cell.Status.Occupied;
                    return;
                }
                if (relation == PolygonRelation.Partial &&
                    (cell.depth < maxDepth && cell.size > minCellSize))
                {
                    cell.status = Cell.Status.Mixed;
                    return;
                }
                if (relation == PolygonRelation.Partial)
                {
                    cell.status = Cell.Status.Occupied;
                    return;
                }
            }

            const float halfY = 10f;
            Vector3 pos = new Vector3(cell.cx, detectionHeight, cell.cz);
            Vector3 halfExt = new Vector3(cell.size * 0.5f, halfY, cell.size * 0.5f);

            if (!Physics.CheckBox(pos, halfExt, Quaternion.identity, obstacleLayerMask))
            {
                cell.status = Cell.Status.Free;
                return;
            }

            // Check corners — use a small fixed-size box to sample just the corner
            // point, NOT a fraction of the cell (which becomes huge for large cells
            // and causes false Occupied classification).
            // halfExt * 0.5f would be 250×250 for a depth-1 cell — way too aggressive.
            float cornerXZ = minCellSize * 0.5f;
            Vector3 cornerHalfExt = new Vector3(cornerXZ, halfY, cornerXZ);
            bool allBlocked = true;
            foreach (var (cx, cz) in new (float, float)[] {
                (cell.xMin, cell.zMin), (cell.xMax, cell.zMin),
                (cell.xMax, cell.zMax), (cell.xMin, cell.zMax)
            })
            {
                if (!Physics.CheckBox(new Vector3(cx, detectionHeight, cz), cornerHalfExt, Quaternion.identity, obstacleLayerMask))
                {
                    allBlocked = false;
                    break;
                }
            }

            cell.status = allBlocked ? Cell.Status.Occupied : Cell.Status.Mixed;
        }

        // ============================================================
        // 2. PRUNING
        // ============================================================

        private HashSet<string> BuildFinalPrunedSet(List<Cell> leaves)
        {
            // Basic: prune small free cells.
            var pruned = new HashSet<string>();
            foreach (var c in leaves)
            {
                if (c.status != Cell.Status.Free || c.size < pruneCellSize - EPS)
                    pruned.Add(c.Key);
            }

            if (enableScanlinePruning)
                pruned = ApplyScanlinePruning(leaves, pruned);

            return pruned;
        }

        private HashSet<string> ApplyScanlinePruning(List<Cell> leaves, HashSet<string> initialPruned)
        {
            var current = new HashSet<string>(initialPruned);
            for (int iter = 0; iter < scanlineMaxIter; iter++)
            {
                var comps = FindPrunedComponents(leaves, current);
                var candidates = leaves.Where(c => !current.Contains(c.Key)).ToList();
                var added = new HashSet<string>();
                foreach (var comp in comps)
                {
                    if (comp.Count < 2) continue;
                    var fromScan = ScanlinePruneComponent(comp, candidates);
                    foreach (var k in fromScan)
                        if (!current.Contains(k))
                            added.Add(k);
                }
                if (added.Count == 0) break;
                foreach (var k in added) current.Add(k);
            }
            return current;
        }

        private List<List<Cell>> FindPrunedComponents(List<Cell> leaves, HashSet<string> prunedKeys)
        {
            var prunedCells = leaves.Where(c => prunedKeys.Contains(c.Key)).ToList();
            int n = prunedCells.Count;
            if (n == 0) return new List<List<Cell>>();

            // Build adjacency via edge bins.
            var edgeBins = new Dictionary<string, List<int>>();
            for (int i = 0; i < n; i++)
            {
                var c = prunedCells[i];
                AddToBin(edgeBins, $"x{F9(c.xMin)}", i);
                AddToBin(edgeBins, $"x{F9(c.xMax)}", i);
                AddToBin(edgeBins, $"z{F9(c.zMin)}", i);
                AddToBin(edgeBins, $"z{F9(c.zMax)}", i);
            }

            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();

            foreach (var kv in edgeBins)
            {
                var indices = kv.Value;
                for (int ii = 0; ii < indices.Count; ii++)
                    for (int jj = ii + 1; jj < indices.Count; jj++)
                    {
                        int a = indices[ii], b = indices[jj];
                        if (AreEdgeAdjacent(prunedCells[a], prunedCells[b]))
                        { adj[a].Add(b); adj[b].Add(a); }
                    }
            }

            var visited = new bool[n];
            var comps = new List<List<Cell>>();
            for (int i = 0; i < n; i++)
            {
                if (visited[i]) continue;
                var comp = new List<Cell>();
                var q = new Queue<int>();
                q.Enqueue(i); visited[i] = true;
                while (q.Count > 0)
                {
                    int cur = q.Dequeue();
                    comp.Add(prunedCells[cur]);
                    foreach (int nb in adj[cur])
                        if (!visited[nb]) { visited[nb] = true; q.Enqueue(nb); }
                }
                comps.Add(comp);
            }
            return comps;
        }

        private void AddToBin(Dictionary<string, List<int>> bins, string key, int index)
        {
            if (!bins.TryGetValue(key, out var list))
            { list = new List<int>(); bins[key] = list; }
            list.Add(index);
        }

        private bool AreEdgeAdjacent(Cell a, Cell b)
        {
            bool vertTouch = Mathf.Abs(a.xMax - b.xMin) <= EPS || Mathf.Abs(b.xMax - a.xMin) <= EPS;
            bool zOverlap = Mathf.Min(a.zMax, b.zMax) - Mathf.Max(a.zMin, b.zMin) > EPS;
            bool horizTouch = Mathf.Abs(a.zMax - b.zMin) <= EPS || Mathf.Abs(b.zMax - a.zMin) <= EPS;
            bool xOverlap = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin) > EPS;
            return (vertTouch && zOverlap) || (horizTouch && xOverlap);
        }

        private HashSet<string> ScanlinePruneComponent(List<Cell> comp, List<Cell> candidates)
        {
            var added = new HashSet<string>();
            var boundaryEdges = GetUnionBoundaryEdges(comp);
            if (boundaryEdges.Count == 0) return added;

            float compMinX = comp.Min(c => c.xMin), compMaxX = comp.Max(c => c.xMax);
            float compMinZ = comp.Min(c => c.zMin), compMaxZ = comp.Max(c => c.zMax);

            var localCandidates = candidates.Where(c =>
                c.cx >= compMinX - EPS && c.cx <= compMaxX + EPS &&
                c.cz >= compMinZ - EPS && c.cz <= compMaxZ + EPS).ToList();

            // Horizontal scan.
            var scanZs = UniqueSorted(localCandidates.Select(c => c.cz).ToList());
            foreach (float z in scanZs)
            {
                var xs = HorizontalIntersections(boundaryEdges, z);
                if (xs.Count <= 2) continue;
                float xL = xs[0], xR = xs[xs.Count - 1];
                foreach (var c in localCandidates)
                    if (Mathf.Abs(c.cz - z) <= EPS && c.cx > xL + EPS && c.cx < xR - EPS)
                        added.Add(c.Key);
            }

            // Vertical scan.
            var scanXs = UniqueSorted(localCandidates.Select(c => c.cx).ToList());
            foreach (float x in scanXs)
            {
                var zs = VerticalIntersections(boundaryEdges, x);
                if (zs.Count <= 2) continue;
                float zB = zs[0], zT = zs[zs.Count - 1];
                foreach (var c in localCandidates)
                    if (Mathf.Abs(c.cx - x) <= EPS && c.cz > zB + EPS && c.cz < zT - EPS)
                        added.Add(c.Key);
            }

            return added;
        }

        private List<(float, float, float, float)> GetUnionBoundaryEdges(List<Cell> cells)
        {
            var counts = new Dictionary<string, int>();
            foreach (var c in cells)
            {
                var corners = new (float, float)[] {
                    (c.xMin, c.zMin), (c.xMax, c.zMin),
                    (c.xMax, c.zMax), (c.xMin, c.zMax)
                };
                for (int i = 0; i < 4; i++)
                {
                    var p1 = corners[i];
                    var p2 = corners[(i + 1) % 4];
                    var a = (F9(p1.Item1), F9(p1.Item2));
                    var b = (F9(p2.Item1), F9(p2.Item2));
                    string key = string.Compare(a.ToString(), b.ToString()) < 0
                        ? $"{a}_{b}" : $"{b}_{a}";
                    if (!counts.ContainsKey(key)) counts[key] = 0;
                    counts[key]++;
                }
            }
            var edges = new List<(float, float, float, float)>();
            foreach (var kv in counts)
            {
                if (kv.Value != 1) continue;
                var parts = kv.Key.Split('_');
                var p1 = ParseTuple(parts[0]);
                var p2 = ParseTuple(parts[1]);
                edges.Add((p1.Item1, p1.Item2, p2.Item1, p2.Item2));
            }
            return edges;
        }

        private (float, float) ParseTuple(string s)
        {
            s = s.Trim('(', ')');
            var parts = s.Split(new[] { ", " }, System.StringSplitOptions.None);
            return (float.Parse(parts[0]), float.Parse(parts[1]));
        }

        private List<float> HorizontalIntersections(List<(float x1, float z1, float x2, float z2)> edges, float z)
        {
            var xs = new List<float>();
            foreach (var e in edges)
            {
                float x1 = e.x1, z1 = e.z1, x2 = e.x2, z2 = e.z2;
                if (Mathf.Abs(x1 - x2) > EPS) continue; // not vertical
                float zMin = Mathf.Min(z1, z2), zMax = Mathf.Max(z1, z2);
                if (zMin - EPS <= z && z < zMax - EPS)
                    xs.Add(x1);
            }
            return UniqueSorted(xs);
        }

        private List<float> VerticalIntersections(List<(float x1, float z1, float x2, float z2)> edges, float x)
        {
            var zs = new List<float>();
            foreach (var e in edges)
            {
                float x1 = e.x1, z1 = e.z1, x2 = e.x2, z2 = e.z2;
                if (Mathf.Abs(z1 - z2) > EPS) continue; // not horizontal
                float xMin = Mathf.Min(x1, x2), xMax = Mathf.Max(x1, x2);
                if (xMin - EPS <= x && x < xMax - EPS)
                    zs.Add(z1);
            }
            return UniqueSorted(zs);
        }

        // ============================================================
        // 3. CORRIDOR EXTRACTION
        // ============================================================

        private List<Seg> ExtractMainCorridorSegments(List<Cell> keptCells,
            float domainXMin, float domainXMax, float domainZMin, float domainZMax)
        {
            var hSegs = ExtractHorizontalCorridors(keptCells);
            var vSegs = ExtractVerticalCorridors(keptCells);
            var all = new List<Seg>(hSegs);
            all.AddRange(vSegs);

            if (enableOuterFrame && (boundary == null || !boundary.HasPolygon))
                AddOuterFrame(all, domainXMin, domainXMax, domainZMin, domainZMax);

            all = SelectParallelSegments(all);
            return all;
        }

        private List<Seg> ExtractHorizontalCorridors(List<Cell> keptCells)
        {
            var result = new List<Seg>();
            var zValues = UniqueSorted(keptCells.Select(c => c.cz).ToList());

            foreach (float z in zValues)
            {
                var intervals = new List<(float, float)>();
                foreach (var c in keptCells)
                    if (c.zMin + EPS < z && z < c.zMax - EPS)
                        intervals.Add((c.xMin, c.xMax));

                foreach (var (x1, x2) in MergeIntervals(intervals))
                {
                    float len = x2 - x1;
                    if (len >= minCorridorLength - EPS)
                        result.Add(new Seg('H', z, x1, x2, len));
                }
            }
            return result;
        }

        private List<Seg> ExtractVerticalCorridors(List<Cell> keptCells)
        {
            var result = new List<Seg>();
            var xValues = UniqueSorted(keptCells.Select(c => c.cx).ToList());

            foreach (float x in xValues)
            {
                var intervals = new List<(float, float)>();
                foreach (var c in keptCells)
                    if (c.xMin + EPS < x && x < c.xMax - EPS)
                        intervals.Add((c.zMin, c.zMax));

                foreach (var (z1, z2) in MergeIntervals(intervals))
                {
                    float len = z2 - z1;
                    if (len >= minCorridorLength - EPS)
                        result.Add(new Seg('V', x, z1, z2, len));
                }
            }
            return result;
        }

        private void AddOuterFrame(List<Seg> segs,
            float xMin, float xMax, float zMin, float zMax)
        {
            float x0 = xMin + outerFrameOffset;
            float x1 = xMax - outerFrameOffset;
            float z0 = zMin + outerFrameOffset;
            float z1 = zMax - outerFrameOffset;
            segs.Add(new Seg('H', z0, x0, x1, 10000f));
            segs.Add(new Seg('H', z1, x0, x1, 10000f));
            segs.Add(new Seg('V', x0, z0, z1, 10000f));
            segs.Add(new Seg('V', x1, z0, z1, 10000f));
        }

        private List<Seg> SelectParallelSegments(List<Seg> segments)
        {
            var ordered = segments.OrderByDescending(s => s.score).ThenByDescending(s => s.Length).ToList();
            var selected = new List<Seg>();
            foreach (var seg in ordered)
            {
                bool keep = true;
                foreach (var old in selected)
                {
                    if (old.orient != seg.orient) continue;
                    if (Mathf.Abs(old.constant - seg.constant) >= minParallelSpacing - EPS) continue;
                    if (!IntervalsOverlap(old.a, old.b, seg.a, seg.b)) continue;
                    keep = false; break;
                }
                if (keep) selected.Add(seg);
            }
            return selected;
        }

        // ============================================================
        // 3b. CLIP SEGMENTS TO BOXCOLLIDER BOUNDS
        // ============================================================

        private List<Seg> ClipSegmentsToBounds(List<Seg> segments,
            float xMin, float xMax, float zMin, float zMax)
        {
            var clipped = new List<Seg>();
            foreach (var seg in segments)
            {
                if (seg.orient == 'H')
                {
                    float a = Mathf.Max(seg.a, xMin);
                    float b = Mathf.Min(seg.b, xMax);
                    if (b - a > EPS)
                        clipped.Add(new Seg('H', seg.constant, a, b, b - a));
                }
                else // 'V'
                {
                    float a = Mathf.Max(seg.a, zMin);
                    float b = Mathf.Min(seg.b, zMax);
                    if (b - a > EPS)
                        clipped.Add(new Seg('V', seg.constant, a, b, b - a));
                }
            }
            Debug.Log($"[AxisGenerator2] Segments after clipping to bbox: {clipped.Count} (was {segments.Count})");
            return clipped;
        }

        // ============================================================
        // 4. GRAPH CONSTRUCTION
        // ============================================================

        private (HashSet<(float, float)> nodes, HashSet<((float, float), (float, float))> edges)
            BuildGraphFromSegments(List<Seg> segments)
        {
            var hSegs = segments.Where(s => s.orient == 'H').ToList();
            var vSegs = segments.Where(s => s.orient == 'V').ToList();

            var splitPts = new Dictionary<string, List<(float, float)>>();

            for (int i = 0; i < hSegs.Count; i++)
            {
                var key = $"H_{i}";
                splitPts[key] = new List<(float, float)> { hSegs[i].p1, hSegs[i].p2 };
            }
            for (int i = 0; i < vSegs.Count; i++)
            {
                var key = $"V_{i}";
                splitPts[key] = new List<(float, float)> { vSegs[i].p1, vSegs[i].p2 };
            }

            // Compute intersections.
            for (int i = 0; i < hSegs.Count; i++)
            {
                var h = hSegs[i];
                for (int j = 0; j < vSegs.Count; j++)
                {
                    var v = vSegs[j];
                    var pt = SegmentIntersection(h, v);
                    if (pt.HasValue)
                    {
                        splitPts[$"H_{i}"].Add(pt.Value);
                        splitPts[$"V_{j}"].Add(pt.Value);
                    }
                }
            }

            var nodes = new HashSet<(float, float)>();
            var edges = new HashSet<((float, float), (float, float))>();

            void AddEdge((float, float) p, (float, float) q)
            {
                p = SnapPt(p); q = SnapPt(q);
                if (p.Equals(q)) return;
                var e = CompareKey(p, q) < 0 ? (p, q) : (q, p);
                edges.Add(e);
                nodes.Add(p); nodes.Add(q);
            }

            // Horizontal segments → split by X.
            for (int i = 0; i < hSegs.Count; i++)
            {
                var pts = UniqueSortedPoints(splitPts[$"H_{i}"], sortAxis: 'x');
                for (int k = 1; k < pts.Count; k++)
                {
                    var p = pts[k - 1]; var q = pts[k];
                    if (Mathf.Abs(p.Item2 - q.Item2) <= SNAP_EPS && Mathf.Abs(q.Item1 - p.Item1) > SNAP_EPS)
                        AddEdge(p, q);
                }
            }

            // Vertical segments → split by Z.
            for (int i = 0; i < vSegs.Count; i++)
            {
                var pts = UniqueSortedPoints(splitPts[$"V_{i}"], sortAxis: 'z');
                for (int k = 1; k < pts.Count; k++)
                {
                    var p = pts[k - 1]; var q = pts[k];
                    if (Mathf.Abs(p.Item1 - q.Item1) <= SNAP_EPS && Mathf.Abs(q.Item2 - p.Item2) > SNAP_EPS)
                        AddEdge(p, q);
                }
            }

            return (nodes, edges);
        }

        private (float, float)? SegmentIntersection(Seg h, Seg v)
        {
            float x = v.constant, z = h.constant;
            if (h.a - EPS <= x && x <= h.b + EPS && v.a - EPS <= z && z <= v.b + EPS)
                return SnapPt((x, z));
            return null;
        }

        // ============================================================
        // 5. GRAPH SIMPLIFICATION
        // ============================================================

        private (HashSet<(float, float)>, HashSet<((float, float), (float, float))>)
            SimplifyGraph(HashSet<(float, float)> nodes, HashSet<((float, float), (float, float))> edges)
        {
            (nodes, edges) = PruneShortDeadEnds(nodes, edges);
            (nodes, edges) = MergeCollinearDegree2Nodes(nodes, edges);
            (nodes, edges) = PruneShortDeadEnds(nodes, edges);
            return (nodes, edges);
        }

        private (HashSet<(float, float)>, HashSet<((float, float), (float, float))>)
            PruneShortDeadEnds(HashSet<(float, float)> nodes, HashSet<((float, float), (float, float))> edges)
        {
            var nSet = new HashSet<(float, float)>(nodes);
            var eSet = new HashSet<((float, float), (float, float))>(edges);

            bool changed = true;
            while (changed)
            {
                changed = false;
                var adj = BuildAdjacency(nSet, eSet);
                var removeEdges = new HashSet<((float, float), (float, float))>();
                var removeNodes = new HashSet<(float, float)>();

                foreach (var n in nSet.ToList())
                {
                    if (!adj.TryGetValue(n, out var nbrs) || nbrs.Count != 1) continue;
                    var m = nbrs.First();
                    var e = CompareKey(n, m) < 0 ? (n, m) : (m, n);
                    float len = Mathf.Sqrt((e.Item2.Item1 - e.Item1.Item1) * (e.Item2.Item1 - e.Item1.Item1) +
                                           (e.Item2.Item2 - e.Item1.Item2) * (e.Item2.Item2 - e.Item1.Item2));
                    // 0 or negative means "prune all dead ends regardless of length".
                    if (minDeadEndLength <= EPS || len < minDeadEndLength - EPS)
                    {
                        removeEdges.Add(e);
                        removeNodes.Add(n);
                    }
                }
                if (removeEdges.Count > 0)
                {
                    foreach (var e in removeEdges) eSet.Remove(e);
                    foreach (var n in removeNodes)
                        if (!eSet.Any(e => e.Item1.Equals(n) || e.Item2.Equals(n)))
                            nSet.Remove(n);
                    changed = true;
                }
            }
            return (nSet, eSet);
        }

        private (HashSet<(float, float)>, HashSet<((float, float), (float, float))>)
            MergeCollinearDegree2Nodes(HashSet<(float, float)> nodes, HashSet<((float, float), (float, float))> edges)
        {
            var nSet = new HashSet<(float, float)>(nodes);
            var eSet = new HashSet<((float, float), (float, float))>(edges);

            bool changed = true;
            while (changed)
            {
                changed = false;
                var adj = BuildAdjacency(nSet, eSet);
                foreach (var n in nSet.ToList())
                {
                    if (!adj.TryGetValue(n, out var nbrs) || nbrs.Count != 2) continue;
                    var list = nbrs.ToList();
                    var a = list[0]; var b = list[1];
                    if (!AreCollinearNeighbors(n, a, b)) continue;

                    var e1 = CompareKey(n, a) < 0 ? (n, a) : (a, n);
                    var e2 = CompareKey(n, b) < 0 ? (n, b) : (b, n);
                    var eNew = CompareKey(a, b) < 0 ? (a, b) : (b, a);
                    eSet.Remove(e1); eSet.Remove(e2); eSet.Add(eNew);
                    nSet.Remove(n);
                    changed = true; break;
                }
            }
            return (nSet, eSet);
        }

        private bool AreCollinearNeighbors((float x, float z) n, (float x, float z) a, (float x, float z) b)
        {
            return (Mathf.Abs(a.x - n.x) <= SNAP_EPS && Mathf.Abs(b.x - n.x) <= SNAP_EPS) ||
                   (Mathf.Abs(a.z - n.z) <= SNAP_EPS && Mathf.Abs(b.z - n.z) <= SNAP_EPS);
        }

        private Dictionary<(float, float), HashSet<(float, float)>> BuildAdjacency(
            HashSet<(float, float)> nodes, HashSet<((float, float), (float, float))> edges)
        {
            var adj = new Dictionary<(float, float), HashSet<(float, float)>>();
            foreach (var n in nodes) adj[n] = new HashSet<(float, float)>();
            foreach (var e in edges)
            {
                if (!adj.ContainsKey(e.Item1)) adj[e.Item1] = new HashSet<(float, float)>();
                if (!adj.ContainsKey(e.Item2)) adj[e.Item2] = new HashSet<(float, float)>();
                adj[e.Item1].Add(e.Item2);
                adj[e.Item2].Add(e.Item1);
            }
            return adj;
        }

        // ============================================================
        // 6. BUILD NETWORK DATA + SAVE
        // ============================================================

        private Network BuildNetworkData(HashSet<(float, float)> nodes,
            HashSet<((float, float), (float, float))> edges, float roadY)
        {
            var net = new Network { NetworkId = "AxisNetwork" };
            var nodeList = nodes.ToList();
            var nodeIndex = new Dictionary<(float, float), int>();

            for (int i = 0; i < nodeList.Count; i++)
            {
                var (x, z) = nodeList[i];
                nodeIndex[nodeList[i]] = i;
                net.Nodes.Add(new Node
                {
                    NodeId = $"N{i}",
                    Position = new Vector3(x, roadY, z),
                    ConnectedEdges = new List<string>(),
                    NeighborNodes = new List<string>()
                });
            }

            int eIdx = 0;
            foreach (var e in edges)
            {
                int a = nodeIndex[e.Item1];
                int b = nodeIndex[e.Item2];
                string eId = $"{a}_{b}";
                var fromPos = net.Nodes[a].Position;
                var toPos = net.Nodes[b].Position;

                net.Edges.Add(new Edge
                {
                    EdgeId = eId,
                    LinkNodesId = new List<string> { $"N{a}", $"N{b}" },
                    Centerline = new List<Vector3> { fromPos, toPos },
                    Length = Vector3.Distance(fromPos, toPos),
                    Lanes = new List<Lane>()
                });

                net.Nodes[a].ConnectedEdges.Add(eId);
                net.Nodes[a].NeighborNodes.Add($"N{b}");
                net.Nodes[b].ConnectedEdges.Add(eId);
                net.Nodes[b].NeighborNodes.Add($"N{a}");
                eIdx++;
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
            Debug.Log($"[AxisGenerator2] Saved {net.Nodes.Count} nodes, {net.Edges.Count} edges to {outputPath}");
        }

        // ============================================================
        // UTILITY HELPERS
        // ============================================================

        private static float F9(float v) => Mathf.Round(v * 1e9f) / 1e9f;

        private static (float, float) SnapPt((float, float) p) => (F9(p.Item1), F9(p.Item2));

        private static int CompareKey((float, float) a, (float, float) b)
        {
            int c = a.Item1.CompareTo(b.Item1);
            return c != 0 ? c : a.Item2.CompareTo(b.Item2);
        }

        private List<float> UniqueSorted(List<float> values)
        {
            values.Sort();
            var outList = new List<float>();
            foreach (float v in values)
                if (outList.Count == 0 || Mathf.Abs(v - outList[outList.Count - 1]) > EPS)
                    outList.Add(v);
            return outList;
        }

        private List<(float, float)> UniqueSortedPoints(List<(float, float)> points, char sortAxis)
        {
            points = points.Select(p => SnapPt(p)).ToList();
            if (sortAxis == 'x')
                points.Sort((a, b) => { int c = a.Item1.CompareTo(b.Item1); return c != 0 ? c : a.Item2.CompareTo(b.Item2); });
            else // 'z'
                points.Sort((a, b) => { int c = a.Item2.CompareTo(b.Item2); return c != 0 ? c : a.Item1.CompareTo(b.Item1); });

            var outList = new List<(float, float)>();
            foreach (var p in points)
            {
                if (outList.Count == 0) { outList.Add(p); continue; }
                var prev = outList[outList.Count - 1];
                if (Mathf.Abs(p.Item1 - prev.Item1) > SNAP_EPS || Mathf.Abs(p.Item2 - prev.Item2) > SNAP_EPS)
                    outList.Add(p);
            }
            return outList;
        }

        private List<(float, float)> MergeIntervals(List<(float, float)> intervals)
        {
            if (intervals.Count == 0) return new List<(float, float)>();
            intervals = intervals.Select(iv => (Mathf.Min(iv.Item1, iv.Item2), Mathf.Max(iv.Item1, iv.Item2)))
                                 .OrderBy(iv => iv.Item1).ToList();
            var merged = new List<(float, float)> { intervals[0] };
            foreach (var iv in intervals.Skip(1))
            {
                var last = merged[merged.Count - 1];
                if (iv.Item1 <= last.Item2 + EPS)
                    merged[merged.Count - 1] = (last.Item1, Mathf.Max(last.Item2, iv.Item2));
                else
                    merged.Add(iv);
            }
            return merged;
        }

        private bool IntervalsOverlap(float a1, float a2, float b1, float b2)
        {
            return Mathf.Min(a2, b2) - Mathf.Max(a1, b1) > EPS;
        }

        private void GetDomainBounds(out float xMin, out float xMax, out float zMin, out float zMax)
        {
            xMin = bboxXMin; xMax = bboxXMax;
            zMin = bboxZMin; zMax = bboxZMax;
        }

        // ============================================================
        // DIAGNOSTIC
        // ============================================================

        private void DiagnoseZone(List<Cell> leaves, List<Cell> keptCells,
            HashSet<string> prunedKeys, float zMin, float xMax, string label,
            System.Func<Cell, bool> filter)
        {
            var zoneLeaves = leaves.Where(filter).ToList();
            var zoneKept = keptCells.Where(filter).ToList();
            var zonePruned = zoneLeaves.Where(c => prunedKeys.Contains(c.Key)).ToList();
            var zoneFree = zoneLeaves.Where(c => c.status == Cell.Status.Free).ToList();
            var zoneOccupied = zoneLeaves.Where(c => c.status == Cell.Status.Occupied).ToList();

            if (zoneLeaves.Count == 0)
            {
                Debug.Log($"[AxisGenerator2] DIAG {label}: NO cells at all in this zone!");
                return;
            }

            Debug.Log($"[AxisGenerator2] DIAG {label}: total={zoneLeaves.Count} free={zoneFree.Count}" +
                $" occupied={zoneOccupied.Count} kept={zoneKept.Count} pruned={zonePruned.Count}");

            // Size distribution of kept free cells
            if (zoneKept.Count > 0)
            {
                float minSz = zoneKept.Min(c => c.size);
                float maxSz = zoneKept.Max(c => c.size);
                float avgSz = zoneKept.Average(c => c.size);
                Debug.Log($"[AxisGenerator2] DIAG {label}: kept cell sizes: min={minSz:F1} max={maxSz:F1} avg={avgSz:F1}");
            }

            // Size distribution of occupied cells
            if (zoneOccupied.Count > 0)
            {
                float minSz = zoneOccupied.Min(c => c.size);
                float maxSz = zoneOccupied.Max(c => c.size);
                Debug.Log($"[AxisGenerator2] DIAG {label}: occupied cell sizes: min={minSz:F1} max={maxSz:F1}");
            }

            // Bounding box of kept cells
            if (zoneKept.Count > 0)
            {
                float kxMin = zoneKept.Min(c => c.xMin);
                float kxMax = zoneKept.Max(c => c.xMax);
                float kzMin = zoneKept.Min(c => c.zMin);
                float kzMax = zoneKept.Max(c => c.zMax);
                Debug.Log($"[AxisGenerator2] DIAG {label}: kept bbox X=[{kxMin:F0},{kxMax:F0}] Z=[{kzMin:F0},{kzMax:F0}]");
            }
        }
    }
}
