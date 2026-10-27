using UnityEditor;
using UnityEngine;
using RoadNetworkEditor;

namespace RoadNetworkEditor.EditorScripts
{
    [CustomEditor(typeof(CityModelController))]
    public class CityModelControllerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            CityModelController controller = (CityModelController)target;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Model Processing", EditorStyles.boldLabel);

            if (GUILayout.Button("Process Building Model", GUILayout.Height(30)))
            {
                controller.ProcessBuildingModel();
                EditorUtility.SetDirty(controller);
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                "Click to execute the following steps:\n" +
                "  1. Add MeshCollider (Convex) to all child buildings\n" +
                "  2. Align all building bases to Y = 0",
                MessageType.Info);
        }
    }
}
