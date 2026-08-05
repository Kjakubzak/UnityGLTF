using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityGLTF.Plugins;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Export context for the view-context visibility hints. On the first callback it collects the authored hint
    /// entries from all <see cref="NodeVisibilityHintSet"/> / <see cref="PrimitiveVisibilityHintSet"/> components
    /// under the export roots, then:
    /// <list type="bullet">
    /// <item><see cref="AfterNodeExport"/> adds <c>KHR_node_visibility_hint</c> to any exported node that is an
    /// authored entry.</item>
    /// <item><see cref="AfterPrimitiveExport"/> adds <c>KHR_mesh_primitive_visibility_hint</c> to any exported
    /// primitive whose <c>(mesh, sub-mesh)</c> is an authored entry.</item>
    /// </list>
    /// Newly authored entries are declared used-only; an imported required declaration is preserved. The authored
    /// role and glTFProperty payload are read from serialized entries, never from host rendering state.
    /// </summary>
    public class VisibilityHintExportContext : GLTFExportPluginContext
    {
        private bool _mapsBuilt;
        private Dictionary<Transform, NodeVisibilityHintSet.NodeVisibilityEntry> _nodeMap;
        private Dictionary<(Mesh mesh, int subMesh), PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry> _primMap;
        private bool _nodeRequired;
        private bool _primitiveRequired;

        public override void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node)
        {
            EnsureMaps(exporter);
            if (transform == null || node == null || !_nodeMap.TryGetValue(transform, out var entry)) return;

            // role is spec-required (minLength:1); a null/empty role could only serialize as an invalid extension.
            if (string.IsNullOrEmpty(entry.Role))
            {
                Debug.LogWarning($"[VisibilityHints] Node '{transform.name}' visibility hint has no 'role' (spec-required, minLength:1); skipping.");
                return;
            }

            var companionExtensions = ParseObject(entry.ExtensionsJson);
            if (TryAddExtension(node, VisibilityHintExtensionNames.NodeVisibilityHint,
                    new KHR_node_visibility_hint
                    {
                        Role = entry.Role,
                        Label = entry.Label,
                        Extensions = companionExtensions,
                        Extras = ParseToken(entry.ExtrasJson),
                        AdditionalProperties = ParseObject(entry.AdditionalPropertiesJson),
                    }))
            {
                exporter.DeclareExtensionUsage(
                    VisibilityHintExtensionNames.NodeVisibilityHint, isRequired: _nodeRequired);
                DeclareCompanionExtensions(
                    exporter, companionExtensions, entry.RequiredCompanionExtensions);
            }
        }

        public override void AfterPrimitiveExport(GLTFSceneExporter exporter, Mesh mesh, MeshPrimitive primitive, int index)
        {
            EnsureMaps(exporter);
            if (mesh == null || primitive == null || !_primMap.TryGetValue((mesh, index), out var entry)) return;

            if (string.IsNullOrEmpty(entry.Role))
            {
                Debug.LogWarning($"[VisibilityHints] Mesh '{mesh.name}' primitive {index} visibility hint has no 'role' (spec-required, minLength:1); skipping.");
                return;
            }

            var companionExtensions = ParseObject(entry.ExtensionsJson);
            if (TryAddExtension(primitive, VisibilityHintExtensionNames.MeshPrimitiveVisibilityHint,
                    new KHR_mesh_primitive_visibility_hint
                    {
                        Role = entry.Role,
                        Label = entry.Label,
                        Extensions = companionExtensions,
                        Extras = ParseToken(entry.ExtrasJson),
                        AdditionalProperties = ParseObject(entry.AdditionalPropertiesJson),
                    }))
            {
                exporter.DeclareExtensionUsage(
                    VisibilityHintExtensionNames.MeshPrimitiveVisibilityHint, isRequired: _primitiveRequired);
                DeclareCompanionExtensions(
                    exporter, companionExtensions, entry.RequiredCompanionExtensions);
            }
        }

        // Collect authored entries once, from every hint-set component under the export roots (include-inactive:
        // imported roots are frequently inactive at edit time). RootTransforms is available for the whole export.
        private void EnsureMaps(GLTFSceneExporter exporter)
        {
            if (_mapsBuilt) return;
            _mapsBuilt = true;
            _nodeMap = new Dictionary<Transform, NodeVisibilityHintSet.NodeVisibilityEntry>();
            _primMap = new Dictionary<(Mesh, int), PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry>();

            var roots = exporter?.RootTransforms;
            if (roots == null) return;
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var set in root.GetComponentsInChildren<NodeVisibilityHintSet>(true))
                {
                    _nodeRequired |= set.RequiredOnImport;
                    foreach (var e in set.Entries)
                        if (e != null && e.Node != null) _nodeMap[e.Node] = e;
                }
                foreach (var set in root.GetComponentsInChildren<PrimitiveVisibilityHintSet>(true))
                {
                    _primitiveRequired |= set.RequiredOnImport;
                    foreach (var e in set.Entries)
                        if (e != null && e.Mesh != null && e.SubMesh >= 0) _primMap[(e.Mesh, e.SubMesh)] = e;
                }
            }
        }

        // GLTFProperty.AddExtension throws on a duplicate key; guard so a reused primitive/node is written once.
        private static bool TryAddExtension(GLTFProperty target, string name, IExtension ext)
        {
            if (target.Extensions != null && target.Extensions.ContainsKey(name)) return false;
            target.AddExtension(name, ext);
            return true;
        }

        private static JObject ParseObject(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JObject.Parse(json); }
            catch { return null; }
        }

        private static JToken ParseToken(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JToken.Parse(json); }
            catch { return null; }
        }

        private static void DeclareCompanionExtensions(
            GLTFSceneExporter exporter, JObject extensions, string[] requiredExtensions)
        {
            if (extensions == null) return;
            var required = requiredExtensions != null
                ? new HashSet<string>(requiredExtensions)
                : new HashSet<string>();
            foreach (var extension in extensions.Properties())
                exporter.DeclareExtensionUsage(extension.Name, required.Contains(extension.Name));
        }
    }
}
