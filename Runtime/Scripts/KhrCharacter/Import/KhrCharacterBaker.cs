using System;
using System.Collections.Generic;
using GLTF.Schema;
using Unity.Mathematics;
using UnityEngine;
using UnityGLTF.Extensions;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Builds a <see cref="CharacterExpressionSet"/> from a parsed <c>KHR_character_expression</c> at import
    /// time. Bakes morph-target (blendshape weight) and joint (node TRS) expressions; the texture domain is
    /// added separately. Each curve is stored as deltas over the animation's frame-0 value. Morph weights are
    /// raw glTF [0..1] (the frame-weight multiplier is applied at evaluation time); joint values are converted
    /// to Unity space at bake.
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
            var rawMasks = new List<KHR_character_expression_mask>(); // aligned with tracks, for the post-pass

            foreach (var item in expressionExt.Expressions)
            {
                if (item == null || string.IsNullOrEmpty(item.Expression)) continue;
                if (root.Animations == null || item.Animation < 0 || item.Animation >= root.Animations.Count)
                {
                    Debug.LogWarning($"[KHR_character] Expression '{item.Expression}' references an invalid animation index ({item.Animation}); skipping.");
                    continue;
                }

                var animation = root.Animations[item.Animation];
                // The glTF schema carries neither a per-expression blendMode nor a driver priority yet, so
                // everything bakes as Additive with Priority = 0. Override is a runtime-selectable compositing
                // policy (ExpressionController honors ExpressionTrack.BlendMode / driver Priority) pending a
                // PR #2512 blendMode/priority discriminator; wire those through here once the schema lands.
                var track = new ExpressionTrack
                {
                    Name = item.Expression,
                    BlendMode = ExpressionBlendMode.Additive,
                };

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
                if (item.Texture?.Channels != null)
                    BakeTextureChannels(root, importer, remapper, animation, item.Texture.Channels, nodeIndexToGo, textureDrivers);
                if (textureDrivers.Count > 0)
                {
                    track.TextureDrivers = textureDrivers.ToArray();
                    track.Domains |= ExpressionDomain.Texture;
                }

                // Binary = every animated channel this expression carries (morph/joint/texture) is STEP, so the
                // weight only ever resolves to discrete states -> a 0/1-snapping control fits. Spans all domains,
                // not morph-only.
                track.IsBinary = AllStep(track);

                rawMasks.Add(item.Mask);
                tracks.Add(track);
            }

            var set = new CharacterExpressionSet { Expressions = tracks.ToArray() };
            set.RebuildIndex();

            // Masks and mappings resolve expression NAMES to track indices, so they run after the index exists.
            for (int i = 0; i < tracks.Count; i++)
            {
                if (rawMasks[i] == null) continue;
                var masks = BuildMaskEntries(rawMasks[i], i, set.NameToIndex);
                if (masks.Length > 0) tracks[i].Masks = masks;
            }

            var mappingExt = GetMappingExtension(root);
            if (mappingExt != null)
                set.MappingSets = BuildMappingSets(mappingExt, set.NameToIndex);

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
                    if (channel.Target.Path == "weights" && channel.Target.Node != null)
                    {
                        if (!nodeIndexToGo.TryGetValue(channel.Target.Node.Id, out var go) || go == null) continue;
                        smr = go.GetComponent<SkinnedMeshRenderer>();
                    }
                    else
                    {
                        var pointer = GetPointer(channel);
                        if (pointer == null || !TryParseNodeWeightsPointer(pointer, out int nodeIndex, out singleShapeIndex)) continue;
                        if (!nodeIndexToGo.TryGetValue(nodeIndex, out var go) || go == null) continue;
                        smr = go.GetComponent<SkinnedMeshRenderer>();
                    }
                    if (smr == null || smr.sharedMesh == null) continue;

                    int samplerIndex = channel.Sampler?.Id ?? -1;
                    if (samplerIndex < 0 || samplerIndex >= animation.Samplers.Count) continue;
                    var sampler = animation.Samplers[samplerIndex];

                    var times = DecodeScalar(importer, GetAccessor(root, sampler.Input));
                    var values = DecodeScalar(importer, GetAccessor(root, sampler.Output));
                    if (singleShapeIndex >= 0)
                        BuildMorphPointerDriver(smr, singleShapeIndex, times, values, sampler.Interpolation, output);
                    else
                        BuildMorphDrivers(smr, times, values, sampler.Interpolation, output);
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
                    BaseValue = 0f, // glTF default morph weight; node/mesh weight overrides are added later
                    Priority = 0,
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
                BaseValue = 0f,
                Priority = 0,
            });
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
                    if (channel?.Target?.Node == null) continue;

                    var path = channel.Target.Path;
                    if (path != "translation" && path != "rotation" && path != "scale") continue;

                    int nodeIndex = channel.Target.Node.Id;
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
                        var quats = new Quaternion[raw.Length];
                        for (int k = 0; k < raw.Length; k++) quats[k] = raw[k].ToUnityQuaternionConvert();
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
                Priority = 0,
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
                Priority = 0,
            });
        }

        // ── Texture channels (KHR_animation_pointer: UV transform + index swap) ──

        private sealed class UvGroup
        {
            public Renderer Renderer;
            public int Slot;
            public int PropId;            // the _ST property
            public Vector4 BaseSt;        // material's current _ST (Unity convention)
            public InterpolationType Interp;
            public float[] ScaleTimes;
            public Vector2[] ScaleVals;   // glTF scale keyframes (or null)
            public float[] OffsetTimes;
            public Vector2[] OffsetVals;  // glTF offset keyframes (or null)
            public string PropertyName;    // Unity texture property (no "_ST"); export-only metadata
            public string GltfTextureSlot; // full glTF slot path, e.g. pbrMetallicRoughness/baseColorTexture
        }

        private static void BakeTextureChannels(
            GLTFRoot root, GLTFSceneImporter importer, MaterialPropertiesRemapper remapper, GLTFAnimation animation,
            int[] channelIndices, IReadOnlyDictionary<int, GameObject> nodeIndexToGo, List<TextureDriver> output)
        {
            // Pair scale + offset channels that target the same material/_ST into one combined UV driver.
            var uvGroups = new Dictionary<(int, string), UvGroup>();

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
                    if (!TryResolveRendererSlot(root, importer, matIndex, out var renderer, out int slot)) continue;

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
                        var vec2 = new Vector2[raw.Length];
                        for (int k = 0; k < raw.Length; k++) vec2[k] = new Vector2(raw[k].x, raw[k].y);

                        int propId = Shader.PropertyToID(unityName);
                        var key = (matIndex, unityName);
                        if (!uvGroups.TryGetValue(key, out var group))
                        {
                            group = new UvGroup
                            {
                                Renderer = renderer, Slot = slot, PropId = propId,
                                BaseSt = mat.GetVector(propId), Interp = sampler.Interpolation,
                                // Export-only metadata (G-B): Unity texture property (strip "_ST") + full glTF slot
                                // path, so a later re-export can rebuild the KHR_animation_pointer paths.
                                PropertyName = StripStSuffix(unityName),
                                GltfTextureSlot = SlotFromGltfProperty(map.GltfPropertyName),
                            };
                            uvGroups[key] = group;
                        }
                        // The remapper's primary glTF property is "scale", secondary is "offset".
                        if (isSecondary) { group.OffsetTimes = times; group.OffsetVals = vec2; }
                        else { group.ScaleTimes = times; group.ScaleVals = vec2; }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[KHR_character] Skipping texture channel {channelIndex}: {e.Message}");
                }
            }

            foreach (var kv in uvGroups)
                BuildUvTransformDriverFromGroup(kv.Value, output);
        }

        private static void BuildUvTransformDriverFromGroup(UvGroup g, List<TextureDriver> output)
        {
            var times = g.OffsetVals != null ? g.OffsetTimes : g.ScaleTimes;
            if (times == null || times.Length == 0) return;

            // Recover base glTF scale/offset from the packed Unity _ST: _ST = (sx, sy, ox, 1 - oy - sy).
            var baseScale = new Vector2(g.BaseSt.x, g.BaseSt.y);
            var baseOffset = new Vector2(g.BaseSt.z, 1f - g.BaseSt.w - g.BaseSt.y);

            int n = times.Length;
            var st = new Vector4[n];
            for (int k = 0; k < n; k++)
            {
                var scale = (g.ScaleVals != null && k < g.ScaleVals.Length) ? g.ScaleVals[k] : baseScale;
                var offset = (g.OffsetVals != null && k < g.OffsetVals.Length) ? g.OffsetVals[k] : baseOffset;
                st[k] = PackSt(scale, offset);
            }
            BuildUvTransformDriver(g.Renderer, g.Slot, g.PropId, times, st, g.BaseSt, g.Interp, output,
                g.PropertyName, g.GltfTextureSlot);
        }

        /// <summary>
        /// Builds a delta-over-rest UV-transform <see cref="TextureDriver"/> from absolute Unity _ST keyframes.
        /// Multi-key deltas are frame-0-relative; single-key stores the absolute target.
        /// </summary>
        internal static void BuildUvTransformDriver(
            Renderer renderer, int slot, int propId, float[] times, Vector4[] stValues, Vector4 baseSt,
            InterpolationType interpolation, List<TextureDriver> output,
            string propertyName = null, string gltfTextureSlot = null)
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
                Sampler = BuildSampler(times, MapInterp(interpolation)),
                StValues = deltas,
                BaseSt = baseSt,
                // Frame-0 absolute _ST (before delta-izing). Export anchors multi-key reconstruction on this so a
                // foreign asset whose authored frame0 != material rest (baseSt) round-trips exactly on the first
                // cycle; BaseSt stays the runtime rest anchor only. HasFrame0St marks it captured (drivers that
                // skip this path — hand-authored sets — leave it false and export falls back to BaseSt).
                Frame0St = stValues[0],
                HasFrame0St = true,
                Priority = 0,
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

        private static bool TryResolveRendererSlot(GLTFRoot root, GLTFSceneImporter importer, int materialIndex, out Renderer renderer, out int slot)
        {
            renderer = null;
            slot = 0;
            if (root?.Nodes == null || importer?.NodeCache == null) return false;
            for (int nodeId = 0; nodeId < root.Nodes.Count && nodeId < importer.NodeCache.Length; nodeId++)
            {
                var go = importer.NodeCache[nodeId];
                if (go == null) continue;
                var prims = root.Nodes[nodeId]?.Mesh?.Value?.Primitives;
                if (prims == null) continue;
                for (int s = 0; s < prims.Count; s++)
                {
                    if (prims[s]?.Material == null || prims[s].Material.Id != materialIndex) continue;
                    var r = go.GetComponent<Renderer>();
                    if (r == null) break;
                    renderer = r;
                    slot = s;
                    return true;
                }
            }
            return false;
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

        // ── Mask + mapping resolution (expression names -> track indices) ────

        internal static MaskEntry[] BuildMaskEntries(KHR_character_expression_mask mask, int sourceIndex, IReadOnlyDictionary<string, int> nameToIndex)
        {
            var list = new List<MaskEntry>();
            if (mask?.Masks != null)
            {
                foreach (var m in mask.Masks)
                {
                    if (m?.Target == null) continue;
                    if (!nameToIndex.TryGetValue(m.Target, out int targetIndex))
                    {
                        Debug.LogWarning($"[KHR_character] Mask references unknown expression '{m.Target}'; dropping.");
                        continue;
                    }
                    list.Add(new MaskEntry
                    {
                        TargetIndex = targetIndex,
                        SourceIndex = sourceIndex,
                        Type = string.Equals(m.Type?.Trim(), "block", StringComparison.OrdinalIgnoreCase) ? MaskType.Block : MaskType.Blend,
                        Amount = m.Amount,
                        Threshold = m.Threshold,
                    });
                }
            }
            return list.ToArray();
        }

        internal static ExpressionMappingSet[] BuildMappingSets(KHR_character_expression_mapping mappingExt, IReadOnlyDictionary<string, int> nameToIndex)
        {
            if (mappingExt?.ExpressionSetMappings == null) return null;
            var sets = new List<ExpressionMappingSet>();
            foreach (var setKv in mappingExt.ExpressionSetMappings)
            {
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
                                if (sw.Source == null) continue;
                                if (!nameToIndex.TryGetValue(sw.Source, out int srcIndex))
                                {
                                    Debug.LogWarning($"[KHR_character] Mapping references unknown expression '{sw.Source}'; dropping.");
                                    continue;
                                }
                                contributions.Add(new MappingContribution { SourceIndex = srcIndex, Weight = sw.Weight });
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

        private static KHR_character_expression_mapping GetMappingExtension(GLTFRoot root)
        {
            if (root.Extensions == null) return null;
            if (!root.Extensions.TryGetValue(KhrCharacterExtensionNames.ExpressionMapping, out var ext)) return null;
            if (ext is KHR_character_expression_mapping typed) return typed;
            if (ext is DefaultExtension raw && raw.ExtensionData != null)
                return new KHR_character_expression_mapping_Factory().Deserialize(root, raw.ExtensionData) as KHR_character_expression_mapping;
            return null;
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

        // True when the track carries at least one driver and every driver -- across morph, joint, and texture
        // domains -- uses STEP interpolation. Used to present binary (on/off) expressions as a 0/1-snapping control.
        internal static bool AllStep(ExpressionTrack track)
        {
            int count = 0;
            if (track.MorphDrivers != null)
                foreach (var d in track.MorphDrivers) { count++; if (d.Sampler.Interp != Interp.Step) return false; }
            if (track.JointDrivers != null)
                foreach (var d in track.JointDrivers) { count++; if (d.Sampler.Interp != Interp.Step) return false; }
            if (track.TextureDrivers != null)
                foreach (var d in track.TextureDrivers) { count++; if (d.Sampler.Interp != Interp.Step) return false; }
            return count > 0;
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
