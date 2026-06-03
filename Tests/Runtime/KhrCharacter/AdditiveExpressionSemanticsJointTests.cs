using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden-value tests for the additive policy's vector/rotation sampling and the commutative rotation
    /// accumulation used by the joint domain.
    /// </summary>
    public class AdditiveExpressionSemanticsJointTests
    {
        private readonly IExpressionSemantics _s = AdditiveExpressionSemantics.Default;

        private static Sampler Linear(float[] times) => new Sampler { Times = times, Interp = Interp.Linear, SingleKey = times.Length <= 1 };

        [Test]
        public void Vector_Linear_GoldenAtPhases()
        {
            var s = Linear(new[] { 0f, 1f });
            var dv = new[] { Vector3.zero, new Vector3(0f, 2f, 0f) };
            AssertVec(new Vector3(0f, 1f, 0f), _s.SampleVectorDelta(s, dv, Vector3.zero, 0.5f));
            AssertVec(new Vector3(0f, 2f, 0f), _s.SampleVectorDelta(s, dv, Vector3.zero, 1f));
        }

        [Test]
        public void Vector_SingleKey_RestToTarget()
        {
            var s = new Sampler { Times = new[] { 0f }, Interp = Interp.Linear, SingleKey = true };
            var dv = new[] { new Vector3(0f, 4f, 0f) }; // absolute target
            AssertVec(new Vector3(0f, 1.5f, 0f), _s.SampleVectorDelta(s, dv, new Vector3(0f, 1f, 0f), 0.5f)); // (4-1)*0.5
        }

        [Test]
        public void Rotation_Linear_GoldenAtPhases()
        {
            var s = Linear(new[] { 0f, 1f });
            var dq = new[] { Quaternion.identity, Quaternion.Euler(0f, 90f, 0f) };
            Assert.Less(Quaternion.Angle(_s.SampleRotationDelta(s, dq, Quaternion.identity, 0.5f), Quaternion.Euler(0f, 45f, 0f)), 0.5f);
            Assert.Less(Quaternion.Angle(_s.SampleRotationDelta(s, dq, Quaternion.identity, 1f), Quaternion.Euler(0f, 90f, 0f)), 0.01f);
        }

        [Test]
        public void AccumulateRotation_RoundTripAndParallelAxes()
        {
            var q = Quaternion.Euler(0f, 45f, 0f);
            // A single accumulate from identity returns the input.
            Assert.Less(Quaternion.Angle(_s.AccumulateRotation(Quaternion.identity, q, 1f), q), 0.01f);
            // Two 45-degree rotations about the same axis accumulate to 90 degrees.
            var acc = _s.AccumulateRotation(Quaternion.identity, q, 1f);
            acc = _s.AccumulateRotation(acc, q, 1f);
            Assert.Less(Quaternion.Angle(acc, Quaternion.Euler(0f, 90f, 0f)), 0.05f);
        }

        private static void AssertVec(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-4f);
            Assert.AreEqual(expected.y, actual.y, 1e-4f);
            Assert.AreEqual(expected.z, actual.z, 1e-4f);
        }
    }
}
