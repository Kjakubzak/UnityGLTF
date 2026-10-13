using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityGLTF.Extensions;
using UnityGLTF.Plugins;
using static UnityGLTF.KhrCharacter.AuthoredExpressionCurves;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Export context for the KHR Character/Avatar extension set.
    /// Handles exporting KHR_character_expression (with sub-extensions), 
    /// KHR_character_skeleton_mapping, and KHR_character_reference_pose.
    /// </summary>
    public class KhrCharacterExportContext : GLTFExportPluginContext
    {
        private sealed class InvalidMappingSetIdentifierException : InvalidOperationException
        {
            public InvalidMappingSetIdentifierException()
                : base("A mapping-set identifier must be a nonempty string.") { }
        }

        private sealed class PassiveExpressionExportNotSupportedException : InvalidOperationException
        {
            public PassiveExpressionExportNotSupportedException()
                : base(
                    "Exporting a passive ExpressionResponseSet is not implemented. " +
                    "Refusing to silently discard KHR_character_expression data.") { }
        }

        private sealed class TextureTargetSignature
        {
            public NormalizedLegacySampler Sampler;
            public Vector2[] Values;
        }

        // The concrete targets of one track in write order. A target whose exported keys all equal its authored
        // initial value is constant: it is never listed and is written only when the track would otherwise have no
        // channel. Every exported channel ends at normalized time 1, so omitting the others keeps the duration.
        private sealed class TrackExportPlan
        {
            public readonly List<string> Targets = new List<string>();
            public readonly HashSet<string> ConstantTargets = new HashSet<string>(StringComparer.Ordinal);
            public string KeptUnlistedTarget;

            public void Add(string target, bool constant)
            {
                Targets.Add(target);
                if (constant) ConstantTargets.Add(target);
            }

            public bool Writes(string target) => !ConstantTargets.Contains(target) || target == KeptUnlistedTarget;

            public bool Lists(string target) => !ConstantTargets.Contains(target);
        }

        private sealed class ExpressionExportPreflight
        {
            public readonly Dictionary<TextureInfo, ExtTextureTransformExtension> TextureTransformsToAdd
                = new Dictionary<TextureInfo, ExtTextureTransformExtension>();
            public readonly Dictionary<TextureInfo, ExtTextureTransformExtension> PlannedTextureTransforms
                = new Dictionary<TextureInfo, ExtTextureTransformExtension>();
            public readonly Dictionary<string, TextureInfo> TextureInfoByTarget
                = new Dictionary<string, TextureInfo>(StringComparer.Ordinal);
            public readonly Dictionary<ExpressionTrack, TrackExportPlan> Plans
                = new Dictionary<ExpressionTrack, TrackExportPlan>();

            public void Apply()
            {
                foreach (var addition in TextureTransformsToAdd)
                    addition.Key.AddExtension(ExtTextureTransformExtensionFactory.EXTENSION_NAME, addition.Value);
            }
        }

        private readonly KhrCharacterExportPlugin _settings;
        private readonly ExportContext _context;
        private static readonly Regex CustomSuppressionTypePattern = new Regex(
            "^[A-Z0-9]+_[a-z0-9]+(?:_[a-z0-9]+)*$",
            RegexOptions.CultureInvariant);

        public KhrCharacterExportContext(KhrCharacterExportPlugin settings, ExportContext context)
        {
            _settings = settings;
            _context = context;
        }

        /// <summary>
        /// Main export hook. Called after the scene has been exported (nodes, meshes, materials,
        /// and standard animations exist). We append KHR_character extensions here.
        /// </summary>
        public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
        {
            RejectPassiveExpressionData(exporter);

            // These node annotations are independent extensions with no KHR_character dependency. Export
            // every authored set under the selected export roots even when no character designation exists.
            ExportNodeFeatures(exporter, gltfRoot);

            // Find the character root and components (edit-time safe)
            var root = FindCharacterRoot(exporter);
            if (root == null) return;

            var controller = root.GetComponentInChildren<ExpressionController>(true);
            var skeleton = root.GetComponentInChildren<SkeletonMap>(true);
            var hasDesignation = root.GetComponentInChildren<KhrCharacter>(true) != null;

            // Source of truth: prefer live, fall back to baked (edit-time safety)
            var expressionSet = controller?.Set ?? controller?.BakedSet;
            var skeletonResult = skeleton?.Result ?? skeleton?.EditorBakedResult;

            bool hasExpressions = expressionSet?.Expressions != null && expressionSet.Expressions.Length > 0;
            bool hasSkeleton = HasSkeletonMappings(skeletonResult);
            bool hasReferencePose = HasReferencePoses(skeletonResult);

            if (!hasDesignation && !hasExpressions && !hasSkeleton && !hasReferencePose)
            {
                // Nothing to export
                return;
            }

            var expressionPreflight = hasExpressions
                ? ValidateExpressionExport(exporter, gltfRoot, expressionSet)
                : null;

            // Phase 0: Emit root KHR_character extension FIRST (F2 blocker)
            // Without this, re-import fails completely (import gates on KHR_character presence)
            EmitRootCharacterExtension(exporter, gltfRoot);

            // Phases 2+3: Export expressions (interleaved: channels then metadata)
            if (hasExpressions)
            {
                expressionPreflight.Apply();
                ExportExpressions(exporter, gltfRoot, expressionSet, expressionPreflight);
            }

            // Phase 4: Export skeleton mapping
            if (hasSkeleton)
            {
                ExportSkeletonMapping(exporter, gltfRoot, skeletonResult);
            }

            // Phase 4: Export reference pose
            if (hasReferencePose)
            {
                if (skeletonResult.ReferencePoses != null && skeletonResult.ReferencePoses.Length > 0)
                {
                    foreach (var pose in skeletonResult.ReferencePoses)
                        ExportReferencePose(exporter, gltfRoot, pose);
                }
                else
                {
                    ExportReferencePose(exporter, gltfRoot, skeletonResult.ReferencePose);
                }
            }
        }

        private static bool HasSkeletonMappings(SkeletonMappingResult result)
        {
            if (result?.MappingSets != null)
                foreach (var set in result.MappingSets)
                    if (set?.Associations != null && set.Associations.Count > 0)
                        return true;
            return result?.Bones != null && result.Bones.Count > 0;
        }

        private static bool HasReferencePoses(SkeletonMappingResult result)
        {
            if (result?.ReferencePoses != null)
                foreach (var pose in result.ReferencePoses)
                    if (pose?.Bones != null && pose.Bones.Length > 0)
                        return true;
            return result?.ReferencePose?.Bones != null && result.ReferencePose.Bones.Length > 0;
        }

        private static void RejectPassiveExpressionData(GLTFSceneExporter exporter)
        {
            var roots = exporter?.RootTransforms;
            if (roots == null) return;
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var responseSet in root.GetComponentsInChildren<ExpressionResponseSet>(true))
                    if (responseSet != null && responseSet.Count > 0)
                        throw new PassiveExpressionExportNotSupportedException();
            }
        }

        // Discovery is scoped to the transforms the user actually asked to export (exporter.RootTransforms),
        // NOT a global scene scan. The previous FindObjectOfType approach returned the first matching component
        // anywhere in the loaded scene, so exporting character A could silently emit character B's expressions
        // or skeleton when both were present in the scene. Scoping to RootTransforms keeps export deterministic
        // and isolated to the requested hierarchy (mirrors how UnityGLTF's own plugins discover components, e.g.
        // MaterialVariantsPlugin). Include-inactive: imported character roots are frequently inactive at edit
        // time (consistent with AfterSceneExport's edit-time-safe intent).
        //
        // KHR_character designates at most one character per asset and names no nodes. When the export set
        // contains several character roots, the first one deterministically supplies the character components to
        // export; the others are exported as ordinary glTF content.
        private Transform FindCharacterRoot(GLTFSceneExporter exporter)
        {
            var roots = exporter?.RootTransforms;
            if (roots == null) return null;

            Transform selected = null;
            var characterRootNames = new List<string>();

            foreach (var root in roots)
            {
                if (root == null) continue;
                if (root.GetComponentInChildren<KhrCharacter>(true) == null
                    && root.GetComponentInChildren<ExpressionController>(true) == null
                    && root.GetComponentInChildren<SkeletonMap>(true) == null)
                    continue;

                if (selected == null) selected = root;   // deterministic: first in RootTransforms order wins
                characterRootNames.Add(root.name);
            }

            if (characterRootNames.Count > 1)
            {
                Debug.LogWarning(
                    $"[KHR_character] Export contains {characterRootNames.Count} character roots " +
                    $"({string.Join(", ", characterRootNames)}). KHR_character allows one character per asset; " +
                    "additional characters are out of scope and are exported as ordinary content. " +
                    $"Character data is exported from '{selected.name}'.");
            }

            return selected;
        }

        private void EmitRootCharacterExtension(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
        {
            gltfRoot.AddExtension(KHR_character.EXTENSION_NAME, new KHR_character());
            // Additive metadata: declare as USED (not REQUIRED) so plain glTF viewers that don't
            // understand KHR_character still load the asset; KHR-aware importers detect it by presence.
            exporter.DeclareExtensionUsage(KHR_character.EXTENSION_NAME, isRequired: false);
        }

        // Emits the independent KHR_node_camera_hint / KHR_node_lookat_target extensions from every passive set
        // under the selected export roots. Node identity round-trips because entries refer to ordinary exported
        // Transforms. Newly authored look-at sets are used-only; imported required provenance is preserved.
        //
        // Camera-index boundary: KHR_node_camera_hint.camera is OMITTED unless the referenced Camera was already
        // exported onto its own node (we only READ gltfRoot.Nodes[camNode].Camera.Id). GLTFSceneExporter.ExportCamera
        // is private and there is no public GetCameraId, so we never force-export a camera here. Import binds
        // Projection when the referenced definition is also instantiated by an ordinary core camera node.
        private void ExportNodeFeatures(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
        {
            var roots = exporter?.RootTransforms;
            if (roots == null) return;

            // Requiredness is a root-level declaration. Aggregate it before emitting so duplicate-set traversal
            // cannot lose imported required provenance when a used-only entry for the same node emits first.
            bool lookAtRequired = false;
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var set in root.GetComponentsInChildren<LookAtTargetSet>(true))
                    if (set != null) lookAtRequired |= set.RequiredOnImport;
            }

            var visitedCameraSets = new HashSet<CameraHintSet>();
            var visitedLookAtSets = new HashSet<LookAtTargetSet>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var cameraHints in root.GetComponentsInChildren<CameraHintSet>(true))
                {
                    if (cameraHints == null || !visitedCameraSets.Add(cameraHints) || cameraHints.Hints == null)
                        continue;
                    foreach (var hint in cameraHints.Hints)
                    {
                        if (hint?.Node == null) continue;
                        int nodeIdx = exporter.GetTransformIndex(hint.Node);
                        if (nodeIdx < 0)
                        {
                            Debug.LogWarning($"[KHR_character] Camera-hint node '{hint.Node.name}' is not part of the export; skipping.");
                            continue;
                        }

                        // KHR_node_camera_hint.role is spec-required (minLength:1); never emit an invalid object.
                        if (string.IsNullOrEmpty(hint.Role))
                        {
                            Debug.LogWarning($"[KHR_character] Camera-hint node '{hint.Node.name}' has no 'role' (spec-required, minLength:1); skipping its camera hint.");
                            continue;
                        }

                        var companionExtensions = ParseExtensionsObject(
                            hint.ExtensionsJson,
                            "Camera hint companion extensions");
                        var ext = new KHR_node_camera_hint
                        {
                            Role = hint.Role,
                            Label = hint.Label,
                            Extensions = companionExtensions,
                            Extras = ParseToken(hint.ExtrasJson),
                            AdditionalProperties = ParseObject(hint.AdditionalPropertiesJson),
                        };

                        if (hint.Target != null)
                        {
                            int targetIdx = exporter.GetTransformIndex(hint.Target);
                            // Spec: a camera hint MUST NOT reference its own node; omit self / out-of-export targets.
                            if (targetIdx >= 0 && targetIdx != nodeIdx) ext.TargetNode = targetIdx;
                        }

                        // Best-effort optional camera index (see boundary note above): only when the referenced camera's
                        // node was already exported with a camera. Never calls the private ExportCamera.
                        if (hint.Projection != null)
                        {
                            int camNodeIdx = exporter.GetTransformIndex(hint.Projection.transform);
                            if (camNodeIdx >= 0 && gltfRoot.Nodes[camNodeIdx].Camera != null)
                                ext.Camera = gltfRoot.Nodes[camNodeIdx].Camera.Id;
                        }

                        if (TryAddNodeExtension(gltfRoot, nodeIdx, KHR_node_camera_hint.EXTENSION_NAME, ext))
                        {
                            // Camera hints MUST NOT be required.
                            exporter.DeclareExtensionUsage(KHR_node_camera_hint.EXTENSION_NAME, isRequired: false);
                            DeclareCompanionExtensions(exporter, companionExtensions, hint.RequiredCompanionExtensions);
                        }
                    }
                }

                foreach (var lookAtTargets in root.GetComponentsInChildren<LookAtTargetSet>(true))
                {
                    if (lookAtTargets == null || !visitedLookAtSets.Add(lookAtTargets) || lookAtTargets.Targets == null)
                        continue;
                    foreach (var target in lookAtTargets.Targets)
                    {
                        if (target?.Node == null) continue;
                        int nodeIdx = exporter.GetTransformIndex(target.Node);
                        if (nodeIdx < 0)
                        {
                            Debug.LogWarning($"[KHR_character] Look-at target node '{target.Node.name}' is not part of the export; skipping.");
                            continue;
                        }

                        var companionExtensions = ParseExtensionsObject(
                            target.ExtensionsJson,
                            "Look-at target companion extensions");
                        // An empty {} is valid: presence alone marks the node as a look-at target; hint is optional.
                        if (TryAddNodeExtension(gltfRoot, nodeIdx, KHR_node_lookat_target.EXTENSION_NAME,
                                new KHR_node_lookat_target
                                {
                                    Hint = target.Hint,
                                    Extensions = companionExtensions,
                                    Extras = ParseToken(target.ExtrasJson),
                                    AdditionalProperties = ParseObject(target.AdditionalPropertiesJson),
                                }))
                        {
                            exporter.DeclareExtensionUsage(
                                KHR_node_lookat_target.EXTENSION_NAME,
                                isRequired: lookAtRequired);
                            DeclareCompanionExtensions(
                                exporter, companionExtensions, target.RequiredCompanionExtensions);
                        }
                    }
                }
            }
        }

        private static void DeclareCompanionExtensions(
            GLTFSceneExporter exporter, JObject extensions, string[] requiredExtensions)
        {
            if (extensions == null) return;
            var required = requiredExtensions != null
                ? new HashSet<string>(requiredExtensions)
                : new HashSet<string>();
            foreach (var extension in extensions.Properties())
                exporter.DeclareExtensionUsage(extension.Name, required.Contains(extension.Name));
        }

        // Node.AddExtension throws if the key already exists. A node may legitimately carry BOTH a camera hint and a
        // look-at target (distinct keys), but never two of the same key, so guard and skip duplicates.
        private static bool TryAddNodeExtension(GLTFRoot gltfRoot, int nodeIdx, string name, IExtension ext)
        {
            var node = gltfRoot.Nodes[nodeIdx];
            if (node.Extensions != null && node.Extensions.ContainsKey(name))
            {
                Debug.LogWarning($"[KHR_character] Node '{node.Name}' already carries extension '{name}'; skipping duplicate.");
                return false;
            }
            node.AddExtension(name, ext);
            return true;
        }

        private ExpressionExportPreflight ValidateExpressionExport(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            CharacterExpressionSet set)
        {
            if (set?.Expressions == null || set.Expressions.Length == 0)
                throw InvalidExpression("The expression set is empty.");

            var preflight = new ExpressionExportPreflight();
            var singleWriterOwners = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int expressionIndex = 0; expressionIndex < set.Expressions.Length; expressionIndex++)
            {
                var track = set.Expressions[expressionIndex];
                if (track == null)
                    throw InvalidExpression($"Expression {expressionIndex} is null.");
                if (string.IsNullOrEmpty(track.Name))
                    throw InvalidExpression($"Expression {expressionIndex} has no nonempty label.");

                var concreteTargets = new HashSet<string>();
                var textureTargets = new Dictionary<string, TextureTargetSignature>();
                var plan = new TrackExportPlan();
                if (track.MorphDrivers != null)
                    for (int driverIndex = 0; driverIndex < track.MorphDrivers.Length; driverIndex++)
                        ValidateMorphDriver(
                            exporter,
                            gltfRoot,
                            track.MorphDrivers[driverIndex],
                            $"Expression {expressionIndex} morph driver {driverIndex}",
                            concreteTargets,
                            plan);
                if (track.JointDrivers != null)
                    for (int driverIndex = 0; driverIndex < track.JointDrivers.Length; driverIndex++)
                        ValidateJointDriver(
                            exporter,
                            gltfRoot,
                            track.JointDrivers[driverIndex],
                            $"Expression {expressionIndex} joint driver {driverIndex}",
                            concreteTargets,
                            plan);
                if (track.TextureDrivers != null)
                    for (int driverIndex = 0; driverIndex < track.TextureDrivers.Length; driverIndex++)
                        ValidateTextureDriver(
                            exporter,
                            gltfRoot,
                            track.TextureDrivers[driverIndex],
                            $"Expression {expressionIndex} texture driver {driverIndex}",
                            concreteTargets,
                            textureTargets,
                            preflight,
                            plan);

                if (concreteTargets.Count == 0)
                    throw InvalidExpression($"Expression {expressionIndex} has no exportable animation channels.");
                if (plan.ConstantTargets.Count == plan.Targets.Count)
                {
                    plan.KeptUnlistedTarget = plan.Targets[0];
                    Debug.LogWarning(
                        $"[KHR_character] Expression '{track.Name}' changes no property: it is exported with one " +
                        "unlisted channel and produces no response records.");
                }
                foreach (var target in plan.Targets)
                {
                    if (!plan.Lists(target) || !IsSingleWriterTarget(target)) continue;
                    if (singleWriterOwners.TryGetValue(target, out int owner))
                        throw InvalidExpression(
                            $"Expressions {owner} ('{set.Expressions[owner].Name}') and {expressionIndex} " +
                            $"('{track.Name}') both animate '{target}', which only one expression may write. " +
                            SingleWriterHint(target));
                    singleWriterOwners.Add(target, expressionIndex);
                }
                preflight.Plans[track] = plan;
                ValidateSuppressions(set.Expressions, expressionIndex, track);
            }

            // Author a missing KHR_texture_transform only where an exported channel points at it.
            foreach (var plan in preflight.Plans.Values)
                foreach (var target in plan.Targets)
                    if (plan.Writes(target) &&
                        preflight.TextureInfoByTarget.TryGetValue(target, out var textureInfo) &&
                        preflight.PlannedTextureTransforms.TryGetValue(textureInfo, out var transform))
                        preflight.TextureTransformsToAdd[textureInfo] = transform;

            ValidateMappings(set);
            return preflight;
        }

        // Node rotation and scale and KHR_texture_transform scale are single-writer (EXP-06): at most one expression
        // may list each of them. The exporter writes no other single-writer property.
        private static bool IsSingleWriterTarget(string target)
        {
            if (target.StartsWith("/nodes/", StringComparison.Ordinal))
                return target.EndsWith("/rotation", StringComparison.Ordinal)
                       || target.EndsWith("/scale", StringComparison.Ordinal);
            return target.EndsWith(
                $"/{ExtTextureTransformExtensionFactory.EXTENSION_NAME}/{ExtTextureTransformExtensionFactory.SCALE}",
                StringComparison.Ordinal);
        }

        private static string SingleWriterHint(string target)
        {
            return target.StartsWith("/nodes/", StringComparison.Ordinal)
                ? "Give each expression its own helper node: insert one ancestor of the joint per expression at the " +
                  "joint's pivot and let each expression rotate or scale only its own helper node."
                : "Move the texture scale change into one expression; texture offsets and rotations may still add up.";
        }

        private static void ValidateMorphDriver(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            MorphDriver driver,
            string description,
            HashSet<string> concreteTargets,
            TrackExportPlan plan)
        {
            if (driver == null) throw InvalidExpression($"{description} is null.");
            if (driver.Smr == null || driver.Smr.sharedMesh == null)
                throw InvalidExpression($"{description} has no renderer mesh.");
            int nodeIndex = exporter.GetTransformIndex(driver.Smr.transform);
            var node = RequireNode(gltfRoot, nodeIndex, description);
            int morphTargetCount = GetMorphTargetCount(node, description);
            if (driver.Smr.sharedMesh.blendShapeCount != morphTargetCount)
                throw InvalidExpression(
                    $"{description} renderer has {driver.Smr.sharedMesh.blendShapeCount} blendshapes; " +
                    $"the exported node has {morphTargetCount} morph targets.");
            if (driver.BlendShapeIndex < 0 || driver.BlendShapeIndex >= morphTargetCount)
                throw InvalidExpression($"{description} targets an invalid morph index {driver.BlendShapeIndex}.");

            var sampler = NormalizeLegacySampler(driver.Sampler, description);
            float authoredInitial = ResolveMorphWeights(node, morphTargetCount, description)[driver.BlendShapeIndex];
            var values = BuildMorphValues(driver, sampler, authoredInitial, description);
            string target = MorphTarget(nodeIndex, driver);
            AddUniqueTarget(concreteTargets, target, description);
            bool constant = true;
            foreach (float value in values) constant &= value == authoredInitial;
            plan.Add(target, constant);
        }

        private static string MorphTarget(int nodeIndex, MorphDriver driver)
            => $"/nodes/{nodeIndex}/weights/{driver.BlendShapeIndex}";

        private static void ValidateJointDriver(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            JointDriver driver,
            string description,
            HashSet<string> concreteTargets,
            TrackExportPlan plan)
        {
            if (driver == null) throw InvalidExpression($"{description} is null.");
            if (driver.Target == null) throw InvalidExpression($"{description} has no target transform.");
            int nodeIndex = exporter.GetTransformIndex(driver.Target);
            var node = RequireNode(gltfRoot, nodeIndex, description);
            if (node.HasMatrix || (node.Matrix != null && !node.Matrix.Equals(GLTF.Math.Matrix4x4.Identity)))
                throw InvalidExpression($"{description} targets TRS on a matrix-backed node.");

            var sampler = NormalizeLegacySampler(driver.Sampler, description);
            string path;
            bool constant = true;
            switch (driver.Channel)
            {
                case TrsChannel.Translation:
                {
                    path = "translation";
                    var values = BuildJointVectorValues(driver, sampler, description);
                    var authored = new[] { node.Translation.X, node.Translation.Y, node.Translation.Z };
                    RequireEquivalent(authored, ToGltfTranslation(values[0]), $"{description} time-zero translation");
                    foreach (var value in values) constant &= SameComponents(authored, ToGltfTranslation(value));
                    break;
                }
                case TrsChannel.Scale:
                {
                    path = "scale";
                    var values = BuildJointVectorValues(driver, sampler, description);
                    var authored = new[] { node.Scale.X, node.Scale.Y, node.Scale.Z };
                    RequireEquivalent(authored, ToComponents(values[0]), $"{description} time-zero scale");
                    foreach (var value in values) constant &= SameComponents(authored, ToComponents(value));
                    break;
                }
                case TrsChannel.Rotation:
                {
                    path = "rotation";
                    var values = BuildJointRotationValues(driver, sampler, description);
                    var authored = new[] { node.Rotation.X, node.Rotation.Y, node.Rotation.Z, node.Rotation.W };
                    if (!ExpressionInitialValueValidation.QuaternionsEquivalent(
                            authored,
                            ToGltfRotation(values[0]),
                            ExpressionAccessorComponentEncoding.Float))
                        throw InvalidExpression($"{description} time-zero rotation does not match the exported node.");
                    foreach (var value in values)
                    {
                        var exported = ToGltfRotation(value);
                        constant &= SameComponents(authored, exported) || SameComponents(authored, Negated(exported));
                    }
                    break;
                }
                default:
                    throw InvalidExpression($"{description} has unknown TRS channel {driver.Channel}.");
            }

            string target = $"/nodes/{nodeIndex}/{path}";
            AddUniqueTarget(concreteTargets, target, description);
            plan.Add(target, constant);
        }

        private void ValidateTextureDriver(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            TextureDriver driver,
            string description,
            HashSet<string> concreteTargets,
            Dictionary<string, TextureTargetSignature> textureTargets,
            ExpressionExportPreflight preflight,
            TrackExportPlan plan)
        {
            if (driver == null) throw InvalidExpression($"{description} is null.");
            if (driver.Renderer == null) throw InvalidExpression($"{description} has no renderer.");
            if (string.IsNullOrEmpty(driver.GltfTextureSlot))
                throw InvalidExpression($"{description} has no glTF texture slot.");
            if (driver.TransformTarget != TextureTransformTarget.Combined &&
                driver.TransformTarget != TextureTransformTarget.Scale &&
                driver.TransformTarget != TextureTransformTarget.Offset)
                throw InvalidExpression($"{description} has an unknown texture-transform target.");

            var materials = driver.Renderer.sharedMaterials;
            if (materials == null || driver.SubmeshSlot < 0 || driver.SubmeshSlot >= materials.Length)
                throw InvalidExpression($"{description} has an invalid material slot.");
            var material = materials[driver.SubmeshSlot];
            if (material == null) throw InvalidExpression($"{description} has no material.");
            var materialId = exporter.GetMaterialId(gltfRoot, material);
            if (materialId == null || gltfRoot.Materials == null || materialId.Id < 0 || materialId.Id >= gltfRoot.Materials.Count)
                throw InvalidExpression($"{description} material is not part of the export.");

            var textureInfo = ResolveTextureInfo(gltfRoot.Materials[materialId.Id], driver.GltfTextureSlot);
            if (textureInfo?.Index == null)
                throw InvalidExpression(
                    $"{description} does not resolve to an authored TextureInfo at '{driver.GltfTextureSlot}'.");

            var sampler = NormalizeLegacySampler(driver.Sampler, description);
            var curves = BuildTextureCurves(driver, sampler, description);
            var textureTransform = ResolveOrPlanTextureTransform(
                gltfRoot,
                textureInfo,
                driver.BaseSt,
                description,
                preflight);
            var expectedScale = new[] { textureTransform.Scale.X, textureTransform.Scale.Y };
            var expectedOffset = new[] { textureTransform.Offset.X, textureTransform.Offset.Y };
            string basePath =
                $"/materials/{materialId.Id}/{driver.GltfTextureSlot}/extensions/" +
                $"{ExtTextureTransformExtensionFactory.EXTENSION_NAME}/";

            if (driver.TransformTarget != TextureTransformTarget.Offset)
            {
                RequireEquivalent(expectedScale, ToComponents(curves.Scale[0]), $"{description} time-zero scale");
                ValidateTextureTarget(
                    basePath + ExtTextureTransformExtensionFactory.SCALE,
                    sampler,
                    curves.Scale,
                    expectedScale,
                    description,
                    concreteTargets,
                    textureTargets,
                    plan);
                preflight.TextureInfoByTarget[basePath + ExtTextureTransformExtensionFactory.SCALE] = textureInfo;
            }
            if (driver.TransformTarget != TextureTransformTarget.Scale)
            {
                RequireEquivalent(expectedOffset, ToComponents(curves.Offset[0]), $"{description} time-zero offset");
                ValidateTextureTarget(
                    basePath + ExtTextureTransformExtensionFactory.OFFSET,
                    sampler,
                    curves.Offset,
                    expectedOffset,
                    description,
                    concreteTargets,
                    textureTargets,
                    plan);
                preflight.TextureInfoByTarget[basePath + ExtTextureTransformExtensionFactory.OFFSET] = textureInfo;
            }
        }

        private static void ValidateTextureTarget(
            string target,
            NormalizedLegacySampler sampler,
            Vector2[] values,
            float[] authored,
            string description,
            HashSet<string> concreteTargets,
            Dictionary<string, TextureTargetSignature> textureTargets,
            TrackExportPlan plan)
        {
            if (textureTargets.TryGetValue(target, out var existing))
            {
                if (!SameSampler(existing.Sampler, sampler) || !SameValues(existing.Values, values))
                    throw InvalidExpression(
                        $"{description} conflicts with another texture driver for concrete target '{target}'.");
                return;
            }
            if (!concreteTargets.Add(target))
                throw InvalidExpression($"{description} duplicates concrete target '{target}'.");
            textureTargets[target] = new TextureTargetSignature { Sampler = sampler, Values = values };
            bool constant = true;
            foreach (var value in values) constant &= SameComponents(authored, ToComponents(value));
            plan.Add(target, constant);
        }

        private static void ValidateSuppressions(ExpressionTrack[] expressions, int expressionIndex, ExpressionTrack track)
        {
            bool hasSuppressions = track.Suppressions != null && track.Suppressions.Length > 0;
            if (!hasSuppressions)
            {
                if (HasCompanionPayload(
                        track.SuppressionExtensionsJson,
                        track.SuppressionExtrasJson,
                        track.SuppressionAdditionalPropertiesJson,
                        track.SuppressionRequiredCompanionExtensions))
                    throw InvalidExpression(
                        $"Expression {expressionIndex} has suppression metadata but no suppression entries.");
                return;
            }

            var companionExtensionNames = new HashSet<string>();
            AddExtensionNames(
                companionExtensionNames,
                ParseExtensionsObject(
                    track.SuppressionExtensionsJson,
                    $"Expression {expressionIndex} suppression companion extensions"));
            ParseToken(track.SuppressionExtrasJson);
            ParseObject(track.SuppressionAdditionalPropertiesJson);
            for (int suppressionIndex = 0; suppressionIndex < track.Suppressions.Length; suppressionIndex++)
            {
                var suppression = track.Suppressions[suppressionIndex];
                string description = $"Expression {expressionIndex} suppression {suppressionIndex}";
                if (suppression == null) throw InvalidExpression($"{description} is null.");
                if (suppression.TargetIndex < 0 || suppression.TargetIndex >= expressions.Length
                    || expressions[suppression.TargetIndex] == null)
                    throw InvalidExpression($"{description} references invalid expression {suppression.TargetIndex}.");
                if (!string.IsNullOrEmpty(suppression.CustomType))
                {
                    if (!CustomSuppressionTypePattern.IsMatch(suppression.CustomType))
                        throw InvalidExpression($"{description} custom type '{suppression.CustomType}' is invalid.");
                }
                else if (suppression.Type != SuppressionType.Blend && suppression.Type != SuppressionType.Block)
                {
                    throw InvalidExpression($"{description} identity type has no custom token.");
                }
                if (WritesAmount(suppression)) RequireUnit(suppression.Amount, $"{description} amount");
                if (WritesThreshold(suppression)) RequireUnit(suppression.Threshold, $"{description} threshold");
                AddExtensionNames(
                    companionExtensionNames,
                    ParseExtensionsObject(suppression.RawExtensionsJson, $"{description} companion extensions"));
                ParseToken(suppression.RawExtrasJson);
                ParseObject(suppression.RawAdditionalPropertiesJson);
            }
            ValidateRequiredCompanionExtensions(
                track.SuppressionRequiredCompanionExtensions,
                companionExtensionNames,
                $"Expression {expressionIndex} suppression metadata");
        }

        private static string SuppressionTypeToken(SuppressionEntry suppression)
        {
            if (!string.IsNullOrEmpty(suppression.CustomType)) return suppression.CustomType;
            return suppression.Type == SuppressionType.Block ? "block" : "blend";
        }

        // Pre-rename serialized masks, inspector edits and direct field writes can leave a presence flag clear on a
        // non-default value that the runtime still applies. Such a value counts as authored; a value omitted on
        // import keeps its default and stays omitted.
        private static bool WritesAmount(SuppressionEntry suppression)
            => suppression.HasAmount || suppression.Amount != 1d;

        private static bool WritesThreshold(SuppressionEntry suppression)
            => (suppression.HasThreshold || suppression.Threshold != 0d)
               && KHR_character_expression_suppression.AllowsThreshold(SuppressionTypeToken(suppression));

        private static void ValidateMappings(CharacterExpressionSet set)
        {
            bool hasForwardMappings = set.MappingSets != null && set.MappingSets.Length > 0;
            bool hasInputMappings = set.InputMappingSets != null && set.InputMappingSets.Length > 0;
            if (!hasForwardMappings && !hasInputMappings &&
                HasCompanionPayload(
                    set.MappingExtensionsJson,
                    set.MappingExtrasJson,
                    set.MappingAdditionalPropertiesJson,
                    set.MappingRequiredCompanionExtensions))
                throw InvalidExpression("The expression set has mapping metadata but no mapping operations.");

            var companionExtensionNames = new HashSet<string>();
            AddExtensionNames(
                companionExtensionNames,
                ParseExtensionsObject(set.MappingExtensionsJson, "Mapping companion extensions"));
            ParseToken(set.MappingExtrasJson);
            ParseObject(set.MappingAdditionalPropertiesJson);

            var forwardNames = new HashSet<string>();
            if (set.MappingSets != null)
                for (int setIndex = 0; setIndex < set.MappingSets.Length; setIndex++)
                {
                    var mappingSet = set.MappingSets[setIndex];
                    string description = $"Forward mapping set {setIndex}";
                    if (mappingSet?.Targets == null || mappingSet.Targets.Length == 0)
                        throw InvalidExpression($"{description} is empty.");
                    ValidateMappingIdentifier(mappingSet.SetName);
                    if (!forwardNames.Add(mappingSet.SetName))
                        throw InvalidExpression($"{description} duplicates identifier '{mappingSet.SetName}'.");
                    var endpointNames = new HashSet<string>();
                    for (int targetIndex = 0; targetIndex < mappingSet.Targets.Length; targetIndex++)
                    {
                        var target = mappingSet.Targets[targetIndex];
                        string targetDescription = $"{description} endpoint {targetIndex}";
                        if (target?.Contributions == null || target.Contributions.Length == 0 ||
                            string.IsNullOrEmpty(target.TargetName))
                            throw InvalidExpression($"{targetDescription} is empty.");
                        if (!endpointNames.Add(target.TargetName))
                            throw InvalidExpression($"{targetDescription} duplicates key '{target.TargetName}'.");
                        for (int contributionIndex = 0; contributionIndex < target.Contributions.Length; contributionIndex++)
                        {
                            var contribution = target.Contributions[contributionIndex];
                            string contributionDescription = $"{targetDescription} contribution {contributionIndex}";
                            ValidateExpressionReference(
                                set.Expressions,
                                contribution.SourceIndex,
                                contribution.Name,
                                contributionDescription);
                            RequireUnit(contribution.Weight, $"{contributionDescription} weight");
                            AddExtensionNames(
                                companionExtensionNames,
                                ParseExtensionsObject(
                                    contribution.ExtensionsJson,
                                    $"{contributionDescription} companion extensions"));
                            ParseToken(contribution.ExtrasJson);
                            ParseObject(contribution.AdditionalPropertiesJson);
                        }
                    }
                }

            var inputNames = new HashSet<string>();
            if (set.InputMappingSets != null)
                for (int setIndex = 0; setIndex < set.InputMappingSets.Length; setIndex++)
                {
                    var mappingSet = set.InputMappingSets[setIndex];
                    string description = $"Input mapping set {setIndex}";
                    if (mappingSet?.Commands == null || mappingSet.Commands.Length == 0)
                        throw InvalidExpression($"{description} is empty.");
                    ValidateMappingIdentifier(mappingSet.SetName);
                    if (!inputNames.Add(mappingSet.SetName))
                        throw InvalidExpression($"{description} duplicates identifier '{mappingSet.SetName}'.");
                    var commandNames = new HashSet<string>();
                    for (int commandIndex = 0; commandIndex < mappingSet.Commands.Length; commandIndex++)
                    {
                        var command = mappingSet.Commands[commandIndex];
                        string commandDescription = $"{description} command {commandIndex}";
                        if (command?.Contributions == null || command.Contributions.Length == 0 ||
                            string.IsNullOrEmpty(command.CommandName))
                            throw InvalidExpression($"{commandDescription} is empty.");
                        if (!commandNames.Add(command.CommandName))
                            throw InvalidExpression($"{commandDescription} duplicates key '{command.CommandName}'.");
                        for (int contributionIndex = 0; contributionIndex < command.Contributions.Length; contributionIndex++)
                        {
                            var contribution = command.Contributions[contributionIndex];
                            string contributionDescription = $"{commandDescription} contribution {contributionIndex}";
                            ValidateExpressionReference(
                                set.Expressions,
                                contribution.TargetIndex,
                                contribution.Name,
                                contributionDescription);
                            RequireUnit(contribution.Weight, $"{contributionDescription} weight");
                            AddExtensionNames(
                                companionExtensionNames,
                                ParseExtensionsObject(
                                    contribution.ExtensionsJson,
                                    $"{contributionDescription} companion extensions"));
                            ParseToken(contribution.ExtrasJson);
                            ParseObject(contribution.AdditionalPropertiesJson);
                        }
                    }
                }

            ValidateRequiredCompanionExtensions(
                set.MappingRequiredCompanionExtensions,
                companionExtensionNames,
                "Mapping metadata");
        }

        private static void ValidateExpressionReference(
            ExpressionTrack[] expressions,
            int expressionIndex,
            string diagnosticName,
            string description)
        {
            if (expressionIndex < 0 || expressionIndex >= expressions.Length || expressions[expressionIndex] == null)
                throw InvalidExpression($"{description} references invalid expression {expressionIndex}.");
            if (diagnosticName != null
                && !string.Equals(diagnosticName, expressions[expressionIndex].Name, StringComparison.Ordinal))
                throw InvalidExpression($"{description} name does not match expression {expressionIndex}.");
        }

        private static void ValidateMappingIdentifier(string identifier)
        {
            if (!KHR_character_expression_mapping.IsValidMappingSetIdentifier(identifier))
                throw new InvalidMappingSetIdentifierException();
        }

        private static Node RequireNode(GLTFRoot root, int nodeIndex, string description)
        {
            if (root?.Nodes == null || nodeIndex < 0 || nodeIndex >= root.Nodes.Count || root.Nodes[nodeIndex] == null)
                throw InvalidExpression($"{description} target is not part of the export.");
            return root.Nodes[nodeIndex];
        }

        private static int GetMorphTargetCount(Node node, string description)
        {
            var mesh = node.Mesh?.Value;
            if (mesh?.Primitives == null || mesh.Primitives.Count == 0)
                throw InvalidExpression($"{description} target node has no mesh primitives.");
            int count = -1;
            foreach (var primitive in mesh.Primitives)
            {
                int primitiveCount = primitive?.Targets?.Count ?? 0;
                if (count < 0) count = primitiveCount;
                else if (count != primitiveCount)
                    throw InvalidExpression($"{description} mesh primitives have inconsistent morph-target counts.");
            }
            if (count < 1) throw InvalidExpression($"{description} target mesh has no morph targets.");
            return count;
        }

        private static float[] ResolveMorphWeights(Node node, int count, string description)
        {
            IReadOnlyList<double> source = node.Weights ?? node.Mesh?.Value?.Weights;
            var result = new float[count];
            if (source == null) return result;
            if (source.Count != count)
                throw InvalidExpression($"{description} authored weight count does not match its morph targets.");
            for (int index = 0; index < count; index++)
            {
                result[index] = (float)source[index];
                RequireFinite(result[index], $"{description} authored weight {index}");
            }
            return result;
        }

        private static TextureInfo ResolveTextureInfo(GLTFMaterial material, string slot)
        {
            if (material == null) return null;
            switch (slot)
            {
                case "pbrMetallicRoughness/baseColorTexture":
                    return material.PbrMetallicRoughness?.BaseColorTexture;
                case "pbrMetallicRoughness/metallicRoughnessTexture":
                    return material.PbrMetallicRoughness?.MetallicRoughnessTexture;
                case "normalTexture":
                    return material.NormalTexture;
                case "occlusionTexture":
                    return material.OcclusionTexture;
                case "emissiveTexture":
                    return material.EmissiveTexture;
                default:
                    return null;
            }
        }

        private static ExtTextureTransformExtension ResolveOrPlanTextureTransform(
            GLTFRoot root,
            TextureInfo textureInfo,
            Vector4 baseSt,
            string description,
            ExpressionExportPreflight preflight)
        {
            ExtTextureTransformExtension transform = null;
            if (textureInfo.Extensions != null &&
                textureInfo.Extensions.TryGetValue(ExtTextureTransformExtensionFactory.EXTENSION_NAME, out var extension))
            {
                transform = extension as ExtTextureTransformExtension;
                if (transform == null)
                {
                    try
                    {
                        transform = new ExtTextureTransformExtensionFactory().Deserialize(root, extension.Serialize())
                            as ExtTextureTransformExtension;
                    }
                    catch (Exception exception)
                    {
                        throw InvalidExpression($"{description} has an invalid KHR_texture_transform: {exception.Message}");
                    }
                }
            }
            else if (!preflight.PlannedTextureTransforms.TryGetValue(textureInfo, out transform))
            {
                transform = TextureTransformFromSt(baseSt);
                preflight.PlannedTextureTransforms.Add(textureInfo, transform);
            }

            if (transform == null) throw InvalidExpression($"{description} could not resolve KHR_texture_transform.");
            var transformSt = StFromTextureTransform(transform);
            RequireEquivalent(
                ToComponents(baseSt),
                ToComponents(transformSt),
                $"{description} static texture transform");
            return transform;
        }

        private static ExtTextureTransformExtension TextureTransformFromSt(Vector4 st)
        {
            var scale = new GLTF.Math.Vector2(st.x, st.y);
            var offset = new GLTF.Math.Vector2(st.z, 1f - st.w - st.y);
            return new ExtTextureTransformExtension(offset, 0d, scale, null);
        }

        private static Vector4 StFromTextureTransform(ExtTextureTransformExtension transform)
        {
            return new Vector4(
                transform.Scale.X,
                transform.Scale.Y,
                transform.Offset.X,
                1f - transform.Offset.Y - transform.Scale.Y);
        }

        private static void AddUniqueTarget(HashSet<string> targets, string target, string description)
        {
            if (!targets.Add(target))
                throw InvalidExpression($"{description} duplicates concrete target '{target}'.");
        }

        private static bool SameSampler(NormalizedLegacySampler left, NormalizedLegacySampler right)
        {
            if (left.Interpolation != right.Interpolation || left.Times.Length != right.Times.Length) return false;
            for (int index = 0; index < left.Times.Length; index++)
                if (left.Times[index] != right.Times[index]) return false;
            return true;
        }

        private static bool SameValues(Vector2[] left, Vector2[] right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
                if (left[index].x != right[index].x || left[index].y != right[index].y) return false;
            return true;
        }

        private static void RequireUnit(double value, string description)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d || value > 1d)
                throw InvalidExpression($"{description} must be finite and in [0, 1].");
        }

        private void ExportExpressions(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            CharacterExpressionSet set,
            ExpressionExportPreflight preflight)
        {
            var expressions = new List<KHR_character_expression.ExpressionItem>();
            var mappingDict = new Dictionary<string, Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>>();
            var inputMappingDict = new Dictionary<string, Dictionary<string, List<KHR_character_expression_mapping.TargetWeight>>>();
            var runtimeToWireIndex = new Dictionary<int, int>();
            var pendingSuppressions = new Dictionary<KHR_character_expression.ExpressionItem, ExpressionTrack>();

            // Track which nested expression sub-extensions are actually emitted, so each can be declared once in
            // extensionsUsed (B1: nested KHR_character_expression_* were written on items but never declared).
            bool anyMorph = false, anyJoint = false, anyTexture = false, anySuppression = false;

            // Process each expression track
            for (int trackIndex = 0; trackIndex < set.Expressions.Length; trackIndex++)
            {
                var track = set.Expressions[trackIndex];
                if (track == null || string.IsNullOrEmpty(track.Name)) continue;

                // Create one animation per expression
                var anim = new GLTFAnimation { Name = track.Name };
                var morphChannels = new List<int>();
                var jointChannels = new List<int>();
                var texChannels = new List<int>();
                var emittedTextureTargets = new HashSet<string>();
                var plan = preflight.Plans[track];

                // Write driver channels and capture the listed indices (Phase 3)
                if (track.MorphDrivers != null)
                    foreach (var driver in track.MorphDrivers)
                        WriteMorphDriver(exporter, gltfRoot, anim, driver, plan, morphChannels);

                if (track.JointDrivers != null)
                    foreach (var driver in track.JointDrivers)
                        WriteJointDriver(exporter, gltfRoot, anim, driver, plan, jointChannels);

                if (track.TextureDrivers != null)
                    foreach (var driver in track.TextureDrivers)
                        WriteTextureDriver(exporter, gltfRoot, anim, driver, emittedTextureTargets, plan, texChannels);

                // Skip empty expressions
                if (anim.Channels.Count == 0) continue;

                gltfRoot.Animations.Add(anim);
                int animIndex = gltfRoot.Animations.Count - 1;

                // Build expression metadata (Phase 2)
                var expressionItem = new KHR_character_expression.ExpressionItem
                {
                    Name = track.Name,
                    Animation = animIndex
                };
                runtimeToWireIndex[trackIndex] = expressions.Count;

                if (morphChannels.Count > 0)
                {
                    expressionItem.Morphtarget =
                        new KHR_character_expression_morphtarget { Channels = morphChannels.ToArray() };
                    anyMorph = true;
                }

                if (jointChannels.Count > 0)
                {
                    expressionItem.Joint =
                        new KHR_character_expression_joint { Channels = jointChannels.ToArray() };
                    anyJoint = true;
                }

                if (texChannels.Count > 0)
                {
                    expressionItem.TextureTransform =
                        new KHR_character_expression_texture_transform { Channels = texChannels.ToArray() };
                    anyTexture = true;
                }

                if (track.Suppressions != null && track.Suppressions.Length > 0)
                    pendingSuppressions[expressionItem] = track;

                expressions.Add(expressionItem);
            }

            foreach (var pending in pendingSuppressions)
            {
                var suppressions = new List<KHR_character_expression_suppression.Suppression>();
                foreach (var suppression in pending.Value.Suppressions)
                {
                    if (suppression == null) continue;
                    if (!runtimeToWireIndex.TryGetValue(suppression.TargetIndex, out int targetIndex)) continue;
                    var companionExtensions = ParseExtensionsObject(
                        suppression.RawExtensionsJson,
                        "Suppression entry companion extensions");
                    suppressions.Add(new KHR_character_expression_suppression.Suppression
                    {
                        Target = targetIndex,
                        Type = SuppressionTypeToken(suppression),
                        Amount = WritesAmount(suppression) ? suppression.Amount : (double?)null,
                        Threshold = WritesThreshold(suppression) ? suppression.Threshold : (double?)null,
                        Extensions = companionExtensions,
                        Extras = ParseToken(suppression.RawExtrasJson),
                        AdditionalProperties = ParseObject(suppression.RawAdditionalPropertiesJson),
                    });
                    DeclareCompanionExtensions(
                        exporter, companionExtensions, pending.Value.SuppressionRequiredCompanionExtensions);
                }
                if (suppressions.Count > 0)
                {
                    var companionExtensions = ParseExtensionsObject(
                        pending.Value.SuppressionExtensionsJson,
                        "Suppression extension companion extensions");
                    pending.Key.Suppression = new KHR_character_expression_suppression
                    {
                        Suppressions = suppressions,
                        Extensions = companionExtensions,
                        Extras = ParseToken(pending.Value.SuppressionExtrasJson),
                        AdditionalProperties = ParseObject(pending.Value.SuppressionAdditionalPropertiesJson),
                    };
                    DeclareCompanionExtensions(
                        exporter, companionExtensions, pending.Value.SuppressionRequiredCompanionExtensions);
                    anySuppression = true;
                }
            }

            // Build mapping sets after expression filtering so runtime indices can be remapped to wire indices.
            if (set.MappingSets != null)
            {
                foreach (var mappingSet in set.MappingSets)
                {
                    if (mappingSet?.Targets == null) continue;
                    ValidateMappingIdentifier(mappingSet.SetName);
                    var setDict = new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>();
                    foreach (var target in mappingSet.Targets)
                    {
                        if (target?.Contributions == null || string.IsNullOrEmpty(target.TargetName)) continue;
                        var contributions = new List<KHR_character_expression_mapping.SourceWeight>();
                        foreach (var contrib in target.Contributions)
                        {
                            if (!runtimeToWireIndex.TryGetValue(contrib.SourceIndex, out int sourceIndex)) continue;
                            var companionExtensions = ParseExtensionsObject(
                                contrib.ExtensionsJson,
                                "Forward mapping contribution companion extensions");
                            contributions.Add(new KHR_character_expression_mapping.SourceWeight
                            {
                                Source = sourceIndex,
                                Name = contrib.Name,
                                Weight = contrib.Weight,
                                Extensions = companionExtensions,
                                Extras = ParseToken(contrib.ExtrasJson),
                                AdditionalProperties = ParseObject(contrib.AdditionalPropertiesJson),
                            });
                            DeclareCompanionExtensions(
                                exporter, companionExtensions, set.MappingRequiredCompanionExtensions);
                        }
                        if (contributions.Count > 0) setDict[target.TargetName] = contributions;
                    }
                    if (setDict.Count > 0) mappingDict[mappingSet.SetName] = setDict;
                }
            }

            if (set.InputMappingSets != null)
            {
                foreach (var mappingSet in set.InputMappingSets)
                {
                    if (mappingSet?.Commands == null) continue;
                    ValidateMappingIdentifier(mappingSet.SetName);
                    var setDict = new Dictionary<string, List<KHR_character_expression_mapping.TargetWeight>>();
                    foreach (var command in mappingSet.Commands)
                    {
                        if (command?.Contributions == null || string.IsNullOrEmpty(command.CommandName)) continue;
                        var contributions = new List<KHR_character_expression_mapping.TargetWeight>();
                        foreach (var contribution in command.Contributions)
                        {
                            if (!runtimeToWireIndex.TryGetValue(contribution.TargetIndex, out int targetIndex)) continue;
                            var companionExtensions = ParseExtensionsObject(
                                contribution.ExtensionsJson,
                                "Input mapping contribution companion extensions");
                            contributions.Add(new KHR_character_expression_mapping.TargetWeight
                            {
                                Target = targetIndex,
                                Name = contribution.Name,
                                Weight = contribution.Weight,
                                Extensions = companionExtensions,
                                Extras = ParseToken(contribution.ExtrasJson),
                                AdditionalProperties = ParseObject(contribution.AdditionalPropertiesJson),
                            });
                            DeclareCompanionExtensions(
                                exporter, companionExtensions, set.MappingRequiredCompanionExtensions);
                        }
                        if (contributions.Count > 0) setDict[command.CommandName] = contributions;
                    }
                    if (setDict.Count > 0) inputMappingDict[mappingSet.SetName] = setDict;
                }
            }

            if (expressions.Count > 0)
            {
                var rootExtension = new KHR_character_expression
                {
                    Expressions = expressions
                };
                gltfRoot.AddExtension(KHR_character_expression.EXTENSION_NAME, rootExtension);
                exporter.DeclareExtensionUsage(KHR_character_expression.EXTENSION_NAME);

                // B1 fix: declare each emitted nested sub-extension in extensionsUsed (deduped by
                // DeclareExtensionUsage), never required — consistent with the parent KHR_character_expression.
                // Only those actually emitted on at least one expression item are declared.
                if (anyMorph) exporter.DeclareExtensionUsage(KHR_character_expression_morphtarget.EXTENSION_NAME);
                if (anyJoint) exporter.DeclareExtensionUsage(KHR_character_expression_joint.EXTENSION_NAME);
                if (anyTexture) exporter.DeclareExtensionUsage(KHR_character_expression_texture_transform.EXTENSION_NAME);
                if (anySuppression) exporter.DeclareExtensionUsage(KHR_character_expression_suppression.EXTENSION_NAME);
            }

            if (mappingDict.Count > 0 || inputMappingDict.Count > 0)
            {
                var mappingExtension = new KHR_character_expression_mapping
                {
                    ExpressionSetMappings = mappingDict,
                    ExpressionSetInputMappings = inputMappingDict,
                    Extensions = ParseExtensionsObject(
                        set.MappingExtensionsJson,
                        "Mapping extension companion extensions"),
                    Extras = ParseToken(set.MappingExtrasJson),
                    AdditionalProperties = ParseObject(set.MappingAdditionalPropertiesJson),
                };
                gltfRoot.AddExtension(KHR_character_expression_mapping.EXTENSION_NAME, mappingExtension);
                exporter.DeclareExtensionUsage(KHR_character_expression_mapping.EXTENSION_NAME);
                DeclareCompanionExtensions(
                    exporter, mappingExtension.Extensions, set.MappingRequiredCompanionExtensions);
            }
        }

        private static bool HasCompanionPayload(
            string extensionsJson,
            string extrasJson,
            string additionalPropertiesJson,
            string[] requiredExtensions)
        {
            return !string.IsNullOrEmpty(extensionsJson) ||
                   !string.IsNullOrEmpty(extrasJson) ||
                   !string.IsNullOrEmpty(additionalPropertiesJson) ||
                   (requiredExtensions != null && requiredExtensions.Length > 0);
        }

        private static void AddExtensionNames(HashSet<string> names, JObject extensions)
        {
            if (extensions == null) return;
            foreach (var extension in extensions.Properties()) names.Add(extension.Name);
        }

        private static void ValidateRequiredCompanionExtensions(
            string[] requiredExtensions,
            HashSet<string> authoredExtensions,
            string description)
        {
            if (requiredExtensions == null) return;
            foreach (var requiredExtension in requiredExtensions)
            {
                if (string.IsNullOrEmpty(requiredExtension) || !authoredExtensions.Contains(requiredExtension))
                    throw InvalidExpression(
                        $"{description} requires companion extension '{requiredExtension}' but does not author it.");
            }
        }

        private static JObject ParseExtensionsObject(string json, string description)
        {
            var extensions = ParseObject(json);
            if (extensions == null) return null;
            foreach (var extension in extensions.Properties())
                if (extension.Value == null || extension.Value.Type != JTokenType.Object)
                    throw InvalidExpression(
                        $"{description} value for '{extension.Name}' must be a JSON object.");
            return extensions;
        }

        private static JObject ParseObject(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return JObject.Parse(json);
        }

        private static JToken ParseToken(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return JToken.Parse(json);
        }

        private void WriteMorphDriver(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            GLTFAnimation anim,
            MorphDriver driver,
            TrackExportPlan plan,
            List<int> listedChannels)
        {
            int nodeIdx = exporter.GetTransformIndex(driver.Smr.transform);
            string concreteTarget = MorphTarget(nodeIdx, driver);
            if (!plan.Writes(concreteTarget)) return;
            var normalizedSampler = NormalizeLegacySampler(driver.Sampler, "Morph driver");
            var node = RequireNode(gltfRoot, nodeIdx, "Morph driver");
            int morphTargetCount = GetMorphTargetCount(node, "Morph driver");
            float authoredInitial = ResolveMorphWeights(node, morphTargetCount, "Morph driver")[driver.BlendShapeIndex];
            float[] absVals = BuildMorphValues(driver, normalizedSampler, authoredInitial, "Morph driver");

            // Use per-blendshape pointer (Pattern B)
            AccessorId inputAcc = exporter.ExportAccessor(normalizedSampler.Times);
            AccessorId outputAcc = exporter.ExportAccessor(absVals);

            var sampler = new AnimationSampler
            {
                Input = inputAcc,
                Output = outputAcc,
                Interpolation = normalizedSampler.Interpolation
            };

            var target = new AnimationChannelTarget { Path = "pointer" };
            var ptr = new KHR_animation_pointer
            {
                path = $"/nodes/{nodeIdx}/weights/{driver.BlendShapeIndex}"
            };
            target.AddExtension(KHR_animation_pointer.EXTENSION_NAME, ptr);

            anim.Samplers.Add(sampler);
            anim.Channels.Add(new AnimationChannel
            {
                Sampler = new AnimationSamplerId
                {
                    Id = anim.Samplers.Count - 1,
                    GLTFAnimation = anim,
                    Root = gltfRoot
                },
                Target = target
            });
            if (plan.Lists(concreteTarget)) listedChannels.Add(anim.Channels.Count - 1);
            exporter.DeclareExtensionUsage(KHR_animation_pointer.EXTENSION_NAME);
        }

        // F7: joints are written as MANUAL native glTF TRS channels (target.Node + path
        // translation/rotation/scale), NOT via AddAnimationData, which under UseAnimationPointer rewrites the channel
        // into a `pointer` channel. Native TRS channels need no KHR_animation_pointer in either export mode.
        private void WriteJointDriver(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            GLTFAnimation anim,
            JointDriver driver,
            TrackExportPlan plan,
            List<int> listedChannels)
        {
            string path = driver.Channel switch
            {
                TrsChannel.Translation => "translation",
                TrsChannel.Rotation => "rotation",
                TrsChannel.Scale => "scale",
                _ => null
            };
            if (path == null) throw InvalidExpression($"Joint driver has unknown TRS channel {driver.Channel}.");

            int nodeIndex = exporter.GetTransformIndex(driver.Target);
            string concreteTarget = $"/nodes/{nodeIndex}/{path}";
            if (!plan.Writes(concreteTarget)) return;
            RequireNode(gltfRoot, nodeIndex, "Joint driver");
            var normalizedSampler = NormalizeLegacySampler(driver.Sampler, "Joint driver");
            AccessorId outputAcc;

            if (driver.Channel == TrsChannel.Rotation)
            {
                Quaternion[] absVals = BuildJointRotationValues(driver, normalizedSampler, "Joint driver");
                outputAcc = ExportRotationAccessor(exporter, absVals);
            }
            else
            {
                Vector3[] absVals = BuildJointVectorValues(driver, normalizedSampler, "Joint driver");
                outputAcc = ExportVectorAccessor(exporter, absVals, flipHandedness: driver.Channel == TrsChannel.Translation);
            }

            EmitNativeTrsChannel(exporter, gltfRoot, anim, nodeIndex, path,
                exporter.ExportAccessor(normalizedSampler.Times), outputAcc, normalizedSampler.Interpolation);
            if (plan.Lists(concreteTarget)) listedChannels.Add(anim.Channels.Count - 1);
        }

        // Translation/scale output accessor (VEC3). Translation gets the Unity->glTF (-1,1,1) X-flip; scale is raw.
        // ExportAccessor(Vector3[]) stamps a 12-byte ByteStride, which is invalid for an animation sampler output,
        // so reset it to 0 (mirrors GLTFSceneExporter.AddAnimationData).
        private static AccessorId ExportVectorAccessor(GLTFSceneExporter exporter, Vector3[] values, bool flipHandedness)
        {
            if (flipHandedness)
                for (int i = 0; i < values.Length; i++) values[i].Scale(new Vector3(-1f, 1f, 1f));
            var acc = exporter.ExportAccessor(values);
            acc.Value.BufferView.Value.ByteStride = 0;
            return acc;
        }

        // Rotation output accessor (VEC4). Matches the standard animation export handedness (SwitchHandedness);
        // the VEC4 accessor overload already uses ByteStride 0.
        private static AccessorId ExportRotationAccessor(GLTFSceneExporter exporter, Quaternion[] values)
        {
            var v4 = new Vector4[values.Length];
            for (int i = 0; i < v4.Length; i++)
            {
                var q = values[i].SwitchHandedness().normalized;
                v4[i] = new Vector4(q.x, q.y, q.z, q.w);
            }
            return exporter.ExportAccessor(v4);
        }

        // Builds a native glTF AnimationSampler + AnimationChannel targeting node `nodeIndex` at `path`
        // (translation/rotation/scale) and appends them to `anim`.
        private void EmitNativeTrsChannel(GLTFSceneExporter exporter, GLTFRoot gltfRoot, GLTFAnimation anim,
            int nodeIndex, string path, AccessorId inputAcc, AccessorId outputAcc, InterpolationType interpolation)
        {
            anim.Samplers.Add(new AnimationSampler
            {
                Input = inputAcc,
                Output = outputAcc,
                Interpolation = interpolation
            });
            anim.Channels.Add(new AnimationChannel
            {
                Sampler = new AnimationSamplerId
                {
                    Id = anim.Samplers.Count - 1,
                    GLTFAnimation = anim,
                    Root = gltfRoot
                },
                Target = new AnimationChannelTarget
                {
                    Path = path,
                    Node = new NodeId { Id = nodeIndex, Root = gltfRoot }
                }
            });
        }

        // Emits the animation channels for a texture driver via KHR_animation_pointer, mirroring the import baker
        // (KhrCharacterBaker.BakeTextureChannels). The captured channel indices populate the expression item's
        // KHR_character_expression_texture_transform listing. GltfTextureSlot is the full glTF slot path the importer
        // parses (e.g. "pbrMetallicRoughness/baseColorTexture") — populated at bake (G-B).
        private void WriteTextureDriver(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            GLTFAnimation anim,
            TextureDriver driver,
            HashSet<string> emittedTargets,
            TrackExportPlan plan,
            List<int> listedChannels)
        {
            var normalizedSampler = NormalizeLegacySampler(driver.Sampler, "Texture driver");
            var curves = BuildTextureCurves(driver, normalizedSampler, "Texture driver");

            // Resolve the glTF material index for the renderer's material at this submesh slot.
            var materials = driver.Renderer.sharedMaterials;
            if (materials == null || driver.SubmeshSlot < 0 || driver.SubmeshSlot >= materials.Length)
                throw InvalidExpression("Texture driver has an invalid material slot.");
            var material = materials[driver.SubmeshSlot];
            if (material == null) throw InvalidExpression("Texture driver has no material.");
            var matId = exporter.GetMaterialId(gltfRoot, material);
            if (matId == null)
                throw InvalidExpression($"Texture driver material '{material.name}' is not part of the export.");

            WriteUvTransformChannels(
                exporter,
                gltfRoot,
                anim,
                driver,
                matId.Id,
                normalizedSampler,
                curves,
                emittedTargets,
                plan,
                listedChannels);
        }

        // UV transform: reconstruct the absolute Unity _ST per key (inverse of the import bake), unpack into glTF
        // scale/offset (the KHR_texture_transform V-flip), and emit a KHR_animation_pointer channel for each target
        // the plan writes:
        //   /materials/{m}/{slot}/extensions/KHR_texture_transform/{scale,offset}
        private void WriteUvTransformChannels(GLTFSceneExporter exporter, GLTFRoot gltfRoot, GLTFAnimation anim,
            TextureDriver driver,
            int materialIndex,
            NormalizedLegacySampler sampler,
            TextureCurves curves,
            HashSet<string> emittedTargets,
            TrackExportPlan plan,
            List<int> listedChannels)
        {
            string basePath = $"/materials/{materialIndex}/{driver.GltfTextureSlot}/extensions/{ExtTextureTransformExtensionFactory.EXTENSION_NAME}/";
            bool emitted = false;
            string scaleTarget = basePath + ExtTextureTransformExtensionFactory.SCALE;
            if (driver.TransformTarget != TextureTransformTarget.Offset && plan.Writes(scaleTarget) &&
                emittedTargets.Add(scaleTarget))
            {
                EmitPointerChannel(exporter, gltfRoot, anim, sampler.Times, exporter.ExportAccessor(curves.Scale),
                    scaleTarget, sampler.Interpolation);
                if (plan.Lists(scaleTarget)) listedChannels.Add(anim.Channels.Count - 1);
                emitted = true;
            }
            string offsetTarget = basePath + ExtTextureTransformExtensionFactory.OFFSET;
            if (driver.TransformTarget != TextureTransformTarget.Scale && plan.Writes(offsetTarget) &&
                emittedTargets.Add(offsetTarget))
            {
                EmitPointerChannel(exporter, gltfRoot, anim, sampler.Times, exporter.ExportAccessor(curves.Offset),
                    offsetTarget, sampler.Interpolation);
                if (plan.Lists(offsetTarget)) listedChannels.Add(anim.Channels.Count - 1);
                emitted = true;
            }

            if (emitted)
            {
                exporter.DeclareExtensionUsage(KHR_animation_pointer.EXTENSION_NAME);
                // Additive (never required): a plain viewer must still load the asset.
                exporter.DeclareExtensionUsage(ExtTextureTransformExtensionFactory.EXTENSION_NAME);
            }
        }

        // Builds a KHR_animation_pointer AnimationSampler + AnimationChannel (target.Path = "pointer") and appends
        // them to `anim` (mirrors WriteMorphDriver). The input (time) accessor gets min/max for free from
        // ExportAccessor(float[]) (ExporterAccessors.cs), satisfying the glTF sampler-input min/max requirement.
        private void EmitPointerChannel(GLTFSceneExporter exporter, GLTFRoot gltfRoot, GLTFAnimation anim,
            float[] times, AccessorId outputAcc, string pointerPath, InterpolationType interpolation)
        {
            var target = new AnimationChannelTarget { Path = "pointer" };
            target.AddExtension(KHR_animation_pointer.EXTENSION_NAME, new KHR_animation_pointer { path = pointerPath });

            anim.Samplers.Add(new AnimationSampler
            {
                Input = exporter.ExportAccessor(times),
                Output = outputAcc,
                Interpolation = interpolation
            });
            anim.Channels.Add(new AnimationChannel
            {
                Sampler = new AnimationSamplerId
                {
                    Id = anim.Samplers.Count - 1,
                    GLTFAnimation = anim,
                    Root = gltfRoot
                },
                Target = target
            });
        }

        private void ExportSkeletonMapping(GLTFSceneExporter exporter, GLTFRoot gltfRoot, SkeletonMappingResult result)
        {
            var mappings = new Dictionary<string, Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>>();
            if (result.MappingSets != null && result.MappingSets.Length > 0)
            {
                foreach (var set in result.MappingSets)
                {
                    if (set == null || !KHR_character_expression_mapping.IsValidMappingSetIdentifier(set.Identifier))
                        continue;
                    var associations = ExportAssociations(exporter, set.Associations);
                    if (associations.Count > 0) mappings[set.Identifier] = associations;
                }
            }
            else if (result.Bones != null && result.Bones.Count > 0)
            {
                var identifier = result.SelectedRig;
                if (KHR_character_expression_mapping.IsValidMappingSetIdentifier(identifier))
                {
                    var associations = ExportAssociations(exporter, result.Bones);
                    if (associations.Count > 0) mappings[identifier] = associations;
                }
            }

            if (mappings.Count == 0) return;

            var mapping = new KHR_character_skeleton_mapping
            {
                SkeletalRigMappings = mappings
            };
            gltfRoot.AddExtension(KHR_character_skeleton_mapping.EXTENSION_NAME, mapping);
            exporter.DeclareExtensionUsage(KHR_character_skeleton_mapping.EXTENSION_NAME);
        }

        private static Dictionary<string, KHR_character_skeleton_mapping.JointAssociation> ExportAssociations(
            GLTFSceneExporter exporter, Dictionary<string, Transform> associations)
        {
            var exported = new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>();
            if (associations == null) return exported;
            foreach (var association in associations)
            {
                if (string.IsNullOrEmpty(association.Key) || association.Value == null) continue;
                int nodeIndex = exporter.GetTransformIndex(association.Value);
                if (nodeIndex < 0) continue;
                exported[association.Key] = new KHR_character_skeleton_mapping.JointAssociation
                {
                    Node = nodeIndex,
                    Name = association.Value.name,
                };
            }
            return exported;
        }

        // F7: the reference pose is written as MANUAL native glTF TRS channels (single STEP key at t=0), NOT via
        // AddAnimationData. As with joints, AddAnimationData under UseAnimationPointer would convert these to
        // `pointer` channels (target.Node = null), which KhrCharacterSkeletonBaker.BakeReferencePose (native-TRS
        // only) silently drops. Native channels survive re-import in both export modes.
        private void ExportReferencePose(GLTFSceneExporter exporter, GLTFRoot gltfRoot, ReferencePose pose)
        {
            if (pose == null) return;
            string poseType = string.IsNullOrEmpty(pose.PoseType) ? ReferencePoseLabels.TPose : pose.PoseType;
            var anim = new GLTFAnimation { Name = $"ReferencePose_{poseType}" };
            if (pose.Bones == null) return;

            int n = pose.Bones.Length;
            bool hasPos = pose.LocalPositions != null && pose.LocalPositions.Length >= n;
            bool hasRot = pose.LocalRotations != null && pose.LocalRotations.Length >= n;
            bool hasScl = pose.LocalScales != null && pose.LocalScales.Length >= n;

            for (int i = 0; i < n; i++)
            {
                if (pose.Bones[i] == null) continue;
                int nodeIndex = exporter.GetTransformIndex(pose.Bones[i]);
                if (nodeIndex < 0)
                {
                    Debug.LogWarning($"[KHR_character] Reference-pose bone '{pose.Bones[i].name}' is not part of the export; skipping.");
                    continue;
                }

                // Single keyframe at t=0, STEP interpolation (a pose marker, not an animated track).
                if (hasPos)
                    EmitNativeTrsChannel(exporter, gltfRoot, anim, nodeIndex, "translation",
                        exporter.ExportAccessor(new[] { 0f }),
                        ExportVectorAccessor(exporter, new[] { pose.LocalPositions[i] }, flipHandedness: true), InterpolationType.STEP);
                if (hasRot)
                    EmitNativeTrsChannel(exporter, gltfRoot, anim, nodeIndex, "rotation",
                        exporter.ExportAccessor(new[] { 0f }),
                        ExportRotationAccessor(exporter, new[] { pose.LocalRotations[i] }), InterpolationType.STEP);
                if (hasScl)
                    EmitNativeTrsChannel(exporter, gltfRoot, anim, nodeIndex, "scale",
                        exporter.ExportAccessor(new[] { 0f }),
                        ExportVectorAccessor(exporter, new[] { pose.LocalScales[i] }, flipHandedness: false), InterpolationType.STEP);
            }

            if (anim.Channels.Count > 0)
            {
                anim.AddExtension(KHR_character_reference_pose.EXTENSION_NAME,
                    new KHR_character_reference_pose { PoseType = poseType });
                gltfRoot.Animations.Add(anim);
                exporter.DeclareExtensionUsage(KHR_character_reference_pose.EXTENSION_NAME);
            }
        }

    }
}
