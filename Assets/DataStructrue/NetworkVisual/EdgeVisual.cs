using System.Collections.Generic;
using System.Linq;
using RoadNetworkEditor;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
[ExecuteAlways]
public class EdgeVisual : MonoBehaviour
    {
        [HideInInspector]
        public string EdgeId;

        [HideInInspector]
        public Edge EdgeData;

        private LineRenderer lineRenderer;
        private Material normalMaterial;
        private Material selectedMaterial;
        private bool isSelected = false;

        // Waypoint visual objects.
        private List<WaypointVisual> waypointVisuals = new List<WaypointVisual>();
        private GameObject waypointPrefab;

        // Detect Centerline data changes from the Inspector.
        private List<Vector3> _lastCenterline;

        // Monitor Visual Settings changes for live width updates.
        private NetworkManager _networkManager;
        private float _lastEdgeWidth;

        private void Awake()
        {
            _networkManager = FindObjectOfType<NetworkManager>();

            if (lineRenderer == null)
            {
                lineRenderer = GetComponent<LineRenderer>();
                if (lineRenderer == null)
                {
                    lineRenderer = gameObject.AddComponent<LineRenderer>();
                }
            }
            LoadMaterials();
            SetupLineRenderer();
            LoadWaypointPrefab();
        }

        private void LoadMaterials()
        {
            normalMaterial = Resources.Load<Material>("Materials/EdgeNormal");
            selectedMaterial = Resources.Load<Material>("Materials/EdgeSelected");
        }

        private void LoadWaypointPrefab()
        {
            waypointPrefab = Resources.Load<GameObject>("Prefabs/WaypointPrefab");
        }

        private void SetupLineRenderer()
        {
            if (lineRenderer != null)
            {
                float width = _networkManager != null ? _networkManager.edgeWidth : 0.2f;
                lineRenderer.startWidth = width;
                lineRenderer.endWidth = width;
                lineRenderer.material = normalMaterial;
                lineRenderer.useWorldSpace = true;
                lineRenderer.numCapVertices = 5;
                _lastEdgeWidth = width;
            }
        }

        public void Initialize(Edge edge, NodeVisual startNode, NodeVisual endNode)
        {
            EdgeData = edge;
            EdgeId = edge.EdgeId;
            gameObject.name = $"Edge_{EdgeId}";

            if (_networkManager == null)
                _networkManager = FindObjectOfType<NetworkManager>();

            if (lineRenderer == null)
            {
                lineRenderer = GetComponent<LineRenderer>();
                if (lineRenderer == null)
                {
                    lineRenderer = gameObject.AddComponent<LineRenderer>();
                }
                LoadMaterials();
                SetupLineRenderer();
                LoadWaypointPrefab();
            }

            if (edge.Centerline == null || edge.Centerline.Count == 0)
            {
                edge.Centerline = new List<Vector3> { startNode.transform.position, endNode.transform.position };
            }

            UpdateVisual();

            // Always create waypoint objects.
            CreateWaypoints();
        }

        public void UpdateVisual()
        {
            if (lineRenderer == null)
            {
                Debug.LogError($"LineRenderer is null on {gameObject.name}");
                return;
            }

            if (EdgeData == null || EdgeData.Centerline == null || EdgeData.Centerline.Count < 2)
                return;

            lineRenderer.positionCount = EdgeData.Centerline.Count;
            lineRenderer.SetPositions(EdgeData.Centerline.ToArray());

            // Compute length.
            CalculateLength();
        }

        private void CalculateLength()
        {
            float length = 0f;
            for (int i = 0; i < EdgeData.Centerline.Count - 1; i++)
            {
                length += Vector3.Distance(EdgeData.Centerline[i], EdgeData.Centerline[i + 1]);
            }
            EdgeData.Length = length;
        }

        private void Update()
        {
            if (EdgeData == null || EdgeData.Centerline == null || EdgeData.Centerline.Count < 2)
                return;

            if (HasCenterlineChanged())
            {
                UpdateVisual();
                SyncWaypointPositions();
            }

            // Detect edgeWidth changes in Visual Settings.
            if (_networkManager == null)
                _networkManager = FindObjectOfType<NetworkManager>();

            if (_networkManager != null && !Mathf.Approximately(_networkManager.edgeWidth, _lastEdgeWidth))
            {
                _lastEdgeWidth = _networkManager.edgeWidth;
                if (lineRenderer != null)
                {
                    lineRenderer.startWidth = _lastEdgeWidth;
                    lineRenderer.endWidth = _lastEdgeWidth;
                }
            }
        }

        /// <summary>
        /// Check whether the Centerline data has changed in the Inspector.
        /// </summary>
        private bool HasCenterlineChanged()
        {
            if (_lastCenterline == null || _lastCenterline.Count != EdgeData.Centerline.Count)
            {
                _lastCenterline = new List<Vector3>(EdgeData.Centerline);
                return true;
            }

            for (int i = 0; i < EdgeData.Centerline.Count; i++)
            {
                if (_lastCenterline[i] != EdgeData.Centerline[i])
                {
                    _lastCenterline = new List<Vector3>(EdgeData.Centerline);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Sync waypoint visual positions without triggering UpdatePosition to avoid cycles.
        /// </summary>
        private void SyncWaypointPositions()
        {
            foreach (var wp in waypointVisuals)
            {
                if (wp != null && wp.WaypointIndex < EdgeData.Centerline.Count)
                {
                    wp.transform.position = EdgeData.Centerline[wp.WaypointIndex];
                }
            }
        }

        public void SetSelected(bool selected)
        {
            isSelected = selected;
            if (lineRenderer != null)
            {
                lineRenderer.material = selected ? selectedMaterial : normalMaterial;
            }

            // Toggle waypoint material to reflect selection state.
            foreach (var waypoint in waypointVisuals)
            {
                if (waypoint != null)
                {
                    waypoint.SetSelected(selected);
                }
            }
        }

        public bool IsSelected()
        {
            return isSelected;
        }

        /// <summary>
        /// Create waypoint visual objects.
        /// </summary>
        private void CreateWaypoints()
        {
            // Clear existing waypoints first to avoid duplicates.
            DestroyWaypoints();

            if (waypointPrefab == null)
            {
                Debug.LogWarning("WaypointPrefab not loaded. Skipping waypoint creation.");
                return;
            }

            if (EdgeData.Centerline == null || EdgeData.Centerline.Count <= 2)
            {
                return; // No intermediate points — no waypoints needed.
            }

            // Only create visual objects for intermediate waypoints (exclude start and end).
            for (int i = 1; i < EdgeData.Centerline.Count - 1; i++)
            {
                GameObject waypointObj = Instantiate(waypointPrefab, EdgeData.Centerline[i], Quaternion.identity, transform);
                WaypointVisual waypoint = waypointObj.GetComponent<WaypointVisual>();

                if (waypoint == null)
                {
                    waypoint = waypointObj.AddComponent<WaypointVisual>();
                }

                waypoint.Initialize(this, i);
                waypointVisuals.Add(waypoint);
            }

            Debug.Log($"Created {waypointVisuals.Count} waypoints for edge {EdgeId}");
        }

        /// <summary>
        /// Destroy all waypoint visual objects.
        /// </summary>
        private void DestroyWaypoints()
        {
            // Mark all waypoints first to suppress OnDestroy callbacks.
            foreach (var waypoint in waypointVisuals)
            {
                if (waypoint != null)
                {
                    waypoint.SetSuppressOnDestroy(true);
                }
            }

            // Destroy in reverse order.
            for (int i = waypointVisuals.Count - 1; i >= 0; i--)
            {
                if (waypointVisuals[i] != null)
                {
                    DestroyImmediate(waypointVisuals[i].gameObject);
                }
            }

            waypointVisuals.Clear();
        }

        /// <summary>
        /// Add a waypoint at the specified position.
        /// </summary>
        public void AddWaypoint(Vector3 position, int insertIndex = -1)
        {
            if (EdgeData.Centerline == null || EdgeData.Centerline.Count < 2)
            {
                Debug.LogError("Centerline must have at least 2 points (start and end nodes)");
                return;
            }

            // If no index specified, insert at the second-to-last position (before end point).
            if (insertIndex < 0)
            {
                insertIndex = EdgeData.Centerline.Count - 1;
            }

            // Clamp index: must not be 0 (start point) or exceed the end point.
            insertIndex = Mathf.Clamp(insertIndex, 1, EdgeData.Centerline.Count);

            EdgeData.Centerline.Insert(insertIndex, position);

            Debug.Log($"Added waypoint at index {insertIndex}. Total points: {EdgeData.Centerline.Count}");

            UpdateVisual();

            // Rebuild waypoints regardless of selection (waypoints are always visible).
            DestroyWaypoints();
            CreateWaypoints();
        }

        /// <summary>
        /// Remove a waypoint.
        /// </summary>
        public void RemoveWaypoint(int index)
        {
            if (EdgeData.Centerline == null || index <= 0 || index >= EdgeData.Centerline.Count - 1)
            {
                Debug.LogWarning($"Cannot remove waypoint at index {index}");
                return;
            }

            // Remove from data.
            EdgeData.Centerline.RemoveAt(index);

            // Update visualisation.
            UpdateVisual();

            // Rebuild all waypoints.
            DestroyWaypoints();
            CreateWaypoints();

            Debug.Log($"Removed waypoint at index {index}. Centerline now has {EdgeData.Centerline.Count} points.");
        }

        /// <summary>
        /// Update a waypoint's position.
        /// </summary>
        public void UpdateWaypoint(int index, Vector3 newPosition)
        {
            if (EdgeData.Centerline != null && index >= 0 && index < EdgeData.Centerline.Count)
            {
                EdgeData.Centerline[index] = newPosition;
                UpdateVisual();
            }
        }

        /// <summary>
        /// Get all waypoint visual objects.
        /// </summary>
        public List<WaypointVisual> GetWaypoints()
        {
            return waypointVisuals;
        }

        private void OnDestroy()
        {
            DestroyWaypoints();
        }

        /// <summary>
        /// Called when a Waypoint object is externally destroyed (e.g., deleted in the Hierarchy).
        /// </summary>
        public void OnWaypointDestroyed(WaypointVisual waypoint)
        {
            if (waypoint == null || EdgeData.Centerline == null)
                return;

            int index = waypoint.WaypointIndex;

            // Validate index (start and end points cannot be removed).
            if (index <= 0 || index >= EdgeData.Centerline.Count - 1)
            {
                Debug.LogWarning($"Cannot remove waypoint at index {index} (start or end point)");
                return;
            }

            // Remove from data.
            EdgeData.Centerline.RemoveAt(index);

            // Remove from visual list.
            waypointVisuals.Remove(waypoint);

            // Update remaining waypoint indices.
            for (int i = 0; i < waypointVisuals.Count; i++)
            {
                if (waypointVisuals[i] != null && waypointVisuals[i].WaypointIndex > index)
                {
                    waypointVisuals[i].WaypointIndex--;
                    waypointVisuals[i].gameObject.name = $"Waypoint_{waypointVisuals[i].WaypointIndex}";
                }
            }

            // Update visualisation.
            UpdateVisual();

#if UNITY_EDITOR
            NetworkManager manager = FindObjectOfType<NetworkManager>();
            if (manager != null)
            {
                UnityEditor.EditorUtility.SetDirty(manager);
            }
#endif

            Debug.Log($"Waypoint at index {index} was destroyed. Centerline now has {EdgeData.Centerline.Count} points.");
        }

        private void OnDrawGizmos()
        {
#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(EdgeId) && EdgeData != null && EdgeData.Centerline != null && EdgeData.Centerline.Count > 0)
            {
                if (_networkManager == null)
                    _networkManager = FindObjectOfType<NetworkManager>();

                if (_networkManager != null && !_networkManager.showTextAnnotations)
                    return;

                int waypointNum = EdgeData.Centerline.Count;
                Vector3 midPoint;

                if (waypointNum % 2 == 1)
                {
                    int midIndex = waypointNum / 2;
                    midPoint = EdgeData.Centerline[midIndex];
                }
                else
                {
                    int midIndex1 = waypointNum / 2 - 1;
                    int midIndex2 = waypointNum / 2;
                    midPoint = (EdgeData.Centerline[midIndex1] + EdgeData.Centerline[midIndex2]) / 2f;
                }

                UnityEditor.Handles.Label(midPoint + Vector3.up * 0.5f, $"{EdgeId}\nLength: {EdgeData.Length:F2}");
            }
#endif
        }
    }
