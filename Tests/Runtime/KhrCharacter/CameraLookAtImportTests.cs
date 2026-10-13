using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GLTF.Schema;
using GLTF.Schema.KHR_lights_punctual;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>Import-contract tests for the two standalone passive node annotations.</summary>
    public class CameraLookAtImportTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var value in _created)
                if (value != null) Object.DestroyImmediate(value);
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
            var child = new GameObject(name);
            child.transform.SetParent(parent.transform, false);
            return child;
        }

        [Test]
        public void StandaloneAnnotations_ImportWithoutCharacterOrBehavior()
        {
            var scene = NewGo("scene");
            var cameraNodeObject = NewChild(scene, "cameraHint");
            var targetNodeObject = NewChild(scene, "target");

            var root = new GLTFRoot
            {
                ExtensionsRequired = new List<string>
                {
                    KHR_node_lookat_target.EXTENSION_NAME,
                    "ACME_marker_payload",
                },
            };
            var cameraNode = new Node();
            cameraNode.AddExtension(KHR_node_camera_hint.EXTENSION_NAME,
                new KHR_node_camera_hint
                {
                    Role = "portrait",
                    Label = "Hero",
                    TargetNode = 1,
                    Extras = JObject.Parse("{\"source\":\"author\"}"),
                    AdditionalProperties = JObject.Parse("{\"futureField\":7}"),
                });
            var targetNode = new Node();
            targetNode.AddExtension(KHR_node_lookat_target.EXTENSION_NAME,
                new KHR_node_lookat_target
                {
                    Hint = "ACME_eye_target",
                    Extensions = JObject.Parse("{\"ACME_marker_payload\":{\"version\":1}}"),
                    Extras = JObject.Parse("{\"tag\":\"hero\"}"),
                    AdditionalProperties = JObject.Parse("{\"futureMarkerField\":true}"),
                });

            var context = new KhrCharacterImportContext(null);
            context.OnAfterImportRoot(root);
            context.OnAfterImportNode(cameraNode, 0, cameraNodeObject);
            context.OnAfterImportNode(targetNode, 1, targetNodeObject);
            context.OnAfterImportScene(null, 0, scene);

            Assert.IsNull(scene.GetComponent<KhrCharacter>(), "standalone annotations do not create KHR_character");
            Assert.IsNull(scene.GetComponent<GazeSolver>(), "passive markers never create a gaze behavior");
            Assert.IsNull(scene.GetComponent<ViewModeController>(), "standalone annotations do not add character utilities");

            var cameras = scene.GetComponent<CameraHintSet>();
            Assert.IsNotNull(cameras);
            Assert.AreEqual(1, cameras.Hints.Count);
            Assert.AreEqual("portrait", cameras.Hints[0].Role);
            Assert.AreSame(cameraNodeObject.transform, cameras.Hints[0].Node);
            Assert.AreSame(targetNodeObject.transform, cameras.Hints[0].Target);
            StringAssert.Contains("futureField", cameras.Hints[0].AdditionalPropertiesJson);

            var targets = scene.GetComponent<LookAtTargetSet>();
            Assert.IsNotNull(targets);
            Assert.IsTrue(targets.RequiredOnImport);
            Assert.AreEqual(1, targets.Targets.Count);
            Assert.AreSame(targetNodeObject.transform, targets.Targets[0].Node);
            Assert.AreEqual("ACME_eye_target", targets.Targets[0].Hint);
            CollectionAssert.AreEqual(
                new[] { "ACME_marker_payload" }, targets.Targets[0].RequiredCompanionExtensions);
            StringAssert.Contains("futureMarkerField", targets.Targets[0].AdditionalPropertiesJson);
        }

        private static Node CameraHintNode(string role, int? targetNode = null, int? camera = null)
        {
            var node = new Node();
            node.AddExtension(KHR_node_camera_hint.EXTENSION_NAME,
                new KHR_node_camera_hint { Role = role, TargetNode = targetNode, Camera = camera });
            return node;
        }

        [Test]
        public void DefaultView_LowestNodeIndexWinsRegardlessOfTraversalOrder()
        {
            var scene = NewGo("scene");
            var late = NewChild(scene, "late");
            var early = NewChild(scene, "early");
            var cased = NewChild(scene, "cased");
            var context = new KhrCharacterImportContext(null);
            context.OnAfterImportRoot(new GLTFRoot());
            context.OnAfterImportNode(CameraHintNode("default_view"), 5, late);
            context.OnAfterImportNode(CameraHintNode("default_view"), 2, early);
            context.OnAfterImportNode(CameraHintNode("Default_View"), 1, cased);
            context.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<CameraHintSet>();
            Assert.AreEqual(5, set.Hints[0].NodeIndex, "hints keep import traversal order");
            Assert.IsTrue(set.TrySelectDefaultView(out var selected));
            Assert.AreSame(early.transform, selected.Node);
            Assert.AreEqual(2, selected.NodeIndex);
        }

        [Test]
        public void UnrecognizedCustomRoles_AreImportedButNotPresented()
        {
            var scene = NewGo("scene");
            var portrait = NewChild(scene, "portrait");
            var inspection = NewChild(scene, "inspection");
            var legacy = NewChild(scene, "legacy");
            var context = new KhrCharacterImportContext(null);
            context.OnAfterImportRoot(new GLTFRoot());
            context.OnAfterImportNode(CameraHintNode("portrait"), 0, portrait);
            context.OnAfterImportNode(CameraHintNode("ACME_inspection_view"), 1, inspection);
            context.OnAfterImportNode(CameraHintNode("framing_target"), 2, legacy);
            context.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<CameraHintSet>();
            Assert.AreEqual(3, set.Hints.Count, "every hint is imported");
            CollectionAssert.AreEqual(new[] { "portrait" }, RolesOf(set.GetPresentableHints()));
            Assert.IsFalse(set.IsRecognizedRole("framing_target"), "a former draft role is a custom role");

            set.RegisteredCustomRoles.Add("ACME_inspection_view");
            CollectionAssert.AreEqual(
                new[] { "portrait", "ACME_inspection_view" }, RolesOf(set.GetPresentableHints()));
        }

        [UnityTest]
        public IEnumerator UnnamedHintNodes_ArePresentedByTheirRoleAfterImport()
        {
            const string json = @"{
                ""asset"": { ""version"": ""2.0"" },
                ""extensionsUsed"": [ ""KHR_node_camera_hint"" ],
                ""scene"": 0,
                ""scenes"": [ { ""nodes"": [ 0, 1, 2 ] } ],
                ""nodes"": [
                    { ""extensions"": { ""KHR_node_camera_hint"": { ""role"": ""portrait"" } } },
                    { ""name"": """", ""extensions"": { ""KHR_node_camera_hint"": { ""role"": ""ACME_inspection_view"" } } },
                    { ""name"": ""Entrance"", ""extensions"": { ""KHR_node_camera_hint"": { ""role"": ""detail"" } } }
                ]
            }";
            var settings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in settings.ImportPlugins)
                if (plugin is KhrCharacterImportPlugin) plugin.Enabled = true;
            var options = new ImportOptions
            {
                AnimationMethod = AnimationMethod.None,
                ThrowOnLowMemory = false,
                ImportContext = new GLTFImportContext(settings),
            };
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var importer = new GLTFSceneImporter(stream, options);
            var load = importer.LoadSceneAsync();
            while (!load.IsCompleted) yield return null;
            Assert.IsFalse(load.IsFaulted, load.Exception?.Flatten().ToString());
            _created.Add(importer.LastLoadedScene);

            var set = importer.LastLoadedScene.GetComponent<CameraHintSet>();
            Assert.IsNotNull(set);
            string Localize(string role) => role == "portrait" ? "Bildnis" : null;
            Assert.IsTrue(set.TryGetByRole("portrait", out var unnamed));
            Assert.AreEqual("GLTFNode0", unnamed.Node.name, "UnityGLTF names the GameObject of an unnamed node");
            Assert.IsTrue(unnamed.NodeUnnamed);
            Assert.AreEqual("portrait", set.GetDisplayName(unnamed));
            Assert.AreEqual("Bildnis", set.GetDisplayName(unnamed, Localize));
            Assert.IsTrue(set.TryGetByRole("ACME_inspection_view", out var emptyName));
            Assert.AreEqual("ACME_inspection_view", set.GetDisplayName(emptyName), "an empty name counts as absent");
            Assert.IsTrue(set.TryGetByRole("detail", out var named));
            Assert.IsFalse(named.NodeUnnamed);
            Assert.AreEqual("Entrance", set.GetDisplayName(named, Localize));

            importer.Dispose();
            stream.Dispose();
        }

        [Test]
        public void ProjectionComesOnlyFromTheHintCamera()
        {
            var scene = NewGo("scene");
            var cameraNodeObject = NewChild(scene, "coreCamera");
            var coreCamera = cameraNodeObject.AddComponent<Camera>();
            var referencing = NewChild(scene, "referencing");
            var root = new GLTFRoot();
            var cameraNode = CameraHintNode("detail");
            cameraNode.Camera = new CameraId { Id = 0, Root = root };

            var context = new KhrCharacterImportContext(null);
            context.OnAfterImportRoot(root);
            context.OnAfterImportNode(cameraNode, 0, cameraNodeObject);
            context.OnAfterImportNode(CameraHintNode("portrait", camera: 0), 1, referencing);
            context.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<CameraHintSet>();
            Assert.IsTrue(set.TryGetByRole("detail", out var withoutCamera));
            Assert.IsNull(withoutCamera.Projection, "node.camera on the hint node does not supply projection data");
            Assert.IsTrue(set.TryGetByRole("portrait", out var withCamera));
            Assert.AreSame(coreCamera, withCamera.Projection);
        }

        [Test]
        public void TargetNode_IsKeptOnlyWhenItNamesAnotherResolvedNode()
        {
            var scene = NewGo("scene");
            var self = NewChild(scene, "self");
            var dangling = NewChild(scene, "dangling");
            var context = new KhrCharacterImportContext(null);
            context.OnAfterImportRoot(new GLTFRoot());
            context.OnAfterImportNode(CameraHintNode("orbit_view", targetNode: 3), 3, self);
            context.OnAfterImportNode(CameraHintNode("framing_view", targetNode: 40), 4, dangling);
            context.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<CameraHintSet>();
            Assert.IsTrue(set.TryGetByRole("orbit_view", out var selfTargeted));
            Assert.AreEqual(3, selfTargeted.NodeIndex);
            Assert.IsNull(selfTargeted.Target, "a hint's own node is not a usable target");
            Assert.IsFalse(CameraHintSet.TryGetSubjectPoint(selfTargeted, out _));
            Assert.IsTrue(set.TryGetByRole("framing_view", out var unresolved));
            Assert.IsNull(unresolved.Target);
        }

        [Test]
        public void LookAtHints_AreExactAndSharedHintsAreAllReturned()
        {
            var scene = NewGo("scene");
            var left = NewChild(scene, "left");
            var right = NewChild(scene, "right");
            var lower = NewChild(scene, "lower");
            Node Marker(string hint)
            {
                var node = new Node();
                node.AddExtension(KHR_node_lookat_target.EXTENSION_NAME, new KHR_node_lookat_target { Hint = hint });
                return node;
            }

            var context = new KhrCharacterImportContext(null);
            context.OnAfterImportRoot(new GLTFRoot());
            context.OnAfterImportNode(Marker("ACME_eye_target"), 0, left);
            context.OnAfterImportNode(Marker("ACME_eye_target"), 1, right);
            context.OnAfterImportNode(Marker("acme_eye_target"), 2, lower);
            context.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<LookAtTargetSet>();
            var shared = set.GetByHint("ACME_eye_target");
            Assert.AreEqual(2, shared.Count);
            Assert.AreSame(left.transform, shared[0].Node);
            Assert.AreSame(right.transform, shared[1].Node);
            Assert.IsTrue(set.TryGetByHint("acme_eye_target", out var other));
            Assert.AreSame(lower.transform, other.Node, "hints are compared exactly");
            Assert.AreEqual(0, set.GetByHint("Acme_Eye_Target").Count);
        }

        private static string[] RolesOf(IReadOnlyList<CameraHint> hints)
        {
            var roles = new string[hints.Count];
            for (int i = 0; i < roles.Length; i++) roles[i] = hints[i].Role;
            return roles;
        }

        [Test]
        public void RequiredSupport_AcceptsPassiveLookAtAndAlwaysRejectsCameraHint()
        {
            GLTFProperty.TryRegisterExtension(new KHR_node_camera_hint_Factory());
            GLTFProperty.TryRegisterExtension(new KHR_node_lookat_target_Factory());

            var cameraFactory = GLTFProperty.TryGetExtension(KHR_node_camera_hint.EXTENSION_NAME);
            var lookAtFactory = GLTFProperty.TryGetExtension(KHR_node_lookat_target.EXTENSION_NAME);
            Assert.IsTrue(cameraFactory.RequiresRuntimeSupportForRequiredUse);
            Assert.IsTrue(lookAtFactory.RequiresRuntimeSupportForRequiredUse);

            var context = new KhrCharacterImportContext(null);
            var lookAtRequired = new GLTFRoot
            {
                ExtensionsRequired = new List<string> { KHR_node_lookat_target.EXTENSION_NAME },
            };
            Assert.Throws<GLTFLoadException>(() => GLTFSceneImporter.ValidateRequiredExtensionSupport(
                lookAtRequired, new List<GLTFImportPluginContext>()));
            Assert.DoesNotThrow(() => GLTFSceneImporter.ValidateRequiredExtensionSupport(
                lookAtRequired, new List<GLTFImportPluginContext> { context }));

            var cameraRequired = new GLTFRoot
            {
                ExtensionsRequired = new List<string> { KHR_node_camera_hint.EXTENSION_NAME },
            };
            Assert.Throws<GLTFLoadException>(() => GLTFSceneImporter.ValidateRequiredExtensionSupport(
                cameraRequired, new List<GLTFImportPluginContext> { context }));
            Assert.IsFalse(context.SupportsRequiredExtension(KHR_node_camera_hint.EXTENSION_NAME));
            Assert.IsTrue(context.SupportsRequiredExtension(KHR_node_lookat_target.EXTENSION_NAME));
        }

        [Test]
        public void LookAtPoint_TracksEvaluatedParentTransform()
        {
            var parent = NewGo("parent");
            var child = NewChild(parent, "marker");
            child.transform.localPosition = new Vector3(1f, 2f, 3f);
            var marker = new LookAtTarget { Node = child.transform, Hint = "dynamic" };

            Assert.IsTrue(LookAtTargetSet.TryGetTargetPoint(marker, out var before));
            parent.transform.SetPositionAndRotation(new Vector3(5f, 0f, -2f), Quaternion.Euler(0f, 90f, 0f));
            Assert.IsTrue(LookAtTargetSet.TryGetTargetPoint(marker, out var after));

            Assert.AreNotEqual(before, after);
            Assert.Less(Vector3.Distance(child.transform.position, after), 1e-5f);
        }

        [Test]
        public void RepeatedCallbacks_DedupeSameTransformAndKeepDistinctInstances()
        {
            var scene = NewGo("scene");
            var first = NewChild(scene, "first");
            var second = NewChild(scene, "second");
            var node = new Node();
            node.AddExtension(KHR_node_lookat_target.EXTENSION_NAME,
                new KHR_node_lookat_target { Hint = "shared" });

            var context = new KhrCharacterImportContext(null);
            context.OnAfterImportRoot(new GLTFRoot());
            context.OnAfterImportNode(node, 4, first);
            context.OnAfterImportNode(node, 4, first);
            context.OnAfterImportNode(node, 4, second);
            context.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<LookAtTargetSet>();
            Assert.AreEqual(2, set.Targets.Count);
            Assert.AreSame(first.transform, set.Targets[0].Node);
            Assert.AreSame(second.transform, set.Targets[1].Node);
        }

        [Test]
        public void GpuInstancing_KeepsAnnotationsOnNodeRatherThanMeshInstances()
        {
            var scene = NewGo("scene");
            var wrapper = NewChild(scene, "wrapper");
            var authoredCollision = NewChild(wrapper, "Instances");
            NewChild(authoredCollision, "AuthoredChild");
            var generatedInstances = NewChild(wrapper, "Instances");
            NewChild(generatedInstances, "Instance 0");
            NewChild(generatedInstances, "Instance 1");

            var node = new Node();
            node.AddExtension(EXT_mesh_gpu_instancing_Factory.EXTENSION_NAME, new EXT_mesh_gpu_instancing());
            node.AddExtension(KHR_node_camera_hint.EXTENSION_NAME,
                new KHR_node_camera_hint { Role = "detail" });
            node.AddExtension(KHR_node_lookat_target.EXTENSION_NAME,
                new KHR_node_lookat_target { Hint = "instance" });

            var context = new KhrCharacterImportContext(null);
            context.OnAfterImportRoot(new GLTFRoot());
            context.OnAfterImportNode(node, 0, wrapper);
            context.OnAfterImportScene(null, 0, scene);

            var lookAt = scene.GetComponent<LookAtTargetSet>();
            Assert.AreEqual(1, lookAt.Targets.Count);
            Assert.AreSame(wrapper.transform, lookAt.Targets[0].Node,
                "EXT_mesh_gpu_instancing transforms the node's mesh, not the node or its extensions");
            Assert.AreSame(wrapper.transform, scene.GetComponent<CameraHintSet>().Hints[0].Node,
                "camera-hint semantics remain attached to the annotated node transform");
        }

        [Test]
        public void CameraHintImport_RecognizesSharedCameraAndLightConversion()
        {
            var scene = NewGo("scene");
            var lightOnly = NewChild(scene, "lightOnly");
            lightOnly.AddComponent<Light>();
            var cameraAndLight = NewChild(scene, "cameraAndLight");
            cameraAndLight.AddComponent<Camera>();
            cameraAndLight.AddComponent<Light>();

            var authoredLightRotation = Quaternion.Euler(5f, 25f, -10f);
            var authoredCombinedRotation = Quaternion.Euler(-8f, 40f, 12f);
            var flip = Quaternion.Euler(0f, 180f, 0f);
            // Core import applies one shared conversion whether the node has a light, camera, or both.
            lightOnly.transform.rotation = authoredLightRotation * flip;
            cameraAndLight.transform.rotation = authoredCombinedRotation * flip;

            var root = new GLTFRoot();
            Node HintNode(string role, bool hasCamera, bool hasLight)
            {
                var node = new Node();
                if (hasCamera) node.Camera = new CameraId { Id = 0, Root = root };
                if (hasLight)
                    node.AddExtension(
                        KHR_lights_punctualExtensionFactory.EXTENSION_NAME,
                        new KHR_LightsPunctualNodeExtension(0, root));
                node.AddExtension(KHR_node_camera_hint.EXTENSION_NAME,
                    new KHR_node_camera_hint { Role = role });
                return node;
            }

            var context = new KhrCharacterImportContext(null);
            context.OnAfterImportRoot(root);
            context.OnAfterImportNode(HintNode("light", hasCamera: false, hasLight: true), 0, lightOnly);
            context.OnAfterImportNode(HintNode("combined", hasCamera: true, hasLight: true), 1, cameraAndLight);
            context.OnAfterImportScene(null, 0, scene);

            var set = scene.GetComponent<CameraHintSet>();
            Assert.IsTrue(set.TryGetByRole("light", out var lightHint));
            Assert.IsTrue(lightHint.NodeTransformHasForwardAxisConversion);
            Assert.IsTrue(set.TryGetByRole("combined", out var combinedHint));
            Assert.IsTrue(combinedHint.NodeTransformHasForwardAxisConversion);

            var lightResult = NewGo("lightResult").AddComponent<Camera>();
            var combinedResult = NewGo("combinedResult").AddComponent<Camera>();
            set.Apply(lightHint, lightResult);
            set.Apply(combinedHint, combinedResult);

            Assert.Less(Quaternion.Angle(lightResult.transform.rotation, authoredLightRotation * flip), 0.1f);
            Assert.Less(Quaternion.Angle(combinedResult.transform.rotation, authoredCombinedRotation * flip), 0.1f);
        }
    }
}
