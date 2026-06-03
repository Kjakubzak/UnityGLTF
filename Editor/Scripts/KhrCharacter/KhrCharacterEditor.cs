#if UNITY_EDITOR
using UnityEditor;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>
    /// Inspector for <see cref="KhrCharacter"/>: shows readiness and the detected capabilities.
    /// </summary>
    [CustomEditor(typeof(KhrCharacter))]
    public class KhrCharacterEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var character = (KhrCharacter)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("KHR Avatar Extensions", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                character.IsReady
                    ? $"Ready. Capabilities: {character.Capabilities.Count}"
                    : "Not ready (await OnCharacterReady).",
                MessageType.Info);

            if (character.Capabilities != null)
            {
                foreach (var cap in character.Capabilities)
                    EditorGUILayout.LabelField("•", cap.ToString());
            }
        }
    }
}
#endif
