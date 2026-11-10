using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF node extension <c>KHR_node_visibility_hint</c>: one or more visibility roles that say when the node's
    /// content, and that of its descendants, is meant to be visible. The application's logic decides which roles
    /// apply in each render view (see <c>UnityGLTF.VisibilityHints.VisibilityRoleContext</c>); a hint allows a view
    /// when any recognized role applies there, and the node's own hint and every ancestor hint apply together.
    /// <c>label</c> is an optional UI string. The hint composes with core <c>KHR_node_visibility</c> and never
    /// overrides it. The extension must not be listed in <c>extensionsRequired</c>.
    /// </summary>
    public class KHR_node_visibility_hint : IExtension
    {
        public const string EXTENSION_NAME = "KHR_node_visibility_hint";

        /// <summary>
        /// The authored roles: distinct nonempty strings in authored order (order has no meaning). Null when the
        /// hint has no valid <c>roles</c>, in which case importers ignore it and exporters skip it.
        /// </summary>
        public string[] Roles;

        public string Label;
        public JObject Extensions;
        public JToken Extras;
        public JObject AdditionalProperties;
        public JProperty RawData;

        public JProperty Serialize()
            => new JProperty(EXTENSION_NAME, VisibilityHintJson.Serialize(Roles, Label, Extensions, Extras, AdditionalProperties));

        public IExtension Clone(GLTFRoot root) => new KHR_node_visibility_hint
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

    public class KHR_node_visibility_hint_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_node_visibility_hint.EXTENSION_NAME;
        public KHR_node_visibility_hint_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            // The hints must never be required, so a listing in extensionsRequired is tolerated (the import plugin
            // warns) instead of failing the import closed.
            RequiresRuntimeSupportForRequiredUse = false;
        }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_node_visibility_hint { RawData = token };
            VisibilityHintJson.Parse(EXTENSION_NAME, token.Value as JObject,
                out ext.Roles, out ext.Label, out ext.Extensions, out ext.Extras, out ext.AdditionalProperties);
            return ext;
        }
    }

    /// <summary>Shared wire handling of the two visibility-hint extension objects.</summary>
    internal static class VisibilityHintJson
    {
        internal static JObject Serialize(
            string[] roles, string label, JObject extensions, JToken extras, JObject additionalProperties)
        {
            var obj = additionalProperties != null ? (JObject)additionalProperties.DeepClone() : new JObject();
            var array = new JArray();
            if (roles != null)
                foreach (var role in roles)
                    if (!string.IsNullOrEmpty(role) && !Contains(array, role)) array.Add(role);
            if (array.Count > 0) obj["roles"] = array;
            else obj.Remove("roles");
            // The legacy single "role" is not part of the specification and is never written.
            obj.Remove("role");
            // label is optional but minLength:1; never emit "" (Unity coerces a null serialized string to "").
            if (!string.IsNullOrEmpty(label)) obj["label"] = label;
            else obj.Remove("label");
            if (extensions != null) obj["extensions"] = extensions.DeepClone();
            if (extras != null) obj["extras"] = extras.DeepClone();
            return obj;
        }

        /// <summary>
        /// Reads <c>roles</c> (required: a nonempty array of distinct nonempty strings) and <c>label</c>. Invalid
        /// items and duplicates are dropped with a warning; a hint left with no valid role reads as null roles. A
        /// legacy single <c>role</c> string is not part of the specification: it is dropped (never preserved or
        /// re-exported) with a warning, and without <c>roles</c> the hint is ignored.
        /// </summary>
        internal static void Parse(
            string extensionName,
            JObject obj,
            out string[] roles,
            out string label,
            out JObject extensions,
            out JToken extras,
            out JObject additionalProperties)
        {
            roles = null;
            label = null;
            extensions = null;
            extras = null;
            additionalProperties = null;
            if (obj == null)
            {
                UnityEngine.Debug.LogWarning($"{extensionName}: the extension object is not a JSON object; ignoring it.");
                return;
            }

            if (obj["roles"] is JArray array)
            {
                var list = new System.Collections.Generic.List<string>();
                foreach (var item in array)
                {
                    string role = item.Type == JTokenType.String ? item.Value<string>() : null;
                    if (string.IsNullOrEmpty(role))
                    {
                        UnityEngine.Debug.LogWarning(
                            $"{extensionName}: ignoring a 'roles' item that is not a nonempty string.");
                        continue;
                    }
                    if (list.Contains(role))
                    {
                        UnityEngine.Debug.LogWarning($"{extensionName}: ignoring duplicate role '{role}'.");
                        continue;
                    }
                    list.Add(role);
                }
                if (list.Count > 0) roles = list.ToArray();
                else UnityEngine.Debug.LogWarning(
                    $"{extensionName}: 'roles' has no valid role; the hint imposes no restriction.");
                if (obj["role"] != null)
                    UnityEngine.Debug.LogWarning(
                        $"{extensionName}: ignoring the legacy 'role' property, which is not part of the " +
                        "specification; 'roles' is used and 'role' is not re-exported.");
            }
            else if (obj["role"] != null)
            {
                UnityEngine.Debug.LogWarning(
                    $"{extensionName}: the legacy 'role' property is not part of the specification and the " +
                    "required 'roles' array is missing; the hint imposes no restriction. Re-export the asset " +
                    "with 'roles'.");
            }
            else
            {
                UnityEngine.Debug.LogWarning(
                    $"{extensionName}: the required 'roles' array is missing; the hint imposes no restriction.");
            }

            if (obj["label"]?.Type == JTokenType.String)
            {
                label = obj["label"].Value<string>();
                if (label.Length == 0) label = null;
            }
            extensions = obj["extensions"] is JObject ext ? (JObject)ext.DeepClone() : null;
            extras = obj["extras"]?.DeepClone();
            var additional = new JObject();
            foreach (var property in obj.Properties())
                if (property.Name != "roles" && property.Name != "role" && property.Name != "label"
                    && property.Name != "extensions" && property.Name != "extras")
                    additional.Add(property.Name, property.Value.DeepClone());
            additionalProperties = additional.HasValues ? additional : null;
        }

        private static bool Contains(JArray array, string value)
        {
            foreach (var item in array)
                if (string.Equals(item.Value<string>(), value, System.StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
