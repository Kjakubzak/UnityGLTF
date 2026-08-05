using System.Collections.Generic;

namespace UnityGLTF.KhrCharacter
{
    public enum CapabilityStatus
    {
        Active,    // a supported passive data surface or selected host adapter is available
        Degraded,  // the selected host adapter is available but incomplete
        Inert,     // declared in the asset without a usable data surface in this implementation
    }

    public struct CapabilityHealth
    {
        public CharacterCapability Capability;
        public CapabilityStatus Status;
    }

    /// <summary>
    /// Per-capability runtime health for a character, suitable for a "Character Health" inspector/HUD: which
    /// capabilities are active vs present-but-inert, and the expression count.
    /// </summary>
    public class CharacterHealthReport
    {
        public readonly List<CapabilityHealth> Capabilities = new List<CapabilityHealth>();
        public int ExpressionCount;
    }
}
