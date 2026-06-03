using NUnit.Framework;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden-value tests for the additive evaluation policy: input-time phase sampling (STEP/LINEAR),
    /// single-key rest->target, blend/block masks, mapping distribution, and clamping.
    /// </summary>
    public class AdditiveExpressionSemanticsTests
    {
        private readonly IExpressionSemantics _s = AdditiveExpressionSemantics.Default;

        private static Sampler MakeSampler(float[] times, Interp interp) => new Sampler
        {
            Times = times,
            Interp = interp,
            SingleKey = times.Length <= 1,
        };

        [Test]
        public void Linear_TwoKey_GoldenAtPhases()
        {
            var s = MakeSampler(new[] { 0f, 1f }, Interp.Linear);
            var d = new[] { 0f, 1f };
            Assert.AreEqual(0f, _s.SampleScalarDelta(s, d, 0f, 0f), 1e-5f);
            Assert.AreEqual(0.5f, _s.SampleScalarDelta(s, d, 0f, 0.5f), 1e-5f);
            Assert.AreEqual(1f, _s.SampleScalarDelta(s, d, 0f, 1f), 1e-5f);
        }

        [Test]
        public void Linear_ThreeKey_GoldenInterpolation()
        {
            var s = MakeSampler(new[] { 0f, 0.5f, 1f }, Interp.Linear);
            var d = new[] { 0f, 0.5f, 1f };
            Assert.AreEqual(0.25f, _s.SampleScalarDelta(s, d, 0f, 0.25f), 1e-5f);
            Assert.AreEqual(0.5f, _s.SampleScalarDelta(s, d, 0f, 0.5f), 1e-5f);
            Assert.AreEqual(0.75f, _s.SampleScalarDelta(s, d, 0f, 0.75f), 1e-5f);
        }

        [Test]
        public void Step_HoldsThenJumps_IncludingEndpoint()
        {
            var s = MakeSampler(new[] { 0f, 0.5f, 1f }, Interp.Step);
            var d = new[] { 0f, 0.5f, 1f };
            Assert.AreEqual(0f, _s.SampleScalarDelta(s, d, 0f, 0.4f), 1e-5f);   // holds the first key
            Assert.AreEqual(0.5f, _s.SampleScalarDelta(s, d, 0f, 0.6f), 1e-5f); // jumped to the second
            Assert.AreEqual(1f, _s.SampleScalarDelta(s, d, 0f, 1f), 1e-5f);     // endpoint = last key
        }

        [Test]
        public void SingleKey_RestToTarget()
        {
            var s = new Sampler { Times = new[] { 0f }, Interp = Interp.Linear, SingleKey = true };
            var d = new[] { 0.8f }; // absolute target value
            Assert.AreEqual(0f, _s.SampleScalarDelta(s, d, 0.2f, 0f), 1e-5f);
            Assert.AreEqual(0.3f, _s.SampleScalarDelta(s, d, 0.2f, 0.5f), 1e-5f); // (0.8-0.2)*0.5
            Assert.AreEqual(0.6f, _s.SampleScalarDelta(s, d, 0.2f, 1f), 1e-5f);   // (0.8-0.2)*1
        }

        [Test]
        public void Clamp01_Bounds()
        {
            Assert.AreEqual(0f, _s.Clamp01(-0.5f));
            Assert.AreEqual(1f, _s.Clamp01(1.5f));
            Assert.AreEqual(0.3f, _s.Clamp01(0.3f), 1e-6f);
        }

        [Test]
        public void Mask_Blend_ReducesProportionally()
        {
            var tracks = new[]
            {
                new ExpressionTrack { Name = "src", Masks = new[] { new MaskEntry { TargetIndex = 1, SourceIndex = 0, Type = MaskType.Blend, Amount = 1f } } },
                new ExpressionTrack { Name = "tgt" },
            };
            Assert.AreEqual(0f, _s.ResolveMaskedInput(1, new[] { 1f, 1f }, tracks), 1e-5f);   // 1*(1 - saturate(1*1))
            Assert.AreEqual(0.5f, _s.ResolveMaskedInput(1, new[] { 0.5f, 1f }, tracks), 1e-5f); // 1*(1 - 0.5)
        }

        [Test]
        public void Mask_Block_GatesOnThreshold()
        {
            var tracks = new[]
            {
                new ExpressionTrack { Name = "src", Masks = new[] { new MaskEntry { TargetIndex = 1, SourceIndex = 0, Type = MaskType.Block, Amount = 1f, Threshold = 0.5f } } },
                new ExpressionTrack { Name = "tgt" },
            };
            Assert.AreEqual(0f, _s.ResolveMaskedInput(1, new[] { 0.6f, 1f }, tracks), 1e-5f); // source > threshold -> 1 - amount
            Assert.AreEqual(1f, _s.ResolveMaskedInput(1, new[] { 0.4f, 1f }, tracks), 1e-5f); // source <= threshold -> unchanged
        }

        [Test]
        public void Mapping_DistributesWeighted()
        {
            var set = new ExpressionMappingSet
            {
                SetName = "vrm",
                Targets = new[]
                {
                    new MappingTarget
                    {
                        TargetName = "happy",
                        Contributions = new[]
                        {
                            new MappingContribution { SourceIndex = 0, Weight = 0.7f },
                            new MappingContribution { SourceIndex = 1, Weight = 0.3f },
                        }
                    }
                }
            };
            var sources = new float[2];
            _s.DistributeMapping(set, "happy", 1f, sources, null);
            Assert.AreEqual(0.7f, sources[0], 1e-5f);
            Assert.AreEqual(0.3f, sources[1], 1e-5f);
        }
    }
}
