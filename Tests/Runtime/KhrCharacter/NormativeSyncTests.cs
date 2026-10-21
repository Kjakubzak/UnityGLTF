using System;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
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
