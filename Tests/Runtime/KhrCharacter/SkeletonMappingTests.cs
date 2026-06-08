using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Tests skeleton-mapping direction auto-detection (spec and inverted layouts both resolve to the same
    /// bones) and the humanoid build's required-bone guard.
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

        private static KHR_character_skeleton_mapping Mapping(Dictionary<string, string> rig)
            => new KHR_character_skeleton_mapping
            {
                SkeletalRigMappings = new Dictionary<string, Dictionary<string, string>> { { "rig", rig } }
            };

        [Test]
        public void BakeSkeleton_SpecDirection_Resolves()
        {
            var hips = NewGo("Hips_node");
            var head = NewGo("Head_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };
            var ext = Mapping(new Dictionary<string, string> { { "hips", "Hips_node" }, { "head", "Head_node" } });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreEqual(MappingDirection.TargetKeyToNodeValue, result.Direction);
            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.AreSame(head.transform, result.Bones["head"]);
        }

        [Test]
        public void BakeSkeleton_InvertedDirection_Resolves()
        {
            var hips = NewGo("Hips_node");
            var head = NewGo("Head_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };
            var ext = Mapping(new Dictionary<string, string> { { "Hips_node", "hips" }, { "Head_node", "head" } });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreEqual(MappingDirection.NodeKeyToTargetValue, result.Direction);
            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.AreSame(head.transform, result.Bones["head"]);
        }

        [Test]
        public void BakeSkeleton_InvertedWithConventionalNodeNames_PicksDirectionByResolvedBones()
        {
            // Inverted layout {nodeName: vocab} where the node names are conventional tokens that ALSO look like
            // vocabulary (Hips/Head). The vocab-count heuristic is a tie here, so the old code mis-detected the
            // direction and resolved nothing. Resolving both directions and keeping the one that maps more real
            // transforms picks the correct NodeKeyToTargetValue reading.
            var hips = NewGo("Hips");
            var head = NewGo("Head");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };
            var ext = Mapping(new Dictionary<string, string> { { "Hips", "hips" }, { "Head", "head" } });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreEqual(MappingDirection.NodeKeyToTargetValue, result.Direction);
            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.AreSame(head.transform, result.Bones["head"]);
        }

        [Test]
        public void BakeSkeleton_UnresolvedRequiredJoint_FlagsMissingRequiredAndInvalid()
        {
            // hips resolves; leftFoot (a REQUIRED humanoid bone) is mapped but its node is absent.
            var hips = NewGo("Hips_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips } };
            var ext = Mapping(new Dictionary<string, string>
            {
                { "hips", "Hips_node" },
                { "leftFoot", "LeftFoot_node" }, // no such node
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
            // hips + head resolve; jaw (an OPTIONAL humanoid bone) is mapped but its node is absent. The mapping
            // is still valid with no missing *required* bone — only a warning is recorded.
            var hips = NewGo("Hips_node");
            var head = NewGo("Head_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };
            var ext = Mapping(new Dictionary<string, string>
            {
                { "hips", "Hips_node" },
                { "head", "Head_node" },
                { "jaw", "Jaw_node" }, // no such node
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
        public void BakeSkeleton_EqualResolvedCounts_TieBreaksToTargetKeyByVocab()
        {
            // Both directions resolve exactly one bone, so resolved-count can't decide; the vocab-count tie-break
            // (bias toward the spec's target-key order) selects TargetKeyToNodeValue.
            var nodeA = NewGo("NodeA");
            var nodeB = NewGo("NodeB");
            var nodeMap = new Dictionary<int, GameObject> { { 0, nodeA }, { 1, nodeB } };
            // "hips" (vocab key) resolves under target-key; "NodeB" (node-name key) resolves under node-key.
            var ext = Mapping(new Dictionary<string, string> { { "hips", "NodeA" }, { "NodeB", "head" } });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Bones.Count);
            Assert.AreEqual(MappingDirection.TargetKeyToNodeValue, result.Direction);
            Assert.AreSame(nodeA.transform, result.Bones["hips"]);
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
