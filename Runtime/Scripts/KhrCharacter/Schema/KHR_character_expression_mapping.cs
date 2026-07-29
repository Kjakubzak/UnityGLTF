using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF root extension <c>KHR_character_expression_mapping</c>: normalizes a common expression vocabulary
    /// to the model's own expressions. Shape: setName -> { targetExpression -> [ {source, weight} ] }.
    /// Weights are unbounded per spec (clamping is a runtime policy, applied after masking).
    /// </summary>
    public class KHR_character_expression_mapping : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_expression_mapping";

        public struct SourceWeight { public int Source; public float Weight; }

        // setName -> (targetExpression -> contributions)
        public Dictionary<string, Dictionary<string, List<SourceWeight>>> ExpressionSetMappings
            = new Dictionary<string, Dictionary<string, List<SourceWeight>>>();

        public JProperty RawData;

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);

            var sets = new JObject();
            if (ExpressionSetMappings != null)
            {
                foreach (var setKv in ExpressionSetMappings)
                {
                    var targets = new JObject();
                    if (setKv.Value != null)
                    {
                        foreach (var targetKv in setKv.Value)
                        {
                            var contributions = new JArray();
                            if (targetKv.Value != null)
                                foreach (var sw in targetKv.Value)
                                    contributions.Add(new JObject { { "source", sw.Source }, { "weight", sw.Weight } });
                            targets.Add(targetKv.Key, contributions);
                        }
                    }
                    sets.Add(setKv.Key, targets);
                }
            }
            return new JProperty(EXTENSION_NAME, new JObject { { "expressionSetMappings", sets } });
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_expression_mapping
        { ExpressionSetMappings = ExpressionSetMappings, RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_character_expression_mapping_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression_mapping.EXTENSION_NAME;
        public KHR_character_expression_mapping_Factory() { ExtensionName = EXTENSION_NAME; }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_character_expression_mapping { RawData = token };
            if (!(token.Value is JObject obj) || !(obj["expressionSetMappings"] is JObject sets))
                return ext;

            foreach (var setProp in sets.Properties())
            {
                if (!(setProp.Value is JObject targets)) continue;
                var targetMap = new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>();
                foreach (var targetProp in targets.Properties())
                {
                    if (!(targetProp.Value is JArray contributions)) continue;
                    var list = new List<KHR_character_expression_mapping.SourceWeight>();
                    foreach (var node in contributions)
                    {
                        if (!(node is JObject c)) continue;
                        list.Add(new KHR_character_expression_mapping.SourceWeight
                        {
                            Source = c["source"]?.Value<int>() ?? -1,
                            Weight = c["weight"]?.Value<float>() ?? 0f,
                        });
                    }
                    targetMap[targetProp.Name] = list;
                }
                ext.ExpressionSetMappings[setProp.Name] = targetMap;
            }
            return ext;
        }
    }
}
