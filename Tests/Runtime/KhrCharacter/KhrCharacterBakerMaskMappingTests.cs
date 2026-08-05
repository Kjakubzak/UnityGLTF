using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden-value tests for remapping wire expression indices to runtime track indices.
    /// </summary>
    public class KhrCharacterBakerMaskMappingTests
    {
        private const string MappingVocab = "https://example.com/expression/v1";
        private static readonly Dictionary<int, int> WireToTrackIndex = new Dictionary<int, int> { { 0, 0 }, { 1, 1 } };
        private static readonly List<KHR_character_expression.ExpressionItem> WireExpressions =
            new List<KHR_character_expression.ExpressionItem>
            {
                new KHR_character_expression.ExpressionItem { Expression = "zero" },
                new KHR_character_expression.ExpressionItem { Expression = "target" },
            };

        [Test]
        public void BuildMaskEntries_ResolvesTargetAndSource()
        {
            var mask = new KHR_character_expression_mask
            {
                Masks = new List<KHR_character_expression_mask.Mask>
                {
                    new KHR_character_expression_mask.Mask { Target = 1, Type = "block", Amount = 0.75f, Threshold = 0.2f },
                    new KHR_character_expression_mask.Mask { Target = 99, Type = "blend", Amount = 1f }, // dropped
                }
            };

            var entries = KhrCharacterBaker.BuildMaskEntries(
                mask, sourceIndex: 0, WireToTrackIndex, WireExpressions);

            Assert.AreEqual(1, entries.Length);
            Assert.AreEqual(1, entries[0].TargetIndex);
            Assert.AreEqual(0, entries[0].SourceIndex);     // owning track
            Assert.AreEqual(MaskType.Block, entries[0].Type);
            Assert.AreEqual(0.75f, entries[0].Amount, 1e-5f);
            Assert.AreEqual(0.2f, entries[0].Threshold, 1e-5f);
        }

        [Test]
        public void BuildMaskEntries_PreservesCustomTypeWithIdentityFallback()
        {
            var mask = new KHR_character_expression_mask
            {
                Masks = new List<KHR_character_expression_mask.Mask>
                {
                    new KHR_character_expression_mask.Mask
                    {
                        Target = 1,
                        Name = "target",
                        Type = "ACME_soft_block",
                        Amount = 0.5f,
                        Extensions = new JObject { { "ACME_soft_block", new JObject { { "value", 1 } } } },
                        Extras = new JArray("mask", 1),
                        AdditionalProperties = new JObject { { "futureMaskField", 2 } },
                    },
                }
            };

            var entries = KhrCharacterBaker.BuildMaskEntries(
                mask, sourceIndex: 0, WireToTrackIndex, WireExpressions);

            Assert.AreEqual(1, entries.Length);
            Assert.AreEqual(MaskType.Identity, entries[0].Type,
                "unsupported application-defined mask types use the normative identity fallback");
            Assert.AreEqual("ACME_soft_block", entries[0].CustomType,
                "the application-defined vocabulary value must survive import and re-export");
            Assert.AreEqual("target", entries[0].Name);
            Assert.AreEqual(1, JObject.Parse(entries[0].RawExtensionsJson)["ACME_soft_block"]["value"].Value<int>());
            Assert.AreEqual("mask", JArray.Parse(entries[0].RawExtrasJson)[0].Value<string>());
            Assert.AreEqual(2,
                JObject.Parse(entries[0].RawAdditionalPropertiesJson)["futureMaskField"].Value<int>());
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
                            Weight = 0.7f,
                            Extensions = new JObject { { "ACME_mapping", new JObject { { "value", 1 } } } },
                            Extras = false,
                            AdditionalProperties = new JObject { { "futureMappingField", 2 } },
                        },
                        new KHR_character_expression_mapping.SourceWeight { Source = 1, Weight = 0.3f },
                        new KHR_character_expression_mapping.SourceWeight { Source = 99, Weight = 1f }, // dropped
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
            Assert.AreEqual(0.7f, contribs[0].Weight, 1e-5f);
            Assert.AreEqual(1, JObject.Parse(contribs[0].ExtensionsJson)["ACME_mapping"]["value"].Value<int>());
            Assert.IsFalse(JToken.Parse(contribs[0].ExtrasJson).Value<bool>());
            Assert.AreEqual(2,
                JObject.Parse(contribs[0].AdditionalPropertiesJson)["futureMappingField"].Value<int>());
            Assert.AreEqual(1, contribs[1].SourceIndex);
            Assert.AreEqual(0.3f, contribs[1].Weight, 1e-5f);
        }

        [Test]
        public void BuildInputMappingSets_ResolvesTargetIndicesSeparately()
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetInputMappings["https://example.com/vocab/v1"] =
                new Dictionary<string, List<KHR_character_expression_mapping.TargetWeight>>
                {
                    {
                        "happy", new List<KHR_character_expression_mapping.TargetWeight>
                        {
                            new KHR_character_expression_mapping.TargetWeight { Target = 1, Weight = 0.75f },
                            new KHR_character_expression_mapping.TargetWeight { Target = 99, Weight = 1f },
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

        [Test]
        public void BuildMappingSets_InvalidUriIdentifiersAreRejectedInBothDirections()
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings["vrm"] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    { "happy", new List<KHR_character_expression_mapping.SourceWeight>
                        { new KHR_character_expression_mapping.SourceWeight { Source = 0, Weight = 1f } } },
                };
            mapping.ExpressionSetInputMappings["vrm"] =
                new Dictionary<string, List<KHR_character_expression_mapping.TargetWeight>>
                {
                    { "happy", new List<KHR_character_expression_mapping.TargetWeight>
                        { new KHR_character_expression_mapping.TargetWeight { Target = 0, Weight = 1f } } },
                };

            var forward = Assert.Throws<System.InvalidOperationException>(
                () => KhrCharacterBaker.BuildMappingSets(mapping, WireToTrackIndex));
            StringAssert.Contains("'vrm' is not a valid absolute URI", forward.Message);
            var input = Assert.Throws<System.InvalidOperationException>(
                () => KhrCharacterBaker.BuildInputMappingSets(mapping, WireToTrackIndex));
            StringAssert.Contains("'vrm' is not a valid absolute URI", input.Message);
        }

        [Test]
        public void TryApplyMappingSets_InvalidDirectionKeepsBaseSetButExposesNoPartialAdapter()
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings[MappingVocab] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    { "happy", new List<KHR_character_expression_mapping.SourceWeight>
                        { new KHR_character_expression_mapping.SourceWeight { Source = 0, Weight = 1f } } },
                };
            mapping.ExpressionSetInputMappings["vrm"] =
                new Dictionary<string, List<KHR_character_expression_mapping.TargetWeight>>
                {
                    { "happy", new List<KHR_character_expression_mapping.TargetWeight>
                        { new KHR_character_expression_mapping.TargetWeight { Target = 0, Weight = 1f } } },
                };
            var baseTrack = new ExpressionTrack { Name = "base" };
            var set = new CharacterExpressionSet { Expressions = new[] { baseTrack } };

            LogAssert.Expect(LogType.Error,
                "[KHR_character] Expression mapping validation failed; no mapping adapter was exposed. " +
                "Mapping-set identifier 'vrm' is not a valid absolute URI.");
            bool applied = KhrCharacterBaker.TryApplyMappingSets(
                new GLTFRoot(), mapping, WireToTrackIndex, WireExpressions, set);

            Assert.IsFalse(applied);
            Assert.AreSame(baseTrack, set.Expressions[0], "optional import retains the base expression response");
            Assert.IsNull(set.MappingSets, "the valid direction is cleared when its companion direction is invalid");
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
            var mask = new KHR_character_expression_mask
            {
                Extensions = new JObject { { "ACME_optional", new JObject() } },
                Masks = new List<KHR_character_expression_mask.Mask>
                {
                    new KHR_character_expression_mask.Mask
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
                                Weight = 1f,
                                Extensions = new JObject { { "ACME_required", new JObject() } },
                            },
                        }
                    },
                };

            CollectionAssert.AreEqual(new[] { "ACME_required" },
                KhrCharacterBaker.GetRequiredMaskCompanionExtensions(root, mask));
            CollectionAssert.AreEqual(new[] { "ACME_required" },
                KhrCharacterBaker.GetRequiredMappingCompanionExtensions(root, mapping));
        }

        [Test]
        public void BuildMaskEntries_AllUnknownTargets_ReturnsEmpty()
        {
            var mask = new KHR_character_expression_mask
            {
                Masks = new List<KHR_character_expression_mask.Mask>
                {
                    new KHR_character_expression_mask.Mask { Target = 98, Type = "blend", Amount = 1f },
                    new KHR_character_expression_mask.Mask { Target = 99, Type = "block", Amount = 1f },
                }
            };

            var entries = KhrCharacterBaker.BuildMaskEntries(mask, sourceIndex: 0, WireToTrackIndex);

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
                        new KHR_character_expression_mapping.SourceWeight { Source = 98, Weight = 1f },
                        new KHR_character_expression_mapping.SourceWeight { Source = 99, Weight = 1f },
                    }
                },
                {
                    "happy", new List<KHR_character_expression_mapping.SourceWeight>
                    {
                        new KHR_character_expression_mapping.SourceWeight { Source = 0, Weight = 1f },
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
                        new KHR_character_expression_mapping.SourceWeight { Source = 99, Weight = 1f },
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
            var mask = new KHR_character_expression_mask
            {
                Masks = new List<KHR_character_expression_mask.Mask>
                {
                    new KHR_character_expression_mask.Mask { Target = 2 },
                }
            };
            var entries = KhrCharacterBaker.BuildMaskEntries(mask, 0, wireToTrack);
            Assert.AreEqual(1, entries[0].TargetIndex);

            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings[MappingVocab] =
                new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
                {
                    {
                        "Target", new List<KHR_character_expression_mapping.SourceWeight>
                        {
                            new KHR_character_expression_mapping.SourceWeight { Source = 2, Weight = 1f },
                        }
                    },
                };
            var sets = KhrCharacterBaker.BuildMappingSets(mapping, wireToTrack);
            Assert.AreEqual(1, sets[0].Targets[0].Contributions[0].SourceIndex);
        }
    }
}
