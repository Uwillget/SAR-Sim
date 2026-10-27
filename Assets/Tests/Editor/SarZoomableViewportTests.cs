using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using RoadNetworkEditor.RuntimeUI;

namespace RoadNetworkEditor.Tests.Editor
{
    public sealed class SarZoomableViewportTests
    {
        [Test]
        public void Zoom_IsClampedAndResetRestoresTargetScale()
        {
            var root = new GameObject("ZoomRoot", typeof(RectTransform), typeof(ScrollRect),
                typeof(SarZoomableViewport));
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(root.transform, false);
            try
            {
                var scroll = root.GetComponent<ScrollRect>();
                var zoom = root.GetComponent<SarZoomableViewport>();
                zoom.Configure(content.GetComponent<RectTransform>(), scroll,
                    new Vector2(0.5f, 0.5f), 1f, 3f, 0.5f);

                zoom.SetZoom(20f);
                Assert.That(zoom.Zoom, Is.EqualTo(3f));
                Assert.That(content.transform.localScale.x, Is.EqualTo(3f));

                zoom.ResetView();
                Assert.That(zoom.ZoomPercent, Is.EqualTo(100));
                Assert.That(content.transform.localScale, Is.EqualTo(Vector3.one));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
