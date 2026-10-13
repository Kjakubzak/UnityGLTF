using System;
using System.Collections.Generic;
using GLTF.Schema;
using Unity.Mathematics;
using UnityEngine;
using UnityGLTF.Extensions;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Resolves <c>KHR_character_skeleton_mapping</c> associations to node indices and concrete transforms, and
    /// resolves every <c>KHR_character_reference_pose</c> animation into reference-pose values for all nodes.
    /// Mapping-set identifiers and role identifiers remain generic; recognized role names are interpreted only by the
    /// optional Unity Humanoid adapter.
    /// </summary>
    internal static class KhrCharacterSkeletonBaker
    {
        // Vocabulary joint (case-insensitive) -> Unity humanoid bone. The HumanBone.humanName required by
        // AvatarBuilder must be exactly HumanTrait.BoneName[(int)bone] (e.g. "LeftUpperArm", not "Left Upper
        // Arm"), so we map to the enum and resolve the canonical name at runtime — robust across Unity versions.
        private static readonly Dictionary<string, HumanBodyBones> VocabToHumanBone = new Dictionary<string, HumanBodyBones>(StringComparer.OrdinalIgnoreCase)
        {
            { "hips", HumanBodyBones.Hips }, { "spine", HumanBodyBones.Spine }, { "chest", HumanBodyBones.Chest }, { "upperChest", HumanBodyBones.UpperChest },
            { "neck", HumanBodyBones.Neck }, { "head", HumanBodyBones.Head }, { "jaw", HumanBodyBones.Jaw },
            { "leftEye", HumanBodyBones.LeftEye }, { "rightEye", HumanBodyBones.RightEye },
            { "leftShoulder", HumanBodyBones.LeftShoulder }, { "rightShoulder", HumanBodyBones.RightShoulder },
            { "leftUpperArm", HumanBodyBones.LeftUpperArm }, { "leftLowerArm", HumanBodyBones.LeftLowerArm }, { "leftHand", HumanBodyBones.LeftHand },
            { "rightUpperArm", HumanBodyBones.RightUpperArm }, { "rightLowerArm", HumanBodyBones.RightLowerArm }, { "rightHand", HumanBodyBones.RightHand },
            { "leftUpperLeg", HumanBodyBones.LeftUpperLeg }, { "leftLowerLeg", HumanBodyBones.LeftLowerLeg }, { "leftFoot", HumanBodyBones.LeftFoot }, { "leftToes", HumanBodyBones.LeftToes },
            { "rightUpperLeg", HumanBodyBones.RightUpperLeg }, { "rightLowerLeg", HumanBodyBones.RightLowerLeg }, { "rightFoot", HumanBodyBones.RightFoot }, { "rightToes", HumanBodyBones.RightToes },
        };

        // HumanTrait.BoneName allocates a fresh array on every access, and TryGetHumanName is called once per
        // mapped joint during a humanoid build. Cache the array (lazily — HumanTrait APIs can't be touched from a
        // static/field initializer) so a build doesn't allocate one array per bone.
        private static string[] _humanBoneNames;
        private static string[] HumanBoneNames => _humanBoneNames ?? (_humanBoneNames = HumanTrait.BoneName);

        public static bool TryGetHumanName(string vocabularyJoint, out string humanName)
        {
            humanName = null;
            if (!VocabToHumanBone.TryGetValue(vocabularyJoint ?? string.Empty, out var bone)) return false;
            // HumanTrait.BoneName is indexed by the HumanBodyBones value; this is the exact string AvatarBuilder
            // and HumanTrait.RequiredBone use, so it always matches the required-bone validation.
            humanName = HumanBoneNames[(int)bone];
            return true;
        }

        public static SkeletonMappingResult BakeSkeleton(GLTFRoot root, IReadOnlyDictionary<int, GameObject> nodeIndexToGo, KHR_character_skeleton_mapping ext)
        {
            if (root == null || nodeIndexToGo == null || ext?.SkeletalRigMappings == null || ext.SkeletalRigMappings.Count == 0) return null;

            var mappingSets = new List<SkeletonMappingSetResult>();
            SkeletonMappingSetResult adapterSelection = null;
            foreach (var mapping in ext.SkeletalRigMappings)
            {
                var result = ResolveMappingSet(root, mapping.Key, mapping.Value, nodeIndexToGo);
                if (result == null) continue;
                mappingSets.Add(result);
                if (adapterSelection == null
                    || result.Associations.Count > adapterSelection.Associations.Count)
                    adapterSelection = result;
            }
            if (mappingSets.Count == 0) return null;
            AssessUnityHumanoidAdapterHealth(adapterSelection);
            return new SkeletonMappingResult
            {
                MappingSets = mappingSets.ToArray(),
                Bones = adapterSelection?.Associations ?? new Dictionary<string, Transform>(),
                SelectedRig = adapterSelection?.Identifier,
                Report = adapterSelection?.Report ?? new ValidationReport(),
            };
        }

        // This is an optional host-adapter assessment, not KHR_character_skeleton_mapping validation. The
        // extension deliberately does not define a required anatomy. Once Unity selects a mapping set for its
        // Humanoid adapter, however, every role that this adapter recognizes as HumanTrait-required must resolve
        // before that adapter can be considered healthy.
        private static void AssessUnityHumanoidAdapterHealth(SkeletonMappingSetResult selection)
        {
            if (selection?.Associations == null || selection.Report == null) return;

            var presentBones = new HashSet<HumanBodyBones>();
            foreach (var role in selection.Associations.Keys)
                if (VocabToHumanBone.TryGetValue(role ?? string.Empty, out var bone))
                    presentBones.Add(bone);

            foreach (var role in VocabToHumanBone)
                if (HumanTrait.RequiredBone((int)role.Value) && !presentBones.Contains(role.Value))
                    selection.Report.MissingRequiredBones.Add(role.Key);

            if (selection.Report.MissingRequiredBones.Count > 0)
            {
                selection.Report.Warnings.Add(
                    $"[KHR_character] Unity Humanoid adapter health: selected mapping set '{selection.Identifier}' " +
                    $"has no resolved association for required recognized role(s): " +
                    $"{string.Join(", ", selection.Report.MissingRequiredBones)}. " +
                    "This host-adapter status does not make the skeleton mapping invalid.");
            }
        }

        /// <summary>Resolves the first valid animation tagged with <c>KHR_character_reference_pose</c>, or null.</summary>
        public static ReferencePose BakeReferencePose(GLTFRoot root, GLTFSceneImporter importer, IReadOnlyDictionary<int, GameObject> nodeIndexToGo)
        {
            foreach (var pose in BakeReferencePoses(root, importer, nodeIndexToGo))
                if (pose.IsValid) return pose;
            return null;
        }

        /// <summary>
        /// Resolves every animation tagged with <c>KHR_character_reference_pose</c>, in animation order. An invalid
        /// tagged animation is returned with <see cref="ReferencePose.IsValid"/> false and its errors, not dropped.
        /// </summary>
        public static ReferencePose[] BakeReferencePoses(
            GLTFRoot root, GLTFSceneImporter importer, IReadOnlyDictionary<int, GameObject> nodeIndexToGo)
        {
            if (importer == null) return Array.Empty<ReferencePose>();
            return BakeReferencePoses(root, ReadBufferView, nodeIndexToGo);

            byte[] ReadBufferView(BufferView bufferView)
            {
                var native = importer.GetBufferViewData(bufferView);
                return native.IsCreated ? native.ToArray() : Array.Empty<byte>();
            }
        }

        internal static ReferencePose[] BakeReferencePoses(
            GLTFRoot root, Func<BufferView, byte[]> readBufferView, IReadOnlyDictionary<int, GameObject> nodeIndexToGo)
        {
            var poses = new List<ReferencePose>();
            if (root?.Animations == null || readBufferView == null || nodeIndexToGo == null) return poses.ToArray();
            NodeLayout layout = null;
            for (int animationIndex = 0; animationIndex < root.Animations.Count; animationIndex++)
            {
                var animation = root.Animations[animationIndex];
                if (animation?.Extensions == null
                    || !animation.Extensions.TryGetValue(KHR_character_reference_pose.EXTENSION_NAME, out var extension))
                    continue;
                if (layout == null) layout = NodeLayout.Build(root, nodeIndexToGo);
                var pose = BakeReferencePoseAnimation(
                    root, readBufferView, layout, animation, animationIndex,
                    ExtractPoseType(root, extension) ?? ReferencePoseLabels.TPose);
                if (!pose.IsValid)
                    Debug.LogWarning(
                        $"[KHR_character] Reference-pose animation {animationIndex} is invalid: {string.Join(" ", pose.Errors)}");
                poses.Add(pose);
            }
            return poses.ToArray();
        }

        private static ReferencePose BakeReferencePoseAnimation(
            GLTFRoot root,
            Func<BufferView, byte[]> readBufferView,
            NodeLayout layout,
            GLTFAnimation animation,
            int animationIndex,
            string poseType)
        {
            var errors = new List<string>();
            if (poseType.Length == 0) errors.Add("poseType is empty; it must be a nonempty label when present.");
            var used = root.ExtensionsUsed;
            if (used == null || !used.Contains(KhrCharacterExtensionNames.ReferencePose))
                errors.Add($"extensionsUsed does not list {KhrCharacterExtensionNames.ReferencePose}.");
            if (used == null || !used.Contains(KhrCharacterExtensionNames.Character))
                errors.Add($"extensionsUsed does not list {KhrCharacterExtensionNames.Character}.");

            var targeted = new Dictionary<int, NodePose>();
            var targetOrder = new List<int>();
            var seenTargets = new HashSet<(int node, string path)>();
            if (animation.Channels == null || animation.Channels.Count == 0)
                errors.Add("The animation contains no channels.");
            else
                for (int channelIndex = 0; channelIndex < animation.Channels.Count; channelIndex++)
                    ReadChannel(root, readBufferView, layout, animation, channelIndex, targeted, targetOrder,
                        seenTargets, errors);

            var pose = new ReferencePose
            {
                AnimationIndex = animationIndex,
                PoseType = poseType,
                IsValid = errors.Count == 0,
                Errors = errors.ToArray(),
                Bones = Array.Empty<Transform>(),
                LocalPositions = Array.Empty<Vector3>(),
                LocalRotations = Array.Empty<Quaternion>(),
                LocalScales = Array.Empty<Vector3>(),
            };
            if (!pose.IsValid) return pose;

            int count = layout.Count;
            pose.ResolvedNodeIndices = new int[count];
            pose.ResolvedNodes = new Transform[count];
            pose.ResolvedLocalPositions = new Vector3[count];
            pose.ResolvedLocalRotations = new Quaternion[count];
            pose.ResolvedLocalScales = new Vector3[count];
            pose.ParentIndices = new int[count];
            pose.ResolvedNodeHasForwardAxisConversion = new bool[count];
            for (int nodeIndex = 0; nodeIndex < count; nodeIndex++)
            {
                var position = layout.Positions[nodeIndex];
                var rotation = layout.Rotations[nodeIndex];
                var scale = layout.Scales[nodeIndex];
                if (targeted.TryGetValue(nodeIndex, out var values))
                {
                    position = values.Translation ?? position;
                    rotation = values.Rotation ?? rotation;
                    scale = values.Scale ?? scale;
                }
                pose.ResolvedNodeIndices[nodeIndex] = nodeIndex;
                pose.ResolvedNodes[nodeIndex] = layout.Transforms[nodeIndex];
                pose.ResolvedLocalPositions[nodeIndex] = position;
                pose.ResolvedLocalRotations[nodeIndex] = rotation;
                pose.ResolvedLocalScales[nodeIndex] = scale;
                pose.ParentIndices[nodeIndex] = layout.Parents[nodeIndex];
                pose.ResolvedNodeHasForwardAxisConversion[nodeIndex] = layout.ForwardAxisConversion[nodeIndex];
            }

            var bones = new List<Transform>();
            var positions = new List<Vector3>();
            var rotations = new List<Quaternion>();
            var scales = new List<Vector3>();
            foreach (int nodeIndex in targetOrder)
            {
                var transform = layout.Transforms[nodeIndex];
                if (transform == null) continue;
                bones.Add(transform);
                positions.Add(pose.ResolvedLocalPositions[nodeIndex]);
                rotations.Add(pose.ResolvedLocalRotations[nodeIndex]);
                scales.Add(pose.ResolvedLocalScales[nodeIndex]);
            }
            pose.Bones = bones.ToArray();
            pose.LocalPositions = positions.ToArray();
            pose.LocalRotations = rotations.ToArray();
            pose.LocalScales = scales.ToArray();
            return pose;
        }

        private static void ReadChannel(
            GLTFRoot root,
            Func<BufferView, byte[]> readBufferView,
            NodeLayout layout,
            GLTFAnimation animation,
            int channelIndex,
            Dictionary<int, NodePose> targeted,
            List<int> targetOrder,
            HashSet<(int node, string path)> seenTargets,
            List<string> errors)
        {
            var channel = animation.Channels[channelIndex];
            var path = channel?.Target?.Path;
            if (channel?.Target?.Node == null || (path != "translation" && path != "rotation" && path != "scale"))
            {
                errors.Add($"Channel {channelIndex} does not target an ordinary node translation, rotation, or scale.");
                return;
            }
            int nodeIndex = channel.Target.Node.Id;
            if (nodeIndex < 0 || nodeIndex >= layout.Count)
            {
                errors.Add($"Channel {channelIndex} targets node {nodeIndex}, which does not exist.");
                return;
            }
            if (layout.HasMatrix[nodeIndex])
            {
                errors.Add($"Channel {channelIndex} targets node {nodeIndex}, which is defined by a matrix.");
                return;
            }
            if (!seenTargets.Add((nodeIndex, path)))
            {
                errors.Add($"Channel {channelIndex} targets node {nodeIndex} {path} a second time.");
                return;
            }

            int samplerIndex = channel.Sampler?.Id ?? -1;
            if (animation.Samplers == null || samplerIndex < 0 || samplerIndex >= animation.Samplers.Count)
            {
                errors.Add($"Channel {channelIndex} references invalid sampler {samplerIndex}.");
                return;
            }
            var sampler = animation.Samplers[samplerIndex];
            if (sampler.Interpolation != InterpolationType.LINEAR && sampler.Interpolation != InterpolationType.STEP)
            {
                errors.Add($"Sampler {samplerIndex} uses {sampler.Interpolation} interpolation; only LINEAR or STEP is allowed.");
                return;
            }
            var input = GetAccessor(root, sampler.Input);
            if (input == null || input.Count != 1 || input.Type != GLTFAccessorAttributeType.SCALAR
                || input.ComponentType != GLTFComponentType.Float || input.Normalized)
            {
                errors.Add($"Sampler {samplerIndex} input must be one FLOAT SCALAR key.");
                return;
            }
            var output = GetAccessor(root, sampler.Output);
            bool isRotation = path == "rotation";
            if (output == null || output.Count != 1 || !IsValidOutputFormat(output, isRotation))
            {
                errors.Add(
                    $"Sampler {samplerIndex} output must be one {(isRotation ? "VEC4 rotation" : "FLOAT VEC3")} value " +
                    $"for node {nodeIndex} {path}.");
                return;
            }

            GltfAccessorDecoder.DecodedAccessor decoded;
            try
            {
                decoded = GltfAccessorDecoder.DecodeAccessor(output, readBufferView);
            }
            catch (Exception exception)
            {
                errors.Add($"Sampler {samplerIndex} output could not be decoded: {exception.Message}");
                return;
            }
            var value = decoded.Values[0];
            foreach (float component in value)
            {
                if (float.IsNaN(component) || float.IsInfinity(component))
                {
                    errors.Add($"Sampler {samplerIndex} output for node {nodeIndex} {path} is not finite.");
                    return;
                }
            }
            if (isRotation
                && !IsUnitQuaternion(value, ExpressionInitialValueValidation.AccessorAllowance(decoded.Encoding)))
            {
                errors.Add($"Sampler {samplerIndex} rotation for node {nodeIndex} is not a unit quaternion.");
                return;
            }

            if (!targeted.TryGetValue(nodeIndex, out var pose)) targetOrder.Add(nodeIndex);
            if (isRotation)
            {
                pose.Rotation = new float4(value[0], value[1], value[2], value[3]).ToUnityQuaternionConvert().normalized;
            }
            else
            {
                var vector = new float3(value[0], value[1], value[2]);
                if (path == "translation") pose.Translation = vector.ToUnityVector3Convert();
                else pose.Scale = vector.ToUnityVector3Raw();
            }
            targeted[nodeIndex] = pose;
        }

        private static bool IsValidOutputFormat(Accessor output, bool isRotation)
        {
            if (!isRotation)
                return output.Type == GLTFAccessorAttributeType.VEC3
                       && output.ComponentType == GLTFComponentType.Float && !output.Normalized;
            if (output.Type != GLTFAccessorAttributeType.VEC4) return false;
            switch (output.ComponentType)
            {
                case GLTFComponentType.Float:
                    return !output.Normalized;
                case GLTFComponentType.Byte:
                case GLTFComponentType.UnsignedByte:
                case GLTFComponentType.Short:
                case GLTFComponentType.UnsignedShort:
                    return output.Normalized;
                default:
                    return false;
            }
        }

        // Same unit tolerance as the expression response baker, widened by the per-component quantization of a
        // normalized-integer output: |q|^2 can move by at most 4h + 4h^2 when each component moves by h.
        private static bool IsUnitQuaternion(float[] value, double allowance)
        {
            double lengthSquared = 0d;
            foreach (float component in value) lengthSquared += (double)component * component;
            return Math.Abs(lengthSquared - 1d) <= 1e-5d + 4d * allowance + 4d * allowance * allowance;
        }

        private struct NodePose
        {
            public Vector3? Translation;
            public Quaternion? Rotation;
            public Vector3? Scale;
        }

        // Authored local transform of every node (decomposed matrix, else TRS with core defaults), its glTF parent,
        // and its instantiated transform. Shared by every tagged animation of one asset.
        private sealed class NodeLayout
        {
            public int Count;
            public Vector3[] Positions;
            public Quaternion[] Rotations;
            public Vector3[] Scales;
            public int[] Parents;
            public bool[] HasMatrix;
            public Transform[] Transforms;
            public bool[] ForwardAxisConversion;

            public static NodeLayout Build(GLTFRoot root, IReadOnlyDictionary<int, GameObject> nodeIndexToGo)
            {
                var nodes = root.Nodes;
                int count = nodes?.Count ?? 0;
                var layout = new NodeLayout
                {
                    Count = count,
                    Positions = new Vector3[count],
                    Rotations = new Quaternion[count],
                    Scales = new Vector3[count],
                    Parents = new int[count],
                    HasMatrix = new bool[count],
                    Transforms = new Transform[count],
                    ForwardAxisConversion = new bool[count],
                };
                for (int i = 0; i < count; i++) layout.Parents[i] = -1;
                for (int i = 0; i < count; i++)
                {
                    var node = nodes[i];
                    if (node == null)
                    {
                        layout.Rotations[i] = Quaternion.identity;
                        layout.Scales[i] = Vector3.one;
                        continue;
                    }
                    node.GetUnityTRSProperties(out var position, out var rotation, out var scale);
                    layout.Positions[i] = position;
                    layout.Rotations[i] = rotation;
                    layout.Scales[i] = scale;
                    layout.HasMatrix[i] = node.HasMatrix;
                    if (node.Children != null)
                        foreach (var child in node.Children)
                            if (child != null && child.Id >= 0 && child.Id < count && layout.Parents[child.Id] < 0)
                                layout.Parents[child.Id] = i;
                    if (nodeIndexToGo.TryGetValue(i, out var go) && go != null)
                    {
                        layout.Transforms[i] = go.transform;
                        layout.ForwardAxisConversion[i] = KhrCharacterImportContext.HasForwardAxisConversion(node, go);
                    }
                }
                return layout;
            }
        }

        // The mapping is identifier -> { role identifier -> association }. Each association is
        // unambiguous: its node is a glTF index into the document's global nodes[] array.
        // Resolve each via a direct node-index -> GameObject lookup (the map the importer builds in
        // OnAfterImportNode), so there is no name coupling and no direction to detect.
        private static SkeletonMappingSetResult ResolveMappingSet(
            GLTFRoot root,
            string identifier,
            Dictionary<string, KHR_character_skeleton_mapping.JointAssociation> mapping,
            IReadOnlyDictionary<int, GameObject> nodeIndexToGo)
        {
            if (mapping == null || mapping.Count == 0) return null;

            var bones = new Dictionary<string, Transform>();
            var nodeIndices = new Dictionary<string, int>();
            var report = new ValidationReport();
            foreach (var kv in mapping)
            {
                string vocab = kv.Key;
                var association = kv.Value;
                if (association == null) continue;
                int nodeIndex = association.Node;
                if (string.IsNullOrEmpty(vocab)) continue;

                if (nodeIndex < 0 || (root?.Nodes != null && nodeIndex >= root.Nodes.Count))
                {
                    report.Warnings.Add($"[KHR_character] skeleton joint '{vocab}' -> node index {nodeIndex} does not exist.");
                    report.IsValid = false;
                    continue;
                }

                nodeIndices[vocab] = nodeIndex;
                if (nodeIndexToGo.TryGetValue(nodeIndex, out var go) && go != null)
                {
                    bones[vocab] = go.transform;
                    if (association.Name != null && association.Name != go.name)
                        report.Warnings.Add($"[KHR_character] skeleton joint '{vocab}' name '{association.Name}' does not match node {nodeIndex} name '{go.name}'.");
                }
                else
                {
                    report.Warnings.Add(
                        $"[KHR_character] skeleton joint '{vocab}' -> node index {nodeIndex} was not instantiated; " +
                        "the association keeps its node index.");
                }
            }

            return new SkeletonMappingSetResult
            {
                Associations = bones,
                NodeIndices = nodeIndices,
                Identifier = identifier,
                Report = report,
            };
        }

        private static string ExtractPoseType(GLTFRoot root, IExtension ext)
        {
            try
            {
                if (ext is KHR_character_reference_pose typed) return typed.PoseType;
                if (ext is DefaultExtension raw && raw.ExtensionData != null)
                    return (new KHR_character_reference_pose_Factory().Deserialize(root, raw.ExtensionData) as KHR_character_reference_pose)?.PoseType;
            }
            catch { /* malformed poseType metadata -> fall back to the default */ }
            return null;
        }

        private static Accessor GetAccessor(GLTFRoot root, AccessorId id)
        {
            if (id == null || root?.Accessors == null) return null;
            return (id.Id >= 0 && id.Id < root.Accessors.Count) ? root.Accessors[id.Id] : null;
        }
    }
}
