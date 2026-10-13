using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// The expression controller of imported characters: it evaluates the imported response set and writes the composed
    /// records through bindings to the imported objects, rehydrates in a copy, keeps UnityGLTF's camera turn and import
    /// scale factor, writes texture transforms as the importer writes materials (flipped textures included), and
    /// previews an authored set as its export evaluates.
    /// </summary>
    public class ExpressionControllerImportTests
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
        public IEnumerator ImportedController_ComposesAndRestoresTheAuthoredTranslation()
        {
            GameObject scene = null;
            yield return CharacterGlbBuilder.TranslationCharacter("a", "b")
                .Import(plugin => plugin.CreateExpressionController = true, loaded => scene = loaded);
            _created.Add(scene);
            var hub = scene.GetComponent<KhrCharacter>();
            var controller = hub.Expressions;
            var body = FindNode(scene, "body");

            Assert.IsNotNull(controller);
            Assert.AreSame(hub.ExpressionResponses, controller.Responses);
            Assert.That(controller.Bindings.Count, Is.EqualTo(1));
            Assert.AreSame(body, controller.Bindings[0].Node);

            controller.SetWeight(0, 1f);
            controller.SetWeight(1, 0.5f);
            yield return null;
            Assert.That(body.localPosition, Is.EqualTo(new Vector3(-1.5f, 0f, 0f)),
                "b + (1 - 0) + (0.5 - 0) in glTF space, X-flipped for Unity");

            controller.ResetAll();
            yield return null;
            Assert.That(body.localPosition, Is.EqualTo(Vector3.zero), "Standalone returns to the authored value");
        }

        [UnityTest]
        public IEnumerator ImportedController_RehydratesInAnInstantiatedCopy()
        {
            GameObject scene = null;
            yield return CharacterGlbBuilder.TranslationCharacter("a")
                .Import(plugin => plugin.CreateExpressionController = true, loaded => scene = loaded);
            _created.Add(scene);

            var copy = Object.Instantiate(scene);
            _created.Add(copy);
            var controller = copy.GetComponent<ExpressionController>();
            var body = FindNode(copy, "body");

            Assert.AreSame(copy.GetComponent<ExpressionResponseSet>(), controller.Responses,
                "the copy evaluates its own response set, not a legacy authored set");
            Assert.That(controller.Count, Is.EqualTo(1));
            Assert.AreSame(body, controller.Bindings[0].Node, "the bindings point into the copy");

            controller.SetWeight(0, 1f);
            yield return null;
            Assert.That(body.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
            Assert.That(FindNode(scene, "body").localPosition, Is.EqualTo(Vector3.zero));
        }

        [UnityTest]
        public IEnumerator ImportedController_KeepsTheCameraForwardAxisTurn()
        {
            float s0 = Mathf.Sin(15f * Mathf.Deg2Rad), c0 = Mathf.Cos(15f * Mathf.Deg2Rad);
            float s1 = Mathf.Sin(45f * Mathf.Deg2Rad), c1 = Mathf.Cos(45f * Mathf.Deg2Rad);
            var glb = new CharacterGlbBuilder();
            int input = glb.AddFloatAccessor("SCALAR", 2, new[] { 0f, 1f }, true);
            int output = glb.AddFloatAccessor("VEC4", 2, new[] { 0f, s0, 0f, c0, 0f, s1, 0f, c1 });
            glb.Json["extensionsUsed"] = new JArray(
                "KHR_character", "KHR_character_expression", "KHR_character_expression_joint");
            glb.Json["cameras"] = new JArray(new JObject
            {
                { "type", "perspective" },
                { "perspective", new JObject { { "yfov", 0.8 }, { "znear", 0.1 } } },
            });
            glb.Json["nodes"] = new JArray(new JObject
            {
                { "name", "eye" },
                { "camera", 0 },
                { "rotation", new JArray(0f, s0, 0f, c0) },
            });
            glb.Json["animations"] = new JArray(SingleChannelAnimation(
                input, output, new JObject { { "node", 0 }, { "path", "rotation" } }));
            glb.Json["extensions"] = CharacterExtensions(Entry("turn", 0, "KHR_character_expression_joint", 0));

            GameObject scene = null;
            yield return glb.Import(plugin => plugin.CreateExpressionController = true, loaded => scene = loaded);
            _created.Add(scene);
            var eye = FindNode(scene, "eye");
            var imported = eye.localRotation;
            var controller = scene.GetComponent<ExpressionController>();
            var forwardAxisTurn = new Quaternion(0f, -1f, 0f, 0f);

            Assert.IsNotNull(eye.GetComponent<Camera>(), "the importer instantiated the core camera");
            Assert.IsTrue(controller.Bindings[0].NodeHasForwardAxisConversion);

            controller.SetWeight(0, 1f);
            yield return null;
            Assert.That(Quaternion.Angle(eye.localRotation, new Quaternion(0f, -s1, 0f, c1) * forwardAxisTurn),
                Is.LessThan(1e-3f), "the sampled rotation keeps UnityGLTF's camera turn");

            controller.SetWeight(0, 0f);
            yield return null;
            Assert.That(Quaternion.Angle(eye.localRotation, imported), Is.LessThan(1e-3f),
                "the authored rotation is restored with the turn");
        }

        [UnityTest]
        public IEnumerator ImportedController_WritesTheWholeMorphVectorAndIntegratedOnlyCoveredWeights()
        {
            var glb = new CharacterGlbBuilder();
            int position = glb.AddFloatAccessor("VEC3", 3, new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f });
            int displacement = glb.AddFloatAccessor("VEC3", 3, new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f });
            int input = glb.AddFloatAccessor("SCALAR", 2, new[] { 0f, 1f }, true);
            int whole = glb.AddFloatAccessor("SCALAR", 4, new[] { 0.25f, 0.5f, 0.5f, 0.5f });
            int element = glb.AddFloatAccessor("SCALAR", 2, new[] { 0.5f, 0.75f });
            glb.Json["extensionsUsed"] = new JArray(
                "KHR_character", "KHR_character_expression", "KHR_character_expression_morphtarget",
                "KHR_animation_pointer");
            glb.Json["meshes"] = new JArray(new JObject
            {
                {
                    "primitives", new JArray(new JObject
                    {
                        { "attributes", new JObject { { "POSITION", position } } },
                        {
                            "targets", new JArray(
                                new JObject { { "POSITION", displacement } },
                                new JObject { { "POSITION", displacement } })
                        },
                    })
                },
                { "weights", new JArray(0.25, 0.5) },
            });
            glb.Json["nodes"] = new JArray(new JObject { { "name", "face" }, { "mesh", 0 } });
            var pointer = new JObject { { "KHR_animation_pointer", new JObject { { "pointer", "/nodes/0/weights/1" } } } };
            glb.Json["animations"] = new JArray(
                SingleChannelAnimation(input, whole, new JObject { { "node", 0 }, { "path", "weights" } }),
                SingleChannelAnimation(input, element, new JObject { { "path", "pointer" }, { "extensions", pointer } }));
            glb.Json["extensions"] = CharacterExtensions(
                Entry("open", 0, "KHR_character_expression_morphtarget", 0),
                Entry("lift", 1, "KHR_character_expression_morphtarget", 0));

            GameObject scene = null;
            yield return glb.Import(plugin => plugin.CreateExpressionController = true, loaded => scene = loaded);
            _created.Add(scene);
            var controller = scene.GetComponent<ExpressionController>();
            var face = scene.GetComponentInChildren<SkinnedMeshRenderer>();

            controller.SetWeight(0, 1f);
            controller.SetWeight(1, 1f);
            yield return null;
            Assert.That(face.GetBlendShapeWeight(0), Is.EqualTo(0.5f), "0.25 + (0.5 - 0.25)");
            Assert.That(face.GetBlendShapeWeight(1), Is.EqualTo(0.75f), "0.5 + (0.5 - 0.5) + (0.75 - 0.5)");

            controller.OwnershipMode = ExpressionControllerOwnershipMode.Integrated;
            controller.SetWeight(0, 0f);
            face.SetBlendShapeWeight(0, 0.9f);
            yield return null;
            Assert.That(face.GetBlendShapeWeight(0), Is.EqualTo(0.9f), "Integrated keeps a weight no record covers");
            Assert.That(face.GetBlendShapeWeight(1), Is.EqualTo(0.75f));

            controller.OwnershipMode = ExpressionControllerOwnershipMode.Standalone;
            yield return null;
            Assert.That(face.GetBlendShapeWeight(0), Is.EqualTo(0.25f), "Standalone writes the complete vector");
        }

        [UnityTest]
        public IEnumerator TextureApplicator_MatchesAMaterialImportedWithTheFinalTransform()
        {
            GameObject animated = null;
            yield return TexturedCharacter(new[] { 0.25f, 0.5f }, new[] { 1f, 1f }, 0.5f,
                    new[] { 0.75f, 0.25f }, new[] { 2f, 4f }, 1.5f)
                .Import(plugin => plugin.CreateExpressionController = true, loaded => animated = loaded);
            _created.Add(animated);
            GameObject reference = null;
            yield return TexturedCharacter(new[] { 0.75f, 0.25f }, new[] { 2f, 4f }, 1.5f)
                .Import(plugin => plugin.CreateExpressionController = true, loaded => reference = loaded);
            _created.Add(reference);

            var controller = animated.GetComponent<ExpressionController>();
            ExpressionTargetBinding binding = null;
            foreach (var candidate in controller.Bindings)
                if (candidate.Kind == ExpressionPropertyKind.TextureOffset) binding = candidate;
            Assert.IsNotNull(binding);
            Assert.IsNotNull(binding.TextureStProperty);
            Assert.IsNotNull(binding.TextureRotationProperty);

            controller.SetWeight(0, 1f);
            yield return null;
            var block = new MaterialPropertyBlock();
            animated.GetComponentInChildren<Renderer>().GetPropertyBlock(block, 0);
            var material = reference.GetComponentInChildren<Renderer>().sharedMaterial;

            Assert.That(block.GetVector(binding.TextureStProperty),
                Is.EqualTo(material.GetVector(binding.TextureStProperty)));
            Assert.That(block.GetFloat(binding.TextureRotationProperty),
                Is.EqualTo(material.GetFloat(binding.TextureRotationProperty)));
        }

        [UnityTest]
        public IEnumerator Applicator_ScalesTranslationsAndMirrorsFlippedTextures()
        {
            var host = new GameObject("host");
            _created.Add(host);
            var node = new GameObject("node").transform;
            node.SetParent(host.transform, false);
            var panel = MakeTexturedPanel(host.transform);
            var responses = BindResponses(ResponseEntry("move",
                Ramp(NodeTranslation, ExpressionPropertyKind.NodeTranslation, KhrCharacterExtensionNames.ExpressionJoint,
                    new[] { 1d, 2d, 3d }, new[] { 2f, 2f, 3f }),
                Ramp(TextureOffset, ExpressionPropertyKind.TextureOffset, KhrCharacterExtensionNames.ExpressionTextureTransform,
                    new[] { 0.25d, 0.5d }, new[] { 0.75f, 0.25f }),
                Ramp(TextureScale, ExpressionPropertyKind.TextureScale, KhrCharacterExtensionNames.ExpressionTextureTransform,
                    new[] { 1d, 1d }, new[] { 2f, 4f })));
            var bindings = new[]
            {
                new ExpressionTargetBinding
                {
                    Property = NodeTranslation,
                    Kind = ExpressionPropertyKind.NodeTranslation,
                    AuthoredInitial = new[] { 1d, 2d, 3d },
                    Node = node,
                    TranslationScale = 2f,
                },
                FlippedTextureBinding(TextureOffset, ExpressionPropertyKind.TextureOffset, new[] { 0.25d, 0.5d }, panel),
                FlippedTextureBinding(TextureScale, ExpressionPropertyKind.TextureScale, new[] { 1d, 1d }, panel),
            };
            var controller = host.AddComponent<ExpressionController>();
            controller.Initialize(null, responses, bindings);

            controller.SetWeight(0, 1f);
            yield return null;
            Assert.That(node.localPosition, Is.EqualTo(new Vector3(-4f, 4f, 6f)), "2 * (-x, y, z) of (2, 2, 3)");
            Assert.That(PanelSt(panel), Is.EqualTo(new Vector4(2f, -4f, 0.75f, 4.25f)),
                "offset (0.75, 0.25) and scale (2, 4) as (s.x, -s.y, o.x, s.y + o.y)");

            controller.SetWeight(0, 0f);
            yield return null;
            Assert.That(node.localPosition, Is.EqualTo(new Vector3(-2f, 4f, 6f)), "the Standalone reset is scaled too");
            Assert.That(PanelSt(panel), Is.EqualTo(new Vector4(1f, -1f, 0.25f, 1.5f)),
                "the authored transform is mirrored too");
        }

        [UnityTest]
        public IEnumerator Applicator_IntegratedFlippedTextureKeepsTheEarlierHalf()
        {
            var host = new GameObject("host");
            _created.Add(host);
            var panel = MakeTexturedPanel(host.transform);
            var responses = BindResponses(
                ResponseEntry("scroll",
                    Ramp(TextureOffset, ExpressionPropertyKind.TextureOffset,
                        KhrCharacterExtensionNames.ExpressionTextureTransform, new[] { 0.25d, 0.5d }, new[] { 0.75f, 0.25f })),
                ResponseEntry("zoom",
                    Ramp(TextureScale, ExpressionPropertyKind.TextureScale,
                        KhrCharacterExtensionNames.ExpressionTextureTransform, new[] { 1d, 1d }, new[] { 4f, 2f })));
            var bindings = new[]
            {
                FlippedTextureBinding(TextureOffset, ExpressionPropertyKind.TextureOffset, new[] { 0.25d, 0.5d }, panel),
                FlippedTextureBinding(TextureScale, ExpressionPropertyKind.TextureScale, new[] { 1d, 1d }, panel),
            };
            var controller = host.AddComponent<ExpressionController>();
            controller.OwnershipMode = ExpressionControllerOwnershipMode.Integrated;
            controller.Initialize(null, responses, bindings);
            // A flipped _ST of glTF scale (2, 3) and offset (0.5, 1).
            var underlay = new Vector4(2f, -3f, 0.5f, 4f);

            SetPanelSt(panel, underlay);
            controller.SetWeight(0, 1f);
            yield return null;
            Assert.That(PanelSt(panel), Is.EqualTo(new Vector4(2f, -3f, 0.75f, 3.25f)),
                "the recorded offset under the earlier scale");

            SetPanelSt(panel, underlay);
            controller.SetWeight(0, 0f);
            controller.SetWeight(1, 1f);
            yield return null;
            Assert.That(PanelSt(panel), Is.EqualTo(new Vector4(4f, -2f, 0.5f, 3f)),
                "the recorded scale over the earlier offset");
        }

        [UnityTest]
        public IEnumerator AuthoredSet_AndItsExportImport_ProduceIdenticalTargets()
        {
            var root = new GameObject("char");
            _created.Add(root);
            var face = MakeMorphRenderer(root.transform, 2);
            face.SetBlendShapeWeight(0, 0.2f);
            var jaw = new GameObject("jaw").transform;
            jaw.SetParent(root.transform, false);
            jaw.localPosition = new Vector3(1f, 0f, 0f);
            jaw.localRotation = Quaternion.Euler(0f, 30f, 0f);
            var panel = MakeTexturedPanel(root.transform);

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "smile",
                        MorphDrivers = new[]
                        {
                            Morph(face, 0, 0.2f, new[] { 0f, 0.5f, 1f }, new[] { 0f, 0.3f, 0.8f }),
                        },
                        JointDrivers = new[]
                        {
                            Translation(jaw, new Vector3(1f, 0f, 0f), new Vector3(0.1f, 0.2f, 0.3f)),
                        },
                    },
                    new ExpressionTrack
                    {
                        Name = "jawOpen",
                        MorphDrivers = new[]
                        {
                            Morph(face, 0, 0.2f, new[] { 0f, 1f }, new[] { 0f, 0.5f }),
                            Morph(face, 1, 0f, new[] { 0f, 1f }, new[] { 0f, 1f }),
                        },
                        JointDrivers = new[]
                        {
                            new JointDriver
                            {
                                Target = jaw,
                                Channel = TrsChannel.Rotation,
                                Sampler = Linear(),
                                BaseQuat = Quaternion.Euler(0f, 30f, 0f),
                                DeltaQuat = new[] { Quaternion.identity, Quaternion.Euler(20f, 0f, 0f) },
                            },
                            Translation(jaw, new Vector3(1f, 0f, 0f), new Vector3(0f, 0.5f, 0f)),
                        },
                    },
                    new ExpressionTrack
                    {
                        Name = "grow",
                        JointDrivers = new[]
                        {
                            new JointDriver
                            {
                                Target = jaw,
                                Channel = TrsChannel.Scale,
                                Sampler = Linear(),
                                BaseVec = Vector3.one,
                                DeltaVec = new[] { Vector3.zero, Vector3.one },
                            },
                        },
                    },
                    new ExpressionTrack
                    {
                        Name = "scroll",
                        TextureDrivers = new[]
                        {
                            new TextureDriver
                            {
                                Renderer = panel,
                                SubmeshSlot = 0,
                                PropertyId = Shader.PropertyToID("_MainTex_ST"),
                                PropertyName = "_MainTex",
                                GltfTextureSlot = "pbrMetallicRoughness/baseColorTexture",
                                Sampler = Linear(),
                                BaseSt = new Vector4(1f, 1f, 0f, 0f),
                                StValues = new[] { Vector4.zero, new Vector4(0f, 0f, 0.5f, 0f) },
                            },
                        },
                    },
                },
            };
            var authored = root.AddComponent<ExpressionController>();
            authored.Initialize(set);
            byte[] glb = Export(root);

            for (int index = 0; index < authored.Count; index++) authored.SetWeight(index, 0.5f);
            yield return null;
            var expected = Capture(face, jaw, panel, TextureBinding(authored));

            GameObject scene = null;
            yield return CharacterGlbBuilder.ImportGlb(
                glb, plugin => plugin.CreateExpressionController = true, loaded => scene = loaded);
            _created.Add(scene);
            var imported = scene.GetComponent<ExpressionController>();
            Assert.That(imported.Count, Is.EqualTo(authored.Count));
            for (int index = 0; index < imported.Count; index++) imported.SetWeight(index, 0.5f);
            yield return null;
            var actual = Capture(
                scene.GetComponentInChildren<SkinnedMeshRenderer>(),
                FindNode(scene, "jaw"),
                FindNode(scene, "panel").GetComponent<Renderer>(),
                TextureBinding(imported));

            Assert.That(actual.Weights, Is.EqualTo(expected.Weights), "blendshape weights");
            Assert.That(actual.Position, Is.EqualTo(expected.Position), "jaw translation");
            Assert.That(actual.Rotation, Is.EqualTo(expected.Rotation), "jaw rotation");
            Assert.That(actual.Scale, Is.EqualTo(expected.Scale), "jaw scale");
            Assert.That(actual.TextureSt, Is.EqualTo(expected.TextureSt), "texture scale and offset");
        }

        private sealed class Targets
        {
            public float[] Weights;
            public Vector3 Position;
            public Vector4 Rotation;
            public Vector3 Scale;
            public Vector4 TextureSt;
        }

        private static Targets Capture(SkinnedMeshRenderer face, Transform jaw, Renderer panel, ExpressionTargetBinding texture)
        {
            var block = new MaterialPropertyBlock();
            panel.GetPropertyBlock(block, 0);
            var rotation = jaw.localRotation;
            return new Targets
            {
                Weights = new[] { face.GetBlendShapeWeight(0), face.GetBlendShapeWeight(1) },
                Position = jaw.localPosition,
                Rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w),
                Scale = jaw.localScale,
                TextureSt = block.GetVector(texture.TextureStProperty),
            };
        }

        private static ExpressionTargetBinding TextureBinding(ExpressionController controller)
        {
            foreach (var binding in controller.Bindings)
                if (binding.Kind == ExpressionPropertyKind.TextureOffset) return binding;
            Assert.Fail("the controller binds no texture offset");
            return null;
        }

        private static byte[] Export(GameObject root)
        {
            var settings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in settings.ExportPlugins)
                if (plugin is KhrCharacterExportPlugin || plugin is AnimationPointerExport)
                    plugin.Enabled = true;
            var exporter = new GLTFSceneExporter(new[] { root.transform }, new ExportContext(settings));
            return exporter.SaveGLBToByteArray("scene");
        }

        private CharacterGlbBuilder TexturedCharacter(
            float[] offset,
            float[] scale,
            float rotation,
            float[] finalOffset = null,
            float[] finalScale = null,
            float finalRotation = 0f)
        {
            var glb = new CharacterGlbBuilder();
            int position = glb.AddFloatAccessor("VEC3", 3, new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f });
            int uv = glb.AddFloatAccessor("VEC2", 3, new[] { 0f, 0f, 1f, 0f, 0f, 1f });
            int image = glb.AddBufferView(MakeTexture().EncodeToPNG());
            glb.Json["images"] = new JArray(new JObject { { "bufferView", image }, { "mimeType", "image/png" } });
            glb.Json["textures"] = new JArray(new JObject { { "source", 0 } });
            var transform = new JObject
            {
                { "offset", new JArray(offset[0], offset[1]) },
                { "scale", new JArray(scale[0], scale[1]) },
                { "rotation", rotation },
            };
            var baseColor = new JObject
            {
                { "index", 0 },
                { "extensions", new JObject { { "KHR_texture_transform", transform } } },
            };
            glb.Json["materials"] = new JArray(new JObject
            {
                { "pbrMetallicRoughness", new JObject { { "baseColorTexture", baseColor } } },
            });
            glb.Json["meshes"] = new JArray(new JObject
            {
                {
                    "primitives", new JArray(new JObject
                    {
                        { "attributes", new JObject { { "POSITION", position }, { "TEXCOORD_0", uv } } },
                        { "material", 0 },
                    })
                },
            });
            glb.Json["nodes"] = new JArray(new JObject { { "name", "panel" }, { "mesh", 0 } });
            var used = new JArray("KHR_character", "KHR_texture_transform");
            glb.Json["extensionsUsed"] = used;
            if (finalOffset == null)
            {
                glb.Json["extensions"] = new JObject { { "KHR_character", new JObject() } };
                return glb;
            }

            used.Add("KHR_character_expression");
            used.Add("KHR_character_expression_texture_transform");
            used.Add("KHR_animation_pointer");
            int input = glb.AddFloatAccessor("SCALAR", 2, new[] { 0f, 1f }, true);
            int offsets = glb.AddFloatAccessor("VEC2", 2, new[] { offset[0], offset[1], finalOffset[0], finalOffset[1] });
            int scales = glb.AddFloatAccessor("VEC2", 2, new[] { scale[0], scale[1], finalScale[0], finalScale[1] });
            int rotations = glb.AddFloatAccessor("SCALAR", 2, new[] { rotation, finalRotation });
            const string basePath = "/materials/0/pbrMetallicRoughness/baseColorTexture/extensions/KHR_texture_transform/";
            var samplers = new JArray();
            var channels = new JArray();
            var outputs = new[] { offsets, scales, rotations };
            var components = new[] { "offset", "scale", "rotation" };
            for (int index = 0; index < outputs.Length; index++)
            {
                samplers.Add(new JObject { { "input", input }, { "output", outputs[index] } });
                var extension = new JObject
                {
                    { "KHR_animation_pointer", new JObject { { "pointer", basePath + components[index] } } },
                };
                channels.Add(new JObject
                {
                    { "sampler", index },
                    { "target", new JObject { { "path", "pointer" }, { "extensions", extension } } },
                });
            }
            glb.Json["animations"] = new JArray(new JObject { { "samplers", samplers }, { "channels", channels } });
            glb.Json["extensions"] = CharacterExtensions(
                Entry("uv", 0, "KHR_character_expression_texture_transform", 0, 1, 2));
            return glb;
        }

        private static JObject SingleChannelAnimation(int input, int output, JObject target)
            => new JObject
            {
                { "samplers", new JArray(new JObject { { "input", input }, { "output", output } }) },
                { "channels", new JArray(new JObject { { "sampler", 0 }, { "target", target } }) },
            };

        private static JObject Entry(string name, int animation, string listing, params int[] channels)
            => new JObject
            {
                { "name", name },
                { "animation", animation },
                { "extensions", new JObject { { listing, new JObject { { "channels", new JArray(channels) } } } } },
            };

        private static JObject CharacterExtensions(params JObject[] entries)
            => new JObject
            {
                { "KHR_character", new JObject() },
                { "KHR_character_expression", new JObject { { "expressions", new JArray(entries) } } },
            };

        private static Transform FindNode(GameObject scene, string name)
        {
            foreach (var transform in scene.GetComponentsInChildren<Transform>(true))
                if (transform.name == name) return transform;
            Assert.Fail($"the imported scene has no node '{name}'");
            return null;
        }

        private static MorphDriver Morph(SkinnedMeshRenderer face, int blendShape, float baseValue, float[] times, float[] deltas)
            => new MorphDriver
            {
                Smr = face,
                BlendShapeIndex = blendShape,
                BaseValue = baseValue,
                Sampler = new Sampler { Times = times, Interp = Interp.Linear },
                DeltaValues = deltas,
            };

        private static JointDriver Translation(Transform target, Vector3 baseValue, Vector3 delta)
            => new JointDriver
            {
                Target = target,
                Channel = TrsChannel.Translation,
                Sampler = Linear(),
                BaseVec = baseValue,
                DeltaVec = new[] { Vector3.zero, delta },
            };

        private static Sampler Linear() => new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear };

        private Texture2D MakeTexture()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "baseColor" };
            texture.SetPixels(new[] { Color.white, Color.red, Color.green, Color.blue });
            texture.Apply();
            _created.Add(texture);
            return texture;
        }

        // UnityGLTF's runtime importer gives blendshape frames weight 1, so the authored renderer does too.
        private SkinnedMeshRenderer MakeMorphRenderer(Transform parent, int blendShapeCount)
        {
            var mesh = new Mesh { name = "face" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            for (int index = 0; index < blendShapeCount; index++)
                mesh.AddBlendShapeFrame("shape" + index, 1f, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
            _created.Add(mesh);
            var go = new GameObject("face", typeof(SkinnedMeshRenderer));
            go.transform.SetParent(parent, false);
            var renderer = go.GetComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.sharedMaterial = MakeMaterial(null);
            return renderer;
        }

        private MeshRenderer MakeTexturedPanel(Transform parent)
        {
            var go = new GameObject("panel", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            var mesh = new Mesh { name = "panel" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            _created.Add(mesh);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = MakeMaterial(MakeTexture());
            return renderer;
        }

        private Material MakeMaterial(Texture texture)
        {
            var shader = Shader.Find("Standard");
            Assert.IsNotNull(shader, "the built-in Standard shader is required for the export fixture");
            var material = new Material(shader) { name = texture != null ? "panel" : "face" };
            if (texture != null) material.mainTexture = texture;
            _created.Add(material);
            return material;
        }

        private const string NodeTranslation = "/nodes/0/translation";
        private const string TexturePath =
            "/materials/0/pbrMetallicRoughness/baseColorTexture/extensions/KHR_texture_transform";
        private const string TextureOffset = TexturePath + "/offset";
        private const string TextureScale = TexturePath + "/scale";

        private ExpressionResponseSet BindResponses(params ExpressionResponseSetEntry[] entries)
        {
            var host = new GameObject("responses");
            _created.Add(host);
            var responses = host.AddComponent<ExpressionResponseSet>();
            responses.Bind(entries, false);
            return responses;
        }

        private static ExpressionResponseSetEntry ResponseEntry(
            string name,
            params (ExpressionResponseSampler Sampler, ExpressionResponseChannel Channel)[] ramps)
        {
            var samplers = new ExpressionResponseSampler[ramps.Length];
            var channels = new ExpressionResponseChannel[ramps.Length];
            for (int index = 0; index < ramps.Length; index++)
            {
                samplers[index] = ramps[index].Sampler;
                channels[index] = ramps[index].Channel;
                channels[index].SamplerIndex = index;
            }
            var animation = new ExpressionResponseAnimation { Samplers = samplers, Channels = channels };
            return new ExpressionResponseSetEntry(name, 0, animation, null, null);
        }

        // A listed LINEAR channel from the authored value at time 0 to its final value at time 1.
        private static (ExpressionResponseSampler, ExpressionResponseChannel) Ramp(
            string property,
            ExpressionPropertyKind kind,
            string listedBy,
            double[] authored,
            float[] final)
        {
            var initial = new float[authored.Length];
            for (int index = 0; index < initial.Length; index++) initial[index] = (float)authored[index];
            var sampler = new ExpressionResponseSampler
            {
                InputTimes = new[] { 0f, 1f },
                OutputValues = new[] { initial, final },
                Interpolation = ExpressionResponseInterpolation.Linear,
                OutputEncoding = ExpressionAccessorComponentEncoding.Float,
            };
            var channel = new ExpressionResponseChannel
            {
                Target = new ExpressionResponseTarget(
                    ExpressionPropertyIdentity.ForProperty(property),
                    authored.Length,
                    ExpressionResponseValueKind.Components,
                    true,
                    kind,
                    authored,
                    0),
                ListedBy = listedBy,
                EmitsRecords = true,
            };
            return (sampler, channel);
        }

        private static ExpressionTargetBinding FlippedTextureBinding(
            string property,
            ExpressionPropertyKind kind,
            double[] authored,
            Renderer panel)
            => new ExpressionTargetBinding
            {
                Property = property,
                Kind = kind,
                AuthoredInitial = authored,
                TextureTransform = TexturePath,
                TextureRenderers = new[] { panel },
                TextureMaterialSlots = new[] { 0 },
                TextureStProperty = "_MainTex_ST",
                TextureFlipped = true,
            };

        private static Vector4 PanelSt(Renderer panel)
        {
            var block = new MaterialPropertyBlock();
            panel.GetPropertyBlock(block, 0);
            return block.GetVector("_MainTex_ST");
        }

        private static void SetPanelSt(Renderer panel, Vector4 st)
        {
            var block = new MaterialPropertyBlock();
            panel.GetPropertyBlock(block, 0);
            block.SetVector("_MainTex_ST", st);
            panel.SetPropertyBlock(block, 0);
        }
    }
}
