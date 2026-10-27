using System.Collections.Generic;
using UnityEngine;

namespace RoadNetworkEditor
{
/// <summary>
/// City model controller: height analysis, highlighting, and one-click building processing.
/// Attach to the CityModel parent object.
/// </summary>
[ExecuteAlways]
public class CityModelController : MonoBehaviour
{
    [Header("Height Analysis")]
    [Tooltip("Child buildings exceeding this height will be highlighted.")]
    public float heightThreshold = 50f;

    [Header("Highlight")]
    [Tooltip("When enabled, child buildings exceeding the height threshold are assigned a highlight material.")]
    public bool enableHighlight = false;
    public Material highlightMaterial;

    // Store original materials for restoring when highlight is disabled.
    private readonly Dictionary<MeshRenderer, Material[]> _originalMaterials =
        new Dictionary<MeshRenderer, Material[]>();
    private bool _lastHighlightState = false;
    private float _lastThreshold = 0f;
    private Material _lastHighlightMaterial = null;
    private Material _runtimeHighlightMaterial;
    public int HighlightedBuildingCount => _originalMaterials.Count;

    private const float TARGET_BASE_Y = 0f;

    private void OnValidate()
    {
        // Apply changes immediately when toggling highlight or adjusting the threshold in the Inspector.
        if (enableHighlight != _lastHighlightState
            || !Mathf.Approximately(heightThreshold, _lastThreshold)
            || highlightMaterial != _lastHighlightMaterial)
        {
            if (enableHighlight && highlightMaterial != null)
                ApplyHighlight();
            else
                RemoveHighlight();

            _lastHighlightState = enableHighlight;
            _lastThreshold = heightThreshold;
            _lastHighlightMaterial = highlightMaterial;
        }
    }

    private void OnDisable()
    {
        if (enableHighlight)
            RemoveHighlight();
    }

    private void OnEnable()
    {
        if (enableHighlight) ApplyHighlight();
    }

    private void OnDestroy()
    {
        RemoveHighlight();
        if (_runtimeHighlightMaterial != null)
        {
            if (Application.isPlaying) Destroy(_runtimeHighlightMaterial);
            else DestroyImmediate(_runtimeHighlightMaterial);
            _runtimeHighlightMaterial = null;
        }
    }

    /// <summary>
    /// Apply highlight material to child buildings that exceed the height threshold.
    /// </summary>
    public void ApplyHighlight()
    {
        Material activeHighlight = IsDistinctHighlight(highlightMaterial)
            ? highlightMaterial
            : (_runtimeHighlightMaterial != null
                ? _runtimeHighlightMaterial
                : (_runtimeHighlightMaterial = CreateDefaultHighlightMaterial()));

        if (activeHighlight == null)
        {
            Debug.LogWarning("Highlight material is not assigned and could not be created.");
            return;
        }

        RemoveHighlight();

        MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>(true);
        foreach (MeshRenderer renderer in renderers)
        {
            // "Building height" is the renderer's vertical extent, not its absolute
            // world-space roof elevation. The latter made every building highlight
            // when the whole city model was translated upward.
            if (renderer.bounds.size.y > heightThreshold)
            {
                Material[] originals = renderer.sharedMaterials;
                _originalMaterials[renderer] = originals;
                int slotCount = Mathf.Max(1, originals.Length);
                Material[] highlighted = new Material[slotCount];
                for (int i = 0; i < slotCount; i++) highlighted[i] = activeHighlight;
                renderer.sharedMaterials = highlighted;
            }
        }

        Debug.Log($"[CityModelController] {_originalMaterials.Count} child building(s) exceeding height {heightThreshold} highlighted.");
    }

    /// <summary>
    /// Restore original materials for all highlighted buildings.
    /// </summary>
    public void RemoveHighlight()
    {
        foreach (var kvp in _originalMaterials)
        {
            if (kvp.Key != null)
                kvp.Key.sharedMaterials = kvp.Value;
        }
        _originalMaterials.Clear();
    }

    private Material CreateDefaultHighlightMaterial()
    {
        // Prefer an unlit opaque shader so the highlight remains unmistakable in
        // both the Editor and the Windows build, independent of scene lighting.
        Shader shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
        if (shader == null) return null;

        Material mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        Color color = new Color(1f, 0.24f, 0.06f, 1f);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", color * 1.4f);
        }
        return mat;
    }

    private static bool IsDistinctHighlight(Material material)
    {
        if (material == null) return false;
        Color color = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") :
            material.HasProperty("_Color") ? material.GetColor("_Color") : Color.gray;
        float maximum = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
        float minimum = Mathf.Min(color.r, Mathf.Min(color.g, color.b));
        // Neutral grey materials are commonly the original city material and do
        // not provide any visible feedback when assigned as a "highlight".
        return maximum > 0.65f && maximum - minimum > 0.22f && color.a > 0.5f;
    }

    /// <summary>
    /// One-click building processing: add colliders and align bases.
    /// </summary>
    public void ProcessBuildingModel()
    {
#if UNITY_EDITOR
        UnityEditor.Undo.RegisterCompleteObjectUndo(transform, "Process Building Model");
#endif

        int colliderCount = 0;
        int levelCount = 0;

        MeshFilter[] meshFilters = GetComponentsInChildren<MeshFilter>(true);

        foreach (MeshFilter mf in meshFilters)
        {
            if (mf.sharedMesh == null) continue;

            GameObject child = mf.gameObject;

            // 1. Add MeshCollider (Convex)
            MeshCollider collider = child.GetComponent<MeshCollider>();
            if (collider == null)
                collider = child.AddComponent<MeshCollider>();
            collider.convex = true;
            colliderCount++;

            // 2. Align building base to target height
            MeshRenderer renderer = child.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                float offset = TARGET_BASE_Y - renderer.bounds.min.y;
                Vector3 pos = child.transform.position;
                pos.y += offset;
                child.transform.position = pos;
                levelCount++;
            }
        }

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(gameObject);
#endif

        Debug.Log($"[CityModelController] Processing complete: {gameObject.name} — "
            + $"Added {colliderCount} MeshCollider(s) (Convex), "
            + $"Aligned {levelCount} building(s) to Y = {TARGET_BASE_Y}");
    }
}
}
