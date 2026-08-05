using UnityGLTF.Plugins;
using UnityEngine;

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
        [SerializeField, Tooltip("Enable only when the host provides complete required-use behavior for node visibility hints, including non-renderer node visuals.")]
        private bool _hostSupportsRequiredNodeUse;
        [SerializeField, Tooltip("Enable only when the host provides complete required-use behavior for primitive visibility hints in every visual pass.")]
        private bool _hostSupportsRequiredPrimitiveUse;

        public bool HostSupportsRequiredNodeUse
        {
            get => _hostSupportsRequiredNodeUse;
            set => _hostSupportsRequiredNodeUse = value;
        }

        public bool HostSupportsRequiredPrimitiveUse
        {
            get => _hostSupportsRequiredPrimitiveUse;
            set => _hostSupportsRequiredPrimitiveUse = value;
        }

        public override string DisplayName => "KHR Visibility Hints (View Context)";
        public override string Description =>
            "Imports KHR_node_visibility_hint and KHR_mesh_primitive_visibility_hint (first/third-person view-context " +
            "visibility) as non-mutating per-view predicates. Composes with core KHR_node_visibility.";

        public override bool EnabledByDefault => false;

        public override GLTFImportPluginContext CreateInstance(GLTFImportContext context)
            => new VisibilityHintImportContext(
                context, _hostSupportsRequiredNodeUse, _hostSupportsRequiredPrimitiveUse);
    }
}
