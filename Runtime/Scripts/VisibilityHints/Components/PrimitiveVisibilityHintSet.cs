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
            public string Role;
            public string Label;
            public string ExtensionsJson;
            public string ExtrasJson;
            public string AdditionalPropertiesJson;
            public string[] RequiredCompanionExtensions;
        }

        [SerializeField, HideInInspector] private List<PrimitiveVisibilityEntry> _entries = new List<PrimitiveVisibilityEntry>();
        [SerializeField, HideInInspector] private bool _requiredOnImport;
        [System.NonSerialized]
        private Dictionary<(Mesh mesh, int subMesh), PrimitiveVisibilityEntry> _entryByPrimitive;
        [System.NonSerialized] private bool _indexDirty = true;

        public IReadOnlyList<PrimitiveVisibilityEntry> Entries => _entries;
        public bool RequiredOnImport => _requiredOnImport;

        public void Bind(IReadOnlyList<PrimitiveVisibilityEntry> entries, bool requiredOnImport = false)
        {
            _entries = entries != null ? new List<PrimitiveVisibilityEntry>(entries) : new List<PrimitiveVisibilityEntry>();
            _requiredOnImport = requiredOnImport;
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

        public string ResolveRole(Renderer renderer, int subMesh)
            => ResolveRole(GetRendererMesh(renderer), subMesh);

        public string ResolveRole(Mesh mesh, int subMesh)
        {
            if (mesh == null || subMesh < 0) return null;
            EnsureIndex();
            return _entryByPrimitive.TryGetValue((mesh, subMesh), out var entry) ? entry.Role : null;
        }

        public bool ShouldRenderPrimitiveInstance(
            Renderer renderer,
            int subMesh,
            string resolvedNodeRole,
            string activeContext,
            bool ancestorInclusiveCoreVisible)
            => VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(
                resolvedNodeRole, ResolveRole(renderer, subMesh), activeContext, ancestorInclusiveCoreVisible);

        private static Mesh GetRendererMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
