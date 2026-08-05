using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Tests
{
    public class ViewContextControllerTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var created in _created)
                if (created != null) Object.DestroyImmediate(created);
            _created.Clear();
        }

        private GameObject NewGo(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        private Mesh NewMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            _created.Add(mesh);
            return mesh;
        }

        private Material NewMaterial(string name)
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader");
            var material = new Material(shader) { name = name };
            _created.Add(material);
            return material;
        }

        private static MeshRenderer AddRenderer(GameObject go, Mesh mesh)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return go.AddComponent<MeshRenderer>();
        }

        [Test]
        public void StandardRoles_NoContextNeverSuppressesContent()
        {
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("always", null));
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("first_person", null));
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("third_person", null));
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("ACME_mirror_only", null));
        }

        [Test]
        public void StandardRoles_UseExactFirstPersonPredicate()
        {
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("first_person", "first_person"));
            Assert.IsFalse(VisibilityHintEvaluator.IsRoleVisible("third_person", "first_person"));

            Assert.IsFalse(VisibilityHintEvaluator.IsRoleVisible("first_person", "third_person"));
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("third_person", "third_person"));

            Assert.IsFalse(VisibilityHintEvaluator.IsRoleVisible("first_person", "mirror"));
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("third_person", "mirror"));
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("First_Person", "first_person"));
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("ACME_mirror_only", "first_person"));
        }

        [Test]
        public void Controller_EvaluatesNodeAndPrimitiveWithoutMutatingSceneState()
        {
            var root = NewGo("root");
            var body = NewGo("body");
            body.transform.SetParent(root.transform, false);
            var mesh = NewMesh();
            var renderer = AddRenderer(body, mesh);

            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = body.transform, Role = "always" },
            });
            root.AddComponent<PrimitiveVisibilityHintSet>().Bind(new[]
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                    { Mesh = mesh, SubMesh = 0, Role = "third_person" },
            });
            var controller = root.AddComponent<ViewContextController>();

            bool originalEnabled = renderer.enabled;
            var originalMesh = body.GetComponent<MeshFilter>().sharedMesh;
            var originalMaterials = renderer.sharedMaterials;

            Assert.IsTrue(controller.ShouldRenderPrimitiveForContext(renderer, 0, null, true));
            Assert.IsFalse(controller.ShouldRenderPrimitiveForContext(renderer, 0, "first_person", true));
            Assert.IsTrue(controller.ShouldRenderPrimitiveForContext(renderer, 0, "mirror", true));

            Assert.AreEqual(originalEnabled, renderer.enabled);
            Assert.AreSame(originalMesh, body.GetComponent<MeshFilter>().sharedMesh);
            CollectionAssert.AreEqual(originalMaterials, renderer.sharedMaterials);
        }

        [Test]
        public void IndexedEntriesUseLastDuplicateAndReadLiveRoleValues()
        {
            var root = NewGo("root");
            var body = NewGo("body");
            body.transform.SetParent(root.transform, false);
            var mesh = NewMesh();
            var renderer = AddRenderer(body, mesh);
            var nodeEntry = new NodeVisibilityHintSet.NodeVisibilityEntry
                { Node = body.transform, Role = "third_person" };
            var primitiveEntry = new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                { Mesh = mesh, SubMesh = 0, Role = "first_person" };
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[] { nodeEntry });
            root.AddComponent<PrimitiveVisibilityHintSet>().Bind(new[]
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                    { Mesh = mesh, SubMesh = 0, Role = "third_person" },
                primitiveEntry,
            });
            var controller = root.AddComponent<ViewContextController>();

            Assert.IsFalse(controller.ShouldRenderPrimitiveForContext(
                renderer, 0, "first_person", true));
            nodeEntry.Role = "first_person";
            Assert.IsTrue(controller.ShouldRenderPrimitiveForContext(
                renderer, 0, "first_person", true));
            primitiveEntry.Role = "third_person";
            Assert.IsFalse(controller.ShouldRenderPrimitiveForContext(
                renderer, 0, "first_person", true));
        }

        [Test]
        public void ExplicitViewQueries_DoNotContaminateEachOtherOrConvenienceContext()
        {
            var root = NewGo("root");
            var node = NewGo("node");
            node.transform.SetParent(root.transform, false);
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = node.transform, Role = "third_person" },
            });
            var controller = root.AddComponent<ViewContextController>();

            Assert.IsTrue(controller.ShouldRenderNodeForContext(node.transform, "third_person", true));
            Assert.IsFalse(controller.ShouldRenderNodeForContext(node.transform, "first_person", true));
            Assert.IsTrue(controller.ShouldRenderNodeForContext(node.transform, "third_person", true));
            Assert.IsNull(controller.ActiveContext, "explicit per-view queries do not change global host state");
        }

        [Test]
        public void SharedMesh_IsEvaluatedPerContainingNodeInstance()
        {
            var root = NewGo("root");
            var first = NewGo("first"); first.transform.SetParent(root.transform, false);
            var third = NewGo("third"); third.transform.SetParent(root.transform, false);
            var mesh = NewMesh();
            var firstRenderer = AddRenderer(first, mesh);
            var thirdRenderer = AddRenderer(third, mesh);

            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = first.transform, Role = "first_person" },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = third.transform, Role = "third_person" },
            });
            root.AddComponent<PrimitiveVisibilityHintSet>().Bind(new[]
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                    { Mesh = mesh, SubMesh = 0, Role = "always" },
            });
            var controller = root.AddComponent<ViewContextController>();

            Assert.IsTrue(controller.ShouldRenderPrimitiveForContext(firstRenderer, 0, "first_person", true));
            Assert.IsFalse(controller.ShouldRenderPrimitiveForContext(thirdRenderer, 0, "first_person", true));
            Assert.IsFalse(controller.ShouldRenderPrimitiveForContext(firstRenderer, 0, "third_person", true));
            Assert.IsTrue(controller.ShouldRenderPrimitiveForContext(thirdRenderer, 0, "third_person", true));
        }

        [Test]
        public void CoreVisibility_IsConjoinedAndCannotBeOverridden()
        {
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderNodeVisualContent("always", null, false));
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderNodeVisualContent("first_person", "first_person", false));
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(
                "always", "always", "first_person", false));
        }

        [Test]
        public void ScopedMaterialAdapter_IsOneNonPersistentRendererRoute()
        {
            var root = NewGo("root");
            var body = NewGo("body");
            body.transform.SetParent(root.transform, false);
            var mesh = NewMesh();
            var renderer = AddRenderer(body, mesh);
            var authored = NewMaterial("authored");
            var noDraw = NewMaterial("host_no_draw");
            renderer.sharedMaterials = new[] { authored };

            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry
                    { Node = body.transform, Role = "third_person" },
            });
            root.AddComponent<ViewContextController>();
            var adapter = root.AddComponent<ScopedMaterialVisibilityAdapter>();
            adapter.NoDrawMaterial = noDraw;

            using (adapter.ApplyForView(renderer, "first_person", true))
                Assert.AreSame(noDraw, renderer.sharedMaterials[0]);

            Assert.AreSame(authored, renderer.sharedMaterials[0],
                "disposing the per-view scope restores the exact authored material reference");
            using (adapter.ApplyForView(renderer, null, true))
                Assert.AreSame(authored, renderer.sharedMaterials[0],
                    "no supplied context never suppresses content");
        }

        [Test]
        public void ScopedMaterialAdapter_CanSuppressOnlyOnePrimitiveSlot()
        {
            var root = NewGo("root");
            var body = NewGo("body");
            body.transform.SetParent(root.transform, false);
            var mesh = NewMesh();
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 0, 2, 1 }, 1);
            var renderer = AddRenderer(body, mesh);
            var first = NewMaterial("first");
            var second = NewMaterial("second");
            var noDraw = NewMaterial("host_no_draw");
            renderer.sharedMaterials = new[] { first, second };
            root.AddComponent<PrimitiveVisibilityHintSet>().Bind(new[]
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                    { Mesh = mesh, SubMesh = 1, Role = "third_person" },
            });
            root.AddComponent<ViewContextController>();
            var adapter = root.AddComponent<ScopedMaterialVisibilityAdapter>();
            adapter.NoDrawMaterial = noDraw;

            using (adapter.ApplyForView(renderer, "first_person", true))
            {
                Assert.AreSame(first, renderer.sharedMaterials[0]);
                Assert.AreSame(noDraw, renderer.sharedMaterials[1]);
            }

            CollectionAssert.AreEqual(new[] { first, second }, renderer.sharedMaterials);
        }

        [Test]
        public void ScopedMaterialAdapter_DoesNotOverwriteAHostChangeDuringScope()
        {
            var root = NewGo("root");
            var body = NewGo("body");
            body.transform.SetParent(root.transform, false);
            var renderer = AddRenderer(body, NewMesh());
            var authored = NewMaterial("authored");
            var noDraw = NewMaterial("host_no_draw");
            var runtimeVariant = NewMaterial("runtime_variant");
            renderer.sharedMaterials = new[] { authored };
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry
                    { Node = body.transform, Role = "third_person" },
            });
            root.AddComponent<ViewContextController>();
            var adapter = root.AddComponent<ScopedMaterialVisibilityAdapter>();
            adapter.NoDrawMaterial = noDraw;

            using (adapter.ApplyForView(renderer, "first_person", true))
                renderer.sharedMaterials = new[] { runtimeVariant };

            Assert.AreSame(runtimeVariant, renderer.sharedMaterials[0],
                "restoration leaves a material changed by another host system intact");
        }

        [Test]
        public void ScopedMaterialAdapter_RejectsOverlappingScopesForOneRenderer()
        {
            var root = NewGo("root");
            var body = NewGo("body");
            body.transform.SetParent(root.transform, false);
            var renderer = AddRenderer(body, NewMesh());
            var authored = NewMaterial("authored");
            var noDraw = NewMaterial("host_no_draw");
            renderer.sharedMaterials = new[] { authored };
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry
                    { Node = body.transform, Role = "third_person" },
            });
            root.AddComponent<ViewContextController>();
            var adapter = root.AddComponent<ScopedMaterialVisibilityAdapter>();
            adapter.NoDrawMaterial = noDraw;

            using (adapter.ApplyForView(renderer, "first_person", true))
            {
                Assert.Throws<System.InvalidOperationException>(() =>
                    adapter.ApplyForView(renderer, "third_person", true));
                Assert.AreSame(noDraw, renderer.sharedMaterials[0]);
            }
            Assert.AreSame(authored, renderer.sharedMaterials[0]);
        }

        [Test]
        public void ScopedMaterialAdapter_RejectsAmbiguousMaterialToSubmeshLayouts()
        {
            var root = NewGo("root");
            var body = NewGo("body");
            body.transform.SetParent(root.transform, false);
            var renderer = AddRenderer(body, NewMesh());
            renderer.sharedMaterials = new[] { NewMaterial("slot0"), NewMaterial("surplus") };
            root.AddComponent<ViewContextController>();
            var adapter = root.AddComponent<ScopedMaterialVisibilityAdapter>();
            adapter.NoDrawMaterial = NewMaterial("host_no_draw");

            Assert.Throws<System.InvalidOperationException>(() =>
                adapter.ApplyForView(renderer, "first_person", true));
        }

        [Test]
        public void ConvenienceContext_ChangeRaisesOnceAndCanReturnToNoContext()
        {
            var controller = NewGo("root").AddComponent<ViewContextController>();
            var observed = new List<string>();
            controller.OnViewContextChanged += context => observed.Add(context);

            Assert.IsFalse(controller.HasActiveContext);
            controller.SetActiveContext("first_person");
            controller.SetActiveContext("first_person");
            controller.ClearActiveContext();

            Assert.AreEqual(2, observed.Count);
            Assert.AreEqual("first_person", observed[0]);
            Assert.IsNull(observed[1]);
            Assert.IsFalse(controller.HasActiveContext);
        }
    }
}
