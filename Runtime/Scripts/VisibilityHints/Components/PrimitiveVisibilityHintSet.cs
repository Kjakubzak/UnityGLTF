using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Scene-root component holding the authored <c>KHR_mesh_primitive_visibility_hint</c> entries and resolving
    /// them onto renderer sub-mesh slots. A primitive hint is self-only (no subtree inheritance) but, because it
    /// lives on a shared <c>meshes[m].primitives[i]</c>, it applies to <b>every</b> renderer that uses that mesh.
    /// Each affected <c>(renderer, subMesh)</c> is registered with the <see cref="ViewContextController"/> as a
    /// material-swap slot (see <see cref="InvisibleMaterialCache"/>).
    ///
    /// <para>Entries are keyed by the shared Unity <see cref="Mesh"/> + sub-mesh index (which matches the glTF
    /// primitive index). The list is serialized so an editor-imported prefab rehydrates on <see cref="Awake"/>; a
    /// live import instead calls <see cref="Bind"/> directly.</para>
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
        }

        [SerializeField, HideInInspector] private List<PrimitiveVisibilityEntry> _entries = new List<PrimitiveVisibilityEntry>();

        public IReadOnlyList<PrimitiveVisibilityEntry> Entries => _entries;

        private bool _resolved;

        /// <summary>Bind authored entries (live import) and immediately resolve them onto renderer sub-mesh slots.</summary>
        public void Bind(IReadOnlyList<PrimitiveVisibilityEntry> entries)
        {
            _entries = entries != null ? new List<PrimitiveVisibilityEntry>(entries) : new List<PrimitiveVisibilityEntry>();
            _resolved = false;
            Resolve();
        }

        private void Awake()
        {
            if (!_resolved) Resolve();
        }

        private void Resolve()
        {
            if (_resolved) return;
            if (_entries == null || _entries.Count == 0) return;
            _resolved = true;

            var controller = GetComponent<ViewContextController>();
            if (controller == null) controller = gameObject.AddComponent<ViewContextController>();

            // mesh -> (subMesh -> role)
            var byMesh = new Dictionary<Mesh, Dictionary<int, string>>();
            foreach (var e in _entries)
            {
                if (e == null || e.Mesh == null || e.SubMesh < 0) continue;
                if (!byMesh.TryGetValue(e.Mesh, out var subMap))
                    byMesh[e.Mesh] = subMap = new Dictionary<int, string>();
                subMap[e.SubMesh] = e.Role;
            }
            if (byMesh.Count == 0) return;

            var invisible = InvisibleMaterialCache.Get();
            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                var mesh = GetRendererMesh(renderer);
                if (mesh == null || !byMesh.TryGetValue(mesh, out var subMap)) continue;

                var materials = renderer.sharedMaterials;
                foreach (var kv in subMap)
                {
                    int subMesh = kv.Key;
                    if (subMesh < 0 || subMesh >= materials.Length) continue;
                    var viewRole = ViewContextController.ParseRole(kv.Value);
                    if (viewRole == ViewContextController.ViewRole.Always) continue; // always visible -> no swap needed
                    controller.RegisterPrimitiveSlot(renderer, subMesh, materials[subMesh], invisible, viewRole);
                }
            }
        }

        // Resolves the mesh a renderer draws: SkinnedMeshRenderer carries it directly; MeshRenderer reads its
        // sibling MeshFilter. Both expose sharedMaterials via the Renderer base type.
        private static Mesh GetRendererMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
