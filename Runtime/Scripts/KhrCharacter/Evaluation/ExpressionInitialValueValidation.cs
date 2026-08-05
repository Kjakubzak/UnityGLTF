using System;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>Output accessor encodings that affect KHR_character_expression initial-value tolerance.</summary>
    public enum ExpressionAccessorComponentEncoding
    {
        Float,
        NonNormalizedInteger,
        NormalizedByte,
        NormalizedUnsignedByte,
        NormalizedShort,
        NormalizedUnsignedShort,
    }

    /// <summary>
    /// Pure helpers for resolving and comparing an expression target's authored initial value. Values are decoded
    /// glTF Asset Object Model values; JSON defaults, accessor conversion, and sparse replacement happen upstream.
    /// </summary>
    public static class ExpressionInitialValueValidation
    {
        /// <summary>Resolves an explicitly authored property value before its specification-defined default.</summary>
        public static float[] ResolveProperty(float[] explicitlyAuthored, float[] specificationDefault)
        {
            if (explicitlyAuthored != null) return Copy(explicitlyAuthored);
            if (specificationDefault != null) return Copy(specificationDefault);
            throw new ExpressionResponseEvaluationException(
                "The target has neither an explicitly authored value nor a specification-defined default.");
        }

        /// <summary>Resolves node morph weights, then mesh weights, then one zero per morph target.</summary>
        public static float[] ResolveMorphWeights(
            float[] nodeWeights,
            float[] meshWeights,
            int morphTargetCount)
        {
            if (morphTargetCount < 0) throw new ArgumentOutOfRangeException(nameof(morphTargetCount));
            if (nodeWeights != null)
            {
                ValidateMorphCount(nodeWeights, morphTargetCount, "node.weights");
                return Copy(nodeWeights);
            }
            if (meshWeights != null)
            {
                ValidateMorphCount(meshWeights, morphTargetCount, "mesh.weights");
                return Copy(meshWeights);
            }
            return new float[morphTargetCount];
        }

        /// <summary>Compares floating-point components using the extension-defined accessor allowance.</summary>
        public static bool ComponentsEquivalent(
            float[] authored,
            float[] sampled,
            ExpressionAccessorComponentEncoding outputEncoding)
        {
            if (!MatchingFiniteDimensions(authored, sampled)) return false;
            double allowance = AccessorAllowance(outputEncoding);
            for (int component = 0; component < authored.Length; component++)
            {
                double a = authored[component];
                double b = sampled[component];
                double tolerance = 1e-6d + allowance + 1e-6d * Math.Max(Math.Abs(a), Math.Abs(b));
                if (Math.Abs(a - b) > tolerance) return false;
            }
            return true;
        }

        /// <summary>
        /// Compares normalized quaternion values with antipodal equivalence. The normalized-integer allowance follows
        /// the extension's q_h = 8 * h * h rule; FLOAT and non-normalized integer encodings have no accessor allowance.
        /// </summary>
        public static bool QuaternionsEquivalent(
            float[] authored,
            float[] sampled,
            ExpressionAccessorComponentEncoding outputEncoding)
        {
            if (authored == null || sampled == null || authored.Length != 4 || sampled.Length != 4)
                return false;
            if (!TryNormalize(authored, out double[] normalizedAuthored) ||
                !TryNormalize(sampled, out double[] normalizedSampled))
                return false;

            double dot = 0d;
            for (int component = 0; component < 4; component++)
                dot += normalizedAuthored[component] * normalizedSampled[component];
            double absoluteDot = Math.Min(1d, Math.Abs(dot));
            double allowance = AccessorAllowance(outputEncoding);
            double quaternionAllowance = allowance == 0d ? 0d : 8d * allowance * allowance;
            return 1d - absoluteDot <= 1e-6d + quaternionAllowance;
        }

        /// <summary>Compares an authored value to a decoded sampler's endpoint-clamped sample at time zero.</summary>
        public static bool MatchesTimeZeroSample(
            float[] authored,
            ExpressionResponseSampler sampler,
            ExpressionResponseTarget target,
            ExpressionAccessorComponentEncoding outputEncoding)
        {
            if (sampler == null) throw new ArgumentNullException(nameof(sampler));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (sampler.InputTimes == null || sampler.InputTimes.Length == 0)
                throw new ExpressionResponseEvaluationException("The sampler has no input keys.");
            if (sampler.OutputValues == null)
                throw new ExpressionResponseEvaluationException("The sampler has no decoded output values.");

            int firstValueRecord;
            switch (sampler.Interpolation)
            {
                case ExpressionResponseInterpolation.Step:
                case ExpressionResponseInterpolation.Linear:
                    firstValueRecord = 0;
                    break;
                case ExpressionResponseInterpolation.CubicSpline:
                    firstValueRecord = 1;
                    break;
                default:
                    throw new ExpressionResponseEvaluationException(
                        $"Unknown interpolation mode {sampler.Interpolation}.");
            }
            if (firstValueRecord >= sampler.OutputValues.Length)
                throw new ExpressionResponseEvaluationException("The sampler has no decoded time-zero value record.");
            var sampled = sampler.OutputValues[firstValueRecord];
            if (sampled == null || sampled.Length != target.ComponentCount) return false;
            return target.ValueKind == ExpressionResponseValueKind.Quaternion
                ? QuaternionsEquivalent(authored, sampled, outputEncoding)
                : ComponentsEquivalent(authored, sampled, outputEncoding);
        }

        public static double AccessorAllowance(ExpressionAccessorComponentEncoding outputEncoding)
        {
            switch (outputEncoding)
            {
                case ExpressionAccessorComponentEncoding.Float:
                case ExpressionAccessorComponentEncoding.NonNormalizedInteger:
                    return 0d;
                case ExpressionAccessorComponentEncoding.NormalizedByte:
                    return 0.5d / 127d;
                case ExpressionAccessorComponentEncoding.NormalizedUnsignedByte:
                    return 0.5d / 255d;
                case ExpressionAccessorComponentEncoding.NormalizedShort:
                    return 0.5d / 32767d;
                case ExpressionAccessorComponentEncoding.NormalizedUnsignedShort:
                    return 0.5d / 65535d;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outputEncoding));
            }
        }

        private static void ValidateMorphCount(float[] weights, int morphTargetCount, string source)
        {
            if (weights.Length != morphTargetCount)
                throw new ExpressionResponseEvaluationException(
                    $"{source} contains {weights.Length} weights; expected {morphTargetCount}.");
        }

        private static bool MatchingFiniteDimensions(float[] authored, float[] sampled)
        {
            if (authored == null || sampled == null || authored.Length == 0 || authored.Length != sampled.Length)
                return false;
            for (int component = 0; component < authored.Length; component++)
                if (!IsFinite(authored[component]) || !IsFinite(sampled[component])) return false;
            return true;
        }

        private static bool TryNormalize(float[] value, out double[] normalized)
        {
            normalized = null;
            double lengthSquared = 0d;
            for (int component = 0; component < value.Length; component++)
            {
                if (!IsFinite(value[component])) return false;
                double v = value[component];
                lengthSquared += v * v;
            }
            if (!(lengthSquared > 0d) || double.IsInfinity(lengthSquared)) return false;

            double inverseLength = 1d / Math.Sqrt(lengthSquared);
            normalized = new double[value.Length];
            for (int component = 0; component < value.Length; component++)
                normalized[component] = value[component] * inverseLength;
            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float[] Copy(float[] value)
        {
            var copy = new float[value.Length];
            Array.Copy(value, copy, value.Length);
            return copy;
        }
    }
}
