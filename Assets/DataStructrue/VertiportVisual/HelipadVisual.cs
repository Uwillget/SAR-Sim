using System.Collections.Generic;
using System.Linq;
using RoadNetworkEditor;
using UnityEngine;

[ExecuteAlways]
public class HelipadVisual : MonoBehaviour
{
    [HideInInspector] public string HelipadId;
    [HideInInspector] public string ParentVertiportId;
    [HideInInspector] public Helipad HelipadData;

    private LineRenderer boundaryRenderer;
    private Material normalMaterial;
    private Material selectedMaterial;
    private bool isSelected = false;

    private VertiportManager manager;
    private int _lastBoundaryCount;

    private void Awake()
    {
        CreateLineRenderer();
        LoadMaterials();
        SetupRenderer();
    }

    private void CreateLineRenderer()
    {
        boundaryRenderer = GetComponent<LineRenderer>();
        if (boundaryRenderer == null)
            boundaryRenderer = gameObject.AddComponent<LineRenderer>();
    }

    private void LoadMaterials()
    {
        normalMaterial = Resources.Load<Material>("Materials/HelipadNormal");
        selectedMaterial = Resources.Load<Material>("Materials/HelipadSelected");
        if (normalMaterial != null && boundaryRenderer != null)
            boundaryRenderer.material = normalMaterial;
    }

    private void SetupRenderer()
    {
        manager = FindObjectOfType<VertiportManager>();
        float width = manager != null ? manager.helipadWidth : 0.15f;

        if (boundaryRenderer != null)
        {
            boundaryRenderer.startWidth = width;
            boundaryRenderer.endWidth = width;
            boundaryRenderer.useWorldSpace = true;
            boundaryRenderer.loop = true;
            boundaryRenderer.numCapVertices = 5;
        }
    }

    public void Initialize(Helipad hp, string vertiportId, VertiportManager mgr)
    {
        HelipadData = hp;
        HelipadId = hp.HelipadId;
        ParentVertiportId = vertiportId;
        manager = mgr;
        gameObject.name = $"Helipad_{HelipadId}";

        _lastBoundaryCount = hp.Boundary != null ? hp.Boundary.Count : 0;

        if (boundaryRenderer == null)
        {
            CreateLineRenderer();
            LoadMaterials();
            SetupRenderer();
        }

        UpdateVisual();
    }

    public void UpdateVisual()
    {
        if (HelipadData == null) return;

        if (boundaryRenderer != null && HelipadData.Boundary != null && HelipadData.Boundary.Count >= 2)
        {
            boundaryRenderer.positionCount = HelipadData.Boundary.Count;
            boundaryRenderer.SetPositions(HelipadData.Boundary.ToArray());
            _lastBoundaryCount = HelipadData.Boundary.Count;
        }
    }

    private void Update()
    {
        if (HelipadData == null) return;

        if (HelipadData.Boundary != null && HelipadData.Boundary.Count != _lastBoundaryCount)
            UpdateVisual();

        if (manager == null) manager = FindObjectOfType<VertiportManager>();
        if (manager != null && boundaryRenderer != null)
        {
            float w = manager.helipadWidth;
            if (!Mathf.Approximately(boundaryRenderer.startWidth, w))
            {
                boundaryRenderer.startWidth = w;
                boundaryRenderer.endWidth = w;
            }
        }
    }

    public void SetSelected(bool selected)
    {
        isSelected = selected;
        if (boundaryRenderer != null)
            boundaryRenderer.material = selected ? selectedMaterial : normalMaterial;
    }

    public bool IsSelected() => isSelected;

    private void OnDestroy()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && gameObject.scene.isLoaded && !string.IsNullOrEmpty(HelipadId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && !manager.IsVisualDataDeletionSuppressed)
                manager.DeleteHelipad(HelipadId, destroyGameObject: false);
        }
#endif
    }

    private void OnDrawGizmos()
    {
#if UNITY_EDITOR
        if (!string.IsNullOrEmpty(HelipadId) && HelipadData != null && HelipadData.Boundary.Count > 0)
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && manager.showTextAnnotations)
            {
                Vector3 center = Vector3.zero;
                foreach (var p in HelipadData.Boundary) center += p;
                center /= HelipadData.Boundary.Count;
                UnityEditor.Handles.Label(center + Vector3.up * 0.5f, HelipadId);
            }
        }
#endif
    }
}
