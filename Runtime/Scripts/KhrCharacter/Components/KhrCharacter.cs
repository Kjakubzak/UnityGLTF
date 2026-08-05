using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Top-level hub attached to the character root. Owns capability discovery and references to the
    /// sub-controllers (null when the corresponding extension is absent). Application code branches on
    /// <see cref="Has"/> / <see cref="Capabilities"/> and registers a readiness callback via
    /// <see cref="WhenReady"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class KhrCharacter : MonoBehaviour
    {
        /// <summary>
        /// Fired once when the character becomes ready (after a live import or prefab rehydration). Readiness can
        /// be reached during Start, or synchronously inside Object.Instantiate / a live import, so a subscription
        /// added afterwards may miss it — prefer <see cref="WhenReady"/>.
        /// </summary>
        public event Action<KhrCharacter> OnCharacterReady;
        public bool IsReady { get; private set; }

        // Sub-controllers — null when the corresponding extension is absent (graceful degradation).
        public ExpressionController Expressions { get; internal set; }
        public ExpressionResponseSet ExpressionResponses { get; internal set; }
        public GazeSolver Gaze { get; internal set; }
        public CameraHintSet CameraHints { get; internal set; }
        public LookAtTargetSet LookAtTargets { get; internal set; }
        public SkeletonMap Skeleton { get; internal set; }
        public ViewModeController View { get; internal set; }
        public int DesignatedRootNodeIndex => _designatedRootNodeIndex;
        public Transform DesignatedRoot => _designatedRoot;

        private readonly List<CharacterCapability> _capabilities = new List<CharacterCapability>();
        public IReadOnlyList<CharacterCapability> Capabilities => _capabilities;

        // Persisted so an editor-imported prefab can rehydrate on Start. A non-empty value is also the signal
        // that this component was deserialized from a baked asset (vs added fresh by a live import). Hidden from
        // the inspector: it's baked data, surfaced read-only by KhrCharacterEditor rather than hand-edited.
        [SerializeField, HideInInspector] private List<CharacterCapability> _serializedCapabilities = new List<CharacterCapability>();
        [SerializeField, HideInInspector] private int _designatedRootNodeIndex = -1;
        [SerializeField, HideInInspector] private Transform _designatedRoot;

        public bool Has(CharacterCapability capability) => _capabilities.Contains(capability);

        // Rehydrate an editor-imported prefab: restore capabilities, re-resolve the sub-controllers that persist
        // as sibling components, then fire OnCharacterReady. Runs in Start (not Awake) so every sub-controller
        // has already rehydrated in its own Awake before readiness is announced. Guarded on the serialized
        // capabilities being present so a live import (which adds this component fresh, then wires + MarkReady
        // itself) is never pre-empted, and on IsReady so the already-wired live path no-ops here.
        private void Start()
        {
            if (IsReady) return;
            if (_serializedCapabilities == null || _serializedCapabilities.Count == 0) return;

            _capabilities.Clear();
            _capabilities.AddRange(_serializedCapabilities);

            if (Expressions == null) Expressions = GetComponent<ExpressionController>();
            if (ExpressionResponses == null) ExpressionResponses = GetComponent<ExpressionResponseSet>();
            if (Gaze == null) Gaze = GetComponent<GazeSolver>();
            if (CameraHints == null) CameraHints = GetComponent<CameraHintSet>();
            if (LookAtTargets == null) LookAtTargets = GetComponent<LookAtTargetSet>();
            if (Skeleton == null) Skeleton = GetComponent<SkeletonMap>();
            if (View == null) View = GetComponent<ViewModeController>();

            MarkReady();
        }

        internal void SetCapabilities(IEnumerable<CharacterCapability> capabilities)
        {
            _capabilities.Clear();
            if (capabilities != null) _capabilities.AddRange(capabilities);
            _serializedCapabilities = new List<CharacterCapability>(_capabilities);   // persist for prefab rehydration
        }

        internal void SetDesignation(int nodeIndex, Transform resolvedTransform)
        {
            _designatedRootNodeIndex = nodeIndex;
            _designatedRoot = resolvedTransform;
        }

        internal void MarkReady()
        {
            if (IsReady) return;   // fire OnCharacterReady exactly once (live import path or Start rehydration)
            IsReady = true;
            OnCharacterReady?.Invoke(this);
        }

        /// <summary>
        /// Register a readiness callback that runs immediately if the character is already ready, or exactly once
        /// when it becomes ready. Unlike subscribing to <see cref="OnCharacterReady"/> directly, this never
        /// misses an already-fired readiness (which happens for rehydrated prefabs and live imports).
        /// </summary>
        public void WhenReady(Action<KhrCharacter> callback)
        {
            if (callback == null) return;
            if (IsReady) callback(this);
            else OnCharacterReady += callback;
        }

        // Reused across calls so the per-frame HUD/inspector polling doesn't allocate a fresh report + list each
        // call. The returned report is owned by this component and is overwritten on the next GetHealth() call.
        private CharacterHealthReport _healthReport;

        /// <summary>
        /// Snapshot of which capabilities are active vs present-but-inert, plus the expression count. Drives the
        /// Character Health inspector/HUD. The returned report instance is reused on each call (overwritten on
        /// the next call); copy it to retain a snapshot.
        /// </summary>
        public CharacterHealthReport GetHealth()
        {
            var report = _healthReport ?? (_healthReport = new CharacterHealthReport());
            report.ExpressionCount = ExpressionResponses != null
                ? ExpressionResponses.Count
                : Expressions != null ? Expressions.Count : 0;
            report.Capabilities.Clear();
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
                    return (ExpressionResponses != null || Expressions != null)
                        ? CapabilityStatus.Active
                        : CapabilityStatus.Inert;
                case CharacterCapability.Morphtarget:
                case CharacterCapability.Joint:
                case CharacterCapability.Texture:
                case CharacterCapability.Mask:
                case CharacterCapability.Mapping:
                    return Expressions != null ? CapabilityStatus.Active : CapabilityStatus.Inert;
                case CharacterCapability.CameraHint:
                    return CameraHints != null ? CapabilityStatus.Active : CapabilityStatus.Inert;
                case CharacterCapability.LookAtTarget:
                    return LookAtTargets != null ? CapabilityStatus.Active : CapabilityStatus.Inert;
                case CharacterCapability.SkeletonMapping:
                    if (Skeleton == null) return CapabilityStatus.Inert;
                    return SkeletonMappingDegraded() ? CapabilityStatus.Degraded : CapabilityStatus.Active;
                case CharacterCapability.ReferencePose:
                    return (Skeleton != null
                            && ((Skeleton.Result?.ReferencePoses != null && Skeleton.Result.ReferencePoses.Length > 0)
                                || Skeleton.Result?.ReferencePose != null))
                        ? CapabilityStatus.Active
                        : CapabilityStatus.Inert;
                default:
                    return CapabilityStatus.Inert;
            }
        }

        // A skeleton mapping is "degraded" (present but only partially driven) when it resolved no bones at all,
        // when generic association resolution was invalid, or when the optional Unity Humanoid adapter's selected
        // mapping lacks a recognized HumanTrait-required role. That adapter-health policy does not make the glTF
        // mapping invalid. Optional Unity roles (jaw/eyes/toes/...) may be absent without degrading the adapter.
        private bool SkeletonMappingDegraded()
        {
            var result = Skeleton.Result;
            if (result?.Bones == null || result.Bones.Count == 0) return true;
            var report = result.Report;
            return report != null && (!report.IsValid || report.MissingRequiredBones.Count > 0);
        }
    }
}
