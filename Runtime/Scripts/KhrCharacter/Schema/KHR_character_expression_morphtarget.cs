using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// <c>KHR_character_expression_morphtarget</c> sub-extension of <c>KHR_character_expression</c>.
    /// Lists indices into the referenced animation's <c>channels[]</c> that drive morph-target (blendshape)
    /// weights (tag-or-ignore: a channel is driven only if a present sub-extension claims it).
    /// </summary>
    public class KHR_character_expression_morphtarget : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_expression_morphtarget";

        public int[] Channels;
        public JProperty RawData;

        public static KHR_character_expression_morphtarget FromJson(JObject obj)
        {
            var ext = new KHR_character_expression_morphtarget();
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

        public IExtension Clone(GLTFRoot root) => new KHR_character_expression_morphtarget
        { Channels = (int[])Channels?.Clone(), RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_character_expression_morphtarget_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression_morphtarget.EXTENSION_NAME;
        public KHR_character_expression_morphtarget_Factory() { ExtensionName = EXTENSION_NAME; }
        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var e = KHR_character_expression_morphtarget.FromJson(token.Value as JObject);
            e.RawData = token;
            return e;
        }
    }
}
