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
        public JProperty RawData;

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);

            var obj = new JObject();
            if (Role != null) obj.Add("role", Role);
            if (Camera.HasValue) obj.Add("camera", Camera.Value);
            if (TargetNode.HasValue) obj.Add("targetNode", TargetNode.Value);
            if (Label != null) obj.Add("label", Label);
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_node_camera_hint
        { Role = Role, Camera = Camera, TargetNode = TargetNode, Label = Label, RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_node_camera_hint_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_node_camera_hint.EXTENSION_NAME;
        public KHR_node_camera_hint_Factory() { ExtensionName = EXTENSION_NAME; }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_node_camera_hint { RawData = token };
            if (token.Value is JObject obj)
            {
                ext.Role = obj["role"]?.Value<string>();
                ext.Camera = obj["camera"]?.Value<int>();
                ext.TargetNode = obj["targetNode"]?.Value<int>();
                ext.Label = obj["label"]?.Value<string>();
            }
            if (string.IsNullOrEmpty(ext.Role))
                UnityEngine.Debug.LogWarning($"{EXTENSION_NAME}: spec-required field 'role' is missing.");
            return ext;
        }
    }
}
