using UnityEngine;
using UnityEngine.UI;

namespace RoadNetworkEditor.RuntimeUI
{
    /// <summary>
    /// Keeps Unity's generated Dropdown List attached to its source field.  The
    /// stock Dropdown may flip or offset a runtime-created template after nested
    /// canvases and scaled scroll views have performed their layout pass.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RuntimeDropdownAnchor : MonoBehaviour
    {
        private Dropdown dropdown;
        private RectTransform source;
        private float maximumHeight = 270f;

        public void Configure(Dropdown target, float maxHeight = 270f)
        {
            dropdown = target;
            source = target != null ? target.transform as RectTransform : null;
            maximumHeight = Mathf.Max(44f, maxHeight);
        }

        private void LateUpdate()
        {
            AlignExpandedList();
        }

        public bool AlignExpandedList()
        {
            if (dropdown == null || source == null) return false;
            RectTransform list = source.Find("Dropdown List") as RectTransform;
            if (list == null || !list.gameObject.activeInHierarchy) return false;

            float itemHeight = 38f;
            float desiredHeight = Mathf.Min(maximumHeight,
                Mathf.Max(44f, dropdown.options.Count * itemHeight + 4f));
            Canvas canvas = dropdown.GetComponentInParent<Canvas>();
            Camera eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
            Vector3[] corners = new Vector3[4];
            source.GetWorldCorners(corners);
            float bottom = RectTransformUtility.WorldToScreenPoint(eventCamera, corners[0]).y;
            float top = RectTransformUtility.WorldToScreenPoint(eventCamera, corners[1]).y;
            float scale = canvas != null ? Mathf.Max(0.01f, canvas.scaleFactor) : 1f;
            float requiredPixels = desiredHeight * scale;
            bool openAbove = bottom < requiredPixels && Screen.height - top > bottom;

            list.anchorMin = openAbove ? new Vector2(0f, 1f) : Vector2.zero;
            list.anchorMax = openAbove ? Vector2.one : new Vector2(1f, 0f);
            list.pivot = openAbove ? new Vector2(0.5f, 0f) : new Vector2(0.5f, 1f);
            list.anchoredPosition = Vector2.zero;
            list.sizeDelta = new Vector2(0f, desiredHeight);
            return true;
        }
    }
}
