using System.Collections.Generic;
using System.IO;
using GLTF;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;
using UnityGLTF.Extensions;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Phase-Z export -> re-bake round trips. The existing baker tests cover the IMPORT half (decoded arrays ->
    /// driver) and the export tests cover the EXPORT half (driver -> glTF), but the two halves were never joined
    /// for the value-carrying domains. These tests export a real character to GLB, decode the resulting sampler
    /// accessors straight out of the binary chunk, and feed them back through the real baker builders -- proving
    /// the export reconstruction is the exact inverse of the import delta math, with interpolation and handedness
    /// preserved (R1 morph LINEAR, R2 joint rotation+translation). Idempotence (P5) is proven WITHOUT a scene
    /// load by chaining that same machinery into repeated export -> re-bake cycles and asserting the pipeline
    /// reaches a fixed point (export #2 == export #3). PlayMode (the export pipeline needs the Unity runtime).
    /// </summary>
    public class KhrCharacterRoundTripTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        // Exports `root` (KHR_character + AnimationPointer enabled) and returns BOTH the GLB bytes (for decoding
        // sampler accessors out of the binary chunk) and the in-memory GLTFRoot (for the schema metadata).
        private static (byte[] glb, GLTFRoot root) ExportToGlb(GameObject root)
        {
            var settings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in settings.ExportPlugins)
                if (plugin is KhrCharacterExportPlugin || plugin is AnimationPointerExport)
                    plugin.Enabled = true;

            var exporter = new GLTFSceneExporter(new[] { root.transform }, new ExportContext(settings));
            var glb = exporter.SaveGLBToByteArray("scene");
            return (glb, exporter.GetRoot());
        }

        // Decodes a Float accessor's per-key values straight out of the GLB binary chunk. All KHR_character export
        // output/input accessors are ComponentType.Float and tightly packed, so plain BitConverter suffices (no
        // Unity.Collections / unsafe). Values are returned in glTF space (caller applies handedness if needed).
        private static float[] ReadAccessorFloats(byte[] glb, GLTFRoot root, Accessor acc)
        {
            using (var ms = new MemoryStream(glb))
            {
                GLTFParser.SeekToBinaryChunk(ms, 0);     // leaves ms.Position at the BIN-chunk data start
                long binStart = ms.Position;
                var bv = root.BufferViews[acc.BufferView.Id];
                long start = binStart + bv.ByteOffset + acc.ByteOffset;   // ByteOffset == 0 for these accessors
                int comps = ComponentCount(acc.Type);
                long stride = bv.ByteStride > 0 ? bv.ByteStride : (long)comps * sizeof(float);
                int count = (int)acc.Count;
                var outv = new float[count * comps];
                for (int k = 0; k < count; k++)
                    for (int c = 0; c < comps; c++)
                        outv[k * comps + c] = System.BitConverter.ToSingle(glb, (int)(start + k * stride) + c * sizeof(float));
                return outv;
            }
        }

        private static int ComponentCount(GLTFAccessorAttributeType t)
        {
            switch (t)
            {
                case GLTFAccessorAttributeType.SCALAR: return 1;
                case GLTFAccessorAttributeType.VEC2: return 2;
                case GLTFAccessorAttributeType.VEC3: return 3;
                case GLTFAccessorAttributeType.VEC4: return 4;
                default: throw new System.NotSupportedException(t.ToString());
            }
        }

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

        // ── R1: LINEAR multi-key morph survives export -> re-bake as frame-0-relative deltas ────────

        [Test]
        public void Morph_LinearMultiKey_ExportRebake_PreservesDeltasAndInterpolation()
        {
            // R1: a multi-key LINEAR morph driver exports as a per-blendshape KHR_animation_pointer whose output
            // is the reconstructed absolute weight (BaseValue + delta). Decoding that accessor and re-baking with
            // BuildMorphPointerDriver must recover the SAME frame-0-relative deltas and LINEAR interpolation.
            var root = new GameObject("char");
            _created.Add(root);
            var smr = MakeMorphSmr(root.transform, "face", 1);

            var originalDeltas = new[] { 0f, 0.3f, 0.8f };
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "blinkLinear",
                        Domains = ExpressionDomain.Morph,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Smr = smr, BlendShapeIndex = 0, BaseValue = 0.2f,
                                Sampler = new Sampler { Times = new[] { 0f, 0.5f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                DeltaValues = originalDeltas,
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var (glb, gltf) = ExportToGlb(root);

            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            var item = ext.Expressions.Find(e => e.Expression == "blinkLinear");
            Assert.IsNotNull(item);
            Assert.IsNotNull(item.Morphtarget);
            Assert.AreEqual(1, item.Morphtarget.Channels.Length);

            var anim = gltf.Animations[item.Animation];
            var ch = anim.Channels[item.Morphtarget.Channels[0]];
            var sampler = anim.Samplers[ch.Sampler.Id];
            Assert.AreEqual(InterpolationType.LINEAR, sampler.Interpolation, "LINEAR must survive export");

            var times = ReadAccessorFloats(glb, gltf, gltf.Accessors[sampler.Input.Id]);
            var values = ReadAccessorFloats(glb, gltf, gltf.Accessors[sampler.Output.Id]);
            Assert.AreEqual(3, values.Length, "three keyframes round-trip");
            // Sanity: the exported absolute weight is BaseValue + delta (raw [0..1], no 100x frame-weight scaling).
            Assert.AreEqual(0.2f, values[0], 1e-4f, "frame 0 absolute = BaseValue + delta0");
            Assert.AreEqual(1.0f, values[2], 1e-4f, "peak absolute = BaseValue + delta2");

            // Re-bake via the real importer builder: deltas come back frame-0-relative, LINEAR preserved.
            var rebaked = new List<MorphDriver>();
            KhrCharacterBaker.BuildMorphPointerDriver(smr, 0, times, values, sampler.Interpolation, rebaked);

            Assert.AreEqual(1, rebaked.Count);
            Assert.AreEqual(0, rebaked[0].BlendShapeIndex);
            Assert.AreEqual(Interp.Linear, rebaked[0].Sampler.Interp, "interpolation round-trips as LINEAR");
            Assert.IsFalse(rebaked[0].Sampler.SingleKey, "a multi-key driver round-trips as multi-key");
            Assert.That(rebaked[0].DeltaValues, Is.EqualTo(originalDeltas).Within(1e-4f),
                "frame-0-relative deltas are recovered exactly");
        }

        // ── R2: joint rotation + translation survive export -> re-bake within epsilon, handedness restored ──

        [Test]
        public void Joint_RotationAndTranslation_ExportRebake_PreservesDeltasAndHandedness()
        {
            // R2: a multi-key LINEAR joint expression (rotation + translation) exports as NATIVE TRS channels with
            // the Unity->glTF handedness applied (rotation SwitchHandedness, translation X-flip). Decoding those
            // accessors, undoing the handedness, and re-baking must recover the original frame-0-relative deltas
            // within epsilon -- proving export and import handedness are mutually inverse.
            var root = new GameObject("char");
            _created.Add(root);
            var joint = new GameObject("jaw").transform;
            joint.SetParent(root.transform, false);

            var baseQ = Quaternion.Euler(0f, 30f, 0f);
            var deltaQ = Quaternion.Euler(20f, 0f, 0f);     // original rotation delta at key 1
            var baseV = new Vector3(1f, 0f, 0f);
            var deltaV = new Vector3(0.1f, 0.2f, 0.3f);     // original translation delta at key 1

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "jaw",
                        Domains = ExpressionDomain.Joint,
                        JointDrivers = new[]
                        {
                            new JointDriver
                            {
                                Target = joint, Channel = TrsChannel.Rotation,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                DeltaQuat = new[] { Quaternion.identity, deltaQ }, BaseQuat = baseQ,
                            },
                            new JointDriver
                            {
                                Target = joint, Channel = TrsChannel.Translation,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                DeltaVec = new[] { Vector3.zero, deltaV }, BaseVec = baseV,
                            },
                        },
                    },
                },
            };
            root.AddComponent<ExpressionController>().Initialize(set);

            var (glb, gltf) = ExportToGlb(root);

            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext);
            var item = ext.Expressions.Find(e => e.Expression == "jaw");
            Assert.IsNotNull(item);
            Assert.IsNotNull(item.Joint);
            Assert.AreEqual(2, item.Joint.Channels.Length);

            var anim = gltf.Animations[item.Animation];
            AnimationChannel rotCh = null, transCh = null;
            foreach (var ci in item.Joint.Channels)
            {
                var c = anim.Channels[ci];
                if (c.Target.Path == "rotation") rotCh = c;
                else if (c.Target.Path == "translation") transCh = c;
            }
            Assert.IsNotNull(rotCh, "native rotation channel present");
            Assert.IsNotNull(transCh, "native translation channel present");

            // ── Rotation: decode VEC4, undo the export SwitchHandedness (its own inverse), re-bake.
            var rotSampler = anim.Samplers[rotCh.Sampler.Id];
            Assert.AreEqual(InterpolationType.LINEAR, rotSampler.Interpolation);
            var rotTimes = ReadAccessorFloats(glb, gltf, gltf.Accessors[rotSampler.Input.Id]);
            var rotOut = ReadAccessorFloats(glb, gltf, gltf.Accessors[rotSampler.Output.Id]);
            Assert.AreEqual(8, rotOut.Length, "two VEC4 rotation keys");
            var quats = new Quaternion[2];
            for (int k = 0; k < 2; k++)
                quats[k] = new Quaternion(rotOut[k * 4], rotOut[k * 4 + 1], rotOut[k * 4 + 2], rotOut[k * 4 + 3]).SwitchHandedness();

            var rebakedRot = new List<JointDriver>();
            KhrCharacterBaker.BuildJointRotationDriver(joint, rotTimes, quats, rotSampler.Interpolation, baseQ, rebakedRot);
            Assert.AreEqual(1, rebakedRot.Count);
            Assert.AreEqual(Interp.Linear, rebakedRot[0].Sampler.Interp);
            Assert.Less(Quaternion.Angle(rebakedRot[0].DeltaQuat[0], Quaternion.identity), 0.1f,
                "rotation delta at key 0 round-trips to identity");
            Assert.Less(Quaternion.Angle(rebakedRot[0].DeltaQuat[1], deltaQ), 0.1f,
                "rotation delta at key 1 round-trips within epsilon (handedness restored)");

            // ── Translation: decode VEC3, undo the export X-flip ((-1,1,1)), re-bake.
            var transSampler = anim.Samplers[transCh.Sampler.Id];
            var transTimes = ReadAccessorFloats(glb, gltf, gltf.Accessors[transSampler.Input.Id]);
            var transOut = ReadAccessorFloats(glb, gltf, gltf.Accessors[transSampler.Output.Id]);
            Assert.AreEqual(6, transOut.Length, "two VEC3 translation keys");
            var vecs = new Vector3[2];
            for (int k = 0; k < 2; k++)
                vecs[k] = new Vector3(-transOut[k * 3], transOut[k * 3 + 1], transOut[k * 3 + 2]); // inverse X-flip

            var rebakedTrans = new List<JointDriver>();
            KhrCharacterBaker.BuildJointVectorDriver(joint, TrsChannel.Translation, transTimes, vecs, transSampler.Interpolation, baseV, rebakedTrans);
            Assert.AreEqual(1, rebakedTrans.Count);
            Assert.AreEqual(Interp.Linear, rebakedTrans[0].Sampler.Interp);
            Assert.Less(Vector3.Distance(rebakedTrans[0].DeltaVec[0], Vector3.zero), 1e-4f,
                "translation delta at key 0 round-trips to zero");
            Assert.Less(Vector3.Distance(rebakedTrans[0].DeltaVec[1], deltaV), 1e-4f,
                "translation delta at key 1 round-trips within epsilon (handedness restored)");
        }

        // ── P5: idempotence (export -> re-bake -> export reaches a fixed point) ──────────────────────

        [Test]
        public void Idempotence_ExportRebakeCycles_ReachStructuralFixedPoint()
        {
            // P5: prove the export is structurally stable across cycles WITHOUT a full GLTFSceneImporter.LoadScene
            // (the headless-flaky path the deferral avoided). We reuse the R1/R2 machinery — export to GLB, decode
            // the sampler accessors out of the binary chunk, re-bake through the REAL importer builders — and chain
            // it: author -> export #1 -> re-bake -> export #2 -> re-bake -> export #3.
            //
            // Idempotence is asserted on the STABLE cycle (#2 == #3), not (#1 == #2), because the FIRST import
            // re-anchors the absolute baseline: the morph baker fixes BaseValue = 0 and stores frame-0-relative
            // deltas, and the joint baker stores deltas over each target's rest TRS (the documented FU2 caveat —
            // import keeps only deltas, discarding the animation's frame-0 absolute). Once in that canonical form,
            // the data is a fixed point: every further export -> re-bake -> export is identical within epsilon.
            // The expression TOPOLOGY (names, per-channel target paths, interpolation) is stable from export #1.
            var root = new GameObject("char");
            _created.Add(root);
            var smr = MakeMorphSmr(root.transform, "face", 1);
            var jaw = new GameObject("jaw").transform;
            jaw.SetParent(root.transform, false);

            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "blinkLinear",
                        Domains = ExpressionDomain.Morph,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Smr = smr, BlendShapeIndex = 0, BaseValue = 0.2f,
                                Sampler = new Sampler { Times = new[] { 0f, 0.5f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                DeltaValues = new[] { 0f, 0.3f, 0.8f },
                            },
                        },
                    },
                    new ExpressionTrack
                    {
                        Name = "jawOpen",
                        Domains = ExpressionDomain.Joint,
                        JointDrivers = new[]
                        {
                            new JointDriver
                            {
                                Target = jaw, Channel = TrsChannel.Rotation,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                DeltaQuat = new[] { Quaternion.identity, Quaternion.Euler(20f, 0f, 0f) },
                                BaseQuat = Quaternion.Euler(0f, 30f, 0f),
                            },
                            new JointDriver
                            {
                                Target = jaw, Channel = TrsChannel.Translation,
                                Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
                                DeltaVec = new[] { Vector3.zero, new Vector3(0.1f, 0.2f, 0.3f) },
                                BaseVec = new Vector3(1f, 0f, 0f),
                            },
                        },
                    },
                },
            };
            var controller = root.AddComponent<ExpressionController>();
            controller.Initialize(set);

            // Cycle 1: author -> export #1.
            var (glb1, gltf1) = ExportToGlb(root);
            // Cycle 2: decode #1 -> re-bake via the real builders -> export #2.
            controller.Initialize(RebakeFromExport(glb1, gltf1, smr, jaw));
            var (glb2, gltf2) = ExportToGlb(root);
            // Cycle 3: decode #2 -> re-bake -> export #3.
            controller.Initialize(RebakeFromExport(glb2, gltf2, smr, jaw));
            var (glb3, gltf3) = ExportToGlb(root);

            const float eps = 1e-4f;
            // Topology is stable from the very first export (only the re-anchored baseline shifts on #1 -> #2).
            AssertCharacterExportsEqual(glb1, gltf1, glb2, gltf2, compareValues: false, eps);
            // Full idempotence: from the canonical form on, the accessor data is bit-stable within epsilon.
            AssertCharacterExportsEqual(glb2, gltf2, glb3, gltf3, compareValues: true, eps);
        }

        // Reconstructs a CharacterExpressionSet from an exported GLB by decoding each expression's sampler
        // accessors and feeding them back through the REAL importer builders (KhrCharacterBaker.Build*), exactly
        // as KhrCharacterBaker does at import — but without a scene load. Joint/morph targets reuse the original
        // Unity objects, so node indices (and therefore the re-exported pointer paths) stay identical across
        // cycles; the joint builders take each target's current local TRS as the delta base, mirroring
        // KhrCharacterBaker.BakeJointChannels. Handedness is undone the same way R2 does (rotation is its own
        // inverse under SwitchHandedness; translation undoes the export X-flip), applied consistently every cycle.
        private CharacterExpressionSet RebakeFromExport(byte[] glb, GLTFRoot gltf, SkinnedMeshRenderer smr, Transform joint)
        {
            var ext = gltf.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(ext, "the export must carry KHR_character_expression to re-bake from");

            var tracks = new List<ExpressionTrack>();
            foreach (var item in ext.Expressions)
            {
                var track = new ExpressionTrack { Name = item.Expression, BlendMode = ExpressionBlendMode.Additive };
                var anim = gltf.Animations[item.Animation];

                if (item.Morphtarget?.Channels != null)
                {
                    var morphs = new List<MorphDriver>();
                    foreach (var ci in item.Morphtarget.Channels)
                    {
                        var ch = anim.Channels[ci];
                        var sampler = anim.Samplers[ch.Sampler.Id];
                        var times = ReadAccessorFloats(glb, gltf, gltf.Accessors[sampler.Input.Id]);
                        var values = ReadAccessorFloats(glb, gltf, gltf.Accessors[sampler.Output.Id]);
                        int blendShapeIndex = 0;
                        if (KhrCharacterBaker.TryParseNodeWeightsPointer(PointerPath(ch), out _, out int bsi))
                            blendShapeIndex = bsi;
                        KhrCharacterBaker.BuildMorphPointerDriver(smr, blendShapeIndex, times, values, sampler.Interpolation, morphs);
                    }
                    if (morphs.Count > 0) { track.MorphDrivers = morphs.ToArray(); track.Domains |= ExpressionDomain.Morph; }
                }

                if (item.Joint?.Channels != null)
                {
                    var joints = new List<JointDriver>();
                    foreach (var ci in item.Joint.Channels)
                    {
                        var ch = anim.Channels[ci];
                        var sampler = anim.Samplers[ch.Sampler.Id];
                        var times = ReadAccessorFloats(glb, gltf, gltf.Accessors[sampler.Input.Id]);
                        var outv = ReadAccessorFloats(glb, gltf, gltf.Accessors[sampler.Output.Id]);
                        if (ch.Target.Path == "rotation")
                        {
                            var quats = new Quaternion[outv.Length / 4];
                            for (int k = 0; k < quats.Length; k++)
                                quats[k] = new Quaternion(outv[k * 4], outv[k * 4 + 1], outv[k * 4 + 2], outv[k * 4 + 3]).SwitchHandedness();
                            KhrCharacterBaker.BuildJointRotationDriver(joint, times, quats, sampler.Interpolation, joint.localRotation, joints);
                        }
                        else if (ch.Target.Path == "translation")
                        {
                            var vecs = new Vector3[outv.Length / 3];
                            for (int k = 0; k < vecs.Length; k++)
                                vecs[k] = new Vector3(-outv[k * 3], outv[k * 3 + 1], outv[k * 3 + 2]); // inverse export X-flip
                            KhrCharacterBaker.BuildJointVectorDriver(joint, TrsChannel.Translation, times, vecs, sampler.Interpolation, joint.localPosition, joints);
                        }
                        else if (ch.Target.Path == "scale")
                        {
                            var vecs = new Vector3[outv.Length / 3];
                            for (int k = 0; k < vecs.Length; k++)
                                vecs[k] = new Vector3(outv[k * 3], outv[k * 3 + 1], outv[k * 3 + 2]); // scale carries no handedness
                            KhrCharacterBaker.BuildJointVectorDriver(joint, TrsChannel.Scale, times, vecs, sampler.Interpolation, joint.localScale, joints);
                        }
                    }
                    if (joints.Count > 0) { track.JointDrivers = joints.ToArray(); track.Domains |= ExpressionDomain.Joint; }
                }

                tracks.Add(track);
            }

            var rebaked = new CharacterExpressionSet { Expressions = tracks.ToArray() };
            rebaked.RebuildIndex();
            return rebaked;
        }

        // Asserts two character exports describe the same KHR_character_expression structure. With
        // compareValues == false it checks only the TOPOLOGY (expression names + per-channel keys + interpolation +
        // key counts); with compareValues == true it additionally asserts every sampler input/output accessor is
        // equal within epsilon (full idempotence).
        private void AssertCharacterExportsEqual(byte[] glbA, GLTFRoot gltfA, byte[] glbB, GLTFRoot gltfB,
            bool compareValues, float eps)
        {
            var extA = gltfA.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            var extB = gltfB.Extensions[KHR_character_expression.EXTENSION_NAME] as KHR_character_expression;
            Assert.IsNotNull(extA); Assert.IsNotNull(extB);
            Assert.AreEqual(extA.Expressions.Count, extB.Expressions.Count, "same expression count across cycles");

            foreach (var itemA in extA.Expressions)
            {
                var itemB = extB.Expressions.Find(e => e.Expression == itemA.Expression);
                Assert.IsNotNull(itemB, $"expression '{itemA.Expression}' is present in both exports");

                var chA = CollectChannelData(glbA, gltfA, itemA);
                var chB = CollectChannelData(glbB, gltfB, itemB);
                CollectionAssert.AreEquivalent(chA.Keys, chB.Keys, $"expression '{itemA.Expression}' has the same channels");

                foreach (var key in chA.Keys)
                {
                    var a = chA[key];
                    var b = chB[key];
                    Assert.AreEqual(a.interp, b.interp, $"{itemA.Expression}/{key}: interpolation is stable");
                    Assert.AreEqual(a.times.Length, b.times.Length, $"{itemA.Expression}/{key}: key count is stable");
                    if (!compareValues) continue;
                    Assert.That(b.times, Is.EqualTo(a.times).Within(eps), $"{itemA.Expression}/{key}: input times are stable");
                    Assert.AreEqual(a.values.Length, b.values.Length, $"{itemA.Expression}/{key}: output length is stable");
                    Assert.That(b.values, Is.EqualTo(a.values).Within(eps), $"{itemA.Expression}/{key}: output values are stable");
                }
            }
        }

        // Decodes every animation channel an expression references into a table keyed by the channel's glTF target
        // path ("rotation"/"translation"/"scale") or, for KHR_animation_pointer channels, the pointer path. Within
        // one expression these keys are unique, so they align the two exports' channels independent of order.
        private Dictionary<string, (InterpolationType interp, float[] times, float[] values)> CollectChannelData(
            byte[] glb, GLTFRoot gltf, KHR_character_expression.ExpressionItem item)
        {
            var map = new Dictionary<string, (InterpolationType interp, float[] times, float[] values)>();
            var anim = gltf.Animations[item.Animation];

            var channelIndices = new List<int>();
            if (item.Morphtarget?.Channels != null) channelIndices.AddRange(item.Morphtarget.Channels);
            if (item.Joint?.Channels != null) channelIndices.AddRange(item.Joint.Channels);
            if (item.Texture?.Channels != null) channelIndices.AddRange(item.Texture.Channels);

            foreach (var ci in channelIndices)
            {
                var ch = anim.Channels[ci];
                string key = ch.Target.Path == "pointer" ? (PointerPath(ch) ?? "pointer") : ch.Target.Path;
                var sampler = anim.Samplers[ch.Sampler.Id];
                var times = ReadAccessorFloats(glb, gltf, gltf.Accessors[sampler.Input.Id]);
                var values = ReadAccessorFloats(glb, gltf, gltf.Accessors[sampler.Output.Id]);
                map[key] = (sampler.Interpolation, times, values);
            }
            return map;
        }

        // Reads the KHR_animation_pointer JSON path off a channel target (null if it is not a pointer channel).
        private static string PointerPath(AnimationChannel channel)
            => channel.Target?.Extensions != null
               && channel.Target.Extensions.TryGetValue(KHR_animation_pointer.EXTENSION_NAME, out var ext)
               && ext is KHR_animation_pointer p ? p.path : null;
    }
}
