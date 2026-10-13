using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Exposes passive <c>KHR_node_camera_hint</c> descriptors. A hint's initial camera pose is its node's authored
    /// position and the rotation <c>R</c> from <see cref="ComputeAuthoredRotation"/>; a target node only supplies a
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
        /// Presentation name for a hint: its label, else its node's nonempty name, else its role. For a recognized
        /// role, <paramref name="roleDisplayName"/> may supply a display name, such as a localized one, in place of
        /// the raw role string.
        /// </summary>
        public string GetDisplayName(CameraHint hint, Func<string, string> roleDisplayName = null)
        {
            if (hint == null) return null;
            if (!string.IsNullOrEmpty(hint.Label)) return hint.Label;
            if (hint.Node != null && !string.IsNullOrEmpty(hint.Node.name)) return hint.Node.name;
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
        /// Computes the hint's authored rotation <c>R</c> in Unity coordinates, relative to
        /// <paramref name="hierarchyRoot"/>. The path runs from the hint node up to, but not including,
        /// <paramref name="hierarchyRoot"/> (the imported scene root), or to the top of the hierarchy when it is null
        /// or not an ancestor. Each TRS node contributes its rotation times the signs of its scale, with the
        /// zero-scale rules; each matrix node contributes its normalized authored columns, with the zero-column rules.
        /// The product is taken root to leaf and its first column is negated when its determinant is negative.
        /// Conjugating by the glTF-to-Unity X flip preserves every sign and determinant rule, so the result is the
        /// spec's <c>R</c> expressed in Unity coordinates.
        /// </summary>
        public Quaternion ComputeAuthoredRotation(CameraHint hint, Transform hierarchyRoot)
        {
            if (hint?.Node == null) return Quaternion.identity;
            var matrices = new Dictionary<Transform, AuthoredNodeMatrix>();
            foreach (var record in AuthoredMatrices)
                if (record?.Node != null) matrices[record.Node] = record;

            var q = Matrix4x4.identity;
            for (var node = hint.Node; node != null && node != hierarchyRoot; node = node.parent)
            {
                bool isHintNode = node == hint.Node;
                Matrix4x4 pathMatrix;
                if (matrices.TryGetValue(node, out var authored))
                {
                    pathMatrix = NormalizedColumns(authored.LocalMatrix);
                    // A flipped matrix node's children sit under an in-between GameObject that undoes the turn.
                    if (authored.ForwardAxisConversion && !isHintNode)
                        pathMatrix *= Matrix4x4.Rotate(ForwardAxisTurn);
                }
                else
                {
                    var rotation = node.localRotation;
                    if (isHintNode && hint.NodeTransformHasForwardAxisConversion)
                        rotation *= Quaternion.Inverse(ForwardAxisTurn);
                    pathMatrix = Matrix4x4.Rotate(rotation) * Matrix4x4.Scale(ScaleSigns(node.localScale));
                }
                q = pathMatrix * q;
            }

            if (q.determinant < 0f)
                q.SetColumn(0, -q.GetColumn(0));
            return Quaternion.LookRotation(q.GetColumn(2), q.GetColumn(1));
        }

        /// <summary>
        /// Places a caller-supplied camera at the hint's initial pose: the hint node's position, and <c>R</c> composed
        /// with the imported scene root's rotation (host placement). The camera looks along the hint's -Z with +Y up.
        /// The target node never aims the camera; use <see cref="TryGetSubjectPoint"/> for framing or orbiting.
        /// Selection, projection adaptation, timing, and ownership remain host policy.
        /// </summary>
        public void Apply(CameraHint hint, Camera camera, bool copyProjection = true)
        {
            if (hint == null || camera == null || hint.Node == null) return;

            var hierarchyRoot = hint.Node != transform && hint.Node.IsChildOf(transform) ? transform : null;
            var rotation = ComputeAuthoredRotation(hint, hierarchyRoot);
            if (hierarchyRoot != null) rotation = hierarchyRoot.rotation * rotation;
            camera.transform.SetPositionAndRotation(hint.Node.position, rotation * CameraAxisConversion);

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

        // d_i is the sign of each scale component. With exactly one zero component, its sign is the product of the
        // other two; with two or more zero components, each zero component uses +1.
        internal static Vector3 ScaleSigns(Vector3 scale)
        {
            float sx = scale.x < 0f ? -1f : 1f;
            float sy = scale.y < 0f ? -1f : 1f;
            float sz = scale.z < 0f ? -1f : 1f;
            int zeros = (scale.x == 0f ? 1 : 0) + (scale.y == 0f ? 1 : 0) + (scale.z == 0f ? 1 : 0);
            if (zeros == 1)
            {
                if (scale.x == 0f) sx = sy * sz;
                else if (scale.y == 0f) sy = sx * sz;
                else sz = sx * sy;
            }
            return new Vector3(sx, sy, sz);
        }

        // Upper-left 3×3 with each column normalized. One zero column becomes the cross product of the other two in
        // cyclic order; two or more zero columns give the identity.
        internal static Matrix4x4 NormalizedColumns(Matrix4x4 matrix)
        {
            var columns = new Vector3[3];
            int zeroColumns = 0;
            int zeroIndex = -1;
            for (int i = 0; i < 3; i++)
            {
                Vector3 column = matrix.GetColumn(i);
                float length = column.magnitude;
                if (length == 0f)
                {
                    zeroColumns++;
                    zeroIndex = i;
                    columns[i] = Vector3.zero;
                }
                else
                {
                    columns[i] = column / length;
                }
            }
            if (zeroColumns >= 2) return Matrix4x4.identity;
            if (zeroColumns == 1)
                columns[zeroIndex] = Vector3.Cross(columns[(zeroIndex + 1) % 3], columns[(zeroIndex + 2) % 3]);

            var result = Matrix4x4.identity;
            for (int i = 0; i < 3; i++) result.SetColumn(i, new Vector4(columns[i].x, columns[i].y, columns[i].z, 0f));
            return result;
        }
    }
}
