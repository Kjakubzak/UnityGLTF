using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// End-to-end play-mode golden tests for the morph evaluation path: a hand-built CharacterExpressionSet is
    /// driven through ExpressionController.LateUpdate and the resulting SkinnedMeshRenderer blendshape weights
    /// are asserted (state, not deformation, so these run headless).
    /// </summary>
    public class ExpressionControllerMorphTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        private SkinnedMeshRenderer MakeSmr(int blendShapeCount, float frameWeight, out GameObject go)
        {
            var mesh = new Mesh { name = "test" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            var delta = new[] { Vector3.one, Vector3.one, Vector3.one };
            for (int i = 0; i < blendShapeCount; i++)
                mesh.AddBlendShapeFrame("shape" + i, frameWeight, delta, null, null);
            _created.Add(mesh);

            go = new GameObject("char");
            _created.Add(go);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            return smr;
        }

        private static MorphDriver LinearMorphDriver(SkinnedMeshRenderer smr, int blendShapeIndex) => new MorphDriver
        {
            Smr = smr,
            BlendShapeIndex = blendShapeIndex,
            BaseValue = 0f,
            Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
            DeltaValues = new[] { 0f, 1f },
        };

        private static CharacterExpressionSet SetWith(params ExpressionTrack[] tracks)
        {
            var set = new CharacterExpressionSet { Expressions = tracks };
            set.RebuildIndex();
            return set;
        }

        [UnityTest]
        public IEnumerator Morph_DrivenWeight_MatchesGoldenAndRebases()
        {
            var smr = MakeSmr(1, 1f, out var go);
            var set = SetWith(new ExpressionTrack
            {
                Name = "smile", Domains = ExpressionDomain.Morph, BlendMode = ExpressionBlendMode.Additive,
                MorphDrivers = new[] { LinearMorphDriver(smr, 0) },
            });
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("smile", 1f);
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-3f);

            ec.SetWeight("smile", 0.5f);
            yield return null;
            Assert.AreEqual(0.5f, smr.GetBlendShapeWeight(0), 1e-3f);

            ec.SetWeight("smile", 0f);
            yield return null;
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(0), 1e-3f); // re-based to neutral
        }

        [UnityTest]
        public IEnumerator Morph_AppliesFrameWeightMultiplier()
        {
            var smr = MakeSmr(1, 100f, out var go); // blendshape frame weight 100
            var set = SetWith(new ExpressionTrack
            {
                Name = "smile", Domains = ExpressionDomain.Morph, BlendMode = ExpressionBlendMode.Additive,
                MorphDrivers = new[] { LinearMorphDriver(smr, 0) },
            });
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("smile", 1f);
            yield return null;
            Assert.AreEqual(100f, smr.GetBlendShapeWeight(0), 1e-2f);

            ec.SetWeight("smile", 0.5f);
            yield return null;
            Assert.AreEqual(50f, smr.GetBlendShapeWeight(0), 1e-2f);
        }

        [UnityTest]
        public IEnumerator Morph_OverlappingExpressions_AddThenClamp()
        {
            var smr = MakeSmr(1, 1f, out var go);
            var set = SetWith(
                new ExpressionTrack { Name = "a", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorphDriver(smr, 0) } },
                new ExpressionTrack { Name = "b", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorphDriver(smr, 0) } });
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("a", 0.5f);
            ec.SetWeight("b", 0.5f);
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-3f); // 0.5 + 0.5

            ec.SetWeight("a", 1f);
            ec.SetWeight("b", 1f);
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-3f); // 1 + 1 -> clamp01 -> 1
        }

        [UnityTest]
        public IEnumerator Morph_TwoExpressions_BothInactive_PreservesBase()
        {
            var smr = MakeSmr(1, 1f, out var go);
            var driverA = LinearMorphDriver(smr, 0); driverA.BaseValue = 0.25f;
            var driverB = LinearMorphDriver(smr, 0); driverB.BaseValue = 0.25f;
            var set = SetWith(
                new ExpressionTrack { Name = "a", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { driverA } },
                new ExpressionTrack { Name = "b", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { driverB } });
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            // Both expressions at d=0: each delta sample returns 0, so the model base weight is preserved (H2).
            ec.SetWeight("a", 0f);
            ec.SetWeight("b", 0f);
            yield return null;
            Assert.AreEqual(0.25f, smr.GetBlendShapeWeight(0), 1e-3f);

            // Driving one expression adds its delta over the preserved base.
            ec.SetWeight("a", 0.5f);
            yield return null;
            Assert.AreEqual(0.75f, smr.GetBlendShapeWeight(0), 1e-3f); // base 0.25 + delta 0.5
        }
    }
}
