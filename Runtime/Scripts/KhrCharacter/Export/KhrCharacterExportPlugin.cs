using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Export plugin for the KHR Character/Avatar extension set (glTF PR #2512).
    /// Exports KHR_character_expression (with sub-extensions), KHR_character_skeleton_mapping,
    /// and KHR_character_reference_pose from a Unity character, plus independent passive camera-hint and
    /// look-at-target node annotations.
    /// 
    /// Disabled by default and marked non-ratified until the extension set is ratified.
    /// 
    /// Scope rule: The exporter writes whatever expressions are in the CharacterExpressionSet.
    /// The caller is responsible for putting only facial expressions (0→1 driven, no loop expectation)
    /// into the set. Body/locomotion animations should be exported via the standard UnityGLTF
    /// animation export path, not through this plugin.
    /// </summary>
    [NonRatifiedPlugin("KHR_character / avatar extension set (glTF PR #2512) — not yet ratified.")]
    public class KhrCharacterExportPlugin : GLTFExportPlugin
    {
        public override string DisplayName => "KHR Character / Avatar Extensions";
        public override string Description =>
            "Exports the Khronos Character/Avatar extension set (KHR_character_expression, " +
            "KHR_character_skeleton_mapping, KHR_character_reference_pose) and independent passive " +
            "camera/look-at node annotations. Facial expressions only; body animations export via standard glTF animation.";

        public override bool EnabledByDefault => false;

        public override GLTFExportPluginContext CreateInstance(ExportContext context)
            => new KhrCharacterExportContext(this, context);
    }
}
