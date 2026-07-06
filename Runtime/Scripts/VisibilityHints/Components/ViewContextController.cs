using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Generalized first/third-person view-context switch. Renderers registered from
    /// <c>KHR_node_visibility_hint</c> are toggled via <see cref="Renderer.enabled"/>; primitive slots registered
    /// from <c>KHR_mesh_primitive_visibility_hint</c> are realized by swapping a single sub-mesh material to a cached
    /// invisible material and back (a single <see cref="Renderer"/> cannot hide one sub-mesh via
    /// <see cref="Renderer.enabled"/>).
    ///
    /// <para>Composition with core <c>KHR_node_visibility</c>: that extension maps to
    /// <see cref="GameObject.SetActive(bool)"/>, so an inactive node never renders regardless of these hints. The
    /// effective visibility is therefore the logical AND of core node visibility and the hint's view-role — this
    /// controller only manages <see cref="Renderer.enabled"/> / material slots of otherwise-active objects.</para>
    ///
    /// <para>This is a standalone, generalized equivalent of the KhrCharacter <c>ViewModeController</c>; the
    /// per-primitive material swap is framed as Unity's representation of the hint data, not a normative runtime
    /// behaviour. A future consolidation could let KhrCharacter delegate to this controller.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public class ViewContextController : MonoBehaviour
    {
        public enum ViewContext { ThirdPerson, FirstPerson }
        public enum ViewRole { Both, FirstPersonOnly, ThirdPersonOnly }

        [SerializeField] private ViewContext _mode = ViewContext.ThirdPerson;

        /// <summary>Raised after <see cref="Mode"/> changes and the new visibility has been applied.</summary>
        public event Action<ViewContext> OnViewContextChanged;

        private readonly List<(Renderer renderer, ViewRole role)> _renderers = new List<(Renderer, ViewRole)>();
        private readonly List<PrimitiveSlot> _primitiveSlots = new List<PrimitiveSlot>();

        private struct PrimitiveSlot
        {
            public Renderer Renderer;
            public int SubMesh;
            public Material Original;
            public Material Invisible;
            public ViewRole Role;
        }

        public ViewContext Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                ApplyAll();
                OnViewContextChanged?.Invoke(_mode);
            }
        }

        /// <summary>Register a whole renderer (node hint). Its <see cref="Renderer.enabled"/> follows the role.</summary>
        public void RegisterRenderer(Renderer renderer, ViewRole role)
        {
            if (renderer == null) return;
            _renderers.Add((renderer, role));
            renderer.enabled = IsVisible(role, _mode);
        }

        /// <summary>
        /// Register a single sub-mesh slot (primitive hint). When the role says the slot should be hidden in the
        /// current context, <c>renderer.sharedMaterials[subMesh]</c> is swapped to <paramref name="invisible"/>;
        /// otherwise it is restored to <paramref name="original"/>.
        /// </summary>
        public void RegisterPrimitiveSlot(Renderer renderer, int subMesh, Material original, Material invisible, ViewRole role)
        {
            if (renderer == null || subMesh < 0) return;
            var slot = new PrimitiveSlot
            {
                Renderer = renderer,
                SubMesh = subMesh,
                Original = original,
                Invisible = invisible,
                Role = role,
            };
            _primitiveSlots.Add(slot);
            ApplySlot(slot);
        }

        /// <summary>Re-apply the visibility policy to every registered renderer and primitive slot.</summary>
        public void ApplyAll()
        {
            for (int i = 0; i < _renderers.Count; i++)
            {
                var (renderer, role) = _renderers[i];
                if (renderer != null) renderer.enabled = IsVisible(role, _mode);
            }
            for (int i = 0; i < _primitiveSlots.Count; i++)
                ApplySlot(_primitiveSlots[i]);
        }

        private void ApplySlot(PrimitiveSlot slot)
        {
            if (slot.Renderer == null) return;
            var materials = slot.Renderer.sharedMaterials; // returns a copy; must reassign to take effect
            if (slot.SubMesh < 0 || slot.SubMesh >= materials.Length) return;

            var desired = IsVisible(slot.Role, _mode) ? slot.Original : slot.Invisible;
            if (materials[slot.SubMesh] == desired) return;
            materials[slot.SubMesh] = desired;
            slot.Renderer.sharedMaterials = materials;
        }

        private static bool IsVisible(ViewRole role, ViewContext mode)
        {
            switch (role)
            {
                case ViewRole.ThirdPersonOnly: return mode == ViewContext.ThirdPerson;
                case ViewRole.FirstPersonOnly: return mode == ViewContext.FirstPerson;
                default: return true; // Both
            }
        }

        /// <summary>
        /// Maps a glTF <c>role</c> string to a <see cref="ViewRole"/>. Unknown/custom roles fall back to
        /// <see cref="ViewRole.Both"/> (never hidden) with a warning, per the spec's open role vocabulary.
        /// </summary>
        public static ViewRole ParseRole(string role)
        {
            switch (role)
            {
                case VisibilityHintExtensionNames.RoleFirstPersonOnly: return ViewRole.FirstPersonOnly;
                case VisibilityHintExtensionNames.RoleThirdPersonOnly: return ViewRole.ThirdPersonOnly;
                case VisibilityHintExtensionNames.RoleBoth: return ViewRole.Both;
                default:
                    Debug.LogWarning($"[VisibilityHints] Unknown visibility role '{role}'; treating as '{VisibilityHintExtensionNames.RoleBoth}'.");
                    return ViewRole.Both;
            }
        }
    }
}
