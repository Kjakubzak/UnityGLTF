using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Consumes <c>KHR_character_skeleton_mapping</c> + <c>KHR_character_reference_pose</c>. By default it
    /// exposes a generic rig plus this mapping metadata. A runtime Mecanim humanoid Avatar is opt-in via
    /// <see cref="BuildHumanoidAvatar"/>; it excludes expression/gaze-driven bones (eyes/jaw), validates the
    /// required bones and unique bone names, derives muscle config from the reference pose (applied transiently
    /// then restored), and returns null (caller keeps the generic rig) on failure.
    /// </summary>
    [DisallowMultipleComponent]
    public class SkeletonMap : MonoBehaviour
    {
        // Bones excluded from the humanoid so the Animator doesn't fight the gaze/expression solvers.
        private static readonly HashSet<string> ExcludedFromHumanoid = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        { "leftEye", "rightEye", "jaw" };

        private static string[] _requiredHumanNames;
        // Computed lazily: HumanTrait APIs may not be called from a static/field initializer (Unity restriction).
        private static string[] RequiredHumanNames => _requiredHumanNames ?? (_requiredHumanNames = BuildRequiredHumanNames());

        private SkeletonMappingResult _result;

        public IReadOnlyList<string> RigVocabularies { get; private set; } = new List<string>();
        public bool HumanoidAvailable { get; private set; }
        public MappingDirection DetectedDirection => _result?.Direction ?? MappingDirection.Unknown;
        public SkeletonMappingResult Result => _result;

        public void Bind(SkeletonMappingResult result)
        {
            _result = result;
            RigVocabularies = result?.SelectedRig != null ? new List<string> { result.SelectedRig } : new List<string>();
        }

        public bool TryGetBone(string vocabularyJoint, out Transform bone)
        {
            bone = null;
            return _result?.Bones != null && vocabularyJoint != null && _result.Bones.TryGetValue(vocabularyJoint, out bone);
        }

        /// <summary>Apply the baked reference pose transiently — e.g. before building a humanoid.</summary>
        public void ApplyReferencePose()
        {
            var pose = _result?.ReferencePose;
            if (pose?.Bones == null) return;
            for (int i = 0; i < pose.Bones.Length; i++)
            {
                var b = pose.Bones[i];
                if (b == null) continue;
                if (pose.LocalPositions != null && i < pose.LocalPositions.Length) b.localPosition = pose.LocalPositions[i];
                if (pose.LocalRotations != null && i < pose.LocalRotations.Length) b.localRotation = pose.LocalRotations[i];
                if (pose.LocalScales != null && i < pose.LocalScales.Length) b.localScale = pose.LocalScales[i];
            }
        }

        /// <summary>
        /// Opt-in: build a runtime Mecanim humanoid Avatar via <see cref="AvatarBuilder.BuildHumanAvatar"/>.
        /// Returns null (generic fallback) when required bones are missing, a bound bone name is not unique, or
        /// Unity rejects the description. When a reference pose was baked it is applied transiently for the
        /// build and restored afterwards.
        /// </summary>
        public Avatar BuildHumanoidAvatar(string vocabulary = "unityHumanoid")
        {
            HumanoidAvailable = false;
            if (_result?.Bones == null || _result.Bones.Count == 0) return null;

            var transforms = gameObject.GetComponentsInChildren<Transform>(true);

            var human = new List<HumanBone>();
            foreach (var kv in _result.Bones)
            {
                if (kv.Value == null || ExcludedFromHumanoid.Contains(kv.Key)) continue;
                if (!KhrCharacterSkeletonBaker.TryGetHumanName(kv.Key, out var humanName)) continue;
                var bone = new HumanBone { boneName = kv.Value.name, humanName = humanName };
                bone.limit.useDefaultValues = true;
                human.Add(bone);
            }

            if (!ValidateRequired(human, out var missing))
            {
                Debug.LogWarning($"[KHR_character] Humanoid avatar not built; missing required bones: {string.Join(", ", missing)}. Using the generic rig.");
                return null;
            }

            // AvatarBuilder binds bones by name, so a duplicated bound-bone name is ambiguous -> abort.
            if (HasDuplicateBoundName(transforms, human, out var duplicate))
            {
                Debug.LogWarning($"[KHR_character] Humanoid avatar not built; bound bone name '{duplicate}' is not unique. Using the generic rig.");
                return null;
            }

            bool applyReference = _result.ReferencePose?.Bones != null && _result.ReferencePose.Bones.Length > 0;
            var snapshot = applyReference ? CapturePose(transforms) : null;

            try
            {
                if (applyReference) ApplyReferencePose();

                // SkeletonBone[] (local TRS) is captured AFTER the reference pose is applied.
                var skeleton = new SkeletonBone[transforms.Length];
                for (int i = 0; i < transforms.Length; i++)
                {
                    var t = transforms[i];
                    skeleton[i] = new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale };
                }

                var description = new HumanDescription
                {
                    skeleton = skeleton,
                    human = human.ToArray(),
                    upperArmTwist = 0.5f,
                    lowerArmTwist = 0.5f,
                    upperLegTwist = 0.5f,
                    lowerLegTwist = 0.5f,
                    armStretch = 0.05f,
                    legStretch = 0.05f,
                    feetSpacing = 0f,
                    hasTranslationDoF = false,
                };

                var avatar = AvatarBuilder.BuildHumanAvatar(gameObject, description);
                if (avatar == null || !avatar.isValid || !avatar.isHuman)
                {
                    if (avatar != null) Object.Destroy(avatar);
                    return null;
                }

                HumanoidAvailable = true;
                return avatar;
            }
            finally
            {
                if (applyReference) RestorePose(snapshot);
            }
        }

        private static bool ValidateRequired(List<HumanBone> human, out List<string> missing)
        {
            var present = new HashSet<string>();
            foreach (var h in human) present.Add(h.humanName);

            missing = new List<string>();
            foreach (var required in RequiredHumanNames)
                if (!present.Contains(required)) missing.Add(required);
            return missing.Count == 0;
        }

        internal static bool HasDuplicateBoundName(Transform[] transforms, List<HumanBone> human, out string duplicate)
        {
            duplicate = null;
            var counts = new Dictionary<string, int>();
            foreach (var t in transforms)
            {
                if (t == null || t.name == null) continue;
                counts[t.name] = counts.TryGetValue(t.name, out var c) ? c + 1 : 1;
            }
            foreach (var h in human)
                if (counts.TryGetValue(h.boneName, out var c) && c > 1) { duplicate = h.boneName; return true; }
            return false;
        }

        private sealed class PoseSnapshot
        {
            public Transform[] Transforms;
            public Vector3[] Positions;
            public Quaternion[] Rotations;
            public Vector3[] Scales;
        }

        private static PoseSnapshot CapturePose(Transform[] transforms)
        {
            var snapshot = new PoseSnapshot
            {
                Transforms = transforms,
                Positions = new Vector3[transforms.Length],
                Rotations = new Quaternion[transforms.Length],
                Scales = new Vector3[transforms.Length],
            };
            for (int i = 0; i < transforms.Length; i++)
            {
                snapshot.Positions[i] = transforms[i].localPosition;
                snapshot.Rotations[i] = transforms[i].localRotation;
                snapshot.Scales[i] = transforms[i].localScale;
            }
            return snapshot;
        }

        private static void RestorePose(PoseSnapshot snapshot)
        {
            if (snapshot?.Transforms == null) return;
            for (int i = 0; i < snapshot.Transforms.Length; i++)
            {
                var t = snapshot.Transforms[i];
                if (t == null) continue;
                t.localPosition = snapshot.Positions[i];
                t.localRotation = snapshot.Rotations[i];
                t.localScale = snapshot.Scales[i];
            }
        }

        private static string[] BuildRequiredHumanNames()
        {
            var list = new List<string>();
            for (int i = 0; i < HumanTrait.BoneCount; i++)
                if (HumanTrait.RequiredBone(i)) list.Add(HumanTrait.BoneName[i]);
            return list.ToArray();
        }
    }
}
