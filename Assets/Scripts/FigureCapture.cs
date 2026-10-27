using UnityEngine;
using System.IO;

namespace RoadNetworkEditor
{
    /// <summary>
    /// High-resolution screenshot capture utility for generating paper-quality figures.
    /// Supports transparent background output.
    /// </summary>
    public class FigureCapture : MonoBehaviour
    {
        public Camera targetCamera;
        public int width = 3000;
        public int height = 1600;
        public string fileName = "Fig6_ExperimentalScenario.png";
        public bool transparentBackground = false;

        public void Capture()
        {
            RenderTexture rt = new RenderTexture(width, height, 24);
            targetCamera.targetTexture = rt;

            TextureFormat format = transparentBackground
                ? TextureFormat.RGBA32
                : TextureFormat.RGB24;

            Texture2D image = new Texture2D(width, height, format, false);

            Color oldBackground = targetCamera.backgroundColor;
            CameraClearFlags oldClearFlags = targetCamera.clearFlags;

            targetCamera.clearFlags = CameraClearFlags.SolidColor;
            targetCamera.backgroundColor = transparentBackground
                ? new Color(1f, 1f, 1f, 0f)
                : Color.white;

            targetCamera.Render();

            RenderTexture.active = rt;
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();

            byte[] bytes = image.EncodeToPNG();
            string path = Path.Combine(Application.dataPath, fileName);
            File.WriteAllBytes(path, bytes);

            targetCamera.targetTexture = null;
            RenderTexture.active = null;
            DestroyImmediate(rt);
            DestroyImmediate(image);

            targetCamera.backgroundColor = oldBackground;
            targetCamera.clearFlags = oldClearFlags;

            Debug.Log("Saved figure image to: " + path);
        }
    }
}
