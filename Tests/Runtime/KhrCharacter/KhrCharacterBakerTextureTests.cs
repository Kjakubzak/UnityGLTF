using System.Collections;
using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden-value tests for the texture baker seams (the _ST V-flip packing and the delta-over-rest UV driver)
    /// plus an end-to-end MaterialPropertyBlock UV drive.
    /// </summary>
    public class KhrCharacterBakerTextureTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        private MeshRenderer MakeRenderer()
        {
            var go = new GameObject("r", typeof(MeshFilter), typeof(MeshRenderer));
            _created.Add(go);
            return go.GetComponent<MeshRenderer>();
        }

        [Test]
        public void PackSt_AppliesVFlip()
        {
            // glTF scale (2,2), offset (0.25, 0.1) -> Unity _ST (2, 2, 0.25, 1 - 0.1 - 2)
            var st = KhrCharacterBaker.PackSt(new Vector2(2f, 2f), new Vector2(0.25f, 0.1f));
            Assert.AreEqual(2f, st.x, 1e-5f);
            Assert.AreEqual(2f, st.y, 1e-5f);
            Assert.AreEqual(0.25f, st.z, 1e-5f);
            Assert.AreEqual(1f - 0.1f - 2f, st.w, 1e-5f);
        }

        [Test]
        public void UvTransform_DeltaOverFrame0()
        {
            var r = MakeRenderer();
            int propId = Shader.PropertyToID("_BaseMap_ST");
            var times = new[] { 0f, 1f };
            var st = new[] { new Vector4(1f, 1f, 0f, 0f), new Vector4(1f, 1f, 0.5f, 0f) };
            var baseSt = new Vector4(1f, 1f, 0f, 0f);

            var drivers = new List<TextureDriver>();
            KhrCharacterBaker.BuildUvTransformDriver(r, 0, propId, times, st, baseSt, InterpolationType.LINEAR, drivers);

            Assert.AreEqual(1, drivers.Count);
            Assert.AreEqual(baseSt, drivers[0].BaseSt);
            AssertV4(Vector4.zero, drivers[0].StValues[0]);
            AssertV4(new Vector4(0f, 0f, 0.5f, 0f), drivers[0].StValues[1]);
        }

        [UnityTest]
        public IEnumerator Controller_AppliesUvToMaterialPropertyBlock()
        {
            var shader = Shader.Find("Unlit/Texture");
            if (shader == null) { Assert.Ignore("No suitable built-in shader available in this project."); yield break; }

            var go = new GameObject("quad", typeof(MeshFilter), typeof(MeshRenderer));
            _created.Add(go);
            var mr = go.GetComponent<MeshRenderer>();
            var mat = new Material(shader);
            _created.Add(mat);
            mr.sharedMaterial = mat;

            int propId = Shader.PropertyToID("_MainTex_ST");
            var driver = new TextureDriver
            {
                Renderer = mr,
                SubmeshSlot = 0,
                PropertyId = propId,
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                StValues = new[] { Vector4.zero, new Vector4(0f, 0f, 1f, 0f) },
                BaseSt = new Vector4(1f, 1f, 0f, 0f),
            };
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "scroll", Domains = ExpressionDomain.Texture, TextureDrivers = new[] { driver } }
                }
            };
            set.RebuildIndex();

            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("scroll", 1f);
            yield return null;

            var mpb = new MaterialPropertyBlock();
            mr.GetPropertyBlock(mpb, 0);
            AssertV4(new Vector4(1f, 1f, 1f, 0f), mpb.GetVector(propId)); // base (1,1,0,0) + delta (0,0,1,0)
        }

        private static void AssertV4(Vector4 e, Vector4 a)
        {
            Assert.AreEqual(e.x, a.x, 1e-4f);
            Assert.AreEqual(e.y, a.y, 1e-4f);
            Assert.AreEqual(e.z, a.z, 1e-4f);
            Assert.AreEqual(e.w, a.w, 1e-4f);
        }
    }
}
