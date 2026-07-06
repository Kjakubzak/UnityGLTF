#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Editor
{
    /// <summary>
    /// Inspector for <see cref="ViewContextController"/>. In Play mode it exposes a Mode popup
    /// (ThirdPerson / FirstPerson) that drives <see cref="ViewContextController.Mode"/> to preview visibility
    /// live; at edit time it explains that the entries are authored on the hint-set components.
    /// </summary>
    [CustomEditor(typeof(ViewContextController))]
    public class ViewContextControllerEditor : UnityEditor.Editor
    {
        private ViewContextController _controller;

        private void OnEnable()
        {
            _controller = target as ViewContextController;
            if (_controller != null) _controller.OnViewContextChanged += OnModeChanged;
        }

        private void OnDisable()
        {
            if (_controller != null) _controller.OnViewContextChanged -= OnModeChanged;
        }

        // Repaint only when Mode actually changes (e.g. driven by script), not every editor frame.
        private void OnModeChanged(ViewContextController.ViewContext _) => Repaint();

        public override void OnInspectorGUI()
        {
            var controller = (ViewContextController)target;

            if (Application.isPlaying)
            {
                EditorGUI.BeginChangeCheck();
                var mode = (ViewContextController.ViewContext)EditorGUILayout.EnumPopup("Mode", controller.Mode);
                if (EditorGUI.EndChangeCheck())
                    controller.Mode = mode; // setter re-applies visibility and raises OnViewContextChanged

                EditorGUILayout.HelpBox(
                    "Switch Mode to preview first/third-person visibility. Third-person-only renderers toggle off " +
                    "and hinted sub-meshes swap to the invisible material (and restore) as you flip it.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Driven by the NodeVisibilityHintSet / PrimitiveVisibilityHintSet components on this object — " +
                    "author hint entries there. Enter Play mode to preview visibility by switching Mode.",
                    MessageType.Info);
            }
        }
    }
}
#endif
