using System;
using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{

	[Serializable]
	public class KHR_node_visibility : IExtension
	{
		public bool visible = true;
        public JObject AdditionalProperties;

		public JProperty Serialize()
		{
			var obj = AdditionalProperties != null ? (JObject)AdditionalProperties.DeepClone() : new JObject();
			JProperty jProperty = new JProperty(KHR_node_visibility_Factory.EXTENSION_NAME, obj);
			obj[nameof(visible)] = visible;
			return jProperty;
		}

		public IExtension Clone(GLTFRoot root)
		{
			return new KHR_node_visibility() { visible = visible, AdditionalProperties = AdditionalProperties != null ? (JObject)AdditionalProperties.DeepClone() : null };
		}
	}

	public class KHR_node_visibility_Factory : ExtensionFactory
	{
		public const string EXTENSION_NAME = "KHR_node_visibility";

		public KHR_node_visibility_Factory()
		{
			ExtensionName = EXTENSION_NAME;
		}

		public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
		{
			if (extensionToken != null)
			{
				var extension = new KHR_node_visibility();
				
				JToken visible = extensionToken.Value[nameof(KHR_node_visibility.visible)];
				if (visible != null)
					extension.visible = visible.Value<bool>();
				else
					extension.visible = true;
                if (extensionToken.Value is JObject obj)
                {
                    extension.AdditionalProperties = (JObject)obj.DeepClone();
                    extension.AdditionalProperties.Remove(nameof(KHR_node_visibility.visible));
                }
				return extension;
			}

			return null;
		}
	}
}

