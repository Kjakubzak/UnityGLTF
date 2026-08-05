using System;
using NUnit.Framework;

namespace UnityGLTF.KhrCharacter.Tests
{
    public class ExpressionInitialValueValidationTests
    {
        [Test]
        public void ExplicitPropertyValuePrecedesSpecificationDefault()
        {
            var explicitlyAuthored = new[] { 2f, 3f, 4f };
            var specificationDefault = new[] { 1f, 1f, 1f };

            var resolved = ExpressionInitialValueValidation.ResolveProperty(
                explicitlyAuthored,
                specificationDefault);

            Assert.That(resolved, Is.EqualTo(explicitlyAuthored));
            Assert.AreNotSame(explicitlyAuthored, resolved);
        }

        [Test]
        public void SpecificationDefaultIsUsedWhenPropertyIsOmitted()
        {
            var scaleDefault = new[] { 1f, 1f, 1f };

            var resolved = ExpressionInitialValueValidation.ResolveProperty(null, scaleDefault);

            Assert.That(resolved, Is.EqualTo(scaleDefault));
            Assert.AreNotSame(scaleDefault, resolved);
        }

        [Test]
        public void PropertyWithoutAuthoredValueOrDefaultIsRejected()
        {
            Assert.Throws<ExpressionResponseEvaluationException>(
                () => ExpressionInitialValueValidation.ResolveProperty(null, null));
        }

        [Test]
        public void MorphWeightsUseNodeThenMeshThenZeroPrecedence()
        {
            var nodeWeights = new[] { 0.1f, 0.2f };
            var meshWeights = new[] { 0.3f, 0.4f };

            var fromNode = ExpressionInitialValueValidation.ResolveMorphWeights(nodeWeights, meshWeights, 2);
            var fromMesh = ExpressionInitialValueValidation.ResolveMorphWeights(null, meshWeights, 2);
            var fromDefault = ExpressionInitialValueValidation.ResolveMorphWeights(null, null, 2);

            Assert.That(fromNode, Is.EqualTo(nodeWeights));
            Assert.That(fromMesh, Is.EqualTo(meshWeights));
            Assert.That(fromDefault, Is.EqualTo(new[] { 0f, 0f }));
        }

        [Test]
        public void MorphWeightsRejectAuthoredArrayWithWrongTargetCount()
        {
            Assert.Throws<ExpressionResponseEvaluationException>(
                () => ExpressionInitialValueValidation.ResolveMorphWeights(
                    new[] { 0.1f },
                    new[] { 0.2f, 0.3f },
                    2));
        }

        [Test]
        public void FloatComparisonUsesAbsoluteAndRelativeTolerance()
        {
            Assert.IsTrue(ExpressionInitialValueValidation.ComponentsEquivalent(
                new[] { 0f, 1_000_000f },
                new[] { 0.9e-6f, 1_000_001f },
                ExpressionAccessorComponentEncoding.Float));
            Assert.IsFalse(ExpressionInitialValueValidation.ComponentsEquivalent(
                new[] { 0f, 1_000_000f },
                new[] { 2.1e-6f, 1_000_001.25f },
                ExpressionAccessorComponentEncoding.Float));
        }

        [Test]
        public void FloatComparisonIncludesNormalizedAccessorAllowance()
        {
            float allowance = (float)(0.5d / 255d);

            Assert.IsTrue(ExpressionInitialValueValidation.ComponentsEquivalent(
                new[] { 0f },
                new[] { allowance + 0.9e-6f },
                ExpressionAccessorComponentEncoding.NormalizedUnsignedByte));
            Assert.IsFalse(ExpressionInitialValueValidation.ComponentsEquivalent(
                new[] { 0f },
                new[] { allowance + 2.1e-6f },
                ExpressionAccessorComponentEncoding.NormalizedUnsignedByte));
        }

        [Test]
        public void QuaternionComparisonTreatsAntipodesAsEquivalent()
        {
            float sine = (float)Math.Sin(Math.PI / 8d);
            float cosine = (float)Math.Cos(Math.PI / 8d);

            Assert.IsTrue(ExpressionInitialValueValidation.QuaternionsEquivalent(
                new[] { 0f, sine, 0f, cosine },
                new[] { 0f, -sine, 0f, -cosine },
                ExpressionAccessorComponentEncoding.Float));
        }

        [Test]
        public void QuaternionComparisonRejectsDifferentRotationAndZeroLength()
        {
            float oneDegreeSine = (float)Math.Sin(Math.PI / 360d);
            float oneDegreeCosine = (float)Math.Cos(Math.PI / 360d);

            Assert.IsFalse(ExpressionInitialValueValidation.QuaternionsEquivalent(
                new[] { 0f, 0f, 0f, 1f },
                new[] { 0f, oneDegreeSine, 0f, oneDegreeCosine },
                ExpressionAccessorComponentEncoding.Float));
            Assert.IsFalse(ExpressionInitialValueValidation.QuaternionsEquivalent(
                new[] { 0f, 0f, 0f, 1f },
                new[] { 0f, 0f, 0f, 0f },
                ExpressionAccessorComponentEncoding.Float));
        }

        [Test]
        public void TimeZeroComparisonUsesCubicValueSlotAndEndpointClamping()
        {
            var sampler = new ExpressionResponseSampler
            {
                InputTimes = new[] { 2f, 4f },
                OutputValues = new[]
                {
                    Vector(99f, 99f, 99f),
                    Vector(1f, 2f, 3f),
                    Vector(4f, 5f, 6f),
                    Vector(7f, 8f, 9f),
                    Vector(10f, 11f, 12f),
                    Vector(13f, 14f, 15f),
                },
                Interpolation = ExpressionResponseInterpolation.CubicSpline,
            };
            var target = new ExpressionResponseTarget(
                ExpressionPropertyIdentity.ForWholeArray("/nodes/0/translation"),
                3);

            Assert.IsTrue(ExpressionInitialValueValidation.MatchesTimeZeroSample(
                new[] { 1f, 2f, 3f },
                sampler,
                target,
                ExpressionAccessorComponentEncoding.Float));
        }

        [Test]
        public void InitialComparisonRequiresMatchingDimensionsAndFiniteValues()
        {
            Assert.IsFalse(ExpressionInitialValueValidation.ComponentsEquivalent(
                new[] { 0f, 1f },
                new[] { 0f },
                ExpressionAccessorComponentEncoding.Float));
            Assert.IsFalse(ExpressionInitialValueValidation.ComponentsEquivalent(
                new[] { 0f },
                new[] { float.NaN },
                ExpressionAccessorComponentEncoding.Float));
        }

        private static float[] Vector(params float[] value)
        {
            return value;
        }
    }
}
