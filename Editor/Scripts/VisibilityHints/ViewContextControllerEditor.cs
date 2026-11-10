#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Editor
{
    /// <summary>
    /// Inspector for the default role logic used by the controller's convenience queries: which roles the
    /// application has logic for, and which of them apply in the default view.
    /// </summary>
    [CustomEditor(typeof(ViewContextController))]
    public class ViewContextControllerEditor : UnityEditor.Editor
    {
        private static readonly string[] Presets = { "No logic", "First-person view", "Other view (third person)" };

        private ViewContextController _controller;

        private void OnEnable()
        {
            _controller = target as ViewContextController;
            if (_controller != null) _controller.OnDefaultRolesChanged += OnRolesChanged;
        }

        private void OnDisable()
        {
            if (_controller != null) _controller.OnDefaultRolesChanged -= OnRolesChanged;
        }

        private void OnRolesChanged(VisibilityRoleContext _) => Repaint();

        public override void OnInspectorGUI()
        {
            var controller = (ViewContextController)target;
            var current = controller.DefaultRoles;
            var applied = new List<string>(current.AppliedRoles);
            var withLogic = new List<string>();
            foreach (var role in current.RolesWithLogic)
                if (!applied.Contains(role)) withLogic.Add(role);
            bool changed = false;

            EditorGUILayout.LabelField("Default Role Logic", EditorStyles.boldLabel);
            int preset = EditorGUILayout.Popup("Preset", -1, Presets);
            if (preset >= 0)
            {
                var context = preset == 0 ? VisibilityRoleContext.NoLogic
                    : preset == 1 ? FirstPersonRolePolicy.FirstPersonView
                    : FirstPersonRolePolicy.OtherView;
                applied = new List<string>(context.AppliedRoles);
                withLogic.Clear();
                foreach (var role in context.RolesWithLogic)
                    if (!applied.Contains(role)) withLogic.Add(role);
                changed = true;
            }

            changed |= DrawStandardRole(applied, withLogic, VisibilityHintExtensionNames.RoleFirstPerson);
            changed |= DrawStandardRole(applied, withLogic, VisibilityHintExtensionNames.RoleThirdPerson);
            changed |= DrawCustomRoles("Applied Custom Roles", applied);
            changed |= DrawCustomRoles("Recognized Custom Roles (not applied)", withLogic);

            if (changed)
            {
                Undo.RecordObject(controller, "Change Visibility Role Logic");
                controller.SetDefaultRoles(new VisibilityRoleContext(applied, withLogic));
                EditorUtility.SetDirty(controller);
            }

            EditorGUILayout.HelpBox(
                "This component evaluates visibility predicates without changing renderers, materials, meshes, " +
                "lights, cameras, or authored visibility. \"always\" applies everywhere. A standard role the " +
                "application has no logic for applies in every view, so with no logic nothing is hidden. Custom " +
                "roles are ignored unless listed here. A render integration queries each view with that view's " +
                "role logic and omits content whose predicate is false.",
                MessageType.Info);
        }

        // Per standard role: no logic (applies everywhere), logic and applies, or logic and does not apply.
        private static bool DrawStandardRole(List<string> applied, List<string> withLogic, string role)
        {
            int state = applied.Contains(role) ? 1 : withLogic.Contains(role) ? 2 : 0;
            int next = EditorGUILayout.Popup(role, state,
                new[] { "No logic (applies everywhere)", "Applies in this view", "Does not apply in this view" });
            if (next == state) return false;
            applied.Remove(role);
            withLogic.Remove(role);
            if (next == 1) applied.Add(role);
            else if (next == 2) withLogic.Add(role);
            return true;
        }

        private static bool DrawCustomRoles(string title, List<string> roles)
        {
            bool changed = false;
            EditorGUILayout.LabelField(title);
            EditorGUI.indentLevel++;
            int removeAt = -1;
            for (int i = 0; i < roles.Count; i++)
            {
                if (VisibilityHintExtensionNames.IsStandardRole(roles[i])) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    string edited = EditorGUILayout.TextField("Custom Role", roles[i]);
                    if (!string.IsNullOrEmpty(edited) && edited != roles[i])
                    {
                        roles[i] = edited;
                        changed = true;
                    }
                    if (GUILayout.Button("Remove", GUILayout.Width(70))) removeAt = i;
                }
            }
            if (removeAt >= 0)
            {
                roles.RemoveAt(removeAt);
                changed = true;
            }
            if (GUILayout.Button("Add Custom Role"))
            {
                roles.Add("ACME_custom_role");
                changed = true;
            }
            EditorGUI.indentLevel--;
            return changed;
        }
    }
}
#endif
