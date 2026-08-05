using System.Collections;
using System.Collections.Generic;
using System.IO;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityGLTF.Plugins;

namespace UnityGLTF.VisibilityHints.Tests
{
    /// <summary>
    /// Export/import composition tests. Most feed the exact extension objects written to an exported
    /// <see cref="GLTFRoot"/> into a fresh <see cref="VisibilityHintImportContext"/>; the shared-accessor case also
    /// performs a full <see cref="GLTFSceneImporter.LoadSceneAsync"/> import to cover UnityGLTF mesh caches.
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
        public void NodeHint_ExportThenImport_PreservesRoleAndPredicate()
        {
            // Author + export.
            var src = NewGo("srcRoot");
            var head = MakeMeshChild(src, "head", out _);
            src.AddComponent<NodeVisibilityHintSet>().Bind(new List<NodeVisibilityHintSet.NodeVisibilityEntry>
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry
                {
                    Node = head.transform,
                    Role = "third_person",
                    Label = "Head",
                    ExtensionsJson = "{\"ACME_visibility\":{\"mode\":3}}",
                    ExtrasJson = "{\"author\":\"test\"}",
                    AdditionalPropertiesJson = "{\"vendorFlag\":7}",
                },
            });

            var gltf = ExportToGltfRoot(src);

            var headNode = gltf.Nodes.Find(n => n.Name == "head");
            Assert.IsNotNull(headNode, "the hinted node should be exported");
            Assert.IsTrue(headNode.Extensions != null && headNode.Extensions.ContainsKey(KHR_node_visibility_hint.EXTENSION_NAME),
                "export should attach KHR_node_visibility_hint to the node");
            var exportedHint = headNode.Extensions[KHR_node_visibility_hint.EXTENSION_NAME]
                as KHR_node_visibility_hint;
            Assert.AreEqual(3, exportedHint.Extensions["ACME_visibility"]["mode"].Value<int>());
            Assert.AreEqual("test", exportedHint.Extras["author"].Value<string>());
            Assert.AreEqual(7, exportedHint.AdditionalProperties["vendorFlag"].Value<int>());
            WireMeshRoot(gltf, headNode);
            gltf.ExtensionsRequired = gltf.ExtensionsRequired ?? new List<string>();
            gltf.ExtensionsRequired.Add("ACME_visibility");

            // Re-import the exported node into a fresh scene.
            var dst = NewGo("dstRoot");
            var head2 = NewChild(dst, "head");
            var head2Renderer = head2.AddComponent<MeshRenderer>();

            var ctx = new VisibilityHintImportContext(null);
            ctx.OnAfterImportRoot(gltf);
            ctx.OnAfterImportNode(headNode, 0, head2);
            ctx.OnAfterImportScene(null, 0, dst);

            var set = dst.GetComponent<NodeVisibilityHintSet>();
            Assert.IsNotNull(set, "re-import should add a NodeVisibilityHintSet");
            Assert.AreEqual(1, set.Entries.Count);
            Assert.AreEqual("third_person", set.Entries[0].Role, "role survives export -> import");
            Assert.AreEqual("Head", set.Entries[0].Label, "label survives export -> import");
            Assert.AreEqual(3, JObject.Parse(set.Entries[0].ExtensionsJson)["ACME_visibility"]["mode"].Value<int>());
            Assert.AreEqual("test", JToken.Parse(set.Entries[0].ExtrasJson)["author"].Value<string>());
            Assert.AreEqual(7, JObject.Parse(set.Entries[0].AdditionalPropertiesJson)["vendorFlag"].Value<int>());
            CollectionAssert.Contains(set.Entries[0].RequiredCompanionExtensions, "ACME_visibility");

            var reexported = ExportToGltfRoot(dst);
            CollectionAssert.Contains(reexported.ExtensionsUsed, "ACME_visibility");
            CollectionAssert.Contains(reexported.ExtensionsRequired, "ACME_visibility");

            var view = dst.GetComponent<ViewContextController>();
            Assert.IsNotNull(view);
            Assert.IsTrue(view.ShouldRenderNodeForContext(head2.transform, null, true));
            Assert.IsFalse(view.ShouldRenderNodeForContext(head2.transform, "first_person", true));
            Assert.IsTrue(head2Renderer.enabled, "round-trip evaluation does not mutate renderer state");
        }

        [Test]
        public void PrimitiveHint_ExportThenImport_PreservesRoleAndPredicate()
        {
            // Author + export.
            var src = NewGo("srcRoot");
            MakeMeshChild(src, "body", out var srcMesh);
            src.AddComponent<PrimitiveVisibilityHintSet>().Bind(new List<PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry>
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry { Mesh = srcMesh, SubMesh = 0, Role = "first_person", Label = "BodyPrim" },
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
            Assert.AreEqual("first_person", set.Entries[0].Role, "role survives export -> import");
            Assert.AreSame(dstMesh, set.Entries[0].Mesh);
            Assert.AreEqual(0, set.Entries[0].SubMesh);

            var view = dst.GetComponent<ViewContextController>();
            Assert.IsNotNull(view);
            Assert.IsTrue(view.ShouldRenderPrimitiveForContext(renderer, 0, null, true));
            Assert.IsFalse(view.ShouldRenderPrimitiveForContext(renderer, 0, "third_person", true));
            Assert.IsTrue(view.ShouldRenderPrimitiveForContext(renderer, 0, "first_person", true));
            Assert.AreSame(original, renderer.sharedMaterials[0],
                "round-trip evaluation preserves the authored material");
        }

        [UnityTest]
        public IEnumerator SharedAccessorsWithDistinctPrimitiveHints_ImportAsDistinctSemanticMeshes()
        {
            var src = NewGo("srcRoot");
            var sharedMesh = NewTriangleMesh("shared");
            var first = NewChild(src, "first");
            first.AddComponent<MeshFilter>().sharedMesh = sharedMesh;
            first.AddComponent<MeshRenderer>().sharedMaterial = NewMaterial("first_mat");
            var second = NewChild(src, "second");
            second.AddComponent<MeshFilter>().sharedMesh = sharedMesh;
            second.AddComponent<MeshRenderer>().sharedMaterial = NewMaterial("second_mat");
            src.AddComponent<PrimitiveVisibilityHintSet>().Bind(new[]
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                    { Mesh = sharedMesh, SubMesh = 0, Role = "first_person" },
            });

            var exportSettings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in exportSettings.ExportPlugins)
                if (plugin is VisibilityHintExportPlugin) plugin.Enabled = true;
            var exporter = new GLTFSceneExporter(
                new[] { src.transform }, new ExportContext(exportSettings));
            var glb = exporter.SaveGLBToByteArray("semantic-meshes");
            var exportedRoot = exporter.GetRoot();
            Assert.AreEqual(2, exportedRoot.Meshes.Count,
                "different materials must force two glTF mesh definitions");
            var firstPrimitive = exportedRoot.Meshes[0].Primitives[0];
            var secondPrimitive = exportedRoot.Meshes[1].Primitives[0];
            Assert.AreEqual(
                firstPrimitive.Attributes[SemanticProperties.POSITION].Id,
                secondPrimitive.Attributes[SemanticProperties.POSITION].Id,
                "the fixture must exercise the importer's early accessor-ID hash cache");
            Assert.AreEqual(firstPrimitive.Indices.Id, secondPrimitive.Indices.Id);
            var secondHint = secondPrimitive.Extensions[
                KHR_mesh_primitive_visibility_hint.EXTENSION_NAME]
                as KHR_mesh_primitive_visibility_hint;
            Assert.IsNotNull(secondHint);
            secondHint.Role = "third_person";

            var importSettings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in importSettings.ImportPlugins)
                if (plugin is VisibilityHintImportPlugin) plugin.Enabled = true;
            var options = new ImportOptions
            {
                AnimationMethod = AnimationMethod.None,
                ThrowOnLowMemory = false,
                ImportContext = new GLTFImportContext(importSettings),
            };
            var stream = new MemoryStream(glb);
            var importer = new GLTFSceneImporter(exportedRoot, stream, options);
            var load = importer.LoadSceneAsync();
            while (!load.IsCompleted) yield return null;
            Assert.IsFalse(load.IsCanceled);
            Assert.IsFalse(load.IsFaulted, load.Exception?.Flatten().ToString());

            var imported = importer.LastLoadedScene;
            _created.Add(imported);
            var set = imported.GetComponent<PrimitiveVisibilityHintSet>();
            Assert.IsNotNull(set);
            Assert.AreEqual(2, set.Entries.Count);
            CollectionAssert.AreEquivalent(
                new[] { "first_person", "third_person" },
                new[] { set.Entries[0].Role, set.Entries[1].Role });
            Assert.AreNotSame(set.Entries[0].Mesh, set.Entries[1].Mesh,
                "geometry-only importer caches must not collapse distinct primitive metadata");

            importer.Dispose();
            stream.Dispose();
        }
    }
}
