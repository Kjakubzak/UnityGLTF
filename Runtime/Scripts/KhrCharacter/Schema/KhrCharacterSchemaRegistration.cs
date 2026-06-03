using GLTF.Schema;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Registers the KHR Character extension factories (and accepted alias spellings) into the global
    /// <see cref="GLTFProperty"/> registry.
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
            // Canonical factories.
            GLTFProperty.TryRegisterExtension(new KHR_character_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_morphtarget_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_joint_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_texture_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_mapping_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_expression_mask_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_reference_pose_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_character_skeleton_mapping_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_node_camera_hint_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_node_lookat_target_Factory());

            // Accepted alias spellings (same factory type, registered under the alias name).
            RegisterAlias(new KHR_character_expression_mask_Factory(),        "KHR_character_expression_masks");        // plural
            RegisterAlias(new KHR_character_expression_morphtarget_Factory(), "KHR_character_expression_morphtargets"); // plural
            RegisterAlias(new KHR_character_expression_joint_Factory(),       "KHR_character_expression_joints");       // plural
            RegisterAlias(new KHR_character_expression_morphtarget_Factory(), "KHR_avatar_expression_morphtarget");     // alternate namespace
            RegisterAlias(new KHR_character_expression_joint_Factory(),       "KHR_avatar_expression_joint");           // alternate namespace
            RegisterAlias(new KHR_character_expression_texture_Factory(),     "KHR_avatar_expression_texture");         // alternate namespace
            RegisterAlias(new KHR_character_reference_pose_Factory(),         "KHR_character_bindpose");                // former name
            RegisterAlias(new KHR_character_reference_pose_Factory(),         "KHR_character_skeleton_bindpose");       // former name
        }

        private static void RegisterAlias(ExtensionFactory factory, string aliasName)
        {
            // The registry key is the alias; the factory still deserializes to the canonical extension type.
            factory.ExtensionName = aliasName;
            if (!GLTFProperty.TryRegisterExtension(factory))
                Debug.LogWarning($"[KHR_character] Alias '{aliasName}' is already registered; skipping.");
        }
    }
}
