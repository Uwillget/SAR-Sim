using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RoadNetworkEditor.RuntimeUI
{
    public enum SceneViewPreset
    {
        Perspective,
        Top,
        Front,
        Right,
        Left,
        Bottom,
        Back
    }

    /// <summary>
    /// Standalone equivalent of the most useful Unity Scene view navigation.
    /// Right drag orbits, middle drag pans and the wheel zooms.
    /// </summary>
    public sealed class RuntimeSceneNavigator : MonoBehaviour
    {
        public Camera ControlledCamera { get; private set; }
        public SceneViewPreset CurrentPreset { get; private set; } = SceneViewPreset.Top;
        public Vector3 Pivot { get; private set; }
        public bool IsOrthographic => ControlledCamera != null && ControlledCamera.orthographic;

        private float distance = 260f;
        private float yaw = 35f;
        private float pitch = 48f;
        private Vector3 lastMousePosition;

        public void Initialize(Camera targetCamera, SimulationManager manager)
        {
            ControlledCamera = targetCamera;
            FrameAll(manager, false);
            SetPreset(SceneViewPreset.Top);
        }

        public void FrameAll(SimulationManager manager, bool keepOrientation = true)
        {
            if (manager == null) return;
            var points = manager.Nodes.Values.Select(node => node.Position)
                .Concat(manager.ApproachNodes.Values.Select(node => node.Position))
                .Concat(manager.Vertices.Values.Select(vertex => vertex.Position))
                .Concat(manager.Helipads.Values.SelectMany(pad => pad.Boundary ?? new System.Collections.Generic.List<Vector3>()))
                .ToList();
            if (points.Count == 0) return;

            Bounds bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Count; i++) bounds.Encapsulate(points[i]);
            Pivot = bounds.center;
            float horizontalSize = bounds.extents.x / Mathf.Max(0.2f, ControlledCamera != null ? ControlledCamera.aspect : 1.777f);
            float fittedHalfSize = Mathf.Max(bounds.extents.z, horizontalSize) * 1.08f;
            distance = Mathf.Clamp(fittedHalfSize * 1.35f, 60f, 20000f);
            if (ControlledCamera != null)
            {
                ControlledCamera.farClipPlane = Mathf.Max(50000f, distance * 4f);
                if (!keepOrientation) ControlledCamera.orthographicSize = Mathf.Max(25f, fittedHalfSize);
                if (CurrentPreset == SceneViewPreset.Perspective) ApplyTransform();
                else SetPreset(CurrentPreset);
            }
        }

        public void FramePoints(IEnumerable<Vector3> source, float padding = 1.25f, bool topView = true)
        {
            List<Vector3> points = source?.ToList() ?? new List<Vector3>();
            if (ControlledCamera == null || points.Count == 0) return;
            Bounds bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Count; i++) bounds.Encapsulate(points[i]);
            Pivot = bounds.center;
            float horizontal = bounds.extents.x / Mathf.Max(0.2f, ControlledCamera.aspect);
            float fitted = Mathf.Max(8f, Mathf.Max(bounds.extents.z, horizontal) * Mathf.Max(1.05f, padding));
            ControlledCamera.orthographicSize = fitted;
            distance = Mathf.Clamp(fitted * 1.5f, 30f, 20000f);
            ControlledCamera.farClipPlane = Mathf.Max(50000f, distance * 4f);
            if (topView) SetPreset(SceneViewPreset.Top);
            else if (CurrentPreset == SceneViewPreset.Perspective) ApplyTransform();
            else SetPreset(CurrentPreset);
        }

        public void FrameBounds(Bounds bounds, float padding = 1.12f, bool topView = true)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            FramePoints(new[]
            {
                new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, min.y, max.z), new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, max.y, max.z), new Vector3(max.x, max.y, max.z)
            }, padding, topView);
        }

        public void FocusPoint(Vector3 point, float halfSize = 25f)
        {
            if (ControlledCamera == null) return;
            Pivot = point;
            ControlledCamera.orthographicSize = Mathf.Clamp(halfSize, 3f, 20000f);
            distance = Mathf.Clamp(halfSize * 1.5f, 12f, 20000f);
            ControlledCamera.farClipPlane = Mathf.Max(50000f, distance * 4f);
            if (CurrentPreset == SceneViewPreset.Perspective)
                ApplyTransform();
            else
                SetPreset(CurrentPreset);
        }

        public void SetPreset(SceneViewPreset preset)
        {
            CurrentPreset = preset;
            if (ControlledCamera == null) return;

            switch (preset)
            {
                case SceneViewPreset.Top:
                    ControlledCamera.orthographic = true;
                    ControlledCamera.transform.position = Pivot + Vector3.up * distance;
                    ControlledCamera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                    break;
                case SceneViewPreset.Front:
                    ControlledCamera.orthographic = true;
                    ControlledCamera.transform.position = Pivot + Vector3.forward * distance;
                    ControlledCamera.transform.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
                    break;
                case SceneViewPreset.Right:
                    ControlledCamera.orthographic = true;
                    ControlledCamera.transform.position = Pivot + Vector3.right * distance;
                    ControlledCamera.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
                    break;
                case SceneViewPreset.Left:
                    ControlledCamera.orthographic = true;
                    ControlledCamera.transform.position = Pivot + Vector3.left * distance;
                    ControlledCamera.transform.rotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
                    break;
                case SceneViewPreset.Bottom:
                    ControlledCamera.orthographic = true;
                    ControlledCamera.transform.position = Pivot + Vector3.down * distance;
                    ControlledCamera.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
                    break;
                case SceneViewPreset.Back:
                    ControlledCamera.orthographic = true;
                    ControlledCamera.transform.position = Pivot + Vector3.back * distance;
                    ControlledCamera.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                    break;
                default:
                    ControlledCamera.orthographic = false;
                    yaw = 35f;
                    pitch = 48f;
                    ApplyTransform();
                    break;
            }
        }

        public void ToggleProjection()
        {
            if (ControlledCamera == null) return;
            ControlledCamera.orthographic = !ControlledCamera.orthographic;
            if (CurrentPreset == SceneViewPreset.Perspective) ApplyTransform();
        }

        private void Update()
        {
            if (ControlledCamera == null || !ControlledCamera.isActiveAndEnabled || ControlledCamera.targetDisplay != 0)
                return;

            bool pointerOverUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            float wheel = pointerOverUi ? 0f : Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.001f)
            {
                if (ControlledCamera.orthographic)
                    ControlledCamera.orthographicSize = Mathf.Clamp(
                        ControlledCamera.orthographicSize * Mathf.Pow(0.88f, wheel), 3f, 20000f);
                else
                {
                    distance = Mathf.Clamp(distance * Mathf.Pow(0.88f, wheel), 3f, 20000f);
                    ApplyTransform();
                }
            }

            Vector3 mouse = Input.mousePosition;
            Vector3 delta = mouse - lastMousePosition;
            lastMousePosition = mouse;
            if (pointerOverUi) return;

            if (Input.GetMouseButton(2))
            {
                float unitsPerPixel = ControlledCamera.orthographic
                    ? ControlledCamera.orthographicSize * 2f / Mathf.Max(1f, Screen.height)
                    : distance * 0.0022f;
                Vector3 shift = (-ControlledCamera.transform.right * delta.x -
                                 ControlledCamera.transform.up * delta.y) * unitsPerPixel;
                Pivot += shift;
                ControlledCamera.transform.position += shift;
            }

            if (Input.GetMouseButton(1))
            {
                CurrentPreset = SceneViewPreset.Perspective;
                ControlledCamera.orthographic = false;
                yaw += delta.x * 0.25f;
                pitch = Mathf.Clamp(pitch - delta.y * 0.22f, 8f, 88f);
                ApplyTransform();
            }

            if (Input.GetKeyDown(KeyCode.F))
                Pivot = GetFocusPoint();
        }

        private Vector3 GetFocusPoint()
        {
            Ray ray = ControlledCamera.ScreenPointToRay(Input.mousePosition);
            Plane plane = new Plane(Vector3.up, Pivot);
            return plane.Raycast(ray, out float enter) ? ray.GetPoint(enter) : Pivot;
        }

        private void ApplyTransform()
        {
            if (ControlledCamera == null || CurrentPreset != SceneViewPreset.Perspective) return;
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            ControlledCamera.transform.position = Pivot - rotation * Vector3.forward * distance;
            ControlledCamera.transform.rotation = rotation;
        }
    }
}
