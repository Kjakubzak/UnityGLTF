using System.Collections.Generic;

namespace UnityGLTF.KhrCharacter
{
    public enum CapabilityStatus
    {
        Active,    // present in the asset and driven at runtime
        Degraded,  // present but only partially driven
        Inert,     // present in the asset but not driven at runtime
    }

    public struct CapabilityHealth
    {
        public CharacterCapability Capability;
        public CapabilityStatus Status;
    }

    /// <summary>
    /// Per-capability runtime health for a character, suitable for a "Character Health" inspector/HUD: which
    /// capabilities are active vs present-but-inert, the resolved skeleton-mapping direction, and the
    /// expression count.
    /// </summary>
    public class CharacterHealthReport
    {
        public readonly List<CapabilityHealth> Capabilities = new List<CapabilityHealth>();
        public MappingDirection SkeletonDirection = MappingDirection.Unknown;
        public int ExpressionCount;
    }
}
