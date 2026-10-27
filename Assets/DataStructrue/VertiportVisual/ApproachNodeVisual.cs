using RoadNetworkEditor;
using UnityEngine;

[ExecuteAlways]
public class ApproachNodeVisual : MonoBehaviour
{
    [HideInInspector] public string NodeId;
    [HideInInspector] public string ParentVertiportId;
    [HideInInspector] public ApproachNode NodeData;

    private Material normalMaterial;
    private Material selectedMaterial;
    private MeshRenderer meshRenderer;
    private bool isSelected = false;

    private Vector3 lastPosition;
    private VertiportManager manager;

    private Vector3 _prefabScale;
    private float _lastVertexSize;

    private void Awake()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        LoadMaterials();
    }

    private void LoadMaterials()
    {
        normalMaterial = Resources.Load<Material>("Materials/ApproachNodeNormal");
        selectedMaterial = Resources.Load<Material>("Materials/ApproachNodeSelected");
        if (normalMaterial != null && meshRenderer != null)
            meshRenderer.material = normalMaterial;
    }

    public void Initialize(ApproachNode node, string vertiportId, VertiportManager mgr)
    {
        NodeData = node;
        NodeId = node.NodeId;
        ParentVertiportId = vertiportId;
        manager = mgr;
        gameObject.name = $"ApprNode_{NodeId}";

        transform.position = node.Position;
        lastPosition = transform.position;

        _prefabScale = transform.localScale;
        _lastVertexSize = manager != null ? manager.vertexSize : 0.4f;

        if (meshRenderer == null)
        {
            meshRenderer = GetComponent<MeshRenderer>();
            LoadMaterials();
        }
    }

    public void UpdatePosition(Vector3 newPosition)
    {
        transform.position = newPosition;
        lastPosition = newPosition;

        if (NodeData != null)
            NodeData.Position = newPosition;

        UpdateConnectedEdges();
    }

    private void UpdateConnectedEdges()
    {
        if (NodeData == null || NodeData.ConnectedEdges == null) return;
        if (manager == null) manager = FindObjectOfType<VertiportManager>();

        foreach (string edgeId in NodeData.ConnectedEdges)
        {
            ApproachEdgeVisual ev = manager != null ? manager.GetApproachEdgeVisual(edgeId) : null;
            if (ev != null) ev.UpdateVisual();
        }
    }

    private void Update()
    {
        if (NodeData == null) return;

        if (transform.position != lastPosition)
        {
            NodeData.Position = transform.position;
            UpdateConnectedEdges();
            lastPosition = transform.position;
        }
        else if (transform.position != NodeData.Position)
        {
            transform.position = NodeData.Position;
            lastPosition = NodeData.Position;
            UpdateConnectedEdges();
        }

        if (manager == null) manager = FindObjectOfType<VertiportManager>();
        if (manager != null && !Mathf.Approximately(manager.vertexSize, _lastVertexSize))
        {
            _lastVertexSize = manager.vertexSize;
            transform.localScale = _prefabScale * _lastVertexSize;
        }
    }

    public void SetSelected(bool selected)
    {
        isSelected = selected;
        if (meshRenderer != null)
            meshRenderer.material = selected ? selectedMaterial : normalMaterial;
    }

    public bool IsSelected() => isSelected;

    private void OnDestroy()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && gameObject.scene.isLoaded && !string.IsNullOrEmpty(NodeId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && !manager.IsVisualDataDeletionSuppressed)
                manager.DeleteApproachNode(NodeId);
        }
#endif
    }

    private void OnDrawGizmos()
    {
#if UNITY_EDITOR
        if (!string.IsNullOrEmpty(NodeId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && manager.showTextAnnotations)
            {
                string label = NodeData != null && NodeData.IsAnchor ? $"{NodeId}\n(anchor)" : NodeId;
                UnityEditor.Handles.Label(transform.position + Vector3.up * 0.3f, label);
            }
        }
#endif
    }
}
