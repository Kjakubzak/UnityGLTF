using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Exposes passive <c>KHR_node_camera_hint</c> descriptors. <see cref="Apply"/> is one optional Unity host
    /// adapter with a world-up policy; the extension itself does not select, create, activate, or drive a camera.
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraHintSet : MonoBehaviour
    {
        private readonly List<CameraHint> _hints = new List<CameraHint>();
        public IReadOnlyList<CameraHint> Hints
            => _hints.Count > 0
                ? _hints
                : (_serializedHints ?? (IReadOnlyList<CameraHint>)System.Array.Empty<CameraHint>());

        // Persisted so an editor-imported prefab can rehydrate on Awake (the live import calls Bind, which also
        // stores here). CameraHint is [Serializable] and its Transform/Camera refs survive prefab serialization.
        // Hidden from the inspector: it's baked data, surfaced read-only by CameraHintSetEditor.
        [SerializeField, HideInInspector] private List<CameraHint> _serializedHints = new List<CameraHint>();

        public void Bind(IReadOnlyList<CameraHint> hints)
        {
            _hints.Clear();
            if (hints != null) _hints.AddRange(hints);
            _serializedHints = new List<CameraHint>(_hints);   // persist for prefab rehydration
        }

        // Rehydrate an editor-imported prefab. A live import adds this component fresh (no serialized hints) and
        // calls Bind itself, so this is a no-op in that path; it only fires for a deserialized prefab.
        private void Awake()
        {
            if (_hints.Count == 0 && _serializedHints != null && _serializedHints.Count > 0)
                Bind(_serializedHints);
        }

        public bool TryGetByRole(string role, out CameraHint hint)
        {
            foreach (var candidate in Hints)
            {
                if (candidate != null && candidate.Role == role) { hint = candidate; return true; }
            }
            hint = null;
            return false;
        }

        /// <summary>
        /// Applies a descriptor to a caller-supplied camera using Unity world-up for a noncoincident target.
        /// Selection, roll, projection adaptation, timing, and ownership remain host policy.
        /// </summary>
        public void Apply(CameraHint hint, Camera camera, bool copyProjection = true)
        {
            if (hint == null || camera == null || hint.Node == null) return;

            if (hint.Target != null)
            {
                var dir = hint.Target.position - hint.Node.position;
                if (dir.sqrMagnitude > 1e-12f)
                    camera.transform.SetPositionAndRotation(hint.Node.position, Quaternion.LookRotation(dir, Vector3.up));
                else
                    ApplyAuthoredPose(hint, camera);
            }
            else
            {
                ApplyAuthoredPose(hint, camera);
            }

            if (copyProjection && hint.Projection != null)
            {
                camera.fieldOfView = hint.Projection.fieldOfView;
                camera.nearClipPlane = hint.Projection.nearClipPlane;
                camera.farClipPlane = hint.Projection.farClipPlane;
                camera.orthographic = hint.Projection.orthographic;
                camera.orthographicSize = hint.Projection.orthographicSize;
            }
        }

        private static void ApplyAuthoredPose(CameraHint hint, Camera camera)
        {
            // A plain glTF transform uses -Z camera forward, while a node whose core camera or punctual light was
            // imported has already received UnityGLTF's shared forward-axis conversion.
            var rotation = hint.NodeTransformHasForwardAxisConversion
                ? hint.Node.rotation
                : hint.Node.rotation * Quaternion.Euler(0f, 180f, 0f);
            camera.transform.SetPositionAndRotation(hint.Node.position, rotation);
        }
    }
}
