using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    public class ExpressionComposerTests
    {
        private const string Translation = "/nodes/0/translation";
        private const string Weights = "/nodes/1/weights";
        private const string TextureRotation =
            "/materials/0/pbrMetallicRoughness/baseColorTexture/extensions/KHR_texture_transform/rotation";

        [Test]
        public void OneRecordUsesTheAuthoredValuePlusItsDifference()
        {
            var target = Target(Translation, ExpressionPropertyKind.NodeTranslation, new[] { 0.25d, 0d, -1d });

            var composition = ExpressionComposer.Compose(new[]
            {
                Response(Record(target, new[] { 1.25f, 2f, 3f }, new[] { 0.25f, 0f, -1f })),
            });

            var record = composition.Records[0];
            Assert.That(record.Property, Is.EqualTo(Translation));
            Assert.That(record.Kind, Is.EqualTo(ExpressionPropertyKind.NodeTranslation));
            Assert.IsFalse(record.IsSingleWriter);
            Assert.That(record.Value, Is.EqualTo(new[] { 1.25d, 2d, 3d }));
            Assert.That(record.Contributed, Is.EqualTo(new[] { true, true, true }));
            Assert.IsTrue(composition.TryGetRecord(Translation, out var found));
            Assert.AreSame(record, found);
        }

        [Test]
        public void TwoTranslationsAdd()
        {
            var target = Target(Translation, ExpressionPropertyKind.NodeTranslation, new[] { 1d, 0d, 0d });

            var composition = ExpressionComposer.Compose(new[]
            {
                Response(Record(target, new[] { 2f, 0f, 0f }, new[] { 1f, 0f, 0f })),
                Response(Record(target, new[] { 1.5f, 1f, 0f }, new[] { 1f, 0f, 0f })),
            });

            Assert.That(composition.Records.Count, Is.EqualTo(1));
            Assert.That(composition.Records[0].Value, Is.EqualTo(new[] { 2.5d, 1d, 0d }));
        }

        [Test]
        public void EachTranslationSubtractsItsOwnNeutralSample()
        {
            var target = Target(Translation, ExpressionPropertyKind.NodeTranslation, new[] { 0d, 1d, 0d });

            var composition = ExpressionComposer.Compose(new[]
            {
                Response(Record(target, new[] { 0f, 2f, 0f }, new[] { 0f, 1f, 0f })),
                Response(Record(target, new[] { 0f, 3.5f, 0f }, new[] { 0f, 1.5f, 0f })),
            });

            Assert.That(composition.Records[0].Value, Is.EqualTo(new[] { 0d, 4d, 0d }), "b + (2 - 1) + (3.5 - 1.5)");
        }

        [Test]
        public void TheNeutralSampleIsSubtractedEvenWhenItDiffersFromTheAuthoredValue()
        {
            float neutral = 128f / 255f;
            var target = ElementTarget(Weights, 0, new[] { 0.5d });
            var animation = new ExpressionResponseAnimation
            {
                Samplers = new[]
                {
                    new ExpressionResponseSampler
                    {
                        InputTimes = new[] { 0f, 1f },
                        OutputValues = new[] { new[] { neutral }, new[] { 1f } },
                        Interpolation = ExpressionResponseInterpolation.Linear,
                        OutputEncoding = ExpressionAccessorComponentEncoding.NormalizedUnsignedByte,
                    },
                },
                Channels = new[] { Channel(0, target) },
            };

            var response = ExpressionResponseEvaluator.Evaluate(animation, 1d);
            var composition = ExpressionComposer.Compose(new[] { response });

            Assert.That(response.Records[0].NeutralValue[0], Is.EqualTo(neutral));
            Assert.That(composition.Records[0].Value[0], Is.EqualTo(1.5d - neutral),
                "y = b + (s - n) with the decoded neutral key, never b + (s - b)");
            Assert.That(composition.Records[0].Value[0], Is.Not.EqualTo(1d));
        }

        [Test]
        public void WeightsAboveOneAreNotClamped()
        {
            var target = ArrayTarget(Weights, new[] { 0.5d, 0d });

            var composition = ExpressionComposer.Compose(new[]
            {
                Response(Record(target, new[] { 1.5f, 1f }, new[] { 0.5f, 0f })),
                Response(Record(target, new[] { 1f, 0.6f }, new[] { 0.5f, 0f })),
            });

            Assert.That(composition.Records[0].Value, Is.EqualTo(new[] { 2d, 1d + 0.6f }));
        }

        [Test]
        public void MixedWholeArrayAndElementRecordsFoldIntoOneVector()
        {
            var authored = new[] { 0.25d, 0.5d, 0.75d };
            var whole = ArrayTarget(Weights, authored);
            var element = ElementTarget(Weights, 1, authored);

            var composition = ExpressionComposer.Compose(new[]
            {
                Response(Record(whole, new[] { 0.25f, 0f, 0.25f }, new[] { 0f, 0f, 0.5f })),
                Response(Record(element, new[] { 0.75f }, new[] { 0.5f })),
            });

            Assert.That(composition.Records.Count, Is.EqualTo(1));
            Assert.That(composition.Records[0].Property, Is.EqualTo(Weights));
            Assert.That(composition.Records[0].Value, Is.EqualTo(new[] { 0.5d, 0.75d, 0.5d }));
            Assert.That(composition.Records[0].Contributed, Is.EqualTo(new[] { true, true, true }));
        }

        [Test]
        public void ElementOnlyRecordsStillProduceTheCompleteVector()
        {
            var authored = new[] { 0.1d, 0.5d, 0.75d };

            var composition = ExpressionComposer.Compose(new[]
            {
                Response(Record(ElementTarget(Weights, 2, authored), new[] { 1f }, new[] { 0.75f })),
            });

            var record = composition.Records[0];
            Assert.That(record.Value, Is.EqualTo(new[] { 0.1d, 0.5d, 1d }),
                "untouched elements keep the authored JSON doubles");
            Assert.That(record.Contributed, Is.EqualTo(new[] { false, false, true }));
        }

        [Test]
        public void OneFailingElementDropsTheVectorOfItsNode()
        {
            var authored = new[] { 0.25d, 0.5d };
            var other = Target(Translation, ExpressionPropertyKind.NodeTranslation, new[] { 0d, 0d, 0d });
            var failed = new ExpressionResponse(
                1d,
                1f,
                1f,
                new ExpressionResponseRecord[0],
                new[] { new ExpressionEvaluationFailure(Weights, 0, -1, 3, "sample component 0 is not finite") });

            var composition = ExpressionComposer.Compose(new[]
            {
                failed,
                Response(
                    Record(ElementTarget(Weights, 1, authored), new[] { 1f }, new[] { 0.5f }),
                    Record(other, new[] { 1f, 0f, 0f }, new[] { 0f, 0f, 0f })),
            });

            Assert.IsFalse(composition.TryGetRecord(Weights, out _), "no partial morph vector");
            Assert.IsTrue(composition.TryGetRecord(Translation, out _), "other properties still compose");
            Assert.That(composition.Failures.Count, Is.EqualTo(1));
            Assert.That(composition.Failures[0].ExpressionIndex, Is.EqualTo(0));
            Assert.That(composition.Failures[0].ChannelIndex, Is.EqualTo(3));
        }

        [Test]
        public void TextureRotationAddsWholeTurnsWithoutWrapping()
        {
            double authored = 2d * Math.PI;
            float neutral = (float)(2d * Math.PI);
            float current = (float)(6d * Math.PI);
            var target = Target(TextureRotation, ExpressionPropertyKind.TextureRotation, new[] { authored }, false);

            var composition = ExpressionComposer.Compose(new[]
            {
                Response(Record(target, new[] { current }, new[] { neutral })),
            });

            double value = composition.Records[0].Value[0];
            Assert.That(value, Is.EqualTo(authored + ((double)current - neutral)));
            Assert.That(value - authored, Is.EqualTo(4d * Math.PI).Within(1e-5d), "two whole turns are added");
        }

        [Test]
        public void ScaleAndRotationPassTheAbsoluteSampleThrough()
        {
            var scale = Target("/nodes/0/scale", ExpressionPropertyKind.NodeScale, new[] { 1d, 1d, 1d });
            var rotation = new ExpressionResponseTarget(
                ExpressionPropertyIdentity.ForWholeArray("/nodes/0/rotation"),
                4,
                ExpressionResponseValueKind.Quaternion,
                true,
                ExpressionPropertyKind.NodeRotation,
                new[] { 0d, 0d, 0d, 1d },
                0);
            float half = (float)Math.Sqrt(0.5d);

            var composition = ExpressionComposer.Compose(new[]
            {
                Response(
                    Record(scale, new[] { 3f, 4f, 5f }, new[] { 2f, 2f, 2f }),
                    Record(rotation, new[] { 0f, half, 0f, half }, new[] { 0f, 0f, 0f, 1f })),
            });

            Assert.IsTrue(composition.TryGetRecord("/nodes/0/scale", out var scaleRecord));
            Assert.IsTrue(scaleRecord.IsSingleWriter);
            Assert.That(scaleRecord.Value, Is.EqualTo(new[] { 3d, 4d, 5d }), "not b + (s - n)");
            Assert.IsTrue(composition.TryGetRecord("/nodes/0/rotation", out var rotationRecord));
            Assert.That(rotationRecord.Value, Is.EqualTo(new double[] { 0f, half, 0f, half }).Within(1e-6));
        }

        [Test]
        public void ResultBitsDoNotDependOnRecordOrder()
        {
            var translation = Target(Translation, ExpressionPropertyKind.NodeTranslation, new[] { 0d, 0.1d, 0d });
            var authored = new[] { 0.1d, 0.2d };
            var whole = ArrayTarget(Weights, authored);
            var element = ElementTarget(Weights, 0, authored);
            var responses = new[]
            {
                Response(
                    Record(translation, new[] { 1e30f, 0.3f, 0f }, new[] { 0f, 0.1f, 0f }),
                    Record(whole, new[] { 0.7f, 0.2f }, new[] { 0.1f, 0.2f })),
                Response(Record(translation, new[] { 1f, 0.7f, 0f }, new[] { 0f, 0.1f, 0f })),
                Response(
                    Record(translation, new[] { -1e30f, 1e-7f, 0f }, new[] { 0f, 0.1f, 0f }),
                    Record(element, new[] { 0.3f }, new[] { 0.1f })),
            };

            long[] expected = null;
            foreach (var order in new[]
            {
                new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
                new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
            })
            {
                var permuted = Array.ConvertAll(order, index => responses[index]);
                var composition = ExpressionComposer.Compose(permuted);
                var bits = new List<long>();
                foreach (var record in composition.Records)
                    foreach (double component in record.Value)
                        bits.Add(BitConverter.DoubleToInt64Bits(component));
                if (expected == null) expected = bits.ToArray();
                else Assert.That(bits, Is.EqualTo(expected), string.Join(",", order));
            }

            Assert.IsTrue(ExpressionComposer.Compose(responses).TryGetRecord(Translation, out var sum));
            Assert.That(sum.Value[0], Is.EqualTo(1d), "1e30 + 1 - 1e30 is exactly 1");
        }

        [Test]
        public void SharedScaleUsesHighestActiveExpressionIndex()
        {
            var scale = Target("/nodes/0/scale", ExpressionPropertyKind.NodeScale, new[] { 1d, 1d, 1d });

            var composition = ExpressionComposer.Compose(new[]
            {
                Response(Record(scale, new[] { 2f, 2f, 2f }, new[] { 1f, 1f, 1f })),
                Response(Record(scale, new[] { 3f, 3f, 3f }, new[] { 1f, 1f, 1f })),
            });

            Assert.IsTrue(composition.TryGetRecord("/nodes/0/scale", out var selected));
            Assert.That(selected.Value, Is.EqualTo(new[] { 3d, 3d, 3d }));
            Assert.That(composition.Failures, Is.Empty);
        }

        [Test]
        public void LegacyVendorDeclarationsCannotDisableMandatoryAddition()
        {
            var registry = new ExpressionDerivedExtensionRegistry();
            registry.Register("ACME_supported", true, isSupported: true,
                declaresSingleWriter: target => target.Kind == ExpressionPropertyKind.NodeTranslation);
            registry.Register("ACME_unsupported", true,
                declaresSingleWriter: target => target.Kind == ExpressionPropertyKind.NodeTranslation);
            registry.TryGet("ACME_supported", out var supported);
            registry.TryGet("ACME_unsupported", out var unsupported);
            var target = Target(Translation, ExpressionPropertyKind.NodeTranslation, new[] { 0d, 0d, 0d });
            var responses = new[]
            {
                Response(Record(target, new[] { 1f, 0f, 0f }, new[] { 0f, 0f, 0f })),
                Response(Record(target, new[] { 2f, 0f, 0f }, new[] { 0f, 0f, 0f })),
            };

            var additive = ExpressionComposer.Compose(
                responses, ExpressionComposer.SingleWriterPredicate(new[] { unsupported }));
            Assert.That(additive.Records[0].Value[0], Is.EqualTo(3d));

            var declared = ExpressionComposer.Compose(
                responses, ExpressionComposer.SingleWriterPredicate(new[] { supported }));
            Assert.That(declared.Records[0].Value[0], Is.EqualTo(3d));
        }

        [Test]
        public void AnAdditiveRecordWithoutAnAuthoredValueIsReported()
        {
            var target = new ExpressionResponseTarget(Translation, 3);

            LogAssert.Expect(LogType.Error, new Regex("no authored initial value"));
            var composition = ExpressionComposer.Compose(new[]
            {
                Response(Record(target, new[] { 1f, 0f, 0f }, new[] { 0f, 0f, 0f })),
            });

            Assert.That(composition.Records, Is.Empty);
            Assert.That(composition.Failures.Count, Is.EqualTo(1));
        }

        private static ExpressionResponseTarget Target(
            string property,
            ExpressionPropertyKind kind,
            double[] authored,
            bool wholeArray = true)
        {
            return new ExpressionResponseTarget(
                wholeArray ? ExpressionPropertyIdentity.ForWholeArray(property) : ExpressionPropertyIdentity.ForProperty(property),
                authored.Length,
                ExpressionResponseValueKind.Components,
                true,
                kind,
                authored,
                0);
        }

        private static ExpressionResponseTarget ArrayTarget(string property, double[] authored)
        {
            return new ExpressionResponseTarget(
                ExpressionPropertyIdentity.ForWholeArray(property),
                authored.Length,
                ExpressionResponseValueKind.Components,
                true,
                ExpressionPropertyKind.NodeWeights,
                authored,
                authored.Length);
        }

        private static ExpressionResponseTarget ElementTarget(string property, int element, double[] authored)
        {
            return new ExpressionResponseTarget(
                ExpressionPropertyIdentity.ForArrayElement(property, element),
                1,
                ExpressionResponseValueKind.Components,
                true,
                ExpressionPropertyKind.NodeWeights,
                new[] { authored[element] },
                authored.Length,
                authored);
        }

        private static ExpressionResponseChannel Channel(int samplerIndex, ExpressionResponseTarget target)
        {
            return new ExpressionResponseChannel
            {
                SamplerIndex = samplerIndex,
                Target = target,
                ListedBy = KhrCharacterExtensionNames.ExpressionMorphtarget,
                EmitsRecords = true,
            };
        }

        private static ExpressionResponseRecord Record(ExpressionResponseTarget target, float[] value, float[] neutral)
        {
            return new ExpressionResponseRecord(0, target, value, neutral);
        }

        private static ExpressionResponse Response(params ExpressionResponseRecord[] records)
        {
            return new ExpressionResponse(1d, 1f, 1f, records);
        }
    }
}
