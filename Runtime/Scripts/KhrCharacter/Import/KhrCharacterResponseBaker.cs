using System;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Mathematics;
using UnityGLTF.Extensions;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Decodes KHR_character_expression animations into passive, absolute wire-space response data. It processes
    /// every animation channel and never resolves or writes a Unity scene target.
    /// </summary>
    internal static class KhrCharacterResponseBaker
    {
        private sealed class ResolvedTarget
        {
            public ExpressionResponseTarget Target;
            public GLTFAccessorAttributeType OutputType;
            public float[] AuthoredInitial;
            public bool ScalarPackedArray;
            public bool AllowsNormalizedIntegerOutput;
            public CoreNodeTargetSource Source;
        }

        private sealed class DecodedAccessor
        {
            public float[][] Values;
            public ExpressionAccessorComponentEncoding Encoding;
        }

        private enum CoreNodeTargetSource
        {
            AnimationChannel,
            AnimationPointer,
        }

        public static ExpressionResponseSetEntry[] Bake(
            GLTFRoot root,
            GLTFSceneImporter importer,
            KHR_character_expression extension)
        {
            if (importer == null) throw new ArgumentNullException(nameof(importer));
            return Bake(root, ReadBufferView, extension);

            byte[] ReadBufferView(BufferView bufferView)
            {
                var native = importer.GetBufferViewData(bufferView);
                return native.IsCreated ? native.ToArray() : Array.Empty<byte>();
            }
        }

        internal static ExpressionResponseSetEntry[] Bake(
            GLTFRoot root,
            Func<BufferView, byte[]> readBufferView,
            KHR_character_expression extension)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (readBufferView == null) throw new ArgumentNullException(nameof(readBufferView));
            if (extension?.Expressions == null || extension.Expressions.Count == 0)
                return Array.Empty<ExpressionResponseSetEntry>();

            var entries = new ExpressionResponseSetEntry[extension.Expressions.Count];
            for (int expressionIndex = 0; expressionIndex < entries.Length; expressionIndex++)
            {
                var item = extension.Expressions[expressionIndex];
                if (item == null || string.IsNullOrEmpty(item.Expression))
                    throw Invalid($"Expression entry {expressionIndex} has no nonempty label.");
                if (root.Animations == null || item.Animation < 0 || item.Animation >= root.Animations.Count)
                    throw Invalid(
                        $"Expression entry {expressionIndex} references invalid animation {item.Animation}.");

                var animation = BakeAnimation(root, readBufferView, root.Animations[item.Animation]);
                entries[expressionIndex] = new ExpressionResponseSetEntry(
                    item.Expression,
                    item.Animation,
                    animation,
                    item.RawExtensions?.ToString(Formatting.None),
                    item.Extras?.ToString(Formatting.None));
            }
            return entries;
        }

        private static ExpressionResponseAnimation BakeAnimation(
            GLTFRoot root,
            Func<BufferView, byte[]> readBufferView,
            GLTFAnimation animation)
        {
            if (animation?.Channels == null || animation.Channels.Count == 0)
                throw Invalid("An expression animation must contain at least one channel.");
            if (animation.Samplers == null)
                throw Invalid("An expression animation has no sampler array.");

            var samplers = new ExpressionResponseSampler[animation.Samplers.Count];
            var channels = new ExpressionResponseChannel[animation.Channels.Count];
            for (int channelIndex = 0; channelIndex < animation.Channels.Count; channelIndex++)
            {
                var channel = animation.Channels[channelIndex];
                int samplerIndex = channel?.Sampler?.Id ?? -1;
                if (samplerIndex < 0 || samplerIndex >= animation.Samplers.Count)
                    throw Invalid($"Channel {channelIndex} references invalid sampler {samplerIndex}.");

                var gltfSampler = animation.Samplers[samplerIndex];
                var interpolation = ConvertInterpolation(gltfSampler.Interpolation, samplerIndex);
                var inputAccessor = GetAccessor(root, gltfSampler.Input, $"sampler {samplerIndex} input");
                ValidateInputAccessor(inputAccessor, samplerIndex);
                var decodedInput = DecodeAccessor(inputAccessor, readBufferView);
                var inputTimes = FlattenScalar(decodedInput, $"sampler {samplerIndex} input");
                ValidateDecodedInputMetadata(inputAccessor, inputTimes, samplerIndex);

                if (samplers[samplerIndex] == null)
                {
                    samplers[samplerIndex] = new ExpressionResponseSampler
                    {
                        InputTimes = inputTimes,
                        Interpolation = interpolation,
                    };
                }
                else
                {
                    EnsureSameInput(samplers[samplerIndex], inputTimes, interpolation, samplerIndex);
                }

                var resolved = ResolveTarget(root, channel, channelIndex);
                channels[channelIndex] = new ExpressionResponseChannel
                {
                    SamplerIndex = samplerIndex,
                    Target = resolved.Target,
                };
                var outputAccessor = GetAccessor(root, gltfSampler.Output, $"sampler {samplerIndex} output");
                if (!resolved.Target.IsSupported) continue;

                ValidateOutputAccessor(outputAccessor, resolved, inputTimes.Length, interpolation, channelIndex);
                var decodedOutput = DecodeAccessor(outputAccessor, readBufferView);
                var logicalValues = resolved.ScalarPackedArray
                    ? ExpressionResponseSampler.FromDecodedScalarStream(
                        inputTimes,
                        FlattenScalar(decodedOutput, $"channel {channelIndex} output"),
                        resolved.Target.ComponentCount,
                        interpolation).OutputValues
                    : decodedOutput.Values;

                if (samplers[samplerIndex].OutputValues == null)
                {
                    samplers[samplerIndex].OutputValues = logicalValues;
                    samplers[samplerIndex].OutputEncoding = decodedOutput.Encoding;
                }
                else
                {
                    EnsureSameOutput(samplers[samplerIndex].OutputValues, logicalValues, samplerIndex);
                    if (samplers[samplerIndex].OutputEncoding != decodedOutput.Encoding)
                        throw Invalid($"Shared sampler {samplerIndex} decoded with inconsistent output encodings.");
                }

                if (!ExpressionInitialValueValidation.MatchesTimeZeroSample(
                        resolved.AuthoredInitial,
                        samplers[samplerIndex],
                        resolved.Target,
                        decodedOutput.Encoding))
                    throw Invalid(
                        $"Channel {channelIndex} time-zero sample does not match its authored initial value.");
            }

            var result = new ExpressionResponseAnimation { Samplers = samplers, Channels = channels };
            ExpressionResponseEvaluator.Evaluate(result, 0f);
            return result;
        }

        private static ResolvedTarget ResolveTarget(GLTFRoot root, AnimationChannel channel, int channelIndex)
        {
            if (channel?.Target == null) throw Invalid($"Channel {channelIndex} has no target.");
            var target = channel.Target;
            if (TryGetPointer(target, out string pointer))
            {
                if (target.Path != "pointer")
                    throw Invalid($"Channel {channelIndex} uses KHR_animation_pointer without path 'pointer'.");
                if (target.Node != null)
                    throw Invalid($"Channel {channelIndex} uses KHR_animation_pointer with a node target.");
                if (!TryParsePointer(pointer, out string[] segments))
                    throw Invalid($"Channel {channelIndex} has an empty or malformed JSON pointer.");
                if (ContainsExtrasSegment(segments))
                    throw Invalid($"Channel {channelIndex} pointer targets extras, which is not portable.");
                if (TryResolveCoreNodePointer(
                        root,
                        pointer,
                        segments,
                        channelIndex,
                        out var resolved))
                    return resolved;
                return new ResolvedTarget
                {
                    Target = new ExpressionResponseTarget(
                        ExpressionPropertyIdentity.ForProperty(pointer),
                        0,
                        ExpressionResponseValueKind.Components,
                        false),
                    Source = CoreNodeTargetSource.AnimationPointer,
                };
            }

            if (target.Path == "pointer")
                throw Invalid($"Channel {channelIndex} has path 'pointer' without KHR_animation_pointer.");

            if (target.Node != null)
                return ResolveCoreNodeTarget(
                    root,
                    target.Node.Id,
                    target.Path,
                    null,
                    channelIndex,
                    CoreNodeTargetSource.AnimationChannel);

            if (target.Extensions != null && target.Extensions.Count > 0)
                return new ResolvedTarget
                {
                    Target = ExpressionResponseTarget.UnsupportedUnresolved(
                        $"channel/{channelIndex}/extension-target"),
                };
            throw Invalid($"Channel {channelIndex} does not resolve to a concrete core or extension property.");
        }

        private static ResolvedTarget ResolveCoreNodeTarget(
            GLTFRoot root,
            int nodeIndex,
            string path,
            int? element,
            int channelIndex,
            CoreNodeTargetSource source)
        {
            if (root.Nodes == null || nodeIndex < 0 || nodeIndex >= root.Nodes.Count)
                throw Invalid($"Channel {channelIndex} references invalid node {nodeIndex}.");
            var node = root.Nodes[nodeIndex];
            if (node == null) throw Invalid($"Channel {channelIndex} references a null node.");
            bool isMatrixBacked =
                node.HasMatrix || node.Matrix == null || !node.Matrix.Equals(GLTF.Math.Matrix4x4.Identity);
            bool isDefinedMatrixPointerTranslation =
                source == CoreNodeTargetSource.AnimationPointer && path == "translation" && isMatrixBacked;
            if (path != "weights" && isMatrixBacked && !isDefinedMatrixPointerTranslation)
                throw Invalid($"Channel {channelIndex} targets TRS on a matrix-backed node.");
            if (isDefinedMatrixPointerTranslation && node.Matrix == null)
                throw Invalid($"Channel {channelIndex} targets translation on a node with no readable matrix.");

            string canonicalProperty = $"/nodes/{nodeIndex}/{path}";
            int componentCount;
            GLTFAccessorAttributeType outputType;
            ExpressionResponseValueKind valueKind = ExpressionResponseValueKind.Components;
            float[] authored;
            bool scalarPacked = false;
            bool allowsNormalizedIntegerOutput = false;

            switch (path)
            {
                case "translation":
                    componentCount = 3;
                    outputType = GLTFAccessorAttributeType.VEC3;
                    authored = isDefinedMatrixPointerTranslation
                        ? new[] { node.Matrix.M14, node.Matrix.M24, node.Matrix.M34 }
                        : new[] { node.Translation.X, node.Translation.Y, node.Translation.Z };
                    break;
                case "rotation":
                    componentCount = 4;
                    outputType = GLTFAccessorAttributeType.VEC4;
                    valueKind = ExpressionResponseValueKind.Quaternion;
                    authored = new[] { node.Rotation.X, node.Rotation.Y, node.Rotation.Z, node.Rotation.W };
                    if (!IsUnitQuaternion(authored))
                        throw Invalid($"Channel {channelIndex} targets a node with an invalid authored rotation.");
                    allowsNormalizedIntegerOutput = true;
                    break;
                case "scale":
                    componentCount = 3;
                    outputType = GLTFAccessorAttributeType.VEC3;
                    authored = new[] { node.Scale.X, node.Scale.Y, node.Scale.Z };
                    break;
                case "weights":
                    componentCount = GetMorphTargetCount(node);
                    if (componentCount < 1)
                        throw Invalid($"Channel {channelIndex} targets weights on a node with no morph targets.");
                    outputType = GLTFAccessorAttributeType.SCALAR;
                    authored = ResolveMorphWeights(node, componentCount);
                    scalarPacked = true;
                    allowsNormalizedIntegerOutput = true;
                    break;
                default:
                    throw Invalid($"Channel {channelIndex} has unsupported core target path '{path}'.");
            }

            ExpressionPropertyIdentity identity;
            if (element.HasValue)
            {
                if (element.Value < 0 || element.Value >= componentCount)
                    throw Invalid(
                        $"Channel {channelIndex} targets element {element.Value} of a {componentCount}-component property.");
                authored = new[] { authored[element.Value] };
                componentCount = 1;
                outputType = GLTFAccessorAttributeType.SCALAR;
                valueKind = ExpressionResponseValueKind.Components;
                scalarPacked = false;
                identity = ExpressionPropertyIdentity.ForArrayElement(canonicalProperty, element.Value);
            }
            else
            {
                identity = ExpressionPropertyIdentity.ForWholeArray(canonicalProperty);
            }

            return new ResolvedTarget
            {
                Target = new ExpressionResponseTarget(identity, componentCount, valueKind),
                OutputType = outputType,
                AuthoredInitial = authored,
                ScalarPackedArray = scalarPacked,
                AllowsNormalizedIntegerOutput = allowsNormalizedIntegerOutput,
                Source = source,
            };
        }

        private static bool TryResolveCoreNodePointer(
            GLTFRoot root,
            string pointer,
            string[] segments,
            int channelIndex,
            out ResolvedTarget target)
        {
            target = null;
            if (segments.Length < 2 || segments[0] != "nodes") return false;
            if (!TryParseArrayIndex(segments[1], out int nodeIndex))
                throw Invalid($"Channel {channelIndex} has a noncanonical node array index in '{pointer}'.");
            if (segments.Length < 3) return false;
            string path = segments[2];
            if (path != "translation" && path != "rotation" && path != "scale" && path != "weights")
                return false;
            int? element = null;
            if (path == "weights")
            {
                if (segments.Length != 3 && segments.Length != 4)
                    throw Invalid($"Channel {channelIndex} has an invalid weights pointer '{pointer}'.");
                if (segments.Length == 4)
                {
                    if (!TryParseArrayIndex(segments[3], out int parsedElement))
                        throw Invalid(
                            $"Channel {channelIndex} has a noncanonical weights array index in '{pointer}'.");
                    element = parsedElement;
                }
            }
            else if (segments.Length != 3)
                throw Invalid(
                    $"Channel {channelIndex} selects an element of '{path}', which is not an Object Model pointer.");
            target = ResolveCoreNodeTarget(
                root,
                nodeIndex,
                path,
                element,
                channelIndex,
                CoreNodeTargetSource.AnimationPointer);
            return true;
        }

        private static bool TryParsePointer(string pointer, out string[] segments)
        {
            segments = null;
            if (string.IsNullOrEmpty(pointer) || pointer[0] != '/') return false;
            var raw = pointer.Substring(1).Split('/');
            for (int index = 0; index < raw.Length; index++)
            {
                for (int character = 0; character < raw[index].Length; character++)
                {
                    if (raw[index][character] != '~') continue;
                    if (++character >= raw[index].Length ||
                        (raw[index][character] != '0' && raw[index][character] != '1'))
                        return false;
                }
                raw[index] = raw[index].Replace("~1", "/").Replace("~0", "~");
            }
            segments = raw;
            return true;
        }

        private static bool TryParseArrayIndex(string token, out int index)
        {
            index = 0;
            if (string.IsNullOrEmpty(token)) return false;
            if (token.Length > 1 && token[0] == '0') return false;
            for (int character = 0; character < token.Length; character++)
            {
                char digit = token[character];
                if (digit < '0' || digit > '9') return false;
                int value = digit - '0';
                if (index > (int.MaxValue - value) / 10) return false;
                index = index * 10 + value;
            }
            return true;
        }

        private static bool ContainsExtrasSegment(string[] segments)
        {
            for (int index = 0; index < segments.Length; index++)
                if (segments[index] == "extras") return true;
            return false;
        }

        private static bool TryGetPointer(AnimationChannelTarget target, out string pointer)
        {
            pointer = null;
            if (target?.Extensions == null ||
                !target.Extensions.TryGetValue(KHR_animation_pointer.EXTENSION_NAME, out var extension))
                return false;
            if (extension == null) return true;
            if (extension is KHR_animation_pointer typed)
            {
                pointer = typed.path;
                return true;
            }
            JProperty serialized = extension.Serialize();
            pointer = (serialized?.Value as JObject)?["pointer"]?.Value<string>();
            return true;
        }

        private static int GetMorphTargetCount(Node node)
        {
            var mesh = node.Mesh?.Value;
            if (mesh?.Primitives != null)
            {
                int count = -1;
                foreach (var primitive in mesh.Primitives)
                {
                    int primitiveCount = primitive?.Targets?.Count ?? 0;
                    if (count < 0) count = primitiveCount;
                    else if (count != primitiveCount)
                        throw Invalid("All primitives of a morph-animated mesh must have the same target count.");
                }
                if (count > 0) return count;
            }
            if (node.Weights != null) return node.Weights.Count;
            return mesh?.Weights?.Count ?? 0;
        }

        private static float[] ResolveMorphWeights(Node node, int morphTargetCount)
        {
            return ExpressionInitialValueValidation.ResolveMorphWeights(
                ToFloats(node.Weights),
                ToFloats(node.Mesh?.Value?.Weights),
                morphTargetCount);
        }

        private static float[] ToFloats(IReadOnlyList<double> values)
        {
            if (values == null) return null;
            var result = new float[values.Count];
            for (int index = 0; index < result.Length; index++) result[index] = (float)values[index];
            return result;
        }

        private static bool IsUnitQuaternion(float[] value)
        {
            if (value == null || value.Length != 4) return false;
            double lengthSquared = 0d;
            for (int index = 0; index < value.Length; index++)
            {
                if (float.IsNaN(value[index]) || float.IsInfinity(value[index])) return false;
                lengthSquared += (double)value[index] * value[index];
            }
            return Math.Abs(lengthSquared - 1d) <= 1e-5d;
        }

        private static void ValidateInputAccessor(Accessor accessor, int samplerIndex)
        {
            if (accessor.Type != GLTFAccessorAttributeType.SCALAR ||
                accessor.ComponentType != GLTFComponentType.Float ||
                accessor.Normalized)
                throw Invalid($"Sampler {samplerIndex} input must be a non-normalized FLOAT SCALAR accessor.");
            if (accessor.Count < 2)
                throw Invalid($"Sampler {samplerIndex} input must contain at least two keys.");
            if (accessor.Min == null || accessor.Min.Count != 1 ||
                accessor.Max == null || accessor.Max.Count != 1)
                throw Invalid($"Sampler {samplerIndex} input must define scalar min and max metadata.");
        }

        private static void ValidateDecodedInputMetadata(Accessor accessor, float[] inputTimes, int samplerIndex)
        {
            if (inputTimes == null || inputTimes.Length < 2)
                throw Invalid($"Sampler {samplerIndex} input must decode at least two keys.");
            float metadataMinimum = (float)accessor.Min[0];
            float metadataMaximum = (float)accessor.Max[0];
            if (metadataMinimum != inputTimes[0] || metadataMaximum != inputTimes[inputTimes.Length - 1])
                throw Invalid(
                    $"Sampler {samplerIndex} input min/max metadata does not match decoded key extrema.");
        }

        private static void ValidateOutputAccessor(
            Accessor accessor,
            ResolvedTarget target,
            int keyCount,
            ExpressionResponseInterpolation interpolation,
            int channelIndex)
        {
            if (accessor.Type != target.OutputType)
                throw Invalid(
                    $"Channel {channelIndex} output accessor type {accessor.Type} does not match {target.OutputType}.");
            bool isFloat = accessor.ComponentType == GLTFComponentType.Float && !accessor.Normalized;
            bool isNormalizableInteger =
                accessor.ComponentType == GLTFComponentType.Byte ||
                accessor.ComponentType == GLTFComponentType.UnsignedByte ||
                accessor.ComponentType == GLTFComponentType.Short ||
                accessor.ComponentType == GLTFComponentType.UnsignedShort;
            bool isAnyInteger = isNormalizableInteger || accessor.ComponentType == GLTFComponentType.UnsignedInt;
            bool isPointerInteger = target.Source == CoreNodeTargetSource.AnimationPointer &&
                isAnyInteger && (!accessor.Normalized || isNormalizableInteger);
            bool isCoreNormalizedInteger = target.Source == CoreNodeTargetSource.AnimationChannel &&
                target.AllowsNormalizedIntegerOutput && accessor.Normalized && isNormalizableInteger;
            if (!isFloat && !isPointerInteger && !isCoreNormalizedInteger)
                throw Invalid(
                    $"Channel {channelIndex} output encoding is invalid for its target property.");
            long recordsPerKey = interpolation == ExpressionResponseInterpolation.CubicSpline ? 3L : 1L;
            long expectedCount = (long)keyCount * recordsPerKey;
            if (target.ScalarPackedArray) expectedCount *= target.Target.ComponentCount;
            if (accessor.Count != expectedCount)
                throw Invalid(
                    $"Channel {channelIndex} output accessor count is {accessor.Count}; expected {expectedCount}.");
        }

        private static DecodedAccessor DecodeAccessor(
            Accessor accessor,
            Func<BufferView, byte[]> readBufferView)
        {
            int componentCount = ComponentCount(accessor.Type);
            if (accessor.Count > int.MaxValue)
                throw Invalid("Accessor is too large to decode.");
            var result = new float[(int)accessor.Count][];
            for (int recordIndex = 0; recordIndex < result.Length; recordIndex++)
                result[recordIndex] = new float[componentCount];

            int componentSize = ComponentSize(accessor.ComponentType);
            if (accessor.BufferView != null)
            {
                var view = accessor.BufferView.Value;
                var data = readBufferView(view) ?? Array.Empty<byte>();
                int packedStride = componentSize * componentCount;
                int stride = view.ByteStride > 0
                    ? SupportedByteOffset(view.ByteStride)
                    : packedStride;
                if (stride < packedStride) throw Invalid("Accessor byteStride is smaller than one element.");
                for (int recordIndex = 0; recordIndex < result.Length; recordIndex++)
                {
                    int recordOffset = SupportedByteOffset(
                        (long)accessor.ByteOffset + (long)recordIndex * stride);
                    for (int component = 0; component < componentCount; component++)
                        result[recordIndex][component] = ReadComponent(
                            data,
                            SupportedByteOffset(recordOffset + (long)component * componentSize),
                            accessor.ComponentType,
                            accessor.Normalized);
                }
            }

            ApplySparse(accessor, result, componentCount, componentSize, readBufferView);
            return new DecodedAccessor
            {
                Values = result,
                Encoding = Encoding(accessor.ComponentType, accessor.Normalized),
            };
        }

        private static void ApplySparse(
            Accessor accessor,
            float[][] result,
            int componentCount,
            int componentSize,
            Func<BufferView, byte[]> readBufferView)
        {
            var sparse = accessor.Sparse;
            if (sparse == null) return;
            if (sparse.Count < 1 || sparse.Count > result.Length || sparse.Indices?.BufferView == null ||
                sparse.Values?.BufferView == null)
                throw Invalid("Sparse accessor metadata is incomplete or out of range.");

            var indexData = readBufferView(sparse.Indices.BufferView.Value) ?? Array.Empty<byte>();
            var valueData = readBufferView(sparse.Values.BufferView.Value) ?? Array.Empty<byte>();
            int indexSize = ComponentSize(sparse.Indices.ComponentType);
            int previousIndex = -1;
            for (int sparseIndex = 0; sparseIndex < sparse.Count; sparseIndex++)
            {
                int indexOffset = SupportedByteOffset(
                    (long)sparse.Indices.ByteOffset + (long)sparseIndex * indexSize);
                int destination = ReadSparseIndex(indexData, indexOffset, sparse.Indices.ComponentType);
                if (destination <= previousIndex || destination < 0 || destination >= result.Length)
                    throw Invalid("Sparse accessor indices must be strictly increasing and in range.");
                previousIndex = destination;

                int valueOffset = SupportedByteOffset(
                    (long)sparse.Values.ByteOffset + (long)sparseIndex * componentCount * componentSize);
                for (int component = 0; component < componentCount; component++)
                    result[destination][component] = ReadComponent(
                        valueData,
                        SupportedByteOffset(valueOffset + (long)component * componentSize),
                        accessor.ComponentType,
                        accessor.Normalized);
            }
        }

        private static float ReadComponent(
            byte[] data,
            int offset,
            GLTFComponentType componentType,
            bool normalized)
        {
            int size = ComponentSize(componentType);
            EnsureRange(data, offset, size);
            switch (componentType)
            {
                case GLTFComponentType.Byte:
                {
                    sbyte value = unchecked((sbyte)data[offset]);
                    return normalized ? Math.Max(value / 127f, -1f) : value;
                }
                case GLTFComponentType.UnsignedByte:
                    return normalized ? data[offset] / 255f : data[offset];
                case GLTFComponentType.Short:
                {
                    short value = unchecked((short)ReadUInt16(data, offset));
                    return normalized ? Math.Max(value / 32767f, -1f) : value;
                }
                case GLTFComponentType.UnsignedShort:
                {
                    ushort value = ReadUInt16(data, offset);
                    return normalized ? value / 65535f : value;
                }
                case GLTFComponentType.UnsignedInt:
                    return ReadUInt32(data, offset);
                case GLTFComponentType.Float:
                    return math.asfloat(ReadUInt32(data, offset));
                default:
                    throw Invalid($"Unsupported accessor component type {componentType}.");
            }
        }

        private static int ReadSparseIndex(byte[] data, int offset, GLTFComponentType componentType)
        {
            switch (componentType)
            {
                case GLTFComponentType.UnsignedByte:
                    EnsureRange(data, offset, 1);
                    return data[offset];
                case GLTFComponentType.UnsignedShort:
                    EnsureRange(data, offset, 2);
                    return ReadUInt16(data, offset);
                case GLTFComponentType.UnsignedInt:
                {
                    EnsureRange(data, offset, 4);
                    uint value = ReadUInt32(data, offset);
                    if (value > int.MaxValue) throw Invalid("Sparse accessor index is too large.");
                    return (int)value;
                }
                default:
                    throw Invalid("Sparse accessor indices must use an unsigned integer component type.");
            }
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | data[offset + 1] << 8);
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] |
                          data[offset + 1] << 8 |
                          data[offset + 2] << 16 |
                          data[offset + 3] << 24);
        }

        private static void EnsureRange(byte[] data, int offset, int size)
        {
            if (data == null || offset < 0 || size < 0 || offset > data.Length - size)
                throw Invalid("Accessor buffer data is truncated.");
        }

        private static int SupportedByteOffset(long offset)
        {
            if (offset < 0L || offset > int.MaxValue)
                throw Invalid("Accessor byte layout exceeds the supported range.");
            return (int)offset;
        }

        private static int ComponentCount(GLTFAccessorAttributeType type)
        {
            switch (type)
            {
                case GLTFAccessorAttributeType.SCALAR: return 1;
                case GLTFAccessorAttributeType.VEC2: return 2;
                case GLTFAccessorAttributeType.VEC3: return 3;
                case GLTFAccessorAttributeType.VEC4: return 4;
                default: throw Invalid($"Unsupported expression accessor type {type}.");
            }
        }

        private static int ComponentSize(GLTFComponentType type)
        {
            switch (type)
            {
                case GLTFComponentType.Byte:
                case GLTFComponentType.UnsignedByte:
                    return 1;
                case GLTFComponentType.Short:
                case GLTFComponentType.UnsignedShort:
                    return 2;
                case GLTFComponentType.UnsignedInt:
                case GLTFComponentType.Float:
                    return 4;
                default:
                    throw Invalid($"Unsupported accessor component type {type}.");
            }
        }

        private static ExpressionAccessorComponentEncoding Encoding(
            GLTFComponentType type,
            bool normalized)
        {
            if (!normalized)
                return type == GLTFComponentType.Float
                    ? ExpressionAccessorComponentEncoding.Float
                    : ExpressionAccessorComponentEncoding.NonNormalizedInteger;
            switch (type)
            {
                case GLTFComponentType.Byte: return ExpressionAccessorComponentEncoding.NormalizedByte;
                case GLTFComponentType.UnsignedByte: return ExpressionAccessorComponentEncoding.NormalizedUnsignedByte;
                case GLTFComponentType.Short: return ExpressionAccessorComponentEncoding.NormalizedShort;
                case GLTFComponentType.UnsignedShort: return ExpressionAccessorComponentEncoding.NormalizedUnsignedShort;
                default: throw Invalid($"Component type {type} cannot use normalized expression output.");
            }
        }

        private static float[] FlattenScalar(DecodedAccessor decoded, string description)
        {
            if (decoded?.Values == null) throw Invalid($"{description} could not be decoded.");
            var result = new float[decoded.Values.Length];
            for (int index = 0; index < result.Length; index++)
            {
                if (decoded.Values[index] == null || decoded.Values[index].Length != 1)
                    throw Invalid($"{description} is not scalar.");
                result[index] = decoded.Values[index][0];
            }
            return result;
        }

        private static void EnsureSameInput(
            ExpressionResponseSampler sampler,
            float[] inputTimes,
            ExpressionResponseInterpolation interpolation,
            int samplerIndex)
        {
            if (sampler.Interpolation != interpolation || sampler.InputTimes.Length != inputTimes.Length)
                throw Invalid($"Shared sampler {samplerIndex} decoded inconsistently.");
            for (int index = 0; index < inputTimes.Length; index++)
                if (sampler.InputTimes[index] != inputTimes[index])
                    throw Invalid($"Shared sampler {samplerIndex} decoded inconsistently.");
        }

        private static void EnsureSameOutput(float[][] existing, float[][] decoded, int samplerIndex)
        {
            if (existing.Length != decoded.Length)
                throw Invalid($"Shared sampler {samplerIndex} has incompatible target dimensions.");
            for (int record = 0; record < existing.Length; record++)
            {
                if (existing[record].Length != decoded[record].Length)
                    throw Invalid($"Shared sampler {samplerIndex} has incompatible target dimensions.");
                for (int component = 0; component < existing[record].Length; component++)
                    if (existing[record][component] != decoded[record][component])
                        throw Invalid($"Shared sampler {samplerIndex} decoded inconsistently.");
            }
        }

        private static Accessor GetAccessor(GLTFRoot root, AccessorId id, string description)
        {
            if (id == null || root.Accessors == null || id.Id < 0 || id.Id >= root.Accessors.Count)
                throw Invalid($"The {description} accessor reference is invalid.");
            return root.Accessors[id.Id] ?? throw Invalid($"The {description} accessor is null.");
        }

        private static ExpressionResponseInterpolation ConvertInterpolation(
            InterpolationType interpolation,
            int samplerIndex)
        {
            switch (interpolation)
            {
                case InterpolationType.STEP: return ExpressionResponseInterpolation.Step;
                case InterpolationType.LINEAR: return ExpressionResponseInterpolation.Linear;
                case InterpolationType.CUBICSPLINE: return ExpressionResponseInterpolation.CubicSpline;
                default: throw Invalid($"Sampler {samplerIndex} uses unsupported interpolation {interpolation}.");
            }
        }

        private static ExpressionResponseEvaluationException Invalid(string message)
        {
            return new ExpressionResponseEvaluationException(message);
        }
    }
}
