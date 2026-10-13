using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>Host-defined ownership policy for the optional Unity expression applicator.</summary>
    public enum ExpressionControllerOwnershipMode
    {
        /// <summary>
        /// The controller owns its targets: a bound property without an expression record returns to its authored
        /// initial value.
        /// </summary>
        Standalone,
        /// <summary>
        /// An earlier animation system owns the targets: a property without a record, and a morph weight no record
        /// covers, keep that system's value. A property with a record replaces that value.
        /// </summary>
        Integrated,
    }

    /// <summary>
    /// Optional Unity host applicator for <c>KHR_character_expression</c>. Each <c>LateUpdate</c>, after the Animator and
    /// after <see cref="GazeSolver"/>, it forms the final native drivers from direct weights or a selected input mapping,
    /// runs the normative pipeline (driver snapshot with suppression, sampling at <c>t = v[i] * T</c> and
    /// order-independent composition) and writes the aggregate records to the bound Unity targets. An imported character
    /// evaluates its <see cref="ExpressionResponseSet"/>; a set authored in Unity is evaluated as the response animations
    /// its export would contain. Mixing the result with other writers is this host's policy
    /// (<see cref="ExpressionControllerOwnershipMode"/>).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public class ExpressionController : MonoBehaviour
    {
        public struct ExpressionHandle
        {
            public string Name;
            public float Value;
            public ExpressionDomain Domains;
            public bool IsBinary;
        }

        // The offset, scale and rotation of one KHR_texture_transform object share one _ST vector.
        private sealed class TextureGroup
        {
            public ExpressionTargetBinding Offset;
            public ExpressionTargetBinding Scale;
            public ExpressionTargetBinding Rotation;
            public Renderer[] Renderers;
            public int[] Slots;
            public int StPropertyId;
            public bool HasStProperty;
            public int TexturePropertyId;
            public bool HasTextureProperty;
            public int RotationPropertyId;
            public bool HasRotationProperty;
            public bool Flipped;
            public readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        }

        // UnityGLTF's camera/light forward-axis turn, 180° about Y (SchemaExtensions.InvertDirection).
        private static readonly Quaternion ForwardAxisTurn = new Quaternion(0f, -1f, 0f, 0f);

        private static readonly List<Material> SharedMaterials = new List<Material>();

        // The authoring set and, for an imported character, its response set and bindings are persisted so a prefab
        // rehydrates in Awake; a live import calls Initialize itself. Hidden from the inspector: baked data.
        [SerializeField, HideInInspector] private CharacterExpressionSet _serializedSet;
        [SerializeField] private ExpressionControllerOwnershipMode _ownershipMode = ExpressionControllerOwnershipMode.Standalone;
        [SerializeField, HideInInspector] private bool _evaluatesImportedResponses;
        [SerializeField, HideInInspector] private ExpressionResponseSet _responses;
        [SerializeField, HideInInspector] private ExpressionTargetBinding[] _bindings = Array.Empty<ExpressionTargetBinding>();

        private bool _initialized;
        private CharacterExpressionSet _set;
        private ExpressionHandle[] _handles = Array.Empty<ExpressionHandle>();
        private float[] _weights = Array.Empty<float>();
        private readonly Dictionary<string, int> _nameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        private ExpressionResponseAnimation[] _authoredAnimations;
        private IReadOnlyList<SuppressionEntry>[] _authoredSuppressions;
        private ExpressionTargetBinding[] _activeBindings = Array.Empty<ExpressionTargetBinding>();
        private readonly List<ExpressionTargetBinding> _nodeBindings = new List<ExpressionTargetBinding>();
        private readonly List<ExpressionTargetBinding> _morphBindings = new List<ExpressionTargetBinding>();
        private readonly List<TextureGroup> _textureGroups = new List<TextureGroup>();

        private readonly Dictionary<string, ExpressionMappingSet> _outputMappingSets =
            new Dictionary<string, ExpressionMappingSet>(StringComparer.Ordinal);
        private readonly Dictionary<string, ExpressionInputMappingSet> _inputMappingSets =
            new Dictionary<string, ExpressionInputMappingSet>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, double>> _commands =
            new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        private string _selectedInputMappingSet;

        private ExpressionDriverSnapshot _snapshot;
        private ExpressionComposition _composition;

        public IReadOnlyList<ExpressionHandle> Expressions => _handles;

        /// <summary>
        /// The authoring set: the set an authored controller was initialized with, or the legacy bake of an imported
        /// character, kept for extraction and export.
        /// </summary>
        public CharacterExpressionSet Set => _set;

        // The baked set as persisted with the component, available at edit time too (the runtime working copy
        // _set is only built in Play mode / after Initialize). Intended for editor authoring/extraction tooling.
        public CharacterExpressionSet BakedSet => _serializedSet;

        public int Count => _handles.Length;
        public IReadOnlyList<string> VocabularySets { get; private set; } = new List<string>();
        public IReadOnlyList<string> InputVocabularySets { get; private set; } = new List<string>();
        public IReadOnlyList<string> OutputVocabularySets { get; private set; } = new List<string>();
        public string SelectedInputMappingSet => _selectedInputMappingSet;
        public ExpressionControllerOwnershipMode OwnershipMode
        {
            get => _ownershipMode;
            set => _ownershipMode = value;
        }

        /// <summary>The response set an imported character evaluates; null for an authored set.</summary>
        public ExpressionResponseSet Responses => _evaluatesImportedResponses ? _responses : null;

        /// <summary>The scene targets this controller writes, one per composed property.</summary>
        public IReadOnlyList<ExpressionTargetBinding> Bindings => _activeBindings;

        /// <summary>The driver snapshot (<c>u</c> and <c>v</c>) of the most recent update; null before the first one.</summary>
        public ExpressionDriverSnapshot Snapshot => _snapshot;

        /// <summary>The composition of the most recent update, with its failures; null before the first one.</summary>
        public ExpressionComposition Composition => _composition;

        // Rehydrates a deserialized prefab. A live import adds this component fresh and calls Initialize itself.
        private void Awake()
        {
            if (_initialized) return;
            if (_evaluatesImportedResponses)
            {
                if (_responses != null) Initialize(_serializedSet, _responses, _bindings);
                else Debug.LogWarning("[KHR_character] The expression controller's response set is missing; it drives nothing.", this);
            }
            // Unity may rehydrate a never-assigned [Serializable] field as an empty instance, so require a payload.
            else if (_serializedSet?.Expressions != null && _serializedSet.Expressions.Length > 0)
            {
                Initialize(_serializedSet);
            }
        }

        /// <summary>
        /// Evaluates a set authored in Unity. Its tracks become the response animations its export would contain (legacy
        /// curves normalized to response progress, in glTF space), so previewing the set matches evaluating the exported
        /// asset. A track that cannot be expressed that way, or that animates a node rotation, node scale or texture
        /// scale an earlier track already animates, is reported as a warning and produces no output.
        /// </summary>
        public void Initialize(CharacterExpressionSet set)
        {
            Clear();
            _evaluatesImportedResponses = false;
            _responses = null;
            _bindings = Array.Empty<ExpressionTargetBinding>();
            _set = set;
            // Awake calls Initialize(_serializedSet), where both are already the same reference.
            if (!ReferenceEquals(_serializedSet, set)) _serializedSet = set;
            _set?.RebuildIndex();

            var tracks = set?.Expressions ?? Array.Empty<ExpressionTrack>();
            var authored = AuthoredExpressionCurves.BuildResponseAnimations(set);
            _authoredAnimations = authored.Animations;
            _authoredSuppressions = BuildAuthoredSuppressions(tracks);

            var handles = new ExpressionHandle[tracks.Length];
            for (int index = 0; index < tracks.Length; index++)
                handles[index] = new ExpressionHandle
                {
                    Name = tracks[index]?.Name,
                    Domains = tracks[index]?.Domains ?? ExpressionDomain.None,
                    IsBinary = tracks[index]?.IsBinary ?? false,
                };
            SetHandles(handles);
            SetMappingSets(set?.MappingSets, set?.InputMappingSets);
            BindTargets(authored.Bindings);
            _initialized = true;
        }

        /// <summary>
        /// Evaluates an imported character's <paramref name="responses"/> and writes the composed records through
        /// <paramref name="bindings"/>. <paramref name="authoringSet"/> is kept only as <see cref="Set"/>, for extraction
        /// and export; it is not evaluated.
        /// </summary>
        /// <param name="authoringSet">The legacy bake of the same entries, or null.</param>
        /// <param name="responses">The imported response set; its entries define the expressions and their order.</param>
        /// <param name="bindings">The scene targets of the composed properties.</param>
        public void Initialize(
            CharacterExpressionSet authoringSet,
            ExpressionResponseSet responses,
            ExpressionTargetBinding[] bindings)
        {
            if (responses == null) throw new ArgumentNullException(nameof(responses));
            Clear();
            _evaluatesImportedResponses = true;
            _responses = responses;
            _bindings = bindings ?? Array.Empty<ExpressionTargetBinding>();
            _set = authoringSet;
            if (!ReferenceEquals(_serializedSet, authoringSet)) _serializedSet = authoringSet;

            var entries = responses.Entries;
            var tracks = authoringSet?.Expressions;
            var handles = new ExpressionHandle[entries.Count];
            for (int index = 0; index < handles.Length; index++)
                handles[index] = new ExpressionHandle
                {
                    Name = entries[index]?.Name,
                    Domains = DomainsOf(entries[index]),
                    IsBinary = tracks != null && index < tracks.Length && tracks[index] != null && tracks[index].IsBinary,
                };
            SetHandles(handles);
            SetMappingSets(responses.ForwardMappingSets, responses.InputMappingSets);
            BindTargets(_bindings);
            _initialized = true;
        }

        public void SetWeight(string name, float driver)
        {
            if (!TryIndex(name, out int i)) return;
            SetWeight(i, driver);
        }

        /// <summary>Sets a native driver. It must be finite and is clamped to [0, 1].</summary>
        public void SetWeight(int index, float driver)
        {
            if (index < 0 || index >= _weights.Length) return;
            float value = Clamp01(driver, nameof(driver), "Expression drivers");
            _weights[index] = value;
            var h = _handles[index]; h.Value = value; _handles[index] = h;
        }

        public float GetWeight(string name) => TryIndex(name, out int i) ? _weights[i] : 0f;
        public float GetWeight(int index) => index >= 0 && index < _weights.Length ? _weights[index] : 0f;

        public void ResetAll()
        {
            _commands.Clear();
            for (int i = 0; i < _weights.Length; i++)
            {
                _weights[i] = 0f;
                var h = _handles[i]; h.Value = 0f; _handles[i] = h;
            }
        }

        /// <summary>
        /// Sets an endpoint command of an input mapping set. The command must be finite and is clamped to [0, 1]; it
        /// drives the expressions only while its set is selected (<see cref="SelectVocabularyInputSet"/>).
        /// </summary>
        public void SetWeightByVocabulary(string setName, string targetExpression, float driver)
        {
            if (setName == null || targetExpression == null) return;
            if (!_inputMappingSets.ContainsKey(setName)) return;
            float value = Clamp01(driver, nameof(driver), "Endpoint commands");
            if (!_commands.TryGetValue(setName, out var commands))
                _commands.Add(setName, commands = new Dictionary<string, double>(StringComparer.Ordinal));
            commands[targetExpression] = value;
        }

        public bool SelectVocabularyInputSet(string setName)
        {
            if (setName == null || !_inputMappingSets.ContainsKey(setName)) return false;
            _selectedInputMappingSet = setName;
            return true;
        }

        public void UseDirectNativeInputs() => _selectedInputMappingSet = null;

        /// <summary>
        /// The endpoint outputs of an <c>expressionSetMappings</c> set, computed from the current pre-suppression drivers
        /// <c>u</c> (after any selected input mapping); suppression never changes them.
        /// </summary>
        public IReadOnlyDictionary<string, float> EvaluateVocabularyOutputs(string setName)
        {
            var outputs = new Dictionary<string, float>(StringComparer.Ordinal);
            if (setName == null || !_outputMappingSets.TryGetValue(setName, out var mapping)) return outputs;
            var u = ExpressionDriverPipeline.ClampDrivers(FinalNativeDrivers(), _weights.Length);
            foreach (var output in ExpressionDriverPipeline.EvaluateForwardMapping(mapping, u))
                outputs[output.Key] = (float)output.Value;
            return outputs;
        }

        public IReadOnlyList<string> VocabularyExpressions(string setName)
        {
            var list = new List<string>();
            if (setName == null) return list;
            if (_inputMappingSets.TryGetValue(setName, out var input))
            {
                if (input.Commands != null)
                    foreach (var command in input.Commands)
                        if (command?.CommandName != null) list.Add(command.CommandName);
            }
            else if (_outputMappingSets.TryGetValue(setName, out var output) && output.Targets != null)
            {
                foreach (var endpoint in output.Targets)
                    if (endpoint?.TargetName != null) list.Add(endpoint.TargetName);
            }
            return list;
        }

        private void LateUpdate()
        {
            if (!_initialized || (_evaluatesImportedResponses && _responses == null)) return;
            var drivers = FinalNativeDrivers();
            if (_evaluatesImportedResponses)
            {
                _snapshot = _responses.BuildSnapshot(drivers);
                _composition = _responses.Compose(_snapshot);
            }
            else
            {
                _snapshot = ExpressionDriverPipeline.BuildSnapshot(drivers, _authoredSuppressions);
                var responses = new ExpressionResponse[_authoredAnimations.Length];
                for (int index = 0; index < responses.Length; index++)
                    if (_authoredAnimations[index] != null)
                        responses[index] = ExpressionResponseEvaluator.Evaluate(
                            _authoredAnimations[index], _snapshot.V[index]);
                _composition = ExpressionComposer.Compose(responses);
            }
            Apply(_composition);
        }

        // The host's final native drivers: the selected input mapping's output, else the direct weights.
        private double?[] FinalNativeDrivers()
        {
            var drivers = new double?[_weights.Length];
            if (_selectedInputMappingSet != null &&
                _inputMappingSets.TryGetValue(_selectedInputMappingSet, out var mapping))
            {
                _commands.TryGetValue(_selectedInputMappingSet, out var commands);
                var native = ExpressionDriverPipeline.EvaluateInputMapping(mapping, commands, drivers.Length);
                for (int index = 0; index < drivers.Length; index++) drivers[index] = native[index];
            }
            else
            {
                for (int index = 0; index < drivers.Length; index++) drivers[index] = _weights[index];
            }
            return drivers;
        }

        private void Apply(ExpressionComposition composition)
        {
            bool standalone = _ownershipMode == ExpressionControllerOwnershipMode.Standalone;
            foreach (var binding in _nodeBindings) ApplyNode(binding, composition, standalone);
            foreach (var binding in _morphBindings) ApplyMorph(binding, composition, standalone);
            foreach (var group in _textureGroups) ApplyTexture(group, composition, standalone);
        }

        private static void ApplyNode(ExpressionTargetBinding binding, ExpressionComposition composition, bool standalone)
        {
            var node = binding.Node;
            if (node == null) return;
            IReadOnlyList<double> value;
            if (composition.TryGetRecord(binding.Property, out var record)) value = record.Value;
            else if (standalone) value = binding.AuthoredInitial;
            else return;

            switch (binding.Kind)
            {
                case ExpressionPropertyKind.NodeTranslation:
                    if (value == null || value.Count != 3) return;
                    node.localPosition = binding.TranslationScale *
                                         new Vector3(-(float)value[0], (float)value[1], (float)value[2]);
                    break;
                case ExpressionPropertyKind.NodeRotation:
                    if (value == null || value.Count != 4) return;
                    var rotation = new Quaternion((float)value[0], -(float)value[1], -(float)value[2], (float)value[3]);
                    if (binding.NodeHasForwardAxisConversion) rotation *= ForwardAxisTurn;
                    node.localRotation = rotation;
                    break;
                case ExpressionPropertyKind.NodeScale:
                    if (value == null || value.Count != 3) return;
                    node.localScale = new Vector3((float)value[0], (float)value[1], (float)value[2]);
                    break;
            }
        }

        // The composed record is the node's complete weight vector. Integrated mode writes only the elements a current
        // record covers, so an earlier writer keeps the others.
        private static void ApplyMorph(ExpressionTargetBinding binding, ExpressionComposition composition, bool standalone)
        {
            var authored = binding.AuthoredInitial;
            var renderers = binding.MorphRenderers;
            if (authored == null || renderers == null) return;
            int length = authored.Length;
            bool hasRecord = composition.TryGetRecord(binding.Property, out var record) && record.Value.Count == length;
            if (!hasRecord && !standalone) return;

            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                if (renderer == null || renderer.sharedMesh == null) continue;
                int count = Math.Min(length, renderer.sharedMesh.blendShapeCount);
                for (int element = 0; element < count; element++)
                {
                    double weight;
                    if (hasRecord)
                    {
                        if (!standalone && !record.Contributed[element]) continue;
                        weight = record.Value[element];
                    }
                    else
                    {
                        weight = authored[element];
                    }
                    renderer.SetBlendShapeWeight(
                        element, (float)(weight * FrameWeight(binding, rendererIndex * length + element)));
                }
            }
        }

        private static float FrameWeight(ExpressionTargetBinding binding, int index)
        {
            var frameWeights = binding.MorphFrameWeights;
            return frameWeights != null && index >= 0 && index < frameWeights.Length ? frameWeights[index] : 1f;
        }

        // Offset and scale share one _ST vector. In Integrated mode, when only one of them has a record, the other keeps
        // the earlier writer's value.
        private static void ApplyTexture(TextureGroup group, ExpressionComposition composition, bool standalone)
        {
            bool hasOffset = TryGetValue(group.Offset, composition, out var offset);
            bool hasScale = TryGetValue(group.Scale, composition, out var scale);
            bool hasRotation = TryGetValue(group.Rotation, composition, out var rotation);
            bool writeSt = group.HasStProperty && (standalone || hasOffset || hasScale);
            bool writeRotation = group.HasRotationProperty && (standalone || hasRotation);
            if (!writeSt && !writeRotation) return;

            bool keepEarlierComponent = writeSt && !standalone && !(hasOffset && hasScale);
            var gltfOffset = offset != null && offset.Count == 2
                ? new Vector2((float)offset[0], (float)offset[1])
                : Vector2.zero;
            var gltfScale = scale != null && scale.Count == 2
                ? new Vector2((float)scale[0], (float)scale[1])
                : Vector2.one;
            float angle = rotation != null && rotation.Count == 1 ? (float)rotation[0] : 0f;

            for (int index = 0; index < group.Renderers.Length; index++)
            {
                var renderer = group.Renderers[index];
                if (renderer == null) continue;
                int slot = group.Slots != null && index < group.Slots.Length ? group.Slots[index] : 0;
                renderer.GetPropertyBlock(group.Block, slot);
                if (writeSt)
                {
                    var slotOffset = gltfOffset;
                    var slotScale = gltfScale;
                    if (keepEarlierComponent &&
                        TryGetEarlierTextureTransform(group, renderer, slot, out var earlierOffset, out var earlierScale))
                    {
                        if (!hasOffset) slotOffset = earlierOffset;
                        if (!hasScale) slotScale = earlierScale;
                    }
                    GLTFSceneImporter.ConvertTextureTransformScaleAndOffset(
                        group.Flipped, slotOffset, slotScale, out var unityScale, out var unityOffset);
                    group.Block.SetVector(
                        group.StPropertyId, new Vector4(unityScale.x, unityScale.y, unityOffset.x, unityOffset.y));
                }
                if (writeRotation) group.Block.SetFloat(group.RotationPropertyId, angle);
                renderer.SetPropertyBlock(group.Block, slot);
            }
        }

        // The glTF offset and scale a slot shows before this controller writes it: its property block's _ST, else its
        // material's texture scale and offset, converted back with the inverse of the importer's _ST conversion.
        private static bool TryGetEarlierTextureTransform(
            TextureGroup group,
            Renderer renderer,
            int slot,
            out Vector2 offset,
            out Vector2 scale)
        {
            Vector4 st;
            if (group.Block.HasVector(group.StPropertyId))
            {
                st = group.Block.GetVector(group.StPropertyId);
            }
            else
            {
                renderer.GetSharedMaterials(SharedMaterials);
                var material = slot >= 0 && slot < SharedMaterials.Count ? SharedMaterials[slot] : null;
                SharedMaterials.Clear();
                if (material == null || !group.HasTextureProperty || !material.HasProperty(group.TexturePropertyId))
                {
                    offset = default;
                    scale = default;
                    return false;
                }
                var textureScale = material.GetTextureScale(group.TexturePropertyId);
                var textureOffset = material.GetTextureOffset(group.TexturePropertyId);
                st = new Vector4(textureScale.x, textureScale.y, textureOffset.x, textureOffset.y);
            }
            scale = new Vector2(st.x, group.Flipped ? -st.y : st.y);
            offset = new Vector2(st.z, group.Flipped ? st.w - scale.y : 1f - scale.y - st.w);
            return true;
        }

        // The current value of one texture-transform component: its record, else its authored value.
        private static bool TryGetValue(
            ExpressionTargetBinding binding,
            ExpressionComposition composition,
            out IReadOnlyList<double> value)
        {
            value = null;
            if (binding == null) return false;
            if (composition.TryGetRecord(binding.Property, out var record))
            {
                value = record.Value;
                return true;
            }
            value = binding.AuthoredInitial;
            return false;
        }

        private void Clear()
        {
            _initialized = false;
            _set = null;
            _authoredAnimations = null;
            _authoredSuppressions = null;
            _snapshot = null;
            _composition = null;
            _commands.Clear();
            _selectedInputMappingSet = null;
            SetHandles(Array.Empty<ExpressionHandle>());
            SetMappingSets(null, null);
            BindTargets(null);
        }

        private void SetHandles(ExpressionHandle[] handles)
        {
            // Array, not List: SetWeight replaces elements in place, so callers may enumerate Expressions while driving.
            _handles = handles;
            _weights = new float[handles.Length];
            _nameToIndex.Clear();
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < handles.Length; index++)
            {
                string name = handles[index].Name;
                if (name == null || duplicates.Contains(name)) continue;
                if (_nameToIndex.Remove(name))
                {
                    duplicates.Add(name);
                    continue;
                }
                _nameToIndex.Add(name, index);
            }
        }

        private void SetMappingSets(
            IReadOnlyList<ExpressionMappingSet> forward,
            IReadOnlyList<ExpressionInputMappingSet> input)
        {
            _outputMappingSets.Clear();
            _inputMappingSets.Clear();
            var outputNames = new List<string>();
            if (forward != null)
                foreach (var set in forward)
                    if (set?.SetName != null && !_outputMappingSets.ContainsKey(set.SetName))
                    {
                        _outputMappingSets.Add(set.SetName, set);
                        outputNames.Add(set.SetName);
                    }
            var inputNames = new List<string>();
            if (input != null)
                foreach (var set in input)
                    if (set?.SetName != null && !_inputMappingSets.ContainsKey(set.SetName))
                    {
                        _inputMappingSets.Add(set.SetName, set);
                        inputNames.Add(set.SetName);
                    }
            OutputVocabularySets = outputNames;
            InputVocabularySets = inputNames;
            var allNames = new List<string>(outputNames);
            foreach (var name in inputNames)
                if (!allNames.Contains(name)) allNames.Add(name);
            VocabularySets = allNames;
        }

        private void BindTargets(ExpressionTargetBinding[] bindings)
        {
            _activeBindings = bindings ?? Array.Empty<ExpressionTargetBinding>();
            _nodeBindings.Clear();
            _morphBindings.Clear();
            _textureGroups.Clear();
            var groups = new Dictionary<string, TextureGroup>(StringComparer.Ordinal);
            foreach (var binding in _activeBindings)
            {
                if (binding == null || string.IsNullOrEmpty(binding.Property)) continue;
                switch (binding.Kind)
                {
                    case ExpressionPropertyKind.NodeTranslation:
                    case ExpressionPropertyKind.NodeRotation:
                    case ExpressionPropertyKind.NodeScale:
                        _nodeBindings.Add(binding);
                        break;
                    case ExpressionPropertyKind.NodeWeights:
                        _morphBindings.Add(binding);
                        break;
                    case ExpressionPropertyKind.TextureOffset:
                    case ExpressionPropertyKind.TextureScale:
                    case ExpressionPropertyKind.TextureRotation:
                        string key = string.IsNullOrEmpty(binding.TextureTransform) ? binding.Property : binding.TextureTransform;
                        if (!groups.TryGetValue(key, out var group))
                        {
                            group = CreateTextureGroup(binding);
                            groups.Add(key, group);
                            _textureGroups.Add(group);
                        }
                        if (binding.Kind == ExpressionPropertyKind.TextureOffset) group.Offset = binding;
                        else if (binding.Kind == ExpressionPropertyKind.TextureScale) group.Scale = binding;
                        else group.Rotation = binding;
                        break;
                }
            }
        }

        private static TextureGroup CreateTextureGroup(ExpressionTargetBinding binding)
        {
            string stProperty = binding.TextureStProperty;
            bool hasSt = !string.IsNullOrEmpty(stProperty);
            bool hasTexture = hasSt && stProperty.Length > 3 && stProperty.EndsWith("_ST", StringComparison.Ordinal);
            bool hasRotation = !string.IsNullOrEmpty(binding.TextureRotationProperty);
            return new TextureGroup
            {
                Renderers = binding.TextureRenderers ?? Array.Empty<Renderer>(),
                Slots = binding.TextureMaterialSlots,
                HasStProperty = hasSt,
                StPropertyId = hasSt ? Shader.PropertyToID(stProperty) : 0,
                HasTextureProperty = hasTexture,
                TexturePropertyId = hasTexture ? Shader.PropertyToID(stProperty.Substring(0, stProperty.Length - 3)) : 0,
                HasRotationProperty = hasRotation,
                RotationPropertyId = hasRotation ? Shader.PropertyToID(binding.TextureRotationProperty) : 0,
                Flipped = binding.TextureFlipped,
            };
        }

        private static ExpressionDomain DomainsOf(ExpressionResponseSetEntry entry)
        {
            var domains = ExpressionDomain.None;
            var channels = entry?.Animation?.Channels;
            if (channels == null) return domains;
            foreach (var channel in channels)
            {
                if (channel?.Target == null || !channel.EmitsRecords) continue;
                switch (channel.Target.Kind)
                {
                    case ExpressionPropertyKind.NodeTranslation:
                    case ExpressionPropertyKind.NodeRotation:
                    case ExpressionPropertyKind.NodeScale:
                        domains |= ExpressionDomain.Joint;
                        break;
                    case ExpressionPropertyKind.NodeWeights:
                        domains |= ExpressionDomain.Morph;
                        break;
                    case ExpressionPropertyKind.TextureOffset:
                    case ExpressionPropertyKind.TextureScale:
                    case ExpressionPropertyKind.TextureRotation:
                        domains |= ExpressionDomain.Texture;
                        break;
                }
            }
            return domains;
        }

        // A suppression lives on its source track; one whose target is not a track of the set is dropped.
        private static IReadOnlyList<SuppressionEntry>[] BuildAuthoredSuppressions(ExpressionTrack[] tracks)
        {
            var result = new IReadOnlyList<SuppressionEntry>[tracks.Length];
            for (int source = 0; source < tracks.Length; source++)
            {
                var suppressions = tracks[source]?.Suppressions;
                if (suppressions == null || suppressions.Length == 0) continue;
                var valid = new List<SuppressionEntry>(suppressions.Length);
                foreach (var suppression in suppressions)
                {
                    if (suppression == null) continue;
                    if (suppression.TargetIndex < 0 || suppression.TargetIndex >= tracks.Length)
                    {
                        Debug.LogWarning(
                            $"[KHR_character] Expression {source} suppresses expression {suppression.TargetIndex}, " +
                            "which is not in the set; ignoring that suppression.");
                        continue;
                    }
                    valid.Add(suppression);
                }
                result[source] = valid;
            }
            return result;
        }

        private bool TryIndex(string name, out int index)
        {
            index = -1;
            return name != null && _nameToIndex.TryGetValue(name, out index);
        }

        private static float Clamp01(float value, string parameter, string description)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameter, $"{description} must be finite.");
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }
    }
}
