using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden-value tests for the morph baker's decoded-arrays -> MorphDriver transform: frame-major weight
    /// layout, delta-over-frame-0, single-key absolute target, and interpolation preservation.
    /// </summary>
    public class KhrCharacterBakerMorphTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        private SkinnedMeshRenderer MakeSmr(int blendShapeCount, float frameWeight)
        {
            var mesh = new Mesh { name = "test" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            var delta = new[] { Vector3.one, Vector3.one, Vector3.one };
            for (int i = 0; i < blendShapeCount; i++)
                mesh.AddBlendShapeFrame("shape" + i, frameWeight, delta, null, null);
            _created.Add(mesh);

            var go = new GameObject("smr");
            _created.Add(go);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            return smr;
        }

        [Test]
        public void Linear_FrameMajor_DeltaOverFrame0()
        {
            var smr = MakeSmr(2, 1f);
            var times = new[] { 0f, 0.5f, 1f };
            // frame-major: frame0=[0,0], frame1=[0.5,0], frame2=[1,0]
            var values = new[] { 0f, 0f, 0.5f, 0f, 1f, 0f };

            var drivers = new List<MorphDriver>();
            KhrCharacterBaker.BuildMorphDrivers(smr, times, values, InterpolationType.LINEAR, drivers);

            Assert.AreEqual(2, drivers.Count);
            var d0 = drivers.Find(d => d.BlendShapeIndex == 0);
            var d1 = drivers.Find(d => d.BlendShapeIndex == 1);
            Assert.IsNotNull(d0);
            Assert.IsNotNull(d1);

            Assert.That(d0.DeltaValues, Is.EqualTo(new[] { 0f, 0.5f, 1f }).Within(1e-5f));
            Assert.That(d1.DeltaValues, Is.EqualTo(new[] { 0f, 0f, 0f }).Within(1e-5f));
            Assert.AreEqual(Interp.Linear, d0.Sampler.Interp);
            Assert.IsFalse(d0.Sampler.SingleKey);
            CollectionAssert.AreEqual(times, d0.Sampler.Times);
            Assert.AreSame(smr, d0.Smr);
        }

        [Test]
        public void SingleKey_StoresAbsoluteTarget()
        {
            var smr = MakeSmr(2, 1f);
            var drivers = new List<MorphDriver>();
            KhrCharacterBaker.BuildMorphDrivers(smr, new[] { 0f }, new[] { 0.8f, 0.3f }, InterpolationType.LINEAR, drivers);

            Assert.AreEqual(2, drivers.Count);
            Assert.IsTrue(drivers[0].Sampler.SingleKey);
            Assert.AreEqual(0.8f, drivers[0].DeltaValues[0], 1e-5f);
            Assert.AreEqual(0.3f, drivers[1].DeltaValues[0], 1e-5f);
        }

        [Test]
        public void CoreWeightsUseNodeWeightsBeforeMeshWeightsAsBase()
        {
            var smr = MakeSmr(2, 1f);
            var root = new GLTFRoot
            {
                Meshes = new List<GLTFMesh>
                {
                    new GLTFMesh { Weights = new List<double> { 0.1d, 0.2d } },
                },
                Nodes = new List<Node>(),
            };
            root.Nodes.Add(new Node
            {
                Mesh = new MeshId { Id = 0, Root = root },
                Weights = new List<double> { 0.25d, 0.5d },
            });
            var bases = KhrCharacterBaker.ResolveMorphBaseValues(root, 0, 2);
            var drivers = new List<MorphDriver>();

            KhrCharacterBaker.BuildMorphDrivers(
                smr,
                new[] { 0f, 1f },
                new[] { 0.25f, 0.5f, 0.75f, 0.25f },
                InterpolationType.LINEAR,
                bases,
                drivers);

            Assert.That(bases, Is.EqualTo(new[] { 0.25f, 0.5f }));
            Assert.That(drivers[0].BaseValue, Is.EqualTo(0.25f));
            Assert.That(drivers[0].DeltaValues, Is.EqualTo(new[] { 0f, 0.5f }).Within(1e-6f));
            Assert.That(drivers[1].BaseValue, Is.EqualTo(0.5f));
            Assert.That(drivers[1].DeltaValues, Is.EqualTo(new[] { 0f, -0.25f }).Within(1e-6f));
        }

        [Test]
        public void Step_PreservesInterpolation()
        {
            var smr = MakeSmr(1, 1f);
            var drivers = new List<MorphDriver>();
            KhrCharacterBaker.BuildMorphDrivers(smr, new[] { 0f, 1f }, new[] { 0f, 1f }, InterpolationType.STEP, drivers);

            Assert.AreEqual(1, drivers.Count);
            Assert.AreEqual(Interp.Step, drivers[0].Sampler.Interp);
        }

        // ── KHR_animation_pointer morph weights ("/nodes/{i}/weights/{j}") ──────

        [Test]
        public void MorphPointer_DrivesSingleBlendshape_DeltaOverFrame0()
        {
            var smr = MakeSmr(3, 1f);
            var times = new[] { 0f, 0.5f, 1f };
            var values = new[] { 0.2f, 0.6f, 1f }; // scalar per keyframe for ONE blendshape (not frame-major N-wide)

            var drivers = new List<MorphDriver>();
            KhrCharacterBaker.BuildMorphPointerDriver(smr, 1, times, values, InterpolationType.LINEAR, drivers);

            Assert.AreEqual(1, drivers.Count);
            Assert.AreEqual(1, drivers[0].BlendShapeIndex);                                  // the pointer's /weights/{j}
            Assert.That(drivers[0].DeltaValues, Is.EqualTo(new[] { 0f, 0.4f, 0.8f }).Within(1e-5f)); // delta over frame0 (0.2)
            Assert.AreEqual(Interp.Linear, drivers[0].Sampler.Interp);
            Assert.IsFalse(drivers[0].Sampler.SingleKey);
            Assert.AreSame(smr, drivers[0].Smr);
        }

        [Test]
        public void MorphPointerUsesMeshWeightAsBaseAndFallsBackToZero()
        {
            var smr = MakeSmr(2, 1f);
            var mesh = new GLTFMesh { Weights = new List<double> { 0.2d, 0.4d } };
            var root = new GLTFRoot
            {
                Meshes = new List<GLTFMesh> { mesh },
                Nodes = new List<Node>(),
            };
            root.Nodes.Add(new Node { Mesh = new MeshId { Id = 0, Root = root } });
            float baseValue = KhrCharacterBaker.ResolveMorphBaseValue(root, 0, 1);
            var drivers = new List<MorphDriver>();

            KhrCharacterBaker.BuildMorphPointerDriver(
                smr,
                1,
                new[] { 0f, 1f },
                new[] { 0.4f, 0.9f },
                InterpolationType.LINEAR,
                baseValue,
                drivers);

            Assert.That(baseValue, Is.EqualTo(0.4f));
            Assert.That(drivers[0].BaseValue, Is.EqualTo(0.4f));
            Assert.That(drivers[0].DeltaValues, Is.EqualTo(new[] { 0f, 0.5f }).Within(1e-6f));

            mesh.Weights = null;
            Assert.That(KhrCharacterBaker.ResolveMorphBaseValue(root, 0, 1), Is.Zero);
        }

        [Test]
        public void MorphPointer_SingleKey_StoresAbsoluteTarget()
        {
            var smr = MakeSmr(2, 1f);
            var drivers = new List<MorphDriver>();
            KhrCharacterBaker.BuildMorphPointerDriver(smr, 0, new[] { 0f }, new[] { 0.7f }, InterpolationType.LINEAR, drivers);

            Assert.AreEqual(1, drivers.Count);
            Assert.IsTrue(drivers[0].Sampler.SingleKey);
            Assert.AreEqual(0, drivers[0].BlendShapeIndex);
            Assert.AreEqual(0.7f, drivers[0].DeltaValues[0], 1e-5f);
        }

        [Test]
        public void MorphPointer_BlendShapeOutOfRange_Dropped()
        {
            var smr = MakeSmr(1, 1f); // only blendshape 0 exists
            var drivers = new List<MorphDriver>();
            KhrCharacterBaker.BuildMorphPointerDriver(smr, 5, new[] { 0f, 1f }, new[] { 0f, 1f }, InterpolationType.LINEAR, drivers);
            Assert.AreEqual(0, drivers.Count); // out-of-range target dropped, no throw
        }

        [Test]
        public void ParseNodeWeightsPointer_ValidAndInvalid()
        {
            Assert.IsTrue(KhrCharacterBaker.TryParseNodeWeightsPointer("/nodes/112/weights/3", out int node, out int shape));
            Assert.AreEqual(112, node);
            Assert.AreEqual(3, shape);

            Assert.IsFalse(KhrCharacterBaker.TryParseNodeWeightsPointer("/meshes/2/weights/1", out _, out _));    // not a node pointer
            Assert.IsFalse(KhrCharacterBaker.TryParseNodeWeightsPointer("/nodes/112/translation", out _, out _)); // not weights
            Assert.IsFalse(KhrCharacterBaker.TryParseNodeWeightsPointer("/nodes/x/weights/3", out _, out _));     // non-numeric
            Assert.IsFalse(KhrCharacterBaker.TryParseNodeWeightsPointer(null, out _, out _));
        }
    }
}
