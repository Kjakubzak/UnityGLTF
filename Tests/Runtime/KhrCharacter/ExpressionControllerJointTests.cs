using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// End-to-end play-mode tests for the joint evaluation path: a hand-built joint expression is driven
    /// through ExpressionController.LateUpdate and the target Transform's local TRS is asserted. Translation composes
    /// as b + sum(s - n) over the authored value; rotation and scale are the single absolute sample.
    /// </summary>
    public class ExpressionControllerJointTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        private GameObject MakeRoot(out Transform joint)
        {
            var root = new GameObject("char");
            _created.Add(root);
            var bone = new GameObject("bone");
            bone.transform.SetParent(root.transform, false);
            joint = bone.transform;
            return root;
        }

        [UnityTest]
        public IEnumerator Rotation_DrivenAndRebased()
        {
            var root = MakeRoot(out var joint);
            var set = JointRotationSet("turn", joint,
                new[] { Quaternion.identity, Quaternion.Euler(0f, 90f, 0f) }, Quaternion.identity);
            var ec = root.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("turn", 1f);
            yield return null;
            Assert.Less(Quaternion.Angle(joint.localRotation, Quaternion.Euler(0f, 90f, 0f)), 0.1f);

            ec.SetWeight("turn", 0.5f);
            yield return null;
            Assert.Less(Quaternion.Angle(joint.localRotation, Quaternion.Euler(0f, 45f, 0f)), 0.5f);

            ec.SetWeight("turn", 0f);
            yield return null;
            Assert.Less(Quaternion.Angle(joint.localRotation, Quaternion.identity), 0.05f); // re-based to neutral
        }

        [UnityTest]
        public IEnumerator Translation_DrivenAndRebased()
        {
            var root = MakeRoot(out var joint);
            var set = JointTranslationSet("slide", joint,
                new[] { Vector3.zero, new Vector3(0f, 1f, 0f) }, Vector3.zero);
            var ec = root.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("slide", 1f);
            yield return null;
            Assert.AreEqual(1f, joint.localPosition.y, 1e-3f);

            ec.SetWeight("slide", 0.25f);
            yield return null;
            Assert.AreEqual(0.25f, joint.localPosition.y, 1e-3f);

            ec.SetWeight("slide", 0f);
            yield return null;
            Assert.AreEqual(0f, joint.localPosition.y, 1e-3f); // re-based to neutral
        }

        [UnityTest]
        public IEnumerator Rotation_ComposesOverBakedNeutral_NotLivePose()
        {
            var root = MakeRoot(out var joint);
            // Baked neutral is identity; the joint base captured by the baker is the node neutral, not reference_pose.
            var set = JointRotationSet("turn", joint,
                new[] { Quaternion.identity, Quaternion.Euler(0f, 90f, 0f) }, Quaternion.identity);
            var ec = root.AddComponent<ExpressionController>();
            ec.Initialize(set);

            // Simulate an Animator writing a pose earlier in the frame.
            joint.localRotation = Quaternion.Euler(0f, 0f, 45f);
            ec.SetWeight("turn", 1f);
            yield return null;

            // Rotation is single-writer: the record is the absolute sample, so the live 45 degree pose is replaced,
            // not stacked.
            Assert.Less(Quaternion.Angle(joint.localRotation, Quaternion.Euler(0f, 90f, 0f)), 0.1f);
            Assert.Greater(Quaternion.Angle(joint.localRotation, Quaternion.Euler(0f, 0f, 45f)), 1f);
        }

        [UnityTest]
        public IEnumerator Translation_AddsEachSampleMinusItsNeutralToTheAuthoredValue()
        {
            var root = MakeRoot(out var joint);
            var lift = new JointDriver
            {
                Target = joint,
                Channel = TrsChannel.Translation,
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                BaseVec = new Vector3(0f, 1f, 0f),
                DeltaVec = new[] { Vector3.zero, new Vector3(0f, 1f, 0f) },
            };
            // The neutral key of this curve is 0.5 above the authored value, so its contribution is s - n, not s - b.
            var raised = new JointDriver
            {
                Target = joint,
                Channel = TrsChannel.Translation,
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                BaseVec = new Vector3(0f, 1f, 0f),
                DeltaVec = new[] { new Vector3(0f, 0.5f, 0f), new Vector3(0f, 2.5f, 0f) },
            };
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "lift", Domains = ExpressionDomain.Joint, JointDrivers = new[] { lift } },
                    new ExpressionTrack { Name = "raised", Domains = ExpressionDomain.Joint, JointDrivers = new[] { raised } },
                }
            };
            var ec = root.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("lift", 1f);
            ec.SetWeight("raised", 1f);
            yield return null;
            Assert.AreEqual(1f + 1f + 2f, joint.localPosition.y, 1e-5f, "b + (2 - 1) + (3.5 - 1.5)");
        }

        [UnityTest]
        public IEnumerator Scale_IsTheAbsoluteSample()
        {
            var root = MakeRoot(out var joint);
            // The neutral key differs from the authored value; a single-writer property ignores both.
            var grow = new JointDriver
            {
                Target = joint,
                Channel = TrsChannel.Scale,
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                BaseVec = new Vector3(2f, 2f, 2f),
                DeltaVec = new[] { new Vector3(0.5f, 0.5f, 0.5f), Vector3.one },
            };
            var ec = root.AddComponent<ExpressionController>();
            ec.Initialize(WrapJoint("grow", grow));

            ec.SetWeight("grow", 1f);
            yield return null;
            Assert.That(joint.localScale, Is.EqualTo(new Vector3(3f, 3f, 3f)), "the sample, not b + (s - n)");

            ec.SetWeight("grow", 0f);
            yield return null;
            Assert.That(joint.localScale, Is.EqualTo(new Vector3(2f, 2f, 2f)), "Standalone restores the authored value");
        }

        private static CharacterExpressionSet JointRotationSet(string name, Transform target, Quaternion[] deltaQuat, Quaternion baseQuat)
        {
            var driver = new JointDriver
            {
                Target = target,
                Channel = TrsChannel.Rotation,
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                DeltaQuat = deltaQuat,
                BaseQuat = baseQuat,
            };
            return WrapJoint(name, driver);
        }

        private static CharacterExpressionSet JointTranslationSet(string name, Transform target, Vector3[] deltaVec, Vector3 baseVec)
        {
            var driver = new JointDriver
            {
                Target = target,
                Channel = TrsChannel.Translation,
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                DeltaVec = deltaVec,
                BaseVec = baseVec,
            };
            return WrapJoint(name, driver);
        }

        private static CharacterExpressionSet WrapJoint(string name, JointDriver driver)
        {
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = name, Domains = ExpressionDomain.Joint, JointDrivers = new[] { driver } }
                }
            };
            set.RebuildIndex();
            return set;
        }
    }
}
