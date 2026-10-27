using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RoadNetworkEditor.RuntimeUI
{
    /// <summary>Runtime drag-and-insert behaviour for the road-network layer stack.</summary>
    public sealed class SarLayerDragItem : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private Action select;
        private Func<Vector2, RectTransform, int> resolveDropIndex;
        private Action<int> dropped;
        private Func<bool> canDrag;
        private RectTransform rect;
        private LayoutElement layoutElement;
        private CanvasGroup canvasGroup;
        private Button button;
        private bool dragging;
        private int originalSiblingIndex;

        public void Configure(Action selectLayer,
            Func<Vector2, RectTransform, int> dropIndexResolver, Action<int> onDropped,
            Func<bool> dragAllowed = null, RectTransform dragTarget = null)
        {
            select = selectLayer;
            resolveDropIndex = dropIndexResolver;
            dropped = onDropped;
            canDrag = dragAllowed;
            rect = dragTarget != null ? dragTarget : transform as RectTransform;
            layoutElement = rect != null ? rect.GetComponent<LayoutElement>() : null;
            canvasGroup = rect != null ? rect.GetComponent<CanvasGroup>() : null;
            // Unity components can compare equal to null while still carrying a managed
            // wrapper, so the C# null-coalescing operator is unsafe here.
            if (canvasGroup == null && rect != null)
                canvasGroup = rect.gameObject.AddComponent<CanvasGroup>();
            button = GetComponent<Button>();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (rect == null || (canDrag != null && !canDrag())) return;
            select?.Invoke();
            dragging = true;
            originalSiblingIndex = rect.GetSiblingIndex();
            Vector3 worldPosition = rect.position;
            if (layoutElement != null) layoutElement.ignoreLayout = true;
            rect.SetAsLastSibling();
            rect.position = worldPosition;
            canvasGroup.alpha = 0.88f;
            canvasGroup.blocksRaycasts = false;
            if (button != null) button.interactable = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!dragging || rect == null) return;
            rect.position += (Vector3)eventData.delta;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!dragging) return;
            dragging = false;
            int targetIndex = resolveDropIndex != null ? resolveDropIndex(eventData.position, rect) : 0;
            canvasGroup.alpha = 1f;
            canvasGroup.blocksRaycasts = true;
            if (layoutElement != null) layoutElement.ignoreLayout = false;
            if (button != null) button.interactable = true;
            // Restore the visual stack before the model decides whether the drop is legal.
            // Successful drops rebuild from model order; rejected drops must not leave the
            // dragged row stranded at the end of the layout hierarchy.
            rect.SetSiblingIndex(originalSiblingIndex);
            dropped?.Invoke(targetIndex);
        }
    }
}
