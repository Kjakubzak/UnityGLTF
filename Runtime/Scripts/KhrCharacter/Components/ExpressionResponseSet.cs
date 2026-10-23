using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Serialization;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Whether import could validate the expression data completely. Validation is incomplete, never failed, when it
    /// meets data it cannot check: an unrecognized or unsupported entry extension, a target it cannot resolve, or
    /// buffer data it cannot decode. Invalid data rejects the whole expression set instead.
    /// </summary>
    public sealed class ExpressionValidationStatus
    {
        private static readonly string[] NoReasons = Array.Empty<string>();

        public ExpressionValidationStatus(IEnumerable<string> incompleteReasons)
        {
            var reasons = incompleteReasons != null ? new List<string>(incompleteReasons) : new List<string>();
            IncompleteReasons = reasons.Count > 0 ? reasons.AsReadOnly() : (IReadOnlyList<string>)NoReasons;
        }

        public static ExpressionValidationStatus Complete { get; } = new ExpressionValidationStatus(null);

        public bool IsComplete => IncompleteReasons.Count == 0;

        /// <summary>Why validation is incomplete; empty when it is complete.</summary>
        public IReadOnlyList<string> IncompleteReasons { get; }
    }

    /// <summary>One authoritative array entry from KHR_character_expression.</summary>
    public sealed class ExpressionResponseSetEntry
    {
        private static readonly SuppressionEntry[] NoSuppressions = Array.Empty<SuppressionEntry>();
        private SuppressionEntry[] _suppressions = NoSuppressions;
        private string[] _extensionNames;

        internal ExpressionResponseSetEntry(
            string name,
            int animationIndex,
            ExpressionResponseAnimation animation,
            string extensionsJson,
            string extrasJson)
        {
            Name = name;
            AnimationIndex = animationIndex;
            Animation = animation ?? throw new ArgumentNullException(nameof(animation));
            ExtensionsJson = extensionsJson;
            ExtrasJson = extrasJson;
        }

        /// <summary>The entry's nonempty <c>name</c>, unique within its asset; the array index identifies the entry.</summary>
        public string Name { get; }
        public int AnimationIndex { get; }
        public ExpressionResponseAnimation Animation { get; }
        public string ExtensionsJson { get; }
        public string ExtrasJson { get; }

        /// <summary>
        /// The entry's <c>KHR_character_expression_suppression</c> rules. Source and target are expression-array
        /// indices; this entry is the source.
        /// </summary>
        public IReadOnlyList<SuppressionEntry> Suppressions => _suppressions;

        /// <summary>The names of the extensions in the entry's <c>extensions</c> object.</summary>
        public IReadOnlyList<string> ExtensionNames => _extensionNames ?? (_extensionNames = ParseExtensionNames());

        internal void SetSuppressions(SuppressionEntry[] suppressions)
        {
            _suppressions = suppressions ?? NoSuppressions;
        }

        private string[] ParseExtensionNames()
        {
            if (string.IsNullOrEmpty(ExtensionsJson)) return Array.Empty<string>();
            try
            {
                var names = new List<string>();
                if (JToken.Parse(ExtensionsJson) is JObject extensions)
                    foreach (var property in extensions.Properties())
                        names.Add(property.Name);
                return names.ToArray();
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>
    /// Passive imported KHR_character_expression data and its normative pipeline. <see cref="BuildSnapshot"/> turns
    /// the host's final native drivers into the pre-suppression vector <c>u</c> and the effective vector <c>v</c>;
    /// <see cref="Compose"/> evaluates every entry at <c>t = v[i] * T</c> and composes the responses once; the mapping
    /// methods compute both mapping operations. Listing decides which channels produce records: only channels that a
    /// supported derived expression extension lists emit, while every channel counts toward the duration. Results are
    /// glTF-space values by authoritative expression-array index and canonical property; nothing here writes Unity
    /// scene targets or mixes expression output with other writers.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ExpressionResponseSet : MonoBehaviour, ISerializationCallbackReceiver
    {
        [Serializable]
        private sealed class SerializedSampler
        {
            public bool Present;
            public float[] InputTimes;
            public ExpressionResponseInterpolation Interpolation;
            public ExpressionAccessorComponentEncoding OutputEncoding;
            public bool HasOutput;
            public int OutputRecordCount;
            public int OutputComponentCount;
            public float[] FlatOutputValues;
        }

        [Serializable]
        private sealed class SerializedTarget
        {
            public bool HasIdentity;
            public string CanonicalProperty;
            public ExpressionPropertySelection Selection;
            public int ArrayElement;
            public string DiagnosticProperty;
            public int ComponentCount;
            public ExpressionResponseValueKind ValueKind;
            public bool IsSupported;
            public ExpressionPropertyKind Kind = ExpressionPropertyKind.Other;
            public bool HasAuthoredInitial;
            public double[] AuthoredInitial;
            public int ArrayLength;
            public bool HasArrayAuthoredInitial;
            public double[] ArrayAuthoredInitial;
        }

        [Serializable]
        private sealed class SerializedChannel
        {
            public int SamplerIndex;
            public SerializedTarget Target;
            public string ListedBy;
            public bool EmitsRecords;
        }

        [Serializable]
        private sealed class SerializedEntry
        {
            [FormerlySerializedAs("Label")] public string Name;
            public int AnimationIndex;
            public string ExtensionsJson;
            public string ExtrasJson;
            public SerializedSampler[] Samplers;
            public SerializedChannel[] Channels;
            public string UnevaluableReason;
            public SuppressionEntry[] Suppressions;
        }

        [SerializeField, HideInInspector] private SerializedEntry[] _serializedEntries = Array.Empty<SerializedEntry>();
        [SerializeField, HideInInspector] private bool _requiredOnImport;
        [SerializeField, HideInInspector] private string[] _incompleteValidationReasons = Array.Empty<string>();
        [SerializeField, HideInInspector] private ExpressionMappingSet[] _forwardMappingSets = Array.Empty<ExpressionMappingSet>();
        [SerializeField, HideInInspector] private ExpressionInputMappingSet[] _inputMappingSets = Array.Empty<ExpressionInputMappingSet>();

        [NonSerialized] private ExpressionResponseSetEntry[] _entries;
        [NonSerialized] private IReadOnlyList<ExpressionResponseSetEntry> _readOnlyEntries;
        [NonSerialized] private ExpressionValidationStatus _validation;
        [NonSerialized] private ExpressionDerivedExtensionRegistry _derivedExtensions;

        public bool RequiredOnImport => _requiredOnImport;

        public IReadOnlyList<ExpressionResponseSetEntry> Entries
        {
            get
            {
                EnsureRuntimeEntries();
                return _readOnlyEntries;
            }
        }

        public int Count
        {
            get
            {
                EnsureRuntimeEntries();
                return _entries.Length;
            }
        }

        /// <summary>Whether import validated the expression data completely, and why not.</summary>
        public ExpressionValidationStatus Validation
            => _validation ?? (_validation = new ExpressionValidationStatus(_incompleteValidationReasons));

        /// <summary>
        /// Every current <c>expressionSetMappings</c> set (external inputs to native drivers), keyed by mapping-set
        /// identifier. Source indices are expression-array indices.
        /// </summary>
        public IReadOnlyList<ExpressionMappingSet> ForwardMappingSets
            => _forwardMappingSets ?? Array.Empty<ExpressionMappingSet>();

        /// <summary>
        /// Input-oriented adapters of every current mapping set, keyed by mapping-set
        /// identifier. Target indices are expression-array indices.
        /// </summary>
        public IReadOnlyList<ExpressionInputMappingSet> InputMappingSets
            => _inputMappingSets ?? Array.Empty<ExpressionInputMappingSet>();

        /// <summary>
        /// The derived-extension registry. Legacy single-writer declarations do not change mandatory addition; <see cref="ExpressionDerivedExtensionRegistry.Default"/>
        /// unless set.
        /// </summary>
        public ExpressionDerivedExtensionRegistry DerivedExtensions
        {
            get => _derivedExtensions ?? ExpressionDerivedExtensionRegistry.Default;
            set => _derivedExtensions = value;
        }

        private void Awake()
        {
            EnsureRuntimeEntries();
        }

        public void OnBeforeSerialize()
        {
            if (_entries != null) _serializedEntries = SerializeEntries(_entries);
        }

        public void OnAfterDeserialize()
        {
            _entries = null;
            _readOnlyEntries = null;
            _validation = null;
        }

        internal void Bind(
            ExpressionResponseSetEntry[] entries,
            bool requiredOnImport,
            ExpressionValidationStatus validation = null,
            ExpressionMappingSet[] forwardMappingSets = null,
            ExpressionInputMappingSet[] inputMappingSets = null)
        {
            _entries = entries ?? Array.Empty<ExpressionResponseSetEntry>();
            _readOnlyEntries = Array.AsReadOnly(_entries);
            _requiredOnImport = requiredOnImport;
            _validation = validation ?? ExpressionValidationStatus.Complete;
            _incompleteValidationReasons = new List<string>(_validation.IncompleteReasons).ToArray();
            _forwardMappingSets = forwardMappingSets ?? Array.Empty<ExpressionMappingSet>();
            _inputMappingSets = _forwardMappingSets.Length > 0
                ? ExpressionDriverPipeline.ToInputMappingSets(_forwardMappingSets)
                : inputMappingSets ?? Array.Empty<ExpressionInputMappingSet>();
            _serializedEntries = SerializeEntries(_entries);
        }

        /// <summary>Evaluates one entry on its own for a driver, without suppression or composition.</summary>
        public ExpressionResponse Evaluate(int expressionIndex, double? driver = null)
        {
            EnsureRuntimeEntries();
            if (expressionIndex < 0 || expressionIndex >= _entries.Length)
                throw new ArgumentOutOfRangeException(nameof(expressionIndex));
            return ExpressionResponseEvaluator.Evaluate(_entries[expressionIndex].Animation, driver);
        }

        /// <summary>
        /// Builds the driver snapshot of one composition step. Each supplied driver must be finite and is clamped to
        /// [0, 1], an absent one is 0, and every entry's suppressions are evaluated from that vector <c>u</c> into
        /// <c>v</c>.
        /// </summary>
        /// <param name="finalNativeDrivers">The host's final native drivers by expression index, after any input
        /// mapping and arbitration; may be shorter than <see cref="Count"/>.</param>
        public ExpressionDriverSnapshot BuildSnapshot(IReadOnlyList<double?> finalNativeDrivers)
        {
            EnsureRuntimeEntries();
            var suppressions = new IReadOnlyList<SuppressionEntry>[_entries.Length];
            for (int index = 0; index < suppressions.Length; index++)
                suppressions[index] = _entries[index]?.Suppressions;
            return ExpressionDriverPipeline.BuildSnapshot(finalNativeDrivers, suppressions);
        }

        /// <summary>
        /// Evaluates every entry with its driver from <paramref name="snapshot"/>'s <c>v</c> at <c>t = v[i] * T</c> and
        /// composes all responses in one step. A property whose evaluation fails gets no record and is reported in
        /// <see cref="ExpressionComposition.Failures"/> and the log.
        /// </summary>
        public ExpressionComposition Compose(ExpressionDriverSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            EnsureRuntimeEntries();
            if (snapshot.V.Count != _entries.Length)
                throw new ArgumentException(
                    $"The snapshot holds {snapshot.V.Count} drivers for {_entries.Length} expression entries.",
                    nameof(snapshot));

            var responses = new ExpressionResponse[_entries.Length];
            var carried = new List<ExpressionDerivedExtension>();
            var registry = DerivedExtensions;
            for (int index = 0; index < _entries.Length; index++)
            {
                var entry = _entries[index];
                if (entry == null) continue;
                responses[index] = ExpressionResponseEvaluator.Evaluate(entry.Animation, snapshot.V[index]);
                foreach (var name in entry.ExtensionNames)
                    if (registry.TryGet(name, out var extension) && !carried.Contains(extension))
                        carried.Add(extension);
            }
            return ExpressionComposer.Compose(responses, ExpressionComposer.SingleWriterPredicate(carried));
        }

        /// <summary>Every current input mapping, preserving wire expression-array identities.</summary>
        public IReadOnlyList<ExpressionMappingSet> MappingSets => ForwardMappingSets;

        /// <summary>Maps external inputs to an immutable full-count native vector before host arbitration and suppression.</summary>
        public IReadOnlyList<double> EvaluateExpressionSetMapping(
            string identifier, IReadOnlyDictionary<string, double> inputs)
        {
            if (!TryGetForwardMappingSet(identifier, out var set))
                throw new ArgumentException($"The asset has no expressionSetMappings set '{identifier}'.", nameof(identifier));
            return Array.AsReadOnly(ExpressionDriverPipeline.EvaluateExpressionSetMapping(set, inputs, Count));
        }

        /// <summary>
        /// Optional host projection of the input table to endpoint outputs with the exact
        /// <paramref name="identifier"/> from the snapshot's pre-suppression vector <c>u</c>.
        /// </summary>
        public IReadOnlyDictionary<string, double> EvaluateForwardMapping(
            string identifier,
            ExpressionDriverSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return EvaluateForwardMapping(identifier, snapshot.U);
        }

        /// <summary>
        /// Optional host projection of the input table to endpoint outputs with the exact
        /// <paramref name="identifier"/> from native drivers <paramref name="u"/>, which are clamped to [0, 1].
        /// </summary>
        /// <exception cref="ArgumentException">The asset has no such forward mapping set.</exception>
        public IReadOnlyDictionary<string, double> EvaluateForwardMapping(string identifier, IReadOnlyList<double> u)
        {
            if (!TryGetForwardMappingSet(identifier, out var set))
                throw new ArgumentException($"The asset has no expressionSetMappings set '{identifier}'.", nameof(identifier));
            return ExpressionDriverPipeline.EvaluateForwardMapping(set, u);
        }

        /// <summary>
        /// Computes native drivers from the current input mapping with the
        /// exact <paramref name="identifier"/> from endpoint commands, which are clamped to [0, 1].
        /// </summary>
        /// <exception cref="ArgumentException">The asset has no such input mapping set.</exception>
        public double[] EvaluateInputMapping(string identifier, IReadOnlyDictionary<string, double> commands)
        {
            if (TryGetForwardMappingSet(identifier, out var currentSet))
                return ExpressionDriverPipeline.EvaluateExpressionSetMapping(currentSet, commands, Count);
            if (!TryGetInputMappingSet(identifier, out var set))
                throw new ArgumentException(
                    $"The asset has no expressionSetMappings set '{identifier}'.", nameof(identifier));
            return ExpressionDriverPipeline.EvaluateInputMapping(set, commands, Count);
        }

        /// <summary>Finds a forward mapping set by its exact identifier.</summary>
        public bool TryGetForwardMappingSet(string identifier, out ExpressionMappingSet set)
        {
            set = FindMappingSet(_forwardMappingSets, identifier, mapping => mapping?.SetName);
            return set != null;
        }

        /// <summary>Finds an input mapping set by its exact identifier.</summary>
        public bool TryGetInputMappingSet(string identifier, out ExpressionInputMappingSet set)
        {
            set = FindMappingSet(_inputMappingSets, identifier, mapping => mapping?.SetName);
            return set != null;
        }

        private static T FindMappingSet<T>(T[] sets, string identifier, Func<T, string> getIdentifier) where T : class
        {
            if (sets == null || identifier == null) return null;
            foreach (var set in sets)
                if (string.Equals(getIdentifier(set), identifier, StringComparison.Ordinal))
                    return set;
            return null;
        }

        private void EnsureRuntimeEntries()
        {
            if (_entries != null) return;
            _entries = DeserializeEntries(_serializedEntries);
            _readOnlyEntries = Array.AsReadOnly(_entries);
        }

        private static SerializedEntry[] SerializeEntries(ExpressionResponseSetEntry[] entries)
        {
            var serialized = new SerializedEntry[entries.Length];
            for (int entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                var entry = entries[entryIndex];
                if (entry == null) continue;
                var animation = entry.Animation;
                var samplers = animation.Samplers ?? Array.Empty<ExpressionResponseSampler>();
                var channels = animation.Channels ?? Array.Empty<ExpressionResponseChannel>();
                var serializedSamplers = new SerializedSampler[samplers.Length];
                for (int samplerIndex = 0; samplerIndex < samplers.Length; samplerIndex++)
                    serializedSamplers[samplerIndex] = SerializeSampler(samplers[samplerIndex]);
                var serializedChannels = new SerializedChannel[channels.Length];
                for (int channelIndex = 0; channelIndex < channels.Length; channelIndex++)
                    serializedChannels[channelIndex] = SerializeChannel(channels[channelIndex]);
                serialized[entryIndex] = new SerializedEntry
                {
                    Name = entry.Name,
                    AnimationIndex = entry.AnimationIndex,
                    ExtensionsJson = entry.ExtensionsJson,
                    ExtrasJson = entry.ExtrasJson,
                    Samplers = serializedSamplers,
                    Channels = serializedChannels,
                    UnevaluableReason = animation.UnevaluableReason,
                    Suppressions = new List<SuppressionEntry>(entry.Suppressions).ToArray(),
                };
            }
            return serialized;
        }

        private static SerializedSampler SerializeSampler(ExpressionResponseSampler sampler)
        {
            if (sampler == null) return new SerializedSampler();
            var result = new SerializedSampler
            {
                Present = true,
                InputTimes = sampler.InputTimes,
                Interpolation = sampler.Interpolation,
                OutputEncoding = sampler.OutputEncoding,
                HasOutput = sampler.OutputValues != null,
            };
            if (sampler.OutputValues == null) return result;

            result.OutputRecordCount = sampler.OutputValues.Length;
            result.OutputComponentCount = sampler.OutputValues.Length > 0 && sampler.OutputValues[0] != null
                ? sampler.OutputValues[0].Length
                : 0;
            result.FlatOutputValues = new float[result.OutputRecordCount * result.OutputComponentCount];
            for (int recordIndex = 0; recordIndex < result.OutputRecordCount; recordIndex++)
            {
                var value = sampler.OutputValues[recordIndex];
                if (value == null || value.Length != result.OutputComponentCount)
                    throw new ExpressionResponseEvaluationException(
                        "A sampler cannot be serialized with inconsistent logical output dimensions.");
                Array.Copy(
                    value,
                    0,
                    result.FlatOutputValues,
                    recordIndex * result.OutputComponentCount,
                    result.OutputComponentCount);
            }
            return result;
        }

        private static SerializedChannel SerializeChannel(ExpressionResponseChannel channel)
        {
            if (channel == null || channel.Target == null)
                throw new ExpressionResponseEvaluationException("A response channel cannot be serialized without a target.");
            var target = channel.Target;
            var authoredInitial = ToArray(target.AuthoredInitial);
            var arrayAuthoredInitial = ToArray(target.ArrayAuthoredInitial);
            return new SerializedChannel
            {
                SamplerIndex = channel.SamplerIndex,
                ListedBy = channel.ListedBy,
                EmitsRecords = channel.EmitsRecords,
                Target = new SerializedTarget
                {
                    HasIdentity = target.Identity != null,
                    CanonicalProperty = target.Identity?.CanonicalProperty,
                    Selection = target.Identity?.Selection ?? ExpressionPropertySelection.Property,
                    ArrayElement = target.Identity?.ArrayElement ?? -1,
                    DiagnosticProperty = target.Property,
                    ComponentCount = target.ComponentCount,
                    ValueKind = target.ValueKind,
                    IsSupported = target.IsSupported,
                    Kind = target.Kind,
                    HasAuthoredInitial = authoredInitial != null,
                    AuthoredInitial = authoredInitial,
                    ArrayLength = target.ArrayLength,
                    HasArrayAuthoredInitial = arrayAuthoredInitial != null,
                    ArrayAuthoredInitial = arrayAuthoredInitial,
                },
            };
        }

        private static double[] ToArray(IReadOnlyList<double> values)
        {
            if (values == null) return null;
            var array = new double[values.Count];
            for (int index = 0; index < array.Length; index++) array[index] = values[index];
            return array;
        }

        private static ExpressionResponseSetEntry[] DeserializeEntries(SerializedEntry[] serialized)
        {
            if (serialized == null) return Array.Empty<ExpressionResponseSetEntry>();
            var entries = new ExpressionResponseSetEntry[serialized.Length];
            for (int entryIndex = 0; entryIndex < serialized.Length; entryIndex++)
            {
                var entry = serialized[entryIndex];
                if (entry == null) continue;
                var serializedSamplers = entry.Samplers ?? Array.Empty<SerializedSampler>();
                var serializedChannels = entry.Channels ?? Array.Empty<SerializedChannel>();
                var samplers = new ExpressionResponseSampler[serializedSamplers.Length];
                for (int samplerIndex = 0; samplerIndex < samplers.Length; samplerIndex++)
                    samplers[samplerIndex] = DeserializeSampler(serializedSamplers[samplerIndex]);
                var channels = new ExpressionResponseChannel[serializedChannels.Length];
                for (int channelIndex = 0; channelIndex < channels.Length; channelIndex++)
                    channels[channelIndex] = DeserializeChannel(serializedChannels[channelIndex]);
                entries[entryIndex] = new ExpressionResponseSetEntry(
                    entry.Name,
                    entry.AnimationIndex,
                    new ExpressionResponseAnimation
                    {
                        Samplers = samplers,
                        Channels = channels,
                        UnevaluableReason = NullIfEmpty(entry.UnevaluableReason),
                    },
                    entry.ExtensionsJson,
                    entry.ExtrasJson);
                entries[entryIndex].SetSuppressions(entry.Suppressions);
            }
            return entries;
        }

        private static ExpressionResponseSampler DeserializeSampler(SerializedSampler sampler)
        {
            if (sampler == null || !sampler.Present) return null;
            float[][] output = null;
            if (sampler.HasOutput)
            {
                output = new float[sampler.OutputRecordCount][];
                var flat = sampler.FlatOutputValues ?? Array.Empty<float>();
                if (flat.Length != sampler.OutputRecordCount * sampler.OutputComponentCount)
                    throw new ExpressionResponseEvaluationException("Serialized response sampler output is truncated.");
                for (int recordIndex = 0; recordIndex < output.Length; recordIndex++)
                {
                    output[recordIndex] = new float[sampler.OutputComponentCount];
                    Array.Copy(
                        flat,
                        recordIndex * sampler.OutputComponentCount,
                        output[recordIndex],
                        0,
                        sampler.OutputComponentCount);
                }
            }
            return new ExpressionResponseSampler
            {
                InputTimes = sampler.InputTimes,
                OutputValues = output,
                Interpolation = sampler.Interpolation,
                OutputEncoding = sampler.OutputEncoding,
            };
        }

        private static ExpressionResponseChannel DeserializeChannel(SerializedChannel channel)
        {
            if (channel?.Target == null)
                throw new ExpressionResponseEvaluationException("Serialized response channel has no target.");
            var serializedTarget = channel.Target;
            ExpressionResponseTarget target;
            if (!serializedTarget.HasIdentity)
            {
                target = ExpressionResponseTarget.UnsupportedUnresolved(serializedTarget.DiagnosticProperty);
            }
            else
            {
                var identity = DeserializeIdentity(serializedTarget);
                target = new ExpressionResponseTarget(
                    identity,
                    serializedTarget.ComponentCount,
                    serializedTarget.ValueKind,
                    serializedTarget.IsSupported,
                    serializedTarget.Kind,
                    serializedTarget.HasAuthoredInitial
                        ? serializedTarget.AuthoredInitial ?? Array.Empty<double>()
                        : null,
                    serializedTarget.ArrayLength,
                    serializedTarget.HasArrayAuthoredInitial
                        ? serializedTarget.ArrayAuthoredInitial ?? Array.Empty<double>()
                        : null);
            }
            return new ExpressionResponseChannel
            {
                SamplerIndex = channel.SamplerIndex,
                Target = target,
                ListedBy = NullIfEmpty(channel.ListedBy),
                EmitsRecords = channel.EmitsRecords,
            };
        }

        private static ExpressionPropertyIdentity DeserializeIdentity(SerializedTarget target)
        {
            switch (target.Selection)
            {
                case ExpressionPropertySelection.Property:
                    return ExpressionPropertyIdentity.ForProperty(target.CanonicalProperty);
                case ExpressionPropertySelection.WholeArray:
                    return ExpressionPropertyIdentity.ForWholeArray(target.CanonicalProperty);
                case ExpressionPropertySelection.ArrayElement:
                    return ExpressionPropertyIdentity.ForArrayElement(target.CanonicalProperty, target.ArrayElement);
                default:
                    throw new ExpressionResponseEvaluationException(
                        $"Unknown serialized property selection {target.Selection}.");
            }
        }

        // Unity serializes a null string as an empty one.
        private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
