using System;
using System.Collections.Generic;
using GLTF.Schema;
using Unity.Mathematics;
using UnityEngine;
using UnityGLTF.Extensions;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Resolves <c>KHR_character_skeleton_mapping</c> associations to concrete transforms and bakes every
    /// <c>KHR_character_reference_pose</c> animation into a static local-space pose. Mapping-set identifiers and
    /// role identifiers remain generic; recognized role names are interpreted only by the optional Unity Humanoid
    /// adapter.
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
                var result = ResolveMappingSet(mapping.Key, mapping.Value, nodeIndexToGo);
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

        /// <summary>
        /// Reads the single sample of the first animation tagged with <c>KHR_character_reference_pose</c> into a
        /// <see cref="ReferencePose"/> (Unity-space local TRS per targeted node). Returns null when absent.
        /// </summary>
        public static ReferencePose BakeReferencePose(GLTFRoot root, GLTFSceneImporter importer, IReadOnlyDictionary<int, GameObject> nodeIndexToGo)
        {
            var poses = BakeReferencePoses(root, importer, nodeIndexToGo);
            return poses.Length > 0 ? poses[0] : null;
        }

        public static ReferencePose[] BakeReferencePoses(
            GLTFRoot root, GLTFSceneImporter importer, IReadOnlyDictionary<int, GameObject> nodeIndexToGo)
        {
            var poses = new List<ReferencePose>();
            if (root?.Animations == null || importer == null || nodeIndexToGo == null) return poses.ToArray();
            for (int animationIndex = 0; animationIndex < root.Animations.Count; animationIndex++)
            {
                var animation = root.Animations[animationIndex];
                if (animation?.Extensions == null
                    || !animation.Extensions.TryGetValue(KHR_character_reference_pose.EXTENSION_NAME, out var extension))
                    continue;
                var pose = BakeReferencePoseAnimation(
                    root, importer, nodeIndexToGo, animation, animationIndex, ExtractPoseType(root, extension) ?? "TPose");
                if (pose != null) poses.Add(pose);
            }
            return poses.ToArray();
        }

        private static ReferencePose BakeReferencePoseAnimation(
            GLTFRoot root,
            GLTFSceneImporter importer,
            IReadOnlyDictionary<int, GameObject> nodeIndexToGo,
            GLTFAnimation refAnim,
            int animationIndex,
            string poseType)
        {
            if (refAnim?.Channels == null || refAnim.Channels.Count == 0) return null;

            var perNode = new Dictionary<int, NodePose>();
            foreach (var channel in refAnim.Channels)
            {
                try
                {
                    if (channel?.Target?.Node == null) return null;
                    var path = channel.Target.Path;
                    if (path != "translation" && path != "rotation" && path != "scale") return null;

                    int samplerIndex = channel.Sampler?.Id ?? -1;
                    if (samplerIndex < 0 || samplerIndex >= refAnim.Samplers.Count) return null;
                    var sampler = refAnim.Samplers[samplerIndex];
                    var input = GetAccessor(root, sampler.Input);
                    if (input == null || input.Count != 1 || sampler.Interpolation == InterpolationType.CUBICSPLINE)
                        return null;
                    var output = GetAccessor(root, sampler.Output);
                    if (output == null || output.Count != 1) return null;

                    int nodeIndex = channel.Target.Node.Id;
                    perNode.TryGetValue(nodeIndex, out var pose);
                    if (path == "rotation")
                    {
                        var q = DecodeVec4(importer, output, 0);
                        if (!q.HasValue) return null;
                        pose.Rotation = q.Value.ToUnityQuaternionConvert();
                    }
                    else
                    {
                        var v = DecodeVec3(importer, output, 0);
                        if (!v.HasValue) return null;
                        if (path == "translation") pose.Translation = v.Value.ToUnityVector3Convert();
                        else pose.Scale = v.Value.ToUnityVector3Raw();
                    }
                    perNode[nodeIndex] = pose;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[KHR_character] Invalid reference-pose animation {animationIndex}: {e.Message}");
                    return null;
                }
            }

            if (perNode.Count == 0) return null;

            var bones = new List<Transform>();
            var positions = new List<Vector3>();
            var rotations = new List<Quaternion>();
            var scales = new List<Vector3>();
            foreach (var kv in perNode)
            {
                if (!nodeIndexToGo.TryGetValue(kv.Key, out var go) || go == null) continue;
                var t = go.transform;
                bones.Add(t);
                positions.Add(kv.Value.Translation ?? t.localPosition);
                rotations.Add(kv.Value.Rotation ?? t.localRotation);
                scales.Add(kv.Value.Scale ?? t.localScale);
            }
            if (bones.Count == 0) return null;

            return new ReferencePose
            {
                AnimationIndex = animationIndex,
                PoseType = poseType,
                Bones = bones.ToArray(),
                LocalPositions = positions.ToArray(),
                LocalRotations = rotations.ToArray(),
                LocalScales = scales.ToArray(),
            };
        }

        private struct NodePose
        {
            public Vector3? Translation;
            public Quaternion? Rotation;
            public Vector3? Scale;
        }

        // The mapping is absolute vocabulary URI -> { role identifier -> association }. Each association is
        // unambiguous: its node is a glTF index into the document's global nodes[] array.
        // Resolve each via a direct node-index -> GameObject lookup (the map the importer builds in
        // OnAfterImportNode), so there is no name coupling and no direction to detect.
        private static SkeletonMappingSetResult ResolveMappingSet(
            string identifier,
            Dictionary<string, KHR_character_skeleton_mapping.JointAssociation> mapping,
            IReadOnlyDictionary<int, GameObject> nodeIndexToGo)
        {
            if (mapping == null || mapping.Count == 0) return null;

            var bones = new Dictionary<string, Transform>();
            var report = new ValidationReport();
            foreach (var kv in mapping)
            {
                string vocab = kv.Key;
                var association = kv.Value;
                if (association == null) continue;
                int nodeIndex = association.Node;
                if (string.IsNullOrEmpty(vocab)) continue;

                if (nodeIndex >= 0 && nodeIndexToGo.TryGetValue(nodeIndex, out var go) && go != null)
                {
                    bones[vocab] = go.transform;
                    if (association.Name != null && association.Name != go.name)
                        report.Warnings.Add($"[KHR_character] skeleton joint '{vocab}' name '{association.Name}' does not match node {nodeIndex} name '{go.name}'.");
                }
                else
                {
                    report.Warnings.Add($"[KHR_character] skeleton joint '{vocab}' -> node index {nodeIndex} was not found.");
                    report.IsValid = false;
                }
            }

            return new SkeletonMappingSetResult
            {
                Associations = bones,
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

        private static float3? DecodeVec3(GLTFSceneImporter importer, Accessor accessor, int index)
        {
            if (accessor?.BufferView == null) return null;
            var data = importer.GetBufferViewData(accessor.BufferView.Value);
            var numeric = new NumericArray();
            var arr = accessor.AsFloat3Array(ref numeric, data, 0);
            return (arr != null && index >= 0 && index < arr.Length) ? arr[index] : (float3?)null;
        }

        private static float4? DecodeVec4(GLTFSceneImporter importer, Accessor accessor, int index)
        {
            if (accessor?.BufferView == null) return null;
            var data = importer.GetBufferViewData(accessor.BufferView.Value);
            var numeric = new NumericArray();
            var arr = accessor.AsFloat4Array(ref numeric, data, 0);
            return (arr != null && index >= 0 && index < arr.Length) ? arr[index] : (float4?)null;
        }
    }
}
