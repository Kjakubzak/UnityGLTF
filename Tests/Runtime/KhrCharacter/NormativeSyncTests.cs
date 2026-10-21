using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityGLTF.Plugins;
using UnityGLTF.VisibilityHints;

namespace UnityGLTF.KhrCharacter.Tests
{
    public class NormativeSyncTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        private GameObject Object(string name)
        {
            var obj = new GameObject(name);
            _objects.Add(obj);
            return obj;
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var obj in _objects) if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
            _objects.Clear();
        }

        [Test]
        public void CurrentMappingAccumulatesRepeatedInputsIntoFullImmutableNativeVector()
        {
            var set = new ExpressionMappingSet
            {
                SetName = "demo_v1",
                Targets = new[]
                {
                    new MappingTarget { TargetName = "A", Contributions = new[]
                    {
                        new MappingContribution { SourceIndex = 0, Weight = .5, Name = "independent" },
                        new MappingContribution { SourceIndex = 0, Weight = .25, Name = "" },
                        new MappingContribution { SourceIndex = 1, Weight = .75 },
                    } },
                    new MappingTarget { TargetName = "B", Contributions = new[]
                    {
                        new MappingContribution { SourceIndex = 1, Weight = .5 },
                    } },
                },
            };
            var responseSet = Object("responses").AddComponent<ExpressionResponseSet>();
            responseSet.Bind(new ExpressionResponseSetEntry[3], false, forwardMappingSets: new[] { set });
            var result = responseSet.EvaluateExpressionSetMapping("demo_v1",
                new Dictionary<string, double> { { "A", .8 }, { "B", .6 }, { "unknown", 1 } });
            Assert.That(result, Is.EqualTo(new[] { .6, .9, 0d }).Within(1e-12));
            Assert.Throws<NotSupportedException>(() => ((IList<double>)result)[0] = 1d);
            Assert.That(responseSet.EvaluateExpressionSetMapping("demo_v1",
                new Dictionary<string, double> { { "A", 2 }, { "B", 2 } }), Is.EqualTo(new[] { .75, 1d, 0d }));
            Assert.That(responseSet.EvaluateExpressionSetMapping("demo_v1", null), Is.EqualTo(new[] { 0d, 0d, 0d }));
        }

        [Test]
        public void CurrentMappingUsesExactInputKeysEvenWithCaseInsensitiveHostDictionary()
        {
            var set = new ExpressionMappingSet { Targets = new[]
            {
                new MappingTarget { TargetName = "Smile", Contributions = new[]
                { new MappingContribution { SourceIndex = 0, Weight = 1 } } },
            } };
            var inputs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { { "smile", 1 } };
            Assert.That(ExpressionDriverPipeline.EvaluateExpressionSetMapping(set, inputs, 1), Is.EqualTo(new[] { 0d }));
            inputs["unknown"] = double.NaN;
            Assert.Throws<ArgumentOutOfRangeException>(() => ExpressionDriverPipeline.EvaluateExpressionSetMapping(set, inputs, 1));
        }

        [Test]
        public void CurrentMappingCompatibilityApiUsesTheSameFiniteInputContract()
        {
            var set = new ExpressionMappingSet { SetName = "demo_v1", Targets = new[]
            {
                new MappingTarget { TargetName = "Smile", Contributions = new[]
                { new MappingContribution { SourceIndex = 0, Weight = 1 } } },
            } };
            var responses = Object("responses").AddComponent<ExpressionResponseSet>();
            responses.Bind(new ExpressionResponseSetEntry[1], false, forwardMappingSets: new[] { set });
            var supplied = new Dictionary<string, double> { { "unknown", double.NaN } };
            Assert.Throws<ArgumentOutOfRangeException>(() => responses.EvaluateExpressionSetMapping("demo_v1", supplied));
            Assert.Throws<ArgumentOutOfRangeException>(() => responses.EvaluateInputMapping("demo_v1", supplied));
            supplied["unknown"] = .5;
            Assert.That(responses.EvaluateInputMapping("demo_v1", supplied), Is.EqualTo(new[] { 0d }));
        }

        [UnityTest]
        public IEnumerator LoadNodeAsyncOwnFalseFiltersVisualsWithoutDeactivatingHierarchy()
            => AssertNodeLoadCoreVisibility(1);

        [UnityTest]
        public IEnumerator LoadNodeAsyncAncestorFalseFiltersTrueChildrenWithoutDeactivatingHierarchy()
            => AssertNodeLoadCoreVisibility(0);

        private IEnumerator AssertNodeLoadCoreVisibility(int nodeIndex)
        {
            var fixture = new CharacterGlbBuilder();
            int position = fixture.AddFloatAccessor("VEC3", 3,
                new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f });
            fixture.Json["extensionsUsed"] = new JArray("KHR_node_visibility", "KHR_lights_punctual");
            fixture.Json["extensions"] = new JObject { { "KHR_lights_punctual", new JObject
            {
                { "lights", new JArray(new JObject { { "type", "point" } }) },
            } } };
            fixture.Json["meshes"] = new JArray(new JObject { { "primitives", new JArray(new JObject
            {
                { "attributes", new JObject { { "POSITION", position } } },
            }) } });
            fixture.Json["cameras"] = new JArray(new JObject
            {
                { "type", "perspective" },
                { "perspective", new JObject { { "yfov", 1 }, { "znear", .1 } } },
            });
            JObject Visibility(bool value) => new JObject
            {
                { "KHR_node_visibility", new JObject { { "visible", value } } },
                { "KHR_lights_punctual", new JObject { { "light", 0 } } },
            };
            fixture.Json["nodes"] = new JArray(
                new JObject { { "name", "instance" }, { "children", new JArray(1) } },
                new JObject { { "name", "falseParent" }, { "children", new JArray(2) },
                    { "mesh", 0 }, { "camera", 0 }, { "extensions", Visibility(false) } },
                new JObject { { "name", "trueChild" }, { "mesh", 0 }, { "camera", 0 },
                    { "extensions", Visibility(true) } });
            var settings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in settings.ImportPlugins)
                if (plugin is LightsPunctualImport) plugin.Enabled = true;
            var options = new ImportOptions
            {
                AnimationMethod = AnimationMethod.None,
                ThrowOnLowMemory = false,
                ImportContext = new GLTFImportContext(settings),
            };
            using (var stream = new MemoryStream(fixture.ToGlb()))
            using (var importer = new GLTFSceneImporter(stream, options))
            {
                var loading = importer.LoadNodeAsync(nodeIndex, CancellationToken.None);
                while (!loading.IsCompleted) yield return null;
                Assert.IsFalse(loading.IsFaulted, loading.Exception?.Flatten().ToString());
                var root = importer.CreatedObject;
                _objects.Add(root);
                Assert.AreEqual(1, root.GetComponentsInChildren<CoreVisibilityController>(true).Length,
                    "the public node route must attach exactly one default rendering adapter");
                var controller = root.GetComponent<CoreVisibilityController>();
                Assert.IsNotNull(controller);
                var falseNode = importer.NodeCache[1];
                var trueNode = importer.NodeCache[2];
                Assert.IsFalse(falseNode.GetComponent<CoreNodeVisibility>().Visible);
                Assert.IsTrue(trueNode.GetComponent<CoreNodeVisibility>().Visible,
                    "ancestor filtering must not overwrite the child's authored own Boolean");
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                var lights = root.GetComponentsInChildren<Light>(true);
                var cameras = root.GetComponentsInChildren<Camera>(true);
                Assert.AreEqual(2, renderers.Length);
                Assert.AreEqual(2, lights.Length);
                Assert.AreEqual(2, cameras.Length);
                foreach (var renderer in renderers) Assert.IsFalse(renderer.enabled,
                    "filtering must already apply on return from LoadNodeAsync");
                foreach (var light in lights) Assert.IsFalse(light.enabled);
                foreach (var camera in cameras) camera.enabled = true; // Host selects imported cameras.
                controller.ApplyVisibility();
                foreach (var camera in cameras) Assert.IsTrue(camera.enabled);
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    Assert.IsTrue(transform.gameObject.activeInHierarchy,
                        "visibility must preserve camera, animation and interaction hierarchy");
                Assert.AreSame(root, nodeIndex == 1 ? falseNode : falseNode.transform.parent.gameObject);
                Assert.IsNull(root.transform.parent, "the partial load does not instantiate omitted source ancestors");
                falseNode.GetComponent<CoreNodeVisibility>().Visible = true;
                controller.ApplyVisibility();
                foreach (var renderer in renderers) Assert.IsTrue(renderer.enabled);
                foreach (var light in lights) Assert.IsTrue(light.enabled);
                foreach (var camera in cameras) Assert.IsTrue(camera.enabled);
            }
        }

        [Test]
        public void SharedQuaternionOverrideRetainsEveryAdditiveTranslationAndNeutral()
        {
            var move = new ExpressionResponseTarget(ExpressionPropertyIdentity.ForProperty("/nodes/0/translation"),
                3, ExpressionResponseValueKind.Components, true, ExpressionPropertyKind.NodeTranslation, new[] { 0d, 10d, 0d }, 0);
            var rotate = new ExpressionResponseTarget(ExpressionPropertyIdentity.ForProperty("/nodes/0/rotation"),
                4, ExpressionResponseValueKind.Quaternion, true, ExpressionPropertyKind.NodeRotation, new[] { 0d, 0d, 0d, 1d }, 0);
            float half = (float)Math.Sqrt(.5);
            ExpressionResponse Response(float current, float[] rotation) => new ExpressionResponse(1, 1, 1, new[]
            {
                new ExpressionResponseRecord(0, move, new[] { 0f, current, 0f }, new[] { 0f, 2f, 0f }),
                new ExpressionResponseRecord(1, rotate, rotation, new[] { 0f, 0f, 0f, 1f }),
            });
            var composition = ExpressionComposer.Compose(new[]
            {
                Response(3, new[] { half, 0f, 0f, half }),
                Response(5, new[] { 0f, half, 0f, half }),
            });
            Assert.IsTrue(composition.TryGetRecord("/nodes/0/translation", out var translation));
            Assert.That(translation.Value, Is.EqualTo(new[] { 0d, 14d, 0d }));
            Assert.IsTrue(composition.TryGetRecord("/nodes/0/rotation", out var selected));
            Assert.That(selected.Value[0], Is.EqualTo(0));
            double norm = 0;
            foreach (var value in selected.Value) norm += value * value;
            Assert.That(norm, Is.EqualTo(1d).Within(1e-12));
            Assert.That(composition.Responses[0].Records.Count, Is.EqualTo(2));
            Assert.That(composition.Responses[1].Records.Count, Is.EqualTo(2));
            Assert.That(composition.Failures, Is.Empty);
        }

        [TestCase(ExpressionResponseValueKind.Boolean, 0f, 1f)]
        [TestCase(ExpressionResponseValueKind.Integer, 2f, 7f)]
        public void DiscreteSharedSamplesUseAbsoluteOverride(ExpressionResponseValueKind kind, float first, float last)
        {
            var target = new ExpressionResponseTarget(ExpressionPropertyIdentity.ForProperty("/extensions/ACME_value/value"),
                1, kind, true, ExpressionPropertyKind.Other, new[] { 0d }, 0);
            ExpressionResponse Response(float value) => new ExpressionResponse(1, 1, 1,
                new[] { new ExpressionResponseRecord(0, target, new[] { value }, new[] { first }) });
            var composition = ExpressionComposer.Compose(new[] { Response(first), Response(last) });
            Assert.That(composition.Records[0].Value, Is.EqualTo(new[] { (double)last }));
        }

        [TestCase("third_person", "always", "first_person", false)]
        [TestCase("third_person", "always", "third_person", true)]
        [TestCase("third_person", "first_person", "first_person", false)]
        [TestCase("third_person", "first_person", "third_person", false)]
        [TestCase("third_person", "first_person", "first_person,third_person", true)]
        [TestCase("ACME_unknown", "always", "third_person", false)]
        public void AncestorPredicatesConjoinPerView(string parentRole, string childRole, string tags, bool expected)
        {
            var root = Object("root");
            var parent = Object("parent"); parent.transform.SetParent(root.transform, false);
            var child = Object("child"); child.transform.SetParent(parent.transform, false);
            var hints = root.AddComponent<NodeVisibilityHintSet>();
            hints.Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = parent.transform, Role = parentRole },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = child.transform, Role = childRole },
            });
            Assert.That(hints.IsHintVisible(child.transform, tags.Split(',')), Is.EqualTo(expected));
            Assert.AreEqual(childRole, hints.ResolveRole(child.transform), "metadata retains the authored nearest role");
            Assert.IsTrue(child.activeInHierarchy);
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(false, "always", tags.Split(','), true));
            Assert.IsFalse(VisibilityHintEvaluator.ShouldRenderPrimitiveInstance(true, "always", tags.Split(','), false));
        }

        [Test]
        public void CoreVisibilityKeepsOwnValuesAndCameraAliveDuringAnimatedFiltering()
        {
            var root = Object("root");
            var parent = Object("parent"); parent.transform.SetParent(root.transform, false);
            var child = Object("child"); child.transform.SetParent(parent.transform, false);
            var parentState = parent.AddComponent<CoreNodeVisibility>();
            parentState.Bind(new KHR_node_visibility { visible = false, AdditionalProperties = new JObject { { "extras", new JObject { { "origin", "fixture" } } } } });
            var childState = child.AddComponent<CoreNodeVisibility>(); childState.Visible = true;
            var camera = child.AddComponent<Camera>();
            var renderer = child.AddComponent<MeshRenderer>();
            var controller = root.AddComponent<CoreVisibilityController>();
            controller.ApplyVisibility();
            Assert.IsFalse(CoreNodeVisibility.IsVisibleInHierarchy(child.transform, root.transform));
            Assert.IsFalse(renderer.enabled);
            Assert.IsTrue(camera.enabled);
            Assert.IsTrue(child.activeInHierarchy);
            Assert.IsTrue(childState.ExportOwnState().visible, "export preserves own true instead of inherited false");
            Assert.AreEqual("fixture", parentState.ExportOwnState().Serialize().Value["extras"]["origin"].Value<string>());
            var clip = new AnimationClip { legacy = true };
            try
            {
                clip.SetCurve("parent", typeof(CoreNodeVisibility), nameof(CoreNodeVisibility.Visible),
                    AnimationCurve.Constant(0, 1, 1));
                clip.SampleAnimation(root, .5f);
                controller.ApplyVisibility();
                Assert.IsTrue(parentState.Visible);
                Assert.IsTrue(renderer.enabled);
                Assert.IsTrue(child.activeInHierarchy);
            }
            finally { UnityEngine.Object.DestroyImmediate(clip); }
        }
    }
}
