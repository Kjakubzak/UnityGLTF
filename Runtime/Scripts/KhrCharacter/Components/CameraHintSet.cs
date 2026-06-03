using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Exposes authored <c>KHR_node_camera_hint</c> entries and, on request, drives a caller-supplied camera.
    /// Advisory only — it never creates or takes over a camera.
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraHintSet : MonoBehaviour
    {
        private readonly List<CameraHint> _hints = new List<CameraHint>();
        public IReadOnlyList<CameraHint> Hints => _hints;

        public void Bind(IReadOnlyList<CameraHint> hints)
        {
            _hints.Clear();
            if (hints != null) _hints.AddRange(hints);
        }

        public bool TryGetByRole(string role, out CameraHint hint)
        {
            for (int i = 0; i < _hints.Count; i++)
            {
                if (_hints[i] != null && _hints[i].Role == role) { hint = _hints[i]; return true; }
            }
            hint = null;
            return false;
        }

        /// <summary>Move a caller-supplied camera to the hint.</summary>
        public void Apply(CameraHint hint, Camera camera, bool copyProjection = true)
        {
            if (hint == null || camera == null || hint.Node == null) return;

            if (hint.Target != null)
            {
                var dir = hint.Target.position - hint.Node.position;
                if (dir.sqrMagnitude > 1e-12f)
                    camera.transform.SetPositionAndRotation(hint.Node.position, Quaternion.LookRotation(dir, Vector3.up));
                else
                    camera.transform.position = hint.Node.position; // target coincides with the node: keep orientation
            }
            else
            {
                // A camera-hint node is a plain transform; glTF cameras look down -Z, which UnityGLTF resolves
                // by applying a 180-degree Y rotation to imported camera nodes (ImporterCameras.cs). Match that.
                camera.transform.SetPositionAndRotation(hint.Node.position, hint.Node.rotation * Quaternion.Euler(0f, 180f, 0f));
            }

            if (copyProjection && hint.Projection != null)
            {
                camera.fieldOfView = hint.Projection.fieldOfView;
                camera.nearClipPlane = hint.Projection.nearClipPlane;
                camera.farClipPlane = hint.Projection.farClipPlane;
                camera.orthographic = hint.Projection.orthographic;
            }
        }
    }
}
