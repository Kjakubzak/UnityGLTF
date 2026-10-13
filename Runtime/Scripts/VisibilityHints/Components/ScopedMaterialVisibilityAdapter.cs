using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Optional renderer and light adapter that realizes predicate-hidden content for the lifetime of an explicit
    /// render scope. Hidden mesh primitives get a host-supplied no-draw material, which must contribute to no visual
    /// pass for the active pipeline (an alpha-zero surface shader is not sufficient). A renderer whose node is hidden
    /// also stops casting shadows, and <see cref="ApplyLightsForView"/> turns off lights on hidden nodes. The adapter
    /// does not cover other node features and does not by itself constitute support for required use of either
    /// extension.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScopedMaterialVisibilityAdapter : MonoBehaviour
    {
        [SerializeField] private Material _noDrawMaterial;
        private readonly HashSet<Renderer> _activeRenderers = new HashSet<Renderer>();
        private readonly HashSet<Light> _activeLights = new HashSet<Light>();

        public Material NoDrawMaterial
        {
            get => _noDrawMaterial;
            set => _noDrawMaterial = value;
        }

        /// <summary>
        /// Applies one renderer instance's node and primitive predicates for one view with the given view tags.
        /// Dispose the returned scope in a <c>finally</c> block immediately after that view renders. Call this
        /// independently for every descendant renderer so a nearer node hint can override an inherited hint.
        /// </summary>
        public IDisposable ApplyForView(
            Renderer renderer, IReadOnlyCollection<string> viewTags, bool ancestorInclusiveCoreVisible)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            var controller = GetComponent<ViewContextController>();
            if (controller == null) return EmptyScope.Instance;
            if (!_activeRenderers.Add(renderer))
                throw new InvalidOperationException(
                    "Overlapping visibility-material scopes for one renderer are unsupported.");

            try
            {
                var original = renderer.sharedMaterials;
                var mesh = GetRendererMesh(renderer);
                if (mesh != null && mesh.subMeshCount != original.Length)
                    throw new InvalidOperationException(
                        "Scoped material suppression requires one material slot per mesh submesh.");
                var replacement = (Material[])original.Clone();
                var replacedSlots = new List<int>();
                for (int subMesh = 0; subMesh < replacement.Length; subMesh++)
                {
                    if (controller.ShouldRenderPrimitiveForView(
                            renderer, subMesh, viewTags, ancestorInclusiveCoreVisible))
                        continue;
                    if (_noDrawMaterial == null)
                        throw new InvalidOperationException(
                            "A pipeline-compatible no-draw material is required to suppress renderer output.");
                    if (replacement[subMesh] == _noDrawMaterial) continue;
                    replacement[subMesh] = _noDrawMaterial;
                    replacedSlots.Add(subMesh);
                }

                bool disableShadows = renderer.shadowCastingMode != ShadowCastingMode.Off
                    && !controller.ShouldRenderNodeForView(
                        renderer.transform, viewTags, ancestorInclusiveCoreVisible);
                if (replacedSlots.Count == 0 && !disableShadows)
                    return new EmptyScope(() => _activeRenderers.Remove(renderer));

                var originalShadows = renderer.shadowCastingMode;
                if (replacedSlots.Count > 0) renderer.sharedMaterials = replacement;
                if (disableShadows) renderer.shadowCastingMode = ShadowCastingMode.Off;
                return new MaterialScope(
                    renderer,
                    original,
                    _noDrawMaterial,
                    replacedSlots,
                    disableShadows,
                    originalShadows,
                    () => _activeRenderers.Remove(renderer));
            }
            catch
            {
                _activeRenderers.Remove(renderer);
                throw;
            }
        }

        /// <summary>Scopes every supplied renderer for one view and restores them in reverse order.</summary>
        public IDisposable ApplyForView(
            IReadOnlyList<Renderer> renderers,
            IReadOnlyCollection<string> viewTags,
            Func<Transform, bool> ancestorInclusiveCoreVisibility)
        {
            if (renderers == null) throw new ArgumentNullException(nameof(renderers));
            if (ancestorInclusiveCoreVisibility == null)
                throw new ArgumentNullException(nameof(ancestorInclusiveCoreVisibility));
            var scopes = new List<IDisposable>();
            try
            {
                foreach (var renderer in renderers)
                {
                    if (renderer == null) continue;
                    scopes.Add(ApplyForView(
                        renderer, viewTags, ancestorInclusiveCoreVisibility(renderer.transform)));
                }
                return new CompositeScope(scopes);
            }
            catch
            {
                DisposeAfterFailure(scopes);
                throw;
            }
        }

        /// <summary>
        /// Turns off, for one view, every supplied light whose node's visual-content predicate is false, and restores
        /// them when the returned scope is disposed. Roles resolve through <see cref="NodeVisibilityHintSet"/>, so a
        /// light that must reach every view needs <c>role: always</c> on its own node. Cameras and camera hints are
        /// never touched.
        /// </summary>
        public IDisposable ApplyLightsForView(
            IReadOnlyList<Light> lights,
            IReadOnlyCollection<string> viewTags,
            Func<Transform, bool> ancestorInclusiveCoreVisibility)
        {
            if (lights == null) throw new ArgumentNullException(nameof(lights));
            if (ancestorInclusiveCoreVisibility == null)
                throw new ArgumentNullException(nameof(ancestorInclusiveCoreVisibility));
            var controller = GetComponent<ViewContextController>();
            if (controller == null) return EmptyScope.Instance;

            var scopes = new List<IDisposable>();
            try
            {
                foreach (var light in lights)
                {
                    if (light == null) continue;
                    if (_activeLights.Contains(light))
                        throw new InvalidOperationException(
                            "Overlapping visibility-light scopes for one light are unsupported.");
                    bool keep = !light.enabled
                        || controller.ShouldRenderNodeForView(
                            light.transform, viewTags, ancestorInclusiveCoreVisibility(light.transform));
                    var activeLight = light;
                    _activeLights.Add(activeLight);
                    void Release() => _activeLights.Remove(activeLight);
                    if (keep)
                    {
                        scopes.Add(new EmptyScope(Release));
                        continue;
                    }
                    light.enabled = false;
                    scopes.Add(new LightScope(light, Release));
                }
                return new CompositeScope(scopes);
            }
            catch
            {
                DisposeAfterFailure(scopes);
                throw;
            }
        }

        private static void DisposeAfterFailure(List<IDisposable> scopes)
        {
            try
            {
                new CompositeScope(scopes).Dispose();
            }
            catch
            {
                // Preserve the failure that prevented the render scope from being created.
            }
        }

        private sealed class MaterialScope : IDisposable
        {
            private Renderer _renderer;
            private readonly Material[] _original;
            private readonly Material _noDrawMaterial;
            private readonly List<int> _replacedSlots;
            private readonly bool _restoreShadows;
            private readonly ShadowCastingMode _originalShadows;
            private readonly Action _onDispose;
            private bool _disposed;

            public MaterialScope(
                Renderer renderer,
                Material[] original,
                Material noDrawMaterial,
                List<int> replacedSlots,
                bool restoreShadows,
                ShadowCastingMode originalShadows,
                Action onDispose)
            {
                _renderer = renderer;
                _original = original;
                _noDrawMaterial = noDrawMaterial;
                _replacedSlots = replacedSlots;
                _restoreShadows = restoreShadows;
                _originalShadows = originalShadows;
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    if (_renderer == null) return;
                    if (_replacedSlots.Count > 0)
                    {
                        var current = _renderer.sharedMaterials;
                        bool changed = false;
                        foreach (int subMesh in _replacedSlots)
                        {
                            if (subMesh >= current.Length || subMesh >= _original.Length
                                || current[subMesh] != _noDrawMaterial)
                                continue;
                            current[subMesh] = _original[subMesh];
                            changed = true;
                        }
                        if (changed) _renderer.sharedMaterials = current;
                    }
                    if (_restoreShadows && _renderer.shadowCastingMode == ShadowCastingMode.Off)
                        _renderer.shadowCastingMode = _originalShadows;
                }
                finally
                {
                    _renderer = null;
                    _onDispose?.Invoke();
                }
            }
        }

        private sealed class LightScope : IDisposable
        {
            private Light _light;
            private readonly Action _onDispose;
            private bool _disposed;

            public LightScope(Light light, Action onDispose)
            {
                _light = light;
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    if (_light != null && !_light.enabled) _light.enabled = true;
                }
                finally
                {
                    _light = null;
                    _onDispose?.Invoke();
                }
            }
        }

        private sealed class EmptyScope : IDisposable
        {
            public static readonly EmptyScope Instance = new EmptyScope(null);
            private Action _onDispose;

            public EmptyScope(Action onDispose) => _onDispose = onDispose;

            public void Dispose()
            {
                _onDispose?.Invoke();
                _onDispose = null;
            }
        }

        private sealed class CompositeScope : IDisposable
        {
            private List<IDisposable> _scopes;

            public CompositeScope(List<IDisposable> scopes) => _scopes = scopes;

            public void Dispose()
            {
                if (_scopes == null) return;
                Exception firstFailure = null;
                for (int i = _scopes.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        _scopes[i].Dispose();
                    }
                    catch (Exception exception)
                    {
                        if (firstFailure == null) firstFailure = exception;
                    }
                }
                _scopes = null;
                if (firstFailure != null) throw firstFailure;
            }
        }

        private static Mesh GetRendererMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
