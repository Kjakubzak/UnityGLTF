using System.Collections.Generic;
using GLTF.Schema;
using UnityEngine;
using UnityGLTF.Plugins;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Per-import instance that reads the view-context visibility hints and wires them onto the imported scene:
    /// node hints become <see cref="NodeVisibilityHintSet"/> entries (subtree inheritance resolved by that
    /// component), and mesh-primitive hints become <see cref="PrimitiveVisibilityHintSet"/> entries keyed by the
    /// Unity mesh + sub-mesh. Both drive a single <see cref="ViewContextController"/> on the scene root.
    /// </summary>
    public class VisibilityHintImportContext : GLTFImportPluginContext
    {
        private readonly GLTFImportContext _context;

        // UnityGLTF doesn't expose a node-index -> GameObject map, so build one from the node callbacks.
        private readonly Dictionary<int, GameObject> _nodeIndexToGo = new Dictionary<int, GameObject>();
        private readonly List<(int nodeIndex, string role, string label)> _nodeHints = new List<(int, string, string)>();
        // Per shared Unity mesh -> (sub-mesh index -> hint). Keying by the Unity mesh dedupes shared meshes.
        private readonly Dictionary<Mesh, Dictionary<int, (string role, string label)>> _primitiveHints
            = new Dictionary<Mesh, Dictionary<int, (string, string)>>();

        public VisibilityHintImportContext(GLTFImportContext context)
        {
            _context = context;
        }

        public override void OnAfterImportNode(Node node, int nodeIndex, GameObject nodeObject)
        {
            _nodeIndexToGo[nodeIndex] = nodeObject;
            if (node == null) return;

            // Node-level hint (applies to this node and its subtree; resolution happens in NodeVisibilityHintSet).
            if (node.Extensions != null
                && node.Extensions.TryGetValue(VisibilityHintExtensionNames.NodeVisibilityHint, out var nodeExt))
            {
                var hint = AsNodeHint(nodeExt);
                if (hint != null) _nodeHints.Add((nodeIndex, hint.Role, hint.Label));
            }

            // Per-primitive hints live on the shared mesh; key them by the Unity mesh + sub-mesh index.
            var primitives = node.Mesh?.Value?.Primitives;
            if (primitives == null || nodeObject == null) return;
            var unityMesh = GetNodeMesh(nodeObject);
            if (unityMesh == null) return;

            for (int i = 0; i < primitives.Count; i++)
            {
                var prim = primitives[i];
                if (prim?.Extensions == null) continue;
                if (!prim.Extensions.TryGetValue(VisibilityHintExtensionNames.MeshPrimitiveVisibilityHint, out var primExt))
                    continue;
                var hint = AsPrimitiveHint(primExt);
                if (hint == null) continue;

                if (!_primitiveHints.TryGetValue(unityMesh, out var subMap))
                    _primitiveHints[unityMesh] = subMap = new Dictionary<int, (string, string)>();
                subMap[i] = (hint.Role, hint.Label);
            }
        }

        // OnAfterImportScene is the last callback at runtime (OnAfterImport is editor-only). Wire the collected
        // hints onto the scene root here, when every node GameObject exists.
        public override void OnAfterImportScene(GLTFScene scene, int sceneIndex, GameObject sceneObject)
        {
            if (sceneObject == null) return;
            if (_nodeHints.Count == 0 && _primitiveHints.Count == 0) return;

            // A single controller drives both node-enable and primitive material-swap.
            if (sceneObject.GetComponent<ViewContextController>() == null)
                sceneObject.AddComponent<ViewContextController>();

            if (_nodeHints.Count > 0)
            {
                var entries = new List<NodeVisibilityHintSet.NodeVisibilityEntry>();
                foreach (var (nodeIndex, role, label) in _nodeHints)
                {
                    if (!_nodeIndexToGo.TryGetValue(nodeIndex, out var go) || go == null) continue;
                    entries.Add(new NodeVisibilityHintSet.NodeVisibilityEntry { Node = go.transform, Role = role, Label = label });
                }
                if (entries.Count > 0)
                {
                    var set = sceneObject.GetComponent<NodeVisibilityHintSet>() ?? sceneObject.AddComponent<NodeVisibilityHintSet>();
                    set.Bind(entries);
                }
            }

            if (_primitiveHints.Count > 0)
            {
                var entries = new List<PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry>();
                foreach (var meshKv in _primitiveHints)
                    foreach (var subKv in meshKv.Value)
                        entries.Add(new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                        {
                            Mesh = meshKv.Key,
                            SubMesh = subKv.Key,
                            Role = subKv.Value.role,
                            Label = subKv.Value.label,
                        });
                if (entries.Count > 0)
                {
                    var set = sceneObject.GetComponent<PrimitiveVisibilityHintSet>() ?? sceneObject.AddComponent<PrimitiveVisibilityHintSet>();
                    set.Bind(entries);
                }
            }
        }

        private KHR_node_visibility_hint AsNodeHint(IExtension ext)
        {
            if (ext is KHR_node_visibility_hint typed) return typed;
            if (ext is DefaultExtension raw && raw.ExtensionData != null)
                return new KHR_node_visibility_hint_Factory().Deserialize(_context?.Root, raw.ExtensionData) as KHR_node_visibility_hint;
            return null;
        }

        private KHR_mesh_primitive_visibility_hint AsPrimitiveHint(IExtension ext)
        {
            if (ext is KHR_mesh_primitive_visibility_hint typed) return typed;
            if (ext is DefaultExtension raw && raw.ExtensionData != null)
                return new KHR_mesh_primitive_visibility_hint_Factory().Deserialize(_context?.Root, raw.ExtensionData) as KHR_mesh_primitive_visibility_hint;
            return null;
        }

        // Resolves the mesh an imported node draws: SkinnedMeshRenderer carries it directly; a static mesh reads
        // the MeshFilter. Sub-mesh index i corresponds 1:1 to glTF primitives[i] (import preserves the order).
        private static Mesh GetNodeMesh(GameObject go)
        {
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr != null) return smr.sharedMesh;
            var filter = go.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
