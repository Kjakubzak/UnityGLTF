using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// <c>KHR_character_expression_texture</c> sub-extension of <c>KHR_character_expression</c>.
    /// Lists indices into the referenced animation's <c>channels[]</c> that drive UV transforms via
    /// <c>KHR_animation_pointer</c> and <c>KHR_texture_transform</c>.
    /// </summary>
    public class KHR_character_expression_texture : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_expression_texture";

        public int[] Channels;
        public JProperty RawData;

        public static KHR_character_expression_texture FromJson(JObject obj)
        {
            var ext = new KHR_character_expression_texture();
            if (obj?["channels"] is JArray arr)
            {
                ext.Channels = new int[arr.Count];
                for (int i = 0; i < arr.Count; i++) ext.Channels[i] = arr[i].Value<int>();
            }
            return ext;
        }

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);
            var obj = new JObject();
            if (Channels != null)
            {
                var arr = new JArray();
                foreach (var c in Channels) arr.Add(c);
                obj.Add("channels", arr);
            }
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_expression_texture
        { Channels = (int[])Channels?.Clone(), RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_character_expression_texture_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression_texture.EXTENSION_NAME;
        public KHR_character_expression_texture_Factory() { ExtensionName = EXTENSION_NAME; }
        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var e = KHR_character_expression_texture.FromJson(token.Value as JObject);
            e.RawData = token;
            return e;
        }
    }
}
