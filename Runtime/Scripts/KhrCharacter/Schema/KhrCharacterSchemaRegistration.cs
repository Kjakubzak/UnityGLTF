using GLTF.Schema;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Registers the KHR Character extension factories into the global <see cref="GLTFProperty"/> registry.
    /// Only the exact extension names are registered; former spellings deserialize as unknown extensions.
    ///
    /// Extension JSON is deserialized at parse time, which for editor and stream imports happens before any
    /// import plugin exists, so the factories are registered at load via the attributes below. The import
    /// plugin additionally detects characters from the raw extension JSON, so behaviour does not depend on
    /// registration timing.
    /// </summary>
    internal static class KhrCharacterSchemaRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#if UNITY_EDITOR
        [InitializeOnLoadMethod]
#endif
        internal static void Register()
        {
            GLTFProperty.TryRegisterExtension(new KHR_character_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_morphtarget_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_joint_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_texture_transform_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_mapping_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_suppression_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_reference_pose_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_skeleton_mapping_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_node_camera_hint_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_node_lookat_target_Factory());
        }
    }
}
