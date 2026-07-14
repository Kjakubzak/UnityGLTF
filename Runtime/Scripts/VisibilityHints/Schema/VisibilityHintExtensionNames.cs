using GLTF.Schema;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Canonical identifiers for the view-context visibility-hint extensions and the standard <c>role</c>
    /// vocabulary. Custom role strings are allowed by the spec; the runtime treats any unrecognized role as
    /// <c>always</c> (see <see cref="ViewContextController.ParseRole"/>).
    /// </summary>
    public static class VisibilityHintExtensionNames
    {
        public const string NodeVisibilityHint = KHR_node_visibility_hint.EXTENSION_NAME;
        public const string MeshPrimitiveVisibilityHint = KHR_mesh_primitive_visibility_hint.EXTENSION_NAME;

        // Standard role vocabulary (custom values are also permitted).
        public const string RoleAlways = "always";
        public const string RoleFirstPerson = "first_person";
        public const string RoleThirdPerson = "third_person";
    }
}
