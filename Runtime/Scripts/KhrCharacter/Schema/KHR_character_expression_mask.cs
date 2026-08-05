using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// <c>KHR_character_expression_mask</c> sub-extension: how the parent expression masks other expressions.
    /// <c>blend</c> reduces the target proportionally; <c>block</c> fully reduces past a threshold.
    /// </summary>
    public class KHR_character_expression_mask : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_expression_mask";

        public List<Mask> Masks = new List<Mask>();
        public JObject Extensions;
        public JToken Extras;
        public JObject AdditionalProperties;
        public JProperty RawData;

        public class Mask
        {
            public int Target = -1;          // required expression index
            public string Name;              // optional exact label of the target expression
            public string Type = "blend";    // "blend" | "block" | vendor-qualified custom token
            public float Amount = 1.0f;       // [0..1]
            public float Threshold = 0.0f;    // [0..1], block only
            public JObject Extensions;        // preserved companion and unrelated extension payloads
            public JToken Extras;
            public JObject AdditionalProperties;
        }

        public static KHR_character_expression_mask FromJson(JObject obj)
        {
            var ext = new KHR_character_expression_mask();
            if (obj != null)
            {
                ext.Extensions = obj["extensions"] is JObject extensions
                    ? (JObject)extensions.DeepClone()
                    : null;
                ext.Extras = obj["extras"]?.DeepClone();
                ext.AdditionalProperties = GetAdditionalProperties(
                    obj, "masks", "extensions", "extras");
            }
            if (obj?["masks"] is JArray arr)
            {
                foreach (var node in arr)
                {
                    if (!(node is JObject m)) continue;
                    ext.Masks.Add(new Mask
                    {
                        Target = m["target"]?.Value<int>() ?? -1,
                        Name = m["name"]?.Value<string>(),
                        Type = m["type"]?.Value<string>() ?? "blend",
                        Amount = m["amount"]?.Value<float>() ?? 1.0f,
                        Threshold = m["threshold"]?.Value<float>() ?? 0.0f,
                        Extensions = m["extensions"] is JObject maskExtensions
                            ? (JObject)maskExtensions.DeepClone()
                            : null,
                        Extras = m["extras"]?.DeepClone(),
                        AdditionalProperties = GetAdditionalProperties(
                            m, "target", "name", "type", "amount", "threshold", "extensions", "extras"),
                    });
                }
            }
            return ext;
        }

        public JProperty Serialize()
        {
            var arr = new JArray();
            if (Masks != null)
            {
                foreach (var m in Masks)
                {
                    if (m == null) continue;
                    var mo = m.AdditionalProperties != null
                        ? (JObject)m.AdditionalProperties.DeepClone()
                        : new JObject();
                    if (m.Target >= 0) mo["target"] = m.Target;
                    else mo.Remove("target");
                    if (m.Name != null) mo["name"] = m.Name;
                    else mo.Remove("name");
                    if (m.Type != null) mo["type"] = m.Type;
                    else mo.Remove("type");
                    mo["amount"] = m.Amount;
                    mo["threshold"] = m.Threshold;
                    if (m.Extensions != null && m.Extensions.HasValues)
                        mo["extensions"] = m.Extensions.DeepClone();
                    else mo.Remove("extensions");
                    if (m.Extras != null) mo["extras"] = m.Extras.DeepClone();
                    else mo.Remove("extras");
                    arr.Add(mo);
                }
            }
            var value = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : new JObject();
            value["masks"] = arr;
            if (Extensions != null && Extensions.HasValues) value["extensions"] = Extensions.DeepClone();
            else value.Remove("extensions");
            if (Extras != null) value["extras"] = Extras.DeepClone();
            else value.Remove("extras");
            return new JProperty(EXTENSION_NAME, value);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_expression_mask
        {
            Masks = CloneMasks(),
            Extensions = Extensions != null ? (JObject)Extensions.DeepClone() : null,
            Extras = Extras?.DeepClone(),
            AdditionalProperties = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : null,
            RawData = RawData != null ? new JProperty(RawData) : null,
        };

        private List<Mask> CloneMasks()
        {
            var clone = new List<Mask>();
            if (Masks == null) return clone;
            foreach (var mask in Masks)
            {
                if (mask == null)
                {
                    clone.Add(null);
                    continue;
                }
                clone.Add(new Mask
                {
                    Target = mask.Target,
                    Name = mask.Name,
                    Type = mask.Type,
                    Amount = mask.Amount,
                    Threshold = mask.Threshold,
                    Extensions = mask.Extensions != null ? (JObject)mask.Extensions.DeepClone() : null,
                    Extras = mask.Extras?.DeepClone(),
                    AdditionalProperties = mask.AdditionalProperties != null
                        ? (JObject)mask.AdditionalProperties.DeepClone()
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

    public class KHR_character_expression_mask_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression_mask.EXTENSION_NAME;
        public KHR_character_expression_mask_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            RequiresRuntimeSupportForRequiredUse = true;
        }
        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var e = KHR_character_expression_mask.FromJson(token.Value as JObject); e.RawData = token; return e;
        }
    }
}
