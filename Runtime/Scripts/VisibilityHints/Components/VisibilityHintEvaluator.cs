using System;
using System.Collections.Generic;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Pure visibility-hint predicates for one render view, identified by the view tags the host gives it.
    /// </summary>
    public static class VisibilityHintEvaluator
    {
        private static readonly string[] NoTags = Array.Empty<string>();

        /// <summary>
        /// Evaluates one resolved role for a render view. A null role (no hint) and <c>always</c> are visible in every
        /// view, including an untagged one. <c>first_person</c> and <c>third_person</c> are visible only when the view
        /// carries the same tag. Any other role is custom: <paramref name="resolver"/> decides when it supports the
        /// role, and otherwise the role is unsupported and visible only when <paramref name="renderUnsupportedRoles"/>
        /// is set. Roles and tags are compared ordinally.
        /// </summary>
        public static bool IsRoleVisible(
            string role,
            IReadOnlyCollection<string> viewTags,
            IVisibilityRoleResolver resolver = null,
            bool renderUnsupportedRoles = false)
        {
            switch (role)
            {
                case null:
                case VisibilityHintExtensionNames.RoleAlways:
                    return true;
                case VisibilityHintExtensionNames.RoleFirstPerson:
                case VisibilityHintExtensionNames.RoleThirdPerson:
                    return HasTag(viewTags, role);
                default:
                    if (resolver != null && resolver.TryResolve(role, viewTags ?? NoTags, out bool visible))
                        return visible;
                    return renderUnsupportedRoles;
            }
        }

        /// <summary><c>renderNodeVisualContent = hintVisible AND coreVisible</c> for one node instance and view.</summary>
        public static bool ShouldRenderNodeVisualContent(
            string resolvedNodeRole,
            IReadOnlyCollection<string> viewTags,
            bool ancestorInclusiveCoreVisible,
            IVisibilityRoleResolver resolver = null,
            bool renderUnsupportedRoles = false)
            => ancestorInclusiveCoreVisible
               && IsRoleVisible(resolvedNodeRole, viewTags, resolver, renderUnsupportedRoles);

        /// <summary>
        /// <c>renderPrimitiveInstance = primitiveHintVisible AND nodeHintVisible AND coreVisible</c> for one primitive
        /// instance and view.
        /// </summary>
        public static bool ShouldRenderPrimitiveInstance(
            string resolvedNodeRole,
            string primitiveRole,
            IReadOnlyCollection<string> viewTags,
            bool ancestorInclusiveCoreVisible,
            IVisibilityRoleResolver resolver = null,
            bool renderUnsupportedRoles = false)
            => ancestorInclusiveCoreVisible
               && IsRoleVisible(resolvedNodeRole, viewTags, resolver, renderUnsupportedRoles)
               && IsRoleVisible(primitiveRole, viewTags, resolver, renderUnsupportedRoles);

        // Iterates instead of calling Contains so a caller's case-insensitive set cannot loosen the exact comparison.
        private static bool HasTag(IReadOnlyCollection<string> viewTags, string tag)
        {
            if (viewTags == null) return false;
            foreach (var candidate in viewTags)
                if (string.Equals(candidate, tag, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
