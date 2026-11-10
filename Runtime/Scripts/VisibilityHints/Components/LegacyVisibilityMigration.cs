using System;
using System.Collections.Generic;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Migrates visibility-hint data serialized in scenes and prefabs by earlier versions of this package: the single
    /// <c>Role</c> of a hint entry and the default view tags of <see cref="ViewContextController"/>. The glTF wire is
    /// not migrated here; an imported legacy <c>role</c> without <c>roles</c> still only produces a warning.
    /// </summary>
    internal static class LegacyVisibilityMigration
    {
        /// <summary>
        /// The role a legacy serialized role becomes: <c>third_person_only</c> and <c>first_person_only</c> (earlier
        /// drafts) map to <c>third_person</c> and <c>first_person</c>; any other role is kept as authored.
        /// </summary>
        internal static string MapLegacyRole(string role)
        {
            switch (role)
            {
                case "third_person_only": return VisibilityHintExtensionNames.RoleThirdPerson;
                case "first_person_only": return VisibilityHintExtensionNames.RoleFirstPerson;
                default: return role;
            }
        }

        /// <summary>
        /// The roles of an entry after migration: <paramref name="roles"/> when it holds any role, otherwise the
        /// mapped <paramref name="legacyRole"/> when that is nonempty, otherwise <paramref name="roles"/> unchanged.
        /// </summary>
        internal static string[] RolesWithLegacyRole(string[] roles, string legacyRole)
        {
            if (HasAnyRole(roles) || string.IsNullOrEmpty(legacyRole)) return roles;
            return new[] { MapLegacyRole(legacyRole) };
        }

        /// <summary>
        /// Maps legacy default view tags to default role logic. A view tag named a role that applies in the view, so
        /// the tags become the applied roles, and the application has logic for <c>first_person</c> and
        /// <c>third_person</c>: with the tag <c>third_person</c>, first-person content is hidden, as before. Custom
        /// tags become applied (and so recognized) custom roles. Returns false, changing nothing, when there are no
        /// legacy tags (the default, which now means no role logic) or when role logic is already serialized.
        /// </summary>
        internal static bool TryMigrateViewTags(
            IReadOnlyList<string> legacyTags,
            IReadOnlyList<string> appliedRoles,
            IReadOnlyList<string> rolesWithLogic,
            out List<string> migratedApplied,
            out List<string> migratedWithLogic)
        {
            migratedApplied = null;
            migratedWithLogic = null;
            if (!HasAnyRole(legacyTags) || HasAnyRole(appliedRoles) || HasAnyRole(rolesWithLogic)) return false;

            migratedApplied = new List<string>();
            foreach (var tag in legacyTags)
            {
                if (string.IsNullOrEmpty(tag)) continue;
                var role = MapLegacyRole(tag);
                if (!migratedApplied.Contains(role)) migratedApplied.Add(role);
            }
            migratedWithLogic = new List<string>();
            foreach (var role in new[] { VisibilityHintExtensionNames.RoleFirstPerson, VisibilityHintExtensionNames.RoleThirdPerson })
                if (!migratedApplied.Contains(role)) migratedWithLogic.Add(role);
            return true;
        }

        private static bool HasAnyRole(IReadOnlyList<string> roles)
        {
            if (roles == null) return false;
            foreach (var role in roles)
                if (!string.IsNullOrEmpty(role)) return true;
            return false;
        }
    }
}
