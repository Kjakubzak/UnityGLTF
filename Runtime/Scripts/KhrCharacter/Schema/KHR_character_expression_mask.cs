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
        public JProperty RawData;

        public class Mask
        {
            public int Target = -1;          // required expression index
            public string Type = "blend";    // "blend" | "block" | (any string -> treat as blend)
            public float Amount = 1.0f;       // [0..1]
            public float Threshold = 0.0f;    // [0..1], block only
        }

        public static KHR_character_expression_mask FromJson(JObject obj)
        {
            var ext = new KHR_character_expression_mask();
            if (obj?["masks"] is JArray arr)
            {
                foreach (var node in arr)
                {
                    if (!(node is JObject m)) continue;
                    ext.Masks.Add(new Mask
                    {
                        Target = m["target"]?.Value<int>() ?? -1,
                        Type = m["type"]?.Value<string>() ?? "blend",
                        Amount = m["amount"]?.Value<float>() ?? 1.0f,
                        Threshold = m["threshold"]?.Value<float>() ?? 0.0f,
                    });
                }
            }
            return ext;
        }

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);

            var arr = new JArray();
            if (Masks != null)
            {
                foreach (var m in Masks)
                {
                    if (m == null) continue;
                    var mo = new JObject();
                    if (m.Target >= 0) mo.Add("target", m.Target);
                    if (m.Type != null) mo.Add("type", m.Type);
                    mo.Add("amount", m.Amount);
                    mo.Add("threshold", m.Threshold);
                    arr.Add(mo);
                }
            }
            return new JProperty(EXTENSION_NAME, new JObject { { "masks", arr } });
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_expression_mask
        { Masks = Masks, RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_character_expression_mask_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression_mask.EXTENSION_NAME;
        public KHR_character_expression_mask_Factory() { ExtensionName = EXTENSION_NAME; }
        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var e = KHR_character_expression_mask.FromJson(token.Value as JObject); e.RawData = token; return e;
        }
    }
}
