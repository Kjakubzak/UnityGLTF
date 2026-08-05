using System;
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
            public JObject Extensions;
            public JToken Extras;
            public JObject AdditionalProperties;
        }

        public struct TargetWeight
        {
            public int Target;
            public string Name;
            public float Weight;
            public JObject Extensions;
            public JToken Extras;
            public JObject AdditionalProperties;
        }

        // setName -> (targetExpression -> contributions)
        public Dictionary<string, Dictionary<string, List<SourceWeight>>> ExpressionSetMappings
            = new Dictionary<string, Dictionary<string, List<SourceWeight>>>();

        // set identifier -> (endpoint command -> native-expression targets)
        public Dictionary<string, Dictionary<string, List<TargetWeight>>> ExpressionSetInputMappings
            = new Dictionary<string, Dictionary<string, List<TargetWeight>>>();

        public JObject Extensions;
        public JToken Extras;
        public JObject AdditionalProperties;
        public JProperty RawData;

        /// <summary>
        /// Returns whether a mapping-set identifier is a well-formed absolute URI. The original identifier remains
        /// authoritative and is never normalized or retrieved by this check.
        /// </summary>
        public static bool IsValidMappingSetIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier)) return false;

            int colon = identifier.IndexOf(':');
            if (colon <= 0 || !IsAsciiLetter(identifier[0])) return false;
            for (int i = 1; i < colon; i++)
            {
                char c = identifier[i];
                if (!IsAsciiLetter(c) && !char.IsDigit(c) && c != '+' && c != '-' && c != '.')
                    return false;
            }

            for (int i = 0; i < identifier.Length; i++)
            {
                char c = identifier[i];
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') return false;
            }

            return Uri.TryCreate(identifier, UriKind.Absolute, out var uri)
                && uri.IsAbsoluteUri
                && Uri.IsWellFormedUriString(identifier, UriKind.Absolute);
        }

        private static bool IsAsciiLetter(char value)
            => (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

        public JProperty Serialize()
        {
            var value = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : new JObject();
            if (ExpressionSetMappings != null && ExpressionSetMappings.Count > 0)
            {
                var mappings = SerializeForwardSets(ExpressionSetMappings);
                if (mappings.HasValues) value["expressionSetMappings"] = mappings;
                else value.Remove("expressionSetMappings");
            }
            else value.Remove("expressionSetMappings");
            if (ExpressionSetInputMappings != null && ExpressionSetInputMappings.Count > 0)
            {
                var mappings = SerializeInputSets(ExpressionSetInputMappings);
                if (mappings.HasValues) value["expressionSetInputMappings"] = mappings;
                else value.Remove("expressionSetInputMappings");
            }
            else value.Remove("expressionSetInputMappings");
            if (Extensions != null && Extensions.HasValues) value["extensions"] = Extensions.DeepClone();
            else value.Remove("extensions");
            if (Extras != null) value["extras"] = Extras.DeepClone();
            else value.Remove("extras");
            return new JProperty(EXTENSION_NAME, value);
        }

        private static JObject SerializeForwardSets(
            Dictionary<string, Dictionary<string, List<SourceWeight>>> mappings)
        {
            var sets = new JObject();
            foreach (var setKv in mappings)
            {
                if (!IsValidMappingSetIdentifier(setKv.Key))
                    throw new InvalidOperationException(
                        $"{EXTENSION_NAME} mapping-set identifier '{setKv.Key}' is not a valid absolute URI.");
                var endpoints = new JObject();
                if (setKv.Value != null)
                    foreach (var endpointKv in setKv.Value)
                    {
                        var contributions = new JArray();
                        if (endpointKv.Value != null)
                            foreach (var entry in endpointKv.Value)
                            {
                                var contribution = entry.AdditionalProperties != null
                                    ? (JObject)entry.AdditionalProperties.DeepClone()
                                    : new JObject();
                                contribution["source"] = entry.Source;
                                if (entry.Name != null) contribution["name"] = entry.Name;
                                else contribution.Remove("name");
                                contribution["weight"] = entry.Weight;
                                if (entry.Extensions != null && entry.Extensions.HasValues)
                                    contribution["extensions"] = entry.Extensions.DeepClone();
                                else contribution.Remove("extensions");
                                if (entry.Extras != null) contribution["extras"] = entry.Extras.DeepClone();
                                else contribution.Remove("extras");
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
                if (!IsValidMappingSetIdentifier(setKv.Key))
                    throw new InvalidOperationException(
                        $"{EXTENSION_NAME} mapping-set identifier '{setKv.Key}' is not a valid absolute URI.");
                var endpoints = new JObject();
                if (setKv.Value != null)
                    foreach (var endpointKv in setKv.Value)
                    {
                        var contributions = new JArray();
                        if (endpointKv.Value != null)
                            foreach (var entry in endpointKv.Value)
                            {
                                var contribution = entry.AdditionalProperties != null
                                    ? (JObject)entry.AdditionalProperties.DeepClone()
                                    : new JObject();
                                contribution["target"] = entry.Target;
                                if (entry.Name != null) contribution["name"] = entry.Name;
                                else contribution.Remove("name");
                                contribution["weight"] = entry.Weight;
                                if (entry.Extensions != null && entry.Extensions.HasValues)
                                    contribution["extensions"] = entry.Extensions.DeepClone();
                                else contribution.Remove("extensions");
                                if (entry.Extras != null) contribution["extras"] = entry.Extras.DeepClone();
                                else contribution.Remove("extras");
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
            ExpressionSetMappings = CloneForwardMappings(),
            ExpressionSetInputMappings = CloneInputMappings(),
            Extensions = Extensions != null ? (JObject)Extensions.DeepClone() : null,
            Extras = Extras?.DeepClone(),
            AdditionalProperties = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : null,
            RawData = RawData != null ? new JProperty(RawData) : null
        };

        private Dictionary<string, Dictionary<string, List<SourceWeight>>> CloneForwardMappings()
        {
            var clone = new Dictionary<string, Dictionary<string, List<SourceWeight>>>();
            if (ExpressionSetMappings == null) return clone;
            foreach (var setKv in ExpressionSetMappings)
            {
                if (setKv.Value == null)
                {
                    clone[setKv.Key] = null;
                    continue;
                }
                var endpoints = new Dictionary<string, List<SourceWeight>>();
                foreach (var endpointKv in setKv.Value)
                {
                    if (endpointKv.Value == null)
                    {
                        endpoints[endpointKv.Key] = null;
                        continue;
                    }
                    var contributions = new List<SourceWeight>(endpointKv.Value.Count);
                    foreach (var entry in endpointKv.Value)
                    {
                        var copied = entry;
                        copied.Extensions = entry.Extensions != null ? (JObject)entry.Extensions.DeepClone() : null;
                        copied.Extras = entry.Extras?.DeepClone();
                        copied.AdditionalProperties = entry.AdditionalProperties != null
                            ? (JObject)entry.AdditionalProperties.DeepClone()
                            : null;
                        contributions.Add(copied);
                    }
                    endpoints[endpointKv.Key] = contributions;
                }
                clone[setKv.Key] = endpoints;
            }
            return clone;
        }

        private Dictionary<string, Dictionary<string, List<TargetWeight>>> CloneInputMappings()
        {
            var clone = new Dictionary<string, Dictionary<string, List<TargetWeight>>>();
            if (ExpressionSetInputMappings == null) return clone;
            foreach (var setKv in ExpressionSetInputMappings)
            {
                if (setKv.Value == null)
                {
                    clone[setKv.Key] = null;
                    continue;
                }
                var endpoints = new Dictionary<string, List<TargetWeight>>();
                foreach (var endpointKv in setKv.Value)
                {
                    if (endpointKv.Value == null)
                    {
                        endpoints[endpointKv.Key] = null;
                        continue;
                    }
                    var contributions = new List<TargetWeight>(endpointKv.Value.Count);
                    foreach (var entry in endpointKv.Value)
                    {
                        var copied = entry;
                        copied.Extensions = entry.Extensions != null ? (JObject)entry.Extensions.DeepClone() : null;
                        copied.Extras = entry.Extras?.DeepClone();
                        copied.AdditionalProperties = entry.AdditionalProperties != null
                            ? (JObject)entry.AdditionalProperties.DeepClone()
                            : null;
                        contributions.Add(copied);
                    }
                    endpoints[endpointKv.Key] = contributions;
                }
                clone[setKv.Key] = endpoints;
            }
            return clone;
        }
    }

    public class KHR_character_expression_mapping_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression_mapping.EXTENSION_NAME;
        public KHR_character_expression_mapping_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            RequiresRuntimeSupportForRequiredUse = true;
        }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_character_expression_mapping { RawData = token };
            if (!(token.Value is JObject obj)) return ext;

            ext.Extensions = obj["extensions"] is JObject extensions
                ? (JObject)extensions.DeepClone()
                : null;
            ext.Extras = obj["extras"]?.DeepClone();
            ext.AdditionalProperties = new JObject();
            foreach (var property in obj.Properties())
                if (property.Name != "expressionSetMappings"
                    && property.Name != "expressionSetInputMappings"
                    && property.Name != "extensions" && property.Name != "extras")
                    ext.AdditionalProperties.Add(property.Name, property.Value.DeepClone());
            if (!ext.AdditionalProperties.HasValues) ext.AdditionalProperties = null;

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
                                Extensions = c["extensions"] is JObject contributionExtensions
                                    ? (JObject)contributionExtensions.DeepClone()
                                    : null,
                                Extras = c["extras"]?.DeepClone(),
                                AdditionalProperties = GetContributionAdditionalProperties(
                                    c, "source"),
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
                                Extensions = c["extensions"] is JObject contributionExtensions
                                    ? (JObject)contributionExtensions.DeepClone()
                                    : null,
                                Extras = c["extras"]?.DeepClone(),
                                AdditionalProperties = GetContributionAdditionalProperties(
                                    c, "target"),
                            });
                        }
                        endpointMap[endpointProp.Name] = list;
                    }
                    ext.ExpressionSetInputMappings[setProp.Name] = endpointMap;
                }
            return ext;
        }

        private static JObject GetContributionAdditionalProperties(JObject contribution, string indexProperty)
        {
            var additional = new JObject();
            foreach (var property in contribution.Properties())
                if (property.Name != indexProperty && property.Name != "name" && property.Name != "weight"
                    && property.Name != "extensions" && property.Name != "extras")
                    additional.Add(property.Name, property.Value.DeepClone());
            return additional.HasValues ? additional : null;
        }
    }
}
