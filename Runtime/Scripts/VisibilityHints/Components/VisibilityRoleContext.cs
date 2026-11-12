using System;
using System.Collections.Generic;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// The application's role logic for one render view and asset instance: which roles apply there, and which roles
    /// the application has logic for. This is the input of every visibility-hint query.
    /// <list type="bullet">
    /// <item><c>always</c> applies in every view.</item>
    /// <item>A standard role (<c>first_person</c>, <c>third_person</c>) that the application has no logic for applies
    /// in every view; one it has logic for applies only where it is listed as applying.</item>
    /// <item>A custom role is recognized only when the application has logic for it, and then applies only where it
    /// is listed as applying. An unrecognized custom role is ignored.</item>
    /// </list>
    /// A role listed as applying counts as having logic. Roles are compared ordinally. Instances are immutable.
    /// <see cref="NoLogic"/> (the default everywhere) has logic for no role, so every hint allows every view.
    /// </summary>
    public sealed class VisibilityRoleContext
    {
        /// <summary>No role logic: every standard role applies in every view and custom roles are ignored, so hints
        /// hide nothing. Suitable for inspectors, thumbnails and hosts without first- or third-person logic.</summary>
        public static readonly VisibilityRoleContext NoLogic = new VisibilityRoleContext(null, null);

        private readonly string[] _applied;
        private readonly string[] _withLogic;

        /// <param name="appliedRoles">Roles that apply in this view for this instance. Null and empty strings are
        /// ignored.</param>
        /// <param name="rolesWithLogic">Further roles the application has logic for (and that therefore do not apply
        /// unless listed in <paramref name="appliedRoles"/>), such as <c>third_person</c> in a first-person view or a
        /// recognized custom role that does not apply in this view. A context with logic for <c>first_person</c> but
        /// not for <c>third_person</c> applies <c>third_person</c> in every view, so content marked
        /// <c>third_person</c> (such as a head) is also drawn in first-person views; see
        /// <see cref="PairsThirdPersonWithFirstPerson"/> and <see cref="FirstPersonRolePolicy"/>.</param>
        public VisibilityRoleContext(IEnumerable<string> appliedRoles, IEnumerable<string> rolesWithLogic)
        {
            _applied = Distinct(appliedRoles, null);
            _withLogic = Distinct(rolesWithLogic, _applied);
        }

        /// <summary>The roles listed as applying in this view, without duplicates.</summary>
        public IReadOnlyList<string> AppliedRoles => _applied;

        /// <summary>Every role the application has logic for: the applied roles followed by the other roles with
        /// logic, without duplicates.</summary>
        public IReadOnlyList<string> RolesWithLogic
        {
            get
            {
                var all = new string[_applied.Length + _withLogic.Length];
                _applied.CopyTo(all, 0);
                _withLogic.CopyTo(all, _applied.Length);
                return all;
            }
        }

        /// <summary>
        /// False when this context has logic for <c>first_person</c> and either has no logic for <c>third_person</c>
        /// or applies neither role; otherwise true. The specification says an application that applies
        /// <c>first_person</c> SHOULD apply <c>third_person</c> wherever <c>first_person</c> does not apply. Without
        /// logic for <c>third_person</c>, that role also applies in first-person views, so <c>third_person</c>
        /// content is drawn there. <see cref="FirstPersonRolePolicy"/> contexts are always true; a host that builds
        /// contexts itself can assert this property.
        /// </summary>
        public bool PairsThirdPersonWithFirstPerson
            => !HasLogicFor(VisibilityHintExtensionNames.RoleFirstPerson)
               || (HasLogicFor(VisibilityHintExtensionNames.RoleThirdPerson)
                   && (Applies(VisibilityHintExtensionNames.RoleFirstPerson)
                       || Applies(VisibilityHintExtensionNames.RoleThirdPerson)));

        /// <summary>True when the application has logic for <paramref name="role"/>.</summary>
        public bool HasLogicFor(string role) => Contains(_applied, role) || Contains(_withLogic, role);

        /// <summary>
        /// True when <paramref name="role"/> takes part in hint evaluation: <c>always</c>, the other standard roles,
        /// and custom roles the application has logic for. An unrecognized custom role is ignored.
        /// </summary>
        public bool IsRecognized(string role)
            => !string.IsNullOrEmpty(role)
               && (VisibilityHintExtensionNames.IsStandardRole(role) || HasLogicFor(role));

        /// <summary>
        /// True when <paramref name="role"/> applies in this view: <c>always</c>; a standard role the application has
        /// no logic for; or a role listed as applying.
        /// </summary>
        public bool Applies(string role)
        {
            if (string.IsNullOrEmpty(role)) return false;
            if (string.Equals(role, VisibilityHintExtensionNames.RoleAlways, StringComparison.Ordinal)) return true;
            if (Contains(_applied, role)) return true;
            return VisibilityHintExtensionNames.IsStandardRole(role) && !Contains(_withLogic, role);
        }

        /// <summary>
        /// Whether a hint with <paramref name="roles"/> allows this view: true when the hint has no role, when any of
        /// its recognized roles applies (roles in one hint are alternatives), or when none of its roles is
        /// recognized. A hint that lists <c>always</c> therefore never restricts.
        /// </summary>
        public bool Allows(IReadOnlyCollection<string> roles)
        {
            if (roles == null || roles.Count == 0) return true;
            bool anyRecognized = false;
            foreach (var role in roles)
            {
                if (!IsRecognized(role)) continue;
                if (Applies(role)) return true;
                anyRecognized = true;
            }
            return !anyRecognized;
        }

        private static string[] Distinct(IEnumerable<string> roles, string[] exclude)
        {
            if (roles == null) return Array.Empty<string>();
            var list = new List<string>();
            foreach (var role in roles)
                if (!string.IsNullOrEmpty(role) && !Contains(list, role) && (exclude == null || !Contains(exclude, role)))
                    list.Add(role);
            return list.ToArray();
        }

        // Iterates instead of calling a collection's Contains so a caller's comparer cannot loosen the exact match.
        private static bool Contains(IEnumerable<string> roles, string role)
        {
            foreach (var candidate in roles)
                if (string.Equals(candidate, role, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>
    /// Role logic for an application with first-person views, following the specification's recommendation: the
    /// application applies <c>first_person</c> where its first-person logic says (for example the user's own eye
    /// views of their avatar) and <c>third_person</c> wherever <c>first_person</c> does not apply. The
    /// <c>first_person</c> role itself is informative; when and where it applies is the application's decision.
    /// </summary>
    public static class FirstPersonRolePolicy
    {
        private static readonly string[] StandardRoles =
        {
            VisibilityHintExtensionNames.RoleFirstPerson,
            VisibilityHintExtensionNames.RoleThirdPerson,
        };

        /// <summary>A first-person view: <c>first_person</c> applies and <c>third_person</c> does not.</summary>
        public static readonly VisibilityRoleContext FirstPersonView = ForView(true);

        /// <summary>Any other view (spectator, mirror, external camera): <c>third_person</c> applies.</summary>
        public static readonly VisibilityRoleContext OtherView = ForView(false);

        /// <summary>
        /// The role logic of one view. <paramref name="appliedCustomRoles"/> adds custom roles that apply in this
        /// view (such as <c>ACME_mirror_only</c> in a mirror view); <paramref name="recognizedCustomRoles"/> lists
        /// custom roles the application recognizes but that do not apply here. Other custom roles are ignored.
        /// </summary>
        public static VisibilityRoleContext ForView(
            bool isFirstPersonView,
            IEnumerable<string> appliedCustomRoles = null,
            IEnumerable<string> recognizedCustomRoles = null)
        {
            var applied = new List<string>
            {
                isFirstPersonView
                    ? VisibilityHintExtensionNames.RoleFirstPerson
                    : VisibilityHintExtensionNames.RoleThirdPerson,
            };
            if (appliedCustomRoles != null) applied.AddRange(appliedCustomRoles);
            var withLogic = new List<string>(StandardRoles);
            if (recognizedCustomRoles != null) withLogic.AddRange(recognizedCustomRoles);
            return new VisibilityRoleContext(applied, withLogic);
        }
    }
}
