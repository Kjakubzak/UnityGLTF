using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Scene-root component holding the authored <c>KHR_node_visibility_hint</c> entries (one per hinted node) and
    /// resolving them onto renderers. A node hint applies to the node <b>and its subtree</b>; a hint on a descendant
    /// overrides an ancestor's hint for that descendant's subtree. Resolution walks each renderer up to the nearest
    /// authored ancestor-or-self and registers the renderer with the <see cref="ViewContextController"/> using that
    /// role.
    ///
    /// <para>The entry list is serialized so an editor-imported prefab rehydrates on <see cref="Awake"/>; a live
    /// import instead calls <see cref="Bind"/> directly.</para>
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
        }

        // Baked data, surfaced only through Entries. HideInInspector: it is authored/imported, not hand-edited.
        [SerializeField, HideInInspector] private List<NodeVisibilityEntry> _entries = new List<NodeVisibilityEntry>();

        public IReadOnlyList<NodeVisibilityEntry> Entries => _entries;

        // Runtime-only (not serialized): reset to false on an Instantiate/deserialize clone so Awake re-resolves.
        private bool _resolved;

        /// <summary>Bind authored entries (live import) and immediately resolve them onto renderers.</summary>
        public void Bind(IReadOnlyList<NodeVisibilityEntry> entries)
        {
            _entries = entries != null ? new List<NodeVisibilityEntry>(entries) : new List<NodeVisibilityEntry>();
            _resolved = false;
            Resolve();
        }

        // Rehydrate a deserialized prefab. A live import calls Bind itself (which sets _resolved), so this only
        // fires for a clone/prefab whose _resolved reset to false.
        private void Awake()
        {
            if (!_resolved) Resolve();
        }

        private void Resolve()
        {
            if (_resolved) return;
            if (_entries == null || _entries.Count == 0) return; // stay unresolved so a later Bind still runs
            _resolved = true;

            var controller = GetComponent<ViewContextController>();
            if (controller == null) controller = gameObject.AddComponent<ViewContextController>();

            var roleByNode = new Dictionary<Transform, string>();
            foreach (var e in _entries)
                if (e != null && e.Node != null && !roleByNode.ContainsKey(e.Node))
                    roleByNode[e.Node] = e.Role;

            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                var role = ResolveRoleFor(renderer.transform, roleByNode);
                if (role == null) continue; // no hint applies to this renderer -> leave it at its default visibility
                var viewRole = ViewContextController.ParseRole(role);
                if (viewRole == ViewContextController.ViewRole.Both) continue; // always visible -> nothing to manage
                controller.RegisterRenderer(renderer, viewRole);
            }
        }

        // Nearest authored ancestor-or-self wins (descendant hint overrides ancestor). Walk stops at the scene root
        // this component lives on.
        private string ResolveRoleFor(Transform t, Dictionary<Transform, string> roleByNode)
        {
            for (var cur = t; cur != null; cur = cur.parent)
            {
                if (roleByNode.TryGetValue(cur, out var role)) return role;
                if (cur == transform) break;
            }
            return null;
        }
    }
}
