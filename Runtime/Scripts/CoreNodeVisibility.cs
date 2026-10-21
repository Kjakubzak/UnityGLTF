using UnityEngine;
using GLTF.Schema;
using Newtonsoft.Json.Linq;

namespace UnityGLTF
{
    /// <summary>Own KHR_node_visibility state, independent of GameObject activation and hint predicates.</summary>
    [DisallowMultipleComponent]
    public sealed class CoreNodeVisibility : MonoBehaviour
    {
        public bool Visible = true;
        [SerializeField, HideInInspector] private string _extensionJson;

        public void Bind(KHR_node_visibility extension)
        {
            Visible = extension?.visible ?? true;
            _extensionJson = extension?.Serialize().Value.ToString(Newtonsoft.Json.Formatting.None);
        }

        public KHR_node_visibility ExportOwnState()
        {
            var extension = !string.IsNullOrEmpty(_extensionJson)
                ? new KHR_node_visibility_Factory().Deserialize(null,
                    new JProperty(KHR_node_visibility_Factory.EXTENSION_NAME, JObject.Parse(_extensionJson))) as KHR_node_visibility
                : new KHR_node_visibility();
            extension.visible = Visible;
            return extension;
        }

        /// <summary>Current Boolean AND in this evaluated instance hierarchy; absent properties contribute true.</summary>
        public static bool IsVisibleInHierarchy(Transform node, Transform instanceRoot = null)
        {
            for (var current = node; current != null; current = current.parent)
            {
                var state = current.GetComponent<CoreNodeVisibility>();
                if (state != null && !state.Visible) return false;
                if (current == instanceRoot) break;
            }
            return true;
        }
    }
}
