using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Verifies the quality-of-life serialization layer: components persist their baked data into
    /// <c>[SerializeField]</c> backing fields and re-initialize themselves on <c>Awake</c> (the gaze solver on
    /// its first <c>LateUpdate</c>; the <see cref="KhrCharacter"/> hub on <c>Start</c>, after every
    /// sub-controller has rehydrated in its own Awake) so an editor-imported / deserialized prefab is live
    /// without a fresh import. <see cref="Object.Instantiate"/> in Play mode reproduces the
    /// serialize -> deserialize -> Awake/Start round-trip: managed <c>[SerializeField]</c> data is deep-copied,
    /// non-serialized runtime state is reset, and the clone's Awake/Start run.
    /// </summary>
    public class SerializationRoundTripTests
    {
        private const string Vocab = "https://example.com/skeleton/v1";
        private const string AlternateVocab = "https://example.com/skeleton/alternate/v1";

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private GameObject NewGo(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        private SkinnedMeshRenderer MakeSmr(GameObject go, int blendShapeCount)
        {
            var mesh = new Mesh { name = "t" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            var delta = new[] { Vector3.one, Vector3.one, Vector3.one };
            for (int i = 0; i < blendShapeCount; i++) mesh.AddBlendShapeFrame("s" + i, 1f, delta, null, null);
            _created.Add(mesh);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            return smr;
        }

        private static MorphDriver Morph(SkinnedMeshRenderer smr, int idx) => new MorphDriver
        {
            Smr = smr,
            BlendShapeIndex = idx,
            BaseValue = 0f,
            Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
            DeltaValues = new[] { 0f, 1f },
        };

        private static JProperty ExpressionProvenanceToken()
        {
            return new JProperty(GLTF.Schema.KHR_character_expression.EXTENSION_NAME,
                new JObject
                {
                    { "expressions", new JArray
                        {
                            new JObject
                            {
                                { "expression", "original" },
                                { "animation", 1 },
                                { "extras", "item-extra" },
                                { "futureItemField", "not-permitted-by-the-item-schema" },
                                { "extensions", new JObject
                                    {
                                        { "ACME_item", new JObject { { "value", 1 } } },
                                        { GLTF.Schema.KHR_character_expression_morphtarget.EXTENSION_NAME,
                                            new JObject
                                            {
                                                { "channels", new JArray(0) },
                                                { "extensions", new JObject { { "ACME_morph", new JObject { { "value", 2 } } } } },
                                                { "extras", false },
                                                { "futureMorphField", 12 },
                                            }
                                        },
                                        { GLTF.Schema.KHR_character_expression_joint.EXTENSION_NAME,
                                            new JObject
                                            {
                                                { "channels", new JArray(1) },
                                                { "extensions", new JObject { { "ACME_joint", new JObject { { "value", 3 } } } } },
                                                { "extras", new JArray("joint", 3) },
                                                { "futureJointField", 13 },
                                            }
                                        },
                                        { GLTF.Schema.KHR_character_expression_texture.EXTENSION_NAME,
                                            new JObject
                                            {
                                                { "channels", new JArray(2) },
                                                { "extensions", new JObject { { "ACME_texture", new JObject { { "value", 4 } } } } },
                                                { "extras", new JObject { { "mode", "texture" } } },
                                                { "futureTextureField", 14 },
                                            }
                                        },
                                        { GLTF.Schema.KHR_character_expression_mask.EXTENSION_NAME,
                                            new JObject
                                            {
                                                { "masks", new JArray
                                                    {
                                                        new JObject
                                                        {
                                                            { "target", 0 },
                                                            { "name", "original" },
                                                            { "type", "ACME_expression_mask_curve" },
                                                            { "amount", 0.25f },
                                                            { "threshold", 0.125f },
                                                            { "extensions", new JObject
                                                                {
                                                                    { "ACME_expression_mask_curve", new JObject { { "value", 6 } } },
                                                                }
                                                            },
                                                            { "extras", "mask-entry-extra" },
                                                            { "futureMaskEntryField", 16 },
                                                        }
                                                    }
                                                },
                                                { "extensions", new JObject { { "ACME_mask_root", new JObject { { "value", 7 } } } } },
                                                { "extras", new JArray("mask-root", 7) },
                                                { "futureMaskRootField", 17 },
                                            }
                                        },
                                    }
                                },
                            }
                        }
                    },
                    { "extensions", new JObject { { "ACME_root", new JObject { { "value", 5 } } } } },
                    { "extras", new JArray("root", 5) },
                    { "futureRootField", new JObject { { "keep", true } } },
                });
        }

        private static GLTF.Schema.KHR_character_expression ParseExpressionProvenanceToken()
        {
            return new GLTF.Schema.KHR_character_expression_Factory()
                .Deserialize(new GLTF.Schema.GLTFRoot(), ExpressionProvenanceToken())
                as GLTF.Schema.KHR_character_expression;
        }

        private static JProperty ExpressionMappingProvenanceToken()
        {
            return new JProperty(GLTF.Schema.KHR_character_expression_mapping.EXTENSION_NAME,
                new JObject
                {
                    { "expressionSetMappings", new JObject
                        {
                            { Vocab, new JObject
                                {
                                    { "Smile", new JArray
                                        {
                                            new JObject
                                            {
                                                { "source", 0 },
                                                { "name", "source-original" },
                                                { "weight", 0.8f },
                                                { "extensions", new JObject { { "ACME_forward", new JObject { { "value", 21 } } } } },
                                                { "extras", false },
                                                { "futureForwardField", 22 },
                                            }
                                        }
                                    },
                                }
                            },
                        }
                    },
                    { "expressionSetInputMappings", new JObject
                        {
                            { Vocab, new JObject
                                {
                                    { "Smile", new JArray
                                        {
                                            new JObject
                                            {
                                                { "target", 1 },
                                                { "name", "target-original" },
                                                { "weight", 0.5f },
                                                { "extensions", new JObject { { "ACME_input", new JObject { { "value", 23 } } } } },
                                                { "extras", new JArray("input", 23) },
                                                { "futureInputField", 24 },
                                            }
                                        }
                                    },
                                }
                            },
                        }
                    },
                    { "extensions", new JObject { { "ACME_mapping_root", new JObject { { "value", 25 } } } } },
                    { "extras", new JObject { { "mapping", true } } },
                    { "futureMappingRootField", 26 },
                });
        }

        private static GLTF.Schema.KHR_character_expression_mapping ParseExpressionMappingProvenanceToken()
        {
            return new GLTF.Schema.KHR_character_expression_mapping_Factory()
                .Deserialize(new GLTF.Schema.GLTFRoot(), ExpressionMappingProvenanceToken())
                as GLTF.Schema.KHR_character_expression_mapping;
        }

        // ── SerializableSkeletonMapping data round-trip ─────────────────────────────

        [Test]
        public void SerializableSkeletonMapping_RoundTrip_PreservesBonesAndMetadata()
        {
            var hips = NewGo("Hips").transform;
            var head = NewGo("Head").transform;
            var source = new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", hips }, { "head", head } },
                SelectedRig = "unityHumanoid",
            };

            var serializable = SerializableSkeletonMapping.FromResult(source);
            Assert.AreEqual(2, serializable.Bones.Length);
            Assert.AreEqual("unityHumanoid", serializable.SelectedRig);

            var restored = serializable.ToResult();
            Assert.AreEqual(2, restored.Bones.Count);
            Assert.AreSame(hips, restored.Bones["hips"]);
            Assert.AreSame(head, restored.Bones["head"]);
            Assert.AreEqual("unityHumanoid", restored.SelectedRig);
        }

        [Test]
        public void SerializableSkeletonMapping_FromNull_ReturnsNull()
        {
            Assert.IsNull(SerializableSkeletonMapping.FromResult(null));
        }

        [Test]
        public void SerializableSkeletonMapping_RoundTrip_PreservesEveryMappingSetAndReferencePose()
        {
            var hips = NewGo("Hips").transform;
            var head = NewGo("Head").transform;
            var source = new SkeletonMappingResult
            {
                MappingSets = new[]
                {
                    new SkeletonMappingSetResult
                    {
                        Identifier = Vocab,
                        Associations = new Dictionary<string, Transform> { { "hips", hips } },
                    },
                    new SkeletonMappingSetResult
                    {
                        Identifier = AlternateVocab,
                        Associations = new Dictionary<string, Transform> { { "head", head } },
                    },
                },
                ReferencePoses = new[]
                {
                    new ReferencePose { AnimationIndex = 2, PoseType = "TPose", Bones = new[] { hips } },
                    new ReferencePose { AnimationIndex = 5, PoseType = "TPose", Bones = new[] { head } },
                },
                Bones = new Dictionary<string, Transform> { { "hips", hips } },
                SelectedRig = Vocab,
            };

            var restored = SerializableSkeletonMapping.FromResult(source).ToResult();

            Assert.AreEqual(2, restored.MappingSets.Length);
            Assert.AreEqual(Vocab, restored.MappingSets[0].Identifier);
            Assert.AreSame(hips, restored.MappingSets[0].Associations["hips"]);
            Assert.AreEqual(AlternateVocab, restored.MappingSets[1].Identifier);
            Assert.AreSame(head, restored.MappingSets[1].Associations["head"]);
            Assert.AreEqual(2, restored.ReferencePoses.Length);
            Assert.AreEqual(2, restored.ReferencePoses[0].AnimationIndex);
            Assert.AreEqual(5, restored.ReferencePoses[1].AnimationIndex);
        }

        // ── KHR_character_skeleton_mapping JSON wire ───────────────────────────────

        [Test]
        public void SkeletonMappingSchema_SerializeDeserialize_PreservesAssociations()
        {
            var hips = new GLTF.Schema.KHR_character_skeleton_mapping.JointAssociation { Node = 1, Name = "Hips" };
            var head = new GLTF.Schema.KHR_character_skeleton_mapping.JointAssociation { Node = 5 };
            var ext = new GLTF.Schema.KHR_character_skeleton_mapping
            {
                SkeletalRigMappings = new Dictionary<string, Dictionary<string, GLTF.Schema.KHR_character_skeleton_mapping.JointAssociation>>
                {
                    { Vocab, new Dictionary<string, GLTF.Schema.KHR_character_skeleton_mapping.JointAssociation> { { "hips", hips }, { "head", head } } },
                },
            };

            // Serialize to the glTF JProperty, then read it back through the factory (the import path).
            var token = ext.Serialize();
            var restored = new GLTF.Schema.KHR_character_skeleton_mapping_Factory()
                .Deserialize(new GLTF.Schema.GLTFRoot(), token) as GLTF.Schema.KHR_character_skeleton_mapping;

            Assert.IsNotNull(restored);
            var rig = restored.SkeletalRigMappings[Vocab];
            Assert.AreEqual(1, rig["hips"].Node);
            Assert.AreEqual("Hips", rig["hips"].Name);
            Assert.AreEqual(5, rig["head"].Node);
            Assert.IsNull(rig["head"].Name);
        }

        [Test]
        public void SkeletonMappingSchema_Deserialize_DropsLegacyScalarValues()
        {
            // Hard cut: pre-change assets carried bare indices or node-name strings. Both scalar forms are
            // dropped rather than throwing and failing the whole document load.
            var token = new JProperty(GLTF.Schema.KHR_character_skeleton_mapping.EXTENSION_NAME,
                new JObject
                {
                    { "skeletalRigMappings", new JObject
                        {
                            { "unityHumanoid", new JObject { { "hips", 2 }, { "head", "Head" } } },
                        }
                    },
                });

            var ext = new GLTF.Schema.KHR_character_skeleton_mapping_Factory()
                .Deserialize(new GLTF.Schema.GLTFRoot(), token) as GLTF.Schema.KHR_character_skeleton_mapping;

            Assert.IsNotNull(ext);
            var rig = ext.SkeletalRigMappings["unityHumanoid"];
            Assert.IsFalse(rig.ContainsKey("hips"), "legacy integer values are dropped");
            Assert.IsFalse(rig.ContainsKey("head"), "legacy string values are dropped, not throwing");
        }

        [Test]
        public void ExpressionSchema_RoundTrip_PreservesGltfPropertyPayloadAndAppliesKnownEdits()
        {
            var ext = ParseExpressionProvenanceToken();
            var item = ext.Expressions[0];

            item.Expression = "edited";
            item.Animation = 7;
            item.Morphtarget.Channels = new[] { 3 };
            item.Joint.Channels = new[] { 4, 5 };
            item.Texture.Channels = new[] { 6 };
            item.Mask.Masks[0].Name = "edited-mask";
            item.Mask.Masks[0].Type = "block";
            item.Mask.Masks[0].Amount = 0.75f;
            item.Mask.Masks[0].Threshold = 0.5f;

            var value = (JObject)ext.Serialize().Value;
            var serializedItem = (JObject)((JArray)value["expressions"])[0];
            var itemExtensions = (JObject)serializedItem["extensions"];
            var morph = (JObject)itemExtensions[GLTF.Schema.KHR_character_expression_morphtarget.EXTENSION_NAME];
            var joint = (JObject)itemExtensions[GLTF.Schema.KHR_character_expression_joint.EXTENSION_NAME];
            var texture = (JObject)itemExtensions[GLTF.Schema.KHR_character_expression_texture.EXTENSION_NAME];
            var mask = (JObject)itemExtensions[GLTF.Schema.KHR_character_expression_mask.EXTENSION_NAME];
            var maskEntry = (JObject)((JArray)mask["masks"])[0];

            Assert.AreEqual("edited", serializedItem["expression"].Value<string>());
            Assert.AreEqual(7, serializedItem["animation"].Value<int>());
            Assert.AreEqual("item-extra", serializedItem["extras"].Value<string>());
            Assert.IsNull(serializedItem["futureItemField"],
                "expression items forbid additional properties, so invalid unknown fields are not retained");
            Assert.AreEqual(1, itemExtensions["ACME_item"]["value"].Value<int>());

            Assert.AreEqual(3, morph["channels"][0].Value<int>());
            Assert.IsFalse(morph["extras"].Value<bool>());
            Assert.AreEqual(2, morph["extensions"]["ACME_morph"]["value"].Value<int>());
            Assert.AreEqual(12, morph["futureMorphField"].Value<int>());

            Assert.AreEqual(4, joint["channels"][0].Value<int>());
            Assert.AreEqual(5, joint["channels"][1].Value<int>());
            Assert.AreEqual("joint", joint["extras"][0].Value<string>());
            Assert.AreEqual(3, joint["extensions"]["ACME_joint"]["value"].Value<int>());
            Assert.AreEqual(13, joint["futureJointField"].Value<int>());

            Assert.AreEqual(6, texture["channels"][0].Value<int>());
            Assert.AreEqual("texture", texture["extras"]["mode"].Value<string>());
            Assert.AreEqual(4, texture["extensions"]["ACME_texture"]["value"].Value<int>());
            Assert.AreEqual(14, texture["futureTextureField"].Value<int>());

            Assert.AreEqual("edited-mask", maskEntry["name"].Value<string>());
            Assert.AreEqual("block", maskEntry["type"].Value<string>());
            Assert.AreEqual(0.75f, maskEntry["amount"].Value<float>());
            Assert.AreEqual(0.5f, maskEntry["threshold"].Value<float>());
            Assert.AreEqual(6, maskEntry["extensions"]["ACME_expression_mask_curve"]["value"].Value<int>());
            Assert.AreEqual("mask-entry-extra", maskEntry["extras"].Value<string>());
            Assert.AreEqual(16, maskEntry["futureMaskEntryField"].Value<int>());
            Assert.AreEqual(7, mask["extensions"]["ACME_mask_root"]["value"].Value<int>());
            Assert.AreEqual("mask-root", mask["extras"][0].Value<string>());
            Assert.AreEqual(17, mask["futureMaskRootField"].Value<int>());

            Assert.AreEqual(5, value["extensions"]["ACME_root"]["value"].Value<int>());
            Assert.AreEqual("root", value["extras"][0].Value<string>());
            Assert.IsTrue(value["futureRootField"]["keep"].Value<bool>());
        }

        [Test]
        public void ExpressionSchema_Clone_DeepCopiesRootItemAndClassifierPayloads()
        {
            var source = ParseExpressionProvenanceToken();
            var clone = source.Clone(new GLTF.Schema.GLTFRoot()) as GLTF.Schema.KHR_character_expression;

            ((JObject)clone.Extensions["ACME_root"])["value"] = 50;
            ((JArray)clone.Extras)[0] = "clone-root";
            ((JObject)clone.AdditionalProperties["futureRootField"])["keep"] = false;
            ((JObject)clone.Expressions[0].RawExtensions["ACME_item"])["value"] = 10;
            clone.Expressions[0].Morphtarget.Channels[0] = 9;
            ((JObject)clone.Expressions[0].Joint.Extensions["ACME_joint"])["value"] = 30;
            ((JObject)clone.Expressions[0].Texture.Extras)["mode"] = "clone-texture";
            clone.Expressions[0].Texture.AdditionalProperties["futureTextureField"] = 140;
            ((JObject)clone.Expressions[0].Mask.Extensions["ACME_mask_root"])["value"] = 70;
            ((JArray)clone.Expressions[0].Mask.Extras)[0] = "clone-mask-root";
            clone.Expressions[0].Mask.AdditionalProperties["futureMaskRootField"] = 170;
            clone.Expressions[0].Mask.Masks[0].Type = "block";
            ((JObject)clone.Expressions[0].Mask.Masks[0].Extensions["ACME_expression_mask_curve"])["value"] = 60;
            clone.Expressions[0].Mask.Masks[0].AdditionalProperties["futureMaskEntryField"] = 160;

            Assert.AreEqual(5, source.Extensions["ACME_root"]["value"].Value<int>());
            Assert.AreEqual("root", source.Extras[0].Value<string>());
            Assert.IsTrue(source.AdditionalProperties["futureRootField"]["keep"].Value<bool>());
            Assert.AreEqual(1, source.Expressions[0].RawExtensions["ACME_item"]["value"].Value<int>());
            Assert.AreEqual(0, source.Expressions[0].Morphtarget.Channels[0]);
            Assert.AreEqual(3, source.Expressions[0].Joint.Extensions["ACME_joint"]["value"].Value<int>());
            Assert.AreEqual("texture", source.Expressions[0].Texture.Extras["mode"].Value<string>());
            Assert.AreEqual(14, source.Expressions[0].Texture.AdditionalProperties["futureTextureField"].Value<int>());
            Assert.AreEqual(7, source.Expressions[0].Mask.Extensions["ACME_mask_root"]["value"].Value<int>());
            Assert.AreEqual("mask-root", source.Expressions[0].Mask.Extras[0].Value<string>());
            Assert.AreEqual(17, source.Expressions[0].Mask.AdditionalProperties["futureMaskRootField"].Value<int>());
            Assert.AreEqual("ACME_expression_mask_curve", source.Expressions[0].Mask.Masks[0].Type);
            Assert.AreEqual(6,
                source.Expressions[0].Mask.Masks[0].Extensions["ACME_expression_mask_curve"]["value"].Value<int>());
            Assert.AreEqual(16,
                source.Expressions[0].Mask.Masks[0].AdditionalProperties["futureMaskEntryField"].Value<int>());
        }

        [Test]
        public void ExpressionMappingSchema_RoundTrip_PreservesProvenanceAndAppliesKnownEdits()
        {
            var ext = ParseExpressionMappingProvenanceToken();
            var forward = ext.ExpressionSetMappings[Vocab]["Smile"][0];
            forward.Source = 2;
            forward.Name = "source-edited";
            forward.Weight = 0.4f;
            ext.ExpressionSetMappings[Vocab]["Smile"][0] = forward;
            var input = ext.ExpressionSetInputMappings[Vocab]["Smile"][0];
            input.Target = 3;
            input.Name = "target-edited";
            input.Weight = 0.6f;
            ext.ExpressionSetInputMappings[Vocab]["Smile"][0] = input;

            var value = (JObject)ext.Serialize().Value;
            var serializedForward = (JObject)value["expressionSetMappings"][Vocab]["Smile"][0];
            var serializedInput = (JObject)value["expressionSetInputMappings"][Vocab]["Smile"][0];

            Assert.AreEqual(2, serializedForward["source"].Value<int>());
            Assert.AreEqual("source-edited", serializedForward["name"].Value<string>());
            Assert.AreEqual(0.4f, serializedForward["weight"].Value<float>());
            Assert.AreEqual(21, serializedForward["extensions"]["ACME_forward"]["value"].Value<int>());
            Assert.IsFalse(serializedForward["extras"].Value<bool>());
            Assert.AreEqual(22, serializedForward["futureForwardField"].Value<int>());

            Assert.AreEqual(3, serializedInput["target"].Value<int>());
            Assert.AreEqual("target-edited", serializedInput["name"].Value<string>());
            Assert.AreEqual(0.6f, serializedInput["weight"].Value<float>());
            Assert.AreEqual(23, serializedInput["extensions"]["ACME_input"]["value"].Value<int>());
            Assert.AreEqual("input", serializedInput["extras"][0].Value<string>());
            Assert.AreEqual(24, serializedInput["futureInputField"].Value<int>());

            Assert.AreEqual(25, value["extensions"]["ACME_mapping_root"]["value"].Value<int>());
            Assert.IsTrue(value["extras"]["mapping"].Value<bool>());
            Assert.AreEqual(26, value["futureMappingRootField"].Value<int>());
        }

        [Test]
        public void ExpressionMappingSchema_Clone_DeepCopiesRootAndContributionPayloads()
        {
            var source = ParseExpressionMappingProvenanceToken();
            var clone = source.Clone(new GLTF.Schema.GLTFRoot())
                as GLTF.Schema.KHR_character_expression_mapping;

            ((JObject)clone.Extensions["ACME_mapping_root"])["value"] = 250;
            clone.Extras["mapping"] = false;
            clone.AdditionalProperties["futureMappingRootField"] = 260;
            var forward = clone.ExpressionSetMappings[Vocab]["Smile"][0];
            ((JObject)forward.Extensions["ACME_forward"])["value"] = 210;
            forward.AdditionalProperties["futureForwardField"] = 220;
            clone.ExpressionSetMappings[Vocab]["Smile"][0] = forward;
            var input = clone.ExpressionSetInputMappings[Vocab]["Smile"][0];
            ((JArray)input.Extras)[0] = "clone-input";
            input.AdditionalProperties["futureInputField"] = 240;
            clone.ExpressionSetInputMappings[Vocab]["Smile"][0] = input;

            Assert.AreEqual(25, source.Extensions["ACME_mapping_root"]["value"].Value<int>());
            Assert.IsTrue(source.Extras["mapping"].Value<bool>());
            Assert.AreEqual(26, source.AdditionalProperties["futureMappingRootField"].Value<int>());
            Assert.AreEqual(21,
                source.ExpressionSetMappings[Vocab]["Smile"][0].Extensions["ACME_forward"]["value"].Value<int>());
            Assert.AreEqual(22,
                source.ExpressionSetMappings[Vocab]["Smile"][0].AdditionalProperties["futureForwardField"].Value<int>());
            Assert.AreEqual("input", source.ExpressionSetInputMappings[Vocab]["Smile"][0].Extras[0].Value<string>());
            Assert.AreEqual(24,
                source.ExpressionSetInputMappings[Vocab]["Smile"][0].AdditionalProperties["futureInputField"].Value<int>());
        }

        [Test]
        public void ExpressionMappingSchema_Deserialize_PreservesInvalidIdentifierAndSerializeRejectsIt()
        {
            var token = ExpressionMappingProvenanceToken();
            ((JObject)token.Value["expressionSetMappings"])["vrm"] =
                new JObject { { "Smile", new JArray(new JObject { { "source", 0 }, { "weight", 1f } }) } };

            var ext = new GLTF.Schema.KHR_character_expression_mapping_Factory()
                .Deserialize(new GLTF.Schema.GLTFRoot(), token)
                as GLTF.Schema.KHR_character_expression_mapping;

            Assert.IsTrue(ext.ExpressionSetMappings.ContainsKey(Vocab));
            Assert.IsTrue(ext.ExpressionSetMappings.ContainsKey("vrm"),
                "parse retains invalid source data for diagnostics instead of silently losing it");
            Assert.Throws<System.InvalidOperationException>(() => ext.Serialize(),
                "serialization must fail loudly instead of emitting or silently dropping an invalid URI key");
        }

        [Test]
        public void ExpressionMaskSchema_RoundTrip_PreservesCustomCompanionPayload()
        {
            var ext = new GLTF.Schema.KHR_character_expression_mask
            {
                Extensions = new JObject { { "ACME_mask_root", new JObject { { "version", 1 } } } },
                Extras = "root-extra",
                AdditionalProperties = new JObject { { "futureMaskRootField", 2 } },
                Masks = new List<GLTF.Schema.KHR_character_expression_mask.Mask>
                {
                    new GLTF.Schema.KHR_character_expression_mask.Mask
                    {
                        Target = 0,
                        Name = "target",
                        Type = "ACME_curve",
                        Extensions = new JObject
                        {
                            { "ACME_curve", new JObject { { "controlPoints", new JArray(0f, 1f) } } },
                        },
                        Extras = new JArray("author", "test"),
                        AdditionalProperties = new JObject { { "futureMaskEntryField", 3 } },
                    },
                },
            };

            var restored = GLTF.Schema.KHR_character_expression_mask.FromJson(ext.Serialize().Value as JObject);

            Assert.AreEqual("ACME_curve", restored.Masks[0].Type);
            Assert.AreEqual("target", restored.Masks[0].Name);
            Assert.AreEqual(2, ((JArray)restored.Masks[0].Extensions["ACME_curve"]["controlPoints"]).Count);
            Assert.AreEqual("author", restored.Masks[0].Extras[0].Value<string>());
            Assert.AreEqual(3, restored.Masks[0].AdditionalProperties["futureMaskEntryField"].Value<int>());
            Assert.AreEqual(1, restored.Extensions["ACME_mask_root"]["version"].Value<int>());
            Assert.AreEqual("root-extra", restored.Extras.Value<string>());
            Assert.AreEqual(2, restored.AdditionalProperties["futureMaskRootField"].Value<int>());
        }

        // ── ExpressionController rehydration on Awake ───────────────────────────────

        [UnityTest]
        public IEnumerator ExpressionController_Instantiate_RehydratesAndDrives()
        {
            var go = NewGo("char");
            var smr = MakeSmr(go, 1);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "smile", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { Morph(smr, 0) } },
                }
            };
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);
            Assert.AreEqual(1, ec.Count);

            // Clone -> the clone's Awake rebuilds from the serialized set (its own _set starts null).
            var clone = Object.Instantiate(go);
            _created.Add(clone);

            var cloneEc = clone.GetComponent<ExpressionController>();
            var cloneSmr = clone.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.AreEqual(1, cloneEc.Count, "expression count restored on Awake");

            cloneEc.SetWeight("smile", 1f);
            yield return null; // LateUpdate evaluates the rehydrated targets
            Assert.AreEqual(1f, cloneSmr.GetBlendShapeWeight(0), 1e-2f);
        }

        // ── KhrCharacter rehydration + OnCharacterReady ─────────────────────────────

        [UnityTest]
        public IEnumerator KhrCharacter_Start_RestoresCapabilitiesAndFiresReadyOnce()
        {
            var go = NewGo("char");
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(new CharacterExpressionSet { Expressions = new[] { new ExpressionTrack { Name = "a" } } });

            var hub = go.AddComponent<KhrCharacter>(); // no rehydrate yet (no serialized capabilities)
            hub.SetCapabilities(new[] { CharacterCapability.Character, CharacterCapability.Expression });
            Assert.IsFalse(hub.IsReady, "a fresh hub stays inert until wired/rehydrated");

            // Clone while inactive so the clone's Start (which fires OnCharacterReady) is deferred until we have
            // subscribed. The hub rehydrates in Start — not Awake — so every sub-controller has already
            // rehydrated in its own Awake before readiness is announced.
            go.SetActive(false);
            var clone = Object.Instantiate(go);
            _created.Add(clone);

            var cloneHub = clone.GetComponent<KhrCharacter>();
            int readyCount = 0;
            cloneHub.OnCharacterReady += _ => readyCount++;

            clone.SetActive(true); // triggers the clone's Awake (sub-controllers) then Start (hub) -> MarkReady
            yield return null;

            Assert.IsTrue(cloneHub.IsReady, "Start marks the rehydrated hub ready");
            Assert.AreEqual(1, readyCount, "OnCharacterReady fires exactly once");
            CollectionAssert.Contains(cloneHub.Capabilities, CharacterCapability.Expression);
            Assert.IsNotNull(cloneHub.Expressions, "sub-controllers are re-resolved on Start");
        }

        [Test]
        public void MarkReady_IsIdempotent_FiresOnce()
        {
            var hub = NewGo("char").AddComponent<KhrCharacter>();
            int count = 0;
            hub.OnCharacterReady += _ => count++;

            hub.MarkReady();
            hub.MarkReady();

            Assert.IsTrue(hub.IsReady);
            Assert.AreEqual(1, count);
        }

        [Test]
        public void WhenReady_AlreadyReady_InvokesImmediately()
        {
            var hub = NewGo("char").AddComponent<KhrCharacter>();
            hub.MarkReady();

            int count = 0;
            hub.WhenReady(_ => count++);

            Assert.AreEqual(1, count, "WhenReady should invoke immediately when the character is already ready");
        }

        [Test]
        public void WhenReady_BeforeReady_InvokesOnceWhenMarkedReady()
        {
            var hub = NewGo("char").AddComponent<KhrCharacter>();

            int count = 0;
            hub.WhenReady(_ => count++);
            Assert.AreEqual(0, count, "callback must not fire before the character is ready");

            hub.MarkReady();
            Assert.AreEqual(1, count, "callback fires once when readiness is reached");

            hub.MarkReady(); // idempotent
            Assert.AreEqual(1, count, "callback must not fire again");
        }

        // ── GazeSolver lazy-bind on first LateUpdate ────────────────────────────────

        [UnityTest]
        public IEnumerator GazeSolver_LazyBind_ResolvesSiblingsAndDrivesLookWeights()
        {
            var go = NewGo("char");
            var smr = MakeSmr(go, 2);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "lookRight", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { Morph(smr, 0) } },
                    new ExpressionTrack { Name = "lookLeft", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { Morph(smr, 1) } },
                }
            };
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            // Note: Bind is deliberately NOT called — the solver must resolve the sibling ExpressionController
            // itself on the first LateUpdate (the deserialized-prefab path).
            var gaze = go.AddComponent<GazeSolver>();
            gaze.Mode = GazeSolver.LookAtMode.CustomTarget;

            var target = NewGo("target");
            target.transform.position = go.transform.position + go.transform.right * 2f; // 90deg right -> saturates
            gaze.Target = target.transform;

            yield return null; // gaze (order 50) lazy-binds + drives, then EC (order 100) writes the blendshape
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-2f);
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(1), 1e-2f);
        }

        // ── SkeletonMap auto-build humanoid on Awake ────────────────────────────────

        [UnityTest]
        public IEnumerator SkeletonMap_BuildAndAssignAvatar_ProducesAndAssignsHumanoidAvatar()
        {
            // A real glTF character imports with an Animator, and the avatar is assigned from normal code on an
            // initialized Animator (the runtime auto-build path and the play-mode "Build" inspector button both
            // funnel through BuildAndAssignAvatar). Mirror that here: existing Animator + a direct call after the
            // object has initialized. Scope: this test covers ONLY the humanoid build + assignment mechanics
            // (incl. the vocab->bone-name mapping); it does not exercise serialization. The mapping-data round
            // trip is covered by SerializableSkeletonMapping_RoundTrip_PreservesBonesAndMetadata, and the
            // _buildHumanoidOnAwake flag's serialize -> rehydrate path by
            // SkeletonMap_RehydratedNonHumanoid_RemovesPreAddedAnimator (which builds via Instantiate).
            var root = NewGo("char");
            var bones = BuildHumanoidRig(root.transform);
            var animator = root.AddComponent<Animator>();
            var skel = root.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult { Bones = bones, SelectedRig = "unityHumanoid" });

            yield return null; // let the GameObject and Animator finish initializing before assigning the avatar

            var avatar = skel.BuildAndAssignAvatar();
            if (avatar != null) _created.Add(avatar);

            Assert.IsNotNull(avatar, "a humanoid avatar was built from the 15-bone rig");
            Assert.IsTrue(avatar.isHuman, "the built avatar is humanoid");
            Assert.AreEqual(avatar, animator.avatar, "the avatar was assigned to the Animator");
        }

        [UnityTest]
        public IEnumerator SkeletonMap_LiveBindOnInactiveRoot_StaysBuildFree()
        {
            // H2: the importer wires a live import by calling Bind + setting the build flag. When the scene root
            // is inactive at import (e.g. HideSceneObjDuringLoad / showSceneObj:false), Awake runs only on
            // activation — after Bind. The deserialize-signal gate (_result already set) must keep this path
            // build-free: no auto-built Animator, even though the rig below is a full humanoid that *could* build.
            var root = NewGo("char");
            root.SetActive(false);
            var bones = BuildHumanoidRig(root.transform);
            var skel = root.AddComponent<SkeletonMap>(); // inactive -> Awake deferred
            skel.Bind(new SkeletonMappingResult { Bones = bones, SelectedRig = "unityHumanoid" });
            skel.BuildHumanoidOnAwake = true;            // importer sets the flag (live path)

            root.SetActive(true); // Awake runs with _result already bound -> deserialized == false -> build-free
            yield return null;    // Start must not build

            Assert.IsNull(root.GetComponent<Animator>(),
                "a live import must not auto-build an Animator, even when the root was inactive at import");
        }

        [UnityTest]
        public IEnumerator SkeletonMap_RehydratedNonHumanoid_RemovesPreAddedAnimator()
        {
            // M1: a baked prefab flagged to build but whose rig can't form a humanoid (only hips+head). The clone
            // takes the rehydrate path: Awake pre-adds an Animator, Start's build fails (missing required bones)
            // and must remove it again so the character isn't left with an empty, unintended Animator.
            var src = NewGo("char");
            src.SetActive(false);
            var hips = new GameObject("Hips").transform; hips.SetParent(src.transform, false);
            var head = new GameObject("Head").transform; head.SetParent(src.transform, false);
            var skel = src.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", hips }, { "head", head } },
                SelectedRig = "rig",
            });
            skel.BuildHumanoidOnAwake = true;

            // Instantiate reproduces deserialize: clone._result starts null while the serialized mapping + flag are
            // copied, so the clone rehydrates, pre-adds an Animator, then fails the build and removes it.
            var clone = Object.Instantiate(src);
            _created.Add(clone);
            clone.SetActive(true);
            yield return null; // Start runs (build fails -> Destroy queued)
            yield return null; // deferred Destroy of the orphan Animator is processed

            Assert.IsNull(clone.GetComponent<Animator>(),
                "the Animator pre-added for a humanoid build must be removed when the build fails");
        }

        [UnityTest]
        public IEnumerator SkeletonMap_RehydratedWithAvatarAssigned_SkipsRuntimeBuild()
        {
            // Editor-imported prefabs get the humanoid Avatar assigned to the Animator at import time (persisted
            // as a sub-asset via ctx.AddObjectToAsset in KhrCharacterImportContext.OnAfterImport). When the
            // prefab rehydrates in Play, SkeletonMap must treat the already-assigned Avatar as authoritative
            // and skip the runtime build: no _lastBuiltAvatar, no redundant runtime Avatar object, no duplicate
            // work each Play. The same rule also respects a user's manual Avatar assignment.
            var src = NewGo("char");
            var bones = BuildHumanoidRig(src.transform);
            var animator = src.AddComponent<Animator>();
            var skel = src.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult { Bones = bones, SelectedRig = "unityHumanoid" });

            yield return null; // let the source's Animator initialize before we build an Avatar for it

            // Pre-build the Avatar and pre-assign it: this reproduces the state a rehydrated import-produced
            // prefab reaches (Animator.avatar already populated by the OnAfterImport sub-asset path).
            var preAvatar = skel.BuildHumanoidAvatar();
            Assert.IsNotNull(preAvatar, "sanity: the 15-bone rig can build a humanoid avatar");
            _created.Add(preAvatar);
            animator.avatar = preAvatar;

            // Flag the build so the clone's Awake would normally queue it, then deactivate + Instantiate so the
            // rehydrate path runs on the clone. Awake's defense-in-depth (Animator has an Avatar) must clear
            // the queued flag and Start must not build a fresh Avatar.
            skel.BuildHumanoidOnAwake = true;
            src.SetActive(false);

            var clone = Object.Instantiate(src);
            _created.Add(clone);
            clone.SetActive(true);
            yield return null; // Awake + Start run

            var cloneSkel = clone.GetComponent<SkeletonMap>();
            var cloneAnimator = clone.GetComponent<Animator>();
            Assert.IsNotNull(cloneAnimator, "the clone kept its Animator");
            Assert.AreSame(preAvatar, cloneAnimator.avatar,
                "the pre-assigned Avatar remains authoritative — the runtime path must not replace it");
            Assert.IsNull(cloneSkel.LastBuiltAvatar,
                "no runtime Avatar was built when the Animator already had one");
        }

        // ── Degraded report + serialized-state determinism across rehydrate ──────────

        private static CapabilityStatus StatusOfCap(CharacterHealthReport report, CharacterCapability cap)
        {
            foreach (var c in report.Capabilities)
                if (c.Capability == cap) return c.Status;
            return CapabilityStatus.Inert;
        }

        [UnityTest]
        public IEnumerator SkeletonMapping_DegradedReport_SurvivesRehydrateAndReadsDegraded()
        {
            // A baked partial mapping (a required bone unbound) must keep its ValidationReport across the
            // serialize -> deserialize round-trip so the rehydrated character still reads Degraded.
            var src = NewGo("char");
            src.SetActive(false);
            var hips = new GameObject("Hips").transform;
            hips.SetParent(src.transform, false); // intra-hierarchy so the bone ref survives Instantiate
            var skel = src.AddComponent<SkeletonMap>();
            var result = new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", hips } },
                SelectedRig = "rig",
            };
            result.Report.IsValid = false;
            result.Report.MissingRequiredBones.Add("leftFoot");
            skel.Bind(result);

            var hub = src.AddComponent<KhrCharacter>();
            hub.Skeleton = skel;
            hub.SetCapabilities(new[] { CharacterCapability.SkeletonMapping });

            var clone = Object.Instantiate(src);
            _created.Add(clone);
            clone.SetActive(true);
            yield return null; // SkeletonMap.Awake rehydrates the report; hub.Start re-resolves Skeleton + caps

            var cloneHub = clone.GetComponent<KhrCharacter>();
            Assert.IsTrue(cloneHub.IsReady, "clone hub rehydrated");
            Assert.AreEqual(CapabilityStatus.Degraded,
                StatusOfCap(cloneHub.GetHealth(), CharacterCapability.SkeletonMapping),
                "the baked partial-mapping report must survive serialization and read Degraded after rehydrate");
        }

        [Test]
        public void SerializableSkeletonMapping_RoundTrip_PreservesReportAndReferencePose()
        {
            var bone = NewGo("b").transform;
            var source = new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", bone } },
                SelectedRig = "rig",
                ReferencePose = new ReferencePose
                {
                    PoseType = "APose",
                    Bones = new[] { bone },
                    LocalPositions = new[] { Vector3.one },
                    LocalRotations = new[] { Quaternion.identity },
                    LocalScales = new[] { Vector3.one },
                },
            };
            source.Report.IsValid = false;
            source.Report.Warnings.Add("w");
            source.Report.MissingRequiredBones.Add("leftFoot");

            var restored = SerializableSkeletonMapping.FromResult(source).ToResult();

            Assert.IsFalse(restored.Report.IsValid);
            CollectionAssert.Contains(restored.Report.MissingRequiredBones, "leftFoot");
            CollectionAssert.Contains(restored.Report.Warnings, "w");
            Assert.IsNotNull(restored.ReferencePose);
            Assert.AreEqual("APose", restored.ReferencePose.PoseType);
            Assert.AreSame(bone, restored.ReferencePose.Bones[0]);
        }

        [Test]
        public void SerializableSkeletonMapping_RoundTrip_DropsEmptyJointNames()
        {
            var bone = NewGo("b").transform;
            var source = new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", bone }, { "", bone } }, // empty key
                SelectedRig = "rig",
            };

            var serializable = SerializableSkeletonMapping.FromResult(source);
            Assert.AreEqual(1, serializable.Bones.Length, "FromResult drops empty joint names (symmetric with ToResult)");

            var restored = serializable.ToResult();
            Assert.AreEqual(1, restored.Bones.Count);
            Assert.IsTrue(restored.Bones.ContainsKey("hips"));
        }

        [UnityTest]
        public IEnumerator SkeletonMap_Rehydrate_DoesNotReshuffleSerializedBoneOrder()
        {
            // The rehydrate path uses BindRuntimeState (no FromResult(ToResult(...)) rebuild), so the persisted
            // bone-array order must be identical on the clone. A regression to Bind() on rehydrate would walk a
            // Dictionary and could reorder it.
            var src = NewGo("char");
            src.SetActive(false);
            var a = new GameObject("A").transform; a.SetParent(src.transform, false);
            var b = new GameObject("B").transform; b.SetParent(src.transform, false);
            var c = new GameObject("C").transform; c.SetParent(src.transform, false);
            var skel = src.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", a }, { "spine", b }, { "head", c } },
                SelectedRig = "rig",
            });
            var srcOrder = skel.SerializedJointOrderForTests();

            var clone = Object.Instantiate(src);
            _created.Add(clone);
            clone.SetActive(true);
            yield return null; // clone Awake rehydrates via BindRuntimeState

            var cloneOrder = clone.GetComponent<SkeletonMap>().SerializedJointOrderForTests();
            CollectionAssert.AreEqual(srcOrder, cloneOrder,
                "rehydrate must preserve the persisted bone order (no FromResult rebuild)");
        }

        // Builds a minimal valid T-pose covering the 15 required humanoid bones (+ chest/neck) and returns the
        // vocab-joint -> Transform map the baker would produce. Character faces +Z; the left side is +X.
        private Dictionary<string, Transform> BuildHumanoidRig(Transform parent)
        {
            var map = new Dictionary<string, Transform>();

            Transform Bone(string name, string vocab, Transform p, Vector3 localPos)
            {
                var t = new GameObject(name).transform;
                t.SetParent(p, false);
                t.localPosition = localPos;
                map[vocab] = t;
                return t;
            }

            var hips = Bone("Hips", "hips", parent, new Vector3(0f, 1f, 0f));
            var spine = Bone("Spine", "spine", hips, new Vector3(0f, 0.2f, 0f));
            var chest = Bone("Chest", "chest", spine, new Vector3(0f, 0.2f, 0f));
            var neck = Bone("Neck", "neck", chest, new Vector3(0f, 0.2f, 0f));
            Bone("Head", "head", neck, new Vector3(0f, 0.1f, 0f));

            var lUpperArm = Bone("LeftUpperArm", "leftUpperArm", chest, new Vector3(0.15f, 0.15f, 0f));
            var lLowerArm = Bone("LeftLowerArm", "leftLowerArm", lUpperArm, new Vector3(0.25f, 0f, 0f));
            Bone("LeftHand", "leftHand", lLowerArm, new Vector3(0.25f, 0f, 0f));

            var rUpperArm = Bone("RightUpperArm", "rightUpperArm", chest, new Vector3(-0.15f, 0.15f, 0f));
            var rLowerArm = Bone("RightLowerArm", "rightLowerArm", rUpperArm, new Vector3(-0.25f, 0f, 0f));
            Bone("RightHand", "rightHand", rLowerArm, new Vector3(-0.25f, 0f, 0f));

            var lUpperLeg = Bone("LeftUpperLeg", "leftUpperLeg", hips, new Vector3(0.1f, -0.05f, 0f));
            var lLowerLeg = Bone("LeftLowerLeg", "leftLowerLeg", lUpperLeg, new Vector3(0f, -0.45f, 0f));
            Bone("LeftFoot", "leftFoot", lLowerLeg, new Vector3(0f, -0.45f, 0.1f));

            var rUpperLeg = Bone("RightUpperLeg", "rightUpperLeg", hips, new Vector3(-0.1f, -0.05f, 0f));
            var rLowerLeg = Bone("RightLowerLeg", "rightLowerLeg", rUpperLeg, new Vector3(0f, -0.45f, 0f));
            Bone("RightFoot", "rightFoot", rLowerLeg, new Vector3(0f, -0.45f, 0.1f));

            return map;
        }
    }
}
