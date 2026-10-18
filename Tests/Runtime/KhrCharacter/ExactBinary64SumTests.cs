using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;

namespace UnityGLTF.KhrCharacter.Tests
{
    public class ExactBinary64SumTests
    {
        private static readonly BigInteger OverflowThreshold =
            Scaled(double.MaxValue) + (BigInteger.One << 2044);

        [Test]
        public void LargeCancellingOperandsLeaveExactlyOneInEveryOrder()
        {
            double large = Math.Pow(2d, 60d);
            var orders = new[]
            {
                new[] { large, 1d, -large },
                new[] { large, -large, 1d },
                new[] { 1d, large, -large },
                new[] { 1d, -large, large },
                new[] { -large, large, 1d },
                new[] { -large, 1d, large },
            };

            foreach (var operands in orders)
            {
                Assert.IsTrue(ExactBinary64Sum.TrySum(operands, operands.Length, out double sum));
                Assert.AreEqual(1d, sum, string.Join(", ", operands));
            }
        }

        [Test]
        public void EqualQuantizedSamplesLeaveTheAuthoredValueExactly()
        {
            double sample = 128f / 255f;
            var operands = new[] { 0.5d, sample, -sample, sample, -sample };

            Assert.IsTrue(ExactBinary64Sum.TrySum(operands, operands.Length, out double sum));
            Assert.AreEqual(0.5d, sum);
        }

        [Test]
        public void ExactZeroIsPositiveZero()
        {
            var cases = new[]
            {
                new double[0],
                new[] { -0d },
                new[] { -0d, -0d },
                new[] { -1d, 1d },
                new[] { 0.1d, -0.1d },
                new[] { -double.Epsilon, double.Epsilon },
            };

            foreach (var operands in cases)
            {
                Assert.IsTrue(ExactBinary64Sum.TrySum(operands, operands.Length, out double sum));
                Assert.AreEqual(0L, BitConverter.DoubleToInt64Bits(sum), string.Join(", ", operands));
            }
        }

        [Test]
        public void SubnormalResultsAreKept()
        {
            double smallestNormal = BitConverter.Int64BitsToDouble(0x0010000000000000L);

            Assert.IsTrue(ExactBinary64Sum.TrySum(new[] { double.Epsilon, double.Epsilon }, 2, out double twice));
            Assert.AreEqual(2L, BitConverter.DoubleToInt64Bits(twice));
            Assert.IsTrue(ExactBinary64Sum.TrySum(new[] { smallestNormal, -double.Epsilon }, 2, out double largest));
            Assert.AreEqual(0x000FFFFFFFFFFFFFL, BitConverter.DoubleToInt64Bits(largest));
            Assert.IsTrue(ExactBinary64Sum.TrySum(new[] { 1d, double.Epsilon, -1d }, 3, out double tiny));
            Assert.AreEqual(double.Epsilon, tiny);
        }

        [Test]
        public void OverflowAndNonfiniteOperandsReturnFalse()
        {
            Assert.IsFalse(ExactBinary64Sum.TrySum(new[] { double.MaxValue, double.MaxValue }, 2, out _));
            Assert.IsFalse(ExactBinary64Sum.TrySum(new[] { -double.MaxValue, -double.MaxValue }, 2, out _));
            Assert.IsFalse(ExactBinary64Sum.TrySum(new[] { double.MaxValue, Math.Pow(2d, 970d) }, 2, out _),
                "a tie at the overflow threshold rounds to even, which is infinite");
            Assert.IsFalse(ExactBinary64Sum.TrySum(new[] { double.NaN }, 1, out _));
            Assert.IsFalse(ExactBinary64Sum.TrySum(new[] { 1d, double.PositiveInfinity }, 2, out _));
            Assert.IsFalse(ExactBinary64Sum.TrySum(
                new[] { double.PositiveInfinity, double.NegativeInfinity }, 2, out _));
        }

        [Test]
        public void IntermediateOverflowStillRoundsTheExactSum()
        {
            var orders = new[]
            {
                new[] { double.MaxValue, double.MaxValue, -double.MaxValue },
                new[] { double.MaxValue, -double.MaxValue, double.MaxValue },
            };
            foreach (var operands in orders)
            {
                Assert.IsTrue(ExactBinary64Sum.TrySum(operands, operands.Length, out double sum));
                Assert.AreEqual(double.MaxValue, sum);
            }

            Assert.IsTrue(ExactBinary64Sum.TrySum(
                new[] { double.MaxValue, Math.Pow(2d, 970d), -double.Epsilon }, 3, out double belowTie));
            Assert.AreEqual(double.MaxValue, belowTie);
        }

        [Test]
        public void SumsOnlyTheRequestedOperands()
        {
            Assert.IsTrue(ExactBinary64Sum.TrySum(new[] { 1d, 2d, double.NaN }, 2, out double sum));
            Assert.AreEqual(3d, sum);
        }

        [Test]
        public void RandomOperandSetsMatchABigIntegerReference()
        {
            var random = new Random(20261013);
            for (int trial = 0; trial < 4000; trial++)
            {
                int count = random.Next(1, 12);
                var operands = new double[count];
                for (int index = 0; index < count; index++)
                    operands[index] = index > 0 && random.Next(3) == 0
                        ? -operands[random.Next(index)] * (random.Next(4) == 0 ? 1d + Math.Pow(2d, -random.Next(1, 60)) : 1d)
                        : RandomDouble(random);
                for (int index = 0; index < count; index++)
                    if (double.IsInfinity(operands[index]) || double.IsNaN(operands[index])) operands[index] = 1d;

                var exact = BigInteger.Zero;
                foreach (double operand in operands) exact += Scaled(operand);
                bool overflows = BigInteger.Abs(exact) >= OverflowThreshold;

                double? first = null;
                for (int order = 0; order < 3; order++)
                {
                    Shuffle(operands, random);
                    bool summed = ExactBinary64Sum.TrySum(operands, count, out double sum);
                    string description = string.Join(", ", Array.ConvertAll(operands, o => o.ToString("R")));
                    if (overflows)
                    {
                        Assert.IsFalse(summed, $"the exact sum of {description} overflows");
                        continue;
                    }
                    Assert.IsTrue(summed, description);
                    Assert.IsTrue(IsCorrectlyRounded(exact, sum), $"{description} rounded to {sum:R}");
                    if (first.HasValue)
                        Assert.AreEqual(
                            BitConverter.DoubleToInt64Bits(first.Value),
                            BitConverter.DoubleToInt64Bits(sum),
                            $"the bits of {description} depend on the order");
                    first = sum;
                }
            }
        }

        // The exact value of a finite double multiplied by 2^1074, which is an integer for every finite double.
        private static BigInteger Scaled(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            int exponent = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;
            var magnitude = exponent == 0
                ? new BigInteger(fraction)
                : new BigInteger(fraction | (1L << 52)) << (exponent - 1);
            return bits < 0 ? -magnitude : magnitude;
        }

        // Correct rounding: no neighbouring double is nearer to the exact sum, and a tie has an even significand.
        private static bool IsCorrectlyRounded(BigInteger exact, double rounded)
        {
            if (double.IsNaN(rounded) || double.IsInfinity(rounded)) return false;
            if (rounded == 0d && BitConverter.DoubleToInt64Bits(rounded) != 0L) return false;
            long bits = BitConverter.DoubleToInt64Bits(rounded);
            var neighbours = new List<double>();
            if (rounded == 0d)
            {
                neighbours.Add(double.Epsilon);
                neighbours.Add(-double.Epsilon);
            }
            else
            {
                foreach (long neighbour in new[] { bits + 1, bits - 1 })
                {
                    double value = BitConverter.Int64BitsToDouble(neighbour);
                    if (!double.IsNaN(value) && !double.IsInfinity(value)) neighbours.Add(value);
                }
            }

            var distance = BigInteger.Abs(exact - Scaled(rounded));
            foreach (double neighbour in neighbours)
            {
                var neighbourDistance = BigInteger.Abs(exact - Scaled(neighbour));
                if (neighbourDistance < distance) return false;
                if (neighbourDistance == distance && (bits & 1L) != 0L) return false;
            }
            return Math.Abs(rounded) != double.MaxValue || BigInteger.Abs(exact) < OverflowThreshold;
        }

        private static double RandomDouble(Random random)
        {
            double sign = random.Next(2) == 0 ? 1d : -1d;
            switch (random.Next(6))
            {
                case 0:
                    return sign * random.NextDouble() * Math.Pow(2d, random.Next(-60, 60));
                case 1:
                {
                    long bits = ((long)random.Next() << 33) ^ ((long)random.Next() << 2) ^ random.Next(4);
                    bits &= 0x7FEFFFFFFFFFFFFFL;
                    return sign * BitConverter.Int64BitsToDouble(bits);
                }
                case 2:
                    return sign * BitConverter.Int64BitsToDouble(random.Next());
                case 3:
                    return sign * random.Next(0, 1000);
                case 4:
                    return sign * double.MaxValue * random.NextDouble();
                default:
                    return sign * Math.Pow(2d, random.Next(-1074, 1024));
            }
        }

        private static void Shuffle(double[] values, Random random)
        {
            for (int index = values.Length - 1; index > 0; index--)
            {
                int other = random.Next(index + 1);
                double swap = values[index];
                values[index] = values[other];
                values[other] = swap;
            }
        }
    }
}
