using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// First/third-person viewpoint switch plus a pluggable mesh-visibility policy. Renderers are registered
    /// with a <see cref="RenderView"/> and toggled when the mode changes (e.g. hide the full-body mesh in
    /// first-person, show first-person-only arms).
    /// </summary>
    [DisallowMultipleComponent]
    public class ViewModeController : MonoBehaviour
    {
        public enum ViewMode { ThirdPerson, FirstPerson }
        public enum RenderView { Both, ThirdPersonOnly, FirstPersonOnly }

        [SerializeField] private ViewMode _mode = ViewMode.ThirdPerson;
        public event Action<ViewMode> OnViewModeChanged;

        private readonly List<(Renderer renderer, RenderView view)> _registered = new List<(Renderer, RenderView)>();

        public ViewMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                ApplyVisibility();
                OnViewModeChanged?.Invoke(_mode);
            }
        }

        public void RegisterRenderer(Renderer renderer, RenderView view)
        {
            if (renderer == null) return;
            _registered.Add((renderer, view));
            renderer.enabled = IsVisible(view, _mode);
        }

        /// <summary>Re-apply the visibility policy to all registered renderers for the current mode.</summary>
        public void ApplyVisibility()
        {
            for (int i = 0; i < _registered.Count; i++)
            {
                var (renderer, view) = _registered[i];
                if (renderer != null) renderer.enabled = IsVisible(view, _mode);
            }
        }

        private static bool IsVisible(RenderView view, ViewMode mode)
        {
            switch (view)
            {
                case RenderView.ThirdPersonOnly: return mode == ViewMode.ThirdPerson;
                case RenderView.FirstPersonOnly: return mode == ViewMode.FirstPerson;
                default: return true; // Both
            }
        }
    }
}
