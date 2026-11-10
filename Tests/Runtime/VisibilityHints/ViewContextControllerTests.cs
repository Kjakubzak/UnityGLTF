using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGLTF.VisibilityHints.Tests
{
    public class ViewContextControllerTests
    {
        private static readonly VisibilityRoleContext NoLogic = VisibilityRoleContext.NoLogic;
        private static readonly VisibilityRoleContext FirstPerson = FirstPersonRolePolicy.FirstPersonView;
        private static readonly VisibilityRoleContext ThirdPerson = FirstPersonRolePolicy.OtherView;
        private static readonly VisibilityRoleContext Mirror =
            FirstPersonRolePolicy.ForView(false, new[] { "ACME_mirror_only" });
        private static readonly VisibilityRoleContext BothStandard =
            new VisibilityRoleContext(new[] { "first_person", "third_person" }, null);

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

        private static string[] R(params string[] roles) => roles;

        private static IEnumerable<TestCaseData> RoleTable()
        {
            // An application with no role logic hides nothing.
            yield return new TestCaseData(null, NoLogic, true).SetName("NoHint_NoLogic_Visible");
            yield return new TestCaseData(R("always"), NoLogic, true).SetName("Always_NoLogic_Visible");
            yield return new TestCaseData(R("first_person"), NoLogic, true).SetName("FirstPerson_NoLogic_Visible");
            yield return new TestCaseData(R("third_person"), NoLogic, true).SetName("ThirdPerson_NoLogic_Visible");
            yield return new TestCaseData(R("ACME_mirror_only"), NoLogic, true).SetName("UnrecognizedCustom_NoLogic_Visible");
            // First-person logic: first_person in the user's own view, third_person everywhere else.
            yield return new TestCaseData(R("first_person"), FirstPerson, true).SetName("FirstPerson_FirstPersonView_Visible");
            yield return new TestCaseData(R("third_person"), FirstPerson, false).SetName("ThirdPerson_FirstPersonView_Hidden");
            yield return new TestCaseData(R("first_person"), ThirdPerson, false).SetName("FirstPerson_OtherView_Hidden");
            yield return new TestCaseData(R("third_person"), ThirdPerson, true).SetName("ThirdPerson_OtherView_Visible");
            yield return new TestCaseData(R("always"), FirstPerson, true).SetName("Always_FirstPersonView_Visible");
            yield return new TestCaseData(R("first_person"), BothStandard, true).SetName("FirstPerson_BothApply_Visible");
            yield return new TestCaseData(R("third_person"), BothStandard, true).SetName("ThirdPerson_BothApply_Visible");
            // Roles in one hint are alternatives; unrecognized custom roles are ignored.
            yield return new TestCaseData(R("first_person", "ACME_mirror_only"), ThirdPerson, false)
                .SetName("MultiRole_UnrecognizedCustomIgnored_Hidden");
            yield return new TestCaseData(R("first_person", "ACME_mirror_only"), Mirror, true)
                .SetName("MultiRole_RecognizedCustomApplies_Visible");
            yield return new TestCaseData(R("first_person", "ACME_mirror_only"), FirstPerson, true)
                .SetName("MultiRole_FirstPersonApplies_Visible");
            yield return new TestCaseData(R("ACME_mirror_only"), ThirdPerson, true).SetName("OnlyUnrecognizedCustom_NoRestriction");
            yield return new TestCaseData(R("ACME_mirror_only"), Mirror, true).SetName("RecognizedCustom_Applies_Visible");
            yield return new TestCaseData(R("ACME_mirror_only"),
                    FirstPersonRolePolicy.ForView(true, null, new[] { "ACME_mirror_only" }), false)
                .SetName("RecognizedCustom_DoesNotApply_Hidden");
            yield return new TestCaseData(R("always", "first_person"), ThirdPerson, true).SetName("AlwaysPlusRole_NoRestriction");
            yield return new TestCaseData(R("First_Person"), ThirdPerson, true)
                .SetName("DifferentlyCasedRole_IsUnrecognizedCustom_NoRestriction");
            yield return new TestCaseData(R("first_person"), new VisibilityRoleContext(new[] { "First_Person" }, new[] { "first_person" }), false)
                .SetName("AppliedRoles_AreCaseSensitive");
        }

        [TestCaseSource(nameof(RoleTable))]
        public void Hints_FollowTheApplicationsRoleLogic(string[] roles, VisibilityRoleContext context, bool expected)
        {
            Assert.AreEqual(expected, VisibilityHintEvaluator.HintAllows(roles, context));
        }

        [Test]
        public void RoleContext_StandardRolesWithoutLogicApplyEverywhere()
        {
            var thirdOnly = new VisibilityRoleContext(new[] { "third_person" }, null);
            Assert.IsTrue(thirdOnly.Applies("third_person"));
            Assert.IsTrue(thirdOnly.Applies("first_person"), "no logic for first_person: it applies in every view");
            Assert.IsTrue(thirdOnly.Applies("always"));
            Assert.IsFalse(thirdOnly.Applies("ACME_mirror_only"));
            Assert.IsFalse(thirdOnly.IsRecognized("ACME_mirror_only"));
            Assert.IsTrue(thirdOnly.HasLogicFor("third_person"), "an applied role counts as having logic");
            Assert.IsFalse(thirdOnly.HasLogicFor("first_person"));

            Assert.IsFalse(ThirdPerson.Applies("first_person"));
            CollectionAssert.AreEqual(new[] { "third_person" }, ThirdPerson.AppliedRoles);
            CollectionAssert.AreEquivalent(new[] { "first_person", "third_person" }, ThirdPerson.RolesWithLogic);
            Assert.IsTrue(VisibilityHintEvaluator.HintAllows(R("first_person"), null), "a null context is no logic");
        }

        [Test]
        public void RoleContext_IgnoresNullEmptyAndDuplicateRoles()
        {
            var context = new VisibilityRoleContext(
                new[] { "first_person", null, "", "first_person", "ACME_mirror_only" },
                new[] { "third_person", "first_person", null });
            CollectionAssert.AreEqual(new[] { "first_person", "ACME_mirror_only" }, context.AppliedRoles);
            CollectionAssert.AreEqual(new[] { "first_person", "ACME_mirror_only", "third_person" }, context.RolesWithLogic);
        }

        [Test]
        public void Controller_DefaultsToNoLogicAndHidesNothing()
        {
            var root = NewGo("root");
            var custom = NewGo("custom");
            custom.transform.SetParent(root.transform, false);
            var third = NewGo("third");
            third.transform.SetParent(root.transform, false);
            var first = NewGo("first");
            first.transform.SetParent(root.transform, false);
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = custom.transform, Roles = R("ACME_mirror_only") },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = third.transform, Roles = R("third_person") },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = first.transform, Roles = R("first_person") },
            });
            var controller = root.AddComponent<ViewContextController>();

            CollectionAssert.IsEmpty(controller.DefaultRoles.AppliedRoles);
            CollectionAssert.IsEmpty(controller.DefaultRoles.RolesWithLogic);
            Assert.IsTrue(controller.ShouldRenderNode(third.transform, true), "no logic: third_person is visible");
            Assert.IsTrue(controller.ShouldRenderNode(first.transform, true), "no logic: first_person is visible");
            Assert.IsTrue(controller.ShouldRenderNode(custom.transform, true), "an unrecognized custom role is ignored");
            Assert.IsTrue(controller.ShouldRenderNode(root.transform, true), "un-hinted content is visible");

            Assert.IsFalse(controller.ShouldRenderNodeForView(third.transform, FirstPerson, true));
            Assert.IsFalse(controller.ShouldRenderNodeForView(first.transform, ThirdPerson, true));
            Assert.IsTrue(controller.ShouldRenderNodeForView(custom.transform, ThirdPerson, true));
            Assert.IsFalse(controller.ShouldRenderNodeForView(custom.transform,
                FirstPersonRolePolicy.ForView(false, null, new[] { "ACME_mirror_only" }), true),
                "a recognized custom role that does not apply hides its content");
            Assert.IsTrue(controller.ShouldRenderNodeForView(custom.transform, Mirror, true));
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
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = body.transform, Roles = R("always") },
            });
            root.AddComponent<PrimitiveVisibilityHintSet>().Bind(new[]
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                    { Mesh = mesh, SubMesh = 0, Roles = R("third_person") },
            });
            var controller = root.AddComponent<ViewContextController>();

            bool originalEnabled = renderer.enabled;
            var originalMesh = body.GetComponent<MeshFilter>().sharedMesh;
            var originalMaterials = renderer.sharedMaterials;

            Assert.IsTrue(controller.ShouldRenderPrimitiveForView(renderer, 0, NoLogic, true));
            Assert.IsFalse(controller.ShouldRenderPrimitiveForView(renderer, 0, FirstPerson, true));
            Assert.IsTrue(controller.ShouldRenderPrimitiveForView(renderer, 0, Mirror, true));

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
                { Node = body.transform, Roles = R("third_person") };
            var primitiveEntry = new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                { Mesh = mesh, SubMesh = 0, Roles = R("first_person") };
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[] { nodeEntry });
            root.AddComponent<PrimitiveVisibilityHintSet>().Bind(new[]
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                    { Mesh = mesh, SubMesh = 0, Roles = R("third_person") },
                primitiveEntry,
            });
            var controller = root.AddComponent<ViewContextController>();

            Assert.IsFalse(controller.ShouldRenderPrimitiveForView(renderer, 0, FirstPerson, true));
            nodeEntry.Roles = R("first_person");
            Assert.IsTrue(controller.ShouldRenderPrimitiveForView(renderer, 0, FirstPerson, true));
            primitiveEntry.Roles = R("third_person");
            Assert.IsFalse(controller.ShouldRenderPrimitiveForView(renderer, 0, FirstPerson, true));
        }

        [Test]
        public void ExplicitViewQueries_DoNotContaminateEachOtherOrDefaultRoles()
        {
            var root = NewGo("root");
            var node = NewGo("node");
            node.transform.SetParent(root.transform, false);
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = node.transform, Roles = R("third_person") },
            });
            var controller = root.AddComponent<ViewContextController>();
            controller.SetDefaultRoles(FirstPerson);

            Assert.IsTrue(controller.ShouldRenderNodeForView(node.transform, ThirdPerson, true));
            Assert.IsFalse(controller.ShouldRenderNodeForView(node.transform, FirstPerson, true));
            Assert.IsTrue(controller.ShouldRenderNodeForView(node.transform, BothStandard, true));
            Assert.IsFalse(controller.ShouldRenderNode(node.transform, true));
            Assert.AreSame(FirstPerson, controller.DefaultRoles,
                "explicit per-view queries do not change the default roles");
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
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = first.transform, Roles = R("first_person") },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = third.transform, Roles = R("third_person") },
            });
            root.AddComponent<PrimitiveVisibilityHintSet>().Bind(new[]
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                    { Mesh = mesh, SubMesh = 0, Roles = R("always") },
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
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderNodeVisualContent(true, false));
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderNodeVisualContent(false, true));
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(true, R("always"), NoLogic, false),
                "KHR_node_visibility always wins");
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(false, R("always"), NoLogic, true),
                "a primitive hint cannot restore what the node hints hide");
            Assert.IsTrue(VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(true, null, FirstPerson, true));
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
                    { Node = body.transform, Roles = R("third_person") },
            });
            root.AddComponent<ViewContextController>();
            var adapter = root.AddComponent<ScopedMaterialVisibilityAdapter>();
            adapter.NoDrawMaterial = noDraw;

            using (adapter.ApplyForView(renderer, FirstPerson, true))
                Assert.AreSame(noDraw, renderer.sharedMaterials[0]);

            Assert.AreSame(authored, renderer.sharedMaterials[0],
                "disposing the per-view scope restores the exact authored material reference");
            using (adapter.ApplyForView(renderer, NoLogic, true))
                Assert.AreSame(authored, renderer.sharedMaterials[0],
                    "an application without role logic hides nothing");
            using (adapter.ApplyForView(renderer, Mirror, true))
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
                    { Mesh = mesh, SubMesh = 1, Roles = R("third_person") },
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
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = body.transform, Roles = R("third_person") },
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
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = thirdNode.transform, Roles = R("third_person") },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = alwaysNode.transform, Roles = R("always") },
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
                    { Node = body.transform, Roles = R("third_person") },
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
                    { Node = body.transform, Roles = R("third_person") },
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
        public void DefaultRoles_ChangeRaisesOnceAndCanReturnToNoLogic()
        {
            var controller = NewGo("root").AddComponent<ViewContextController>();
            var observed = new List<VisibilityRoleContext>();
            controller.OnDefaultRolesChanged += roles => observed.Add(roles);

            controller.SetDefaultRoles(FirstPersonRolePolicy.ForView(true, new[] { "ACME_mirror_only" }));
            controller.SetDefaultRoles(new VisibilityRoleContext(
                new[] { "ACME_mirror_only", "first_person" }, new[] { "third_person" }));
            controller.SetDefaultRoles(null);

            Assert.AreEqual(2, observed.Count, "an equal set of roles raises no event");
            CollectionAssert.AreEquivalent(new[] { "first_person", "ACME_mirror_only" }, observed[0].AppliedRoles);
            Assert.AreSame(VisibilityRoleContext.NoLogic, observed[1]);
            CollectionAssert.IsEmpty(controller.DefaultRoles.RolesWithLogic);
        }

        [Test]
        public void DisplayNames_ListAllRolesWhenNoLabelOrNodeName()
        {
            Assert.AreEqual("first_person, ACME_mirror_only", NodeVisibilityHintSet.GetDisplayName(
                new NodeVisibilityHintSet.NodeVisibilityEntry { Roles = R("first_person", "ACME_mirror_only") }));
            Assert.IsNull(PrimitiveVisibilityHintSet.GetDisplayName(
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry()));
        }

        [Test]
        public void DisplayNames_FollowLabelThenNodeNameThenRole()
        {
            var node = NewGo("HeadNode").transform;
            var unnamed = NewGo(string.Empty).transform;

            Assert.AreEqual("Head", NodeVisibilityHintSet.GetDisplayName(new NodeVisibilityHintSet.NodeVisibilityEntry
                { Node = node, Roles = R("third_person"), Label = "Head" }));
            Assert.AreEqual("HeadNode", NodeVisibilityHintSet.GetDisplayName(new NodeVisibilityHintSet.NodeVisibilityEntry
                { Node = node, Roles = R("third_person"), Label = string.Empty }));
            Assert.AreEqual("third_person", NodeVisibilityHintSet.GetDisplayName(
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = unnamed, Roles = R("third_person") }));
            Assert.AreEqual("first_person", NodeVisibilityHintSet.GetDisplayName(
                new NodeVisibilityHintSet.NodeVisibilityEntry { Roles = R("first_person") }));

            Assert.AreEqual("Mask Shell", PrimitiveVisibilityHintSet.GetDisplayName(
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry { Roles = R("third_person"), Label = "Mask Shell" }));
            Assert.AreEqual("third_person", PrimitiveVisibilityHintSet.GetDisplayName(
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry { Roles = R("third_person"), Label = string.Empty }));
        }
    }
}
