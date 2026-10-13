using System;
using GLTF.Schema;
using Unity.Mathematics;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Decodes glTF accessors, including strided buffer views, normalized integers and sparse substitution, into
    /// float records. Malformed layouts throw <see cref="ExpressionResponseEvaluationException"/>.
    /// </summary>
    internal static class GltfAccessorDecoder
    {
        internal sealed class DecodedAccessor
        {
            public float[][] Values;
            public ExpressionAccessorComponentEncoding Encoding;
        }

        internal static DecodedAccessor DecodeAccessor(
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

        internal static int ComponentCount(GLTFAccessorAttributeType type)
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

        internal static int ComponentSize(GLTFComponentType type)
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

        private static ExpressionResponseEvaluationException Invalid(string message)
        {
            return new ExpressionResponseEvaluationException(message);
        }
    }
}
