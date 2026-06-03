using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Play-mode tests for GazeSolver: Expression mode drives look-* weights through the controller (verified
    /// via blendshape output), and BoneAim mode rotates an eye bone clamped to the max yaw.
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

        [UnityTest]
        public IEnumerator Expression_TargetSide_DrivesLookWeights()
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
            set.RebuildIndex();
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            var target = NewGo("target");
            var gaze = go.AddComponent<GazeSolver>();
            gaze.Bind(new List<LookAtTarget>(), ec, null);
            gaze.OutputMode = GazeSolver.GazeOutputMode.Expression;
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

        [UnityTest]
        public IEnumerator BoneAim_TargetRight_RotatesEyeClampedThenRebases()
        {
            var go = NewGo("char");
            var eye = new GameObject("eye");
            eye.transform.SetParent(go.transform, false); // rest = identity
            _created.Add(eye);

            var gaze = go.AddComponent<GazeSolver>();
            gaze.SetEyeBones(eye.transform, eye.transform, null);
            gaze.OutputMode = GazeSolver.GazeOutputMode.BoneAim;
            gaze.MaxYawDegrees = 35f;

            var target = NewGo("target");
            target.transform.position = go.transform.position + go.transform.right * 2f; // 90deg -> clamps to 35
            gaze.Target = target.transform;
            gaze.Mode = GazeSolver.LookAtMode.CustomTarget;

            yield return null;
            Assert.AreEqual(35f, NormalizeAngle(eye.transform.localEulerAngles.y), 0.5f);

            gaze.Mode = GazeSolver.LookAtMode.None;
            yield return null;
            Assert.AreEqual(0f, NormalizeAngle(eye.transform.localEulerAngles.y), 0.5f); // re-based to rest
        }

        private static float NormalizeAngle(float deg) => deg > 180f ? deg - 360f : deg;
    }
}
