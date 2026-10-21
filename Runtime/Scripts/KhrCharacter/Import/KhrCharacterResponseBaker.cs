using System;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityGLTF.Extensions;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>Settings for <see cref="KhrCharacterResponseBaker"/>.</summary>
    internal sealed class ExpressionResponseBakeOptions
    {
        /// <summary>The recognized entry extensions; null uses <see cref="ExpressionDerivedExtensionRegistry.Default"/>.</summary>
        public ExpressionDerivedExtensionRegistry Registry;

        /// <summary>Decides which material-extension textures resolve; null uses a default remapper.</summary>
        public MaterialPropertiesRemapper MaterialRemapper;

        /// <summary>True when the importer decodes <c>EXT_meshopt_compression</c> bufferViews.</summary>
        public bool MeshoptDecoderActive;
    }

    /// <summary>
    /// Decodes KHR_character_expression animations into passive, absolute wire-space response data and validates
    /// them. It never resolves or writes a Unity scene target. Each channel records which derived expression extension
    /// lists it; only channels listed by a supported extension produce records, while every channel still counts
    /// toward the duration and is checked against its authored initial value when it resolves. Invalid data throws
    /// <see cref="ExpressionResponseEvaluationException"/>; data that cannot be checked marks validation incomplete.
    /// </summary>
    internal static class KhrCharacterResponseBaker
    {
        private const string MeshoptExtensionName = "EXT_meshopt_compression";

        private static readonly string[] CoreTextureInfoPaths =
        {
            "pbrMetallicRoughness/baseColorTexture",
            "pbrMetallicRoughness/metallicRoughnessTexture",
            "normalTexture",
            "occlusionTexture",
            "emissiveTexture",
        };

        private sealed class ResolvedTarget
        {
            public ExpressionResponseTarget Target;
            public GLTFAccessorAttributeType OutputType;
            public double[] AuthoredInitial;
            public double[] ArrayAuthoredInitial;
            public bool ScalarPackedArray;
            public bool AllowsNormalizedIntegerOutput;
            public CoreNodeTargetSource Source;
            public ExpressionChannelClassification? Classification;
            public bool Resolved;
            public string UnresolvedReason;
        }

        private enum CoreNodeTargetSource
        {
            AnimationChannel,
            AnimationPointer,
        }

        private sealed class EntryListing
        {
            public string[] ListedBy;
            public readonly List<ExpressionDerivedExtension> Carried = new List<ExpressionDerivedExtension>();
        }

        private sealed class PossibleRecord
        {
            public int EntryIndex;
            public int ChannelIndex;
            public ExpressionResponseTarget Target;
        }

        private sealed class BakeContext
        {
            public GLTFRoot Root;
            public Func<BufferView, byte[]> ReadBufferView;
            public ExpressionDerivedExtensionRegistry Registry;
            public MaterialPropertiesRemapper Remapper;
            public bool MeshoptDecoderActive;
            public readonly List<string> IncompleteReasons = new List<string>();
        }

        public static ExpressionResponseSetEntry[] Bake(
            GLTFRoot root,
            GLTFSceneImporter importer,
            KHR_character_expression extension,
            ExpressionResponseBakeOptions options,
            out ExpressionValidationStatus validation)
        {
            if (importer == null) throw new ArgumentNullException(nameof(importer));
            return Bake(root, ReadBufferView, extension, options, out validation);

            byte[] ReadBufferView(BufferView bufferView)
            {
                var native = importer.GetBufferViewData(bufferView);
                return native.IsCreated ? native.ToArray() : Array.Empty<byte>();
            }
        }

        internal static ExpressionResponseSetEntry[] Bake(
            GLTFRoot root,
            Func<BufferView, byte[]> readBufferView,
            KHR_character_expression extension,
            ExpressionResponseBakeOptions options = null)
        {
            return Bake(root, readBufferView, extension, options, out _);
        }

        internal static ExpressionResponseSetEntry[] Bake(
            GLTFRoot root,
            Func<BufferView, byte[]> readBufferView,
            KHR_character_expression extension,
            ExpressionResponseBakeOptions options,
            out ExpressionValidationStatus validation)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (readBufferView == null) throw new ArgumentNullException(nameof(readBufferView));
            validation = ExpressionValidationStatus.Complete;
            if (extension?.Expressions == null || extension.Expressions.Count == 0)
                return Array.Empty<ExpressionResponseSetEntry>();

            var context = new BakeContext
            {
                Root = root,
                ReadBufferView = readBufferView,
                Registry = options?.Registry ?? ExpressionDerivedExtensionRegistry.Default,
                Remapper = options?.MaterialRemapper ?? new DefaultMaterialPropertiesRemapper(),
                MeshoptDecoderActive = options?.MeshoptDecoderActive ?? false,
            };
            var entries = new ExpressionResponseSetEntry[extension.Expressions.Count];
            var listings = new EntryListing[entries.Length];
            var possibleRecords = new List<PossibleRecord>();
            bool conflictValidationIncomplete = false;
            for (int expressionIndex = 0; expressionIndex < entries.Length; expressionIndex++)
            {
                var item = extension.Expressions[expressionIndex];
                if (item == null || string.IsNullOrEmpty(item.Name))
                    throw Invalid($"Expression entry {expressionIndex} has no nonempty name.");
                if (root.Animations == null || item.Animation < 0 || item.Animation >= root.Animations.Count)
                    throw Invalid(
                        $"Expression entry {expressionIndex} references invalid animation {item.Animation}.");

                var gltfAnimation = root.Animations[item.Animation];
                int channelCount = gltfAnimation?.Channels?.Count ?? 0;
                var listing = DeriveListing(context, item, expressionIndex, channelCount);
                listings[expressionIndex] = listing;
                foreach (var carried in listing.Carried)
                {
                    if (carried.IsSupported || !carried.ListsChannels) continue;
                    conflictValidationIncomplete = true;
                    context.IncompleteReasons.Add(
                        $"Expression entry {expressionIndex} carries '{carried.Name}', which this implementation " +
                        "does not support; its channels produce no records and conflict validation is incomplete.");
                }

                var animation = BakeAnimation(context, gltfAnimation, expressionIndex, listing);
                entries[expressionIndex] = new ExpressionResponseSetEntry(
                    item.Name,
                    item.Animation,
                    animation,
                    item.RawExtensions?.ToString(Formatting.None),
                    item.Extras?.ToString(Formatting.None));

                for (int channelIndex = 0; channelIndex < animation.Channels.Length; channelIndex++)
                {
                    var channel = animation.Channels[channelIndex];
                    if (channel.ListedBy == null) continue;
                    if (channel.Target.Kind == ExpressionPropertyKind.Other)
                    {
                        conflictValidationIncomplete = true;
                        continue;
                    }
                    possibleRecords.Add(new PossibleRecord
                    {
                        EntryIndex = expressionIndex,
                        ChannelIndex = channelIndex,
                        Target = channel.Target,
                    });
                }
            }

            if (conflictValidationIncomplete)
                context.IncompleteReasons.Add(
                    "Conflict validation across expression entries is incomplete: a listed channel could not be " +
                    "resolved or an entry carries an unsupported derived expression extension.");
            validation = new ExpressionValidationStatus(context.IncompleteReasons);
            return entries;
        }

        private static EntryListing DeriveListing(
            BakeContext context,
            KHR_character_expression.ExpressionItem item,
            int expressionIndex,
            int channelCount)
        {
            var listing = new EntryListing { ListedBy = new string[channelCount] };
            AddTypedListing(context, listing, item, expressionIndex, KhrCharacterExtensionNames.ExpressionJoint,
                item.Joint?.Channels, item.Joint != null);
            AddTypedListing(context, listing, item, expressionIndex, KhrCharacterExtensionNames.ExpressionMorphtarget,
                item.Morphtarget?.Channels, item.Morphtarget != null);
            AddTypedListing(context, listing, item, expressionIndex,
                KhrCharacterExtensionNames.ExpressionTextureTransform, item.TextureTransform?.Channels,
                item.TextureTransform != null);
            if (item.Suppression != null &&
                context.Registry.TryGet(KhrCharacterExtensionNames.ExpressionSuppression, out var suppression))
                listing.Carried.Add(suppression);

            if (item.RawExtensions == null) return listing;
            foreach (var property in item.RawExtensions.Properties())
            {
                string name = property.Name;
                if (name == KhrCharacterExtensionNames.ExpressionJoint
                    || name == KhrCharacterExtensionNames.ExpressionMorphtarget
                    || name == KhrCharacterExtensionNames.ExpressionTextureTransform
                    || name == KhrCharacterExtensionNames.ExpressionSuppression)
                {
                    if (!(property.Value is JObject))
                        throw Invalid($"Expression entry {expressionIndex} extension '{name}' must be an object.");
                    continue;
                }
                if (!context.Registry.TryGet(name, out var registered))
                {
                    context.IncompleteReasons.Add(
                        $"Expression entry {expressionIndex} carries the unrecognized extension '{name}'; listing " +
                        "and conflict validation for it are incomplete.");
                    continue;
                }
                listing.Carried.Add(registered);
                if (!registered.ListsChannels) continue;
                if (!(property.Value is JObject vendorObject))
                    throw Invalid($"Expression entry {expressionIndex} extension '{name}' must be an object.");
                AddListing(listing, expressionIndex, name, ReadChannels(vendorObject, expressionIndex, name));
            }
            return listing;
        }

        private static void AddTypedListing(
            BakeContext context,
            EntryListing listing,
            KHR_character_expression.ExpressionItem item,
            int expressionIndex,
            string name,
            int[] channels,
            bool present)
        {
            if (!present)
            {
                var raw = item.RawExtensions?[name];
                if (raw == null) return;
                if (!(raw is JObject rawObject))
                    throw Invalid($"Expression entry {expressionIndex} extension '{name}' must be an object.");
                channels = ReadChannels(rawObject, expressionIndex, name);
            }
            if (context.Registry.TryGet(name, out var extension)) listing.Carried.Add(extension);
            AddListing(listing, expressionIndex, name, channels);
        }

        private static int[] ReadChannels(JObject extension, int expressionIndex, string name)
        {
            if (!(extension["channels"] is JArray array)) return null;
            var channels = new int[array.Count];
            for (int index = 0; index < channels.Length; index++)
            {
                if (array[index].Type != JTokenType.Integer)
                    throw Invalid($"Expression entry {expressionIndex} extension '{name}' lists a non-integer channel.");
                long value = array[index].Value<long>();
                channels[index] = value < int.MinValue || value > int.MaxValue ? -1 : (int)value;
            }
            return channels;
        }

        private static void AddListing(EntryListing listing, int expressionIndex, string name, int[] channels)
        {
            if (channels == null || channels.Length == 0)
                throw Invalid($"Expression entry {expressionIndex} extension '{name}' must list at least one channel.");
            var seen = new HashSet<int>();
            foreach (int channelIndex in channels)
            {
                if (channelIndex < 0 || channelIndex >= listing.ListedBy.Length)
                    throw Invalid(
                        $"Expression entry {expressionIndex} extension '{name}' lists channel {channelIndex}, which " +
                        "is not a channel of the entry's animation.");
                if (!seen.Add(channelIndex))
                    throw Invalid(
                        $"Expression entry {expressionIndex} extension '{name}' lists channel {channelIndex} more " +
                        "than once.");
                if (listing.ListedBy[channelIndex] != null)
                    throw Invalid(
                        $"Channel {channelIndex} of expression entry {expressionIndex} is listed by both " +
                        $"'{listing.ListedBy[channelIndex]}' and '{name}'.");
                listing.ListedBy[channelIndex] = name;
            }
        }

        private static ExpressionResponseAnimation BakeAnimation(
            BakeContext context,
            GLTFAnimation animation,
            int expressionIndex,
            EntryListing listing)
        {
            var root = context.Root;
            if (animation?.Channels == null || animation.Channels.Count == 0)
                throw Invalid("An expression animation must contain at least one channel.");
            if (animation.Samplers == null)
                throw Invalid("An expression animation has no sampler array.");

            string unevaluableReason = FindUndecodableInput(context, animation, expressionIndex);
            if (unevaluableReason != null) context.IncompleteReasons.Add(unevaluableReason);

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
                float[] inputTimes = null;
                if (unevaluableReason == null)
                {
                    var decodedInput = GltfAccessorDecoder.DecodeAccessor(inputAccessor, context.ReadBufferView);
                    inputTimes = FlattenScalar(decodedInput, $"sampler {samplerIndex} input");
                    ValidateDecodedInputMetadata(inputAccessor, inputTimes, samplerIndex);
                }

                if (samplers[samplerIndex] == null)
                {
                    samplers[samplerIndex] = new ExpressionResponseSampler
                    {
                        InputTimes = inputTimes,
                        Interpolation = interpolation,
                    };
                }
                else if (inputTimes != null)
                {
                    EnsureSameInput(samplers[samplerIndex], inputTimes, interpolation, samplerIndex);
                }

                var resolved = ResolveTarget(context, channel, channelIndex);
                string listedBy = listing.ListedBy[channelIndex];
                if (listedBy != null)
                    EnsureClassification(context, listedBy, resolved, expressionIndex, channelIndex);
                if (!resolved.Resolved)
                    context.IncompleteReasons.Add(
                        $"Channel {channelIndex} of expression entry {expressionIndex}: {resolved.UnresolvedReason}");

                var outputAccessor = GetAccessor(root, gltfSampler.Output, $"sampler {samplerIndex} output");
                bool decoded = false;
                if (resolved.Resolved)
                    ValidateOutputAccessor(
                        outputAccessor,
                        resolved,
                        (long)inputAccessor.Count,
                        interpolation,
                        channelIndex);
                if (resolved.Resolved && unevaluableReason == null)
                {
                    if (!CanDecode(context, outputAccessor, out string undecodable))
                    {
                        context.IncompleteReasons.Add(
                            $"Channel {channelIndex} of expression entry {expressionIndex} has output data " +
                            $"that cannot be decoded ({undecodable}); it produces no records and its initial value " +
                            "is not checked.");
                    }
                    else
                    {
                        DecodeOutput(
                            context,
                            outputAccessor,
                            resolved,
                            samplers[samplerIndex],
                            inputTimes,
                            interpolation,
                            samplerIndex,
                            channelIndex);
                        decoded = true;
                    }
                }

                var target = resolved.Resolved
                    ? new ExpressionResponseTarget(
                        resolved.Target.Identity,
                        resolved.Target.ComponentCount,
                        resolved.Target.ValueKind,
                        decoded,
                        resolved.Target.Kind,
                        resolved.AuthoredInitial,
                        resolved.Target.ArrayLength,
                        resolved.ArrayAuthoredInitial)
                    : resolved.Target;
                bool supportedListing = listedBy != null &&
                    context.Registry.TryGet(listedBy, out var listingExtension) && listingExtension.IsSupported;
                channels[channelIndex] = new ExpressionResponseChannel
                {
                    SamplerIndex = samplerIndex,
                    Target = target,
                    ListedBy = listedBy,
                    EmitsRecords = supportedListing && target.IsSupported,
                };
            }

            var result = new ExpressionResponseAnimation
            {
                Samplers = samplers,
                Channels = channels,
                UnevaluableReason = unevaluableReason,
            };
            ExpressionResponseEvaluator.Evaluate(result, 0f);
            return result;
        }

        private static void DecodeOutput(
            BakeContext context,
            Accessor outputAccessor,
            ResolvedTarget resolved,
            ExpressionResponseSampler sampler,
            float[] inputTimes,
            ExpressionResponseInterpolation interpolation,
            int samplerIndex,
            int channelIndex)
        {
            var decodedOutput = GltfAccessorDecoder.DecodeAccessor(outputAccessor, context.ReadBufferView);
            var logicalValues = resolved.ScalarPackedArray
                ? ExpressionResponseSampler.FromDecodedScalarStream(
                    inputTimes,
                    FlattenScalar(decodedOutput, $"channel {channelIndex} output"),
                    resolved.Target.ComponentCount,
                    interpolation).OutputValues
                : decodedOutput.Values;

            if (sampler.OutputValues == null)
            {
                sampler.OutputValues = logicalValues;
                sampler.OutputEncoding = decodedOutput.Encoding;
            }
            else
            {
                EnsureSameOutput(sampler.OutputValues, logicalValues, samplerIndex);
                if (sampler.OutputEncoding != decodedOutput.Encoding)
                    throw Invalid($"Shared sampler {samplerIndex} decoded with inconsistent output encodings.");
            }

            // b and the channel's time-zero sample n are independent; differing n is valid.
        }

        private static void EnsureClassification(
            BakeContext context,
            string listedBy,
            ResolvedTarget resolved,
            int expressionIndex,
            int channelIndex)
        {
            if (!context.Registry.TryGet(listedBy, out var extension)) return;
            var required = extension.Classification;
            if (required == ExpressionChannelClassification.Any || resolved.Classification == required) return;
            throw Invalid(
                $"Channel {channelIndex} of expression entry {expressionIndex} is listed by '{listedBy}' but does " +
                $"not target {Describe(required)}.");
        }

        private static string Describe(ExpressionChannelClassification classification)
        {
            switch (classification)
            {
                case ExpressionChannelClassification.NodeTrs: return "node translation, rotation or scale";
                case ExpressionChannelClassification.MorphWeights: return "node morph weights";
                case ExpressionChannelClassification.TextureTransform:
                    return "offset, scale or rotation of an authored KHR_texture_transform";
                default: return "a supported property";
            }
        }

        private static string FindUndecodableInput(BakeContext context, GLTFAnimation animation, int expressionIndex)
        {
            var checkedSamplers = new HashSet<int>();
            foreach (var channel in animation.Channels)
            {
                int samplerIndex = channel?.Sampler?.Id ?? -1;
                if (samplerIndex < 0 || samplerIndex >= animation.Samplers.Count || !checkedSamplers.Add(samplerIndex))
                    continue;
                var inputId = animation.Samplers[samplerIndex]?.Input;
                if (inputId == null || context.Root.Accessors == null || inputId.Id < 0 ||
                    inputId.Id >= context.Root.Accessors.Count || context.Root.Accessors[inputId.Id] == null)
                    continue;
                if (!CanDecode(context, context.Root.Accessors[inputId.Id], out string reason))
                    return $"Expression entry {expressionIndex} cannot be evaluated: sampler {samplerIndex} input " +
                           $"data cannot be decoded ({reason}).";
            }
            return null;
        }

        // Compressed or filtered bufferViews are decoded only by a decoder the importer runs. Reading the fallback
        // data instead would certify values the asset does not contain.
        private static bool CanDecode(BakeContext context, Accessor accessor, out string reason)
        {
            reason = null;
            return BufferViewDecodable(accessor.BufferView?.Value, context.MeshoptDecoderActive, ref reason)
                   && BufferViewDecodable(accessor.Sparse?.Indices?.BufferView?.Value, context.MeshoptDecoderActive, ref reason)
                   && BufferViewDecodable(accessor.Sparse?.Values?.BufferView?.Value, context.MeshoptDecoderActive, ref reason);
        }

        private static bool BufferViewDecodable(BufferView view, bool meshoptDecoderActive, ref string reason)
        {
            if (view?.Extensions == null) return true;
            foreach (var name in view.Extensions.Keys)
            {
                if (name == MeshoptExtensionName && meshoptDecoderActive) continue;
                reason = $"its bufferView uses {name}, which this importer does not decode";
                return false;
            }
            return true;
        }

        private static ResolvedTarget ResolveTarget(BakeContext context, AnimationChannel channel, int channelIndex)
        {
            var root = context.Root;
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
                if (TryResolveCoreNodePointer(root, pointer, segments, channelIndex, out var resolved))
                    return resolved;
                if (TryResolveTextureTransformPointer(context, pointer, segments, channelIndex, out resolved))
                    return resolved;
                return Unresolved(
                    new ExpressionResponseTarget(
                        ExpressionPropertyIdentity.ForProperty(pointer),
                        0,
                        ExpressionResponseValueKind.Components,
                        false),
                    $"its pointer '{pointer}' does not resolve to a property this implementation supports.");
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
                return Unresolved(
                    ExpressionResponseTarget.UnsupportedUnresolved($"channel/{channelIndex}/extension-target"),
                    "its extension-defined target is not supported by this implementation.");
            throw Invalid($"Channel {channelIndex} does not resolve to a concrete core or extension property.");
        }

        private static ResolvedTarget Unresolved(ExpressionResponseTarget target, string reason)
        {
            return new ResolvedTarget
            {
                Target = target,
                Source = CoreNodeTargetSource.AnimationPointer,
                Resolved = false,
                UnresolvedReason = reason,
            };
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
            // Core glTF forbids matrix on any node a core channel targets. Through a pointer, rotation and scale of a
            // matrix node do not resolve, and KHR_character_expression forbids its translation.
            bool isMatrixBacked =
                node.HasMatrix || (node.Matrix != null && !node.Matrix.Equals(GLTF.Math.Matrix4x4.Identity));
            if (isMatrixBacked && (source == CoreNodeTargetSource.AnimationChannel || path != "weights"))
                throw Invalid($"Channel {channelIndex} targets {path} on a matrix-backed node.");

            string canonicalProperty = $"/nodes/{nodeIndex}/{path}";
            int componentCount;
            GLTFAccessorAttributeType outputType;
            ExpressionResponseValueKind valueKind = ExpressionResponseValueKind.Components;
            ExpressionPropertyKind kind;
            double[] authored;
            int arrayLength = 0;
            bool scalarPacked = false;
            bool allowsNormalizedIntegerOutput = false;

            switch (path)
            {
                case "translation":
                    componentCount = 3;
                    outputType = GLTFAccessorAttributeType.VEC3;
                    kind = ExpressionPropertyKind.NodeTranslation;
                    authored = new double[] { node.Translation.X, node.Translation.Y, node.Translation.Z };
                    break;
                case "rotation":
                    componentCount = 4;
                    outputType = GLTFAccessorAttributeType.VEC4;
                    valueKind = ExpressionResponseValueKind.Quaternion;
                    kind = ExpressionPropertyKind.NodeRotation;
                    authored = new double[] { node.Rotation.X, node.Rotation.Y, node.Rotation.Z, node.Rotation.W };
                    if (!IsUnitQuaternion(authored))
                        throw Invalid($"Channel {channelIndex} targets a node with an invalid authored rotation.");
                    allowsNormalizedIntegerOutput = true;
                    break;
                case "scale":
                    componentCount = 3;
                    outputType = GLTFAccessorAttributeType.VEC3;
                    kind = ExpressionPropertyKind.NodeScale;
                    authored = new double[] { node.Scale.X, node.Scale.Y, node.Scale.Z };
                    break;
                case "weights":
                    componentCount = GetMorphTargetCount(node);
                    if (componentCount < 1)
                        throw Invalid($"Channel {channelIndex} targets weights on a node with no morph targets.");
                    outputType = GLTFAccessorAttributeType.SCALAR;
                    kind = ExpressionPropertyKind.NodeWeights;
                    authored = ExpressionInitialValueValidation.ResolveAuthoredMorphWeights(
                        node.Weights,
                        node.Mesh?.Value?.Weights,
                        componentCount);
                    arrayLength = componentCount;
                    scalarPacked = true;
                    allowsNormalizedIntegerOutput = true;
                    break;
                default:
                    throw Invalid($"Channel {channelIndex} has unsupported core target path '{path}'.");
            }

            double[] arrayAuthored = arrayLength > 0 ? authored : null;
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
                Target = new ExpressionResponseTarget(
                    identity,
                    componentCount,
                    valueKind,
                    true,
                    kind,
                    authored,
                    arrayLength,
                    arrayAuthored),
                OutputType = outputType,
                AuthoredInitial = authored,
                ArrayAuthoredInitial = arrayAuthored,
                ScalarPackedArray = scalarPacked,
                AllowsNormalizedIntegerOutput = allowsNormalizedIntegerOutput,
                Source = source,
                Classification = path == "weights"
                    ? ExpressionChannelClassification.MorphWeights
                    : ExpressionChannelClassification.NodeTrs,
                Resolved = true,
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

        // Resolves /materials/{m}/<textureInfo>/extensions/KHR_texture_transform/{offset|scale|rotation} on the core
        // texture slots and on material-extension textures the material remapper handles. Any other textureInfo
        // location needs an extension this implementation does not support, so it stays unresolved.
        private static bool TryResolveTextureTransformPointer(
            BakeContext context,
            string pointer,
            string[] segments,
            int channelIndex,
            out ResolvedTarget target)
        {
            target = null;
            int count = segments.Length;
            if (count < 6 || segments[0] != "materials" || segments[count - 3] != "extensions" ||
                segments[count - 2] != ExtTextureTransformExtensionFactory.EXTENSION_NAME)
                return false;
            if (!TryParseArrayIndex(segments[1], out int materialIndex))
                throw Invalid($"Channel {channelIndex} has a noncanonical material array index in '{pointer}'.");

            string property = segments[count - 1];
            GLTFAccessorAttributeType outputType;
            ExpressionPropertyKind kind;
            int componentCount;
            switch (property)
            {
                case ExtTextureTransformExtensionFactory.OFFSET:
                    outputType = GLTFAccessorAttributeType.VEC2;
                    kind = ExpressionPropertyKind.TextureOffset;
                    componentCount = 2;
                    break;
                case ExtTextureTransformExtensionFactory.SCALE:
                    outputType = GLTFAccessorAttributeType.VEC2;
                    kind = ExpressionPropertyKind.TextureScale;
                    componentCount = 2;
                    break;
                case ExtTextureTransformExtensionFactory.ROTATION:
                    outputType = GLTFAccessorAttributeType.SCALAR;
                    kind = ExpressionPropertyKind.TextureRotation;
                    componentCount = 1;
                    break;
                default:
                    throw Invalid(
                        $"Channel {channelIndex} targets '{property}' of KHR_texture_transform, which is not an " +
                        "animatable offset, scale or rotation.");
            }

            var root = context.Root;
            if (root.Materials == null || materialIndex >= root.Materials.Count || root.Materials[materialIndex] == null)
                throw Invalid($"Channel {channelIndex} pointer '{pointer}' references a missing material.");
            var material = root.Materials[materialIndex];
            string textureInfoPath = string.Join("/", segments, 2, count - 5);
            string remapperProperty =
                $"{textureInfoPath}/extensions/{ExtTextureTransformExtensionFactory.EXTENSION_NAME}/{property}";

            double[] authored;
            if (Array.IndexOf(CoreTextureInfoPaths, textureInfoPath) >= 0)
            {
                var transform = GetCoreTextureTransform(root, material, textureInfoPath, pointer, channelIndex, out _);
                authored = AuthoredTextureTransformValue(transform, property);
            }
            else if (segments[2] == "extensions" && count >= 8 && context.Remapper.HasImportMap(remapperProperty))
            {
                var transform = GetExtensionTextureTransform(material, segments, pointer, channelIndex, out _);
                authored = AuthoredTextureTransformValue(transform, property);
            }
            else
            {
                target = Unresolved(
                    new ExpressionResponseTarget(
                        ExpressionPropertyIdentity.ForProperty(pointer),
                        0,
                        ExpressionResponseValueKind.Components,
                        false),
                    $"its pointer '{pointer}' addresses a textureInfo that needs an extension this " +
                    "implementation does not support.");
                target.Classification = ExpressionChannelClassification.TextureTransform;
                return true;
            }

            target = new ResolvedTarget
            {
                Target = new ExpressionResponseTarget(
                    ExpressionPropertyIdentity.ForProperty(pointer),
                    componentCount,
                    ExpressionResponseValueKind.Components,
                    true,
                    kind,
                    authored,
                    0),
                OutputType = outputType,
                AuthoredInitial = authored,
                Source = CoreNodeTargetSource.AnimationPointer,
                Classification = ExpressionChannelClassification.TextureTransform,
                Resolved = true,
            };
            return true;
        }

        /// <summary>
        /// Resolves the authored <c>KHR_texture_transform</c> object at <paramref name="transformPointer"/>
        /// (<c>/materials/{m}/&lt;textureInfo&gt;/extensions/KHR_texture_transform</c>) on a core texture slot or a
        /// material-extension texture: its offset, scale and rotation, each defaulted when omitted, and the textureInfo's
        /// texture index, or -1 when it has none.
        /// </summary>
        internal static bool TryGetAuthoredTextureTransform(
            GLTFRoot root,
            string transformPointer,
            out int materialIndex,
            out string textureInfoPath,
            out double[] offset,
            out double[] scale,
            out double rotation,
            out int textureIndex)
        {
            materialIndex = -1;
            textureInfoPath = null;
            offset = null;
            scale = null;
            rotation = 0d;
            textureIndex = -1;
            string pointer = $"{transformPointer}/{ExtTextureTransformExtensionFactory.OFFSET}";
            if (root?.Materials == null || !TryParsePointer(pointer, out string[] segments)) return false;
            int count = segments.Length;
            if (count < 6 || segments[0] != "materials" || segments[count - 3] != "extensions" ||
                segments[count - 2] != ExtTextureTransformExtensionFactory.EXTENSION_NAME ||
                !TryParseArrayIndex(segments[1], out materialIndex) || materialIndex >= root.Materials.Count ||
                root.Materials[materialIndex] == null)
                return false;
            var material = root.Materials[materialIndex];
            textureInfoPath = string.Join("/", segments, 2, count - 5);
            JObject transform;
            try
            {
                if (Array.IndexOf(CoreTextureInfoPaths, textureInfoPath) >= 0)
                    transform = GetCoreTextureTransform(root, material, textureInfoPath, pointer, -1, out textureIndex);
                else if (segments[2] == "extensions" && count >= 8)
                    transform = GetExtensionTextureTransform(material, segments, pointer, -1, out textureIndex);
                else
                    return false;
            }
            catch (ExpressionResponseEvaluationException)
            {
                return false;
            }
            offset = AuthoredTextureTransformValue(transform, ExtTextureTransformExtensionFactory.OFFSET);
            scale = AuthoredTextureTransformValue(transform, ExtTextureTransformExtensionFactory.SCALE);
            rotation = AuthoredTextureTransformValue(transform, ExtTextureTransformExtensionFactory.ROTATION)[0];
            return true;
        }

        private static JObject GetCoreTextureTransform(
            GLTFRoot root,
            GLTFMaterial material,
            string textureInfoPath,
            string pointer,
            int channelIndex,
            out int textureIndex)
        {
            TextureInfo textureInfo;
            switch (textureInfoPath)
            {
                case "pbrMetallicRoughness/baseColorTexture":
                    textureInfo = material.PbrMetallicRoughness?.BaseColorTexture;
                    break;
                case "pbrMetallicRoughness/metallicRoughnessTexture":
                    textureInfo = material.PbrMetallicRoughness?.MetallicRoughnessTexture;
                    break;
                case "normalTexture":
                    textureInfo = material.NormalTexture;
                    break;
                case "occlusionTexture":
                    textureInfo = material.OcclusionTexture;
                    break;
                default:
                    textureInfo = material.EmissiveTexture;
                    break;
            }
            if (textureInfo == null)
                throw Invalid($"Channel {channelIndex} pointer '{pointer}' references a textureInfo that is not authored.");
            textureIndex = textureInfo.Index?.Id ?? -1;
            if (textureInfo.Extensions == null ||
                !textureInfo.Extensions.TryGetValue(ExtTextureTransformExtensionFactory.EXTENSION_NAME, out var extension) ||
                extension == null)
                throw Invalid(
                    $"Channel {channelIndex} pointer '{pointer}' references a textureInfo without an authored " +
                    "KHR_texture_transform.");
            return extension.Serialize()?.Value as JObject ?? new JObject();
        }

        private static JObject GetExtensionTextureTransform(
            GLTFMaterial material,
            string[] segments,
            string pointer,
            int channelIndex,
            out int textureIndex)
        {
            string extensionName = segments[3];
            if (material.Extensions == null || !material.Extensions.TryGetValue(extensionName, out var extension) ||
                !(extension?.Serialize()?.Value is JObject token))
                throw Invalid(
                    $"Channel {channelIndex} pointer '{pointer}' references the material extension " +
                    $"'{extensionName}', which the material does not author.");
            for (int index = 4; index < segments.Length - 3; index++)
            {
                token = token[segments[index]] as JObject;
                if (token == null)
                    throw Invalid(
                        $"Channel {channelIndex} pointer '{pointer}' references a textureInfo that is not authored.");
            }
            if (!(token["extensions"]?[ExtTextureTransformExtensionFactory.EXTENSION_NAME] is JObject transform))
                throw Invalid(
                    $"Channel {channelIndex} pointer '{pointer}' references a textureInfo without an authored " +
                    "KHR_texture_transform.");
            textureIndex = token["index"]?.Type == JTokenType.Integer ? token.Value<int>("index") : -1;
            return transform;
        }

        private static double[] AuthoredTextureTransformValue(JObject transform, string property)
        {
            var token = transform[property];
            switch (property)
            {
                case ExtTextureTransformExtensionFactory.ROTATION:
                    return new[] { token != null ? token.Value<double>() : 0d };
                case ExtTextureTransformExtensionFactory.SCALE:
                    return token is JArray scale && scale.Count == 2
                        ? new[] { scale[0].Value<double>(), scale[1].Value<double>() }
                        : new[] { 1d, 1d };
                default:
                    return token is JArray offset && offset.Count == 2
                        ? new[] { offset[0].Value<double>(), offset[1].Value<double>() }
                        : new[] { 0d, 0d };
            }
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

        private static bool IsUnitQuaternion(double[] value)
        {
            if (value == null || value.Length != 4) return false;
            double lengthSquared = 0d;
            for (int index = 0; index < value.Length; index++)
            {
                if (double.IsNaN(value[index]) || double.IsInfinity(value[index])) return false;
                lengthSquared += value[index] * value[index];
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
            long keyCount,
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
            long expectedCount = keyCount * recordsPerKey;
            if (target.ScalarPackedArray) expectedCount *= target.Target.ComponentCount;
            if (accessor.Count != expectedCount)
                throw Invalid(
                    $"Channel {channelIndex} output accessor count is {accessor.Count}; expected {expectedCount}.");
        }

        private static float[] FlattenScalar(GltfAccessorDecoder.DecodedAccessor decoded, string description)
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
