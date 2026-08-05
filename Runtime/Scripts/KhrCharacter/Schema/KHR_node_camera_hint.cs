using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF node extension <c>KHR_node_camera_hint</c>: marks a node as a recommended camera placement.
    /// <c>role</c> is required; <c>camera</c>/<c>targetNode</c> are node/camera indices; <c>label</c> is for UI.
    /// </summary>
    public class KHR_node_camera_hint : IExtension
    {
        public const string EXTENSION_NAME = "KHR_node_camera_hint";

        public string Role;
        public int? Camera;       // index into glTF cameras
        public int? TargetNode;   // index into glTF nodes
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
            else obj.Remove("role");
            if (Camera.HasValue) obj["camera"] = Camera.Value;
            else obj.Remove("camera");
            if (TargetNode.HasValue) obj["targetNode"] = TargetNode.Value;
            else obj.Remove("targetNode");
            // label is optional but minLength:1 — never emit "" (Unity coerces a null SerializeField string to "").
            if (!string.IsNullOrEmpty(Label)) obj["label"] = Label;
            else obj.Remove("label");
            if (Extensions != null) obj["extensions"] = Extensions.DeepClone();
            else obj.Remove("extensions");
            if (Extras != null) obj["extras"] = Extras.DeepClone();
            else obj.Remove("extras");
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_node_camera_hint
        {
            Role = Role,
            Camera = Camera,
            TargetNode = TargetNode,
            Label = Label,
            Extensions = Extensions != null ? (JObject)Extensions.DeepClone() : null,
            Extras = Extras?.DeepClone(),
            AdditionalProperties = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : null,
            RawData = RawData != null ? new JProperty(RawData) : null,
        };
    }

    public class KHR_node_camera_hint_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_node_camera_hint.EXTENSION_NAME;
        public KHR_node_camera_hint_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            RequiresRuntimeSupportForRequiredUse = true;
        }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_node_camera_hint { RawData = token };
            if (token.Value is JObject obj)
            {
                ext.Role = obj["role"]?.Value<string>();
                ext.Camera = obj["camera"]?.Value<int>();
                ext.TargetNode = obj["targetNode"]?.Value<int>();
                ext.Label = obj["label"]?.Value<string>();
                ext.Extensions = obj["extensions"] is JObject extensions
                    ? (JObject)extensions.DeepClone()
                    : null;
                ext.Extras = obj["extras"]?.DeepClone();
                ext.AdditionalProperties = new JObject();
                foreach (var property in obj.Properties())
                    if (property.Name != "role" && property.Name != "camera"
                        && property.Name != "targetNode" && property.Name != "label"
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
