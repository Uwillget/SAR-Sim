using RoadNetworkEditor;
using UnityEngine;

[ExecuteAlways]
public class WaypointVisual : MonoBehaviour
    {
        [HideInInspector]
        public EdgeVisual ParentEdge;

        [HideInInspector]
        public int WaypointIndex;

        private Material normalMaterial;
        private Material selectedMaterial;
        private MeshRenderer meshRenderer;
        private bool isSelected = false;

        private Vector3 lastPosition;
        private NetworkManager manager;

        // Monitor Visual Settings changes for live size updates.
        private Vector3 _prefabScale;
        private float _lastWaypointSize;

        // Flag to suppress OnDestroy callback during batch deletion.
        private bool suppressOnDestroy = false;

        private void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            LoadMaterials();
        }

        private void LoadMaterials()
        {
            normalMaterial = Resources.Load<Material>("Materials/WaypointNormal");
            selectedMaterial = Resources.Load<Material>("Materials/WaypointSelected");

            if (normalMaterial != null && meshRenderer != null)
            {
                meshRenderer.material = normalMaterial;
            }
        }

        public void Initialize(EdgeVisual edge, int index)
        {
            ParentEdge = edge;
            WaypointIndex = index;

            if (edge.EdgeData.Centerline != null && index < edge.EdgeData.Centerline.Count)
            {
                transform.position = edge.EdgeData.Centerline[index];
            }

            gameObject.name = $"Waypoint_{index}";
            lastPosition = transform.position;
            manager = FindObjectOfType<NetworkManager>();

            // Record prefab original scale and apply waypointSize.
            _prefabScale = transform.localScale;
            _lastWaypointSize = manager != null ? manager.waypointSize : 0.3f;
            transform.localScale = _prefabScale * _lastWaypointSize;
        }

        public void UpdatePosition(Vector3 newPosition)
        {
            transform.position = newPosition;
            lastPosition = newPosition;

            if (ParentEdge != null && ParentEdge.EdgeData.Centerline != null
                && WaypointIndex < ParentEdge.EdgeData.Centerline.Count)
            {
                ParentEdge.EdgeData.Centerline[WaypointIndex] = newPosition;
                ParentEdge.UpdateVisual();
            }
        }

        private void Update()
        {
            if (transform.position != lastPosition)
            {
                OnPositionChanged();
                lastPosition = transform.position;
            }

            // Detect waypointSize changes in Visual Settings and sync scale.
            if (manager == null)
                manager = FindObjectOfType<NetworkManager>();

            if (manager != null && !Mathf.Approximately(manager.waypointSize, _lastWaypointSize))
            {
                _lastWaypointSize = manager.waypointSize;
                transform.localScale = _prefabScale * _lastWaypointSize;
            }
        }

        /// <summary>
        /// Callback when the position is changed (from visual to data).
        /// </summary>
        private void OnPositionChanged()
        {
            // Perform full null checks.
            if (ParentEdge == null)
            {
                // Silently return to avoid console spam.
                return;
            }

            if (ParentEdge.EdgeData == null || ParentEdge.EdgeData.Centerline == null)
            {
                return;
            }

            // Validate index bounds.
            if (WaypointIndex < 0 || WaypointIndex >= ParentEdge.EdgeData.Centerline.Count)
            {
                return;
            }

            ParentEdge.EdgeData.Centerline[WaypointIndex] = transform.position;
            ParentEdge.UpdateVisual();

#if UNITY_EDITOR
            if (manager == null)
                manager = FindObjectOfType<NetworkManager>();

            if (manager != null)
            {
                UnityEditor.EditorUtility.SetDirty(manager);
            }
#endif
        }

        // Let the parent control whether OnDestroy callbacks fire.
        public void SetSuppressOnDestroy(bool suppress)
        {
            suppressOnDestroy = suppress;
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            // If suppression is set, skip callback.
            if (suppressOnDestroy)
            {
                return;
            }

            // Only handle in Edit mode (not needed at runtime).
            if (!Application.isPlaying && ParentEdge != null)
            {
                // Notify the parent edge to remove the corresponding data point.
                ParentEdge.OnWaypointDestroyed(this);
            }
#endif
        }

        public void SetSelected(bool selected)
        {
            isSelected = selected;
            if (meshRenderer != null)
            {
                meshRenderer.material = selected ? selectedMaterial : normalMaterial;
            }
        }

        public bool IsSelected()
        {
            return isSelected;
        }
    }
