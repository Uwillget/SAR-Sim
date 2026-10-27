using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RoadNetworkEditor.RuntimeUI
{
    public sealed class SarUiButtonMotion : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        private Image image;
        private RectTransform rect;
        private Color normal;
        private Color hover;
        private Color pressed;
        private Color targetColor;
        private Vector3 targetScale = Vector3.one;

        public void Configure(Color baseColor)
        {
            image = GetComponent<Image>();
            rect = GetComponent<RectTransform>();
            normal = baseColor;
            hover = Color.Lerp(baseColor, Color.white, 0.10f);
            pressed = Color.Lerp(baseColor, Color.black, 0.08f);
            targetColor = normal;
        }

        private void Awake()
        {
            image = GetComponent<Image>();
            rect = GetComponent<RectTransform>();
            if (image != null && normal == default)
                Configure(image.color);
        }

        private void Update()
        {
            if (image != null) image.color = Color.Lerp(image.color, targetColor, 16f * Time.unscaledDeltaTime);
            if (rect != null) rect.localScale = Vector3.Lerp(rect.localScale, targetScale, 18f * Time.unscaledDeltaTime);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            targetColor = hover;
            targetScale = Vector3.one * 1.018f;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            targetColor = normal;
            targetScale = Vector3.one;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            targetColor = pressed;
            targetScale = Vector3.one * 0.975f;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            targetColor = eventData.pointerEnter == gameObject ? hover : normal;
            targetScale = eventData.pointerEnter == gameObject ? Vector3.one * 1.018f : Vector3.one;
        }
    }
}
