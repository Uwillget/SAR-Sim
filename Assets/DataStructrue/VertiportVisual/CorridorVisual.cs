using RoadNetworkEditor;
using UnityEngine;

[ExecuteAlways]
public class CorridorVisual : MonoBehaviour
{
    [HideInInspector] public string CorridorId;
    [HideInInspector] public NetConnectedCorridor CorridorData;
    [HideInInspector] public string ParentVertiportId;

    private VertiportManager manager;
    private bool isSelected = false;

    public void Initialize(NetConnectedCorridor corridor, string parentVpId, VertiportManager mgr)
    {
        CorridorData = corridor;
        CorridorId = corridor.CorridorId;
        ParentVertiportId = parentVpId;
        manager = mgr;
        gameObject.name = $"Corridor_{CorridorId}";
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
        // Only cascade-delete during explicit user deletion, not scene unload / domain reload
        if (!Application.isPlaying && gameObject.scene.isLoaded && !string.IsNullOrEmpty(CorridorId))
        {
            if (manager == null) manager = FindObjectOfType<VertiportManager>();
            if (manager != null && !manager.IsVisualDataDeletionSuppressed)
                manager.DeleteCorridor(CorridorId);
        }
#endif
    }

    private void OnDrawGizmos()
    {
        if (CorridorData == null) return;

        Vector3 lanePt = CorridorData.LaneLinkPoint;
        Vector3 rampPt = CorridorData.RampLinkPoint;

        if (manager == null) manager = FindObjectOfType<VertiportManager>();

        bool isEntry = CorridorData.CorridorType == "Entry";
        Color baseColor = isEntry ? new Color(0.2f, 0.8f, 0.2f, 0.9f) : new Color(0.8f, 0.4f, 0.2f, 0.9f);
        Color selectedColor = new Color(1f, 1f, 0f, 1f);

        Color drawColor = isSelected ? selectedColor : baseColor;
        float bezierDist = Vector3.Distance(lanePt, rampPt);

        Vector3 laneDir = ComputeLaneDirection();
        Vector3 rampDir = ComputeRampDirection();
        float ratio = manager != null ? manager.corridorTangentRatio : 0.3f;
        float d = bezierDist * ratio;

        Vector3 p0, p1, p2, p3;

        if (isEntry)
        {
            // Entry: Ramp → Lane. Tangent at ramp points toward lane; tangent at lane follows lane forward direction.
            if (Vector3.Dot(rampDir, lanePt - rampPt) < 0) rampDir = -rampDir;
            p0 = rampPt; p3 = lanePt;
            p1 = p0 + rampDir * d;
            p2 = p3 - laneDir * d;
        }
        else
        {
            // Exit: Lane → Ramp. Tangent at lane points toward ramp; tangent at ramp follows ramp forward direction.
            if (Vector3.Dot(laneDir, rampPt - lanePt) < 0) laneDir = -laneDir;
            p0 = lanePt; p3 = rampPt;
            p1 = p0 + laneDir * d;
            p2 = p3 - rampDir * d;
        }

        // Draw Bezier curve
        int segments = 32;
        Vector3 prev = p0;
        for (int i = 1; i <= segments; i++)
        {
            float t = i / (float)segments;
            Vector3 pt = EvaluateBezier(p0, p1, p2, p3, t);
            Gizmos.color = drawColor;
            Gizmos.DrawLine(prev, pt);
            prev = pt;
        }

        // Arrow at midpoint along Bezier tangent
        Vector3 midPoint = EvaluateBezier(p0, p1, p2, p3, 0.5f);
        Vector3 midTangent = EvaluateBezierDerivative(p0, p1, p2, p3, 0.5f);
        if (midTangent.magnitude < 0.001f) midTangent = Vector3.forward;
        else midTangent.Normalize();

        // Arrow direction follows the Bezier travel direction (P0 → P3)
        Vector3 arrowDir = midTangent;
        if (arrowDir.magnitude < 0.001f) arrowDir = Vector3.forward;
        DrawArrowAt(midPoint, arrowDir, drawColor);

        // Text label
        if (manager != null && manager.showTextAnnotations)
        {
            Vector3 labelPos = midPoint + Vector3.up * 0.8f;
#if UNITY_EDITOR
            UnityEditor.Handles.Label(labelPos, $"{CorridorId}\n({CorridorData.CorridorType})");
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

    #region Bezier Helpers

    private Vector3 ComputeLaneDirection()
    {
        NetworkManager netMgr = FindObjectOfType<NetworkManager>();
        if (netMgr == null || netMgr.FullNetwork == null) return Vector3.forward;

        foreach (var edge in netMgr.FullNetwork.Edges)
        {
            foreach (var lane in edge.Lanes)
            {
                if (lane.LaneId != CorridorData.LinkLaneId || lane.Centerline == null || lane.Centerline.Count < 2)
                    continue;

                float bestDist = float.MaxValue;
                Vector3 bestDir = Vector3.forward;
                for (int i = 0; i < lane.Centerline.Count - 1; i++)
                {
                    Vector3 proj = ProjectPointOnSegment(CorridorData.LaneLinkPoint,
                        lane.Centerline[i], lane.Centerline[i + 1]);
                    float d = Vector3.Distance(CorridorData.LaneLinkPoint, proj);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestDir = (lane.Centerline[i + 1] - lane.Centerline[i]).normalized;
                    }
                }
                return bestDir;
            }
        }
        return Vector3.forward;
    }

    private Vector3 ComputeRampDirection()
    {
        if (manager == null) manager = FindObjectOfType<VertiportManager>();
        if (manager == null) return Vector3.forward;

        RampVisual rv = manager.GetRampVisual(CorridorData.LinkRampId);
        if (rv == null || rv.RampData == null) return Vector3.forward;

        VertexVisual fromV = manager.GetVertexVisual(rv.RampData.FromVertexId);
        VertexVisual toV = manager.GetVertexVisual(rv.RampData.ToVertexId);
        if (fromV == null || toV == null) return Vector3.forward;

        if (string.IsNullOrEmpty(rv.LinkCircleId))
        {
            return (toV.transform.position - fromV.transform.position).normalized;
        }
        else
        {
            CircleVisual cv = manager.GetCircleVisual(rv.LinkCircleId);
            if (cv == null || cv.CircleData == null) return Vector3.forward;

            Vector3 center = cv.CircleData.Center;
            Vector3 radial = CorridorData.RampLinkPoint - center;
            radial.y = 0;
            if (radial.magnitude < 0.001f) return Vector3.forward;
            radial.Normalize();

            Vector3 fromDir = fromV.transform.position - center;
            Vector3 toDir = toV.transform.position - center;
            fromDir.y = 0; toDir.y = 0;
            float signedAngle = Mathf.Atan2(
                Vector3.Dot(Vector3.up, Vector3.Cross(fromDir, toDir)),
                Vector3.Dot(fromDir, toDir));

            Vector3 tangent = Vector3.Cross(Vector3.up, radial);
            if (signedAngle < 0) tangent = -tangent;
            return tangent.normalized;
        }
    }

    private Vector3 ProjectPointOnSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        Vector3 ap = point - a;
        float t = Vector3.Dot(ap, ab) / Mathf.Max(ab.sqrMagnitude, 0.0001f);
        t = Mathf.Clamp01(t);
        return a + t * ab;
    }

    private Vector3 EvaluateBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1 - t;
        return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
    }

    private Vector3 EvaluateBezierDerivative(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1 - t;
        return 3 * u * u * (p1 - p0) + 6 * u * t * (p2 - p1) + 3 * t * t * (p3 - p2);
    }

    #endregion
}
