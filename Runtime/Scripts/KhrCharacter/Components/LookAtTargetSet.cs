using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Passive <c>KHR_node_lookat_target</c> metadata. Each entry exposes the evaluated node instance and its
    /// current global-space point without selecting a consumer or causing gaze, camera, IK, or animation behavior.
    /// </summary>
    [DisallowMultipleComponent]
    public class LookAtTargetSet : MonoBehaviour
    {
        private readonly List<LookAtTarget> _targets = new List<LookAtTarget>();
        [SerializeField, HideInInspector] private List<LookAtTarget> _serializedTargets = new List<LookAtTarget>();
        [SerializeField, HideInInspector] private bool _requiredOnImport;

        public IReadOnlyList<LookAtTarget> Targets
            => _targets.Count > 0
                ? _targets
                : (_serializedTargets ?? (IReadOnlyList<LookAtTarget>)System.Array.Empty<LookAtTarget>());
        public bool RequiredOnImport => _requiredOnImport;

        public void Bind(IReadOnlyList<LookAtTarget> targets, bool requiredOnImport = false)
        {
            _targets.Clear();
            if (targets != null) _targets.AddRange(targets);
            _serializedTargets = new List<LookAtTarget>(_targets);
            _requiredOnImport = requiredOnImport;
        }

        private void Awake()
        {
            if (_targets.Count == 0 && _serializedTargets != null && _serializedTargets.Count > 0)
                Bind(_serializedTargets, _requiredOnImport);
        }

        public bool TryGetByHint(string hint, out LookAtTarget target)
        {
            foreach (var candidate in Targets)
            {
                if (candidate != null && candidate.Hint == hint)
                {
                    target = candidate;
                    return true;
                }
            }
            target = null;
            return false;
        }

        public static bool TryGetTargetPoint(LookAtTarget target, out Vector3 point)
        {
            if (target?.Node == null)
            {
                point = default;
                return false;
            }
            point = target.Node.position;
            return true;
        }
    }
}
