#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>
    /// Inspector for the optional Unity-specific <see cref="GazeSolver"/> host adapter.
    /// </summary>
    [CustomEditor(typeof(GazeSolver))]
    public class GazeSolverEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var gaze = (GazeSolver)target;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Optional host adapter: its expression vocabulary, target selection, response curve, and update policy are not defined by KHR_node_lookat_target.",
                MessageType.Info);
            EditorGUILayout.LabelField("Gaze Controls", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play mode to drive gaze.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Look at Main Camera")) gaze.Mode = GazeSolver.LookAtMode.Camera;
                if (GUILayout.Button("Stop and Clear"))
                {
                    gaze.Mode = GazeSolver.LookAtMode.None;
                    gaze.ClearOutputs();
                }
            }
            gaze.Weight = EditorGUILayout.Slider("Weight", gaze.Weight, 0f, 1f);
            EditorGUILayout.LabelField("Active Mode", gaze.Mode.ToString());

            Repaint();
        }
    }
}
#endif
