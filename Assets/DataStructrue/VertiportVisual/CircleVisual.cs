using RoadNetworkEditor;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
[ExecuteAlways]
public class CircleVisual : MonoBehaviour
{
    [HideInInspector] public string CircleId;
    [HideInInspector] public string ParentHoldingStructureId;
    [HideInInspector] public Circle CircleData;

    private LineRenderer lineRenderer;
    private Material normalMaterial;
    private Material selectedMaterial;
    private bool isSelected = false;

    private Vector3 lastCenter;
    private float lastRadius;
    private int lastSegments;
    private VertiportManager manager;

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
        normalMaterial = Resources.Load<Material>("Materials/CircleNormal");
        selectedMaterial = Resources.Load<Material>("Materials/CircleSelected");
        if (normalMaterial != null && lineRenderer != null)
            lineRenderer.material = normalMaterial;
    }

    private void SetupLineRenderer()
    {
        if (lineRenderer != null)
        {
            manager = FindObjectOfType<VertiportManager>();
            float width = manager != null ? manager.circleWidth : 0.15f;
            lineRenderer.startWidth = width;
            lineRenderer.endWidth = width;
            lineRenderer.useWorldSpace = true;
            lineRenderer.loop = true;
            lineRenderer.numCapVertices = 5;
        }
    }

    public void Initialize(Circle circle, string hsId, VertiportManager mgr)
    {
        CircleData = circle;
        CircleId = circle.CircleId;
        ParentHoldingStructureId = hsId;
        manager = mgr;
        gameObject.name = $"Circle_{CircleId}";

        lastCenter = circle.Center;
        lastRadius = circle.Radius;
        lastSegments = manager != null ? manager.circleSegments : 64;

        transform.position = circle.Center;

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
            if (lineRenderer == null || CircleData == null) return;

            // The circle is a construction guide for positioning vertices, not a route.
            // Keep it hidden by default so it cannot be confused with an arc ramp.
            bool showGuide = manager != null && manager.showCircleGuides;
            lineRenderer.enabled = showGuide;
            if (!showGuide)
            {
                lineRenderer.positionCount = 0;
                lastCenter = CircleData.Center;
                lastRadius = CircleData.Radius;
                return;
            }

            int segments = manager != null ? manager.circleSegments : 64;
        float radius = CircleData.Radius;
        Vector3 center = CircleData.Center;

        lineRenderer.positionCount = segments + 1;
        for (int i = 0; i <= segments; i++)
        {
            float angle = (360f / segments) * i * Mathf.Deg2Rad;
            Vector3 pos = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            lineRenderer.SetPosition(i, pos);
        }

        lastCenter = center;
        lastRadius = radius;
        lastSegments = segments;
    }

    private void Update()
    {
        if (CircleData == null) return;

        // Detect Inspector changes to center/radius
        bool changed = false;
        if (CircleData.Center != lastCenter)
        {
            transform.position = CircleData.Center;
            changed = true;
        }
        if (!Mathf.Approximately(CircleData.Radius, lastRadius))
            changed = true;

        if (manager == null) manager = FindObjectOfType<VertiportManager>();
        int segs = manager != null ? manager.circleSegments : 64;
        if (segs != lastSegments) changed = true;

        if (changed) UpdateVisual();

        // Poll manager visual settings
        if (manager != null && lineRenderer != null)
        {
            float w = manager.circleWidth;
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
        if (!Application.isPlaying && gameObject.scene.isLoaded && !string.IsNullOrEmpty(CircleId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && !manager.IsVisualDataDeletionSuppressed)
                manager.DeleteCircle(CircleId);
        }
#endif
    }

    private void OnDrawGizmos()
    {
#if UNITY_EDITOR
        if (!string.IsNullOrEmpty(CircleId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && manager.showTextAnnotations && CircleData != null)
            {
                UnityEditor.Handles.Label(CircleData.Center + Vector3.up * 0.5f, CircleId);
            }
        }
#endif
    }
}
