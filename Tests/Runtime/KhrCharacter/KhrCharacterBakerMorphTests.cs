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
        public void Step_PreservesInterpolation()
        {
            var smr = MakeSmr(1, 1f);
            var drivers = new List<MorphDriver>();
            KhrCharacterBaker.BuildMorphDrivers(smr, new[] { 0f, 1f }, new[] { 0f, 1f }, InterpolationType.STEP, drivers);

            Assert.AreEqual(1, drivers.Count);
            Assert.AreEqual(Interp.Step, drivers[0].Sampler.Interp);
        }
    }
}
