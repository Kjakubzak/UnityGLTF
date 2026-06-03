using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Synchronous tests for CameraHintSet.Apply (look-at + the -Z InvertDirection no-target case) and
    /// ViewModeController visibility toggling.
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

        [Test]
        public void CameraHint_WithTarget_LooksAtTarget()
        {
            var node = NewGo("node");
            node.transform.position = new Vector3(0f, 1f, 0f);
            var target = NewGo("target");
            target.transform.position = new Vector3(0f, 1f, 5f); // +Z from the node
            var cam = NewGo("cam").AddComponent<Camera>();
            var chs = NewGo("chs").AddComponent<CameraHintSet>();

            chs.Apply(new CameraHint { Role = "main", Node = node.transform, Target = target.transform }, cam);

            Assert.Less(Vector3.Distance(cam.transform.position, node.transform.position), 1e-4f);
            Assert.Greater(Vector3.Dot(cam.transform.forward, Vector3.forward), 0.999f);
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
