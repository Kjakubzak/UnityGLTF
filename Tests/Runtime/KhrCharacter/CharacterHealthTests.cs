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
    }
}
