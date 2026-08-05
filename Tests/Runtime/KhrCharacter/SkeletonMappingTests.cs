using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Tests generic skeleton-association resolution by glTF node index, preservation of multiple mapping sets,
    /// and the separate Unity Humanoid adapter.
    /// </summary>
    public class SkeletonMappingTests
    {
        private const string Vocab = "https://example.com/skeleton/v1";
        private const string SparseVocab = "https://example.com/skeleton/sparse/v1";
        private const string FullVocab = "https://example.com/skeleton/full/v1";
        private static readonly string[] RecognizedUnityHumanoidRoles =
        {
            "hips", "spine", "chest", "upperChest", "neck", "head", "jaw", "leftEye", "rightEye",
            "leftShoulder", "rightShoulder", "leftUpperArm", "leftLowerArm", "leftHand", "rightUpperArm",
            "rightLowerArm", "rightHand", "leftUpperLeg", "leftLowerLeg", "leftFoot", "leftToes",
            "rightUpperLeg", "rightLowerLeg", "rightFoot", "rightToes",
        };

        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        private GameObject NewGo(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        private static KHR_character_skeleton_mapping.JointAssociation Joint(int node, string name = null)
            => new KHR_character_skeleton_mapping.JointAssociation { Node = node, Name = name };

        private static KHR_character_skeleton_mapping Mapping(Dictionary<string, KHR_character_skeleton_mapping.JointAssociation> rig)
            => new KHR_character_skeleton_mapping
            {
                SkeletalRigMappings = new Dictionary<string, Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>> { { Vocab, rig } }
            };

        private SkeletonMappingResult BakeRecognizedRoles(string omittedRole = null, string unresolvedRole = null)
        {
            var nodeMap = new Dictionary<int, GameObject>();
            var rig = new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>();
            int nodeIndex = 0;
            foreach (var role in RecognizedUnityHumanoidRoles)
            {
                if (role == omittedRole) continue;
                if (role == unresolvedRole)
                {
                    rig[role] = Joint(9999);
                    continue;
                }

                var bone = NewGo(role);
                nodeMap[nodeIndex] = bone;
                rig[role] = Joint(nodeIndex);
                nodeIndex++;
            }
            return KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, Mapping(rig));
        }

        [Test]
        public void BakeSkeleton_ResolvesByNodeIndex()
        {
            var hips = NewGo("Hips_node");
            var head = NewGo("Head_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };
            var ext = Mapping(new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>
            {
                { "hips", Joint(0, "Hips_node") },
                { "head", Joint(1, "Head_node") }
            });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.AreSame(head.transform, result.Bones["head"]);
            Assert.IsTrue(result.Report.IsValid, "omitting roles is valid generic skeleton-mapping data");
            Assert.Contains("leftFoot", result.Report.MissingRequiredBones,
                "the separately assessed Unity Humanoid adapter is incomplete");
            Assert.AreEqual(Vocab, result.MappingSets[0].Identifier);
        }

        [Test]
        public void BakeSkeleton_AllRecognizedRequiredRolesResolve_UnityAdapterIsHealthy()
        {
            var result = BakeRecognizedRoles();

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Report.IsValid);
            Assert.IsEmpty(result.Report.MissingRequiredBones);
        }

        [Test]
        public void BakeSkeleton_OmittedRequiredRole_IsValidMappingButDegradedUnityAdapter()
        {
            var result = BakeRecognizedRoles(omittedRole: "leftFoot");

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Report.IsValid,
                "KHR_character_skeleton_mapping does not prescribe required anatomy");
            CollectionAssert.AreEqual(new[] { "leftFoot" }, result.Report.MissingRequiredBones);
            StringAssert.Contains("does not make the skeleton mapping invalid", result.Report.Warnings[0]);
        }

        [Test]
        public void BakeSkeleton_OmittedOptionalRole_DoesNotDegradeUnityAdapter()
        {
            var result = BakeRecognizedRoles(omittedRole: "jaw");

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Report.IsValid);
            Assert.IsEmpty(result.Report.MissingRequiredBones, "jaw is optional to Unity Humanoid");
        }

        [Test]
        public void BakeSkeleton_MultipleSets_PreservesAllAndSelectsLargestForUnityAdapter()
        {
            var hips = NewGo("Hips_node");
            var head = NewGo("Head_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };

            var ext = new KHR_character_skeleton_mapping
            {
                SkeletalRigMappings = new Dictionary<string, Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>>
                {
                    { SparseVocab, new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation> { { "hips", Joint(0) } } },
                    { FullVocab, new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation> { { "hips", Joint(0) }, { "head", Joint(1) } } },
                }
            };

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreEqual(2, result.MappingSets.Length, "generic support preserves every mapping set");
            Assert.AreEqual(1, result.MappingSets[0].Associations.Count);
            Assert.AreEqual(2, result.MappingSets[1].Associations.Count);
            Assert.AreEqual(FullVocab, result.SelectedRig, "largest-set selection is only the Unity adapter policy");
            Assert.AreEqual(2, result.Bones.Count);
            Assert.IsEmpty(result.MappingSets[0].Report.MissingRequiredBones,
                "adapter health is assessed only for the selected mapping set");
            Assert.IsNotEmpty(result.MappingSets[1].Report.MissingRequiredBones);
        }

        [Test]
        public void BakeSkeleton_UnresolvedRequiredRole_SeparatesValidityFromAdapterHealth()
        {
            var hips = NewGo("Hips_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips } };
            var ext = Mapping(new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>
            {
                { "hips", Joint(0) },
                { "leftFoot", Joint(99) }, // no such node index
            });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.IsFalse(result.Bones.ContainsKey("leftFoot"));
            Assert.IsFalse(result.Report.IsValid, "an association whose node cannot resolve is invalid");
            Assert.Contains("leftFoot", result.Report.MissingRequiredBones,
                "the optional Unity adapter independently records its required recognized role");
        }

        [Test]
        public void BakeSkeleton_UnresolvedRole_DoesNotApplyUnityHumanoidOptionality()
        {
            var result = BakeRecognizedRoles(unresolvedRole: "jaw");

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Report.MissingRequiredBones.Count);
            Assert.IsFalse(result.Report.IsValid,
                "a dangling association is invalid even when Unity Humanoid treats that role as optional");
            Assert.Greater(result.Report.Warnings.Count, 0);
        }

        [Test]
        public void BakeSkeleton_NameMismatchWarnsButResolvesByIndex()
        {
            var hips = NewGo("Hips_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips } };
            var ext = Mapping(new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>
            {
                { "hips", Joint(0, "WrongName") },
            });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.IsNotEmpty(result.Report.Warnings);
        }

        [Test]
        public void BuildHumanoidAvatar_MissingRequiredBones_ReturnsNullAndFallsBack()
        {
            var root = NewGo("char");
            var hips = NewGo("hips");
            hips.transform.SetParent(root.transform, false);
            var head = NewGo("head");
            head.transform.SetParent(root.transform, false);

            var skel = root.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", hips.transform }, { "head", head.transform } },
                SelectedRig = "rig",
            });

            var avatar = skel.BuildHumanoidAvatar();
            if (avatar != null) _created.Add(avatar);

            Assert.IsNull(avatar);
            Assert.IsFalse(skel.HumanoidAvailable);
        }

        [Test]
        public void HasDuplicateBoundName_DetectsCollision()
        {
            var a = NewGo("Bone");
            var b = NewGo("Bone"); // same name -> ambiguous binding
            var c = NewGo("Other");
            var transforms = new[] { a.transform, b.transform, c.transform };
            var human = new List<HumanBone> { new HumanBone { boneName = "Bone", humanName = "Hips" } };

            Assert.IsTrue(SkeletonMap.HasDuplicateBoundName(transforms, human, out var dup));
            Assert.AreEqual("Bone", dup);
        }

        [Test]
        public void HasDuplicateBoundName_UniqueNames_False()
        {
            var a = NewGo("Hips");
            var b = NewGo("Head");
            var transforms = new[] { a.transform, b.transform };
            var human = new List<HumanBone>
            {
                new HumanBone { boneName = "Hips", humanName = "Hips" },
                new HumanBone { boneName = "Head", humanName = "Head" },
            };

            Assert.IsFalse(SkeletonMap.HasDuplicateBoundName(transforms, human, out _));
        }

        [Test]
        public void ApplyReferencePose_MovesBonesToBakedPose()
        {
            var bone = NewGo("bone");
            var target = new Vector3(0f, 2f, 0f);
            var rot = Quaternion.Euler(0f, 90f, 0f);

            var skel = NewGo("char").AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform>(),
                ReferencePose = new ReferencePose
                {
                    PoseType = "TPose",
                    Bones = new[] { bone.transform },
                    LocalPositions = new[] { target },
                    LocalRotations = new[] { rot },
                    LocalScales = new[] { Vector3.one },
                },
            });

            skel.ApplyReferencePose();

            Assert.Less(Vector3.Distance(target, bone.transform.localPosition), 1e-5f);
            Assert.Less(Quaternion.Angle(rot, bone.transform.localRotation), 0.01f);
        }

        [Test]
        public void SkeletonMap_ExposesMappingSetsAndSelectsReferencePoseByAnimationIndex()
        {
            var hips = NewGo("hips").transform;
            var head = NewGo("head").transform;
            var target = new Vector3(0f, 3f, 0f);
            var skel = NewGo("char").AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                MappingSets = new[]
                {
                    new SkeletonMappingSetResult
                    {
                        Identifier = Vocab,
                        Associations = new Dictionary<string, Transform> { { "hips", hips } },
                    },
                    new SkeletonMappingSetResult
                    {
                        Identifier = FullVocab,
                        Associations = new Dictionary<string, Transform> { { "head", head } },
                    },
                },
                ReferencePoses = new[]
                {
                    new ReferencePose { AnimationIndex = 3, PoseType = "TPose", Bones = new[] { hips }, LocalPositions = new[] { Vector3.one } },
                    new ReferencePose { AnimationIndex = 7, PoseType = "TPose", Bones = new[] { hips }, LocalPositions = new[] { target } },
                },
                Bones = new Dictionary<string, Transform> { { "hips", hips } },
                SelectedRig = Vocab,
            });

            Assert.AreEqual(2, skel.RigVocabularies.Count);
            Assert.IsTrue(skel.TryGetAssociation(FullVocab, "head", out var resolved));
            Assert.AreSame(head, resolved);
            Assert.AreEqual(2, skel.ReferencePoses.Count);
            Assert.IsTrue(skel.ApplyReferencePose(7));
            Assert.AreEqual(target, hips.localPosition);
            Assert.IsFalse(skel.ApplyReferencePose(99));
        }
    }
}
