using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Non-mutating facade for evaluating imported node and primitive visibility hints. The host gives each render
    /// view zero or more view tags; a render integration queries each view and instance with its tags, then omits
    /// content whose predicate is false. This component does not change renderers, materials, meshes, lights,
    /// cameras, or authored visibility state.
    /// </summary>
    [DisallowMultipleComponent]
    public class ViewContextController : MonoBehaviour
    {
        [SerializeField] private List<string> _defaultViewTags = new List<string>();
        [SerializeField] private bool _renderUnsupportedRoles;

        /// <summary>Raised with the new tag list when <see cref="SetDefaultViewTags"/> changes it.</summary>
        public event Action<IReadOnlyList<string>> OnDefaultViewTagsChanged;

        /// <summary>
        /// View tags used by the convenience queries <see cref="ShouldRenderNode"/> and
        /// <see cref="ShouldRenderPrimitive"/>. Empty by default, which is an untagged view: only <c>always</c> and
        /// un-hinted content is visible there.
        /// </summary>
        public IReadOnlyList<string> DefaultViewTags
            => _defaultViewTags ?? (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>
        /// Application override of the role-support rule. When false (the default), content whose resolved role is
        /// unsupported (a custom role that <see cref="CustomRoleResolver"/> does not resolve) is hidden.
        /// </summary>
        public bool RenderUnsupportedRoles
        {
            get => _renderUnsupportedRoles;
            set => _renderUnsupportedRoles = value;
        }

        /// <summary>
        /// Optional resolver for custom roles defined by a separate specification or application profile. Not
        /// serialized; the host assigns it at runtime.
        /// </summary>
        public IVisibilityRoleResolver CustomRoleResolver { get; set; }

        /// <summary>
        /// Replaces the default view tags. Null and empty strings are ignored and duplicates are kept once, in order.
        /// Raises <see cref="OnDefaultViewTagsChanged"/> only when the list changes.
        /// </summary>
        public void SetDefaultViewTags(IEnumerable<string> tags)
        {
            var next = new List<string>();
            if (tags != null)
                foreach (var tag in tags)
                    if (!string.IsNullOrEmpty(tag) && !next.Contains(tag)) next.Add(tag);

            if (_defaultViewTags != null && _defaultViewTags.Count == next.Count)
            {
                bool same = true;
                for (int i = 0; i < next.Count && same; i++)
                    same = string.Equals(_defaultViewTags[i], next[i], StringComparison.Ordinal);
                if (same) return;
            }
            _defaultViewTags = next;
            OnDefaultViewTagsChanged?.Invoke(next);
        }

        /// <summary>Node predicate for a view that carries <see cref="DefaultViewTags"/>.</summary>
        public bool ShouldRenderNode(Transform node, bool ancestorInclusiveCoreVisible)
            => ShouldRenderNodeForView(node, DefaultViewTags, ancestorInclusiveCoreVisible);

        /// <summary>
        /// Node predicate for one render view with explicit tags. Tags that differ per asset instance are passed
        /// with each query.
        /// </summary>
        public bool ShouldRenderNodeForView(
            Transform node, IReadOnlyCollection<string> viewTags, bool ancestorInclusiveCoreVisible)
        {
            var nodeHints = GetComponent<NodeVisibilityHintSet>();
            return ancestorInclusiveCoreVisible && (nodeHints == null
                || nodeHints.IsHintVisible(node, viewTags, CustomRoleResolver, _renderUnsupportedRoles));
        }

        /// <summary>Primitive-instance predicate for a view that carries <see cref="DefaultViewTags"/>.</summary>
        public bool ShouldRenderPrimitive(
            Renderer renderer, int subMesh, bool ancestorInclusiveCoreVisible)
            => ShouldRenderPrimitiveForView(renderer, subMesh, DefaultViewTags, ancestorInclusiveCoreVisible);

        /// <summary>Primitive-instance predicate for one render view with explicit tags.</summary>
        public bool ShouldRenderPrimitiveForView(
            Renderer renderer,
            int subMesh,
            IReadOnlyCollection<string> viewTags,
            bool ancestorInclusiveCoreVisible)
        {
            var nodeHints = GetComponent<NodeVisibilityHintSet>();
            var primitiveHints = GetComponent<PrimitiveVisibilityHintSet>();
            bool nodeHintVisible = nodeHints == null || nodeHints.IsHintVisible(
                renderer != null ? renderer.transform : null, viewTags, CustomRoleResolver, _renderUnsupportedRoles);
            var primitiveRole = primitiveHints != null ? primitiveHints.ResolveRole(renderer, subMesh) : null;
            return VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(
                nodeHintVisible, primitiveRole, viewTags, ancestorInclusiveCoreVisible,
                CustomRoleResolver, _renderUnsupportedRoles);
        }
    }
}
