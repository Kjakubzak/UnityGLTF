#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>
    /// Inspector for <see cref="SkeletonMap"/>: the build-on-awake toggle, the resolved vocab-&gt;bone table
    /// (read from the serialized mapping so it shows at edit time), humanoid availability, and a Play-mode
    /// "Build Humanoid Avatar" button.
    /// </summary>
    [CustomEditor(typeof(SkeletonMap))]
    public class SkeletonMapEditor : UnityEditor.Editor
    {
        private bool _showBones = true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var buildProp = serializedObject.FindProperty("_buildHumanoidOnAwake");
            if (buildProp != null)
                EditorGUILayout.PropertyField(buildProp, new GUIContent("Build Humanoid On Awake"));

            var mappingProp = serializedObject.FindProperty("_serializedMapping");
            var rigProp = mappingProp?.FindPropertyRelative("SelectedRig");
            var bonesProp = mappingProp?.FindPropertyRelative("Bones");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Skeleton Mapping", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Selected Rig", rigProp != null ? StringOrNone(rigProp.stringValue) : "—");

            int boneCount = bonesProp != null ? bonesProp.arraySize : 0;
            _showBones = EditorGUILayout.Foldout(_showBones, $"Bones ({boneCount})", true);
            if (_showBones && bonesProp != null)
            {
                using (new EditorGUI.DisabledScope(true)) // read-only table
                {
                    EditorGUI.indentLevel++;
                    for (int i = 0; i < bonesProp.arraySize; i++)
                    {
                        var entry = bonesProp.GetArrayElementAtIndex(i);
                        var joint = entry.FindPropertyRelative("JointName")?.stringValue;
                        var bone = entry.FindPropertyRelative("Bone")?.objectReferenceValue;
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(StringOrNone(joint), GUILayout.Width(140f));
                            EditorGUILayout.ObjectField(bone, typeof(Transform), true);
                        }
                    }
                    EditorGUI.indentLevel--;
                }
            }

            serializedObject.ApplyModifiedProperties();

            var skeleton = (SkeletonMap)target;
            EditorGUILayout.Space();
            // RigVocabularies is runtime-populated (after Bind/rehydrate); the serialized "Selected Rig" above is
            // the edit-time equivalent. Show the live detected vocabularies in Play mode.
            if (Application.isPlaying)
            {
                var vocab = skeleton.RigVocabularies;
                EditorGUILayout.LabelField("Rig Vocabularies",
                    vocab != null && vocab.Count > 0 ? string.Join(", ", vocab) : "—");
            }
            EditorGUILayout.LabelField("Humanoid Available", skeleton.HumanoidAvailable.ToString());

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button(Application.isPlaying
                        ? "Build Humanoid Avatar"
                        : "Build Humanoid Avatar (Play mode only)"))
                {
                    var avatar = skeleton.BuildAndAssignAvatar();
                    Debug.Log(avatar != null
                        ? "[KHR_character] Humanoid avatar built and assigned."
                        : "[KHR_character] Humanoid avatar could not be built; generic rig kept.");
                }
            }

            if (Application.isPlaying) Repaint();
        }

        private static string StringOrNone(string s) => string.IsNullOrEmpty(s) ? "—" : s;
    }
}
#endif
