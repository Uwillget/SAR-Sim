using System.Linq;
using RoadNetworkEditor;
using UnityEngine;

/// <summary>
/// Visual representation of a node, attached to the node GameObject in the scene.
/// </summary>
[ExecuteAlways]
public class NodeVisual : MonoBehaviour
    {
        [HideInInspector]
        public string NodeId;

        [HideInInspector]
        public Node NodeData;

        private Material normalMaterial;
        private Material selectedMaterial;
        private MeshRenderer meshRenderer;
        private bool isSelected = false;

        // Track position changes for live sync.
        private Vector3 lastPosition;
        private NetworkManager manager;

        // Monitor Visual Settings changes for live size updates.
        private Vector3 _prefabScale;
        private float _lastNodeSize;

        private void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            LoadMaterials();
        }

        private void LoadMaterials()
        {
            normalMaterial = Resources.Load<Material>("Materials/NodeNormal");
            selectedMaterial = Resources.Load<Material>("Materials/NodeSelected");

            if (normalMaterial != null && meshRenderer != null)
            {
                meshRenderer.material = normalMaterial;
            }
        }

        public void Initialize(Node node)
        {
            NodeData = node;
            NodeId = node.NodeId;
            transform.position = node.Position;
            gameObject.name = $"Node_{NodeId}";

            // Initialise position tracking.
            lastPosition = transform.position;

            // Locate the NetworkManager.
            manager = FindObjectOfType<NetworkManager>();

            // Record prefab original scale and apply nodeSize.
            _prefabScale = transform.localScale;
            _lastNodeSize = manager != null ? manager.nodeSize : 0.5f;
            transform.localScale = _prefabScale * _lastNodeSize;
        }

        public void UpdatePosition(Vector3 newPosition)
        {
            transform.position = newPosition;
            lastPosition = newPosition; // Update tracked position.

            if (NodeData != null)
            {
                NodeData.Position = newPosition;
            }
        }

        /// <summary>
        /// Update visualisation for all connected edges.
        /// </summary>
        public void UpdateConnectedEdges()
        {
            if (NodeData == null || NodeData.ConnectedEdges == null)
                return;

            // Locate the NetworkManager.
            if (manager == null)
                manager = FindObjectOfType<NetworkManager>();

            if (manager == null)
                return;

            // Update all connected edges.
            foreach (string edgeId in NodeData.ConnectedEdges)
            {
                EdgeVisual edgeVisual = manager.GetEdgeVisual(edgeId);
                if (edgeVisual != null)
                {
                    // Update start or end position of the edge.
                    Edge edgeData = edgeVisual.EdgeData;
                    if (edgeData.LinkNodesId.First() == NodeId)
                    {
                        edgeData.Centerline[0] = transform.position;
                    }
                    else if (edgeData.LinkNodesId.Last() == NodeId)
                    {
                        edgeData.Centerline[edgeData.Centerline.Count - 1] = transform.position;
                    }

                    edgeVisual.UpdateVisual();
                }
            }
        }

        private void Update()
        {
            if (NodeData == null)
                return;

            // User dragged the node in the Scene view → sync to data.
            if (transform.position != lastPosition)
            {
                NodeData.Position = transform.position;
                UpdateConnectedEdges();
                lastPosition = transform.position;
            }
            // Inspector data was modified → sync to Transform.
            else if (transform.position != NodeData.Position)
            {
                transform.position = NodeData.Position;
                lastPosition = NodeData.Position;
                UpdateConnectedEdges();
            }

            // Detect nodeSize changes in Visual Settings and sync scale.
            if (manager != null && !Mathf.Approximately(manager.nodeSize, _lastNodeSize))
            {
                _lastNodeSize = manager.nodeSize;
                transform.localScale = _prefabScale * _lastNodeSize;
            }
        }

        /// <summary>
        /// Callback when the position is changed (from visual to data).
        /// </summary>
        private void OnPositionChanged()
        {
            if (NodeData == null)
                return;

            // Update backend data.
            NodeData.Position = transform.position;

            // Update connected edges.
            UpdateConnectedEdges();

#if UNITY_EDITOR
            // Mark scene as dirty to ensure saving.
            if (manager != null)
            {
                UnityEditor.EditorUtility.SetDirty(manager);
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

        private void OnDestroy()
        {
#if UNITY_EDITOR
            // Only handle in Edit mode (not needed at runtime).
            if (!Application.isPlaying && !string.IsNullOrEmpty(NodeId))
            {
                // Locate the NetworkManager.
                if (manager == null)
                    manager = FindObjectOfType<NetworkManager>();

                if (manager != null)
                {
                    // Pass false because the GameObject is already being destroyed.
                    manager.DeleteNode(NodeId, destroyGameObject: false);
                }
            }
#endif
        }

        private void OnDrawGizmos()
        {
#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(NodeId))
            {
                if (manager == null)
                    manager = FindObjectOfType<NetworkManager>();

                if (manager != null && manager.showTextAnnotations)
                {
                    UnityEditor.Handles.Label(transform.position + Vector3.up * 0.5f, NodeId);
                }
            }
#endif
        }
    }
