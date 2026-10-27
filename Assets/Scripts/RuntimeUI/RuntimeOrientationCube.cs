using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RoadNetworkEditor.RuntimeUI
{
    /// <summary>
    /// Runtime equivalent of Unity's classic Scene Gizmo: a neutral centre cube controls
    /// projection, while six independent cones select the six orthographic directions.
    /// The gizmo uses the same projection model as the controlled camera.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RuntimeOrientationCube : MaskableGraphic, IPointerClickHandler
    {
        private const float CenterHalfSize = 0.23f;
        // Unity's Scene Gizmo cones point inward: the narrow tip meets the centre
        // cube and the circular base faces out toward its axis label.
        private const float ConeTipDistance = 0.30f;
        private const float ConeBaseDistance = 0.91f;
        private const float ConeBaseRadius = 0.20f;
        private const int ConeSegments = 10;

        private static readonly Vector3[] UnitCubeVertices =
        {
            new Vector3(-1f, -1f, -1f), new Vector3(1f, -1f, -1f),
            new Vector3(1f, 1f, -1f), new Vector3(-1f, 1f, -1f),
            new Vector3(-1f, -1f, 1f), new Vector3(1f, -1f, 1f),
            new Vector3(1f, 1f, 1f), new Vector3(-1f, 1f, 1f)
        };

        private static readonly int[,] CubeFaces =
        {
            { 1, 5, 6, 2 }, { 4, 0, 3, 7 },
            { 3, 2, 6, 7 }, { 0, 4, 5, 1 },
            { 4, 7, 6, 5 }, { 0, 1, 2, 3 }
        };

        private static readonly int[,] CubeEdges =
        {
            { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
            { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
            { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
        };

        private static readonly Vector3[] CubeFaceNormals =
        {
            Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
        };

        private static readonly Vector3[] ConeDirections =
        {
            Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
        };

        private static readonly SceneViewPreset[] ConePresets =
        {
            SceneViewPreset.Right, SceneViewPreset.Left, SceneViewPreset.Top,
            SceneViewPreset.Bottom, SceneViewPreset.Front, SceneViewPreset.Back
        };

        private readonly Vector2[] coneHitPoints = new Vector2[6];
        private readonly float[] coneHitRadii = new float[6];
        private readonly Text[] axisLabels = new Text[3];
        private RuntimeSceneNavigator navigator;
        private Action<SceneViewPreset> selectPreset;
        private Action toggleProjection;
        private Quaternion lastRotation;
        private bool lastOrthographic;
        private float centerHitRadius = 15f;

        public Quaternion VisualRotation => navigator?.ControlledCamera != null
            ? Quaternion.Inverse(navigator.ControlledCamera.transform.rotation)
            : Quaternion.identity;

        public int DirectionConeCount => ConeDirections.Length;

        public bool UsesPerspectiveProjection => navigator?.ControlledCamera != null &&
                                                 !navigator.ControlledCamera.orthographic;

        public void Configure(RuntimeSceneNavigator sceneNavigator, Action<SceneViewPreset> selection,
            Font labelFont = null)
        {
            Configure(sceneNavigator, selection, () => sceneNavigator?.ToggleProjection(), labelFont);
        }

        public void Configure(RuntimeSceneNavigator sceneNavigator, Action<SceneViewPreset> selection,
            Action projectionToggle, Font labelFont = null)
        {
            navigator = sceneNavigator;
            selectPreset = selection;
            toggleProjection = projectionToggle;
            raycastTarget = true;
            color = Color.white;
            lastOrthographic = navigator?.ControlledCamera == null || navigator.ControlledCamera.orthographic;
            BuildAxisLabels(labelFont ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            UpdateAxisLabels();
            SetVerticesDirty();
        }

        /// <summary>Depth multiplier used by the gizmo's current projection.</summary>
        public float ProjectionScaleAtDepth(float cameraLocalDepth)
        {
            if (!UsesPerspectiveProjection) return 1f;
            return 1f / Mathf.Max(0.58f, 1f + cameraLocalDepth * 0.28f);
        }

        /// <summary>Activates the centre cube. Kept public for keyboard/accessibility bridges and tests.</summary>
        public void ActivateCenter()
        {
            toggleProjection?.Invoke();
            UpdateAxisLabels();
            SetVerticesDirty();
        }

        private void Update()
        {
            Quaternion rotation = VisualRotation;
            bool orthographic = navigator?.ControlledCamera == null || navigator.ControlledCamera.orthographic;
            if (Quaternion.Angle(lastRotation, rotation) < 0.05f && lastOrthographic == orthographic) return;
            lastRotation = rotation;
            lastOrthographic = orthographic;
            UpdateAxisLabels();
            SetVerticesDirty();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            UpdateAxisLabels();
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vertexHelper)
        {
            vertexHelper.Clear();
            Quaternion rotation = VisualRotation;
            int[] order = Enumerable.Range(0, ConeDirections.Length)
                .OrderByDescending(index => (rotation * ConeDirections[index]).z).ToArray();

            // Depth-sort all cones, then keep the projection-switch cube visibly on top.
            // This mirrors Unity's interaction priority: the centre remains identifiable
            // even when one axis points directly towards the viewer.
            foreach (int index in order)
                AddCone(vertexHelper, index, rotation);
            AddCenterCube(vertexHelper, rotation);

            centerHitRadius = Mathf.Max(12f,
                Mathf.Min(rectTransform.rect.width, rectTransform.rect.height) * 0.13f);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform,
                    eventData.position, eventData.pressEventCamera, out Vector2 point)) return;

            // The centre cube always has priority over direction cones.
            if (point.sqrMagnitude <= centerHitRadius * centerHitRadius)
            {
                ActivateCenter();
                return;
            }

            int bestCone = -1;
            float bestDistance = float.MaxValue;
            for (int index = 0; index < coneHitPoints.Length; index++)
            {
                float distance = (coneHitPoints[index] - point).sqrMagnitude;
                if (distance > coneHitRadii[index] * coneHitRadii[index] || distance >= bestDistance) continue;
                bestDistance = distance;
                bestCone = index;
            }
            if (bestCone >= 0) selectPreset?.Invoke(ConePresets[bestCone]);
        }

        private void AddCenterCube(VertexHelper helper, Quaternion rotation)
        {
            Vector3[] rotated = UnitCubeVertices
                .Select(vertex => rotation * (vertex * CenterHalfSize)).ToArray();
            Vector2[] projected = rotated.Select(Project).ToArray();
            var visibleFaces = new List<(int index, float depth)>();
            for (int face = 0; face < CubeFaceNormals.Length; face++)
            {
                Vector3 normal = rotation * CubeFaceNormals[face];
                if (normal.z <= 0.02f) visibleFaces.Add((face, AverageCubeFaceDepth(rotated, face)));
            }

            foreach ((int face, _) in visibleFaces.OrderByDescending(item => item.depth))
                AddQuad(helper, projected, face, CenterFaceColor(face));
            for (int edge = 0; edge < CubeEdges.GetLength(0); edge++)
                AddLine(helper, projected[CubeEdges[edge, 0]], projected[CubeEdges[edge, 1]],
                    new Color(0.20f, 0.19f, 0.18f, 0.92f), 1.2f);
        }

        private void AddCone(VertexHelper helper, int index, Quaternion rotation)
        {
            Vector3 direction = ConeDirections[index];
            Vector3 tangentSeed = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.9f
                ? Vector3.right : Vector3.up;
            Vector3 tangent = Vector3.Cross(direction, tangentSeed).normalized;
            Vector3 bitangent = Vector3.Cross(direction, tangent).normalized;
            Vector3 baseCenter = direction * ConeBaseDistance;
            Vector3 tip = direction * ConeTipDistance;
            Vector2 projectedTip = Project(rotation * tip);
            Vector2 projectedBaseCenter = Project(rotation * baseCenter);
            Color color = ConeColor(index);
            Color capColor = Color.Lerp(color, Color.white, index % 2 == 0 ? 0.18f : 0.08f);

            Vector2[] ring = new Vector2[ConeSegments];
            for (int segment = 0; segment < ConeSegments; segment++)
            {
                float angle = segment * Mathf.PI * 2f / ConeSegments;
                Vector3 point = baseCenter + tangent * (Mathf.Cos(angle) * ConeBaseRadius) +
                                bitangent * (Mathf.Sin(angle) * ConeBaseRadius);
                ring[segment] = Project(rotation * point);
            }

            for (int segment = 0; segment < ConeSegments; segment++)
            {
                int next = (segment + 1) % ConeSegments;
                AddTriangle(helper, projectedTip, ring[segment], ring[next], color);
                AddTriangle(helper, projectedBaseCenter, ring[next], ring[segment], capColor);
            }

            coneHitPoints[index] = Vector2.Lerp(projectedBaseCenter, projectedTip, 0.57f);
            float projectedRadius = ring.Max(point => Vector2.Distance(point, projectedBaseCenter));
            coneHitRadii[index] = Mathf.Max(12f,
                Vector2.Distance(projectedBaseCenter, projectedTip) * 0.42f + projectedRadius);
        }

        private Vector2 Project(Vector3 cameraLocalPoint)
        {
            float size = Mathf.Min(rectTransform.rect.width, rectTransform.rect.height) * 0.35f;
            return new Vector2(cameraLocalPoint.x, cameraLocalPoint.y) * size *
                   ProjectionScaleAtDepth(cameraLocalPoint.z);
        }

        private void BuildAxisLabels(Font font)
        {
            if (axisLabels[0] != null) return;
            string[] names = { "X", "Y", "Z" };
            Color[] colors =
            {
                new Color(0.88f, 0.30f, 0.24f, 1f),
                new Color(0.44f, 0.72f, 0.25f, 1f),
                new Color(0.25f, 0.48f, 0.86f, 1f)
            };
            for (int index = 0; index < axisLabels.Length; index++)
            {
                var labelObject = new GameObject("OrientationAxisLabel_" + names[index], typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(Text));
                labelObject.transform.SetParent(transform, false);
                RectTransform labelRect = (RectTransform)labelObject.transform;
                labelRect.anchorMin = labelRect.anchorMax = new Vector2(0.5f, 0.5f);
                labelRect.sizeDelta = new Vector2(22f, 20f);
                Text label = labelObject.GetComponent<Text>();
                label.font = font;
                label.fontSize = 12;
                label.fontStyle = FontStyle.Bold;
                label.alignment = TextAnchor.MiddleCenter;
                label.color = colors[index];
                label.text = names[index];
                label.raycastTarget = false;
                Outline outline = labelObject.AddComponent<Outline>();
                outline.effectColor = new Color(1f, 1f, 1f, 0.72f);
                outline.effectDistance = new Vector2(1f, -1f);
                axisLabels[index] = label;
            }
        }

        private void UpdateAxisLabels()
        {
            if (axisLabels[0] == null || rectTransform.rect.width <= 0f) return;
            Vector3[] positiveDirections = { Vector3.right, Vector3.up, Vector3.forward };
            Quaternion rotation = VisualRotation;
            for (int index = 0; index < axisLabels.Length; index++)
            {
                Vector2 position = Project(rotation * positiveDirections[index] * 1.27f);
                axisLabels[index].gameObject.SetActive(position.magnitude > centerHitRadius * 1.25f);
                axisLabels[index].rectTransform.anchoredPosition = position;
            }
        }

        private static float AverageCubeFaceDepth(IReadOnlyList<Vector3> vertices, int face)
        {
            float depth = 0f;
            for (int corner = 0; corner < 4; corner++) depth += vertices[CubeFaces[face, corner]].z;
            return depth * 0.25f;
        }

        private static Color CenterFaceColor(int face)
        {
            Color light = new Color(0.92f, 0.91f, 0.88f, 1f);
            Color shade = new Color(0.63f, 0.62f, 0.59f, 1f);
            return Color.Lerp(light, shade, (face % 3) * 0.18f);
        }

        private static Color ConeColor(int index)
        {
            if (index % 2 == 1) return new Color(0.68f, 0.67f, 0.64f, 1f);
            if (index < 2) return new Color(0.88f, 0.30f, 0.24f, 1f);
            if (index < 4) return new Color(0.44f, 0.72f, 0.25f, 1f);
            return new Color(0.25f, 0.48f, 0.86f, 1f);
        }

        private static void AddQuad(VertexHelper helper, IReadOnlyList<Vector2> points, int face, Color color)
        {
            int start = helper.currentVertCount;
            for (int corner = 0; corner < 4; corner++)
                helper.AddVert(points[CubeFaces[face, corner]], color, Vector2.zero);
            helper.AddTriangle(start, start + 1, start + 2);
            helper.AddTriangle(start, start + 2, start + 3);
        }

        private static void AddTriangle(VertexHelper helper, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            int start = helper.currentVertCount;
            helper.AddVert(a, color, Vector2.zero);
            helper.AddVert(b, color, Vector2.zero);
            helper.AddVert(c, color, Vector2.zero);
            helper.AddTriangle(start, start + 1, start + 2);
        }

        private static void AddLine(VertexHelper helper, Vector2 from, Vector2 to, Color color, float width)
        {
            Vector2 direction = to - from;
            if (direction.sqrMagnitude < 0.0001f) return;
            Vector2 normal = new Vector2(-direction.y, direction.x).normalized * width * 0.5f;
            int start = helper.currentVertCount;
            helper.AddVert(from - normal, color, Vector2.zero);
            helper.AddVert(from + normal, color, Vector2.zero);
            helper.AddVert(to + normal, color, Vector2.zero);
            helper.AddVert(to - normal, color, Vector2.zero);
            helper.AddTriangle(start, start + 1, start + 2);
            helper.AddTriangle(start, start + 2, start + 3);
        }
    }
}
