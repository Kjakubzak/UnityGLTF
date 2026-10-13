using System.Collections;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Golden-value tests for the joint baker's Unity-space keyframes -> JointDriver transform: delta-over-
    /// frame-0, single-key absolute target, base capture, and channel selection.
    /// </summary>
    public class KhrCharacterBakerJointTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        private Transform MakeTransform()
        {
            var go = new GameObject("joint");
            _created.Add(go);
            return go.transform;
        }

        [Test]
        public void Translation_DeltaOverFrame0()
        {
            var t = MakeTransform();
            var times = new[] { 0f, 1f };
            var values = new[] { Vector3.zero, new Vector3(0f, 1f, 0f) };
            var baseVec = new Vector3(5f, 5f, 5f);

            var drivers = new List<JointDriver>();
            KhrCharacterBaker.BuildJointVectorDriver(t, TrsChannel.Translation, times, values, InterpolationType.LINEAR, baseVec, drivers);

            Assert.AreEqual(1, drivers.Count);
            var d = drivers[0];
            Assert.AreEqual(TrsChannel.Translation, d.Channel);
            Assert.AreEqual(baseVec, d.BaseVec);
            Assert.IsFalse(d.Sampler.SingleKey);
            AssertVec(Vector3.zero, d.DeltaVec[0]);          // frame0 - frame0
            AssertVec(new Vector3(0f, 1f, 0f), d.DeltaVec[1]);
        }

        [Test]
        public void Scale_SingleKey_StoresAbsoluteTarget()
        {
            var t = MakeTransform();
            var drivers = new List<JointDriver>();
            KhrCharacterBaker.BuildJointVectorDriver(t, TrsChannel.Scale, new[] { 0f }, new[] { new Vector3(2f, 2f, 2f) }, InterpolationType.LINEAR, Vector3.one, drivers);

            Assert.AreEqual(1, drivers.Count);
            Assert.AreEqual(TrsChannel.Scale, drivers[0].Channel);
            Assert.IsTrue(drivers[0].Sampler.SingleKey);
            AssertVec(new Vector3(2f, 2f, 2f), drivers[0].DeltaVec[0]); // absolute target
        }

        [Test]
        public void Rotation_DeltaIsFrame0Relative()
        {
            var t = MakeTransform();
            var q0 = Quaternion.identity;
            var q1 = Quaternion.Euler(0f, 90f, 0f);

            var drivers = new List<JointDriver>();
            KhrCharacterBaker.BuildJointRotationDriver(t, new[] { 0f, 1f }, new[] { q0, q1 }, InterpolationType.LINEAR, Quaternion.identity, drivers);

            Assert.AreEqual(1, drivers.Count);
            Assert.AreEqual(TrsChannel.Rotation, drivers[0].Channel);
            Assert.Less(Quaternion.Angle(drivers[0].DeltaQuat[0], Quaternion.identity), 1e-3f); // q0 * inverse(q0)
            Assert.Less(Quaternion.Angle(drivers[0].DeltaQuat[1], q1), 1e-3f);                  // q1 * inverse(q0)
        }

        [Test]
        public void Base_IsCapturedFromNodeNeutral()
        {
            var t = MakeTransform();
            // The baker captures the base from the node's neutral local pose (target.localRotation/localPosition/
            // localScale), not from any reference_pose. Mirror that capture and assert it round-trips (H2).
            t.localPosition = new Vector3(1f, 2f, 3f);
            t.localRotation = Quaternion.Euler(10f, 20f, 30f);

            var rotDrivers = new List<JointDriver>();
            KhrCharacterBaker.BuildJointRotationDriver(t, new[] { 0f, 1f }, new[] { Quaternion.identity, Quaternion.Euler(0f, 90f, 0f) },
                InterpolationType.LINEAR, t.localRotation, rotDrivers);
            Assert.Less(Quaternion.Angle(rotDrivers[0].BaseQuat, t.localRotation), 1e-3f);

            var posDrivers = new List<JointDriver>();
            KhrCharacterBaker.BuildJointVectorDriver(t, TrsChannel.Translation, new[] { 0f, 1f },
                new[] { Vector3.zero, new Vector3(0f, 1f, 0f) }, InterpolationType.LINEAR, t.localPosition, posDrivers);
            AssertVec(t.localPosition, posDrivers[0].BaseVec);
        }

        [UnityTest]
        public IEnumerator LegacyBakerBakesPointerFormJointRotation()
        {
            var glb = new CharacterGlbBuilder();
            int input = glb.AddFloatAccessor("SCALAR", 2, new[] { 0f, 1f }, true);
            float half = Mathf.Sqrt(0.5f);
            int output = glb.AddFloatAccessor("VEC4", 2, new[] { 0f, 0f, 0f, 1f, 0f, half, 0f, half });
            glb.Json["extensionsUsed"] = new JArray(
                "KHR_character", "KHR_character_expression", "KHR_character_expression_joint", "KHR_animation_pointer");
            glb.Json["nodes"] = new JArray(new JObject { { "name", "head" } });
            glb.Json["animations"] = new JArray(new JObject
            {
                { "samplers", new JArray(new JObject { { "input", input }, { "output", output } }) },
                {
                    "channels", new JArray(new JObject
                    {
                        { "sampler", 0 },
                        {
                            "target", new JObject
                            {
                                { "path", "pointer" },
                                {
                                    "extensions", new JObject
                                    {
                                        { "KHR_animation_pointer", new JObject { { "pointer", "/nodes/0/rotation" } } },
                                    }
                                },
                            }
                        },
                    })
                },
            });
            glb.Json["extensions"] = new JObject
            {
                { "KHR_character", new JObject() },
                {
                    "KHR_character_expression", new JObject
                    {
                        {
                            "expressions", new JArray(new JObject
                            {
                                { "name", "turn" },
                                { "animation", 0 },
                                {
                                    "extensions", new JObject
                                    {
                                        { "KHR_character_expression_joint", new JObject { { "channels", new JArray(0) } } },
                                    }
                                },
                            })
                        },
                    }
                },
            };

            GameObject scene = null;
            yield return glb.Import(plugin => plugin.CreateExpressionController = true, loaded => scene = loaded);
            _created.Add(scene);

            var controller = scene.GetComponent<ExpressionController>();
            Assert.IsNotNull(controller, "the pointer-form joint expression passes validation and creates the adapter");
            var drivers = controller.Set.Expressions[0].JointDrivers;
            Assert.IsNotNull(drivers, "a KHR_animation_pointer joint channel is baked, not dropped");
            Assert.AreEqual(1, drivers.Length);
            Assert.AreEqual(TrsChannel.Rotation, drivers[0].Channel);
            Assert.AreEqual("head", drivers[0].Target.name);
            Assert.AreEqual(90f, Quaternion.Angle(Quaternion.identity, drivers[0].DeltaQuat[1]), 1e-3f);
        }

        private static void AssertVec(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-4f);
            Assert.AreEqual(expected.y, actual.y, 1e-4f);
            Assert.AreEqual(expected.z, actual.z, 1e-4f);
        }
    }
}
