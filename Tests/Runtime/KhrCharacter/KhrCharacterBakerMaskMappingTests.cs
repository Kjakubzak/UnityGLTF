using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden-value tests for remapping wire expression indices to runtime track indices.
    /// </summary>
    public class KhrCharacterBakerMaskMappingTests
    {
        private static readonly Dictionary<int, int> WireToTrackIndex = new Dictionary<int, int> { { 0, 0 }, { 1, 1 } };

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

            var entries = KhrCharacterBaker.BuildMaskEntries(mask, sourceIndex: 0, WireToTrackIndex);

            Assert.AreEqual(1, entries.Length);
            Assert.AreEqual(1, entries[0].TargetIndex);
            Assert.AreEqual(0, entries[0].SourceIndex);     // owning track
            Assert.AreEqual(MaskType.Block, entries[0].Type);
            Assert.AreEqual(0.75f, entries[0].Amount, 1e-5f);
            Assert.AreEqual(0.2f, entries[0].Threshold, 1e-5f);
        }

        [Test]
        public void BuildMaskEntries_PreservesCustomTypeWithBlendFallback()
        {
            var mask = new KHR_character_expression_mask
            {
                Masks = new List<KHR_character_expression_mask.Mask>
                {
                    new KHR_character_expression_mask.Mask
                    {
                        Target = 1, Type = "soft_block", Amount = 0.5f
                    },
                }
            };

            var entries = KhrCharacterBaker.BuildMaskEntries(mask, sourceIndex: 0, WireToTrackIndex);

            Assert.AreEqual(1, entries.Length);
            Assert.AreEqual(MaskType.Blend, entries[0].Type,
                "application-defined mask types use blend as the runtime fallback");
            Assert.AreEqual("soft_block", entries[0].CustomType,
                "the application-defined vocabulary value must survive import and re-export");
        }

        [Test]
        public void BuildMappingSets_ResolvesSourceIndices()
        {
            var mapping = new KHR_character_expression_mapping();
            mapping.ExpressionSetMappings["vrm"] = new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
            {
                {
                    "happy", new List<KHR_character_expression_mapping.SourceWeight>
                    {
                        new KHR_character_expression_mapping.SourceWeight { Source = 0, Weight = 0.7f },
                        new KHR_character_expression_mapping.SourceWeight { Source = 1, Weight = 0.3f },
                        new KHR_character_expression_mapping.SourceWeight { Source = 99, Weight = 1f }, // dropped
                    }
                }
            };

            var sets = KhrCharacterBaker.BuildMappingSets(mapping, WireToTrackIndex);

            Assert.AreEqual(1, sets.Length);
            Assert.AreEqual("vrm", sets[0].SetName);
            Assert.AreEqual(1, sets[0].Targets.Length);
            Assert.AreEqual("happy", sets[0].Targets[0].TargetName);

            var contribs = sets[0].Targets[0].Contributions;
            Assert.AreEqual(2, contribs.Length);
            Assert.AreEqual(0, contribs[0].SourceIndex);
            Assert.AreEqual(0.7f, contribs[0].Weight, 1e-5f);
            Assert.AreEqual(1, contribs[1].SourceIndex);
            Assert.AreEqual(0.3f, contribs[1].Weight, 1e-5f);
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
            mapping.ExpressionSetMappings["vrm"] = new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
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
            mapping.ExpressionSetMappings["vrm"] = new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>
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
            mapping.ExpressionSetMappings["example"] =
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
