#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Editor
{
    /// <summary>
    /// Inspector for <see cref="ExpressionController"/>. At edit time it lists the baked expressions and
    /// vocabulary sets (read from the serialized set). In Play mode it shows a live control per expression —
    /// a slider, or a toggle for binary expressions — wired to GetWeight/SetWeight, plus a Reset All button.
    /// </summary>
    [CustomEditor(typeof(ExpressionController))]
    public class ExpressionControllerEditor : UnityEditor.Editor
    {
        private bool _showExpressions = true;
        private bool _showVocabularySets = true;
        private CharacterExpressionSetAsset _authoringAsset;

        public override void OnInspectorGUI()
        {
            var controller = (ExpressionController)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_ownershipMode"));
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.Space();

            if (Application.isPlaying)
                DrawPlayMode(controller);
            else
                DrawEditMode();
        }

        private void DrawPlayMode(ExpressionController controller)
        {
            EditorGUILayout.LabelField($"Expressions ({controller.Count})", EditorStyles.boldLabel);
            if (controller.Count == 0)
            {
                EditorGUILayout.HelpBox("No expressions initialized.", MessageType.Info);
                return;
            }

            if (GUILayout.Button("Reset All")) controller.ResetAll();

            // Index-based loop, not foreach: SetWeight below reassigns an element of the controller's handle list,
            // which bumps the List version and would throw "Collection was modified" mid-enumeration.
            var handles = controller.Expressions;
            for (int i = 0; i < handles.Count; i++)
            {
                var handle = handles[i];
                if (string.IsNullOrEmpty(handle.Name)) continue;
                float current = controller.GetWeight(handle.Name);
                float raw = EditorGUILayout.Slider(handle.Name, current, 0f, 1f);
                // Explicit host-authored binary metadata opts this control into 0/1 snapping.
                float next = handle.IsBinary ? Mathf.Round(raw) : raw;
                if (!Mathf.Approximately(next, current))
                    controller.SetWeight(handle.Name, next);
            }

            Repaint();
        }

        private void DrawEditMode()
        {
            serializedObject.Update(); // refresh the cached SerializedProperties (no DrawDefaultInspector here)
            var setProp = serializedObject.FindProperty("_serializedSet");
            var exprProp = setProp?.FindPropertyRelative("Expressions");

            int exprCount = exprProp != null ? exprProp.arraySize : 0;
            _showExpressions = EditorGUILayout.Foldout(_showExpressions, $"Baked Expressions ({exprCount})", true);
            if (exprCount == 0)
            {
                if (_showExpressions)
                    EditorGUILayout.HelpBox("No baked expressions. Enter Play mode to drive expressions.", MessageType.Info);
                return;
            }

            if (_showExpressions)
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < exprProp.arraySize; i++)
                {
                    var element = exprProp.GetArrayElementAtIndex(i);
                    var name = element.FindPropertyRelative("Name")?.stringValue;
                    EditorGUILayout.LabelField("•", string.IsNullOrEmpty(name) ? $"(expression {i})" : name);
                }
                EditorGUI.indentLevel--;
            }

            var mapProp = setProp.FindPropertyRelative("MappingSets");
            if (mapProp != null && mapProp.arraySize > 0)
            {
                EditorGUILayout.Space();
                _showVocabularySets = EditorGUILayout.Foldout(_showVocabularySets, $"Vocabulary Sets ({mapProp.arraySize})", true);
                if (_showVocabularySets)
                {
                    EditorGUI.indentLevel++;
                    for (int i = 0; i < mapProp.arraySize; i++)
                    {
                        var element = mapProp.GetArrayElementAtIndex(i);
                        var name = element.FindPropertyRelative("SetName")?.stringValue;
                        EditorGUILayout.LabelField("•", string.IsNullOrEmpty(name) ? $"(set {i})" : name);
                    }
                    EditorGUI.indentLevel--;
                }
            }

            DrawAuthoring();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Enter Play mode to drive expression weights.", MessageType.Info);
        }

        // Edit-time authoring affordances: extract the baked set into a reusable ScriptableObject asset (whose
        // default inspector then edits expression metadata), plus a session slot to quickly re-open such an asset.
        private void DrawAuthoring()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Authoring", EditorStyles.boldLabel);

            _authoringAsset = (CharacterExpressionSetAsset)EditorGUILayout.ObjectField(
                "Expression Set Asset", _authoringAsset, typeof(CharacterExpressionSetAsset), false);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Extract to ScriptableObject")) ExtractToAsset();
                using (new EditorGUI.DisabledScope(_authoringAsset == null))
                    if (GUILayout.Button("Edit Asset")) Selection.activeObject = _authoringAsset;
            }

            EditorGUILayout.HelpBox(
                "Extract creates a CharacterExpressionSetAsset from the baked set, capturing drivers as " +
                "scene-independent bindings (renderer/bone paths + blendshape names + curves) you can edit in its " +
                "inspector and re-resolve onto a character. The export plugin accepts controller-authored sets; " +
                "imported passive response data fails closed until a lossless passive writer exists.", MessageType.Info);
        }

        private void ExtractToAsset()
        {
            var controller = (ExpressionController)target;
            var baked = controller.BakedSet;
            if (baked?.Expressions == null || baked.Expressions.Length == 0)
            {
                EditorUtility.DisplayDialog("Extract Expression Set", "No baked expressions to extract.", "OK");
                return;
            }

            var path = EditorUtility.SaveFilePanelInProject(
                "Save Expression Set", controller.gameObject.name + "_Expressions", "asset",
                "Choose where to save the extracted CharacterExpressionSetAsset.");
            if (string.IsNullOrEmpty(path)) return;

            var asset = ScriptableObject.CreateInstance<CharacterExpressionSetAsset>();
            // Capture scene-independent driver bindings (renderer/bone paths + blendshape names + curves) plus
            // metadata. controller.transform is the character root (the importer attaches the controller there),
            // so driver paths resolve relative to it and the asset serializes without live scene refs.
            asset.Bindings = CharacterExpressionSetAsset.Extract(baked, controller.transform);

            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            _authoringAsset = asset;
            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;
        }
    }
}
#endif
