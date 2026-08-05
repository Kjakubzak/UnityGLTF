using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Tests the Character Health report: capabilities whose controller is wired read Active; those present in
    /// the asset but not driven read Inert.
    /// </summary>
    public class CharacterHealthTests
    {
        private readonly System.Collections.Generic.List<UnityEngine.Object> _created = new System.Collections.Generic.List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        private static CapabilityStatus StatusOf(CharacterHealthReport report, CharacterCapability cap)
        {
            foreach (var c in report.Capabilities)
                if (c.Capability == cap) return c.Status;
            return CapabilityStatus.Inert;
        }

        [Test]
        public void GetHealth_ReportsActiveForWiredAndInertForUnwired()
        {
            var go = new GameObject("char");
            _created.Add(go);
            var hub = go.AddComponent<KhrCharacter>();

            var set = new CharacterExpressionSet { Expressions = new[] { new ExpressionTrack { Name = "a", Domains = ExpressionDomain.Morph } } };
            set.RebuildIndex();
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);
            hub.Expressions = ec;

            hub.SetCapabilities(new[]
            {
                CharacterCapability.Character,
                CharacterCapability.Expression,
                CharacterCapability.CameraHint, // present but no CameraHintSet wired -> inert
            });

            var report = hub.GetHealth();

            Assert.AreEqual(CapabilityStatus.Active, StatusOf(report, CharacterCapability.Character));
            Assert.AreEqual(CapabilityStatus.Active, StatusOf(report, CharacterCapability.Expression));
            Assert.AreEqual(CapabilityStatus.Inert, StatusOf(report, CharacterCapability.CameraHint));
            Assert.AreEqual(1, report.ExpressionCount);
        }

        [Test]
        public void GetHealth_ReportsActiveForFullyResolvedSkeletonMapping()
        {
            var go = new GameObject("char");
            _created.Add(go);
            var hub = go.AddComponent<KhrCharacter>();

            var skel = go.AddComponent<SkeletonMap>();
            // Health consumes the baker's adapter assessment: resolved bones plus a valid report with no missing
            // adapter-required roles means Active.
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new System.Collections.Generic.Dictionary<string, Transform> { { "hips", go.transform } },
                SelectedRig = "rig",
            });
            hub.Skeleton = skel;
            hub.SetCapabilities(new[] { CharacterCapability.SkeletonMapping });

            Assert.AreEqual(CapabilityStatus.Active, StatusOf(hub.GetHealth(), CharacterCapability.SkeletonMapping));
        }

        [Test]
        public void GetHealth_ActiveForHealthyAdapterReport()
        {
            // Optional-role omission is covered in SkeletonMappingTests. At this layer, a valid upstream report
            // with resolved bones and no missing adapter-required roles must remain Active.
            var go = new GameObject("char");
            _created.Add(go);
            var hub = go.AddComponent<KhrCharacter>();

            var skel = go.AddComponent<SkeletonMap>();
            var result = new SkeletonMappingResult
            {
                Bones = new System.Collections.Generic.Dictionary<string, Transform> { { "hips", go.transform } },
                SelectedRig = "rig",
            };
            skel.Bind(result);
            hub.Skeleton = skel;
            hub.SetCapabilities(new[] { CharacterCapability.SkeletonMapping });

            Assert.AreEqual(CapabilityStatus.Active, StatusOf(hub.GetHealth(), CharacterCapability.SkeletonMapping));
        }

        [Test]
        public void GetHealth_ReportsDegradedForMissingRequiredBone()
        {
            // A schema-valid mapping may still be incomplete for the optional Unity Humanoid adapter. The baker
            // records that host-adapter condition separately in MissingRequiredBones, so health reads Degraded.
            var go = new GameObject("char");
            _created.Add(go);
            var hub = go.AddComponent<KhrCharacter>();

            var skel = go.AddComponent<SkeletonMap>();
            var result = new SkeletonMappingResult
            {
                Bones = new System.Collections.Generic.Dictionary<string, Transform> { { "hips", go.transform } },
                SelectedRig = "rig",
            };
            result.Report.MissingRequiredBones.Add("leftFoot");
            skel.Bind(result);
            hub.Skeleton = skel;
            hub.SetCapabilities(new[] { CharacterCapability.SkeletonMapping });

            Assert.IsTrue(result.Report.IsValid, "host-adapter health does not invalidate the glTF mapping");
            Assert.AreEqual(CapabilityStatus.Degraded, StatusOf(hub.GetHealth(), CharacterCapability.SkeletonMapping));
        }

        [Test]
        public void GetHealth_ReportsDegradedForZeroResolvedBones()
        {
            // The extension is present but nothing resolved (e.g. a reference-pose-only holder): present but not
            // driven -> Degraded, not a falsely-healthy Active.
            var go = new GameObject("char");
            _created.Add(go);
            var hub = go.AddComponent<KhrCharacter>();

            var skel = go.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new System.Collections.Generic.Dictionary<string, Transform>(), // nothing resolved
                SelectedRig = "rig",
            });
            hub.Skeleton = skel;
            hub.SetCapabilities(new[] { CharacterCapability.SkeletonMapping });

            Assert.AreEqual(CapabilityStatus.Degraded, StatusOf(hub.GetHealth(), CharacterCapability.SkeletonMapping));
        }

        [Test]
        public void GetHealth_ReportsInertForSkeletonMappingWithoutController()
        {
            var go = new GameObject("char");
            _created.Add(go);
            var hub = go.AddComponent<KhrCharacter>();
            hub.SetCapabilities(new[] { CharacterCapability.SkeletonMapping }); // no SkeletonMap wired

            Assert.AreEqual(CapabilityStatus.Inert, StatusOf(hub.GetHealth(), CharacterCapability.SkeletonMapping));
        }
    }
}
