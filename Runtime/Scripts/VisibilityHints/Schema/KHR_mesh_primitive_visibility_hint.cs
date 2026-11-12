using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF mesh-primitive extension <c>KHR_mesh_primitive_visibility_hint</c>: one or more visibility roles for a
    /// single mesh primitive (not inherited). It lives on the shared <c>meshes[m].primitives[i]</c>, so it applies to
    /// every referencing node, and it further restricts the containing node's ancestor-inclusive hint result and core
    /// <c>KHR_node_visibility</c>. <c>label</c> is an optional UI string. The extension must not be listed in
    /// <c>extensionsRequired</c>.
    /// </summary>
    public class KHR_mesh_primitive_visibility_hint : IExtension
    {
        public const string EXTENSION_NAME = "KHR_mesh_primitive_visibility_hint";

        /// <summary>
        /// The authored roles: distinct nonempty strings in authored order (order has no meaning). Null when the
        /// hint has no valid <c>roles</c>, in which case importers ignore it and <see cref="Serialize"/> returns null,
        /// so the hint is not written.
        /// </summary>
        public string[] Roles;

        public string Label;
        public JObject Extensions;
        public JToken Extras;
        public JObject AdditionalProperties;
        public JProperty RawData;

        /// <summary>The wire property, or null when there is no valid role; <see cref="GLTFProperty"/> omits a null
        /// extension with a warning.</summary>
        public JProperty Serialize()
            => VisibilityHintJson.ToProperty(EXTENSION_NAME, Roles, Label, Extensions, Extras, AdditionalProperties);

        public IExtension Clone(GLTFRoot root) => new KHR_mesh_primitive_visibility_hint
        {
            Roles = Roles != null ? (string[])Roles.Clone() : null,
            Label = Label,
            Extensions = Extensions != null ? (JObject)Extensions.DeepClone() : null,
            Extras = Extras?.DeepClone(),
            AdditionalProperties = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : null,
            RawData = RawData != null ? new JProperty(RawData) : null,
        };
    }

    public class KHR_mesh_primitive_visibility_hint_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_mesh_primitive_visibility_hint.EXTENSION_NAME;
        public KHR_mesh_primitive_visibility_hint_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            // The hints must never be required, so a listing in extensionsRequired is tolerated (the import plugin
            // warns) instead of failing the import closed.
            RequiresRuntimeSupportForRequiredUse = false;
        }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_mesh_primitive_visibility_hint { RawData = token };
            VisibilityHintJson.Parse(EXTENSION_NAME, token.Value as JObject,
                out ext.Roles, out ext.Label, out ext.Extensions, out ext.Extras, out ext.AdditionalProperties);
            return ext;
        }
    }
}
