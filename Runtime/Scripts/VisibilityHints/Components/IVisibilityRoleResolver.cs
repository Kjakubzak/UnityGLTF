using System.Collections.Generic;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Host hook for custom visibility roles that a separate specification or application profile defines. The
    /// standard roles never reach a resolver.
    /// </summary>
    public interface IVisibilityRoleResolver
    {
        /// <summary>
        /// Returns true when this resolver supports <paramref name="role"/>, and sets <paramref name="visible"/> to the
        /// role's visibility in a render view that carries <paramref name="viewTags"/>. Returning false leaves the role
        /// unsupported, so the caller applies the role-support rule.
        /// </summary>
        bool TryResolve(string role, IReadOnlyCollection<string> viewTags, out bool visible);
    }
}
