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
            var shader = FindTestShader();

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

        [UnityTest]
        public IEnumerator SharedGltfMaterial_FansOutToEveryRendererUse()
        {
            var shader = FindTestShader();

            var material = new Material(shader);
            _created.Add(material);
            var first = MakeRenderer();
            var second = MakeRenderer();
            first.sharedMaterial = material;
            second.sharedMaterial = material;

            var root = new GLTFRoot
            {
                Meshes = new List<GLTFMesh>(),
                Nodes = new List<Node>(),
            };
            root.Meshes.Add(new GLTFMesh
            {
                Primitives = new List<MeshPrimitive>
                {
                    new MeshPrimitive { Material = new MaterialId { Id = 0, Root = root } },
                },
            });
            root.Nodes.Add(new Node { Mesh = new MeshId { Id = 0, Root = root } });
            root.Nodes.Add(new Node { Mesh = new MeshId { Id = 0, Root = root } });
            var nodeObjects = new Dictionary<int, GameObject>
            {
                [0] = first.gameObject,
                [1] = second.gameObject,
            };

            var bindings = KhrCharacterBaker.ResolveRendererSlots(root, nodeObjects, 0);
            Assert.That(bindings, Has.Count.EqualTo(2));

            int propId = Shader.PropertyToID("_MainTex_ST");
            var baseSt = new Vector4(1f, 1f, 0f, 0f);
            var stValues = new[] { baseSt, new Vector4(1f, 1f, 0.5f, 0f) };
            var drivers = new List<TextureDriver>();
            foreach (var binding in bindings)
                KhrCharacterBaker.BuildUvTransformDriver(
                    binding.Renderer,
                    binding.Slot,
                    propId,
                    new[] { 0f, 1f },
                    stValues,
                    baseSt,
                    InterpolationType.LINEAR,
                    drivers,
                    "_MainTex",
                    "pbrMetallicRoughness/baseColorTexture",
                    TextureTransformTarget.Offset);

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "scroll",
                        Domains = ExpressionDomain.Texture,
                        TextureDrivers = drivers.ToArray(),
                    },
                },
            };
            set.RebuildIndex();
            var controller = first.gameObject.AddComponent<ExpressionController>();
            controller.Initialize(set);
            controller.SetWeight("scroll", 1f);
            yield return null;

            var block = new MaterialPropertyBlock();
            first.GetPropertyBlock(block, 0);
            AssertV4(stValues[1], block.GetVector(propId));
            block.Clear();
            second.GetPropertyBlock(block, 0);
            AssertV4(stValues[1], block.GetVector(propId));
        }

        [UnityTest]
        public IEnumerator ScaleAndOffset_KeepIndependentSamplers()
        {
            var shader = FindTestShader();

            var renderer = MakeRenderer();
            var material = new Material(shader);
            _created.Add(material);
            renderer.sharedMaterial = material;
            int propId = Shader.PropertyToID("_MainTex_ST");
            var baseSt = new Vector4(1f, 1f, 0f, 0f);
            var drivers = new List<TextureDriver>();

            KhrCharacterBaker.BuildUvTransformDriver(
                renderer,
                0,
                propId,
                new[] { 0f, 1f },
                new[] { baseSt, new Vector4(2f, 2f, 0f, -1f) },
                baseSt,
                InterpolationType.LINEAR,
                drivers,
                transformTarget: TextureTransformTarget.Scale);
            KhrCharacterBaker.BuildUvTransformDriver(
                renderer,
                0,
                propId,
                new[] { 0f, 0.25f, 1f },
                new[]
                {
                    baseSt,
                    new Vector4(1f, 1f, 0.25f, 0f),
                    new Vector4(1f, 1f, 0.75f, 0f),
                },
                baseSt,
                InterpolationType.STEP,
                drivers,
                transformTarget: TextureTransformTarget.Offset);

            Assert.That(drivers, Has.Count.EqualTo(2));
            Assert.That(drivers[0].Sampler.Times, Has.Length.EqualTo(2));
            Assert.That(drivers[0].Sampler.Interp, Is.EqualTo(Interp.Linear));
            Assert.That(drivers[0].TransformTarget, Is.EqualTo(TextureTransformTarget.Scale));
            Assert.That(drivers[1].Sampler.Times, Has.Length.EqualTo(3));
            Assert.That(drivers[1].Sampler.Interp, Is.EqualTo(Interp.Step));
            Assert.That(drivers[1].TransformTarget, Is.EqualTo(TextureTransformTarget.Offset));

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "mixedUv",
                        Domains = ExpressionDomain.Texture,
                        TextureDrivers = drivers.ToArray(),
                    },
                },
            };
            set.RebuildIndex();
            var controller = renderer.gameObject.AddComponent<ExpressionController>();
            controller.Initialize(set);
            controller.SetWeight("mixedUv", 0.5f);
            yield return null;

            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block, 0);
            AssertV4(new Vector4(1.5f, 1.5f, 0.25f, -0.5f), block.GetVector(propId));
        }

        private static Shader FindTestShader()
        {
            var shader = Shader.Find("Unlit/Texture")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Hidden/InternalErrorShader");
            Assert.IsNotNull(shader, "A deterministic built-in shader is required for texture adapter tests.");
            return shader;
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
