using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityGLTF.Extensions;
using UnityGLTF.Plugins;

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
            public InvalidMappingSetIdentifierException(string identifier)
                : base($"Mapping-set identifier '{identifier}' is not a valid absolute URI.") { }
        }

        private sealed class PassiveExpressionExportNotSupportedException : InvalidOperationException
        {
            public PassiveExpressionExportNotSupportedException()
                : base(
                    "Exporting a passive ExpressionResponseSet is not implemented. " +
                    "Refusing to silently discard KHR_character_expression data.") { }
        }

        private sealed class NormalizedLegacySampler
        {
            public float[] Times;
            public int SourceKeyCount;
            public bool SynthesizedSingleKey;
            public InterpolationType Interpolation;
        }

        private sealed class TextureCurves
        {
            public Vector2[] Scale;
            public Vector2[] Offset;
        }

        private sealed class TextureTargetSignature
        {
            public NormalizedLegacySampler Sampler;
            public Vector2[] Values;
        }

        private sealed class ExpressionExportPreflight
        {
            public readonly Dictionary<TextureInfo, ExtTextureTransformExtension> TextureTransformsToAdd
                = new Dictionary<TextureInfo, ExtTextureTransformExtension>();

            public void Apply()
            {
                foreach (var addition in TextureTransformsToAdd)
                    addition.Key.AddExtension(ExtTextureTransformExtensionFactory.EXTENSION_NAME, addition.Value);
            }
        }

        private readonly KhrCharacterExportPlugin _settings;
        private readonly ExportContext _context;
        private static readonly Regex CustomMaskTypePattern = new Regex(
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
            EmitRootCharacterExtension(exporter, gltfRoot, root);

            // Phases 2+3: Export expressions (interleaved: channels then metadata)
            if (hasExpressions)
            {
                expressionPreflight.Apply();
                ExportExpressions(exporter, gltfRoot, expressionSet);
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
        // KHR_character provides one author-selected root designation. When the export set contains several
        // character components, choose one deterministically for that designation and leave the others as
        // ordinary glTF content; the extension does not assert that the asset contains only one character.
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
                    $"({string.Join(", ", characterRootNames)}). Designating '{selected.name}' as rootNode; " +
                    "additional roots remain ordinary glTF content unless exported separately with their own designation.");
            }

            return selected;
        }

        private void EmitRootCharacterExtension(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform root)
        {
            int rootNodeIndex = exporter.GetTransformIndex(root);
            if (rootNodeIndex < 0)
            {
                Debug.LogWarning("[KHR_character] Could not resolve character root node index, skipping root extension");
                return;
            }

            var rootExtension = new KHR_character
            {
                RootNode = rootNodeIndex
            };
            gltfRoot.AddExtension(KHR_character.EXTENSION_NAME, rootExtension);
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
            for (int expressionIndex = 0; expressionIndex < set.Expressions.Length; expressionIndex++)
            {
                var track = set.Expressions[expressionIndex];
                if (track == null)
                    throw InvalidExpression($"Expression {expressionIndex} is null.");
                if (string.IsNullOrEmpty(track.Name))
                    throw InvalidExpression($"Expression {expressionIndex} has no nonempty label.");

                var concreteTargets = new HashSet<string>();
                var textureTargets = new Dictionary<string, TextureTargetSignature>();
                if (track.MorphDrivers != null)
                    for (int driverIndex = 0; driverIndex < track.MorphDrivers.Length; driverIndex++)
                        ValidateMorphDriver(
                            exporter,
                            gltfRoot,
                            track.MorphDrivers[driverIndex],
                            $"Expression {expressionIndex} morph driver {driverIndex}",
                            concreteTargets);
                if (track.JointDrivers != null)
                    for (int driverIndex = 0; driverIndex < track.JointDrivers.Length; driverIndex++)
                        ValidateJointDriver(
                            exporter,
                            gltfRoot,
                            track.JointDrivers[driverIndex],
                            $"Expression {expressionIndex} joint driver {driverIndex}",
                            concreteTargets);
                if (track.TextureDrivers != null)
                    for (int driverIndex = 0; driverIndex < track.TextureDrivers.Length; driverIndex++)
                        ValidateTextureDriver(
                            exporter,
                            gltfRoot,
                            track.TextureDrivers[driverIndex],
                            $"Expression {expressionIndex} texture driver {driverIndex}",
                            concreteTargets,
                            textureTargets,
                            preflight);

                if (concreteTargets.Count == 0)
                    throw InvalidExpression($"Expression {expressionIndex} has no exportable animation channels.");
                ValidateMasks(set.Expressions, expressionIndex, track);
            }

            ValidateMappings(set);
            return preflight;
        }

        private static void ValidateMorphDriver(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            MorphDriver driver,
            string description,
            HashSet<string> concreteTargets)
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
            BuildMorphValues(driver, sampler, authoredInitial, description);
            AddUniqueTarget(
                concreteTargets,
                $"/nodes/{nodeIndex}/weights/{driver.BlendShapeIndex}",
                description);
        }

        private static void ValidateJointDriver(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            JointDriver driver,
            string description,
            HashSet<string> concreteTargets)
        {
            if (driver == null) throw InvalidExpression($"{description} is null.");
            if (driver.Target == null) throw InvalidExpression($"{description} has no target transform.");
            int nodeIndex = exporter.GetTransformIndex(driver.Target);
            var node = RequireNode(gltfRoot, nodeIndex, description);
            if (node.HasMatrix || (node.Matrix != null && !node.Matrix.Equals(GLTF.Math.Matrix4x4.Identity)))
                throw InvalidExpression($"{description} targets TRS on a matrix-backed node.");

            var sampler = NormalizeLegacySampler(driver.Sampler, description);
            string path;
            switch (driver.Channel)
            {
                case TrsChannel.Translation:
                {
                    path = "translation";
                    var values = BuildJointVectorValues(driver, sampler, description);
                    RequireEquivalent(
                        new[] { node.Translation.X, node.Translation.Y, node.Translation.Z },
                        ToGltfTranslation(values[0]),
                        $"{description} time-zero translation");
                    break;
                }
                case TrsChannel.Scale:
                {
                    path = "scale";
                    var values = BuildJointVectorValues(driver, sampler, description);
                    RequireEquivalent(
                        new[] { node.Scale.X, node.Scale.Y, node.Scale.Z },
                        ToComponents(values[0]),
                        $"{description} time-zero scale");
                    break;
                }
                case TrsChannel.Rotation:
                {
                    path = "rotation";
                    var values = BuildJointRotationValues(driver, sampler, description);
                    var first = ToGltfRotation(values[0]);
                    if (!ExpressionInitialValueValidation.QuaternionsEquivalent(
                            new[] { node.Rotation.X, node.Rotation.Y, node.Rotation.Z, node.Rotation.W },
                            first,
                            ExpressionAccessorComponentEncoding.Float))
                        throw InvalidExpression($"{description} time-zero rotation does not match the exported node.");
                    break;
                }
                default:
                    throw InvalidExpression($"{description} has unknown TRS channel {driver.Channel}.");
            }

            AddUniqueTarget(concreteTargets, $"/nodes/{nodeIndex}/{path}", description);
        }

        private void ValidateTextureDriver(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            TextureDriver driver,
            string description,
            HashSet<string> concreteTargets,
            Dictionary<string, TextureTargetSignature> textureTargets,
            ExpressionExportPreflight preflight)
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
                    description,
                    concreteTargets,
                    textureTargets);
            }
            if (driver.TransformTarget != TextureTransformTarget.Scale)
            {
                RequireEquivalent(expectedOffset, ToComponents(curves.Offset[0]), $"{description} time-zero offset");
                ValidateTextureTarget(
                    basePath + ExtTextureTransformExtensionFactory.OFFSET,
                    sampler,
                    curves.Offset,
                    description,
                    concreteTargets,
                    textureTargets);
            }
        }

        private static void ValidateTextureTarget(
            string target,
            NormalizedLegacySampler sampler,
            Vector2[] values,
            string description,
            HashSet<string> concreteTargets,
            Dictionary<string, TextureTargetSignature> textureTargets)
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
        }

        private static void ValidateMasks(ExpressionTrack[] expressions, int expressionIndex, ExpressionTrack track)
        {
            bool hasMasks = track.Masks != null && track.Masks.Length > 0;
            if (!hasMasks)
            {
                if (HasCompanionPayload(
                        track.MaskExtensionsJson,
                        track.MaskExtrasJson,
                        track.MaskAdditionalPropertiesJson,
                        track.MaskRequiredCompanionExtensions))
                    throw InvalidExpression(
                        $"Expression {expressionIndex} has mask metadata but no mask entries.");
                return;
            }

            var companionExtensionNames = new HashSet<string>();
            AddExtensionNames(
                companionExtensionNames,
                ParseExtensionsObject(
                    track.MaskExtensionsJson,
                    $"Expression {expressionIndex} mask companion extensions"));
            ParseToken(track.MaskExtrasJson);
            ParseObject(track.MaskAdditionalPropertiesJson);
            for (int maskIndex = 0; maskIndex < track.Masks.Length; maskIndex++)
            {
                var mask = track.Masks[maskIndex];
                string description = $"Expression {expressionIndex} mask {maskIndex}";
                if (mask == null) throw InvalidExpression($"{description} is null.");
                if (mask.TargetIndex < 0 || mask.TargetIndex >= expressions.Length || expressions[mask.TargetIndex] == null)
                    throw InvalidExpression($"{description} references invalid expression {mask.TargetIndex}.");
                if (mask.Name != null && mask.Name != expressions[mask.TargetIndex].Name)
                    throw InvalidExpression($"{description} name does not match its target expression.");
                RequireUnit(mask.Amount, $"{description} amount");
                RequireUnit(mask.Threshold, $"{description} threshold");
                if (!string.IsNullOrEmpty(mask.CustomType))
                {
                    if (!CustomMaskTypePattern.IsMatch(mask.CustomType))
                        throw InvalidExpression($"{description} custom type '{mask.CustomType}' is invalid.");
                }
                else if (mask.Type != MaskType.Blend && mask.Type != MaskType.Block)
                {
                    throw InvalidExpression($"{description} identity type has no custom token.");
                }
                AddExtensionNames(
                    companionExtensionNames,
                    ParseExtensionsObject(mask.RawExtensionsJson, $"{description} companion extensions"));
                ParseToken(mask.RawExtrasJson);
                ParseObject(mask.RawAdditionalPropertiesJson);
            }
            ValidateRequiredCompanionExtensions(
                track.MaskRequiredCompanionExtensions,
                companionExtensionNames,
                $"Expression {expressionIndex} mask metadata");
        }

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
            if (diagnosticName != null && diagnosticName != expressions[expressionIndex].Name)
                throw InvalidExpression($"{description} name does not match expression {expressionIndex}.");
        }

        private static void ValidateMappingIdentifier(string identifier)
        {
            if (!KHR_character_expression_mapping.IsValidMappingSetIdentifier(identifier))
                throw new InvalidMappingSetIdentifierException(identifier);
        }

        private static NormalizedLegacySampler NormalizeLegacySampler(Sampler sampler, string description)
        {
            var source = sampler.Times;
            if (source == null) throw InvalidExpression($"{description} has no input times.");
            if (sampler.SingleKey)
            {
                if (source.Length != 1 || !IsFinite(source[0]) || source[0] < 0f)
                    throw InvalidExpression($"{description} has invalid legacy single-key input.");
                return new NormalizedLegacySampler
                {
                    Times = new[] { 0f, 1f },
                    SourceKeyCount = 1,
                    SynthesizedSingleKey = true,
                    Interpolation = InterpolationType.LINEAR,
                };
            }
            if (source.Length < 2)
                throw InvalidExpression($"{description} must contain at least two input keys.");

            float previous = source[0];
            if (!IsFinite(previous) || previous < 0f)
                throw InvalidExpression($"{description} input key 0 must be finite and nonnegative.");
            for (int index = 1; index < source.Length; index++)
            {
                float current = source[index];
                if (!IsFinite(current) || current < 0f || current <= previous)
                    throw InvalidExpression($"{description} input keys must be finite, nonnegative, and strictly increasing.");
                previous = current;
            }

            InterpolationType interpolation;
            switch (sampler.Interp)
            {
                case Interp.Step:
                    interpolation = InterpolationType.STEP;
                    break;
                case Interp.Linear:
                case Interp.CubicSpline:
                    interpolation = InterpolationType.LINEAR;
                    break;
                default:
                    throw InvalidExpression($"{description} has unknown interpolation {sampler.Interp}.");
            }

            float first = source[0];
            float duration = source[source.Length - 1] - first;
            var normalized = new float[source.Length];
            normalized[0] = 0f;
            for (int index = 1; index < normalized.Length; index++)
            {
                float value = index == normalized.Length - 1
                    ? 1f
                    : (source[index] - first) / duration;
                if (!IsFinite(value) || value <= normalized[index - 1])
                    throw InvalidExpression(
                        $"{description} input keys collapse after normalization to response progress.");
                normalized[index] = value;
            }
            return new NormalizedLegacySampler
            {
                Times = normalized,
                SourceKeyCount = source.Length,
                Interpolation = interpolation,
            };
        }

        private static float[] BuildMorphValues(
            MorphDriver driver,
            NormalizedLegacySampler sampler,
            float authoredInitial,
            string description)
        {
            if (!IsFinite(driver.BaseValue))
                throw InvalidExpression($"{description} has a non-finite base weight.");
            RequireEquivalent(
                new[] { authoredInitial },
                new[] { driver.BaseValue },
                $"{description} base weight");
            if (driver.DeltaValues == null || driver.DeltaValues.Length != sampler.SourceKeyCount)
                throw InvalidExpression($"{description} output count does not match its input count.");

            if (sampler.SynthesizedSingleKey)
            {
                RequireFinite(driver.DeltaValues[0], $"{description} target weight");
                return new[] { authoredInitial, driver.DeltaValues[0] };
            }

            var values = new float[sampler.SourceKeyCount];
            for (int index = 0; index < values.Length; index++)
            {
                RequireFinite(driver.DeltaValues[index], $"{description} output {index}");
                values[index] = driver.BaseValue + driver.DeltaValues[index];
                RequireFinite(values[index], $"{description} reconstructed output {index}");
            }
            RequireEquivalent(
                new[] { authoredInitial },
                new[] { values[0] },
                $"{description} time-zero weight");
            return values;
        }

        private static Vector3[] BuildJointVectorValues(
            JointDriver driver,
            NormalizedLegacySampler sampler,
            string description)
        {
            RequireFinite(driver.BaseVec, $"{description} base value");
            if (driver.DeltaVec == null || driver.DeltaVec.Length != sampler.SourceKeyCount)
                throw InvalidExpression($"{description} output count does not match its input count.");
            if (sampler.SynthesizedSingleKey)
            {
                RequireFinite(driver.DeltaVec[0], $"{description} target value");
                return new[] { driver.BaseVec, driver.DeltaVec[0] };
            }

            var values = new Vector3[sampler.SourceKeyCount];
            for (int index = 0; index < values.Length; index++)
            {
                RequireFinite(driver.DeltaVec[index], $"{description} output {index}");
                values[index] = driver.BaseVec + driver.DeltaVec[index];
                RequireFinite(values[index], $"{description} reconstructed output {index}");
            }
            return values;
        }

        private static Quaternion[] BuildJointRotationValues(
            JointDriver driver,
            NormalizedLegacySampler sampler,
            string description)
        {
            RequireQuaternion(driver.BaseQuat, $"{description} base rotation");
            if (driver.DeltaQuat == null || driver.DeltaQuat.Length != sampler.SourceKeyCount)
                throw InvalidExpression($"{description} output count does not match its input count.");
            if (sampler.SynthesizedSingleKey)
            {
                RequireQuaternion(driver.DeltaQuat[0], $"{description} target rotation");
                return new[] { driver.BaseQuat, driver.DeltaQuat[0] };
            }

            var values = new Quaternion[sampler.SourceKeyCount];
            for (int index = 0; index < values.Length; index++)
            {
                RequireQuaternion(driver.DeltaQuat[index], $"{description} output {index}");
                values[index] = driver.DeltaQuat[index] * driver.BaseQuat;
                RequireQuaternion(values[index], $"{description} reconstructed output {index}");
            }
            return values;
        }

        private static TextureCurves BuildTextureCurves(
            TextureDriver driver,
            NormalizedLegacySampler sampler,
            string description)
        {
            RequireFinite(driver.BaseSt, $"{description} base texture transform");
            if (driver.StValues == null || driver.StValues.Length != sampler.SourceKeyCount)
                throw InvalidExpression($"{description} output count does not match its input count.");

            Vector4[] values;
            if (sampler.SynthesizedSingleKey)
            {
                RequireFinite(driver.StValues[0], $"{description} target texture transform");
                values = new[] { driver.BaseSt, driver.StValues[0] };
            }
            else
            {
                values = new Vector4[sampler.SourceKeyCount];
                for (int index = 0; index < values.Length; index++)
                {
                    RequireFinite(driver.StValues[index], $"{description} output {index}");
                    values[index] = driver.BaseSt + driver.StValues[index];
                    RequireFinite(values[index], $"{description} reconstructed output {index}");
                }
                RequireEquivalent(
                    ToComponents(driver.BaseSt),
                    ToComponents(values[0]),
                    $"{description} time-zero texture transform");
            }

            var scale = new Vector2[values.Length];
            var offset = new Vector2[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                scale[index] = new Vector2(values[index].x, values[index].y);
                offset[index] = new Vector2(values[index].z, 1f - values[index].w - values[index].y);
                RequireFinite(scale[index], $"{description} scale output {index}");
                RequireFinite(offset[index], $"{description} offset output {index}");
            }
            return new TextureCurves { Scale = scale, Offset = offset };
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
            else if (!preflight.TextureTransformsToAdd.TryGetValue(textureInfo, out transform))
            {
                transform = TextureTransformFromSt(baseSt);
                preflight.TextureTransformsToAdd.Add(textureInfo, transform);
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

        private static float[] ToGltfTranslation(Vector3 value)
        {
            return new[] { -value.x, value.y, value.z };
        }

        private static float[] ToGltfRotation(Quaternion value)
        {
            var converted = value.SwitchHandedness().normalized;
            return new[] { converted.x, converted.y, converted.z, converted.w };
        }

        private static float[] ToComponents(Vector2 value) => new[] { value.x, value.y };
        private static float[] ToComponents(Vector3 value) => new[] { value.x, value.y, value.z };
        private static float[] ToComponents(Vector4 value) => new[] { value.x, value.y, value.z, value.w };

        private static void RequireEquivalent(float[] expected, float[] actual, string description)
        {
            if (!ExpressionInitialValueValidation.ComponentsEquivalent(
                    expected,
                    actual,
                    ExpressionAccessorComponentEncoding.Float))
                throw InvalidExpression($"{description} does not match the exported authored initial value.");
        }

        private static void RequireUnit(float value, string description)
        {
            if (!IsFinite(value) || value < 0f || value > 1f)
                throw InvalidExpression($"{description} must be finite and in [0, 1].");
        }

        private static void RequireFinite(float value, string description)
        {
            if (!IsFinite(value)) throw InvalidExpression($"{description} must be finite.");
        }

        private static void RequireFinite(Vector2 value, string description)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y))
                throw InvalidExpression($"{description} must be finite.");
        }

        private static void RequireFinite(Vector3 value, string description)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
                throw InvalidExpression($"{description} must be finite.");
        }

        private static void RequireFinite(Vector4 value, string description)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z) || !IsFinite(value.w))
                throw InvalidExpression($"{description} must be finite.");
        }

        private static void RequireQuaternion(Quaternion value, string description)
        {
            RequireFinite(new Vector4(value.x, value.y, value.z, value.w), description);
            float lengthSquared = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            if (!IsFinite(lengthSquared) || !(lengthSquared > 0f))
                throw InvalidExpression($"{description} must have finite nonzero length.");
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static InvalidOperationException InvalidExpression(string message)
        {
            return new InvalidOperationException($"KHR_character_expression export is invalid: {message}");
        }

        private void ExportExpressions(GLTFSceneExporter exporter, GLTFRoot gltfRoot, CharacterExpressionSet set)
        {
            var expressions = new List<KHR_character_expression.ExpressionItem>();
            var mappingDict = new Dictionary<string, Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>>();
            var inputMappingDict = new Dictionary<string, Dictionary<string, List<KHR_character_expression_mapping.TargetWeight>>>();
            var runtimeToWireIndex = new Dictionary<int, int>();
            var pendingMasks = new Dictionary<KHR_character_expression.ExpressionItem, ExpressionTrack>();

            // Track which nested expression sub-extensions are actually emitted, so each can be declared once in
            // extensionsUsed (B1: nested KHR_character_expression_* were written on items but never declared).
            bool anyMorph = false, anyJoint = false, anyTexture = false, anyMask = false;

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

                // Write driver channels, capture indices (Phase 3)
                if (track.MorphDrivers != null)
                {
                    foreach (var driver in track.MorphDrivers)
                    {
                        int before = anim.Channels.Count;
                        WriteMorphDriver(exporter, gltfRoot, anim, driver);
                        int after = anim.Channels.Count;
                        for (int i = before; i < after; i++)
                            morphChannels.Add(i);
                    }
                }

                if (track.JointDrivers != null)
                {
                    foreach (var driver in track.JointDrivers)
                    {
                        int before = anim.Channels.Count;
                        WriteJointDriver(exporter, gltfRoot, anim, driver);
                        int after = anim.Channels.Count;
                        for (int i = before; i < after; i++)
                            jointChannels.Add(i);
                    }
                }

                if (track.TextureDrivers != null)
                {
                    foreach (var driver in track.TextureDrivers)
                    {
                        int before = anim.Channels.Count;
                        WriteTextureDriver(exporter, gltfRoot, anim, driver, emittedTextureTargets);
                        int after = anim.Channels.Count;
                        for (int i = before; i < after; i++)
                            texChannels.Add(i);
                    }
                }

                // Skip empty expressions
                if (anim.Channels.Count == 0) continue;

                gltfRoot.Animations.Add(anim);
                int animIndex = gltfRoot.Animations.Count - 1;

                // Build expression metadata (Phase 2)
                var expressionItem = new KHR_character_expression.ExpressionItem
                {
                    Expression = track.Name,
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
                    expressionItem.Texture =
                        new KHR_character_expression_texture { Channels = texChannels.ToArray() };
                    anyTexture = true;
                }

                if (track.Masks != null && track.Masks.Length > 0)
                    pendingMasks[expressionItem] = track;

                // blendMode/priority are intentionally NOT exported (no ratified KHR field; the baker reconstructs
                // Additive + Priority 0), so no vendor extras are written — the expression wire stays neutral.

                expressions.Add(expressionItem);
            }

            foreach (var pending in pendingMasks)
            {
                var masks = new List<KHR_character_expression_mask.Mask>();
                foreach (var mask in pending.Value.Masks)
                {
                    if (mask == null) continue;
                    if (!runtimeToWireIndex.TryGetValue(mask.TargetIndex, out int targetIndex)) continue;
                    var companionExtensions = ParseExtensionsObject(
                        mask.RawExtensionsJson,
                        "Mask entry companion extensions");
                    masks.Add(new KHR_character_expression_mask.Mask
                    {
                        Target = targetIndex,
                        Name = mask.Name,
                        Type = !string.IsNullOrEmpty(mask.CustomType)
                            ? mask.CustomType
                            : mask.Type == MaskType.Block ? "block" : "blend",
                        Amount = mask.Amount,
                        Threshold = mask.Threshold,
                        Extensions = companionExtensions,
                        Extras = ParseToken(mask.RawExtrasJson),
                        AdditionalProperties = ParseObject(mask.RawAdditionalPropertiesJson),
                    });
                    DeclareCompanionExtensions(
                        exporter, companionExtensions, pending.Value.MaskRequiredCompanionExtensions);
                }
                if (masks.Count > 0)
                {
                    var companionExtensions = ParseExtensionsObject(
                        pending.Value.MaskExtensionsJson,
                        "Mask extension companion extensions");
                    pending.Key.Mask = new KHR_character_expression_mask
                    {
                        Masks = masks,
                        Extensions = companionExtensions,
                        Extras = ParseToken(pending.Value.MaskExtrasJson),
                        AdditionalProperties = ParseObject(pending.Value.MaskAdditionalPropertiesJson),
                    };
                    DeclareCompanionExtensions(
                        exporter, companionExtensions, pending.Value.MaskRequiredCompanionExtensions);
                    anyMask = true;
                }
            }

            // Build mapping sets after expression filtering so runtime indices can be remapped to wire indices.
            if (set.MappingSets != null)
            {
                foreach (var mappingSet in set.MappingSets)
                {
                    if (mappingSet?.Targets == null) continue;
                    if (!KHR_character_expression_mapping.IsValidMappingSetIdentifier(mappingSet.SetName))
                        throw new InvalidMappingSetIdentifierException(mappingSet.SetName);
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
                    if (!KHR_character_expression_mapping.IsValidMappingSetIdentifier(mappingSet.SetName))
                        throw new InvalidMappingSetIdentifierException(mappingSet.SetName);
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
                if (anyTexture) exporter.DeclareExtensionUsage(KHR_character_expression_texture.EXTENSION_NAME);
                if (anyMask) exporter.DeclareExtensionUsage(KHR_character_expression_mask.EXTENSION_NAME);
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

        private void WriteMorphDriver(GLTFSceneExporter exporter, GLTFRoot gltfRoot, GLTFAnimation anim, MorphDriver driver)
        {
            var normalizedSampler = NormalizeLegacySampler(driver.Sampler, "Morph driver");
            int nodeIdx = exporter.GetTransformIndex(driver.Smr.transform);
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
            exporter.DeclareExtensionUsage(KHR_animation_pointer.EXTENSION_NAME);
        }

        // F7: joints are written as MANUAL native glTF TRS channels (target.Node + path
        // translation/rotation/scale), NOT via AddAnimationData. Under UseAnimationPointer, AddAnimationData
        // rewrites the channel into a `pointer` channel (target.Node = null), which the native-only joint baker
        // (KhrCharacterBaker.BakeJointChannels) silently drops -> joint round-trip breaks. Emitting native TRS
        // directly keeps joints readable in both native and AnimationPointer export modes.
        private void WriteJointDriver(GLTFSceneExporter exporter, GLTFRoot gltfRoot, GLTFAnimation anim, JointDriver driver)
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
        // KHR_character_expression_texture sub-extension. GltfTextureSlot is the full glTF slot path the importer
        // parses (e.g. "pbrMetallicRoughness/baseColorTexture") — populated at bake (G-B).
        private void WriteTextureDriver(
            GLTFSceneExporter exporter,
            GLTFRoot gltfRoot,
            GLTFAnimation anim,
            TextureDriver driver,
            HashSet<string> emittedTargets)
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
                emittedTargets);
        }

        // UV transform: reconstruct the absolute Unity _ST per key (inverse of the import bake), unpack into glTF
        // scale/offset (the KHR_texture_transform V-flip), and emit two KHR_animation_pointer channels:
        //   /materials/{m}/{slot}/extensions/KHR_texture_transform/{scale,offset}
        private void WriteUvTransformChannels(GLTFSceneExporter exporter, GLTFRoot gltfRoot, GLTFAnimation anim,
            TextureDriver driver,
            int materialIndex,
            NormalizedLegacySampler sampler,
            TextureCurves curves,
            HashSet<string> emittedTargets)
        {
            string basePath = $"/materials/{materialIndex}/{driver.GltfTextureSlot}/extensions/{ExtTextureTransformExtensionFactory.EXTENSION_NAME}/";
            bool emitted = false;
            string scaleTarget = basePath + ExtTextureTransformExtensionFactory.SCALE;
            if (driver.TransformTarget != TextureTransformTarget.Offset && emittedTargets.Add(scaleTarget))
            {
                EmitPointerChannel(exporter, gltfRoot, anim, sampler.Times, exporter.ExportAccessor(curves.Scale),
                    scaleTarget, sampler.Interpolation);
                emitted = true;
            }
            string offsetTarget = basePath + ExtTextureTransformExtensionFactory.OFFSET;
            if (driver.TransformTarget != TextureTransformTarget.Scale && emittedTargets.Add(offsetTarget))
            {
                EmitPointerChannel(exporter, gltfRoot, anim, sampler.Times, exporter.ExportAccessor(curves.Offset),
                    offsetTarget, sampler.Interpolation);
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
                    if (set == null || string.IsNullOrEmpty(set.Identifier)) continue;
                    if (!Uri.TryCreate(set.Identifier, UriKind.Absolute, out _))
                    {
                        Debug.LogWarning($"[KHR_character] Skeleton mapping-set identifier '{set.Identifier}' is not an absolute URI; skipping it.");
                        continue;
                    }
                    var associations = ExportAssociations(exporter, set.Associations);
                    if (associations.Count > 0) mappings[set.Identifier] = associations;
                }
            }
            else if (result.Bones != null && result.Bones.Count > 0)
            {
                var identifier = result.SelectedRig;
                if (!string.IsNullOrEmpty(identifier))
                {
                    if (!Uri.TryCreate(identifier, UriKind.Absolute, out _))
                    {
                        Debug.LogWarning($"[KHR_character] Skeleton mapping-set identifier '{identifier}' is not an absolute URI; skipping it.");
                        return;
                    }
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
            var anim = new GLTFAnimation { Name = $"ReferencePose_{pose.PoseType}" };
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
                    new KHR_character_reference_pose { PoseType = pose.PoseType });
                gltfRoot.Animations.Add(anim);
                exporter.DeclareExtensionUsage(KHR_character_reference_pose.EXTENSION_NAME);
            }
        }

    }
}
