using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Tests
{
    public class SubtreeInheritanceTests
    {
        private static readonly VisibilityRoleContext FirstPerson = FirstPersonRolePolicy.FirstPersonView;
        private static readonly VisibilityRoleContext ThirdPerson = FirstPersonRolePolicy.OtherView;
        private static readonly VisibilityRoleContext NoLogic = VisibilityRoleContext.NoLogic;

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
        public void DescendantHintAddsRestrictionToAllAncestors()
        {
            var root = NewGo("root");
            var a = NewChild(root, "A");
            var b = NewChild(a, "B");
            var c = NewChild(b, "C");
            var set = root.AddComponent<NodeVisibilityHintSet>();
            set.Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = a.transform, Roles = new[] { "third_person" } },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = c.transform, Roles = new[] { "first_person" } },
            });

            CollectionAssert.AreEqual(new[] { "third_person" }, set.ResolveRoles(a.transform));
            CollectionAssert.AreEqual(new[] { "third_person" }, set.ResolveRoles(b.transform));
            CollectionAssert.AreEqual(new[] { "first_person" }, set.ResolveRoles(c.transform));
            Assert.IsFalse(set.ShouldRenderVisualContent(a.transform, FirstPerson, true));
            Assert.IsFalse(set.ShouldRenderVisualContent(b.transform, FirstPerson, true));
            Assert.IsFalse(set.ShouldRenderVisualContent(c.transform, FirstPerson, true));
            Assert.IsFalse(set.ShouldRenderVisualContent(c.transform, ThirdPerson, true));
            Assert.IsTrue(set.ShouldRenderVisualContent(
                c.transform, new VisibilityRoleContext(new[] { "first_person", "third_person" }, null), true));
            Assert.IsTrue(set.ShouldRenderVisualContent(c.transform, NoLogic, true), "no role logic hides nothing");
        }

        [Test]
        public void ExplicitAlwaysCannotCancelAncestorRestriction()
        {
            var root = NewGo("root");
            var parent = NewChild(root, "parent");
            var child = NewChild(parent, "child");
            var set = root.AddComponent<NodeVisibilityHintSet>();
            set.Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = parent.transform, Roles = new[] { "third_person" } },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = child.transform, Roles = new[] { "always" } },
            });

            Assert.IsFalse(set.ShouldRenderVisualContent(parent.transform, FirstPerson, true));
            Assert.IsFalse(set.ShouldRenderVisualContent(child.transform, FirstPerson, true));
            Assert.IsTrue(set.ShouldRenderVisualContent(child.transform, ThirdPerson, true));
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
                    { Node = node.transform, Roles = new[] { "third_person" } },
                new NodeVisibilityHintSet.NodeVisibilityEntry
                    { Node = node.transform, Roles = new[] { "first_person" } },
            });

            CollectionAssert.AreEqual(new[] { "first_person" }, set.ResolveRoles(node.transform));
            Assert.IsTrue(set.ShouldRenderVisualContent(node.transform, FirstPerson, true));
            Assert.IsFalse(set.ShouldRenderVisualContent(node.transform, ThirdPerson, true));
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
                    { Node = annotated.transform, Roles = new[] { "third_person" } },
            });

            Assert.IsNull(set.ResolveRoles(plain.transform));
            Assert.IsTrue(set.ShouldRenderVisualContent(plain.transform, FirstPerson, true));
        }

        [Test]
        public void InstantiatePreservesAnnotationsWithoutResolvingPersistentState()
        {
            var root = NewGo("root");
            var parent = NewChild(root, "parent");
            var child = NewChild(parent, "child");
            root.AddComponent<NodeVisibilityHintSet>().Bind(new[]
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry
                    { Node = parent.transform, NodeUnnamed = true, Roles = new[] { "third_person" } },
            });

            var clone = Object.Instantiate(root);
            _created.Add(clone);
            var cloneSet = clone.GetComponent<NodeVisibilityHintSet>();
            var cloneChild = FindTransform(clone, "child");

            Assert.IsNotNull(cloneSet);
            Assert.AreEqual("third_person", NodeVisibilityHintSet.GetDisplayName(cloneSet.Entries[0]),
                "an unnamed imported node stays unnamed after Instantiate");
            CollectionAssert.AreEqual(new[] { "third_person" }, cloneSet.ResolveRoles(cloneChild));
            Assert.IsFalse(cloneSet.ShouldRenderVisualContent(cloneChild, FirstPerson, true));
            Assert.IsTrue(cloneSet.ShouldRenderVisualContent(cloneChild, NoLogic, true));
            Assert.IsTrue(cloneSet.ShouldRenderVisualContent(cloneChild, ThirdPerson, true));
            Assert.AreSame(child.transform, FindTransform(root, "child"));
        }
    }
}
