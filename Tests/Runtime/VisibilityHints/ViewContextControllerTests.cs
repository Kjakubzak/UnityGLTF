using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGLTF.VisibilityHints.Tests
{
    public class ViewContextControllerTests
    {
        private static readonly string[] Untagged = new string[0];
        private static readonly string[] FirstPerson = { "first_person" };
        private static readonly string[] ThirdPerson = { "third_person" };
        private static readonly string[] ThirdPersonMirror = { "third_person", "ACME_mirror" };
        private static readonly string[] BothStandard = { "first_person", "third_person" };

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

        private sealed class MirrorOnlyResolver : IVisibilityRoleResolver
        {
            public readonly List<string> Seen = new List<string>();

            public bool TryResolve(string role, IReadOnlyCollection<string> viewTags, out bool visible)
            {
                Seen.Add(role);
                visible = false;
                if (role != "ACME_mirror_only") return false;
                foreach (var tag in viewTags)
                    if (tag == "ACME_mirror") visible = true;
                return true;
            }
        }

        private static IEnumerable<TestCaseData> RoleTable()
        {
            yield return new TestCaseData(null, Untagged, true).SetName("NoHint_Untagged_Visible");
            yield return new TestCaseData("always", Untagged, true).SetName("Always_Untagged_Visible");
            yield return new TestCaseData("first_person", Untagged, false).SetName("FirstPerson_Untagged_Hidden");
            yield return new TestCaseData("third_person", Untagged, false).SetName("ThirdPerson_Untagged_Hidden");
            yield return new TestCaseData("first_person", FirstPerson, true).SetName("FirstPerson_FirstPersonView_Visible");
            yield return new TestCaseData("third_person", FirstPerson, false).SetName("ThirdPerson_FirstPersonView_Hidden");
            yield return new TestCaseData("first_person", ThirdPerson, false).SetName("FirstPerson_ThirdPersonView_Hidden");
            yield return new TestCaseData("third_person", ThirdPersonMirror, true).SetName("ThirdPerson_ThirdPersonMirrorView_Visible");
            yield return new TestCaseData("first_person", ThirdPersonMirror, false).SetName("FirstPerson_ThirdPersonMirrorView_Hidden");
            yield return new TestCaseData("always", ThirdPersonMirror, true).SetName("Always_ThirdPersonMirrorView_Visible");
            yield return new TestCaseData("first_person", BothStandard, true).SetName("FirstPerson_BothTags_Visible");
            yield return new TestCaseData("third_person", BothStandard, true).SetName("ThirdPerson_BothTags_Visible");
            yield return new TestCaseData("ACME_mirror_only", ThirdPersonMirror, false).SetName("UnsupportedCustomRole_Hidden");
            yield return new TestCaseData("First_Person", FirstPerson, false).SetName("DifferentlyCasedRole_IsCustomAndHidden");
            yield return new TestCaseData("first_person", new[] { "First_Person" }, false).SetName("ViewTags_AreCaseSensitive");
        }

        [TestCaseSource(nameof(RoleTable))]
        public void StandardRoles_FollowViewTagMembership(string role, string[] viewTags, bool expected)
        {
            Assert.AreEqual(expected, VisibilityHintEvaluator.IsRoleVisible(role, viewTags));
        }

        [Test]
        public void CustomRoles_AreHiddenUnlessTheOverrideOrAResolverAllowsThem()
        {
            Assert.IsFalse(VisibilityHintEvaluator.IsRoleVisible("ACME_mirror_only", ThirdPersonMirror));
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible(
                "ACME_mirror_only", ThirdPersonMirror, renderUnsupportedRoles: true));

            var resolver = new MirrorOnlyResolver();
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("ACME_mirror_only", ThirdPersonMirror, resolver));
            Assert.IsFalse(VisibilityHintEvaluator.IsRoleVisible("ACME_mirror_only", ThirdPerson, resolver));
            Assert.IsFalse(VisibilityHintEvaluator.IsRoleVisible("ACME_mirror_only", ThirdPerson, resolver, true),
                "a supported custom role follows its resolver, not the unsupported-role override");
            Assert.IsFalse(VisibilityHintEvaluator.IsRoleVisible("ACME_other", ThirdPersonMirror, resolver),
                "a role the resolver does not support falls back to the role-support rule");
            Assert.IsFalse(VisibilityHintEvaluator.IsRoleVisible("First_Person", FirstPerson, resolver));
            Assert.IsTrue(VisibilityHintEvaluator.IsRoleVisible("third_person", ThirdPerson, resolver));
            CollectionAssert.AreEqual(
                new[] { "ACME_mirror_only", "ACME_mirror_only", "ACME_mirror_only", "ACME_other", "First_Person" },
                resolver.Seen,
                "standard roles never reach the resolver; differently cased tokens do, as custom roles");
        }

        [Test]
        public void Controller_DefaultsToUntaggedViewAndHidesUnsupportedRoles()
        {
            var root = NewGo("root");
            var custom = NewGo("custom");
            custom.transform.SetParent(root.transform, false);
            var third = NewGo("third");
            third.transform.SetParent(root.transform, false);
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = custom.transform, Role = "ACME_mirror_only" },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = third.transform, Role = "third_person" },
            });
            var controller = root.AddComponent<ViewContextController>();

            CollectionAssert.IsEmpty(controller.DefaultViewTags);
            Assert.IsFalse(controller.RenderUnsupportedRoles);
            Assert.IsNull(controller.CustomRoleResolver);
            Assert.IsFalse(controller.ShouldRenderNode(third.transform, true), "an untagged view hides third_person");
            Assert.IsTrue(controller.ShouldRenderNode(root.transform, true), "un-hinted content is visible");
            Assert.IsFalse(controller.ShouldRenderNodeForView(custom.transform, ThirdPersonMirror, true));

            controller.RenderUnsupportedRoles = true;
            Assert.IsTrue(controller.ShouldRenderNodeForView(custom.transform, ThirdPerson, true));
            controller.RenderUnsupportedRoles = false;
            controller.CustomRoleResolver = new MirrorOnlyResolver();
            Assert.IsTrue(controller.ShouldRenderNodeForView(custom.transform, ThirdPersonMirror, true));
            Assert.IsFalse(controller.ShouldRenderNodeForView(custom.transform, ThirdPerson, true));
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

            Assert.IsFalse(controller.ShouldRenderPrimitiveForView(renderer, 0, Untagged, true));
            Assert.IsFalse(controller.ShouldRenderPrimitiveForView(renderer, 0, FirstPerson, true));
            Assert.IsTrue(controller.ShouldRenderPrimitiveForView(renderer, 0, ThirdPersonMirror, true));

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

            Assert.IsFalse(controller.ShouldRenderPrimitiveForView(renderer, 0, FirstPerson, true));
            nodeEntry.Role = "first_person";
            Assert.IsTrue(controller.ShouldRenderPrimitiveForView(renderer, 0, FirstPerson, true));
            primitiveEntry.Role = "third_person";
            Assert.IsFalse(controller.ShouldRenderPrimitiveForView(renderer, 0, FirstPerson, true));
        }

        [Test]
        public void ExplicitViewQueries_DoNotContaminateEachOtherOrDefaultTags()
        {
            var root = NewGo("root");
            var node = NewGo("node");
            node.transform.SetParent(root.transform, false);
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = node.transform, Role = "third_person" },
            });
            var controller = root.AddComponent<ViewContextController>();
            controller.SetDefaultViewTags(FirstPerson);

            Assert.IsTrue(controller.ShouldRenderNodeForView(node.transform, ThirdPerson, true));
            Assert.IsFalse(controller.ShouldRenderNodeForView(node.transform, FirstPerson, true));
            Assert.IsTrue(controller.ShouldRenderNodeForView(node.transform, BothStandard, true));
            Assert.IsFalse(controller.ShouldRenderNode(node.transform, true));
            CollectionAssert.AreEqual(FirstPerson, controller.DefaultViewTags,
                "explicit per-view queries do not change the default tags");
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

            Assert.IsTrue(controller.ShouldRenderPrimitiveForView(firstRenderer, 0, FirstPerson, true));
            Assert.IsFalse(controller.ShouldRenderPrimitiveForView(thirdRenderer, 0, FirstPerson, true));
            Assert.IsFalse(controller.ShouldRenderPrimitiveForView(firstRenderer, 0, ThirdPerson, true));
            Assert.IsTrue(controller.ShouldRenderPrimitiveForView(thirdRenderer, 0, ThirdPerson, true));
        }

        [Test]
        public void CoreVisibility_IsConjoinedAndCannotBeOverridden()
        {
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderNodeVisualContent("always", Untagged, false));
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderNodeVisualContent("first_person", FirstPerson, false));
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderNodeVisualContent(
                "ACME_mirror_only", ThirdPersonMirror, false, renderUnsupportedRoles: true));
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(
                "always", "always", FirstPerson, false));
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

            using (adapter.ApplyForView(renderer, FirstPerson, true))
                Assert.AreSame(noDraw, renderer.sharedMaterials[0]);

            Assert.AreSame(authored, renderer.sharedMaterials[0],
                "disposing the per-view scope restores the exact authored material reference");
            using (adapter.ApplyForView(renderer, Untagged, true))
                Assert.AreSame(noDraw, renderer.sharedMaterials[0],
                    "an untagged view hides third_person content");
            using (adapter.ApplyForView(renderer, ThirdPersonMirror, true))
                Assert.AreSame(authored, renderer.sharedMaterials[0]);
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
            renderer.shadowCastingMode = ShadowCastingMode.On;

            using (adapter.ApplyForView(renderer, FirstPerson, true))
            {
                Assert.AreSame(first, renderer.sharedMaterials[0]);
                Assert.AreSame(noDraw, renderer.sharedMaterials[1]);
                Assert.AreEqual(ShadowCastingMode.On, renderer.shadowCastingMode,
                    "a visible node keeps casting shadows; the no-draw material covers the hidden primitive");
            }

            CollectionAssert.AreEqual(new[] { first, second }, renderer.sharedMaterials);
        }

        [Test]
        public void ScopedMaterialAdapter_TurnsOffShadowsOfAHiddenNodeAndRestoresThem()
        {
            var root = NewGo("root");
            var body = NewGo("body");
            body.transform.SetParent(root.transform, false);
            var renderer = AddRenderer(body, NewMesh());
            renderer.sharedMaterials = new[] { NewMaterial("authored") };
            renderer.shadowCastingMode = ShadowCastingMode.TwoSided;
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = body.transform, Role = "third_person" },
            });
            root.AddComponent<ViewContextController>();
            var adapter = root.AddComponent<ScopedMaterialVisibilityAdapter>();
            adapter.NoDrawMaterial = NewMaterial("host_no_draw");

            using (adapter.ApplyForView(renderer, FirstPerson, true))
                Assert.AreEqual(ShadowCastingMode.Off, renderer.shadowCastingMode);
            Assert.AreEqual(ShadowCastingMode.TwoSided, renderer.shadowCastingMode);

            using (adapter.ApplyForView(renderer, ThirdPerson, true))
                Assert.AreEqual(ShadowCastingMode.TwoSided, renderer.shadowCastingMode);

            using (adapter.ApplyForView(renderer, FirstPerson, true))
                renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            Assert.AreEqual(ShadowCastingMode.ShadowsOnly, renderer.shadowCastingMode,
                "restoration leaves a value changed by another host system intact");
        }

        [Test]
        public void ScopedLightAdapter_TurnsOffLightsOnHiddenNodesForOneView()
        {
            var root = NewGo("root");
            var thirdNode = NewGo("thirdLight");
            thirdNode.transform.SetParent(root.transform, false);
            var alwaysNode = NewGo("alwaysLight");
            alwaysNode.transform.SetParent(root.transform, false);
            var thirdLight = thirdNode.AddComponent<Light>();
            var thirdCamera = thirdNode.AddComponent<Camera>();
            var alwaysLight = alwaysNode.AddComponent<Light>();
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = thirdNode.transform, Role = "third_person" },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = alwaysNode.transform, Role = "always" },
            });
            root.AddComponent<ViewContextController>();
            var adapter = root.AddComponent<ScopedMaterialVisibilityAdapter>();
            var lights = new[] { thirdLight, alwaysLight };

            using (adapter.ApplyLightsForView(lights, FirstPerson, _ => true))
            {
                Assert.IsFalse(thirdLight.enabled, "a light on a hidden node must not light the view");
                Assert.IsTrue(alwaysLight.enabled);
                Assert.IsTrue(thirdCamera.enabled, "cameras on hidden nodes are not affected");
                Assert.Throws<System.InvalidOperationException>(() =>
                    adapter.ApplyLightsForView(new[] { thirdLight }, ThirdPerson, _ => true));
            }
            Assert.IsTrue(thirdLight.enabled);
            Assert.IsTrue(alwaysLight.enabled);

            using (adapter.ApplyLightsForView(lights, ThirdPerson, _ => true))
                Assert.IsTrue(thirdLight.enabled);
            using (adapter.ApplyLightsForView(lights, ThirdPerson, node => node != alwaysNode.transform))
                Assert.IsFalse(alwaysLight.enabled, "core visibility also hides the node's light");
            Assert.IsTrue(alwaysLight.enabled);
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

            using (adapter.ApplyForView(renderer, FirstPerson, true))
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

            using (adapter.ApplyForView(renderer, FirstPerson, true))
            {
                Assert.Throws<System.InvalidOperationException>(() =>
                    adapter.ApplyForView(renderer, ThirdPerson, true));
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
                adapter.ApplyForView(renderer, FirstPerson, true));
        }

        [Test]
        public void DefaultViewTags_ChangeRaisesOnceAndCanReturnToUntagged()
        {
            var controller = NewGo("root").AddComponent<ViewContextController>();
            var observed = new List<string[]>();
            controller.OnDefaultViewTagsChanged += tags => observed.Add(new List<string>(tags).ToArray());

            controller.SetDefaultViewTags(new[] { "first_person", "ACME_mirror", "first_person", null, "" });
            controller.SetDefaultViewTags(new[] { "first_person", "ACME_mirror" });
            controller.SetDefaultViewTags(null);

            Assert.AreEqual(2, observed.Count);
            CollectionAssert.AreEqual(new[] { "first_person", "ACME_mirror" }, observed[0],
                "null, empty and duplicate tags are dropped");
            CollectionAssert.IsEmpty(observed[1]);
            CollectionAssert.IsEmpty(controller.DefaultViewTags);
        }

        [Test]
        public void DisplayNames_FollowLabelThenNodeNameThenRole()
        {
            var node = NewGo("HeadNode").transform;
            var unnamed = NewGo(string.Empty).transform;

            Assert.AreEqual("Head", NodeVisibilityHintSet.GetDisplayName(new NodeVisibilityHintSet.NodeVisibilityEntry
                { Node = node, Role = "third_person", Label = "Head" }));
            Assert.AreEqual("HeadNode", NodeVisibilityHintSet.GetDisplayName(new NodeVisibilityHintSet.NodeVisibilityEntry
                { Node = node, Role = "third_person", Label = string.Empty }));
            Assert.AreEqual("third_person", NodeVisibilityHintSet.GetDisplayName(
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = unnamed, Role = "third_person" }));
            Assert.AreEqual("first_person", NodeVisibilityHintSet.GetDisplayName(
                new NodeVisibilityHintSet.NodeVisibilityEntry { Role = "first_person" }));

            Assert.AreEqual("Mask Shell", PrimitiveVisibilityHintSet.GetDisplayName(
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry { Role = "third_person", Label = "Mask Shell" }));
            Assert.AreEqual("third_person", PrimitiveVisibilityHintSet.GetDisplayName(
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry { Role = "third_person", Label = string.Empty }));
        }
    }
}
