using System;
using System.Collections.Generic;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// The immutable driver snapshot of one composition step: the pre-suppression vector <c>u</c> and the effective
    /// vector <c>v</c> that suppression produces from it, both indexed by expression entry.
    /// </summary>
    public sealed class ExpressionDriverSnapshot
    {
        internal ExpressionDriverSnapshot(double[] u, double[] v)
        {
            U = Array.AsReadOnly(u);
            V = Array.AsReadOnly(v);
        }

        /// <summary>
        /// The host's final native drivers clamped to [0, 1], with 0 for an absent driver. Input mapping and host arbitration precede this vector.
        /// </summary>
        public IReadOnlyList<double> U { get; }

        /// <summary>
        /// The effective drivers, produced after every suppression factor has been evaluated from <see cref="U"/>.
        /// Each expression entry is evaluated with its value from this vector.
        /// </summary>
        public IReadOnlyList<double> V { get; }
    }

    /// <summary>
    /// The driver stages of the KHR_character_expression pipeline as pure functions: the host's final native drivers
    /// become <c>u</c>, suppression turns <c>u</c> into <c>v</c>, and input mappings drive native
    /// expressions from an external vocabulary. Nothing here reads or writes Unity scene objects.
    /// </summary>
    public static class ExpressionDriverPipeline
    {
        /// <summary>
        /// Builds <c>u</c>: every supplied driver must be finite and is clamped to [0, 1]; a null or missing driver
        /// is 0.
        /// </summary>
        /// <param name="supplied">The host's final native drivers by expression index; may be null or shorter than
        /// <paramref name="count"/>.</param>
        /// <param name="count">The number of expression entries.</param>
        public static double[] ClampDrivers(IReadOnlyList<double?> supplied, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (supplied != null && supplied.Count > count)
                throw new ArgumentException(
                    $"{supplied.Count} drivers were supplied for {count} expression entries.", nameof(supplied));
            var u = new double[count];
            if (supplied == null) return u;
            for (int index = 0; index < supplied.Count; index++)
            {
                if (!supplied[index].HasValue) continue;
                u[index] = ClampFinite(supplied[index].Value, nameof(supplied), "Native expression drivers");
            }
            return u;
        }

        /// <summary>
        /// Builds <c>v</c> from <c>u</c>. Each target driver is multiplied by the factor of every suppression that
        /// targets it, and every factor reads its source from <paramref name="u"/>, never from a suppressed value, so
        /// cycles and self-suppression are well defined.
        /// </summary>
        /// <param name="u">The clamped pre-suppression drivers.</param>
        /// <param name="suppressionsByEntry">One suppression list per expression entry, holding the suppressions that
        /// entry contains; the containing entry is each suppression's source. Lists may be null.</param>
        public static double[] ApplySuppressions(
            IReadOnlyList<double> u,
            IReadOnlyList<IReadOnlyList<SuppressionEntry>> suppressionsByEntry)
        {
            if (u == null) throw new ArgumentNullException(nameof(u));
            var v = new double[u.Count];
            for (int index = 0; index < v.Length; index++) v[index] = u[index];
            if (suppressionsByEntry == null) return v;
            if (suppressionsByEntry.Count > u.Count)
                throw new ArgumentException(
                    "There are more suppression lists than expression drivers.", nameof(suppressionsByEntry));

            for (int source = 0; source < suppressionsByEntry.Count; source++)
            {
                var suppressions = suppressionsByEntry[source];
                if (suppressions == null) continue;
                foreach (var suppression in suppressions)
                {
                    if (suppression == null) continue;
                    if (suppression.TargetIndex < 0 || suppression.TargetIndex >= v.Length)
                        throw new ArgumentOutOfRangeException(
                            nameof(suppressionsByEntry),
                            $"Suppression of expression {source} targets expression {suppression.TargetIndex}, " +
                            "which does not exist.");
                    v[suppression.TargetIndex] *= SuppressionFactor(suppression, u[source]);
                }
            }
            return v;
        }

        /// <summary>
        /// The factor of one suppression for its source's pre-suppression driver: <c>1 - amount * u</c> for blend,
        /// <c>1 - amount</c> when <c>u</c> is strictly above the threshold for block, and 1 for a custom type, whose
        /// companion this implementation does not support.
        /// </summary>
        public static double SuppressionFactor(SuppressionEntry suppression, double sourceDriver)
        {
            if (suppression == null) throw new ArgumentNullException(nameof(suppression));
            switch (suppression.Type)
            {
                case SuppressionType.Blend:
                    return 1d - suppression.Amount * sourceDriver;
                case SuppressionType.Block:
                    return sourceDriver > suppression.Threshold ? 1d - suppression.Amount : 1d;
                default:
                    return 1d;
            }
        }

        /// <summary>Builds the driver snapshot: <c>u</c> from the supplied drivers, then <c>v</c> by suppression.</summary>
        /// <param name="supplied">The host's final native drivers by expression index.</param>
        /// <param name="suppressionsByEntry">One suppression list per expression entry; its count is the number of
        /// entries.</param>
        public static ExpressionDriverSnapshot BuildSnapshot(
            IReadOnlyList<double?> supplied,
            IReadOnlyList<IReadOnlyList<SuppressionEntry>> suppressionsByEntry)
        {
            if (suppressionsByEntry == null) throw new ArgumentNullException(nameof(suppressionsByEntry));
            var u = ClampDrivers(supplied, suppressionsByEntry.Count);
            return new ExpressionDriverSnapshot(u, ApplySuppressions(u, suppressionsByEntry));
        }

        /// <summary>
        /// Optional host projection from native drivers to keys. This is not a standardized mapping operation or inverse.
        /// Each input is clamped to [0, 1] and a missing one is 0; each <c>weight * value</c> is one binary64
        /// product, the products are summed exactly and the sum is clamped once.
        /// </summary>
        /// <returns>One output per endpoint key.</returns>
        public static IReadOnlyDictionary<string, double> EvaluateForwardMapping(
            ExpressionMappingSet set,
            IReadOnlyList<double> u)
        {
            var products = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            if (set?.Targets != null)
            {
                foreach (var endpoint in set.Targets)
                {
                    if (endpoint?.TargetName == null) continue;
                    if (!products.TryGetValue(endpoint.TargetName, out var operands))
                        products.Add(endpoint.TargetName, operands = new List<double>());
                    if (endpoint.Contributions == null) continue;
                    foreach (var contribution in endpoint.Contributions)
                    {
                        double input = contribution.SourceIndex >= 0 && u != null && contribution.SourceIndex < u.Count
                            ? ClampFinite(u[contribution.SourceIndex], nameof(u), "Native expression drivers")
                            : 0d;
                        operands.Add(Product(contribution.Weight, input));
                    }
                }
            }

            var outputs = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var endpoint in products)
                outputs.Add(endpoint.Key, ClampedSum(endpoint.Value));
            return outputs;
        }

        /// <summary>
        /// Computes the native drivers of an <c>expressionSetInputMappings</c> set from endpoint commands. Each command
        /// is clamped to [0, 1] and a missing or unrecognized one is 0; each <c>weight * command</c> is one binary64
        /// product, and each native driver is the exact sum of its products, clamped once.
        /// </summary>
        /// <param name="set">The selected input mapping set.</param>
        /// <param name="commands">Endpoint commands by endpoint key; may be null.</param>
        /// <param name="nativeCount">The number of expression entries.</param>
        public static double[] EvaluateInputMapping(
            ExpressionInputMappingSet set,
            IReadOnlyDictionary<string, double> commands,
            int nativeCount)
        {
            if (nativeCount < 0) throw new ArgumentOutOfRangeException(nameof(nativeCount));
            var products = new List<double>[nativeCount];
            if (set?.Commands != null)
            {
                foreach (var command in set.Commands)
                {
                    if (command?.CommandName == null || command.Contributions == null) continue;
                    double value = commands != null && TryGetExactInput(commands, command.CommandName, out double supplied)
                        ? ClampFinite(supplied, nameof(commands), "Endpoint commands")
                        : 0d;
                    foreach (var contribution in command.Contributions)
                    {
                        if (contribution.TargetIndex < 0 || contribution.TargetIndex >= nativeCount) continue;
                        var operands = products[contribution.TargetIndex] ??
                                       (products[contribution.TargetIndex] = new List<double>());
                        operands.Add(Product(contribution.Weight, value));
                    }
                }
            }

            var native = new double[nativeCount];
            for (int index = 0; index < nativeCount; index++)
                if (products[index] != null) native[index] = ClampedSum(products[index]);
            return native;
        }

        /// <summary>Evaluates the current external-input to native-driver table, preserving the full native count.</summary>
        public static double[] EvaluateExpressionSetMapping(
            ExpressionMappingSet set, IReadOnlyDictionary<string, double> inputs, int nativeCount)
        {
            if (inputs != null)
                foreach (var supplied in inputs) ClampFinite(supplied.Value, nameof(inputs), "External inputs");
            if (set?.Targets != null)
                foreach (var input in set.Targets)
                    if (input?.Contributions != null)
                        foreach (var term in input.Contributions)
                            if (double.IsNaN(term.Weight) || term.Weight < 0d || term.Weight > 1d)
                                throw new ArgumentOutOfRangeException(nameof(set), "Mapping coefficients must be in [0, 1].");
            return EvaluateInputMapping(ToInputMappingSet(set), inputs, nativeCount);
        }

        /// <summary>Adapts the current table to the controller's input-oriented representation without changing indices.</summary>
        public static ExpressionInputMappingSet ToInputMappingSet(ExpressionMappingSet set)
        {
            if (set == null) return null;
            var commands = new List<InputMappingCommand>();
            if (set.Targets != null)
                foreach (var input in set.Targets)
                {
                    if (input == null) continue;
                    var terms = new List<InputMappingContribution>();
                    if (input.Contributions != null)
                        foreach (var term in input.Contributions)
                            terms.Add(new InputMappingContribution
                            {
                                TargetIndex = term.SourceIndex, Name = term.Name, Weight = term.Weight,
                                ExtensionsJson = term.ExtensionsJson, ExtrasJson = term.ExtrasJson,
                                AdditionalPropertiesJson = term.AdditionalPropertiesJson,
                            });
                    commands.Add(new InputMappingCommand { CommandName = input.TargetName, Contributions = terms.ToArray() });
                }
            return new ExpressionInputMappingSet { SetName = set.SetName, Commands = commands.ToArray() };
        }

        public static ExpressionInputMappingSet[] ToInputMappingSets(IReadOnlyList<ExpressionMappingSet> sets)
        {
            if (sets == null) return null;
            var result = new ExpressionInputMappingSet[sets.Count];
            for (int i = 0; i < result.Length; i++) result[i] = ToInputMappingSet(sets[i]);
            return result;
        }

        /// <summary>Explicit migration of the former input-only authored operation into the current wire-shaped table.</summary>
        public static ExpressionMappingSet[] FromLegacyInputMappingSets(IReadOnlyList<ExpressionInputMappingSet> sets)
        {
            if (sets == null) return null;
            var result = new ExpressionMappingSet[sets.Count];
            for (int i = 0; i < result.Length; i++)
            {
                var set = sets[i];
                if (set == null) continue;
                var targets = new List<MappingTarget>();
                if (set.Commands != null)
                    foreach (var command in set.Commands)
                    {
                        if (command == null) continue;
                        var terms = new List<MappingContribution>();
                        if (command.Contributions != null)
                            foreach (var term in command.Contributions)
                                terms.Add(new MappingContribution
                                {
                                    SourceIndex = term.TargetIndex, Name = term.Name, Weight = term.Weight,
                                    ExtensionsJson = term.ExtensionsJson, ExtrasJson = term.ExtrasJson,
                                    AdditionalPropertiesJson = term.AdditionalPropertiesJson,
                                });
                        targets.Add(new MappingTarget { TargetName = command.CommandName, Contributions = terms.ToArray() });
                    }
                result[i] = new ExpressionMappingSet { SetName = set.SetName, Targets = targets.ToArray() };
            }
            return result;
        }

        private static bool TryGetExactInput(IReadOnlyDictionary<string, double> inputs, string key, out double value)
        {
            foreach (var input in inputs)
                if (string.Equals(input.Key, key, StringComparison.Ordinal))
                {
                    value = input.Value;
                    return true;
                }
            value = 0d;
            return false;
        }

        private static double Product(double weight, double value)
        {
            if (double.IsNaN(weight) || double.IsInfinity(weight))
                throw new ArgumentOutOfRangeException(nameof(weight), "Mapping weights must be finite.");
            return weight * value;
        }

        // The products are already stored, so each was rounded on its own.
        private static double ClampedSum(List<double> products)
        {
            var operands = products.ToArray();
            if (!ExactBinary64Sum.TrySum(operands, operands.Length, out double sum))
                throw new ArgumentOutOfRangeException(nameof(products), "A mapping sum is not finite.");
            return Math.Min(1d, Math.Max(0d, sum));
        }

        private static double ClampFinite(double value, string parameter, string description)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameter, $"{description} must be finite.");
            if (value < 0d) return 0d;
            return value > 1d ? 1d : value;
        }
    }
}
