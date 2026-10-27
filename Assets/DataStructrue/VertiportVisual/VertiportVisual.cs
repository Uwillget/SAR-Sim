using RoadNetworkEditor;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
[ExecuteAlways]
public class VertiportVisual : MonoBehaviour
{
    [HideInInspector] public string VertiportId;
    [HideInInspector] public Vertiport VertiportData;

    private LineRenderer lineRenderer;
    private Material normalMaterial;
    private Material selectedMaterial;
    private bool isSelected = false;

    private Vector3 lastPosition;
    private VertiportManager manager;

    // Track boundary changes from Inspector
    private int _lastBoundaryCount;

    private void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        if (lineRenderer == null)
            lineRenderer = gameObject.AddComponent<LineRenderer>();
        LoadMaterials();
        SetupLineRenderer();
    }

    private void LoadMaterials()
    {
        normalMaterial = Resources.Load<Material>("Materials/VertiportNormal");
        selectedMaterial = Resources.Load<Material>("Materials/VertiportSelected");
        if (normalMaterial != null && lineRenderer != null)
            lineRenderer.material = normalMaterial;
    }

    private void SetupLineRenderer()
    {
        if (lineRenderer != null)
        {
            manager = FindObjectOfType<VertiportManager>();
            float width = manager != null ? manager.vertiportWidth : 0.2f;
            lineRenderer.startWidth = width;
            lineRenderer.endWidth = width;
            lineRenderer.useWorldSpace = true;
            lineRenderer.loop = true;
            lineRenderer.numCapVertices = 5;
        }
    }

    public void Initialize(Vertiport vp, VertiportManager mgr)
    {
        VertiportData = vp;
        VertiportId = vp.VertiportId;
        gameObject.name = $"Vertiport_{VertiportId}";
        manager = mgr;
        lastPosition = transform.position;
        _lastBoundaryCount = vp.Boundary != null ? vp.Boundary.Count : 0;

        if (lineRenderer == null)
        {
            lineRenderer = GetComponent<LineRenderer>();
            if (lineRenderer == null)
                lineRenderer = gameObject.AddComponent<LineRenderer>();
            LoadMaterials();
            SetupLineRenderer();
        }

        UpdateVisual();
    }

    public void UpdateVisual()
    {
        if (lineRenderer == null || VertiportData == null || VertiportData.Boundary == null || VertiportData.Boundary.Count < 2)
        {
            if (lineRenderer != null) lineRenderer.positionCount = 0;
            return;
        }

        lineRenderer.positionCount = VertiportData.Boundary.Count;
        lineRenderer.SetPositions(VertiportData.Boundary.ToArray());
        _lastBoundaryCount = VertiportData.Boundary.Count;
    }

    private void Update()
    {
        if (VertiportData == null) return;

        // Detect boundary changes in Inspector
        if (VertiportData.Boundary != null && VertiportData.Boundary.Count != _lastBoundaryCount)
        {
            UpdateVisual();
        }

        // Poll manager visual settings
        if (manager == null) manager = FindObjectOfType<VertiportManager>();
        if (manager != null && lineRenderer != null)
        {
            float w = manager.vertiportWidth;
            if (!Mathf.Approximately(lineRenderer.startWidth, w))
            {
                lineRenderer.startWidth = w;
                lineRenderer.endWidth = w;
            }
        }
    }

    public void SetSelected(bool selected)
    {
        isSelected = selected;
        if (lineRenderer != null)
            lineRenderer.material = selected ? selectedMaterial : normalMaterial;
    }

    public bool IsSelected() => isSelected;

    private void OnDestroy()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && gameObject.scene.isLoaded && !string.IsNullOrEmpty(VertiportId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && !manager.IsVisualDataDeletionSuppressed)
                manager.DeleteVertiport(VertiportId, destroyGameObject: false);
        }
#endif
    }

    private void OnDrawGizmos()
    {
#if UNITY_EDITOR
        if (!string.IsNullOrEmpty(VertiportId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && manager.showTextAnnotations && VertiportData != null && VertiportData.Boundary.Count > 0)
            {
                Vector3 center = Vector3.zero;
                foreach (var p in VertiportData.Boundary) center += p;
                center /= VertiportData.Boundary.Count;
                UnityEditor.Handles.Label(center + Vector3.up * 1f, VertiportId);
            }
        }
#endif
    }
}
