using System;
using System.Collections.Generic;
using GLTF.Schema;
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
            Assert.That(entries[0].Label, Is.EqualTo("move"));
            Assert.That(response.Duration, Is.EqualTo(2f));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(1f).Within(1e-6f));
            Assert.That(response.Records[0].Value[1], Is.EqualTo(2f).Within(1e-6f));
            Assert.That(response.Records[0].Value[2], Is.EqualTo(3f).Within(1e-6f));
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
        public void MatrixBackedNodeTranslationCanBeTargetedByAnimationPointer()
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

            var response = ExpressionResponseEvaluator.Evaluate(fixture.Bake("matrix-pointer")[0].Animation, 1f);

            Assert.That(response.Records[0].Value[0], Is.EqualTo(5f));
            Assert.That(response.Records[0].Value[1], Is.EqualTo(6f));
            Assert.That(response.Records[0].Value[2], Is.EqualTo(7f));
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
            int output = fixture.AddFloatAccessor(
                GLTFAccessorAttributeType.VEC3,
                2,
                new[] { 0f, 0f, 0f, 2f, 0f, 0f });
            fixture.Root.Nodes.Add(new Node());
            fixture.AddAnimation(
                new[] { new SamplerSpec(input, output) },
                new[] { CoreChannel(0, fixture.Root, 0, "translation") });
            var entries = fixture.Bake("serialized");

            var source = CreateGameObject("source").AddComponent<ExpressionResponseSet>();
            source.Bind(entries, true);
            string json = JsonUtility.ToJson(source);
            var restored = CreateGameObject("restored").AddComponent<ExpressionResponseSet>();
            JsonUtility.FromJsonOverwrite(json, restored);

            var response = restored.Evaluate(0, 0.5f);
            Assert.IsTrue(restored.RequiredOnImport);
            Assert.That(restored.Entries[0].Label, Is.EqualTo("serialized"));
            Assert.That(response.Records[0].Value[0], Is.EqualTo(1f).Within(1e-6f));
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
                    Meshes = new List<GLTFMesh>(),
                    Nodes = new List<Node>(),
                };
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

            public ExpressionResponseSetEntry[] Bake(string label)
            {
                var extension = new KHR_character_expression
                {
                    Expressions = new List<KHR_character_expression.ExpressionItem>
                    {
                        new KHR_character_expression.ExpressionItem
                        {
                            Expression = label,
                            Animation = 0,
                        },
                    },
                };
                return KhrCharacterResponseBaker.Bake(Root, view => _bufferData[view], extension);
            }

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
