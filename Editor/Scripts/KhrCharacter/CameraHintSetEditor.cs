#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>
    /// Inspector for <see cref="CameraHintSet"/>: a read-only table of the authored hints (Role / Label /
    /// Node / Target), read from the serialized hints so it shows at edit time.
    /// </summary>
    [CustomEditor(typeof(CameraHintSet))]
    public class CameraHintSetEditor : UnityEditor.Editor
    {
        private bool _showHints = true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(
                "Passive descriptors. Applying a hint to a camera is optional host policy.",
                MessageType.Info);

            var hintsProp = serializedObject.FindProperty("_serializedHints");
            int count = hintsProp != null ? hintsProp.arraySize : 0;

            _showHints = EditorGUILayout.Foldout(_showHints, $"Camera Hints ({count})", true);
            if (count == 0)
            {
                if (_showHints)
                    EditorGUILayout.HelpBox("No camera hints.", MessageType.Info);
                return;
            }
            if (!_showHints) return;

            EditorGUI.indentLevel++;
            using (new EditorGUI.DisabledScope(true)) // read-only table
            {
                for (int i = 0; i < hintsProp.arraySize; i++)
                {
                    var hint = hintsProp.GetArrayElementAtIndex(i);
                    var role = hint.FindPropertyRelative("Role")?.stringValue;
                    var label = hint.FindPropertyRelative("Label")?.stringValue;
                    var node = hint.FindPropertyRelative("Node")?.objectReferenceValue;
                    var tgt = hint.FindPropertyRelative("Target")?.objectReferenceValue;
                    var projection = hint.FindPropertyRelative("Projection")?.objectReferenceValue;

                    EditorGUILayout.LabelField($"#{i}  {StringOrNone(role)} / {StringOrNone(label)}", EditorStyles.miniBoldLabel);
                    EditorGUI.indentLevel++;
                    EditorGUILayout.ObjectField("Node", node, typeof(Transform), true);
                    EditorGUILayout.ObjectField("Target", tgt, typeof(Transform), true);
                    EditorGUILayout.ObjectField("Projection", projection, typeof(Camera), true);
                    EditorGUI.indentLevel--;
                }
            }
            EditorGUI.indentLevel--;
        }

        private static string StringOrNone(string s) => string.IsNullOrEmpty(s) ? "—" : s;
    }
}
#endif
