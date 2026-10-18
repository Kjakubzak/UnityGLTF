using System;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// The Unity scene objects that one composed expression property writes, with the property's authored initial value
    /// <c>b</c> in glTF space. <see cref="ExpressionController"/> applies the aggregate record of <see cref="Property"/>
    /// through it and, in Standalone mode, writes <see cref="AuthoredInitial"/> while the property has no record.
    /// </summary>
    [Serializable]
    public class ExpressionTargetBinding
    {
        /// <summary>
        /// The canonical property: <c>/nodes/3/translation</c>, <c>/nodes/2/weights</c>, a <c>KHR_texture_transform</c>
        /// pointer, or an <c>authored:&lt;instanceId&gt;/...</c> key for a set authored in Unity.
        /// </summary>
        public string Property;

        /// <summary>The concrete property type.</summary>
        public ExpressionPropertyKind Kind = ExpressionPropertyKind.Other;

        /// <summary>The authored initial value in glTF space; the complete vector B for morph weights.</summary>
        public double[] AuthoredInitial;

        /// <summary>The node whose translation, rotation or scale the property is.</summary>
        public Transform Node;

        /// <summary>
        /// UnityGLTF turned the node's GameObject 180° about Y because it carries a core camera or punctual light, so a
        /// written rotation keeps that turn.
        /// </summary>
        public bool NodeHasForwardAxisConversion;

        /// <summary>The import scale factor applied to node translations; 1 outside editor imports.</summary>
        public float TranslationScale = 1f;

        /// <summary>The renderers that show the node's morph targets.</summary>
        public SkinnedMeshRenderer[] MorphRenderers;

        /// <summary>
        /// The Unity blendshape frame weight of renderer <c>r</c> and morph target <c>j</c> at <c>r * N + j</c>, where N is
        /// the length of <see cref="AuthoredInitial"/>. A glTF weight <c>w</c> is written as <c>w * frameWeight</c>.
        /// </summary>
        public float[] MorphFrameWeights;

        /// <summary>
        /// The <c>KHR_texture_transform</c> object the property belongs to. The offset, scale and rotation bindings of one
        /// object share it and are written together.
        /// </summary>
        public string TextureTransform;

        /// <summary>The renderers whose materials use the texture.</summary>
        public Renderer[] TextureRenderers;

        /// <summary>The material slot of each renderer in <see cref="TextureRenderers"/>.</summary>
        public int[] TextureMaterialSlots;

        /// <summary>The Unity texture scale-and-offset property, such as <c>baseColorTexture_ST</c>.</summary>
        public string TextureStProperty;

        /// <summary>The Unity texture rotation property, such as <c>baseColorTextureRotation</c>; null when the material has none.</summary>
        public string TextureRotationProperty;

        /// <summary>The importer flips the texture vertically (as for <c>KHR_texture_basisu</c>), which mirrors V in <c>_ST</c>.</summary>
        public bool TextureFlipped;
    }
}
