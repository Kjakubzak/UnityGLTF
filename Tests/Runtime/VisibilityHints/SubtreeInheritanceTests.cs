using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Tests
{
    /// <summary>
    /// Tests <see cref="NodeVisibilityHintSet"/> subtree-inheritance resolution: a node hint applies to its whole
    /// subtree, a descendant hint overrides an ancestor, renderers with no applicable hint are left untouched, and
    /// the resolution survives a serialize -> deserialize (Instantiate) rehydrate.
    /// </summary>
    public class SubtreeInheritanceTests
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

        private GameObject NewChild(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go; // owned by the root; destroyed with it
        }

        private static Renderer FindRenderer(GameObject root, string name)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (r.name == name) return r;
            return null;
        }

        [Test]
        public void SubtreeInheritance_DescendantOverridesAncestor()
        {
            // root > A(third_person_only) > B(inherits A) > C(first_person_only, overrides A)
            var root = NewGo("root");
            var a = NewChild(root, "A"); var aR = a.AddComponent<MeshRenderer>();
            var b = NewChild(a, "B"); var bR = b.AddComponent<MeshRenderer>();
            var c = NewChild(b, "C"); var cR = c.AddComponent<MeshRenderer>();

            var set = root.AddComponent<NodeVisibilityHintSet>();
            set.Bind(new List<NodeVisibilityHintSet.NodeVisibilityEntry>
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = a.transform, Role = "third_person_only" },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = c.transform, Role = "first_person_only" },
            });

            var view = root.GetComponent<ViewContextController>();
            Assert.IsNotNull(view, "Bind must add a ViewContextController to the scene root");

            // Default ThirdPerson.
            Assert.IsTrue(aR.enabled, "A (third_person_only) is visible in third-person");
            Assert.IsTrue(bR.enabled, "B inherits A's third_person_only -> visible in third-person");
            Assert.IsFalse(cR.enabled, "C (first_person_only override) is hidden in third-person");

            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.IsFalse(aR.enabled, "A hidden in first-person");
            Assert.IsFalse(bR.enabled, "B inherits A -> hidden in first-person");
            Assert.IsTrue(cR.enabled, "C (first_person_only) visible in first-person");
        }

        [Test]
        public void NonHintedRenderer_LeftUntouched()
        {
            // D is a sibling with no hint on itself or any ancestor -> never registered, always default-enabled.
            var root = NewGo("root");
            var a = NewChild(root, "A"); a.AddComponent<MeshRenderer>();
            var d = NewChild(root, "D"); var dR = d.AddComponent<MeshRenderer>();

            root.AddComponent<NodeVisibilityHintSet>().Bind(new List<NodeVisibilityHintSet.NodeVisibilityEntry>
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = a.transform, Role = "third_person_only" },
            });

            var view = root.GetComponent<ViewContextController>();
            Assert.IsTrue(dR.enabled, "a renderer with no applicable hint stays enabled");
            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.IsTrue(dR.enabled, "a non-hinted renderer is unaffected by mode changes");
        }

        [Test]
        public void Rehydrate_Instantiate_ReappliesInheritance()
        {
            // A baked prefab (serialized entries) must re-resolve on the clone's Awake, without a fresh import.
            var root = NewGo("root");
            var a = NewChild(root, "A"); a.AddComponent<MeshRenderer>();
            var b = NewChild(a, "B"); b.AddComponent<MeshRenderer>();
            var c = NewChild(b, "C"); c.AddComponent<MeshRenderer>();

            root.AddComponent<NodeVisibilityHintSet>().Bind(new List<NodeVisibilityHintSet.NodeVisibilityEntry>
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = a.transform, Role = "third_person_only" },
                new NodeVisibilityHintSet.NodeVisibilityEntry { Node = c.transform, Role = "first_person_only" },
            });

            // Instantiate reproduces serialize -> deserialize: entries are deep-copied (intra-hierarchy Transform
            // refs remap to the clone) and _resolved resets, so the clone's Awake re-resolves.
            var clone = Object.Instantiate(root);
            _created.Add(clone);

            var cloneView = clone.GetComponent<ViewContextController>();
            Assert.IsNotNull(cloneView, "the clone rehydrates a ViewContextController on Awake");
            var aR = FindRenderer(clone, "A");
            var bR = FindRenderer(clone, "B");
            var cR = FindRenderer(clone, "C");

            Assert.IsTrue(aR.enabled);
            Assert.IsTrue(bR.enabled);
            Assert.IsFalse(cR.enabled);

            cloneView.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.IsFalse(aR.enabled);
            Assert.IsFalse(bR.enabled);
            Assert.IsTrue(cR.enabled);
        }
    }
}
