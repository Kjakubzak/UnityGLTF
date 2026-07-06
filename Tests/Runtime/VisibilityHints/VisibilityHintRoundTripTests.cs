using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Tests
{
    /// <summary>
    /// Export -> import composition, in-memory (the codebase's non-flaky round-trip convention; no
    /// GLTFSceneImporter.LoadScene). The exact extension objects the exporter writes onto the exported
    /// <see cref="GLTFRoot"/> are fed straight back into a fresh <see cref="VisibilityHintImportContext"/>,
    /// proving the export and import halves are mutually inverse. (Wire-level JSON serialize/deserialize is
    /// covered by <see cref="SchemaRoundTripTests"/>; this closes the object-level loop.)
    /// </summary>
    public class VisibilityHintRoundTripTests
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

        private static GameObject NewChild(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
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

        private Mesh NewTriangleMesh(string name)
        {
            var mesh = new Mesh { name = name + "_mesh" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            _created.Add(mesh);
            return mesh;
        }

        private GameObject MakeMeshChild(GameObject parent, string name, out Mesh mesh)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent.transform, false);
            mesh = NewTriangleMesh(name);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = NewMaterial(name + "_mat");
            return go;
        }

        private static GLTFRoot ExportToGltfRoot(GameObject root)
        {
            var settings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in settings.ExportPlugins)
                if (plugin is VisibilityHintExportPlugin) plugin.Enabled = true;

            var exporter = new GLTFSceneExporter(new[] { root.transform }, new ExportContext(settings));
            exporter.SaveGLBToByteArray("scene");
            return exporter.GetRoot();
        }

        // Bridge helper: the in-memory export skips serialize/deserialize, so a node's MeshId.Root may be unset
        // (the real importer's parse wires it). Set it so node.Mesh.Value resolves during re-import.
        private static void WireMeshRoot(GLTFRoot gltf, Node node)
        {
            if (node.Mesh != null && node.Mesh.Root == null) node.Mesh.Root = gltf;
        }

        [Test]
        public void NodeHint_ExportThenImport_PreservesRoleAndDrivesRenderer()
        {
            // Author + export.
            var src = NewGo("srcRoot");
            var head = MakeMeshChild(src, "head", out _);
            src.AddComponent<NodeVisibilityHintSet>().Bind(new List<NodeVisibilityHintSet.NodeVisibilityEntry>
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = head.transform, Role = "third_person_only", Label = "Head" },
            });

            var gltf = ExportToGltfRoot(src);

            var headNode = gltf.Nodes.Find(n => n.Name == "head");
            Assert.IsNotNull(headNode, "the hinted node should be exported");
            Assert.IsTrue(headNode.Extensions != null && headNode.Extensions.ContainsKey(KHR_node_visibility_hint.EXTENSION_NAME),
                "export should attach KHR_node_visibility_hint to the node");
            WireMeshRoot(gltf, headNode);

            // Re-import the exported node into a fresh scene.
            var dst = NewGo("dstRoot");
            var head2 = NewChild(dst, "head");
            var head2Renderer = head2.AddComponent<MeshRenderer>();

            var ctx = new VisibilityHintImportContext(null);
            ctx.OnAfterImportNode(headNode, 0, head2);
            ctx.OnAfterImportScene(null, 0, dst);

            var set = dst.GetComponent<NodeVisibilityHintSet>();
            Assert.IsNotNull(set, "re-import should add a NodeVisibilityHintSet");
            Assert.AreEqual(1, set.Entries.Count);
            Assert.AreEqual("third_person_only", set.Entries[0].Role, "role survives export -> import");
            Assert.AreEqual("Head", set.Entries[0].Label, "label survives export -> import");

            var view = dst.GetComponent<ViewContextController>();
            Assert.IsNotNull(view);
            Assert.IsTrue(head2Renderer.enabled, "third_person_only visible in default third-person");
            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.IsFalse(head2Renderer.enabled, "third_person_only hidden in first-person after round-trip");
        }

        [Test]
        public void PrimitiveHint_ExportThenImport_PreservesRoleAndSwapsMaterial()
        {
            // Author + export.
            var src = NewGo("srcRoot");
            MakeMeshChild(src, "body", out var srcMesh);
            src.AddComponent<PrimitiveVisibilityHintSet>().Bind(new List<PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry>
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry { Mesh = srcMesh, SubMesh = 0, Role = "first_person_only", Label = "BodyPrim" },
            });

            var gltf = ExportToGltfRoot(src);

            var bodyNode = gltf.Nodes.Find(n => n.Name == "body" && n.Mesh != null);
            Assert.IsNotNull(bodyNode, "the mesh node should be exported");
            WireMeshRoot(gltf, bodyNode);
            Assert.IsTrue(bodyNode.Mesh.Value.Primitives[0].Extensions != null
                && bodyNode.Mesh.Value.Primitives[0].Extensions.ContainsKey(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME),
                "export should attach KHR_mesh_primitive_visibility_hint to the primitive");

            // Re-import into a fresh scene with a fresh renderer + mesh (import keys by the Unity mesh + sub-mesh).
            var dst = NewGo("dstRoot");
            var body2 = NewChild(dst, "body");
            var filter = body2.AddComponent<MeshFilter>();
            var renderer = body2.AddComponent<MeshRenderer>();
            var dstMesh = NewTriangleMesh("body2");
            filter.sharedMesh = dstMesh;
            var original = NewMaterial("original");
            renderer.sharedMaterials = new[] { original };

            var ctx = new VisibilityHintImportContext(null);
            ctx.OnAfterImportNode(bodyNode, 0, body2);
            ctx.OnAfterImportScene(null, 0, dst);

            var set = dst.GetComponent<PrimitiveVisibilityHintSet>();
            Assert.IsNotNull(set, "re-import should add a PrimitiveVisibilityHintSet");
            Assert.AreEqual(1, set.Entries.Count);
            Assert.AreEqual("first_person_only", set.Entries[0].Role, "role survives export -> import");
            Assert.AreSame(dstMesh, set.Entries[0].Mesh);
            Assert.AreEqual(0, set.Entries[0].SubMesh);

            var view = dst.GetComponent<ViewContextController>();
            Assert.IsNotNull(view);
            Assert.AreNotSame(original, renderer.sharedMaterials[0], "first_person_only hidden in third-person -> invisible material");
            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.AreSame(original, renderer.sharedMaterials[0], "restored in first-person after round-trip");
        }
    }
}
