using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden-value tests for remapping wire expression indices to runtime track indices in suppressions and
    /// mapping sets.
    /// </summary>
    public class KhrCharacterBakerMaskMappingTests
    {
        private const string MappingVocab = "https://example.com/expression/v1";
        private static readonly Dictionary<int, int> WireToTrackIndex = new Dictionary<int, int> { { 0, 0 }, { 1, 1 } };
        private static readonly List<KHR_character_expression.ExpressionItem> WireExpressions =
            new List<KHR_character_expression.ExpressionItem>
            {
                new KHR_character_expression.ExpressionItem { Name = "zero" },
                new KHR_character_expression.ExpressionItem { Name = "target" },
            };

        [UnityTest]
        public IEnumerator SuppressionsAndMappingSetsBindToTheResponseSetWithoutTheController()
        {
            var glb = CharacterGlbBuilder.TranslationCharacter("aa", "happy");
            ((JArray)glb.Json["extensionsUsed"]).Add(KhrCharacterExtensionNames.ExpressionSuppression);
            ((JArray)glb.Json["extensionsUsed"]).Add(KhrCharacterExtensionNames.ExpressionMapping);
            ((JObject)glb.Expression(1)["extensions"])[KhrCharacterExtensionNames.ExpressionSuppression] = new JObject
            {
                {
                    "suppressions", new JArray(new JObject
                    {
                        { "target", 0 }, { "type", "block" }, { "amount", 0.5 }, { "threshold", 0.25 },
                    })
                },
            };
            JArray Contribution(string key, int index, double weight)
                => new JArray(new JObject { { key, index }, { "weight", weight } });
            glb.Json["extensions"][KhrCharacterExtensionNames.ExpressionMapping] = new JObject
            {
                {
                    "expressionSetMappings", new JObject
                    {
                        { "vrm", new JObject { { "Smile", Contribution("source", 1, 0.5) } } },
                        { "relative/v1", new JObject { { "Smile", Contribution("source", 1, 0.25) } } },
                        { "", new JObject { { "Smile", Contribution("source", 0, 1) } } },
                    }
                },

            };

            LogAssert.Expect(LogType.Error,
                new Regex("Expression mapping set dropped: an expressionSetMappings identifier is empty"));
            GameObject scene = null;
            yield return glb.Import(plugin => plugin.CreateExpressionController = false, loaded => scene = loaded);
            try
            {
                var hub = scene.GetComponent<KhrCharacter>();
                Assert.IsNull(hub.Expressions);
                var responses = hub.ExpressionResponses;
                Assert.IsNotNull(responses);

                Assert.IsEmpty(responses.Entries[0].Suppressions);
                var suppression = responses.Entries[1].Suppressions[0];
                Assert.AreEqual(1, suppression.SourceIndex);
                Assert.AreEqual(0, suppression.TargetIndex);
                Assert.AreEqual(SuppressionType.Block, suppression.Type);
                Assert.AreEqual(0.5, suppression.Amount);
                Assert.AreEqual(0.25, suppression.Threshold);

                Assert.AreEqual(2, responses.MappingSets.Count, "the empty identifier drops only its own set");
                Assert.IsTrue(responses.TryGetForwardMappingSet("vrm", out var forward));
                Assert.AreEqual(1, forward.Targets[0].Contributions[0].SourceIndex);
                Assert.AreEqual(0.5, forward.Targets[0].Contributions[0].Weight);
                Assert.IsTrue(responses.TryGetInputMappingSet("relative/v1", out var input));
                Assert.AreEqual(1, input.Commands[0].Contributions[0].TargetIndex);
                Assert.AreEqual(0.25, input.Commands[0].Contributions[0].Weight);
            }
            finally
            {
                Object.DestroyImmediate(scene);
            }
        }

        [Test]
        public void BuildSuppressionEntries_ResolvesTargetAndSource()
        {
            var suppression = new KHR_character_expression_suppression
            {
                Suppressions = new List<KHR_character_expression_suppression.Suppression>
                {
                    new KHR_character_expression_suppression.Suppression { Target = 1, Type = "block", Amount = 0.75, Threshold = 0.2 },
                    new KHR_character_expression_suppression.Suppression { Target = 99, Type = "blend", Amount = 1 }, // dropped
                }
            };

            var entries = KhrCharacterBaker.BuildSuppressionEntries(suppression, sourceIndex: 0, WireToTrackIndex);

            Assert.AreEqual(1, entries.Length);
            Assert.AreEqual(1, entries[0].TargetIndex);
            Assert.AreEqual(0, entries[0].SourceIndex);     // owning track
            Assert.AreEqual(SuppressionType.Block, entries[0].Type);
            Assert.AreEqual(0.75, entries[0].Amount);
            Assert.IsTrue(entries[0].HasAmount);
            Assert.AreEqual(0.2, entries[0].Threshold);
            Assert.IsTrue(entries[0].HasThreshold);
        }

        [Test]
        public void BuildSuppressionEntries_OmittedAmountAndThresholdKeepDefaultsWithoutPresence()
        {
            var suppression = new KHR_character_expression_suppression
            {
                Suppressions = new List<KHR_character_expression_suppression.Suppression>
                {
                    new KHR_character_expression_suppression.Suppression { Target = 1 },
                }
            };

            var entries = KhrCharacterBaker.BuildSuppressionEntries(suppression, sourceIndex: 0, WireToTrackIndex);

            Assert.AreEqual(SuppressionType.Blend, entries[0].Type, "an omitted type is blend");
            Assert.AreEqual(1d, entries[0].Amount);
            Assert.IsFalse(entries[0].HasAmount);
            Assert.AreEqual(0d, entries[0].Threshold);
            Assert.IsFalse(entries[0].HasThreshold);
        }

        [Test]
        public void BuildSuppressionEntries_LegacyNameIsIgnoredWithoutWarning()
        {
            var suppression = KHR_character_expression_suppression.FromJson(new JObject
            {
                { "suppressions", new JArray
                    {
                        new JObject { { "target", 1 }, { "name", "not-the-target-name" }, { "type", "blend" } },
                    }
                },
            });

            var entries = KhrCharacterBaker.BuildSuppressionEntries(suppression, sourceIndex: 0, WireToTrackIndex);

            Assert.AreEqual(1, entries.Length);
            Assert.AreEqual(1, entries[0].TargetIndex);
            Assert.AreEqual("not-the-target-name",
                JObject.Parse(entries[0].RawAdditionalPropertiesJson)["name"].Value<string>(),
                "a legacy name is an unknown member: preserved verbatim, never compared");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void BuildSuppressionEntries_PreservesCustomTypeWithIdentityFallback()
        {
            var suppression = new KHR_character_expression_suppression
            {
                Suppressions = new List<KHR_character_expression_suppression.Suppression>
                {
                    new KHR_character_expression_suppression.Suppression
                    {
                        Target = 1,
                        Type = "ACME_soft_block",
                        Amount = 0.1,
                        Threshold = 0.3,
                        Extensions = new JObject { { "ACME_soft_block", new JObject { { "value", 1 } } } },
                        Extras = new JArray("suppression", 1),
                        AdditionalProperties = new JObject { { "futureSuppressionField", 2 } },
                    },
                }
            };

            var entries = KhrCharacterBaker.BuildSuppressionEntries(suppression, sourceIndex: 0, WireToTrackIndex);

            Assert.AreEqual(1, entries.Length);
            Assert.AreEqual(SuppressionType.Identity, entries[0].Type,
                "unsupported application-defined suppression types use the normative identity fallback");
            Assert.AreEqual("ACME_soft_block", entries[0].CustomType,
                "the application-defined vocabulary value must survive import and re-export");
            Assert.AreEqual(0.1, entries[0].Amount, "the authored double is kept exactly");
            Assert.AreEqual(0.3, entries[0].Threshold, "the authored double is kept exactly");
            Assert.IsTrue(entries[0].HasAmount && entries[0].HasThreshold);
            Assert.AreEqual(1, JObject.Parse(entries[0].RawExtensionsJson)["ACME_soft_block"]["value"].Value<int>());
            Assert.AreEqual("suppression", JArray.Parse(entries[0].RawExtrasJson)[0].Value<string>());
            Assert.AreEqual(2,
                JObject.Parse(entries[0].RawAdditionalPropertiesJson)["futureSuppressionField"].Value<int>());
        }

        [Test]
        public void BuildMappingSets_ResolvesSourceIndices()
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings[MappingVocab] = new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
            {
                {
                    "happy", new List<KHR_character_expression_mapping.SourceWeight>
                    {
                        new KHR_character_expression_mapping.SourceWeight
                        {
                            Source = 0,
                            Name = "zero",
                            Weight = 0.7,
                            Extensions = new JObject { { "ACME_mapping", new JObject { { "value", 1 } } } },
                            Extras = false,
                            AdditionalProperties = new JObject { { "futureMappingField", 2 } },
                        },
                        new KHR_character_expression_mapping.SourceWeight { Source = 1, Weight = 0.3 },
                        new KHR_character_expression_mapping.SourceWeight { Source = 99, Weight = 1 }, // dropped
                    }
                }
            };

            var sets = KhrCharacterBaker.BuildMappingSets(mapping, WireToTrackIndex, WireExpressions);

            Assert.AreEqual(1, sets.Length);
            Assert.AreEqual(MappingVocab, sets[0].SetName);
            Assert.AreEqual(1, sets[0].Targets.Length);
            Assert.AreEqual("happy", sets[0].Targets[0].TargetName);

            var contribs = sets[0].Targets[0].Contributions;
            Assert.AreEqual(2, contribs.Length);
            Assert.AreEqual(0, contribs[0].SourceIndex);
            Assert.AreEqual("zero", contribs[0].Name);
            Assert.AreEqual(0.7, contribs[0].Weight, "weights keep the authored double");
            Assert.AreEqual(1, JObject.Parse(contribs[0].ExtensionsJson)["ACME_mapping"]["value"].Value<int>());
            Assert.IsFalse(JToken.Parse(contribs[0].ExtrasJson).Value<bool>());
            Assert.AreEqual(2,
                JObject.Parse(contribs[0].AdditionalPropertiesJson)["futureMappingField"].Value<int>());
            Assert.AreEqual(1, contribs[1].SourceIndex);
            Assert.AreEqual(0.3, contribs[1].Weight);
        }

        [Test]
        public void BuildInputMappingSets_AdaptsCurrentSourceIndices()
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings["https://example.com/vocab/v1"] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    {
                        "happy", new List<KHR_character_expression_mapping.SourceWeight>
                        {
                            new KHR_character_expression_mapping.SourceWeight { Source = 1, Weight = 0.75 },
                            new KHR_character_expression_mapping.SourceWeight { Source = 99, Weight = 1 },
                        }
                    },
                };

            var sets = KhrCharacterBaker.BuildInputMappingSets(mapping, WireToTrackIndex);

            Assert.AreEqual(1, sets.Length);
            Assert.AreEqual("https://example.com/vocab/v1", sets[0].SetName);
            Assert.AreEqual("happy", sets[0].Commands[0].CommandName);
            Assert.AreEqual(1, sets[0].Commands[0].Contributions.Length);
            Assert.AreEqual(1, sets[0].Commands[0].Contributions[0].TargetIndex);
        }

        [TestCase("vrm")]
        [TestCase("relative/v1")]
        [TestCase("https://example.com/expression/v1")]
        public void BuildMappingSets_NonEmptyIdentifiersAreAcceptedInCanonicalAndInputAdapter(string identifier)
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings[identifier] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    { "happy", new List<KHR_character_expression_mapping.SourceWeight>
                        { new KHR_character_expression_mapping.SourceWeight { Source = 0, Weight = 1 } } },
                };

            var forward = KhrCharacterBaker.BuildMappingSets(mapping, WireToTrackIndex);
            var input = KhrCharacterBaker.BuildInputMappingSets(mapping, WireToTrackIndex);

            Assert.AreEqual(identifier, forward[0].SetName, "identifiers are opaque keys kept verbatim");
            Assert.AreEqual(identifier, input[0].SetName);
        }

        [Test]
        public void BuildMappingSets_EmptyIdentifierIsRejectedInCanonicalAndInputAdapter()
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings[""] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    { "happy", new List<KHR_character_expression_mapping.SourceWeight>
                        { new KHR_character_expression_mapping.SourceWeight { Source = 0, Weight = 1 } } },
                };

            Assert.Throws<System.InvalidOperationException>(
                () => KhrCharacterBaker.BuildMappingSets(mapping, WireToTrackIndex));
            Assert.Throws<System.InvalidOperationException>(
                () => KhrCharacterBaker.BuildInputMappingSets(mapping, WireToTrackIndex));
        }

        [Test]
        public void BuildMappingSets_IndependentNamesRemainWithoutWarnings()
        {
            var wireExpressions = new List<KHR_character_expression.ExpressionItem>
            {
                new KHR_character_expression.ExpressionItem { Name = "Smile" },
                new KHR_character_expression.ExpressionItem { Name = "target" },
            };
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings[MappingVocab] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    { "Smile", new List<KHR_character_expression_mapping.SourceWeight>
                        {
                            new KHR_character_expression_mapping.SourceWeight { Source = 0, Name = "Smile", Weight = 1 },
                            new KHR_character_expression_mapping.SourceWeight { Source = 0, Name = "smile", Weight = 1 },
                        }
                    },
                };

            var forward = KhrCharacterBaker.BuildMappingSets(mapping, WireToTrackIndex, wireExpressions);
            var input = KhrCharacterBaker.BuildInputMappingSets(mapping, WireToTrackIndex, wireExpressions);

            Assert.AreEqual(2, forward[0].Targets[0].Contributions.Length,
                "independent labels do not affect index resolution");
            Assert.AreEqual(0, forward[0].Targets[0].Contributions[1].SourceIndex);
            Assert.AreEqual(0, input[0].Commands[0].Contributions[0].TargetIndex);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void TryApplyMappingSets_InvalidCurrentIdentifierKeepsBaseSetButExposesNoPartialAdapter()
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings[MappingVocab] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    { "happy", new List<KHR_character_expression_mapping.SourceWeight>
                        { new KHR_character_expression_mapping.SourceWeight { Source = 0, Weight = 1 } } },
                };
            mapping.ExpressionSetMappings[""] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    { "happy", new List<KHR_character_expression_mapping.SourceWeight>
                        { new KHR_character_expression_mapping.SourceWeight { Source = 0, Weight = 1 } } },
                };
            var baseTrack = new ExpressionTrack { Name = "base" };
            var set = new CharacterExpressionSet { Expressions = new[] { baseTrack } };

            LogAssert.Expect(LogType.Error,
                "[KHR_character] Expression mapping validation failed; no mapping adapter was exposed. " +
                "A mapping-set identifier is empty.");
            bool applied = KhrCharacterBaker.TryApplyMappingSets(
                new GLTFRoot(), mapping, WireToTrackIndex, WireExpressions, set);

            Assert.IsFalse(applied);
            Assert.AreSame(baseTrack, set.Expressions[0], "optional import retains the base expression response");
            Assert.IsNull(set.MappingSets, "invalid current metadata prevents exposing a partial mapping adapter");
            Assert.IsNull(set.InputMappingSets);
            Assert.IsNull(set.MappingExtensionsJson);
            Assert.IsNull(set.MappingRequiredCompanionExtensions);
        }

        [Test]
        public void RequiredCompanionProvenance_UsesOnlyPayloadsListedGloballyRequired()
        {
            var root = new GLTFRoot
            {
                ExtensionsRequired = new List<string>
                {
                    "ACME_required",
                    "ACME_unrelated",
                },
            };
            var suppression = new KHR_character_expression_suppression
            {
                Extensions = new JObject { { "ACME_optional", new JObject() } },
                Suppressions = new List<KHR_character_expression_suppression.Suppression>
                {
                    new KHR_character_expression_suppression.Suppression
                    {
                        Target = 0,
                        Extensions = new JObject { { "ACME_required", new JObject { { "value", 1 } } } },
                    },
                },
            };
            var mapping = new KHR_character_expression_mapping
            {
                Extensions = new JObject { { "ACME_optional", new JObject() } },
            };
            mapping.ExpressionSetMappings[MappingVocab] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    { "happy", new List<KHR_character_expression_mapping.SourceWeight>
                        {
                            new KHR_character_expression_mapping.SourceWeight
                            {
                                Source = 0,
                                Weight = 1,
                                Extensions = new JObject { { "ACME_required", new JObject() } },
                            },
                        }
                    },
                };

            CollectionAssert.AreEqual(new[] { "ACME_required" },
                KhrCharacterBaker.GetRequiredSuppressionCompanionExtensions(root, suppression));
            CollectionAssert.AreEqual(new[] { "ACME_required" },
                KhrCharacterBaker.GetRequiredMappingCompanionExtensions(root, mapping));
        }

        [Test]
        public void BuildSuppressionEntries_AllUnknownTargets_ReturnsEmpty()
        {
            var suppression = new KHR_character_expression_suppression
            {
                Suppressions = new List<KHR_character_expression_suppression.Suppression>
                {
                    new KHR_character_expression_suppression.Suppression { Target = 98, Type = "blend", Amount = 1 },
                    new KHR_character_expression_suppression.Suppression { Target = 99, Type = "block", Amount = 1 },
                }
            };

            var entries = KhrCharacterBaker.BuildSuppressionEntries(suppression, sourceIndex: 0, WireToTrackIndex);

            Assert.IsNotNull(entries);
            Assert.AreEqual(0, entries.Length); // every dangling target dropped; the call still succeeds (M2/N4)
        }

        [Test]
        public void BuildMappingSets_TargetWithAllUnknownSources_IsDropped()
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings[MappingVocab] = new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
            {
                {
                    "ghostTarget", new List<KHR_character_expression_mapping.SourceWeight>
                    {
                        new KHR_character_expression_mapping.SourceWeight { Source = 98, Weight = 1 },
                        new KHR_character_expression_mapping.SourceWeight { Source = 99, Weight = 1 },
                    }
                },
                {
                    "happy", new List<KHR_character_expression_mapping.SourceWeight>
                    {
                        new KHR_character_expression_mapping.SourceWeight { Source = 0, Weight = 1 },
                    }
                },
            };

            var sets = KhrCharacterBaker.BuildMappingSets(mapping, WireToTrackIndex);

            // The all-unknown target is dropped; the valid target survives, so the set still builds (M2/N4).
            Assert.AreEqual(1, sets.Length);
            Assert.AreEqual(1, sets[0].Targets.Length);
            Assert.AreEqual("happy", sets[0].Targets[0].TargetName);
        }

        [Test]
        public void BuildMappingSets_AllUnknown_ReturnsNull()
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings[MappingVocab] = new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
            {
                {
                    "ghost", new List<KHR_character_expression_mapping.SourceWeight>
                    {
                        new KHR_character_expression_mapping.SourceWeight { Source = 99, Weight = 1 },
                    }
                },
            };

            var sets = KhrCharacterBaker.BuildMappingSets(mapping, WireToTrackIndex);

            Assert.IsNull(sets); // no resolvable contributions -> no set, handled gracefully (no throw)
        }

        [Test]
        public void WireIndices_RemapWhenAnExpressionWasSkipped()
        {
            var wireToTrack = new Dictionary<int, int> { { 0, 0 }, { 2, 1 } };
            var suppression = new KHR_character_expression_suppression
            {
                Suppressions = new List<KHR_character_expression_suppression.Suppression>
                {
                    new KHR_character_expression_suppression.Suppression { Target = 2 },
                }
            };
            var entries = KhrCharacterBaker.BuildSuppressionEntries(suppression, 0, wireToTrack);
            Assert.AreEqual(1, entries[0].TargetIndex);

            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings[MappingVocab] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    {
                        "Target", new List<KHR_character_expression_mapping.SourceWeight>
                        {
                            new KHR_character_expression_mapping.SourceWeight { Source = 2, Weight = 1 },
                        }
                    },
                };
            var sets = KhrCharacterBaker.BuildMappingSets(mapping, wireToTrack);
            Assert.AreEqual(1, sets[0].Targets[0].Contributions[0].SourceIndex);
        }
    }
}
