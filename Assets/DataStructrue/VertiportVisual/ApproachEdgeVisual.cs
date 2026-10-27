using RoadNetworkEditor;
using UnityEngine;

[ExecuteAlways]
public class ApproachEdgeVisual : MonoBehaviour
{
    [HideInInspector] public string EdgeId;
    [HideInInspector] public ApproachEdge EdgeData;

    private ApproachNodeVisual fromNode;
    private ApproachNodeVisual toNode;
    private VertiportManager manager;
    private bool isSelected = false;

    public void Initialize(ApproachEdge edge, ApproachNodeVisual from, ApproachNodeVisual to, VertiportManager mgr)
    {
        EdgeData = edge;
        EdgeId = edge.EdgeId;
        fromNode = from;
        toNode = to;
        manager = mgr;
        gameObject.name = $"ApprEdge_{EdgeId}";
    }

    public void UpdateVisual()
    {
#if UNITY_EDITOR
        UnityEditor.SceneView.RepaintAll();
#endif
    }

    public void SetSelected(bool selected)
    {
        isSelected = selected;
    }

    public bool IsSelected() => isSelected;

    private void OnDestroy()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && gameObject.scene.isLoaded && !string.IsNullOrEmpty(EdgeId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && !manager.IsVisualDataDeletionSuppressed)
                manager.DeleteApproachEdge(EdgeId);
        }
#endif
    }

    private void OnDrawGizmos()
    {
        if (EdgeData == null || fromNode == null || toNode == null) return;

        Vector3 fromPos = fromNode.transform.position;
        Vector3 toPos = toNode.transform.position;

        if (manager == null) manager = FindObjectOfType<VertiportManager>();

        Color normalColor = new Color(0.3f, 0.7f, 1f, 0.9f);
        Color selectedColor = new Color(1f, 1f, 0f, 1f);

        Gizmos.color = isSelected ? selectedColor : normalColor;
        Gizmos.DrawLine(fromPos, toPos);

        // Direction arrow at midpoint
        Vector3 midPoint = (fromPos + toPos) * 0.5f;
        Vector3 dir = (toPos - fromPos).normalized;
        if (dir.magnitude < 0.001f) dir = Vector3.forward;
        DrawArrowAt(midPoint, dir, Gizmos.color);

        // Text label
        if (manager != null && manager.showTextAnnotations)
        {
            Vector3 labelPos = midPoint + Vector3.up * 0.8f;
#if UNITY_EDITOR
            UnityEditor.Handles.Label(labelPos, EdgeId);
#endif
        }
    }

    private void DrawArrowAt(Vector3 tip, Vector3 dir, Color color)
    {
        Gizmos.color = color;
        Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
        float arrowLen = 0.6f;
        Gizmos.DrawLine(tip, tip - dir * arrowLen + right * arrowLen * 0.4f);
        Gizmos.DrawLine(tip, tip - dir * arrowLen - right * arrowLen * 0.4f);
    }

    private void OnDrawGizmosSelected()
    {
        if (!isSelected) return;
        if (EdgeData == null || fromNode == null || toNode == null) return;

        Gizmos.color = new Color(1f, 1f, 0f, 1f);
        Gizmos.DrawLine(fromNode.transform.position, toNode.transform.position);
    }
}
