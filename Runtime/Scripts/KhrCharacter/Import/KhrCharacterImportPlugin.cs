using System.Collections.Generic;
using GLTF.Schema;
using GLTF.Schema.KHR_lights_punctual;
using Newtonsoft.Json;
using UnityEngine;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Import plugin for the KHR Character/Avatar extension set (glTF PR #2512). Modeled on the
    /// KHR_audio_emitter plugin. Disabled by default and marked non-ratified until the extension set is
    /// ratified.
    /// </summary>
    [NonRatifiedPlugin("KHR_character / avatar extension set (glTF PR #2512) — not yet ratified.")]
    public class KhrCharacterImportPlugin : GLTFImportPlugin
    {
        public override string DisplayName => "KHR Character / Avatar Extensions";
        public override string Description =>
            "Imports the Khronos Character/Avatar extension set (KHR_character*, KHR_node_lookat_target, KHR_node_camera_hint).";

        public override bool EnabledByDefault => false;

        [Tooltip("Rig: Humanoid (default) builds and assigns a Mecanim humanoid Avatar when the skeleton mapping " +
                 "resolves the required bones; Generic keeps the generic rig and never builds a humanoid Avatar. " +
                 "Vendor-neutral — operates over whatever rig vocabularies the model declares.")]
        public RigImportMode Rig = RigImportMode.Humanoid;

        [Tooltip("Optional Unity host adapter. When enabled, imports the legacy additive/override " +
                 "ExpressionController and lets it write scene targets. This is application policy, not " +
                 "KHR_character_expression wire behavior.")]
        public bool CreateExpressionController;

        [Tooltip("Optional Unity host policy. When enabled together with the expression controller, prevents " +
                 "the imported expression/reference-pose clips from becoming an automatic default animation. " +
                 "This changes the Unity animation host and is not extension conformance behavior.")]
        public bool SuppressNonInteractiveAnimationAutoPlay;

        public override GLTFImportPluginContext CreateInstance(GLTFImportContext context)
            => new KhrCharacterImportContext(
                context,
                Rig,
                CreateExpressionController,
                SuppressNonInteractiveAnimationAutoPlay);
    }

    /// <summary>
    /// Per-import instance: detects the character, builds a node-index -> GameObject map, bakes expression
    /// tracks, and attaches the <see cref="KhrCharacter"/> hub and its controllers.
    /// </summary>
    public class KhrCharacterImportContext : GLTFImportPluginContext
    {
        private readonly GLTFImportContext _context;
        private readonly RigImportMode _rigMode;
        private readonly bool _createExpressionController;
        private readonly bool _suppressNonInteractiveAnimationAutoPlay;
        private bool _isCharacter;
        private int _characterRootNodeIndex = -1;

        // UnityGLTF doesn't expose a GameObject -> node-index map, so build our own from the node callbacks.
        private readonly Dictionary<int, GameObject> _nodeIndexToGo = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, Camera> _cameraIndexToProjection = new Dictionary<int, Camera>();
        private readonly HashSet<string> _presentExtensions = new HashSet<string>();
        private readonly List<CameraHintRecord> _cameraHints = new List<CameraHintRecord>();
        private readonly List<LookAtTargetRecord> _lookatTargets = new List<LookAtTargetRecord>();
        private readonly HashSet<Transform> _cameraHintNodes = new HashSet<Transform>();
        private readonly HashSet<Transform> _lookAtTargetNodes = new HashSet<Transform>();
        private HashSet<string> _requiredExtensions = new HashSet<string>();
        private bool _lookAtRequired;

        private sealed class CameraHintRecord
        {
            public Transform Node;
            public KHR_node_camera_hint Hint;
            public bool NodeTransformHasForwardAxisConversion;
        }

        private sealed class LookAtTargetRecord
        {
            public Transform Node;
            public KHR_node_lookat_target Target;
        }

        public KhrCharacterImportContext(
            GLTFImportContext context,
            RigImportMode rigMode = RigImportMode.Humanoid,
            bool createExpressionController = false,
            bool suppressNonInteractiveAnimationAutoPlay = false)
        {
            _context = context;
            _rigMode = rigMode;
            _createExpressionController = createExpressionController;
            _suppressNonInteractiveAnimationAutoPlay = suppressNonInteractiveAnimationAutoPlay;
        }

        public override bool SupportsRequiredExtension(string extensionName)
        {
            if (extensionName == KhrCharacterExtensionNames.NodeLookatTarget) return true;

            // Parsing or baking a subset of expression data is not the complete behavior promised by required use.
            // Keep these explicit so adding a schema factory cannot silently turn recognition into a capability claim.
            switch (extensionName)
            {
                case KhrCharacterExtensionNames.Expression:
                case KhrCharacterExtensionNames.ExpressionMorphtarget:
                case KhrCharacterExtensionNames.ExpressionJoint:
                case KhrCharacterExtensionNames.ExpressionTexture:
                case KhrCharacterExtensionNames.ExpressionMapping:
                case KhrCharacterExtensionNames.ExpressionMask:
                    return false;
                default:
                    return false;
            }
        }

        public override void OnAfterImportRoot(GLTFRoot gltfRoot)
        {
            // Detect by name from the root extensions. This works whether the extension deserialized to a
            // typed object or to a raw DefaultExtension.
            _presentExtensions.Clear();
            _nodeIndexToGo.Clear();
            _cameraIndexToProjection.Clear();
            _cameraHints.Clear();
            _lookatTargets.Clear();
            _cameraHintNodes.Clear();
            _lookAtTargetNodes.Clear();
            _characterRootNodeIndex = -1;
            _requiredExtensions = gltfRoot?.ExtensionsRequired != null
                ? new HashSet<string>(gltfRoot.ExtensionsRequired)
                : new HashSet<string>();
            _lookAtRequired = _requiredExtensions.Contains(KhrCharacterExtensionNames.NodeLookatTarget);
            if (gltfRoot?.Extensions != null)
            {
                foreach (var key in gltfRoot.Extensions.Keys)
                {
                    var canonical = KhrCharacterExtensionNames.Canonicalize(key);
                    if (KhrCharacterExtensionNames.IsCharacterExtension(canonical))
                        _presentExtensions.Add(canonical);
                }
            }
            _isCharacter = _presentExtensions.Contains(KhrCharacterExtensionNames.Character);
            if (_isCharacter)
            {
                var character = GetCharacterExtension(gltfRoot);
                if (character?.RootNode == null
                    || gltfRoot.Nodes == null
                    || character.RootNode.Value < 0
                    || character.RootNode.Value >= gltfRoot.Nodes.Count)
                {
                    Debug.LogError("[KHR_character] rootNode does not resolve to a top-level node index; character support is unavailable.");
                    _isCharacter = false;
                }
                else
                    _characterRootNodeIndex = character.RootNode.Value;
            }
        }

        public override void OnAfterImportNode(Node node, int nodeIndex, GameObject nodeObject)
        {
            if (nodeObject != null) _nodeIndexToGo[nodeIndex] = nodeObject;
            if (node?.Camera != null && nodeObject != null)
            {
                var projection = nodeObject.GetComponent<Camera>();
                if (projection != null) _cameraIndexToProjection[node.Camera.Id] = projection;
            }
            if (node?.Extensions == null) return;

            // Node-level extensions (camera hint / look-at target) live on nodes; detect + record them here.
            foreach (var kv in node.Extensions)
            {
                var canonical = KhrCharacterExtensionNames.Canonicalize(kv.Key);
                if (!KhrCharacterExtensionNames.IsCharacterExtension(canonical)) continue;
                _presentExtensions.Add(canonical);

                if (canonical == KhrCharacterExtensionNames.NodeCameraHint)
                {
                    var hint = AsCameraHint(kv.Value);
                    var transform = nodeObject != null ? nodeObject.transform : null;
                    if (hint != null && transform != null && _cameraHintNodes.Add(transform))
                        _cameraHints.Add(new CameraHintRecord
                        {
                            Node = transform,
                            Hint = hint,
                            NodeTransformHasForwardAxisConversion =
                                (node.Camera != null && nodeObject.GetComponent<Camera>() != null)
                                || (node.Extensions.ContainsKey(KHR_lights_punctualExtensionFactory.EXTENSION_NAME)
                                    && nodeObject.GetComponent<Light>() != null),
                        });
                }
                else if (canonical == KhrCharacterExtensionNames.NodeLookatTarget)
                {
                    var target = AsLookatTarget(kv.Value);
                    var transform = nodeObject != null ? nodeObject.transform : null;
                    if (target != null && transform != null && _lookAtTargetNodes.Add(transform))
                        _lookatTargets.Add(new LookAtTargetRecord { Node = transform, Target = target });
                }
            }
        }

        public override void OnAfterImportScene(GLTFScene scene, int sceneIndex, GameObject sceneObject)
        {
            // OnAfterImportScene is the last callback invoked at runtime (OnAfterImport is editor-only).
            if (sceneObject == null) return;

            // These two node annotations have no dependency on KHR_character. Expose them even for an otherwise
            // ordinary glTF asset; look-at required use is satisfied by the passive live-point set itself.
            if (!_isCharacter)
            {
                WireNodeFeatures(sceneObject, null);
                return;
            }

            var hub = sceneObject.GetComponent<KhrCharacter>();
            if (hub == null) hub = sceneObject.AddComponent<KhrCharacter>();
            _nodeIndexToGo.TryGetValue(_characterRootNodeIndex, out var designatedRootObject);
            hub.SetDesignation(_characterRootNodeIndex, designatedRootObject != null ? designatedRootObject.transform : null);

            // Parse the response metadata once for the explicitly enabled Unity host adapters below. Neither the
            // controller nor clip suppression is implied by the extension, so both remain opt-in import policy.
            var expressionExt = _context?.Root != null ? GetExpressionExtension(_context.Root) : null;

            // The passive response set is the wire implementation: it preserves every channel, evaluates absolute
            // records, and never writes scene targets. It is independent of the optional Unity controller below.
            var responses = TryBakeExpressionResponses(sceneObject, hub, expressionExt);

            // The legacy scene-writing adapter may only consume data that passed the base wire evaluator's
            // validation. Invalid optional expression data is ignored atomically rather than partially applied.
            var set = _createExpressionController && responses != null
                ? TryBakeExpressions(sceneObject, hub, expressionExt)
                : null;
            WireSkeleton(sceneObject, hub);
            WireNodeFeatures(sceneObject, hub);

            // A host that explicitly gives the optional controller ownership may also opt into changing Unity's
            // default animation selection. Ordinary imported clips are otherwise left untouched.
            if (_suppressNonInteractiveAnimationAutoPlay && set != null)
                SuppressNonInteractiveClipAutoPlay(sceneObject, expressionExt);

            var capabilities = DeriveCapabilities(_presentExtensions, set);
            if (hub.Skeleton?.Result?.ReferencePose != null) capabilities.Add(CharacterCapability.ReferencePose);
            hub.SetCapabilities(capabilities);

            hub.MarkReady();
        }

#if UNITY_EDITOR
        // Editor-only: after every built-in sub-asset (Meshes/Materials/Textures/Avatar/etc.) has been registered
        // and ctx.SetMainObject was called, build the humanoid Avatar once and persist it as a sub-asset of the
        // imported prefab. This makes the Animator's Avatar slot populated at pure edit time (without pressing
        // Play). The runtime rebuild path in SkeletonMap.Start respects an already-assigned Avatar and skips.
        public override void OnAfterImport()
        {
            if (!_isCharacter || _context?.AssetContext == null) return;
            var root = _context.AssetContext.mainObject as GameObject;
            if (root == null) return;
            var skeleton = root.GetComponent<SkeletonMap>();
            if (skeleton == null) return;
            if (!ShouldBuildHumanoid(_rigMode, skeleton.EditorBakedResult)) return;

            var avatar = skeleton.BuildHumanoidAvatar();
            if (avatar == null) return;

            avatar.name = "KhrCharacterAvatar";
            _context.AssetContext.AddObjectToAsset("khrCharacterAvatar", avatar);

            var animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
            animator.avatar = avatar;
        }
#endif

        private ExpressionResponseSet TryBakeExpressionResponses(
            GameObject sceneObject,
            KhrCharacter hub,
            KHR_character_expression expressionExt)
        {
            var root = _context?.Root;
            var importer = _context?.SceneImporter;
            if (root == null || importer == null || expressionExt == null) return null;

            try
            {
                var entries = KhrCharacterResponseBaker.Bake(root, importer, expressionExt);
                if (entries.Length == 0) return null;
                var responses = sceneObject.GetComponent<ExpressionResponseSet>();
                if (responses == null) responses = sceneObject.AddComponent<ExpressionResponseSet>();
                responses.Bind(
                    entries,
                    root.ExtensionsRequired != null &&
                    root.ExtensionsRequired.Contains(KhrCharacterExtensionNames.Expression));
                hub.ExpressionResponses = responses;
                return responses;
            }
            catch (ExpressionResponseEvaluationException exception)
            {
                Debug.LogError($"[KHR_character] Expression response data is invalid: {exception.Message}");
                return null;
            }
        }

        private CharacterExpressionSet TryBakeExpressions(GameObject sceneObject, KhrCharacter hub, KHR_character_expression expressionExt)
        {
            var root = _context?.Root;
            var importer = _context?.SceneImporter;
            if (root == null || importer == null) return null;

            if (expressionExt == null) return null;

            MaterialPropertiesRemapper remapper = null;
            if (_context.TryGetPlugin<AnimationPointerImportContext>(out var pointerCtx))
                remapper = pointerCtx.materialPropertiesRemapper;

            var set = KhrCharacterBaker.Bake(root, importer, expressionExt, _nodeIndexToGo, remapper);
            if (set?.Expressions == null || set.Expressions.Length == 0) return set;

            var controller = sceneObject.GetComponent<ExpressionController>();
            if (controller == null) controller = sceneObject.AddComponent<ExpressionController>();
            controller.Initialize(set);
            hub.Expressions = controller;
            return set;
        }

        // Stops the importer's auto-play host from auto-running the expression / reference-pose clips, which the
        // ExpressionController drives explicitly (it decodes the glTF accessors itself and never plays these Unity
        // clips). Import-only and plugin-local: it acts on the imported scene's clip objects + animation host,
        // never on the neutral wire data, the exporter, or the core importer.
        private void SuppressNonInteractiveClipAutoPlay(GameObject sceneObject, KHR_character_expression expressionExt)
        {
            var root = _context?.Root;
            var importer = _context?.SceneImporter;
            if (root == null || importer == null) return;

            // CreatedAnimationClips is { get; private set; } — act on the clip objects; never reassign the array.
            var clips = importer.CreatedAnimationClips;
            if (clips == null || clips.Length == 0) return;

            // expressionExt is parsed once in OnAfterImportScene and threaded in (avoids re-deserializing it here).
            var suppress = CollectSuppressedAnimationIndices(root, expressionExt);
            if (suppress.Count == 0) return;

            SuppressClipAutoPlay(sceneObject, clips, suppress);
        }

        // Maps the animations that must not auto-play to their animation indices: every expression's referenced
        // animation, plus ANY animation tagged KHR_character_reference_pose (alias-tolerant — a pose marker must
        // never auto-play). Indices line up 1:1 with GLTFSceneImporter.CreatedAnimationClips, which builds one clip
        // per entry in root.Animations order. Pure + internal so it is unit-testable without a full glTF import.
        internal static HashSet<int> CollectSuppressedAnimationIndices(GLTFRoot root, KHR_character_expression expressionExt)
        {
            var indices = new HashSet<int>();
            if (root?.Animations == null) return indices;
            int count = root.Animations.Count;

            if (expressionExt?.Expressions != null)
            {
                foreach (var item in expressionExt.Expressions)
                {
                    if (item == null) continue;
                    if (item.Animation >= 0 && item.Animation < count) indices.Add(item.Animation);
                }
            }

            for (int i = 0; i < count; i++)
            {
                var anim = root.Animations[i];
                if (anim?.Extensions == null) continue;
                foreach (var key in anim.Extensions.Keys)
                {
                    if (KhrCharacterExtensionNames.Canonicalize(key) == KhrCharacterExtensionNames.ReferencePose)
                    {
                        indices.Add(i);
                        break;
                    }
                }
            }
            return indices;
        }

        // Suppresses auto-play of the indexed clips without destroying them: each clip stays in
        // GLTFSceneImporter.CreatedAnimationClips and registered on its host component, so app code (or a future
        // controller) can still play it explicitly. Only the auto-play *entry points* are neutralized. Internal so
        // the behavior is unit-testable on real Unity Animation/AnimationClip objects.
        internal static void SuppressClipAutoPlay(GameObject sceneObject, AnimationClip[] createdClips, ICollection<int> suppressIndices)
        {
            if (sceneObject == null || createdClips == null || suppressIndices == null) return;

            var suppressed = new HashSet<AnimationClip>();
            foreach (var index in suppressIndices)
            {
                if (index < 0 || index >= createdClips.Length) continue;
                var clip = createdClips[index];
                if (clip == null) continue;
                // The importer imports clips as WrapMode.Loop; drop the loop so the pose/expression neither
                // auto-loops as a default state nor cycles if played explicitly. The clip object stays valid.
                clip.wrapMode = UnityEngine.WrapMode.Once;
                suppressed.Add(clip);
            }
            if (suppressed.Count == 0) return;

            // Legacy Animation host (the default AnimationMethod): only its default clip auto-plays.
            var legacy = sceneObject.GetComponent<Animation>();
            if (legacy != null) NeutralizeLegacyAnimationAutoPlay(legacy, suppressed, createdClips);

#if UNITY_EDITOR
            // Mecanim host: the importer only builds an AnimatorController (with an auto-playing default state) in
            // the editor; a player build leaves the Animator controller-less, so there is nothing to auto-play.
            var animator = sceneObject.GetComponent<Animator>();
            if (animator != null) NeutralizeAnimatorAutoPlay(animator, suppressed);
#endif
        }

        // Legacy Animation auto-plays only its default clip (Animation.clip, which the importer sets to clip 0). If
        // that default is a suppressed clip, re-point it to the first non-suppressed clip; when every clip is
        // suppressed (e.g. a face-only character) clear it and turn auto-play off. Suppressed clips stay registered
        // on the component, so Play(name) still works for explicit control.
        private static void NeutralizeLegacyAnimationAutoPlay(Animation animation, HashSet<AnimationClip> suppressed, AnimationClip[] createdClips)
        {
            var current = animation.clip;
            if (current == null || !suppressed.Contains(current)) return;

            AnimationClip replacement = null;
            foreach (var clip in createdClips)
            {
                if (clip != null && !suppressed.Contains(clip)) { replacement = clip; break; }
            }

            animation.clip = replacement;
            if (replacement == null) animation.playAutomatically = false;
        }

#if UNITY_EDITOR
        // The editor-built Mecanim controller auto-plays its state-machine default state. If that state's clip is
        // suppressed, re-point the default to a non-suppressed state; when none exists, detach the controller so
        // nothing auto-plays. Either way the clips remain in CreatedAnimationClips, reachable for explicit control.
        private static void NeutralizeAnimatorAutoPlay(Animator animator, HashSet<AnimationClip> suppressed)
        {
            if (!(animator.runtimeAnimatorController is UnityEditor.Animations.AnimatorController controller)) return;
            if (controller.layers == null || controller.layers.Length == 0) return;
            var stateMachine = controller.layers[0].stateMachine;
            if (stateMachine == null) return;

            var current = stateMachine.defaultState;
            if (current != null && current.motion is AnimationClip clip && !suppressed.Contains(clip)) return;

            foreach (var child in stateMachine.states)
            {
                if (child.state != null && child.state.motion is AnimationClip motion && !suppressed.Contains(motion))
                {
                    stateMachine.defaultState = child.state;
                    return;
                }
            }
            animator.runtimeAnimatorController = null;
        }
#endif

        private void WireNodeFeatures(GameObject sceneObject, KhrCharacter hub)
        {
            // Camera hints -> CameraHintSet (advisory; never creates or owns a camera).
            if (_cameraHints.Count > 0)
            {
                var hints = new List<CameraHint>();
                foreach (var record in _cameraHints)
                {
                    var raw = record.Hint;
                    if (record.Node == null || raw == null) continue;
                    Transform target = null;
                    if (raw.TargetNode.HasValue && _nodeIndexToGo.TryGetValue(raw.TargetNode.Value, out var tgo) && tgo != null)
                        target = tgo.transform;
                    Camera projection = null;
                    if (raw.Camera.HasValue) _cameraIndexToProjection.TryGetValue(raw.Camera.Value, out projection);
                    hints.Add(new CameraHint
                    {
                        Role = raw.Role,
                        Label = raw.Label,
                        Node = record.Node,
                        Projection = projection,
                        Target = target,
                        NodeTransformHasForwardAxisConversion = record.NodeTransformHasForwardAxisConversion,
                        ExtensionsJson = raw.Extensions?.ToString(Formatting.None),
                        ExtrasJson = raw.Extras?.ToString(Formatting.None),
                        AdditionalPropertiesJson = raw.AdditionalProperties?.ToString(Formatting.None),
                        RequiredCompanionExtensions = GetRequiredCompanionExtensions(raw.Extensions),
                    });
                }
                if (hints.Count > 0)
                {
                    var component = sceneObject.GetComponent<CameraHintSet>() ?? sceneObject.AddComponent<CameraHintSet>();
                    component.Bind(hints);
                    if (hub != null) hub.CameraHints = component;
                }
            }

            // Look-at targets remain passive metadata. A host may explicitly add/configure GazeSolver or any
            // other consumer, but importing a marker never creates behavior or writes expression weights.
            var lookTargets = new List<LookAtTarget>();
            foreach (var record in _lookatTargets)
            {
                var raw = record.Target;
                if (record.Node == null || raw == null) continue;
                lookTargets.Add(new LookAtTarget
                {
                    Node = record.Node,
                    Hint = raw.Hint,
                    ExtensionsJson = raw.Extensions?.ToString(Formatting.None),
                    ExtrasJson = raw.Extras?.ToString(Formatting.None),
                    AdditionalPropertiesJson = raw.AdditionalProperties?.ToString(Formatting.None),
                    RequiredCompanionExtensions = GetRequiredCompanionExtensions(raw.Extensions),
                });
            }
            if (lookTargets.Count > 0)
            {
                var component = sceneObject.GetComponent<LookAtTargetSet>() ?? sceneObject.AddComponent<LookAtTargetSet>();
                component.Bind(lookTargets, _lookAtRequired);
                if (hub != null) hub.LookAtTargets = component;
            }

            // ViewModeController is a passive utility (no extension required); expose it for app code.
            if (hub != null)
                hub.View = sceneObject.GetComponent<ViewModeController>() ?? sceneObject.AddComponent<ViewModeController>();
        }

        private string[] GetRequiredCompanionExtensions(Newtonsoft.Json.Linq.JObject extensions)
        {
            if (extensions == null) return System.Array.Empty<string>();
            var required = new List<string>();
            foreach (var extension in extensions.Properties())
                if (_requiredExtensions.Contains(extension.Name)) required.Add(extension.Name);
            return required.ToArray();
        }

        private void WireSkeleton(GameObject sceneObject, KhrCharacter hub)
        {
            var root = _context?.Root;
            var importer = _context?.SceneImporter;
            if (root == null) return;

            SkeletonMappingResult result = null;
            var ext = GetSkeletonMappingExtension(root);
            if (ext != null) result = KhrCharacterSkeletonBaker.BakeSkeleton(root, _nodeIndexToGo, ext);

            var referencePoses = importer != null
                ? KhrCharacterSkeletonBaker.BakeReferencePoses(root, importer, _nodeIndexToGo)
                : System.Array.Empty<ReferencePose>();
            if (result == null && referencePoses.Length == 0) return;

            // Reference poses and generic mapping sets remain independently discoverable. The singular fields
            // are optional selections used only by the Unity humanoid adapter.
            result = result ?? new SkeletonMappingResult { Bones = new Dictionary<string, Transform>() };
            result.ReferencePoses = referencePoses;
            result.ReferencePose = referencePoses.Length > 0 ? referencePoses[0] : null;

            var skeleton = sceneObject.GetComponent<SkeletonMap>() ?? sceneObject.AddComponent<SkeletonMap>();
            skeleton.Bind(result);
            // Flag the intent to (re)build + assign the humanoid Avatar when this prefab rehydrates at runtime,
            // gated on the Rig import mode: only Humanoid, and only when a bone mapping actually resolved. The
            // build self-validates required bones and falls back to the generic rig; Generic never builds one.
            if (ShouldBuildHumanoid(_rigMode, result))
                skeleton.BuildHumanoidOnAwake = true;
            hub.Skeleton = skeleton;
        }

        private KHR_character_skeleton_mapping GetSkeletonMappingExtension(GLTFRoot root)
        {
            if (root.Extensions == null) return null;
            if (!root.Extensions.TryGetValue(KhrCharacterExtensionNames.SkeletonMapping, out var ext)) return null;
            if (ext is KHR_character_skeleton_mapping typed) return typed;
            if (ext is DefaultExtension raw && raw.ExtensionData != null)
                return new KHR_character_skeleton_mapping_Factory().Deserialize(root, raw.ExtensionData) as KHR_character_skeleton_mapping;
            return null;
        }

        private KHR_node_camera_hint AsCameraHint(IExtension ext)
        {
            if (ext is KHR_node_camera_hint typed) return typed;
            if (ext is DefaultExtension raw && raw.ExtensionData != null)
                return new KHR_node_camera_hint_Factory().Deserialize(_context?.Root, raw.ExtensionData) as KHR_node_camera_hint;
            return null;
        }

        private KHR_node_lookat_target AsLookatTarget(IExtension ext)
        {
            if (ext is KHR_node_lookat_target typed) return typed;
            if (ext is DefaultExtension raw && raw.ExtensionData != null)
                return new KHR_node_lookat_target_Factory().Deserialize(_context?.Root, raw.ExtensionData) as KHR_node_lookat_target;
            return null;
        }

        // Returns the typed KHR_character_expression whether it deserialized as a typed object or was preserved
        // as raw JSON (DefaultExtension), so baking does not depend on factory registration timing.
        private static KHR_character_expression GetExpressionExtension(GLTFRoot root)
        {
            if (root.Extensions == null) return null;
            if (!root.Extensions.TryGetValue(KhrCharacterExtensionNames.Expression, out var ext)) return null;
            if (ext is KHR_character_expression typed) return typed;
            if (ext is DefaultExtension raw && raw.ExtensionData != null)
                return new KHR_character_expression_Factory().Deserialize(root, raw.ExtensionData) as KHR_character_expression;
            return null;
        }

        private static KHR_character GetCharacterExtension(GLTFRoot root)
        {
            if (root?.Extensions == null) return null;
            if (!root.Extensions.TryGetValue(KhrCharacterExtensionNames.Character, out var ext)) return null;
            if (ext is KHR_character typed) return typed;
            if (ext is DefaultExtension raw && raw.ExtensionData != null)
                return new KHR_character_Factory().Deserialize(root, raw.ExtensionData) as KHR_character;
            return null;
        }

        // Decide whether to flag the SkeletonMap to (re)build + assign the humanoid Avatar when the prefab
        // rehydrates: only in Humanoid rig mode AND only when an actual bone mapping resolved. Pure function so
        // the rig-mode gating is unit-testable without a full glTF import.
        internal static bool ShouldBuildHumanoid(RigImportMode rigMode, SkeletonMappingResult result)
            => rigMode == RigImportMode.Humanoid && result?.Bones != null && result.Bones.Count > 0;

        internal static List<CharacterCapability> DeriveCapabilities(ICollection<string> presentExtensions, CharacterExpressionSet set)
        {
            var caps = new List<CharacterCapability>();
            void AddIf(bool present, CharacterCapability c) { if (present) caps.Add(c); }
            bool Present(string name) => presentExtensions != null && presentExtensions.Contains(name);

            // Root/node extensions come from the presence scan.
            AddIf(Present(KhrCharacterExtensionNames.Character), CharacterCapability.Character);
            AddIf(Present(KhrCharacterExtensionNames.Expression), CharacterCapability.Expression);
            AddIf(Present(KhrCharacterExtensionNames.NodeCameraHint), CharacterCapability.CameraHint);
            AddIf(Present(KhrCharacterExtensionNames.NodeLookatTarget), CharacterCapability.LookAtTarget);
            AddIf(Present(KhrCharacterExtensionNames.SkeletonMapping), CharacterCapability.SkeletonMapping);

            // Per-domain sub-extensions are nested inside expression items, so report what was actually baked.
            if (set?.Expressions != null)
            {
                bool morph = false, joint = false, texture = false, mask = false;
                foreach (var t in set.Expressions)
                {
                    if (t == null) continue;
                    morph |= (t.Domains & ExpressionDomain.Morph) != 0;
                    joint |= (t.Domains & ExpressionDomain.Joint) != 0;
                    texture |= (t.Domains & ExpressionDomain.Texture) != 0;
                    mask |= t.Masks != null && t.Masks.Length > 0;
                }
                AddIf(morph, CharacterCapability.Morphtarget);
                AddIf(joint, CharacterCapability.Joint);
                AddIf(texture, CharacterCapability.Texture);
                AddIf(mask, CharacterCapability.Mask);
            }
            AddIf((set?.MappingSets != null && set.MappingSets.Length > 0)
                  || (set?.InputMappingSets != null && set.InputMappingSets.Length > 0),
                CharacterCapability.Mapping);

            return caps;
        }
    }
}
