using GLTF.Schema;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Canonical identifiers for the visibility-hint extensions, the standard <c>role</c> vocabulary and the standard
    /// view tags. Roles and tags are exact and case-sensitive. Any other role, including a differently cased standard
    /// token such as <c>"First_Person"</c>, is a custom role; a consumer that does not support it hides its content by
    /// default (see <see cref="VisibilityHintEvaluator.IsRoleVisible"/>).
    /// </summary>
    public static class VisibilityHintExtensionNames
    {
        public const string NodeVisibilityHint = KHR_node_visibility_hint.EXTENSION_NAME;
        public const string MeshPrimitiveVisibilityHint = KHR_mesh_primitive_visibility_hint.EXTENSION_NAME;

        // Standard role vocabulary (custom values are also permitted).
        public const string RoleAlways = "always";
        public const string RoleFirstPerson = "first_person";
        public const string RoleThirdPerson = "third_person";

        /// <summary>Standard view tag for a view presented from the character's own viewpoint.</summary>
        public const string ViewTagFirstPerson = "first_person";

        /// <summary>Standard view tag for a third-person view of the character, such as a spectator or mirror view.</summary>
        public const string ViewTagThirdPerson = "third_person";
    }
}
