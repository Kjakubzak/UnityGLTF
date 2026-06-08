using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// End-to-end play-mode tests for the Override compositing policy (H1) and deterministic same-slot texture
    /// resolution (H3). Additive over-composition is characterized first, then Override is shown to win
    /// outright by Priority (ties broken by latest declaration), across the morph, joint, and texture domains.
    /// </summary>
    public class ExpressionControllerOverrideTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        // ── Harness ──────────────────────────────────────────────────────────

        private GameObject MakeRoot(out Transform joint)
        {
            var root = new GameObject("char");
            _created.Add(root);
            var bone = new GameObject("bone");
            bone.transform.SetParent(root.transform, false);
            joint = bone.transform;
            return root;
        }

        private SkinnedMeshRenderer MakeSmr(out GameObject go)
        {
            var mesh = new Mesh { name = "test" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("shape0", 1f, new[] { Vector3.one, Vector3.one, Vector3.one }, null, null);
            _created.Add(mesh);

            go = new GameObject("char");
            _created.Add(go);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            return smr;
        }

        // Absolute-pose rotation driver: multi-key delta [identity -> target] over an identity base, so at full
        // weight the sampled delta equals 'target' and localRotation resolves to target * base.
        private static JointDriver Rot(Transform t, int priority, Quaternion target) => new JointDriver
        {
            Target = t,
            Channel = TrsChannel.Rotation,
            Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
            DeltaQuat = new[] { Quaternion.identity, target },
            BaseQuat = Quaternion.identity,
            Priority = priority,
        };

        // Linear morph driver: at weight w the sampled delta is w, so the absolute result is BaseValue + w.
        private static MorphDriver Morph(SkinnedMeshRenderer smr, int priority) => new MorphDriver
        {
            Smr = smr,
            BlendShapeIndex = 0,
            BaseValue = 0f,
            Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
            DeltaValues = new[] { 0f, 1f },
            Priority = priority,
        };

        // Index-swap driver that selects 'tex' whenever it is active (both STEP keys map to the same texture, so
        // the test isolates the winner selection from the STEP-index phase).
        private static TextureDriver Index(Renderer r, int propId, Texture tex, int priority) => new TextureDriver
        {
            Renderer = r,
            SubmeshSlot = 0,
            Kind = TexKind.IndexSwap,
            PropertyId = propId,
            Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Step, SingleKey = false },
            SwapTextures = new[] { tex, tex },
            Priority = priority,
        };

        private static CharacterExpressionSet SetWith(params ExpressionTrack[] tracks)
        {
            var set = new CharacterExpressionSet { Expressions = tracks };
            set.RebuildIndex();
            return set;
        }

        // ── Joint rotation ───────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator Rotation_Additive_TwoAbsolutePoses_OverCompose()
        {
            var root = MakeRoot(out var joint);
            var set = SetWith(
                new ExpressionTrack { Name = "a", Domains = ExpressionDomain.Joint, BlendMode = ExpressionBlendMode.Additive, JointDrivers = new[] { Rot(joint, 0, Quaternion.Euler(0f, 90f, 0f)) } },
                new ExpressionTrack { Name = "b", Domains = ExpressionDomain.Joint, BlendMode = ExpressionBlendMode.Additive, JointDrivers = new[] { Rot(joint, 0, Quaternion.Euler(0f, 90f, 0f)) } });
            var ec = root.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("a", 1f);
            ec.SetWeight("b", 1f);
            yield return null;

            // Two additive 90 degree poses accumulate to ~180 degrees: the documented over-composition.
            Assert.Less(Quaternion.Angle(joint.localRotation, Quaternion.Euler(0f, 180f, 0f)), 1f);
            Assert.Greater(Quaternion.Angle(joint.localRotation, Quaternion.Euler(0f, 90f, 0f)), 45f);
        }

        [UnityTest]
        public IEnumerator Rotation_Override_HigherPriorityWins_RegardlessOfDeclarationOrder()
        {
            var root = MakeRoot(out var joint);
            // 'wide' is declared first but has the higher priority; it must win outright.
            var set = SetWith(
                new ExpressionTrack { Name = "wide", Domains = ExpressionDomain.Joint, BlendMode = ExpressionBlendMode.Override, JointDrivers = new[] { Rot(joint, 10, Quaternion.Euler(0f, 90f, 0f)) } },
                new ExpressionTrack { Name = "narrow", Domains = ExpressionDomain.Joint, BlendMode = ExpressionBlendMode.Override, JointDrivers = new[] { Rot(joint, 1, Quaternion.Euler(0f, 20f, 0f)) } });
            var ec = root.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("wide", 1f);
            ec.SetWeight("narrow", 1f);
            yield return null;

            // Winner-takes the absolute 90 degree pose; it is neither the additive 110 nor the loser's 20.
            Assert.Less(Quaternion.Angle(joint.localRotation, Quaternion.Euler(0f, 90f, 0f)), 1f);
            Assert.Greater(Quaternion.Angle(joint.localRotation, Quaternion.Euler(0f, 110f, 0f)), 10f);
        }

        [UnityTest]
        public IEnumerator Rotation_Override_EqualPriority_LatestDeclarationWins()
        {
            var root = MakeRoot(out var joint);
            var set = SetWith(
                new ExpressionTrack { Name = "first", Domains = ExpressionDomain.Joint, BlendMode = ExpressionBlendMode.Override, JointDrivers = new[] { Rot(joint, 5, Quaternion.Euler(0f, 90f, 0f)) } },
                new ExpressionTrack { Name = "second", Domains = ExpressionDomain.Joint, BlendMode = ExpressionBlendMode.Override, JointDrivers = new[] { Rot(joint, 5, Quaternion.Euler(0f, 20f, 0f)) } });
            var ec = root.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("first", 1f);
            ec.SetWeight("second", 1f);
            yield return null;

            // Equal priority resolves to the latest declaration (highest expression index) deterministically.
            Assert.Less(Quaternion.Angle(joint.localRotation, Quaternion.Euler(0f, 20f, 0f)), 1f);
        }

        // ── Morph ────────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator Morph_Override_WinnerReplacesAdditiveSum()
        {
            var smr = MakeSmr(out var go);
            var set = SetWith(
                new ExpressionTrack { Name = "low", Domains = ExpressionDomain.Morph, BlendMode = ExpressionBlendMode.Override, MorphDrivers = new[] { Morph(smr, 1) } },
                new ExpressionTrack { Name = "high", Domains = ExpressionDomain.Morph, BlendMode = ExpressionBlendMode.Override, MorphDrivers = new[] { Morph(smr, 5) } });
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("low", 0.3f);
            ec.SetWeight("high", 0.6f);
            yield return null;

            // The priority-5 winner replaces the result with its absolute value (0.6), not the additive sum (0.9).
            Assert.AreEqual(0.6f, smr.GetBlendShapeWeight(0), 1e-3f);
        }

        [UnityTest]
        public IEnumerator Morph_Override_BeatsAdditive_OnSharedTarget()
        {
            var smr = MakeSmr(out var go);
            var set = SetWith(
                new ExpressionTrack { Name = "add", Domains = ExpressionDomain.Morph, BlendMode = ExpressionBlendMode.Additive, MorphDrivers = new[] { Morph(smr, 0) } },
                new ExpressionTrack { Name = "ovr", Domains = ExpressionDomain.Morph, BlendMode = ExpressionBlendMode.Override, MorphDrivers = new[] { Morph(smr, 0) } });
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("add", 0.5f);
            ec.SetWeight("ovr", 0.4f);
            yield return null;

            // Any active Override contributor wins the target, discarding the additive 0.5 contribution.
            Assert.AreEqual(0.4f, smr.GetBlendShapeWeight(0), 1e-3f);
        }

        // ── Texture index swap (H3) ──────────────────────────────────────────

        [UnityTest]
        public IEnumerator IndexSwap_PriorityWins_RegardlessOfWeightOrder()
        {
            var shader = Shader.Find("Unlit/Texture");
            if (shader == null) { Assert.Ignore("No suitable built-in shader available in this project."); yield break; }

            var go = new GameObject("quad", typeof(MeshFilter), typeof(MeshRenderer));
            _created.Add(go);
            var mr = go.GetComponent<MeshRenderer>();
            var mat = new Material(shader);
            _created.Add(mat);
            mr.sharedMaterial = mat;

            var texA = new Texture2D(1, 1) { name = "A" };
            var texB = new Texture2D(1, 1) { name = "B" };
            _created.Add(texA);
            _created.Add(texB);

            int propId = Shader.PropertyToID("_MainTex");
            var set = SetWith(
                new ExpressionTrack { Name = "a", Domains = ExpressionDomain.Texture, TextureDrivers = new[] { Index(mr, propId, texA, 1) } },
                new ExpressionTrack { Name = "b", Domains = ExpressionDomain.Texture, TextureDrivers = new[] { Index(mr, propId, texB, 5) } });
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            // 'a' has the higher weight but the lower priority; 'b' (priority 5) must win.
            ec.SetWeight("a", 1f);
            ec.SetWeight("b", 0.6f);
            yield return null;
            var mpb = new MaterialPropertyBlock();
            mr.GetPropertyBlock(mpb, 0);
            Assert.AreSame(texB, mpb.GetTexture(propId));

            // Reverse the weights: priority still decides, so the winner is independent of weight order.
            ec.SetWeight("a", 0.6f);
            ec.SetWeight("b", 1f);
            yield return null;
            mr.GetPropertyBlock(mpb, 0);
            Assert.AreSame(texB, mpb.GetTexture(propId));
        }
    }
}
