// Scene-independent authoring mirror of the runtime expression contracts (Contracts/KhrCharacter.Contracts.cs).
// The runtime drivers (MorphDriver/JointDriver/TextureDriver) hold LIVE scene-object references
// (SkinnedMeshRenderer/Transform/Renderer), which Unity cannot serialize into a standalone project asset. These
// "binding" types replace those live references with stable hierarchy paths + names so a
// CharacterExpressionSetAsset is self-contained, re-resolvable back onto a character, and is groundwork for the
// future glTF exporter.
//
// The runtime contracts stay unchanged — they remain the import↔runtime boundary with live refs. Conversion
// between the two lives in CharacterExpressionSetAsset.Extract/Resolve.
//
// Reused portable contract types (already [Serializable]): Sampler, MaskEntry, ExpressionMappingSet, TrsChannel,
// TexKind, ExpressionDomain, ExpressionBlendMode.

using System;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    // Path-based mirror of CharacterExpressionSet. MappingSets are reused as-is (already portable).
    [Serializable]
    public class ExpressionBindingSet
    {
        public ExpressionBinding[] Expressions;
        public ExpressionMappingSet[] MappingSets;
    }

    // Path-based mirror of ExpressionTrack. Metadata (Name/Domains/BlendMode/IsBinary/Masks) is carried verbatim;
    // the driver arrays become path-based bindings.
    [Serializable]
    public class ExpressionBinding
    {
        public string Name;
        public ExpressionDomain Domains;
        public ExpressionBlendMode BlendMode = ExpressionBlendMode.Additive;
        public bool IsBinary;
        public MaskEntry[] Masks;
        public MorphBinding[] MorphBindings;
        public JointBinding[] JointBindings;
        public TextureBinding[] TextureBindings;
    }

    // Mirror of MorphDriver: the live SkinnedMeshRenderer becomes RendererPath (relative to the character root)
    // and the blendshape index becomes its stable name (resolved back to an index on the target mesh).
    [Serializable]
    public class MorphBinding
    {
        public string RendererPath;     // hierarchy path of the SkinnedMeshRenderer's transform, relative to root
        public string BlendShapeName;   // resolved from BlendShapeIndex at extract; re-resolved at Resolve
        public Sampler Sampler;
        public float[] DeltaValues;      // frame-0-relative, raw glTF [0..1] space
        public float BaseValue;          // model default weight in raw [0..1]
        public int Priority;
    }

    // Mirror of JointDriver: the live Transform target becomes TargetPath (relative to the character root). Curve
    // data is carried verbatim (the Quaternion[] rotation curves are why the baked set cannot serialize directly).
    [Serializable]
    public class JointBinding
    {
        public string TargetPath;        // hierarchy path of the target transform, relative to root
        public TrsChannel Channel;
        public Sampler Sampler;
        public Vector3[] DeltaVec;       // Translation/Scale (frame-0-relative); null for Rotation
        public Quaternion[] DeltaQuat;   // Rotation (frame-0-relative delta); null for T/S
        public Vector3 BaseVec;          // model default local position/scale
        public Quaternion BaseQuat;      // model default local rotation
        public int Priority;
    }

    // Mirror of TextureDriver: the live Renderer becomes RendererPath. SwapTextures are project assets and
    // serialize fine. PropertyId (a stable Shader.PropertyToID hash, already persisted in prefabs) is kept as-is;
    // capturing the human-readable property name for glTF export is deferred to the exporter work.
    [Serializable]
    public class TextureBinding
    {
        public string RendererPath;      // hierarchy path of the Renderer's transform, relative to root
        public int SubmeshSlot;          // material index on the renderer
        public int PropertyId;           // resolved per pipeline (Shader.PropertyToID) at bake
        public TexKind Kind;
        public Sampler Sampler;
        public Texture[] SwapTextures;   // IndexSwap: resolved texture per STEP key (project assets)
        public Vector4[] StValues;       // UvTransform: frame-0-relative _ST deltas
        public Vector4 BaseSt;           // UvTransform: the material's base _ST
        public int Priority;
    }
}
