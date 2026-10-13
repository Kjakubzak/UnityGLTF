using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;
using UnityGLTF.Extensions;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Synchronous tests for the CameraHintSet initial camera pose (authored rotation path rule, host -Z camera axis,
    /// scene-root placement), subject point, roles and display names, plus ViewModeController visibility toggling.
    /// </summary>
    public class CameraHintViewTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        private GameObject NewGo(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        private static readonly Quaternion CameraYaw = Quaternion.Euler(0f, 180f, 0f);

        private static void AssertRotation(Quaternion expected, Quaternion actual, string message = null)
            => Assert.Less(Quaternion.Angle(expected, actual), 0.01f, message);

        [Test]
        public void CameraHint_TargetDoesNotChangeRotation_AndIsTheSubjectPoint()
        {
            var node = NewGo("node");
            node.transform.SetPositionAndRotation(new Vector3(0f, 1f, 0f), Quaternion.Euler(0f, 30f, 0f));
            var target = NewGo("target");
            target.transform.position = new Vector3(0f, 1f, 5f);
            var cam = NewGo("cam").AddComponent<Camera>();
            var chs = NewGo("chs").AddComponent<CameraHintSet>();
            var hint = new CameraHint { Role = "framing_view", Node = node.transform, Target = target.transform };

            chs.Apply(hint, cam);

            Assert.Less(Vector3.Distance(cam.transform.position, node.transform.position), 1e-4f);
            AssertRotation(node.transform.rotation * CameraYaw, cam.transform.rotation,
                "the target never aims the camera; orientation comes from R");
            Assert.IsTrue(CameraHintSet.TryGetSubjectPoint(hint, out var subject));
            Assert.Less(Vector3.Distance(target.transform.position, subject), 1e-5f);

            Assert.IsFalse(CameraHintSet.TryGetSubjectPoint(
                new CameraHint { Role = "detail", Node = node.transform }, out _), "no target, no subject point");
            Assert.IsFalse(CameraHintSet.TryGetSubjectPoint(
                new CameraHint { Role = "detail", Node = node.transform, Target = node.transform }, out _),
                "a target naming the hint's own node is not usable");
        }

        [Test]
        public void AuthoredRotation_ZeroScaleComponentTakesTheSpecSign()
        {
            var chs = NewGo("chs").AddComponent<CameraHintSet>();
            var flat = NewGo("flat");
            flat.transform.localScale = new Vector3(-1f, 1f, 0f);
            var squashed = NewGo("squashed");
            squashed.transform.localScale = new Vector3(-1f, 1f, 0.5f);

            var flatRotation = chs.ComputeAuthoredRotation(new CameraHint { Node = flat.transform }, null);
            AssertRotation(Quaternion.Euler(0f, 180f, 0f), flatRotation, "scale (-1, 1, 0) gives R = diag(-1, 1, -1)");
            Assert.Greater(Quaternion.Angle(flat.transform.rotation, flatRotation), 1f,
                "the transform's rotation alone does not carry the scale signs");
            AssertRotation(Quaternion.identity,
                chs.ComputeAuthoredRotation(new CameraHint { Node = squashed.transform }, null),
                "scale (-1, 1, 0.5) is a reflection, which R removes");
        }

        [Test]
        public void AuthoredRotation_NegativeAncestorScaleKeepsLookAndUpDirections()
        {
            var scene = NewGo("scene");
            var chs = scene.AddComponent<CameraHintSet>();
            var mirrored = NewGo("mirrored");
            mirrored.transform.SetParent(scene.transform, false);
            mirrored.transform.localScale = new Vector3(-1f, 1f, 1f);
            var node = NewGo("node");
            node.transform.SetParent(mirrored.transform, false);
            node.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            var hint = new CameraHint { Role = "detail", Node = node.transform };

            var r = chs.ComputeAuthoredRotation(hint, scene.transform);

            AssertRotation(Quaternion.Euler(0f, 0f, -90f), r,
                "the reflection is removed by negating the first column, keeping the look and up directions");
            var cam = NewGo("cam").AddComponent<Camera>();
            chs.Apply(hint, cam);
            AssertRotation(r * CameraYaw, cam.transform.rotation);
        }

        [Test]
        public void AuthoredRotation_MatrixNodeMatchesEquivalentTrsNode()
        {
            var scene = NewGo("scene");
            var matrixObject = NewGo("matrixNode");
            matrixObject.transform.SetParent(scene.transform, false);
            var trsObject = NewGo("trsNode");
            trsObject.transform.SetParent(scene.transform, false);

            var rotation = Quaternion.Euler(20f, 30f, 0f);
            var scale = new Vector3(-1f, 1f, 0f);
            var position = new Vector3(1f, 2f, 3f);
            trsObject.transform.localPosition = position;
            trsObject.transform.localRotation = rotation;
            trsObject.transform.localScale = scale;
            // Decomposing a matrix with a zero column loses the scale sign; only the authored columns keep it.
            matrixObject.transform.localPosition = position;
            matrixObject.transform.localScale = new Vector3(1f, 1f, 0f);

            var root = new GLTFRoot();
            var matrixNode = new Node
            {
                Matrix = Matrix4x4.TRS(position, rotation, scale).ToGltfMatrix4x4Convert(),
                HasMatrix = true,
            };
            matrixNode.AddExtension(KHR_node_camera_hint.EXTENSION_NAME, new KHR_node_camera_hint { Role = "detail" });
            var trsNode = new Node { UseTRS = true };
            trsNode.AddExtension(KHR_node_camera_hint.EXTENSION_NAME, new KHR_node_camera_hint { Role = "portrait" });

            var context = new KhrCharacterImportContext(null);
            context.OnAfterImportRoot(root);
            context.OnAfterImportNode(matrixNode, 0, matrixObject);
            context.OnAfterImportNode(trsNode, 1, trsObject);
            context.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<CameraHintSet>();
            Assert.AreEqual(1, set.AuthoredMatrices.Count);
            Assert.IsTrue(set.TryGetByRole("detail", out var matrixHint));
            Assert.IsTrue(set.TryGetByRole("portrait", out var trsHint));
            var fromMatrix = set.ComputeAuthoredRotation(matrixHint, scene.transform);
            var fromTrs = set.ComputeAuthoredRotation(trsHint, scene.transform);

            AssertRotation(fromTrs, fromMatrix);
            AssertRotation(rotation * Quaternion.Euler(0f, 180f, 0f), fromTrs);
        }

        [Test]
        public void Apply_ComposesAuthoredRotationWithTheSceneRootPlacement()
        {
            var scene = NewGo("scene");
            scene.transform.SetPositionAndRotation(new Vector3(4f, 0f, -2f), Quaternion.Euler(0f, 90f, 0f));
            var chs = scene.AddComponent<CameraHintSet>();
            var parent = NewGo("parent");
            parent.transform.SetParent(scene.transform, false);
            parent.transform.localRotation = Quaternion.Euler(0f, 20f, 0f);
            var node = NewGo("node");
            node.transform.SetParent(parent.transform, false);
            node.transform.localPosition = new Vector3(0f, 1f, 2f);
            node.transform.localRotation = Quaternion.Euler(10f, 0f, 0f);
            var hint = new CameraHint { Role = "default_view", Node = node.transform };
            var cam = NewGo("cam").AddComponent<Camera>();

            chs.Apply(hint, cam);

            var r = chs.ComputeAuthoredRotation(hint, scene.transform);
            AssertRotation(Quaternion.Euler(0f, 20f, 0f) * Quaternion.Euler(10f, 0f, 0f), r,
                "R covers the glTF node path below the scene root");
            AssertRotation(scene.transform.rotation * r * CameraYaw, cam.transform.rotation);
            Assert.Less(Vector3.Distance(node.transform.position, cam.transform.position), 1e-4f);
        }

        [Test]
        public void CameraHintRoles_AreExactAndCaseSensitive()
        {
            foreach (var role in new[]
                     {
                         "default_view", "detail", "first_person", "framing_view", "full_body", "orbit_view",
                         "portrait", "third_person",
                     })
            {
                Assert.IsTrue(CameraHintRoles.IsStandard(role), role);
                CollectionAssert.Contains(CameraHintRoles.Standard, role);
            }
            Assert.AreEqual(8, CameraHintRoles.Standard.Count);
            foreach (var role in new[] { "Default_View", "framing_target", "orbit_center", "orbit", "", null })
                Assert.IsFalse(CameraHintRoles.IsStandard(role), role ?? "null");
        }

        [Test]
        public void DisplayName_UsesLabelThenNodeNameThenRole()
        {
            var chs = NewGo("chs").AddComponent<CameraHintSet>();
            var named = NewGo("Empty_07").transform;
            var unnamed = NewGo(string.Empty).transform;
            string Localize(string role) => role == "portrait" ? "Bildnis" : null;

            Assert.AreEqual("Entrance view", chs.GetDisplayName(
                new CameraHint { Role = "portrait", Label = "Entrance view", Node = named }));
            Assert.AreEqual("Empty_07", chs.GetDisplayName(new CameraHint { Role = "portrait", Node = named }));
            Assert.AreEqual("portrait", chs.GetDisplayName(new CameraHint { Role = "portrait", Node = unnamed }));
            Assert.AreEqual("Bildnis", chs.GetDisplayName(
                new CameraHint { Role = "portrait", Node = unnamed }, Localize));
            Assert.AreEqual("detail", chs.GetDisplayName(
                new CameraHint { Role = "detail", Node = unnamed }, Localize), "no provided name keeps the role");
            Assert.AreEqual("ACME_inspection_view", chs.GetDisplayName(
                new CameraHint { Role = "ACME_inspection_view", Node = unnamed }, _ => "Inspection"),
                "display names are provided only for recognized roles");
            chs.RegisteredCustomRoles.Add("ACME_inspection_view");
            Assert.AreEqual("Inspection", chs.GetDisplayName(
                new CameraHint { Role = "ACME_inspection_view", Node = unnamed }, _ => "Inspection"));
        }

        [Test]
        public void CameraHint_NoTarget_AppliesInvertDirection()
        {
            var node = NewGo("node");
            node.transform.rotation = Quaternion.Euler(0f, 30f, 0f);
            var cam = NewGo("cam").AddComponent<Camera>();
            var chs = NewGo("chs").AddComponent<CameraHintSet>();

            chs.Apply(new CameraHint { Role = "main", Node = node.transform }, cam);

            var expected = node.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
            Assert.Less(Quaternion.Angle(cam.transform.rotation, expected), 0.1f);
        }

        [Test]
        public void CameraHint_CoreCameraNode_DoesNotDoubleFlipAuthoredRotation()
        {
            var node = NewGo("node");
            node.transform.rotation = Quaternion.Euler(5f, 25f, 10f);
            var cam = NewGo("cam").AddComponent<Camera>();
            var hints = NewGo("hints").AddComponent<CameraHintSet>();

            hints.Apply(new CameraHint
            {
                Role = "default_view",
                Node = node.transform,
                NodeTransformHasForwardAxisConversion = true,
            }, cam);

            Assert.Less(Quaternion.Angle(cam.transform.rotation, node.transform.rotation), 0.1f);
        }

        [Test]
        public void CameraHint_ProjectionAdapter_CopiesOrthographicSize()
        {
            var node = NewGo("node");
            var source = NewGo("projection").AddComponent<Camera>();
            source.orthographic = true;
            source.orthographicSize = 7.25f;
            var destination = NewGo("destination").AddComponent<Camera>();
            var hints = NewGo("hints").AddComponent<CameraHintSet>();

            hints.Apply(new CameraHint { Role = "detail", Node = node.transform, Projection = source }, destination);

            Assert.IsTrue(destination.orthographic);
            Assert.AreEqual(7.25f, destination.orthographicSize, 1e-5f);
        }

        [Test]
        public void ViewMode_TogglesRendererVisibility()
        {
            var view = NewGo("char").AddComponent<ViewModeController>();
            var third = NewGo("third").AddComponent<MeshRenderer>();
            var first = NewGo("first").AddComponent<MeshRenderer>();
            var both = NewGo("both").AddComponent<MeshRenderer>();

            view.RegisterRenderer(third, ViewModeController.RenderView.ThirdPersonOnly);
            view.RegisterRenderer(first, ViewModeController.RenderView.FirstPersonOnly);
            view.RegisterRenderer(both, ViewModeController.RenderView.Both);

            Assert.IsTrue(third.enabled);
            Assert.IsFalse(first.enabled);
            Assert.IsTrue(both.enabled);

            view.Mode = ViewModeController.ViewMode.FirstPerson;
            Assert.IsFalse(third.enabled);
            Assert.IsTrue(first.enabled);
            Assert.IsTrue(both.enabled);
        }
    }
}
