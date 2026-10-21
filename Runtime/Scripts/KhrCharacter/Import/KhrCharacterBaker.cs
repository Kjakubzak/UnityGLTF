using System;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEngine;
using UnityGLTF.Extensions;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Builds the authoring model (<see cref="CharacterExpressionSet"/>) of an imported character from a parsed
    /// <c>KHR_character_expression</c>, for extraction and export only; <see cref="ExpressionController"/> evaluates the
    /// imported <see cref="ExpressionResponseSet"/> instead. Bakes morph-target (blendshape weight), joint (node TRS)
    /// and texture-transform expressions. Each curve is stored as deltas over the animation's frame-0 value. Morph
    /// weights are raw glTF [0..1]; joint values are converted to Unity space at bake.
    /// </summary>
    internal static class KhrCharacterBaker
    {
        public static CharacterExpressionSet Bake(
            GLTFRoot root,
            GLTFSceneImporter importer,
            KHR_character_expression expressionExt,
            IReadOnlyDictionary<int, GameObject> nodeIndexToGo,
            MaterialPropertiesRemapper remapper = null)
        {
            if (root == null || importer == null || expressionExt?.Expressions == null)
                return null;

            remapper = remapper ?? new DefaultMaterialPropertiesRemapper();
            var tracks = new List<ExpressionTrack>();
            var rawSuppressions = new List<KHR_character_expression_suppression>(); // aligned with tracks, for the post-pass
            var wireToTrackIndex = new Dictionary<int, int>();

            for (int expressionIndex = 0; expressionIndex < expressionExt.Expressions.Count; expressionIndex++)
            {
                var item = expressionExt.Expressions[expressionIndex];
                if (item == null || string.IsNullOrEmpty(item.Name)) continue;
                if (root.Animations == null || item.Animation < 0 || item.Animation >= root.Animations.Count)
                {
                    Debug.LogWarning($"[KHR_character] Expression '{item.Name}' references an invalid animation index ({item.Animation}); skipping.");
                    continue;
                }

                var animation = root.Animations[item.Animation];
                var track = new ExpressionTrack { Name = item.Name };

                var morphDrivers = new List<MorphDriver>();
                if (item.Morphtarget?.Channels != null)
                    BakeMorphChannels(root, importer, animation, item.Morphtarget.Channels, nodeIndexToGo, morphDrivers);
                if (morphDrivers.Count > 0)
                {
                    track.MorphDrivers = morphDrivers.ToArray();
                    track.Domains |= ExpressionDomain.Morph;
                }

                var jointDrivers = new List<JointDriver>();
                if (item.Joint?.Channels != null)
                    BakeJointChannels(root, importer, animation, item.Joint.Channels, nodeIndexToGo, jointDrivers);
                if (jointDrivers.Count > 0)
                {
                    track.JointDrivers = jointDrivers.ToArray();
                    track.Domains |= ExpressionDomain.Joint;
                }

                var textureDrivers = new List<TextureDriver>();
                if (item.TextureTransform?.Channels != null)
                    BakeTextureChannels(root, importer, remapper, animation, item.TextureTransform.Channels, nodeIndexToGo, textureDrivers);
                if (textureDrivers.Count > 0)
                {
                    track.TextureDrivers = textureDrivers.ToArray();
                    track.Domains |= ExpressionDomain.Texture;
                }

                rawSuppressions.Add(item.Suppression);
                wireToTrackIndex[expressionIndex] = tracks.Count;
                tracks.Add(track);
            }

            var set = new CharacterExpressionSet { Expressions = tracks.ToArray() };
            set.RebuildIndex();

            // Wire references target expression-array indices. Remap them because invalid expressions may have
            // been skipped while baking runtime tracks.
            for (int i = 0; i < tracks.Count; i++)
            {
                if (rawSuppressions[i] == null) continue;
                var suppressions = BuildSuppressionEntries(rawSuppressions[i], i, wireToTrackIndex);
                if (suppressions.Length > 0)
                {
                    tracks[i].Suppressions = suppressions;
                    tracks[i].SuppressionExtensionsJson = rawSuppressions[i].Extensions?.ToString(
                        Newtonsoft.Json.Formatting.None);
                    tracks[i].SuppressionExtrasJson = rawSuppressions[i].Extras?.ToString(
                        Newtonsoft.Json.Formatting.None);
                    tracks[i].SuppressionAdditionalPropertiesJson = rawSuppressions[i].AdditionalProperties?.ToString(
                        Newtonsoft.Json.Formatting.None);
                    tracks[i].SuppressionRequiredCompanionExtensions = GetRequiredSuppressionCompanionExtensions(
                        root, rawSuppressions[i], suppressions);
                }
            }

            var mappingExt = GetMappingExtension(root);
            if (mappingExt != null)
                TryApplyMappingSets(
                    root, mappingExt, wireToTrackIndex, expressionExt.Expressions, set);

            return set;
        }

        // ── Morph (blendshape weight) channels ───────────────────────────────

        private static void BakeMorphChannels(
            GLTFRoot root, GLTFSceneImporter importer, GLTFAnimation animation,
            int[] channelIndices, IReadOnlyDictionary<int, GameObject> nodeIndexToGo, List<MorphDriver> output)
        {
            foreach (var channelIndex in channelIndices)
            {
                try
                {
                    if (channelIndex < 0 || channelIndex >= animation.Channels.Count) continue;
                    var channel = animation.Channels[channelIndex];
                    if (channel?.Target == null) continue;

                    // Resolve the target renderer and, for the pointer form, the single blendshape it drives. Two
                    // authoring conventions reach the same SkinnedMeshRenderer:
                    //  • standard glTF: target.path == "weights" on a node (one channel drives ALL its blendshapes)
                    //  • KHR_animation_pointer: "/nodes/{i}/weights/{j}" (one channel drives ONE blendshape) — the
                    //    VRM convention. Requires the KHR_animation_pointer import plugin so the channel's
                    //    target extension deserializes to a typed pointer (see GetPointer).
                    SkinnedMeshRenderer smr = null;
                    int singleShapeIndex = -1;
                    int targetNodeIndex = -1;
                    if (channel.Target.Path == "weights" && channel.Target.Node != null)
                    {
                        targetNodeIndex = channel.Target.Node.Id;
                        if (!nodeIndexToGo.TryGetValue(targetNodeIndex, out var go) || go == null) continue;
                        smr = go.GetComponent<SkinnedMeshRenderer>();
                    }
                    else
                    {
                        var pointer = GetPointer(channel);
                        if (pointer == null || !TryParseNodeWeightsPointer(pointer, out targetNodeIndex, out singleShapeIndex)) continue;
                        if (!nodeIndexToGo.TryGetValue(targetNodeIndex, out var go) || go == null) continue;
                        smr = go.GetComponent<SkinnedMeshRenderer>();
                    }
                    if (smr == null || smr.sharedMesh == null) continue;

                    int samplerIndex = channel.Sampler?.Id ?? -1;
                    if (samplerIndex < 0 || samplerIndex >= animation.Samplers.Count) continue;
                    var sampler = animation.Samplers[samplerIndex];

                    var times = DecodeScalar(importer, GetAccessor(root, sampler.Input));
                    var values = DecodeScalar(importer, GetAccessor(root, sampler.Output));
                    if (singleShapeIndex >= 0)
                        BuildMorphPointerDriver(
                            smr,
                            singleShapeIndex,
                            times,
                            values,
                            sampler.Interpolation,
                            ResolveMorphBaseValue(root, targetNodeIndex, singleShapeIndex),
                            output);
                    else
                        BuildMorphDrivers(
                            smr,
                            times,
                            values,
                            sampler.Interpolation,
                            ResolveMorphBaseValues(root, targetNodeIndex, smr.sharedMesh.blendShapeCount),
                            output);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[KHR_character] Skipping morph channel {channelIndex}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Turns decoded keyframe times + flat weight values into one delta-over-rest <see cref="MorphDriver"/>
        /// per blendshape. The weight output is frame-major; CUBICSPLINE keyframes are [inTangent, value,
        /// outTangent] blocks and only the value block is used. Single-key samplers store the absolute target.
        /// </summary>
        internal static void BuildMorphDrivers(
            SkinnedMeshRenderer smr, float[] times, float[] values, InterpolationType interpolation, List<MorphDriver> output)
            => BuildMorphDrivers(smr, times, values, interpolation, null, output);

        internal static void BuildMorphDrivers(
            SkinnedMeshRenderer smr,
            float[] times,
            float[] values,
            InterpolationType interpolation,
            IReadOnlyList<float> baseValues,
            List<MorphDriver> output)
        {
            if (smr?.sharedMesh == null || times == null || values == null) return;
            int n = times.Length;
            if (n == 0 || values.Length == 0) return;

            bool isCubic = interpolation == InterpolationType.CUBICSPLINE;
            int valueStride = isCubic ? 3 : 1;
            var interp = MapInterp(interpolation);

            int blendShapeCount = smr.sharedMesh.blendShapeCount;
            int morphCount = values.Length / n / valueStride;
            if (morphCount <= 0) return;
            if (morphCount != blendShapeCount)
                Debug.LogWarning($"[KHR_character] weights channel on '{smr.name}' has {morphCount} targets but the mesh has {blendShapeCount} blendshapes; baking the overlap.");

            var samplerData = BuildSampler(times, interp);
            int count = Mathf.Min(morphCount, blendShapeCount);

            for (int i = 0; i < count; i++)
            {
                var deltas = new float[n];
                if (n == 1)
                {
                    deltas[0] = SampleWeight(values, 0, i, morphCount, valueStride);
                }
                else
                {
                    float frame0 = SampleWeight(values, 0, i, morphCount, valueStride);
                    for (int k = 0; k < n; k++)
                        deltas[k] = SampleWeight(values, k, i, morphCount, valueStride) - frame0;
                }

                output.Add(new MorphDriver
                {
                    Smr = smr,
                    BlendShapeIndex = i,
                    Sampler = samplerData,
                    DeltaValues = deltas,
                    BaseValue = baseValues != null && i < baseValues.Count ? baseValues[i] : 0f,
                });
            }
        }

        // Weight of morph element i at keyframe k. For CUBICSPLINE each keyframe block is
        // [inTangents(M), values(M), outTangents(M)]; we take the value sub-block.
        private static float SampleWeight(float[] values, int k, int i, int morphCount, int valueStride)
        {
            int index = (valueStride == 3)
                ? k * morphCount * 3 + morphCount + i
                : k * morphCount + i;
            return (index >= 0 && index < values.Length) ? values[index] : 0f;
        }

        /// <summary>
        /// Builds one delta-over-rest <see cref="MorphDriver"/> from a KHR_animation_pointer
        /// "/nodes/{i}/weights/{j}" channel: a scalar sampler driving a single blendshape (unlike the standard
        /// "weights" channel that drives all of a node's blendshapes at once). CUBICSPLINE keyframes are
        /// [inTangent, value, outTangent] blocks; only the value is used. Single-key samplers store the absolute target.
        /// </summary>
        internal static void BuildMorphPointerDriver(
            SkinnedMeshRenderer smr, int blendShapeIndex, float[] times, float[] values, InterpolationType interpolation, List<MorphDriver> output)
            => BuildMorphPointerDriver(smr, blendShapeIndex, times, values, interpolation, 0f, output);

        internal static void BuildMorphPointerDriver(
            SkinnedMeshRenderer smr,
            int blendShapeIndex,
            float[] times,
            float[] values,
            InterpolationType interpolation,
            float baseValue,
            List<MorphDriver> output)
        {
            if (smr?.sharedMesh == null || times == null || values == null) return;
            int n = times.Length;
            if (n == 0 || values.Length == 0) return;
            if (blendShapeIndex < 0 || blendShapeIndex >= smr.sharedMesh.blendShapeCount)
            {
                Debug.LogWarning($"[KHR_character] morph pointer targets blendshape {blendShapeIndex} but '{smr.name}' has {smr.sharedMesh.blendShapeCount}; skipping.");
                return;
            }

            bool isCubic = interpolation == InterpolationType.CUBICSPLINE;
            int stride = isCubic ? 3 : 1, off = isCubic ? 1 : 0;
            if (values.Length < n * stride) return;

            var deltas = new float[n];
            if (n == 1)
            {
                deltas[0] = values[off];
            }
            else
            {
                float frame0 = values[off];
                for (int k = 0; k < n; k++) deltas[k] = values[k * stride + off] - frame0;
            }

            output.Add(new MorphDriver
            {
                Smr = smr,
                BlendShapeIndex = blendShapeIndex,
                Sampler = BuildSampler(times, MapInterp(interpolation)),
                DeltaValues = deltas,
                BaseValue = baseValue,
            });
        }

        internal static float[] ResolveMorphBaseValues(
            GLTFRoot root,
            int nodeIndex,
            int blendShapeCount)
        {
            var result = new float[Mathf.Max(0, blendShapeCount)];
            for (int index = 0; index < result.Length; index++)
                result[index] = ResolveMorphBaseValue(root, nodeIndex, index);
            return result;
        }

        internal static float ResolveMorphBaseValue(GLTFRoot root, int nodeIndex, int blendShapeIndex)
        {
            if (root?.Nodes == null || nodeIndex < 0 || nodeIndex >= root.Nodes.Count || blendShapeIndex < 0)
                return 0f;
            var node = root.Nodes[nodeIndex];
            var weights = node?.Weights ?? node?.Mesh?.Value?.Weights;
            return weights != null && blendShapeIndex < weights.Count
                ? (float)weights[blendShapeIndex]
                : 0f;
        }

        // ── Joint (node TRS) channels ──────────────────────────────

        private static void BakeJointChannels(
            GLTFRoot root, GLTFSceneImporter importer, GLTFAnimation animation,
            int[] channelIndices, IReadOnlyDictionary<int, GameObject> nodeIndexToGo, List<JointDriver> output)
        {
            foreach (var channelIndex in channelIndices)
            {
                try
                {
                    if (channelIndex < 0 || channelIndex >= animation.Channels.Count) continue;
                    var channel = animation.Channels[channelIndex];
                    if (!TryResolveJointTarget(channel, out int nodeIndex, out string path)) continue;
                    if (!nodeIndexToGo.TryGetValue(nodeIndex, out var go) || go == null) continue;
                    var target = go.transform;

                    int samplerIndex = channel.Sampler?.Id ?? -1;
                    if (samplerIndex < 0 || samplerIndex >= animation.Samplers.Count) continue;
                    var sampler = animation.Samplers[samplerIndex];

                    var times = DecodeScalar(importer, GetAccessor(root, sampler.Input));
                    if (times.Length == 0) continue;

                    if (path == "rotation")
                    {
                        var raw = DecodeVec4(importer, GetAccessor(root, sampler.Output));
                        if (raw.Length == 0) continue;
                        // CUBICSPLINE tangents are not rotations and may be zero; only value records are normalized.
                        bool isCubic = sampler.Interpolation == InterpolationType.CUBICSPLINE;
                        var quats = new Quaternion[raw.Length];
                        for (int k = 0; k < raw.Length; k++)
                        {
                            var key = raw[k].ToUnityQuaternionConvert();
                            quats[k] = isCubic && k % 3 != 1 ? key : Normalized(key);
                        }
                        BuildJointRotationDriver(target, times, quats, sampler.Interpolation, target.localRotation, output);
                    }
                    else
                    {
                        var raw = DecodeVec3(importer, GetAccessor(root, sampler.Output));
                        if (raw.Length == 0) continue;
                        bool isTranslation = path == "translation";
                        var vecs = new Vector3[raw.Length];
                        for (int k = 0; k < raw.Length; k++)
                            vecs[k] = isTranslation ? raw[k].ToUnityVector3Convert() : raw[k].ToUnityVector3Raw();
                        var channelKind = isTranslation ? TrsChannel.Translation : TrsChannel.Scale;
                        var baseVec = isTranslation ? target.localPosition : target.localScale;
                        BuildJointVectorDriver(target, channelKind, times, vecs, sampler.Interpolation, baseVec, output);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[KHR_character] Skipping joint channel {channelIndex}: {e.Message}");
                }
            }
        }

        // A joint channel targets node translation, rotation or scale through target.node or through a
        // KHR_animation_pointer "/nodes/{n}/{path}" pointer.
        internal static bool TryResolveJointTarget(AnimationChannel channel, out int nodeIndex, out string path)
        {
            nodeIndex = -1;
            path = null;
            if (channel?.Target == null) return false;
            if (channel.Target.Node != null)
            {
                nodeIndex = channel.Target.Node.Id;
                path = channel.Target.Path;
            }
            else
            {
                var parts = GetPointer(channel)?.Split('/');
                if (parts == null || parts.Length != 4 || parts[0].Length != 0 || parts[1] != "nodes" ||
                    !int.TryParse(parts[2], out nodeIndex))
                    return false;
                path = parts[3];
            }
            return path == "translation" || path == "rotation" || path == "scale";
        }

        // Rotation keys hold unit quaternions; decoded normalized-integer keys are only approximately unit length.
        private static Quaternion Normalized(Quaternion value)
        {
            float length = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            if (!(length > 0f) || float.IsInfinity(length))
                throw new InvalidOperationException("a rotation key has zero or non-finite length");
            return new Quaternion(value.x / length, value.y / length, value.z / length, value.w / length);
        }

        /// <summary>
        /// Builds a delta-over-rest translation/scale <see cref="JointDriver"/> from Unity-space keyframe values.
        /// CUBICSPLINE keyframes are [inTangent, value, outTangent]; only the value block is used. Single-key
        /// samplers store the absolute target.
        /// </summary>
        internal static void BuildJointVectorDriver(
            Transform target, TrsChannel channel, float[] times, Vector3[] values, InterpolationType interpolation,
            Vector3 baseVec, List<JointDriver> output)
        {
            int n = times?.Length ?? 0;
            if (target == null || values == null || n == 0) return;
            bool isCubic = interpolation == InterpolationType.CUBICSPLINE;
            int stride = isCubic ? 3 : 1, off = isCubic ? 1 : 0;
            if (values.Length < n * stride) return;

            var deltas = new Vector3[n];
            if (n == 1)
            {
                deltas[0] = values[off];
            }
            else
            {
                var frame0 = values[off];
                for (int k = 0; k < n; k++) deltas[k] = values[k * stride + off] - frame0;
            }

            output.Add(new JointDriver
            {
                Target = target,
                Channel = channel,
                Sampler = BuildSampler(times, MapInterp(interpolation)),
                DeltaVec = deltas,
                BaseVec = baseVec,
            });
        }

        /// <summary>
        /// Builds a delta-over-rest rotation <see cref="JointDriver"/> from Unity-space keyframe quaternions.
        /// Multi-key deltas are frame-0-relative (q * inverse(frame0)); single-key stores the absolute target.
        /// </summary>
        internal static void BuildJointRotationDriver(
            Transform target, float[] times, Quaternion[] values, InterpolationType interpolation,
            Quaternion baseQuat, List<JointDriver> output)
        {
            int n = times?.Length ?? 0;
            if (target == null || values == null || n == 0) return;
            bool isCubic = interpolation == InterpolationType.CUBICSPLINE;
            int stride = isCubic ? 3 : 1, off = isCubic ? 1 : 0;
            if (values.Length < n * stride) return;

            var deltas = new Quaternion[n];
            if (n == 1)
            {
                deltas[0] = values[off];
            }
            else
            {
                var frame0Inv = Quaternion.Inverse(values[off]);
                for (int k = 0; k < n; k++) deltas[k] = values[k * stride + off] * frame0Inv;
            }

            output.Add(new JointDriver
            {
                Target = target,
                Channel = TrsChannel.Rotation,
                Sampler = BuildSampler(times, MapInterp(interpolation)),
                DeltaQuat = deltas,
                BaseQuat = baseQuat,
            });
        }

        // ── Texture channels (KHR_animation_pointer: UV transform + index swap) ──

        private static void BakeTextureChannels(
            GLTFRoot root, GLTFSceneImporter importer, MaterialPropertiesRemapper remapper, GLTFAnimation animation,
            int[] channelIndices, IReadOnlyDictionary<int, GameObject> nodeIndexToGo, List<TextureDriver> output)
        {
            foreach (var channelIndex in channelIndices)
            {
                try
                {
                    if (channelIndex < 0 || channelIndex >= animation.Channels.Count) continue;
                    var channel = animation.Channels[channelIndex];
                    if (channel?.Target == null) continue;

                    var pointer = GetPointer(channel);
                    if (pointer == null) continue;
                    if (!TryParseMaterialPointer(pointer, out int matIndex, out string gltfProperty)) continue;

                    if (importer.MaterialCache == null || matIndex < 0 || matIndex >= importer.MaterialCache.Length) continue;
                    var mat = importer.MaterialCache[matIndex]?.UnityMaterial;
                    if (mat == null) continue;
                    var rendererSlots = ResolveRendererSlots(root, nodeIndexToGo, matIndex);
                    if (rendererSlots.Count == 0) continue;

                    int samplerIndex = channel.Sampler?.Id ?? -1;
                    if (samplerIndex < 0 || samplerIndex >= animation.Samplers.Count) continue;
                    var sampler = animation.Samplers[samplerIndex];

                    var times = DecodeScalar(importer, GetAccessor(root, sampler.Input));
                    if (times.Length == 0) continue;

                    if (gltfProperty.Contains("KHR_texture_transform"))
                    {
                        if (!remapper.GetUnityPropertyName(mat, gltfProperty, out string unityName, out var map, out bool isSecondary)) continue;
                        if (map.PropertyType != MaterialPointerPropertyMap.PropertyTypeOption.TextureTransform) continue;

                        var raw = DecodeVec2(importer, GetAccessor(root, sampler.Output));
                        if (raw.Length == 0) continue;

                        int propId = Shader.PropertyToID(unityName);
                        var baseSt = mat.GetVector(propId);
                        var baseScale = new Vector2(baseSt.x, baseSt.y);
                        var baseOffset = new Vector2(baseSt.z, 1f - baseSt.w - baseSt.y);
                        var transformTarget = isSecondary
                            ? TextureTransformTarget.Offset
                            : TextureTransformTarget.Scale;
                        var stValues = new Vector4[raw.Length];
                        for (int k = 0; k < raw.Length; k++)
                        {
                            var value = new Vector2(raw[k].x, raw[k].y);
                            stValues[k] = isSecondary
                                ? PackSt(baseScale, value)
                                : PackSt(value, baseOffset);
                        }

                        foreach (var rendererSlot in rendererSlots)
                            BuildUvTransformDriver(
                                rendererSlot.Renderer,
                                rendererSlot.Slot,
                                propId,
                                times,
                                stValues,
                                baseSt,
                                sampler.Interpolation,
                                output,
                                StripStSuffix(unityName),
                                SlotFromGltfProperty(map.GltfPropertyName),
                                transformTarget);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[KHR_character] Skipping texture channel {channelIndex}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Builds a delta-over-rest UV-transform <see cref="TextureDriver"/> from absolute Unity _ST keyframes.
        /// Multi-key deltas are frame-0-relative; single-key stores the absolute target.
        /// </summary>
        internal static void BuildUvTransformDriver(
            Renderer renderer, int slot, int propId, float[] times, Vector4[] stValues, Vector4 baseSt,
            InterpolationType interpolation, List<TextureDriver> output,
            string propertyName = null, string gltfTextureSlot = null,
            TextureTransformTarget transformTarget = TextureTransformTarget.Combined)
        {
            int n = times?.Length ?? 0;
            if (renderer == null || stValues == null || n == 0 || stValues.Length < n) return;

            var deltas = new Vector4[n];
            if (n == 1)
            {
                deltas[0] = stValues[0];
            }
            else
            {
                var frame0 = stValues[0];
                for (int k = 0; k < n; k++) deltas[k] = stValues[k] - frame0;
            }

            output.Add(new TextureDriver
            {
                Renderer = renderer,
                SubmeshSlot = slot,
                PropertyId = propId,
                PropertyName = propertyName,
                GltfTextureSlot = gltfTextureSlot,
                TransformTarget = transformTarget,
                Sampler = BuildSampler(times, MapInterp(interpolation)),
                StValues = deltas,
                BaseSt = baseSt,
                // Frame-0 absolute _ST is retained as legacy/provenance diagnostic metadata. Conformant export
                // anchors on BaseSt so the time-zero value matches the static Asset Object Model value.
                Frame0St = stValues[0],
                HasFrame0St = true,
            });
        }

        // glTF (scale, offset) -> Unity _ST = (tiling.xy, offset.zw), with the V-axis flip UnityGLTF uses.
        internal static Vector4 PackSt(Vector2 gltfScale, Vector2 gltfOffset)
            => new Vector4(gltfScale.x, gltfScale.y, gltfOffset.x, 1f - gltfOffset.y - gltfScale.y);

        // Export-only metadata helpers (G-B): the texture property without its "_ST" suffix, and the glTF slot path
        // with any trailing "/extensions/KHR_texture_transform/<scale|offset>" removed.
        private static string StripStSuffix(string unityName)
            => (!string.IsNullOrEmpty(unityName) && unityName.EndsWith("_ST", StringComparison.Ordinal))
                ? unityName.Substring(0, unityName.Length - 3) : unityName;

        private static string SlotFromGltfProperty(string gltfPropertyName)
        {
            if (string.IsNullOrEmpty(gltfPropertyName)) return gltfPropertyName;
            int extIdx = gltfPropertyName.IndexOf("/extensions/", StringComparison.Ordinal);
            return extIdx > 0 ? gltfPropertyName.Substring(0, extIdx) : gltfPropertyName;
        }

        private static string GetPointer(AnimationChannel channel)
        {
            if (channel.Target?.Extensions != null
                && channel.Target.Extensions.TryGetValue(KHR_animation_pointer.EXTENSION_NAME, out var ext)
                && ext is KHR_animation_pointer p)
                return p.path;
            return null;
        }

        private static bool TryParseMaterialPointer(string pointer, out int materialIndex, out string gltfProperty)
        {
            materialIndex = -1;
            gltfProperty = null;
            if (string.IsNullOrEmpty(pointer)) return false;
            var parts = pointer.Split('/'); // ["", "materials", "{m}", ...]
            if (parts.Length < 4 || parts[1] != "materials") return false;
            if (!int.TryParse(parts[2], out materialIndex)) return false;
            gltfProperty = string.Join("/", parts, 3, parts.Length - 3);
            return true;
        }

        // Parse a KHR_animation_pointer morph-weight path of the exact form "/nodes/{nodeIndex}/weights/{blendShapeIndex}"
        // (the VRM per-blendshape convention). Other shapes (e.g. "/meshes/.../weights") are rejected.
        internal static bool TryParseNodeWeightsPointer(string pointer, out int nodeIndex, out int blendShapeIndex)
        {
            nodeIndex = -1;
            blendShapeIndex = -1;
            if (string.IsNullOrEmpty(pointer)) return false;
            var parts = pointer.Split('/'); // ["", "nodes", "{i}", "weights", "{j}"]
            if (parts.Length != 5 || parts[1] != "nodes" || parts[3] != "weights") return false;
            return int.TryParse(parts[2], out nodeIndex) && int.TryParse(parts[4], out blendShapeIndex);
        }

        internal static List<(Renderer Renderer, int Slot)> ResolveRendererSlots(
            GLTFRoot root,
            IReadOnlyDictionary<int, GameObject> nodeIndexToGo,
            int materialIndex)
        {
            var result = new List<(Renderer Renderer, int Slot)>();
            if (root?.Nodes == null || nodeIndexToGo == null) return result;
            for (int nodeId = 0; nodeId < root.Nodes.Count; nodeId++)
            {
                if (!nodeIndexToGo.TryGetValue(nodeId, out var go) || go == null) continue;
                var prims = root.Nodes[nodeId]?.Mesh?.Value?.Primitives;
                if (prims == null) continue;
                var renderer = go.GetComponent<Renderer>();
                if (renderer == null) continue;
                for (int s = 0; s < prims.Count; s++)
                {
                    if (prims[s]?.Material == null || prims[s].Material.Id != materialIndex) continue;
                    result.Add((renderer, s));
                }
            }
            return result;
        }

        private static bool TryResolveTextureProperty(MaterialPropertiesRemapper remapper, Material mat, string gltfProperty, out int propId, out string unityTextureName)
        {
            propId = 0;
            unityTextureName = null;
            int idx = gltfProperty.LastIndexOf("/index", StringComparison.Ordinal);
            if (idx <= 0) return false;
            var slot = gltfProperty.Substring(0, idx); // e.g. pbrMetallicRoughness/baseColorTexture

            // Reuse the remapper's texture-transform entry for this slot to find the Unity texture name (strip "_ST").
            var ttOffset = slot + "/extensions/KHR_texture_transform/offset";
            if (remapper.GetUnityPropertyName(mat, ttOffset, out string stName, out _, out _) && stName.EndsWith("_ST", StringComparison.Ordinal))
            {
                var texName = stName.Substring(0, stName.Length - 3);
                if (mat.HasProperty(texName)) { propId = Shader.PropertyToID(texName); unityTextureName = texName; return true; }
            }

            foreach (var name in new[] { "_BaseMap", "_MainTex", "_BaseColorTexture" })
                if (mat.HasProperty(name)) { propId = Shader.PropertyToID(name); unityTextureName = name; return true; }
            return false;
        }

        private static Texture ResolveTexture(GLTFSceneImporter importer, int textureIndex)
        {
            if (importer.TextureCache == null || textureIndex < 0 || textureIndex >= importer.TextureCache.Length) return null;
            return importer.TextureCache[textureIndex]?.Texture;
        }

        // ── Suppression + mapping resolution (wire expression indices -> track indices) ────

        internal static bool TryApplyMappingSets(
            GLTFRoot root,
            KHR_character_expression_mapping mappingExt,
            IReadOnlyDictionary<int, int> wireToTrackIndex,
            IReadOnlyList<KHR_character_expression.ExpressionItem> wireExpressions,
            CharacterExpressionSet set)
        {
            if (set == null) throw new ArgumentNullException(nameof(set));
            try
            {
                set.MappingSets = BuildMappingSets(mappingExt, wireToTrackIndex, wireExpressions);
                set.InputMappingSets = BuildInputMappingSets(mappingExt, wireToTrackIndex, wireExpressions);
                if (set.MappingSets != null || set.InputMappingSets != null)
                {
                    set.MappingExtensionsJson = mappingExt?.Extensions?.ToString(
                        Newtonsoft.Json.Formatting.None);
                    set.MappingExtrasJson = mappingExt?.Extras?.ToString(Newtonsoft.Json.Formatting.None);
                    set.MappingAdditionalPropertiesJson = mappingExt?.AdditionalProperties?.ToString(
                        Newtonsoft.Json.Formatting.None);
                    set.MappingRequiredCompanionExtensions = GetRequiredMappingCompanionExtensions(
                        root, mappingExt, set.MappingSets, set.InputMappingSets);
                }
                return true;
            }
            catch (InvalidOperationException exception)
            {
                set.MappingSets = null;
                set.InputMappingSets = null;
                set.MappingExtensionsJson = null;
                set.MappingExtrasJson = null;
                set.MappingAdditionalPropertiesJson = null;
                set.MappingRequiredCompanionExtensions = null;
                Debug.LogError(
                    $"[KHR_character] Expression mapping validation failed; no mapping adapter was exposed. {exception.Message}");
                return false;
            }
        }

        internal static SuppressionEntry[] BuildSuppressionEntries(
            KHR_character_expression_suppression suppression,
            int sourceIndex,
            IReadOnlyDictionary<int, int> wireToTrackIndex)
        {
            var list = new List<SuppressionEntry>();
            if (suppression?.Suppressions != null)
            {
                foreach (var s in suppression.Suppressions)
                {
                    if (s == null || !wireToTrackIndex.TryGetValue(s.Target, out int targetIndex))
                    {
                        Debug.LogWarning($"[KHR_character] Suppression references invalid expression index {s?.Target ?? -1}; dropping.");
                        continue;
                    }
                    string type = s.Type?.Trim();
                    bool isBlock = string.Equals(type, "block", StringComparison.Ordinal);
                    bool isBlend = string.IsNullOrEmpty(type) ||
                        string.Equals(type, "blend", StringComparison.Ordinal);
                    list.Add(new SuppressionEntry
                    {
                        TargetIndex = targetIndex,
                        SourceIndex = sourceIndex,
                        Type = isBlock ? SuppressionType.Block : isBlend ? SuppressionType.Blend : SuppressionType.Identity,
                        CustomType = isBlend || isBlock ? null : type,
                        RawExtensionsJson = s.Extensions?.ToString(Newtonsoft.Json.Formatting.None),
                        RawExtrasJson = s.Extras?.ToString(Newtonsoft.Json.Formatting.None),
                        RawAdditionalPropertiesJson = s.AdditionalProperties?.ToString(
                            Newtonsoft.Json.Formatting.None),
                        Amount = s.Amount ?? 1d,
                        HasAmount = s.Amount.HasValue,
                        Threshold = s.Threshold ?? 0d,
                        HasThreshold = s.Threshold.HasValue,
                    });
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// Builds current external-input mapping sets. An empty identifier throws, or, when <paramref name="onInvalidSet"/> is
        /// supplied, drops only that set and reports it.
        /// </summary>
        internal static ExpressionMappingSet[] BuildMappingSets(
            KHR_character_expression_mapping mappingExt,
            IReadOnlyDictionary<int, int> wireToTrackIndex,
            IReadOnlyList<KHR_character_expression.ExpressionItem> wireExpressions = null,
            Action<string> onInvalidSet = null)
        {
            if (mappingExt?.ExpressionSetMappings == null) return null;
            var sets = new List<ExpressionMappingSet>();
            foreach (var setKv in mappingExt.ExpressionSetMappings)
            {
                if (!KHR_character_expression_mapping.IsValidMappingSetIdentifier(setKv.Key))
                {
                    if (onInvalidSet == null) throw new InvalidOperationException("A mapping-set identifier is empty.");
                    onInvalidSet("an expressionSetMappings identifier is empty.");
                    continue;
                }
                var targets = new List<MappingTarget>();
                if (setKv.Value != null)
                {
                    foreach (var targetKv in setKv.Value)
                    {
                        var contributions = new List<MappingContribution>();
                        if (targetKv.Value != null)
                        {
                            foreach (var sw in targetKv.Value)
                            {
                                if (!wireToTrackIndex.TryGetValue(sw.Source, out int srcIndex))
                                {
                                    Debug.LogWarning($"[KHR_character] Mapping references invalid expression index {sw.Source}; dropping.");
                                    continue;
                                }
                                contributions.Add(new MappingContribution
                                {
                                    SourceIndex = srcIndex,
                                    Name = sw.Name,
                                    Weight = sw.Weight,
                                    ExtensionsJson = sw.Extensions?.ToString(Newtonsoft.Json.Formatting.None),
                                    ExtrasJson = sw.Extras?.ToString(Newtonsoft.Json.Formatting.None),
                                    AdditionalPropertiesJson = sw.AdditionalProperties?.ToString(
                                        Newtonsoft.Json.Formatting.None),
                                });
                            }
                        }
                        if (contributions.Count > 0)
                            targets.Add(new MappingTarget { TargetName = targetKv.Key, Contributions = contributions.ToArray() });
                    }
                }
                if (targets.Count > 0)
                    sets.Add(new ExpressionMappingSet { SetName = setKv.Key, Targets = targets.ToArray() });
            }
            return sets.Count > 0 ? sets.ToArray() : null;
        }

        /// <summary>
        /// Builds the input mapping sets. An empty identifier throws, or, when <paramref name="onInvalidSet"/> is
        /// supplied, drops only that set and reports it.
        /// </summary>
        internal static ExpressionInputMappingSet[] BuildInputMappingSets(
            KHR_character_expression_mapping mappingExt,
            IReadOnlyDictionary<int, int> wireToTrackIndex,
            IReadOnlyList<KHR_character_expression.ExpressionItem> wireExpressions = null,
            Action<string> onInvalidSet = null)
        {
            return ExpressionDriverPipeline.ToInputMappingSets(
                BuildMappingSets(mappingExt, wireToTrackIndex, wireExpressions, onInvalidSet));
        }

        internal static KHR_character_expression_mapping GetMappingExtension(GLTFRoot root)
        {
            if (root.Extensions == null) return null;
            if (!root.Extensions.TryGetValue(KhrCharacterExtensionNames.ExpressionMapping, out var ext)) return null;
            if (ext is KHR_character_expression_mapping typed) return typed;
            if (ext is DefaultExtension raw && raw.ExtensionData != null)
                return new KHR_character_expression_mapping_Factory().Deserialize(root, raw.ExtensionData) as KHR_character_expression_mapping;
            return null;
        }

        internal static string[] GetRequiredSuppressionCompanionExtensions(
            GLTFRoot root,
            KHR_character_expression_suppression suppression,
            IReadOnlyList<SuppressionEntry> retainedSuppressions = null)
        {
            var present = new HashSet<string>();
            AddCompanionNames(present, suppression?.Extensions);
            if (retainedSuppressions != null)
            {
                foreach (var entry in retainedSuppressions)
                    AddCompanionNames(present, entry?.RawExtensionsJson);
            }
            else if (suppression?.Suppressions != null)
                foreach (var entry in suppression.Suppressions)
                    AddCompanionNames(present, entry?.Extensions);
            return GetDeclaredRequiredCompanions(root, present);
        }

        internal static string[] GetRequiredMappingCompanionExtensions(
            GLTFRoot root,
            KHR_character_expression_mapping mapping,
            IReadOnlyList<ExpressionMappingSet> retainedForward = null,
            IReadOnlyList<ExpressionInputMappingSet> retainedInput = null)
        {
            var present = new HashSet<string>();
            AddCompanionNames(present, mapping?.Extensions);
            if (retainedForward != null || retainedInput != null)
            {
                if (retainedForward != null)
                    foreach (var set in retainedForward)
                        if (set?.Targets != null)
                            foreach (var target in set.Targets)
                                if (target?.Contributions != null)
                                    foreach (var contribution in target.Contributions)
                                        AddCompanionNames(present, contribution.ExtensionsJson);
                if (retainedInput != null)
                    foreach (var set in retainedInput)
                        if (set?.Commands != null)
                            foreach (var command in set.Commands)
                                if (command?.Contributions != null)
                                    foreach (var contribution in command.Contributions)
                                        AddCompanionNames(present, contribution.ExtensionsJson);
            }
            else
            {
                if (mapping?.ExpressionSetMappings != null)
                    foreach (var set in mapping.ExpressionSetMappings.Values)
                        if (set != null)
                            foreach (var endpoint in set.Values)
                                if (endpoint != null)
                                    foreach (var contribution in endpoint)
                                        AddCompanionNames(present, contribution.Extensions);
                if (mapping?.ExpressionSetInputMappings != null)
                    foreach (var set in mapping.ExpressionSetInputMappings.Values)
                        if (set != null)
                            foreach (var endpoint in set.Values)
                                if (endpoint != null)
                                    foreach (var contribution in endpoint)
                                        AddCompanionNames(present, contribution.Extensions);
            }
            return GetDeclaredRequiredCompanions(root, present);
        }

        private static void AddCompanionNames(HashSet<string> names, JObject extensions)
        {
            if (extensions == null) return;
            foreach (var extension in extensions.Properties()) names.Add(extension.Name);
        }

        private static void AddCompanionNames(HashSet<string> names, string extensionsJson)
        {
            if (string.IsNullOrEmpty(extensionsJson)) return;
            try { AddCompanionNames(names, JObject.Parse(extensionsJson)); }
            catch { }
        }

        private static string[] GetDeclaredRequiredCompanions(GLTFRoot root, HashSet<string> present)
        {
            if (root?.ExtensionsRequired == null || present.Count == 0)
                return Array.Empty<string>();
            var required = new List<string>();
            foreach (var extension in root.ExtensionsRequired)
                if (present.Contains(extension)) required.Add(extension);
            return required.ToArray();
        }

        // ── Shared helpers ───────────────────────────────────────────────────

        private static Sampler BuildSampler(float[] times, Interp interp) => new Sampler
        {
            Times = times,
            Interp = interp,
            SingleKey = times.Length <= 1,
        };

        private static Interp MapInterp(InterpolationType t)
        {
            switch (t)
            {
                case InterpolationType.STEP: return Interp.Step;
                case InterpolationType.LINEAR: return Interp.Linear;
                // CUBICSPLINE/CATMULLROMSPLINE values are sampled and treated linearly for now.
                default: return Interp.Linear;
            }
        }

        private static Accessor GetAccessor(GLTFRoot root, AccessorId id)
        {
            if (id == null || root?.Accessors == null) return null;
            return (id.Id >= 0 && id.Id < root.Accessors.Count) ? root.Accessors[id.Id] : null;
        }

        private static float[] DecodeScalar(GLTFSceneImporter importer, Accessor accessor)
        {
            if (accessor == null) return Array.Empty<float>();
            if (accessor.BufferView == null)
            {
                Debug.LogWarning("[KHR_character] sparse animation accessors are not supported yet; skipping a channel.");
                return Array.Empty<float>();
            }
            var data = importer.GetBufferViewData(accessor.BufferView.Value);
            var numeric = new NumericArray();
            return accessor.AsFloatArray(ref numeric, data, 0) ?? Array.Empty<float>();
        }

        private static float3[] DecodeVec3(GLTFSceneImporter importer, Accessor accessor)
        {
            if (accessor?.BufferView == null) return Array.Empty<float3>();
            var data = importer.GetBufferViewData(accessor.BufferView.Value);
            var numeric = new NumericArray();
            return accessor.AsFloat3Array(ref numeric, data, 0) ?? Array.Empty<float3>();
        }

        private static float4[] DecodeVec4(GLTFSceneImporter importer, Accessor accessor)
        {
            if (accessor?.BufferView == null) return Array.Empty<float4>();
            var data = importer.GetBufferViewData(accessor.BufferView.Value);
            var numeric = new NumericArray();
            return accessor.AsFloat4Array(ref numeric, data, 0) ?? Array.Empty<float4>();
        }

        private static float2[] DecodeVec2(GLTFSceneImporter importer, Accessor accessor)
        {
            if (accessor?.BufferView == null) return Array.Empty<float2>();
            var data = importer.GetBufferViewData(accessor.BufferView.Value);
            var numeric = new NumericArray();
            return accessor.AsFloat2Array(ref numeric, data, 0) ?? Array.Empty<float2>();
        }
    }
}
