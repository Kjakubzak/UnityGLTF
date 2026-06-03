using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden-value tests for the additive policy's Vector4 (_ST) sampling and STEP-index selection used by
    /// the texture domain.
    /// </summary>
    public class AdditiveExpressionSemanticsTextureTests
    {
        private readonly IExpressionSemantics _s = AdditiveExpressionSemantics.Default;

        private static Sampler Linear(float[] t) => new Sampler { Times = t, Interp = Interp.Linear, SingleKey = t.Length <= 1 };
        private static Sampler Step(float[] t) => new Sampler { Times = t, Interp = Interp.Step, SingleKey = t.Length <= 1 };

        [Test]
        public void Vector4_Linear_GoldenAtPhases()
        {
            var s = Linear(new[] { 0f, 1f });
            var dv = new[] { Vector4.zero, new Vector4(0f, 0f, 2f, 0f) };
            AssertV4(new Vector4(0f, 0f, 1f, 0f), _s.SampleVector4Delta(s, dv, Vector4.zero, 0.5f));
            AssertV4(new Vector4(0f, 0f, 2f, 0f), _s.SampleVector4Delta(s, dv, Vector4.zero, 1f));
        }

        [Test]
        public void Vector4_SingleKey_RestToTarget()
        {
            var s = new Sampler { Times = new[] { 0f }, Interp = Interp.Linear, SingleKey = true };
            var dv = new[] { new Vector4(1f, 1f, 1f, 1f) }; // absolute target
            AssertV4(new Vector4(0.5f, 0.5f, 0.5f, 0.5f), _s.SampleVector4Delta(s, dv, Vector4.zero, 0.5f)); // (1-0)*0.5
        }

        [Test]
        public void StepIndex_PicksKeyframeIncludingEndpoint()
        {
            var s = Step(new[] { 0f, 0.5f, 1f });
            Assert.AreEqual(0, _s.SampleStepIndex(s, 0f));
            Assert.AreEqual(0, _s.SampleStepIndex(s, 0.4f));
            Assert.AreEqual(1, _s.SampleStepIndex(s, 0.6f));
            Assert.AreEqual(2, _s.SampleStepIndex(s, 1f));
        }

        private static void AssertV4(Vector4 e, Vector4 a)
        {
            Assert.AreEqual(e.x, a.x, 1e-4f);
            Assert.AreEqual(e.y, a.y, 1e-4f);
            Assert.AreEqual(e.z, a.z, 1e-4f);
            Assert.AreEqual(e.w, a.w, 1e-4f);
        }
    }
}
