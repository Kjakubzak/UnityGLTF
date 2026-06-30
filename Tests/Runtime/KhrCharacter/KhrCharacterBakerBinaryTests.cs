using NUnit.Framework;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Verifies ExpressionTrack.IsBinary detection (KhrCharacterBaker.AllStep): an expression is "binary" when it
    /// carries at least one driver and EVERY driver -- across morph, joint, AND texture domains -- uses STEP
    /// interpolation. Binary is not morph-exclusive: a STEP-only joint or texture expression is binary too.
    /// </summary>
    public class KhrCharacterBakerBinaryTests
    {
        private static Sampler Step => new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Step };
        private static Sampler Linear => new Sampler { Times = new[] { 0f, 1f }, Interp = Interp.Linear };

        [Test]
        public void Morph_AllStep_IsBinary()
        {
            var t = new ExpressionTrack { MorphDrivers = new[] { new MorphDriver { Sampler = Step } } };
            Assert.IsTrue(KhrCharacterBaker.AllStep(t));
        }

        [Test]
        public void Morph_WithLinearChannel_IsNotBinary()
        {
            var t = new ExpressionTrack
            {
                MorphDrivers = new[] { new MorphDriver { Sampler = Step }, new MorphDriver { Sampler = Linear } },
            };
            Assert.IsFalse(KhrCharacterBaker.AllStep(t));
        }

        // The broadening: a STEP-only JOINT expression with no morph drivers is still binary.
        [Test]
        public void JointOnly_AllStep_IsBinary()
        {
            var t = new ExpressionTrack { JointDrivers = new[] { new JointDriver { Sampler = Step } } };
            Assert.IsTrue(KhrCharacterBaker.AllStep(t));
        }

        // ...and a STEP-only TEXTURE expression (e.g. an index swap) is binary too.
        [Test]
        public void TextureOnly_AllStep_IsBinary()
        {
            var t = new ExpressionTrack { TextureDrivers = new[] { new TextureDriver { Sampler = Step } } };
            Assert.IsTrue(KhrCharacterBaker.AllStep(t));
        }

        // A single non-STEP channel in any domain disqualifies the whole expression.
        [Test]
        public void MixedDomains_OneLinear_IsNotBinary()
        {
            var t = new ExpressionTrack
            {
                MorphDrivers = new[] { new MorphDriver { Sampler = Step } },
                JointDrivers = new[] { new JointDriver { Sampler = Linear } },
            };
            Assert.IsFalse(KhrCharacterBaker.AllStep(t));
        }

        [Test]
        public void NoDrivers_IsNotBinary()
        {
            Assert.IsFalse(KhrCharacterBaker.AllStep(new ExpressionTrack()));
        }
    }
}
