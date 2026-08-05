// Shared data types for KHR Character/Avatar extension support. These are produced by the import-time
// baker (the import/library layer) and consumed by the runtime components (ExpressionController, SkeletonMap,
// GazeSolver, ...). They form the boundary between import and runtime, so a change here affects both sides.
//
// Notes:
//  • Expression tracks are baked as deltas over the animation's frame-0 value (additive model).
//  • Morph base + delta are stored in raw glTF [0..1] (before the BlendShapeFrameWeight multiplier).
//  • Interp is a 3-valued enum (Step/Linear/CubicSpline).
//  • JointDriver and TextureDriver carry a priority used for override-blend resolution.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    // ── Enums ────────────────────────────────────────────────────────────────
    public enum Interp { Step, Linear, CubicSpline }

    public enum ExpressionBlendMode { Additive, Override }

    [Flags]
    public enum ExpressionDomain { None = 0, Morph = 1, Joint = 2, Texture = 4 }

    public enum TrsChannel { Translation, Rotation, Scale }

    public enum MaskType { Blend, Block, Identity }

    public enum CharacterCapability
    {
        Character, Expression, Morphtarget, Joint, Texture, Mapping, Mask,
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
        public int Priority;         // tie-break for override blend mode
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
        public int Priority;
    }

    [Serializable]
    public class TextureDriver
    {
        public Renderer Renderer;
        public int SubmeshSlot;          // material index on the renderer
        public int PropertyId;           // resolved per pipeline (Shader.PropertyToID) at bake
        public string PropertyName;      // human-readable shader property name (e.g., "_BaseMap") — required for export
        public string GltfTextureSlot;   // glTF texture slot name (e.g., "baseColorTexture") — required for export
        public Sampler Sampler;
        public Vector4[] StValues;       // Frame-0-relative _ST (tiling.xy, offset.zw) deltas
        public Vector4 BaseSt;           // The material's base _ST (runtime rest anchor)
        public Vector4 Frame0St;         // Animation frame-0 absolute _ST; multi-key export anchor when HasFrame0St
        public bool HasFrame0St;         // true once import captured the authored frame-0 absolute; else export anchors on BaseSt
        public int Priority;             // same-slot conflict resolution
    }

    [Serializable]
    public class MaskEntry
    {
        public int TargetIndex;          // expression index this mask attenuates
        public MaskType Type;            // Custom types without supported companion semantics use Identity
        public string CustomType;        // Preserved application-defined type; null for blend/block
        public string RawExtensionsJson; // Preserved same-object companion/unrelated extension payloads
        public string RawExtrasJson;     // Preserved mask extras payload
        public float Amount;             // [0..1], default 1
        public float Threshold;          // [0..1], Block only, default 0
        public int SourceIndex;          // owning expression by default; explicit if the schema allows it
    }

    // ── Expression + set ─────────────────────────────────────────────────────
    [Serializable]
    public class ExpressionTrack
    {
        public string Name;
        public ExpressionDomain Domains;     // set only for the sub-extensions present
        public ExpressionBlendMode BlendMode = ExpressionBlendMode.Additive;
        public bool IsBinary;                // every morph/joint/texture channel is STEP (>=1 driver); UI uses a 0/1-snapping control
        public MorphDriver[] MorphDrivers;
        public JointDriver[] JointDrivers;
        public TextureDriver[] TextureDrivers;
        public MaskEntry[] Masks;
    }

    [Serializable]
    public struct MappingContribution { public int SourceIndex; public float Weight; }

    [Serializable]
    public class MappingTarget { public string TargetName; public MappingContribution[] Contributions; }

    [Serializable]
    public class ExpressionMappingSet { public string SetName; public MappingTarget[] Targets; }

    [Serializable]
    public struct InputMappingContribution { public int TargetIndex; public float Weight; }

    [Serializable]
    public class InputMappingCommand { public string CommandName; public InputMappingContribution[] Contributions; }

    [Serializable]
    public class ExpressionInputMappingSet { public string SetName; public InputMappingCommand[] Commands; }

    [Serializable]
    public class CharacterExpressionSet
    {
        public ExpressionTrack[] Expressions;
        public ExpressionMappingSet[] MappingSets;           // native drivers -> endpoint outputs
        public ExpressionInputMappingSet[] InputMappingSets; // endpoint commands -> native drivers

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
    [Serializable]
    public class ReferencePose
    {
        public int AnimationIndex;
        public string PoseType;          // "TPose"/"APose"/... (retarget pose; not the neutral/bind pose)
        public Transform[] Bones;
        public Vector3[] LocalPositions;
        public Quaternion[] LocalRotations;
        public Vector3[] LocalScales;
    }

    [Serializable]
    public class SkeletonMappingSetResult
    {
        public string Identifier;
        public Dictionary<string, Transform> Associations;
        public ValidationReport Report = new ValidationReport();
    }

    [Serializable]
    public class ValidationReport
    {
        public bool IsValid = true;
        public List<string> Warnings = new List<string>();
        public List<string> MissingRequiredBones = new List<string>();
    }

    public class SkeletonMappingResult
    {
        public SkeletonMappingSetResult[] MappingSets;
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
    public class SerializableSkeletonMappingSet
    {
        public string Identifier;
        public SerializableBoneEntry[] Associations;
        public ValidationReport Report = new ValidationReport();
    }

    [Serializable]
    public class SerializableSkeletonMapping
    {
        public SerializableBoneEntry[] Bones;
        public string SelectedRig;
        public ReferencePose ReferencePose;   // already [Serializable]
        public SerializableSkeletonMappingSet[] MappingSets;
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
                            Report = set.Report ?? new ValidationReport(),
                        });
            return new SerializableSkeletonMapping
            {
                Bones = entries,
                SelectedRig = result.SelectedRig,
                ReferencePose = result.ReferencePose,
                MappingSets = mappingSets.ToArray(),
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
                            Report = set.Report ?? new ValidationReport(),
                        });
            return new SkeletonMappingResult
            {
                Bones = bones,
                SelectedRig = SelectedRig,
                ReferencePose = ReferencePose,
                MappingSets = mappingSets.ToArray(),
                ReferencePoses = ReferencePoses,
                Report = Report ?? new ValidationReport(),
            };
        }

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
    }

    // ── Node-extension metadata ──────────────────────────────────────────────
    [Serializable]
    public class CameraHint
    {
        public string Role;
        public string Label;
        public Transform Node;
        public Camera Projection;   // optional referenced camera (FOV/clip)
        public Transform Target;    // optional targetNode
    }

    [Serializable]
    public class LookAtTarget { public Transform Node; public string Hint; }

    // ── Evaluator policy implemented by the runtime ExpressionController ──────
    public interface IExpressionSemantics
    {
        float Clamp01(float v);

        // Sample a baked delta track at normalized phase d (input-time mapped). Results are frame-0-relative.
        float SampleScalarDelta(Sampler s, float[] deltaValues, float baseValue, float d);
        Vector3 SampleVectorDelta(Sampler s, Vector3[] deltaVec, Vector3 baseVec, float d);
        Quaternion SampleRotationDelta(Sampler s, Quaternion[] deltaQuat, Quaternion baseQuat, float d);
        Vector4 SampleVector4Delta(Sampler s, Vector4[] deltaVec, Vector4 baseVec, float d);

        // Mask: returns the masked input for a target track given raw inputs (blend/block, commutative).
        float ResolveMaskedInput(int trackIndex, IReadOnlyList<float> rawInputs, ExpressionTrack[] tracks);

        // Mapping operations are explicitly directed and never inferred from one another.
        void ApplyInputMapping(ExpressionInputMappingSet set,
                               IReadOnlyDictionary<string, float> commandInputs,
                               float[] nativeOutputs);
        IReadOnlyDictionary<string, float> EvaluateForwardMapping(ExpressionMappingSet set,
                                                                  IReadOnlyList<float> nativeInputs);

        // Commutative rotation accumulation. Returns the new accumulated delta.
        Quaternion AccumulateRotation(Quaternion accumulatedDelta, Quaternion deltaToAdd, float weight);
    }
}
