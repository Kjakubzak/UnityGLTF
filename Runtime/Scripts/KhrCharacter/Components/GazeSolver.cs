using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Gaze solver. Two output modes: <see cref="GazeOutputMode.BoneAim"/> rotates eye bones toward the target,
    /// or <see cref="GazeOutputMode.Expression"/> drives look left/right/up/down expressions. Runs before
    /// <see cref="ExpressionController"/> (execution order 50 &lt; 100) so it feeds the same-frame evaluation.
    /// Angles are measured against the character's forward frame; a target behind the head is clamped, never
    /// flipped.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(50)]
    public class GazeSolver : MonoBehaviour
    {
        public enum GazeOutputMode { BoneAim, Expression }
        public enum LookAtMode { None, Camera, CustomTarget }

        public GazeOutputMode OutputMode = GazeOutputMode.Expression;
        public LookAtMode Mode = LookAtMode.None;

        public Transform Target;
        public Camera TargetCamera;          // used when Mode == Camera; defaults to Camera.main
        [Range(0f, 1f)] public float Weight = 1f;
        public float MaxYawDegrees = 35f;    // BoneAim clamp
        public float MaxPitchDegrees = 30f;  // BoneAim clamp

        // Look-expression names (override per model vocabulary if needed).
        public string LookLeft = "lookLeft";
        public string LookRight = "lookRight";
        public string LookUp = "lookUp";
        public string LookDown = "lookDown";

        private readonly List<LookAtTarget> _authoredTargets = new List<LookAtTarget>();
        public IReadOnlyList<LookAtTarget> AuthoredTargets => _authoredTargets;

        private ExpressionController _expressions;
        private Transform _leftEye, _rightEye, _head;
        private Quaternion _leftEyeRest, _rightEyeRest;
        private bool _hasEyes;

        private Vector3 _worldTarget;
        private bool _hasWorldTarget;

        public void SetWorldTarget(Vector3 worldPosition)
        {
            _worldTarget = worldPosition;
            _hasWorldTarget = true;
            Mode = LookAtMode.CustomTarget;
        }

        public void Bind(IReadOnlyList<LookAtTarget> authoredTargets, ExpressionController expressions, SkeletonMap skeleton = null)
        {
            _authoredTargets.Clear();
            if (authoredTargets != null) _authoredTargets.AddRange(authoredTargets);
            _expressions = expressions;
            ResolveBones(skeleton);
        }

        /// <summary>Explicit eye/head bone assignment (used when no SkeletonMap is available, e.g. in tests).</summary>
        public void SetEyeBones(Transform leftEye, Transform rightEye, Transform head)
        {
            _leftEye = leftEye;
            _rightEye = rightEye;
            _head = head;
            CacheEyeRest();
        }

        private void ResolveBones(SkeletonMap skeleton)
        {
            if (skeleton == null) { _hasEyes = false; return; }
            skeleton.TryGetBone("leftEye", out _leftEye);
            skeleton.TryGetBone("rightEye", out _rightEye);
            skeleton.TryGetBone("head", out _head);
            CacheEyeRest();
        }

        private void CacheEyeRest()
        {
            if (_leftEye != null) _leftEyeRest = _leftEye.localRotation;
            if (_rightEye != null) _rightEyeRest = _rightEye.localRotation;
            _hasEyes = _leftEye != null || _rightEye != null;
        }

        private void LateUpdate()
        {
            if (Mode == LookAtMode.None || Weight <= 0f) { ResetOutputs(); return; }
            if (!TryGetTargetPosition(out var targetPos)) { ResetOutputs(); return; }

            var frame = _head != null ? _head : transform;
            var toTarget = targetPos - frame.position;
            if (toTarget.sqrMagnitude < 1e-10f) { ResetOutputs(); return; }

            // Measure yaw/pitch in the frame the eyes parent to (head when available, else the root) so the
            // decomposition stays consistent with where BoneAim applies the rotation.
            var local = frame.InverseTransformDirection(toTarget.normalized);
            float yaw = Mathf.Atan2(local.x, local.z);                                          // + = right
            float pitch = Mathf.Atan2(local.y, Mathf.Sqrt(local.x * local.x + local.z * local.z)); // + = up

            if (OutputMode == GazeOutputMode.Expression) DriveExpressions(yaw, pitch);
            else DriveBones(yaw, pitch);
        }

        private bool TryGetTargetPosition(out Vector3 pos)
        {
            pos = default;
            if (Mode == LookAtMode.Camera)
            {
                var cam = TargetCamera != null ? TargetCamera : Camera.main;
                if (cam == null) return false;
                pos = cam.transform.position;
                return true;
            }
            if (Mode == LookAtMode.CustomTarget)
            {
                if (Target != null) { pos = Target.position; return true; }
                if (_hasWorldTarget) { pos = _worldTarget; return true; }
            }
            return false;
        }

        private void DriveExpressions(float yaw, float pitch)
        {
            if (_expressions == null) return;
            const float half = Mathf.PI * 0.5f; // saturate(angle / (pi/2)) per the look-expression convention
            SetIfPresent(LookRight, Mathf.Clamp01(Mathf.Max(yaw, 0f) / half) * Weight);
            SetIfPresent(LookLeft, Mathf.Clamp01(Mathf.Max(-yaw, 0f) / half) * Weight);
            SetIfPresent(LookUp, Mathf.Clamp01(Mathf.Max(pitch, 0f) / half) * Weight);
            SetIfPresent(LookDown, Mathf.Clamp01(Mathf.Max(-pitch, 0f) / half) * Weight);
        }

        private void SetIfPresent(string name, float value)
        {
            if (!string.IsNullOrEmpty(name)) _expressions.SetWeight(name, value);
        }

        private void DriveBones(float yaw, float pitch)
        {
            if (!_hasEyes) return;
            float yawDeg = Mathf.Clamp(yaw * Mathf.Rad2Deg, -MaxYawDegrees, MaxYawDegrees);
            float pitchDeg = Mathf.Clamp(pitch * Mathf.Rad2Deg, -MaxPitchDegrees, MaxPitchDegrees);
            var gaze = Quaternion.Euler(-pitchDeg, yawDeg, 0f); // pitch about local X, yaw about local Y
            if (_leftEye != null) _leftEye.localRotation = Quaternion.Slerp(_leftEyeRest, _leftEyeRest * gaze, Weight);
            if (_rightEye != null) _rightEye.localRotation = Quaternion.Slerp(_rightEyeRest, _rightEyeRest * gaze, Weight);
        }

        private void ResetOutputs()
        {
            if (OutputMode == GazeOutputMode.Expression && _expressions != null)
            {
                SetIfPresent(LookRight, 0f);
                SetIfPresent(LookLeft, 0f);
                SetIfPresent(LookUp, 0f);
                SetIfPresent(LookDown, 0f);
            }
            else if (_hasEyes)
            {
                if (_leftEye != null) _leftEye.localRotation = _leftEyeRest;
                if (_rightEye != null) _rightEye.localRotation = _rightEyeRest;
            }
        }
    }
}
