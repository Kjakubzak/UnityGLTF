using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityGLTF.KhrCharacter;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF root extension <c>KHR_character_expression</c> (PR #2512): declares labeled finite scalar-response
    /// entries, each referencing one glTF animation evaluated by a 0..1 driver. Each item's classifier extensions
    /// (morphtarget / joint / texture / mask) are parsed inline from the item's <c>extensions</c>.
    /// </summary>
    public class KHR_character_expression : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_expression";

        public List<ExpressionItem> Expressions = new List<ExpressionItem>();
        public JObject Extensions;
        public JToken Extras;
        public JObject AdditionalProperties;
        public JProperty RawData;

        public class ExpressionItem
        {
            public string Expression;                              // expression label (required)
            public int Animation;                                 // index into glTF animations (required)
            public KHR_character_expression_morphtarget Morphtarget;
            public KHR_character_expression_joint Joint;
            public KHR_character_expression_texture Texture;
            public KHR_character_expression_mask Mask;
            public JToken Extras;                                 // glTF-standard per-item extras; may contain any JSON value
            public JObject RawExtensions;                         // preserved for forward-compat
        }

        public JProperty Serialize()
        {
            var arr = new JArray();
            if (Expressions != null)
            {
                foreach (var item in Expressions)
                {
                    if (item == null) continue;
                    var itemObj = new JObject();
                    if (item.Expression != null) itemObj.Add("expression", item.Expression);
                    itemObj.Add("animation", item.Animation);

                    var exts = item.RawExtensions != null
                        ? (JObject)item.RawExtensions.DeepClone()
                        : new JObject();
                    RemoveKnownSubExtensions(exts);
                    AddSubExtension(exts, item.Morphtarget);
                    AddSubExtension(exts, item.Joint);
                    AddSubExtension(exts, item.Texture);
                    AddSubExtension(exts, item.Mask);
                    if (exts.HasValues) itemObj.Add("extensions", exts);

                    if (item.Extras != null) itemObj.Add("extras", item.Extras.DeepClone());

                    arr.Add(itemObj);
                }
            }

            var value = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : new JObject();
            value["expressions"] = arr;
            if (Extensions != null && Extensions.HasValues) value["extensions"] = Extensions.DeepClone();
            else value.Remove("extensions");
            if (Extras != null) value["extras"] = Extras.DeepClone();
            else value.Remove("extras");
            return new JProperty(EXTENSION_NAME, value);
        }

        private static void AddSubExtension(JObject extensions, IExtension sub)
        {
            if (sub == null) return;
            var p = sub.Serialize();
            extensions.Add(p.Name, p.Value.DeepClone()); // DeepClone detaches the token so it can be re-parented
        }

        private static void RemoveKnownSubExtensions(JObject extensions)
        {
            var names = new List<string>();
            foreach (var property in extensions.Properties())
            {
                var canonical = KhrCharacterExtensionNames.Canonicalize(property.Name);
                if (canonical == KhrCharacterExtensionNames.ExpressionMorphtarget
                    || canonical == KhrCharacterExtensionNames.ExpressionJoint
                    || canonical == KhrCharacterExtensionNames.ExpressionTexture
                    || canonical == KhrCharacterExtensionNames.ExpressionMask)
                    names.Add(property.Name);
            }
            foreach (var name in names) extensions.Remove(name);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_expression
        {
            Expressions = CloneExpressions(root),
            Extensions = Extensions != null ? (JObject)Extensions.DeepClone() : null,
            Extras = Extras?.DeepClone(),
            AdditionalProperties = AdditionalProperties != null
                ? (JObject)AdditionalProperties.DeepClone()
                : null,
            RawData = RawData != null ? new JProperty(RawData) : null
        };

        private List<ExpressionItem> CloneExpressions(GLTFRoot root)
        {
            var clone = new List<ExpressionItem>();
            if (Expressions == null) return clone;
            foreach (var item in Expressions)
            {
                if (item == null)
                {
                    clone.Add(null);
                    continue;
                }
                clone.Add(new ExpressionItem
                {
                    Expression = item.Expression,
                    Animation = item.Animation,
                    Morphtarget = item.Morphtarget?.Clone(root) as KHR_character_expression_morphtarget,
                    Joint = item.Joint?.Clone(root) as KHR_character_expression_joint,
                    Texture = item.Texture?.Clone(root) as KHR_character_expression_texture,
                    Mask = item.Mask?.Clone(root) as KHR_character_expression_mask,
                    Extras = item.Extras?.DeepClone(),
                    RawExtensions = item.RawExtensions != null
                        ? (JObject)item.RawExtensions.DeepClone()
                        : null,
                });
            }
            return clone;
        }
    }

    public class KHR_character_expression_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_expression.EXTENSION_NAME;

        public KHR_character_expression_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            RequiresRuntimeSupportForRequiredUse = true;
        }

        public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
        {
            var ext = new KHR_character_expression { RawData = extensionToken };
            if (!(extensionToken.Value is JObject obj)) return ext;

            ext.Extensions = obj["extensions"] is JObject extensions
                ? (JObject)extensions.DeepClone()
                : null;
            ext.Extras = obj["extras"]?.DeepClone();
            ext.AdditionalProperties = new JObject();
            foreach (var property in obj.Properties())
                if (property.Name != "expressions" && property.Name != "extensions" && property.Name != "extras")
                    ext.AdditionalProperties.Add(property.Name, property.Value.DeepClone());
            if (!ext.AdditionalProperties.HasValues) ext.AdditionalProperties = null;

            if (!(obj["expressions"] is JArray arr)) return ext;

            foreach (var node in arr)
            {
                if (!(node is JObject itemObj)) continue;
                var item = new KHR_character_expression.ExpressionItem
                {
                    Expression = itemObj["expression"]?.Value<string>(),
                    Animation = itemObj["animation"]?.Value<int>() ?? -1,
                    Extras = itemObj["extras"]?.DeepClone(),
                };

                if (itemObj["extensions"] is JObject itemExts)
                {
                    item.RawExtensions = (JObject)itemExts.DeepClone();
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
