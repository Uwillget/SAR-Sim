using Newtonsoft.Json;
using UnityEngine;

namespace RoadNetworkEditor
{
    /// <summary>
    /// Abstract base class for axis generation algorithms.
    /// Derive from this to implement a new axis generation method,
    /// then assign the component to NetworkManager's Axis Generator slot.
    /// </summary>
    public abstract class AxisGeneratorBase : MonoBehaviour
    {
        [Header("Detection Bounds")]
        [Tooltip("Collider defining the generation area. Auto-uses BoxCollider on this GameObject if assigned.")]
        public BoxCollider detectionArea;
        [Tooltip("Optional polygon. When set, cells outside it are not part of the road domain.")]
        public GenerationBoundary boundary;

        protected bool OutsideBoundary(float x, float z)
        {
            return boundary != null && boundary.HasPolygon && !boundary.Contains(x, z);
        }
        [Tooltip("Y-coordinate for obstacle detection and for the generated axis altitude.")]
        public float detectionHeight = 50f;

        protected void StampRoadAltitude(Network net)
        {
            if (net == null) return;
            float y = detectionHeight;
            if (net.Nodes != null)
            {
                foreach (Node node in net.Nodes)
                {
                    if (node == null) continue;
                    Vector3 position = node.Position;
                    position.y = y;
                    node.Position = position;
                }
            }
            if (net.Edges != null)
            {
                foreach (Edge edge in net.Edges)
                {
                    if (edge?.Centerline == null) continue;
                    for (int i = 0; i < edge.Centerline.Count; i++)
                    {
                        Vector3 point = edge.Centerline[i];
                        point.y = y;
                        edge.Centerline[i] = point;
                    }
                }
            }
            if (net.AltitudeLayers == null) net.AltitudeLayers = new System.Collections.Generic.List<AltitudeLayer>();
            if (net.AltitudeLayers.Count == 0)
            {
                net.AltitudeLayers.Add(new AltitudeLayer
                {
                    LayerId = "L0",
                    DisplayName = "Default Layer",
                    NominalAltitude = y,
                    VerticalTolerance = 5f,
                    Enabled = true,
                    Frozen = false
                });
            }
            else
            {
                foreach (AltitudeLayer layer in net.AltitudeLayers)
                    if (layer != null) layer.NominalAltitude = y;
            }
        }

        [Header("Lane Parameters")]
        [Tooltip("Standard lane width for collision margin.")]
        public float laneWidth = 5f;
        [Range(1, 10)] public int minLaneCount = 2;
        [Range(1, 10)] public int maxLaneCount = 8;

        [Header("Obstacle Layer")]
        public LayerMask obstacleLayerMask = 1 << 6; // Buildings layer by default

        [Header("Output")]
        [Tooltip("Where to save the generated axis network JSON.")]
        public string outputPath = "Assets/DataBase/AxisNetwork.json";

        /// <summary>Run the axis generation algorithm. Called by NetworkManager.</summary>
        public abstract void GenerateAxisNetwork();
    }
}
