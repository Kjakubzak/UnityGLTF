using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden values for the response animations of an authored set: legacy samplers normalized to response progress,
    /// glTF-space values, unlisted constant channels and the bindings of their properties. The animations are sampled
    /// with the passive evaluator, as the expression controller samples them.
    /// </summary>
    public class AuthoredExpressionCurvesTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var value in _created)
                if (value != null) Object.DestroyImmediate(value);
            _created.Clear();
        }

        [Test]
        public void Linear_TwoKey_GoldenAtPhases()
        {
            var animation = MorphAnimation(new[] { 0f, 1f }, Interp.Linear, new[] { 0f, 1f }, 0f);
            Assert.That(Sample(animation, 0.5d), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(Sample(animation, 1d), Is.EqualTo(1f));
            Assert.That(ExpressionResponseEvaluator.Evaluate(animation, 0d).Records, Is.Empty,
                "a zero driver produces no record");
        }

        [Test]
        public void Linear_ThreeKey_NormalizesTheSourceRangeToResponseProgress()
        {
            // Source keys at 2, 3 and 6 seconds become 0, 0.25 and 1.
            var animation = MorphAnimation(new[] { 2f, 3f, 6f }, Interp.Linear, new[] { 0f, 0.5f, 1f }, 0f);
            Assert.That(animation.Samplers[0].InputTimes, Is.EqualTo(new[] { 0f, 0.25f, 1f }));
            Assert.That(Sample(animation, 0.125d), Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(Sample(animation, 0.25d), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(Sample(animation, 0.625d), Is.EqualTo(0.75f).Within(1e-6f));
        }

        [Test]
        public void Step_HoldsThenJumps_IncludingEndpoint()
        {
            var animation = MorphAnimation(new[] { 0f, 0.5f, 1f }, Interp.Step, new[] { 0f, 0.5f, 1f }, 0f);
            Assert.That(Sample(animation, 0.4d), Is.EqualTo(0f));
            Assert.That(Sample(animation, 0.6d), Is.EqualTo(0.5f));
            Assert.That(Sample(animation, 1d), Is.EqualTo(1f));
        }

        [Test]
        public void SingleKey_BecomesALinearRestToTargetResponse()
        {
            var smr = MakeSmr(1);
            var set = Set(new ExpressionTrack
            {
                Name = "smile",
                MorphDrivers = new[]
                {
                    new MorphDriver
                    {
                        Smr = smr,
                        BlendShapeIndex = 0,
                        BaseValue = 0.2f,
                        Sampler = new Sampler { Times = new[] { 0f }, Interp = Interp.Step, SingleKey = true },
                        DeltaValues = new[] { 0.8f },
                    },
                },
            });

            var animation = AuthoredExpressionCurves.BuildResponseAnimations(set).Animations[0];

            Assert.That(animation.Samplers[0].Interpolation, Is.EqualTo(ExpressionResponseInterpolation.Linear));
            Assert.That(animation.Samplers[0].InputTimes, Is.EqualTo(new[] { 0f, 1f }));
            Assert.That(Sample(animation, 0.5d), Is.EqualTo(0.5f).Within(1e-6f));
        }

        [Test]
        public void Joint_ValuesAreInGltfSpaceWithTheDriverBaseAsAuthoredValue()
        {
            var node = new GameObject("node").transform;
            _created.Add(node.gameObject);
            var baseRotation = Quaternion.Euler(0f, 30f, 0f);
            var set = Set(new ExpressionTrack
            {
                Name = "jaw",
                JointDrivers = new[]
                {
                    new JointDriver
                    {
                        Target = node,
                        Channel = TrsChannel.Translation,
                        Sampler = Linear(),
                        BaseVec = new Vector3(1f, 2f, 3f),
                        DeltaVec = new[] { Vector3.zero, new Vector3(1f, 0f, 0f) },
                    },
                    new JointDriver
                    {
                        Target = node,
                        Channel = TrsChannel.Rotation,
                        Sampler = Linear(),
                        BaseQuat = baseRotation,
                        DeltaQuat = new[] { Quaternion.identity, Quaternion.Euler(20f, 0f, 0f) },
                    },
                    new JointDriver
                    {
                        Target = node,
                        Channel = TrsChannel.Scale,
                        Sampler = Linear(),
                        BaseVec = Vector3.one,
                        DeltaVec = new[] { Vector3.zero, new Vector3(1f, 0f, 0f) },
                    },
                },
            });

            var result = AuthoredExpressionCurves.BuildResponseAnimations(set);
            var channels = result.Animations[0].Channels;

            Assert.That(channels[0].Target.Kind, Is.EqualTo(ExpressionPropertyKind.NodeTranslation));
            Assert.That(channels[0].Target.AuthoredInitial, Is.EqualTo(new[] { -1d, 2d, 3d }));
            Assert.That(result.Animations[0].Samplers[0].OutputValues[1], Is.EqualTo(new[] { -2f, 2f, 3f }));
            Assert.That(channels[1].Target.Kind, Is.EqualTo(ExpressionPropertyKind.NodeRotation));
            Assert.That(channels[1].Target.ValueKind, Is.EqualTo(ExpressionResponseValueKind.Quaternion));
            Assert.That(channels[2].Target.Kind, Is.EqualTo(ExpressionPropertyKind.NodeScale));
            Assert.That(result.Animations[0].Samplers[2].OutputValues[1], Is.EqualTo(new[] { 2f, 1f, 1f }));
            foreach (var channel in channels)
            {
                StringAssert.StartsWith("authored:", channel.Target.Identity.CanonicalProperty);
                Assert.That(channel.ListedBy, Is.EqualTo(KhrCharacterExtensionNames.ExpressionJoint));
            }

            Assert.That(result.Bindings.Length, Is.EqualTo(3));
            foreach (var binding in result.Bindings) Assert.That(binding.Node, Is.SameAs(node));
            var rotation = result.Bindings[1].AuthoredInitial;
            var gltf = new Quaternion((float)rotation[0], (float)rotation[1], (float)rotation[2], (float)rotation[3]);
            Assert.That(Quaternion.Angle(new Quaternion(gltf.x, -gltf.y, -gltf.z, gltf.w), baseRotation),
                Is.LessThan(1e-3f), "the authored rotation is the driver base in glTF space");
        }

        [Test]
        public void ConstantChannels_AreUnlistedAndKeepTheDuration()
        {
            var renderer = MakeTexturedRenderer();
            var set = Set(new ExpressionTrack
            {
                Name = "uv",
                TextureDrivers = new[]
                {
                    new TextureDriver
                    {
                        Renderer = renderer,
                        SubmeshSlot = 0,
                        PropertyName = "_MainTex",
                        PropertyId = Shader.PropertyToID("_MainTex_ST"),
                        Sampler = Linear(),
                        BaseSt = new Vector4(1f, 1f, 0f, 0f),
                        StValues = new[] { Vector4.zero, new Vector4(0f, 0f, 0.5f, 0f) },
                    },
                },
            });

            var result = AuthoredExpressionCurves.BuildResponseAnimations(set);
            var animation = result.Animations[0];
            var response = ExpressionResponseEvaluator.Evaluate(animation, 1d);

            Assert.That(animation.Channels.Length, Is.EqualTo(2));
            Assert.That(animation.Channels[0].Target.Kind, Is.EqualTo(ExpressionPropertyKind.TextureScale));
            Assert.IsFalse(animation.Channels[0].EmitsRecords, "the scale never leaves its authored value");
            Assert.IsNull(animation.Channels[0].ListedBy);
            Assert.That(animation.Channels[1].Target.Kind, Is.EqualTo(ExpressionPropertyKind.TextureOffset));
            Assert.IsTrue(animation.Channels[1].EmitsRecords);
            Assert.That(response.Duration, Is.EqualTo(1f));
            Assert.That(response.Records.Count, Is.EqualTo(1));
            Assert.That(response.Records[0].Value, Is.EqualTo(new[] { 0.5f, 0f }), "offset is glTF space");

            Assert.That(result.Bindings.Length, Is.EqualTo(2), "offset and scale of one transform are both bound");
            foreach (var binding in result.Bindings)
            {
                Assert.That(binding.TextureStProperty, Is.EqualTo("_MainTex_ST"));
                Assert.That(binding.TextureRenderers, Is.EqualTo(new Renderer[] { renderer }));
            }
        }

        [Test]
        public void TrackWithoutDrivers_HasNoAnimation()
        {
            var result = AuthoredExpressionCurves.BuildResponseAnimations(Set(new ExpressionTrack { Name = "empty" }));
            Assert.IsNull(result.Animations[0]);
            Assert.That(result.Bindings, Is.Empty);
        }

        [Test]
        public void InvalidTrack_WarnsAndOtherTracksStillBuild()
        {
            var smr = MakeSmr(2);
            var set = Set(
                new ExpressionTrack
                {
                    Name = "broken",
                    MorphDrivers = new[]
                    {
                        new MorphDriver
                        {
                            Smr = smr,
                            BlendShapeIndex = 0,
                            Sampler = new Sampler { Times = new[] { 1f, 0f }, Interp = Interp.Linear },
                            DeltaValues = new[] { 0f, 1f },
                        },
                    },
                },
                new ExpressionTrack
                {
                    Name = "fine",
                    MorphDrivers = new[]
                    {
                        new MorphDriver { Smr = smr, BlendShapeIndex = 1, Sampler = Linear(), DeltaValues = new[] { 0f, 1f } },
                    },
                });

            LogAssert.Expect(LogType.Warning, new Regex("Expression 0 \\('broken'\\) produces no output"));
            var result = AuthoredExpressionCurves.BuildResponseAnimations(set);

            Assert.IsNull(result.Animations[0]);
            Assert.IsNotNull(result.Animations[1]);
            Assert.That(result.Bindings.Length, Is.EqualTo(1));
        }

        [Test]
        public void MorphBase_UsesDriverBasesAndTheRendererWeightElsewhere()
        {
            var smr = MakeSmr(3);
            smr.SetBlendShapeWeight(2, 40f);
            var set = Set(new ExpressionTrack
            {
                Name = "smile",
                MorphDrivers = new[]
                {
                    new MorphDriver
                    {
                        Smr = smr,
                        BlendShapeIndex = 1,
                        BaseValue = 0.25f,
                        Sampler = Linear(),
                        DeltaValues = new[] { 0f, 0.5f },
                    },
                },
            });

            var result = AuthoredExpressionCurves.BuildResponseAnimations(set);
            var target = result.Animations[0].Channels[0].Target;

            Assert.That(target.Identity.Selection, Is.EqualTo(ExpressionPropertySelection.ArrayElement));
            Assert.That(target.Identity.ArrayElement, Is.EqualTo(1));
            Assert.That(target.ArrayLength, Is.EqualTo(3));
            Assert.That(target.ArrayAuthoredInitial, Is.EqualTo(new[] { 0d, 0.25d, 0.4d }).Within(1e-7d));
            Assert.That(result.Bindings[0].MorphFrameWeights, Is.EqualTo(new[] { 100f, 100f, 100f }));
        }

        private ExpressionResponseAnimation MorphAnimation(float[] times, Interp interp, float[] deltas, float baseValue)
        {
            var smr = MakeSmr(1);
            var set = Set(new ExpressionTrack
            {
                Name = "smile",
                MorphDrivers = new[]
                {
                    new MorphDriver
                    {
                        Smr = smr,
                        BlendShapeIndex = 0,
                        BaseValue = baseValue,
                        Sampler = new Sampler { Times = times, Interp = interp },
                        DeltaValues = deltas,
                    },
                },
            });
            return AuthoredExpressionCurves.BuildResponseAnimations(set).Animations[0];
        }

        private static float Sample(ExpressionResponseAnimation animation, double driver)
            => ExpressionResponseEvaluator.Evaluate(animation, driver).Records[0].Value[0];

        private static CharacterExpressionSet Set(params ExpressionTrack[] tracks)
            => new CharacterExpressionSet { Expressions = tracks };

        private static Sampler Linear() => new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear };

        private SkinnedMeshRenderer MakeSmr(int blendShapeCount)
        {
            var mesh = new Mesh { name = "curves" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            for (int index = 0; index < blendShapeCount; index++)
                mesh.AddBlendShapeFrame("shape" + index, 100f, new[] { Vector3.one, Vector3.one, Vector3.one }, null, null);
            _created.Add(mesh);
            var go = new GameObject("face");
            _created.Add(go);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            return smr;
        }

        private MeshRenderer MakeTexturedRenderer()
        {
            var shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Standard");
            Assert.IsNotNull(shader, "a built-in texture shader is required");
            var go = new GameObject("panel", typeof(MeshFilter), typeof(MeshRenderer));
            _created.Add(go);
            var material = new Material(shader);
            _created.Add(material);
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return renderer;
        }
    }
}
