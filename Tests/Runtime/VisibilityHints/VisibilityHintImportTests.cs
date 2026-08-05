using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityGLTF.Plugins;

namespace UnityGLTF.VisibilityHints.Tests
{
    /// <summary>
    /// Import-context tests. Rather than run a full (headless-flaky) scene load, these drive the
    /// <see cref="VisibilityHintImportContext"/> callbacks directly with hand-built glTF nodes/primitives and real
    /// GameObjects, then assert the wired metadata and pure <see cref="ViewContextController"/> predicates. The
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
        public void NodeHint_Import_AddsSetAndExposesPurePredicate()
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
            Assert.IsTrue(view.ShouldRenderNodeForContext(head.transform, null, true),
                "no supplied context never suppresses content");
            Assert.IsFalse(view.ShouldRenderNodeForContext(head.transform, "first_person", true));
            Assert.IsTrue(headRenderer.enabled, "predicate queries do not mutate renderer state");
        }

        [Test]
        public void PrimitiveHint_Import_AddsSetAndExposesPurePredicate()
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
            Assert.IsTrue(view.ShouldRenderPrimitiveForContext(renderer, 0, null, true));
            Assert.IsTrue(view.ShouldRenderPrimitiveForContext(renderer, 0, "first_person", true));
            Assert.IsFalse(view.ShouldRenderPrimitiveForContext(renderer, 0, "third_person", true));
            Assert.AreSame(original, renderer.sharedMaterials[0], "predicate queries preserve authored materials");
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
            Assert.IsFalse(view.ShouldRenderNodeForContext(head.transform, "first_person", true));
            Assert.IsFalse(view.ShouldRenderPrimitiveForContext(bodyRenderer, 0, "first_person", true));
            Assert.IsTrue(view.ShouldRenderNodeForContext(head.transform, "third_person", true));
            Assert.IsTrue(view.ShouldRenderPrimitiveForContext(bodyRenderer, 0, "third_person", true));
            Assert.IsTrue(headRenderer.enabled);
            Assert.AreSame(original, bodyRenderer.sharedMaterials[0],
                "evaluating either extension does not mutate shared scene state");
        }

        [Test]
        public void RequiredHints_FailWithoutAnExplicitCompleteHostCapability()
        {
            var nodeFactory = GLTFProperty.TryGetExtension(KHR_node_visibility_hint.EXTENSION_NAME);
            var primitiveFactory = GLTFProperty.TryGetExtension(
                KHR_mesh_primitive_visibility_hint.EXTENSION_NAME);
            Assert.IsNotNull(nodeFactory, "runtime schema registration must run before import");
            Assert.IsNotNull(primitiveFactory, "runtime schema registration must run before import");
            Assert.IsTrue(nodeFactory.RequiresRuntimeSupportForRequiredUse);
            Assert.IsTrue(primitiveFactory.RequiresRuntimeSupportForRequiredUse);
            var root = new GLTFRoot
            {
                ExtensionsRequired = new List<string>
                {
                    KHR_node_visibility_hint.EXTENSION_NAME,
                    KHR_mesh_primitive_visibility_hint.EXTENSION_NAME,
                },
            };

            Assert.Throws<UnityGLTF.GLTFLoadException>(() =>
                GLTFSceneImporter.ValidateRequiredExtensionSupport(
                    root, new List<GLTFImportPluginContext>()));
            Assert.Throws<UnityGLTF.GLTFLoadException>(() =>
                GLTFSceneImporter.ValidateRequiredExtensionSupport(
                    root, new List<GLTFImportPluginContext>
                    {
                        new VisibilityHintImportContext(null),
                    }));

            Assert.DoesNotThrow(() => GLTFSceneImporter.ValidateRequiredExtensionSupport(
                root, new List<GLTFImportPluginContext>
                {
                    new VisibilityHintImportContext(
                        null,
                        hostSupportsRequiredNodeUse: true,
                        hostSupportsRequiredPrimitiveUse: true),
                }));

            Assert.Throws<UnityGLTF.GLTFLoadException>(() =>
                GLTFSceneImporter.ValidateRequiredExtensionSupport(
                    root, new List<GLTFImportPluginContext>
                    {
                        new VisibilityHintImportContext(
                            null,
                            hostSupportsRequiredNodeUse: false,
                            hostSupportsRequiredPrimitiveUse: true),
                    }));
        }

        [Test]
        public void ImportPlugin_PropagatesIndependentRequiredUseCapabilities()
        {
            var plugin = ScriptableObject.CreateInstance<VisibilityHintImportPlugin>();
            _created.Add(plugin);

            var defaultContext = plugin.CreateInstance(null);
            Assert.IsFalse(defaultContext.SupportsRequiredExtension(
                VisibilityHintExtensionNames.NodeVisibilityHint));
            Assert.IsFalse(defaultContext.SupportsRequiredExtension(
                VisibilityHintExtensionNames.MeshPrimitiveVisibilityHint));

            plugin.HostSupportsRequiredNodeUse = true;
            var nodeOnlyContext = plugin.CreateInstance(null);
            Assert.IsTrue(nodeOnlyContext.SupportsRequiredExtension(
                VisibilityHintExtensionNames.NodeVisibilityHint));
            Assert.IsFalse(nodeOnlyContext.SupportsRequiredExtension(
                VisibilityHintExtensionNames.MeshPrimitiveVisibilityHint));

            plugin.HostSupportsRequiredPrimitiveUse = true;
            var completeContext = plugin.CreateInstance(null);
            Assert.IsTrue(completeContext.SupportsRequiredExtension(
                VisibilityHintExtensionNames.NodeVisibilityHint));
            Assert.IsTrue(completeContext.SupportsRequiredExtension(
                VisibilityHintExtensionNames.MeshPrimitiveVisibilityHint));
        }

        [Test]
        public void UsedOnlyHints_DoNotRequireARenderCapability()
        {
            var root = new GLTFRoot
            {
                ExtensionsUsed = new List<string> { KHR_node_visibility_hint.EXTENSION_NAME },
            };
            Assert.DoesNotThrow(() => GLTFSceneImporter.ValidateRequiredExtensionSupport(
                root, new List<GLTFImportPluginContext>()));
        }

        [UnityTest]
        public IEnumerator RequiredHints_AreRejectedByEveryPartialLoadApi()
        {
            GLTFRoot RequiredRoot() => new GLTFRoot
            {
                ExtensionsRequired = new List<string>
                    { KHR_node_visibility_hint.EXTENSION_NAME },
            };

            GLTFSceneImporter Importer()
            {
                var settings = GLTFSettings.GetDefaultSettings();
                foreach (var plugin in settings.ImportPlugins)
                    if (plugin is VisibilityHintImportPlugin visibility)
                    {
                        visibility.Enabled = true;
                        visibility.HostSupportsRequiredNodeUse = true;
                    }
                var options = new ImportOptions
                {
                    ThrowOnLowMemory = false,
                    ImportContext = new GLTFImportContext(settings),
                };
                return new GLTFSceneImporter(RequiredRoot(), new MemoryStream(), options);
            }

            var nodeImporter = Importer();
            yield return AssertRequiredBehaviorRejected(
                nodeImporter.LoadNodeAsync(0, CancellationToken.None));
            var meshImporter = Importer();
            yield return AssertRequiredBehaviorRejected(
                meshImporter.LoadMeshAsync(0, CancellationToken.None));
            var materialImporter = Importer();
            yield return AssertRequiredBehaviorRejected(materialImporter.LoadMaterialAsync(0));
        }

        private static IEnumerator AssertRequiredBehaviorRejected(Task load)
        {
            while (!load.IsCompleted) yield return null;
            Assert.IsFalse(load.IsCanceled);
            var exception = load.Exception?.Flatten().InnerException as UnityGLTF.GLTFLoadException;
            Assert.IsNotNull(exception,
                "partial load unexpectedly accepted a required scene-level behavior");
            StringAssert.Contains("required runtime behavior is unsupported", exception.Message);
        }

        [Test]
        public void NodeHintCallbacks_PreserveDistinctInstancesWithTheSameNodeIndex()
        {
            var scene = NewGo("scene");
            var firstInstance = NewChild(scene, "firstInstance");
            var secondInstance = NewChild(scene, "secondInstance");
            var node = new Node();
            node.AddExtension(KHR_node_visibility_hint.EXTENSION_NAME,
                new KHR_node_visibility_hint { Role = "third_person" });
            var context = new VisibilityHintImportContext(null);

            context.OnAfterImportNode(node, 4, firstInstance);
            context.OnAfterImportNode(node, 4, secondInstance);
            context.OnAfterImportScene(null, 0, scene);

            var entries = scene.GetComponent<NodeVisibilityHintSet>().Entries;
            Assert.AreEqual(2, entries.Count);
            Assert.AreSame(firstInstance.transform, entries[0].Node);
            Assert.AreSame(secondInstance.transform, entries[1].Node);
        }

        [Test]
        public void PrimitiveHintedMesh_OptsOutOfGeometryOnlyDeduplication()
        {
            var nodeObject = NewGo("node");
            var mesh = NewTriangleMesh("hinted");
            nodeObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            nodeObject.AddComponent<MeshRenderer>();
            var root = new GLTFRoot { Meshes = new List<GLTFMesh>() };
            var primitive = new MeshPrimitive();
            primitive.AddExtension(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME,
                new KHR_mesh_primitive_visibility_hint { Role = "third_person" });
            root.Meshes.Add(new GLTFMesh { Primitives = new List<MeshPrimitive> { primitive } });
            var node = new Node { Mesh = new MeshId { Id = 0, Root = root } };
            var context = new VisibilityHintImportContext(null);

            context.OnAfterImportNode(node, 0, nodeObject);

            Assert.IsFalse(context.CanDeduplicateMesh(mesh));
            Assert.IsTrue(context.CanDeduplicateMesh(NewTriangleMesh("plain")));
        }

        [Test]
        public void PrimitiveHintedDefinition_OptsOutBeforeUnityMeshConstruction()
        {
            var hintedPrimitive = new MeshPrimitive();
            hintedPrimitive.AddExtension(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME,
                new KHR_mesh_primitive_visibility_hint { Role = "third_person" });
            var context = new VisibilityHintImportContext(null);

            Assert.IsFalse(context.CanShareMeshData(
                new GLTFMesh { Primitives = new List<MeshPrimitive> { hintedPrimitive } }, 0));
            Assert.IsTrue(context.CanShareMeshData(
                new GLTFMesh { Primitives = new List<MeshPrimitive> { new MeshPrimitive() } }, 1));
        }

        [Test]
        public void GpuInstancingWrapper_ResolvesPrimitiveMeshWithoutSelectingAuthoredChildren()
        {
            var scene = NewGo("scene");
            var wrapper = NewChild(scene, "wrapper");
            var authoredChild = NewChild(wrapper, "authoredChild");
            authoredChild.AddComponent<MeshFilter>().sharedMesh = NewTriangleMesh("unrelated");
            authoredChild.AddComponent<MeshRenderer>();
            var instances = NewChild(wrapper, "Instances");
            var instance = NewChild(instances, "Instance 0");
            var instanceMesh = NewTriangleMesh("instanced");
            instance.AddComponent<MeshFilter>().sharedMesh = instanceMesh;
            instance.AddComponent<MeshRenderer>();

            var root = new GLTFRoot { Meshes = new List<GLTFMesh>() };
            var primitive = new MeshPrimitive();
            primitive.AddExtension(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME,
                new KHR_mesh_primitive_visibility_hint { Role = "third_person" });
            root.Meshes.Add(new GLTFMesh { Primitives = new List<MeshPrimitive> { primitive } });
            var node = new Node { Mesh = new MeshId { Id = 0, Root = root } };
            var context = new VisibilityHintImportContext(null);

            context.OnAfterImportNode(node, 0, wrapper);
            context.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<PrimitiveVisibilityHintSet>();
            Assert.IsNotNull(set);
            Assert.AreEqual(1, set.Entries.Count);
            Assert.AreSame(instanceMesh, set.Entries[0].Mesh);
        }
    }
}
