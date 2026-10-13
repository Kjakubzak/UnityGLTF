using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// End-to-end play-mode tests for suppression (one expression attenuating another) and vocabulary mapping
    /// (a common-vocabulary weight distributed onto model expressions), observed through morph output. Endpoint
    /// outputs read the pre-suppression drivers u.
    /// </summary>
    public class ExpressionControllerSuppressionMappingTests
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
        public IEnumerator Suppression_Blend_AttenuatesTargetExpression()
        {
            var smr = MakeSmr(out var go);
            // "a" drives blendshape 0; "b" blend-suppresses "a" fully (amount 1).
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "a", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorph(smr) } },
                    new ExpressionTrack
                    {
                        Name = "b", Domains = ExpressionDomain.None,
                        Suppressions = new[] { new SuppressionEntry { TargetIndex = 0, SourceIndex = 1, Type = SuppressionType.Blend, Amount = 1 } },
                    },
                }
            };
            set.RebuildIndex();
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("a", 1f);
            ec.SetWeight("b", 1f);
            yield return null;
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(0), 1e-3f); // fully suppressed

            ec.SetWeight("b", 0f);
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-3f); // unsuppressed
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
        public IEnumerator Suppression_Block_GatesAboveThreshold()
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
                        Suppressions = new[] { new SuppressionEntry { TargetIndex = 0, SourceIndex = 1, Type = SuppressionType.Block, Amount = 1, Threshold = 0.5 } },
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

        [UnityTest]
        public IEnumerator VocabularyOutputs_ReadPreSuppressionDrivers()
        {
            var smr = MakeSmr(out var go);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "a", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorph(smr) } },
                    new ExpressionTrack
                    {
                        Name = "b",
                        Suppressions = new[] { new SuppressionEntry { TargetIndex = 0, SourceIndex = 1, Type = SuppressionType.Blend, Amount = 1 } },
                    },
                },
                MappingSets = new[]
                {
                    new ExpressionMappingSet
                    {
                        SetName = "out",
                        Targets = new[]
                        {
                            new MappingTarget
                            {
                                TargetName = "Smile",
                                Contributions = new[] { new MappingContribution { SourceIndex = 0, Weight = 1 } },
                            },
                        },
                    },
                },
            };
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("a", 0.8f);
            ec.SetWeight("b", 1f);
            yield return null;
            Assert.AreEqual(0f, smr.GetBlendShapeWeight(0), 1e-6f, "b fully suppresses a's response");
            Assert.That(ec.Snapshot.V[0], Is.EqualTo(0d));
            Assert.That(ec.EvaluateVocabularyOutputs("out")["Smile"], Is.EqualTo(0.8f),
                "the suppressed source keeps its endpoint output, read from u");
        }

        [Test]
        public void VocabularyOutputs_FollowTheSelectedInputMapping()
        {
            var smr = MakeSmr(out var go);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "smileLeft", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorph(smr) } },
                },
                MappingSets = new[]
                {
                    new ExpressionMappingSet
                    {
                        SetName = "out",
                        Targets = new[]
                        {
                            new MappingTarget
                            {
                                TargetName = "Smile",
                                Contributions = new[] { new MappingContribution { SourceIndex = 0, Weight = 1 } },
                            },
                        },
                    },
                },
                InputMappingSets = new[]
                {
                    new ExpressionInputMappingSet
                    {
                        SetName = "vrm",
                        Commands = new[] { new InputMappingCommand { CommandName = "happy", Contributions = new[] { new InputMappingContribution { TargetIndex = 0, Weight = 0.5 } } } },
                    },
                },
            };
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            ec.SetWeight("smileLeft", 0.3f);
            ec.SetWeightByVocabulary("vrm", "happy", 1f);
            Assert.That(ec.EvaluateVocabularyOutputs("out")["Smile"], Is.EqualTo(0.3f), "direct drivers are u");
            Assert.IsTrue(ec.SelectVocabularyInputSet("vrm"));
            Assert.That(ec.EvaluateVocabularyOutputs("out")["Smile"], Is.EqualTo(0.5f),
                "the selected input mapping's output is u");
        }

        [UnityTest]
        public IEnumerator Commands_AreClampedAndMustBeFinite()
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
                        Commands = new[] { new InputMappingCommand { CommandName = "happy", Contributions = new[] { new InputMappingContribution { TargetIndex = 0, Weight = 1 } } } },
                    },
                },
            };
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);
            Assert.IsTrue(ec.SelectVocabularyInputSet("vrm"));

            ec.SetWeightByVocabulary("vrm", "happy", 3f);
            yield return null;
            Assert.AreEqual(1f, smr.GetBlendShapeWeight(0), 1e-6f, "an out-of-range command is clamped to 1");
            Assert.Throws<ArgumentOutOfRangeException>(() => ec.SetWeightByVocabulary("vrm", "happy", float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => ec.SetWeight(0, float.PositiveInfinity));
        }

        [Test]
        public void VocabularyExpressions_ListsOutputOnlySets()
        {
            var smr = MakeSmr(out var go);
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack { Name = "smileLeft", Domains = ExpressionDomain.Morph, MorphDrivers = new[] { LinearMorph(smr) } },
                },
                MappingSets = new[]
                {
                    new ExpressionMappingSet
                    {
                        SetName = "out",
                        Targets = new[]
                        {
                            new MappingTarget
                            {
                                TargetName = "Smile",
                                Contributions = new[] { new MappingContribution { SourceIndex = 0, Weight = 1 } },
                            },
                        },
                    },
                },
            };
            var ec = go.AddComponent<ExpressionController>();
            ec.Initialize(set);

            CollectionAssert.AreEqual(new[] { "Smile" }, (ICollection)ec.VocabularyExpressions("out"));
        }
    }
}
