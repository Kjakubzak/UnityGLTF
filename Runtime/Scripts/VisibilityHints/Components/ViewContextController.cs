using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Non-mutating facade for evaluating imported node and primitive visibility hints. For each render view and
    /// asset instance the host supplies its role logic as a <see cref="VisibilityRoleContext"/> (which roles apply,
    /// and which roles it has logic for); a render integration queries each view and instance, then omits content
    /// whose predicate is false. This component does not change renderers, materials, meshes, lights, cameras, or
    /// authored visibility state.
    /// </summary>
    [DisallowMultipleComponent]
    public class ViewContextController : MonoBehaviour
    {
        [SerializeField] private List<string> _defaultAppliedRoles = new List<string>();
        [SerializeField] private List<string> _defaultRolesWithLogic = new List<string>();
        [NonSerialized] private VisibilityRoleContext _defaultRoles;

        /// <summary>Raised with the new context when <see cref="SetDefaultRoles"/> changes it.</summary>
        public event Action<VisibilityRoleContext> OnDefaultRolesChanged;

        /// <summary>
        /// Role logic used by the convenience queries <see cref="ShouldRenderNode"/> and
        /// <see cref="ShouldRenderPrimitive"/>. <see cref="VisibilityRoleContext.NoLogic"/> by default, so an
        /// application without role logic hides nothing.
        /// </summary>
        public VisibilityRoleContext DefaultRoles
            => _defaultRoles ?? (_defaultRoles = HasNoRoles(_defaultAppliedRoles) && HasNoRoles(_defaultRolesWithLogic)
                ? VisibilityRoleContext.NoLogic
                : new VisibilityRoleContext(_defaultAppliedRoles, _defaultRolesWithLogic));

        /// <summary>
        /// Replaces the default role logic; null restores <see cref="VisibilityRoleContext.NoLogic"/>. Raises
        /// <see cref="OnDefaultRolesChanged"/> only when the applied roles or the roles with logic change.
        /// </summary>
        public void SetDefaultRoles(VisibilityRoleContext roles)
        {
            roles = roles ?? VisibilityRoleContext.NoLogic;
            var current = DefaultRoles;
            if (SameSet(current.AppliedRoles, roles.AppliedRoles) && SameSet(current.RolesWithLogic, roles.RolesWithLogic))
                return;
            _defaultAppliedRoles = new List<string>(roles.AppliedRoles);
            var withLogic = new List<string>();
            foreach (var role in roles.RolesWithLogic)
                if (!_defaultAppliedRoles.Contains(role)) withLogic.Add(role);
            _defaultRolesWithLogic = withLogic;
            _defaultRoles = roles;
            OnDefaultRolesChanged?.Invoke(roles);
        }

        /// <summary>Node predicate for a view with the <see cref="DefaultRoles"/>.</summary>
        public bool ShouldRenderNode(Transform node, bool ancestorInclusiveCoreVisible)
            => ShouldRenderNodeForView(node, DefaultRoles, ancestorInclusiveCoreVisible);

        /// <summary>
        /// Node predicate for one render view and instance with explicit role logic (null means
        /// <see cref="VisibilityRoleContext.NoLogic"/>).
        /// </summary>
        public bool ShouldRenderNodeForView(
            Transform node, VisibilityRoleContext roles, bool ancestorInclusiveCoreVisible)
        {
            var nodeHints = GetComponent<NodeVisibilityHintSet>();
            return ancestorInclusiveCoreVisible && (nodeHints == null || nodeHints.IsHintVisible(node, roles));
        }

        /// <summary>Primitive-instance predicate for a view with the <see cref="DefaultRoles"/>.</summary>
        public bool ShouldRenderPrimitive(
            Renderer renderer, int subMesh, bool ancestorInclusiveCoreVisible)
            => ShouldRenderPrimitiveForView(renderer, subMesh, DefaultRoles, ancestorInclusiveCoreVisible);

        /// <summary>
        /// Primitive-instance predicate for one render view with explicit role logic: the primitive hint AND the
        /// containing node's ancestor-inclusive hint AND core visibility.
        /// </summary>
        public bool ShouldRenderPrimitiveForView(
            Renderer renderer,
            int subMesh,
            VisibilityRoleContext roles,
            bool ancestorInclusiveCoreVisible)
        {
            var nodeHints = GetComponent<NodeVisibilityHintSet>();
            var primitiveHints = GetComponent<PrimitiveVisibilityHintSet>();
            bool nodeHintVisible = nodeHints == null
                || nodeHints.IsHintVisible(renderer != null ? renderer.transform : null, roles);
            var primitiveRoles = primitiveHints != null ? primitiveHints.ResolveRoles(renderer, subMesh) : null;
            return VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(
                nodeHintVisible, primitiveRoles, roles, ancestorInclusiveCoreVisible);
        }

        private void OnValidate() => _defaultRoles = null;

        private static bool HasNoRoles(List<string> roles)
        {
            if (roles == null) return true;
            foreach (var role in roles)
                if (!string.IsNullOrEmpty(role)) return false;
            return true;
        }

        private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var role in a)
            {
                bool found = false;
                foreach (var other in b)
                    if (string.Equals(role, other, StringComparison.Ordinal)) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }
    }
}
