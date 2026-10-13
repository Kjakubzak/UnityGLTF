using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityGLTF.KhrCharacter;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF root extension <c>KHR_character_expression</c> (PR #2512): declares labeled finite scalar-response
    /// entries, each referencing one glTF animation evaluated by a 0..1 driver. Each entry's derived expression
    /// extensions (morphtarget / joint / texture_transform) and its suppression extension are parsed inline from
    /// the entry's <c>extensions</c>; any other entry extension is kept verbatim in
    /// <see cref="ExpressionItem.RawExtensions"/>.
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
            /// <summary>Required nonempty descriptive label. Labels are not unique; array indices identify entries.</summary>
            public string Name;
            public int Animation;                                 // index into glTF animations (required)
            public KHR_character_expression_morphtarget Morphtarget;
            public KHR_character_expression_joint Joint;
            public KHR_character_expression_texture_transform TextureTransform;
            public KHR_character_expression_suppression Suppression;
            public JToken Extras;                                 // glTF-standard per-item extras; may contain any JSON value
            public JObject RawExtensions;                         // preserved for forward-compat

            /// <summary>
            /// Entry members other than <c>name</c>, <c>animation</c>, <c>extensions</c> and <c>extras</c>, preserved
            /// verbatim. A legacy <c>expression</c> label stays here and never populates <see cref="Name"/>.
            /// </summary>
            public JObject AdditionalProperties;
        }

        public JProperty Serialize()
        {
            var arr = new JArray();
            if (Expressions != null)
            {
                foreach (var item in Expressions)
                {
                    if (item == null) continue;
                    var itemObj = item.AdditionalProperties != null
                        ? (JObject)item.AdditionalProperties.DeepClone()
                        : new JObject();
                    if (item.Name != null) itemObj["name"] = item.Name;
                    else itemObj.Remove("name");
                    itemObj["animation"] = item.Animation;

                    var exts = item.RawExtensions != null
                        ? (JObject)item.RawExtensions.DeepClone()
                        : new JObject();
                    RemoveKnownSubExtensions(exts);
                    AddSubExtension(exts, item.Morphtarget);
                    AddSubExtension(exts, item.Joint);
                    AddSubExtension(exts, item.TextureTransform);
                    AddSubExtension(exts, item.Suppression);
                    if (exts.HasValues) itemObj["extensions"] = exts;
                    else itemObj.Remove("extensions");

                    if (item.Extras != null) itemObj["extras"] = item.Extras.DeepClone();
                    else itemObj.Remove("extras");

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
                if (property.Name == KhrCharacterExtensionNames.ExpressionMorphtarget
                    || property.Name == KhrCharacterExtensionNames.ExpressionJoint
                    || property.Name == KhrCharacterExtensionNames.ExpressionTextureTransform
                    || property.Name == KhrCharacterExtensionNames.ExpressionSuppression)
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
                    Name = item.Name,
                    Animation = item.Animation,
                    Morphtarget = item.Morphtarget?.Clone(root) as KHR_character_expression_morphtarget,
                    Joint = item.Joint?.Clone(root) as KHR_character_expression_joint,
                    TextureTransform = item.TextureTransform?.Clone(root) as KHR_character_expression_texture_transform,
                    Suppression = item.Suppression?.Clone(root) as KHR_character_expression_suppression,
                    Extras = item.Extras?.DeepClone(),
                    RawExtensions = item.RawExtensions != null
                        ? (JObject)item.RawExtensions.DeepClone()
                        : null,
                    AdditionalProperties = item.AdditionalProperties != null
                        ? (JObject)item.AdditionalProperties.DeepClone()
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
                    Name = itemObj["name"]?.Value<string>(),
                    Animation = itemObj["animation"]?.Value<int>() ?? -1,
                    Extras = itemObj["extras"]?.DeepClone(),
                    AdditionalProperties = new JObject(),
                };
                foreach (var property in itemObj.Properties())
                    if (property.Name != "name" && property.Name != "animation"
                        && property.Name != "extensions" && property.Name != "extras")
                        item.AdditionalProperties.Add(property.Name, property.Value.DeepClone());
                if (!item.AdditionalProperties.HasValues) item.AdditionalProperties = null;

                if (itemObj["extensions"] is JObject itemExts)
                {
                    item.RawExtensions = (JObject)itemExts.DeepClone();
                    foreach (var p in itemExts.Properties())
                    {
                        var subObj = p.Value as JObject;
                        if (subObj == null) continue;
                        if (p.Name == KhrCharacterExtensionNames.ExpressionMorphtarget)
                            item.Morphtarget = KHR_character_expression_morphtarget.FromJson(subObj);
                        else if (p.Name == KhrCharacterExtensionNames.ExpressionJoint)
                            item.Joint = KHR_character_expression_joint.FromJson(subObj);
                        else if (p.Name == KhrCharacterExtensionNames.ExpressionTextureTransform)
                            item.TextureTransform = KHR_character_expression_texture_transform.FromJson(subObj);
                        else if (p.Name == KhrCharacterExtensionNames.ExpressionSuppression)
                            item.Suppression = KHR_character_expression_suppression.FromJson(subObj);
                    }
                }

                ext.Expressions.Add(item);
            }
            return ext;
        }
    }
}
