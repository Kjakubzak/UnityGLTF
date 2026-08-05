using System;
using NUnit.Framework;

namespace UnityGLTF.KhrCharacter.Tests
{
    public class ExpressionResponseEvaluatorTests
    {
        [Test]
        public void ZeroDriverReturnsNoAuthoredInitialValues()
        {
            var animation = Animation(
                new[] { Linear(new[] { 0f, 2f }, Scalar(4f), Scalar(9f)) },
                Channel(0, "/nodes/0/translation/0", 1));

            var response = ExpressionResponseEvaluator.Evaluate(animation, 0f);

            Assert.That(response.EffectiveDriver, Is.Zero);
            Assert.That(response.Duration, Is.EqualTo(2f));
            Assert.That(response.Records, Is.Empty);
        }

        [Test]
        public void MissingDriverDefaultsToEmptyResponse()
        {
            var animation = Animation(
                new[] { Linear(new[] { 0f, 2f }, Scalar(4f), Scalar(9f)) },
                Channel(0, "/nodes/0/translation/0", 1));

            var response = ExpressionResponseEvaluator.Evaluate(animation);

            Assert.That(response.EffectiveDriver, Is.Zero);
            Assert.That(response.Records, Is.Empty);
        }

        [Test]
        public void NegativeDriverClampsToEmptyResponse()
        {
            var animation = Animation(
                new[] { Linear(new[] { 0f, 2f }, Scalar(4f), Scalar(9f)) },
                Channel(0, "/nodes/0/translation/0", 1));

            var response = ExpressionResponseEvaluator.Evaluate(animation, -3f);

            Assert.That(response.EffectiveDriver, Is.Zero);
            Assert.That(response.Records, Is.Empty);
        }

        [Test]
        public void DriverAboveOneClampsToNonLoopingEndpoint()
        {
            var animation = Animation(
                new[] { Linear(new[] { 0f, 2f }, Scalar(4f), Scalar(9f)) },
                Channel(0, "/nodes/0/translation/0", 1));

            var response = ExpressionResponseEvaluator.Evaluate(animation, 7f);

            Assert.That(response.EffectiveDriver, Is.EqualTo(1f));
            Assert.That(response.SampleTime, Is.EqualTo(2f));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(9f));
        }

        [Test]
        public void NonFiniteDriversAreRejected()
        {
            var animation = Animation(
                new[] { Linear(new[] { 0f, 2f }, Scalar(4f), Scalar(9f)) },
                Channel(0, "/nodes/0/translation/0", 1));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => ExpressionResponseEvaluator.Evaluate(animation, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ExpressionResponseEvaluator.Evaluate(animation, float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ExpressionResponseEvaluator.Evaluate(animation, float.NegativeInfinity));
        }

        [Test]
        public void ZeroDriverDoesNotBypassDecodedSamplerValidation()
        {
            var animation = Animation(
                new[]
                {
                    new ExpressionResponseSampler
                    {
                        InputTimes = new[] { 0f },
                        OutputValues = new[] { Scalar(1f) },
                        Interpolation = ExpressionResponseInterpolation.Linear,
                    },
                },
                Channel(0, "/nodes/0/translation/0", 1));

            Assert.Throws<ExpressionResponseEvaluationException>(
                () => ExpressionResponseEvaluator.Evaluate(animation, 0f));
        }

        [Test]
        public void MismatchedSamplersUseOneCommonAbsoluteTimeline()
        {
            var animation = Animation(
                new[]
                {
                    Linear(new[] { 0f, 1f }, Scalar(0f), Scalar(10f)),
                    Linear(new[] { 0f, 4f }, Scalar(0f), Scalar(40f)),
                },
                Channel(0, "/nodes/0/translation/0", 1),
                Channel(1, "/nodes/0/translation/1", 1));

            var response = ExpressionResponseEvaluator.Evaluate(animation, 0.5f);

            Assert.That(response.Duration, Is.EqualTo(4f));
            Assert.That(response.SampleTime, Is.EqualTo(2f));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(10f));
            Assert.That(response.Records[1].Value[0], Is.EqualTo(20f).Within(1e-6f));
        }

        [Test]
        public void DelayedSamplerClampsAtItsFirstKeyOnCommonTimeline()
        {
            var animation = Animation(
                new[]
                {
                    Linear(new[] { 2f, 4f }, Scalar(7f), Scalar(11f)),
                    Linear(new[] { 0f, 8f }, Scalar(0f), Scalar(8f)),
                },
                Channel(0, "/nodes/0/translation/0", 1),
                Channel(1, "/nodes/0/translation/1", 1));

            var response = ExpressionResponseEvaluator.Evaluate(animation, 0.125f);

            Assert.That(response.SampleTime, Is.EqualTo(1f));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(7f));
            Assert.That(response.Records[1].Value[0], Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void UnsupportedOptionalTargetStillContributesToDuration()
        {
            var unsupported = new ExpressionResponseTarget(
                "/extensions/OPTIONAL/value",
                0,
                ExpressionResponseValueKind.Components,
                false);
            var animation = Animation(
                new[]
                {
                    Linear(new[] { 0f, 2f }, Scalar(0f), Scalar(2f)),
                    new ExpressionResponseSampler
                    {
                        InputTimes = new[] { 0f, 10f },
                        Interpolation = ExpressionResponseInterpolation.Linear,
                    },
                },
                Channel(0, "/nodes/0/translation/0", 1),
                new ExpressionResponseChannel { SamplerIndex = 1, Target = unsupported });

            var response = ExpressionResponseEvaluator.Evaluate(animation, 0.5f);

            Assert.That(response.Duration, Is.EqualTo(10f));
            Assert.That(response.SampleTime, Is.EqualTo(5f));
            Assert.That(response.Records.Count, Is.EqualTo(1));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(2f));
        }

        [Test]
        public void EverySupportedChannelProducesARecordWithoutTypedClassification()
        {
            var animation = Animation(
                new[]
                {
                    Linear(new[] { 0f, 1f }, Scalar(0f), Scalar(1f)),
                    Linear(new[] { 0f, 1f }, Scalar(10f), Scalar(20f)),
                    Linear(new[] { 0f, 1f }, Scalar(30f), Scalar(40f)),
                },
                Channel(0, "/nodes/0/weights/0", 1),
                Channel(1, "/nodes/0/translation/0", 1),
                Channel(2, "/materials/0/pbrMetallicRoughness/baseColorFactor/0", 1));

            var response = ExpressionResponseEvaluator.Evaluate(animation, 0.5f);

            Assert.That(response.Records.Count, Is.EqualTo(3));
            Assert.That(response.Records[0].ChannelIndex, Is.EqualTo(0));
            Assert.That(response.Records[1].ChannelIndex, Is.EqualTo(1));
            Assert.That(response.Records[2].ChannelIndex, Is.EqualTo(2));
        }

        [Test]
        public void CanonicalIdentityRejectsCoreAndPointerDuplicate()
        {
            var canonicalTranslation = ExpressionPropertyIdentity.ForWholeArray("/nodes/0/translation");
            var animation = Animation(
                new[]
                {
                    Linear(new[] { 0f, 1f }, Vector(0f, 0f, 0f), Vector(1f, 0f, 0f)),
                    Linear(new[] { 0f, 1f }, Vector(0f, 0f, 0f), Vector(0f, 1f, 0f)),
                },
                Channel(0, canonicalTranslation, 3),
                Channel(1, canonicalTranslation, 3));

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => ExpressionResponseEvaluator.Evaluate(animation, 0.5f));
            StringAssert.Contains("overlapping concrete properties", exception.Message);
        }

        [Test]
        public void CanonicalIdentityRejectsWholeArrayAndPointerElementOverlap()
        {
            var animation = Animation(
                new[]
                {
                    Linear(new[] { 0f, 1f }, Vector(0f, 0f), Vector(1f, 1f)),
                    Linear(new[] { 0f, 1f }, Scalar(0f), Scalar(1f)),
                },
                Channel(0, ExpressionPropertyIdentity.ForWholeArray("/nodes/0/weights"), 2),
                Channel(1, ExpressionPropertyIdentity.ForArrayElement("/nodes/0/weights", 0), 1));

            Assert.Throws<ExpressionResponseEvaluationException>(
                () => ExpressionResponseEvaluator.Evaluate(animation, 0.5f));
        }

        [Test]
        public void DistinctArrayElementsAreDistinctConcreteProperties()
        {
            var animation = Animation(
                new[]
                {
                    Linear(new[] { 0f, 1f }, Scalar(0f), Scalar(1f)),
                    Linear(new[] { 0f, 1f }, Scalar(0f), Scalar(2f)),
                },
                Channel(0, ExpressionPropertyIdentity.ForArrayElement("/nodes/0/weights", 0), 1),
                Channel(1, ExpressionPropertyIdentity.ForArrayElement("/nodes/0/weights", 1), 1));

            var response = ExpressionResponseEvaluator.Evaluate(animation, 1f);

            Assert.That(response.Records.Count, Is.EqualTo(2));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(1f));
            Assert.That(response.Records[1].Value[0], Is.EqualTo(2f));
        }

        [Test]
        public void ResolvableUnsupportedTargetParticipatesInOverlapValidation()
        {
            var animation = Animation(
                new[]
                {
                    Linear(new[] { 0f, 1f }, Vector(0f, 0f), Vector(1f, 1f)),
                    new ExpressionResponseSampler
                    {
                        InputTimes = new[] { 0f, 2f },
                        Interpolation = ExpressionResponseInterpolation.Linear,
                    },
                },
                Channel(0, ExpressionPropertyIdentity.ForWholeArray("/nodes/0/weights"), 2),
                new ExpressionResponseChannel
                {
                    SamplerIndex = 1,
                    Target = new ExpressionResponseTarget(
                        ExpressionPropertyIdentity.ForArrayElement("/nodes/0/weights", 1),
                        0,
                        ExpressionResponseValueKind.Components,
                        false),
                });

            Assert.Throws<ExpressionResponseEvaluationException>(
                () => ExpressionResponseEvaluator.Evaluate(animation, 0.5f));
        }

        [Test]
        public void DecodedPackedMorphWeightsBecomeCompleteLogicalValues()
        {
            var sampler = ExpressionResponseSampler.FromDecodedScalarStream(
                new[] { 0f, 2f },
                new[] { 0f, 0.1f, 0.2f, 1f, 0.8f, 0.6f },
                3,
                ExpressionResponseInterpolation.Linear);
            var animation = Animation(
                new[] { sampler },
                Channel(0, ExpressionPropertyIdentity.ForWholeArray("/nodes/0/weights"), 3));

            var value = ExpressionResponseEvaluator.Evaluate(animation, 0.5f).Records[0].Value;

            Assert.That(value[0], Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(value[1], Is.EqualTo(0.45f).Within(1e-6f));
            Assert.That(value[2], Is.EqualTo(0.4f).Within(1e-6f));
        }

        [Test]
        public void DecodedPackedMorphWeightsRejectIncompleteKeyValue()
        {
            Assert.Throws<ExpressionResponseEvaluationException>(() =>
                ExpressionResponseSampler.FromDecodedScalarStream(
                    new[] { 0f, 1f },
                    new[] { 0f, 0.1f, 0.2f, 1f, 0.8f },
                    3,
                    ExpressionResponseInterpolation.Linear));
        }

        [Test]
        public void EvaluationIsIndependentOfPriorDriverHistory()
        {
            var animation = Animation(
                new[] { Linear(new[] { 0f, 5f }, Scalar(2f), Scalar(12f)) },
                Channel(0, "/nodes/0/translation/0", 1));

            var first = ExpressionResponseEvaluator.Evaluate(animation, 0.8f);
            ExpressionResponseEvaluator.Evaluate(animation, 0.2f);
            var repeated = ExpressionResponseEvaluator.Evaluate(animation, 0.8f);

            Assert.That(repeated.Records[0].Value[0], Is.EqualTo(first.Records[0].Value[0]));
            Assert.That(repeated.SampleTime, Is.EqualTo(first.SampleTime));
        }

        [Test]
        public void ReferencedOneKeySamplerIsRejected()
        {
            var animation = Animation(
                new[]
                {
                    new ExpressionResponseSampler
                    {
                        InputTimes = new[] { 0f },
                        OutputValues = new[] { Scalar(1f) },
                        Interpolation = ExpressionResponseInterpolation.Linear,
                    },
                },
                Channel(0, "/nodes/0/translation/0", 1));

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => ExpressionResponseEvaluator.Evaluate(animation, 0.5f));
            StringAssert.Contains("at least two input keys", exception.Message);
        }

        [Test]
        public void UnreferencedOneKeySamplerDoesNotAffectEvaluation()
        {
            var animation = Animation(
                new[]
                {
                    Linear(new[] { 0f, 1f }, Scalar(0f), Scalar(1f)),
                    new ExpressionResponseSampler
                    {
                        InputTimes = new[] { 0f },
                        OutputValues = new[] { Scalar(99f) },
                        Interpolation = ExpressionResponseInterpolation.Linear,
                    },
                },
                Channel(0, "/nodes/0/translation/0", 1));

            var response = ExpressionResponseEvaluator.Evaluate(animation, 1f);

            Assert.That(response.Duration, Is.EqualTo(1f));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(1f));
        }

        [Test]
        public void StepUsesNextKeyAtBoundaryAndDoesNotLoopAtOne()
        {
            var sampler = new ExpressionResponseSampler
            {
                InputTimes = new[] { 0f, 1f, 2f },
                OutputValues = new[] { Scalar(10f), Scalar(20f), Scalar(30f) },
                Interpolation = ExpressionResponseInterpolation.Step,
            };
            var animation = Animation(
                new[] { sampler },
                Channel(0, "/nodes/0/translation/0", 1));

            var boundary = ExpressionResponseEvaluator.Evaluate(animation, 0.5f);
            var end = ExpressionResponseEvaluator.Evaluate(animation, 1f);

            Assert.That(boundary.Records[0].Value[0], Is.EqualTo(20f));
            Assert.That(end.Records[0].Value[0], Is.EqualTo(30f));
        }

        [Test]
        public void CubicSplineSamplesScalarWithTimeScaledTangents()
        {
            var sampler = Cubic(
                new[] { 0f, 2f },
                Scalar(0f), Scalar(0f), Scalar(2f),
                Scalar(0f), Scalar(2f), Scalar(0f));
            var animation = Animation(
                new[] { sampler },
                Channel(0, "/nodes/0/translation/0", 1));

            var response = ExpressionResponseEvaluator.Evaluate(animation, 0.5f);

            Assert.That(response.Records[0].Value[0], Is.EqualTo(1.5f).Within(1e-6f));
        }

        [Test]
        public void CubicSplineSamplesAllVectorComponents()
        {
            var sampler = Cubic(
                new[] { 0f, 2f },
                Vector(0f, 0f), Vector(0f, 0f), Vector(1f, 2f),
                Vector(1f, 0f), Vector(2f, 4f), Vector(0f, 0f));
            var animation = Animation(
                new[] { sampler },
                Channel(0, "/nodes/0/translation", 2));

            var response = ExpressionResponseEvaluator.Evaluate(animation, 0.5f);

            Assert.That(response.Records[0].Value[0], Is.EqualTo(1f).Within(1e-6f));
            Assert.That(response.Records[0].Value[1], Is.EqualTo(2.5f).Within(1e-6f));
        }

        [Test]
        public void LinearQuaternionUsesShortestPathAndNormalizes()
        {
            float halfNinety = (float)Math.Sqrt(0.5d);
            var sampler = Linear(
                new[] { 0f, 2f },
                Vector(0f, 0f, 0f, 1f),
                Vector(0f, 0f, -halfNinety, -halfNinety));
            var animation = Animation(
                new[] { sampler },
                Channel(0, "/nodes/0/rotation", 4, ExpressionResponseValueKind.Quaternion));

            var value = ExpressionResponseEvaluator.Evaluate(animation, 0.5f).Records[0].Value;
            float halfFortyFive = (float)Math.Sin(Math.PI / 8d);
            float expectedW = (float)Math.Cos(Math.PI / 8d);
            float absoluteDot = Math.Abs(value[2] * halfFortyFive + value[3] * expectedW);
            float length = (float)Math.Sqrt(value[0] * value[0] + value[1] * value[1] + value[2] * value[2] + value[3] * value[3]);

            Assert.That(absoluteDot, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(length, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void NonUnitQuaternionKeyValueIsRejected()
        {
            var sampler = Linear(
                new[] { 0f, 1f },
                Vector(0f, 0f, 0f, 1f),
                Vector(0f, 0f, 0f, 2f));
            var animation = Animation(
                new[] { sampler },
                Channel(0, "/nodes/0/rotation", 4, ExpressionResponseValueKind.Quaternion));

            Assert.Throws<ExpressionResponseEvaluationException>(
                () => ExpressionResponseEvaluator.Evaluate(animation, 0.5f));
        }

        [Test]
        public void CubicQuaternionUsesComponentHermiteThenNormalizes()
        {
            var zero = Vector(0f, 0f, 0f, 0f);
            var sampler = Cubic(
                new[] { 0f, 2f },
                zero, Vector(0f, 0f, 0f, 1f), zero,
                zero, Vector(0f, 0f, 1f, 0f), zero);
            var animation = Animation(
                new[] { sampler },
                Channel(0, "/nodes/0/rotation", 4, ExpressionResponseValueKind.Quaternion));

            var value = ExpressionResponseEvaluator.Evaluate(animation, 0.5f).Records[0].Value;
            float expected = (float)Math.Sqrt(0.5d);

            Assert.That(value[0], Is.Zero.Within(1e-6f));
            Assert.That(value[1], Is.Zero.Within(1e-6f));
            Assert.That(value[2], Is.EqualTo(expected).Within(1e-6f));
            Assert.That(value[3], Is.EqualTo(expected).Within(1e-6f));
        }

        private static ExpressionResponseAnimation Animation(
            ExpressionResponseSampler[] samplers,
            params ExpressionResponseChannel[] channels)
        {
            return new ExpressionResponseAnimation { Samplers = samplers, Channels = channels };
        }

        private static ExpressionResponseChannel Channel(
            int samplerIndex,
            string property,
            int components,
            ExpressionResponseValueKind kind = ExpressionResponseValueKind.Components)
        {
            return new ExpressionResponseChannel
            {
                SamplerIndex = samplerIndex,
                Target = new ExpressionResponseTarget(property, components, kind),
            };
        }

        private static ExpressionResponseChannel Channel(
            int samplerIndex,
            ExpressionPropertyIdentity identity,
            int components,
            ExpressionResponseValueKind kind = ExpressionResponseValueKind.Components)
        {
            return new ExpressionResponseChannel
            {
                SamplerIndex = samplerIndex,
                Target = new ExpressionResponseTarget(identity, components, kind),
            };
        }

        private static ExpressionResponseSampler Linear(float[] times, params float[][] values)
        {
            return new ExpressionResponseSampler
            {
                InputTimes = times,
                OutputValues = values,
                Interpolation = ExpressionResponseInterpolation.Linear,
            };
        }

        private static ExpressionResponseSampler Cubic(float[] times, params float[][] values)
        {
            return new ExpressionResponseSampler
            {
                InputTimes = times,
                OutputValues = values,
                Interpolation = ExpressionResponseInterpolation.CubicSpline,
            };
        }

        private static float[] Scalar(float value)
        {
            return new[] { value };
        }

        private static float[] Vector(params float[] value)
        {
            return value;
        }
    }
}
