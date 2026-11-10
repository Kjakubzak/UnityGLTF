using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Tests
{
    /// <summary>
    /// Scenes and prefabs saved by earlier versions of this package serialized a single <c>Role</c> per hint entry and
    /// default view tags on <see cref="ViewContextController"/>. Both migrate after deserialization instead of being
    /// silently lost.
    /// </summary>
    public class LegacySerializationMigrationTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var created in _created)
                if (created != null) Object.DestroyImmediate(created);
            _created.Clear();
        }

        [TestCase("third_person", "third_person")]
        [TestCase("first_person", "first_person")]
        [TestCase("always", "always")]
        [TestCase("third_person_only", "third_person")]
        [TestCase("first_person_only", "first_person")]
        [TestCase("ACME_mirror_only", "ACME_mirror_only")]
        public void LegacyRole_MigratesToRoles(string legacy, string expected)
        {
            var node = new NodeVisibilityHintSet.NodeVisibilityEntry { LegacyRole = legacy };
            ((ISerializationCallbackReceiver)node).OnAfterDeserialize();
            CollectionAssert.AreEqual(new[] { expected }, node.Roles);
            Assert.IsNull(node.LegacyRole, "the legacy field is cleared so it is never migrated twice");

            var primitive = new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry { LegacyRole = legacy };
            ((ISerializationCallbackReceiver)primitive).OnAfterDeserialize();
            CollectionAssert.AreEqual(new[] { expected }, primitive.Roles);
            Assert.IsNull(primitive.LegacyRole);
        }

        [Test]
        public void LegacyRole_NeverOverridesSerializedRoles_AndEmptyLegacyRoleIsIgnored()
        {
            var both = new NodeVisibilityHintSet.NodeVisibilityEntry
                { Roles = new[] { "first_person", "ACME_mirror_only" }, LegacyRole = "third_person" };
            ((ISerializationCallbackReceiver)both).OnAfterDeserialize();
            CollectionAssert.AreEqual(new[] { "first_person", "ACME_mirror_only" }, both.Roles);

            // Unity writes a null string as "", so a migrated entry deserializes with an empty legacy role.
            var empty = new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry { Roles = new string[0], LegacyRole = "" };
            ((ISerializationCallbackReceiver)empty).OnAfterDeserialize();
            CollectionAssert.IsEmpty(empty.Roles);
        }

        [Test]
        public void LegacyViewTags_MapToRoleLogic()
        {
            Assert.IsTrue(LegacyVisibilityMigration.TryMigrateViewTags(
                new[] { "third_person" }, new string[0], new string[0], out var applied, out var withLogic));
            var context = new VisibilityRoleContext(applied, withLogic);
            Assert.IsTrue(context.Applies("third_person"));
            Assert.IsFalse(context.Applies("first_person"), "a third_person view tag hid first-person content");
            Assert.IsTrue(context.Applies("always"));

            Assert.IsTrue(LegacyVisibilityMigration.TryMigrateViewTags(
                new[] { "first_person", "third_person", "ACME_mirror_only", "" }, null, null, out applied, out withLogic));
            context = new VisibilityRoleContext(applied, withLogic);
            Assert.IsTrue(context.Applies("first_person"));
            Assert.IsTrue(context.Applies("third_person"));
            Assert.IsTrue(context.IsRecognized("ACME_mirror_only"), "a custom view tag becomes a recognized role");
            Assert.IsTrue(context.Applies("ACME_mirror_only"));
        }

        [Test]
        public void LegacyViewTags_EmptyOrAlreadyMigrated_ChangeNothing()
        {
            Assert.IsFalse(LegacyVisibilityMigration.TryMigrateViewTags(
                new string[0], new string[0], new string[0], out _, out _), "no tags now means no role logic");
            Assert.IsFalse(LegacyVisibilityMigration.TryMigrateViewTags(
                new[] { "" }, null, null, out _, out _));
            Assert.IsFalse(LegacyVisibilityMigration.TryMigrateViewTags(
                new[] { "third_person" }, new[] { "first_person" }, new string[0], out _, out _),
                "serialized role logic is never overwritten by legacy tags");
        }

        [Test]
        public void ViewContextController_MigratesLegacyDefaultViewTags()
        {
            var go = new GameObject("legacy-view");
            _created.Add(go);
            var controller = go.AddComponent<ViewContextController>();
            controller.SetLegacyDefaultViewTagsForTest(new[] { "first_person" });
            ((ISerializationCallbackReceiver)controller).OnAfterDeserialize();
            var roles = controller.DefaultRoles;
            Assert.AreNotSame(VisibilityRoleContext.NoLogic, roles);
            Assert.IsTrue(roles.Applies("first_person"));
            Assert.IsFalse(roles.Applies("third_person"));

            // A second deserialization finds the legacy tags cleared and keeps the migrated logic.
            ((ISerializationCallbackReceiver)controller).OnAfterDeserialize();
            Assert.IsFalse(controller.DefaultRoles.Applies("third_person"));

            var fresh = new GameObject("fresh-view").AddComponent<ViewContextController>();
            _created.Add(fresh.gameObject);
            ((ISerializationCallbackReceiver)fresh).OnAfterDeserialize();
            Assert.AreSame(VisibilityRoleContext.NoLogic, fresh.DefaultRoles, "no legacy tags: no role logic");
        }

        [Test]
        public void UnitySerialization_ReadsTheLegacyFieldNames()
        {
            // JsonUtility uses Unity's serializer, including FormerlySerializedAs and the serialization callbacks.
            var entry = JsonUtility.FromJson<NodeVisibilityHintSet.NodeVisibilityEntry>("{\"Role\":\"third_person_only\"}");
            CollectionAssert.AreEqual(new[] { "third_person" }, entry.Roles);
            var primitive = JsonUtility.FromJson<PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry>("{\"Role\":\"first_person\"}");
            CollectionAssert.AreEqual(new[] { "first_person" }, primitive.Roles);

            var go = new GameObject("legacy-json-view");
            _created.Add(go);
            var controller = go.AddComponent<ViewContextController>();
            JsonUtility.FromJsonOverwrite("{\"_defaultViewTags\":[\"third_person\"]}", controller);
            Assert.IsTrue(controller.DefaultRoles.Applies("third_person"));
            Assert.IsFalse(controller.DefaultRoles.Applies("first_person"));
        }
    }
}
