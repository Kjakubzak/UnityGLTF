#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>
    /// Inspector for <see cref="KhrCharacter"/>: readiness plus a per-capability health readout
    /// (Active / Degraded / Inert) and expression count. In Play mode it shows the live
    /// <see cref="KhrCharacter.GetHealth"/> snapshot; at edit time it lists the baked capabilities
    /// from the serialized data (the runtime lists only populate on Awake).
    /// </summary>
    [CustomEditor(typeof(KhrCharacter))]
    public class KhrCharacterEditor : UnityEditor.Editor
    {
        private bool _showCapabilities = true;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var character = (KhrCharacter)target;
            bool playing = Application.isPlaying;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("KHR Character Health", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                playing
                    ? (character.IsReady
                        ? $"Ready. {character.Capabilities.Count} capabilities."
                        : "Not ready (awaiting OnCharacterReady).")
                    : "Edit-time view of baked data. Enter Play mode for live status and controls.",
                playing && !character.IsReady ? MessageType.Warning : MessageType.Info);

            if (playing)
            {
                var health = character.GetHealth();
                EditorGUILayout.LabelField("Expressions", health.ExpressionCount.ToString());
                DrawLiveCapabilities(health);
                Repaint();
            }
            else
            {
                DrawSerializedCapabilities();
            }
        }

        private void DrawLiveCapabilities(CharacterHealthReport health)
        {
            EditorGUILayout.Space();
            _showCapabilities = EditorGUILayout.Foldout(_showCapabilities, $"Capabilities ({health.Capabilities.Count})", true);
            if (!_showCapabilities) return;
            if (health.Capabilities.Count == 0)
            {
                EditorGUILayout.LabelField("(none)");
                return;
            }
            EditorGUI.indentLevel++;
            foreach (var cap in health.Capabilities)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(cap.Capability.ToString());
                    var prev = GUI.color;
                    GUI.color = ColorFor(cap.Status);
                    EditorGUILayout.LabelField(cap.Status.ToString(), GUILayout.Width(90f));
                    GUI.color = prev;
                }
            }
            EditorGUI.indentLevel--;
        }

        private void DrawSerializedCapabilities()
        {
            var prop = serializedObject.FindProperty("_serializedCapabilities");
            int count = prop != null ? prop.arraySize : 0;

            EditorGUILayout.Space();
            _showCapabilities = EditorGUILayout.Foldout(_showCapabilities, $"Capabilities ({count})", true);
            if (!_showCapabilities) return;
            if (count == 0)
            {
                EditorGUILayout.LabelField("(none — not a baked character, or not yet imported)");
                return;
            }
            EditorGUI.indentLevel++;
            for (int i = 0; i < prop.arraySize; i++)
            {
                var element = prop.GetArrayElementAtIndex(i);
                string name = element.enumValueIndex >= 0 && element.enumValueIndex < element.enumDisplayNames.Length
                    ? element.enumDisplayNames[element.enumValueIndex]
                    : element.intValue.ToString();
                EditorGUILayout.LabelField("•", name);
            }
            EditorGUI.indentLevel--;
        }

        private static Color ColorFor(CapabilityStatus status)
        {
            switch (status)
            {
                case CapabilityStatus.Active: return new Color(0.4f, 0.9f, 0.4f);
                case CapabilityStatus.Degraded: return new Color(0.95f, 0.8f, 0.3f);
                default: return new Color(0.9f, 0.5f, 0.5f); // Inert
            }
        }
    }
}
#endif
