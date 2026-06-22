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

        // Persisted so an editor-imported prefab can rehydrate on Awake (the live import calls Bind, which also
        // stores here). Unity can't serialize the Dictionary in SkeletonMappingResult, hence the mirror type.
        // Hidden from the inspector: it's baked data, surfaced read-only by SkeletonMapEditor rather than edited.
        [SerializeField, HideInInspector] private SerializableSkeletonMapping _serializedMapping;

        // Set by the importer so the humanoid Avatar is (re)built + assigned when the prefab rehydrates at
        // runtime. Serialized into the prefab; the build self-validates and falls back to the generic rig.
        [SerializeField] private bool _buildHumanoidOnAwake;
        internal bool BuildHumanoidOnAwake { set => _buildHumanoidOnAwake = value; }
        private bool _buildHumanoidQueued;
        private bool _animatorPreAdded;     // true when Awake added the Animator (so Start can remove it on build failure)
        private Avatar _lastBuiltAvatar;    // the avatar we built last; destroyed on the next build to avoid leaks

        public IReadOnlyList<string> RigVocabularies { get; private set; } = new List<string>();
        public bool HumanoidAvailable { get; private set; }
        public MappingDirection DetectedDirection => _result?.Direction ?? MappingDirection.Unknown;
        public SkeletonMappingResult Result => _result;

        // Internal access to the last built avatar for runtime rig switching (avoids exposing the field publicly).
        internal Avatar LastBuiltAvatar => _lastBuiltAvatar;

        // Edit-time accessor for the baked skeleton mapping (used by exporter at edit-time when Awake/Bind not called).
        // Returns the live result if available, otherwise the deserialized baked result.
        public SkeletonMappingResult EditorBakedResult => _result ?? _serializedMapping?.ToResult();

        public void Bind(SkeletonMappingResult result)
        {
            BindRuntimeState(result);
            _serializedMapping = SerializableSkeletonMapping.FromResult(result);   // persist for prefab rehydration
        }

        // Populate the runtime working state (the dictionary-backed result + rig list) without rewriting the
        // serialized mirror. The rehydrate path uses this directly so it doesn't rebuild the mirror it just
        // deserialized: FromResult(ToResult(...)) is redundant and, because it walks a Dictionary, would reorder
        // the persisted bone array nondeterministically on every load.
        private void BindRuntimeState(SkeletonMappingResult result)
        {
            _result = result;
            RigVocabularies = result?.SelectedRig != null ? new List<string> { result.SelectedRig } : new List<string>();
        }

        private static bool HasSerializedBones(SerializableSkeletonMapping m)
            => m.Bones != null && m.Bones.Length > 0;

        private static bool HasSerializedReferencePose(SerializableSkeletonMapping m)
            => m.ReferencePose?.Bones != null && m.ReferencePose.Bones.Length > 0;

        // Test hook: the persisted joint-name order. Rehydrate must not reshuffle it (the deserialize path uses
        // BindRuntimeState, which does not rebuild the mirror via FromResult(ToResult(...))).
        internal string[] SerializedJointOrderForTests()
        {
            if (_serializedMapping?.Bones == null) return System.Array.Empty<string>();
            var names = new string[_serializedMapping.Bones.Length];
            for (int i = 0; i < names.Length; i++) names[i] = _serializedMapping.Bones[i].JointName;
            return names;
        }

        // Rehydrate an editor-imported prefab and (only for such a prefab) queue the humanoid build for Start.
        private void Awake()
        {
            // Distinguish a genuinely deserialized/baked prefab from a live import by runtime-vs-serialized state,
            // NOT by Awake-vs-flag timing: a live import (active OR inactive root) has already called Bind before
            // Awake runs, so _result is non-null there; a rehydrated prefab has only the serialized mirror. Require
            // a real payload (bones or a reference pose): Unity may rehydrate a never-assigned [Serializable] field
            // as a default (non-null) instance, which must NOT install an empty result.
            bool deserialized = _result == null && _serializedMapping != null
                                && (HasSerializedBones(_serializedMapping) || HasSerializedReferencePose(_serializedMapping));
            if (deserialized)
                BindRuntimeState(_serializedMapping.ToResult());

            // Queue the build only for a rehydrated prefab that the importer flagged and that actually resolved
            // bones. This keeps EVERY live-import path build-free (the host app owns the runtime Animator), even
            // when the scene root is inactive at import (e.g. HideSceneObjDuringLoad / showSceneObj:false).
            _buildHumanoidQueued = deserialized && _buildHumanoidOnAwake
                                   && _result?.Bones != null && _result.Bones.Count > 0;

            // Pre-add the Animator now so it attaches/initializes during this activation (assigning the avatar to
            // a component AddComponent'd and used within the same Start call throws). Remember we added it so Start
            // can remove it if the build fails — don't leave an orphan Animator on a non-humanoid rig.
            if (_buildHumanoidQueued && GetComponent<Animator>() == null)
            {
                gameObject.AddComponent<Animator>();
                _animatorPreAdded = true;
            }
        }

        private void Start()
        {
            if (!_buildHumanoidQueued) return;
            _buildHumanoidQueued = false;

            var avatar = BuildAndAssignAvatar();

            // BuildHumanoidAvatar self-validates and returns null for a non-humanoid / incomplete rig. If we
            // pre-added an Animator in Awake solely for that build, remove it again so the character isn't left
            // with an empty, unintended Animator (which would also conflict with a legacy Animation component).
            if (avatar == null && _animatorPreAdded)
            {
                var preAdded = GetComponent<Animator>();
                if (preAdded != null) Destroy(preAdded);
            }
            _animatorPreAdded = false;
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
        public Avatar BuildHumanoidAvatar()
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

        /// <summary>
        /// Build the humanoid Avatar (see <see cref="BuildHumanoidAvatar"/>) and assign it to the character's
        /// Animator, rebinding so Mecanim picks it up. When the build fails the generic rig is kept and the
        /// Animator is left untouched. An Animator is added if the character has none. Returns the assigned
        /// Avatar, or null when none could be built.
        /// </summary>
        public Avatar BuildAndAssignAvatar()
        {
            var avatar = BuildHumanoidAvatar();
            if (avatar == null) return null;

            var animator = GetComponent<Animator>();
            if (animator == null) animator = gameObject.AddComponent<Animator>();

            // Assign the new avatar, then destroy the one we built on a previous call so repeated builds (e.g. the
            // play-mode "Build" button) don't leak runtime Avatar objects. Only ever destroy our own built avatar.
            var previous = _lastBuiltAvatar;
            _lastBuiltAvatar = avatar;
            animator.avatar = avatar;
            // Rebind only when the Animator is already initialized (e.g. the play-mode "Build" button on a live
            // character). A freshly added / not-yet-initialized Animator adopts the avatar on its first update;
            // calling Rebind() before native init throws MissingComponentException.
            if (animator.isInitialized) animator.Rebind();
            if (previous != null && previous != avatar) Object.Destroy(previous);
            return avatar;
        }

        /// <summary>
        /// Switches the character's rig mode between Generic and Humanoid at runtime.
        /// </summary>
        /// <param name="mode">The target rig mode. Humanoid builds and assigns a Mecanim humanoid Avatar
        /// when the skeleton mapping resolves the required bones. Generic removes the humanoid Avatar and
        /// keeps the generic rig.</param>
        /// <returns>True if the switch succeeded, false otherwise (e.g., Humanoid requested but bones missing).</returns>
        public bool SwitchRigMode(RigImportMode mode)
        {
            if (mode == RigImportMode.Humanoid)
            {
                // BuildAndAssignAvatar handles validation, leak prevention via _lastBuiltAvatar,
                // and sets HumanoidAvailable flag. Returns null on failure.
                var avatar = BuildAndAssignAvatar();
                return avatar != null;
            }
            else // RigImportMode.Generic
            {
                // Remove the humanoid Avatar and revert to generic rig.
                var animator = GetComponent<Animator>();
                if (animator != null)
                {
                    animator.avatar = null;
                    // Optionally destroy the Animator if it was auto-added and no other components need it.
                    // For now, we keep the Animator component (it may be used for other purposes).
                }

                // Destroy the built avatar to avoid leaks.
                if (_lastBuiltAvatar != null)
                {
                    Object.Destroy(_lastBuiltAvatar);
                    _lastBuiltAvatar = null;
                }

                HumanoidAvailable = false;
                return true;
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
            var boneNames = HumanTrait.BoneName;   // property allocates a fresh array per call; fetch it once
            var list = new List<string>();
            for (int i = 0; i < HumanTrait.BoneCount; i++)
                if (HumanTrait.RequiredBone(i)) list.Add(boneNames[i]);
            return list.ToArray();
        }
    }
}
