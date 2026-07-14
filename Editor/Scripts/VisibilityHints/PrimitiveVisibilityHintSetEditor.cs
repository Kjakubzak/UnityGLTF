#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Editor
{
    /// <summary>
    /// Editable inspector for <see cref="PrimitiveVisibilityHintSet"/>. Lists per-primitive hints (shared Mesh +
    /// sub-mesh index, role, optional label) and lets you add / edit / remove them. A "Collect child renderers"
    /// button (in the style of <c>MaterialVariants</c>) scans the subtree and appends any missing
    /// <c>(mesh, sub-mesh)</c> slots defaulting to role <c>always</c> for you to set. Entries are written through
    /// the serialized backing list, so edits are undoable and never trigger runtime resolution.
    /// </summary>
    [CustomEditor(typeof(PrimitiveVisibilityHintSet))]
    public class PrimitiveVisibilityHintSetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var entriesProp = serializedObject.FindProperty("_entries");

            EditorGUILayout.LabelField($"Primitive Visibility Hints ({entriesProp.arraySize})", EditorStyles.boldLabel);

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
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("Mesh"), new GUIContent("Mesh"));
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("SubMesh"), new GUIContent("Sub Mesh"));
                    NodeVisibilityHintSetEditor.DrawRolePopup(element.FindPropertyRelative("Role"));
                    EditorGUILayout.PropertyField(element.FindPropertyRelative("Label"), new GUIContent("Label"));
                }
            }

            if (removeAt >= 0)
                entriesProp.DeleteArrayElementAtIndex(removeAt);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Primitive Hint")) AppendEntry(entriesProp);
                if (GUILayout.Button("Collect child renderers"))
                    CollectChildRenderers((PrimitiveVisibilityHintSet)target, entriesProp);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "A primitive hint targets a shared Mesh + sub-mesh index, so it applies to every renderer that uses " +
                "that mesh. \"Collect child renderers\" appends any missing (mesh, sub-mesh) slots as role \"always\"; " +
                "set the ones you want to first_person / third_person. Enable the \"KHR Visibility Hints " +
                "(View Context)\" export plugin to write these hints to glTF.",
                MessageType.Info);
        }

        private static void AppendEntry(SerializedProperty entriesProp)
        {
            int idx = entriesProp.arraySize;
            entriesProp.arraySize++; // appends a duplicate of the last element, so reset every field explicitly
            var el = entriesProp.GetArrayElementAtIndex(idx);
            el.FindPropertyRelative("Mesh").objectReferenceValue = null;
            el.FindPropertyRelative("SubMesh").intValue = 0;
            el.FindPropertyRelative("Role").stringValue = VisibilityHintExtensionNames.RoleAlways;
            el.FindPropertyRelative("Label").stringValue = string.Empty;
        }

        // Append every (mesh, sub-mesh) slot reachable in the subtree that isn't already present, defaulting to
        // role "always" (a no-op the user then edits). Mirrors MaterialVariants' Collect, but writes through the
        // serialized list so the operation is undoable.
        private static void CollectChildRenderers(PrimitiveVisibilityHintSet target, SerializedProperty entriesProp)
        {
            var existing = new HashSet<(Mesh mesh, int sub)>();
            for (int i = 0; i < entriesProp.arraySize; i++)
            {
                var el = entriesProp.GetArrayElementAtIndex(i);
                var m = el.FindPropertyRelative("Mesh").objectReferenceValue as Mesh;
                if (m != null) existing.Add((m, el.FindPropertyRelative("SubMesh").intValue));
            }

            foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = GetRendererMesh(renderer);
                if (mesh == null) continue;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (!existing.Add((mesh, sub))) continue; // dedupe against existing + already-collected slots
                    int idx = entriesProp.arraySize;
                    entriesProp.arraySize++;
                    var el = entriesProp.GetArrayElementAtIndex(idx);
                    el.FindPropertyRelative("Mesh").objectReferenceValue = mesh;
                    el.FindPropertyRelative("SubMesh").intValue = sub;
                    el.FindPropertyRelative("Role").stringValue = VisibilityHintExtensionNames.RoleAlways;
                    el.FindPropertyRelative("Label").stringValue = string.Empty;
                }
            }
        }

        // The mesh a renderer draws: SkinnedMeshRenderer carries it directly; MeshRenderer reads its MeshFilter.
        private static Mesh GetRendererMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
#endif
