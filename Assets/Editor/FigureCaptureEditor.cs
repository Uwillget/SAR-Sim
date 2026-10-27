using UnityEditor;
using UnityEngine;
using RoadNetworkEditor;

namespace RoadNetworkEditor.EditorScripts
{
    [CustomEditor(typeof(FigureCapture))]
    public class FigureCaptureEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            FigureCapture capture = (FigureCapture)target;

            if (GUILayout.Button("Capture Figure Image"))
            {
                capture.Capture();
            }
        }
    }
}
