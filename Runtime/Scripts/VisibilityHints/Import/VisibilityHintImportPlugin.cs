using UnityGLTF.Plugins;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Import plugin for the view-context visibility-hint extensions (<c>KHR_node_visibility_hint</c> and
    /// <c>KHR_mesh_primitive_visibility_hint</c>). Generalized — it applies to any asset carrying the hints and is
    /// not gated on character detection. Disabled by default and marked non-ratified until the extensions are
    /// ratified. Composes on top of core <c>KHR_node_visibility</c> (which this plugin never reimplements).
    /// </summary>
    [NonRatifiedPlugin("KHR_node_visibility_hint / KHR_mesh_primitive_visibility_hint — view-context visibility hints (not yet ratified).")]
    public class VisibilityHintImportPlugin : GLTFImportPlugin
    {
        public override string DisplayName => "KHR Visibility Hints (View Context)";
        public override string Description =>
            "Imports KHR_node_visibility_hint and KHR_mesh_primitive_visibility_hint (first/third-person view-context " +
            "visibility) and presents them at runtime via a ViewContextController. Composes on top of core KHR_node_visibility.";

        public override bool EnabledByDefault => false;

        public override GLTFImportPluginContext CreateInstance(GLTFImportContext context)
            => new VisibilityHintImportContext(context);
    }
}
