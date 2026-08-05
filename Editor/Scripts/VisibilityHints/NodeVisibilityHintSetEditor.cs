#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Editor
{
    /// <summary>
    /// Editable inspector for <see cref="NodeVisibilityHintSet"/>. Lists the authored node-hint entries (whether
    /// imported or hand-authored) and lets you add / edit / remove them: a Transform reference, a role popup
    /// (always / first_person / third_person / Custom…), and an optional label. Entries are written
    /// through the serialized backing list, so edits are undoable and never trigger runtime resolution.
    /// </summary>
    [CustomEditor(typeof(NodeVisibilityHintSet))]
    public class NodeVisibilityHintSetEditor : UnityEditor.Editor
    {
        // Standard role vocabulary, index-aligned with the first three popup labels below.
        private static readonly string[] StandardRoles =
        {
            VisibilityHintExtensionNames.RoleAlways,
            VisibilityHintExtensionNames.RoleFirstPerson,
            VisibilityHintExtensionNames.RoleThirdPerson,
        };
        private static readonly string[] RolePopupLabels = { "always", "first_person", "third_person", "Custom…" };
        private const int CustomRoleIndex = 3;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var entriesProp = serializedObject.FindProperty("_entries");

            EditorGUILayout.LabelField($"Node Visibility Hints ({entriesProp.arraySize})", EditorStyles.boldLabel);

            int removeAt = -1;
            for (int i = 0; i < entriesProp.arraySize; i++)
            {
                var element = entriesProp.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"#{i}", GUILayout.Width(30));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Remove", GUILayout.Width(70))) removeAt = i;
                    }
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("Node"), new GUIContent("Node"));
                    DrawRolePopup(element.FindPropertyRelative("Role"));
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("Label"), new GUIContent("Label"));
                }
            }

            if (removeAt >= 0)
                entriesProp.DeleteArrayElementAtIndex(removeAt);

            if (GUILayout.Button("Add Node Hint"))
                AppendEntry(entriesProp);

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "A node hint applies to the node and its entire subtree (a descendant hint overrides an ancestor " +
                "for its subtree). It composes as a logical AND with core KHR_node_visibility: an inactive node " +
                "never renders regardless of the hint. Enable the \"KHR Visibility Hints (View Context)\" export " +
                "plugin to write these hints to glTF.",
                MessageType.Info);
        }

        private static void AppendEntry(SerializedProperty entriesProp)
        {
            int idx = entriesProp.arraySize;
            entriesProp.arraySize++; // appends a duplicate of the last element, so reset every field explicitly
            var el = entriesProp.GetArrayElementAtIndex(idx);
            el.FindPropertyRelative("Node").objectReferenceValue = null;
            el.FindPropertyRelative("Role").stringValue = VisibilityHintExtensionNames.RoleAlways;
            el.FindPropertyRelative("Label").stringValue = string.Empty;
            ResetPayload(el);
        }

        internal static void ResetPayload(SerializedProperty element)
        {
            element.FindPropertyRelative("ExtensionsJson").stringValue = string.Empty;
            element.FindPropertyRelative("ExtrasJson").stringValue = string.Empty;
            element.FindPropertyRelative("AdditionalPropertiesJson").stringValue = string.Empty;
            element.FindPropertyRelative("RequiredCompanionExtensions").arraySize = 0;
        }

        /// <summary>
        /// Draws a role selector for a string property: a popup of the three standard roles plus a "Custom…"
        /// option that reveals a free-text field (the spec allows an open role vocabulary). Shared with
        /// <see cref="PrimitiveVisibilityHintSetEditor"/>.
        /// </summary>
        internal static void DrawRolePopup(SerializedProperty roleProp)
        {
            string current = roleProp.stringValue ?? string.Empty;
            int standardIndex = System.Array.IndexOf(StandardRoles, current);
            bool isCustom = standardIndex < 0;
            int shownIndex = isCustom ? CustomRoleIndex : standardIndex;

            int newIndex = EditorGUILayout.Popup("Role", shownIndex, RolePopupLabels);
            if (newIndex != shownIndex)
            {
                if (newIndex == CustomRoleIndex)
                {
                    roleProp.stringValue = string.Empty; // begin a fresh custom value
                    isCustom = true;
                }
                else
                {
                    roleProp.stringValue = StandardRoles[newIndex];
                    isCustom = false;
                }
            }

            if (isCustom)
            {
                EditorGUI.indentLevel++;
                roleProp.stringValue = EditorGUILayout.TextField("Custom Role", roleProp.stringValue);
                EditorGUI.indentLevel--;
            }
        }
    }
}
#endif
