using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF node extension <c>KHR_node_visibility_hint</c>: a view-context visibility hint that applies to a node
    /// and its subtree. <c>role</c> selects the view context in which the subtree should render
    /// (<c>both</c> | <c>first_person_only</c> | <c>third_person_only</c>, plus custom vocabulary); <c>label</c> is
    /// an optional UI string. This is an advisory hint that <b>composes on top of</b> core
    /// <c>KHR_node_visibility</c> — it never overrides a node hidden by <c>KHR_node_visibility</c>.
    /// Modeled on <see cref="KHR_node_camera_hint"/>.
    /// </summary>
    public class KHR_node_visibility_hint : IExtension
    {
        public const string EXTENSION_NAME = "KHR_node_visibility_hint";

        public string Role;
        public string Label;
        public JProperty RawData;

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);

            var obj = new JObject();
            if (Role != null) obj.Add("role", Role);
            // label is optional but minLength:1 — never emit "" (Unity coerces a null SerializeField string to "").
            if (!string.IsNullOrEmpty(Label)) obj.Add("label", Label);
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_node_visibility_hint
        { Role = Role, Label = Label, RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_node_visibility_hint_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_node_visibility_hint.EXTENSION_NAME;
        public KHR_node_visibility_hint_Factory() { ExtensionName = EXTENSION_NAME; }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_node_visibility_hint { RawData = token };
            if (token.Value is JObject obj)
            {
                ext.Role = obj["role"]?.Value<string>();
                ext.Label = obj["label"]?.Value<string>();
            }
            if (string.IsNullOrEmpty(ext.Role))
                UnityEngine.Debug.LogWarning($"{EXTENSION_NAME}: spec-required field 'role' is missing.");
            return ext;
        }
    }
}
