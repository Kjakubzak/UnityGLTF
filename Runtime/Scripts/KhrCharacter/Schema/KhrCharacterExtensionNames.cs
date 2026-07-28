using System.Collections.Generic;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Canonical KHR Character/Avatar extension identifiers (glTF PR #2512) and a closed table of accepted
    /// alternate spellings. Only known spellings are mapped to a canonical name; unknown names are returned
    /// unchanged. VRMC_* identifiers are intentionally kept separate from KHR_*.
    /// </summary>
    public static class KhrCharacterExtensionNames
    {
        public const string Character             = "KHR_character";
        public const string XmpJsonLd             = "KHR_xmp_json_ld";
        public const string Expression            = "KHR_character_expression";
        public const string ExpressionMorphtarget = "KHR_character_expression_morphtarget";
        public const string ExpressionJoint       = "KHR_character_expression_joint";
        public const string ExpressionTexture     = "KHR_character_expression_texture";
        public const string ExpressionMapping     = "KHR_character_expression_mapping";
        public const string ExpressionMask        = "KHR_character_expression_mask";
        public const string ReferencePose         = "KHR_character_reference_pose";
        public const string SkeletonMapping       = "KHR_character_skeleton_mapping";
        public const string NodeCameraHint        = "KHR_node_camera_hint";
        public const string NodeLookatTarget      = "KHR_node_lookat_target";

        /// <summary>VRM companion gaze extension (not one of the KHR extensions). Recognized, kept separate.</summary>
        public const string VrmcExpressionLookat  = "VRMC_character_expression_lookat";

        // Accepted alternate spelling -> canonical. Unknown spellings are returned unchanged.
        private static readonly Dictionary<string, string> _aliases = new Dictionary<string, string>
        {
            { "KHR_character_expression_masks",        ExpressionMask },        // plural spelling
            { "KHR_character_expression_morphtargets", ExpressionMorphtarget }, // plural spelling
            { "KHR_character_expression_joints",       ExpressionJoint },       // plural spelling
            { "KHR_avatar_expression_morphtarget",     ExpressionMorphtarget }, // alternate namespace
            { "KHR_avatar_expression_joint",           ExpressionJoint },       // alternate namespace
            { "KHR_avatar_expression_texture",         ExpressionTexture },     // alternate namespace
            { "KHR_character_bindpose",                ReferencePose },         // former name
            { "KHR_character_skeleton_bindpose",       ReferencePose },         // former name
        };

        /// <summary>Canonical name for a known alias, or the input unchanged. Never folds unknown spellings.</summary>
        public static string Canonicalize(string name)
        {
            if (name == null) return null;
            return _aliases.TryGetValue(name, out var canonical) ? canonical : name;
        }

        /// <summary>True if the (canonicalized) name is one of the KHR character/avatar extensions.</summary>
        public static bool IsCharacterExtension(string name)
        {
            var c = Canonicalize(name);
            return c == Character || c == Expression || c == ExpressionMorphtarget || c == ExpressionJoint
                || c == ExpressionTexture || c == ExpressionMapping || c == ExpressionMask
                || c == ReferencePose || c == SkeletonMapping || c == NodeCameraHint || c == NodeLookatTarget;
        }
    }
}
