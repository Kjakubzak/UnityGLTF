using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Tests
{
    /// <summary>
    /// Import-context tests. Rather than run a full (headless-flaky) scene load, these drive the
    /// <see cref="VisibilityHintImportContext"/> callbacks directly with hand-built glTF nodes/primitives and real
    /// GameObjects, then assert the wired components + <see cref="ViewContextController"/> registrations. The
    /// GLTFImportContext is a class, so the (unused-in-this-path) context is passed as null.
    /// </summary>
    public class VisibilityHintImportTests
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
            return go; // destroyed with the root
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

        [Test]
        public void NodeHint_Import_AddsSetAndRegistersRenderer()
        {
            var scene = NewGo("scene");
            var head = NewChild(scene, "head");
            var headRenderer = head.AddComponent<MeshRenderer>();

            var ctx = new VisibilityHintImportContext(null);
            var node = new Node { Name = "head" };
            node.AddExtension(KHR_node_visibility_hint.EXTENSION_NAME,
                new KHR_node_visibility_hint { Role = "third_person", Label = "Head" });

            ctx.OnAfterImportNode(node, 0, head);
            ctx.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<NodeVisibilityHintSet>();
            Assert.IsNotNull(set, "import should add a NodeVisibilityHintSet to the scene root");
            Assert.AreEqual(1, set.Entries.Count);
            Assert.AreEqual("third_person", set.Entries[0].Role);
            Assert.AreEqual("Head", set.Entries[0].Label);
            Assert.AreSame(head.transform, set.Entries[0].Node);

            var view = scene.GetComponent<ViewContextController>();
            Assert.IsNotNull(view, "import should add a ViewContextController");
            Assert.IsTrue(headRenderer.enabled, "third_person is visible in the default third-person context");
            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.IsFalse(headRenderer.enabled, "third_person hides in first-person");
        }

        [Test]
        public void PrimitiveHint_Import_AddsSetAndRegistersSwapSlot()
        {
            var scene = NewGo("scene");
            var body = NewChild(scene, "body");
            var filter = body.AddComponent<MeshFilter>();
            var renderer = body.AddComponent<MeshRenderer>();
            var unityMesh = NewTriangleMesh("body");
            filter.sharedMesh = unityMesh;
            var original = NewMaterial("original");
            renderer.sharedMaterials = new[] { original };

            // A glTF root whose mesh primitive 0 carries the hint; node.Mesh.Value resolves through Root.
            var gltf = new GLTFRoot { Meshes = new List<GLTFMesh>(), Nodes = new List<Node>() };
            var prim = new MeshPrimitive();
            prim.AddExtension(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME,
                new KHR_mesh_primitive_visibility_hint { Role = "first_person", Label = "BodyPrim" });
            gltf.Meshes.Add(new GLTFMesh { Primitives = new List<MeshPrimitive> { prim } });
            var node = new Node { Name = "body", Mesh = new MeshId { Id = 0, Root = gltf } };

            var ctx = new VisibilityHintImportContext(null);
            ctx.OnAfterImportNode(node, 0, body);
            ctx.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<PrimitiveVisibilityHintSet>();
            Assert.IsNotNull(set, "import should add a PrimitiveVisibilityHintSet to the scene root");
            Assert.AreEqual(1, set.Entries.Count);
            Assert.AreSame(unityMesh, set.Entries[0].Mesh);
            Assert.AreEqual(0, set.Entries[0].SubMesh);
            Assert.AreEqual("first_person", set.Entries[0].Role);

            var view = scene.GetComponent<ViewContextController>();
            Assert.IsNotNull(view);
            // first_person is hidden in the default third-person context -> swapped to the invisible material.
            Assert.AreNotSame(original, renderer.sharedMaterials[0], "hidden slot swaps to the invisible material");
            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.AreSame(original, renderer.sharedMaterials[0], "slot restores the original material in first-person");
        }

        [Test]
        public void NoHints_Import_AddsNoComponents()
        {
            var scene = NewGo("scene");
            var plain = NewChild(scene, "plain");
            plain.AddComponent<MeshRenderer>();

            var ctx = new VisibilityHintImportContext(null);
            ctx.OnAfterImportNode(new Node { Name = "plain" }, 0, plain);
            ctx.OnAfterImportScene(null, 0, scene);

            Assert.IsNull(scene.GetComponent<ViewContextController>(), "no hints -> no controller added");
            Assert.IsNull(scene.GetComponent<NodeVisibilityHintSet>(), "no hints -> no node set added");
            Assert.IsNull(scene.GetComponent<PrimitiveVisibilityHintSet>(), "no hints -> no primitive set added");
        }

        [Test]
        public void NodeAndPrimitiveHints_Import_ShareOneController()
        {
            var scene = NewGo("scene");

            // Node hint on "head".
            var head = NewChild(scene, "head");
            var headRenderer = head.AddComponent<MeshRenderer>();
            var headNode = new Node { Name = "head" };
            headNode.AddExtension(KHR_node_visibility_hint.EXTENSION_NAME,
                new KHR_node_visibility_hint { Role = "third_person" });

            // Primitive hint on "body".
            var body = NewChild(scene, "body");
            var filter = body.AddComponent<MeshFilter>();
            var bodyRenderer = body.AddComponent<MeshRenderer>();
            var unityMesh = NewTriangleMesh("body");
            filter.sharedMesh = unityMesh;
            var original = NewMaterial("original");
            bodyRenderer.sharedMaterials = new[] { original };

            var gltf = new GLTFRoot { Meshes = new List<GLTFMesh>(), Nodes = new List<Node>() };
            var prim = new MeshPrimitive();
            prim.AddExtension(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME,
                new KHR_mesh_primitive_visibility_hint { Role = "third_person" });
            gltf.Meshes.Add(new GLTFMesh { Primitives = new List<MeshPrimitive> { prim } });
            var bodyNode = new Node { Name = "body", Mesh = new MeshId { Id = 0, Root = gltf } };

            var ctx = new VisibilityHintImportContext(null);
            ctx.OnAfterImportNode(headNode, 0, head);
            ctx.OnAfterImportNode(bodyNode, 1, body);
            ctx.OnAfterImportScene(null, 0, scene);

            Assert.IsNotNull(scene.GetComponent<NodeVisibilityHintSet>());
            Assert.IsNotNull(scene.GetComponent<PrimitiveVisibilityHintSet>());
            var controllers = scene.GetComponents<ViewContextController>();
            Assert.AreEqual(1, controllers.Length, "both sets must share a single ViewContextController");

            var view = controllers[0];
            // Both are third_person: visible now, both hidden in first-person (renderer disabled + material swap).
            Assert.IsTrue(headRenderer.enabled);
            Assert.AreSame(original, bodyRenderer.sharedMaterials[0]);

            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.IsFalse(headRenderer.enabled, "node hint disables the renderer in first-person");
            Assert.AreNotSame(original, bodyRenderer.sharedMaterials[0], "primitive hint swaps to invisible in first-person");
        }
    }
}
