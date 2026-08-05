using System.Collections.Generic;
using System.Text.RegularExpressions;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityGLTF.Extensions;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Tests for the KHR Character export plugin: drives a real GLTFSceneExporter over an in-memory character
    /// and verifies expression metadata (morph/joint/texture/mask/mapping), skeleton mapping, reference pose,
    /// and the facial-only scope guard. Runs in PlayMode (the export pipeline needs the Unity runtime).
    /// </summary>
    public class KhrCharacterExportTests
    {
        private const string SkeletonVocab = "https://example.com/skeleton/unity-humanoid/v1";
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        // Exports `root` (an in-memory character) through a real GLTFSceneExporter with the KHR_character export
        // plugin AND AnimationPointer export enabled, then returns the populated in-memory GLTFRoot. Enabling
        // AnimationPointer is the critical mode: the pre-F7 code routed joints/reference-pose through
        // AddAnimationData, which under AnimationPointer rewrote them into `pointer` channels that the native-only
        // baker drops. GLTFSettings.GetDefaultSettings() is an isolated instance, so global settings are untouched.
        private static GLTFRoot ExportToGltfRoot(GameObject root)
        {
            var settings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in settings.ExportPlugins)
                if (plugin is KhrCharacterExportPlugin || plugin is AnimationPointerExport)
                    plugin.Enabled = true;

            var exporter = new GLTFSceneExporter(new[] { root.transform }, new ExportContext(settings));
            exporter.SaveGLBToByteArray("scene");   // synchronous; runs the AfterSceneExport plugin hooks
            return exporter.GetRoot();
        }

        // Like ExportToGltfRoot but with multiple export roots: the exporter sees several character components
        // and must deterministically select one rootNode designation.
        private static GLTFRoot ExportRootsToGltfRoot(params GameObject[] roots)
        {
            var settings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in settings.ExportPlugins)
                if (plugin is KhrCharacterExportPlugin || plugin is AnimationPointerExport)
                    plugin.Enabled = true;

            var transforms = new Transform[roots.Length];
            for (int i = 0; i < roots.Length; i++) transforms[i] = roots[i].transform;

            var exporter = new GLTFSceneExporter(transforms, new ExportContext(settings));
            exporter.SaveGLBToByteArray("scene");
            return exporter.GetRoot();
        }

        // Resolves the output accessor backing an animation channel's sampler.
        private static Accessor OutputAccessor(GLTFRoot root, GLTFAnimation anim, AnimationChannel channel)
            => root.Accessors[anim.Samplers[channel.Sampler.Id].Output.Id];

        // Reads the KHR_animation_pointer JSON path off a channel target (null if it's not a pointer channel).
        private static string PointerPath(AnimationChannel channel)
            => channel.Target?.Extensions != null
               && channel.Target.Extensions.TryGetValue(KHR_animation_pointer.EXTENSION_NAME, out var ext)
               && ext is KHR_animation_pointer p ? p.path : null;

        // A minimal single-key joint rotation driver: enough to make an expression emit one channel so the
        // expression item is kept (the exporter drops expressions with zero channels).
        private static JointDriver RotationDriver(Transform target) => new JointDriver
        {
            Target = target, Channel = TrsChannel.Rotation,
            Sampler = new Sampler { Times = new[] { 0f }, Interp = Interp.Step, SingleKey = true },
            DeltaQuat = new[] { Quaternion.Euler(10f, 0f, 0f) }, BaseQuat = Quaternion.identity,
        };

        [Test]
        public void ExportPlugin_IsDisabledByDefault()
        {
            var plugin = ScriptableObject.CreateInstance<KhrCharacterExportPlugin>();
            Assert.IsFalse(plugin.EnabledByDefault, "KHR Character export plugin should be disabled by default");
            Object.DestroyImmediate(plugin);
        }

        [Test]
        public void ExportPlugin_HasNonRatifiedAttribute()
        {
            var pluginType = typeof(KhrCharacterExportPlugin);
            var attr = pluginType.GetCustomAttributes(typeof(NonRatifiedPluginAttribute), true);
            Assert.IsNotEmpty(attr, "KhrCharacterExportPlugin should have NonRatifiedPlugin attribute");
        }

        [Test]
        public void ExportPlugin_DisplayName_IsCorrect()
        {
            var plugin = ScriptableObject.CreateInstance<KhrCharacterExportPlugin>();
            Assert.AreEqual("KHR Character / Avatar Extensions", plugin.DisplayName);
            Object.DestroyImmediate(plugin);
        }

        [Test]
        public void ExportPlugin_Description_MentionsFacialExpressions()
        {
            var plugin = ScriptableObject.CreateInstance<KhrCharacterExportPlugin>();
            StringAssert.Contains("facial", plugin.Description.ToLowerInvariant(), 
                "Description should mention facial expressions scope rule");
            Object.DestroyImmediate(plugin);
        }

        [Test]
        public void ExportContext_CanBeCreated_FromPlugin()
        {
            var plugin = ScriptableObject.CreateInstance<KhrCharacterExportPlugin>();
            var context = new ExportContext();
            var exportContext = plugin.CreateInstance(context);
            
            Assert.IsNotNull(exportContext, "CreateInstance should return a valid context");
            Assert.IsInstanceOf<KhrCharacterExportContext>(exportContext, 
                "Context should be of type KhrCharacterExportContext");
            
            Object.DestroyImmediate(plugin);
        }

        [Test]
        public void ExportContext_StoresPluginAndContextReferences()
        {
            // This test verifies the constructor properly stores references.
            // The actual implementation details are internal, but we can verify
            // the context was created successfully with the plugin.
            var plugin = ScriptableObject.CreateInstance<KhrCharacterExportPlugin>();
            var context = new ExportContext();
            
            Assert.DoesNotThrow(() => {
                var exportContext = new KhrCharacterExportContext(plugin, context);
                Assert.IsNotNull(exportContext);
            }, "KhrCharacterExportContext constructor should not throw");
            
            Object.DestroyImmediate(plugin);
        }

        [Test]
        public void ExpressionMetadataExport_IncludesMorphtargetChannels()
        {
            // A MorphDriver exports as a KHR_animation_pointer channel targeting a single blendshape weight
            // ("/nodes/{n}/weights/{j}" — the per-blendshape convention KhrCharacterBaker.BakeMorphChannels
            // parses back), referenced by the expression item's KHR_character_expression_morphtarget
            // sub-extension. Weights stay raw [0..1] (F5: no BlendShapeFrameWeight 100x), STEP for a binary toggle.
            var root = new GameObject("char");
            _created.Add(root);

            // A blendshapes-only (boneless) SkinnedMeshRenderer child: the exporter skips skinning but still
            // exports the mesh + morph targets, so the pointer's target node legitimately carries weights.
            var mesh = new Mesh { name = "face" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            mesh.AddBlendShapeFrame("blink", 100f, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
            _created.Add(mesh);
            var smrGo = new GameObject("face", typeof(SkinnedMeshRenderer));
            smrGo.transform.SetParent(root.transform, false);
            var smr = smrGo.GetComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;

            // A binary blink: raw weight 0 -> 1 over two STEP keys (BaseValue 0, frame-0-relative deltas).
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "blink",
                        Domains = ExpressionDomain.Morph,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Smr = smr, BlendShapeIndex = 0, BaseValue = 0f,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Step, SingleKey = false },
                                DeltaValues = new[] { 0f, 1f },
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);

            Assert.IsTrue(gltf.Extensions != null && gltf.Extensions.ContainsKey(KHR_character_expression.EXTENSION_NAME),
                "KHR_character_expression root extension should be present");
            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            var item = ext.Expressions.Find(e => e.Expression == "blink");
            Assert.IsNotNull(item, "the exported expression item should be present");
            Assert.IsNotNull(item.Morphtarget, "the expression should have a KHR_character_expression_morphtarget sub-extension");
            Assert.AreEqual(1, item.Morphtarget.Channels.Length, "one morph driver -> one channel");

            var anim = gltf.Animations[item.Animation];
            var ch = anim.Channels[item.Morphtarget.Channels[0]];

            // Morph weights animate via KHR_animation_pointer to a single blendshape weight (the baker's inverse).
            Assert.AreEqual("pointer", ch.Target.Path, "morph weights export via KHR_animation_pointer");
            var path = PointerPath(ch);
            Assert.IsNotNull(path, "morph channel must carry a KHR_animation_pointer path");
            Assert.IsTrue(KhrCharacterBaker.TryParseNodeWeightsPointer(path, out _, out int shapeIndex),
                "pointer must be the '/nodes/{n}/weights/{j}' per-blendshape form the baker parses");
            Assert.AreEqual(0, shapeIndex, "pointer must target blendshape index 0");

            // Raw [0..1] weights (no 100x frame-weight scaling), single STEP track 0 -> 1.
            var sampler = anim.Samplers[ch.Sampler.Id];
            Assert.AreEqual(InterpolationType.STEP, sampler.Interpolation, "binary blink uses STEP");
            var outAcc = OutputAccessor(gltf, anim, ch);
            Assert.AreEqual(GLTFAccessorAttributeType.SCALAR, outAcc.Type);
            Assert.AreEqual(2, (int)outAcc.Count);
            Assert.AreEqual(0f, (float)outAcc.Min[0], 1e-5f, "raw morph weight stays 0..1 (frame-0 = 0)");
            Assert.AreEqual(1f, (float)outAcc.Max[0], 1e-5f, "raw morph weight stays 0..1 (peaks at 1)");
        }

        [Test]
        public void ExpressionMetadataExport_IncludesJointChannels()
        {
            // F7 (T7): with AnimationPointer export ENABLED, joint TRS channels must be written as NATIVE glTF
            // channels (target.Node + path rotation/translation), NOT KHR_animation_pointer channels — the
            // native-only joint baker (KhrCharacterBaker.BakeJointChannels) drops pointer channels on re-import.
            var root = new GameObject("char");
            _created.Add(root);
            var jaw = new GameObject("jaw").transform;
            jaw.SetParent(root.transform, false);

            var rotAbs = Quaternion.Euler(15f, 0f, 0f);
            var posAbs = new Vector3(0.1f, 0.2f, 0.3f);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "jawOpen",
                        Domains = ExpressionDomain.Joint,
                        JointDrivers = new[]
                        {
                            new JointDriver
                            {
                                Target = jaw, Channel = TrsChannel.Rotation,
                                Sampler = new Sampler { Times = new[] { 0f }, Interp = Interp.Step, SingleKey = true },
                                DeltaQuat = new[] { rotAbs }, BaseQuat = Quaternion.identity,
                            },
                            new JointDriver
                            {
                                Target = jaw, Channel = TrsChannel.Translation,
                                Sampler = new Sampler { Times = new[] { 0f }, Interp = Interp.Step, SingleKey = true },
                                DeltaVec = new[] { posAbs }, BaseVec = Vector3.zero,
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);

            Assert.IsTrue(gltf.Extensions != null && gltf.Extensions.ContainsKey(KHR_character_expression.EXTENSION_NAME),
                "KHR_character_expression root extension should be present");
            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            var item = ext.Expressions.Find(e => e.Expression == "jawOpen");
            Assert.IsNotNull(item, "the exported expression item should be present");
            Assert.IsNotNull(item.Joint, "the expression should have a KHR_character_expression_joint sub-extension");
            Assert.AreEqual(2, item.Joint.Channels.Length, "both joint TRS channels should be captured");

            var anim = gltf.Animations[item.Animation];
            AnimationChannel rotCh = null, transCh = null;
            foreach (var ci in item.Joint.Channels)
            {
                var ch = anim.Channels[ci];
                Assert.IsNotNull(ch.Target.Node,
                    "joint channel must be a NATIVE TRS channel (target.Node set), not a pointer channel");
                Assert.AreNotEqual("pointer", ch.Target.Path,
                    "joint channel must not be routed through KHR_animation_pointer");
                if (ch.Target.Path == "rotation") rotCh = ch;
                else if (ch.Target.Path == "translation") transCh = ch;
            }
            Assert.IsNotNull(rotCh, "a native 'rotation' channel should be present");
            Assert.IsNotNull(transCh, "a native 'translation' channel should be present");

            // Rotation output: VEC4, single key.
            var rotAcc = OutputAccessor(gltf, anim, rotCh);
            Assert.AreEqual(GLTFAccessorAttributeType.VEC4, rotAcc.Type);
            Assert.AreEqual(1, (int)rotAcc.Count);

            // Translation output: VEC3, single key, with the Unity->glTF (-1,1,1) X-flip applied.
            var transAcc = OutputAccessor(gltf, anim, transCh);
            Assert.AreEqual(GLTFAccessorAttributeType.VEC3, transAcc.Type);
            Assert.AreEqual(1, (int)transAcc.Count);
            Assert.AreEqual(-posAbs.x, (float)transAcc.Min[0], 1e-4f, "translation X must be flipped on export");
            Assert.AreEqual(posAbs.y, (float)transAcc.Min[1], 1e-4f);
            Assert.AreEqual(posAbs.z, (float)transAcc.Min[2], 1e-4f);
        }

        [Test]
        public void ExpressionMetadataExport_IncludesTextureChannels()
        {
            // A UV-transform TextureDriver must export as two KHR_animation_pointer channels into the material's
            // KHR_texture_transform (scale + offset), referenced by the expression item's
            // KHR_character_expression_texture sub-extension — the exact inverse of KhrCharacterBaker's import.
            var shader = Shader.Find("Unlit/Texture");
            if (shader == null) { Assert.Ignore("No suitable built-in shader available in this project."); return; }

            var root = new GameObject("char");
            _created.Add(root);

            // A child renderer with a real mesh + material, so the material is exported and GetMaterialId resolves.
            var quad = new GameObject("quad", typeof(MeshFilter), typeof(MeshRenderer));
            quad.transform.SetParent(root.transform, false);
            var mesh = new Mesh { name = "quad" };
            mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            _created.Add(mesh);
            quad.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mat = new Material(shader) { name = "mat" };
            _created.Add(mat);
            var mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;

            // Scroll the base-color UV offset.x from 0 -> 1. StValues are frame-0-relative _ST deltas (the baker's
            // convention); BaseSt is the material's base _ST; GltfTextureSlot is the full slot path the importer parses.
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "scroll",
                        Domains = ExpressionDomain.Texture,
                        TextureDrivers = new[]
                        {
                            new TextureDriver
                            {
                                Renderer = mr,
                                SubmeshSlot = 0,
                                PropertyId = Shader.PropertyToID("_MainTex_ST"),
                                PropertyName = "_MainTex",
                                GltfTextureSlot = "pbrMetallicRoughness/baseColorTexture",
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                StValues = new[] { Vector4.zero, new Vector4(0f, 0f, 1f, 0f) },
                                BaseSt = new Vector4(1f, 1f, 0f, 0f),
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);

            Assert.IsTrue(gltf.Extensions != null && gltf.Extensions.ContainsKey(KHR_character_expression.EXTENSION_NAME),
                "KHR_character_expression root extension should be present");
            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            var item = ext.Expressions.Find(e => e.Expression == "scroll");
            Assert.IsNotNull(item, "the exported expression item should be present");
            Assert.IsNotNull(item.Texture, "the expression should have a KHR_character_expression_texture sub-extension");
            Assert.AreEqual(2, item.Texture.Channels.Length, "a UV transform exports a scale + an offset channel");

            // The referenced channels are KHR_animation_pointer channels into this material's texture transform.
            var anim = gltf.Animations[item.Animation];
            AnimationChannel scaleCh = null, offsetCh = null;
            foreach (var ci in item.Texture.Channels)
            {
                var ch = anim.Channels[ci];
                Assert.AreEqual("pointer", ch.Target.Path, "texture channels are exported via KHR_animation_pointer");
                var path = PointerPath(ch);
                Assert.IsNotNull(path, "texture channel must carry a KHR_animation_pointer path");
                StringAssert.StartsWith("/materials/", path);
                StringAssert.Contains("/pbrMetallicRoughness/baseColorTexture/", path);
                if (path.EndsWith("/extensions/KHR_texture_transform/scale")) scaleCh = ch;
                else if (path.EndsWith("/extensions/KHR_texture_transform/offset")) offsetCh = ch;
            }
            Assert.IsNotNull(scaleCh, "a KHR_texture_transform 'scale' pointer channel should be present");
            Assert.IsNotNull(offsetCh, "a KHR_texture_transform 'offset' pointer channel should be present");

            // R6: animation sampler INPUT (time) accessors must carry min/max (else invalid glTF / viewer rejection).
            var inAcc = gltf.Accessors[anim.Samplers[offsetCh.Sampler.Id].Input.Id];
            Assert.IsNotNull(inAcc.Min, "sampler input (time) accessor must have min (R6)");
            Assert.IsNotNull(inAcc.Max, "sampler input (time) accessor must have max (R6)");
            Assert.AreEqual(0f, (float)inAcc.Min[0], 1e-5f);
            Assert.AreEqual(1f, (float)inAcc.Max[0], 1e-5f);

            // Offset output: VEC2, two keys; abs _ST = BaseSt + delta, then gltfOffset.x = _ST.z (the scrolled axis).
            var offAcc = OutputAccessor(gltf, anim, offsetCh);
            Assert.AreEqual(GLTFAccessorAttributeType.VEC2, offAcc.Type);
            Assert.AreEqual(2, (int)offAcc.Count);
            Assert.IsNotNull(offAcc.Min);
            Assert.IsNotNull(offAcc.Max);
            Assert.AreEqual(0f, (float)offAcc.Min[0], 1e-4f, "frame-0 offset.x is 0");
            Assert.AreEqual(1f, (float)offAcc.Max[0], 1e-4f, "offset.x scrolls to 1");

            // Scale output: VEC2, base scale (1,1) unchanged across the two keys.
            var scaleAcc = OutputAccessor(gltf, anim, scaleCh);
            Assert.AreEqual(GLTFAccessorAttributeType.VEC2, scaleAcc.Type);
            Assert.AreEqual(1f, (float)scaleAcc.Min[0], 1e-4f);
            Assert.AreEqual(1f, (float)scaleAcc.Max[0], 1e-4f);
        }

        [Test]
        public void ExpressionMetadataExport_IncludesMaskEntries()
        {
            // MaskEntry[] on an expression export as a KHR_character_expression_mask sub-extension whose masks[]
            // carry { target (expression index), type "blend"|"block", amount, threshold } — the exact inverse of
            // KhrCharacterBaker.BuildMaskEntries. Mirrors the spec example: "happy" blend-masks "aa"; "angry"
            // block-masks "aa". Each masking expression also has a driver channel so its item is emitted.
            var root = new GameObject("char");
            _created.Add(root);
            var ctrl = new GameObject("ctrl").transform;
            ctrl.SetParent(root.transform, false);

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack // index 0: the lip-sync phoneme being masked
                    {
                        Name = "aa", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrl) },
                    },
                    new ExpressionTrack // index 1: blend-masks "aa" by 0.5
                    {
                        Name = "happy", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrl) },
                        Masks = new[] { new MaskEntry { TargetIndex = 0, Type = MaskType.Blend, Amount = 0.5f, Threshold = 0f } },
                    },
                    new ExpressionTrack // index 2: block-masks "aa" above threshold 0.2
                    {
                        Name = "angry", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrl) },
                        Masks = new[] { new MaskEntry { TargetIndex = 0, Type = MaskType.Block, Amount = 1f, Threshold = 0.2f } },
                    },
                    new ExpressionTrack // index 3: application-defined mask type, with blend fallback at runtime
                    {
                        Name = "soft", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrl) },
                        Masks = new[] { new MaskEntry { TargetIndex = 0, Type = MaskType.Identity, CustomType = "ACME_soft_block", Amount = 0.25f } },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);

            Assert.IsTrue(gltf.Extensions != null && gltf.Extensions.ContainsKey(KHR_character_expression.EXTENSION_NAME),
                "KHR_character_expression root extension should be present");
            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);

            // Exported entries are contiguous here, so wire and runtime indices are identical.
            var nameToIndex = new Dictionary<string, int>();
            for (int i = 0; i < ext.Expressions.Count; i++) nameToIndex[ext.Expressions[i].Expression] = i;
            var wireToTrackIndex = new Dictionary<int, int>();
            for (int i = 0; i < ext.Expressions.Count; i++) wireToTrackIndex[i] = i;

            // "aa" carries no mask of its own.
            var aa = ext.Expressions.Find(e => e.Expression == "aa");
            Assert.IsNotNull(aa);
            Assert.IsNull(aa.Mask, "an expression with no MaskEntry must not emit an (empty) mask sub-extension");

            // "happy": one blend mask on "aa", amount 0.5.
            var happy = ext.Expressions.Find(e => e.Expression == "happy");
            Assert.IsNotNull(happy);
            Assert.IsNotNull(happy.Mask, "happy should carry a KHR_character_expression_mask sub-extension");
            Assert.AreEqual(1, happy.Mask.Masks.Count);
            Assert.AreEqual(nameToIndex["aa"], happy.Mask.Masks[0].Target);
            Assert.AreEqual("blend", happy.Mask.Masks[0].Type);
            Assert.AreEqual(0.5f, happy.Mask.Masks[0].Amount, 1e-5f);

            // "angry": one block mask on "aa", threshold 0.2.
            var angry = ext.Expressions.Find(e => e.Expression == "angry");
            Assert.IsNotNull(angry);
            Assert.IsNotNull(angry.Mask);
            Assert.AreEqual("block", angry.Mask.Masks[0].Type);
            Assert.AreEqual(0.2f, angry.Mask.Masks[0].Threshold, 1e-5f);

            // Round-trip through the real import baker: exported masks resolve back to the original MaskEntry.
            var happyEntries = KhrCharacterBaker.BuildMaskEntries(happy.Mask, nameToIndex["happy"], wireToTrackIndex);
            Assert.AreEqual(1, happyEntries.Length);
            Assert.AreEqual(nameToIndex["aa"], happyEntries[0].TargetIndex);
            Assert.AreEqual(MaskType.Blend, happyEntries[0].Type);
            Assert.AreEqual(0.5f, happyEntries[0].Amount, 1e-5f);

            var angryEntries = KhrCharacterBaker.BuildMaskEntries(angry.Mask, nameToIndex["angry"], wireToTrackIndex);
            Assert.AreEqual(1, angryEntries.Length);
            Assert.AreEqual(nameToIndex["aa"], angryEntries[0].TargetIndex);
            Assert.AreEqual(MaskType.Block, angryEntries[0].Type);
            Assert.AreEqual(0.2f, angryEntries[0].Threshold, 1e-5f);

            var soft = ext.Expressions.Find(e => e.Expression == "soft");
            Assert.IsNotNull(soft?.Mask);
            Assert.AreEqual("ACME_soft_block", soft.Mask.Masks[0].Type,
                "application-defined mask type must be preserved on export");
            var softEntries = KhrCharacterBaker.BuildMaskEntries(soft.Mask, nameToIndex["soft"], wireToTrackIndex);
            Assert.AreEqual(MaskType.Identity, softEntries[0].Type);
            Assert.AreEqual("ACME_soft_block", softEntries[0].CustomType);
        }

        [Test]
        public void ExpressionMetadataExport_IncludesMappingSets()
        {
            // ExpressionMappingSet[] export as the root KHR_character_expression_mapping extension:
            //   expressionSetMappings[setName][targetName] = [ { source, weight }, ... ]
            // where source is the model's own expression index. Exact inverse of KhrCharacterBaker.BuildMappingSets.
            var root = new GameObject("char");
            _created.Add(root);
            var ctrl = new GameObject("ctrl").transform;
            ctrl.SetParent(root.transform, false);

            // Two model expressions feed a "Smile" term in the "vrm" target vocabulary.
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "smileLeft", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrl) } },
                    new ExpressionTrack { Name = "smileRight", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrl) } },
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
                                TargetName = "Smile",
                                Contributions = new[]
                                {
                                    new MappingContribution { SourceIndex = 0, Weight = 0.8f },
                                    new MappingContribution { SourceIndex = 1, Weight = 0.8f },
                                },
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);

            Assert.IsTrue(gltf.Extensions != null && gltf.Extensions.ContainsKey(KHR_character_expression_mapping.EXTENSION_NAME),
                "KHR_character_expression_mapping root extension should be present");
            var mappingExt = gltf.Extensions[KHR_character_expression_mapping.EXTENSION_NAME] as KHR_character_expression_mapping;
            Assert.IsNotNull(mappingExt);
            Assert.IsTrue(mappingExt.ExpressionSetMappings.ContainsKey("vrm"), "the 'vrm' target set should be present");
            var smileSet = mappingExt.ExpressionSetMappings["vrm"];
            Assert.IsTrue(smileSet.ContainsKey("Smile"));
            var contribs = smileSet["Smile"];
            Assert.AreEqual(2, contribs.Count, "Smile is composed from two source expressions");
            Assert.AreEqual(0, contribs[0].Source);
            Assert.AreEqual(0.8f, contribs[0].Weight, 1e-5f);
            Assert.AreEqual(1, contribs[1].Source);
            Assert.AreEqual(0.8f, contribs[1].Weight, 1e-5f);

            // Round-trip through the real import baker: wire indices resolve back to track indices.
            var exprExt = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(exprExt, "source expressions with drivers should also export as expression items");
            var wireToTrackIndex = new Dictionary<int, int>();
            for (int i = 0; i < exprExt.Expressions.Count; i++) wireToTrackIndex[i] = i;

            var sets = KhrCharacterBaker.BuildMappingSets(mappingExt, wireToTrackIndex);
            Assert.AreEqual(1, sets.Length);
            Assert.AreEqual("vrm", sets[0].SetName);
            Assert.AreEqual(1, sets[0].Targets.Length);
            Assert.AreEqual("Smile", sets[0].Targets[0].TargetName);
            var baked = sets[0].Targets[0].Contributions;
            Assert.AreEqual(2, baked.Length);
            Assert.AreEqual(0, baked[0].SourceIndex);
            Assert.AreEqual(0.8f, baked[0].Weight, 1e-5f);
            Assert.AreEqual(1, baked[1].SourceIndex);
            Assert.AreEqual(0.8f, baked[1].Weight, 1e-5f);
        }

        [Test]
        public void ExpressionMetadataExport_RemapsReferencesAfterFiltering()
        {
            var root = new GameObject("char");
            _created.Add(root);
            var ctrl = new GameObject("ctrl").transform;
            ctrl.SetParent(root.transform, false);

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "skipped" },
                    new ExpressionTrack
                    {
                        Name = "source",
                        Domains = ExpressionDomain.Joint,
                        JointDrivers = new[] { RotationDriver(ctrl) },
                        Masks = new[] { new MaskEntry { TargetIndex = 2, Type = MaskType.Blend, Amount = 1f } },
                    },
                    new ExpressionTrack
                    {
                        Name = "target",
                        Domains = ExpressionDomain.Joint,
                        JointDrivers = new[] { RotationDriver(ctrl) },
                    },
                },
                MappingSets = new[]
                {
                    new ExpressionMappingSet
                    {
                        SetName = "example",
                        Targets = new[]
                        {
                            new MappingTarget
                            {
                                TargetName = "Target",
                                Contributions = new[] { new MappingContribution { SourceIndex = 2, Weight = 1f } },
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);
            var expressions = (gltf.Extensions[KHR_character_expression.EXTENSION_NAME]
                as KHR_character_expression).Expressions;
            Assert.AreEqual(2, expressions.Count);
            Assert.AreEqual("source", expressions[0].Expression);
            Assert.AreEqual("target", expressions[1].Expression);
            Assert.AreEqual(1, expressions[0].Mask.Masks[0].Target,
                "runtime target index 2 remaps to wire expression index 1");

            var mapping = gltf.Extensions[KHR_character_expression_mapping.EXTENSION_NAME]
                as KHR_character_expression_mapping;
            Assert.AreEqual(1, mapping.ExpressionSetMappings["example"]["Target"][0].Source,
                "runtime source index 2 remaps to wire expression index 1");
        }

        [Test]
        public void ExtensionsUsed_DeclaresEmittedNestedExpressionSubExtensions()
        {
            // B1: every nested KHR_character_expression_* sub-extension actually emitted on an expression item must be
            // declared in extensionsUsed (deduped, exactly once), and NONE of them in extensionsRequired (they stay
            // non-required, consistent with the parent KHR_character_expression). Exercises morph + joint + texture +
            // mask in one character so all four nested tokens are emitted.
            var shader = Shader.Find("Unlit/Texture");
            if (shader == null) { Assert.Ignore("No suitable built-in shader available in this project."); return; }

            var root = MakeUvCharacter(shader, out var mr); // root + quad + material (texture domain)

            // Morph: a boneless SMR with one blendshape (the exporter still exports the mesh + morph target).
            var mesh = new Mesh { name = "face" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            mesh.AddBlendShapeFrame("blink", 100f, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
            _created.Add(mesh);
            var smrGo = new GameObject("face", typeof(SkinnedMeshRenderer));
            smrGo.transform.SetParent(root.transform, false);
            smrGo.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            var smr = smrGo.GetComponent<SkinnedMeshRenderer>();

            var jaw = new GameObject("jaw").transform; jaw.SetParent(root.transform, false);
            var ctrl = new GameObject("ctrl").transform; ctrl.SetParent(root.transform, false);

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack // 0: morph
                    {
                        Name = "blink", Domains = ExpressionDomain.Morph,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Smr = smr, BlendShapeIndex = 0, BaseValue = 0f,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Step, SingleKey = false },
                                DeltaValues = new[] { 0f, 1f },
                            },
                        },
                    },
                    new ExpressionTrack // 1: joint
                    {
                        Name = "jawOpen", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(jaw) },
                    },
                    new ExpressionTrack // 2: texture (UV transform)
                    {
                        Name = "scroll", Domains = ExpressionDomain.Texture,
                        TextureDrivers = new[]
                        {
                            new TextureDriver
                            {
                                Renderer = mr, SubmeshSlot = 0,
                                PropertyId = Shader.PropertyToID("_MainTex_ST"), PropertyName = "_MainTex",
                                GltfTextureSlot = "pbrMetallicRoughness/baseColorTexture",
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                StValues = new[] { Vector4.zero, new Vector4(0f, 0f, 1f, 0f) },
                                BaseSt = new Vector4(1f, 1f, 0f, 0f),
                            },
                        },
                    },
                    new ExpressionTrack // 3: the masked target (joint so its item is emitted)
                    {
                        Name = "aa", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrl) },
                    },
                    new ExpressionTrack // 4: joint + a blend mask on "aa" (emits both _joint and _mask)
                    {
                        Name = "happy", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrl) },
                        Masks = new[] { new MaskEntry { TargetIndex = 3, Type = MaskType.Blend, Amount = 0.5f, Threshold = 0f } },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);

            Assert.IsNotNull(gltf.ExtensionsUsed, "extensionsUsed must be populated");
            var nestedTokens = new[]
            {
                KhrCharacterExtensionNames.Character,
                KHR_character_expression.EXTENSION_NAME,
                KHR_character_expression_morphtarget.EXTENSION_NAME,
                KHR_character_expression_joint.EXTENSION_NAME,
                KHR_character_expression_texture.EXTENSION_NAME,
                KHR_character_expression_mask.EXTENSION_NAME,
            };
            foreach (var token in nestedTokens)
            {
                Assert.IsTrue(gltf.ExtensionsUsed.Contains(token), $"{token} must be declared in extensionsUsed");
                Assert.AreEqual(1, gltf.ExtensionsUsed.FindAll(e => e == token).Count,
                    $"{token} must be declared exactly once (DeclareExtensionUsage dedups)");
                Assert.IsTrue(gltf.ExtensionsRequired == null || !gltf.ExtensionsRequired.Contains(token),
                    $"{token} must NOT be in extensionsRequired (non-required, like the parent)");
            }
            Assert.IsFalse(gltf.ExtensionsUsed.Contains(KhrCharacterExtensionNames.XmpJsonLd),
                "metadata-free character assets do not declare an XMP dependency");
            Assert.IsFalse(gltf.Extensions.ContainsKey(KhrCharacterExtensionNames.XmpJsonLd));
        }

        [Test]
        public void ExtensionsUsed_MorphOnly_DeclaresOnlyMorphtargetNested()
        {
            // B1 guard: a morph-only character declares KHR_character_expression_morphtarget but NONE of the other
            // nested tokens (no spurious _joint / _texture / _mask declarations).
            var root = new GameObject("char");
            _created.Add(root);

            var mesh = new Mesh { name = "face" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            mesh.AddBlendShapeFrame("blink", 100f, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
            _created.Add(mesh);
            var smrGo = new GameObject("face", typeof(SkinnedMeshRenderer));
            smrGo.transform.SetParent(root.transform, false);
            smrGo.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            var smr = smrGo.GetComponent<SkinnedMeshRenderer>();

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "blink", Domains = ExpressionDomain.Morph,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Smr = smr, BlendShapeIndex = 0, BaseValue = 0f,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Step, SingleKey = false },
                                DeltaValues = new[] { 0f, 1f },
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);

            Assert.IsNotNull(gltf.ExtensionsUsed);
            Assert.IsFalse(gltf.ExtensionsUsed.Contains(KhrCharacterExtensionNames.XmpJsonLd),
                "KHR_character does not impose a transitive XMP dependency");
            Assert.IsFalse(gltf.Extensions.ContainsKey(KhrCharacterExtensionNames.XmpJsonLd),
                "metadata-free assets must not synthesize an XMP packet object");
            Assert.IsTrue(gltf.ExtensionsUsed.Contains(KHR_character_expression_morphtarget.EXTENSION_NAME),
                "morph-only must declare KHR_character_expression_morphtarget");
            Assert.IsFalse(gltf.ExtensionsUsed.Contains(KHR_character_expression_joint.EXTENSION_NAME),
                "morph-only must NOT declare KHR_character_expression_joint");
            Assert.IsFalse(gltf.ExtensionsUsed.Contains(KHR_character_expression_texture.EXTENSION_NAME),
                "morph-only must NOT declare KHR_character_expression_texture");
            Assert.IsFalse(gltf.ExtensionsUsed.Contains(KHR_character_expression_mask.EXTENSION_NAME),
                "morph-only must NOT declare KHR_character_expression_mask");
        }

        [Test]
        public void SkeletonMappingExport_WritesVocabularyAssociationDictionary()
        {
            // The outer key is a version-stable vocabulary URI. Role keys and their node associations are preserved;
            // no Unity Humanoid completeness or anatomy semantics are written into the extension.
            var root = new GameObject("char");
            _created.Add(root);
            var hips = new GameObject("Hips").transform; hips.SetParent(root.transform, false);
            var leg = new GameObject("LeftUpperLeg").transform; leg.SetParent(hips, false);
            var head = new GameObject("Head").transform; head.SetParent(hips, false);

            var skel = root.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform>
                {
                    { "hips", hips }, { "leftUpperLeg", leg }, { "head", head },
                },
                SelectedRig = SkeletonVocab,
            });

            var gltf = ExportToGltfRoot(root);

            Assert.IsTrue(gltf.Extensions != null && gltf.Extensions.ContainsKey(KHR_character_skeleton_mapping.EXTENSION_NAME),
                "KHR_character_skeleton_mapping root extension should be present");
            var ext = gltf.Extensions[KHR_character_skeleton_mapping.EXTENSION_NAME] as KHR_character_skeleton_mapping;
            Assert.IsNotNull(ext);
            Assert.IsTrue(ext.SkeletalRigMappings.ContainsKey(SkeletonVocab),
                "the absolute vocabulary URI keys the mapping set");
            var rig = ext.SkeletalRigMappings[SkeletonVocab];

            // Canonical target-vocabulary joint names (keys) -> source node INDICES (values) into nodes[].
            Assert.AreEqual(3, rig.Count);
            Assert.AreEqual("Hips", gltf.Nodes[rig["hips"].Node].Name);
            Assert.AreEqual("LeftUpperLeg", gltf.Nodes[rig["leftUpperLeg"].Node].Name);
            Assert.AreEqual("Head", gltf.Nodes[rig["head"].Node].Name);

            // Each value must be a valid, non-negative 0-based index into the exported nodes[] (spec: glTFid).
            var nodeIndices = new List<int>();
            foreach (var association in rig.Values)
            {
                Assert.GreaterOrEqual(association.Node, 0, "skeleton mapping values are non-negative node indices");
                Assert.Less(association.Node, gltf.Nodes.Count, "skeleton mapping value must index a real glTF node");
                Assert.AreEqual(gltf.Nodes[association.Node].Name, association.Name);
                nodeIndices.Add(association.Node);
            }

            // Distinct joints bind distinct transforms, so they resolve to distinct node indices.
            CollectionAssert.AllItemsAreUnique(nodeIndices);
        }

        [Test]
        public void ReferencePoseExport_CreatesAnimationWithBoneChannels()
        {
            // F7 (T9): the reference pose must export as a NATIVE TRS animation (target.Node + translation/
            // rotation/scale, single STEP key) tagged with KHR_character_reference_pose — even with
            // AnimationPointer enabled — so KhrCharacterSkeletonBaker.BakeReferencePose (native-only) reads it back.
            var root = new GameObject("char");
            _created.Add(root);
            var bone = new GameObject("bone").transform;
            bone.SetParent(root.transform, false);
            var pos = new Vector3(1f, 2f, 3f);
            var rot = Quaternion.Euler(0f, 30f, 0f);
            var scale = new Vector3(1f, 1f, 2f);
            bone.localPosition = pos; bone.localRotation = rot; bone.localScale = scale;

            var skel = root.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", bone } },
                SelectedRig = SkeletonVocab,
                ReferencePose = new ReferencePose
                {
                    PoseType = "TPose",
                    Bones = new[] { bone },
                    LocalPositions = new[] { pos },
                    LocalRotations = new[] { rot },
                    LocalScales = new[] { scale },
                },
            });

            var gltf = ExportToGltfRoot(root);

            GLTFAnimation refAnim = null;
            foreach (var a in gltf.Animations)
                if (a.Extensions != null && a.Extensions.ContainsKey(KHR_character_reference_pose.EXTENSION_NAME))
                {
                    refAnim = a;
                    break;
                }
            Assert.IsNotNull(refAnim, "an animation tagged KHR_character_reference_pose should be exported");
            Assert.AreEqual(3, refAnim.Channels.Count, "one translation + rotation + scale channel for the single bone");

            AnimationChannel tCh = null, rCh = null, sCh = null;
            foreach (var ch in refAnim.Channels)
            {
                Assert.IsNotNull(ch.Target.Node,
                    "reference-pose channel must be NATIVE TRS (target.Node set), not a pointer channel");
                Assert.AreNotEqual("pointer", ch.Target.Path);
                switch (ch.Target.Path)
                {
                    case "translation": tCh = ch; break;
                    case "rotation": rCh = ch; break;
                    case "scale": sCh = ch; break;
                }
            }
            Assert.IsNotNull(tCh, "a native 'translation' channel should be present");
            Assert.IsNotNull(rCh, "a native 'rotation' channel should be present");
            Assert.IsNotNull(sCh, "a native 'scale' channel should be present");

            // Single STEP key at t=0.
            var tSampler = refAnim.Samplers[tCh.Sampler.Id];
            Assert.AreEqual(InterpolationType.STEP, tSampler.Interpolation, "reference pose is a single STEP key");
            Assert.AreEqual(1, (int)gltf.Accessors[tSampler.Input.Id].Count, "reference pose has a single keyframe");

            // Translation X-flip; scale raw (Unity->glTF handedness).
            var tAcc = OutputAccessor(gltf, refAnim, tCh);
            Assert.AreEqual(-pos.x, (float)tAcc.Min[0], 1e-4f, "translation X must be flipped on export");
            Assert.AreEqual(pos.y, (float)tAcc.Min[1], 1e-4f);
            Assert.AreEqual(pos.z, (float)tAcc.Min[2], 1e-4f);
            var sAcc = OutputAccessor(gltf, refAnim, sCh);
            Assert.AreEqual(scale.x, (float)sAcc.Min[0], 1e-4f, "scale must be exported raw (no handedness flip)");
            Assert.AreEqual(scale.y, (float)sAcc.Min[1], 1e-4f);
            Assert.AreEqual(scale.z, (float)sAcc.Min[2], 1e-4f);
        }

        [Test]
        public void SkeletonAndReferencePoseExport_PreservesAllSetsAndDuplicatePoseLabels()
        {
            const string alternateVocab = "https://example.com/skeleton/alternate/v1";
            var root = new GameObject("char");
            _created.Add(root);
            var hips = new GameObject("Hips").transform; hips.SetParent(root.transform, false);
            var head = new GameObject("Head").transform; head.SetParent(hips, false);

            root.AddComponent<SkeletonMap>().Bind(new SkeletonMappingResult
            {
                MappingSets = new[]
                {
                    new SkeletonMappingSetResult
                    {
                        Identifier = SkeletonVocab,
                        Associations = new Dictionary<string, Transform> { { "hips", hips } },
                    },
                    new SkeletonMappingSetResult
                    {
                        Identifier = alternateVocab,
                        Associations = new Dictionary<string, Transform> { { "head", head } },
                    },
                },
                ReferencePoses = new[]
                {
                    new ReferencePose
                    {
                        AnimationIndex = 4, PoseType = "TPose", Bones = new[] { hips },
                        LocalPositions = new[] { Vector3.one },
                    },
                    new ReferencePose
                    {
                        AnimationIndex = 9, PoseType = "TPose", Bones = new[] { head },
                        LocalPositions = new[] { Vector3.up },
                    },
                },
            });

            var gltf = ExportToGltfRoot(root);
            var mapping = gltf.Extensions[KHR_character_skeleton_mapping.EXTENSION_NAME]
                as KHR_character_skeleton_mapping;
            Assert.IsNotNull(mapping);
            Assert.AreEqual(2, mapping.SkeletalRigMappings.Count);
            Assert.IsTrue(mapping.SkeletalRigMappings.ContainsKey(SkeletonVocab));
            Assert.IsTrue(mapping.SkeletalRigMappings.ContainsKey(alternateVocab));

            int poseCount = 0;
            foreach (var animation in gltf.Animations)
                if (animation.Extensions != null
                    && animation.Extensions.ContainsKey(KHR_character_reference_pose.EXTENSION_NAME))
                    poseCount++;
            Assert.AreEqual(2, poseCount,
                "duplicate poseType labels do not collapse distinct animation-indexed reference poses");
        }

        [Test]
        public void NonFacialAnimation_NotExportedAsExpression()
        {
            // Scope guard: the exporter only emits expressions that live in the CharacterExpressionSet. A standard
            // Unity animation driving an unrelated bone must export as a PLAIN glTF animation and must NOT be
            // referenced by any KHR_character_expression item (KHR_character is facial-expressions-only).
            var root = new GameObject("char");
            _created.Add(root);
            var jaw = new GameObject("jaw").transform;
            jaw.SetParent(root.transform, false);

            // The only facial expression in the set -> the only KHR_character_expression item.
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "jawOpen", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(jaw) },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            // A standard, non-facial Unity animation on a separate bone (NOT part of the expression set). A legacy
            // clip animating all three localPosition components (UnityGLTF bakes T/S per-component; partial curves
            // would throw) so the standard animation exporter emits a plain glTF animation named "Wave".
            var arm = new GameObject("armBone");
            arm.transform.SetParent(root.transform, false);
            var clip = new AnimationClip { name = "Wave", legacy = true };
            _created.Add(clip);
            var curve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 1f));
            clip.SetCurve("", typeof(Transform), "localPosition.x", curve);
            clip.SetCurve("", typeof(Transform), "localPosition.y", curve);
            clip.SetCurve("", typeof(Transform), "localPosition.z", curve);
            var animation = arm.AddComponent<Animation>();
            animation.playAutomatically = false;
            animation.AddClip(clip, "Wave");
            animation.clip = clip;

            var gltf = ExportToGltfRoot(root);

            // The facial expression is present, and it is the ONLY expression item.
            Assert.IsTrue(gltf.Extensions != null && gltf.Extensions.ContainsKey(KHR_character_expression.EXTENSION_NAME),
                "KHR_character_expression root extension should be present");
            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            Assert.AreEqual(1, ext.Expressions.Count, "only the set's facial expression should be an expression item");
            Assert.AreEqual("jawOpen", ext.Expressions[0].Expression);

            // The standard non-facial animation exported as a plain glTF animation...
            int waveIndex = gltf.Animations.FindIndex(a => a.Name == "Wave");
            Assert.GreaterOrEqual(waveIndex, 0,
                "the standard non-facial animation should be exported as a plain glTF animation");

            // ...and it is NOT referenced by any KHR_character_expression item.
            var expressionAnimIndices = new HashSet<int>();
            foreach (var item in ext.Expressions) expressionAnimIndices.Add(item.Animation);
            Assert.IsFalse(expressionAnimIndices.Contains(waveIndex),
                "a non-facial animation must NOT be referenced by KHR_character_expression");

            // The expression's own animation is a different, dedicated track.
            Assert.IsTrue(expressionAnimIndices.Contains(ext.Expressions[0].Animation));
            Assert.AreNotEqual(waveIndex, ext.Expressions[0].Animation);
        }

        [Test]
        public void ExportPlugin_RespectsScopeRule_FacialExpressionsOnly()
        {
            // The exporter writes whatever is in the CharacterExpressionSet.
            // The caller is responsible for putting only facial expressions (0→1 driven,
            // no loop expectation) into the set. Body/locomotion animations must use
            // standard glTF animation export, not the KHR plugin.
            //
            // This test documents the scope rule; actual enforcement is by convention
            // (the exporter cannot infer intent from data alone).
            var plugin = ScriptableObject.CreateInstance<KhrCharacterExportPlugin>();
            StringAssert.Contains("facial", plugin.Description.ToLowerInvariant(),
                "Plugin description should document the facial-expressions-only scope rule");
            Object.DestroyImmediate(plugin);
        }

        [Test]
        public void ExportDiscovery_ScopedToExportRoot_IgnoresUnrelatedSceneCharacter()
        {
            // R3: character discovery must be scoped to the export root (exporter.RootTransforms), NOT a global
            // scene scan. Two independent character roots coexist in the scene; exporting ONLY rootA must emit
            // rootA's expression ("A_only") and never rootB's ("B_only"). With the old FindObjectOfType discovery,
            // rootB's components could win/leak into rootA's export. This locks the deterministic, isolated behavior.
            var rootA = new GameObject("charA");
            _created.Add(rootA);
            var ctrlA = new GameObject("ctrlA").transform;
            ctrlA.SetParent(rootA.transform, false);
            rootA.AddComponent<ExpressionController>().Initialize(new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "A_only", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrlA) } },
                },
            });

            // A second, unrelated character elsewhere in the same scene (NOT under rootA, NOT exported).
            var rootB = new GameObject("charB");
            _created.Add(rootB);
            var ctrlB = new GameObject("ctrlB").transform;
            ctrlB.SetParent(rootB.transform, false);
            rootB.AddComponent<ExpressionController>().Initialize(new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "B_only", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrlB) } },
                },
            });

            var gltf = ExportToGltfRoot(rootA);

            Assert.IsTrue(gltf.Extensions != null && gltf.Extensions.ContainsKey(KHR_character_expression.EXTENSION_NAME),
                "exporting rootA should still emit KHR_character_expression");
            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            Assert.IsNotNull(ext.Expressions.Find(e => e.Expression == "A_only"),
                "the exported expression must come from the export root (rootA)");
            Assert.IsNull(ext.Expressions.Find(e => e.Expression == "B_only"),
                "an unrelated character elsewhere in the scene must NOT leak into rootA's export (R3 isolation)");

            // rootB's hierarchy must not appear in rootA's export at all.
            Assert.IsFalse(gltf.Nodes.Exists(n => n.Name == "ctrlB"),
                "nodes from the unrelated character (rootB) must not be present in rootA's export");

            // Stronger: the root KHR_character.rootNode must resolve to rootA's own node, not rootB's.
            Assert.IsTrue(gltf.Extensions.ContainsKey(KHR_character.EXTENSION_NAME), "root KHR_character should be present");
            var rootExt = gltf.Extensions[KHR_character.EXTENSION_NAME] as KHR_character;
            Assert.IsNotNull(rootExt);
            Assert.IsTrue(rootExt.RootNode.HasValue, "KHR_character.rootNode should be set");
            Assert.AreEqual("charA", gltf.Nodes[rootExt.RootNode.Value].Name,
                "rootNode must point at the exported root (charA), proving root-scoped discovery");
        }

        [Test]
        public void ExportedExpressions_CarryNoVendorExtras_WireIsNeutral()
        {
            // R4 neutrality guard: the exporter must emit NO vendor extras on expression items — not for an
            // all-default expression, and not for one with a non-Additive blend mode or a non-zero driver
            // priority. blendMode/priority are intentionally not exported (no ratified KHR field; the import baker
            // reconstructs Additive + Priority 0), so any META_* token would be write-only and break Khronos wire
            // neutrality. This test FAILS if anyone re-introduces a vendor extras token.
            var root = new GameObject("char");
            _created.Add(root);
            var ctrlDefault = new GameObject("ctrlDefault").transform; ctrlDefault.SetParent(root.transform, false);
            var ctrlOverride = new GameObject("ctrlOverride").transform; ctrlOverride.SetParent(root.transform, false);
            var ctrlPriority = new GameObject("ctrlPriority").transform; ctrlPriority.SetParent(root.transform, false);

            var prioritized = RotationDriver(ctrlPriority);
            prioritized.Priority = 5;

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    // all-default: Additive blend mode, driver Priority 0
                    new ExpressionTrack { Name = "neutral", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrlDefault) } },
                    // non-default blend mode
                    new ExpressionTrack { Name = "blinkOverride", Domains = ExpressionDomain.Joint, BlendMode = ExpressionBlendMode.Override, JointDrivers = new[] { RotationDriver(ctrlOverride) } },
                    // non-zero priority
                    new ExpressionTrack { Name = "prioritized", Domains = ExpressionDomain.Joint, JointDrivers = new[] { prioritized } },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);

            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            Assert.AreEqual(3, ext.Expressions.Count, "all three expressions should export (each has a channel)");

            foreach (var item in ext.Expressions)
            {
                // Current behavior: no extras emitted at all (the strongest neutral wire).
                if (item.Extras == null) continue;
                // Tolerate future non-vendor extras, but fail hard on ANY vendor (META_*) token.
                Assert.IsNull(item.Extras["META_character_runtime"],
                    $"expression '{item.Expression}' must not carry the META_character_runtime vendor token (wire neutrality)");
                foreach (var prop in item.Extras.Properties())
                    StringAssert.DoesNotStartWith("META_", prop.Name,
                        $"expression '{item.Expression}' must not carry any META_* vendor extras token (wire neutrality)");
            }
        }

        // Builds a character root with one quad MeshRenderer + material, ready to carry a UV-transform texture
        // driver. Mirrors the inline setup in ExpressionMetadataExport_IncludesTextureChannels; the caller adds the
        // ExpressionController. Registers created objects for teardown.
        private GameObject MakeUvCharacter(Shader shader, out MeshRenderer mr)
        {
            var root = new GameObject("char");
            _created.Add(root);

            var quad = new GameObject("quad", typeof(MeshFilter), typeof(MeshRenderer));
            quad.transform.SetParent(root.transform, false);
            var mesh = new Mesh { name = "quad" };
            mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            _created.Add(mesh);
            quad.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mat = new Material(shader) { name = "mat" };
            _created.Add(mat);
            mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return root;
        }

        // Finds the KHR_texture_transform scale/offset pointer channels for the single texture expression "scroll".
        private static void FindUvChannels(GLTFRoot gltf, out Accessor scaleAcc, out Accessor offsetAcc)
        {
            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            var item = ext.Expressions.Find(e => e.Expression == "scroll");
            Assert.IsNotNull(item, "the exported expression item should be present");
            Assert.IsNotNull(item.Texture, "the expression should carry a texture sub-extension");

            var anim = gltf.Animations[item.Animation];
            AnimationChannel scaleCh = null, offsetCh = null;
            foreach (var ci in item.Texture.Channels)
            {
                var ch = anim.Channels[ci];
                var path = PointerPath(ch);
                if (path == null) continue;
                if (path.EndsWith("/extensions/KHR_texture_transform/scale")) scaleCh = ch;
                else if (path.EndsWith("/extensions/KHR_texture_transform/offset")) offsetCh = ch;
            }
            Assert.IsNotNull(scaleCh, "a KHR_texture_transform 'scale' pointer channel should be present");
            Assert.IsNotNull(offsetCh, "a KHR_texture_transform 'offset' pointer channel should be present");
            scaleAcc = OutputAccessor(gltf, anim, scaleCh);
            offsetAcc = OutputAccessor(gltf, anim, offsetCh);
        }

        [Test]
        public void UvTransform_MultiKey_AnchorsAtFrame0_NotMaterialRest()
        {
            // FU2: a multi-key UV driver carrying an authored frame-0 absolute (HasFrame0St) must export absolutes
            // anchored at Frame0St, NOT the material rest (BaseSt). Here Frame0St.z = 0.25 != BaseSt.z = 0, so the
            // exported frame-0 offset.x must be 0.25 (Frame0St-anchored). Pre-fix (BaseSt-anchored) it would be 0.
            var shader = Shader.Find("Unlit/Texture");
            if (shader == null) { Assert.Ignore("No suitable built-in shader available in this project."); return; }

            var root = MakeUvCharacter(shader, out var mr);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "scroll", Domains = ExpressionDomain.Texture,
                        TextureDrivers = new[]
                        {
                            new TextureDriver
                            {
                                Renderer = mr, SubmeshSlot = 0,
                                PropertyId = Shader.PropertyToID("_MainTex_ST"),
                                PropertyName = "_MainTex",
                                GltfTextureSlot = "pbrMetallicRoughness/baseColorTexture",
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                StValues = new[] { Vector4.zero, new Vector4(0f, 0f, 0.5f, 0f) },
                                BaseSt = new Vector4(1f, 1f, 0f, 0f),       // material rest (NOT the anchor)
                                Frame0St = new Vector4(1f, 1f, 0.25f, 0f),  // authored frame-0 absolute (the anchor)
                                HasFrame0St = true,
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);
            FindUvChannels(gltf, out var scaleAcc, out var offsetAcc);

            // offset.x is monotonic, so Min == key0, Max == key1. Frame0St-anchored: key0 = 0.25, key1 = 0.75.
            Assert.AreEqual(0.25f, (float)offsetAcc.Min[0], 1e-4f,
                "frame-0 offset.x must anchor at Frame0St.z (0.25), not BaseSt.z (0)");
            Assert.AreEqual(0.75f, (float)offsetAcc.Max[0], 1e-4f,
                "offset.x scrolls from the Frame0St baseline (0.25 -> 0.75)");

            // scale comes from Frame0St.xy (1,1).
            Assert.AreEqual(1f, (float)scaleAcc.Min[0], 1e-4f);
            Assert.AreEqual(1f, (float)scaleAcc.Min[1], 1e-4f);
        }

        [Test]
        public void UvTransform_MultiKey_NoFrame0St_FallsBackToBaseSt()
        {
            // Regression guard: a multi-key UV driver WITHOUT a captured frame-0 (HasFrame0St == false —
            // hand-authored / synthesized) keeps anchoring on BaseSt (the pre-FU2 behavior). This is exactly why
            // the existing UV tests, which never set Frame0St, remain correct after the FU2 change.
            var shader = Shader.Find("Unlit/Texture");
            if (shader == null) { Assert.Ignore("No suitable built-in shader available in this project."); return; }

            var root = MakeUvCharacter(shader, out var mr);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "scroll", Domains = ExpressionDomain.Texture,
                        TextureDrivers = new[]
                        {
                            new TextureDriver
                            {
                                Renderer = mr, SubmeshSlot = 0,
                                PropertyId = Shader.PropertyToID("_MainTex_ST"),
                                PropertyName = "_MainTex",
                                GltfTextureSlot = "pbrMetallicRoughness/baseColorTexture",
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                StValues = new[] { Vector4.zero, new Vector4(0f, 0f, 0.5f, 0f) },
                                BaseSt = new Vector4(1f, 1f, 0f, 0f),
                                // Frame0St left default; HasFrame0St == false -> BaseSt is the anchor.
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);
            FindUvChannels(gltf, out var scaleAcc, out var offsetAcc);

            Assert.AreEqual(0f, (float)offsetAcc.Min[0], 1e-4f, "no captured frame0 -> offset.x anchors at BaseSt.z (0)");
            Assert.AreEqual(0.5f, (float)offsetAcc.Max[0], 1e-4f, "offset.x scrolls from the BaseSt baseline (0 -> 0.5)");
            Assert.AreEqual(1f, (float)scaleAcc.Min[0], 1e-4f, "scale comes from BaseSt.xy (1,1)");
        }

        [Test]
        public void UvTransform_RoundTrip_FirstCycleExact_ForeignFrame0()
        {
            // FU2 end-to-end: a FOREIGN asset whose authored frame-0 _ST != material rest must round-trip exactly on
            // the FIRST cycle. The real import baker captures the frame-0 absolute (Frame0St) + frame-0-relative
            // deltas; the real exporter reconstructs the original absolute _ST per key. Proven through real
            // BuildUvTransformDriver + real export (no silent re-baselining to BaseSt).
            var shader = Shader.Find("Unlit/Texture");
            if (shader == null) { Assert.Ignore("No suitable built-in shader available in this project."); return; }

            var root = MakeUvCharacter(shader, out var mr);

            // Foreign source: absolute _ST per key with a frame-0 baseline (offset.z = 0.25) distinct from the
            // material rest (offset.z = 0). _ST = (tiling.x, tiling.y, offset.x, packedV).
            var stAbs = new[] { new Vector4(1f, 1f, 0.25f, 0f), new Vector4(1f, 1f, 0.75f, 0f) };
            var materialRest = new Vector4(1f, 1f, 0f, 0f);
            var times = new[] { 0f, 1f };

            var drivers = new List<TextureDriver>();
            KhrCharacterBaker.BuildUvTransformDriver(mr, 0, Shader.PropertyToID("_MainTex_ST"), times, stAbs,
                materialRest, InterpolationType.LINEAR, drivers, "_MainTex", "pbrMetallicRoughness/baseColorTexture");
            Assert.AreEqual(1, drivers.Count);
            var driver = drivers[0];

            // Baker captured the authored frame-0 absolute + frame-0-relative deltas; BaseSt stays the material rest.
            Assert.IsTrue(driver.HasFrame0St, "baker must mark the captured frame-0 absolute");
            Assert.AreEqual(0.25f, driver.Frame0St.z, 1e-5f,
                "Frame0St must be the authored frame-0 absolute, not the material rest");
            Assert.AreEqual(0f, driver.BaseSt.z, 1e-5f, "BaseSt stays the material rest");
            Assert.AreEqual(0f, driver.StValues[0].z, 1e-5f, "frame-0 delta is zero");
            Assert.AreEqual(0.5f, driver.StValues[1].z, 1e-5f, "key-1 delta is frame-0-relative (0.75 - 0.25)");

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "scroll", Domains = ExpressionDomain.Texture, TextureDrivers = new[] { driver } },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);
            FindUvChannels(gltf, out _, out var offsetAcc);

            // offset.x == _ST.z; the exported absolutes must equal the ORIGINAL foreign absolutes (0.25 -> 0.75).
            Assert.AreEqual(0.25f, (float)offsetAcc.Min[0], 1e-4f,
                "exported frame-0 offset.x must equal the original 0.25 (first-cycle exact)");
            Assert.AreEqual(0.75f, (float)offsetAcc.Max[0], 1e-4f,
                "exported key-1 offset.x must equal the original 0.75 (first-cycle exact)");
        }

        // ── #4: Camera-hint / look-at node-extension export ──────────────────────────────────────────────

        // Finds the exported glTF node index by name (export sets node.Name = transform.name when ExportNames).
        private static int FindNodeIndex(GLTFRoot gltf, string name) => gltf.Nodes.FindIndex(n => n.Name == name);

        // Reads a typed node extension off the exported node (null if the node or extension is absent).
        private static T NodeExtension<T>(GLTFRoot gltf, int nodeIndex, string name) where T : class
            => nodeIndex >= 0 && gltf.Nodes[nodeIndex].Extensions != null
               && gltf.Nodes[nodeIndex].Extensions.TryGetValue(name, out var ext) ? ext as T : null;

        [Test]
        public void CameraHintExport_EmitsNodeExtension()
        {
            // A CameraHintSet hint exports as a KHR_node_camera_hint on the hint's node, carrying role/label and a
            // resolved targetNode index. Declared used, never required (neutral). Mirrors KHR_node_visibility export.
            var root = new GameObject("char");
            _created.Add(root);
            root.AddComponent<KhrCharacter>();   // discoverable character root (FindCharacterRoot detects the hub)

            var camNode = new GameObject("camNode").transform; camNode.SetParent(root.transform, false);
            var faceNode = new GameObject("faceNode").transform; faceNode.SetParent(root.transform, false);

            root.AddComponent<CameraHintSet>().Bind(new List<CameraHint>
            {
                new CameraHint { Role = "portrait", Label = "Profile", Node = camNode, Target = faceNode },
            });

            var gltf = ExportToGltfRoot(root);

            int camIdx = FindNodeIndex(gltf, "camNode");
            Assert.GreaterOrEqual(camIdx, 0, "the camera-hint node should be exported");
            var ext = NodeExtension<KHR_node_camera_hint>(gltf, camIdx, KHR_node_camera_hint.EXTENSION_NAME);
            Assert.IsNotNull(ext, "the camera-hint node should carry KHR_node_camera_hint");
            Assert.AreEqual("portrait", ext.Role);
            Assert.AreEqual("Profile", ext.Label);
            Assert.IsTrue(ext.TargetNode.HasValue, "targetNode should be set");
            Assert.AreEqual(FindNodeIndex(gltf, "faceNode"), ext.TargetNode.Value, "targetNode must resolve to the face node");
            Assert.IsFalse(ext.Camera.HasValue, "no Projection bound -> camera index omitted (documented boundary)");

            // Neutrality: used, never required.
            Assert.IsTrue(gltf.ExtensionsUsed != null && gltf.ExtensionsUsed.Contains(KHR_node_camera_hint.EXTENSION_NAME),
                "KHR_node_camera_hint must be declared in extensionsUsed");
            Assert.IsTrue(gltf.ExtensionsRequired == null || !gltf.ExtensionsRequired.Contains(KHR_node_camera_hint.EXTENSION_NAME),
                "KHR_node_camera_hint must NOT be required (neutrality)");
        }

        [Test]
        public void LookatTargetExport_EmitsNodeExtension()
        {
            // A GazeSolver authored target exports as a KHR_node_lookat_target on the target's node, carrying hint.
            // Declared used, never required.
            var root = new GameObject("char");
            _created.Add(root);
            root.AddComponent<KhrCharacter>();

            var targetNode = new GameObject("gazeTarget").transform; targetNode.SetParent(root.transform, false);

            root.AddComponent<GazeSolver>().Bind(
                new List<LookAtTarget> { new LookAtTarget { Node = targetNode, Hint = "eye_target" } }, null);

            var gltf = ExportToGltfRoot(root);

            int idx = FindNodeIndex(gltf, "gazeTarget");
            Assert.GreaterOrEqual(idx, 0, "the look-at target node should be exported");
            var ext = NodeExtension<KHR_node_lookat_target>(gltf, idx, KHR_node_lookat_target.EXTENSION_NAME);
            Assert.IsNotNull(ext, "the target node should carry KHR_node_lookat_target");
            Assert.AreEqual("eye_target", ext.Hint);

            Assert.IsTrue(gltf.ExtensionsUsed != null && gltf.ExtensionsUsed.Contains(KHR_node_lookat_target.EXTENSION_NAME),
                "KHR_node_lookat_target must be declared in extensionsUsed");
            Assert.IsTrue(gltf.ExtensionsRequired == null || !gltf.ExtensionsRequired.Contains(KHR_node_lookat_target.EXTENSION_NAME),
                "KHR_node_lookat_target must NOT be required (neutrality)");
        }

        [Test]
        public void CameraHint_Lookat_RoundTrip()
        {
            // Export, then re-parse the emitted node extensions through their factories (the wire round-trip):
            // role/label/targetNode/hint must survive serialize -> deserialize unchanged.
            var root = new GameObject("char");
            _created.Add(root);
            root.AddComponent<KhrCharacter>();

            var camNode = new GameObject("camNode").transform; camNode.SetParent(root.transform, false);
            var faceNode = new GameObject("faceNode").transform; faceNode.SetParent(root.transform, false);

            root.AddComponent<CameraHintSet>().Bind(new List<CameraHint>
            {
                new CameraHint { Role = "orbit", Label = "Orbit", Node = camNode, Target = faceNode },
            });
            root.AddComponent<GazeSolver>().Bind(
                new List<LookAtTarget> { new LookAtTarget { Node = faceNode, Hint = "gaze" } }, null);

            var gltf = ExportToGltfRoot(root);

            int camIdx = FindNodeIndex(gltf, "camNode");
            int faceIdx = FindNodeIndex(gltf, "faceNode");
            var camExt = NodeExtension<KHR_node_camera_hint>(gltf, camIdx, KHR_node_camera_hint.EXTENSION_NAME);
            var lookExt = NodeExtension<KHR_node_lookat_target>(gltf, faceIdx, KHR_node_lookat_target.EXTENSION_NAME);
            Assert.IsNotNull(camExt);
            Assert.IsNotNull(lookExt);

            // Round-trip the camera hint through serialize -> factory deserialize.
            var camReparsed = new KHR_node_camera_hint_Factory().Deserialize(gltf, camExt.Serialize()) as KHR_node_camera_hint;
            Assert.IsNotNull(camReparsed);
            Assert.AreEqual("orbit", camReparsed.Role);
            Assert.AreEqual("Orbit", camReparsed.Label);
            Assert.IsTrue(camReparsed.TargetNode.HasValue);
            Assert.AreEqual(faceIdx, camReparsed.TargetNode.Value);

            // Round-trip the look-at target.
            var lookReparsed = new KHR_node_lookat_target_Factory().Deserialize(gltf, lookExt.Serialize()) as KHR_node_lookat_target;
            Assert.IsNotNull(lookReparsed);
            Assert.AreEqual("gaze", lookReparsed.Hint);
        }

        [Test]
        public void CameraHint_SelfTargetOmitted()
        {
            // Spec: a camera hint MUST NOT reference its own node. When Target == Node, the exported extension omits
            // targetNode (role still present).
            var root = new GameObject("char");
            _created.Add(root);
            root.AddComponent<KhrCharacter>();

            var camNode = new GameObject("camNode").transform; camNode.SetParent(root.transform, false);

            root.AddComponent<CameraHintSet>().Bind(new List<CameraHint>
            {
                new CameraHint { Role = "portrait", Node = camNode, Target = camNode },   // self-reference
            });

            var gltf = ExportToGltfRoot(root);

            var ext = NodeExtension<KHR_node_camera_hint>(gltf, FindNodeIndex(gltf, "camNode"), KHR_node_camera_hint.EXTENSION_NAME);
            Assert.IsNotNull(ext);
            Assert.AreEqual("portrait", ext.Role);
            Assert.IsFalse(ext.TargetNode.HasValue, "a self-referencing target must be omitted");
        }

        [Test]
        public void CameraHint_EmptyOrNullRole_NotExported_ValidRoleStillExported()
        {
            // Spec: KHR_node_camera_hint.role is REQUIRED (minLength:1). A hint whose Role is null or empty can only
            // ever serialize as an invalid extension (role cannot be omitted), so the exporter must SKIP that node's
            // camera hint entirely — never emit an empty/missing role — while a sibling hint with a valid role still
            // exports. Mirrors the other required-field export guards (and keeps the wire spec-valid + neutral).
            var root = new GameObject("char");
            _created.Add(root);
            root.AddComponent<KhrCharacter>();

            var emptyRoleNode = new GameObject("emptyRoleNode").transform; emptyRoleNode.SetParent(root.transform, false);
            var nullRoleNode = new GameObject("nullRoleNode").transform; nullRoleNode.SetParent(root.transform, false);
            var validRoleNode = new GameObject("validRoleNode").transform; validRoleNode.SetParent(root.transform, false);

            root.AddComponent<CameraHintSet>().Bind(new List<CameraHint>
            {
                new CameraHint { Role = "", Label = "Empty", Node = emptyRoleNode },          // empty role -> skipped
                new CameraHint { Role = null, Label = "Null", Node = nullRoleNode },          // null role  -> skipped
                new CameraHint { Role = "portrait", Label = "Valid", Node = validRoleNode },  // valid role -> exported
            });

            // Each invalid (empty/null) role is skipped with a spec-required-role warning; consume both.
            LogAssert.Expect(LogType.Warning, new Regex("has no 'role'.*skipping"));
            LogAssert.Expect(LogType.Warning, new Regex("has no 'role'.*skipping"));

            var gltf = ExportToGltfRoot(root);

            // The nodes themselves are still exported, but neither invalid hint emits a camera_hint extension.
            int emptyIdx = FindNodeIndex(gltf, "emptyRoleNode");
            Assert.GreaterOrEqual(emptyIdx, 0, "the empty-role node itself should still be exported");
            Assert.IsNull(NodeExtension<KHR_node_camera_hint>(gltf, emptyIdx, KHR_node_camera_hint.EXTENSION_NAME),
                "an empty role must NOT emit an invalid KHR_node_camera_hint");

            int nullIdx = FindNodeIndex(gltf, "nullRoleNode");
            Assert.GreaterOrEqual(nullIdx, 0, "the null-role node itself should still be exported");
            Assert.IsNull(NodeExtension<KHR_node_camera_hint>(gltf, nullIdx, KHR_node_camera_hint.EXTENSION_NAME),
                "a missing role must NOT emit an invalid KHR_node_camera_hint");

            // The valid-role hint still exports a well-formed extension (role satisfies minLength:1).
            int validIdx = FindNodeIndex(gltf, "validRoleNode");
            var validExt = NodeExtension<KHR_node_camera_hint>(gltf, validIdx, KHR_node_camera_hint.EXTENSION_NAME);
            Assert.IsNotNull(validExt, "a hint with a valid role must still export");
            Assert.AreEqual("portrait", validExt.Role);

            // The valid hint still declares the extension as used, never required (wire stays neutral).
            Assert.IsTrue(gltf.ExtensionsUsed != null && gltf.ExtensionsUsed.Contains(KHR_node_camera_hint.EXTENSION_NAME),
                "the valid hint must still declare KHR_node_camera_hint in extensionsUsed");
            Assert.IsTrue(gltf.ExtensionsRequired == null || !gltf.ExtensionsRequired.Contains(KHR_node_camera_hint.EXTENSION_NAME),
                "KHR_node_camera_hint must NOT be required (neutrality)");
        }

        [Test]
        public void CameraHint_EmptyOrNullLabel_OmittedFromWire_ValidLabelKept()
        {
            // Spec: KHR_node_camera_hint.label is OPTIONAL but minLength:1 WHEN PRESENT — an empty string is invalid.
            // Unity coerces a null [SerializeField] string to "" on prefab save, so a label that was absent on import
            // can resurface as "". With a valid role, the hint still exports, but an empty/null label must be OMITTED
            // from the wire while a real label is kept. Asserts the serialized JSON (the actual wire contract).
            var root = new GameObject("char");
            _created.Add(root);
            root.AddComponent<KhrCharacter>();

            var emptyLabelNode = new GameObject("emptyLabelNode").transform; emptyLabelNode.SetParent(root.transform, false);
            var nullLabelNode = new GameObject("nullLabelNode").transform; nullLabelNode.SetParent(root.transform, false);
            var validLabelNode = new GameObject("validLabelNode").transform; validLabelNode.SetParent(root.transform, false);

            root.AddComponent<CameraHintSet>().Bind(new List<CameraHint>
            {
                new CameraHint { Role = "portrait", Label = "",     Node = emptyLabelNode },   // empty label -> omitted
                new CameraHint { Role = "portrait", Label = null,   Node = nullLabelNode },    // null label  -> omitted
                new CameraHint { Role = "portrait", Label = "Hero", Node = validLabelNode },   // valid label -> kept
            });

            var gltf = ExportToGltfRoot(root);

            // Empty label: the hint still exports (role is valid) but the serialized wire must NOT contain "label".
            var emptyExt = NodeExtension<KHR_node_camera_hint>(gltf, FindNodeIndex(gltf, "emptyLabelNode"), KHR_node_camera_hint.EXTENSION_NAME);
            Assert.IsNotNull(emptyExt, "a valid role still exports the camera hint");
            var emptyObj = (JObject)emptyExt.Serialize().Value;
            Assert.IsTrue(emptyObj.ContainsKey("role"), "role must still be present on the wire");
            Assert.IsFalse(emptyObj.ContainsKey("label"), "an empty label must be omitted from the wire (schema minLength:1)");

            // Null label: same — no "label" key on the wire.
            var nullExt = NodeExtension<KHR_node_camera_hint>(gltf, FindNodeIndex(gltf, "nullLabelNode"), KHR_node_camera_hint.EXTENSION_NAME);
            Assert.IsNotNull(nullExt);
            Assert.IsFalse(((JObject)nullExt.Serialize().Value).ContainsKey("label"), "a null label must be omitted from the wire");

            // Valid label: emitted verbatim.
            var validExt = NodeExtension<KHR_node_camera_hint>(gltf, FindNodeIndex(gltf, "validLabelNode"), KHR_node_camera_hint.EXTENSION_NAME);
            Assert.IsNotNull(validExt);
            Assert.AreEqual("Hero", ((JObject)validExt.Serialize().Value)["label"]?.Value<string>(), "a non-empty label must be emitted");
        }

        [Test]
        public void LookatTarget_EmptyHint_OmittedFromWire_ValidHintKept()
        {
            // Spec: KHR_node_lookat_target.hint is OPTIONAL but minLength:1 WHEN PRESENT. An empty string (e.g. from a
            // prefab-saved null) must be omitted, leaving a valid empty {} extension (presence alone marks the target);
            // a real hint is kept. Asserts the serialized wire.
            var root = new GameObject("char");
            _created.Add(root);
            root.AddComponent<KhrCharacter>();

            var emptyHintNode = new GameObject("emptyHintNode").transform; emptyHintNode.SetParent(root.transform, false);
            var validHintNode = new GameObject("validHintNode").transform; validHintNode.SetParent(root.transform, false);

            root.AddComponent<GazeSolver>().Bind(new List<LookAtTarget>
            {
                new LookAtTarget { Node = emptyHintNode, Hint = "" },           // empty hint -> omitted, {} stays valid
                new LookAtTarget { Node = validHintNode, Hint = "eye_target" }, // valid hint -> kept
            }, null);

            var gltf = ExportToGltfRoot(root);

            // Empty hint: extension still present (marks the target) but the wire must NOT contain "hint".
            var emptyExt = NodeExtension<KHR_node_lookat_target>(gltf, FindNodeIndex(gltf, "emptyHintNode"), KHR_node_lookat_target.EXTENSION_NAME);
            Assert.IsNotNull(emptyExt, "presence alone marks the node as a look-at target");
            Assert.IsFalse(((JObject)emptyExt.Serialize().Value).ContainsKey("hint"), "an empty hint must be omitted from the wire (schema minLength:1)");

            // Valid hint: emitted.
            var validExt = NodeExtension<KHR_node_lookat_target>(gltf, FindNodeIndex(gltf, "validHintNode"), KHR_node_lookat_target.EXTENSION_NAME);
            Assert.IsNotNull(validExt);
            Assert.AreEqual("eye_target", ((JObject)validExt.Serialize().Value)["hint"]?.Value<string>(), "a non-empty hint must be emitted");
        }

        [Test]
        public void LookatTarget_EmptyHint_StillEmitsExtension()
        {
            // hint is optional: an empty {} is valid and its presence alone marks the node as a look-at target.
            var root = new GameObject("char");
            _created.Add(root);
            root.AddComponent<KhrCharacter>();

            var targetNode = new GameObject("gazeTarget").transform; targetNode.SetParent(root.transform, false);

            root.AddComponent<GazeSolver>().Bind(
                new List<LookAtTarget> { new LookAtTarget { Node = targetNode, Hint = null } }, null);

            var gltf = ExportToGltfRoot(root);

            var ext = NodeExtension<KHR_node_lookat_target>(gltf, FindNodeIndex(gltf, "gazeTarget"), KHR_node_lookat_target.EXTENSION_NAME);
            Assert.IsNotNull(ext, "the node must still be marked as a look-at target even with no hint");
            Assert.IsNull(ext.Hint, "hint stays null (empty extension object)");
        }

        [Test]
        public void GazeSolver_NoAuthoredTargets_EmitsNothing()
        {
            // A GazeSolver with zero authored targets (it is attached whenever expressions exist) must emit no
            // KHR_node_lookat_target on any node, and must not declare the extension.
            var root = new GameObject("char");
            _created.Add(root);
            var ctrl = new GameObject("ctrl").transform; ctrl.SetParent(root.transform, false);
            root.AddComponent<ExpressionController>().Initialize(new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "blink", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrl) } },
                },
            });
            root.AddComponent<GazeSolver>().Bind(new List<LookAtTarget>(), null);   // no authored targets

            var gltf = ExportToGltfRoot(root);

            foreach (var n in gltf.Nodes)
                Assert.IsTrue(n.Extensions == null || !n.Extensions.ContainsKey(KHR_node_lookat_target.EXTENSION_NAME),
                    "no node should carry KHR_node_lookat_target when there are no authored targets");
            Assert.IsTrue(gltf.ExtensionsUsed == null || !gltf.ExtensionsUsed.Contains(KHR_node_lookat_target.EXTENSION_NAME),
                "KHR_node_lookat_target must not be declared when nothing was emitted");
        }

        // ── #6: Deterministic root designation with multiple character-like roots ─────────────────────────

        [Test]
        public void MultiCharacter_ExportsFirstDeterministically_AndWarns()
        {
            // KHR_character carries one rootNode designation. With two character roots in the same export set, the
            // exporter designates the first RootTransforms entry and leaves the other as ordinary glTF content.
            var rootA = new GameObject("charA");
            _created.Add(rootA);
            var ctrlA = new GameObject("ctrlA").transform; ctrlA.SetParent(rootA.transform, false);
            rootA.AddComponent<ExpressionController>().Initialize(new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "A_only", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrlA) } },
                },
            });

            var rootB = new GameObject("charB");
            _created.Add(rootB);
            var ctrlB = new GameObject("ctrlB").transform; ctrlB.SetParent(rootB.transform, false);
            rootB.AddComponent<ExpressionController>().Initialize(new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "B_only", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrlB) } },
                },
            });

            // Expect the multi-character warning (consumes it so it does not fail the run).
            LogAssert.Expect(LogType.Warning, new Regex("Export contains 2 character roots.*charA.*charB"));

            var gltf = ExportRootsToGltfRoot(rootA, rootB);

            // Exactly one KHR_character, rooted at charA (the first).
            Assert.IsTrue(gltf.Extensions != null && gltf.Extensions.ContainsKey(KHR_character.EXTENSION_NAME),
                "a single root KHR_character should be present");
            var rootExt = gltf.Extensions[KHR_character.EXTENSION_NAME] as KHR_character;
            Assert.IsNotNull(rootExt);
            Assert.IsTrue(rootExt.RootNode.HasValue);
            Assert.AreEqual("charA", gltf.Nodes[rootExt.RootNode.Value].Name,
                "the first character root (charA) must win deterministically");

            // Only charA's expression is present; charB's must not leak.
            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            Assert.IsNotNull(ext.Expressions.Find(e => e.Expression == "A_only"),
                "the first character's expression must be exported");
            Assert.IsNull(ext.Expressions.Find(e => e.Expression == "B_only"),
                "the skipped character's expression must NOT leak into the document");
        }

        [Test]
        public void MultiCharacter_SeparateExports_EachRoundTripsIndependently()
        {
            // The recommended multi-character workflow: export each character root to its OWN glTF document. Each
            // document is a valid single-character file carrying only its own expression — and no warning fires
            // (each export set has exactly one character root).
            var rootA = new GameObject("charA");
            _created.Add(rootA);
            var ctrlA = new GameObject("ctrlA").transform; ctrlA.SetParent(rootA.transform, false);
            rootA.AddComponent<ExpressionController>().Initialize(new CharacterExpressionSet
            {
                Expressions = new[] { new ExpressionTrack { Name = "A_only", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrlA) } } },
            });

            var rootB = new GameObject("charB");
            _created.Add(rootB);
            var ctrlB = new GameObject("ctrlB").transform; ctrlB.SetParent(rootB.transform, false);
            rootB.AddComponent<ExpressionController>().Initialize(new CharacterExpressionSet
            {
                Expressions = new[] { new ExpressionTrack { Name = "B_only", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(ctrlB) } } },
            });

            var gltfA = ExportToGltfRoot(rootA);
            var extA = gltfA.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(extA);
            Assert.IsNotNull(extA.Expressions.Find(e => e.Expression == "A_only"));
            Assert.IsNull(extA.Expressions.Find(e => e.Expression == "B_only"));
            var rootExtA = gltfA.Extensions[KHR_character.EXTENSION_NAME] as KHR_character;
            Assert.AreEqual("charA", gltfA.Nodes[rootExtA.RootNode.Value].Name);

            var gltfB = ExportToGltfRoot(rootB);
            var extB = gltfB.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(extB);
            Assert.IsNotNull(extB.Expressions.Find(e => e.Expression == "B_only"));
            Assert.IsNull(extB.Expressions.Find(e => e.Expression == "A_only"));
            var rootExtB = gltfB.Extensions[KHR_character.EXTENSION_NAME] as KHR_character;
            Assert.AreEqual("charB", gltfB.Nodes[rootExtB.RootNode.Value].Name);
        }
    }
}
