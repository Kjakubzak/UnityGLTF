using System;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityGLTF.Extensions;

namespace UnityGLTF.KhrCharacter.Tests
{
    public class KhrCharacterResponseBakerTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var gameObject in _created)
                if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
            _created.Clear();
        }

        [Test]
        public void CoreTranslationBakesAbsoluteWireResponse()
        {
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 2f },
                0d,
                2d);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new[] { 0f, 0f, 0f, 2f, 4f, 6f });
            var copiedDefaultNode = new Node(new Node(), fixture.Root);
            Assert.IsFalse(copiedDefaultNode.HasMatrix);
            fixture.Root.Nodes.Add(copiedDefaultNode);
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });

            var entries = fixture.Bake("move");
            var response = ExpressionResponseEvaluator.Evaluate(entries[0].Animation, 0.5f);

            Assert.That(entries, Has.Length.EqualTo(1));
            Assert.That(entries[0].Name, Is.EqualTo("move"));
            Assert.That(response.Duration, Is.EqualTo(2f));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(1f).Within(1e-6f));
            Assert.That(response.Records[0].Value[1], Is.EqualTo(2f).Within(1e-6f));
            Assert.That(response.Records[0].Value[2], Is.EqualTo(3f).Within(1e-6f));
        }

        [Test]
        public void EntryWithOnlyLegacyExpressionLabelIsRejectedForMissingName()
        {
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(GLTFAccessorAttributeType.SCALAR, 2, new[] { 0f, 1f }, 0d, 1d);
            int output = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC3, 2, new[] { 0f, 0f, 0f, 1f, 1f, 1f });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });
            var extension = new KHR_character_expression_Factory().Deserialize(
                fixture.Root,
                new Newtonsoft.Json.Linq.JProperty(KHR_character_expression.EXTENSION_NAME, new Newtonsoft.Json.Linq.JObject
                {
                    { "expressions", new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject
                        {
                            { "expression", "legacy" },
                            { "animation", 0 },
                        })
                    },
                })) as KHR_character_expression;

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(() => fixture.Bake(extension));
            StringAssert.Contains("Expression entry 0 has no nonempty name", exception.Message);
        }

        [Test]
        public void PackedMorphWeightsUseMeshNeutralAndRemainOneArrayRecord()
        {
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f },
                0d,
                1d);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                4,
                new[] { 0.1f, 0.2f, 0.8f, 0.6f });
            var mesh = new GLTFMesh
            {
                Weights = new List<double> { 0.1d, 0.2d },
                Primitives = new List<MeshPrimitive>
                {
                    new MeshPrimitive
                    {
                        Targets = new List<Dictionary<string, AccessorId>>
                        {
                            new Dictionary<string, AccessorId>(),
                            new Dictionary<string, AccessorId>(),
                        },
                    },
                },
            };
            fixture.Root.Meshes.Add(mesh);
            fixture.Root.Nodes.Add(new Node
            {
                Mesh = new MeshId { Id = 0, Root = fixture.Root },
            });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "weights") });

            var response = ExpressionResponseEvaluator.Evaluate(fixture.Bake("morph")[0].Animation, 0.5f);

            Assert.That(response.Records.Count, Is.EqualTo(1));
            Assert.That(response.Records[0].Value.Count, Is.EqualTo(2));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(0.45f).Within(1e-6f));
            Assert.That(response.Records[0].Value[1], Is.EqualTo(0.4f).Within(1e-6f));
        }

        [Test]
        public void MorphTimeZeroMustMatchNodeOrMeshNeutralWeight()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f });
            fixture.Root.Meshes.Add(new GLTFMesh
            {
                Weights = new List<double> { 0.25d },
                Primitives = new List<MeshPrimitive>
                {
                    new MeshPrimitive
                    {
                        Targets = new List<Dictionary<string, AccessorId>>
                        {
                            new Dictionary<string, AccessorId>(),
                        },
                    },
                },
            });
            fixture.Root.Nodes.Add(new Node
            {
                Mesh = new MeshId { Id = 0, Root = fixture.Root },
            });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "weights") });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("invalid-neutral"));
            StringAssert.Contains("authored initial value", exception.Message);
        }

        [Test]
        public void SparseInputAndOutputAreAppliedBeforeValidationAndSampling()
        {
            var fixture = new Fixture();
            int input = fixture.AddSparseFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                1,
                new[] { 1f },
                0d,
                1d);
            int output = fixture.AddSparseFloatAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                1,
                new[] { 2f, 4f, 6f });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });

            var response = ExpressionResponseEvaluator.Evaluate(fixture.Bake("sparse")[0].Animation, 1f);

            Assert.That(response.Duration, Is.EqualTo(1f));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(2f));
            Assert.That(response.Records[0].Value[1], Is.EqualTo(4f));
            Assert.That(response.Records[0].Value[2], Is.EqualTo(6f));
        }

        [Test]
        public void OversizedAccessorLayoutFailsAsExpressionValidationError()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new[] { 0f, 0f, 0f, 1f, 2f, 3f });
            fixture.Root.Accessors[output].ByteOffset = uint.MaxValue;
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("oversized-layout"));
            StringAssert.Contains("supported range", exception.Message);
        }

        [Test]
        public void UnsupportedOptionalOutputIsNotDecodedButItsInputSetsDuration()
        {
            var fixture = new Fixture();
            int supportedInput = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f },
                0d,
                1d);
            int supportedOutput = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new[] { 0f, 0f, 0f, 1f, 2f, 3f });
            int unsupportedInput = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 4f },
                0d,
                4d);
            int unsupportedOutput = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[]
                {
                    new SamplerSpec(supportedInput, supportedOutput),
                    new SamplerSpec(unsupportedInput, unsupportedOutput),
                },
                new[]
                {
                    CoreChannel(0, fixture.Root, 0, "translation"),
                    PointerChannel(1, fixture.Root, "/materials/0/extensions/VENDOR/a~1b~0c"),
                });

            var response = ExpressionResponseEvaluator.Evaluate(fixture.Bake("mixed")[0].Animation, 0.5f);

            Assert.That(response.Duration, Is.EqualTo(4f));
            Assert.That(response.SampleTime, Is.EqualTo(2f));
            Assert.That(response.Records.Count, Is.EqualTo(1));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(1f));
        }

        [Test]
        public void UnsupportedOptionalChannelStillRejectsInvalidReferencedInput()
        {
            var fixture = new Fixture();
            int invalidInput = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                1,
                new[] { 0f },
                0d,
                0d);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f });
            fixture.AddAnimation(
                new[] { new SamplerSpec(invalidInput, output) },
                new[] { PointerChannel(0, fixture.Root, "/materials/0/extensions/VENDOR/value") });

            Assert.Throws<ExpressionResponseEvaluationException>(() => fixture.Bake("invalid"));
        }

        [Test]
        public void UnsupportedOptionalChannelStillRejectsInvalidReferencedOutput()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, 999) },
                new[] { PointerChannel(0, fixture.Root, "/materials/0/extensions/VENDOR/value") });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("invalid-output"));
            StringAssert.Contains("output accessor reference is invalid", exception.Message);
        }

        [Test]
        public void OpaqueUnsupportedNumericPointerDoesNotImplyArrayElementOverlap()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f });
            fixture.AddAnimation(
                new[]
                {
                    new SamplerSpec(input, output),
                    new SamplerSpec(input, output),
                },
                new[]
                {
                    PointerChannel(0, fixture.Root, "/extensions/VENDOR/property"),
                    PointerChannel(1, fixture.Root, "/extensions/VENDOR/property/0"),
                });

            var response = ExpressionResponseEvaluator.Evaluate(fixture.Bake("opaque-pointers")[0].Animation, 1f);

            Assert.That(response.Duration, Is.EqualTo(1f));
            Assert.That(response.Records, Is.Empty);
        }

        [Test]
        public void InputMetadataMustMatchDecodedExtrema()
        {
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f },
                0d,
                2d);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new[] { 0f, 0f, 0f, 1f, 1f, 1f });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(() => fixture.Bake("metadata"));
            StringAssert.Contains("min/max metadata", exception.Message);
        }

        [Test]
        public void InputMetadataIsComparedAtAccessorFloatPrecision()
        {
            const float decodedEnd = 0.4166666567325592f;
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, decodedEnd },
                0d,
                0.4166666666666667d);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new[] { 0f, 0f, 0f, 1f, 2f, 3f });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });

            var response = ExpressionResponseEvaluator.Evaluate(fixture.Bake("metadata-float")[0].Animation, 1f);

            Assert.That(response.Duration, Is.EqualTo(decodedEnd));
        }

        [Test]
        public void MatrixBackedNodeCannotBeTargetedByTrsChannel()
        {
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f },
                0d,
                1d);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new[] { 0f, 0f, 0f, 1f, 1f, 1f });
            fixture.Root.Nodes.Add(new Node
            {
                Matrix = new GLTF.Math.Matrix4x4(GLTF.Math.Matrix4x4.Identity),
                HasMatrix = true,
            });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(() => fixture.Bake("matrix"));
            StringAssert.Contains("matrix-backed", exception.Message);
        }

        [Test]
        public void MatrixBackedNodeTranslationCannotBeTargetedByAnimationPointer()
        {
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f },
                0d,
                1d);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new[] { 2f, 3f, 4f, 5f, 6f, 7f });
            fixture.Root.Nodes.Add(new Node
            {
                Matrix = new GLTF.Math.Matrix4x4(
                    1f, 0f, 0f, 0f,
                    0f, 1f, 0f, 0f,
                    0f, 0f, 1f, 0f,
                    2f, 3f, 4f, 1f),
                HasMatrix = true,
            });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { PointerChannel(0, fixture.Root, "/nodes/0/translation") });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("matrix-pointer"),
                "an expression channel must not target the translation of a matrix node, even through a pointer");
            StringAssert.Contains("matrix-backed", exception.Message);
        }

        [Test]
        public void CoreWeightsChannelOnMatrixNodeIsInvalidButPointerWeightsAreAllowed()
        {
            foreach (bool pointerForm in new[] { false, true })
            {
                var fixture = new Fixture();
                int input = AddTwoKeyInput(fixture);
                int output = fixture.AddFloatAccessor(GLTFAccessorAttributeType.SCALAR, 2, new[] { 0f, 1f });
                AddMorphMesh(fixture, null, 1);
                fixture.Root.Nodes.Add(new Node
                {
                    Mesh = new MeshId { Id = 0, Root = fixture.Root },
                    Matrix = new GLTF.Math.Matrix4x4(GLTF.Math.Matrix4x4.Identity),
                    HasMatrix = true,
                });
                fixture.AddAnimation(
                    new[] { new SamplerSpec(input, output) },
                    new[]
                    {
                        pointerForm
                            ? PointerChannel(0, fixture.Root, "/nodes/0/weights")
                            : CoreChannel(0, fixture.Root, 0, "weights"),
                    });

                if (pointerForm)
                {
                    Assert.DoesNotThrow(() => fixture.Bake("pointer-weights"),
                        "core glTF forbids matrix only on nodes a core channel targets");
                }
                else
                {
                    var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                        () => fixture.Bake("core-weights"));
                    StringAssert.Contains("matrix-backed", exception.Message);
                }
            }
        }

        [Test]
        public void UnlistedChannelExtendsDurationAndEmitsNothing()
        {
            var fixture = new Fixture();
            int shortInput = AddTwoKeyInput(fixture);
            int longInput = fixture.AddFloatAccessor(GLTFAccessorAttributeType.SCALAR, 2, new[] { 0f, 4f }, 0d, 4d);
            int move = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC3, 2, new[] { 0f, 0f, 0f, 1f, 2f, 3f });
            int grow = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC3, 2, new[] { 1f, 1f, 1f, 2f, 2f, 2f });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(shortInput, move), new SamplerSpec(longInput, grow) },
                new[]
                {
                    CoreChannel(0, fixture.Root, 0, "translation"),
                    CoreChannel(1, fixture.Root, 0, "scale"),
                });

            var entries = fixture.Bake(Entry("move", 0, joint: new[] { 0 }));
            var unlisted = entries[0].Animation.Channels[1];
            var response = ExpressionResponseEvaluator.Evaluate(entries[0].Animation, 0.5f);

            Assert.That(response.Duration, Is.EqualTo(4f), "the unlisted channel still counts toward T");
            Assert.That(response.Records.Count, Is.EqualTo(1));
            Assert.That(response.Records[0].ChannelIndex, Is.EqualTo(0));
            Assert.That(entries[0].Animation.Channels[0].ListedBy, Is.EqualTo(KhrCharacterExtensionNames.ExpressionJoint));
            Assert.IsNull(unlisted.ListedBy);
            Assert.IsFalse(unlisted.EmitsRecords);
            Assert.IsTrue(unlisted.Target.IsSupported, "an unlisted channel is still decoded and validated");
        }

        [Test]
        public void UnlistedChannelWithWrongTimeZeroValueIsStillRejected()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int move = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC3, 2, new[] { 0f, 0f, 0f, 1f, 2f, 3f });
            int grow = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC3, 2, new[] { 2f, 2f, 2f, 3f, 3f, 3f });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, move), new SamplerSpec(input, grow) },
                new[]
                {
                    CoreChannel(0, fixture.Root, 0, "translation"),
                    CoreChannel(1, fixture.Root, 0, "scale"),
                });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake(Entry("move", 0, joint: new[] { 0 })));
            StringAssert.Contains("Channel 1 time-zero sample", exception.Message);
        }

        [Test]
        public void ChannelListedOnlyByUnknownExtensionEmitsNothingAndMakesValidationIncomplete()
        {
            var fixture = TranslationFixture();
            var item = Entry("vendor", 0);
            item.RawExtensions = new JObject { { "ACME_x", new JObject { { "channels", new JArray(0) } } } };

            var entries = fixture.Bake(new[] { item }, null, out var validation);

            Assert.IsFalse(validation.IsComplete, "an unrecognized entry extension is not a rejection");
            StringAssert.Contains("ACME_x", string.Join(" ", validation.IncompleteReasons));
            Assert.IsNull(entries[0].Animation.Channels[0].ListedBy);
            Assert.That(ExpressionResponseEvaluator.Evaluate(entries[0].Animation, 1f).Records, Is.Empty);
        }

        [Test]
        public void ChannelListedByTwoDerivedExtensionsIsInvalid()
        {
            var fixture = TranslationFixture();

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake(Entry("twice", 0, joint: new[] { 0 }, morph: new[] { 0 })));
            StringAssert.Contains("listed by both", exception.Message);
        }

        [Test]
        public void ListingOutsideTheAnimationOrAgainstItsClassificationIsInvalid()
        {
            var fixture = TranslationFixture();
            var outside = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake(Entry("outside", 0, joint: new[] { 3 })));
            StringAssert.Contains("lists channel 3", outside.Message);

            var misclassified = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake(Entry("misclassified", 0, morph: new[] { 0 })));
            StringAssert.Contains("does not target node morph weights", misclassified.Message);
        }

        [Test]
        public void TranslationOverlapAcrossEntriesIsAllowed()
        {
            var fixture = TranslationFixture();

            var entries = fixture.Bake(
                new[] { Entry("first", 0, joint: new[] { 0 }), Entry("second", 0, joint: new[] { 0 }) },
                null,
                out var validation);

            Assert.That(entries, Has.Length.EqualTo(2));
            Assert.IsTrue(validation.IsComplete);
        }

        [Test]
        public void CoreRotationOnOneEntryAgainstPointerRotationOnAnotherIsInvalid()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            float half = (float)Math.Sqrt(0.5d);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.VEC4,
                2,
                new[] { 0f, 0f, 0f, 1f, 0f, half, 0f, half });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "rotation") });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { PointerChannel(0, fixture.Root, "/nodes/0/rotation") });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake(Entry("lookLeft", 0, joint: new[] { 0 }), Entry("lookRight", 1, joint: new[] { 0 })));
            StringAssert.Contains("'lookLeft'", exception.Message);
            StringAssert.Contains("'lookRight'", exception.Message);
            StringAssert.Contains("/nodes/0/rotation", exception.Message);
        }

        [Test]
        public void OverlapOnVendorDeclaredSingleWriterPropertyIsInvalid()
        {
            var fixture = TranslationFixture();
            var registry = new ExpressionDerivedExtensionRegistry();
            registry.Register(
                "ACME_translation_lock",
                false,
                isSupported: true,
                declaresSingleWriter: target => target.Kind == ExpressionPropertyKind.NodeTranslation);
            var first = Entry("first", 0, joint: new[] { 0 });
            first.RawExtensions = new JObject { { "ACME_translation_lock", new JObject() } };

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake(
                    new[] { first, Entry("second", 0, joint: new[] { 0 }) },
                    new ExpressionResponseBakeOptions { Registry = registry },
                    out _));
            StringAssert.Contains("single-writer", exception.Message);
        }

        [Test]
        public void UnsupportedDerivedExtensionDeclarationsOnlyMakeConflictValidationIncomplete()
        {
            var fixture = TranslationFixture();
            var registry = new ExpressionDerivedExtensionRegistry();
            registry.Register(
                "ACME_locked_motion",
                true,
                ExpressionChannelClassification.NodeTrs,
                false,
                target => target.Kind == ExpressionPropertyKind.NodeTranslation);
            var vendor = Entry("vendor", 0);
            vendor.RawExtensions = new JObject
            {
                { "ACME_locked_motion", new JObject { { "channels", new JArray(0) } } },
            };

            var entries = fixture.Bake(
                new[] { vendor, Entry("joint", 0, joint: new[] { 0 }) },
                new ExpressionResponseBakeOptions { Registry = registry },
                out var validation);

            Assert.IsFalse(validation.IsComplete);
            StringAssert.Contains("ACME_locked_motion", string.Join(" ", validation.IncompleteReasons));
            Assert.That(entries[0].Animation.Channels[0].ListedBy, Is.EqualTo("ACME_locked_motion"));
            Assert.IsFalse(entries[0].Animation.Channels[0].EmitsRecords,
                "a channel listed only by an unsupported derived extension produces no record");
            Assert.IsTrue(entries[1].Animation.Channels[0].EmitsRecords);
        }

        [Test]
        public void TextureTransformPointersResolveWithTheirDefaults()
        {
            var fixture = new Fixture();
            fixture.AddTexturedMaterial(new ExtTextureTransformExtension(
                new GLTF.Math.Vector2(0.25f, 0.5f),
                0.1d,
                new GLTF.Math.Vector2(1f, 1f),
                null));
            const string basePath = "/materials/0/pbrMetallicRoughness/baseColorTexture/extensions/KHR_texture_transform/";
            int input = AddTwoKeyInput(fixture);
            int offset = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC2, 2, new[] { 0.25f, 0.5f, 0.75f, 1f });
            int scale = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC2, 2, new[] { 1f, 1f, 2f, 3f });
            int rotation = fixture.AddFloatAccessor(GLTFAccessorAttributeType.SCALAR, 2, new[] { 0.1f, 1f });
            fixture.AddAnimation(
                new[]
                {
                    new SamplerSpec(input, offset),
                    new SamplerSpec(input, scale),
                    new SamplerSpec(input, rotation),
                },
                new[]
                {
                    PointerChannel(0, fixture.Root, basePath + "offset"),
                    PointerChannel(1, fixture.Root, basePath + "scale"),
                    PointerChannel(2, fixture.Root, basePath + "rotation"),
                });

            var entries = fixture.Bake("texture");
            var channels = entries[0].Animation.Channels;
            var response = ExpressionResponseEvaluator.Evaluate(entries[0].Animation, 1f);

            Assert.That(channels[0].Target.Kind, Is.EqualTo(ExpressionPropertyKind.TextureOffset));
            Assert.That(channels[0].Target.AuthoredInitial, Is.EqualTo(new[] { 0.25d, 0.5d }));
            Assert.That(channels[1].Target.Kind, Is.EqualTo(ExpressionPropertyKind.TextureScale));
            Assert.That(channels[1].Target.AuthoredInitial, Is.EqualTo(new[] { 1d, 1d }), "an omitted scale is [1, 1]");
            Assert.That(channels[2].Target.Kind, Is.EqualTo(ExpressionPropertyKind.TextureRotation));
            Assert.That(channels[2].Target.AuthoredInitial[0], Is.EqualTo(0.1d), "rotation keeps the JSON double");
            foreach (var channel in channels)
            {
                Assert.That(channel.ListedBy, Is.EqualTo(KhrCharacterExtensionNames.ExpressionTextureTransform));
                Assert.IsTrue(channel.EmitsRecords);
            }
            Assert.That(response.Records.Count, Is.EqualTo(3));
            Assert.That(response.Records[1].Value, Is.EqualTo(new[] { 2f, 3f }));
        }

        [Test]
        public void TextureRotationIsSampledWithoutWrapping()
        {
            var fixture = new Fixture();
            fixture.AddTexturedMaterial(new ExtTextureTransformExtension(
                new GLTF.Math.Vector2(0f, 0f), 0d, new GLTF.Math.Vector2(1f, 1f), null));
            int input = AddTwoKeyInput(fixture);
            int rotation = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, (float)(6d * Math.PI) });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, rotation) },
                new[]
                {
                    PointerChannel(
                        0,
                        fixture.Root,
                        "/materials/0/pbrMetallicRoughness/baseColorTexture/extensions/KHR_texture_transform/rotation"),
                });

            var response = ExpressionResponseEvaluator.Evaluate(fixture.Bake("spin")[0].Animation, 0.5f);

            Assert.That(response.Records[0].Value[0], Is.EqualTo((float)(3d * Math.PI)).Within(1e-5f));
        }

        [Test]
        public void PointerToTextureInfoWithoutTextureTransformIsInvalid()
        {
            var fixture = new Fixture();
            fixture.AddTexturedMaterial(null);
            int input = AddTwoKeyInput(fixture);
            int offset = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC2, 2, new[] { 0f, 0f, 1f, 0f });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, offset) },
                new[]
                {
                    PointerChannel(
                        0,
                        fixture.Root,
                        "/materials/0/pbrMetallicRoughness/baseColorTexture/extensions/KHR_texture_transform/offset"),
                });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(() => fixture.Bake("scroll"));
            StringAssert.Contains("without an authored KHR_texture_transform", exception.Message);
        }

        [Test]
        public void TexturePointerThroughUnsupportedMaterialExtensionIsIncomplete()
        {
            var fixture = new Fixture();
            fixture.AddTexturedMaterial(null);
            int input = AddTwoKeyInput(fixture);
            int offset = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC2, 2, new[] { 0f, 0f, 1f, 0f });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, offset) },
                new[]
                {
                    PointerChannel(
                        0,
                        fixture.Root,
                        "/materials/0/extensions/ACME_material/detailTexture/extensions/KHR_texture_transform/offset"),
                });

            var entries = fixture.Bake(new[] { Entry("detail", 0, texture: new[] { 0 }) }, null, out var validation);

            Assert.IsFalse(validation.IsComplete);
            Assert.IsFalse(entries[0].Animation.Channels[0].Target.IsSupported);
            Assert.That(ExpressionResponseEvaluator.Evaluate(entries[0].Animation, 1f).Records, Is.Empty);
        }

        [TestCase("KHR_meshopt_compression", false)]
        [TestCase("EXT_meshopt_compression", false)]
        [TestCase("EXT_meshopt_compression", true)]
        public void CompressedOutputIsDecodedOnlyByAnActiveDecoder(string extension, bool decoderActive)
        {
            var fixture = TranslationFixture();
            int outputView = fixture.Root.Accessors[1].BufferView.Id;
            fixture.Root.BufferViews[outputView].Extensions = new Dictionary<string, IExtension>
            {
                { extension, new DefaultExtensionFactory().Deserialize(fixture.Root, new JProperty(extension, new JObject())) },
            };

            var entries = fixture.Bake(
                new[] { Entry("compressed", 0, joint: new[] { 0 }) },
                new ExpressionResponseBakeOptions { MeshoptDecoderActive = decoderActive },
                out var validation);
            var response = ExpressionResponseEvaluator.Evaluate(entries[0].Animation, 1f);

            bool decodable = decoderActive && extension == "EXT_meshopt_compression";
            Assert.That(validation.IsComplete, Is.EqualTo(decodable));
            Assert.That(entries[0].Animation.Channels[0].Target.IsSupported, Is.EqualTo(decodable));
            Assert.That(response.Records.Count, Is.EqualTo(decodable ? 1 : 0),
                "fallback bufferView data never produces records");
            Assert.That(response.Duration, Is.EqualTo(1f));
        }

        [Test]
        public void CompressedInputMakesTheEntryUnevaluable()
        {
            var fixture = TranslationFixture();
            int inputView = fixture.Root.Accessors[0].BufferView.Id;
            fixture.Root.BufferViews[inputView].Extensions = new Dictionary<string, IExtension>
            {
                {
                    "KHR_meshopt_compression",
                    new DefaultExtensionFactory().Deserialize(
                        fixture.Root, new JProperty("KHR_meshopt_compression", new JObject()))
                },
            };

            var entries = fixture.Bake(new[] { Entry("compressed", 0, joint: new[] { 0 }) }, null, out var validation);
            var response = ExpressionResponseEvaluator.Evaluate(entries[0].Animation, 1f);

            Assert.IsFalse(validation.IsComplete);
            Assert.IsFalse(entries[0].Animation.IsEvaluable);
            Assert.IsFalse(response.IsEvaluable);
            Assert.That(response.Records, Is.Empty);
        }

        [Test]
        public void MorphWeightsKeepTheirAuthoredJsonDoubles()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int output = fixture.AddFloatAccessor(GLTFAccessorAttributeType.SCALAR, 2, new[] { 0.1f, 1f });
            AddMorphMesh(fixture, new List<double> { 0.1d }, 1);
            fixture.Root.Nodes.Add(new Node { Mesh = new MeshId { Id = 0, Root = fixture.Root } });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "weights") });

            var target = fixture.Bake("weights")[0].Animation.Channels[0].Target;

            Assert.That(target.Kind, Is.EqualTo(ExpressionPropertyKind.NodeWeights));
            Assert.That(target.ArrayLength, Is.EqualTo(1));
            Assert.That(target.AuthoredInitial[0], Is.EqualTo(0.1d), "b is the JSON double, not the float32 0.1");
        }

        [TestCase("rotation", 4)]
        [TestCase("scale", 3)]
        public void MatrixBackedNodeRotationAndScalePointersAreUndefined(string path, int componentCount)
        {
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f },
                0d,
                1d);
            int output = fixture.AddFloatAccessor(
                componentCount == 4 ? GLTFAccessorAttributeType.VEC4 : GLTFAccessorAttributeType.VEC3,
                2,
                new float[componentCount * 2]);
            fixture.Root.Nodes.Add(new Node
            {
                Matrix = new GLTF.Math.Matrix4x4(GLTF.Math.Matrix4x4.Identity),
                HasMatrix = true,
            });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { PointerChannel(0, fixture.Root, $"/nodes/0/{path}") });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("undefined-matrix-pointer"));
            StringAssert.Contains("matrix-backed", exception.Message);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("nodes/0/translation")]
        [TestCase("/nodes/0/trans~2lation")]
        [TestCase("/nodes/0/translation~")]
        public void AnimationPointerMustBeNonemptyAndRfc6901WellFormed(string pointer)
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, 999) },
                new[] { PointerChannel(0, fixture.Root, pointer) });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("malformed-pointer"));
            StringAssert.Contains("malformed JSON pointer", exception.Message);
        }

        [Test]
        public void AnimationPointerRequiresPointerTargetPath()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            fixture.Root.Nodes.Add(new Node());
            var channel = PointerChannel(0, fixture.Root, "/nodes/0/translation");
            channel.Target.Path = "translation";
            fixture.AddAnimation(new[] { new SamplerSpec(input, 999) }, new[] { channel });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("pointer-path"));
            StringAssert.Contains("path 'pointer'", exception.Message);
        }

        [Test]
        public void AnimationPointerForbidsNodeTarget()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            fixture.Root.Nodes.Add(new Node());
            var channel = PointerChannel(0, fixture.Root, "/nodes/0/translation");
            channel.Target.Node = new NodeId { Id = 0, Root = fixture.Root };
            fixture.AddAnimation(new[] { new SamplerSpec(input, 999) }, new[] { channel });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("pointer-node"));
            StringAssert.Contains("with a node target", exception.Message);
        }

        [Test]
        public void PointerTargetPathRequiresAnimationPointerExtension()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, 999) },
                new[]
                {
                    new AnimationChannel
                    {
                        Sampler = new AnimationSamplerId { Id = 0, Root = fixture.Root },
                        Target = new AnimationChannelTarget { Path = "pointer" },
                    },
                });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("missing-pointer-extension"));
            StringAssert.Contains("without KHR_animation_pointer", exception.Message);
        }

        [TestCase("/nodes/00/translation")]
        [TestCase("/nodes/+0/translation")]
        [TestCase("/nodes/0/weights/01")]
        public void AnimationPointerArrayIndicesMustUseCanonicalGrammar(string pointer)
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, 999) },
                new[] { PointerChannel(0, fixture.Root, pointer) });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("pointer-index"));
            StringAssert.Contains("noncanonical", exception.Message);
        }

        [TestCase("translation")]
        [TestCase("rotation")]
        [TestCase("scale")]
        public void AnimationPointerCannotSelectTrsElements(string path)
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, 999) },
                new[] { PointerChannel(0, fixture.Root, $"/nodes/0/{path}/0") });

            var exception = Assert.Throws<ExpressionResponseEvaluationException>(
                () => fixture.Bake("trs-element"));
            StringAssert.Contains("not an Object Model pointer", exception.Message);
        }

        [Test]
        public void AnimationPointerCanSelectMorphWeightElement()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0.25f, 0.75f });
            fixture.Root.Meshes.Add(new GLTFMesh
            {
                Weights = new List<double> { 0.25d, 0.5d },
                Primitives = new List<MeshPrimitive>
                {
                    new MeshPrimitive
                    {
                        Targets = new List<Dictionary<string, AccessorId>>
                        {
                            new Dictionary<string, AccessorId>(),
                            new Dictionary<string, AccessorId>(),
                        },
                    },
                },
            });
            fixture.Root.Nodes.Add(new Node
            {
                Mesh = new MeshId { Id = 0, Root = fixture.Root },
            });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { PointerChannel(0, fixture.Root, "/nodes/0/weights/0") });

            var response = ExpressionResponseEvaluator.Evaluate(fixture.Bake("weight-element")[0].Animation, 1f);

            Assert.That(response.Records[0].Value, Is.EqualTo(new[] { 0.75f }));
        }

        [Test]
        public void AnimationPointerTranslationAcceptsNormalizedIntegerOutput()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int output = fixture.AddUnsignedByteAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new byte[] { 0, 0, 0, 255, 128, 64 },
                true);
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { PointerChannel(0, fixture.Root, "/nodes/0/translation") });

            var animation = fixture.Bake("normalized-pointer-translation")[0].Animation;
            var response = ExpressionResponseEvaluator.Evaluate(animation, 1f);

            Assert.That(
                animation.Samplers[0].OutputEncoding,
                Is.EqualTo(ExpressionAccessorComponentEncoding.NormalizedUnsignedByte));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(1f));
        }

        [Test]
        public void AnimationPointerScaleAcceptsNormalizedIntegerOutput()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int output = fixture.AddUnsignedByteAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new byte[] { 255, 255, 255, 128, 64, 0 },
                true);
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { PointerChannel(0, fixture.Root, "/nodes/0/scale") });

            var response = ExpressionResponseEvaluator.Evaluate(fixture.Bake("normalized-pointer-scale")[0].Animation, 1f);

            Assert.That(response.Records[0].Value[0], Is.EqualTo(128f / 255f).Within(1e-6f));
        }

        [Test]
        public void AnimationPointerTranslationAcceptsNonNormalizedIntegerOutput()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int output = fixture.AddUnsignedByteAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new byte[] { 1, 2, 3, 4, 5, 6 },
                false);
            fixture.Root.Nodes.Add(new Node
            {
                Translation = new GLTF.Math.Vector3(1f, 2f, 3f),
            });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { PointerChannel(0, fixture.Root, "/nodes/0/translation") });

            var animation = fixture.Bake("integer-pointer-translation")[0].Animation;
            var response = ExpressionResponseEvaluator.Evaluate(animation, 1f);

            Assert.That(
                animation.Samplers[0].OutputEncoding,
                Is.EqualTo(ExpressionAccessorComponentEncoding.NonNormalizedInteger));
            Assert.That(response.Records[0].Value, Is.EqualTo(new[] { 4f, 5f, 6f }));
        }

        [Test]
        public void RotationAcceptsNormalizedUnsignedByteOutput()
        {
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f },
                0d,
                1d);
            int output = fixture.AddNormalizedUnsignedByteAccessor(
                GLTFAccessorAttributeType.VEC4,
                2,
                new byte[] { 0, 0, 0, 255, 0, 0, 180, 180 });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "rotation") });

            var animation = fixture.Bake("rotation")[0].Animation;
            var response = ExpressionResponseEvaluator.Evaluate(animation, 1f);
            float expected = (float)Math.Sqrt(0.5d);

            Assert.That(
                animation.Samplers[0].OutputEncoding,
                Is.EqualTo(ExpressionAccessorComponentEncoding.NormalizedUnsignedByte));
            Assert.That(response.Records[0].Value[2], Is.EqualTo(expected).Within(1e-5f));
            Assert.That(response.Records[0].Value[3], Is.EqualTo(expected).Within(1e-5f));
        }

        [Test]
        public void TranslationRejectsNormalizedIntegerOutput()
        {
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f },
                0d,
                1d);
            int output = fixture.AddNormalizedUnsignedByteAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new byte[] { 0, 0, 0, 255, 255, 255 });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });

            Assert.Throws<ExpressionResponseEvaluationException>(() => fixture.Bake("translation"));
        }

        [Test]
        public void CoreTranslationRejectsNonNormalizedIntegerOutput()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int output = fixture.AddUnsignedByteAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new byte[] { 0, 0, 0, 1, 2, 3 },
                false);
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });

            Assert.Throws<ExpressionResponseEvaluationException>(() => fixture.Bake("core-integer-translation"));
        }

        [Test]
        public void PassiveSetEvaluatesAfterUnitySerializationWithoutWritingTargets()
        {
            var fixture = new Fixture();
            int input = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f },
                0d,
                1d);
            int longInput = fixture.AddFloatAccessor(GLTFAccessorAttributeType.SCALAR, 2, new[] { 0f, 2f }, 0d, 2d);
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new[] { 0.5f, 0f, 0f, 2.5f, 0f, 0f });
            int grow = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC3, 2, new[] { 1f, 1f, 1f, 2f, 2f, 2f });
            fixture.Root.Nodes.Add(new Node { Translation = new GLTF.Math.Vector3(0.5f, 0f, 0f) });
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output), new SamplerSpec(longInput, grow) },
                new[]
                {
                    CoreChannel(0, fixture.Root, 0, "translation"),
                    CoreChannel(1, fixture.Root, 0, "scale"),
                });
            var entries = fixture.Bake(
                new[] { Entry("serialized", 0, joint: new[] { 0 }), Entry("other", 0, joint: new[] { 0 }) },
                null,
                out _);
            entries[0].SetSuppressions(new[]
            {
                new SuppressionEntry
                {
                    SourceIndex = 0,
                    TargetIndex = 1,
                    Type = SuppressionType.Block,
                    Amount = 0.1,
                    HasAmount = true,
                    Threshold = 0.3,
                    HasThreshold = true,
                },
            });
            var forward = new[]
            {
                new ExpressionMappingSet
                {
                    SetName = "vrm",
                    Targets = new[]
                    {
                        new MappingTarget
                        {
                            TargetName = "happy",
                            Contributions = new[] { new MappingContribution { SourceIndex = 1, Weight = 0.1 } },
                        },
                    },
                },
            };
            var inputSets = new[]
            {
                new ExpressionInputMappingSet
                {
                    SetName = "relative/v1",
                    Commands = new[]
                    {
                        new InputMappingCommand
                        {
                            CommandName = "happy",
                            Contributions = new[] { new InputMappingContribution { TargetIndex = 0, Weight = 0.25 } },
                        },
                    },
                },
            };

            var source = CreateGameObject("source").AddComponent<ExpressionResponseSet>();
            source.Bind(entries, true, new ExpressionValidationStatus(new[] { "partial" }), forward, inputSets);
            string json = JsonUtility.ToJson(source);
            var restored = CreateGameObject("restored").AddComponent<ExpressionResponseSet>();
            JsonUtility.FromJsonOverwrite(json, restored);

            var response = restored.Evaluate(0, 0.5f);
            var channels = restored.Entries[0].Animation.Channels;
            Assert.IsTrue(restored.RequiredOnImport);
            Assert.That(restored.Entries[0].Name, Is.EqualTo("serialized"));
            Assert.That(response.Duration, Is.EqualTo(2f), "the unlisted scale channel keeps setting T");
            Assert.That(response.Records.Count, Is.EqualTo(1));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(2.5f).Within(1e-6f));
            Assert.That(channels[0].ListedBy, Is.EqualTo(KhrCharacterExtensionNames.ExpressionJoint));
            Assert.IsTrue(channels[0].EmitsRecords);
            Assert.That(channels[0].Target.Kind, Is.EqualTo(ExpressionPropertyKind.NodeTranslation));
            Assert.That(channels[0].Target.AuthoredInitial, Is.EqualTo(new[] { 0.5d, 0d, 0d }));
            Assert.IsNull(channels[1].ListedBy, "Unity's empty string for null is restored as unlisted");
            Assert.IsFalse(channels[1].EmitsRecords);
            Assert.That(channels[1].Target.Kind, Is.EqualTo(ExpressionPropertyKind.NodeScale));
            Assert.IsTrue(channels[1].Target.IsSupported);
            var suppression = restored.Entries[0].Suppressions[0];
            Assert.That(suppression.TargetIndex, Is.EqualTo(1));
            Assert.That(suppression.Type, Is.EqualTo(SuppressionType.Block));
            Assert.That(suppression.Amount, Is.EqualTo(0.1d));
            Assert.That(suppression.Threshold, Is.EqualTo(0.3d));
            Assert.That(restored.Entries[1].Suppressions, Is.Empty);
            Assert.IsTrue(restored.TryGetForwardMappingSet("vrm", out var restoredForward));
            Assert.That(restoredForward.Targets[0].Contributions[0].Weight, Is.EqualTo(0.1d));
            Assert.IsTrue(restored.TryGetInputMappingSet("relative/v1", out var restoredInput));
            Assert.That(restoredInput.Commands[0].Contributions[0].TargetIndex, Is.EqualTo(0));
            Assert.IsFalse(restored.Validation.IsComplete);
            Assert.That(restored.Validation.IncompleteReasons, Is.EqualTo(new[] { "partial" }));
            Assert.IsNull(restored.GetComponent<ExpressionController>());
        }

        private GameObject CreateGameObject(string name)
        {
            var gameObject = new GameObject(name);
            _created.Add(gameObject);
            return gameObject;
        }

        private static int AddTwoKeyInput(Fixture fixture)
        {
            return fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.SCALAR,
                2,
                new[] { 0f, 1f },
                0d,
                1d);
        }

        // Accessor 0 is the input and accessor 1 the output of one core translation channel on node 0.
        private static Fixture TranslationFixture()
        {
            var fixture = new Fixture();
            int input = AddTwoKeyInput(fixture);
            int output = fixture.AddFloatAccessor(GLTFAccessorAttributeType.VEC3, 2, new[] { 0f, 0f, 0f, 1f, 2f, 3f });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });
            return fixture;
        }

        private static void AddMorphMesh(Fixture fixture, List<double> weights, int targetCount)
        {
            var targets = new List<Dictionary<string, AccessorId>>();
            for (int index = 0; index < targetCount; index++) targets.Add(new Dictionary<string, AccessorId>());
            fixture.Root.Meshes.Add(new GLTFMesh
            {
                Weights = weights,
                Primitives = new List<MeshPrimitive> { new MeshPrimitive { Targets = targets } },
            });
        }

        private static KHR_character_expression.ExpressionItem Entry(
            string name,
            int animation,
            int[] joint = null,
            int[] morph = null,
            int[] texture = null)
        {
            return new KHR_character_expression.ExpressionItem
            {
                Name = name,
                Animation = animation,
                Joint = joint != null ? new KHR_character_expression_joint { Channels = joint } : null,
                Morphtarget = morph != null ? new KHR_character_expression_morphtarget { Channels = morph } : null,
                TextureTransform = texture != null
                    ? new KHR_character_expression_texture_transform { Channels = texture }
                    : null,
            };
        }

        private static AnimationChannel CoreChannel(
            int samplerIndex,
            GLTFRoot root,
            int nodeIndex,
            string path)
        {
            return new AnimationChannel
            {
                Sampler = new AnimationSamplerId { Id = samplerIndex, Root = root },
                Target = new AnimationChannelTarget
                {
                    Node = new NodeId { Id = nodeIndex, Root = root },
                    Path = path,
                },
            };
        }

        private static AnimationChannel PointerChannel(int samplerIndex, GLTFRoot root, string pointer)
        {
            return new AnimationChannel
            {
                Sampler = new AnimationSamplerId { Id = samplerIndex, Root = root },
                Target = new AnimationChannelTarget
                {
                    Path = "pointer",
                    Extensions = new Dictionary<string, IExtension>
                    {
                        {
                            KHR_animation_pointer.EXTENSION_NAME,
                            new KHR_animation_pointer { path = pointer }
                        },
                    },
                },
            };
        }

        private readonly struct SamplerSpec
        {
            public SamplerSpec(int input, int output)
            {
                Input = input;
                Output = output;
            }

            public int Input { get; }
            public int Output { get; }
        }

        private sealed class Fixture
        {
            private readonly Dictionary<BufferView, byte[]> _bufferData = new Dictionary<BufferView, byte[]>();

            public Fixture()
            {
                Root = new GLTFRoot
                {
                    Accessors = new List<Accessor>(),
                    Animations = new List<GLTFAnimation>(),
                    BufferViews = new List<BufferView>(),
                    Materials = new List<GLTFMaterial>(),
                    Meshes = new List<GLTFMesh>(),
                    Nodes = new List<Node>(),
                };
            }

            public void AddTexturedMaterial(ExtTextureTransformExtension transform)
            {
                var textureInfo = new TextureInfo { Index = new TextureId { Id = 0, Root = Root } };
                if (transform != null)
                    textureInfo.Extensions = new Dictionary<string, IExtension>
                    {
                        { ExtTextureTransformExtensionFactory.EXTENSION_NAME, transform },
                    };
                Root.Materials.Add(new GLTFMaterial
                {
                    PbrMetallicRoughness = new PbrMetallicRoughness { BaseColorTexture = textureInfo },
                });
            }

            public GLTFRoot Root { get; }

            public int AddFloatAccessor(
                GLTFAccessorAttributeType type,
                uint count,
                float[] values,
                double? minimum = null,
                double? maximum = null)
            {
                var view = AddBufferView(FloatBytes(values));
                var accessor = new Accessor
                {
                    BufferView = new BufferViewId { Id = view, Root = Root },
                    ComponentType = GLTFComponentType.Float,
                    Count = count,
                    Type = type,
                    Min = minimum.HasValue ? new List<double> { minimum.Value } : null,
                    Max = maximum.HasValue ? new List<double> { maximum.Value } : null,
                };
                Root.Accessors.Add(accessor);
                return Root.Accessors.Count - 1;
            }

            public int AddSparseFloatAccessor(
                GLTFAccessorAttributeType type,
                uint count,
                byte sparseIndex,
                float[] sparseValues,
                double? minimum = null,
                double? maximum = null)
            {
                int indexView = AddBufferView(new[] { sparseIndex });
                int valueView = AddBufferView(FloatBytes(sparseValues));
                var accessor = new Accessor
                {
                    ComponentType = GLTFComponentType.Float,
                    Count = count,
                    Type = type,
                    Min = minimum.HasValue ? new List<double> { minimum.Value } : null,
                    Max = maximum.HasValue ? new List<double> { maximum.Value } : null,
                    Sparse = new AccessorSparse
                    {
                        Count = 1,
                        Indices = new AccessorSparseIndices
                        {
                            BufferView = new BufferViewId { Id = indexView, Root = Root },
                            ComponentType = GLTFComponentType.UnsignedByte,
                        },
                        Values = new AccessorSparseValues
                        {
                            BufferView = new BufferViewId { Id = valueView, Root = Root },
                        },
                    },
                };
                Root.Accessors.Add(accessor);
                return Root.Accessors.Count - 1;
            }

            public int AddNormalizedUnsignedByteAccessor(
                GLTFAccessorAttributeType type,
                uint count,
                byte[] values)
            {
                return AddUnsignedByteAccessor(type, count, values, true);
            }

            public int AddUnsignedByteAccessor(
                GLTFAccessorAttributeType type,
                uint count,
                byte[] values,
                bool normalized)
            {
                int view = AddBufferView(values);
                Root.Accessors.Add(new Accessor
                {
                    BufferView = new BufferViewId { Id = view, Root = Root },
                    ComponentType = GLTFComponentType.UnsignedByte,
                    Normalized = normalized,
                    Count = count,
                    Type = type,
                });
                return Root.Accessors.Count - 1;
            }

            public void AddAnimation(SamplerSpec[] samplerSpecs, AnimationChannel[] channels)
            {
                var animation = new GLTFAnimation();
                foreach (var spec in samplerSpecs)
                {
                    animation.Samplers.Add(new AnimationSampler
                    {
                        Input = new AccessorId { Id = spec.Input, Root = Root },
                        Output = new AccessorId { Id = spec.Output, Root = Root },
                        Interpolation = InterpolationType.LINEAR,
                    });
                }
                foreach (var channel in channels)
                {
                    channel.Sampler.GLTFAnimation = animation;
                    animation.Channels.Add(channel);
                }
                Root.Animations.Add(animation);
            }

            // Lists every channel of animation 0 through the derived extension that classifies its target.
            public ExpressionResponseSetEntry[] Bake(string label)
            {
                var joint = new List<int>();
                var morph = new List<int>();
                var texture = new List<int>();
                var channels = Root.Animations[0].Channels;
                for (int index = 0; index < channels.Count; index++)
                {
                    var target = channels[index].Target;
                    string pointer = target?.Extensions != null &&
                        target.Extensions.TryGetValue(KHR_animation_pointer.EXTENSION_NAME, out var extension)
                        ? (extension as KHR_animation_pointer)?.path
                        : null;
                    string path = target?.Node != null ? target.Path : pointer?.Split('/').Length > 3
                        ? pointer.Split('/')[3]
                        : null;
                    if (pointer != null && pointer.StartsWith("/materials/") &&
                        pointer.Contains("/KHR_texture_transform/"))
                        texture.Add(index);
                    else if (pointer != null && !pointer.StartsWith("/nodes/"))
                        continue;
                    else if (path == "weights") morph.Add(index);
                    else if (path == "translation" || path == "rotation" || path == "scale") joint.Add(index);
                }
                return Bake(Entry(
                    label,
                    0,
                    joint.Count > 0 ? joint.ToArray() : null,
                    morph.Count > 0 ? morph.ToArray() : null,
                    texture.Count > 0 ? texture.ToArray() : null));
            }

            public ExpressionResponseSetEntry[] Bake(params KHR_character_expression.ExpressionItem[] items)
                => Bake(items, null, out _);

            public ExpressionResponseSetEntry[] Bake(
                KHR_character_expression.ExpressionItem[] items,
                ExpressionResponseBakeOptions options,
                out ExpressionValidationStatus validation)
            {
                var extension = new KHR_character_expression
                {
                    Expressions = new List<KHR_character_expression.ExpressionItem>(items),
                };
                return KhrCharacterResponseBaker.Bake(Root, view => _bufferData[view], extension, options, out validation);
            }

            public ExpressionResponseSetEntry[] Bake(KHR_character_expression extension)
                => KhrCharacterResponseBaker.Bake(Root, view => _bufferData[view], extension);

            private int AddBufferView(byte[] data)
            {
                var view = new BufferView { ByteLength = (uint)data.Length };
                Root.BufferViews.Add(view);
                _bufferData.Add(view, data);
                return Root.BufferViews.Count - 1;
            }

            private static byte[] FloatBytes(float[] values)
            {
                var bytes = new byte[values.Length * sizeof(float)];
                Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                return bytes;
            }
        }
    }
}
