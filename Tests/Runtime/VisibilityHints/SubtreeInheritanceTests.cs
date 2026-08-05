using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Tests
{
    public class SubtreeInheritanceTests
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

        private static GameObject NewChild(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static Transform FindTransform(GameObject root, string name)
        {
            foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
                if (candidate.name == name) return candidate;
            return null;
        }

        [Test]
        public void NearestDescendantHintReplacesInheritedHint()
        {
            var root = NewGo("root");
            var a = NewChild(root, "A");
            var b = NewChild(a, "B");
            var c = NewChild(b, "C");
            var set = root.AddComponent<NodeVisibilityHintSet>();
            set.Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = a.transform, Role = "third_person" },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = c.transform, Role = "first_person" },
            });

            Assert.AreEqual("third_person", set.ResolveRole(a.transform));
            Assert.AreEqual("third_person", set.ResolveRole(b.transform));
            Assert.AreEqual("first_person", set.ResolveRole(c.transform));
            Assert.IsFalse(set.ShouldRenderVisualContent(a.transform, "first_person", true));
            Assert.IsFalse(set.ShouldRenderVisualContent(b.transform, "first_person", true));
            Assert.IsTrue(set.ShouldRenderVisualContent(c.transform, "first_person", true));
        }

        [Test]
        public void ExplicitAlwaysHintOverridesInheritedRole()
        {
            var root = NewGo("root");
            var parent = NewChild(root, "parent");
            var child = NewChild(parent, "child");
            var set = root.AddComponent<NodeVisibilityHintSet>();
            set.Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = parent.transform, Role = "third_person" },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = child.transform, Role = "always" },
            });

            Assert.IsFalse(set.ShouldRenderVisualContent(parent.transform, "first_person", true));
            Assert.IsTrue(set.ShouldRenderVisualContent(child.transform, "first_person", true));
        }

        [Test]
        public void DuplicateNodeEntriesUseLastAuthoredValueLikeExport()
        {
            var root = NewGo("root");
            var node = NewChild(root, "node");
            var set = root.AddComponent<NodeVisibilityHintSet>();
            set.Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry
                    { Node = node.transform, Role = "third_person" },
                new NodeVisibilityHintSet.NodeVisibilityEntry
                    { Node = node.transform, Role = "first_person" },
            });

            Assert.AreEqual("first_person", set.ResolveRole(node.transform));
            Assert.IsTrue(set.ShouldRenderVisualContent(node.transform, "first_person", true));
            Assert.IsFalse(set.ShouldRenderVisualContent(node.transform, "third_person", true));
        }

        [Test]
        public void UnannotatedNodeUsesVisibleFallback()
        {
            var root = NewGo("root");
            var annotated = NewChild(root, "annotated");
            var plain = NewChild(root, "plain");
            var set = root.AddComponent<NodeVisibilityHintSet>();
            set.Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry
                    { Node = annotated.transform, Role = "third_person" },
            });

            Assert.IsNull(set.ResolveRole(plain.transform));
            Assert.IsTrue(set.ShouldRenderVisualContent(plain.transform, "first_person", true));
        }

        [Test]
        public void InstantiatePreservesAnnotationsWithoutResolvingPersistentState()
        {
            var root = NewGo("root");
            var parent = NewChild(root, "parent");
            var child = NewChild(parent, "child");
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = parent.transform, Role = "third_person" },
            });

            var clone = Object.Instantiate(root);
            _created.Add(clone);
            var cloneSet = clone.GetComponent<NodeVisibilityHintSet>();
            var cloneChild = FindTransform(clone, "child");

            Assert.IsNotNull(cloneSet);
            Assert.AreEqual("third_person", cloneSet.ResolveRole(cloneChild));
            Assert.IsFalse(cloneSet.ShouldRenderVisualContent(cloneChild, "first_person", true));
            Assert.IsTrue(cloneSet.ShouldRenderVisualContent(cloneChild, null, true));
            Assert.AreSame(child.transform, FindTransform(root, "child"));
        }
    }
}
