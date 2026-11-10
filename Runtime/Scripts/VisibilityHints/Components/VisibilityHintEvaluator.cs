using System.Collections.Generic;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Pure visibility-hint predicates for one render view and asset instance, whose role logic the host supplies as
    /// a <see cref="VisibilityRoleContext"/>. A null context means <see cref="VisibilityRoleContext.NoLogic"/>.
    /// </summary>
    public static class VisibilityHintEvaluator
    {
        /// <summary>
        /// Whether one hint allows the view (<c>ownHintVisible</c>): true for no hint (null or empty roles), when any
        /// recognized role applies, or when no role is recognized. See <see cref="VisibilityRoleContext.Allows"/>.
        /// </summary>
        public static bool HintAllows(IReadOnlyCollection<string> roles, VisibilityRoleContext context)
            => (context ?? VisibilityRoleContext.NoLogic).Allows(roles);

        /// <summary><c>renderNodeVisualContent = hintVisible AND coreVisible</c> for one node instance and view, where
        /// <paramref name="ancestorInclusiveHintVisible"/> is already the AND over the node and its ancestors.</summary>
        public static bool ShouldRenderNodeVisualContent(
            bool ancestorInclusiveHintVisible, bool ancestorInclusiveCoreVisible)
            => ancestorInclusiveHintVisible && ancestorInclusiveCoreVisible;

        /// <summary>
        /// <c>renderPrimitiveInstance = primitiveHintVisible AND nodeHintVisible AND coreVisible</c> for one primitive
        /// instance and view. <paramref name="nodeHintVisible"/> is the containing node's ancestor-inclusive hint
        /// result; a primitive hint can only add a restriction, even when it lists <c>always</c>.
        /// </summary>
        public static bool ShouldRenderPrimitiveInstance(
            bool nodeHintVisible,
            IReadOnlyCollection<string> primitiveRoles,
            VisibilityRoleContext context,
            bool ancestorInclusiveCoreVisible)
            => ancestorInclusiveCoreVisible && nodeHintVisible && HintAllows(primitiveRoles, context);
    }
}
