using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF mesh-primitive extension <c>KHR_mesh_primitive_visibility_hint</c>: a view-context visibility hint that
    /// applies to a single mesh primitive (self-only, no subtree inheritance). <c>role</c> selects the view context
    /// in which the primitive should render (<c>always</c> | <c>first_person</c> | <c>third_person</c>, plus
    /// custom vocabulary); <c>label</c> is an optional UI string. Advisory; composes on top of core
    /// <c>KHR_node_visibility</c>. Because the extension lives on the shared <c>meshes[m].primitives[i]</c>, it
    /// applies to every node that references that mesh.
    /// </summary>
    public class KHR_mesh_primitive_visibility_hint : IExtension
    {
        public const string EXTENSION_NAME = "KHR_mesh_primitive_visibility_hint";

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

        public IExtension Clone(GLTFRoot root) => new KHR_mesh_primitive_visibility_hint
        { Role = Role, Label = Label, RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_mesh_primitive_visibility_hint_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_mesh_primitive_visibility_hint.EXTENSION_NAME;
        public KHR_mesh_primitive_visibility_hint_Factory() { ExtensionName = EXTENSION_NAME; }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_mesh_primitive_visibility_hint { RawData = token };
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
