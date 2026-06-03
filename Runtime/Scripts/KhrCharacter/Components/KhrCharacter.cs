using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Top-level hub attached to the character root. Owns capability discovery and references to the
    /// sub-controllers (null when the corresponding extension is absent). Application code branches on
    /// <see cref="Has"/> / <see cref="Capabilities"/> and waits for <see cref="OnCharacterReady"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class KhrCharacter : MonoBehaviour
    {
        /// <summary>Fired after import wiring completes. Do not assume readiness in Awake (async import).</summary>
        public event Action<KhrCharacter> OnCharacterReady;
        public bool IsReady { get; private set; }

        // Sub-controllers — null when the corresponding extension is absent (graceful degradation).
        public ExpressionController Expressions { get; internal set; }
        public GazeSolver Gaze { get; internal set; }
        public CameraHintSet CameraHints { get; internal set; }
        public SkeletonMap Skeleton { get; internal set; }
        public ViewModeController View { get; internal set; }

        private readonly List<CharacterCapability> _capabilities = new List<CharacterCapability>();
        public IReadOnlyList<CharacterCapability> Capabilities => _capabilities;

        public bool Has(CharacterCapability capability) => _capabilities.Contains(capability);

        internal void SetCapabilities(IEnumerable<CharacterCapability> capabilities)
        {
            _capabilities.Clear();
            if (capabilities != null) _capabilities.AddRange(capabilities);
        }

        internal void MarkReady()
        {
            IsReady = true;
            OnCharacterReady?.Invoke(this);
        }

        /// <summary>
        /// Snapshot of which capabilities are active vs present-but-inert, plus the resolved skeleton direction.
        /// Drives the Character Health inspector/HUD and helps diagnose dropped name-couplings.
        /// </summary>
        public CharacterHealthReport GetHealth()
        {
            var report = new CharacterHealthReport
            {
                SkeletonDirection = Skeleton != null ? Skeleton.DetectedDirection : MappingDirection.Unknown,
                ExpressionCount = Expressions != null ? Expressions.Count : 0,
            };
            foreach (var capability in _capabilities)
                report.Capabilities.Add(new CapabilityHealth { Capability = capability, Status = StatusFor(capability) });
            return report;
        }

        private CapabilityStatus StatusFor(CharacterCapability capability)
        {
            switch (capability)
            {
                case CharacterCapability.Character:
                    return CapabilityStatus.Active;
                case CharacterCapability.Expression:
                case CharacterCapability.Morphtarget:
                case CharacterCapability.Joint:
                case CharacterCapability.Texture:
                case CharacterCapability.Mask:
                case CharacterCapability.Mapping:
                    return Expressions != null ? CapabilityStatus.Active : CapabilityStatus.Inert;
                case CharacterCapability.CameraHint:
                    return CameraHints != null ? CapabilityStatus.Active : CapabilityStatus.Inert;
                case CharacterCapability.LookAtTarget:
                    return Gaze != null ? CapabilityStatus.Active : CapabilityStatus.Inert;
                case CharacterCapability.SkeletonMapping:
                    return Skeleton != null ? CapabilityStatus.Active : CapabilityStatus.Inert;
                case CharacterCapability.ReferencePose:
                    return (Skeleton != null && Skeleton.Result?.ReferencePose != null) ? CapabilityStatus.Active : CapabilityStatus.Inert;
                default:
                    return CapabilityStatus.Inert;
            }
        }
    }
}
