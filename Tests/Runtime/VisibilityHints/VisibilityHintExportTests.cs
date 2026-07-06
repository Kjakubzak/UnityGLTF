using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;
using UnityGLTF.Plugins;

namespace UnityGLTF.VisibilityHints.Tests
{
    /// <summary>
    /// Export tests: authored <see cref="NodeVisibilityHintSet"/> / <see cref="PrimitiveVisibilityHintSet"/>
    /// entries emit <c>KHR_node_visibility_hint</c> / <c>KHR_mesh_primitive_visibility_hint</c> on the right
    /// node/primitive, declared used (never required), and export reads the authored role — not live material
    /// state. Runs in PlayMode (the export pipeline needs the Unity runtime).
    /// </summary>
    public class VisibilityHintExportTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private GameObject NewGo(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        private Material NewMaterial(string name)
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader");
            var mat = new Material(shader) { name = name };
            _created.Add(mat);
            return mat;
        }

        // A child renderer with a single-submesh triangle mesh + a non-null material (a null material makes the
        // exporter skip the submesh entirely).
        private GameObject MakeMeshChild(GameObject parent, string name, out Mesh mesh)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent.transform, false);
            mesh = new Mesh { name = name + "_mesh" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            _created.Add(mesh);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = NewMaterial(name + "_mat");
            return go;
        }

        private static GLTFRoot ExportToGltfRoot(GameObject root)
        {
            var settings = GLTFSettings.GetDefaultSettings(); // isolated instance; global settings untouched
            foreach (var plugin in settings.ExportPlugins)
                if (plugin is VisibilityHintExportPlugin) plugin.Enabled = true;

            var exporter = new GLTFSceneExporter(new[] { root.transform }, new ExportContext(settings));
            exporter.SaveGLBToByteArray("scene"); // synchronous; runs the export plugin hooks
            return exporter.GetRoot();
        }

        // export sets node.Name = transform.name when ExportNames (default).
        private static int FindNodeIndex(GLTFRoot gltf, string name) => gltf.Nodes.FindIndex(n => n.Name == name);

        private static T NodeExtension<T>(GLTFRoot gltf, int nodeIndex, string name) where T : class
            => nodeIndex >= 0 && gltf.Nodes[nodeIndex].Extensions != null
               && gltf.Nodes[nodeIndex].Extensions.TryGetValue(name, out var ext) ? ext as T : null;

        private static KHR_mesh_primitive_visibility_hint FindPrimitiveHint(GLTFRoot gltf)
        {
            if (gltf.Meshes == null) return null;
            foreach (var mesh in gltf.Meshes)
            {
                if (mesh.Primitives == null) continue;
                foreach (var prim in mesh.Primitives)
                    if (prim.Extensions != null
                        && prim.Extensions.TryGetValue(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME, out var ext)
                        && ext is KHR_mesh_primitive_visibility_hint hint)
                        return hint;
            }
            return null;
        }

        [Test]
        public void ExportPlugin_IsDisabledByDefault()
        {
            var plugin = ScriptableObject.CreateInstance<VisibilityHintExportPlugin>();
            Assert.IsFalse(plugin.EnabledByDefault, "the visibility-hint export plugin should be opt-in");
            Object.DestroyImmediate(plugin);
        }

        [Test]
        public void ExportPlugin_HasNonRatifiedAttribute()
        {
            var attr = typeof(VisibilityHintExportPlugin).GetCustomAttributes(typeof(NonRatifiedPluginAttribute), true);
            Assert.IsNotEmpty(attr, "the export plugin should be marked non-ratified");
        }

        [Test]
        public void NodeHint_ExportsNodeExtension_UsedNotRequired()
        {
            var root = NewGo("root");
            var head = MakeMeshChild(root, "head", out _);

            root.AddComponent<NodeVisibilityHintSet>().Bind(new List<NodeVisibilityHintSet.NodeVisibilityEntry>
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = head.transform, Role = "third_person_only", Label = "Head" },
            });

            var gltf = ExportToGltfRoot(root);

            int idx = FindNodeIndex(gltf, "head");
            Assert.GreaterOrEqual(idx, 0, "the hinted node should be exported");
            var ext = NodeExtension<KHR_node_visibility_hint>(gltf, idx, KHR_node_visibility_hint.EXTENSION_NAME);
            Assert.IsNotNull(ext, "the hinted node should carry KHR_node_visibility_hint");
            Assert.AreEqual("third_person_only", ext.Role);
            Assert.AreEqual("Head", ext.Label);

            Assert.IsTrue(gltf.ExtensionsUsed != null && gltf.ExtensionsUsed.Contains(KHR_node_visibility_hint.EXTENSION_NAME),
                "KHR_node_visibility_hint must be declared in extensionsUsed");
            Assert.IsTrue(gltf.ExtensionsRequired == null || !gltf.ExtensionsRequired.Contains(KHR_node_visibility_hint.EXTENSION_NAME),
                "KHR_node_visibility_hint must NOT be required (neutrality)");
        }

        [Test]
        public void PrimitiveHint_ExportsPrimitiveExtension_UsedNotRequired()
        {
            var root = NewGo("root");
            MakeMeshChild(root, "body", out var mesh);

            root.AddComponent<PrimitiveVisibilityHintSet>().Bind(new List<PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry>
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry { Mesh = mesh, SubMesh = 0, Role = "third_person_only", Label = "BodyPrim" },
            });

            var gltf = ExportToGltfRoot(root);

            var hint = FindPrimitiveHint(gltf);
            Assert.IsNotNull(hint, "the hinted primitive should carry KHR_mesh_primitive_visibility_hint");
            Assert.AreEqual("third_person_only", hint.Role);
            Assert.AreEqual("BodyPrim", hint.Label);

            Assert.IsTrue(gltf.ExtensionsUsed != null && gltf.ExtensionsUsed.Contains(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME),
                "KHR_mesh_primitive_visibility_hint must be declared in extensionsUsed");
            Assert.IsTrue(gltf.ExtensionsRequired == null || !gltf.ExtensionsRequired.Contains(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME),
                "KHR_mesh_primitive_visibility_hint must NOT be required (neutrality)");
        }

        [Test]
        public void PrimitiveHint_ExportReadsAuthoredRole_NotLiveMaterialState()
        {
            // A first_person_only slot is swapped to the invisible material in the default ThirdPerson context at
            // Bind time. Export must still emit role "first_person_only" from the authored entry — never inferring
            // visibility from the (now invisible) live material.
            var root = NewGo("root");
            MakeMeshChild(root, "arms", out var mesh);

            root.AddComponent<PrimitiveVisibilityHintSet>().Bind(new List<PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry>
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry { Mesh = mesh, SubMesh = 0, Role = "first_person_only" },
            });

            var gltf = ExportToGltfRoot(root);

            var hint = FindPrimitiveHint(gltf);
            Assert.IsNotNull(hint, "the hint must still export even though the slot is currently swapped to invisible");
            Assert.AreEqual("first_person_only", hint.Role, "export reads the authored role, not live material state");
        }
    }
}
