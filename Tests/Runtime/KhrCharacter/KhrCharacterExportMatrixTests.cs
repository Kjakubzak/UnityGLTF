using System.Collections.Generic;
using System.Reflection;
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
    /// Phase-Z matrix-gap coverage for the KHR_character EXPORT path that the original export suite left open:
    /// mixed-domain channel partition (T3), skeleton-only export (X1), null/missing-target skips (X3/X6),
    /// duplicate node names (X4), the baked-state
    /// edit-time fallback (N2), correctness with KHR_animation_pointer export disabled (N3), shared-material
    /// resolution (P4), and sampler-input min/max across all domains (P3). All drive a real GLTFSceneExporter
    /// over an in-memory character, mirroring KhrCharacterExportTests. PlayMode (the export pipeline needs the
    /// Unity runtime). None of these duplicate an existing test.
    /// </summary>
    public class KhrCharacterExportMatrixTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        // ── Export harness (mirrors KhrCharacterExportTests.ExportToGltfRoot) ───────────────────────

        // Exports `root` with the KHR_character plugin AND AnimationPointer export enabled.
        // GetDefaultSettings() is an isolated instance, so toggling plugins here never touches global settings.
        private static GLTFRoot ExportToGltfRoot(GameObject root)
        {
            var settings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in settings.ExportPlugins)
                if (plugin is KhrCharacterExportPlugin || plugin is AnimationPointerExport)
                    plugin.Enabled = true;

            var exporter = new GLTFSceneExporter(new[] { root.transform }, new ExportContext(settings));
            exporter.SaveGLBToByteArray("scene");
            return exporter.GetRoot();
        }

        // Exports `root` with the KHR_character plugin enabled but AnimationPointer export DISABLED (N3).
        private static GLTFRoot ExportToGltfRootPointerDisabled(GameObject root)
        {
            var settings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in settings.ExportPlugins)
            {
                if (plugin is KhrCharacterExportPlugin) plugin.Enabled = true;
                else if (plugin is AnimationPointerExport) plugin.Enabled = false;
            }

            var exporter = new GLTFSceneExporter(new[] { root.transform }, new ExportContext(settings));
            exporter.SaveGLBToByteArray("scene");
            return exporter.GetRoot();
        }

        private static string PointerPath(AnimationChannel channel)
            => channel.Target?.Extensions != null
               && channel.Target.Extensions.TryGetValue(KHR_animation_pointer.EXTENSION_NAME, out var ext)
               && ext is KHR_animation_pointer p ? p.path : null;

        private static Accessor InputAccessor(GLTFRoot root, GLTFAnimation anim, AnimationChannel channel)
            => root.Accessors[anim.Samplers[channel.Sampler.Id].Input.Id];

        // A single-key joint rotation driver: enough to make an expression emit one native channel.
        private static JointDriver RotationDriver(Transform target, int priority = 0) => new JointDriver
        {
            Target = target, Channel = TrsChannel.Rotation, Priority = priority,
            Sampler = new Sampler { Times = new[] { 0f }, Interp = Interp.Step, SingleKey = true },
            DeltaQuat = new[] { Quaternion.Euler(10f, 0f, 0f) }, BaseQuat = Quaternion.identity,
        };

        private SkinnedMeshRenderer MakeMorphSmr(Transform parent, string name, int blendShapeCount)
        {
            var mesh = new Mesh { name = name + "_mesh" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            var delta = new[] { Vector3.up, Vector3.up, Vector3.up };
            for (int i = 0; i < blendShapeCount; i++)
                mesh.AddBlendShapeFrame("shape" + i, 100f, delta, null, null);
            _created.Add(mesh);

            var go = new GameObject(name, typeof(SkinnedMeshRenderer));
            go.transform.SetParent(parent, false);
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            return smr;
        }

        // A child quad carrying a real mesh + the supplied material, so the material is exported and
        // GetMaterialId resolves (the texture export path needs it).
        private MeshRenderer MakeQuad(Transform parent, string name, Material mat)
        {
            var quad = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            quad.transform.SetParent(parent, false);
            var mesh = new Mesh { name = name + "_mesh" };
            mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            _created.Add(mesh);
            quad.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return mr;
        }

        private Material MakeMaterial(Shader shader, string name)
        {
            var mat = new Material(shader) { name = name };
            _created.Add(mat);
            return mat;
        }

        private static TextureDriver UvTransformDriver(Renderer mr)
            => new TextureDriver
            {
                Renderer = mr, SubmeshSlot = 0,
                PropertyId = Shader.PropertyToID("_MainTex_ST"), PropertyName = "_MainTex",
                GltfTextureSlot = "pbrMetallicRoughness/baseColorTexture",
                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                StValues = new[] { Vector4.zero, new Vector4(0f, 0f, 1f, 0f) },
                BaseSt = new Vector4(1f, 1f, 0f, 0f),
            };

        private static Shader UnlitTextureShaderOrIgnore()
        {
            var shader = Shader.Find("Unlit/Texture");
            if (shader == null) Assert.Ignore("No suitable built-in shader available in this project.");
            return shader;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var f = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(f, $"expected a private field '{fieldName}' on {target.GetType().Name}");
            f.SetValue(target, value);
        }

        // ── T3: a single expression with morph + joint + texture drivers partitions its channels ────

        [Test]
        public void ExpressionMetadataExport_PartitionsMixedDomainChannels()
        {
            // T3: one expression that drives all three domains must split its animation channels cleanly across
            // the three sub-extensions: each domain's channel indices are disjoint, together cover every channel,
            // and each references the right KIND of channel (morph -> /weights/ pointer, joint -> native TRS,
            // texture -> /materials/ pointer). The original suite tests each domain only in isolation.
            var shader = UnlitTextureShaderOrIgnore();

            var root = new GameObject("char");
            _created.Add(root);
            var smr = MakeMorphSmr(root.transform, "face", 1);
            var jaw = new GameObject("jaw").transform; jaw.SetParent(root.transform, false);
            var mat = MakeMaterial(shader, "mat");
            var mr = MakeQuad(root.transform, "quad", mat);

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "mixed",
                        Domains = ExpressionDomain.Morph | ExpressionDomain.Joint | ExpressionDomain.Texture,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Smr = smr, BlendShapeIndex = 0, BaseValue = 0f,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Step, SingleKey = false },
                                DeltaValues = new[] { 0f, 1f },
                            },
                        },
                        JointDrivers = new[] { RotationDriver(jaw) },
                        TextureDrivers = new[] { UvTransformDriver(mr) },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);

            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            var item = ext.Expressions.Find(e => e.Expression == "mixed");
            Assert.IsNotNull(item, "the mixed-domain expression item should be present");
            Assert.IsNotNull(item.Morphtarget, "morph sub-extension present");
            Assert.IsNotNull(item.Joint, "joint sub-extension present");
            Assert.IsNotNull(item.Texture, "texture sub-extension present");
            Assert.AreEqual(1, item.Morphtarget.Channels.Length, "one morph driver -> one channel");
            Assert.AreEqual(1, item.Joint.Channels.Length, "one joint rotation driver -> one channel");
            Assert.AreEqual(2, item.Texture.Channels.Length, "one UV-transform driver -> scale and offset channels");

            var anim = gltf.Animations[item.Animation];

            // Disjoint + total partition: the three domains index distinct channels.
            var morph = new HashSet<int>(item.Morphtarget.Channels);
            var joint = new HashSet<int>(item.Joint.Channels);
            var tex = new HashSet<int>(item.Texture.Channels);
            Assert.AreEqual(0, CountIntersect(morph, joint), "morph and joint channels must be disjoint");
            Assert.AreEqual(0, CountIntersect(morph, tex), "morph and texture channels must be disjoint");
            Assert.AreEqual(0, CountIntersect(joint, tex), "joint and texture channels must be disjoint");
            var union = new HashSet<int>(morph); union.UnionWith(joint); union.UnionWith(tex);
            Assert.AreEqual(4, union.Count, "the partition must cover the four emitted channels");
            foreach (var ci in union)
                Assert.IsTrue(ci >= 0 && ci < anim.Channels.Count, "every referenced channel index must be valid");

            // Each domain references the correct channel KIND.
            var morphCh = anim.Channels[item.Morphtarget.Channels[0]];
            Assert.AreEqual("pointer", morphCh.Target.Path, "morph is a KHR_animation_pointer channel");
            StringAssert.Contains("/weights/", PointerPath(morphCh) ?? "", "morph pointer targets a blendshape weight");

            var jointCh = anim.Channels[item.Joint.Channels[0]];
            Assert.IsNotNull(jointCh.Target.Node, "joint is a NATIVE TRS channel (target.Node set)");
            Assert.AreNotEqual("pointer", jointCh.Target.Path, "joint must not route through KHR_animation_pointer");

            var texCh = anim.Channels[item.Texture.Channels[0]];
            Assert.AreEqual("pointer", texCh.Target.Path, "texture is a KHR_animation_pointer channel");
            StringAssert.StartsWith("/materials/", PointerPath(texCh) ?? "", "texture pointer targets a material");
        }

        private static int CountIntersect(HashSet<int> a, HashSet<int> b)
        {
            int n = 0;
            foreach (var x in a) if (b.Contains(x)) n++;
            return n;
        }

        // ── X1: skeleton-only export keeps the root KHR_character; no (empty) expression extension ──

        [Test]
        public void SkeletonOnlyExport_EmitsRootCharacter_NoExpressionExtension()
        {
            // X1: a character with a skeleton but ZERO expressions must still emit the root KHR_character
            // extension (the import gate, F2) and the skeleton mapping, and must NOT emit an (empty)
            // KHR_character_expression extension.
            var root = new GameObject("char");
            _created.Add(root);
            var hips = new GameObject("Hips").transform; hips.SetParent(root.transform, false);
            var head = new GameObject("Head").transform; head.SetParent(hips, false);

            var skel = root.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", hips }, { "head", head } },
                SelectedRig = "unityHumanoid",
            });
            // Deliberately NO ExpressionController.

            var gltf = ExportToGltfRoot(root);

            Assert.IsNotNull(gltf.Extensions, "the export must carry root extensions");
            Assert.IsTrue(gltf.Extensions.ContainsKey(KHR_character.EXTENSION_NAME),
                "root KHR_character must be present even with zero expressions (the import gate)");
            Assert.IsTrue(gltf.Extensions.ContainsKey(KHR_character_skeleton_mapping.EXTENSION_NAME),
                "the skeleton mapping must still be emitted");
            Assert.IsFalse(gltf.Extensions.ContainsKey(KHR_character_expression.EXTENSION_NAME),
                "no expressions -> no (empty) KHR_character_expression extension on the wire");
        }

        // ── X3: a driver whose target is not in the export is skipped + warns; survivors stay contiguous ──

        [Test]
        public void JointDriver_TargetNotInExport_SkippedWithWarning_ContiguousChannels()
        {
            // X3: a joint driver whose target Transform is not part of the exported hierarchy must be skipped
            // with a warning (KhrCharacterExportContext.WriteJointDriver), and the surviving drivers' channels
            // must remain contiguous and valid (no gap left by the skipped driver).
            var root = new GameObject("char");
            _created.Add(root);
            var jaw = new GameObject("jaw").transform; jaw.SetParent(root.transform, false);

            // A loose transform NOT parented under the export root -> GetTransformIndex returns -1 -> warn+skip.
            var loose = new GameObject("looseBone").transform;
            _created.Add(loose.gameObject);

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
                            // valid (channel 0)
                            new JointDriver
                            {
                                Target = jaw, Channel = TrsChannel.Rotation,
                                Sampler = new Sampler { Times = new[] { 0f }, Interp = Interp.Step, SingleKey = true },
                                DeltaQuat = new[] { Quaternion.Euler(15f, 0f, 0f) }, BaseQuat = Quaternion.identity,
                            },
                            // invalid target -> skipped + warned
                            new JointDriver
                            {
                                Target = loose, Channel = TrsChannel.Rotation,
                                Sampler = new Sampler { Times = new[] { 0f }, Interp = Interp.Step, SingleKey = true },
                                DeltaQuat = new[] { Quaternion.Euler(20f, 0f, 0f) }, BaseQuat = Quaternion.identity,
                            },
                            // valid (channel 1)
                            new JointDriver
                            {
                                Target = jaw, Channel = TrsChannel.Translation,
                                Sampler = new Sampler { Times = new[] { 0f }, Interp = Interp.Step, SingleKey = true },
                                DeltaVec = new[] { new Vector3(0f, 0.1f, 0f) }, BaseVec = Vector3.zero,
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                @"\[KHR_character\] Joint target 'looseBone' is not part of the export"));

            var gltf = ExportToGltfRoot(root);

            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            var item = ext.Expressions.Find(e => e.Expression == "jawOpen");
            Assert.IsNotNull(item, "the expression item is still emitted from the surviving drivers");
            Assert.IsNotNull(item.Joint);
            Assert.AreEqual(2, item.Joint.Channels.Length, "only the two in-export drivers contribute channels");

            var anim = gltf.Animations[item.Animation];
            // Surviving channel indices are contiguous (0,1) and each is a valid native channel.
            var indices = new List<int>(item.Joint.Channels);
            indices.Sort();
            Assert.AreEqual(0, indices[0], "first surviving channel is index 0 (no gap from the skip)");
            Assert.AreEqual(1, indices[1], "second surviving channel is index 1 (contiguous)");
            foreach (var ci in item.Joint.Channels)
            {
                Assert.IsTrue(ci >= 0 && ci < anim.Channels.Count, "channel index in range");
                Assert.IsNotNull(anim.Channels[ci].Target.Node, "surviving joint channel is native TRS");
            }
        }

        // ── X4: node-index mapping is unambiguous even when two bound bones share a Unity name ──

        [Test]
        public void SkeletonMappingExport_DuplicateNodeNames_ValuesResolveToDistinctNodeIndices()
        {
            // X4: the skeleton mapping emits each bound bone's glTF node INDEX, so even when two bound bones share
            // a Unity name the mapping is unambiguous — each joint resolves to its own distinct node index (the
            // former name-based ambiguity is gone entirely).
            var root = new GameObject("char");
            _created.Add(root);
            var b1 = new GameObject("Joint").transform; b1.SetParent(root.transform, false);
            var b2 = new GameObject("Joint").transform; b2.SetParent(root.transform, false); // same name
            var spine = new GameObject("Spine").transform; spine.SetParent(root.transform, false); // unique control

            var skel = root.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", b1 }, { "head", b2 }, { "spine", spine } },
                SelectedRig = "unityHumanoid",
            });

            var gltf = ExportToGltfRoot(root);

            var ext = gltf.Extensions[KHR_character_skeleton_mapping.EXTENSION_NAME] as KHR_character_skeleton_mapping;
            Assert.IsNotNull(ext);
            var rig = ext.SkeletalRigMappings["unityHumanoid"];
            Assert.AreEqual(3, rig.Count);

            // Every value is a valid, non-negative index into nodes[].
            foreach (var kv in rig)
            {
                Assert.GreaterOrEqual(kv.Value.Node, 0, $"mapping value for '{kv.Key}' must be a non-negative node index");
                Assert.Less(kv.Value.Node, gltf.Nodes.Count, $"mapping value for '{kv.Key}' must index a real exported node");
                Assert.AreEqual(gltf.Nodes[kv.Value.Node].Name, kv.Value.Name);
            }

            // The unique-named control bone resolves to the node actually named "Spine".
            Assert.AreEqual("Spine", gltf.Nodes[rig["spine"].Node].Name);

            // The two same-named bones resolve to DISTINCT node indices — no ambiguity despite the shared name.
            Assert.AreNotEqual(rig["hips"].Node, rig["head"].Node, "same-named bound bones still map to distinct node indices");
            Assert.AreEqual("Joint", gltf.Nodes[rig["hips"].Node].Name);
            Assert.AreEqual("Joint", gltf.Nodes[rig["head"].Node].Name);

            // Both colliding bones were exported as distinct nodes.
            int jointNodes = gltf.Nodes.FindAll(n => n.Name == "Joint").Count;
            Assert.AreEqual(2, jointNodes, "both same-named bones export as distinct nodes");
        }

        // ── X6: a texture driver with no glTF slot is skipped + warns; the expression still emits ───

        [Test]
        public void TextureDriver_EmptyGltfSlot_SkippedWithWarning()
        {
            // X6: a TextureDriver whose GltfTextureSlot was never captured (empty) cannot be addressed on the
            // wire, so it is skipped with a warning (KhrCharacterExportContext.WriteTextureDriver). A sibling
            // joint driver keeps the expression item alive, and it carries no texture sub-extension.
            var root = new GameObject("char");
            _created.Add(root);
            var jaw = new GameObject("jaw").transform; jaw.SetParent(root.transform, false);
            // Renderer must be non-null for the slot check to be reached. A real mesh keeps the export clean;
            // a material is attached only if a shader is available (the guard returns before material use, so
            // this test stays shader-independent).
            var shader = Shader.Find("Unlit/Texture");
            var mat = shader != null ? MakeMaterial(shader, "mat") : null;
            var mr = MakeQuad(root.transform, "quad", mat);

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "scrollNoSlot",
                        Domains = ExpressionDomain.Joint | ExpressionDomain.Texture,
                        JointDrivers = new[] { RotationDriver(jaw) },
                        TextureDrivers = new[]
                        {
                            new TextureDriver
                            {
                                Renderer = mr, SubmeshSlot = 0,
                                PropertyId = Shader.PropertyToID("_MainTex_ST"), PropertyName = "_MainTex",
                                GltfTextureSlot = "", // <- missing slot
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                StValues = new[] { Vector4.zero, new Vector4(0f, 0f, 1f, 0f) },
                                BaseSt = new Vector4(1f, 1f, 0f, 0f),
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                @"\[KHR_character\] TextureDriver has no GltfTextureSlot"));

            var gltf = ExportToGltfRoot(root);

            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            var item = ext.Expressions.Find(e => e.Expression == "scrollNoSlot");
            Assert.IsNotNull(item, "the joint driver keeps the expression item alive");
            Assert.IsNotNull(item.Joint, "the joint channel survived");
            Assert.IsNull(item.Texture, "the slot-less texture driver emitted no texture sub-extension");
        }

        // ── N2: edit-time export reads BakedSet / EditorBakedResult when runtime state is null ──────

        [Test]
        public void Export_UsesBakedSetAndEditorBakedResult_WhenRuntimeStateNull()
        {
            // N2 (F3): at edit time Awake/Initialize/Bind have not run, so ExpressionController.Set and
            // SkeletonMap.Result are null; the exporter must fall back to the serialized baked state
            // (ExpressionController.BakedSet / SkeletonMap.EditorBakedResult). We reproduce that state by
            // populating ONLY the serialized backing fields (the deserialized-prefab condition).
            var root = new GameObject("char");
            _created.Add(root);
            var jaw = new GameObject("jaw").transform; jaw.SetParent(root.transform, false);
            var hips = new GameObject("Hips").transform; hips.SetParent(root.transform, false);

            // Controller: Awake ran with no serialized set (so _set stays null); now plant the baked set only.
            var controller = root.AddComponent<ExpressionController>();
            var bakedSet = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "jawOpen", Domains = ExpressionDomain.Joint, JointDrivers = new[] { RotationDriver(jaw) } },
                },
            };
            SetPrivateField(controller, "_serializedSet", bakedSet);
            Assert.IsNull(controller.Set, "precondition: runtime Set is null (Awake did not Initialize)");
            Assert.IsNotNull(controller.BakedSet, "precondition: BakedSet is available");

            // Skeleton: plant the serialized mirror only, leaving the runtime Result null.
            var skeleton = root.AddComponent<SkeletonMap>();
            var bakedSkeleton = new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", hips } },
                SelectedRig = "unityHumanoid",
            };
            SetPrivateField(skeleton, "_serializedMapping", SerializableSkeletonMapping.FromResult(bakedSkeleton));
            Assert.IsNull(skeleton.Result, "precondition: runtime Result is null (Awake did not Bind)");
            Assert.IsNotNull(skeleton.EditorBakedResult, "precondition: EditorBakedResult is available");

            var gltf = ExportToGltfRoot(root);

            // Expressions came from BakedSet.
            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext, "expressions must be emitted from the baked set fallback");
            Assert.IsNotNull(ext.Expressions.Find(e => e.Expression == "jawOpen"));
            // Skeleton came from EditorBakedResult.
            var skelExt = gltf.Extensions[KHR_character_skeleton_mapping.EXTENSION_NAME] as KHR_character_skeleton_mapping;
            Assert.IsNotNull(skelExt, "skeleton mapping must be emitted from the editor-baked result fallback");
            Assert.IsTrue(skelExt.SkeletalRigMappings.ContainsKey("unityHumanoid"));
        }

        // ── N3: with AnimationPointer export disabled, joints/pose stay native and morph stays self-contained ──

        [Test]
        public void Export_WithAnimationPointerDisabled_JointsAndPoseNative_MorphStillPointer()
        {
            // N3: the KHR_character export does not depend on the AnimationPointer EXPORT plugin being enabled.
            // With it OFF, joints + reference pose are still NATIVE TRS (F7), the skeleton mapping is still
            // emitted, and morph weights are still written as KHR_animation_pointer channels (the KHR exporter
            // builds those channels itself). This is the pointer-OFF complement to the pointer-ON suite.
            var root = new GameObject("char");
            _created.Add(root);
            var smr = MakeMorphSmr(root.transform, "face", 1);
            var jaw = new GameObject("jaw").transform; jaw.SetParent(root.transform, false);
            var bone = new GameObject("bone").transform; bone.SetParent(root.transform, false);
            bone.localPosition = new Vector3(1f, 2f, 3f);

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "morphJoint",
                        Domains = ExpressionDomain.Morph | ExpressionDomain.Joint,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Smr = smr, BlendShapeIndex = 0, BaseValue = 0f,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Step, SingleKey = false },
                                DeltaValues = new[] { 0f, 1f },
                            },
                        },
                        JointDrivers = new[] { RotationDriver(jaw) },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var skel = root.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", bone } },
                SelectedRig = "unityHumanoid",
                ReferencePose = new ReferencePose
                {
                    PoseType = "TPose",
                    Bones = new[] { bone },
                    LocalPositions = new[] { bone.localPosition },
                    LocalRotations = new[] { Quaternion.identity },
                    LocalScales = new[] { Vector3.one },
                },
            });

            var gltf = ExportToGltfRootPointerDisabled(root);

            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext, "expressions are emitted regardless of the pointer export plugin state");
            var item = ext.Expressions.Find(e => e.Expression == "morphJoint");
            Assert.IsNotNull(item);
            Assert.IsNotNull(item.Joint, "joint sub-extension present");
            Assert.IsNotNull(item.Morphtarget, "morph sub-extension present");

            var anim = gltf.Animations[item.Animation];
            var jointCh = anim.Channels[item.Joint.Channels[0]];
            Assert.IsNotNull(jointCh.Target.Node, "joints stay NATIVE TRS even with pointer export disabled");
            Assert.AreNotEqual("pointer", jointCh.Target.Path);

            var morphCh = anim.Channels[item.Morphtarget.Channels[0]];
            Assert.AreEqual("pointer", morphCh.Target.Path, "morph is still emitted as a self-contained pointer channel");
            Assert.IsNotNull(PointerPath(morphCh), "morph pointer path present");

            // Skeleton mapping + reference pose unaffected.
            Assert.IsTrue(gltf.Extensions.ContainsKey(KHR_character_skeleton_mapping.EXTENSION_NAME),
                "skeleton mapping is emitted regardless of pointer export state");
            GLTFAnimation refAnim = null;
            foreach (var a in gltf.Animations)
                if (a.Extensions != null && a.Extensions.ContainsKey(KHR_character_reference_pose.EXTENSION_NAME)) { refAnim = a; break; }
            Assert.IsNotNull(refAnim, "the reference pose animation is emitted");
            foreach (var ch in refAnim.Channels)
            {
                Assert.IsNotNull(ch.Target.Node, "reference-pose channels stay NATIVE TRS with pointer export disabled");
                Assert.AreNotEqual("pointer", ch.Target.Path);
            }
        }

        // ── P4: two renderers sharing one material resolve to a single exported material index ───────

        [Test]
        public void TextureExport_SharedMaterial_ResolvesToSingleMaterialIndex()
        {
            // P4: a UV-transform pointer is addressed as /materials/{m}/... (per material, not per renderer).
            // When two renderers share one material, two expressions each animating one renderer's UV both
            // resolve to the SAME material index -- the documented shared-material ambiguity. This pins that
            // GetMaterialId collapses them to one material on the wire.
            var shader = UnlitTextureShaderOrIgnore();

            var root = new GameObject("char");
            _created.Add(root);
            var sharedMat = MakeMaterial(shader, "shared");
            var mrA = MakeQuad(root.transform, "quadA", sharedMat);
            var mrB = MakeQuad(root.transform, "quadB", sharedMat); // SAME material instance

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "scrollA", Domains = ExpressionDomain.Texture, TextureDrivers = new[] { UvTransformDriver(mrA) } },
                    new ExpressionTrack { Name = "scrollB", Domains = ExpressionDomain.Texture, TextureDrivers = new[] { UvTransformDriver(mrB) } },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var gltf = ExportToGltfRoot(root);

            Assert.IsNotNull(gltf.Materials);
            Assert.AreEqual(1, gltf.Materials.Count, "the shared material is exported exactly once");

            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            int matA = MaterialIndexOfFirstTextureChannel(gltf, ext, "scrollA");
            int matB = MaterialIndexOfFirstTextureChannel(gltf, ext, "scrollB");
            Assert.GreaterOrEqual(matA, 0, "scrollA texture pointer resolves a material index");
            Assert.AreEqual(matA, matB, "both expressions resolve to the same shared material index");
        }

        // Parses the /materials/{m}/... index out of the first texture pointer channel of the named expression.
        private static int MaterialIndexOfFirstTextureChannel(GLTFRoot gltf, KHR_character_expression ext, string name)
        {
            var item = ext.Expressions.Find(e => e.Expression == name);
            Assert.IsNotNull(item, $"expression '{name}' present");
            Assert.IsNotNull(item.Texture, $"expression '{name}' has texture channels");
            var anim = gltf.Animations[item.Animation];
            var ch = anim.Channels[item.Texture.Channels[0]];
            var path = PointerPath(ch);
            Assert.IsNotNull(path, "texture channel carries a pointer path");
            var parts = path.Split('/'); // ["", "materials", "{m}", ...]
            Assert.IsTrue(parts.Length >= 3 && parts[1] == "materials");
            return int.Parse(parts[2]);
        }

        // ── P3: sampler INPUT (time) accessors carry min/max for morph, joint, and reference pose ────

        [Test]
        public void SamplerInputs_CarryMinMax_ForMorphJointAndReferencePose()
        {
            // P3 (R6 hardening): every animation sampler INPUT (time) accessor must carry min/max or the asset is
            // invalid glTF. The existing texture test asserts this only for the texture path; this extends the
            // guarantee to morph, joint, and reference-pose sampler inputs.
            var root = new GameObject("char");
            _created.Add(root);
            var smr = MakeMorphSmr(root.transform, "face", 1);
            var jaw = new GameObject("jaw").transform; jaw.SetParent(root.transform, false);
            var bone = new GameObject("bone").transform; bone.SetParent(root.transform, false);

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "anim",
                        Domains = ExpressionDomain.Morph | ExpressionDomain.Joint,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Smr = smr, BlendShapeIndex = 0, BaseValue = 0f,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                DeltaValues = new[] { 0f, 1f },
                            },
                        },
                        JointDrivers = new[]
                        {
                            new JointDriver
                            {
                                Target = jaw, Channel = TrsChannel.Rotation,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                DeltaQuat = new[] { Quaternion.identity, Quaternion.Euler(15f, 0f, 0f) }, BaseQuat = Quaternion.identity,
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var skel = root.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", bone } },
                SelectedRig = "unityHumanoid",
                ReferencePose = new ReferencePose
                {
                    PoseType = "TPose", Bones = new[] { bone },
                    LocalPositions = new[] { Vector3.zero }, LocalRotations = new[] { Quaternion.identity }, LocalScales = new[] { Vector3.one },
                },
            });

            var gltf = ExportToGltfRoot(root);

            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            var item = ext.Expressions.Find(e => e.Expression == "anim");
            Assert.IsNotNull(item);
            var anim = gltf.Animations[item.Animation];

            AssertInputHasMinMax(InputAccessor(gltf, anim, anim.Channels[item.Morphtarget.Channels[0]]), "morph");
            AssertInputHasMinMax(InputAccessor(gltf, anim, anim.Channels[item.Joint.Channels[0]]), "joint");

            GLTFAnimation refAnim = null;
            foreach (var a in gltf.Animations)
                if (a.Extensions != null && a.Extensions.ContainsKey(KHR_character_reference_pose.EXTENSION_NAME)) { refAnim = a; break; }
            Assert.IsNotNull(refAnim, "reference pose animation present");
            AssertInputHasMinMax(InputAccessor(gltf, refAnim, refAnim.Channels[0]), "reference pose");
        }

        private static void AssertInputHasMinMax(Accessor input, string label)
        {
            Assert.IsNotNull(input.Min, $"{label} sampler input must have min");
            Assert.IsNotNull(input.Max, $"{label} sampler input must have max");
            Assert.LessOrEqual(System.Convert.ToDouble(input.Min[0]), System.Convert.ToDouble(input.Max[0]),
                $"{label} sampler input min <= max");
        }
    }
}
