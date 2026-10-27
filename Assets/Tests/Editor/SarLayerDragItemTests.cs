using NUnit.Framework;
using RoadNetworkEditor.RuntimeUI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RoadNetworkEditor.Tests.Editor
{
    public sealed class SarLayerDragItemTests
    {
        [Test]
        public void RejectedOrNoOpDrop_RestoresOriginalVisualSibling()
        {
            var parent = new GameObject("Layer rows", typeof(RectTransform));
            var first = new GameObject("First", typeof(RectTransform));
            var dragged = new GameObject("Dragged", typeof(RectTransform), typeof(Image), typeof(Button),
                typeof(LayoutElement), typeof(SarLayerDragItem));
            var last = new GameObject("Last", typeof(RectTransform));
            try
            {
                first.transform.SetParent(parent.transform, false);
                dragged.transform.SetParent(parent.transform, false);
                last.transform.SetParent(parent.transform, false);
                RectTransform receivedRow = null;
                int receivedIndex = -1;
                dragged.GetComponent<SarLayerDragItem>().Configure(
                    () => { },
                    (_, row) => { receivedRow = row; return 1; },
                    index => receivedIndex = index,
                    () => true,
                    (RectTransform)dragged.transform);

                var pointer = new PointerEventData(null) { position = new Vector2(100f, 100f) };
                dragged.GetComponent<SarLayerDragItem>().OnBeginDrag(pointer);
                Assert.That(dragged.transform.GetSiblingIndex(), Is.EqualTo(2));
                dragged.GetComponent<SarLayerDragItem>().OnEndDrag(pointer);

                Assert.That(dragged.transform.GetSiblingIndex(), Is.EqualTo(1));
                Assert.That(receivedRow, Is.SameAs(dragged.transform));
                Assert.That(receivedIndex, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }
    }
}
