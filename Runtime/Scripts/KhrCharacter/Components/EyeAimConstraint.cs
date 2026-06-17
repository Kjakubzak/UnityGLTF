using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// NON-SPEC Unity convenience — <b>not</b> part of KHR_character. Geometrically aims eye bones at a target,
    /// clamped to a maximum yaw/pitch and re-based to the eyes' rest rotation when inactive. This is the "bone
    /// aim" output that used to live on <see cref="GazeSolver"/>; it was split out so the KHR import path stays
    /// purely expression-driven and vendor-neutral (the spec defines gaze as expression weights, not eye-bone
    /// rotation).
    ///
    /// The importer never attaches this component — add it manually when you want geometric eye aiming. Eye/head
    /// bones can be assigned explicitly via <see cref="SetEyeBones"/> / the inspector, or resolved from a sibling
    /// <see cref="SkeletonMap"/>. Runs before <see cref="ExpressionController"/> (execution order 50 &lt; 100).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(50)]
    public class EyeAimConstraint : MonoBehaviour
    {
        public enum LookAtMode { None, Camera, CustomTarget }

        public LookAtMode Mode = LookAtMode.None;

        public Transform Target;
        public Camera TargetCamera;          // used when Mode == Camera; defaults to Camera.main
        [Range(0f, 1f)] public float Weight = 1f;
        public float MaxYawDegrees = 35f;    // horizontal clamp
        public float MaxPitchDegrees = 30f;  // vertical clamp

        // Optional aim origin / measurement frame. Falls back to the head bone, then this transform.
        [Tooltip("Optional aim origin/measurement frame. Falls back to the head bone, then this transform.")]
        public Transform ReferenceFrame;

        // Eye bones. Assignable explicitly (inspector / SetEyeBones) or resolved from a sibling SkeletonMap.
        public Transform LeftEye;
        public Transform RightEye;

        private Transform _head;
        private Quaternion _leftEyeRest, _rightEyeRest;
        private bool _hasEyes;
        private bool _resolved;

        /// <summary>Explicit eye/head bone assignment (used when no SkeletonMap is available, e.g. in tests).</summary>
        public void SetEyeBones(Transform leftEye, Transform rightEye, Transform head)
        {
            LeftEye = leftEye;
            RightEye = rightEye;
            _head = head;
            CacheEyeRest();
            _resolved = true;   // explicit assignment fully resolves links; skip the LateUpdate lazy path
        }

        // Fill any unassigned eye/head bones from a sibling SkeletonMap, then cache the eyes' rest rotation.
        private void Resolve()
        {
            _resolved = true;
            if (LeftEye == null || RightEye == null || _head == null)
            {
                var skeleton = GetComponent<SkeletonMap>();
                if (skeleton != null)
                {
                    if (LeftEye == null) skeleton.TryGetBone("leftEye", out LeftEye);
                    if (RightEye == null) skeleton.TryGetBone("rightEye", out RightEye);
                    if (_head == null) skeleton.TryGetBone("head", out _head);
                }
            }
            CacheEyeRest();
        }

        private void CacheEyeRest()
        {
            if (LeftEye != null) _leftEyeRest = LeftEye.localRotation;
            if (RightEye != null) _rightEyeRest = RightEye.localRotation;
            _hasEyes = LeftEye != null || RightEye != null;
        }

        private void LateUpdate()
        {
            if (!_resolved) Resolve();
            if (Mode == LookAtMode.None || Weight <= 0f) { ResetOutputs(); return; }
            if (!_hasEyes) return;
            if (!TryGetTargetPosition(out var targetPos)) { ResetOutputs(); return; }

            // Frame resolution order: explicit ReferenceFrame -> head bone -> root transform. Eyes parent to the
            // head when available, so measuring there keeps the decomposition consistent with where we apply it.
            var frame = ReferenceFrame != null ? ReferenceFrame : (_head != null ? _head : transform);
            var toTarget = targetPos - frame.position;
            if (toTarget.sqrMagnitude < 1e-10f) { ResetOutputs(); return; }

            var local = frame.InverseTransformDirection(toTarget.normalized);
            float yaw = Mathf.Atan2(local.x, local.z);                                          // + = right
            float pitch = Mathf.Atan2(local.y, Mathf.Sqrt(local.x * local.x + local.z * local.z)); // + = up
            DriveBones(yaw, pitch);
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
            if (Mode == LookAtMode.CustomTarget && Target != null)
            {
                pos = Target.position;
                return true;
            }
            return false;
        }

        private void DriveBones(float yaw, float pitch)
        {
            float yawDeg = Mathf.Clamp(yaw * Mathf.Rad2Deg, -MaxYawDegrees, MaxYawDegrees);
            float pitchDeg = Mathf.Clamp(pitch * Mathf.Rad2Deg, -MaxPitchDegrees, MaxPitchDegrees);
            var aim = Quaternion.Euler(-pitchDeg, yawDeg, 0f); // pitch about local X, yaw about local Y
            if (LeftEye != null) LeftEye.localRotation = Quaternion.Slerp(_leftEyeRest, _leftEyeRest * aim, Weight);
            if (RightEye != null) RightEye.localRotation = Quaternion.Slerp(_rightEyeRest, _rightEyeRest * aim, Weight);
        }

        private void ResetOutputs()
        {
            if (!_hasEyes) return;
            if (LeftEye != null) LeftEye.localRotation = _leftEyeRest;
            if (RightEye != null) RightEye.localRotation = _rightEyeRest;
        }
    }
}
