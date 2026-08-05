using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF node extension <c>KHR_node_lookat_target</c>: marks a node as a passive look-at target. The node's
    /// world position is the target point; <c>hint</c> is a free-form advisory string. The extension object may
    /// be empty (<c>{}</c>); its presence alone marks the node as a target.
    /// </summary>
    public class KHR_node_lookat_target : IExtension
    {
        public const string EXTENSION_NAME = "KHR_node_lookat_target";

        public string Hint;
        public JObject Extensions;
        public JToken Extras;
        public JObject AdditionalProperties;
        public JProperty RawData;

        public JProperty Serialize()
        {
            var obj = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : new JObject();
            // hint is optional but minLength:1 — never emit "" (Unity coerces a null SerializeField string to "").
            if (!string.IsNullOrEmpty(Hint)) obj["hint"] = Hint;
            else obj.Remove("hint");
            if (Extensions != null) obj["extensions"] = Extensions.DeepClone();
            else obj.Remove("extensions");
            if (Extras != null) obj["extras"] = Extras.DeepClone();
            else obj.Remove("extras");
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_node_lookat_target
        {
            Hint = Hint,
            Extensions = Extensions != null ? (JObject)Extensions.DeepClone() : null,
            Extras = Extras?.DeepClone(),
            AdditionalProperties = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : null,
            RawData = RawData != null ? new JProperty(RawData) : null,
        };
    }

    public class KHR_node_lookat_target_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_node_lookat_target.EXTENSION_NAME;
        public KHR_node_lookat_target_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            RequiresRuntimeSupportForRequiredUse = true;
        }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_node_lookat_target { RawData = token };
            if (token.Value is JObject obj)
            {
                ext.Hint = obj["hint"]?.Value<string>();
                ext.Extensions = obj["extensions"] is JObject extensions
                    ? (JObject)extensions.DeepClone()
                    : null;
                ext.Extras = obj["extras"]?.DeepClone();
                ext.AdditionalProperties = new JObject();
                foreach (var property in obj.Properties())
                    if (property.Name != "hint" && property.Name != "extensions" && property.Name != "extras")
                        ext.AdditionalProperties.Add(property.Name, property.Value.DeepClone());
                if (!ext.AdditionalProperties.HasValues) ext.AdditionalProperties = null;
            }
            return ext;
        }
    }
}
