using System.Collections.Generic;
using System.Text.RegularExpressions;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
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
                    new ExpressionTrack { Name = "b", Domains = ExpressionDomain.Texture, Suppressions = new[] { new SuppressionEntry { TargetIndex = 0 } } },
                },
                MappingSets = new[] { new ExpressionMappingSet { SetName = "vrm" } },
            };

            var caps = KhrCharacterImportContext.DeriveCapabilities(present, set);

            CollectionAssert.Contains(caps, CharacterCapability.Character);
            CollectionAssert.Contains(caps, CharacterCapability.Expression);
            CollectionAssert.Contains(caps, CharacterCapability.Morphtarget);
            CollectionAssert.Contains(caps, CharacterCapability.Joint);
            CollectionAssert.Contains(caps, CharacterCapability.TextureTransform);
            CollectionAssert.Contains(caps, CharacterCapability.Suppression);
            CollectionAssert.Contains(caps, CharacterCapability.Mapping);
        }

        [Test]
        public void CharacterCapability_RenamedMembersKeepTheirPersistedValues()
        {
            Assert.AreEqual(4, (int)CharacterCapability.TextureTransform, "serialized capability lists store integers");
            Assert.AreEqual(6, (int)CharacterCapability.Suppression, "serialized capability lists store integers");
            Assert.AreEqual(10, (int)CharacterCapability.LookAtTarget);
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
        public void CharacterDesignation_IsAnAssetLevelFlagWithoutNodeBinding()
        {
            var root = new GLTFRoot
            {
                Extensions = new Dictionary<string, IExtension>
                {
                    { KhrCharacterExtensionNames.Character, new KHR_character() },
                },
            };
            var context = new KhrCharacterImportContext(null);
            var sceneRoot = new GameObject("scene");
            try
            {
                context.OnAfterImportRoot(root);
                context.OnAfterImportScene(null, 0, sceneRoot);

                var character = sceneRoot.GetComponent<KhrCharacter>();
                Assert.IsNotNull(character, "the designation attaches the hub to the imported scene root");
                Assert.IsTrue(character.Has(CharacterCapability.Character),
                    "the hub exposes the designation without resolving any node");
            }
            finally
            {
                Object.DestroyImmediate(sceneRoot);
            }
        }

        [Test]
        public void NodeLevelCharacter_WarnsAndAddsNoCapability()
        {
            var node = new Node
            {
                Name = "misplaced",
                Extensions = new Dictionary<string, IExtension>
                {
                    { KhrCharacterExtensionNames.Character, new KHR_character() },
                },
            };
            var root = new GLTFRoot { Nodes = new List<Node> { node } };
            var context = new KhrCharacterImportContext(null);
            var nodeObject = new GameObject("misplaced");
            var sceneRoot = new GameObject("scene");
            try
            {
                context.OnAfterImportRoot(root);
                LogAssert.Expect(LogType.Warning,
                    new Regex("Node 0 carries a KHR_character object, which is valid only on the top-level glTF object"));
                context.OnAfterImportNode(node, 0, nodeObject);
                context.OnAfterImportScene(null, 0, sceneRoot);

                Assert.IsNull(sceneRoot.GetComponent<KhrCharacter>(),
                    "a node-level KHR_character is not a designation");
                Assert.IsNull(nodeObject.GetComponent<KhrCharacter>());
            }
            finally
            {
                Object.DestroyImmediate(nodeObject);
                Object.DestroyImmediate(sceneRoot);
            }
        }

        [Test]
        public void RequiredCharacter_IsRejectedWithoutThePluginAndAcceptedWithIt()
        {
            var factory = new KHR_character_Factory();
            Assert.IsTrue(factory.RequiresRuntimeSupportForRequiredUse,
                "schema recognition alone does not expose the designation to host logic");
            GLTFProperty.TryRegisterExtension(factory);
            var root = new GLTFRoot
            {
                ExtensionsRequired = new List<string> { KhrCharacterExtensionNames.Character },
            };

            Assert.Throws<GLTFLoadException>(() => GLTFSceneImporter.ValidateRequiredExtensionSupport(root, null));
            Assert.Throws<GLTFLoadException>(() => GLTFSceneImporter.ValidateRequiredExtensionSupport(
                root, new List<GLTFImportPluginContext>()));
            Assert.IsTrue(new KhrCharacterImportContext(null).SupportsRequiredExtension(KhrCharacterExtensionNames.Character));
            Assert.DoesNotThrow(() => GLTFSceneImporter.ValidateRequiredExtensionSupport(
                root, new List<GLTFImportPluginContext> { new KhrCharacterImportContext(null) }));
        }

        [Test]
        public void RequiredExpressionFamily_IsBehaviorGatedAndConservativelyRejected()
        {
            var factories = new ExtensionFactory[]
            {
                new KHR_character_expression_Factory(),
                new KHR_character_expression_morphtarget_Factory(),
                new KHR_character_expression_joint_Factory(),
                new KHR_character_expression_texture_transform_Factory(),
                new KHR_character_expression_mapping_Factory(),
                new KHR_character_expression_suppression_Factory(),
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

        [TestCase("vrm")]
        [TestCase("relative/v1")]
        [TestCase("https://example.com/expression-vocabularies/example/v1")]
        [TestCase("urn:example:expression:v1")]
        [TestCase("https://exa mple.com/v1")]
        public void MappingSetIdentifier_AcceptsAnyNonemptyString(string identifier)
        {
            Assert.IsTrue(KHR_character_expression_mapping.IsValidMappingSetIdentifier(identifier));
        }

        [TestCase(null)]
        [TestCase("")]
        public void MappingSetIdentifier_RejectsOnlyNullAndEmpty(string identifier)
        {
            Assert.IsFalse(KHR_character_expression_mapping.IsValidMappingSetIdentifier(identifier));
        }
    }
}
