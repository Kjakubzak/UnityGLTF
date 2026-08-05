using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Serialized <c>KHR_node_visibility_hint</c> annotations and their nearest-ancestor resolution. Resolution is
    /// performed as a pure query so a descendant hint can replace an inherited hint independently for each node
    /// instance and render view.
    /// </summary>
    [DisallowMultipleComponent]
    public class NodeVisibilityHintSet : MonoBehaviour
    {
        [System.Serializable]
        public class NodeVisibilityEntry
        {
            public Transform Node;
            public string Role;
            public string Label;
            public string ExtensionsJson;
            public string ExtrasJson;
            public string AdditionalPropertiesJson;
            public string[] RequiredCompanionExtensions;
        }

        [SerializeField, HideInInspector] private List<NodeVisibilityEntry> _entries = new List<NodeVisibilityEntry>();
        [SerializeField, HideInInspector] private bool _requiredOnImport;
        [System.NonSerialized] private Dictionary<Transform, NodeVisibilityEntry> _entryByNode;
        [System.NonSerialized] private bool _indexDirty = true;

        public IReadOnlyList<NodeVisibilityEntry> Entries => _entries;
        public bool RequiredOnImport => _requiredOnImport;

        public void Bind(IReadOnlyList<NodeVisibilityEntry> entries, bool requiredOnImport = false)
        {
            _entries = entries != null ? new List<NodeVisibilityEntry>(entries) : new List<NodeVisibilityEntry>();
            _requiredOnImport = requiredOnImport;
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

        public string ResolveRole(Transform node)
        {
            if (node == null || (node != transform && !node.IsChildOf(transform))) return null;
            EnsureIndex();
            for (var current = node; current != null; current = current.parent)
            {
                if (_entryByNode.TryGetValue(current, out var entry)) return entry.Role;
                if (current == transform) break;
            }
            return null;
        }

        public bool ShouldRenderVisualContent(
            Transform node, string activeContext, bool ancestorInclusiveCoreVisible)
            => VisibilityHintEvaluator.ShouldRenderNodeVisualContent(
                ResolveRole(node), activeContext, ancestorInclusiveCoreVisible);
    }
}
