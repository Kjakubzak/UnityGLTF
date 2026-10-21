using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Exposes passive <c>KHR_node_camera_hint</c> descriptors. A hint's initial camera pose is its node's authored
    /// global transform with scale removed under core camera rules; a target node only supplies a
    /// subject point. <see cref="Apply"/> is one optional Unity host adapter; the extension itself does not select,
    /// create, activate, or drive a camera.
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraHintSet : MonoBehaviour
    {
        // UnityGLTF's camera/light forward-axis turn, 180° about Y (SchemaExtensions.InvertDirection).
        private static readonly Quaternion ForwardAxisTurn = new Quaternion(0f, -1f, 0f, 0f);

        // Host camera-axis choice: a Unity camera looks along +Z, while the hint follows the glTF camera convention of
        // looking along -Z and up along +Y.
        private static readonly Quaternion CameraAxisConversion = Quaternion.Euler(0f, 180f, 0f);

        private readonly List<CameraHint> _hints = new List<CameraHint>();
        public IReadOnlyList<CameraHint> Hints
            => _hints.Count > 0
                ? _hints
                : (_serializedHints ?? (IReadOnlyList<CameraHint>)Array.Empty<CameraHint>());

        // Persisted so an editor-imported prefab can rehydrate on Awake (the live import calls Bind, which also
        // stores here). CameraHint is [Serializable] and its Transform/Camera refs survive prefab serialization.
        // Hidden from the inspector: it's baked data, surfaced read-only by CameraHintSetEditor.
        [SerializeField, HideInInspector] private List<CameraHint> _serializedHints = new List<CameraHint>();
        [SerializeField, HideInInspector] private List<AuthoredNodeMatrix> _authoredMatrices = new List<AuthoredNodeMatrix>();

        /// <summary>
        /// Custom roles the host recognizes because it implements the specification or profile that defines them.
        /// Filled by the host at runtime; not serialized.
        /// </summary>
        public ISet<string> RegisteredCustomRoles { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Matrix-defined nodes on the hints' paths, recorded at import.</summary>
        public IReadOnlyList<AuthoredNodeMatrix> AuthoredMatrices
            => _authoredMatrices ?? (IReadOnlyList<AuthoredNodeMatrix>)Array.Empty<AuthoredNodeMatrix>();

        public void Bind(IReadOnlyList<CameraHint> hints, IReadOnlyList<AuthoredNodeMatrix> authoredMatrices = null)
        {
            _hints.Clear();
            if (hints != null) _hints.AddRange(hints);
            _serializedHints = new List<CameraHint>(_hints);   // persist for prefab rehydration
            _authoredMatrices = authoredMatrices != null
                ? new List<AuthoredNodeMatrix>(authoredMatrices)
                : new List<AuthoredNodeMatrix>();
        }

        // Rehydrate an editor-imported prefab. A live import adds this component fresh (no serialized hints) and
        // calls Bind itself, so this is a no-op in that path; it only fires for a deserialized prefab.
        private void Awake()
        {
            if (_hints.Count == 0 && _serializedHints != null && _serializedHints.Count > 0)
                Bind(_serializedHints, _authoredMatrices);
        }

        /// <summary>
        /// Returns the first hint with exactly <paramref name="role"/> in list order. Several hints may share a role,
        /// and neither list order nor node order implies priority among them.
        /// </summary>
        public bool TryGetByRole(string role, out CameraHint hint)
        {
            foreach (var candidate in Hints)
            {
                if (candidate != null && candidate.Role == role) { hint = candidate; return true; }
            }
            hint = null;
            return false;
        }

        /// <summary>True for a standard role or a role in <see cref="RegisteredCustomRoles"/>.</summary>
        public bool IsRecognizedRole(string role)
            => CameraHintRoles.IsStandard(role) || (role != null && RegisteredCustomRoles.Contains(role));

        /// <summary>
        /// Hints a host may surface in default camera affordances such as menus, tours or thumbnails: those with a
        /// recognized role, in list order. Hints with unrecognized custom roles stay imported and exported, and
        /// diagnostic views may still list them.
        /// </summary>
        public IReadOnlyList<CameraHint> GetPresentableHints()
        {
            var presentable = new List<CameraHint>();
            foreach (var hint in Hints)
                if (hint != null && IsRecognizedRole(hint.Role)) presentable.Add(hint);
            return presentable;
        }

        /// <summary>
        /// Automatic default-view choice: among hints whose role is exactly <c>default_view</c>, the one on the node
        /// with the lowest glTF node index. Hints without an index (not imported) follow indexed ones in list order.
        /// </summary>
        public bool TrySelectDefaultView(out CameraHint hint)
        {
            hint = null;
            foreach (var candidate in Hints)
            {
                if (candidate == null || candidate.Role != CameraHintRoles.DefaultView) continue;
                if (hint == null || IsLowerNodeIndex(candidate, hint)) hint = candidate;
            }
            return hint != null;
        }

        /// <summary>
        /// Presentation name for a hint: its label, else its node's nonempty name, else its role. The node name is the
        /// GameObject's name, except for a hint imported from an unnamed glTF node
        /// (<see cref="CameraHint.NodeUnnamed"/>). For a recognized role, <paramref name="roleDisplayName"/> may supply
        /// a display name, such as a localized one, in place of the raw role string.
        /// </summary>
        public string GetDisplayName(CameraHint hint, Func<string, string> roleDisplayName = null)
        {
            if (hint == null) return null;
            if (!string.IsNullOrEmpty(hint.Label)) return hint.Label;
            if (!hint.NodeUnnamed && hint.Node != null && !string.IsNullOrEmpty(hint.Node.name)) return hint.Node.name;
            if (roleDisplayName != null && IsRecognizedRole(hint.Role))
            {
                var provided = roleDisplayName(hint.Role);
                if (!string.IsNullOrEmpty(provided)) return provided;
            }
            return hint.Role;
        }

        /// <summary>
        /// Returns the subject point, the target node's global position, when the hint has a usable target node (a
        /// different node that resolved). The subject point never changes the initial camera pose.
        /// </summary>
        public static bool TryGetSubjectPoint(CameraHint hint, out Vector3 point)
        {
            if (hint?.Target == null || hint.Target == hint.Node)
            {
                point = default;
                return false;
            }
            point = hint.Target.position;
            return true;
        }

        /// <summary>
        /// The core camera frame in asset coordinates: accumulate full local transforms before removing global scale.
        /// Returns false for an undefined/singular frame, including zero scale. No fallback pose is invented.
        /// </summary>
        public bool TryGetInitialCameraFrame(CameraHint hint, Transform hierarchyRoot, out Matrix4x4 frame)
        {
            frame = Matrix4x4.identity;
            if (hint?.Node == null) return false;
            var matrices = new Dictionary<Transform, AuthoredNodeMatrix>();
            foreach (var record in AuthoredMatrices)
                if (record?.Node != null) matrices[record.Node] = record;
            for (var node = hint.Node; node != null && node != hierarchyRoot; node = node.parent)
            {
                bool isHintNode = node == hint.Node;
                Matrix4x4 local;
                if (matrices.TryGetValue(node, out var authored))
                {
                    local = authored.LocalMatrix;
                    if (authored.ForwardAxisConversion && !isHintNode)
                        local *= Matrix4x4.Rotate(ForwardAxisTurn);
                }
                else
                {
                    var rotation = node.localRotation;
                    if (isHintNode && hint.NodeTransformHasForwardAxisConversion)
                        rotation *= Quaternion.Inverse(ForwardAxisTurn);
                    local = Matrix4x4.TRS(node.localPosition, rotation, node.localScale);
                }
                frame = local * frame;
            }
            for (int column = 0; column < 3; column++)
            {
                var v = frame.GetColumn(column);
                double length = Math.Sqrt((double)v.x * v.x + (double)v.y * v.y + (double)v.z * v.z);
                if (length == 0d || double.IsNaN(length) || double.IsInfinity(length)) return false;
                frame.SetColumn(column, new Vector4((float)(v.x / length), (float)(v.y / length), (float)(v.z / length), 0f));
            }
            return frame.determinant != 0f && !float.IsNaN(frame.determinant) && !float.IsInfinity(frame.determinant);
        }

        /// <summary>
        /// Rigid Unity host adaptation of a defined core camera frame. A reflected/sheared frame is retained by
        /// TryGetInitialCameraFrame; a quaternion cannot express that full frame. Position excludes host placement.
        /// </summary>
        public bool TryGetInitialPose(CameraHint hint, Transform hierarchyRoot, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (!TryGetInitialCameraFrame(hint, hierarchyRoot, out var frame)) return false;
            position = frame.GetColumn(3);
            rotation = Quaternion.LookRotation(frame.GetColumn(2), frame.GetColumn(1));
            return true;
        }

        /// <summary>Compatibility rigid host adaptation. Use TryGetInitialCameraFrame to detect undefined poses.</summary>
        public Quaternion ComputeAuthoredRotation(CameraHint hint, Transform hierarchyRoot)
        {
            if (!TryGetInitialPose(hint, hierarchyRoot, out _, out var rotation))
                throw new InvalidOperationException("The core camera frame is undefined; the hint supplies no initial pose.");
            return rotation;
        }

        /// <summary>
        /// Places a caller-supplied camera using the defined core camera frame composed
        /// with the imported scene root (host placement). Undefined frames leave the camera untouched. The camera looks along the hint's -Z with +Y up.
        /// The target node never aims the camera; use <see cref="TryGetSubjectPoint"/> for framing or orbiting.
        /// Selection, projection adaptation, timing, and ownership remain host policy.
        /// </summary>
        public void Apply(CameraHint hint, Camera camera, bool copyProjection = true)
        {
            if (hint == null || camera == null || hint.Node == null) return;

            var hierarchyRoot = hint.Node != transform && hint.Node.IsChildOf(transform) ? transform : null;
            if (!TryGetInitialCameraFrame(hint, hierarchyRoot, out var frame)) return;
            var placedFrame = hierarchyRoot != null ? hierarchyRoot.localToWorldMatrix * frame : frame;
            var rotation = Quaternion.LookRotation(placedFrame.GetColumn(2), placedFrame.GetColumn(1));
            camera.transform.SetPositionAndRotation(placedFrame.GetColumn(3), rotation * CameraAxisConversion);
            // Keep reflected/sheared core frames in the view matrix; a Transform quaternion alone loses them.
            camera.worldToCameraMatrix = Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
                * (placedFrame * Matrix4x4.Rotate(CameraAxisConversion)).inverse;

            if (copyProjection && hint.Projection != null)
            {
                camera.fieldOfView = hint.Projection.fieldOfView;
                camera.nearClipPlane = hint.Projection.nearClipPlane;
                camera.farClipPlane = hint.Projection.farClipPlane;
                camera.orthographic = hint.Projection.orthographic;
                camera.orthographicSize = hint.Projection.orthographicSize;
            }
        }

        private static bool IsLowerNodeIndex(CameraHint candidate, CameraHint current)
        {
            if (candidate.NodeIndex < 0) return false;
            return current.NodeIndex < 0 || candidate.NodeIndex < current.NodeIndex;
        }

    }
}
