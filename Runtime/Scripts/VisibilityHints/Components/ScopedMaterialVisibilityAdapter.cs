using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.VisibilityHints
{
    /// <summary>
    /// Optional renderer-only adapter that realizes predicate-hidden mesh primitives with a host-supplied no-draw
    /// material for the lifetime of an explicit render scope. The material must contribute to no visual pass for
    /// the active pipeline; an alpha-zero surface shader is not sufficient. This adapter does not cover non-renderer
    /// node features and does not by itself constitute support for required use of either extension.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScopedMaterialVisibilityAdapter : MonoBehaviour
    {
        [SerializeField] private Material _noDrawMaterial;
        private readonly HashSet<Renderer> _activeRenderers = new HashSet<Renderer>();

        public Material NoDrawMaterial
        {
            get => _noDrawMaterial;
            set => _noDrawMaterial = value;
        }

        /// <summary>
        /// Applies one renderer instance's node and primitive predicates for one view. Dispose the returned scope in
        /// a <c>finally</c> block immediately after that view renders. Call this independently for every descendant
        /// renderer so a nearer node hint can override an inherited hint.
        /// </summary>
        public IDisposable ApplyForView(
            Renderer renderer, string context, bool ancestorInclusiveCoreVisible)
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
                    if (controller.ShouldRenderPrimitiveForContext(
                            renderer, subMesh, context, ancestorInclusiveCoreVisible))
                        continue;
                    if (_noDrawMaterial == null)
                        throw new InvalidOperationException(
                            "A pipeline-compatible no-draw material is required to suppress renderer output.");
                    if (replacement[subMesh] == _noDrawMaterial) continue;
                    replacement[subMesh] = _noDrawMaterial;
                    replacedSlots.Add(subMesh);
                }

                if (replacedSlots.Count == 0)
                    return new EmptyScope(() => _activeRenderers.Remove(renderer));
                renderer.sharedMaterials = replacement;
                return new MaterialScope(
                    renderer,
                    original,
                    _noDrawMaterial,
                    replacedSlots,
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
            string context,
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
                        renderer, context, ancestorInclusiveCoreVisibility(renderer.transform)));
                }
                return new CompositeScope(scopes);
            }
            catch
            {
                try
                {
                    new CompositeScope(scopes).Dispose();
                }
                catch
                {
                    // Preserve the failure that prevented the render scope from being created.
                }
                throw;
            }
        }

        private sealed class MaterialScope : IDisposable
        {
            private Renderer _renderer;
            private readonly Material[] _original;
            private readonly Material _noDrawMaterial;
            private readonly List<int> _replacedSlots;
            private readonly Action _onDispose;
            private bool _disposed;

            public MaterialScope(
                Renderer renderer,
                Material[] original,
                Material noDrawMaterial,
                List<int> replacedSlots,
                Action onDispose)
            {
                _renderer = renderer;
                _original = original;
                _noDrawMaterial = noDrawMaterial;
                _replacedSlots = replacedSlots;
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    if (_renderer == null) return;
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
                finally
                {
                    _renderer = null;
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
