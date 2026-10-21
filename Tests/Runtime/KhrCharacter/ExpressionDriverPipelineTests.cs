using System;
using System.Collections;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    public class ExpressionDriverPipelineTests
    {
        private const string VocabularyIdentifier = "https://example.com/expression-vocabularies/example/v1";

        // The complete example of the KHR_character_expression_mapping README.
        private const string MappingReadmeExample = @"
            {
              ""asset"": { ""version"": ""2.0"" },
              ""scene"": 0,
              ""scenes"": [
                { ""nodes"": [0] }
              ],
              ""extensionsUsed"": [
                ""KHR_character"",
                ""KHR_character_expression"",
                ""KHR_character_expression_joint"",
                ""KHR_character_expression_mapping""
              ],
              ""nodes"": [
                { ""name"": ""CharacterRoot"" }
              ],
              ""buffers"": [
                {
                  ""byteLength"": 32,
                  ""uri"": ""data:application/octet-stream;base64,AAAAAAAAgD8AAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAA=""
                }
              ],
              ""bufferViews"": [
                { ""buffer"": 0, ""byteOffset"": 0, ""byteLength"": 8 },
                { ""buffer"": 0, ""byteOffset"": 8, ""byteLength"": 24 }
              ],
              ""accessors"": [
                {
                  ""bufferView"": 0,
                  ""componentType"": 5126,
                  ""count"": 2,
                  ""type"": ""SCALAR"",
                  ""min"": [0],
                  ""max"": [1]
                },
                {
                  ""bufferView"": 1,
                  ""componentType"": 5126,
                  ""count"": 2,
                  ""type"": ""VEC3""
                }
              ],
              ""animations"": [
                {
                  ""channels"": [
                    { ""sampler"": 0, ""target"": { ""node"": 0, ""path"": ""translation"" } }
                  ],
                  ""samplers"": [
                    { ""input"": 0, ""output"": 1, ""interpolation"": ""LINEAR"" }
                  ]
                }
              ],
              ""extensions"": {
                ""KHR_character"": {},
                ""KHR_character_expression"": {
                  ""expressions"": [
                    {
                      ""name"": ""smileLeft"",
                      ""animation"": 0,
                      ""extensions"": {
                        ""KHR_character_expression_joint"": { ""channels"": [0] }
                      }
                    },
                    {
                      ""name"": ""smileRight"",
                      ""animation"": 0,
                      ""extensions"": {
                        ""KHR_character_expression_joint"": { ""channels"": [0] }
                      }
                    },
                    {
                      ""name"": ""jawOpen"",
                      ""animation"": 0,
                      ""extensions"": {
                        ""KHR_character_expression_joint"": { ""channels"": [0] }
                      }
                    }
                  ]
                },
                ""KHR_character_expression_mapping"": {
                  ""expressionSetMappings"": {
                    ""https://example.com/expression-vocabularies/example/v1"": {
                      ""Smile"": [
                        { ""source"": 0, ""name"": ""smileLeft"", ""weight"": 0.8 },
                        { ""source"": 1, ""name"": ""smileRight"", ""weight"": 0.8 }
                      ],
                      ""MouthOpen"": [
                        { ""source"": 2, ""name"": ""jawOpen"", ""weight"": 1.0 }
                      ]
                    }
                  }
                }
              }
            }";

        [Test]
        public void ClampingMakesUFromTheSuppliedDrivers()
        {
            var u = ExpressionDriverPipeline.ClampDrivers(new double?[] { 1.5d, -0.25d, null, 0.5d }, 5);

            Assert.That(u, Is.EqualTo(new[] { 1d, 0d, 0d, 0.5d, 0d }), "absent drivers are 0");
            Assert.That(ExpressionDriverPipeline.ClampDrivers(null, 2), Is.EqualTo(new[] { 0d, 0d }));
        }

        [Test]
        public void NonFiniteOrSurplusDriversAreRejected()
        {
            foreach (double driver in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => ExpressionDriverPipeline.ClampDrivers(new double?[] { 0d, driver }, 2));
            Assert.Throws<ArgumentException>(
                () => ExpressionDriverPipeline.ClampDrivers(new double?[] { 0d, 0d, 0d }, 2));
        }

        [Test]
        public void BlendAndBlockReadTheSourceFromU()
        {
            // The standard-suppression example: aa (0), happy (1) blends aa by 0.5, angry (2) blocks aa above 0.2.
            var suppressions = new IReadOnlyList<SuppressionEntry>[]
            {
                null,
                new[] { Suppression(0, SuppressionType.Blend, amount: 0.5d) },
                new[] { Suppression(0, SuppressionType.Block, amount: 1d, threshold: 0.2d) },
            };

            var below = ExpressionDriverPipeline.BuildSnapshot(new double?[] { 1d, 0.5d, 0.1d }, suppressions);
            var above = ExpressionDriverPipeline.BuildSnapshot(new double?[] { 1d, 0.5d, 0.6d }, suppressions);
            var atThreshold = ExpressionDriverPipeline.BuildSnapshot(new double?[] { 1d, 0d, 0.2d }, suppressions);

            Assert.That(below.V, Is.EqualTo(new[] { 0.75d, 0.5d, 0.1d }));
            Assert.That(above.V[0], Is.EqualTo(0d));
            Assert.That(atThreshold.V[0], Is.EqualTo(1d), "the block comparison is strict");
            Assert.That(above.U, Is.EqualTo(new[] { 1d, 0.5d, 0.6d }), "u is never modified");
        }

        [Test]
        public void ACycleReadsOnlyU()
        {
            var suppressions = new IReadOnlyList<SuppressionEntry>[]
            {
                new[] { Suppression(1, SuppressionType.Blend) },
                new[] { Suppression(0, SuppressionType.Blend) },
            };

            var snapshot = ExpressionDriverPipeline.BuildSnapshot(new double?[] { 1d, 0.5d }, suppressions);

            Assert.That(snapshot.V, Is.EqualTo(new[] { 0.5d, 0d }));
        }

        [Test]
        public void SelfSuppressionReadsItsOwnUnsuppressedDriver()
        {
            var suppressions = new IReadOnlyList<SuppressionEntry>[]
            {
                new[] { Suppression(0, SuppressionType.Blend, amount: 0.5d) },
            };

            var snapshot = ExpressionDriverPipeline.BuildSnapshot(new double?[] { 0.8d }, suppressions);

            Assert.That(snapshot.V[0], Is.EqualTo(0.48d).Within(1e-15d));
        }

        [Test]
        public void ACustomTypeHasFactorOne()
        {
            var custom = Suppression(0, SuppressionType.Identity, amount: 0.25d, threshold: 0.1d);
            custom.CustomType = "ACME_expression_suppression_curve";
            var suppressions = new IReadOnlyList<SuppressionEntry>[] { null, new[] { custom } };

            var snapshot = ExpressionDriverPipeline.BuildSnapshot(new double?[] { 0.7d, 1d }, suppressions);

            Assert.That(snapshot.V, Is.EqualTo(new[] { 0.7d, 1d }));
            Assert.That(ExpressionDriverPipeline.SuppressionFactor(custom, 1d), Is.EqualTo(1d));
        }

        [Test]
        public void OmittedAmountAndThresholdReadAsOneAndZero()
        {
            var blend = new SuppressionEntry { TargetIndex = 0, Type = SuppressionType.Blend };
            var block = new SuppressionEntry { TargetIndex = 0, Type = SuppressionType.Block };
            Assert.IsFalse(blend.HasAmount);
            Assert.IsFalse(block.HasThreshold);

            Assert.That(ExpressionDriverPipeline.SuppressionFactor(blend, 0.25d), Is.EqualTo(0.75d));
            Assert.That(ExpressionDriverPipeline.SuppressionFactor(block, 0.01d), Is.EqualTo(0d));
            Assert.That(ExpressionDriverPipeline.SuppressionFactor(block, 0d), Is.EqualTo(1d));
        }

        [Test]
        public void TheForwardMappingReadsUWhileItsSourceIsSuppressed()
        {
            var suppressions = new IReadOnlyList<SuppressionEntry>[]
            {
                null,
                new[] { Suppression(0, SuppressionType.Blend) },
            };
            var mapping = new ExpressionMappingSet
            {
                SetName = VocabularyIdentifier,
                Targets = new[]
                {
                    new MappingTarget
                    {
                        TargetName = "Smile",
                        Contributions = new[] { new MappingContribution { SourceIndex = 0, Weight = 1d } },
                    },
                },
            };

            var snapshot = ExpressionDriverPipeline.BuildSnapshot(new double?[] { 1d, 1d }, suppressions);
            var outputs = ExpressionDriverPipeline.EvaluateForwardMapping(mapping, snapshot.U);

            Assert.That(snapshot.V[0], Is.EqualTo(0d));
            Assert.That(outputs["Smile"], Is.EqualTo(1d), "endpoint outputs reflect the requested drivers");
        }

        [Test]
        public void CurrentMappingAndOptionalHostProjectionUseTheSameCoefficients()
        {
            ParseMappingExample(out var forward, out var input);

            var outputs = ExpressionDriverPipeline.EvaluateForwardMapping(forward, new[] { 0.5d, 0.25d, 1d });
            var native = ExpressionDriverPipeline.EvaluateInputMapping(
                input,
                new Dictionary<string, double> { { "Smile", 1d }, { "MouthOpen", 0.25d } },
                3);

            Assert.That(BitConverter.DoubleToInt64Bits(outputs["Smile"]),
                Is.EqualTo(BitConverter.DoubleToInt64Bits(0.6000000000000001d)),
                "the exact binary64 sum of 0.8 * 0.5 and 0.8 * 0.25 rounds to 0.6000000000000001");
            Assert.That(outputs["MouthOpen"], Is.EqualTo(1d));
            Assert.That(native, Is.EqualTo(new[] { 0.8d, 0.8d, 0.25d }));
        }

        [Test]
        public void OutOfRangeCommandsAreClamped()
        {
            ParseMappingExample(out _, out var input);

            var native = ExpressionDriverPipeline.EvaluateInputMapping(
                input,
                new Dictionary<string, double> { { "Smile", 1.5d }, { "MouthOpen", -0.5d }, { "Unknown", 1d } },
                3);

            Assert.That(native, Is.EqualTo(new[] { 0.8d, 0.8d, 0d }));
            Assert.Throws<ArgumentOutOfRangeException>(() => ExpressionDriverPipeline.EvaluateInputMapping(
                input, new Dictionary<string, double> { { "Smile", double.NaN } }, 3));
        }

        [UnityTest]
        public IEnumerator ImportedMappingExampleComposesTheSharedTranslationAdditively()
        {
            GameObject scene = null;
            yield return CharacterGlbBuilder.ImportJson(
                MappingReadmeExample,
                plugin => plugin.CreateExpressionController = false,
                loaded => scene = loaded);
            try
            {
                var responses = scene.GetComponent<KhrCharacter>().ExpressionResponses;
                Assert.IsNotNull(responses);
                Assert.IsTrue(responses.Validation.IsComplete);

                var snapshot = responses.BuildSnapshot(new double?[] { 1d, 1d, 1d });
                var composition = responses.Compose(snapshot);

                var target = responses.Entries[0].Animation.Channels[0].Target;
                var delta = new double[3];
                for (int component = 0; component < 3; component++)
                    delta[component] = composition.Responses[0].Records[0].Value[component] -
                                       (double)composition.Responses[0].Records[0].NeutralValue[component];
                Assert.That(composition.Failures, Is.Empty);
                Assert.IsTrue(composition.TryGetRecord("/nodes/0/translation", out var translation));
                for (int component = 0; component < 3; component++)
                    Assert.That(translation.Value[component],
                        Is.EqualTo(target.AuthoredInitial[component] + 3d * delta[component]), "b + 3 * delta");
                Assert.That(translation.Value, Is.EqualTo(new[] { 3d, 0d, 0d }));

                var outputs = responses.EvaluateForwardMapping(
                    VocabularyIdentifier,
                    responses.BuildSnapshot(new double?[] { 0.5d, 0.25d, 1d }));
                Assert.That(outputs["Smile"], Is.EqualTo(0.6000000000000001d));
                Assert.That(responses.EvaluateInputMapping(
                        VocabularyIdentifier,
                        new Dictionary<string, double> { { "Smile", 1d }, { "MouthOpen", 0.25d } }),
                    Is.EqualTo(new[] { 0.8d, 0.8d, 0.25d }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scene);
            }
        }

        private static void ParseMappingExample(out ExpressionMappingSet forward, out ExpressionInputMappingSet input)
        {
            var root = JObject.Parse(MappingReadmeExample);
            var extensions = (JObject)root["extensions"];
            var mapping = (KHR_character_expression_mapping)new KHR_character_expression_mapping_Factory().Deserialize(
                null,
                new JProperty(KhrCharacterExtensionNames.ExpressionMapping, extensions[KhrCharacterExtensionNames.ExpressionMapping]));
            var expressions = new List<KHR_character_expression.ExpressionItem>();
            foreach (var entry in (JArray)extensions[KhrCharacterExtensionNames.Expression]["expressions"])
                expressions.Add(new KHR_character_expression.ExpressionItem { Name = (string)entry["name"] });
            var identity = new Dictionary<int, int> { { 0, 0 }, { 1, 1 }, { 2, 2 } };

            forward = KhrCharacterBaker.BuildMappingSets(mapping, identity, expressions)[0];
            input = KhrCharacterBaker.BuildInputMappingSets(mapping, identity, expressions)[0];
            Assert.That(forward.SetName, Is.EqualTo(VocabularyIdentifier));
            Assert.That(input.SetName, Is.EqualTo(VocabularyIdentifier));
        }

        private static SuppressionEntry Suppression(
            int target,
            SuppressionType type,
            double amount = 1d,
            double threshold = 0d)
        {
            return new SuppressionEntry { TargetIndex = target, Type = type, Amount = amount, Threshold = threshold };
        }
    }
}
