using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Default expression evaluation policy: additive over rest. A 0..1 driver is mapped across the sampler's
    /// input-time range and sampled per interpolation; each expression contributes a delta over the rest value;
    /// masks attenuate inputs (blend/block); rotations accumulate commutatively in log space.
    /// </summary>
    public sealed class AdditiveExpressionSemantics : IExpressionSemantics
    {
        public static readonly AdditiveExpressionSemantics Default = new AdditiveExpressionSemantics();

        public float Clamp01(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
                throw new ArgumentOutOfRangeException(nameof(v), "Expression drivers must be finite.");
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        public float SampleScalarDelta(Sampler s, float[] deltaValues, float baseValue, float d)
        {
            if (deltaValues == null || deltaValues.Length == 0) return 0f;
            d = Clamp01(d);
            if (s.SingleKey)
                return (deltaValues[0] - baseValue) * d; // deltaValues[0] holds the absolute target value
            int i = FindInterval(s.Times, d, out float u);
            if (i + 1 >= deltaValues.Length) return deltaValues[deltaValues.Length - 1];
            if (s.Interp == Interp.Step) return (u >= 1f) ? deltaValues[i + 1] : deltaValues[i];
            return Mathf.Lerp(deltaValues[i], deltaValues[i + 1], u);
        }

        public Vector3 SampleVectorDelta(Sampler s, Vector3[] deltaVec, Vector3 baseVec, float d)
        {
            if (deltaVec == null || deltaVec.Length == 0) return Vector3.zero;
            d = Clamp01(d);
            if (s.SingleKey) return (deltaVec[0] - baseVec) * d;
            int i = FindInterval(s.Times, d, out float u);
            if (i + 1 >= deltaVec.Length) return deltaVec[deltaVec.Length - 1];
            if (s.Interp == Interp.Step) return (u >= 1f) ? deltaVec[i + 1] : deltaVec[i];
            return Vector3.Lerp(deltaVec[i], deltaVec[i + 1], u);
        }

        public Quaternion SampleRotationDelta(Sampler s, Quaternion[] deltaQuat, Quaternion baseQuat, float d)
        {
            if (deltaQuat == null || deltaQuat.Length == 0) return Quaternion.identity;
            d = Clamp01(d);
            if (s.SingleKey) return Quaternion.Slerp(Quaternion.identity, deltaQuat[0] * Quaternion.Inverse(baseQuat), d);
            int i = FindInterval(s.Times, d, out float u);
            if (i + 1 >= deltaQuat.Length) return deltaQuat[deltaQuat.Length - 1];
            if (s.Interp == Interp.Step) return (u >= 1f) ? deltaQuat[i + 1] : deltaQuat[i];
            return Quaternion.Slerp(deltaQuat[i], deltaQuat[i + 1], u);
        }

        public Vector4 SampleVector4Delta(Sampler s, Vector4[] deltaVec, Vector4 baseVec, float d)
        {
            if (deltaVec == null || deltaVec.Length == 0) return Vector4.zero;
            d = Clamp01(d);
            if (s.SingleKey) return (deltaVec[0] - baseVec) * d;
            int i = FindInterval(s.Times, d, out float u);
            if (i + 1 >= deltaVec.Length) return deltaVec[deltaVec.Length - 1];
            if (s.Interp == Interp.Step) return (u >= 1f) ? deltaVec[i + 1] : deltaVec[i];
            return Vector4.Lerp(deltaVec[i], deltaVec[i + 1], u);
        }

        public float ResolveMaskedInput(int trackIndex, IReadOnlyList<float> rawInputs, ExpressionTrack[] tracks)
        {
            if (rawInputs == null || trackIndex < 0 || trackIndex >= rawInputs.Count) return 0f;
            float result = rawInputs[trackIndex];
            if (tracks == null) return result;

            // A mask lives on the source expression and references the target it attenuates.
            for (int s = 0; s < tracks.Length; s++)
            {
                var masks = tracks[s]?.Masks;
                if (masks == null) continue;
                for (int m = 0; m < masks.Length; m++)
                {
                    var mask = masks[m];
                    if (mask == null || mask.TargetIndex != trackIndex) continue;
                    int src = mask.SourceIndex;
                    float sv = (src >= 0 && src < rawInputs.Count) ? rawInputs[src] : 0f;
                    float f;
                    if (mask.Type == MaskType.Block)
                        f = sv > mask.Threshold ? 1f - mask.Amount : 1f;
                    else if (mask.Type == MaskType.Blend)
                        f = 1f - mask.Amount * sv;
                    else
                        f = 1f;
                    result *= f;
                }
            }
            return result;
        }

        public void ApplyInputMapping(ExpressionInputMappingSet set,
                                      IReadOnlyDictionary<string, float> commandInputs,
                                      float[] nativeOutputs)
        {
            if (nativeOutputs == null) return;
            for (int i = 0; i < nativeOutputs.Length; i++) nativeOutputs[i] = 0f;
            if (set?.Commands == null || commandInputs == null) return;

            var sums = new double[nativeOutputs.Length];
            for (int c = 0; c < set.Commands.Length; c++)
            {
                var command = set.Commands[c];
                if (command?.CommandName == null || command.Contributions == null) continue;
                if (!commandInputs.TryGetValue(command.CommandName, out float value)) continue;
                ValidateUnit(value, nameof(commandInputs));
                for (int i = 0; i < command.Contributions.Length; i++)
                {
                    var contribution = command.Contributions[i];
                    if (contribution.TargetIndex < 0 || contribution.TargetIndex >= sums.Length) continue;
                    ValidateUnit(contribution.Weight, nameof(contribution.Weight));
                    sums[contribution.TargetIndex] += value * contribution.Weight;
                }
            }

            for (int i = 0; i < nativeOutputs.Length; i++)
                nativeOutputs[i] = (float)System.Math.Min(1d, sums[i]);
        }

        public IReadOnlyDictionary<string, float> EvaluateForwardMapping(
            ExpressionMappingSet set, IReadOnlyList<float> nativeInputs)
        {
            var outputs = new Dictionary<string, float>();
            if (set?.Targets == null || nativeInputs == null) return outputs;
            for (int t = 0; t < set.Targets.Length; t++)
            {
                var endpoint = set.Targets[t];
                if (endpoint?.TargetName == null || endpoint.Contributions == null) continue;
                double sum = 0d;
                for (int c = 0; c < endpoint.Contributions.Length; c++)
                {
                    var contribution = endpoint.Contributions[c];
                    if (contribution.SourceIndex < 0 || contribution.SourceIndex >= nativeInputs.Count) continue;
                    float value = nativeInputs[contribution.SourceIndex];
                    ValidateUnit(value, nameof(nativeInputs));
                    ValidateUnit(contribution.Weight, nameof(contribution.Weight));
                    sum += value * contribution.Weight;
                }
                outputs[endpoint.TargetName] = (float)System.Math.Min(1d, sum);
            }
            return outputs;
        }

        private static void ValidateUnit(float value, string parameter)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(parameter, "Mapping inputs and weights must be finite values in [0, 1].");
        }

        public Quaternion AccumulateRotation(Quaternion accumulatedDelta, Quaternion deltaToAdd, float weight)
        {
            // Accumulate in log space so the result is order-independent: exp(log(acc) + w*log(delta)).
            Vector3 v = Log(accumulatedDelta) + weight * Log(deltaToAdd);
            return Exp(v);
        }

        // ── helpers ──────────────────────────────────────────────────────────

        // Map d in [0,1] across [Times[0], Times[n-1]] and return the interval index + local fraction.
        private static int FindInterval(float[] times, float d, out float u)
        {
            int n = times != null ? times.Length : 0;
            if (n <= 1) { u = 0f; return 0; }
            float t0 = times[0];
            float tau = t0 + d * (times[n - 1] - t0);
            if (tau <= times[0]) { u = 0f; return 0; }
            if (tau >= times[n - 1]) { u = 1f; return n - 2; }
            for (int i = 0; i < n - 1; i++)
            {
                if (tau < times[i + 1])
                {
                    float dt = times[i + 1] - times[i];
                    u = dt > 1e-9f ? (tau - times[i]) / dt : 0f;
                    return i;
                }
            }
            u = 1f; return n - 2;
        }

        // Unit-quaternion log -> rotation vector (axis * angle); exp inverts it.
        private static Vector3 Log(Quaternion q)
        {
            if (q.w < 0f) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; } // shortest arc
            float vlen = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            if (vlen < 1e-6f) return Vector3.zero; // near identity
            float angle = 2f * Mathf.Atan2(vlen, q.w);
            float scale = angle / vlen;
            return new Vector3(q.x * scale, q.y * scale, q.z * scale);
        }

        private static Quaternion Exp(Vector3 v)
        {
            float angle = v.magnitude;
            if (angle < 1e-6f) return Quaternion.identity;
            float half = angle * 0.5f;
            float scale = Mathf.Sin(half) / angle;
            return new Quaternion(v.x * scale, v.y * scale, v.z * scale, Mathf.Cos(half));
        }
    }
}
