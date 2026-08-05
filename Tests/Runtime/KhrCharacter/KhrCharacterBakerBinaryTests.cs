using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>STEP interpolation is timeline behavior, not an authored binary-driver declaration.</summary>
    public class KhrCharacterBakerBinaryTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        [Test]
        public void StepSamplerDoesNotImplicitlyMakeTrackBinary()
        {
            var track = new ExpressionTrack
            {
                MorphDrivers = new[]
                {
                    new MorphDriver
                    {
                        Sampler = new Sampler
                        {
                            Times = new[] { 0f, 1f },
                            Interp = Interp.Step,
                        },
                    },
                },
            };

            Assert.IsFalse(track.IsBinary);
        }

        [Test]
        public void ControllerPresentsStepTrackAsContinuousByDefault()
        {
            var controller = CreateController(false);

            Assert.That(controller.Expressions.Count, Is.EqualTo(1));
            Assert.IsFalse(controller.Expressions[0].IsBinary);
        }

        [Test]
        public void ExplicitHostAuthoredBinaryMetadataIsPreserved()
        {
            var controller = CreateController(true);

            Assert.IsTrue(controller.Expressions[0].IsBinary);
        }

        private ExpressionController CreateController(bool isBinary)
        {
            _root = new GameObject("binary-host-metadata");
            var set = new CharacterExpressionSet
            {
                Expressions = new[]
                {
                    new ExpressionTrack
                    {
                        Name = "expression",
                        IsBinary = isBinary,
                        MorphDrivers = new[]
                        {
                            new MorphDriver
                            {
                                Sampler = new Sampler
                                {
                                    Times = new[] { 0f, 1f },
                                    Interp = Interp.Step,
                                },
                            },
                        },
                    },
                },
            };
            set.RebuildIndex();
            var controller = _root.AddComponent<ExpressionController>();
            controller.Initialize(set);
            return controller;
        }
    }
}
