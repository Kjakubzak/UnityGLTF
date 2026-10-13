#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>
    /// Inspector for <see cref="SkeletonMap"/>: the build-on-awake toggle, every mapping set with its role → node
    /// associations, the tagged reference poses, the bones the humanoid adapter selected (all read from the
    /// serialized mapping so they show at edit time), humanoid availability, and a Play-mode "Build Humanoid
    /// Avatar" button.
    /// </summary>
    [CustomEditor(typeof(SkeletonMap))]
    public class SkeletonMapEditor : UnityEditor.Editor
    {
        private bool _showSets = true;
        private bool _showPoses = true;
        private bool _showBones = true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var buildProp = serializedObject.FindProperty("_buildHumanoidOnAwake");
            if (buildProp != null)
                EditorGUILayout.PropertyField(buildProp, new GUIContent("Build Humanoid On Awake"));

            var mappingProp = serializedObject.FindProperty("_serializedMapping");
            var rigProp = mappingProp?.FindPropertyRelative("SelectedRig");
            var setsProp = mappingProp?.FindPropertyRelative("MappingSets");
            var posesProp = mappingProp?.FindPropertyRelative("ReferencePoses");
            var bonesProp = mappingProp?.FindPropertyRelative("Bones");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Skeleton Mapping", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true)) // read-only tables
            {
                int setCount = setsProp != null ? setsProp.arraySize : 0;
                _showSets = EditorGUILayout.Foldout(_showSets, $"Mapping Sets ({setCount})", true);
                if (_showSets && setsProp != null)
                {
                    EditorGUI.indentLevel++;
                    for (int i = 0; i < setsProp.arraySize; i++) DrawMappingSet(setsProp.GetArrayElementAtIndex(i));
                    EditorGUI.indentLevel--;
                }

                int poseCount = posesProp != null ? posesProp.arraySize : 0;
                _showPoses = EditorGUILayout.Foldout(_showPoses, $"Reference Poses ({poseCount})", true);
                if (_showPoses && posesProp != null)
                {
                    EditorGUI.indentLevel++;
                    for (int i = 0; i < posesProp.arraySize; i++) DrawReferencePose(posesProp.GetArrayElementAtIndex(i));
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.LabelField("Humanoid Adapter Rig", rigProp != null ? StringOrNone(rigProp.stringValue) : "—");
                int boneCount = bonesProp != null ? bonesProp.arraySize : 0;
                _showBones = EditorGUILayout.Foldout(_showBones, $"Humanoid Adapter Bones ({boneCount})", true);
                if (_showBones && bonesProp != null)
                {
                    EditorGUI.indentLevel++;
                    for (int i = 0; i < bonesProp.arraySize; i++)
                    {
                        var entry = bonesProp.GetArrayElementAtIndex(i);
                        DrawRoleRow(
                            entry.FindPropertyRelative("JointName")?.stringValue,
                            entry.FindPropertyRelative("Bone")?.objectReferenceValue,
                            null);
                    }
                    EditorGUI.indentLevel--;
                }
            }

            serializedObject.ApplyModifiedProperties();

            var skeleton = (SkeletonMap)target;
            EditorGUILayout.Space();
            // RigVocabularies is runtime-populated (after Bind/rehydrate); the serialized mapping sets above are the
            // edit-time equivalent. Show the live detected vocabularies in Play mode.
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

        private static void DrawMappingSet(SerializedProperty set)
        {
            EditorGUILayout.LabelField(StringOrNone(set.FindPropertyRelative("Identifier")?.stringValue), EditorStyles.miniBoldLabel);
            var associations = set.FindPropertyRelative("Associations");
            var nodeIndices = set.FindPropertyRelative("NodeIndices");
            EditorGUI.indentLevel++;
            if (nodeIndices != null && nodeIndices.arraySize > 0)
            {
                for (int i = 0; i < nodeIndices.arraySize; i++)
                {
                    var entry = nodeIndices.GetArrayElementAtIndex(i);
                    string role = entry.FindPropertyRelative("Role")?.stringValue;
                    int node = entry.FindPropertyRelative("Node")?.intValue ?? -1;
                    DrawRoleRow(role, FindBone(associations, role), node);
                }
            }
            else if (associations != null)
            {
                for (int i = 0; i < associations.arraySize; i++)
                {
                    var entry = associations.GetArrayElementAtIndex(i);
                    DrawRoleRow(
                        entry.FindPropertyRelative("JointName")?.stringValue,
                        entry.FindPropertyRelative("Bone")?.objectReferenceValue,
                        null);
                }
            }
            EditorGUI.indentLevel--;
        }

        private static void DrawReferencePose(SerializedProperty pose)
        {
            int animation = pose.FindPropertyRelative("AnimationIndex")?.intValue ?? -1;
            string label = pose.FindPropertyRelative("PoseType")?.stringValue;
            bool valid = pose.FindPropertyRelative("IsValid")?.boolValue ?? true;
            EditorGUILayout.LabelField(
                $"Animation {animation}", $"{StringOrNone(label)}{(valid ? string.Empty : "  (invalid)")}");
            var errors = pose.FindPropertyRelative("Errors");
            if (valid || errors == null) return;
            EditorGUI.indentLevel++;
            for (int i = 0; i < errors.arraySize; i++)
                EditorGUILayout.LabelField(errors.GetArrayElementAtIndex(i).stringValue, EditorStyles.wordWrappedMiniLabel);
            EditorGUI.indentLevel--;
        }

        private static Object FindBone(SerializedProperty associations, string role)
        {
            if (associations == null) return null;
            for (int i = 0; i < associations.arraySize; i++)
            {
                var entry = associations.GetArrayElementAtIndex(i);
                if (entry.FindPropertyRelative("JointName")?.stringValue == role)
                    return entry.FindPropertyRelative("Bone")?.objectReferenceValue;
            }
            return null;
        }

        private static void DrawRoleRow(string role, Object bone, int? nodeIndex)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(StringOrNone(role), GUILayout.Width(140f));
                if (nodeIndex.HasValue) EditorGUILayout.LabelField($"node {nodeIndex.Value}", GUILayout.Width(80f));
                if (bone != null || !nodeIndex.HasValue) EditorGUILayout.ObjectField(bone, typeof(Transform), true);
                else EditorGUILayout.LabelField("not instantiated");
            }
        }

        private static string StringOrNone(string s) => string.IsNullOrEmpty(s) ? "—" : s;
    }
}
#endif
