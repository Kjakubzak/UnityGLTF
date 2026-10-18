#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Editor
{
    /// <summary>
    /// Inspector for the default view tags used by the controller's convenience queries, plus the
    /// unsupported-role override.
    /// </summary>
    [CustomEditor(typeof(ViewContextController))]
    public class ViewContextControllerEditor : UnityEditor.Editor
    {
        private ViewContextController _controller;

        private void OnEnable()
        {
            _controller = target as ViewContextController;
            if (_controller != null) _controller.OnDefaultViewTagsChanged += OnTagsChanged;
        }

        private void OnDisable()
        {
            if (_controller != null) _controller.OnDefaultViewTagsChanged -= OnTagsChanged;
        }

        private void OnTagsChanged(IReadOnlyList<string> _) => Repaint();

        public override void OnInspectorGUI()
        {
            var controller = (ViewContextController)target;
            var tags = new List<string>(controller.DefaultViewTags);
            bool changed = false;

            EditorGUILayout.LabelField("Default View Tags", EditorStyles.boldLabel);
            changed |= DrawStandardTag(tags, VisibilityHintExtensionNames.ViewTagFirstPerson);
            changed |= DrawStandardTag(tags, VisibilityHintExtensionNames.ViewTagThirdPerson);

            int removeAt = -1;
            for (int i = 0; i < tags.Count; i++)
            {
                if (IsStandardTag(tags[i])) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    string edited = EditorGUILayout.TextField("Custom Tag", tags[i]);
                    if (!string.IsNullOrEmpty(edited) && edited != tags[i])
                    {
                        tags[i] = edited;
                        changed = true;
                    }
                    if (GUILayout.Button("Remove", GUILayout.Width(70))) removeAt = i;
                }
            }
            if (removeAt >= 0)
            {
                tags.RemoveAt(removeAt);
                changed = true;
            }
            if (GUILayout.Button("Add Custom Tag"))
            {
                tags.Add("ACME_custom_tag");
                changed = true;
            }

            if (changed)
            {
                Undo.RecordObject(controller, "Change View Tags");
                controller.SetDefaultViewTags(tags);
                EditorUtility.SetDirty(controller);
            }

            EditorGUILayout.Space();
            bool renderUnsupported = EditorGUILayout.Toggle(
                new GUIContent("Render Unsupported Roles",
                    "Application override: render content whose custom role no resolver supports."),
                controller.RenderUnsupportedRoles);
            if (renderUnsupported != controller.RenderUnsupportedRoles)
            {
                Undo.RecordObject(controller, "Change Unsupported Role Policy");
                controller.RenderUnsupportedRoles = renderUnsupported;
                EditorUtility.SetDirty(controller);
            }

            EditorGUILayout.HelpBox(
                "This component evaluates visibility predicates without changing renderers, materials, meshes, " +
                "lights, cameras, or authored visibility. An untagged view shows only \"always\" and un-hinted " +
                "content; first_person and third_person content needs the matching tag. A render integration " +
                "queries each view with that view's tags and omits content whose predicate is false.",
                MessageType.Info);
        }

        private static bool DrawStandardTag(List<string> tags, string tag)
        {
            bool present = tags.Contains(tag);
            bool wanted = EditorGUILayout.Toggle(tag, present);
            if (wanted == present) return false;
            if (wanted) tags.Add(tag);
            else tags.Remove(tag);
            return true;
        }

        private static bool IsStandardTag(string tag)
            => tag == VisibilityHintExtensionNames.ViewTagFirstPerson
               || tag == VisibilityHintExtensionNames.ViewTagThirdPerson;
    }
}
#endif
