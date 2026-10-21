using System;
using System.Collections.Generic;
using System.Globalization;
using GLTF.Schema;
using UnityEngine;
using UnityGLTF.Extensions;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// The glTF-space response curves of the authoring model (<see cref="CharacterExpressionSet"/>). The exporter writes
    /// these curves, and <see cref="BuildResponseAnimations"/> turns a whole set into response animations so that
    /// <see cref="ExpressionController"/> previews an authored set the way its exported asset evaluates.
    /// </summary>
    internal static class AuthoredExpressionCurves
    {
        private const string KeyPrefix = "authored:";

        /// <summary>The response animations of an authored set and the scene targets of their properties.</summary>
        internal sealed class AuthoredResponses
        {
            /// <summary>One animation per track, by track index; null for a track without channels or that failed.</summary>
            public ExpressionResponseAnimation[] Animations;

            public ExpressionTargetBinding[] Bindings;
        }

        internal sealed class NormalizedLegacySampler
        {
            public float[] Times;
            public int SourceKeyCount;
            public bool SynthesizedSingleKey;
            public InterpolationType Interpolation;
        }

        internal sealed class TextureCurves
        {
            public Vector2[] Scale;
            public Vector2[] Offset;
        }

        internal static bool SameComponents(float[] left, float[] right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
                if (left[index] != right[index]) return false;
            return true;
        }

        internal static float[] Negated(float[] value)
        {
            var result = new float[value.Length];
            for (int index = 0; index < result.Length; index++) result[index] = -value[index];
            return result;
        }

        internal static NormalizedLegacySampler NormalizeLegacySampler(Sampler sampler, string description)
        {
            var source = sampler.Times;
            if (source == null) throw InvalidExpression($"{description} has no input times.");
            if (sampler.SingleKey)
            {
                if (source.Length != 1 || !IsFinite(source[0]) || source[0] < 0f)
                    throw InvalidExpression($"{description} has invalid legacy single-key input.");
                return new NormalizedLegacySampler
                {
                    Times = new[] { 0f, 1f },
                    SourceKeyCount = 1,
                    SynthesizedSingleKey = true,
                    Interpolation = InterpolationType.LINEAR,
                };
            }
            if (source.Length < 2)
                throw InvalidExpression($"{description} must contain at least two input keys.");

            float previous = source[0];
            if (!IsFinite(previous) || previous < 0f)
                throw InvalidExpression($"{description} input key 0 must be finite and nonnegative.");
            for (int index = 1; index < source.Length; index++)
            {
                float current = source[index];
                if (!IsFinite(current) || current < 0f || current <= previous)
                    throw InvalidExpression($"{description} input keys must be finite, nonnegative, and strictly increasing.");
                previous = current;
            }

            InterpolationType interpolation;
            switch (sampler.Interp)
            {
                case Interp.Step:
                    interpolation = InterpolationType.STEP;
                    break;
                case Interp.Linear:
                case Interp.CubicSpline:
                    interpolation = InterpolationType.LINEAR;
                    break;
                default:
                    throw InvalidExpression($"{description} has unknown interpolation {sampler.Interp}.");
            }

            float first = source[0];
            float duration = source[source.Length - 1] - first;
            var normalized = new float[source.Length];
            normalized[0] = 0f;
            for (int index = 1; index < normalized.Length; index++)
            {
                float value = index == normalized.Length - 1
                    ? 1f
                    : (source[index] - first) / duration;
                if (!IsFinite(value) || value <= normalized[index - 1])
                    throw InvalidExpression(
                        $"{description} input keys collapse after normalization to response progress.");
                normalized[index] = value;
            }
            return new NormalizedLegacySampler
            {
                Times = normalized,
                SourceKeyCount = source.Length,
                Interpolation = interpolation,
            };
        }

        internal static float[] BuildMorphValues(
            MorphDriver driver,
            NormalizedLegacySampler sampler,
            float authoredInitial,
            string description)
        {
            if (!IsFinite(driver.BaseValue))
                throw InvalidExpression($"{description} has a non-finite base weight.");
            RequireEquivalent(
                new[] { authoredInitial },
                new[] { driver.BaseValue },
                $"{description} base weight");
            if (driver.DeltaValues == null || driver.DeltaValues.Length != sampler.SourceKeyCount)
                throw InvalidExpression($"{description} output count does not match its input count.");

            if (sampler.SynthesizedSingleKey)
            {
                RequireFinite(driver.DeltaValues[0], $"{description} target weight");
                return new[] { authoredInitial, driver.DeltaValues[0] };
            }

            var values = new float[sampler.SourceKeyCount];
            for (int index = 0; index < values.Length; index++)
            {
                RequireFinite(driver.DeltaValues[index], $"{description} output {index}");
                values[index] = driver.BaseValue + driver.DeltaValues[index];
                RequireFinite(values[index], $"{description} reconstructed output {index}");
            }
            return values;
        }

        internal static Vector3[] BuildJointVectorValues(
            JointDriver driver,
            NormalizedLegacySampler sampler,
            string description)
        {
            RequireFinite(driver.BaseVec, $"{description} base value");
            if (driver.DeltaVec == null || driver.DeltaVec.Length != sampler.SourceKeyCount)
                throw InvalidExpression($"{description} output count does not match its input count.");
            if (sampler.SynthesizedSingleKey)
            {
                RequireFinite(driver.DeltaVec[0], $"{description} target value");
                return new[] { driver.BaseVec, driver.DeltaVec[0] };
            }

            var values = new Vector3[sampler.SourceKeyCount];
            for (int index = 0; index < values.Length; index++)
            {
                RequireFinite(driver.DeltaVec[index], $"{description} output {index}");
                values[index] = driver.BaseVec + driver.DeltaVec[index];
                RequireFinite(values[index], $"{description} reconstructed output {index}");
            }
            return values;
        }

        internal static Quaternion[] BuildJointRotationValues(
            JointDriver driver,
            NormalizedLegacySampler sampler,
            string description)
        {
            RequireQuaternion(driver.BaseQuat, $"{description} base rotation");
            if (driver.DeltaQuat == null || driver.DeltaQuat.Length != sampler.SourceKeyCount)
                throw InvalidExpression($"{description} output count does not match its input count.");
            if (sampler.SynthesizedSingleKey)
            {
                RequireQuaternion(driver.DeltaQuat[0], $"{description} target rotation");
                return new[] { driver.BaseQuat, driver.DeltaQuat[0] };
            }

            var values = new Quaternion[sampler.SourceKeyCount];
            for (int index = 0; index < values.Length; index++)
            {
                RequireQuaternion(driver.DeltaQuat[index], $"{description} output {index}");
                values[index] = driver.DeltaQuat[index] * driver.BaseQuat;
                RequireQuaternion(values[index], $"{description} reconstructed output {index}");
            }
            return values;
        }

        internal static TextureCurves BuildTextureCurves(
            TextureDriver driver,
            NormalizedLegacySampler sampler,
            string description)
        {
            RequireFinite(driver.BaseSt, $"{description} base texture transform");
            if (driver.StValues == null || driver.StValues.Length != sampler.SourceKeyCount)
                throw InvalidExpression($"{description} output count does not match its input count.");

            Vector4[] values;
            if (sampler.SynthesizedSingleKey)
            {
                RequireFinite(driver.StValues[0], $"{description} target texture transform");
                values = new[] { driver.BaseSt, driver.StValues[0] };
            }
            else
            {
                values = new Vector4[sampler.SourceKeyCount];
                for (int index = 0; index < values.Length; index++)
                {
                    RequireFinite(driver.StValues[index], $"{description} output {index}");
                    values[index] = driver.BaseSt + driver.StValues[index];
                    RequireFinite(values[index], $"{description} reconstructed output {index}");
                }
            }

            var scale = new Vector2[values.Length];
            var offset = new Vector2[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                scale[index] = GltfTextureScale(values[index]);
                offset[index] = GltfTextureOffset(values[index]);
                RequireFinite(scale[index], $"{description} scale output {index}");
                RequireFinite(offset[index], $"{description} offset output {index}");
            }
            return new TextureCurves { Scale = scale, Offset = offset };
        }

        internal static float[] ToGltfTranslation(Vector3 value)
        {
            return new[] { -value.x, value.y, value.z };
        }

        internal static float[] ToGltfRotation(Quaternion value)
        {
            var converted = value.SwitchHandedness().normalized;
            return new[] { converted.x, converted.y, converted.z, converted.w };
        }

        internal static float[] ToComponents(Vector2 value) => new[] { value.x, value.y };
        internal static float[] ToComponents(Vector3 value) => new[] { value.x, value.y, value.z };
        internal static float[] ToComponents(Vector4 value) => new[] { value.x, value.y, value.z, value.w };

        internal static void RequireEquivalent(float[] expected, float[] actual, string description)
        {
            if (!ExpressionInitialValueValidation.ComponentsEquivalent(
                    expected,
                    actual,
                    ExpressionAccessorComponentEncoding.Float))
                throw InvalidExpression($"{description} does not match the exported authored initial value.");
        }

        internal static void RequireFinite(float value, string description)
        {
            if (!IsFinite(value)) throw InvalidExpression($"{description} must be finite.");
        }

        internal static void RequireFinite(Vector2 value, string description)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y))
                throw InvalidExpression($"{description} must be finite.");
        }

        internal static void RequireFinite(Vector3 value, string description)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
                throw InvalidExpression($"{description} must be finite.");
        }

        internal static void RequireFinite(Vector4 value, string description)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z) || !IsFinite(value.w))
                throw InvalidExpression($"{description} must be finite.");
        }

        internal static void RequireQuaternion(Quaternion value, string description)
        {
            RequireFinite(new Vector4(value.x, value.y, value.z, value.w), description);
            float lengthSquared = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            if (!IsFinite(lengthSquared) || !(lengthSquared > 0f))
                throw InvalidExpression($"{description} must have finite nonzero length.");
        }

        internal static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static InvalidOperationException InvalidExpression(string message)
        {
            return new InvalidOperationException($"KHR_character_expression export is invalid: {message}");
        }

        /// <summary>The glTF <c>KHR_texture_transform</c> scale of a Unity <c>_ST</c> vector.</summary>
        internal static Vector2 GltfTextureScale(Vector4 st) => new Vector2(st.x, st.y);

        /// <summary>The glTF <c>KHR_texture_transform</c> offset of a Unity <c>_ST</c> vector, with UnityGLTF's V flip.</summary>
        internal static Vector2 GltfTextureOffset(Vector4 st) => new Vector2(st.z, 1f - st.w - st.y);

        /// <summary>
        /// Builds one response animation per track of <paramref name="set"/>, in glTF space, with the channels the exporter
        /// writes: each legacy sampler normalized to response progress, one channel per concrete target, and a channel whose
        /// keys never leave the authored value left unlisted. A property is keyed <c>authored:&lt;instanceId&gt;/...</c>
        /// after the Unity object it belongs to, and its binding takes the authored value from the drivers' bases. A track
        /// that the exporter would refuse produces no animation and one warning: one whose curves are invalid, including
        /// invalid joint samples. Shared targets retain every supported channel for composition.
        /// </summary>
        internal static AuthoredResponses BuildResponseAnimations(CharacterExpressionSet set)
        {
            var tracks = set?.Expressions ?? Array.Empty<ExpressionTrack>();
            var animations = new ExpressionResponseAnimation[tracks.Length];
            var bindings = new List<ExpressionTargetBinding>();
            var bound = new HashSet<string>(StringComparer.Ordinal);
            var morphBases = ResolveMorphBases(tracks);
            for (int expressionIndex = 0; expressionIndex < tracks.Length; expressionIndex++)
            {
                var track = tracks[expressionIndex];
                if (track == null) continue;
                var builder = new TrackBuilder(expressionIndex, morphBases);
                ExpressionResponseAnimation animation;
                try
                {
                    builder.AddTrack(track);
                    animation = builder.Build();
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException || exception is ExpressionResponseEvaluationException)
                {
                    Debug.LogWarning(
                        $"[KHR_character] Expression {expressionIndex} ('{track.Name}') produces no output: " +
                        exception.Message);
                    continue;
                }
                animations[expressionIndex] = animation;
                foreach (var binding in builder.Bindings)
                    if (bound.Add(binding.Property)) bindings.Add(binding);
            }
            return new AuthoredResponses { Animations = animations, Bindings = bindings.ToArray() };
        }

        private static string DescribeProperty(string property, List<ExpressionTargetBinding> bindings)
        {
            foreach (var binding in bindings)
            {
                if (!string.Equals(binding.Property, property, StringComparison.Ordinal)) continue;
                if (binding.Node != null)
                    return $"the {property.Substring(property.LastIndexOf('/') + 1)} of '{binding.Node.name}'";
                if (binding.TextureRenderers != null && binding.TextureRenderers.Length > 0 &&
                    binding.TextureRenderers[0] != null)
                    return $"the {binding.TextureStProperty} scale of '{binding.TextureRenderers[0].name}'";
            }
            return $"'{property}'";
        }

        private sealed class TrackBuilder
        {
            private readonly int _expressionIndex;
            private readonly Dictionary<SkinnedMeshRenderer, double[]> _morphBases;
            private readonly List<ExpressionResponseSampler> _samplers = new List<ExpressionResponseSampler>();
            private readonly List<ExpressionResponseChannel> _channels = new List<ExpressionResponseChannel>();
            private readonly Dictionary<string, ExpressionResponseSampler> _targets =
                new Dictionary<string, ExpressionResponseSampler>(StringComparer.Ordinal);
            private readonly HashSet<string> _bound = new HashSet<string>(StringComparer.Ordinal);

            public readonly List<ExpressionTargetBinding> Bindings = new List<ExpressionTargetBinding>();

            public TrackBuilder(int expressionIndex, Dictionary<SkinnedMeshRenderer, double[]> morphBases)
            {
                _expressionIndex = expressionIndex;
                _morphBases = morphBases;
            }

            public void AddTrack(ExpressionTrack track)
            {
                if (track.MorphDrivers != null)
                    for (int index = 0; index < track.MorphDrivers.Length; index++)
                        AddMorph(track.MorphDrivers[index], $"Expression {_expressionIndex} morph driver {index}");
                if (track.JointDrivers != null)
                    for (int index = 0; index < track.JointDrivers.Length; index++)
                        AddJoint(track.JointDrivers[index], $"Expression {_expressionIndex} joint driver {index}");
                if (track.TextureDrivers != null)
                    for (int index = 0; index < track.TextureDrivers.Length; index++)
                        AddTexture(track.TextureDrivers[index], $"Expression {_expressionIndex} texture driver {index}");
            }

            public ExpressionResponseAnimation Build()
            {
                if (_channels.Count == 0) return null;
                var animation = new ExpressionResponseAnimation
                {
                    Samplers = _samplers.ToArray(),
                    Channels = _channels.ToArray(),
                };
                ExpressionResponseEvaluator.Evaluate(animation, 0d);
                return animation;
            }

            private void AddMorph(MorphDriver driver, string description)
            {
                if (driver == null) throw InvalidExpression($"{description} is null.");
                var renderer = driver.Smr;
                if (renderer == null || renderer.sharedMesh == null)
                    throw InvalidExpression($"{description} has no renderer mesh.");
                var weights = _morphBases[renderer];
                int element = driver.BlendShapeIndex;
                if (element < 0 || element >= weights.Length)
                    throw InvalidExpression($"{description} targets an invalid morph index {element}.");

                var sampler = NormalizeLegacySampler(driver.Sampler, description);
                var values = BuildMorphValues(driver, sampler, (float)weights[element], description);
                var outputs = new float[values.Length][];
                bool constant = true;
                for (int key = 0; key < values.Length; key++)
                {
                    outputs[key] = new[] { values[key] };
                    constant &= values[key] == weights[element];
                }
                string property = Key(renderer.transform, "weights");
                AddChannel(
                    ExpressionPropertyIdentity.ForArrayElement(property, element),
                    ExpressionPropertyKind.NodeWeights,
                    ExpressionResponseValueKind.Components,
                    new[] { weights[element] },
                    weights.Length,
                    weights,
                    sampler,
                    outputs,
                    constant,
                    KhrCharacterExtensionNames.ExpressionMorphtarget,
                    description);
                Bind(new ExpressionTargetBinding
                {
                    Property = property,
                    Kind = ExpressionPropertyKind.NodeWeights,
                    AuthoredInitial = (double[])weights.Clone(),
                    MorphRenderers = new[] { renderer },
                    MorphFrameWeights = FrameWeights(renderer.sharedMesh, weights.Length),
                });
            }

            private void AddJoint(JointDriver driver, string description)
            {
                if (driver == null) throw InvalidExpression($"{description} is null.");
                var node = driver.Target;
                if (node == null) throw InvalidExpression($"{description} has no target transform.");

                var sampler = NormalizeLegacySampler(driver.Sampler, description);
                string path;
                ExpressionPropertyKind kind;
                var valueKind = ExpressionResponseValueKind.Components;
                float[] authored;
                float[][] outputs;
                bool constant = true;
                switch (driver.Channel)
                {
                    case TrsChannel.Translation:
                    {
                        path = "translation";
                        kind = ExpressionPropertyKind.NodeTranslation;
                        var values = BuildJointVectorValues(driver, sampler, description);
                        authored = ToGltfTranslation(driver.BaseVec);
                        outputs = new float[values.Length][];
                        for (int key = 0; key < values.Length; key++)
                        {
                            outputs[key] = ToGltfTranslation(values[key]);
                            constant &= SameComponents(authored, outputs[key]);
                        }
                        break;
                    }
                    case TrsChannel.Scale:
                    {
                        path = "scale";
                        kind = ExpressionPropertyKind.NodeScale;
                        var values = BuildJointVectorValues(driver, sampler, description);
                        authored = ToComponents(driver.BaseVec);
                        outputs = new float[values.Length][];
                        for (int key = 0; key < values.Length; key++)
                        {
                            outputs[key] = ToComponents(values[key]);
                            constant &= SameComponents(authored, outputs[key]);
                        }
                        break;
                    }
                    case TrsChannel.Rotation:
                    {
                        path = "rotation";
                        kind = ExpressionPropertyKind.NodeRotation;
                        valueKind = ExpressionResponseValueKind.Quaternion;
                        var values = BuildJointRotationValues(driver, sampler, description);
                        authored = ToGltfRotation(driver.BaseQuat);
                        outputs = new float[values.Length][];
                        for (int key = 0; key < values.Length; key++)
                        {
                            outputs[key] = ToGltfRotation(values[key]);
                            constant &= SameComponents(authored, outputs[key]) ||
                                        SameComponents(authored, Negated(outputs[key]));
                        }
                        break;
                    }
                    default:
                        throw InvalidExpression($"{description} has unknown TRS channel {driver.Channel}.");
                }

                string property = Key(node, path);
                var initial = ToDoubles(authored);
                AddChannel(
                    ExpressionPropertyIdentity.ForProperty(property),
                    kind,
                    valueKind,
                    initial,
                    0,
                    null,
                    sampler,
                    outputs,
                    constant,
                    KhrCharacterExtensionNames.ExpressionJoint,
                    description);
                Bind(new ExpressionTargetBinding
                {
                    Property = property,
                    Kind = kind,
                    AuthoredInitial = initial,
                    Node = node,
                });
            }

            private void AddTexture(TextureDriver driver, string description)
            {
                if (driver == null) throw InvalidExpression($"{description} is null.");
                var renderer = driver.Renderer;
                if (renderer == null) throw InvalidExpression($"{description} has no renderer.");
                if (driver.TransformTarget != TextureTransformTarget.Combined &&
                    driver.TransformTarget != TextureTransformTarget.Scale &&
                    driver.TransformTarget != TextureTransformTarget.Offset)
                    throw InvalidExpression($"{description} has an unknown texture-transform target.");
                var materials = renderer.sharedMaterials;
                if (materials == null || driver.SubmeshSlot < 0 || driver.SubmeshSlot >= materials.Length)
                    throw InvalidExpression($"{description} has an invalid material slot.");
                string stProperty = ResolveStProperty(driver, materials[driver.SubmeshSlot]);
                if (stProperty == null)
                    throw InvalidExpression($"{description} names no texture scale-and-offset property.");

                var sampler = NormalizeLegacySampler(driver.Sampler, description);
                var curves = BuildTextureCurves(driver, sampler, description);
                var authoredScale = ToComponents(GltfTextureScale(driver.BaseSt));
                var authoredOffset = ToComponents(GltfTextureOffset(driver.BaseSt));
                string transform = $"{KeyPrefix}{ObjectId(renderer)}/{driver.SubmeshSlot}/{stProperty}/extensions/" +
                                   ExtTextureTransformExtensionFactory.EXTENSION_NAME;
                if (driver.TransformTarget != TextureTransformTarget.Offset)
                    AddTextureChannel(
                        $"{transform}/{ExtTextureTransformExtensionFactory.SCALE}",
                        ExpressionPropertyKind.TextureScale,
                        curves.Scale,
                        authoredScale,
                        sampler,
                        description);
                if (driver.TransformTarget != TextureTransformTarget.Scale)
                    AddTextureChannel(
                        $"{transform}/{ExtTextureTransformExtensionFactory.OFFSET}",
                        ExpressionPropertyKind.TextureOffset,
                        curves.Offset,
                        authoredOffset,
                        sampler,
                        description);
                BindTexture(transform, ExtTextureTransformExtensionFactory.OFFSET, ExpressionPropertyKind.TextureOffset,
                    authoredOffset, renderer, driver.SubmeshSlot, stProperty);
                BindTexture(transform, ExtTextureTransformExtensionFactory.SCALE, ExpressionPropertyKind.TextureScale,
                    authoredScale, renderer, driver.SubmeshSlot, stProperty);
            }

            private void AddTextureChannel(
                string property,
                ExpressionPropertyKind kind,
                Vector2[] values,
                float[] authored,
                NormalizedLegacySampler sampler,
                string description)
            {
                var outputs = new float[values.Length][];
                bool constant = true;
                for (int key = 0; key < values.Length; key++)
                {
                    outputs[key] = ToComponents(values[key]);
                    constant &= SameComponents(authored, outputs[key]);
                }
                AddChannel(
                    ExpressionPropertyIdentity.ForProperty(property),
                    kind,
                    ExpressionResponseValueKind.Components,
                    ToDoubles(authored),
                    0,
                    null,
                    sampler,
                    outputs,
                    constant,
                    KhrCharacterExtensionNames.ExpressionTextureTransform,
                    description);
            }

            private void BindTexture(
                string transform,
                string component,
                ExpressionPropertyKind kind,
                float[] authored,
                Renderer renderer,
                int slot,
                string stProperty)
            {
                Bind(new ExpressionTargetBinding
                {
                    Property = $"{transform}/{component}",
                    Kind = kind,
                    AuthoredInitial = ToDoubles(authored),
                    TextureTransform = transform,
                    TextureRenderers = new[] { renderer },
                    TextureMaterialSlots = new[] { slot },
                    TextureStProperty = stProperty,
                });
            }

            private void AddChannel(
                ExpressionPropertyIdentity identity,
                ExpressionPropertyKind kind,
                ExpressionResponseValueKind valueKind,
                double[] authored,
                int arrayLength,
                double[] arrayAuthored,
                NormalizedLegacySampler sampler,
                float[][] outputs,
                bool constant,
                string listedBy,
                string description)
            {
                var responseSampler = new ExpressionResponseSampler
                {
                    InputTimes = sampler.Times,
                    OutputValues = outputs,
                    Interpolation = sampler.Interpolation == InterpolationType.STEP
                        ? ExpressionResponseInterpolation.Step
                        : ExpressionResponseInterpolation.Linear,
                    OutputEncoding = ExpressionAccessorComponentEncoding.Float,
                };
                string key = identity.ToString();
                if (_targets.TryGetValue(key, out var existing))
                {
                    // Like the exporter, identical texture curves for one concrete target are written once.
                    bool texture = kind == ExpressionPropertyKind.TextureOffset || kind == ExpressionPropertyKind.TextureScale;
                    if (texture && SameSampler(existing, responseSampler)) return;
                    throw InvalidExpression($"{description} duplicates concrete target '{key}'.");
                }
                _targets.Add(key, responseSampler);
                _samplers.Add(responseSampler);
                _channels.Add(new ExpressionResponseChannel
                {
                    SamplerIndex = _samplers.Count - 1,
                    Target = new ExpressionResponseTarget(
                        identity,
                        outputs[0].Length,
                        valueKind,
                        true,
                        kind,
                        authored,
                        arrayLength,
                        arrayAuthored),
                    ListedBy = constant ? null : listedBy,
                    EmitsRecords = !constant,
                });
            }

            private void Bind(ExpressionTargetBinding binding)
            {
                if (_bound.Add(binding.Property)) Bindings.Add(binding);
            }
        }

        // The base vector B of each driven renderer: a driven element takes its first driver's base, any other element
        // the renderer's current weight, which is what the exporter writes as the node's weights.
        private static Dictionary<SkinnedMeshRenderer, double[]> ResolveMorphBases(ExpressionTrack[] tracks)
        {
            var bases = new Dictionary<SkinnedMeshRenderer, double[]>();
            var driven = new Dictionary<SkinnedMeshRenderer, bool[]>();
            foreach (var track in tracks)
            {
                if (track?.MorphDrivers == null) continue;
                foreach (var driver in track.MorphDrivers)
                {
                    var renderer = driver?.Smr;
                    var mesh = renderer != null ? renderer.sharedMesh : null;
                    if (mesh == null) continue;
                    if (!bases.TryGetValue(renderer, out var weights))
                    {
                        weights = new double[mesh.blendShapeCount];
                        for (int element = 0; element < weights.Length; element++)
                            weights[element] = renderer.GetBlendShapeWeight(element) / FrameWeight(mesh, element);
                        bases.Add(renderer, weights);
                        driven.Add(renderer, new bool[weights.Length]);
                    }
                    int index = driver.BlendShapeIndex;
                    if (index < 0 || index >= weights.Length || driven[renderer][index]) continue;
                    driven[renderer][index] = true;
                    weights[index] = driver.BaseValue;
                }
            }
            return bases;
        }

        private static float[] FrameWeights(Mesh mesh, int count)
        {
            var weights = new float[count];
            for (int element = 0; element < count; element++) weights[element] = FrameWeight(mesh, element);
            return weights;
        }

        // UnityGLTF adds one blendshape frame per morph target; a glTF weight w is the Unity weight w * frameWeight.
        private static float FrameWeight(Mesh mesh, int blendShape)
        {
            float weight = blendShape < mesh.blendShapeCount && mesh.GetBlendShapeFrameCount(blendShape) > 0
                ? mesh.GetBlendShapeFrameWeight(blendShape, 0)
                : 1f;
            return weight != 0f ? weight : 1f;
        }

        private static string ResolveStProperty(TextureDriver driver, Material material)
        {
            if (!string.IsNullOrEmpty(driver.PropertyName))
                return driver.PropertyName.EndsWith("_ST", StringComparison.Ordinal)
                    ? driver.PropertyName
                    : driver.PropertyName + "_ST";
            if (material == null) return null;
            foreach (var name in material.GetTexturePropertyNames())
                if (Shader.PropertyToID(name + "_ST") == driver.PropertyId)
                    return name + "_ST";
            return null;
        }

        private static bool SameSampler(ExpressionResponseSampler left, ExpressionResponseSampler right)
        {
            if (left.Interpolation != right.Interpolation || left.InputTimes.Length != right.InputTimes.Length ||
                left.OutputValues.Length != right.OutputValues.Length)
                return false;
            for (int key = 0; key < left.InputTimes.Length; key++)
                if (left.InputTimes[key] != right.InputTimes[key]) return false;
            for (int record = 0; record < left.OutputValues.Length; record++)
                if (!SameComponents(left.OutputValues[record], right.OutputValues[record])) return false;
            return true;
        }

        private static string Key(UnityEngine.Object owner, string path) => $"{KeyPrefix}{ObjectId(owner)}/{path}";

        private static string ObjectId(UnityEngine.Object owner)
        {
#if UNITY_6000_4_OR_NEWER
            return owner.GetEntityId().ToString();
#else
            return owner.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }

        private static double[] ToDoubles(float[] values)
        {
            var result = new double[values.Length];
            for (int index = 0; index < result.Length; index++) result[index] = values[index];
            return result;
        }
    }
}
