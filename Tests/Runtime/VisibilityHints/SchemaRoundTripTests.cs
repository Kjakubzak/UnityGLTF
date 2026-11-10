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
    /// round-trips of <c>roles</c>, preservation of additional properties and glTFProperty payloads, the
    /// optional-label <c>minLength:1</c> guard, invalid and legacy <c>role</c> input, and the non-required factory
    /// policy.
    /// </summary>
    public class SchemaRoundTripTests
    {
        [Test]
        public void NodeHint_AuthoredRoundTrip_PreservesRolesAndLabel()
        {
            var ext = new KHR_node_visibility_hint { Roles = new[] { "first_person", "ACME_mirror_only" }, Label = "Self status" };
            var wire = (JObject)ext.Serialize().Value;
            CollectionAssert.AreEqual(new[] { "first_person", "ACME_mirror_only" }, wire["roles"].ToObject<string[]>());
            Assert.IsNull(wire["role"], "the legacy single role is never written");

            var reparsed = new KHR_node_visibility_hint_Factory().Deserialize(null, ext.Serialize()) as KHR_node_visibility_hint;
            Assert.IsNotNull(reparsed);
            CollectionAssert.AreEqual(new[] { "first_person", "ACME_mirror_only" }, reparsed.Roles);
            Assert.AreEqual("Self status", reparsed.Label);
        }

        [Test]
        public void PrimitiveHint_AuthoredRoundTrip_PreservesRolesAndLabel()
        {
            var ext = new KHR_mesh_primitive_visibility_hint { Roles = new[] { "first_person" }, Label = "Arms" };
            var reparsed = new KHR_mesh_primitive_visibility_hint_Factory().Deserialize(null, ext.Serialize()) as KHR_mesh_primitive_visibility_hint;

            Assert.IsNotNull(reparsed);
            CollectionAssert.AreEqual(new[] { "first_person" }, reparsed.Roles);
            Assert.AreEqual("Arms", reparsed.Label);
        }

        [Test]
        public void NodeHint_AdditionalProperties_PassThroughLosslessly()
        {
            var json = new JProperty(KHR_node_visibility_hint.EXTENSION_NAME, new JObject(
                new JProperty("roles", new JArray("ACME_custom_role")),
                new JProperty("label", "Custom"),
                new JProperty("vendorFlag", 7)));

            var ext = new KHR_node_visibility_hint_Factory().Deserialize(null, json) as KHR_node_visibility_hint;
            Assert.IsNotNull(ext);
            CollectionAssert.AreEqual(new[] { "ACME_custom_role" }, ext.Roles);

            var obj = (JObject)ext.Serialize().Value;
            CollectionAssert.AreEqual(new[] { "ACME_custom_role" }, obj["roles"].ToObject<string[]>());
            Assert.AreEqual("Custom", obj["label"]?.Value<string>());
            Assert.AreEqual(7, obj["vendorFlag"]?.Value<int>());
        }

        [Test]
        public void PrimitiveHint_AdditionalProperties_PassThroughLosslessly()
        {
            var json = new JProperty(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME, new JObject(
                new JProperty("roles", new JArray("third_person")),
                new JProperty("extra", "keepme")));

            var ext = new KHR_mesh_primitive_visibility_hint_Factory().Deserialize(null, json) as KHR_mesh_primitive_visibility_hint;
            Assert.IsNotNull(ext);

            var obj = (JObject)ext.Serialize().Value;
            Assert.AreEqual("keepme", obj["extra"]?.Value<string>());
        }

        [Test]
        public void NodeHint_PreservesExtensionsAndExtrasWhileAllowingRoleEdits()
        {
            var json = new JProperty(KHR_node_visibility_hint.EXTENSION_NAME, new JObject
            {
                { "roles", new JArray("third_person") },
                { "extensions", new JObject { { "ACME_visibility", new JObject { { "mode", 3 } } } } },
                { "extras", new JObject { { "author", "test" } } },
            });
            var ext = (KHR_node_visibility_hint)new KHR_node_visibility_hint_Factory().Deserialize(null, json);
            ext.Roles = new[] { "first_person" };

            var serialized = (JObject)ext.Serialize().Value;
            CollectionAssert.AreEqual(new[] { "first_person" }, serialized["roles"].ToObject<string[]>());
            Assert.AreEqual(3, serialized["extensions"]?["ACME_visibility"]?["mode"]?.Value<int>());
            Assert.AreEqual("test", serialized["extras"]?["author"]?.Value<string>());
        }

        [Test]
        public void NodeHint_EmptyLabel_OmittedFromWire()
        {
            // label is optional but minLength:1: an authored empty label (Unity coerces null -> "") must be omitted.
            var ext = new KHR_node_visibility_hint { Roles = new[] { "always" }, Label = "" };
            var obj = (JObject)ext.Serialize().Value;

            Assert.IsTrue(obj.ContainsKey("roles"), "roles must be present on the wire");
            Assert.IsFalse(obj.ContainsKey("label"), "an empty label must be omitted (schema minLength:1)");
        }

        [Test]
        public void PrimitiveHint_EmptyLabel_OmittedFromWire()
        {
            var ext = new KHR_mesh_primitive_visibility_hint { Roles = new[] { "always" }, Label = null };
            var obj = (JObject)ext.Serialize().Value;

            Assert.IsFalse(obj.ContainsKey("label"), "a null label must be omitted (schema minLength:1)");
        }

        [Test]
        public void Serialize_DropsEmptyAndDuplicateRoles()
        {
            var ext = new KHR_node_visibility_hint { Roles = new[] { "third_person", "", null, "third_person" } };
            var obj = (JObject)ext.Serialize().Value;
            CollectionAssert.AreEqual(new[] { "third_person" }, obj["roles"].ToObject<string[]>(),
                "roles is a set of distinct nonempty strings");
        }

        [Test]
        public void NodeHint_MissingRoles_LogsWarningAndReadsNoRoles()
        {
            LogAssert.Expect(LogType.Warning, new Regex("'roles' array is missing"));
            var ext = new KHR_node_visibility_hint_Factory()
                .Deserialize(null, new JProperty(KHR_node_visibility_hint.EXTENSION_NAME, new JObject())) as KHR_node_visibility_hint;

            Assert.IsNotNull(ext);
            Assert.IsNull(ext.Roles, "a missing roles array deserializes as null");
        }

        [Test]
        public void PrimitiveHint_MissingRoles_LogsWarningAndReadsNoRoles()
        {
            LogAssert.Expect(LogType.Warning, new Regex("'roles' array is missing"));
            var ext = new KHR_mesh_primitive_visibility_hint_Factory()
                .Deserialize(null, new JProperty(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME, new JObject())) as KHR_mesh_primitive_visibility_hint;

            Assert.IsNotNull(ext);
            Assert.IsNull(ext.Roles);
        }

        [Test]
        public void LegacyRole_IsNotReadAsRolesAndWarns()
        {
            LogAssert.Expect(LogType.Warning, new Regex("legacy 'role'"));
            var ext = new KHR_node_visibility_hint_Factory().Deserialize(null,
                new JProperty(KHR_node_visibility_hint.EXTENSION_NAME, new JObject { { "role", "third_person" } }))
                as KHR_node_visibility_hint;

            Assert.IsNotNull(ext);
            Assert.IsNull(ext.Roles, "the legacy single role is not migrated; the hint imposes no restriction");
            Assert.AreEqual("third_person", ext.AdditionalProperties["role"].Value<string>(),
                "the legacy member is preserved as an unknown property");
        }

        [Test]
        public void InvalidAndDuplicateRoleItems_AreDroppedWithWarnings()
        {
            LogAssert.Expect(LogType.Warning, new Regex("not a nonempty string"));
            LogAssert.Expect(LogType.Warning, new Regex("not a nonempty string"));
            LogAssert.Expect(LogType.Warning, new Regex("duplicate role 'first_person'"));
            var ext = new KHR_mesh_primitive_visibility_hint_Factory().Deserialize(null,
                new JProperty(KHR_mesh_primitive_visibility_hint.EXTENSION_NAME, new JObject
                {
                    { "roles", new JArray("first_person", "", 3, "first_person", "ACME_x_only") },
                })) as KHR_mesh_primitive_visibility_hint;

            CollectionAssert.AreEqual(new[] { "first_person", "ACME_x_only" }, ext.Roles);
        }

        [Test]
        public void Factories_NeverRequireRuntimeSupportForRequiredUse()
        {
            Assert.IsFalse(new KHR_node_visibility_hint_Factory().RequiresRuntimeSupportForRequiredUse,
                "the hints must not be required, so a listing never fails an import closed");
            Assert.IsFalse(new KHR_mesh_primitive_visibility_hint_Factory().RequiresRuntimeSupportForRequiredUse);
        }
    }
}
