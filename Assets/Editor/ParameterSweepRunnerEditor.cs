using UnityEngine;
using UnityEditor;
using System;
using System.Linq;

namespace RoadNetworkEditor.EditorScripts
{
    [CustomEditor(typeof(ParameterSweepRunner))]
    public class ParameterSweepRunnerEditor : Editor
    {
        private ParameterSweepRunner runner;
        private string[] configFieldNames;

        void OnEnable()
        {
            runner = (ParameterSweepRunner)target;
            configFieldNames = ParameterSweepRunner.SweepableConfigFields.ToArray();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("batchDuration"));
            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Independent Variables", EditorStyles.boldLabel);
            var varsProp = serializedObject.FindProperty("variables");

            for (int i = 0; i < varsProp.arraySize; i++)
            {
                var el = varsProp.GetArrayElementAtIndex(i);
                var en = el.FindPropertyRelative("enabled");
                var fn = el.FindPropertyRelative("configField");
                var sv = el.FindPropertyRelative("startValue");
                var sp = el.FindPropertyRelative("step");
                var cn = el.FindPropertyRelative("count");

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(en, GUIContent.none, GUILayout.Width(20));
                EditorGUILayout.LabelField($"Var {i + 1}", EditorStyles.boldLabel);
                if (GUILayout.Button("×", GUILayout.Width(25))) { varsProp.DeleteArrayElementAtIndex(i); break; }
                EditorGUILayout.EndHorizontal();
                if (en.boolValue)
                {
                    int ix = Array.IndexOf(configFieldNames, fn.stringValue); if (ix < 0) ix = 0;
                    ix = EditorGUILayout.Popup("Field", ix, configFieldNames);
                    fn.stringValue = configFieldNames[ix];
                    EditorGUILayout.PropertyField(sv, new GUIContent("Start"));
                    EditorGUILayout.PropertyField(sp, new GUIContent("Step"));
                    EditorGUILayout.PropertyField(cn, new GUIContent("Count"));
                }
                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("+ Add Variable"))
            {
                varsProp.arraySize++;
                var nv = varsProp.GetArrayElementAtIndex(varsProp.arraySize - 1);
                nv.FindPropertyRelative("enabled").boolValue = true;
                nv.FindPropertyRelative("configField").stringValue = "signalPropagationDelay";
                nv.FindPropertyRelative("startValue").floatValue = 0f;
                nv.FindPropertyRelative("step").floatValue = 0.2f;
                nv.FindPropertyRelative("count").intValue = 5;
            }

            serializedObject.ApplyModifiedProperties();
            runner.ResolveGrid();

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Grid", EditorStyles.boldLabel);
            for (int i = 0; i < runner.variables.Count; i++)
            {
                var v = runner.variables[i];
                if (!v.enabled) continue;
                EditorGUILayout.LabelField($"  {v.configField}: {v.startValue}→{v.startValue + (v.count - 1) * v.step} (step={v.step}, n={v.count})");
            }
            EditorGUILayout.LabelField($"Total: {runner.totalBatches} batches (~{(runner.totalBatches * runner.batchDuration / 60f):F0} min)");

            EditorGUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !runner.isRunning && runner.totalBatches > 0;
            if (GUILayout.Button("START SWEEP", GUILayout.Height(40)))
            {
                if (EditorApplication.isPlaying) runner.StartSweep();
                else ParameterSweepPlayModeBridge.RequestStart();
            }
            GUI.enabled = runner.isRunning;
            if (GUILayout.Button("STOP", GUILayout.Height(40))) { runner.StopSweepInternal(); }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (runner.isRunning)
            {
                Repaint();
                EditorGUILayout.LabelField($"Running: Batch {runner.currentBatch}/{runner.totalBatches}  Phase={runner.batchPhase}");
            }
            else if (!string.IsNullOrEmpty(runner.lastError))
                EditorGUILayout.HelpBox(runner.lastError, MessageType.Error);

            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("outputPath"));
            serializedObject.ApplyModifiedProperties();
        }
    }

    [InitializeOnLoad]
    internal static class ParameterSweepPlayModeBridge
    {
        private const string PendingKey = "SAR.ParameterSweep.StartOnPlay";

        static ParameterSweepPlayModeBridge()
        {
            EditorApplication.playModeStateChanged -= HandlePlayModeChanged;
            EditorApplication.playModeStateChanged += HandlePlayModeChanged;
        }

        public static void RequestStart()
        {
            SessionState.SetBool(PendingKey, true);
            EditorApplication.isPlaying = true;
        }

        private static void HandlePlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(PendingKey, false)) return;
            SessionState.EraseBool(PendingKey);
            ParameterSweepRunner runner = UnityEngine.Object.FindObjectOfType<ParameterSweepRunner>();
            if (runner == null) Debug.LogError("[Sweep] No ParameterSweepRunner exists in the active scene.");
            else runner.StartSweep();
        }
    }
}
