#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>
    /// Inspector for <see cref="EyeAimConstraint"/>: the default tunables (mode, weight, clamps, reference frame,
    /// eye bones) plus Play-mode controls — a "Look at Main Camera" / "Stop" pair and a live weight slider.
    /// Mirrors <see cref="GazeSolverEditor"/>; this is a non-spec engine convenience, not part of KHR_character.
    /// </summary>
    [CustomEditor(typeof(EyeAimConstraint))]
    public class EyeAimConstraintEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var aim = (EyeAimConstraint)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Eye Aim Controls", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Non-spec engine convenience (not KHR_character). Enter Play mode to drive eye aim.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Look at Main Camera")) aim.Mode = EyeAimConstraint.LookAtMode.Camera;
                if (GUILayout.Button("Stop")) aim.Mode = EyeAimConstraint.LookAtMode.None;
            }
            aim.Weight = EditorGUILayout.Slider("Weight", aim.Weight, 0f, 1f);
            EditorGUILayout.LabelField("Active Mode", aim.Mode.ToString());

            Repaint();
        }
    }
}
#endif
