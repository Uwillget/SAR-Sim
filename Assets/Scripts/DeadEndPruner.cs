using System.Collections.Generic;
using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Removes dangling roads. A dead end is an edge with an endpoint of degree 1.
    /// Deleting it can expose the next edge, so the sweep repeats until every
    /// remaining node has degree 0 or at least 2.
    /// </summary>
    public static class DeadEndPruner
    {
        public static int Prune(Network net)
        {
            if (net == null || net.Edges == null) return 0;
            int removed = 0;
            bool changed = true;
            while (changed)
            {
                changed = false;
                var degree = new Dictionary<string, int>();
                foreach (Edge edge in net.Edges)
                {
                    if (!TryEnds(edge, out string a, out string b)) continue;
                    Add(degree, a);
                    Add(degree, b);
                }

                int before = net.Edges.Count;
                net.Edges.RemoveAll(edge =>
                {
                    if (!TryEnds(edge, out string a, out string b)) return true;
                    int da = degree.TryGetValue(a, out int va) ? va : 0;
                    int db = degree.TryGetValue(b, out int vb) ? vb : 0;
                    return da <= 1 || db <= 1;
                });
                int dropped = before - net.Edges.Count;
                if (dropped > 0)
                {
                    removed += dropped;
                    changed = true;
                }
            }

            var used = new HashSet<string>();
            foreach (Edge edge in net.Edges)
            {
                if (!TryEnds(edge, out string a, out string b)) continue;
                used.Add(a);
                used.Add(b);
            }
            if (net.Nodes != null)
                net.Nodes.RemoveAll(node => node == null || !used.Contains(node.NodeId));

            RebuildAdjacency(net);
            if (removed > 0)
                Debug.Log($"[DeadEndPruner] Removed {removed} dead-end edge(s). Remaining edges={net.Edges.Count}, nodes={net.Nodes.Count}.");
            return removed;
        }

        static void RebuildAdjacency(Network net)
        {
            if (net.Nodes == null) return;
            var byId = new Dictionary<string, Node>();
            foreach (Node node in net.Nodes)
            {
                if (node == null || string.IsNullOrEmpty(node.NodeId)) continue;
                node.ConnectedEdges = new List<string>();
                node.NeighborNodes = new List<string>();
                byId[node.NodeId] = node;
            }
            foreach (Edge edge in net.Edges)
            {
                if (!TryEnds(edge, out string a, out string b)) continue;
                if (byId.TryGetValue(a, out Node na))
                {
                    na.ConnectedEdges.Add(edge.EdgeId);
                    na.NeighborNodes.Add(b);
                }
                if (byId.TryGetValue(b, out Node nb))
                {
                    nb.ConnectedEdges.Add(edge.EdgeId);
                    nb.NeighborNodes.Add(a);
                }
            }
        }

        static void Add(Dictionary<string, int> degree, string id)
        {
            degree.TryGetValue(id, out int value);
            degree[id] = value + 1;
        }

        static bool TryEnds(Edge edge, out string a, out string b)
        {
            a = null;
            b = null;
            if (edge?.LinkNodesId == null || edge.LinkNodesId.Count < 2) return false;
            a = edge.LinkNodesId[0];
            b = edge.LinkNodesId[edge.LinkNodesId.Count - 1];
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b);
        }
    }
}
