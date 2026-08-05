using System;
using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Non-mutating facade for evaluating imported node and primitive visibility hints. A render integration can
    /// query an explicit context independently for each view and instance, then omit content whose predicate is
    /// false. This component does not change renderers, materials, meshes, cameras, or authored visibility state.
    /// </summary>
    [DisallowMultipleComponent]
    public class ViewContextController : MonoBehaviour
    {
        [SerializeField] private bool _hasActiveContext;
        [SerializeField] private string _activeContext;

        public event Action<string> OnViewContextChanged;

        /// <summary>Host-selected convenience context, or null when no context is supplied.</summary>
        public string ActiveContext => _hasActiveContext ? _activeContext : null;

        public bool HasActiveContext => _hasActiveContext;

        public void SetActiveContext(string context)
        {
            bool hasContext = context != null;
            if (_hasActiveContext == hasContext && (!_hasActiveContext || _activeContext == context)) return;
            _hasActiveContext = hasContext;
            _activeContext = hasContext ? context : null;
            OnViewContextChanged?.Invoke(ActiveContext);
        }

        public void ClearActiveContext() => SetActiveContext(null);

        public bool ShouldRenderNode(Transform node, bool ancestorInclusiveCoreVisible)
            => ShouldRenderNodeForContext(node, ActiveContext, ancestorInclusiveCoreVisible);

        public bool ShouldRenderNodeForContext(
            Transform node, string context, bool ancestorInclusiveCoreVisible)
        {
            var nodeHints = GetComponent<NodeVisibilityHintSet>();
            var role = nodeHints != null ? nodeHints.ResolveRole(node) : null;
            return VisibilityHintEvaluator.ShouldRenderNodeVisualContent(
                role, context, ancestorInclusiveCoreVisible);
        }

        public bool ShouldRenderPrimitive(
            Renderer renderer, int subMesh, bool ancestorInclusiveCoreVisible)
            => ShouldRenderPrimitiveForContext(
                renderer, subMesh, ActiveContext, ancestorInclusiveCoreVisible);

        public bool ShouldRenderPrimitiveForContext(
            Renderer renderer, int subMesh, string context, bool ancestorInclusiveCoreVisible)
        {
            var nodeHints = GetComponent<NodeVisibilityHintSet>();
            var primitiveHints = GetComponent<PrimitiveVisibilityHintSet>();
            var nodeRole = nodeHints != null ? nodeHints.ResolveRole(renderer != null ? renderer.transform : null) : null;
            var primitiveRole = primitiveHints != null ? primitiveHints.ResolveRole(renderer, subMesh) : null;
            return VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(
                nodeRole, primitiveRole, context, ancestorInclusiveCoreVisible);
        }
    }
}
