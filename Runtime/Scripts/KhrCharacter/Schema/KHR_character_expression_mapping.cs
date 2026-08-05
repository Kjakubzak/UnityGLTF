using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF root extension <c>KHR_character_expression_mapping</c>. The forward and input directions are
    /// separately authored operations and are never inferred from one another.
    /// </summary>
    public class KHR_character_expression_mapping : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_expression_mapping";

        public struct SourceWeight
        {
            public int Source;
            public string Name;
            public float Weight;
        }

        public struct TargetWeight
        {
            public int Target;
            public string Name;
            public float Weight;
        }

        // setName -> (targetExpression -> contributions)
        public Dictionary<string, Dictionary<string, List<SourceWeight>>> ExpressionSetMappings
            = new Dictionary<string, Dictionary<string, List<SourceWeight>>>();

        // set identifier -> (endpoint command -> native-expression targets)
        public Dictionary<string, Dictionary<string, List<TargetWeight>>> ExpressionSetInputMappings
            = new Dictionary<string, Dictionary<string, List<TargetWeight>>>();

        public JProperty RawData;

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);

            var value = new JObject();
            if (ExpressionSetMappings != null && ExpressionSetMappings.Count > 0)
                value.Add("expressionSetMappings", SerializeForwardSets(ExpressionSetMappings));
            if (ExpressionSetInputMappings != null && ExpressionSetInputMappings.Count > 0)
                value.Add("expressionSetInputMappings", SerializeInputSets(ExpressionSetInputMappings));
            return new JProperty(EXTENSION_NAME, value);
        }

        private static JObject SerializeForwardSets(
            Dictionary<string, Dictionary<string, List<SourceWeight>>> mappings)
        {
            var sets = new JObject();
            foreach (var setKv in mappings)
            {
                var endpoints = new JObject();
                if (setKv.Value != null)
                    foreach (var endpointKv in setKv.Value)
                    {
                        var contributions = new JArray();
                        if (endpointKv.Value != null)
                            foreach (var entry in endpointKv.Value)
                            {
                                var contribution = new JObject { { "source", entry.Source } };
                                if (entry.Name != null) contribution.Add("name", entry.Name);
                                contribution.Add("weight", entry.Weight);
                                contributions.Add(contribution);
                            }
                        endpoints.Add(endpointKv.Key, contributions);
                    }
                sets.Add(setKv.Key, endpoints);
            }
            return sets;
        }

        private static JObject SerializeInputSets(
            Dictionary<string, Dictionary<string, List<TargetWeight>>> mappings)
        {
            var sets = new JObject();
            foreach (var setKv in mappings)
            {
                var endpoints = new JObject();
                if (setKv.Value != null)
                    foreach (var endpointKv in setKv.Value)
                    {
                        var contributions = new JArray();
                        if (endpointKv.Value != null)
                            foreach (var entry in endpointKv.Value)
                            {
                                var contribution = new JObject { { "target", entry.Target } };
                                if (entry.Name != null) contribution.Add("name", entry.Name);
                                contribution.Add("weight", entry.Weight);
                                contributions.Add(contribution);
                            }
                        endpoints.Add(endpointKv.Key, contributions);
                    }
                sets.Add(setKv.Key, endpoints);
            }
            return sets;
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_expression_mapping
        {
            ExpressionSetMappings = ExpressionSetMappings,
            ExpressionSetInputMappings = ExpressionSetInputMappings,
            RawData = RawData != null ? new JProperty(RawData) : null
        };
    }

    public class KHR_character_expression_mapping_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression_mapping.EXTENSION_NAME;
        public KHR_character_expression_mapping_Factory() { ExtensionName = EXTENSION_NAME; }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_character_expression_mapping { RawData = token };
            if (!(token.Value is JObject obj)) return ext;

            if (obj["expressionSetMappings"] is JObject forwardSets)
            {
                foreach (var setProp in forwardSets.Properties())
                {
                    if (!(setProp.Value is JObject endpoints)) continue;
                    var endpointMap = new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>();
                    foreach (var endpointProp in endpoints.Properties())
                    {
                        if (!(endpointProp.Value is JArray contributions)) continue;
                        var list = new List<KHR_character_expression_mapping.SourceWeight>();
                        foreach (var node in contributions)
                        {
                            if (!(node is JObject c)) continue;
                            list.Add(new KHR_character_expression_mapping.SourceWeight
                            {
                                Source = c["source"]?.Value<int>() ?? -1,
                                Name = c["name"]?.Value<string>(),
                                Weight = c["weight"]?.Value<float>() ?? 0f,
                            });
                        }
                        endpointMap[endpointProp.Name] = list;
                    }
                    ext.ExpressionSetMappings[setProp.Name] = endpointMap;
                }
            }

            if (obj["expressionSetInputMappings"] is JObject inputSets)
                foreach (var setProp in inputSets.Properties())
                {
                    if (!(setProp.Value is JObject endpoints)) continue;
                    var endpointMap = new Dictionary<string, List<KHR_character_expression_mapping.TargetWeight>>();
                    foreach (var endpointProp in endpoints.Properties())
                    {
                        if (!(endpointProp.Value is JArray contributions)) continue;
                        var list = new List<KHR_character_expression_mapping.TargetWeight>();
                        foreach (var node in contributions)
                        {
                            if (!(node is JObject c)) continue;
                            list.Add(new KHR_character_expression_mapping.TargetWeight
                            {
                                Target = c["target"]?.Value<int>() ?? -1,
                                Name = c["name"]?.Value<string>(),
                                Weight = c["weight"]?.Value<float>() ?? 0f,
                            });
                        }
                        endpointMap[endpointProp.Name] = list;
                    }
                    ext.ExpressionSetInputMappings[setProp.Name] = endpointMap;
                }
            return ext;
        }
    }
}
