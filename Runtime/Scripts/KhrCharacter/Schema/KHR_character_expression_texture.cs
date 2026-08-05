using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// <c>KHR_character_expression_texture</c> sub-extension of <c>KHR_character_expression</c>.
    /// Classifies channels whose <c>KHR_animation_pointer</c> targets an authored
    /// <c>KHR_texture_transform</c> property. It does not select or alter base evaluation.
    /// </summary>
    public class KHR_character_expression_texture : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_expression_texture";

        public int[] Channels;
        public JObject Extensions;
        public JToken Extras;
        public JObject AdditionalProperties;
        public JProperty RawData;

        public static KHR_character_expression_texture FromJson(JObject obj)
        {
            var ext = new KHR_character_expression_texture();
            if (obj?["channels"] is JArray arr)
            {
                ext.Channels = new int[arr.Count];
                for (int i = 0; i < arr.Count; i++) ext.Channels[i] = arr[i].Value<int>();
            }
            if (obj != null)
            {
                ext.Extensions = obj["extensions"] is JObject extensions
                    ? (JObject)extensions.DeepClone()
                    : null;
                ext.Extras = obj["extras"]?.DeepClone();
                ext.AdditionalProperties = new JObject();
                foreach (var property in obj.Properties())
                    if (property.Name != "channels" && property.Name != "extensions" && property.Name != "extras")
                        ext.AdditionalProperties.Add(property.Name, property.Value.DeepClone());
                if (!ext.AdditionalProperties.HasValues) ext.AdditionalProperties = null;
            }
            return ext;
        }

        public JProperty Serialize()
        {
            var obj = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : new JObject();
            if (Channels != null)
            {
                var arr = new JArray();
                foreach (var c in Channels) arr.Add(c);
                obj["channels"] = arr;
            }
            else obj.Remove("channels");
            if (Extensions != null && Extensions.HasValues) obj["extensions"] = Extensions.DeepClone();
            else obj.Remove("extensions");
            if (Extras != null) obj["extras"] = Extras.DeepClone();
            else obj.Remove("extras");
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_expression_texture
        {
            Channels = (int[])Channels?.Clone(),
            Extensions = Extensions != null ? (JObject)Extensions.DeepClone() : null,
            Extras = Extras?.DeepClone(),
            AdditionalProperties = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : null,
            RawData = RawData != null ? new JProperty(RawData) : null,
        };
    }

    public class KHR_character_expression_texture_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression_texture.EXTENSION_NAME;
        public KHR_character_expression_texture_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            RequiresRuntimeSupportForRequiredUse = true;
        }
        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var e = KHR_character_expression_texture.FromJson(token.Value as JObject);
            e.RawData = token;
            return e;
        }
    }
}
