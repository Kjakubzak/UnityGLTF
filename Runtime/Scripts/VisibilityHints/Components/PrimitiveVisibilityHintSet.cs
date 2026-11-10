using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Serialized <c>KHR_mesh_primitive_visibility_hint</c> annotations. The annotation belongs to a shared mesh
    /// primitive, while complete visibility remains a per-containing-node, per-view query.
    /// </summary>
    [DisallowMultipleComponent]
    public class PrimitiveVisibilityHintSet : MonoBehaviour
    {
        [System.Serializable]
        public class PrimitiveVisibilityEntry
        {
            public Mesh Mesh;
            public int SubMesh;

            /// <summary>The authored roles (alternatives; order has no meaning).</summary>
            public string[] Roles;
            public string Label;
            public string ExtensionsJson;
            public string ExtrasJson;
            public string AdditionalPropertiesJson;
            public string[] RequiredCompanionExtensions;
        }

        [SerializeField, HideInInspector] private List<PrimitiveVisibilityEntry> _entries = new List<PrimitiveVisibilityEntry>();
        [System.NonSerialized]
        private Dictionary<(Mesh mesh, int subMesh), PrimitiveVisibilityEntry> _entryByPrimitive;
        [System.NonSerialized] private bool _indexDirty = true;

        public IReadOnlyList<PrimitiveVisibilityEntry> Entries => _entries;

        public void Bind(IReadOnlyList<PrimitiveVisibilityEntry> entries)
        {
            _entries = entries != null ? new List<PrimitiveVisibilityEntry>(entries) : new List<PrimitiveVisibilityEntry>();
            _indexDirty = true;
        }

        /// <summary>Rebuilds key lookup after runtime code changes an entry's mesh or sub-mesh reference.</summary>
        public void RefreshIndex() => _indexDirty = true;

        private void OnValidate() => _indexDirty = true;

        private void EnsureIndex()
        {
            if (!_indexDirty && _entryByPrimitive != null) return;
            _entryByPrimitive = new Dictionary<(Mesh, int), PrimitiveVisibilityEntry>();
            if (_entries != null)
                foreach (var entry in _entries)
                    if (entry?.Mesh != null && entry.SubMesh >= 0)
                        _entryByPrimitive[(entry.Mesh, entry.SubMesh)] = entry;
            _indexDirty = false;
        }

        public IReadOnlyList<string> ResolveRoles(Renderer renderer, int subMesh)
            => ResolveRoles(GetRendererMesh(renderer), subMesh);

        public IReadOnlyList<string> ResolveRoles(Mesh mesh, int subMesh)
        {
            if (mesh == null || subMesh < 0) return null;
            EnsureIndex();
            return _entryByPrimitive.TryGetValue((mesh, subMesh), out var entry) ? entry.Roles : null;
        }

        /// <summary>Uses the cumulative node hint result for the containing instance.</summary>
        public bool ShouldRenderPrimitiveInstance(
            Renderer renderer,
            int subMesh,
            bool nodeHintVisible,
            VisibilityRoleContext roles,
            bool ancestorInclusiveCoreVisible)
            => VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(
                nodeHintVisible, ResolveRoles(renderer, subMesh), roles, ancestorInclusiveCoreVisible);

        /// <summary>Presentation name for an entry: its label, else its roles.</summary>
        public static string GetDisplayName(PrimitiveVisibilityEntry entry)
        {
            if (entry == null) return null;
            return !string.IsNullOrEmpty(entry.Label) ? entry.Label : NodeVisibilityHintSet.FormatRoles(entry.Roles);
        }

        private static Mesh GetRendererMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
