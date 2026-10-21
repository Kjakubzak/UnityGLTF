// Shared data types for KHR Character/Avatar extension support. These are produced by the import-time
// baker (the import/library layer) and consumed by the runtime components (ExpressionController, SkeletonMap,
// GazeSolver, ...). They form the boundary between import and runtime, so a change here affects both sides.
//
// Notes:
//  • Expression tracks are baked as deltas over the animation's frame-0 value (additive model).
//  • Morph base + delta are stored in raw glTF [0..1] (before the BlendShapeFrameWeight multiplier).
//  • Interp is a 3-valued enum (Step/Linear/CubicSpline).

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace UnityGLTF.KhrCharacter
{
    // ── Enums ────────────────────────────────────────────────────────────────
    public enum Interp { Step, Linear, CubicSpline }

    [Flags]
    public enum ExpressionDomain { None = 0, Morph = 1, Joint = 2, Texture = 4 }

    public enum TrsChannel { Translation, Rotation, Scale }

    public enum TextureTransformTarget { Combined, Scale, Offset }

    /// <summary>
    /// Suppression factor kind. <see cref="Identity"/> is a custom type without supported companion semantics,
    /// which contributes a factor of 1.
    /// </summary>
    public enum SuppressionType { Blend, Block, Identity }

    // Persisted as integers: append new members, never reorder or remove.
    public enum CharacterCapability
    {
        Character, Expression, Morphtarget, Joint, TextureTransform, Mapping, Suppression,
        ReferencePose, SkeletonMapping, CameraHint, LookAtTarget
    }

    /// <summary>How the importer treats the character's rig (mirrors FBX-style "Rig" import settings).</summary>
    public enum RigImportMode
    {
        /// <summary>Build + assign a Mecanim humanoid Avatar when the skeleton mapping resolves the required bones.</summary>
        Humanoid,
        /// <summary>Keep the generic rig; never build a humanoid Avatar.</summary>
        Generic,
    }

    // ── Sampler (shared by all driver kinds) ─────────────────────────────────
    [Serializable]
    public struct Sampler
    {
        public float[] Times;     // input accessor times (seconds), length N
        public Interp Interp;
        public bool SingleKey;    // N==1 -> use the rest->target rule instead of frame-0 subtraction
    }

    // ── Per-target drivers (one per concrete Unity target) ───────────────────
    [Serializable]
    public class MorphDriver
    {
        public SkinnedMeshRenderer Smr;
        public int BlendShapeIndex;
        public Sampler Sampler;
        public float[] DeltaValues;  // frame-0-relative, raw glTF [0..1] space (length == Times.Length)
        public float BaseValue;      // model default weight in raw [0..1]
    }

    [Serializable]
    public class JointDriver
    {
        public Transform Target;
        public TrsChannel Channel;
        public Sampler Sampler;
        public Vector3[] DeltaVec;       // Translation/Scale (frame-0-relative); null for Rotation
        public Quaternion[] DeltaQuat;   // Rotation (frame-0-relative delta); null for T/S
        public Vector3 BaseVec;          // model default local position/scale
        public Quaternion BaseQuat;      // model default local rotation
    }

    [Serializable]
    public class TextureDriver
    {
        public Renderer Renderer;
        public int SubmeshSlot;          // material index on the renderer
        public int PropertyId;           // resolved per pipeline (Shader.PropertyToID) at bake
        public string PropertyName;      // human-readable shader property name (e.g., "_BaseMap") — required for export
        public string GltfTextureSlot;   // glTF material slot path (e.g., "pbrMetallicRoughness/baseColorTexture")
        public TextureTransformTarget TransformTarget; // Imported scale/offset channels retain independent samplers
        public Sampler Sampler;
        public Vector4[] StValues;       // Frame-0-relative _ST (tiling.xy, offset.zw) deltas
        public Vector4 BaseSt;           // The material's base _ST (runtime rest anchor)
        public Vector4 Frame0St;         // Legacy/provenance copy of the imported animation's frame-0 absolute _ST
        public bool HasFrame0St;         // true when Frame0St was captured; export always anchors responses on BaseSt
    }

    /// <summary>
    /// One <c>KHR_character_expression_suppression</c> rule. <see cref="Amount"/> and <see cref="Threshold"/>
    /// carry the authored JSON doubles; the presence flags record whether each was authored. Export writes a value
    /// when its flag is set or when it differs from its default, so a value omitted on import stays omitted and a
    /// non-default value without its flag (pre-rename serialized data, inspector or code edits) is not dropped.
    /// </summary>
    [Serializable]
    public class SuppressionEntry
    {
        public int TargetIndex;          // expression index this suppression attenuates
        public SuppressionType Type;     // Custom types without supported companion semantics use Identity
        public string CustomType;        // Preserved application-defined type; null for blend/block
        public string RawExtensionsJson; // Preserved same-object companion/unrelated extension payloads
        public string RawExtrasJson;     // Preserved suppression extras payload
        public string RawAdditionalPropertiesJson;

        /// <summary>Attenuation amount in [0, 1]; reads as 1 when not authored.</summary>
        public double Amount = 1;

        /// <summary>Whether <see cref="Amount"/> was authored.</summary>
        public bool HasAmount;

        /// <summary>Strict block threshold in [0, 1]; reads as 0 when not authored.</summary>
        public double Threshold;

        /// <summary>Whether <see cref="Threshold"/> was authored.</summary>
        public bool HasThreshold;

        public int SourceIndex;          // the expression whose entry contains this suppression
    }

    // ── Expression + set ─────────────────────────────────────────────────────
    [Serializable]
    public class ExpressionTrack
    {
        public string Name;
        public ExpressionDomain Domains;     // set only for the sub-extensions present
        public bool IsBinary;                // explicit host/UI snapping metadata; never inferred from STEP interpolation
        public MorphDriver[] MorphDrivers;
        public JointDriver[] JointDrivers;
        public TextureDriver[] TextureDrivers;
        [FormerlySerializedAs("Masks")] public SuppressionEntry[] Suppressions;
        [FormerlySerializedAs("MaskExtensionsJson")] public string SuppressionExtensionsJson;
        [FormerlySerializedAs("MaskExtrasJson")] public string SuppressionExtrasJson;
        [FormerlySerializedAs("MaskAdditionalPropertiesJson")] public string SuppressionAdditionalPropertiesJson;
        [FormerlySerializedAs("MaskRequiredCompanionExtensions")] public string[] SuppressionRequiredCompanionExtensions;
    }

    [Serializable]
    public struct MappingContribution
    {
        public int SourceIndex; // Native expression driven by this external input term (wire source).
        public string Name;
        public double Weight;
        public string ExtensionsJson;
        public string ExtrasJson;
        public string AdditionalPropertiesJson;
    }

    [Serializable]
    /// <summary>One external input key. TargetName is retained for authored asset compatibility.</summary>
    public class MappingTarget { public string TargetName; public MappingContribution[] Contributions; }

    [Serializable]
    public class ExpressionMappingSet { public string SetName; public MappingTarget[] Targets; }

    [Serializable]
    public struct InputMappingContribution
    {
        public int TargetIndex;
        public string Name;
        public double Weight;
        public string ExtensionsJson;
        public string ExtrasJson;
        public string AdditionalPropertiesJson;
    }

    [Serializable]
    public class InputMappingCommand { public string CommandName; public InputMappingContribution[] Contributions; }

    [Serializable]
    public class ExpressionInputMappingSet { public string SetName; public InputMappingCommand[] Commands; }

    [Serializable]
    public class CharacterExpressionSet
    {
        public ExpressionTrack[] Expressions;
        public ExpressionMappingSet[] MappingSets;           // Current external input keys -> native drivers
        public ExpressionInputMappingSet[] InputMappingSets; // Legacy authored input table; migrate with FromLegacyInputMappingSets
        public string MappingExtensionsJson;
        public string MappingExtrasJson;
        public string MappingAdditionalPropertiesJson;
        public string[] MappingRequiredCompanionExtensions;

        // Rebuilt at runtime from Expressions (not serialized).
        [NonSerialized] public Dictionary<string, int> NameToIndex;

        public void RebuildIndex()
        {
            NameToIndex = new Dictionary<string, int>();
            if (Expressions == null) return;
            var duplicateNames = new HashSet<string>();
            for (int i = 0; i < Expressions.Length; i++)
            {
                var track = Expressions[i];
                if (track?.Name == null) continue;
                if (duplicateNames.Contains(track.Name)) continue;
                if (NameToIndex.ContainsKey(track.Name))
                {
                    NameToIndex.Remove(track.Name);
                    duplicateNames.Add(track.Name);
                    Debug.LogWarning($"[KHR_character] Duplicate expression label '{track.Name}'; use its authoritative array index.");
                    continue;
                }
                NameToIndex.Add(track.Name, i);
            }
        }
    }

    // ── Skeleton / reference pose ────────────────────────────────────────────
    /// <summary>
    /// One animation tagged with <c>KHR_character_reference_pose</c>. Values are local transforms in Unity
    /// coordinates and in the imported scene's units: translations include the scale factor that the editor importer
    /// applies to node positions. <see cref="Bones"/> and the <c>Local*</c> arrays cover the instantiated nodes the
    /// animation targets, which is what export writes. The <c>Resolved*</c> arrays cover every node of the asset:
    /// targeted TRS properties come from the animation and everything else from the node's authored <c>matrix</c> or
    /// TRS with the core defaults, never from live transforms. An invalid tagged animation keeps its index, label and
    /// <see cref="Errors"/> but carries no values.
    /// </summary>
    [Serializable]
    public class ReferencePose
    {
        public int AnimationIndex;
        public string PoseType = ReferencePoseLabels.TPose; // exact required poseType label, e.g. ReferencePoseLabels.TPose (retarget pose; not the neutral/bind pose)
        public Transform[] Bones;
        public Vector3[] LocalPositions;
        public Quaternion[] LocalRotations;
        public Vector3[] LocalScales;

        /// <summary>Whether the tagged animation satisfies the reference-pose validity rules.</summary>
        public bool IsValid = true;

        /// <summary>Validity errors; empty for a valid pose.</summary>
        public string[] Errors;

        /// <summary>glTF node indices of the resolved entries; every node in the asset.</summary>
        public int[] ResolvedNodeIndices;

        /// <summary>Instantiated transform per resolved entry, or null for a node outside the imported scene.</summary>
        public Transform[] ResolvedNodes;

        public Vector3[] ResolvedLocalPositions;
        public Quaternion[] ResolvedLocalRotations;
        public Vector3[] ResolvedLocalScales;

        /// <summary>glTF parent node index per resolved entry, or -1 for a root node.</summary>
        public int[] ParentIndices;

        /// <summary>
        /// Per resolved entry: UnityGLTF rotated the node's GameObject 180° about Y because it carries a core camera
        /// or punctual light. The resolved rotation excludes that turn; applying a pose to the GameObject adds it.
        /// </summary>
        public bool[] ResolvedNodeHasForwardAxisConversion;

        /// <summary>True when the pose carries resolved values for the asset's nodes.</summary>
        public bool HasResolvedValues => ResolvedNodeIndices != null && ResolvedNodeIndices.Length > 0;
    }

    [Serializable]
    public class SkeletonMappingSetResult
    {
        public string Identifier;

        /// <summary>Role → transform for associations whose node was instantiated.</summary>
        public Dictionary<string, Transform> Associations;

        /// <summary>Role → glTF node index for every association, instantiated or not.</summary>
        public Dictionary<string, int> NodeIndices = new Dictionary<string, int>();
        public Dictionary<string, string> AssociationNames = new Dictionary<string, string>();
        public Dictionary<string, string> AssociationPayloads = new Dictionary<string, string>();

        public ValidationReport Report = new ValidationReport();
    }

    [Serializable]
    public class ValidationReport
    {
        public bool IsValid = true;
        public List<string> Warnings = new List<string>();
        // Optional host-adapter health detail; entries here do not by themselves imply invalid glTF data.
        public List<string> MissingRequiredBones = new List<string>();
    }

    public class SkeletonMappingResult
    {
        public SkeletonMappingSetResult[] MappingSets;
        public string RawExtensionJson;
        public ReferencePose[] ReferencePoses;
        public Dictionary<string, Transform> Bones;   // vocab joint name -> resolved Transform
        public string SelectedRig;                    // optional host-adapter selection
        public ReferencePose ReferencePose;           // optional host-adapter selection
        public ValidationReport Report = new ValidationReport();
    }

    // Serializable mirror of SkeletonMappingResult: Unity can't serialize the Dictionary<string,Transform>,
    // so persist the bone map as a parallel entry array. Used by SkeletonMap to survive prefab deserialize.
    // Converted to/from the runtime SkeletonMappingResult via FromResult/ToResult around the public API.
    [Serializable]
    public struct SerializableBoneEntry
    {
        public string JointName;   // vocab joint name (hips/head/...)
        public Transform Bone;     // resolved Transform (intra-hierarchy refs survive prefab serialization)
    }

    [Serializable]
    public struct SerializableNodeIndexEntry
    {
        public string Role;
        public int Node;
    }

    [Serializable]
    public struct SerializableAssociationMetadata
    {
        public string Role;
        public string Name;
        public string Payload;
    }

    [Serializable]
    public class SerializableSkeletonMappingSet
    {
        public string Identifier;
        public SerializableBoneEntry[] Associations;
        public SerializableNodeIndexEntry[] NodeIndices;
        public SerializableAssociationMetadata[] AssociationMetadata;
        public ValidationReport Report = new ValidationReport();
    }

    [Serializable]
    public class SerializableSkeletonMapping
    {
        public SerializableBoneEntry[] Bones;
        public string SelectedRig;
        public ReferencePose ReferencePose;   // already [Serializable]
        public SerializableSkeletonMappingSet[] MappingSets;
        public string RawExtensionJson;
        public ReferencePose[] ReferencePoses;
        public ValidationReport Report = new ValidationReport();   // already [Serializable]

        public static SerializableSkeletonMapping FromResult(SkeletonMappingResult result)
        {
            if (result == null) return null;
            var entries = ToEntries(result.Bones);
            var mappingSets = new List<SerializableSkeletonMappingSet>();
            if (result.MappingSets != null)
                foreach (var set in result.MappingSets)
                    if (set != null)
                        mappingSets.Add(new SerializableSkeletonMappingSet
                        {
                            Identifier = set.Identifier,
                            Associations = ToEntries(set.Associations),
                            NodeIndices = ToNodeIndexEntries(set.NodeIndices),
                            AssociationMetadata = ToAssociationMetadata(set),
                            Report = set.Report ?? new ValidationReport(),
                        });
            return new SerializableSkeletonMapping
            {
                Bones = entries,
                SelectedRig = result.SelectedRig,
                ReferencePose = result.ReferencePose,
                MappingSets = mappingSets.ToArray(),
                RawExtensionJson = result.RawExtensionJson,
                ReferencePoses = result.ReferencePoses,
                Report = result.Report ?? new ValidationReport(),
            };
        }

        public SkeletonMappingResult ToResult()
        {
            var bones = FromEntries(Bones);
            var mappingSets = new List<SkeletonMappingSetResult>();
            if (MappingSets != null)
                foreach (var set in MappingSets)
                    if (set != null)
                        mappingSets.Add(new SkeletonMappingSetResult
                        {
                            Identifier = set.Identifier,
                            Associations = FromEntries(set.Associations),
                            NodeIndices = FromNodeIndexEntries(set.NodeIndices),
                            AssociationNames = FromAssociationMetadata(set.AssociationMetadata, false),
                            AssociationPayloads = FromAssociationMetadata(set.AssociationMetadata, true),
                            Report = set.Report ?? new ValidationReport(),
                        });
            return new SkeletonMappingResult
            {
                Bones = bones,
                SelectedRig = SelectedRig,
                ReferencePose = NullIfEmpty(ReferencePose),
                MappingSets = mappingSets.ToArray(),
                RawExtensionJson = RawExtensionJson,
                ReferencePoses = ReferencePoses,
                Report = Report ?? new ValidationReport(),
            };
        }

        private static SerializableAssociationMetadata[] ToAssociationMetadata(SkeletonMappingSetResult set)
        {
            var entries = new List<SerializableAssociationMetadata>();
            if (set.AssociationNames != null)
                foreach (var item in set.AssociationNames)
                {
                    string payload = null;
                    set.AssociationPayloads?.TryGetValue(item.Key, out payload);
                    entries.Add(new SerializableAssociationMetadata { Role = item.Key, Name = item.Value, Payload = payload });
                }
            return entries.ToArray();
        }

        private static Dictionary<string, string> FromAssociationMetadata(SerializableAssociationMetadata[] entries, bool payload)
        {
            var result = new Dictionary<string, string>();
            if (entries != null)
                foreach (var entry in entries)
                    if (entry.Role != null) result[entry.Role] = payload ? entry.Payload : entry.Name;
            return result;
        }

        // Unity rehydrates a null ReferencePose as an empty instance, which must not be listed as a tagged animation.
        private static ReferencePose NullIfEmpty(ReferencePose pose)
            => pose == null
               || ((pose.Bones == null || pose.Bones.Length == 0)
                   && !pose.HasResolvedValues
                   && (pose.Errors == null || pose.Errors.Length == 0))
                ? null
                : pose;

        private static SerializableBoneEntry[] ToEntries(Dictionary<string, Transform> associations)
        {
            var entries = new List<SerializableBoneEntry>();
            if (associations != null)
                foreach (var kv in associations)
                    if (!string.IsNullOrEmpty(kv.Key))
                        entries.Add(new SerializableBoneEntry { JointName = kv.Key, Bone = kv.Value });
            return entries.ToArray();
        }

        private static Dictionary<string, Transform> FromEntries(SerializableBoneEntry[] entries)
        {
            var associations = new Dictionary<string, Transform>();
            if (entries != null)
                foreach (var entry in entries)
                    if (!string.IsNullOrEmpty(entry.JointName)) associations[entry.JointName] = entry.Bone;
            return associations;
        }

        private static SerializableNodeIndexEntry[] ToNodeIndexEntries(Dictionary<string, int> nodeIndices)
        {
            var entries = new List<SerializableNodeIndexEntry>();
            if (nodeIndices != null)
                foreach (var kv in nodeIndices)
                    if (!string.IsNullOrEmpty(kv.Key))
                        entries.Add(new SerializableNodeIndexEntry { Role = kv.Key, Node = kv.Value });
            return entries.ToArray();
        }

        private static Dictionary<string, int> FromNodeIndexEntries(SerializableNodeIndexEntry[] entries)
        {
            var nodeIndices = new Dictionary<string, int>();
            if (entries != null)
                foreach (var entry in entries)
                    if (!string.IsNullOrEmpty(entry.Role)) nodeIndices[entry.Role] = entry.Node;
            return nodeIndices;
        }
    }

    // ── Node-extension metadata ──────────────────────────────────────────────
    [Serializable]
    public class CameraHint
    {
        public string Role;
        public string Label;
        public Transform Node;

        /// <summary>Index of the hint node in the glTF <c>nodes</c> array, or -1 when the hint was not imported.</summary>
        public int NodeIndex = -1;

        /// <summary>
        /// True when the hint was imported from a glTF node without a nonempty <c>name</c>. UnityGLTF names such a
        /// node's GameObject <c>GLTFNode&lt;index&gt;</c>, which is not a node name, so presentation skips it.
        /// </summary>
        public bool NodeUnnamed;

        public Camera Projection;   // optional host-bound representation of the advisory camera definition
        public Transform Target;    // optional targetNode; set only when it names a different node that resolves
        [UnityEngine.Serialization.FormerlySerializedAs("NodeUsesUnityCameraOrientation")]
        public bool NodeTransformHasForwardAxisConversion;
        public string ExtensionsJson;
        public string ExtrasJson;
        public string AdditionalPropertiesJson;
        public string[] RequiredCompanionExtensions;
    }

    /// <summary>
    /// A glTF node defined by <c>matrix</c> on a camera hint's path, with its authored local matrix in Unity
    /// coordinates. Animation cannot target a matrix node, so the authored matrix is also its evaluation state.
    /// </summary>
    [Serializable]
    public class AuthoredNodeMatrix
    {
        public Transform Node;
        public Matrix4x4 LocalMatrix;

        /// <summary>The importer turned the node's GameObject 180° about Y for a core camera or punctual light.</summary>
        public bool ForwardAxisConversion;
    }

    [Serializable]
    public class LookAtTarget
    {
        public Transform Node;
        public string Hint;
        public string ExtensionsJson;
        public string ExtrasJson;
        public string AdditionalPropertiesJson;
        public string[] RequiredCompanionExtensions;
    }
}
