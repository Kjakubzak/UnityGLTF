using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Tests skeleton-mapping resolution by glTF node index (including missing-index handling and multi-rig
    /// selection) and the humanoid build's required-bone guard.
    /// </summary>
    public class SkeletonMappingTests
    {
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

        private static KHR_character_skeleton_mapping Mapping(Dictionary<string, int> rig)
            => new KHR_character_skeleton_mapping
            {
                SkeletalRigMappings = new Dictionary<string, Dictionary<string, int>> { { "rig", rig } }
            };

        [Test]
        public void BakeSkeleton_ResolvesByNodeIndex()
        {
            var hips = NewGo("Hips_node");
            var head = NewGo("Head_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };
            var ext = Mapping(new Dictionary<string, int> { { "hips", 0 }, { "head", 1 } });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.AreSame(head.transform, result.Bones["head"]);
            Assert.IsTrue(result.Report.IsValid);
        }

        [Test]
        public void BakeSkeleton_MultipleRigs_PicksRigResolvingMostBones()
        {
            var hips = NewGo("Hips_node");
            var head = NewGo("Head_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };

            var ext = new KHR_character_skeleton_mapping
            {
                SkeletalRigMappings = new Dictionary<string, Dictionary<string, int>>
                {
                    { "sparse", new Dictionary<string, int> { { "hips", 0 } } },
                    { "full", new Dictionary<string, int> { { "hips", 0 }, { "head", 1 } } },
                }
            };

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreEqual("full", result.SelectedRig);
            Assert.AreEqual(2, result.Bones.Count);
        }

        [Test]
        public void BakeSkeleton_UnresolvedRequiredJoint_FlagsMissingRequiredAndInvalid()
        {
            // hips resolves; leftFoot (a REQUIRED humanoid bone) maps to an index with no node.
            var hips = NewGo("Hips_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips } };
            var ext = Mapping(new Dictionary<string, int>
            {
                { "hips", 0 },
                { "leftFoot", 99 }, // no such node index
            });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.IsFalse(result.Bones.ContainsKey("leftFoot"));
            CollectionAssert.Contains(result.Report.MissingRequiredBones, "leftFoot");
            Assert.IsFalse(result.Report.IsValid, "a missing required bone invalidates the mapping");
        }

        [Test]
        public void BakeSkeleton_UnresolvedOptionalJoint_StaysValid()
        {
            // hips + head resolve; jaw (an OPTIONAL humanoid bone) maps to an absent index. The mapping is still
            // valid with no missing *required* bone — only a warning is recorded.
            var hips = NewGo("Hips_node");
            var head = NewGo("Head_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };
            var ext = Mapping(new Dictionary<string, int>
            {
                { "hips", 0 },
                { "head", 1 },
                { "jaw", 99 }, // no such node index
            });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.AreSame(head.transform, result.Bones["head"]);
            Assert.AreEqual(0, result.Report.MissingRequiredBones.Count, "jaw is optional, not a required bone");
            Assert.IsTrue(result.Report.IsValid, "an unresolved optional joint must not invalidate the mapping");
            Assert.Greater(result.Report.Warnings.Count, 0, "the unresolved jaw still records a warning");
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

            Assert.IsNull(avatar);                 // 13 of 15 required bones missing
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
    }
}
