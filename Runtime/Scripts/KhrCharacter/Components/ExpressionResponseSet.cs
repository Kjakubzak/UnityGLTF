using System;
using System.Collections.Generic;
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

        /// <summary>The entry's descriptive <c>name</c>. Names are not unique; the array index identifies the entry.</summary>
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

        internal void SetSuppressions(SuppressionEntry[] suppressions)
        {
            _suppressions = suppressions ?? NoSuppressions;
        }
    }

    /// <summary>
    /// Passive imported KHR_character_expression data. It evaluates absolute wire-space response records by
    /// authoritative expression-array index and never writes Unity scene targets or chooses a composition policy.
    /// Listing decides which channels produce records: only channels that a supported derived expression extension
    /// lists emit, while every channel counts toward the duration. It also exposes each entry's suppressions and every
    /// expression mapping set to host logic.
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
        /// Every <c>expressionSetMappings</c> set (native drivers to endpoint outputs), keyed by mapping-set
        /// identifier. Source indices are expression-array indices.
        /// </summary>
        public IReadOnlyList<ExpressionMappingSet> ForwardMappingSets
            => _forwardMappingSets ?? Array.Empty<ExpressionMappingSet>();

        /// <summary>
        /// Every <c>expressionSetInputMappings</c> set (endpoint commands to native drivers), keyed by mapping-set
        /// identifier. Target indices are expression-array indices.
        /// </summary>
        public IReadOnlyList<ExpressionInputMappingSet> InputMappingSets
            => _inputMappingSets ?? Array.Empty<ExpressionInputMappingSet>();

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
            _inputMappingSets = inputMappingSets ?? Array.Empty<ExpressionInputMappingSet>();
            _serializedEntries = SerializeEntries(_entries);
        }

        public ExpressionResponse Evaluate(int expressionIndex, float? driver = null)
        {
            EnsureRuntimeEntries();
            if (expressionIndex < 0 || expressionIndex >= _entries.Length)
                throw new ArgumentOutOfRangeException(nameof(expressionIndex));
            return ExpressionResponseEvaluator.Evaluate(_entries[expressionIndex].Animation, driver);
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
            double[] authoredInitial = null;
            if (target.AuthoredInitial != null)
            {
                authoredInitial = new double[target.AuthoredInitial.Count];
                for (int component = 0; component < authoredInitial.Length; component++)
                    authoredInitial[component] = target.AuthoredInitial[component];
            }
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
                },
            };
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
                    serializedTarget.ArrayLength);
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
