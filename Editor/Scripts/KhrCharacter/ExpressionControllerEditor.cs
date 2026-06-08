#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>
    /// Inspector for <see cref="ExpressionController"/>. At edit time it lists the baked expressions and
    /// vocabulary sets (read from the serialized set). In Play mode it shows a live control per expression —
    /// a slider, or a toggle for binary expressions — wired to GetWeight/SetWeight, plus a Reset All button.
    /// </summary>
    [CustomEditor(typeof(ExpressionController))]
    public class ExpressionControllerEditor : UnityEditor.Editor
    {
        private bool _showExpressions = true;
        private bool _showVocabularySets = true;

        public override void OnInspectorGUI()
        {
            var controller = (ExpressionController)target;

            if (Application.isPlaying)
                DrawPlayMode(controller);
            else
                DrawEditMode();
        }

        private void DrawPlayMode(ExpressionController controller)
        {
            EditorGUILayout.LabelField($"Expressions ({controller.Count})", EditorStyles.boldLabel);
            if (controller.Count == 0)
            {
                EditorGUILayout.HelpBox("No expressions initialized.", MessageType.Info);
                return;
            }

            if (GUILayout.Button("Reset All")) controller.ResetAll();

            // Index-based loop, not foreach: SetWeight below reassigns an element of the controller's handle list,
            // which bumps the List version and would throw "Collection was modified" mid-enumeration.
            var handles = controller.Expressions;
            for (int i = 0; i < handles.Count; i++)
            {
                var handle = handles[i];
                if (string.IsNullOrEmpty(handle.Name)) continue;
                float current = controller.GetWeight(handle.Name);
                float next = handle.IsBinary
                    ? (EditorGUILayout.Toggle(handle.Name, current >= 0.5f) ? 1f : 0f)
                    : EditorGUILayout.Slider(handle.Name, current, 0f, 1f);
                if (!Mathf.Approximately(next, current))
                    controller.SetWeight(handle.Name, next);
            }

            Repaint();
        }

        private void DrawEditMode()
        {
            serializedObject.Update(); // refresh the cached SerializedProperties (no DrawDefaultInspector here)
            var setProp = serializedObject.FindProperty("_serializedSet");
            var exprProp = setProp?.FindPropertyRelative("Expressions");

            int exprCount = exprProp != null ? exprProp.arraySize : 0;
            _showExpressions = EditorGUILayout.Foldout(_showExpressions, $"Baked Expressions ({exprCount})", true);
            if (exprCount == 0)
            {
                if (_showExpressions)
                    EditorGUILayout.HelpBox("No baked expressions. Enter Play mode to drive expressions.", MessageType.Info);
                return;
            }

            if (_showExpressions)
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < exprProp.arraySize; i++)
                {
                    var element = exprProp.GetArrayElementAtIndex(i);
                    var name = element.FindPropertyRelative("Name")?.stringValue;
                    EditorGUILayout.LabelField("•", string.IsNullOrEmpty(name) ? $"(expression {i})" : name);
                }
                EditorGUI.indentLevel--;
            }

            var mapProp = setProp.FindPropertyRelative("MappingSets");
            if (mapProp != null && mapProp.arraySize > 0)
            {
                EditorGUILayout.Space();
                _showVocabularySets = EditorGUILayout.Foldout(_showVocabularySets, $"Vocabulary Sets ({mapProp.arraySize})", true);
                if (_showVocabularySets)
                {
                    EditorGUI.indentLevel++;
                    for (int i = 0; i < mapProp.arraySize; i++)
                    {
                        var element = mapProp.GetArrayElementAtIndex(i);
                        var name = element.FindPropertyRelative("SetName")?.stringValue;
                        EditorGUILayout.LabelField("•", string.IsNullOrEmpty(name) ? $"(set {i})" : name);
                    }
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Enter Play mode to drive expression weights.", MessageType.Info);
        }
    }
}
#endif
