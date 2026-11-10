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

        /// <summary>True for a Boolean or integer value that this host selects from the highest active
        /// expression-array index; retained name for API compatibility. False for additive components, node and
        /// texture scale (additive) and rotations (approximately additive).</summary>
        public bool IsSingleWriter { get; }

        public bool IsHostComposed => IsSingleWriter;

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
    /// entry for one driver snapshot independent of order. Additive components use <c>y = b + sum(s - n)</c> with
    /// exact binary64 accumulation, and morph weights become one complete vector per node. Node <c>scale</c> and
    /// <c>KHR_texture_transform</c> <c>scale</c> use the same additive formula, as recommended by
    /// KHR_character_expression_joint and KHR_character_expression_texture_transform. Rotations (quaternions) compose
    /// approximately additively: <c>y = normalize(b * exp(sum(log(n^-1 * s))))</c>, see
    /// <see cref="ComposeRotation"/>. Boolean and integer properties select the absolute sample of the highest
    /// active expression-array index.
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
        /// Legacy classifier name for the properties this host composes by selecting the absolute sample of the
        /// highest active expression-array index: Boolean and integer values. It is not a structural single-writer
        /// rule. Node <c>scale</c>, <c>KHR_texture_transform</c> <c>scale</c> and rotations are no longer selected
        /// this way; they compose additively and approximately additively.
        /// </summary>
        public static bool IsStandardSingleWriter(ExpressionResponseTarget target)
        {
            if (target == null) return false;
            return target.ValueKind == ExpressionResponseValueKind.Boolean
                || target.ValueKind == ExpressionResponseValueKind.Integer;
        }

        /// <summary>True for a rotation, which this host composes with <see cref="ComposeRotation"/>.</summary>
        public static bool IsRotation(ExpressionResponseTarget target)
            => target != null && (target.ValueKind == ExpressionResponseValueKind.Quaternion
                                  || target.Kind == ExpressionPropertyKind.NodeRotation);

        /// <summary>
        /// Compatibility classifier for host-composed property types. Historical declarations cannot override
        /// mandatory additive rules.
        /// </summary>
        public static Func<ExpressionResponseTarget, bool> SingleWriterPredicate(
            IEnumerable<ExpressionDerivedExtension> carriedExtensions)
        {
            // Historical vendor single-writer declarations cannot disable required additive aggregation.
            return IsStandardSingleWriter;
        }

        /// <summary>True for properties using this host's highest-active-index selection (Boolean and integer values),
        /// not a validity gate.</summary>
        public static bool IsHostComposed(ExpressionResponseTarget target) => IsStandardSingleWriter(target);

        /// <summary>Composes the responses of all entries for one driver snapshot.</summary>
        /// <param name="responses">The response of every expression entry, by expression index.</param>
        /// <param name="isSingleWriter">Retained compatibility parameter; current type rules always determine the mandatory additive boundary.</param>
        public static ExpressionComposition Compose(
            IReadOnlyList<ExpressionResponse> responses,
            Func<ExpressionResponseTarget, bool> isSingleWriter = null)
        {
            if (responses == null) throw new ArgumentNullException(nameof(responses));
            var predicate = (Func<ExpressionResponseTarget, bool>)IsHostComposed;
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
                if (target.Kind != first.Kind || target.ArrayLength != first.ArrayLength || target.ValueKind != first.ValueKind)
                {
                    error = "its records disagree on the property's type or size";
                    return null;
                }
                singleWriter |= isSingleWriter(target);
            }

            if (first.ArrayLength > 0)
                return ComposeArray(property, first, singleWriter, contributions, out error);
            if (IsRotation(first))
                return ComposeRotation(property, first, contributions, out error);
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
            // Contributions were gathered in serialized expression-array order. Inactive entries emitted no record.
            var sample = contributions[contributions.Count - 1].Record.Value;
            if (sample.Count != target.ComponentCount)
            {
                error = "the host-selected absolute sample has the wrong size";
                return null;
            }
            var value = new double[sample.Count];
            var contributed = new bool[sample.Count];
            for (int component = 0; component < value.Length; component++)
            {
                value[component] = sample[component];
                if (!ValidHostComponent(value[component], target.ValueKind))
                {
                    error = "the host-selected sample violates its finite or discrete value requirements";
                    return null;
                }
                contributed[component] = true;
            }
            error = null;
            return new ExpressionAggregateRecord(property, target.Kind, true, value, contributed);
        }

        /// <summary>
        /// Approximately additive rotation composition, this host's choice for the KHR_character_expression_joint
        /// recommendation (the specification prescribes no algorithm). Each record contributes its change from its
        /// own time-zero sample as a rotation vector, <c>r = log(n^-1 * s)</c>, taken on the shorter arc (the
        /// relative quaternion is flipped to <c>w &gt;= 0</c>, so each change is at most half a turn). The rotation
        /// vectors are summed per component exactly in binary64, and the aggregate is
        /// <c>y = normalize(b * exp(sum(r)))</c>, with <c>b</c> the authored initial rotation. Quaternions are glTF
        /// <c>[x, y, z, w]</c> and every product is a local (right) composition. Properties:
        /// the result does not depend on record order; changes about one shared axis add exactly (10 + 10 degrees
        /// is 20 degrees); a single record whose neutral sample equals <c>b</c> reproduces its sample; small changes
        /// about different axes combine approximately additively (10 degrees right and 10 degrees up give about 10
        /// degrees right and 10 degrees up, as one 14.1-degree rotation about the diagonal axis). The result sign is
        /// whatever this product gives; it is a unit quaternion either way.
        /// </summary>
        private static ExpressionAggregateRecord ComposeRotation(
            string property,
            ExpressionResponseTarget target,
            List<Contribution> contributions,
            out string error)
        {
            var authored = target.AuthoredInitial;
            if (target.ComponentCount != 4 || authored == null || authored.Count != 4)
            {
                error = "it has no authored initial rotation to compose onto";
                return null;
            }
            if (!TryNormalize(authored[0], authored[1], authored[2], authored[3], out var b))
            {
                error = "the authored initial rotation cannot be normalized";
                return null;
            }

            var x = new double[contributions.Count];
            var y = new double[contributions.Count];
            var z = new double[contributions.Count];
            for (int index = 0; index < contributions.Count; index++)
            {
                var record = contributions[index].Record;
                if (record.Value.Count != 4 || record.NeutralValue.Count != 4)
                {
                    error = $"a record of expression entry {contributions[index].ExpressionIndex} has the wrong size";
                    return null;
                }
                if (!TryNormalize(record.Value[0], record.Value[1], record.Value[2], record.Value[3], out var s) ||
                    !TryNormalize(record.NeutralValue[0], record.NeutralValue[1], record.NeutralValue[2],
                        record.NeutralValue[3], out var n))
                {
                    error = $"a rotation of expression entry {contributions[index].ExpressionIndex} cannot be normalized";
                    return null;
                }
                var change = Multiply(Conjugate(n), s);
                var vector = Log(change);
                x[index] = vector[0];
                y[index] = vector[1];
                z[index] = vector[2];
            }

            if (!ExactBinary64Sum.TrySum(x, x.Length, out double sumX) ||
                !ExactBinary64Sum.TrySum(y, y.Length, out double sumY) ||
                !ExactBinary64Sum.TrySum(z, z.Length, out double sumZ))
            {
                error = "the summed rotation change is not finite";
                return null;
            }
            var composed = Multiply(b, Exp(sumX, sumY, sumZ));
            if (!TryNormalize(composed[0], composed[1], composed[2], composed[3], out var value))
            {
                error = "the composed rotation cannot be normalized";
                return null;
            }
            error = null;
            return new ExpressionAggregateRecord(
                property, target.Kind, false, value, new[] { true, true, true, true });
        }

        private static bool TryNormalize(double x, double y, double z, double w, out double[] q)
        {
            q = null;
            double normSquared = x * x + y * y + z * z + w * w;
            if (!(normSquared > 0d) || double.IsInfinity(normSquared)) return false;
            double norm = Math.Sqrt(normSquared);
            q = new[] { x / norm, y / norm, z / norm, w / norm };
            return true;
        }

        private static double[] Conjugate(double[] q) => new[] { -q[0], -q[1], -q[2], q[3] };

        // Hamilton product a * b of [x, y, z, w] quaternions.
        private static double[] Multiply(double[] a, double[] b) => new[]
        {
            a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
            a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
            a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
        };

        // Rotation vector (axis times full angle) of a unit quaternion, on the shorter arc.
        private static double[] Log(double[] q)
        {
            double sign = q[3] < 0d ? -1d : 1d;
            double vx = sign * q[0], vy = sign * q[1], vz = sign * q[2], w = sign * q[3];
            double sine = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            if (sine == 0d) return new[] { 0d, 0d, 0d };
            double angle = 2d * Math.Atan2(sine, w);
            double scale = angle / sine;
            return new[] { vx * scale, vy * scale, vz * scale };
        }

        // Unit quaternion of a rotation vector (axis times full angle).
        private static double[] Exp(double x, double y, double z)
        {
            double angle = Math.Sqrt(x * x + y * y + z * z);
            if (angle == 0d) return new[] { 0d, 0d, 0d, 1d };
            double half = 0.5d * angle;
            double scale = Math.Sin(half) / angle;
            return new[] { x * scale, y * scale, z * scale, Math.Cos(half) };
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
                    if (!ValidHostComponent(sample, first.ValueKind))
                    {
                        error = "the host-selected array sample violates its value requirements";
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

        private static bool ValidHostComponent(double value, ExpressionResponseValueKind kind)
            => !double.IsNaN(value) && !double.IsInfinity(value)
               && (kind != ExpressionResponseValueKind.Boolean || value == 0d || value == 1d)
               && (kind != ExpressionResponseValueKind.Integer || value == Math.Truncate(value));

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
