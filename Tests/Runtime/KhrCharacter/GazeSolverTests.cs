using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Play-mode tests for the expression-driven GazeSolver: it drives look-* weights through the controller
    /// (verified via blendshape output) measured against its ReferenceFrame, works on a non-humanoid object with
    /// no SkeletonMap, honors overridden look-expression names, and the importer auto-detects look names from the
    /// baked set. Geometric eye-bone aiming now lives in EyeAimConstraint (see EyeAimConstraintTests).
    /// </summary>
    public class GazeSolverTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
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

        private ExpressionController MakeController(GameObject go, params (string name, MorphDriver morph)[] expressions)
        {
            var tracks = new ExpressionTrack[expressions.Length];
            for (int i = 0; i < expressions.Length; i++)
                tracks[i] = new ExpressionTrack
                {
                    Name = expressions[i].name,
                    Domains = ExpressionDomain.Morph,
                    MorphDrivers = new[] { expressions[i].morph },
                };
            var set = new CharacterExpressionSet { Expressions = tracks };
            set.RebuildIndex();
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);
            return ec;
        }

        [UnityTest]
        public IEnumerator Expression_TargetSide_DrivesLookWeights()
        {
            var go = NewGo("char");
            var smr = MakeSmr(go, 2);
            var ec = MakeController(go, ("lookRight", Morph(smr, 0)), ("lookLeft", Morph(smr, 1)));

            var target = NewGo("target");
            var gaze = go.AddComponent<GazeSolver>();
            gaze.Bind(new List<LookAtTarget>(), ec, null);
            gaze.Target = target.transform;
            gaze.Mode = GazeSolver.LookAtMode.CustomTarget;

            target.transform.position = go.transform.position + go.transform.right * 2f; // 90deg right -> saturate = 1
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-2f);
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(1), 1e-2f);

            target.transform.position = go.transform.position - go.transform.right * 2f; // 90deg left
            yield return null;
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(0), 1e-2f);
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(1), 1e-2f);
        }

        // A non-humanoid object (no SkeletonMap) drives look expressions purely from an explicit ReferenceFrame.
        // The frame is rotated 180° about Y, so a target at world +X reads as "look left" in that frame — proving
        // the gaze is measured against ReferenceFrame, not the root transform.
        [UnityTest]
        public IEnumerator NonHumanoid_ReferenceFrame_DrivesLookWeights()
        {
            var go = NewGo("char");
            var smr = MakeSmr(go, 2);
            var ec = MakeController(go, ("lookRight", Morph(smr, 0)), ("lookLeft", Morph(smr, 1)));

            var refFrame = NewGo("refFrame");
            refFrame.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            var gaze = go.AddComponent<GazeSolver>();
            gaze.Bind(new List<LookAtTarget>(), ec, null); // no SkeletonMap
            gaze.ReferenceFrame = refFrame.transform;

            var target = NewGo("target");
            target.transform.position = new Vector3(2f, 0f, 0f); // world +X -> reference-frame local -X
            gaze.Target = target.transform;
            gaze.Mode = GazeSolver.LookAtMode.CustomTarget;

            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(1), 1e-2f, "look LEFT saturates in the rotated reference frame");
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(0), 1e-2f, "look RIGHT inactive");
        }

        // Look-expression names are overridable; the solver drives whatever names are set (vendor-neutral).
        [UnityTest]
        public IEnumerator LookNames_Override_DrivesCustomNamedExpressions()
        {
            var go = NewGo("char");
            var smr = MakeSmr(go, 2);
            var ec = MakeController(go, ("gaze_right", Morph(smr, 0)), ("gaze_left", Morph(smr, 1)));

            var gaze = go.AddComponent<GazeSolver>();
            gaze.Bind(new List<LookAtTarget>(), ec, null);
            gaze.LookRight = "gaze_right";
            gaze.LookLeft = "gaze_left";

            var target = NewGo("target");
            gaze.Target = target.transform;
            gaze.Mode = GazeSolver.LookAtMode.CustomTarget;

            target.transform.position = go.transform.position + go.transform.right * 2f; // 90deg right
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-2f);
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(1), 1e-2f);
        }

        // The importer binds each look direction to whichever expression actually exists in the baked set
        // (vendor-neutral spellings), and keeps the default when a direction is absent.
        [Test]
        public void ImporterAutoDetect_BindsRealLookNames_AndFallsBack()
        {
            var go = NewGo("char");
            var smr = MakeSmr(go, 3);
            var ec = MakeController(go,
                ("look_right", Morph(smr, 0)),   // snake_case
                ("look_left", Morph(smr, 1)),
                ("eyeLookUp", Morph(smr, 2)));   // eye- prefix; "down" deliberately absent

            var gaze = go.AddComponent<GazeSolver>();
            KhrCharacterImportContext.BindLookExpressionNames(gaze, ec);

            Assert.AreEqual("look_right", gaze.LookRight);
            Assert.AreEqual("look_left", gaze.LookLeft);
            Assert.AreEqual("eyeLookUp", gaze.LookUp);
            Assert.AreEqual("lookDown", gaze.LookDown, "absent direction keeps the GazeSolver default");
        }
    }
}
