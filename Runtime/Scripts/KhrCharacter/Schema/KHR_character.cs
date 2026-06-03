using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF root extension <c>KHR_character</c> (PR #2512): marks the asset as a Character and records the
    /// character root node. Raw JSON is preserved so unknown / forward-compatible fields round-trip.
    /// </summary>
    public class KHR_character : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character";

        /// <summary>Index into <c>nodes</c> for the character root. Required by the spec; tolerated if absent.</summary>
        public int? RootNode;

        /// <summary>The original JProperty, preserved for round-trip / forward-compat.</summary>
        public JProperty RawData;

        public JProperty Serialize()
        {
            // Round-trip the original data when present; otherwise emit the known fields.
            if (RawData != null)
                return new JProperty(RawData.Name, RawData.Value);

            var obj = new JObject();
            if (RootNode.HasValue)
                obj.Add("rootNode", RootNode.Value);
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root)
        {
            return new KHR_character
            {
                RootNode = RootNode,
                RawData = RawData != null ? new JProperty(RawData) : null
            };
        }
    }

    /// <summary>
    /// Deserializes the <c>KHR_character</c> root extension. When this factory is not registered, the
    /// extension still round-trips as a <see cref="DefaultExtension"/> and the import plugin detects the
    /// character by name.
    /// </summary>
    public class KHR_character_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character.EXTENSION_NAME;

        public KHR_character_Factory()
        {
            ExtensionName = EXTENSION_NAME;
        }

        public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
        {
            var ext = new KHR_character { RawData = extensionToken };
            if (extensionToken.Value is JObject obj && obj.TryGetValue("rootNode", out var rootNodeToken))
                ext.RootNode = rootNodeToken.Value<int>();
            if (!ext.RootNode.HasValue)
                UnityEngine.Debug.LogWarning($"{EXTENSION_NAME}: required field 'rootNode' is missing.");
            return ext;
        }
    }
}
