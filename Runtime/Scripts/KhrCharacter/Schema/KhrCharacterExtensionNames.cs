namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Canonical KHR Character extension identifiers (glTF PR #2512). Names are compared exactly; former and
    /// alternate spellings are not recognized and import as unknown extensions. VRMC_* identifiers are
    /// intentionally kept separate from KHR_*.
    /// </summary>
    public static class KhrCharacterExtensionNames
    {
        public const string Character                  = "KHR_character";
        public const string XmpJsonLd                  = "KHR_xmp_json_ld";
        public const string Expression                 = "KHR_character_expression";
        public const string ExpressionMorphtarget      = "KHR_character_expression_morphtarget";
        public const string ExpressionJoint            = "KHR_character_expression_joint";
        public const string ExpressionTextureTransform = "KHR_character_expression_texture_transform";
        public const string ExpressionMapping          = "KHR_character_expression_mapping";
        public const string ExpressionSuppression      = "KHR_character_expression_suppression";
        public const string ReferencePose              = "KHR_character_reference_pose";
        public const string SkeletonMapping            = "KHR_character_skeleton_mapping";
        public const string NodeCameraHint             = "KHR_node_camera_hint";
        public const string NodeLookatTarget           = "KHR_node_lookat_target";

        /// <summary>VRM companion gaze extension (not one of the KHR extensions). Recognized, kept separate.</summary>
        public const string VrmcExpressionLookat  = "VRMC_character_expression_lookat";

        /// <summary>
        /// Returns <paramref name="name"/> unchanged. Extension names are exact, so no spelling is folded onto a
        /// canonical name.
        /// </summary>
        public static string Canonicalize(string name) => name;

        /// <summary>True if the name is exactly one of the KHR character extensions.</summary>
        public static bool IsCharacterExtension(string name)
        {
            return name == Character || name == Expression || name == ExpressionMorphtarget || name == ExpressionJoint
                || name == ExpressionTextureTransform || name == ExpressionMapping || name == ExpressionSuppression
                || name == ReferencePose || name == SkeletonMapping || name == NodeCameraHint || name == NodeLookatTarget;
        }
    }
}
