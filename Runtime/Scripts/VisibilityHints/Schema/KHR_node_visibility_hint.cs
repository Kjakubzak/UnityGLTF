using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF node extension <c>KHR_node_visibility_hint</c>: a view-context visibility hint that applies to a node
    /// and its subtree. <c>role</c> selects the view context in which the subtree should render
    /// (<c>always</c> | <c>first_person</c> | <c>third_person</c>, plus custom vocabulary); <c>label</c> is
    /// an optional UI string. The predicate <b>composes on top of</b> core <c>KHR_node_visibility</c> and never
    /// overrides a node hidden by it. Used-only assets may be ignored; required use must honor the predicate.
    /// Modeled on <see cref="KHR_node_camera_hint"/>.
    /// </summary>
    public class KHR_node_visibility_hint : IExtension
    {
        public const string EXTENSION_NAME = "KHR_node_visibility_hint";

        public string Role;
        public string Label;
        public JObject Extensions;
        public JToken Extras;
        public JObject AdditionalProperties;
        public JProperty RawData;

        public JProperty Serialize()
        {
            var obj = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : new JObject();
            if (Role != null) obj["role"] = Role;
            // label is optional but minLength:1 — never emit "" (Unity coerces a null SerializeField string to "").
            if (!string.IsNullOrEmpty(Label)) obj["label"] = Label;
            else obj.Remove("label");
            if (Extensions != null) obj["extensions"] = Extensions.DeepClone();
            if (Extras != null) obj["extras"] = Extras.DeepClone();
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_node_visibility_hint
        {
            Role = Role,
            Label = Label,
            Extensions = Extensions != null ? (JObject)Extensions.DeepClone() : null,
            Extras = Extras?.DeepClone(),
            AdditionalProperties = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : null,
            RawData = RawData != null ? new JProperty(RawData) : null,
        };
    }

    public class KHR_node_visibility_hint_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_node_visibility_hint.EXTENSION_NAME;
        public KHR_node_visibility_hint_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            RequiresRuntimeSupportForRequiredUse = true;
        }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_node_visibility_hint { RawData = token };
            if (token.Value is JObject obj)
            {
                ext.Role = obj["role"]?.Value<string>();
                ext.Label = obj["label"]?.Value<string>();
                ext.Extensions = obj["extensions"] is JObject extensions
                    ? (JObject)extensions.DeepClone()
                    : null;
                ext.Extras = obj["extras"]?.DeepClone();
                ext.AdditionalProperties = new JObject();
                foreach (var property in obj.Properties())
                    if (property.Name != "role" && property.Name != "label"
                        && property.Name != "extensions" && property.Name != "extras")
                        ext.AdditionalProperties.Add(property.Name, property.Value.DeepClone());
                if (!ext.AdditionalProperties.HasValues) ext.AdditionalProperties = null;
            }
            if (string.IsNullOrEmpty(ext.Role))
                UnityEngine.Debug.LogWarning($"{EXTENSION_NAME}: spec-required field 'role' is missing.");
            return ext;
        }
    }
}
