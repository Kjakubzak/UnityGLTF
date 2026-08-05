using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json;
using UnityEngine;
using UnityGLTF.Plugins;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Per-import instance that reads view-context visibility hints into queryable metadata components. The shared
    /// <see cref="ViewContextController"/> exposes pure per-view and per-instance predicates; it does not mutate
    /// scene or rendering state.
    /// </summary>
    public class VisibilityHintImportContext : GLTFImportPluginContext
    {
        private readonly GLTFImportContext _context;

        private readonly bool _hostSupportsRequiredNodeUse;
        private readonly bool _hostSupportsRequiredPrimitiveUse;
        private readonly List<NodeHintRecord> _nodeHints = new List<NodeHintRecord>();
        // Per shared Unity mesh -> (sub-mesh index -> hint). Keying by the Unity mesh dedupes shared meshes.
        private readonly Dictionary<Mesh, Dictionary<int, HintPayload>> _primitiveHints
            = new Dictionary<Mesh, Dictionary<int, HintPayload>>();
        private readonly HashSet<Mesh> _meshesWithPrimitiveHints = new HashSet<Mesh>();
        private bool _nodeHintRequired;
        private bool _primitiveHintRequired;
        private HashSet<string> _requiredExtensions = new HashSet<string>();

        private sealed class NodeHintRecord
        {
            public Transform Node;
            public HintPayload Payload;
        }

        private sealed class HintPayload
        {
            public string Role;
            public string Label;
            public string ExtensionsJson;
            public string ExtrasJson;
            public string AdditionalPropertiesJson;
            public string[] RequiredCompanionExtensions;
        }

        public VisibilityHintImportContext(
            GLTFImportContext context,
            bool hostSupportsRequiredNodeUse = false,
            bool hostSupportsRequiredPrimitiveUse = false)
        {
            _context = context;
            _hostSupportsRequiredNodeUse = hostSupportsRequiredNodeUse;
            _hostSupportsRequiredPrimitiveUse = hostSupportsRequiredPrimitiveUse;
        }

        public override bool SupportsRequiredExtension(string extensionName)
            => (extensionName == VisibilityHintExtensionNames.NodeVisibilityHint
                    && _hostSupportsRequiredNodeUse)
               || (extensionName == VisibilityHintExtensionNames.MeshPrimitiveVisibilityHint
                    && _hostSupportsRequiredPrimitiveUse);

        public override bool CanDeduplicateMesh(Mesh mesh)
            => mesh == null || !_meshesWithPrimitiveHints.Contains(mesh);

        public override bool CanShareMeshData(GLTFMesh mesh, int meshIndex)
        {
            if (mesh?.Primitives == null) return true;
            foreach (var primitive in mesh.Primitives)
                if (primitive?.Extensions != null
                    && primitive.Extensions.ContainsKey(
                        VisibilityHintExtensionNames.MeshPrimitiveVisibilityHint))
                    return false;
            return true;
        }

        public override void OnAfterImportRoot(GLTFRoot gltfRoot)
        {
            _nodeHintRequired = gltfRoot?.ExtensionsRequired?.Contains(
                VisibilityHintExtensionNames.NodeVisibilityHint) == true;
            _primitiveHintRequired = gltfRoot?.ExtensionsRequired?.Contains(
                VisibilityHintExtensionNames.MeshPrimitiveVisibilityHint) == true;
            _requiredExtensions = gltfRoot?.ExtensionsRequired != null
                ? new HashSet<string>(gltfRoot.ExtensionsRequired)
                : new HashSet<string>();
        }

        public override void OnAfterImportNode(Node node, int nodeIndex, GameObject nodeObject)
        {
            if (node == null) return;

            // Node-level hint (applies to this node and its subtree; resolution happens in NodeVisibilityHintSet).
            if (node.Extensions != null
                && node.Extensions.TryGetValue(VisibilityHintExtensionNames.NodeVisibilityHint, out var nodeExt))
            {
                var hint = AsNodeHint(nodeExt);
                if (hint != null && nodeObject != null)
                    _nodeHints.Add(new NodeHintRecord
                    {
                        Node = nodeObject.transform,
                        Payload = CreatePayload(
                            hint.Role, hint.Label, hint.Extensions, hint.Extras, hint.AdditionalProperties),
                    });
            }

            // Per-primitive hints live on the shared mesh; key them by the Unity mesh + sub-mesh index.
            var primitives = node.Mesh?.Value?.Primitives;
            if (primitives == null || nodeObject == null) return;
            var unityMesh = GetNodeMesh(node, nodeObject);
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
                    _primitiveHints[unityMesh] = subMap = new Dictionary<int, HintPayload>();
                _meshesWithPrimitiveHints.Add(unityMesh);
                subMap[i] = CreatePayload(
                    hint.Role, hint.Label, hint.Extensions, hint.Extras, hint.AdditionalProperties);
            }
        }

        // OnAfterImportScene is the last callback at runtime (OnAfterImport is editor-only). Wire the collected
        // hints onto the scene root here, when every node GameObject exists.
        public override void OnAfterImportScene(GLTFScene scene, int sceneIndex, GameObject sceneObject)
        {
            if (sceneObject == null) return;
            if (_nodeHints.Count == 0 && _primitiveHints.Count == 0) return;

            // A single controller composes node, primitive, and host-supplied core visibility predicates.
            if (sceneObject.GetComponent<ViewContextController>() == null)
                sceneObject.AddComponent<ViewContextController>();

            if (_nodeHints.Count > 0)
            {
                var entries = new List<NodeVisibilityHintSet.NodeVisibilityEntry>();
                foreach (var record in _nodeHints)
                {
                    if (record.Node == null) continue;
                    var payload = record.Payload;
                    entries.Add(new NodeVisibilityHintSet.NodeVisibilityEntry
                    {
                        Node = record.Node,
                        Role = payload.Role,
                        Label = payload.Label,
                        ExtensionsJson = payload.ExtensionsJson,
                        ExtrasJson = payload.ExtrasJson,
                        AdditionalPropertiesJson = payload.AdditionalPropertiesJson,
                        RequiredCompanionExtensions = payload.RequiredCompanionExtensions,
                    });
                }
                if (entries.Count > 0)
                {
                    var set = sceneObject.GetComponent<NodeVisibilityHintSet>() ?? sceneObject.AddComponent<NodeVisibilityHintSet>();
                    set.Bind(entries, _nodeHintRequired);
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
                            Role = subKv.Value.Role,
                            Label = subKv.Value.Label,
                            ExtensionsJson = subKv.Value.ExtensionsJson,
                            ExtrasJson = subKv.Value.ExtrasJson,
                            AdditionalPropertiesJson = subKv.Value.AdditionalPropertiesJson,
                            RequiredCompanionExtensions = subKv.Value.RequiredCompanionExtensions,
                        });
                if (entries.Count > 0)
                {
                    var set = sceneObject.GetComponent<PrimitiveVisibilityHintSet>() ?? sceneObject.AddComponent<PrimitiveVisibilityHintSet>();
                    set.Bind(entries, _primitiveHintRequired);
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

        private string[] GetRequiredCompanionExtensions(Newtonsoft.Json.Linq.JObject extensions)
        {
            if (extensions == null) return System.Array.Empty<string>();
            var required = new List<string>();
            foreach (var extension in extensions.Properties())
                if (_requiredExtensions.Contains(extension.Name)) required.Add(extension.Name);
            return required.ToArray();
        }

        private HintPayload CreatePayload(
            string role,
            string label,
            Newtonsoft.Json.Linq.JObject extensions,
            Newtonsoft.Json.Linq.JToken extras,
            Newtonsoft.Json.Linq.JObject additionalProperties)
            => new HintPayload
            {
                Role = role,
                Label = label,
                ExtensionsJson = extensions?.ToString(Formatting.None),
                ExtrasJson = extras?.ToString(Formatting.None),
                AdditionalPropertiesJson = additionalProperties?.ToString(Formatting.None),
                RequiredCompanionExtensions = GetRequiredCompanionExtensions(extensions),
            };

        private KHR_mesh_primitive_visibility_hint AsPrimitiveHint(IExtension ext)
        {
            if (ext is KHR_mesh_primitive_visibility_hint typed) return typed;
            if (ext is DefaultExtension raw && raw.ExtensionData != null)
                return new KHR_mesh_primitive_visibility_hint_Factory().Deserialize(_context?.Root, raw.ExtensionData) as KHR_mesh_primitive_visibility_hint;
            return null;
        }

        // Resolves the mesh an imported node draws: SkinnedMeshRenderer carries it directly; a static mesh reads
        // the MeshFilter. Sub-mesh index i corresponds 1:1 to glTF primitives[i] (import preserves the order).
        private Mesh GetNodeMesh(Node node, GameObject go)
        {
            if (node?.Mesh != null && _context?.SceneImporter?.MeshCache != null)
            {
                var meshIndex = node.Mesh.Id;
                var meshCache = _context.SceneImporter.MeshCache;
                if (meshIndex >= 0 && meshIndex < meshCache.Length
                    && meshCache[meshIndex]?.LoadedMesh != null)
                    return meshCache[meshIndex].LoadedMesh;
            }

            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr != null) return smr.sharedMesh;
            var filter = go.GetComponent<MeshFilter>();
            if (filter != null) return filter.sharedMesh;

            // EXT_mesh_gpu_instancing puts renderers below a generated "Instances" child while the node callback
            // receives the wrapper. Constrain the fallback so ordinary authored child meshes cannot be mistaken for
            // this node's mesh in direct-callback hosts that do not provide an import context.
            var instances = go.transform.Find("Instances");
            if (instances == null) return null;
            smr = instances.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null) return smr.sharedMesh;
            filter = instances.GetComponentInChildren<MeshFilter>(true);
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
