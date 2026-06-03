using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityGLTF.KhrCharacter;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF root extension <c>KHR_character_expression</c> (PR #2512): declares named facial expressions,
    /// each referencing one glTF animation driven by a 0..1 value. Each item's per-domain sub-extensions
    /// (morphtarget / joint / texture / mask) are parsed inline from the item's <c>extensions</c>.
    /// </summary>
    public class KHR_character_expression : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_expression";

        public List<ExpressionItem> Expressions = new List<ExpressionItem>();
        public JProperty RawData;

        public class ExpressionItem
        {
            public string Expression;                              // expression label (required)
            public int Animation;                                 // index into glTF animations (required)
            public KHR_character_expression_morphtarget Morphtarget;
            public KHR_character_expression_joint Joint;
            public KHR_character_expression_texture Texture;
            public KHR_character_expression_mask Mask;
            public JObject RawExtensions;                         // preserved for forward-compat
        }

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);

            var arr = new JArray();
            if (Expressions != null)
            {
                foreach (var item in Expressions)
                {
                    if (item == null) continue;
                    var itemObj = new JObject();
                    if (item.Expression != null) itemObj.Add("expression", item.Expression);
                    itemObj.Add("animation", item.Animation);

                    var exts = new JObject();
                    AddSubExtension(exts, item.Morphtarget);
                    AddSubExtension(exts, item.Joint);
                    AddSubExtension(exts, item.Texture);
                    AddSubExtension(exts, item.Mask);
                    if (exts.HasValues) itemObj.Add("extensions", exts);

                    arr.Add(itemObj);
                }
            }
            return new JProperty(EXTENSION_NAME, new JObject { { "expressions", arr } });
        }

        private static void AddSubExtension(JObject extensions, IExtension sub)
        {
            if (sub == null) return;
            var p = sub.Serialize();
            extensions.Add(p.Name, p.Value.DeepClone()); // DeepClone detaches the token so it can be re-parented
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_expression
        {
            Expressions = Expressions,
            RawData = RawData != null ? new JProperty(RawData) : null
        };
    }

    public class KHR_character_expression_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression.EXTENSION_NAME;

        public KHR_character_expression_Factory() { ExtensionName = EXTENSION_NAME; }

        public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
        {
            var ext = new KHR_character_expression { RawData = extensionToken };
            if (!(extensionToken.Value is JObject obj) || !(obj["expressions"] is JArray arr))
                return ext;

            foreach (var node in arr)
            {
                if (!(node is JObject itemObj)) continue;
                var item = new KHR_character_expression.ExpressionItem
                {
                    Expression = itemObj["expression"]?.Value<string>(),
                    Animation = itemObj["animation"]?.Value<int>() ?? -1,
                };

                if (itemObj["extensions"] is JObject itemExts)
                {
                    item.RawExtensions = itemExts;
                    foreach (var p in itemExts.Properties())
                    {
                        var canonical = KhrCharacterExtensionNames.Canonicalize(p.Name);
                        var subObj = p.Value as JObject;
                        if (subObj == null) continue;
                        if (canonical == KhrCharacterExtensionNames.ExpressionMorphtarget)
                            item.Morphtarget = KHR_character_expression_morphtarget.FromJson(subObj);
                        else if (canonical == KhrCharacterExtensionNames.ExpressionJoint)
                            item.Joint = KHR_character_expression_joint.FromJson(subObj);
                        else if (canonical == KhrCharacterExtensionNames.ExpressionTexture)
                            item.Texture = KHR_character_expression_texture.FromJson(subObj);
                        else if (canonical == KhrCharacterExtensionNames.ExpressionMask)
                            item.Mask = KHR_character_expression_mask.FromJson(subObj);
                    }
                }

                ext.Expressions.Add(item);
            }
            return ext;
        }
    }
}
