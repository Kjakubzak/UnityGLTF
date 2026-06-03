using System;
using System.Collections.Generic;
using GLTF.Schema;
using Unity.Mathematics;
using UnityEngine;
using UnityGLTF.Extensions;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Resolves <c>KHR_character_skeleton_mapping</c> to concrete bone transforms and bakes the
    /// <c>KHR_character_reference_pose</c> animation into a retarget pose. The mapping JSON is
    /// <c>rigName -&gt; { jointA -&gt; jointB }</c> with an ambiguous direction: one side is a known vocabulary
    /// joint (hips/head/leftUpperArm/...), the other is a model node name. We auto-detect which side is the
    /// vocabulary by counting matches against a known token set, so both the spec layout and the inverted
    /// (0b5vr-style) layout resolve. Also maps vocabulary joints to Unity humanoid bone names.
    /// </summary>
    internal static class KhrCharacterSkeletonBaker
    {
        // Vocabulary joint (case-insensitive) -> Unity HumanTrait.BoneName (the spaced form).
        private static readonly Dictionary<string, string> VocabToHumanName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "hips", "Hips" }, { "spine", "Spine" }, { "chest", "Chest" }, { "upperChest", "Upper Chest" },
            { "neck", "Neck" }, { "head", "Head" }, { "jaw", "Jaw" },
            { "leftEye", "Left Eye" }, { "rightEye", "Right Eye" },
            { "leftShoulder", "Left Shoulder" }, { "rightShoulder", "Right Shoulder" },
            { "leftUpperArm", "Left Upper Arm" }, { "leftLowerArm", "Left Lower Arm" }, { "leftHand", "Left Hand" },
            { "rightUpperArm", "Right Upper Arm" }, { "rightLowerArm", "Right Lower Arm" }, { "rightHand", "Right Hand" },
            { "leftUpperLeg", "Left Upper Leg" }, { "leftLowerLeg", "Left Lower Leg" }, { "leftFoot", "Left Foot" }, { "leftToes", "Left Toes" },
            { "rightUpperLeg", "Right Upper Leg" }, { "rightLowerLeg", "Right Lower Leg" }, { "rightFoot", "Right Foot" }, { "rightToes", "Right Toes" },
        };

        public static bool TryGetHumanName(string vocabularyJoint, out string humanName)
            => VocabToHumanName.TryGetValue(vocabularyJoint ?? string.Empty, out humanName);

        public static SkeletonMappingResult BakeSkeleton(GLTFRoot root, IReadOnlyDictionary<int, GameObject> nodeIndexToGo, KHR_character_skeleton_mapping ext)
        {
            if (root == null || ext?.SkeletalRigMappings == null || ext.SkeletalRigMappings.Count == 0) return null;

            var nameToTransform = BuildNameToTransform(root, nodeIndexToGo);

            // Choose the rig that resolves the most bones.
            SkeletonMappingResult best = null;
            foreach (var rig in ext.SkeletalRigMappings)
            {
                var result = ResolveRig(rig.Key, rig.Value, nameToTransform);
                if (result != null && (best == null || result.Bones.Count > best.Bones.Count))
                    best = result;
            }
            return best;
        }

        /// <summary>
        /// Samples frame 0 of the animation tagged with <c>KHR_character_reference_pose</c> into a
        /// <see cref="ReferencePose"/> (Unity-space local TRS per targeted node). Returns null when absent.
        /// </summary>
        public static ReferencePose BakeReferencePose(GLTFRoot root, GLTFSceneImporter importer, IReadOnlyDictionary<int, GameObject> nodeIndexToGo)
        {
            if (root?.Animations == null || importer == null || nodeIndexToGo == null) return null;

            GLTFAnimation refAnim = null;
            string poseType = "TPose";
            foreach (var anim in root.Animations)
            {
                if (anim?.Extensions == null || !anim.Extensions.TryGetValue(KHR_character_reference_pose.EXTENSION_NAME, out var ext)) continue;
                refAnim = anim;
                poseType = ExtractPoseType(root, ext) ?? "TPose";
                break;
            }
            if (refAnim?.Channels == null) return null;

            var perNode = new Dictionary<int, NodePose>();
            foreach (var channel in refAnim.Channels)
            {
                try
                {
                    if (channel?.Target?.Node == null) continue;
                    var path = channel.Target.Path;
                    if (path != "translation" && path != "rotation" && path != "scale") continue;

                    int samplerIndex = channel.Sampler?.Id ?? -1;
                    if (samplerIndex < 0 || samplerIndex >= refAnim.Samplers.Count) continue;
                    var sampler = refAnim.Samplers[samplerIndex];
                    int valueIndex = sampler.Interpolation == InterpolationType.CUBICSPLINE ? 1 : 0; // [inTangent, value, outTangent]
                    var output = GetAccessor(root, sampler.Output);
                    if (output == null) continue;

                    int nodeIndex = channel.Target.Node.Id;
                    perNode.TryGetValue(nodeIndex, out var pose);
                    if (path == "rotation")
                    {
                        var q = DecodeVec4(importer, output, valueIndex);
                        if (q.HasValue) pose.Rotation = q.Value.ToUnityQuaternionConvert();
                    }
                    else
                    {
                        var v = DecodeVec3(importer, output, valueIndex);
                        if (v.HasValue)
                        {
                            if (path == "translation") pose.Translation = v.Value.ToUnityVector3Convert();
                            else pose.Scale = v.Value.ToUnityVector3Raw();
                        }
                    }
                    perNode[nodeIndex] = pose;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[KHR_character] Skipping a reference-pose channel: {e.Message}");
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

        private static SkeletonMappingResult ResolveRig(string rigName, Dictionary<string, string> mapping, Dictionary<string, Transform> nameToTransform)
        {
            if (mapping == null || mapping.Count == 0) return null;

            // Direction auto-detect: whichever side has more known-vocabulary joints is the vocabulary side.
            int keyVocab = 0, valueVocab = 0;
            foreach (var kv in mapping)
            {
                if (kv.Key != null && VocabToHumanName.ContainsKey(kv.Key)) keyVocab++;
                if (kv.Value != null && VocabToHumanName.ContainsKey(kv.Value)) valueVocab++;
            }

            var direction = keyVocab >= valueVocab ? MappingDirection.TargetKeyToNodeValue : MappingDirection.NodeKeyToTargetValue;
            bool keyIsVocab = direction == MappingDirection.TargetKeyToNodeValue;

            var bones = new Dictionary<string, Transform>();
            var report = new ValidationReport();
            foreach (var kv in mapping)
            {
                string vocab = keyIsVocab ? kv.Key : kv.Value;
                string nodeName = keyIsVocab ? kv.Value : kv.Key;
                if (string.IsNullOrEmpty(vocab) || string.IsNullOrEmpty(nodeName)) continue;

                if (nameToTransform.TryGetValue(nodeName, out var t) && t != null)
                    bones[vocab] = t;
                else
                    report.Warnings.Add($"[KHR_character] skeleton joint '{vocab}' -> node '{nodeName}' was not found.");
            }

            if (bones.Count == 0) return null;
            return new SkeletonMappingResult { Bones = bones, SelectedRig = rigName, Direction = direction, Report = report };
        }

        private static Dictionary<string, Transform> BuildNameToTransform(GLTFRoot root, IReadOnlyDictionary<int, GameObject> nodeIndexToGo)
        {
            var map = new Dictionary<string, Transform>();
            if (nodeIndexToGo == null) return map;
            foreach (var kv in nodeIndexToGo)
            {
                if (kv.Value == null) continue;
                var t = kv.Value.transform;
                if (!string.IsNullOrEmpty(kv.Value.name)) map[kv.Value.name] = t;
                // The glTF node name is authoritative, so let it win over the (possibly de-duplicated) GameObject name.
                if (root.Nodes != null && kv.Key >= 0 && kv.Key < root.Nodes.Count)
                {
                    var nodeName = root.Nodes[kv.Key].Name;
                    if (!string.IsNullOrEmpty(nodeName)) map[nodeName] = t;
                }
            }
            return map;
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
