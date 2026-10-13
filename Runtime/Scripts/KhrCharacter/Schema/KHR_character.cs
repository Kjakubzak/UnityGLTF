using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF root extension <c>KHR_character</c> (PR #2512): designates the asset as containing at most one
    /// character. The object defines no properties and names no nodes; it is <c>{}</c> unless it carries
    /// <c>extensions</c> or <c>extras</c>. Unknown members, such as a legacy <c>rootNode</c>, are kept in
    /// <see cref="AdditionalProperties"/> and carry no meaning.
    /// </summary>
    public class KHR_character : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character";

        public JObject Extensions;
        public JToken Extras;

        /// <summary>Members the extension does not define, preserved verbatim for round-trip.</summary>
        public JObject AdditionalProperties;

        /// <summary>The original JProperty, preserved for diagnostics.</summary>
        public JProperty RawData;

        public JProperty Serialize()
        {
            var obj = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : new JObject();
            if (Extensions != null && Extensions.HasValues) obj["extensions"] = Extensions.DeepClone();
            else obj.Remove("extensions");
            if (Extras != null) obj["extras"] = Extras.DeepClone();
            else obj.Remove("extras");
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root)
        {
            return new KHR_character
            {
                Extensions = Extensions != null ? (JObject)Extensions.DeepClone() : null,
                Extras = Extras?.DeepClone(),
                AdditionalProperties = AdditionalProperties != null
                    ? (JObject)AdditionalProperties.DeepClone()
                    : null,
                RawData = RawData != null ? new JProperty(RawData) : null
            };
        }
    }

    /// <summary>
    /// Deserializes the <c>KHR_character</c> root extension. When this factory is not registered, the
    /// extension still round-trips as a <see cref="DefaultExtension"/> and the import plugin detects the
    /// character by name. Required use is accepted only when an import plugin exposes the designation.
    /// </summary>
    public class KHR_character_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character.EXTENSION_NAME;

        public KHR_character_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            RequiresRuntimeSupportForRequiredUse = true;
        }

        public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
        {
            var ext = new KHR_character { RawData = extensionToken };
            if (!(extensionToken?.Value is JObject obj)) return ext;

            ext.Extensions = obj["extensions"] is JObject extensions
                ? (JObject)extensions.DeepClone()
                : null;
            ext.Extras = obj["extras"]?.DeepClone();
            ext.AdditionalProperties = new JObject();
            foreach (var property in obj.Properties())
                if (property.Name != "extensions" && property.Name != "extras")
                    ext.AdditionalProperties.Add(property.Name, property.Value.DeepClone());
            if (!ext.AdditionalProperties.HasValues) ext.AdditionalProperties = null;
            return ext;
        }
    }
}
