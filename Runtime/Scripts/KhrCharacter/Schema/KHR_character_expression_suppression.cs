using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// <c>KHR_character_expression_suppression</c> extension of a <c>KHR_character_expression</c> entry: how the
    /// containing (source) expression attenuates the driver of other expressions. <c>blend</c> reduces the target
    /// proportionally; <c>block</c> attenuates it past a strict threshold. Authored <c>amount</c> and
    /// <c>threshold</c> values keep their presence and exact JSON doubles so a round trip re-emits what was
    /// authored.
    /// </summary>
    public class KHR_character_expression_suppression : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_expression_suppression";

        public List<Suppression> Suppressions = new List<Suppression>();
        public JObject Extensions;
        public JToken Extras;
        public JObject AdditionalProperties;
        public JProperty RawData;

        /// <summary>One suppression object. Its source is the expression entry that contains the extension.</summary>
        public class Suppression
        {
            /// <summary>Required index of the attenuated expression; -1 when absent.</summary>
            public int Target = -1;

            /// <summary><c>"blend"</c>, <c>"block"</c> or a vendor-qualified custom token; null when omitted (blend).</summary>
            public string Type;

            /// <summary>Authored amount, or null when omitted (reads as 1).</summary>
            public double? Amount;

            /// <summary>Authored threshold, or null when omitted (reads as 0).</summary>
            public double? Threshold;

            public JObject Extensions;        // preserved companion and unrelated extension payloads
            public JToken Extras;

            /// <summary>Members this extension does not define, such as a legacy <c>name</c>, preserved verbatim.</summary>
            public JObject AdditionalProperties;
        }

        /// <summary>
        /// Whether a <c>threshold</c> may be written for a suppression of this type. Exporters must not write it
        /// on a <c>blend</c> or omitted type; <c>block</c> and custom tokens keep it.
        /// </summary>
        public static bool AllowsThreshold(string type)
            => !string.IsNullOrEmpty(type) && type != "blend";

        public static KHR_character_expression_suppression FromJson(JObject obj)
        {
            var ext = new KHR_character_expression_suppression();
            if (obj != null)
            {
                ext.Extensions = obj["extensions"] is JObject extensions
                    ? (JObject)extensions.DeepClone()
                    : null;
                ext.Extras = obj["extras"]?.DeepClone();
                ext.AdditionalProperties = GetAdditionalProperties(
                    obj, "suppressions", "extensions", "extras");
            }
            if (obj?["suppressions"] is JArray arr)
            {
                foreach (var node in arr)
                {
                    if (!(node is JObject s)) continue;
                    ext.Suppressions.Add(new Suppression
                    {
                        Target = s["target"]?.Value<int>() ?? -1,
                        Type = s["type"]?.Value<string>(),
                        Amount = s["amount"]?.Value<double>(),
                        Threshold = s["threshold"]?.Value<double>(),
                        Extensions = s["extensions"] is JObject suppressionExtensions
                            ? (JObject)suppressionExtensions.DeepClone()
                            : null,
                        Extras = s["extras"]?.DeepClone(),
                        AdditionalProperties = GetAdditionalProperties(
                            s, "target", "type", "amount", "threshold", "extensions", "extras"),
                    });
                }
            }
            return ext;
        }

        public JProperty Serialize()
        {
            var arr = new JArray();
            if (Suppressions != null)
            {
                foreach (var s in Suppressions)
                {
                    if (s == null) continue;
                    var so = s.AdditionalProperties != null
                        ? (JObject)s.AdditionalProperties.DeepClone()
                        : new JObject();
                    if (s.Target >= 0) so["target"] = s.Target;
                    else so.Remove("target");
                    if (s.Type != null) so["type"] = s.Type;
                    else so.Remove("type");
                    if (s.Amount.HasValue) so["amount"] = s.Amount.Value;
                    else so.Remove("amount");
                    if (s.Threshold.HasValue && AllowsThreshold(s.Type)) so["threshold"] = s.Threshold.Value;
                    else so.Remove("threshold");
                    if (s.Extensions != null && s.Extensions.HasValues)
                        so["extensions"] = s.Extensions.DeepClone();
                    else so.Remove("extensions");
                    if (s.Extras != null) so["extras"] = s.Extras.DeepClone();
                    else so.Remove("extras");
                    arr.Add(so);
                }
            }
            var value = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : new JObject();
            value["suppressions"] = arr;
            if (Extensions != null && Extensions.HasValues) value["extensions"] = Extensions.DeepClone();
            else value.Remove("extensions");
            if (Extras != null) value["extras"] = Extras.DeepClone();
            else value.Remove("extras");
            return new JProperty(EXTENSION_NAME, value);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_expression_suppression
        {
            Suppressions = CloneSuppressions(),
            Extensions = Extensions != null ? (JObject)Extensions.DeepClone() : null,
            Extras = Extras?.DeepClone(),
            AdditionalProperties = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : null,
            RawData = RawData != null ? new JProperty(RawData) : null,
        };

        private List<Suppression> CloneSuppressions()
        {
            var clone = new List<Suppression>();
            if (Suppressions == null) return clone;
            foreach (var suppression in Suppressions)
            {
                if (suppression == null)
                {
                    clone.Add(null);
                    continue;
                }
                clone.Add(new Suppression
                {
                    Target = suppression.Target,
                    Type = suppression.Type,
                    Amount = suppression.Amount,
                    Threshold = suppression.Threshold,
                    Extensions = suppression.Extensions != null ? (JObject)suppression.Extensions.DeepClone() : null,
                    Extras = suppression.Extras?.DeepClone(),
                    AdditionalProperties = suppression.AdditionalProperties != null
                        ? (JObject)suppression.AdditionalProperties.DeepClone()
                        : null,
                });
            }
            return clone;
        }

        private static JObject GetAdditionalProperties(JObject obj, params string[] knownNames)
        {
            var known = new HashSet<string>(knownNames);
            var additional = new JObject();
            foreach (var property in obj.Properties())
                if (!known.Contains(property.Name))
                    additional.Add(property.Name, property.Value.DeepClone());
            return additional.HasValues ? additional : null;
        }
    }

    public class KHR_character_expression_suppression_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression_suppression.EXTENSION_NAME;
        public KHR_character_expression_suppression_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            RequiresRuntimeSupportForRequiredUse = true;
        }
        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var e = KHR_character_expression_suppression.FromJson(token.Value as JObject); e.RawData = token; return e;
        }
    }
}
