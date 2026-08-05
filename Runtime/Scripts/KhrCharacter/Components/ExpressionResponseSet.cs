using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>One authoritative array entry from KHR_character_expression.</summary>
    public sealed class ExpressionResponseSetEntry
    {
        internal ExpressionResponseSetEntry(
            string label,
            int animationIndex,
            ExpressionResponseAnimation animation,
            string extensionsJson,
            string extrasJson)
        {
            Label = label;
            AnimationIndex = animationIndex;
            Animation = animation ?? throw new ArgumentNullException(nameof(animation));
            ExtensionsJson = extensionsJson;
            ExtrasJson = extrasJson;
        }

        public string Label { get; }
        public int AnimationIndex { get; }
        public ExpressionResponseAnimation Animation { get; }
        public string ExtensionsJson { get; }
        public string ExtrasJson { get; }
    }

    /// <summary>
    /// Passive imported KHR_character_expression data. It evaluates absolute wire-space response records by
    /// authoritative expression-array index and never writes Unity scene targets or chooses a composition policy.
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
        }

        [Serializable]
        private sealed class SerializedChannel
        {
            public int SamplerIndex;
            public SerializedTarget Target;
        }

        [Serializable]
        private sealed class SerializedEntry
        {
            public string Label;
            public int AnimationIndex;
            public string ExtensionsJson;
            public string ExtrasJson;
            public SerializedSampler[] Samplers;
            public SerializedChannel[] Channels;
        }

        [SerializeField, HideInInspector] private SerializedEntry[] _serializedEntries = Array.Empty<SerializedEntry>();
        [SerializeField, HideInInspector] private bool _requiredOnImport;

        [NonSerialized] private ExpressionResponseSetEntry[] _entries;
        [NonSerialized] private IReadOnlyList<ExpressionResponseSetEntry> _readOnlyEntries;

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
        }

        internal void Bind(ExpressionResponseSetEntry[] entries, bool requiredOnImport)
        {
            _entries = entries ?? Array.Empty<ExpressionResponseSetEntry>();
            _readOnlyEntries = Array.AsReadOnly(_entries);
            _requiredOnImport = requiredOnImport;
            _serializedEntries = SerializeEntries(_entries);
        }

        public ExpressionResponse Evaluate(int expressionIndex, float? driver = null)
        {
            EnsureRuntimeEntries();
            if (expressionIndex < 0 || expressionIndex >= _entries.Length)
                throw new ArgumentOutOfRangeException(nameof(expressionIndex));
            return ExpressionResponseEvaluator.Evaluate(_entries[expressionIndex].Animation, driver);
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
                    Label = entry.Label,
                    AnimationIndex = entry.AnimationIndex,
                    ExtensionsJson = entry.ExtensionsJson,
                    ExtrasJson = entry.ExtrasJson,
                    Samplers = serializedSamplers,
                    Channels = serializedChannels,
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
            return new SerializedChannel
            {
                SamplerIndex = channel.SamplerIndex,
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
                    entry.Label,
                    entry.AnimationIndex,
                    new ExpressionResponseAnimation { Samplers = samplers, Channels = channels },
                    entry.ExtensionsJson,
                    entry.ExtrasJson);
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
                    serializedTarget.IsSupported);
            }
            return new ExpressionResponseChannel { SamplerIndex = channel.SamplerIndex, Target = target };
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
    }
}
