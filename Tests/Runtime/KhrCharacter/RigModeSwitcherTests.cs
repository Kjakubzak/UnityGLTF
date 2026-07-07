using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// PlayMode tests for the runtime rig mode switcher (SkeletonMap.SwitchRigMode).
    /// Verifies Generic ↔ Humanoid switching at runtime on an already-imported character.
    /// </summary>
    public class RigModeSwitcherTests
    {
        private GameObject _character;
        private SkeletonMap _skeleton;

        [SetUp]
        public void SetUp()
        {
            // A minimal but VALID humanoid T-pose. The bone-map keys use the canonical vocabulary
            // (KhrCharacterSkeletonBaker.VocabToHumanBone: leftUpperLeg/leftLowerLeg/leftUpperArm/leftLowerArm,
            // ...) — NOT Mixamo names (leftUpLeg/leftLeg/leftArm/leftForeArm), which TryGetHumanName does not
            // recognize — and the bones carry non-degenerate offsets so AvatarBuilder.BuildHumanAvatar accepts
            // the rig. Mirrors the proven rig in SerializationRoundTripTests.BuildHumanoidRig.
            _character = new GameObject("TestCharacter");
            var bones = BuildHumanoidRig(_character.transform);

            _skeleton = _character.AddComponent<SkeletonMap>();
            _skeleton.Bind(new SkeletonMappingResult
            {
                Bones = bones,
                SelectedRig = "unityHumanoid",
            });
        }

        // Builds a minimal valid T-pose covering the 15 required humanoid bones (+ chest/neck) and returns the
        // canonical vocab-joint -> Transform map. Character faces +Z; the left side is +X.
        private static Dictionary<string, Transform> BuildHumanoidRig(Transform parent)
        {
            var map = new Dictionary<string, Transform>();

            Transform Bone(string name, string vocab, Transform p, Vector3 localPos)
            {
                var t = new GameObject(name).transform;
                t.SetParent(p, false);
                t.localPosition = localPos;
                map[vocab] = t;
                return t;
            }

            var hips = Bone("Hips", "hips", parent, new Vector3(0f, 1f, 0f));
            var spine = Bone("Spine", "spine", hips, new Vector3(0f, 0.2f, 0f));
            var chest = Bone("Chest", "chest", spine, new Vector3(0f, 0.2f, 0f));
            var neck = Bone("Neck", "neck", chest, new Vector3(0f, 0.2f, 0f));
            Bone("Head", "head", neck, new Vector3(0f, 0.1f, 0f));

            var lUpperArm = Bone("LeftUpperArm", "leftUpperArm", chest, new Vector3(0.15f, 0.15f, 0f));
            var lLowerArm = Bone("LeftLowerArm", "leftLowerArm", lUpperArm, new Vector3(0.25f, 0f, 0f));
            Bone("LeftHand", "leftHand", lLowerArm, new Vector3(0.25f, 0f, 0f));

            var rUpperArm = Bone("RightUpperArm", "rightUpperArm", chest, new Vector3(-0.15f, 0.15f, 0f));
            var rLowerArm = Bone("RightLowerArm", "rightLowerArm", rUpperArm, new Vector3(-0.25f, 0f, 0f));
            Bone("RightHand", "rightHand", rLowerArm, new Vector3(-0.25f, 0f, 0f));

            var lUpperLeg = Bone("LeftUpperLeg", "leftUpperLeg", hips, new Vector3(0.1f, -0.05f, 0f));
            var lLowerLeg = Bone("LeftLowerLeg", "leftLowerLeg", lUpperLeg, new Vector3(0f, -0.45f, 0f));
            Bone("LeftFoot", "leftFoot", lLowerLeg, new Vector3(0f, -0.45f, 0.1f));

            var rUpperLeg = Bone("RightUpperLeg", "rightUpperLeg", hips, new Vector3(-0.1f, -0.05f, 0f));
            var rLowerLeg = Bone("RightLowerLeg", "rightLowerLeg", rUpperLeg, new Vector3(0f, -0.45f, 0f));
            Bone("RightFoot", "rightFoot", rLowerLeg, new Vector3(0f, -0.45f, 0.1f));

            return map;
        }

        [TearDown]
        public void TearDown()
        {
            if (_character != null)
            {
                Object.DestroyImmediate(_character);
            }
        }

        [UnityTest]
        public IEnumerator Generic_To_Humanoid_Succeeds_WithValidBones()
        {
            // Arrange: Character starts in Generic mode (no humanoid avatar)
            Assert.IsFalse(_skeleton.HumanoidAvailable);
            // Use Unity's overloaded == (handles the fake-null sentinel); the null-conditional ?. does not,
            // so `GetComponent<Animator>()?.avatar` would throw MissingComponentException when no Animator exists.
            var preAnimator = _character.GetComponent<Animator>();
            Assert.IsTrue(preAnimator == null || preAnimator.avatar == null,
                "Character should start with no humanoid avatar");

            // Act: Switch to Humanoid mode
            bool success = _skeleton.SwitchRigMode(RigImportMode.Humanoid);

            // Assert: Humanoid avatar was built and assigned
            Assert.IsTrue(success, "SwitchRigMode(Humanoid) should succeed with valid bones");
            Assert.IsTrue(_skeleton.HumanoidAvailable, "HumanoidAvailable should be true after successful switch");
            var animator = _character.GetComponent<Animator>();
            Assert.IsNotNull(animator, "Animator should exist after switching to Humanoid");
            Assert.IsNotNull(animator.avatar, "Animator.avatar should be assigned");
            Assert.IsTrue(animator.avatar.isHuman, "Avatar should be a valid humanoid");

            yield return null;
        }

        [UnityTest]
        public IEnumerator Humanoid_To_Generic_RemovesAvatar_NoLeak()
        {
            // Arrange: Start in Humanoid mode
            bool humanoidSuccess = _skeleton.SwitchRigMode(RigImportMode.Humanoid);
            Assert.IsTrue(humanoidSuccess, "Setup: Switch to Humanoid should succeed");
            var animator = _character.GetComponent<Animator>();
            Assert.IsNotNull(animator?.avatar, "Setup: Avatar should be assigned");
            var avatarBefore = animator.avatar;

            // Act: Switch to Generic mode
            bool genericSuccess = _skeleton.SwitchRigMode(RigImportMode.Generic);

            // Assert: Avatar removed, no leak, HumanoidAvailable false
            Assert.IsTrue(genericSuccess, "SwitchRigMode(Generic) should always succeed");
            Assert.IsFalse(_skeleton.HumanoidAvailable, "HumanoidAvailable should be false after switching to Generic");
            Assert.IsNull(animator.avatar, "Animator.avatar should be null after switching to Generic");
            // The avatar should have been destroyed (can't directly test destruction, but LastBuiltAvatar should be null)
            Assert.IsNull(_skeleton.LastBuiltAvatar, "LastBuiltAvatar should be null after switching to Generic");

            yield return null;
        }

        [UnityTest]
        public IEnumerator Invalid_Humanoid_WithMissingBones_ReturnsFalse_StaysGeneric()
        {
            // Arrange: Create a skeleton with missing required bones
            var invalidCharacter = new GameObject("InvalidCharacter");
            var hipsOnly = new GameObject("Hips").transform;
            hipsOnly.SetParent(invalidCharacter.transform);
            var invalidSkeleton = invalidCharacter.AddComponent<SkeletonMap>();
            var invalidResult = new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform>
                {
                    { "hips", hipsOnly },
                    // Missing spine, head, limbs - not enough for humanoid
                },
                SelectedRig = "unityHumanoid",
            };
            invalidSkeleton.Bind(invalidResult);

            // Act: Try to switch to Humanoid with insufficient bones
            bool success = invalidSkeleton.SwitchRigMode(RigImportMode.Humanoid);

            // Assert: Switch fails, stays Generic, no avatar created
            Assert.IsFalse(success, "SwitchRigMode(Humanoid) should fail with missing bones");
            Assert.IsFalse(invalidSkeleton.HumanoidAvailable, "HumanoidAvailable should remain false");
            var animator = invalidCharacter.GetComponent<Animator>();
            Assert.IsTrue(animator == null || animator.avatar == null, "No avatar should be assigned");

            // Cleanup
            Object.DestroyImmediate(invalidCharacter);

            yield return null;
        }

        [UnityTest]
        public IEnumerator Switch_Humanoid_To_Humanoid_ReplacesAvatar_NoLeak()
        {
            // Arrange: Start in Humanoid mode
            bool firstSuccess = _skeleton.SwitchRigMode(RigImportMode.Humanoid);
            Assert.IsTrue(firstSuccess);
            var firstAvatar = _character.GetComponent<Animator>().avatar;
            Assert.IsNotNull(firstAvatar);

            // Act: Switch to Humanoid again (should replace the avatar)
            bool secondSuccess = _skeleton.SwitchRigMode(RigImportMode.Humanoid);

            // Assert: New avatar assigned, old one destroyed (no leak)
            Assert.IsTrue(secondSuccess, "Second SwitchRigMode(Humanoid) should succeed");
            var secondAvatar = _character.GetComponent<Animator>().avatar;
            Assert.IsNotNull(secondAvatar);
            Assert.AreNotSame(firstAvatar, secondAvatar, "Avatar should be replaced on second build");
            Assert.AreSame(secondAvatar, _skeleton.LastBuiltAvatar, "LastBuiltAvatar should reference the new avatar");

            yield return null;
        }
    }
}
