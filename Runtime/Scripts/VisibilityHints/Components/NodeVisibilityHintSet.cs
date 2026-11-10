using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Serialized authored hints and pure ancestor-inclusive visibility queries per node instance and render view.
    /// Authored roles remain available independently of the cumulative Boolean rendering result.
    /// </summary>
    [DisallowMultipleComponent]
    public class NodeVisibilityHintSet : MonoBehaviour
    {
        [System.Serializable]
        public class NodeVisibilityEntry
        {
            public Transform Node;

            /// <summary>
            /// True when the entry was imported from a glTF node without a nonempty <c>name</c>. UnityGLTF names such
            /// a node's GameObject <c>GLTFNode&lt;index&gt;</c>, which is not a node name, so presentation skips it.
            /// </summary>
            public bool NodeUnnamed;

            /// <summary>The authored roles (alternatives; order has no meaning).</summary>
            public string[] Roles;
            public string Label;
            public string ExtensionsJson;
            public string ExtrasJson;
            public string AdditionalPropertiesJson;
            public string[] RequiredCompanionExtensions;
        }

        [SerializeField, HideInInspector] private List<NodeVisibilityEntry> _entries = new List<NodeVisibilityEntry>();
        [System.NonSerialized] private Dictionary<Transform, NodeVisibilityEntry> _entryByNode;
        [System.NonSerialized] private bool _indexDirty = true;

        public IReadOnlyList<NodeVisibilityEntry> Entries => _entries;

        public void Bind(IReadOnlyList<NodeVisibilityEntry> entries)
        {
            _entries = entries != null ? new List<NodeVisibilityEntry>(entries) : new List<NodeVisibilityEntry>();
            _indexDirty = true;
        }

        /// <summary>Rebuilds key lookup after runtime code changes an entry's node reference.</summary>
        public void RefreshIndex() => _indexDirty = true;

        private void OnValidate() => _indexDirty = true;

        private void EnsureIndex()
        {
            if (!_indexDirty && _entryByNode != null) return;
            _entryByNode = new Dictionary<Transform, NodeVisibilityEntry>();
            if (_entries != null)
                foreach (var entry in _entries)
                    if (entry?.Node != null) _entryByNode[entry.Node] = entry;
            _indexDirty = false;
        }

        /// <summary>Nearest authored roles for metadata presentation; not the effective rendering predicate.</summary>
        public IReadOnlyList<string> ResolveRoles(Transform node)
        {
            if (node == null || (node != transform && !node.IsChildOf(transform))) return null;
            EnsureIndex();
            for (var current = node; current != null; current = current.parent)
            {
                if (_entryByNode.TryGetValue(current, out var entry)) return entry.Roles;
                if (current == transform) break;
            }
            return null;
        }

        /// <summary>
        /// <c>hintVisible</c>: ANDs the hint of this node and of every ancestor within this imported instance root for
        /// one view, whose role logic is <paramref name="roles"/> (null means
        /// <see cref="VisibilityRoleContext.NoLogic"/>).
        /// </summary>
        public bool IsHintVisible(Transform node, VisibilityRoleContext roles)
        {
            if (node == null || (node != transform && !node.IsChildOf(transform))) return true;
            EnsureIndex();
            for (var current = node; current != null; current = current.parent)
            {
                if (_entryByNode.TryGetValue(current, out var entry)
                    && !VisibilityHintEvaluator.HintAllows(entry.Roles, roles))
                    return false;
                if (current == transform) break;
            }
            return true;
        }

        public bool ShouldRenderVisualContent(
            Transform node, VisibilityRoleContext roles, bool ancestorInclusiveCoreVisible)
            => ancestorInclusiveCoreVisible && IsHintVisible(node, roles);

        /// <summary>
        /// Presentation name for an entry: its label, else the node's nonempty name, else its roles. The node name is
        /// the GameObject's name, except for an entry imported from an unnamed glTF node
        /// (<see cref="NodeVisibilityEntry.NodeUnnamed"/>). Export still writes the synthesized GameObject name back.
        /// </summary>
        public static string GetDisplayName(NodeVisibilityEntry entry)
        {
            if (entry == null) return null;
            if (!string.IsNullOrEmpty(entry.Label)) return entry.Label;
            if (!entry.NodeUnnamed && entry.Node != null && !string.IsNullOrEmpty(entry.Node.name))
                return entry.Node.name;
            return FormatRoles(entry.Roles);
        }

        /// <summary>The roles joined with ", "; null when there are none.</summary>
        public static string FormatRoles(IReadOnlyList<string> roles)
        {
            if (roles == null || roles.Count == 0) return null;
            var parts = new List<string>();
            foreach (var role in roles)
                if (!string.IsNullOrEmpty(role)) parts.Add(role);
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
    }
}
