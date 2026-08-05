using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// End-to-end play-mode tests for masking (one expression attenuating another) and vocabulary mapping
    /// (a common-vocabulary weight distributed onto model expressions), observed through morph output.
    /// </summary>
    public class ExpressionControllerMaskMappingTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
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

        private static MorphDriver LinearMorph(SkinnedMeshRenderer smr) => new MorphDriver
        {
            Smr = smr,
            BlendShapeIndex = 0,
            BaseValue = 0f,
            Sampler = new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear, SingleKey = false },
            DeltaValues = new[] { 0f, 1f },
        };

        [UnityTest]
        public IEnumerator Mask_Blend_AttenuatesTargetExpression()
        {
            var smr = MakeSmr(out var go);
            // "a" drives blendshape 0; "b" blend-masks "a" fully (amount 1).
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "a", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorph(smr) } },
                    new ExpressionTrack
                    {
                        Name = "b", Domains = ExpressionDomain.None,
                        Masks = new[] { new MaskEntry { TargetIndex = 0, SourceIndex = 1, Type = MaskType.Blend, Amount = 1f } },
                    },
                }
            };
            set.RebuildIndex();
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("a", 1f);
            ec.SetWeight("b", 1f);
            yield return null;
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(0), 1e-3f); // fully masked

            ec.SetWeight("b", 0f);
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-3f); // unmasked
        }

        [UnityTest]
        public IEnumerator Mapping_DistributesVocabularyWeight()
        {
            var smr = MakeSmr(out var go);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "smileLeft", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorph(smr) } },
                },
                InputMappingSets = new[]
                {
                    new ExpressionInputMappingSet
                    {
                        SetName = "vrm",
                        Commands = new[]
                        {
                            new InputMappingCommand
                            {
                                CommandName = "happy",
                                Contributions = new[] { new InputMappingContribution { TargetIndex = 0, Weight = 1f } },
                            }
                        }
                    }
                }
            };
            set.RebuildIndex();
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            CollectionAssert.Contains(new List<string>(ec.VocabularySets), "vrm");

            Assert.IsTrue(ec.SelectVocabularyInputSet("vrm"));
            ec.SetWeightByVocabulary("vrm", "happy", 1f);
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-3f);

            ec.SetWeightByVocabulary("vrm", "happy", 0.5f);
            yield return null;
            Assert.AreEqual(0.5f, smr.GetBlendShapeWeight(0), 1e-3f);
        }

        [UnityTest]
        public IEnumerator Mask_Block_GatesAboveThreshold()
        {
            var smr = MakeSmr(out var go);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "a", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorph(smr) } },
                    new ExpressionTrack
                    {
                        Name = "b", Domains = ExpressionDomain.None,
                        Masks = new[] { new MaskEntry { TargetIndex = 0, SourceIndex = 1, Type = MaskType.Block, Amount = 1f, Threshold = 0.5f } },
                    },
                }
            };
            set.RebuildIndex();
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("a", 1f);
            ec.SetWeight("b", 0.6f); // above threshold -> blocks a
            yield return null;
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(0), 1e-3f);

            ec.SetWeight("b", 0.4f); // below threshold -> a passes
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-3f);
        }

        [UnityTest]
        public IEnumerator Mapping_DoesNotImplicitlyComposeWithDirectWeight()
        {
            var smr = MakeSmr(out var go);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "smileLeft", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorph(smr) } },
                },
                InputMappingSets = new[]
                {
                    new ExpressionInputMappingSet
                    {
                        SetName = "vrm",
                        Commands = new[] { new InputMappingCommand { CommandName = "happy", Contributions = new[] { new InputMappingContribution { TargetIndex = 0, Weight = 0.5f } } } },
                    }
                }
            };
            set.RebuildIndex();
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("smileLeft", 0.3f);
            ec.SetWeightByVocabulary("vrm", "happy", 1f);
            yield return null;
            Assert.AreEqual(0.3f, smr.GetBlendShapeWeight(0), 1e-3f,
                "endpoint commands do not alter the direct-native surface until the host selects it");

            Assert.IsTrue(ec.SelectVocabularyInputSet("vrm"));
            yield return null;
            Assert.AreEqual(0.5f, smr.GetBlendShapeWeight(0), 1e-3f,
                "the selected mapped surface replaces rather than adds to direct-native input");
        }

        [UnityTest]
        public IEnumerator ResetAll_ClearsSelectedVocabularyInputWeights()
        {
            var smr = MakeSmr(out var go);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "smileLeft", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorph(smr) } },
                },
                InputMappingSets = new[]
                {
                    new ExpressionInputMappingSet
                    {
                        SetName = "vrm",
                        Commands = new[] { new InputMappingCommand { CommandName = "happy", Contributions = new[] { new InputMappingContribution { TargetIndex = 0, Weight = 1f } } } },
                    }
                }
            };
            set.RebuildIndex();
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            Assert.IsTrue(ec.SelectVocabularyInputSet("vrm"));
            ec.SetWeightByVocabulary("vrm", "happy", 1f);
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-3f);

            ec.ResetAll();
            yield return null;
            Assert.AreEqual("vrm", ec.SelectedInputMappingSet,
                "resetting values should not silently change the host-selected input surface");
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(0), 1e-3f,
                "ResetAll must clear latent mapped commands as well as direct-native weights");
        }

        [Test]
        public void Mapping_VocabularyOverdrive_IsRejected()
        {
            var smr = MakeSmr(out var go);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "smileLeft", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorph(smr) } },
                },
                InputMappingSets = new[]
                {
                    new ExpressionInputMappingSet
                    {
                        SetName = "vrm",
                        Commands = new[] { new InputMappingCommand { CommandName = "happy", Contributions = new[] { new InputMappingContribution { TargetIndex = 0, Weight = 1f } } } },
                    }
                }
            };
            set.RebuildIndex();
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                ec.SetWeightByVocabulary("vrm", "happy", 1.5f));
        }
    }
}
