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

    public enum TexKind { IndexSwap, UvTransform }

    public enum MaskType { Blend, Block }

    public enum MappingDirection { Unknown, TargetKeyToNodeValue, NodeKeyToTargetValue }

    public enum CharacterCapability
    {
        Character, Expression, Morphtarget, Joint, Texture, Mapping, Mask,
        ReferencePose, SkeletonMapping, CameraHint, LookAtTarget
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
        public TexKind Kind;
        public Sampler Sampler;
        public Texture[] SwapTextures;   // IndexSwap: resolved texture per STEP key
        public Vector4[] StValues;       // UvTransform: frame-0-relative _ST (tiling.xy, offset.zw) deltas
        public Vector4 BaseSt;           // UvTransform: the material's base _ST
        public int Priority;             // same-slot conflict resolution
    }

    [Serializable]
    public class MaskEntry
    {
        public int TargetIndex;          // expression index this mask attenuates
        public MaskType Type;            // Blend | Block (unknown spelling -> Blend)
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
        public bool IsBinary;                // all driver channels are STEP (UI + fast path)
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
    public class CharacterExpressionSet
    {
        public ExpressionTrack[] Expressions;
        public ExpressionMappingSet[] MappingSets;

        // Rebuilt at runtime from Expressions (not serialized).
        [NonSerialized] public Dictionary<string, int> NameToIndex;

        public void RebuildIndex()
        {
            NameToIndex = new Dictionary<string, int>();
            if (Expressions == null) return;
            for (int i = 0; i < Expressions.Length; i++)
            {
                var track = Expressions[i];
                if (track?.Name == null) continue;
                if (NameToIndex.ContainsKey(track.Name))
                    Debug.LogWarning($"[KHR_character] Duplicate expression name '{track.Name}'; the earlier track becomes unreachable by name.");
                NameToIndex[track.Name] = i;
            }
        }
    }

    // ── Skeleton / reference pose ────────────────────────────────────────────
    [Serializable]
    public class ReferencePose
    {
        public string PoseType;          // "TPose"/"APose"/... (retarget pose; not the neutral/bind pose)
        public Transform[] Bones;
        public Vector3[] LocalPositions;
        public Quaternion[] LocalRotations;
        public Vector3[] LocalScales;
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
        public Dictionary<string, Transform> Bones;   // vocab joint name -> resolved Transform
        public string SelectedRig;                    // e.g. "vrmHumanoid" / "unityHumanoid"
        public ReferencePose ReferencePose;
        public MappingDirection Direction;            // resolved when the rig is consumed
        public ValidationReport Report = new ValidationReport();
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

        // STEP keyframe index at normalized phase d (used for discrete texture-index swaps).
        int SampleStepIndex(Sampler s, float d);

        // Mask: returns the masked input for a target track given raw inputs (blend/block, commutative).
        float ResolveMaskedInput(int trackIndex, IReadOnlyList<float> rawInputs, ExpressionTrack[] tracks);

        // Mapping: distribute an endpoint target value onto model-source inputs.
        void DistributeMapping(ExpressionMappingSet set, string targetName, float value,
                               float[] sourceInputs, IReadOnlyDictionary<string, int> nameToIndex);

        // Commutative rotation accumulation. Returns the new accumulated delta.
        Quaternion AccumulateRotation(Quaternion accumulatedDelta, Quaternion deltaToAdd, float weight);
    }
}
