using System;
using System.Collections.Generic;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>The aggregate response of one composition step for one concrete property, in glTF space.</summary>
    public sealed class ExpressionAggregateRecord
    {
        internal ExpressionAggregateRecord(
            string property,
            ExpressionPropertyKind kind,
            bool isSingleWriter,
            double[] value,
            bool[] contributed)
        {
            Property = property;
            Kind = kind;
            IsSingleWriter = isSingleWriter;
            Value = Array.AsReadOnly(value);
            Contributed = Array.AsReadOnly(contributed);
        }

        /// <summary>The canonical property, such as <c>/nodes/3/translation</c> or <c>/nodes/2/weights</c>.</summary>
        public string Property { get; }

        /// <summary>The concrete property type.</summary>
        public ExpressionPropertyKind Kind { get; }

        /// <summary>True when the value is the single absolute sample of a single-writer property.</summary>
        public bool IsSingleWriter { get; }

        /// <summary>
        /// The aggregate value in glTF space. An additive component is <c>b + sum(s - n)</c>, accumulated exactly and
        /// never clamped or wrapped; morph weights are the complete vector of the node.
        /// </summary>
        public IReadOnlyList<double> Value { get; }

        /// <summary>
        /// Per component, whether a current response record covered it. Only a morph-weight element can be false;
        /// such an element holds its authored value.
        /// </summary>
        public IReadOnlyList<bool> Contributed { get; }
    }

    /// <summary>The result of one simultaneous-expression composition step.</summary>
    public sealed class ExpressionComposition
    {
        private readonly Dictionary<string, ExpressionAggregateRecord> _byProperty;

        internal ExpressionComposition(
            ExpressionAggregateRecord[] records,
            ExpressionEvaluationFailure[] failures,
            IReadOnlyList<ExpressionResponse> responses)
        {
            Records = Array.AsReadOnly(records);
            Failures = Array.AsReadOnly(failures);
            Responses = responses;
            _byProperty = new Dictionary<string, ExpressionAggregateRecord>(StringComparer.Ordinal);
            foreach (var record in records) _byProperty[record.Property] = record;
        }

        /// <summary>One aggregate record per property with at least one current response record, by property.</summary>
        public IReadOnlyList<ExpressionAggregateRecord> Records { get; }

        /// <summary>
        /// The failures of this step, from evaluation and composition. A property with a failure has no record.
        /// </summary>
        public IReadOnlyList<ExpressionEvaluationFailure> Failures { get; }

        /// <summary>The per-entry responses that were composed, by expression index.</summary>
        public IReadOnlyList<ExpressionResponse> Responses { get; }

        /// <summary>Finds the aggregate record of a canonical property; false when the property has none.</summary>
        public bool TryGetRecord(string property, out ExpressionAggregateRecord record)
        {
            record = null;
            return property != null && _byProperty.TryGetValue(property, out record);
        }
    }

    /// <summary>
    /// The composition stage of KHR_character_expression as a pure function. It combines the responses of every
    /// entry for one driver snapshot without depending on their order: additive components use
    /// <c>y = b + sum(s - n)</c> with exact binary64 accumulation, morph weights become one complete vector per node,
    /// and a single-writer property keeps its single absolute sample.
    /// </summary>
    public static class ExpressionComposer
    {
        private readonly struct Contribution
        {
            public Contribution(int expressionIndex, ExpressionResponseRecord record)
            {
                ExpressionIndex = expressionIndex;
                Record = record;
            }

            public int ExpressionIndex { get; }
            public ExpressionResponseRecord Record { get; }
        }

        private sealed class PropertyGroup
        {
            public readonly List<Contribution> Contributions = new List<Contribution>();
            public bool Failed;
        }

        /// <summary>
        /// True for the properties KHR_character_expression and its standard derived extensions make single-writer:
        /// quaternions, node <c>rotation</c> and <c>scale</c>, and <c>KHR_texture_transform</c> <c>scale</c>.
        /// </summary>
        public static bool IsStandardSingleWriter(ExpressionResponseTarget target)
        {
            if (target == null) return false;
            switch (target.Kind)
            {
                case ExpressionPropertyKind.NodeRotation:
                case ExpressionPropertyKind.NodeScale:
                case ExpressionPropertyKind.TextureScale:
                    return true;
            }
            return target.ValueKind == ExpressionResponseValueKind.Quaternion;
        }

        /// <summary>
        /// The single-writer predicate for an asset: the standard single-writer properties plus every property that a
        /// derived extension carried by one of its entries declares single-writer. Declarations count only for
        /// extensions this implementation supports.
        /// </summary>
        public static Func<ExpressionResponseTarget, bool> SingleWriterPredicate(
            IEnumerable<ExpressionDerivedExtension> carriedExtensions)
        {
            var declarations = new List<Func<ExpressionResponseTarget, bool>>();
            if (carriedExtensions != null)
                foreach (var extension in carriedExtensions)
                    if (extension != null && extension.IsSupported && extension.DeclaresSingleWriter != null &&
                        !declarations.Contains(extension.DeclaresSingleWriter))
                        declarations.Add(extension.DeclaresSingleWriter);
            if (declarations.Count == 0) return IsStandardSingleWriter;
            return target =>
            {
                if (IsStandardSingleWriter(target)) return true;
                foreach (var declaration in declarations)
                    if (declaration(target)) return true;
                return false;
            };
        }

        /// <summary>Composes the responses of all entries for one driver snapshot.</summary>
        /// <param name="responses">The response of every expression entry, by expression index.</param>
        /// <param name="isSingleWriter">The asset's single-writer predicate; null uses
        /// <see cref="IsStandardSingleWriter"/>.</param>
        public static ExpressionComposition Compose(
            IReadOnlyList<ExpressionResponse> responses,
            Func<ExpressionResponseTarget, bool> isSingleWriter = null)
        {
            if (responses == null) throw new ArgumentNullException(nameof(responses));
            var predicate = isSingleWriter ?? IsStandardSingleWriter;
            var groups = new Dictionary<string, PropertyGroup>(StringComparer.Ordinal);
            var failures = new List<ExpressionEvaluationFailure>();
            for (int expressionIndex = 0; expressionIndex < responses.Count; expressionIndex++)
            {
                var response = responses[expressionIndex];
                if (response == null) continue;
                foreach (var failure in response.Failures)
                {
                    failures.Add(failure.ExpressionIndex == expressionIndex
                        ? failure
                        : failure.WithExpressionIndex(expressionIndex));
                    if (failure.Property != null) GetGroup(groups, failure.Property).Failed = true;
                }
                foreach (var record in response.Records)
                {
                    var identity = record?.Target?.Identity;
                    if (identity == null) continue;
                    GetGroup(groups, identity.CanonicalProperty).Contributions.Add(
                        new Contribution(expressionIndex, record));
                }
            }

            var properties = new List<string>(groups.Keys);
            properties.Sort(StringComparer.Ordinal);
            var records = new List<ExpressionAggregateRecord>(properties.Count);
            foreach (var property in properties)
            {
                var group = groups[property];
                // A failed sample fails its whole property; the evaluator already reported it.
                if (group.Failed || group.Contributions.Count == 0) continue;
                var record = ComposeProperty(property, group.Contributions, predicate, out string error);
                if (record != null)
                {
                    records.Add(record);
                    continue;
                }
                failures.Add(new ExpressionEvaluationFailure(property, -1, -1, -1, error));
                UnityEngine.Debug.LogError(
                    $"[KHR_character] Expression composition failed for '{property}': {error}; it receives no record.");
            }
            return new ExpressionComposition(records.ToArray(), failures.ToArray(), responses);
        }

        private static PropertyGroup GetGroup(Dictionary<string, PropertyGroup> groups, string property)
        {
            if (!groups.TryGetValue(property, out var group)) groups.Add(property, group = new PropertyGroup());
            return group;
        }

        private static ExpressionAggregateRecord ComposeProperty(
            string property,
            List<Contribution> contributions,
            Func<ExpressionResponseTarget, bool> isSingleWriter,
            out string error)
        {
            var first = contributions[0].Record.Target;
            bool singleWriter = false;
            foreach (var contribution in contributions)
            {
                var target = contribution.Record.Target;
                if (target.Kind != first.Kind || target.ArrayLength != first.ArrayLength)
                {
                    error = "its records disagree on the property's type or size";
                    return null;
                }
                singleWriter |= isSingleWriter(target);
            }

            if (first.ArrayLength > 0)
                return ComposeArray(property, first, singleWriter, contributions, out error);
            if (singleWriter)
                return ComposeSingleWriter(property, first, contributions, out error);
            return ComposeAdditive(property, first, contributions, out error);
        }

        private static ExpressionAggregateRecord ComposeSingleWriter(
            string property,
            ExpressionResponseTarget target,
            List<Contribution> contributions,
            out string error)
        {
            if (contributions.Count > 1)
            {
                error = $"expression entries {DescribeEntries(contributions)} each produced a record for this " +
                        "single-writer property, so the asset is invalid";
                return null;
            }
            var sample = contributions[0].Record.Value;
            var value = new double[sample.Count];
            var contributed = new bool[sample.Count];
            for (int component = 0; component < value.Length; component++)
            {
                value[component] = sample[component];
                contributed[component] = true;
            }
            error = null;
            return new ExpressionAggregateRecord(property, target.Kind, true, value, contributed);
        }

        private static ExpressionAggregateRecord ComposeAdditive(
            string property,
            ExpressionResponseTarget target,
            List<Contribution> contributions,
            out string error)
        {
            int componentCount = target.ComponentCount;
            var authored = target.AuthoredInitial;
            if (authored == null || authored.Count != componentCount)
            {
                error = "it has no authored initial value to add to";
                return null;
            }
            foreach (var contribution in contributions)
            {
                if (contribution.Record.Value.Count == componentCount &&
                    contribution.Record.NeutralValue.Count == componentCount)
                    continue;
                error = $"a record of expression entry {contribution.ExpressionIndex} has the wrong size";
                return null;
            }

            var operands = new double[1 + 2 * contributions.Count];
            var value = new double[componentCount];
            var contributed = new bool[componentCount];
            for (int component = 0; component < componentCount; component++)
            {
                int count = 0;
                operands[count++] = authored[component];
                foreach (var contribution in contributions)
                {
                    operands[count++] = contribution.Record.Value[component];
                    operands[count++] = -(double)contribution.Record.NeutralValue[component];
                }
                if (!ExactBinary64Sum.TrySum(operands, count, out value[component]))
                {
                    error = $"component {component} of the aggregate is not finite";
                    return null;
                }
                contributed[component] = true;
            }
            error = null;
            return new ExpressionAggregateRecord(property, target.Kind, false, value, contributed);
        }

        // Whole-array and element records of one array property fold into one complete vector over its base.
        private static ExpressionAggregateRecord ComposeArray(
            string property,
            ExpressionResponseTarget first,
            bool singleWriter,
            List<Contribution> contributions,
            out string error)
        {
            int length = first.ArrayLength;
            IReadOnlyList<double> authored = null;
            foreach (var contribution in contributions)
            {
                var target = contribution.Record.Target;
                var record = contribution.Record;
                bool element = target.Identity.Selection == ExpressionPropertySelection.ArrayElement;
                int size = element ? 1 : length;
                if ((element && target.Identity.ArrayElement >= length) ||
                    record.Value.Count != size || record.NeutralValue.Count != size)
                {
                    error = $"a record of expression entry {contribution.ExpressionIndex} has the wrong size";
                    return null;
                }
                var arrayAuthored = target.ArrayAuthoredInitial ?? (element ? null : target.AuthoredInitial);
                if (authored == null && arrayAuthored != null && arrayAuthored.Count == length)
                    authored = arrayAuthored;
            }
            if (authored == null)
            {
                error = "it has no authored initial value for the whole array";
                return null;
            }

            var operands = new double[1 + 2 * contributions.Count];
            var value = new double[length];
            var contributed = new bool[length];
            for (int element = 0; element < length; element++)
            {
                int count = 0;
                int writers = 0;
                double sample = 0d;
                operands[count++] = authored[element];
                foreach (var contribution in contributions)
                {
                    var identity = contribution.Record.Target.Identity;
                    int component = element;
                    if (identity.Selection == ExpressionPropertySelection.ArrayElement)
                    {
                        if (identity.ArrayElement != element) continue;
                        component = 0;
                    }
                    sample = contribution.Record.Value[component];
                    operands[count++] = sample;
                    operands[count++] = -(double)contribution.Record.NeutralValue[component];
                    writers++;
                }

                if (writers == 0)
                {
                    value[element] = authored[element];
                    continue;
                }
                contributed[element] = true;
                if (singleWriter)
                {
                    if (writers > 1)
                    {
                        error = $"element {element} has records from several expression entries " +
                                $"({DescribeEntries(contributions)}) but is single-writer, so the asset is invalid";
                        return null;
                    }
                    value[element] = sample;
                }
                else if (!ExactBinary64Sum.TrySum(operands, count, out value[element]))
                {
                    error = $"element {element} of the aggregate is not finite";
                    return null;
                }
            }
            error = null;
            return new ExpressionAggregateRecord(property, first.Kind, singleWriter, value, contributed);
        }

        private static string DescribeEntries(List<Contribution> contributions)
        {
            var indices = new List<int>();
            foreach (var contribution in contributions)
                if (!indices.Contains(contribution.ExpressionIndex)) indices.Add(contribution.ExpressionIndex);
            indices.Sort();
            return string.Join(", ", indices);
        }
    }
}
