using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Play-mode tests for EyeAimConstraint — the non-spec, opt-in geometric eye-bone aiming that used to be the
    /// GazeSolver "BoneAim" output. Verifies it rotates the eye toward a target clamped to MaxYaw, then re-bases
    /// to the eye's rest rotation when stopped.
    /// </summary>
    public class EyeAimConstraintTests
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

        [UnityTest]
        public IEnumerator TargetRight_RotatesEyeClampedThenRebases()
        {
            var go = NewGo("char");
            var eye = new GameObject("eye");
            eye.transform.SetParent(go.transform, false); // rest = identity
            _created.Add(eye);

            var aim = go.AddComponent<EyeAimConstraint>();
            aim.SetEyeBones(eye.transform, eye.transform, null);
            aim.MaxYawDegrees = 35f;

            var target = NewGo("target");
            target.transform.position = go.transform.position + go.transform.right * 2f; // 90deg -> clamps to 35
            aim.Target = target.transform;
            aim.Mode = EyeAimConstraint.LookAtMode.CustomTarget;

            yield return null;
            Assert.AreEqual(35f, NormalizeAngle(eye.transform.localEulerAngles.y), 0.5f);

            aim.Mode = EyeAimConstraint.LookAtMode.None;
            yield return null;
            Assert.AreEqual(0f, NormalizeAngle(eye.transform.localEulerAngles.y), 0.5f); // re-based to rest
        }

        private static float NormalizeAngle(float deg) => deg > 180f ? deg - 360f : deg;
    }
}
