using System.Text.RegularExpressions;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGLTF.VisibilityHints.Tests
{
    /// <summary>
    /// Wire-level tests for the two visibility-hint extension factories: authored serialize -> deserialize
    /// round-trips, lossless <c>RawData</c> passthrough (custom fields survive), the optional-label
    /// <c>minLength:1</c> guard, and the missing-<c>role</c> warning.
    /// </summary>
    public class SchemaRoundTripTests
    {
        [Test]
        public void NodeHint_AuthoredRoundTrip_PreservesRoleAndLabel()
        {
            var ext = new KHR_node_visibility_hint { Role = "third_person", Label = "Head" };
            var reparsed = new KHR_node_visibility_hint_Factory().Deserialize(null, ext.Serialize()) as KHR_node_visibility_hint;

            Assert.IsNotNull(reparsed);
            Assert.AreEqual("third_person", reparsed.Role);
            Assert.AreEqual("Head", reparsed.Label);
        }

        [Test]
        public void PrimitiveHint_AuthoredRoundTrip_PreservesRoleAndLabel()
        {
            var ext = new KHR_mesh_primitive_visibility_hint { Role = "first_person", Label = "Arms" };
            var reparsed = new KHR_mesh_primitive_visibility_hint_Factory().Deserialize(null, ext.Serialize()) as KHR_mesh_primitive_visibility_hint;

            Assert.IsNotNull(reparsed);
            Assert.AreEqual("first_person", reparsed.Role);
            Assert.AreEqual("Arms", reparsed.Label);
        }

        [Test]
        public void NodeHint_RawData_PassesThroughUnknownFieldsLosslessly()
        {
            // A deserialized hint keeps the raw token; re-serializing must preserve custom/unknown fields verbatim.
            var json = new JProperty(KHR_node_visibility_hint.EXTENSION_NAME, new JObject(
                new JProperty("role", "custom_role"),
                new JProperty("label", "Custom"),
                new JProperty("vendorFlag", 7)));

            var ext = new KHR_node_visibility_hint_Factory().Deserialize(null, json) as KHR_node_visibility_hint;
            Assert.IsNotNull(ext);
            Assert.AreEqual("custom_role", ext.Role);

            var obj = (JObject)ext.Serialize().Value;
            Assert.AreEqual("custom_role", obj["role"]?.Value<string>());
            Assert.AreEqual("Custom", obj["label"]?.Value<string>());
            Assert.AreEqual(7, obj["vendorFlag"]?.Value<int>(), "unknown fields must survive via RawData passthrough");
        }

        [Test]
        public void PrimitiveHint_RawData_PassesThroughUnknownFieldsLosslessly()
        {
            var json = new JProperty(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME, new JObject(
                new JProperty("role", "third_person"),
                new JProperty("extra", "keepme")));

            var ext = new KHR_mesh_primitive_visibility_hint_Factory().Deserialize(null, json) as KHR_mesh_primitive_visibility_hint;
            Assert.IsNotNull(ext);

            var obj = (JObject)ext.Serialize().Value;
            Assert.AreEqual("keepme", obj["extra"]?.Value<string>(), "unknown fields must survive via RawData passthrough");
        }

        [Test]
        public void NodeHint_EmptyLabel_OmittedFromWire()
        {
            // label is optional but minLength:1: an authored empty label (Unity coerces null -> "") must be omitted.
            var ext = new KHR_node_visibility_hint { Role = "always", Label = "" };
            var obj = (JObject)ext.Serialize().Value;

            Assert.IsTrue(obj.ContainsKey("role"), "role must be present on the wire");
            Assert.IsFalse(obj.ContainsKey("label"), "an empty label must be omitted (schema minLength:1)");
        }

        [Test]
        public void PrimitiveHint_EmptyLabel_OmittedFromWire()
        {
            var ext = new KHR_mesh_primitive_visibility_hint { Role = "always", Label = null };
            var obj = (JObject)ext.Serialize().Value;

            Assert.IsFalse(obj.ContainsKey("label"), "a null label must be omitted (schema minLength:1)");
        }

        [Test]
        public void NodeHint_MissingRole_LogsWarning()
        {
            LogAssert.Expect(LogType.Warning, new Regex("'role' is missing"));
            var ext = new KHR_node_visibility_hint_Factory()
                .Deserialize(null, new JProperty(KHR_node_visibility_hint.EXTENSION_NAME, new JObject())) as KHR_node_visibility_hint;

            Assert.IsNotNull(ext);
            Assert.IsTrue(string.IsNullOrEmpty(ext.Role), "a missing role deserializes as null/empty");
        }

        [Test]
        public void PrimitiveHint_MissingRole_LogsWarning()
        {
            LogAssert.Expect(LogType.Warning, new Regex("'role' is missing"));
            var ext = new KHR_mesh_primitive_visibility_hint_Factory()
                .Deserialize(null, new JProperty(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME, new JObject())) as KHR_mesh_primitive_visibility_hint;

            Assert.IsNotNull(ext);
            Assert.IsTrue(string.IsNullOrEmpty(ext.Role));
        }
    }
}
