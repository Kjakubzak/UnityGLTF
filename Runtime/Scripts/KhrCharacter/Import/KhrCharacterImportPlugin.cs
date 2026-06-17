using System.Collections.Generic;
using GLTF.Schema;
using UnityEngine;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>How the importer treats the character's rig (mirrors FBX-style "Rig" import settings).</summary>
    public enum RigImportMode
    {
        /// <summary>Build + assign a Mecanim humanoid Avatar when the skeleton mapping resolves the required bones.</summary>
        Humanoid,
        /// <summary>Keep the generic rig; never build a humanoid Avatar.</summary>
        Generic,
    }

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

        public override GLTFImportPluginContext CreateInstance(GLTFImportContext context)
            => new KhrCharacterImportContext(context, Rig);
    }

    /// <summary>
    /// Per-import instance: detects the character, builds a node-index -> GameObject map, bakes expression
    /// tracks, and attaches the <see cref="KhrCharacter"/> hub and its controllers.
    /// </summary>
    public class KhrCharacterImportContext : GLTFImportPluginContext
    {
        private readonly GLTFImportContext _context;
        private readonly RigImportMode _rigMode;
        private bool _isCharacter;

        // UnityGLTF doesn't expose a GameObject -> node-index map, so build our own from the node callbacks.
        private readonly Dictionary<int, GameObject> _nodeIndexToGo = new Dictionary<int, GameObject>();
        private readonly HashSet<string> _presentExtensions = new HashSet<string>();
        private readonly List<(int nodeIndex, KHR_node_camera_hint hint)> _cameraHints = new List<(int, KHR_node_camera_hint)>();
        private readonly List<(int nodeIndex, KHR_node_lookat_target target)> _lookatTargets = new List<(int, KHR_node_lookat_target)>();

        public KhrCharacterImportContext(GLTFImportContext context, RigImportMode rigMode = RigImportMode.Humanoid)
        {
            _context = context;
            _rigMode = rigMode;
        }

        public override void OnAfterImportRoot(GLTFRoot gltfRoot)
        {
            // Detect by name from the root extensions. This works whether the extension deserialized to a
            // typed object or to a raw DefaultExtension.
            _presentExtensions.Clear();
            _nodeIndexToGo.Clear();
            _cameraHints.Clear();
            _lookatTargets.Clear();
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
        }

        public override void OnAfterImportNode(Node node, int nodeIndex, GameObject nodeObject)
        {
            if (!_isCharacter) return;
            _nodeIndexToGo[nodeIndex] = nodeObject;
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
                    if (hint != null) _cameraHints.Add((nodeIndex, hint));
                }
                else if (canonical == KhrCharacterExtensionNames.NodeLookatTarget)
                {
                    var target = AsLookatTarget(kv.Value);
                    if (target != null) _lookatTargets.Add((nodeIndex, target));
                }
            }
        }

        public override void OnAfterImportScene(GLTFScene scene, int sceneIndex, GameObject sceneObject)
        {
            // OnAfterImportScene is the last callback invoked at runtime (OnAfterImport is editor-only).
            if (!_isCharacter || sceneObject == null) return;

            var hub = sceneObject.GetComponent<KhrCharacter>();
            if (hub == null) hub = sceneObject.AddComponent<KhrCharacter>();

            var set = TryBakeExpressions(sceneObject, hub);
            WireSkeleton(sceneObject, hub);
            WireNodeFeatures(sceneObject, hub);

            var capabilities = DeriveCapabilities(_presentExtensions, set);
            if (hub.Skeleton?.Result?.ReferencePose != null) capabilities.Add(CharacterCapability.ReferencePose);
            hub.SetCapabilities(capabilities);

            hub.MarkReady();
        }

        private CharacterExpressionSet TryBakeExpressions(GameObject sceneObject, KhrCharacter hub)
        {
            var root = _context?.Root;
            var importer = _context?.SceneImporter;
            if (root == null || importer == null) return null;

            var expressionExt = GetExpressionExtension(root);
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

        private void WireNodeFeatures(GameObject sceneObject, KhrCharacter hub)
        {
            // Camera hints -> CameraHintSet (advisory; never creates or owns a camera).
            if (_cameraHints.Count > 0)
            {
                var hints = new List<CameraHint>();
                foreach (var (nodeIndex, raw) in _cameraHints)
                {
                    if (!_nodeIndexToGo.TryGetValue(nodeIndex, out var go) || go == null) continue;
                    Transform target = null;
                    if (raw.TargetNode.HasValue && _nodeIndexToGo.TryGetValue(raw.TargetNode.Value, out var tgo) && tgo != null)
                        target = tgo.transform;
                    hints.Add(new CameraHint { Role = raw.Role, Label = raw.Label, Node = go.transform, Target = target });
                }
                if (hints.Count > 0)
                {
                    var component = sceneObject.GetComponent<CameraHintSet>() ?? sceneObject.AddComponent<CameraHintSet>();
                    component.Bind(hints);
                    hub.CameraHints = component;
                }
            }

            // Look-at targets -> GazeSolver (also attached when expressions exist so it can drive look-* weights).
            var lookTargets = new List<LookAtTarget>();
            foreach (var (nodeIndex, raw) in _lookatTargets)
            {
                if (!_nodeIndexToGo.TryGetValue(nodeIndex, out var go) || go == null) continue;
                lookTargets.Add(new LookAtTarget { Node = go.transform, Hint = raw.Hint });
            }
            if (lookTargets.Count > 0 || hub.Expressions != null)
            {
                var gaze = sceneObject.GetComponent<GazeSolver>() ?? sceneObject.AddComponent<GazeSolver>();
                gaze.Bind(lookTargets, hub.Expressions, hub.Skeleton);
                BindLookExpressionNames(gaze, hub.Expressions);
                hub.Gaze = gaze;
            }

            // ViewModeController is a passive utility (no extension required); expose it for app code.
            hub.View = sceneObject.GetComponent<ViewModeController>() ?? sceneObject.AddComponent<ViewModeController>();
        }

        // Look-direction expression name candidates, ordered by preference. Vendor-neutral: covers common
        // spellings/conventions (camelCase, snake_case, eye/eyes/gaze prefixes), not VRM only. The first entry
        // matches the GazeSolver default.
        private static readonly string[] LookRightCandidates = { "lookRight", "look_right", "lookatRight", "eyeLookRight", "eyesLookRight", "gazeRight" };
        private static readonly string[] LookLeftCandidates  = { "lookLeft", "look_left", "lookatLeft", "eyeLookLeft", "eyesLookLeft", "gazeLeft" };
        private static readonly string[] LookUpCandidates    = { "lookUp", "look_up", "lookatUp", "eyeLookUp", "eyesLookUp", "gazeUp" };
        private static readonly string[] LookDownCandidates  = { "lookDown", "look_down", "lookatDown", "eyeLookDown", "eyesLookDown", "gazeDown" };

        // Bind each of the four look directions to whichever expression actually exists in the baked set, so the
        // gaze solver drives the model's real expression names instead of a hardcoded vocabulary. Falls back to
        // the GazeSolver default when no candidate matches (SetWeight no-ops on an unknown name -> inert). The
        // names stay public/inspector-editable, so this auto-detection is overridable. Internal for unit tests.
        internal static void BindLookExpressionNames(GazeSolver gaze, ExpressionController expressions)
        {
            if (expressions == null) return;
            var handles = expressions.Expressions;
            if (handles == null || handles.Count == 0) return;

            // Case-insensitive match -> actual baked name (ExpressionController.SetWeight is case-sensitive).
            var byLower = new Dictionary<string, string>();
            foreach (var h in handles)
                if (!string.IsNullOrEmpty(h.Name)) byLower[h.Name.ToLowerInvariant()] = h.Name;

            if (TryResolveLookName(byLower, LookRightCandidates, out var right)) gaze.LookRight = right;
            if (TryResolveLookName(byLower, LookLeftCandidates, out var left)) gaze.LookLeft = left;
            if (TryResolveLookName(byLower, LookUpCandidates, out var up)) gaze.LookUp = up;
            if (TryResolveLookName(byLower, LookDownCandidates, out var down)) gaze.LookDown = down;
        }

        private static bool TryResolveLookName(Dictionary<string, string> byLower, string[] candidates, out string matched)
        {
            foreach (var c in candidates)
                if (byLower.TryGetValue(c.ToLowerInvariant(), out matched)) return true;
            matched = null;
            return false;
        }

        private void WireSkeleton(GameObject sceneObject, KhrCharacter hub)
        {
            var root = _context?.Root;
            var importer = _context?.SceneImporter;
            if (root == null) return;

            SkeletonMappingResult result = null;
            var ext = GetSkeletonMappingExtension(root);
            if (ext != null) result = KhrCharacterSkeletonBaker.BakeSkeleton(root, _nodeIndexToGo, ext);

            var referencePose = importer != null ? KhrCharacterSkeletonBaker.BakeReferencePose(root, importer, _nodeIndexToGo) : null;
            if (result == null && referencePose == null) return;

            // A reference pose with no skeleton mapping still gets a holder so it can be applied later.
            result = result ?? new SkeletonMappingResult { Bones = new Dictionary<string, Transform>(), Direction = MappingDirection.Unknown };
            result.ReferencePose = referencePose;

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
            AddIf(set?.MappingSets != null && set.MappingSets.Length > 0, CharacterCapability.Mapping);

            return caps;
        }
    }
}
