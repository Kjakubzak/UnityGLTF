#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>
    /// Inspector for <see cref="GazeSolver"/>: the default tunables (mode, weight, clamps, look-expression
    /// names, authored targets) plus Play-mode controls — a "Look at Main Camera" / "Stop" pair and a live
    /// weight slider.
    /// </summary>
    [CustomEditor(typeof(GazeSolver))]
    public class GazeSolverEditor : UnityEditor.Editor
    {
        private bool _showTargets = true;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var gaze = (GazeSolver)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Gaze Controls", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                DrawSerializedTargets();
                EditorGUILayout.HelpBox("Enter Play mode to drive gaze.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Look at Main Camera")) gaze.Mode = GazeSolver.LookAtMode.Camera;
                if (GUILayout.Button("Stop")) gaze.Mode = GazeSolver.LookAtMode.None;
            }
            gaze.Weight = EditorGUILayout.Slider("Weight", gaze.Weight, 0f, 1f);
            EditorGUILayout.LabelField("Active Mode", gaze.Mode.ToString());

            var targets = gaze.AuthoredTargets;
            if (targets != null && targets.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"Authored Targets ({targets.Count})", EditorStyles.boldLabel);
                foreach (var t in targets)
                    EditorGUILayout.LabelField("•", t?.Node != null ? $"{t.Node.name} ({t.Hint})" : $"(unbound) ({t?.Hint})");
            }

            Repaint();
        }

        // Edit-time read-only view of the baked look-at targets. They persist in the [HideInInspector] mirror
        // _serializedTargets, so DrawDefaultInspector won't show them; surface them here to match the other KHR
        // Character inspectors. In Play mode the live AuthoredTargets readout is shown instead.
        private void DrawSerializedTargets()
        {
            var prop = serializedObject.FindProperty("_serializedTargets");
            int count = prop != null ? prop.arraySize : 0;
            _showTargets = EditorGUILayout.Foldout(_showTargets, $"Authored Targets ({count})", true);
            if (!_showTargets) return;
            if (count == 0)
            {
                EditorGUILayout.LabelField("(none)");
                return;
            }
            using (new EditorGUI.DisabledScope(true)) // read-only
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < count; i++)
                {
                    var entry = prop.GetArrayElementAtIndex(i);
                    var node = entry.FindPropertyRelative("Node")?.objectReferenceValue;
                    var hint = entry.FindPropertyRelative("Hint")?.stringValue;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.ObjectField(node, typeof(Transform), true);
                        EditorGUILayout.LabelField(string.IsNullOrEmpty(hint) ? "—" : hint, GUILayout.Width(120f));
                    }
                }
                EditorGUI.indentLevel--;
            }
        }
    }
}
#endif
