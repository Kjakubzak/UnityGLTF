using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden-value tests for resolving expression names to track indices when baking masks and mappings.
    /// </summary>
    public class KhrCharacterBakerMaskMappingTests
    {
        private static readonly Dictionary<string, int> NameToIndex = new Dictionary<string, int> { { "a", 0 }, { "b", 1 } };

        [Test]
        public void BuildMaskEntries_ResolvesTargetAndSource()
        {
            var mask = new KHR_character_expression_mask
            {
                Masks = new List<KHR_character_expression_mask.Mask>
                {
                    new KHR_character_expression_mask.Mask { Target = "b", Type = "block", Amount = 0.75f, Threshold = 0.2f },
                    new KHR_character_expression_mask.Mask { Target = "missing", Type = "blend", Amount = 1f }, // dropped
                }
            };

            var entries = KhrCharacterBaker.BuildMaskEntries(mask, sourceIndex: 0, NameToIndex);

            Assert.AreEqual(1, entries.Length);
            Assert.AreEqual(1, entries[0].TargetIndex);     // "b"
            Assert.AreEqual(0, entries[0].SourceIndex);     // owning track
            Assert.AreEqual(MaskType.Block, entries[0].Type);
            Assert.AreEqual(0.75f, entries[0].Amount, 1e-5f);
            Assert.AreEqual(0.2f, entries[0].Threshold, 1e-5f);
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
                        new KHR_character_expression_mapping.SourceWeight { Source = "a", Weight = 0.7f },
                        new KHR_character_expression_mapping.SourceWeight { Source = "b", Weight = 0.3f },
                        new KHR_character_expression_mapping.SourceWeight { Source = "missing", Weight = 1f }, // dropped
                    }
                }
            };

            var sets = KhrCharacterBaker.BuildMappingSets(mapping, NameToIndex);

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
    }
}
