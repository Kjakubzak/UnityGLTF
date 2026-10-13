using System;
using System.Collections.Generic;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>Interpolation modes used by decoded glTF animation samplers.</summary>
    public enum ExpressionResponseInterpolation
    {
        Step,
        Linear,
        CubicSpline,
    }

    /// <summary>Identifies whether ordinary component interpolation or glTF quaternion rules apply.</summary>
    public enum ExpressionResponseValueKind
    {
        Components,
        Quaternion,
    }

    /// <summary>
    /// A decoded glTF animation sampler. Values remain in glTF Asset Object Model space; no Unity coordinate or
    /// unit conversion is performed. CUBICSPLINE values use the glTF in-tangent, value, out-tangent record order.
    /// </summary>
    public sealed class ExpressionResponseSampler
    {
        public float[] InputTimes { get; set; }
        public float[][] OutputValues { get; set; }
        public ExpressionResponseInterpolation Interpolation { get; set; }
        public ExpressionAccessorComponentEncoding OutputEncoding { get; set; }

        /// <summary>
        /// Reshapes a decoded SCALAR accessor stream into complete logical array values. Accessor normalization and
        /// sparse replacement must already have been applied. For CUBICSPLINE, each key is ordered as all in-tangent
        /// elements, all value elements, then all out-tangent elements, matching glTF's packed morph-weight layout.
        /// </summary>
        public static ExpressionResponseSampler FromDecodedScalarStream(
            float[] inputTimes,
            float[] decodedOutputScalars,
            int componentsPerLogicalValue,
            ExpressionResponseInterpolation interpolation,
            ExpressionAccessorComponentEncoding outputEncoding = ExpressionAccessorComponentEncoding.Float)
        {
            if (inputTimes == null) throw new ArgumentNullException(nameof(inputTimes));
            if (decodedOutputScalars == null) throw new ArgumentNullException(nameof(decodedOutputScalars));
            if (componentsPerLogicalValue < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(componentsPerLogicalValue),
                    "A packed array target must contain at least one component per logical value.");

            int recordsPerKey;
            switch (interpolation)
            {
                case ExpressionResponseInterpolation.Step:
                case ExpressionResponseInterpolation.Linear:
                    recordsPerKey = 1;
                    break;
                case ExpressionResponseInterpolation.CubicSpline:
                    recordsPerKey = 3;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(interpolation));
            }

            long logicalRecordCount = (long)inputTimes.Length * recordsPerKey;
            long expectedScalarCount = logicalRecordCount * componentsPerLogicalValue;
            if (decodedOutputScalars.LongLength != expectedScalarCount)
                throw new ExpressionResponseEvaluationException(
                    $"Decoded scalar stream contains {decodedOutputScalars.LongLength} values; " +
                    $"expected {expectedScalarCount} for {inputTimes.Length} keys and " +
                    $"{componentsPerLogicalValue} components.");
            if (logicalRecordCount > int.MaxValue)
                throw new ExpressionResponseEvaluationException("Decoded scalar stream has too many logical records.");

            var logicalValues = new float[(int)logicalRecordCount][];
            for (int recordIndex = 0; recordIndex < logicalValues.Length; recordIndex++)
            {
                var value = new float[componentsPerLogicalValue];
                Array.Copy(
                    decodedOutputScalars,
                    recordIndex * componentsPerLogicalValue,
                    value,
                    0,
                    componentsPerLogicalValue);
                logicalValues[recordIndex] = value;
            }

            return new ExpressionResponseSampler
            {
                InputTimes = inputTimes,
                OutputValues = logicalValues,
                Interpolation = interpolation,
                OutputEncoding = outputEncoding,
            };
        }
    }

    /// <summary>Describes how a channel selects its canonical Asset Object Model property.</summary>
    public enum ExpressionPropertySelection
    {
        Property,
        WholeArray,
        ArrayElement,
    }

    /// <summary>The concrete property type a resolved expression channel targets.</summary>
    public enum ExpressionPropertyKind
    {
        NodeTranslation,
        NodeRotation,
        NodeScale,
        NodeWeights,
        TextureOffset,
        TextureScale,
        TextureRotation,
        Other,
    }

    /// <summary>
    /// Canonical identity for a concrete channel target. Core animation paths and extension pointer paths must be
    /// resolved to the same canonical property string before construction. Array selections make whole-array and
    /// element overlap explicit without retaining the channel's source syntax.
    /// </summary>
    public sealed class ExpressionPropertyIdentity : IEquatable<ExpressionPropertyIdentity>
    {
        private ExpressionPropertyIdentity(
            string canonicalProperty,
            ExpressionPropertySelection selection,
            int arrayElement)
        {
            if (string.IsNullOrEmpty(canonicalProperty))
                throw new ArgumentException("A canonical property identity is required.", nameof(canonicalProperty));
            if (selection == ExpressionPropertySelection.ArrayElement && arrayElement < 0)
                throw new ArgumentOutOfRangeException(nameof(arrayElement));

            CanonicalProperty = canonicalProperty;
            Selection = selection;
            ArrayElement = selection == ExpressionPropertySelection.ArrayElement ? arrayElement : -1;
        }

        public string CanonicalProperty { get; }
        public ExpressionPropertySelection Selection { get; }
        public int ArrayElement { get; }

        public static ExpressionPropertyIdentity ForProperty(string canonicalProperty)
        {
            return new ExpressionPropertyIdentity(canonicalProperty, ExpressionPropertySelection.Property, -1);
        }

        public static ExpressionPropertyIdentity ForWholeArray(string canonicalProperty)
        {
            return new ExpressionPropertyIdentity(canonicalProperty, ExpressionPropertySelection.WholeArray, -1);
        }

        public static ExpressionPropertyIdentity ForArrayElement(string canonicalProperty, int arrayElement)
        {
            return new ExpressionPropertyIdentity(
                canonicalProperty,
                ExpressionPropertySelection.ArrayElement,
                arrayElement);
        }

        public bool Overlaps(ExpressionPropertyIdentity other)
        {
            if (other == null || !string.Equals(CanonicalProperty, other.CanonicalProperty, StringComparison.Ordinal))
                return false;
            if (Selection == ExpressionPropertySelection.ArrayElement &&
                other.Selection == ExpressionPropertySelection.ArrayElement)
                return ArrayElement == other.ArrayElement;
            return true;
        }

        public bool Equals(ExpressionPropertyIdentity other)
        {
            return other != null &&
                   string.Equals(CanonicalProperty, other.CanonicalProperty, StringComparison.Ordinal) &&
                   Selection == other.Selection &&
                   ArrayElement == other.ArrayElement;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ExpressionPropertyIdentity);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(CanonicalProperty);
                hash = hash * 397 ^ (int)Selection;
                hash = hash * 397 ^ ArrayElement;
                return hash;
            }
        }

        public override string ToString()
        {
            return Selection == ExpressionPropertySelection.ArrayElement
                ? $"{CanonicalProperty}/{ArrayElement}"
                : CanonicalProperty;
        }
    }

    /// <summary>
    /// Identity and shape of a concrete animation-channel target. A target that did not resolve, or whose data could
    /// not be decoded, keeps its channel and sampler for duration calculation while producing no response record.
    /// </summary>
    public sealed class ExpressionResponseTarget
    {
        private readonly double[] _authoredInitial;

        public ExpressionResponseTarget(
            string property,
            int componentCount,
            ExpressionResponseValueKind valueKind = ExpressionResponseValueKind.Components,
            bool isSupported = true)
            : this(ExpressionPropertyIdentity.ForProperty(property), componentCount, valueKind, isSupported)
        {
        }

        public ExpressionResponseTarget(
            ExpressionPropertyIdentity identity,
            int componentCount,
            ExpressionResponseValueKind valueKind = ExpressionResponseValueKind.Components,
            bool isSupported = true)
            : this(identity, componentCount, valueKind, isSupported, ExpressionPropertyKind.Other, null, 0)
        {
        }

        /// <summary>Creates a resolved target with its property kind and authored initial value.</summary>
        /// <param name="identity">Canonical property identity shared by core and pointer forms.</param>
        /// <param name="componentCount">Components of one logical sample.</param>
        /// <param name="valueKind">Whether glTF quaternion rules apply.</param>
        /// <param name="isSupported">Whether the target resolved and its data decoded.</param>
        /// <param name="kind">The concrete property type.</param>
        /// <param name="authoredInitial">The authored initial value <c>b</c> in glTF space; one element for an
        /// array-element target.</param>
        /// <param name="arrayLength">The element count of the whole array property (N for morph weights), else 0.</param>
        public ExpressionResponseTarget(
            ExpressionPropertyIdentity identity,
            int componentCount,
            ExpressionResponseValueKind valueKind,
            bool isSupported,
            ExpressionPropertyKind kind,
            double[] authoredInitial,
            int arrayLength)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            Property = identity.ToString();
            ComponentCount = componentCount;
            ValueKind = valueKind;
            IsSupported = isSupported;
            Kind = kind;
            _authoredInitial = authoredInitial != null ? (double[])authoredInitial.Clone() : null;
            AuthoredInitial = _authoredInitial != null ? Array.AsReadOnly(_authoredInitial) : null;
            ArrayLength = arrayLength;
        }

        private ExpressionResponseTarget(string diagnosticProperty)
        {
            Property = diagnosticProperty;
            ComponentCount = 0;
            ValueKind = ExpressionResponseValueKind.Components;
            IsSupported = false;
            Kind = ExpressionPropertyKind.Other;
        }

        /// <summary>
        /// Represents an optional extension target whose schema is unavailable to this implementation. It remains
        /// in the channel set and duration calculation, but overlap and value validation are necessarily incomplete.
        /// </summary>
        public static ExpressionResponseTarget UnsupportedUnresolved(string diagnosticProperty)
        {
            return new ExpressionResponseTarget(diagnosticProperty);
        }

        public string Property { get; }
        public ExpressionPropertyIdentity Identity { get; }
        public int ComponentCount { get; }
        public ExpressionResponseValueKind ValueKind { get; }

        /// <summary>True when the target resolved to a concrete property and its sampler data decoded.</summary>
        public bool IsSupported { get; }

        /// <summary>The concrete property type, or <see cref="ExpressionPropertyKind.Other"/> when unresolved.</summary>
        public ExpressionPropertyKind Kind { get; }

        /// <summary>
        /// The authored initial value <c>b</c> in glTF space, or null when the target did not resolve. Node
        /// translation, rotation and scale and texture offset and scale carry the float32 values UnityGLTF parses;
        /// morph weights and texture rotation carry the JSON doubles.
        /// </summary>
        public IReadOnlyList<double> AuthoredInitial { get; }

        /// <summary>The element count of the whole array property (N for morph weights), or 0.</summary>
        public int ArrayLength { get; }
    }

    /// <summary>A channel in the expression entry's referenced glTF animation.</summary>
    public sealed class ExpressionResponseChannel
    {
        public int SamplerIndex { get; set; }
        public ExpressionResponseTarget Target { get; set; }

        /// <summary>
        /// The derived expression extension on the entry that lists this channel, or null when none lists it. An
        /// unlisted channel still counts toward the expression duration but produces no response record.
        /// </summary>
        public string ListedBy { get; set; }

        /// <summary>
        /// True when a derived expression extension this implementation supports lists the channel and its target
        /// resolved and decoded. Only such channels produce response records.
        /// </summary>
        public bool EmitsRecords { get; set; }
    }

    /// <summary>Decoded sampler and channel data for one expression animation.</summary>
    public sealed class ExpressionResponseAnimation
    {
        public ExpressionResponseSampler[] Samplers { get; set; }
        public ExpressionResponseChannel[] Channels { get; set; }

        /// <summary>
        /// Why the expression cannot be evaluated, for example because a sampler input uses a compression filter
        /// the importer does not decode; null when it can be evaluated.
        /// </summary>
        public string UnevaluableReason { get; set; }

        /// <summary>False when <see cref="UnevaluableReason"/> is set; such an expression produces no records.</summary>
        public bool IsEvaluable => UnevaluableReason == null;
    }

    /// <summary>An absolute sampled value for one concrete glTF property target.</summary>
    public sealed class ExpressionResponseRecord
    {
        internal ExpressionResponseRecord(int channelIndex, ExpressionResponseTarget target, float[] value)
        {
            ChannelIndex = channelIndex;
            Target = target;
            Value = Array.AsReadOnly(value);
        }

        public int ChannelIndex { get; }
        public ExpressionResponseTarget Target { get; }
        public IReadOnlyList<float> Value { get; }
    }

    /// <summary>
    /// The stateless result of one expression evaluation. It contains absolute property samples only and does not
    /// prescribe how a host applies, blends, prioritizes, or otherwise composes them.
    /// </summary>
    public sealed class ExpressionResponse
    {
        internal ExpressionResponse(
            float effectiveDriver,
            float duration,
            float sampleTime,
            ExpressionResponseRecord[] records,
            bool isEvaluable = true)
        {
            EffectiveDriver = effectiveDriver;
            Duration = duration;
            SampleTime = sampleTime;
            Records = Array.AsReadOnly(records);
            IsEvaluable = isEvaluable;
        }

        public float EffectiveDriver { get; }

        /// <summary>The expression duration <c>T</c>; NaN when the expression is not evaluable.</summary>
        public float Duration { get; }

        /// <summary>The sample time <c>t = e * T</c>; NaN when the expression is not evaluable.</summary>
        public float SampleTime { get; }

        public IReadOnlyList<ExpressionResponseRecord> Records { get; }

        /// <summary>False when the expression's data could not be decoded; such a response has no records.</summary>
        public bool IsEvaluable { get; }
    }

    /// <summary>Reports invalid decoded expression-animation data supplied to the response evaluator.</summary>
    public sealed class ExpressionResponseEvaluationException : Exception
    {
        public ExpressionResponseEvaluationException(string message) : base(message) { }
    }

    /// <summary>
    /// Pure KHR_character_expression response evaluator. Each call derives a fresh response from decoded glTF data
    /// and the current driver; it retains no history and never reads or writes Unity scene targets. Every channel
    /// counts toward the duration and every resolved channel is validated, but only channels with
    /// <see cref="ExpressionResponseChannel.EmitsRecords"/> produce response records.
    /// </summary>
    public static class ExpressionResponseEvaluator
    {
        private static readonly ExpressionResponseRecord[] EmptyRecords = new ExpressionResponseRecord[0];

        /// <summary>Evaluates with the extension-defined default driver value of zero.</summary>
        public static ExpressionResponse Evaluate(ExpressionResponseAnimation animation)
        {
            return Evaluate(animation, null);
        }

        /// <summary>Evaluates an expression animation for the supplied optional driver.</summary>
        public static ExpressionResponse Evaluate(ExpressionResponseAnimation animation, float? driver)
        {
            if (animation == null) throw new ArgumentNullException(nameof(animation));

            float effectiveDriver = driver ?? 0f;
            if (!IsFinite(effectiveDriver))
                throw new ArgumentOutOfRangeException(nameof(driver), "Expression drivers must be finite.");
            effectiveDriver = Clamp01(effectiveDriver);
            if (!animation.IsEvaluable)
                return new ExpressionResponse(effectiveDriver, float.NaN, float.NaN, EmptyRecords, false);

            var channels = animation.Channels;
            var samplers = animation.Samplers;
            if (channels == null || channels.Length == 0)
                throw Invalid("The expression animation must contain at least one channel.");
            if (samplers == null)
                throw Invalid("The expression animation has no sampler array.");

            var referencedSamplers = new bool[samplers.Length];
            for (int channelIndex = 0; channelIndex < channels.Length; channelIndex++)
            {
                var channel = channels[channelIndex];
                if (channel == null)
                    throw Invalid($"Channel {channelIndex} is null.");
                if (channel.Target == null)
                    throw Invalid($"Channel {channelIndex} does not resolve to a concrete property target.");
                if (channel.SamplerIndex < 0 || channel.SamplerIndex >= samplers.Length)
                    throw Invalid($"Channel {channelIndex} references invalid sampler {channel.SamplerIndex}.");
                referencedSamplers[channel.SamplerIndex] = true;
            }

            float duration = 0f;
            for (int samplerIndex = 0; samplerIndex < referencedSamplers.Length; samplerIndex++)
            {
                if (!referencedSamplers[samplerIndex]) continue;
                ValidateInput(samplers[samplerIndex], samplerIndex);
                var times = samplers[samplerIndex].InputTimes;
                duration = Math.Max(duration, times[times.Length - 1]);
            }

            var resolvedIdentities = new List<ExpressionPropertyIdentity>(channels.Length);
            var resolvedIdentityChannels = new List<int>(channels.Length);
            for (int channelIndex = 0; channelIndex < channels.Length; channelIndex++)
            {
                var channel = channels[channelIndex];
                var target = channel.Target;
                if (target.Identity != null)
                {
                    for (int previousIndex = 0; previousIndex < resolvedIdentities.Count; previousIndex++)
                    {
                        if (!target.Identity.Overlaps(resolvedIdentities[previousIndex])) continue;
                        throw Invalid(
                            $"Channels {resolvedIdentityChannels[previousIndex]} and {channelIndex} target " +
                            $"overlapping concrete properties '{resolvedIdentities[previousIndex]}' and " +
                            $"'{target.Identity}'.");
                    }
                    resolvedIdentities.Add(target.Identity);
                    resolvedIdentityChannels.Add(channelIndex);
                }
                if (!target.IsSupported) continue;

                ValidateTarget(target, channelIndex);
                var sampler = samplers[channel.SamplerIndex];
                ValidateOutput(sampler, channel.SamplerIndex, target, channelIndex);
            }

            // Asset-data validation is independent of the current driver. The extension still contributes no
            // property value at zero and, in particular, never manufactures an authored initial-state record.
            if (effectiveDriver == 0f)
                return new ExpressionResponse(0f, duration, 0f, EmptyRecords);

            float sampleTime = effectiveDriver * duration;
            var records = new List<ExpressionResponseRecord>(channels.Length);
            for (int channelIndex = 0; channelIndex < channels.Length; channelIndex++)
            {
                var channel = channels[channelIndex];
                var target = channel.Target;
                if (!target.IsSupported || !channel.EmitsRecords) continue;

                var sampler = samplers[channel.SamplerIndex];
                records.Add(new ExpressionResponseRecord(
                    channelIndex,
                    target,
                    Sample(sampler, target, sampleTime)));
            }

            return new ExpressionResponse(effectiveDriver, duration, sampleTime, records.ToArray());
        }

        private static void ValidateInput(ExpressionResponseSampler sampler, int samplerIndex)
        {
            if (sampler == null)
                throw Invalid($"Referenced sampler {samplerIndex} is null.");
            var times = sampler.InputTimes;
            if (times == null || times.Length < 2)
                throw Invalid($"Referenced sampler {samplerIndex} must contain at least two input keys.");

            float previous = times[0];
            if (!IsFinite(previous) || previous < 0f)
                throw Invalid($"Sampler {samplerIndex} input key 0 must be finite and nonnegative.");
            for (int i = 1; i < times.Length; i++)
            {
                float current = times[i];
                if (!IsFinite(current) || current < 0f)
                    throw Invalid($"Sampler {samplerIndex} input key {i} must be finite and nonnegative.");
                if (current <= previous)
                    throw Invalid($"Sampler {samplerIndex} input keys must be strictly increasing.");
                previous = current;
            }
        }

        private static void ValidateTarget(ExpressionResponseTarget target, int channelIndex)
        {
            if (target.Identity == null)
                throw Invalid($"Supported channel {channelIndex} has no concrete property identity.");
            if (target.ComponentCount < 1)
                throw Invalid($"Supported channel {channelIndex} must have at least one output component.");
            if (target.ValueKind == ExpressionResponseValueKind.Quaternion && target.ComponentCount != 4)
                throw Invalid($"Quaternion channel {channelIndex} must have exactly four output components.");
        }

        private static void ValidateOutput(
            ExpressionResponseSampler sampler,
            int samplerIndex,
            ExpressionResponseTarget target,
            int channelIndex)
        {
            var values = sampler.OutputValues;
            int keyCount = sampler.InputTimes.Length;
            int expectedRecords;
            switch (sampler.Interpolation)
            {
                case ExpressionResponseInterpolation.Step:
                case ExpressionResponseInterpolation.Linear:
                    expectedRecords = keyCount;
                    break;
                case ExpressionResponseInterpolation.CubicSpline:
                    expectedRecords = keyCount * 3;
                    break;
                default:
                    throw Invalid($"Sampler {samplerIndex} has unknown interpolation mode {sampler.Interpolation}.");
            }
            if (values == null || values.Length != expectedRecords)
                throw Invalid(
                    $"Sampler {samplerIndex} output for channel {channelIndex} must contain {expectedRecords} logical records.");

            for (int recordIndex = 0; recordIndex < values.Length; recordIndex++)
            {
                var value = values[recordIndex];
                if (value == null || value.Length != target.ComponentCount)
                    throw Invalid(
                        $"Sampler {samplerIndex} output record {recordIndex} must contain {target.ComponentCount} components.");
                for (int component = 0; component < value.Length; component++)
                    if (!IsFinite(value[component]))
                        throw Invalid(
                            $"Sampler {samplerIndex} output record {recordIndex}, component {component} must be finite.");
            }

            if (target.ValueKind == ExpressionResponseValueKind.Quaternion)
            {
                for (int recordIndex = 0; recordIndex < values.Length; recordIndex++)
                {
                    if (sampler.Interpolation == ExpressionResponseInterpolation.CubicSpline && recordIndex % 3 != 1)
                        continue;
                    float lengthSquared = Dot(values[recordIndex], values[recordIndex]);
                    double accessorAllowance = ExpressionInitialValueValidation.AccessorAllowance(
                        sampler.OutputEncoding);
                    double unitTolerance = 1e-5d + 4d * accessorAllowance;
                    if (Math.Abs(lengthSquared - 1f) > unitTolerance)
                        throw Invalid(
                            $"Sampler {samplerIndex} quaternion value record {recordIndex} must have unit length.");
                }
            }
        }

        private static float[] Sample(
            ExpressionResponseSampler sampler,
            ExpressionResponseTarget target,
            float sampleTime)
        {
            var times = sampler.InputTimes;
            int lastKey = times.Length - 1;
            if (sampleTime <= times[0]) return KeyValue(sampler, target, 0);
            if (sampleTime >= times[lastKey]) return KeyValue(sampler, target, lastKey);

            int lowerKey = FindLowerKey(times, sampleTime);
            float interval = times[lowerKey + 1] - times[lowerKey];
            float u = (sampleTime - times[lowerKey]) / interval;

            switch (sampler.Interpolation)
            {
                case ExpressionResponseInterpolation.Step:
                    return KeyValue(sampler, target, lowerKey);
                case ExpressionResponseInterpolation.Linear:
                    return target.ValueKind == ExpressionResponseValueKind.Quaternion
                        ? Slerp(KeyRecord(sampler, lowerKey), KeyRecord(sampler, lowerKey + 1), u)
                        : Lerp(KeyRecord(sampler, lowerKey), KeyRecord(sampler, lowerKey + 1), u);
                case ExpressionResponseInterpolation.CubicSpline:
                    return CubicSpline(sampler, target, lowerKey, interval, u);
                default:
                    throw Invalid($"Unknown interpolation mode {sampler.Interpolation}.");
            }
        }

        private static int FindLowerKey(float[] times, float sampleTime)
        {
            int low = 0;
            int high = times.Length - 1;
            while (low + 1 < high)
            {
                int middle = low + (high - low) / 2;
                if (sampleTime < times[middle]) high = middle;
                else low = middle;
            }
            return low;
        }

        private static float[] KeyValue(
            ExpressionResponseSampler sampler,
            ExpressionResponseTarget target,
            int keyIndex)
        {
            var result = Copy(KeyRecord(sampler, keyIndex));
            return target.ValueKind == ExpressionResponseValueKind.Quaternion ? NormalizeQuaternion(result) : result;
        }

        private static float[] KeyRecord(ExpressionResponseSampler sampler, int keyIndex)
        {
            int recordIndex = sampler.Interpolation == ExpressionResponseInterpolation.CubicSpline
                ? keyIndex * 3 + 1
                : keyIndex;
            return sampler.OutputValues[recordIndex];
        }

        private static float[] Lerp(float[] from, float[] to, float u)
        {
            var result = new float[from.Length];
            for (int component = 0; component < result.Length; component++)
                result[component] = from[component] + (to[component] - from[component]) * u;
            return result;
        }

        private static float[] Slerp(float[] fromValue, float[] toValue, float u)
        {
            var from = NormalizeQuaternion(Copy(fromValue));
            var to = NormalizeQuaternion(Copy(toValue));
            float dot = Dot(from, to);
            if (dot < 0f)
            {
                dot = -dot;
                for (int component = 0; component < 4; component++) to[component] = -to[component];
            }

            dot = Math.Max(-1f, Math.Min(1f, dot));
            if (dot > 0.9995f)
                return NormalizeQuaternion(Lerp(from, to, u));

            float theta = (float)Math.Acos(dot);
            float sinTheta = (float)Math.Sin(theta);
            float fromWeight = (float)Math.Sin((1f - u) * theta) / sinTheta;
            float toWeight = (float)Math.Sin(u * theta) / sinTheta;
            var result = new float[4];
            for (int component = 0; component < 4; component++)
                result[component] = from[component] * fromWeight + to[component] * toWeight;
            return NormalizeQuaternion(result);
        }

        private static float[] CubicSpline(
            ExpressionResponseSampler sampler,
            ExpressionResponseTarget target,
            int lowerKey,
            float interval,
            float u)
        {
            var values = sampler.OutputValues;
            var from = values[lowerKey * 3 + 1];
            var outTangent = values[lowerKey * 3 + 2];
            var inTangent = values[(lowerKey + 1) * 3];
            var to = values[(lowerKey + 1) * 3 + 1];

            float u2 = u * u;
            float u3 = u2 * u;
            float h00 = 2f * u3 - 3f * u2 + 1f;
            float h10 = u3 - 2f * u2 + u;
            float h01 = -2f * u3 + 3f * u2;
            float h11 = u3 - u2;

            var result = new float[target.ComponentCount];
            for (int component = 0; component < result.Length; component++)
            {
                result[component] =
                    h00 * from[component] +
                    h10 * interval * outTangent[component] +
                    h01 * to[component] +
                    h11 * interval * inTangent[component];
            }

            return target.ValueKind == ExpressionResponseValueKind.Quaternion
                ? NormalizeQuaternion(result)
                : result;
        }

        private static float[] NormalizeQuaternion(float[] value)
        {
            float lengthSquared = Dot(value, value);
            if (!IsFinite(lengthSquared) || lengthSquared <= 0f)
                throw Invalid("Quaternion samples must have nonzero finite length.");
            float inverseLength = 1f / (float)Math.Sqrt(lengthSquared);
            for (int component = 0; component < value.Length; component++)
                value[component] *= inverseLength;
            return value;
        }

        private static float Dot(float[] left, float[] right)
        {
            float result = 0f;
            for (int component = 0; component < left.Length; component++)
                result += left[component] * right[component];
            return result;
        }

        private static float[] Copy(float[] value)
        {
            var copy = new float[value.Length];
            Array.Copy(value, copy, value.Length);
            return copy;
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            return value > 1f ? 1f : value;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static ExpressionResponseEvaluationException Invalid(string message)
        {
            return new ExpressionResponseEvaluationException(message);
        }
    }
}
