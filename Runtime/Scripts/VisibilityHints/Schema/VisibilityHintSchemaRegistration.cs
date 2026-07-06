using GLTF.Schema;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Registers the view-context visibility-hint extension factories into the global <see cref="GLTFProperty"/>
    /// registry.
    ///
    /// Extension JSON is deserialized at parse time, which for editor and stream imports happens before any import
    /// plugin exists, so the factories are registered at load via the attributes below. The import plugin also
    /// handles the raw <see cref="DefaultExtension"/> fallback, so behaviour does not depend on registration timing.
    /// </summary>
    internal static class VisibilityHintSchemaRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#if UNITY_EDITOR
        [InitializeOnLoadMethod]
#endif
        internal static void Register()
        {
            GLTFProperty.TryRegisterExtension(new KHR_node_visibility_hint_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_mesh_primitive_visibility_hint_Factory());
        }
    }
}
