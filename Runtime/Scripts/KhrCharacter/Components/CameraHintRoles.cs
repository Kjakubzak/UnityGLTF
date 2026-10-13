using System.Collections.Generic;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Standard <c>KHR_node_camera_hint</c> roles. Roles are exact and case-sensitive: any other string, including a
    /// differently cased standard value such as <c>"Default_View"</c> or a former draft role such as
    /// <c>"framing_target"</c>, is a custom role.
    /// </summary>
    public static class CameraHintRoles
    {
        public const string DefaultView = "default_view";
        public const string Detail = "detail";
        public const string FirstPerson = "first_person";
        public const string FramingView = "framing_view";
        public const string FullBody = "full_body";
        public const string OrbitView = "orbit_view";
        public const string Portrait = "portrait";
        public const string ThirdPerson = "third_person";

        /// <summary>The eight standard roles.</summary>
        public static IReadOnlyList<string> Standard { get; } = new[]
        {
            DefaultView, Detail, FirstPerson, FramingView, FullBody, OrbitView, Portrait, ThirdPerson,
        };

        /// <summary>True when <paramref name="role"/> is exactly one of the standard roles.</summary>
        public static bool IsStandard(string role)
        {
            switch (role)
            {
                case DefaultView:
                case Detail:
                case FirstPerson:
                case FramingView:
                case FullBody:
                case OrbitView:
                case Portrait:
                case ThirdPerson:
                    return true;
                default:
                    return false;
            }
        }
    }
}
