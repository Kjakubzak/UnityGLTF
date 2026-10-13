using System;
using System.Numerics;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Exact binary64 summation for the reproducible additive accumulation of <c>KHR_character_expression</c>. The
    /// operands are added exactly and the exact sum is rounded once to binary64 with round-to-nearest, ties-to-even,
    /// so the result bits do not depend on the operand order. An exact zero is +0 and subnormal results are kept.
    /// </summary>
    public static class ExactBinary64Sum
    {
        [ThreadStatic] private static double[] s_partials;

        /// <summary>Sums the first <paramref name="count"/> operands exactly and rounds the sum once.</summary>
        /// <param name="operands">The binary64 operands. A product that is an operand must be stored in this array
        /// before the call, so that it is rounded on its own and never fused with an addition.</param>
        /// <param name="count">How many operands, from the start of the array, to add.</param>
        /// <param name="result">The rounded sum, or 0 when the method returns false.</param>
        /// <returns>False when an operand is not finite or the rounded sum is not finite.</returns>
        public static bool TrySum(double[] operands, int count, out double result)
        {
            if (operands == null) throw new ArgumentNullException(nameof(operands));
            if (count < 0 || count > operands.Length) throw new ArgumentOutOfRangeException(nameof(count));
            result = 0d;
            for (int index = 0; index < count; index++)
                if (!IsFinite(operands[index])) return false;

            // Shewchuk's nonoverlapping partials, as in Python's math.fsum.
            var partials = s_partials;
            if (partials == null || partials.Length < count)
                s_partials = partials = new double[Math.Max(count, 16)];
            int partialCount = 0;
            for (int index = 0; index < count; index++)
            {
                double x = operands[index];
                int kept = 0;
                for (int partial = 0; partial < partialCount; partial++)
                {
                    double y = partials[partial];
                    if (Math.Abs(x) < Math.Abs(y))
                    {
                        double swap = x;
                        x = y;
                        y = swap;
                    }
                    double high = x + y;
                    double low = y - (high - x);
                    if (low != 0d) partials[kept++] = low;
                    x = high;
                }
                if (!IsFinite(x)) return TrySumDyadic(operands, count, out result);
                partialCount = kept;
                if (x != 0d) partials[partialCount++] = x;
            }

            double sum = 0d;
            if (partialCount > 0)
            {
                sum = partials[--partialCount];
                double low = 0d;
                while (partialCount > 0)
                {
                    double x = sum;
                    double y = partials[--partialCount];
                    sum = x + y;
                    low = y - (sum - x);
                    if (low != 0d) break;
                }
                // Half-way correction: the partials below can push an exact tie to the next representable value.
                if (partialCount > 0 &&
                    ((low < 0d && partials[partialCount - 1] < 0d) || (low > 0d && partials[partialCount - 1] > 0d)))
                {
                    double y = low * 2d;
                    double x = sum + y;
                    if (y == x - sum) sum = x;
                }
            }

            // Only a sum next to the overflow threshold reaches this; the exact sum decides.
            if (!IsFinite(sum)) return TrySumDyadic(operands, count, out result);
            result = sum == 0d ? 0d : sum;
            return true;
        }

        private static bool TrySumDyadic(double[] operands, int count, out double result)
        {
            var total = BigInteger.Zero;
            for (int index = 0; index < count; index++)
                total += ToScaledInteger(operands[index]);
            result = FromScaledInteger(total);
            if (IsFinite(result)) return true;
            result = 0d;
            return false;
        }

        // The exact value of a finite double multiplied by 2^1074, which is an integer for every finite double.
        private static BigInteger ToScaledInteger(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            int exponentField = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;
            var magnitude = exponentField == 0
                ? new BigInteger(fraction)
                : new BigInteger(fraction | (1L << 52)) << (exponentField - 1);
            return bits < 0 ? -magnitude : magnitude;
        }

        // Rounds scaled * 2^-1074 to binary64, ties to even; returns an infinity when the rounded value overflows.
        private static double FromScaledInteger(BigInteger scaled)
        {
            if (scaled.IsZero) return 0d;
            bool negative = scaled.Sign < 0;
            var magnitude = BigInteger.Abs(scaled);
            int bitLength = BitLength(magnitude);
            long bits;
            if (bitLength <= 53)
            {
                // Exact: below 2^-1022 the bits are the subnormal fraction, and from 2^52 up they are the
                // fraction of the smallest normal binade with its implicit bit in the exponent field.
                bits = (long)magnitude;
            }
            else
            {
                int shift = bitLength - 53;
                var quotient = magnitude >> shift;
                var remainder = magnitude - (quotient << shift);
                int comparison = remainder.CompareTo(BigInteger.One << (shift - 1));
                if (comparison > 0 || (comparison == 0 && !quotient.IsEven)) quotient += BigInteger.One;
                if (BitLength(quotient) > 53)
                {
                    quotient >>= 1;
                    shift++;
                }
                int exponentField = shift + 1;
                if (exponentField >= 0x7FF) return negative ? double.NegativeInfinity : double.PositiveInfinity;
                bits = ((long)exponentField << 52) | ((long)quotient - (1L << 52));
            }
            if (negative) bits |= long.MinValue;
            return BitConverter.Int64BitsToDouble(bits);
        }

        private static int BitLength(BigInteger positive)
        {
            var bytes = positive.ToByteArray();
            int top = bytes.Length - 1;
            while (top > 0 && bytes[top] == 0) top--;
            int length = top * 8;
            for (int value = bytes[top]; value != 0; value >>= 1) length++;
            return length;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
