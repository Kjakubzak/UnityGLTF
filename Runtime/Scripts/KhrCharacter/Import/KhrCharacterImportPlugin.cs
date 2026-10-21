using System.Collections.Generic;
using GLTF.Schema;
using GLTF.Schema.KHR_lights_punctual;
using Newtonsoft.Json;
using UnityEngine;
using UnityGLTF.Extensions;
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

        [Tooltip("Optional Unity host adapter. When enabled, adds an ExpressionController that drives the imported " +
                 "ExpressionResponseSet and writes its composed output to the node transforms, blendshape weights " +
                 "and material property blocks. How that output mixes with other writers is application policy. " +
                 "The passive ExpressionResponseSet, its suppressions and its mapping sets are imported either way.")]
        public bool CreateExpressionController;

        [Tooltip("Optional Unity host policy. When enabled, prevents the animations that expression entries " +
                 "reference and the reference-pose animations from becoming an automatic default animation; they " +
                 "stay explicitly playable. This changes the Unity animation host and is not extension " +
                 "conformance behavior.")]
        public bool SuppressNonInteractiveAnimationAutoPlay;

        public override GLTFImportPluginContext CreateInstance(GLTFImportContext context)
            => new KhrCharacterImportContext(
                context,
                Rig,
                CreateExpressionController,
                SuppressNonInteractiveAnimationAutoPlay);
    }

    /// <summary>
    /// Per-import instance: detects the top-level <c>KHR_character</c> designation, builds a node-index ->
    /// GameObject map, bakes expression tracks, and attaches the <see cref="KhrCharacter"/> hub and its
    /// controllers.
    /// </summary>
    public class KhrCharacterImportContext : GLTFImportPluginContext
    {
        private readonly GLTFImportContext _context;
        private readonly RigImportMode _rigMode;
        private readonly bool _createExpressionController;
        private readonly bool _suppressNonInteractiveAnimationAutoPlay;
        private bool _isCharacter;

        // UnityGLTF doesn't expose a GameObject -> node-index map, so build our own from the node callbacks.
        private readonly Dictionary<int, GameObject> _nodeIndexToGo = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, Camera> _cameraIndexToProjection = new Dictionary<int, Camera>();
        private readonly HashSet<string> _presentExtensions = new HashSet<string>();
        private readonly List<CameraHintRecord> _cameraHints = new List<CameraHintRecord>();
        private readonly List<LookAtTargetRecord> _lookatTargets = new List<LookAtTargetRecord>();
        private readonly HashSet<Transform> _cameraHintNodes = new HashSet<Transform>();
        private readonly HashSet<Transform> _lookAtTargetNodes = new HashSet<Transform>();
        private readonly Dictionary<Transform, AuthoredNodeMatrix> _matrixNodes = new Dictionary<Transform, AuthoredNodeMatrix>();
        private HashSet<string> _requiredExtensions = new HashSet<string>();
        private bool _lookAtRequired;

        private sealed class CameraHintRecord
        {
            public Transform Node;
            public int NodeIndex;
            public bool NodeUnnamed;
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
            // The hub exposes the designation to host logic, which is all KHR_character support requires.
            if (extensionName == KhrCharacterExtensionNames.Character) return true;
            if (extensionName == KhrCharacterExtensionNames.NodeLookatTarget) return true;
            // SkeletonMap exposes every tagged animation with its resolved label and values, and every mapping set
            // with each role's node index.
            if (extensionName == KhrCharacterExtensionNames.ReferencePose) return true;
            if (extensionName == KhrCharacterExtensionNames.SkeletonMapping) return true;

            // ExpressionResponseSet implements the expression pipeline: listed channels are sampled and composed
            // with exact accumulation, suppressions turn u into v, and every mapping set is exposed and computable.
            // Keep the list explicit so adding a schema factory cannot silently turn recognition into a claim.
            switch (extensionName)
            {
                case KhrCharacterExtensionNames.Expression:
                case KhrCharacterExtensionNames.ExpressionMorphtarget:
                case KhrCharacterExtensionNames.ExpressionJoint:
                case KhrCharacterExtensionNames.ExpressionTextureTransform:
                case KhrCharacterExtensionNames.ExpressionMapping:
                case KhrCharacterExtensionNames.ExpressionSuppression:
                    return true;
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
            _matrixNodes.Clear();
            _requiredExtensions = gltfRoot?.ExtensionsRequired != null
                ? new HashSet<string>(gltfRoot.ExtensionsRequired)
                : new HashSet<string>();
            _lookAtRequired = _requiredExtensions.Contains(KhrCharacterExtensionNames.NodeLookatTarget);
            if (gltfRoot?.Extensions != null)
            {
                foreach (var key in gltfRoot.Extensions.Keys)
                {
                    if (KhrCharacterExtensionNames.IsCharacterExtension(key))
                        _presentExtensions.Add(key);
                }
            }
            // Presence alone is the designation; members such as a legacy rootNode carry no meaning.
            _isCharacter = _presentExtensions.Contains(KhrCharacterExtensionNames.Character);
        }

        public override void OnAfterImportNode(Node node, int nodeIndex, GameObject nodeObject)
        {
            if (nodeObject != null) _nodeIndexToGo[nodeIndex] = nodeObject;
            if (node?.Camera != null && nodeObject != null)
            {
                var projection = nodeObject.GetComponent<Camera>();
                if (projection != null) _cameraIndexToProjection[node.Camera.Id] = projection;
            }
            // The importer instantiated this node from its matrix, whose decomposition can lose scale signs and
            // zero columns; camera hints need the authored columns.
            if (node != null && nodeObject != null && node.HasMatrix && !node.UseTRS)
                _matrixNodes[nodeObject.transform] = new AuthoredNodeMatrix
                {
                    Node = nodeObject.transform,
                    LocalMatrix = node.Matrix.ToUnityMatrix4x4Convert(),
                    ForwardAxisConversion = HasForwardAxisConversion(node, nodeObject),
                };
            if (node?.Extensions == null) return;

            // Node-level extensions (camera hint / look-at target) live on nodes; detect + record them here.
            foreach (var kv in node.Extensions)
            {
                var name = kv.Key;
                if (!KhrCharacterExtensionNames.IsCharacterExtension(name)) continue;
                if (name == KhrCharacterExtensionNames.Character)
                {
                    Debug.LogWarning(
                        $"[KHR_character] Node {nodeIndex} carries a KHR_character object, which is valid only on the " +
                        "top-level glTF object; ignoring it.");
                    continue;
                }
                _presentExtensions.Add(name);

                if (name == KhrCharacterExtensionNames.NodeCameraHint)
                {
                    var hint = AsCameraHint(kv.Value);
                    var transform = nodeObject != null ? nodeObject.transform : null;
                    if (hint != null && transform != null && _cameraHintNodes.Add(transform))
                        _cameraHints.Add(new CameraHintRecord
                        {
                            Node = transform,
                            NodeIndex = nodeIndex,
                            NodeUnnamed = string.IsNullOrEmpty(node.Name),
                            Hint = hint,
                            NodeTransformHasForwardAxisConversion = HasForwardAxisConversion(node, nodeObject),
                        });
                }
                else if (name == KhrCharacterExtensionNames.NodeLookatTarget)
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

            // Parse the response metadata once for the explicitly enabled Unity host adapters below. Neither the
            // controller nor clip suppression is implied by the extension, so both remain opt-in import policy.
            var expressionExt = _context?.Root != null ? GetExpressionExtension(_context.Root) : null;

            // The passive response set is the wire implementation: it evaluates absolute records for the channels a
            // supported derived extension lists and never writes scene targets. It is independent of the optional
            // Unity controller below.
            var responses = TryBakeExpressionResponses(sceneObject, hub, expressionExt);

            // The scene-writing adapter consumes only data that passed validation; invalid expression data binds no
            // response set, so nothing is partially applied.
            var set = _createExpressionController && responses != null
                ? TryBakeExpressions(sceneObject, hub, expressionExt, responses)
                : null;
            WireSkeleton(sceneObject, hub);
            WireNodeFeatures(sceneObject, hub);

            // An explicit host policy; ordinary imported clips are otherwise left untouched.
            if (_suppressNonInteractiveAnimationAutoPlay)
                SuppressNonInteractiveClipAutoPlay(sceneObject, expressionExt);

            var capabilities = DeriveCapabilities(_presentExtensions, set, _context?.Root?.ExtensionsUsed, expressionExt);
            if (hub.Skeleton != null && hub.Skeleton.ReferencePoses.Count > 0)
                capabilities.Add(CharacterCapability.ReferencePose);
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

            MaterialPropertiesRemapper remapper = null;
            if (_context.TryGetPlugin<AnimationPointerImportContext>(out var pointerContext))
                remapper = pointerContext.materialPropertiesRemapper;
            var options = new ExpressionResponseBakeOptions
            {
                MaterialRemapper = remapper,
                MeshoptDecoderActive = MeshoptDecoderActive,
            };

            try
            {
                var entries = KhrCharacterResponseBaker.Bake(root, importer, expressionExt, options, out var validation);
                if (entries.Length == 0) return null;

                // Indices stay expression-array indices: the set is all-or-nothing, so every entry is present.
                var identity = new Dictionary<int, int>();
                for (int index = 0; index < entries.Length; index++) identity[index] = index;
                for (int index = 0; index < entries.Length; index++)
                {
                    var suppression = expressionExt.Expressions[index]?.Suppression;
                    if (suppression != null)
                        entries[index].SetSuppressions(
                            KhrCharacterBaker.BuildSuppressionEntries(suppression, index, identity));
                }

                ExpressionMappingSet[] forward = null;
                ExpressionInputMappingSet[] input = null;
                var mappingExt = KhrCharacterBaker.GetMappingExtension(root);
                if (mappingExt != null)
                {
                    void DropSet(string message) =>
                        Debug.LogError($"[KHR_character] Expression mapping set dropped: {message}");
                    forward = KhrCharacterBaker.BuildMappingSets(mappingExt, identity, expressionExt.Expressions, DropSet);
                    input = ExpressionDriverPipeline.ToInputMappingSets(forward);
                }

                var responses = sceneObject.GetComponent<ExpressionResponseSet>();
                if (responses == null) responses = sceneObject.AddComponent<ExpressionResponseSet>();
                responses.Bind(
                    entries,
                    root.ExtensionsRequired != null &&
                    root.ExtensionsRequired.Contains(KhrCharacterExtensionNames.Expression),
                    validation,
                    forward,
                    input);
                hub.ExpressionResponses = responses;
                if (!validation.IsComplete)
                    Debug.LogWarning(
                        "[KHR_character] Expression validation is incomplete: " +
                        string.Join(" ", validation.IncompleteReasons));
                return responses;
            }
            catch (ExpressionResponseEvaluationException exception)
            {
                Debug.LogError($"[KHR_character] Expression response data is invalid: {exception.Message}");
                return null;
            }
        }

        // EXT_meshopt_compression bufferViews are decoded only when the decompression package is installed and its
        // import plugin is enabled.
        private bool MeshoptDecoderActive
        {
            get
            {
#if HAVE_MESHOPT_DECOMPRESS
                return _context != null && _context.TryGetPlugin<MeshoptImportContext>(out _);
#else
                return false;
#endif
            }
        }

        // The controller evaluates the response set through bindings to the imported scene objects. The legacy bake is
        // kept only as the controller's authoring set, for extraction and export.
        private CharacterExpressionSet TryBakeExpressions(
            GameObject sceneObject,
            KhrCharacter hub,
            KHR_character_expression expressionExt,
            ExpressionResponseSet responses)
        {
            var root = _context?.Root;
            var importer = _context?.SceneImporter;
            if (root == null || importer == null || expressionExt == null || responses.Count == 0) return null;

            MaterialPropertiesRemapper remapper = null;
            if (_context.TryGetPlugin<AnimationPointerImportContext>(out var pointerCtx))
                remapper = pointerCtx.materialPropertiesRemapper;

            var set = KhrCharacterBaker.Bake(root, importer, expressionExt, _nodeIndexToGo, remapper);
            var bindings = ExpressionTargetBindingBaker.Bake(
                root, importer, responses, _nodeIndexToGo, remapper, TranslationScale);

            var controller = sceneObject.GetComponent<ExpressionController>();
            if (controller == null) controller = sceneObject.AddComponent<ExpressionController>();
            controller.Initialize(set, responses, bindings);
            hub.Expressions = controller;
            return set;
        }

        // Stops the importer's auto-play host from auto-running the expression / reference-pose clips, which
        // expression evaluation samples itself (it decodes the glTF accessors and never plays these Unity clips).
        // Import-only and plugin-local: it acts on the imported scene's clip objects + animation host, never on the
        // neutral wire data, the exporter, or the core importer.
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
        // animation, plus ANY animation tagged KHR_character_reference_pose (a pose marker must never auto-play).
        // Indices line up 1:1 with GLTFSceneImporter.CreatedAnimationClips, which builds one clip per entry in
        // root.Animations order. Pure + internal so it is unit-testable without a full glTF import.
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
                if (anim.Extensions.ContainsKey(KhrCharacterExtensionNames.ReferencePose))
                    indices.Add(i);
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
                var authoredMatrices = new List<AuthoredNodeMatrix>();
                foreach (var record in _cameraHints)
                {
                    var raw = record.Hint;
                    if (record.Node == null || raw == null) continue;
                    // A target naming the hint's own node is not usable and defines no subject point.
                    Transform target = null;
                    if (raw.TargetNode.HasValue && raw.TargetNode.Value != record.NodeIndex
                        && _nodeIndexToGo.TryGetValue(raw.TargetNode.Value, out var tgo) && tgo != null)
                        target = tgo.transform;
                    // Projection comes only from the hint's own camera reference, never from node.camera.
                    Camera projection = null;
                    if (raw.Camera.HasValue) _cameraIndexToProjection.TryGetValue(raw.Camera.Value, out projection);
                    for (var pathNode = record.Node; pathNode != null && pathNode != sceneObject.transform; pathNode = pathNode.parent)
                        if (_matrixNodes.TryGetValue(pathNode, out var matrixNode) && !authoredMatrices.Contains(matrixNode))
                            authoredMatrices.Add(matrixNode);
                    hints.Add(new CameraHint
                    {
                        Role = raw.Role,
                        Label = raw.Label,
                        Node = record.Node,
                        NodeIndex = record.NodeIndex,
                        NodeUnnamed = record.NodeUnnamed,
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
                    component.Bind(hints, authoredMatrices);
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
                ? KhrCharacterSkeletonBaker.BakeReferencePoses(root, importer, _nodeIndexToGo, TranslationScale)
                : System.Array.Empty<ReferencePose>();
            if (result == null && referencePoses.Length == 0) return;

            // Reference poses and generic mapping sets remain independently discoverable. The singular fields
            // are optional selections used only by the Unity humanoid adapter.
            result = result ?? new SkeletonMappingResult { Bones = new Dictionary<string, Transform>() };
            result.ReferencePoses = referencePoses;
            result.ReferencePose = System.Array.Find(referencePoses, pose => pose.IsValid);

            var skeleton = sceneObject.GetComponent<SkeletonMap>() ?? sceneObject.AddComponent<SkeletonMap>();
            skeleton.Bind(result);
            // Flag the intent to (re)build + assign the humanoid Avatar when this prefab rehydrates at runtime,
            // gated on the Rig import mode: only Humanoid, and only when a bone mapping actually resolved. The
            // build self-validates required bones and falls back to the generic rig; Generic never builds one.
            if (ShouldBuildHumanoid(_rigMode, result))
                skeleton.BuildHumanoidOnAwake = true;
            hub.Skeleton = skeleton;
        }

        // UnityGLTF applies its import scale factor only in the editor: the asset importer scales every node position
        // after this plugin has run, and translation curves are scaled as they are built.
        private float TranslationScale
        {
            get
            {
#if UNITY_EDITOR
                return _context?.ImportScaleFactor ?? 1f;
#else
                return 1f;
#endif
            }
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

        // UnityGLTF turns a node's GameObject 180° about Y when it instantiates a core camera or punctual light on it.
        internal static bool HasForwardAxisConversion(Node node, GameObject nodeObject)
        {
            if (node == null || nodeObject == null) return false;
            return (node.Camera != null && nodeObject.GetComponent<Camera>() != null)
                   || (node.Extensions != null
                       && node.Extensions.ContainsKey(KHR_lights_punctualExtensionFactory.EXTENSION_NAME)
                       && nodeObject.GetComponent<Light>() != null);
        }

        // Decide whether to flag the SkeletonMap to (re)build + assign the humanoid Avatar when the prefab
        // rehydrates: only in Humanoid rig mode AND only when an actual bone mapping resolved. Pure function so
        // the rig-mode gating is unit-testable without a full glTF import.
        internal static bool ShouldBuildHumanoid(RigImportMode rigMode, SkeletonMappingResult result)
            => rigMode == RigImportMode.Humanoid && result?.Bones != null && result.Bones.Count > 0;

        /// <summary>
        /// Lists the extension capabilities an asset declares. The expression types come from
        /// <c>extensionsUsed</c>, from the extension keys of the expression entries and from an optional baked set,
        /// so they are discovered without the Unity controller.
        /// </summary>
        internal static List<CharacterCapability> DeriveCapabilities(
            ICollection<string> presentExtensions,
            CharacterExpressionSet set,
            IEnumerable<string> extensionsUsed = null,
            KHR_character_expression expressionExt = null)
        {
            var caps = new List<CharacterCapability>();
            var declared = new HashSet<string>();
            if (extensionsUsed != null)
                foreach (var name in extensionsUsed)
                    if (name != null) declared.Add(name);
            if (expressionExt?.Expressions != null)
            {
                foreach (var item in expressionExt.Expressions)
                {
                    if (item == null) continue;
                    if (item.Morphtarget != null) declared.Add(KhrCharacterExtensionNames.ExpressionMorphtarget);
                    if (item.Joint != null) declared.Add(KhrCharacterExtensionNames.ExpressionJoint);
                    if (item.TextureTransform != null) declared.Add(KhrCharacterExtensionNames.ExpressionTextureTransform);
                    if (item.Suppression != null) declared.Add(KhrCharacterExtensionNames.ExpressionSuppression);
                    if (item.RawExtensions != null)
                        foreach (var property in item.RawExtensions.Properties())
                            declared.Add(property.Name);
                }
            }

            void AddIf(bool present, CharacterCapability c) { if (present) caps.Add(c); }
            bool Present(string name) => presentExtensions != null && presentExtensions.Contains(name);
            bool Declared(string name) => declared.Contains(name) || Present(name);

            // Root/node extensions come from the presence scan.
            AddIf(Present(KhrCharacterExtensionNames.Character), CharacterCapability.Character);
            AddIf(Present(KhrCharacterExtensionNames.Expression), CharacterCapability.Expression);
            AddIf(Present(KhrCharacterExtensionNames.NodeCameraHint), CharacterCapability.CameraHint);
            AddIf(Present(KhrCharacterExtensionNames.NodeLookatTarget), CharacterCapability.LookAtTarget);
            AddIf(Present(KhrCharacterExtensionNames.SkeletonMapping), CharacterCapability.SkeletonMapping);

            bool morph = Declared(KhrCharacterExtensionNames.ExpressionMorphtarget);
            bool joint = Declared(KhrCharacterExtensionNames.ExpressionJoint);
            bool texture = Declared(KhrCharacterExtensionNames.ExpressionTextureTransform);
            bool suppression = Declared(KhrCharacterExtensionNames.ExpressionSuppression);
            bool mapping = Declared(KhrCharacterExtensionNames.ExpressionMapping);
            if (set?.Expressions != null)
            {
                foreach (var t in set.Expressions)
                {
                    if (t == null) continue;
                    morph |= (t.Domains & ExpressionDomain.Morph) != 0;
                    joint |= (t.Domains & ExpressionDomain.Joint) != 0;
                    texture |= (t.Domains & ExpressionDomain.Texture) != 0;
                    suppression |= t.Suppressions != null && t.Suppressions.Length > 0;
                }
            }
            mapping |= (set?.MappingSets != null && set.MappingSets.Length > 0)
                       || (set?.InputMappingSets != null && set.InputMappingSets.Length > 0);
            AddIf(morph, CharacterCapability.Morphtarget);
            AddIf(joint, CharacterCapability.Joint);
            AddIf(texture, CharacterCapability.TextureTransform);
            AddIf(suppression, CharacterCapability.Suppression);
            AddIf(mapping, CharacterCapability.Mapping);

            return caps;
        }
    }
}
