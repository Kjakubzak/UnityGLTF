using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;
using UnityGLTF.Plugins;

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

        [Test]
        public void CharacterDesignation_PreservesResolvedIndexAndOptionalTransform()
        {
            var host = new GameObject("host");
            var designated = new GameObject("designated");
            try
            {
                var character = host.AddComponent<KhrCharacter>();
                character.SetDesignation(7, designated.transform);

                Assert.AreEqual(7, character.DesignatedRootNodeIndex);
                Assert.AreSame(designated.transform, character.DesignatedRoot);

                character.SetDesignation(9, null);
                Assert.AreEqual(9, character.DesignatedRootNodeIndex,
                    "the glTF node index remains resolved even when the node is not instantiated in this scene");
                Assert.IsNull(character.DesignatedRoot);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(designated);
            }
        }

        [Test]
        public void RequiredExpressionFamily_IsBehaviorGatedAndConservativelyRejected()
        {
            var factories = new ExtensionFactory[]
            {
                new KHR_character_expression_Factory(),
                new KHR_character_expression_morphtarget_Factory(),
                new KHR_character_expression_joint_Factory(),
                new KHR_character_expression_texture_Factory(),
                new KHR_character_expression_mapping_Factory(),
                new KHR_character_expression_mask_Factory(),
            };
            var context = new KhrCharacterImportContext(null);

            foreach (var factory in factories)
            {
                Assert.IsTrue(factory.RequiresRuntimeSupportForRequiredUse,
                    $"{factory.ExtensionName} must not treat schema recognition as required-use support");
                Assert.IsFalse(context.SupportsRequiredExtension(factory.ExtensionName),
                    $"{factory.ExtensionName} is not yet implemented completely enough for required use");

                GLTFProperty.TryRegisterExtension(factory);
                var root = new GLTFRoot
                {
                    ExtensionsRequired = new List<string> { factory.ExtensionName },
                };
                Assert.Throws<GLTFLoadException>(() => GLTFSceneImporter.ValidateRequiredExtensionSupport(
                    root, new List<GLTFImportPluginContext> { context }),
                    $"{factory.ExtensionName} required use must be rejected until its complete behavior is integrated");
            }
        }

        [Test]
        public void RequiredAnimationPointer_IsNotSatisfiedBySchemaRecognition()
        {
            var factory = new KHR_animation_pointerExtensionFactory();
            var context = new AnimationPointerImportContext();

            Assert.IsTrue(factory.RequiresRuntimeSupportForRequiredUse);
            Assert.IsFalse(context.SupportsRequiredExtension(factory.ExtensionName));

            GLTFProperty.TryRegisterExtension(factory);
            var root = new GLTFRoot
            {
                ExtensionsRequired = new List<string> { factory.ExtensionName },
            };
            Assert.Throws<GLTFLoadException>(() => GLTFSceneImporter.ValidateRequiredExtensionSupport(
                root, new List<GLTFImportPluginContext> { context }));
        }

        [TestCase("https://example.com/expression-vocabularies/example/v1")]
        [TestCase("urn:example:expression:v1")]
        [TestCase("tag:example.com,2026:expression-v1")]
        public void MappingSetIdentifier_AcceptsWellFormedAbsoluteUris(string identifier)
        {
            Assert.IsTrue(KHR_character_expression_mapping.IsValidMappingSetIdentifier(identifier));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("relative/v1")]
        [TestCase("://missing-scheme")]
        [TestCase("https://exa mple.com/v1")]
        [TestCase(@"https:\example.com\v1")]
        public void MappingSetIdentifier_RejectsNonAbsoluteOrMalformedValues(string identifier)
        {
            Assert.IsFalse(KHR_character_expression_mapping.IsValidMappingSetIdentifier(identifier));
        }
    }
}
