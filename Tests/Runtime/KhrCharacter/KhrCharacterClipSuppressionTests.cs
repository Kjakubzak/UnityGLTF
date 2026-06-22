using System.Collections;
using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// P1 (P-I1, Option B): import-only suppression of auto-playing expression + reference-pose clips. The
    /// importer turns every glTF animation — including each expression's animation and the reference-pose
    /// animation — into an AnimationClip and wires an auto-play host (the Legacy <see cref="Animation"/> default
    /// clip, or, in the editor, a Mecanim controller default state). The ExpressionController drives expressions
    /// itself (it decodes the glTF accessors; it never plays these Unity clips), so auto-play would double-drive
    /// the mesh or loop the reference pose. These tests verify, on real Unity Animation/AnimationClip objects,
    /// that after suppression: (a) the reference-pose clip is never the auto-played default, (b) expression clips
    /// neither auto-drive as the default state nor loop, and (c) every clip stays alive and reachable for explicit
    /// playback. They exercise the internal statics directly — the same white-box style the rest of the import
    /// suite uses (<c>DeriveCapabilities</c>, <c>ShouldBuildHumanoid</c>) — plus a Play-mode proof of no auto-play.
    /// </summary>
    public class KhrCharacterClipSuppressionTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private GameObject NewGo(string name, bool active)
        {
            var go = new GameObject(name);
            go.SetActive(active);
            _created.Add(go);
            return go;
        }

        // A real, explicitly-playable clip with a non-trivial length, imported as WrapMode.Loop (what the importer
        // sets) so the suppression's loop-removal is observable.
        private AnimationClip MakeClip(string name, bool legacy = true)
        {
            var clip = new AnimationClip { name = name, legacy = legacy, wrapMode = UnityEngine.WrapMode.Loop };
            clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 100f, 1f));
            _created.Add(clip);
            return clip;
        }

        // Mirror GLTFSceneImporter's Legacy host wiring: every clip registered; clip 0 is the auto-played default.
        private static Animation MakeLegacyHost(GameObject go, AnimationClip[] clips)
        {
            var animation = go.AddComponent<Animation>();
            for (int i = 0; i < clips.Length; i++)
            {
                animation.AddClip(clips[i], clips[i].name);
                if (i == 0) animation.clip = clips[i];
            }
            animation.playAutomatically = true;
            return animation;
        }

        private static GLTFAnimation WithExtension(string key, IExtension ext)
            => new GLTFAnimation { Extensions = new Dictionary<string, IExtension> { { key, ext } } };

        // ── Index collection: map expression + reference-pose animations to clip indices ─────────────

        [Test]
        public void CollectIndices_TagsExpressionAnimationsAndReferencePose_NotBodyClips()
        {
            var expr = new KHR_character_expression();
            expr.Expressions.Add(new KHR_character_expression.ExpressionItem { Expression = "smile", Animation = 1 });

            var root = new GLTFRoot
            {
                Animations = new List<GLTFAnimation>
                {
                    new GLTFAnimation(),                                                                  // 0: body, not referenced
                    new GLTFAnimation(),                                                                  // 1: expression target
                    WithExtension(KHR_character_reference_pose.EXTENSION_NAME, new KHR_character_reference_pose()), // 2: reference pose
                },
            };

            var indices = KhrCharacterImportContext.CollectSuppressedAnimationIndices(root, expr);

            CollectionAssert.AreEquivalent(new[] { 1, 2 }, indices);
            CollectionAssert.DoesNotContain(indices, 0);
        }

        [Test]
        public void CollectIndices_ReferencePoseAlias_IsDetected()
        {
            // The reference pose must ALWAYS be suppressed even under an accepted alternate spelling.
            var root = new GLTFRoot
            {
                Animations = new List<GLTFAnimation>
                {
                    WithExtension("KHR_character_bindpose", new KHR_character_reference_pose()),
                },
            };

            var indices = KhrCharacterImportContext.CollectSuppressedAnimationIndices(root, null);
            CollectionAssert.Contains(indices, 0);
        }

        [Test]
        public void CollectIndices_OutOfRangeExpressionIndex_IsIgnored()
        {
            var expr = new KHR_character_expression();
            expr.Expressions.Add(new KHR_character_expression.ExpressionItem { Expression = "x", Animation = 5 });
            var root = new GLTFRoot { Animations = new List<GLTFAnimation> { new GLTFAnimation() } };

            var indices = KhrCharacterImportContext.CollectSuppressedAnimationIndices(root, expr);
            Assert.AreEqual(0, indices.Count, "an expression pointing past the animation list contributes no clip");
        }

        // ── Legacy Animation suppression (default AnimationMethod) ───────────────────────────────────

        [Test]
        public void Legacy_FaceOnly_ClearsDefaultAndDisablesAutoPlay_ClipsRemain()
        {
            var go = NewGo("char", active: false);
            var expr = MakeClip("smile");          // index 0 — the importer's default clip
            var pose = MakeClip("ReferencePose");  // index 1
            var clips = new[] { expr, pose };
            var animation = MakeLegacyHost(go, clips);

            KhrCharacterImportContext.SuppressClipAutoPlay(go, clips, new[] { 0, 1 });

            // (a)+(b): nothing remains the auto-played default and auto-play is off.
            Assert.IsNull(animation.clip, "no suppressed clip may remain the auto-played default");
            Assert.IsFalse(animation.playAutomatically, "auto-play must be off when every clip is suppressed");
            // (b): the pose/expression no longer loop.
            Assert.AreEqual(UnityEngine.WrapMode.Once, expr.wrapMode);
            Assert.AreEqual(UnityEngine.WrapMode.Once, pose.wrapMode);
            // (c): clips alive + reachable for explicit playback.
            Assert.IsTrue(expr != null && pose != null, "clips must not be destroyed");
            Assert.IsNotNull(animation.GetClip("smile"), "expression clip stays registered for explicit play");
            Assert.IsNotNull(animation.GetClip("ReferencePose"), "pose clip stays registered for explicit play");
        }

        [Test]
        public void Legacy_MixedWithBodyClip_RepointsDefaultToBody_ExpressionStaysReachable()
        {
            var go = NewGo("char", active: false);
            var expr = MakeClip("smile"); // index 0 — suppressed, currently the default
            var body = MakeClip("idle");  // index 1 — NOT suppressed
            var clips = new[] { expr, body };
            var animation = MakeLegacyHost(go, clips);

            KhrCharacterImportContext.SuppressClipAutoPlay(go, clips, new[] { 0 });

            Assert.AreSame(body, animation.clip, "the default re-points to the non-suppressed body clip");
            Assert.IsTrue(animation.playAutomatically, "auto-play stays on so the body clip still plays");
            Assert.AreEqual(UnityEngine.WrapMode.Once, expr.wrapMode, "the suppressed expression no longer loops");
            Assert.AreEqual(UnityEngine.WrapMode.Loop, body.wrapMode, "the body clip is left untouched");
            Assert.IsNotNull(animation.GetClip("smile"), "the expression clip is still reachable for explicit play");
        }

        [Test]
        public void Legacy_ReferencePoseNotIndexZero_NeverBecomesDefault()
        {
            var go = NewGo("char", active: false);
            var body = MakeClip("idle");          // index 0 — NOT suppressed, already the default
            var pose = MakeClip("ReferencePose"); // index 1 — suppressed
            var clips = new[] { body, pose };
            var animation = MakeLegacyHost(go, clips);

            KhrCharacterImportContext.SuppressClipAutoPlay(go, clips, new[] { 1 });

            // Only the default clip auto-plays; the pose is never the default, so it cannot auto-play.
            Assert.AreSame(body, animation.clip, "the existing non-suppressed default is preserved");
            Assert.AreEqual(UnityEngine.WrapMode.Once, pose.wrapMode, "the pose no longer loops");
            Assert.IsNotNull(animation.GetClip("ReferencePose"), "the pose stays reachable for explicit play");
        }

        [UnityTest]
        public IEnumerator Legacy_FaceOnly_DoesNotAutoPlayInPlayMode_ButPlaysOnDemand()
        {
            var go = NewGo("char", active: false);
            var expr = MakeClip("smile");
            var pose = MakeClip("ReferencePose");
            var clips = new[] { expr, pose };
            var animation = MakeLegacyHost(go, clips);

            KhrCharacterImportContext.SuppressClipAutoPlay(go, clips, new[] { 0, 1 });

            go.SetActive(true);
            yield return null; // the frame in which playAutomatically would otherwise have started a clip

            Assert.IsFalse(animation.isPlaying, "no suppressed clip auto-plays once the character is live");

            // (c) the suppressed clips remain explicitly playable.
            Assert.IsTrue(animation.Play("ReferencePose"), "a suppressed clip can still be played on demand");
            yield return null;
            Assert.IsTrue(animation.IsPlaying("ReferencePose"), "explicit playback of a suppressed clip works");
        }

#if UNITY_EDITOR
        // ── Mecanim suppression (editor-only controller; mirrors GLTFSceneImporter's editor import path) ──

        private UnityEditor.Animations.AnimatorController BuildController(
            Animator animator, out UnityEditor.Animations.AnimatorStateMachine stateMachine, params (string name, AnimationClip clip)[] states)
        {
            var controller = new UnityEditor.Animations.AnimatorController { name = "test" };
            controller.AddLayer("Base");
            stateMachine = controller.layers[0].stateMachine;
            foreach (var (name, clip) in states)
            {
                var state = stateMachine.AddState(name); // the first AddState also becomes the default state
                state.motion = clip;
            }
            animator.runtimeAnimatorController = controller;
            _created.Add(controller);
            return controller;
        }

        [Test]
        public void Mecanim_DefaultStateSuppressed_RepointsToNonSuppressedState()
        {
            var go = NewGo("char", active: false);
            var expr = MakeClip("smile", legacy: false); // index 0 — suppressed, becomes the default state
            var body = MakeClip("idle", legacy: false);  // index 1 — NOT suppressed
            var clips = new[] { expr, body };
            var animator = go.AddComponent<Animator>();
            BuildController(animator, out var sm, ("smile", expr), ("idle", body));

            Assert.AreEqual(expr, sm.defaultState.motion as AnimationClip, "precondition: the suppressed clip is the default state");

            KhrCharacterImportContext.SuppressClipAutoPlay(go, clips, new[] { 0 });

            Assert.AreEqual(body, sm.defaultState.motion as AnimationClip, "the default state re-points off the suppressed clip");
            Assert.IsNotNull(animator.runtimeAnimatorController, "the controller is kept while a non-suppressed state exists");
        }

        [Test]
        public void Mecanim_AllStatesSuppressed_DetachesControllerButKeepsClips()
        {
            var go = NewGo("char", active: false);
            var expr = MakeClip("smile", legacy: false);
            var pose = MakeClip("ReferencePose", legacy: false);
            var clips = new[] { expr, pose };
            var animator = go.AddComponent<Animator>();
            BuildController(animator, out _, ("smile", expr), ("pose", pose));

            KhrCharacterImportContext.SuppressClipAutoPlay(go, clips, new[] { 0, 1 });

            Assert.IsNull(animator.runtimeAnimatorController, "the controller is detached when every state is suppressed");
            Assert.IsTrue(expr != null && pose != null, "clips remain alive (still referenced by CreatedAnimationClips)");
        }
#endif
    }
}
