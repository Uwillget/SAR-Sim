using RoadNetworkEditor;
using UnityEngine;

[ExecuteAlways]
public class RampVisual : MonoBehaviour
{
    [HideInInspector] public string RampId;
    [HideInInspector] public Ramp RampData;
    [HideInInspector] public string LinkCircleId;

    private VertexVisual fromVertex;
    private VertexVisual toVertex;
    private VertiportManager manager;
    private bool isSelected = false;

    public void Initialize(Ramp ramp, VertexVisual from, VertexVisual to, string linkCircleId, VertiportManager mgr)
    {
        RampData = ramp;
        RampId = ramp.RampId;
        LinkCircleId = linkCircleId;
        fromVertex = from;
        toVertex = to;
        manager = mgr;
        gameObject.name = $"Ramp_{RampId}";
    }

    public void UpdateVisual()
    {
        // Gizmos-only: just mark for repaint
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
        if (!Application.isPlaying && gameObject.scene.isLoaded && !string.IsNullOrEmpty(RampId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && !manager.IsVisualDataDeletionSuppressed)
                manager.DeleteRamp(RampId);
        }
#endif
    }

    private void OnDrawGizmos()
    {
        if (RampData == null || fromVertex == null || toVertex == null) return;

        Vector3 fromPos = fromVertex.transform.position;
        Vector3 toPos = toVertex.transform.position;

        if (manager == null) manager = FindObjectOfType<VertiportManager>();

        // Choose color
        Color arcColor = new Color(0.7f, 0f, 0.7f, 0.9f);     // purple for arc
        Color straightColor = new Color(1f, 0.5f, 0f, 0.9f);    // orange for straight
        Color selectedTint = new Color(1f, 1f, 0f, 1f);          // yellow highlight
        Color arrowColor = Color.yellow;

        Color baseColor = string.IsNullOrEmpty(LinkCircleId) ? straightColor : arcColor;
        Gizmos.color = isSelected ? selectedTint : baseColor;

        if (string.IsNullOrEmpty(LinkCircleId))
        {
            // Straight line between different circles
            Gizmos.DrawLine(fromPos, toPos);
            DrawDirectionArrow(fromPos, toPos, Gizmos.color);
        }
        else
        {
            // Arc along the circle between two vertices
            CircleVisual cv = manager != null ? manager.GetCircleVisual(LinkCircleId) : null;
            if (cv == null || cv.CircleData == null)
            {
                Gizmos.DrawLine(fromPos, toPos);
                DrawDirectionArrow(fromPos, toPos, Gizmos.color);
                return;
            }

            Vector3 center = cv.CircleData.Center;
            float radius = cv.CircleData.Radius;

            Vector3 dirFrom = fromPos - center;
            Vector3 dirTo = toPos - center;
            float angleFrom = Mathf.Atan2(dirFrom.z, dirFrom.x);
            float angleTo = Mathf.Atan2(dirTo.z, dirTo.x);

            // Arc direction
            float angleDiff = angleTo - angleFrom;
            while (angleDiff < 0) angleDiff += 2 * Mathf.PI;
            while (angleDiff >= 2 * Mathf.PI) angleDiff -= 2 * Mathf.PI;

            ArcDirection dir = RampData?.Direction ?? ArcDirection.Shortest;
            if (dir == ArcDirection.Shortest && angleDiff > Mathf.PI)
                angleDiff = angleDiff - 2 * Mathf.PI;  // go the short way (negative)
            else if (dir == ArcDirection.Clockwise)
                angleDiff = angleDiff - 2 * Mathf.PI;  // clockwise = negative arc
            // CounterClockwise: keep positive angleDiff

            int segments = 48;
            Vector3 prev = fromPos;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = angleFrom + angleDiff * t;
                Vector3 pos = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                Gizmos.DrawLine(prev, pos);
                prev = pos;
            }

            // Direction arrow at mid-point of arc
            float midAngle = angleFrom + angleDiff * 0.5f;
            Vector3 midPoint = center + new Vector3(Mathf.Cos(midAngle), 0f, Mathf.Sin(midAngle)) * radius;
            Vector3 tangent = new Vector3(-Mathf.Sin(midAngle), 0f, Mathf.Cos(midAngle));
            if (angleDiff < 0) tangent = -tangent;
            DrawArrowAt(midPoint, tangent, Gizmos.color);
        }

        // Text label
        if (manager != null && manager.showTextAnnotations)
        {
            Vector3 mid = fromPos;
            if (!string.IsNullOrEmpty(LinkCircleId))
            {
                CircleVisual cv = manager.GetCircleVisual(LinkCircleId);
                if (cv != null && cv.CircleData != null)
                {
                    Vector3 dirFrom2 = fromPos - cv.CircleData.Center;
                    Vector3 dirTo2 = toPos - cv.CircleData.Center;
                    float aFrom = Mathf.Atan2(dirFrom2.z, dirFrom2.x);
                    float aTo = Mathf.Atan2(dirTo2.z, dirTo2.x);
                    float diff = aTo - aFrom;
                    while (diff < -Mathf.PI) diff += 2 * Mathf.PI;
                    while (diff > Mathf.PI) diff -= 2 * Mathf.PI;
                    float aMid = aFrom + diff * 0.5f;
                    mid = cv.CircleData.Center + new Vector3(Mathf.Cos(aMid), 0f, Mathf.Sin(aMid)) * cv.CircleData.Radius * 1.15f;
                }
            }
            else
            {
                mid = (fromPos + toPos) * 0.5f;
            }

            string label = string.IsNullOrEmpty(LinkCircleId) ? $"{RampId}\n(straight)" : $"{RampId}\n(arc)";
#if UNITY_EDITOR
            UnityEditor.Handles.Label(mid + Vector3.up * 0.6f, label);
#endif
        }
    }

    private void DrawDirectionArrow(Vector3 from, Vector3 to, Color color)
    {
        Vector3 dir = (to - from).normalized;
        Vector3 tip = to - dir * 0.4f;
        DrawArrowAt(tip, dir, color);
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
        if (RampData == null || fromVertex == null || toVertex == null) return;

        Gizmos.color = new Color(1f, 1f, 0f, 1f);

        if (string.IsNullOrEmpty(LinkCircleId))
        {
            Gizmos.DrawLine(fromVertex.transform.position, toVertex.transform.position);
        }
        else
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            CircleVisual cv = manager != null ? manager.GetCircleVisual(LinkCircleId) : null;
            if (cv == null || cv.CircleData == null) return;

            Vector3 center = cv.CircleData.Center;
            float radius = cv.CircleData.Radius;
            Vector3 dirFrom = fromVertex.transform.position - center;
            Vector3 dirTo = toVertex.transform.position - center;
            float angleFrom = Mathf.Atan2(dirFrom.z, dirFrom.x);
            float angleTo = Mathf.Atan2(dirTo.z, dirTo.x);
            float angleDiff = angleTo - angleFrom;
            while (angleDiff < 0) angleDiff += 2 * Mathf.PI;
            while (angleDiff >= 2 * Mathf.PI) angleDiff -= 2 * Mathf.PI;

            ArcDirection dir = RampData?.Direction ?? ArcDirection.Shortest;
            if (dir == ArcDirection.Shortest && angleDiff > Mathf.PI)
                angleDiff -= 2 * Mathf.PI;
            else if (dir == ArcDirection.Clockwise)
                angleDiff -= 2 * Mathf.PI;

            Vector3 prev = fromVertex.transform.position;
            for (int i = 1; i <= 48; i++)
            {
                float t = i / 48f;
                float angle = angleFrom + angleDiff * t;
                Vector3 pos = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                Gizmos.DrawLine(prev, pos);
                prev = pos;
            }
        }
    }
}
