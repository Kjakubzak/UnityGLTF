using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Tests for the authoring asset (<see cref="CharacterExpressionSetAsset"/>) and its scene-independent
    /// bindings. <see cref="CharacterExpressionSetAsset.Extract"/> converts a baked
    /// <see cref="CharacterExpressionSet"/> (live scene refs + curves) into an <see cref="ExpressionBindingSet"/>
    /// (renderer/bone paths + blendshape names + curves) that serializes into a project asset, and
    /// <see cref="CharacterExpressionSetAsset.Resolve"/> re-resolves it back onto a character — including the eye
    /// joint-rotation drivers that previously broke extraction.
    /// </summary>
    public class CharacterExpressionSetAssetTests
    {
        private readonly List<Object> _spawned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned)
                if (o != null) Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        // Synthetic character: root → "Body" (SkinnedMeshRenderer with named blendshapes) + "Eye" (a bone).
        private Transform BuildCharacter(out SkinnedMeshRenderer bodySmr, out Transform eye)
        {
            var root = new GameObject("Character");
            _spawned.Add(root);

            var body = new GameObject("Body");
            body.transform.SetParent(root.transform);
            bodySmr = body.AddComponent<SkinnedMeshRenderer>();

            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            var delta = new[] { Vector3.zero, Vector3.zero, Vector3.zero };
            mesh.AddBlendShapeFrame("smile", 100f, delta, null, null);
            mesh.AddBlendShapeFrame("frown", 100f, delta, null, null);
            bodySmr.sharedMesh = mesh;
            _spawned.Add(mesh);

            var eyeGo = new GameObject("Eye");
            eyeGo.transform.SetParent(root.transform);
            eye = eyeGo.transform;

            return root.transform;
        }

        // A morph (Body, "smile") + a rotation joint (Eye) expression, with metadata, masks and a mapping set.
        private static CharacterExpressionSet BuildBaked(SkinnedMeshRenderer bodySmr, Transform eye)
        {
            return new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "smile",
                        Domains = ExpressionDomain.Morph | ExpressionDomain.Joint,
                        BlendMode = ExpressionBlendMode.Override,
                        IsBinary = false,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Smr = bodySmr,
                                BlendShapeIndex = 0, // "smile"
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear },
                                DeltaValues = new[] { 0f, 1f },
                                BaseValue = 0f,
                                Priority = 3,
                            },
                        },
                        JointDrivers = new[]
                        {
                            new JointDriver
                            {
                                Target = eye,
                                Channel = TrsChannel.Rotation,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear },
                                DeltaQuat = new[] { Quaternion.identity, Quaternion.Euler(0f, 30f, 0f) },
                                BaseQuat = Quaternion.Euler(5f, 0f, 0f),
                                Priority = 2,
                            },
                        },
                        Masks = new[] { new MaskEntry { TargetIndex = 0, Type = MaskType.Blend, Amount = 1f } },
                    },
                },
                MappingSets = new[]
                {
                    new ExpressionMappingSet
                    {
                        SetName = "vrm",
                        Targets = new[]
                        {
                            new MappingTarget
                            {
                                TargetName = "happy",
                                Contributions = new[] { new MappingContribution { SourceIndex = 0, Weight = 1f } },
                            },
                        },
                    },
                },
            };
        }

        private static bool QuatApprox(Quaternion a, Quaternion b) => Quaternion.Angle(a, b) < 0.05f;

        [Test]
        public void RoundTrip_PreservesDriversIncludingEyeRotation()
        {
            var root = BuildCharacter(out var bodySmr, out var eye);
            var baked = BuildBaked(bodySmr, eye);

            var bindings = CharacterExpressionSetAsset.Extract(baked, root);

            // Extract: scene refs replaced by paths/names (the binding types carry no live SMR/Transform/Renderer).
            Assert.AreEqual(1, bindings.Expressions.Length);
            var eb = bindings.Expressions[0];
            Assert.AreEqual(1, eb.MorphBindings.Length);
            Assert.AreEqual("Body", eb.MorphBindings[0].RendererPath);
            Assert.AreEqual("smile", eb.MorphBindings[0].BlendShapeName);
            Assert.AreEqual(1, eb.JointBindings.Length);
            Assert.AreEqual("Eye", eb.JointBindings[0].TargetPath);
            Assert.AreEqual(TrsChannel.Rotation, eb.JointBindings[0].Channel);

            // Resolve: paths/names re-resolved back onto the same character.
            var resolved = CharacterExpressionSetAsset.Resolve(bindings, root);
            Assert.AreEqual(1, resolved.Expressions.Length);
            var rt = resolved.Expressions[0];

            Assert.AreEqual(1, rt.MorphDrivers.Length);
            Assert.AreSame(bodySmr, rt.MorphDrivers[0].Smr);
            Assert.AreEqual(0, rt.MorphDrivers[0].BlendShapeIndex);
            Assert.AreEqual(3, rt.MorphDrivers[0].Priority);
            CollectionAssert.AreEqual(new[] { 0f, 1f }, rt.MorphDrivers[0].DeltaValues);

            Assert.AreEqual(1, rt.JointDrivers.Length);
            var jd = rt.JointDrivers[0];
            Assert.AreSame(eye, jd.Target);
            Assert.AreEqual(TrsChannel.Rotation, jd.Channel);
            Assert.AreEqual(2, jd.DeltaQuat.Length);
            Assert.IsTrue(QuatApprox(Quaternion.identity, jd.DeltaQuat[0]));
            Assert.IsTrue(QuatApprox(Quaternion.Euler(0f, 30f, 0f), jd.DeltaQuat[1]));
            Assert.IsTrue(QuatApprox(Quaternion.Euler(5f, 0f, 0f), jd.BaseQuat));
            Assert.AreEqual(2, jd.Priority);
        }

        [Test]
        public void Extract_SerializesIntoAssetCleanly()
        {
            var root = BuildCharacter(out var bodySmr, out var eye);
            var baked = BuildBaked(bodySmr, eye);
            var bindings = CharacterExpressionSetAsset.Extract(baked, root);

            var asset = ScriptableObject.CreateInstance<CharacterExpressionSetAsset>();
            _spawned.Add(asset);
            asset.Bindings = bindings;

            // This is the scenario that previously threw on eye joint-rotation drivers (live Transform refs +
            // Quaternion[] curves). With path-based bindings the asset serializes without error.
            var json = JsonUtility.ToJson(asset);
            Assert.IsNotNull(json);

            var clone = ScriptableObject.CreateInstance<CharacterExpressionSetAsset>();
            _spawned.Add(clone);
            JsonUtility.FromJsonOverwrite(json, clone);

            Assert.IsNotNull(clone.Bindings?.Expressions);
            Assert.AreEqual(1, clone.Bindings.Expressions.Length);
            Assert.AreEqual("Body", clone.Bindings.Expressions[0].MorphBindings[0].RendererPath);
            Assert.AreEqual("Eye", clone.Bindings.Expressions[0].JointBindings[0].TargetPath);

            // The Quaternion rotation curve survives Unity serialization.
            var quat = clone.Bindings.Expressions[0].JointBindings[0].DeltaQuat;
            Assert.AreEqual(2, quat.Length);
            Assert.IsTrue(QuatApprox(Quaternion.Euler(0f, 30f, 0f), quat[1]));
        }

        [Test]
        public void Extract_PreservesMetadataAndMappingSets()
        {
            var root = BuildCharacter(out var bodySmr, out var eye);
            var baked = BuildBaked(bodySmr, eye);

            var bindings = CharacterExpressionSetAsset.Extract(baked, root);
            var eb = bindings.Expressions[0];

            Assert.AreEqual("smile", eb.Name);
            Assert.AreEqual(ExpressionBlendMode.Override, eb.BlendMode);
            Assert.AreEqual(ExpressionDomain.Morph | ExpressionDomain.Joint, eb.Domains);
            Assert.IsFalse(eb.IsBinary);
            Assert.AreEqual(1, eb.Masks.Length);
            Assert.AreEqual(MaskType.Blend, eb.Masks[0].Type);
            Assert.AreEqual("vrm", bindings.MappingSets[0].SetName);
            Assert.AreEqual("happy", bindings.MappingSets[0].Targets[0].TargetName);

            // Metadata also survives the inverse.
            var resolved = CharacterExpressionSetAsset.Resolve(bindings, root);
            Assert.AreEqual("smile", resolved.Expressions[0].Name);
            Assert.AreEqual(ExpressionBlendMode.Override, resolved.Expressions[0].BlendMode);
            Assert.AreEqual(ExpressionDomain.Morph | ExpressionDomain.Joint, resolved.Expressions[0].Domains);
            Assert.AreEqual("vrm", resolved.MappingSets[0].SetName);
        }

        [Test]
        public void Resolve_DropsUnresolvedBindings_WithoutThrowing()
        {
            var root = BuildCharacter(out _, out _);

            var bindings = new ExpressionBindingSet
            {
                Expressions = new[]
                {
                    new ExpressionBinding
                    {
                        Name = "x",
                        JointBindings = new[]
                        {
                            new JointBinding
                            {
                                TargetPath = "DoesNotExist",
                                Channel = TrsChannel.Rotation,
                                DeltaQuat = new[] { Quaternion.identity },
                                BaseQuat = Quaternion.identity,
                            },
                        },
                    },
                },
            };

            CharacterExpressionSet resolved = null;
            Assert.DoesNotThrow(() => resolved = CharacterExpressionSetAsset.Resolve(bindings, root));
            Assert.AreEqual(1, resolved.Expressions.Length);
            Assert.IsTrue(resolved.Expressions[0].JointDrivers == null || resolved.Expressions[0].JointDrivers.Length == 0);
        }
    }
}
