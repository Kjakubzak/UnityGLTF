using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Optional Unity host adapter that drives configured expression weights toward a host-selected target.
    /// Its vocabulary, 90-degree response curve, world-space target selection, and update policy are not behavior
    /// defined by <c>KHR_node_lookat_target</c> or <c>KHR_character_expression</c>.
    ///
    /// This component is intentionally vendor-neutral and carries no geometric eye-bone aiming: that engine-only
    /// convenience lives in the separate, opt-in <see cref="EyeAimConstraint"/> (not part of KHR_character).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(50)]
    public class GazeSolver : MonoBehaviour
    {
        public enum LookAtMode { None, Camera, CustomTarget }

        public LookAtMode Mode = LookAtMode.None;

        public Transform Target;
        public Camera TargetCamera;          // used when Mode == Camera; defaults to Camera.main
        [Range(0f, 1f)] public float Weight = 1f;

        // Gaze origin / measurement frame. When unset, resolves to the mapped head bone (if a SkeletonMap
        // resolved one), else this transform — so expression-driven gaze works on non-humanoid characters with
        // no skeleton mapping. Serialized with the prefab (intra-hierarchy Transform refs survive).
        [Tooltip("Optional gaze origin/measurement frame. Falls back to the mapped head bone, then this transform.")]
        public Transform ReferenceFrame;

        // Host-configured look-expression names. The imported marker provides no expression vocabulary or link.
        public string LookLeft = "lookLeft";
        public string LookRight = "lookRight";
        public string LookUp = "lookUp";
        public string LookDown = "lookDown";

        private ExpressionController _expressions;
        private Transform _head;
        private bool _lazyBound;

        private Vector3 _worldTarget;
        private bool _hasWorldTarget;

        public void SetWorldTarget(Vector3 worldPosition)
        {
            _worldTarget = worldPosition;
            _hasWorldTarget = true;
            Mode = LookAtMode.CustomTarget;
        }

        public void Bind(ExpressionController expressions, SkeletonMap skeleton = null)
        {
            _expressions = expressions;
            ResolveHead(skeleton);
            _lazyBound = true;   // a live bind fully resolves links; skip the LateUpdate lazy path
        }

        // Resolve only the head bone (the default gaze frame when ReferenceFrame is unset). Eye-bone resolution
        // is no longer this component's concern — see EyeAimConstraint.
        private void ResolveHead(SkeletonMap skeleton)
        {
            if (skeleton == null) return;
            skeleton.TryGetBone("head", out _head);
        }

        private void LateUpdate()
        {
            if (!_lazyBound) LazyBind();
            if (Mode == LookAtMode.None || Weight <= 0f) return;
            if (!TryGetTargetPosition(out var targetPos)) return;

            // Frame resolution order: explicit ReferenceFrame -> mapped head -> root transform.
            var frame = ReferenceFrame != null ? ReferenceFrame : (_head != null ? _head : transform);
            var toTarget = targetPos - frame.position;
            if (toTarget.sqrMagnitude < 1e-10f) return;

            // Measure yaw/pitch in the gaze frame so the decomposition is consistent with the chosen origin.
            var local = frame.InverseTransformDirection(toTarget.normalized);
            float yaw = Mathf.Atan2(local.x, local.z);                                          // + = right
            float pitch = Mathf.Atan2(local.y, Mathf.Sqrt(local.x * local.x + local.z * local.z)); // + = up

            DriveExpressions(yaw, pitch);
        }

        // Cross-component Awake order isn't guaranteed, so a deserialized prefab resolves its sibling
        // ExpressionController / SkeletonMap on the first frame instead (LateUpdate runs after every Awake).
        // A live import calls Bind (which sets _lazyBound), so this only fires for a rehydrated prefab.
        private void LazyBind()
        {
            _lazyBound = true;
            if (_expressions == null) _expressions = GetComponent<ExpressionController>();
            if (_head == null) ResolveHead(GetComponent<SkeletonMap>());
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
            const float half = Mathf.PI * 0.5f; // this adapter's host-selected 90-degree saturation response
            SetIfPresent(LookRight, Mathf.Clamp01(Mathf.Max(yaw, 0f) / half) * Weight);
            SetIfPresent(LookLeft, Mathf.Clamp01(Mathf.Max(-yaw, 0f) / half) * Weight);
            SetIfPresent(LookUp, Mathf.Clamp01(Mathf.Max(pitch, 0f) / half) * Weight);
            SetIfPresent(LookDown, Mathf.Clamp01(Mathf.Max(-pitch, 0f) / half) * Weight);
        }

        private void SetIfPresent(string name, float value)
        {
            if (!string.IsNullOrEmpty(name)) _expressions.SetWeight(name, value);
        }

        /// <summary>Explicitly clears the four configured adapter outputs.</summary>
        public void ClearOutputs()
        {
            if (_expressions == null) return;
            SetIfPresent(LookRight, 0f);
            SetIfPresent(LookLeft, 0f);
            SetIfPresent(LookUp, 0f);
            SetIfPresent(LookDown, 0f);
        }
    }
}
