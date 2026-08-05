#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>Read-only inspector for imported passive look-at target markers.</summary>
    [CustomEditor(typeof(LookAtTargetSet))]
    public class LookAtTargetSetEditor : UnityEditor.Editor
    {
        private bool _showTargets = true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(
                "Passive markers. Their live global positions are exposed without selecting or driving a consumer.",
                MessageType.Info);
            var targets = serializedObject.FindProperty("_serializedTargets");
            var required = serializedObject.FindProperty("_requiredOnImport");
            int count = targets != null ? targets.arraySize : 0;
            if (required != null && required.boolValue)
                EditorGUILayout.LabelField("Imported declaration", "Required");
            _showTargets = EditorGUILayout.Foldout(_showTargets, $"Look-at Targets ({count})", true);
            if (!_showTargets) return;
            if (count == 0)
            {
                EditorGUILayout.LabelField("(none)");
                return;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < count; i++)
                {
                    var entry = targets.GetArrayElementAtIndex(i);
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
