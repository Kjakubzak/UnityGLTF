#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Editor
{
    /// <summary>
    /// Editable inspector for <see cref="NodeVisibilityHintSet"/>. Lists the authored node-hint entries (whether
    /// imported or hand-authored) and lets you add / edit / remove them: a Transform reference, one or more roles
    /// (each always / first_person / third_person / Custom…), and an optional label. Entries are written
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
                        EditorGUILayout.LabelField($"#{i}  {DisplayName(element)}", EditorStyles.miniBoldLabel);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Remove", GUILayout.Width(70))) removeAt = i;
                    }
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("Node"), new GUIContent("Node"));
                    // The imported node's missing name says nothing about a node picked here.
                    if (EditorGUI.EndChangeCheck())
                        element.FindPropertyRelative("NodeUnnamed").boolValue = false;
                    DrawRoles(element.FindPropertyRelative("Roles"));
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
                "A node hint applies to the node and its entire subtree; its roles are alternatives, and the hints of " +
                "the node and all its ancestors apply together (a descendant cannot restore what an ancestor hides). " +
                "It composes as a logical AND with core KHR_node_visibility. The application decides which roles " +
                "apply in each view; with no role logic nothing is hidden. Custom roles, including differently cased " +
                "standard roles such as \"First_Person\", are ignored unless the application recognizes them. Enable " +
                "the \"KHR Visibility Hints (View Context)\" export plugin to write these hints to glTF.",
                MessageType.Info);
        }

        private static string DisplayName(SerializedProperty element)
            => NodeVisibilityHintSet.GetDisplayName(new NodeVisibilityHintSet.NodeVisibilityEntry
            {
                Node = element.FindPropertyRelative("Node").objectReferenceValue as Transform,
                NodeUnnamed = element.FindPropertyRelative("NodeUnnamed").boolValue,
                Roles = ReadRoles(element.FindPropertyRelative("Roles")),
                Label = element.FindPropertyRelative("Label").stringValue,
            });

        private static void AppendEntry(SerializedProperty entriesProp)
        {
            int idx = entriesProp.arraySize;
            entriesProp.arraySize++; // appends a duplicate of the last element, so reset every field explicitly
            var el = entriesProp.GetArrayElementAtIndex(idx);
            el.FindPropertyRelative("Node").objectReferenceValue = null;
            el.FindPropertyRelative("NodeUnnamed").boolValue = false;
            WriteRoles(el.FindPropertyRelative("Roles"), VisibilityHintExtensionNames.RoleAlways);
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

        internal static string[] ReadRoles(SerializedProperty rolesProp)
        {
            var roles = new string[rolesProp.arraySize];
            for (int i = 0; i < roles.Length; i++) roles[i] = rolesProp.GetArrayElementAtIndex(i).stringValue;
            return roles;
        }

        internal static void WriteRoles(SerializedProperty rolesProp, params string[] roles)
        {
            rolesProp.arraySize = roles.Length;
            for (int i = 0; i < roles.Length; i++) rolesProp.GetArrayElementAtIndex(i).stringValue = roles[i];
        }

        /// <summary>
        /// Draws the editable role list of a hint: one role selector per role, a remove button for each, and an
        /// "Add Role" button. Shared with <see cref="PrimitiveVisibilityHintSetEditor"/>.
        /// </summary>
        internal static void DrawRoles(SerializedProperty rolesProp)
        {
            EditorGUILayout.LabelField("Roles (any one allows a view)");
            EditorGUI.indentLevel++;
            int removeAt = -1;
            for (int i = 0; i < rolesProp.arraySize; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                        DrawRolePopup(rolesProp.GetArrayElementAtIndex(i));
                    if (GUILayout.Button("Remove", GUILayout.Width(70))) removeAt = i;
                }
            }
            if (removeAt >= 0) rolesProp.DeleteArrayElementAtIndex(removeAt);
            if (rolesProp.arraySize == 0)
                EditorGUILayout.HelpBox("A hint needs at least one role; export skips a hint without one.",
                    MessageType.Warning);
            if (GUILayout.Button("Add Role"))
            {
                int index = rolesProp.arraySize;
                rolesProp.arraySize++;
                rolesProp.GetArrayElementAtIndex(index).stringValue = VisibilityHintExtensionNames.RoleThirdPerson;
            }
            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Draws a role selector for a string property: a popup of the three standard roles plus a "Custom…"
        /// option that reveals a free-text field (the spec allows an open role vocabulary).
        /// </summary>
        private static void DrawRolePopup(SerializedProperty roleProp)
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
