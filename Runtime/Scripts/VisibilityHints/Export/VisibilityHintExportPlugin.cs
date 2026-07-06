using UnityGLTF.Plugins;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Export plugin for the view-context visibility-hint extensions (<c>KHR_node_visibility_hint</c> and
    /// <c>KHR_mesh_primitive_visibility_hint</c>). Emits hints from the authored <see cref="NodeVisibilityHintSet"/>
    /// and <see cref="PrimitiveVisibilityHintSet"/> entries (never from live material/renderer state, so a slot
    /// currently swapped to the invisible material still round-trips). Disabled by default and marked non-ratified.
    /// </summary>
    [NonRatifiedPlugin("KHR_node_visibility_hint / KHR_mesh_primitive_visibility_hint — view-context visibility hints (not yet ratified).")]
    public class VisibilityHintExportPlugin : GLTFExportPlugin
    {
        public override string DisplayName => "KHR Visibility Hints (View Context)";
        public override string Description =>
            "Exports KHR_node_visibility_hint and KHR_mesh_primitive_visibility_hint from the authored VisibilityHints " +
            "components. Declared used (never required); composes on top of core KHR_node_visibility.";

        public override bool EnabledByDefault => false;

        public override GLTFExportPluginContext CreateInstance(ExportContext context)
            => new VisibilityHintExportContext();
    }
}
