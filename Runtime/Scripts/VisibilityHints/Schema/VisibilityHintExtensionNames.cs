using System;
using GLTF.Schema;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Canonical identifiers for the visibility-hint extensions and the standard role vocabulary. Roles are exact and
    /// case-sensitive. Any other role, including a differently cased standard token such as <c>"First_Person"</c>,
    /// is a custom role, which a host ignores unless it recognizes it (see <c>VisibilityRoleContext</c>).
    /// </summary>
    public static class VisibilityHintExtensionNames
    {
        public const string NodeVisibilityHint = KHR_node_visibility_hint.EXTENSION_NAME;
        public const string MeshPrimitiveVisibilityHint = KHR_mesh_primitive_visibility_hint.EXTENSION_NAME;

        // Standard role vocabulary (custom values are also permitted).
        public const string RoleAlways = "always";
        public const string RoleFirstPerson = "first_person";
        public const string RoleThirdPerson = "third_person";

        /// <summary>True for <c>always</c>, <c>first_person</c> and <c>third_person</c>, compared ordinally.</summary>
        public static bool IsStandardRole(string role)
            => string.Equals(role, RoleAlways, StringComparison.Ordinal)
               || string.Equals(role, RoleFirstPerson, StringComparison.Ordinal)
               || string.Equals(role, RoleThirdPerson, StringComparison.Ordinal);
    }
}
