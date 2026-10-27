using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RoadNetworkEditor.RuntimeUI
{
    /// <summary>
    /// Adds image-viewer style wheel zoom to a ScrollRect. The ScrollRect remains
    /// responsible for clamped drag/pan bounds, so enlarged content cannot be lost
    /// outside its viewport.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SarZoomableViewport : MonoBehaviour, IScrollHandler
    {
        private RectTransform target;
        private ScrollRect scrollRect;
        private float minimumZoom = 1f;
        private float maximumZoom = 5f;
        private float zoomStep = 0.25f;
        private float zoom = 1f;
        private Vector2 resetNormalizedPosition = new Vector2(0.5f, 0.5f);

        public float Zoom => zoom;
        public int ZoomPercent => Mathf.RoundToInt(zoom * 100f);
        public event Action<int> ZoomChanged;

        public void Configure(RectTransform zoomTarget, ScrollRect owner,
            Vector2 resetPosition, float minimum = 1f, float maximum = 5f,
            float step = 0.25f)
        {
            target = zoomTarget;
            scrollRect = owner;
            if (scrollRect != null && scrollRect.content == null)
                scrollRect.content = target;
            resetNormalizedPosition = resetPosition;
            minimumZoom = Mathf.Max(0.1f, minimum);
            maximumZoom = Mathf.Max(minimumZoom, maximum);
            zoomStep = Mathf.Max(0.05f, step);
            ResetView();
        }

        public void ZoomIn() => SetZoom(zoom + zoomStep);
        public void ZoomOut() => SetZoom(zoom - zoomStep);

        public void ResetView()
        {
            zoom = minimumZoom;
            ApplyZoom(true);
        }

        public void SetZoom(float value)
        {
            zoom = Mathf.Clamp(value, minimumZoom, maximumZoom);
            ApplyZoom(false);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (eventData == null || Mathf.Approximately(eventData.scrollDelta.y, 0f)) return;
            SetZoom(zoom + Mathf.Sign(eventData.scrollDelta.y) * zoomStep);
            eventData.Use();
        }

        private void ApplyZoom(bool resetPosition)
        {
            if (target == null) return;
            target.localScale = new Vector3(zoom, zoom, 1f);
            ZoomChanged?.Invoke(ZoomPercent);
            if (scrollRect == null) return;

            scrollRect.StopMovement();
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.horizontal = true;
            scrollRect.vertical = true;
            Canvas.ForceUpdateCanvases();
            if (!resetPosition) return;

            if (Mathf.Approximately(zoom, minimumZoom) &&
                resetNormalizedPosition == new Vector2(0.5f, 0.5f))
                target.anchoredPosition = Vector2.zero;
            scrollRect.normalizedPosition = resetNormalizedPosition;
        }
    }
}
