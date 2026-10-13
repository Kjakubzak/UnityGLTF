using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "lift",
                        Domains = ExpressionDomain.Joint,
                        JointDrivers = new[] { TranslationDriver(joint, new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f)) },
                    },
                    new ExpressionTrack
                    {
                        Name = "raise",
                        Domains = ExpressionDomain.Joint,
                        JointDrivers = new[] { TranslationDriver(joint, new Vector3(0f, 1f, 0f), new Vector3(0f, 2f, 0f)) },
                    },
                }
            };
            var ec = root.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("lift", 1f);
            ec.SetWeight("raise", 1f);
            yield return null;
            Assert.AreEqual(1f + 1f + 2f, joint.localPosition.y, 1e-5f, "b + (2 - 1) + (3 - 1)");
        }

        [UnityTest]
        public IEnumerator TimeZeroKeyAwayFromTheBase_DrivesNothing()
        {
            var root = MakeRoot(out var joint);
            joint.localPosition = new Vector3(0f, 7f, 0f);
            // The exporter refuses this curve: its first key is 0.5 above the authored value.
            var raised = new JointDriver
            {
                Target = joint,
                Channel = TrsChannel.Translation,
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                BaseVec = new Vector3(0f, 1f, 0f),
                DeltaVec = new[] { new Vector3(0f, 0.5f, 0f), new Vector3(0f, 2.5f, 0f) },
            };
            var ec = root.AddComponent<ExpressionController>();
            LogAssert.Expect(LogType.Warning,
                new Regex("Expression 0 \\('raised'\\) produces no output: .*time-zero translation"));
            ec.Initialize(WrapJoint("raised", raised));

            ec.SetWeight("raised", 1f);
            yield return null;
            Assert.That(ec.Bindings, Is.Empty);
            Assert.That(joint.localPosition, Is.EqualTo(new Vector3(0f, 7f, 0f)));
        }

        [UnityTest]
        public IEnumerator RotationOfOneJointOnTwoTracks_KeepsTheFirstTrackWithOneWarning()
        {
            var root = MakeRoot(out var joint);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "lookRight",
                        Domains = ExpressionDomain.Joint,
                        JointDrivers = new[] { RotationDriver(joint, Quaternion.Euler(0f, 30f, 0f)) },
                    },
                    new ExpressionTrack
                    {
                        Name = "lookUp",
                        Domains = ExpressionDomain.Joint,
                        JointDrivers = new[] { RotationDriver(joint, Quaternion.Euler(-20f, 0f, 0f)) },
                    },
                }
            };
            var ec = root.AddComponent<ExpressionController>();
            LogAssert.Expect(LogType.Warning, new Regex(
                "Expressions 0 \\('lookRight'\\) and 1 \\('lookUp'\\) both animate the rotation of 'bone'.*" +
                "expression 1 produces no output"));
            ec.Initialize(set);

            ec.SetWeight("lookRight", 1f);
            ec.SetWeight("lookUp", 1f);
            for (int frame = 0; frame < 3; frame++) yield return null;

            // A repeated warning or a per-frame composition error would be unexpected.
            LogAssert.NoUnexpectedReceived();
            Assert.That(ec.Composition.Failures, Is.Empty, "composition never sees two rotation records");
            Assert.Less(Quaternion.Angle(joint.localRotation, Quaternion.Euler(0f, 30f, 0f)), 0.1f,
                "the first track owns the rotation");
        }

        [UnityTest]
        public IEnumerator Scale_IsTheAbsoluteSample()
        {
            var root = MakeRoot(out var joint);
            var grow = new JointDriver
            {
                Target = joint,
                Channel = TrsChannel.Scale,
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                BaseVec = new Vector3(2f, 2f, 2f),
                DeltaVec = new[] { Vector3.zero, Vector3.one },
            };
            var ec = root.AddComponent<ExpressionController>();
            ec.Initialize(WrapJoint("grow", grow));

            // Simulate an Animator writing a scale earlier in the frame.
            joint.localScale = new Vector3(5f, 5f, 5f);
            ec.SetWeight("grow", 1f);
            yield return null;
            Assert.That(joint.localScale, Is.EqualTo(new Vector3(3f, 3f, 3f)), "the sample replaces the live scale");

            ec.SetWeight("grow", 0f);
            yield return null;
            Assert.That(joint.localScale, Is.EqualTo(new Vector3(2f, 2f, 2f)), "Standalone restores the authored value");
        }

        private static JointDriver TranslationDriver(Transform target, Vector3 baseVec, Vector3 delta)
        {
            return new JointDriver
            {
                Target = target,
                Channel = TrsChannel.Translation,
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                BaseVec = baseVec,
                DeltaVec = new[] { Vector3.zero, delta },
            };
        }

        private static JointDriver RotationDriver(Transform target, Quaternion delta)
        {
            return new JointDriver
            {
                Target = target,
                Channel = TrsChannel.Rotation,
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                BaseQuat = Quaternion.identity,
                DeltaQuat = new[] { Quaternion.identity, delta },
            };
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
