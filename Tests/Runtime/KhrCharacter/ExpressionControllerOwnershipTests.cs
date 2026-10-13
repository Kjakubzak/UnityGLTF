using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    public class ExpressionControllerOwnershipTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var value in _created)
                if (value != null) Object.DestroyImmediate(value);
            _created.Clear();
        }

        [UnityTest]
        public IEnumerator IntegratedInactiveMorphAndJointPreserveLiveUnderlay()
        {
            var root = BuildMorphAndJointSet(out var set, out var smr, out var joint);
            var controller = root.AddComponent<ExpressionController>();
            controller.OwnershipMode = ExpressionControllerOwnershipMode.Integrated;
            controller.Initialize(set);

            controller.SetWeight(0, 1f);
            yield return null;
            smr.SetBlendShapeWeight(0, 37f);
            joint.localPosition = new Vector3(9f, 8f, 7f);
            controller.SetWeight(0, 0f);
            yield return null;

            Assert.That(smr.GetBlendShapeWeight(0), Is.EqualTo(37f).Within(1e-4f));
            Assert.That(joint.localPosition, Is.EqualTo(new Vector3(9f, 8f, 7f)));
        }

        [UnityTest]
        public IEnumerator StandaloneInactiveMorphAndJointRestoreBakedBase()
        {
            var root = BuildMorphAndJointSet(out var set, out var smr, out var joint);
            var controller = root.AddComponent<ExpressionController>();
            controller.Initialize(set);

            controller.SetWeight(0, 1f);
            yield return null;
            smr.SetBlendShapeWeight(0, 37f);
            joint.localPosition = new Vector3(9f, 8f, 7f);
            controller.SetWeight(0, 0f);
            yield return null;

            Assert.That(smr.GetBlendShapeWeight(0), Is.EqualTo(25f).Within(1e-4f));
            Assert.That(joint.localPosition, Is.EqualTo(new Vector3(1f, 2f, 3f)));
        }

        [UnityTest]
        public IEnumerator IntegratedPartlyActiveMorphWritesOnlyCoveredWeights()
        {
            var root = BuildMorphAndJointSet(out var set, out var smr, out _, blendShapeCount: 2);
            var controller = root.AddComponent<ExpressionController>();
            controller.OwnershipMode = ExpressionControllerOwnershipMode.Integrated;
            controller.Initialize(set);

            smr.SetBlendShapeWeight(1, 37f);
            controller.SetWeight(0, 1f);
            yield return null;

            Assert.That(smr.GetBlendShapeWeight(0), Is.EqualTo(75f).Within(1e-4f), "the covered weight is written");
            Assert.That(smr.GetBlendShapeWeight(1), Is.EqualTo(37f).Within(1e-4f),
                "a weight no record covers keeps the earlier writer's value");
        }

        [UnityTest]
        public IEnumerator IntegratedInactiveTexturePreservesLivePropertyBlock()
        {
            var renderer = BuildTextureSet(out var set, out int propertyId, out var root);
            var controller = root.AddComponent<ExpressionController>();
            controller.OwnershipMode = ExpressionControllerOwnershipMode.Integrated;
            controller.Initialize(set);
            var underlay = new Vector4(2f, 3f, 4f, 5f);

            controller.SetWeight(0, 1f);
            yield return null;
            SetPropertyBlock(renderer, propertyId, underlay);

            controller.SetWeight(0, 0f);
            yield return null;

            Assert.That(GetPropertyBlock(renderer, propertyId), Is.EqualTo(underlay));
        }

        [UnityTest]
        public IEnumerator IntegratedPartlyActiveTextureKeepsTheEarlierScale()
        {
            var renderer = BuildTextureSet(out var set, out int propertyId, out var root);
            var controller = root.AddComponent<ExpressionController>();
            controller.OwnershipMode = ExpressionControllerOwnershipMode.Integrated;
            controller.Initialize(set);

            // The expression lists only the offset: at full weight its record is the glTF offset (1, 0).
            renderer.sharedMaterial.SetTextureScale("_MainTex", new Vector2(2f, 3f));
            controller.SetWeight(0, 1f);
            yield return null;
            Assert.That(GetPropertyBlock(renderer, propertyId), Is.EqualTo(new Vector4(2f, 3f, 1f, -2f)),
                "the material's scale is kept under the recorded offset");

            yield return null;
            Assert.That(GetPropertyBlock(renderer, propertyId), Is.EqualTo(new Vector4(2f, 3f, 1f, -2f)),
                "reading back its own write keeps the same scale");

            SetPropertyBlock(renderer, propertyId, new Vector4(4f, 0.5f, 9f, 9f));
            yield return null;
            Assert.That(GetPropertyBlock(renderer, propertyId), Is.EqualTo(new Vector4(4f, 0.5f, 1f, 0.5f)),
                "a property block's scale is kept under the recorded offset");
        }

        [UnityTest]
        public IEnumerator StandaloneInactiveTextureRestoresBakedBase()
        {
            var renderer = BuildTextureSet(out var set, out int propertyId, out var root);
            var controller = root.AddComponent<ExpressionController>();
            controller.Initialize(set);

            controller.SetWeight(0, 1f);
            yield return null;
            SetPropertyBlock(renderer, propertyId, new Vector4(2f, 3f, 4f, 5f));

            controller.SetWeight(0, 0f);
            yield return null;

            Assert.That(
                GetPropertyBlock(renderer, propertyId),
                Is.EqualTo(new Vector4(1f, 1f, 0f, 0f)));
        }

        private GameObject BuildMorphAndJointSet(
            out CharacterExpressionSet set,
            out SkinnedMeshRenderer smr,
            out Transform joint,
            int blendShapeCount = 1)
        {
            var root = new GameObject("character");
            _created.Add(root);
            var mesh = new Mesh { name = "ownership-test" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            for (int index = 0; index < blendShapeCount; index++)
                mesh.AddBlendShapeFrame(
                    "shape" + index,
                    100f,
                    new[] { Vector3.one, Vector3.one, Vector3.one },
                    null,
                    null);
            _created.Add(mesh);
            smr = root.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;

            var jointObject = new GameObject("joint");
            _created.Add(jointObject);
            jointObject.transform.SetParent(root.transform, false);
            joint = jointObject.transform;

            set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "expression",
                        Domains = ExpressionDomain.Morph | ExpressionDomain.Joint,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Smr = smr,
                                BlendShapeIndex = 0,
                                BaseValue = 0.25f,
                                Sampler = LinearSampler(),
                                DeltaValues = new[] { 0f, 0.5f },
                            },
                        },
                        JointDrivers = new[]
                        {
                            new JointDriver
                            {
                                Target = joint,
                                Channel = TrsChannel.Translation,
                                BaseVec = new Vector3(1f, 2f, 3f),
                                Sampler = LinearSampler(),
                                DeltaVec = new[] { Vector3.zero, Vector3.one },
                            },
                        },
                    },
                },
            };
            set.RebuildIndex();
            return root;
        }

        private MeshRenderer BuildTextureSet(
            out CharacterExpressionSet set,
            out int propertyId,
            out GameObject root)
        {
            var shader = Shader.Find("Unlit/Texture")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Hidden/InternalErrorShader");
            Assert.IsNotNull(shader, "A deterministic built-in shader is required for ownership tests.");
            root = new GameObject("texture", typeof(MeshFilter), typeof(MeshRenderer));
            _created.Add(root);
            var renderer = root.GetComponent<MeshRenderer>();
            var material = new Material(shader);
            _created.Add(material);
            renderer.sharedMaterial = material;
            propertyId = Shader.PropertyToID("_MainTex_ST");

            set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "texture",
                        Domains = ExpressionDomain.Texture,
                        TextureDrivers = new[]
                        {
                            new TextureDriver
                            {
                                Renderer = renderer,
                                SubmeshSlot = 0,
                                PropertyId = propertyId,
                                BaseSt = new Vector4(1f, 1f, 0f, 0f),
                                Sampler = LinearSampler(),
                                StValues = new[] { Vector4.zero, new Vector4(0f, 0f, 1f, 0f) },
                            },
                        },
                    },
                },
            };
            set.RebuildIndex();
            return renderer;
        }

        private static Sampler LinearSampler()
        {
            return new Sampler
            {
                Times = new[] { 0f, 1f },
                Interp = Interp.Linear,
                SingleKey = false,
            };
        }

        private static void SetPropertyBlock(Renderer renderer, int propertyId, Vector4 value)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block, 0);
            block.SetVector(propertyId, value);
            renderer.SetPropertyBlock(block, 0);
        }

        private static Vector4 GetPropertyBlock(Renderer renderer, int propertyId)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block, 0);
            return block.GetVector(propertyId);
        }
    }
}
