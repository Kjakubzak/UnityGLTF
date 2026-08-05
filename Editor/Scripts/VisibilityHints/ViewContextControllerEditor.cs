#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Editor
{
    /// <summary>Inspector for the host-selected convenience context used by predicate queries.</summary>
    [CustomEditor(typeof(ViewContextController))]
    public class ViewContextControllerEditor : UnityEditor.Editor
    {
        private ViewContextController _controller;

        private void OnEnable()
        {
            _controller = target as ViewContextController;
            if (_controller != null) _controller.OnViewContextChanged += OnContextChanged;
        }

        private void OnDisable()
        {
            if (_controller != null) _controller.OnViewContextChanged -= OnContextChanged;
        }

        private void OnContextChanged(string _) => Repaint();

        public override void OnInspectorGUI()
        {
            var controller = (ViewContextController)target;
            bool hasContext = EditorGUILayout.Toggle("Supply Context", controller.HasActiveContext);
            if (!hasContext)
            {
                if (controller.HasActiveContext) controller.ClearActiveContext();
            }
            else
            {
                string context = EditorGUILayout.TextField("Active Context", controller.ActiveContext ?? "first_person");
                if (!controller.HasActiveContext || context != controller.ActiveContext)
                    controller.SetActiveContext(context);
            }

            EditorGUILayout.HelpBox(
                "This component evaluates visibility predicates without changing renderers, materials, meshes, " +
                "cameras, or authored visibility. A render integration must query the desired context for each " +
                "view and omit content whose predicate is false.",
                MessageType.Info);
        }
    }
}
#endif
