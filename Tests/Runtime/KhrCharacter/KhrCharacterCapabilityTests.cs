using System.Collections.Generic;
using NUnit.Framework;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Tests that capabilities are derived from the baked set (sub-domains are nested in expression items and
    /// are not visible in the root/node presence scan).
    /// </summary>
    public class KhrCharacterCapabilityTests
    {
        [Test]
        public void DeriveCapabilities_FromBakedSet_ReportsSubDomains()
        {
            var present = new HashSet<string>
            {
                KhrCharacterExtensionNames.Character,
                KhrCharacterExtensionNames.Expression,
            };
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "a", Domains = ExpressionDomain.Morph | ExpressionDomain.Joint },
                    new ExpressionTrack { Name = "b", Domains = ExpressionDomain.Texture, Masks = new[] { new MaskEntry { TargetIndex = 0 } } },
                },
                MappingSets = new[] { new ExpressionMappingSet { SetName = "vrm" } },
            };

            var caps = KhrCharacterImportContext.DeriveCapabilities(present, set);

            CollectionAssert.Contains(caps, CharacterCapability.Character);
            CollectionAssert.Contains(caps, CharacterCapability.Expression);
            CollectionAssert.Contains(caps, CharacterCapability.Morphtarget);
            CollectionAssert.Contains(caps, CharacterCapability.Joint);
            CollectionAssert.Contains(caps, CharacterCapability.Texture);
            CollectionAssert.Contains(caps, CharacterCapability.Mask);
            CollectionAssert.Contains(caps, CharacterCapability.Mapping);
        }

        [Test]
        public void DeriveCapabilities_NoSet_ReportsOnlyPresentRootExtensions()
        {
            var present = new HashSet<string> { KhrCharacterExtensionNames.Character };

            var caps = KhrCharacterImportContext.DeriveCapabilities(present, null);

            CollectionAssert.Contains(caps, CharacterCapability.Character);
            CollectionAssert.DoesNotContain(caps, CharacterCapability.Expression);
            CollectionAssert.DoesNotContain(caps, CharacterCapability.Morphtarget);
            CollectionAssert.DoesNotContain(caps, CharacterCapability.Mapping);
        }
    }
}
