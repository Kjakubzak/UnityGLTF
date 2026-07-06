using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Provides a single shared, fully-transparent material used to realize a hidden mesh primitive by material
    /// swap. This material is <b>Unity's representation of hidden-primitive data</b> — it is not a normative glTF
    /// runtime behaviour, and its exact appearance is render-pipeline dependent (the extension only carries a
    /// visibility <c>role</c>, not a material). Export never reads live material state; it emits the authored role
    /// from the serialized hint entries, so a slot that is currently swapped to this material still round-trips
    /// correctly.
    /// </summary>
    public static class InvisibleMaterialCache
    {
        private static Material _invisible;

        /// <summary>Returns the shared invisible material, creating it on first use.</summary>
        public static Material Get()
        {
            if (_invisible != null) return _invisible;
            _invisible = CreateInvisibleMaterial();
            return _invisible;
        }

        private static Material CreateInvisibleMaterial()
        {
            // Pick whatever transparency-capable shader the active pipeline provides; the material is a
            // best-effort representation, so we degrade gracefully across pipelines.
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Transparent")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Sprites/Default");

            var mat = new Material(shader != null ? shader : Shader.Find("Hidden/InternalErrorShader"))
            {
                name = "VisibilityHints_Invisible",
                hideFlags = HideFlags.HideAndDontSave,
            };

            // Fully-transparent tint on whichever color property the shader exposes.
            var clear = new Color(0f, 0f, 0f, 0f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", clear);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", clear);

            // Built-in Standard transparent setup (no-ops on shaders lacking these properties/keywords).
            if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 3f); // Transparent
            if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            // URP surface-type = transparent.
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            return mat;
        }
    }
}
