using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.VisibilityHints.Tests
{
    /// <summary>
    /// Runtime tests for <see cref="ViewContextController"/>: node renderers toggle via <c>enabled</c>, primitive
    /// slots swap the sub-mesh material, role parsing, the change event, and AND-composition with core
    /// <c>KHR_node_visibility</c> (an inactive GameObject).
    /// </summary>
    public class ViewContextControllerTests
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

        private Material NewMaterial(string name)
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader");
            var mat = new Material(shader) { name = name };
            _created.Add(mat);
            return mat;
        }

        [Test]
        public void NodeRenderers_ToggleEnabledWithMode()
        {
            var view = NewGo("char").AddComponent<ViewContextController>();
            var third = NewGo("third").AddComponent<MeshRenderer>();
            var first = NewGo("first").AddComponent<MeshRenderer>();
            var always = NewGo("always").AddComponent<MeshRenderer>();

            view.RegisterRenderer(third, ViewContextController.ViewRole.ThirdPerson);
            view.RegisterRenderer(first, ViewContextController.ViewRole.FirstPerson);
            view.RegisterRenderer(always, ViewContextController.ViewRole.Always);

            // Default mode is ThirdPerson.
            Assert.IsTrue(third.enabled);
            Assert.IsFalse(first.enabled);
            Assert.IsTrue(always.enabled);

            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.IsFalse(third.enabled);
            Assert.IsTrue(first.enabled);
            Assert.IsTrue(always.enabled);
        }

        [Test]
        public void PrimitiveSlot_SwapsSubMeshMaterialWithMode()
        {
            var view = NewGo("char").AddComponent<ViewContextController>();
            var rend = NewGo("body").AddComponent<MeshRenderer>();
            var original = NewMaterial("original");
            var invisible = NewMaterial("invisible");
            rend.sharedMaterials = new[] { original };

            view.RegisterPrimitiveSlot(rend, 0, original, invisible, ViewContextController.ViewRole.ThirdPerson);

            // Default ThirdPerson -> the third-person-only slot is visible (original material).
            Assert.AreSame(original, rend.sharedMaterials[0]);

            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.AreSame(invisible, rend.sharedMaterials[0], "hidden slot swaps to the invisible material");

            view.Mode = ViewContextController.ViewContext.ThirdPerson;
            Assert.AreSame(original, rend.sharedMaterials[0], "switching back restores the original material");
        }

        [Test]
        public void PrimitiveSlot_OtherSubMeshesUntouched()
        {
            // Only the hinted sub-mesh is swapped; sibling sub-mesh materials are left alone.
            var view = NewGo("char").AddComponent<ViewContextController>();
            var rend = NewGo("body").AddComponent<MeshRenderer>();
            var slot0 = NewMaterial("slot0");
            var slot1 = NewMaterial("slot1");
            var invisible = NewMaterial("invisible");
            rend.sharedMaterials = new[] { slot0, slot1 };

            view.RegisterPrimitiveSlot(rend, 1, slot1, invisible, ViewContextController.ViewRole.FirstPerson);

            // ThirdPerson: the first-person-only slot 1 is hidden; slot 0 is never touched.
            Assert.AreSame(slot0, rend.sharedMaterials[0]);
            Assert.AreSame(invisible, rend.sharedMaterials[1]);

            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.AreSame(slot0, rend.sharedMaterials[0], "the non-hinted sub-mesh stays put");
            Assert.AreSame(slot1, rend.sharedMaterials[1]);
        }

        [Test]
        public void Mode_Change_RaisesEventOnce()
        {
            var view = NewGo("char").AddComponent<ViewContextController>();
            int count = 0;
            var seen = ViewContextController.ViewContext.ThirdPerson;
            view.OnViewContextChanged += m => { count++; seen = m; };

            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.AreEqual(1, count);
            Assert.AreEqual(ViewContextController.ViewContext.FirstPerson, seen);

            view.Mode = ViewContextController.ViewContext.FirstPerson; // no-op
            Assert.AreEqual(1, count, "setting the same mode must not re-raise the event");
        }

        [Test]
        public void ParseRole_MapsKnownRoles()
        {
            Assert.AreEqual(ViewContextController.ViewRole.FirstPerson, ViewContextController.ParseRole("first_person"));
            Assert.AreEqual(ViewContextController.ViewRole.ThirdPerson, ViewContextController.ParseRole("third_person"));
            Assert.AreEqual(ViewContextController.ViewRole.Always, ViewContextController.ParseRole("always"));
        }

        [Test]
        public void ParseRole_UnknownRole_FallsBackToAlwaysAndWarns()
        {
            LogAssert.Expect(LogType.Warning, new Regex("Unknown visibility role"));
            Assert.AreEqual(ViewContextController.ViewRole.Always, ViewContextController.ParseRole("wibble"));
        }

        [Test]
        public void AndComposition_InactiveGameObject_StaysEffectivelyHidden()
        {
            // Core KHR_node_visibility maps to GameObject.SetActive(false). Our controller only manages
            // renderer.enabled, so the effective visibility is the logical AND: an inactive node never renders,
            // regardless of the hint's view-role.
            var view = NewGo("char").AddComponent<ViewContextController>();
            var go = NewGo("body");
            var rend = go.AddComponent<MeshRenderer>();
            view.RegisterRenderer(rend, ViewContextController.ViewRole.ThirdPerson);

            go.SetActive(false); // core visibility: hidden

            // In ThirdPerson the hint would enable the renderer, but the object is inactive -> effectively hidden.
            Assert.IsTrue(rend.enabled, "the hint enables the renderer for third-person");
            Assert.IsFalse(go.activeInHierarchy, "core KHR_node_visibility hides the object");
            Assert.IsFalse(rend.enabled && go.activeInHierarchy, "effective visibility is the AND (hidden)");

            view.Mode = ViewContextController.ViewContext.FirstPerson;
            Assert.IsFalse(rend.enabled, "third-person-only is disabled in first-person");
            Assert.IsFalse(rend.enabled && go.activeInHierarchy, "still effectively hidden");
        }
    }
}
