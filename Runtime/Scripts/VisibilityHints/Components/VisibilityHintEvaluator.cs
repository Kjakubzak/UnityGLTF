namespace UnityGLTF.VisibilityHints
{
    /// <summary>Pure predicates for the standard view-context visibility-hint roles.</summary>
    public static class VisibilityHintEvaluator
    {
        public static bool IsRoleVisible(string role, string activeContext)
        {
            if (activeContext == null) return true;
            switch (role)
            {
                case VisibilityHintExtensionNames.RoleFirstPerson:
                    return activeContext == VisibilityHintExtensionNames.RoleFirstPerson;
                case VisibilityHintExtensionNames.RoleThirdPerson:
                    return activeContext != VisibilityHintExtensionNames.RoleFirstPerson;
                default:
                    return true;
            }
        }

        public static bool ShouldRenderNodeVisualContent(
            string resolvedNodeRole, string activeContext, bool ancestorInclusiveCoreVisible)
            => ancestorInclusiveCoreVisible && IsRoleVisible(resolvedNodeRole, activeContext);

        public static bool ShouldRenderPrimitiveInstance(
            string resolvedNodeRole,
            string primitiveRole,
            string activeContext,
            bool ancestorInclusiveCoreVisible)
            => ancestorInclusiveCoreVisible
               && IsRoleVisible(resolvedNodeRole, activeContext)
               && IsRoleVisible(primitiveRole, activeContext);
    }
}
