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
        public JProperty RawData;

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);
            var obj = new JObject();
            if (Hint != null) obj.Add("hint", Hint);
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_node_lookat_target
        { Hint = Hint, RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_node_lookat_target_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_node_lookat_target.EXTENSION_NAME;
        public KHR_node_lookat_target_Factory() { ExtensionName = EXTENSION_NAME; }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_node_lookat_target { RawData = token };
            if (token.Value is JObject obj)
                ext.Hint = obj["hint"]?.Value<string>();
            return ext;
        }
    }
}
