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
                    { "unityHumanoid", new Dictionary<string, GLTF.Schema.KHR_character_skeleton_mapping.JointAssociation> { { "hips", hips }, { "head", head } } },
                },
            };

            // Serialize to the glTF JProperty, then read it back through the factory (the import path).
            var token = ext.Serialize();
            var restored = new GLTF.Schema.KHR_character_skeleton_mapping_Factory()
                .Deserialize(new GLTF.Schema.GLTFRoot(), token) as GLTF.Schema.KHR_character_skeleton_mapping;

            Assert.IsNotNull(restored);
            var rig = restored.SkeletalRigMappings["unityHumanoid"];
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
        public void ExpressionMappingSchema_RoundTrip_PreservesIndependentDirections()
        {
            var ext = new GLTF.Schema.KHR_character_expression_mapping();
            ext.ExpressionSetMappings["https://example.com/vocab/v1"] =
                new Dictionary<string, List<GLTF.Schema.KHR_character_expression_mapping.SourceWeight>>
                {
                    { "Smile", new List<GLTF.Schema.KHR_character_expression_mapping.SourceWeight>
                        { new GLTF.Schema.KHR_character_expression_mapping.SourceWeight { Source = 0, Weight = 0.8f } } },
                };
            ext.ExpressionSetInputMappings["https://example.com/vocab/v1"] =
                new Dictionary<string, List<GLTF.Schema.KHR_character_expression_mapping.TargetWeight>>
                {
                    { "Smile", new List<GLTF.Schema.KHR_character_expression_mapping.TargetWeight>
                        { new GLTF.Schema.KHR_character_expression_mapping.TargetWeight { Target = 1, Weight = 0.5f } } },
                };

            var restored = new GLTF.Schema.KHR_character_expression_mapping_Factory()
                .Deserialize(new GLTF.Schema.GLTFRoot(), ext.Serialize()) as GLTF.Schema.KHR_character_expression_mapping;

            Assert.AreEqual(0, restored.ExpressionSetMappings["https://example.com/vocab/v1"]["Smile"][0].Source);
            Assert.AreEqual(1, restored.ExpressionSetInputMappings["https://example.com/vocab/v1"]["Smile"][0].Target);
        }

        [Test]
        public void ExpressionMaskSchema_RoundTrip_PreservesCustomCompanionPayload()
        {
            var ext = new GLTF.Schema.KHR_character_expression_mask
            {
                Masks = new List<GLTF.Schema.KHR_character_expression_mask.Mask>
                {
                    new GLTF.Schema.KHR_character_expression_mask.Mask
                    {
                        Target = 0,
                        Type = "ACME_curve",
                        Extensions = new JObject
                        {
                            { "ACME_curve", new JObject { { "controlPoints", new JArray(0f, 1f) } } },
                        },
                        Extras = new JObject { { "author", "test" } },
                    },
                },
            };

            var restored = GLTF.Schema.KHR_character_expression_mask.FromJson(ext.Serialize().Value as JObject);

            Assert.AreEqual("ACME_curve", restored.Masks[0].Type);
            Assert.AreEqual(2, ((JArray)restored.Masks[0].Extensions["ACME_curve"]["controlPoints"]).Count);
            Assert.AreEqual("test", restored.Masks[0].Extras["author"].Value<string>());
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
