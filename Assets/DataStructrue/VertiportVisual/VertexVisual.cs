using RoadNetworkEditor;
using UnityEngine;

[ExecuteAlways]
public class VertexVisual : MonoBehaviour
{
    [HideInInspector] public string VertexId;
    [HideInInspector] public string LinkCircleId;
    [HideInInspector] public Vertex VertexData;

    private Material normalMaterial;
    private Material selectedMaterial;
    private MeshRenderer meshRenderer;
    private bool isSelected = false;

    private Vector3 lastPosition;
    private VertiportManager manager;

    // Track visual settings
    private Vector3 _prefabScale;
    private float _lastVertexSize;

    private void Awake()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        LoadMaterials();
    }

    private void LoadMaterials()
    {
        normalMaterial = Resources.Load<Material>("Materials/VertexNormal");
        selectedMaterial = Resources.Load<Material>("Materials/VertexSelected");
        if (normalMaterial != null && meshRenderer != null)
            meshRenderer.material = normalMaterial;
    }

    public void Initialize(Vertex v, string circleId, VertiportManager mgr)
    {
        VertexData = v;
        VertexId = v.VertexId;
        LinkCircleId = circleId;
        manager = mgr;
        gameObject.name = $"Vertex_{VertexId}";

        transform.position = v.Position;
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
        // Constrain to circle arc if LinkCircleId is set
        if (!string.IsNullOrEmpty(LinkCircleId) && manager != null)
        {
            CircleVisual cv = manager.GetCircleVisual(LinkCircleId);
            if (cv != null && cv.CircleData != null)
            {
                Vector3 center = cv.CircleData.Center;
                float radius = cv.CircleData.Radius;
                Vector3 dir = newPosition - center;
                dir.y = 0f;
                if (dir.magnitude > 0.001f)
                    newPosition = center + dir.normalized * radius;
                else
                    newPosition = center + Vector3.forward * radius;
            }
        }

        transform.position = newPosition;
        lastPosition = newPosition;

        if (VertexData != null)
            VertexData.Position = newPosition;

        // Update connected ramps
        UpdateConnectedRamps();
    }

    private void UpdateConnectedRamps()
    {
        if (VertexData == null || VertexData.ConnectedRamps == null) return;
        if (manager == null) manager = FindObjectOfType<VertiportManager>();

        foreach (string rampId in VertexData.ConnectedRamps)
        {
            RampVisual rv = manager != null ? manager.GetRampVisual(rampId) : null;
            if (rv != null) rv.UpdateVisual();
        }
    }

    private void Update()
    {
        if (VertexData == null) return;

        // Detect position changes (user drag in Scene view)
        if (transform.position != lastPosition)
        {
            // Constrain to circle arc
            if (!string.IsNullOrEmpty(LinkCircleId))
            {
                if (manager == null) manager = FindObjectOfType<VertiportManager>();
                CircleVisual cv = manager != null ? manager.GetCircleVisual(LinkCircleId) : null;
                if (cv != null && cv.CircleData != null)
                {
                    Vector3 center = cv.CircleData.Center;
                    float radius = cv.CircleData.Radius;
                    Vector3 dir = transform.position - center;
                    dir.y = 0f;
                    if (dir.magnitude > 0.001f)
                        transform.position = center + dir.normalized * radius;
                }
            }

            VertexData.Position = transform.position;
            UpdateConnectedRamps();
            lastPosition = transform.position;
        }
        // Detect data changes from Inspector
        else if (transform.position != VertexData.Position)
        {
            transform.position = VertexData.Position;
            lastPosition = VertexData.Position;
            UpdateConnectedRamps();
        }

        // Poll visual settings
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
        if (!Application.isPlaying && gameObject.scene.isLoaded && !string.IsNullOrEmpty(VertexId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && !manager.IsVisualDataDeletionSuppressed)
                manager.DeleteVertex(VertexId, destroyGameObject: false);
        }
#endif
    }

    private void OnDrawGizmos()
    {
#if UNITY_EDITOR
        if (!string.IsNullOrEmpty(VertexId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && manager.showTextAnnotations)
            {
                UnityEditor.Handles.Label(transform.position + Vector3.up * 0.3f, VertexId);
            }
        }
#endif
    }
}
