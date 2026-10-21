using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGLTF
{
    /// <summary>Default host rendering adapter for view-independent core visibility; leaves hierarchy and cameras active.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(10000)]
    public sealed class CoreVisibilityController : MonoBehaviour
    {
        private readonly Dictionary<Renderer, bool> _hiddenRenderers = new Dictionary<Renderer, bool>();
        private readonly Dictionary<Light, bool> _hiddenLights = new Dictionary<Light, bool>();

        private void OnEnable()
        {
            Camera.onPreCull += BeforeCamera;
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
            ApplyVisibility();
        }
        private void OnDisable()
        {
            Camera.onPreCull -= BeforeCamera;
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            foreach (var item in _hiddenRenderers) if (item.Key != null && !item.Key.enabled) item.Key.enabled = item.Value;
            foreach (var item in _hiddenLights) if (item.Key != null && !item.Key.enabled) item.Key.enabled = item.Value;
            _hiddenRenderers.Clear();
            _hiddenLights.Clear();
        }
        private void LateUpdate() => ApplyVisibility();
        private void BeforeCamera(Camera camera) => ApplyVisibility();
        private void BeforeCamera(ScriptableRenderContext context, Camera camera) => ApplyVisibility();

        /// <summary>Applies current Boolean state without changing authored state, activation, transforms or cameras.</summary>
        public void ApplyVisibility()
        {
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (!CoreNodeVisibility.IsVisibleInHierarchy(renderer.transform, transform))
                {
                    if (!_hiddenRenderers.ContainsKey(renderer)) _hiddenRenderers.Add(renderer, renderer.enabled);
                    renderer.enabled = false;
                }
                else if (_hiddenRenderers.TryGetValue(renderer, out bool original))
                {
                    if (!renderer.enabled) renderer.enabled = original;
                    _hiddenRenderers.Remove(renderer);
                }
            }
            foreach (var light in GetComponentsInChildren<Light>(true))
            {
                if (!CoreNodeVisibility.IsVisibleInHierarchy(light.transform, transform))
                {
                    if (!_hiddenLights.ContainsKey(light)) _hiddenLights.Add(light, light.enabled);
                    light.enabled = false;
                }
                else if (_hiddenLights.TryGetValue(light, out bool original))
                {
                    if (!light.enabled) light.enabled = original;
                    _hiddenLights.Remove(light);
                }
            }
        }
    }
}
