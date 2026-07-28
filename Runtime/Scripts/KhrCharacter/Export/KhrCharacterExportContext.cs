using System;
using System.Collections.Generic;
using GLTF.Schema;
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
        private readonly KhrCharacterExportPlugin _settings;
        private readonly ExportContext _context;

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
            try
            {
                // Find the character root and components (edit-time safe)
                var root = FindCharacterRoot(exporter);
                if (root == null) return;

                var controller = root.GetComponentInChildren<ExpressionController>();
                var skeleton = root.GetComponentInChildren<SkeletonMap>();
                // Node-feature components: include-inactive (imported roots are often inactive at edit time).
                var cameraHints = root.GetComponentInChildren<CameraHintSet>(true);
                var gaze = root.GetComponentInChildren<GazeSolver>(true);

                // Source of truth: prefer live, fall back to baked (edit-time safety)
                var expressionSet = controller?.Set ?? controller?.BakedSet;
                var skeletonResult = skeleton?.Result ?? skeleton?.EditorBakedResult;

                bool hasExpressions = expressionSet?.Expressions != null && expressionSet.Expressions.Length > 0;
                bool hasSkeleton = skeletonResult?.Bones != null && skeletonResult.Bones.Count > 0;
                bool hasReferencePose = skeletonResult?.ReferencePose?.Bones != null;
                // F6: a character may carry node-level features (camera hints / look-at targets) even without
                // expressions/skeleton — include them in the gate so the root KHR_character + node extensions still emit.
                bool hasNodeFeatures = (cameraHints?.Hints != null && cameraHints.Hints.Count > 0)
                    || (gaze?.AuthoredTargets != null && gaze.AuthoredTargets.Count > 0);

                if (!hasExpressions && !hasSkeleton && !hasReferencePose && !hasNodeFeatures)
                {
                    // Nothing to export
                    return;
                }

                // Phase 0: Emit root KHR_character extension FIRST (F2 blocker)
                // Without this, re-import fails completely (import gates on KHR_character presence)
                EmitRootCharacterExtension(exporter, gltfRoot, root);

                // Phases 2+3: Export expressions (interleaved: channels then metadata)
                if (hasExpressions)
                {
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
                    ExportReferencePose(exporter, gltfRoot, skeletonResult.ReferencePose);
                }

                // F6: Export node-level features (camera hints / look-at targets) from the runtime components.
                ExportNodeFeatures(exporter, gltfRoot, cameraHints, gaze);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KHR_character] Export failed: {ex.Message}\n{ex.StackTrace}");
                // Don't throw - allow export to continue without KHR_character data
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
        // F6 (multi-character stopgap): PR #2512 models ONE character per glTF document (KHR_character is a root
        // singleton with a single rootNode). With multiple character-bearing roots in the export set we keep the
        // deterministic first-in-RootTransforms-order selection and emit a warning naming the skipped roots, so
        // nothing is silently dropped. True multi-character export needs a schema RFC + import rework (out of
        // scope); the documented workflow is one glTF document per character. (A single root nesting two
        // characters is not separately diagnosed here — GetComponentInChildren takes the first component.)
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
                    $"({string.Join(", ", characterRootNames)}); PR #2512 is one-character-per-document. " +
                    $"Exporting '{selected.name}'; the rest are skipped. To export multiple characters, export each " +
                    "character root to a separate glTF (one document per character).");
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

        // F6: emits the node-level KHR character extensions (KHR_node_camera_hint / KHR_node_lookat_target) from the
        // runtime components, mirroring how core export emits KHR_node_visibility (node.AddExtension +
        // DeclareExtensionUsage, never required — GLTFSceneExporter.cs). Both are official KHR node extensions, so a
        // plain viewer ignores unknown node extensions and renders normally. Node identity round-trips because the
        // hint/target nodes are ordinary nodes in the exported hierarchy. Iterates in stored order for determinism.
        //
        // Camera-index boundary: KHR_node_camera_hint.camera is OMITTED unless the referenced Camera was already
        // exported onto its own node (we only READ gltfRoot.Nodes[camNode].Camera.Id). GLTFSceneExporter.ExportCamera
        // is PRIVATE and there is no public GetCameraId, so we never force-export a camera here — spec-legal since
        // `camera` is optional. Import also does not populate CameraHint.Projection today, so in practice `camera` is
        // omitted; closing that round-trip is a separate, optional add-on (role/label/targetNode/hint DO round-trip).
        private void ExportNodeFeatures(GLTFSceneExporter exporter, GLTFRoot gltfRoot, CameraHintSet cameraHints, GazeSolver gaze)
        {
            if (cameraHints?.Hints != null)
            {
                foreach (var hint in cameraHints.Hints)
                {
                    if (hint?.Node == null) continue;
                    int nodeIdx = exporter.GetTransformIndex(hint.Node);
                    if (nodeIdx < 0)
                    {
                        Debug.LogWarning($"[KHR_character] Camera-hint node '{hint.Node.name}' is not part of the export; skipping.");
                        continue;
                    }

                    // KHR_node_camera_hint.role is spec-required (minLength:1) and cannot be omitted, so a hint with
                    // no role could only ever serialize as an invalid extension. Skip it entirely (mirrors the
                    // not-in-export guard above and the importer's missing-role warning) — never emit an empty role.
                    if (string.IsNullOrEmpty(hint.Role))
                    {
                        Debug.LogWarning($"[KHR_character] Camera-hint node '{hint.Node.name}' has no 'role' (spec-required, minLength:1); skipping its camera hint.");
                        continue;
                    }

                    var ext = new KHR_node_camera_hint { Role = hint.Role, Label = hint.Label };

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
                        exporter.DeclareExtensionUsage(KHR_node_camera_hint.EXTENSION_NAME, isRequired: false);
                }
            }

            if (gaze?.AuthoredTargets != null)
            {
                foreach (var target in gaze.AuthoredTargets)
                {
                    if (target?.Node == null) continue;
                    int nodeIdx = exporter.GetTransformIndex(target.Node);
                    if (nodeIdx < 0)
                    {
                        Debug.LogWarning($"[KHR_character] Look-at target node '{target.Node.name}' is not part of the export; skipping.");
                        continue;
                    }

                    // An empty {} is valid: presence alone marks the node as a look-at target; hint is optional.
                    if (TryAddNodeExtension(gltfRoot, nodeIdx, KHR_node_lookat_target.EXTENSION_NAME,
                            new KHR_node_lookat_target { Hint = target.Hint }))
                        exporter.DeclareExtensionUsage(KHR_node_lookat_target.EXTENSION_NAME, isRequired: false);
                }
            }
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

        private void ExportExpressions(GLTFSceneExporter exporter, GLTFRoot gltfRoot, CharacterExpressionSet set)
        {
            var expressions = new List<KHR_character_expression.ExpressionItem>();
            var mappingDict = new Dictionary<string, Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>>();

            // Build mapping sets (root extension, not per-expression)
            if (set.MappingSets != null)
            {
                foreach (var mappingSet in set.MappingSets)
                {
                    var setDict = new Dictionary<string, List<KHR_character_expression_mapping.SourceWeight>>();
                    foreach (var target in mappingSet.Targets)
                    {
                        var contributions = new List<KHR_character_expression_mapping.SourceWeight>();
                        foreach (var contrib in target.Contributions)
                        {
                            if (contrib.SourceIndex < 0 || contrib.SourceIndex >= set.Expressions.Length) continue;
                            contributions.Add(new KHR_character_expression_mapping.SourceWeight {
                                Source = set.Expressions[contrib.SourceIndex].Name,
                                Weight = contrib.Weight
                            });
                        }
                        if (contributions.Count > 0) setDict[target.TargetName] = contributions;
                    }
                    if (setDict.Count > 0) mappingDict[mappingSet.SetName] = setDict;
                }
            }

            // Track which nested expression sub-extensions are actually emitted, so each can be declared once in
            // extensionsUsed (B1: nested KHR_character_expression_* were written on items but never declared).
            bool anyMorph = false, anyJoint = false, anyTexture = false, anyMask = false;

            // Process each expression track
            foreach (var track in set.Expressions)
            {
                if (track == null || string.IsNullOrEmpty(track.Name)) continue;

                // Create one animation per expression
                var anim = new GLTFAnimation { Name = track.Name };
                var morphChannels = new List<int>();
                var jointChannels = new List<int>();
                var texChannels = new List<int>();

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
                        WriteTextureDriver(exporter, gltfRoot, anim, driver);
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
                {
                    var masks = new List<KHR_character_expression_mask.Mask>();
                    foreach (var mask in track.Masks)
                    {
                        if (mask.TargetIndex < 0 || mask.TargetIndex >= set.Expressions.Length) continue;
                        masks.Add(new KHR_character_expression_mask.Mask
                        {
                            Target = set.Expressions[mask.TargetIndex].Name,
                            Type = mask.Type == MaskType.Block ? "block" : "blend",
                            Amount = mask.Amount,
                            Threshold = mask.Threshold
                        });
                    }
                    if (masks.Count > 0)
                    {
                        expressionItem.Mask =
                            new KHR_character_expression_mask { Masks = masks };
                        anyMask = true;
                    }
                }

                // blendMode/priority are intentionally NOT exported (no ratified KHR field; the baker reconstructs
                // Additive + Priority 0), so no vendor extras are written — the expression wire stays neutral.

                expressions.Add(expressionItem);
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

            if (mappingDict.Count > 0)
            {
                var mappingExtension = new KHR_character_expression_mapping
                {
                    ExpressionSetMappings = mappingDict
                };
                gltfRoot.AddExtension(KHR_character_expression_mapping.EXTENSION_NAME, mappingExtension);
                exporter.DeclareExtensionUsage(KHR_character_expression_mapping.EXTENSION_NAME);
            }
        }

        private void WriteMorphDriver(GLTFSceneExporter exporter, GLTFRoot gltfRoot, GLTFAnimation anim, MorphDriver driver)
        {
            if (driver.Smr == null) return;
            
            int nodeIdx = exporter.GetTransformIndex(driver.Smr.transform);
            if (nodeIdx < 0) return;

            // Reconstruct absolute values: base + delta (raw [0..1])
            float[] times = driver.Sampler.Times;
            float[] absVals;
            if (driver.Sampler.SingleKey)
            {
                absVals = new[] { driver.DeltaValues[0] }; // Already absolute
            }
            else
            {
                absVals = new float[driver.DeltaValues.Length];
                for (int i = 0; i < absVals.Length; i++)
                {
                    absVals[i] = driver.BaseValue + driver.DeltaValues[i];
                }
            }

            // Use per-blendshape pointer (Pattern B)
            AccessorId inputAcc = exporter.ExportAccessor(times);
            AccessorId outputAcc = exporter.ExportAccessor(absVals);

            var sampler = new AnimationSampler
            {
                Input = inputAcc,
                Output = outputAcc,
                Interpolation = MapInterpolation(driver.Sampler.Interp)
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
            if (driver.Target == null) return;

            string path = driver.Channel switch
            {
                TrsChannel.Translation => "translation",
                TrsChannel.Rotation => "rotation",
                TrsChannel.Scale => "scale",
                _ => null
            };
            if (path == null) return;

            int nodeIndex = exporter.GetTransformIndex(driver.Target);
            if (nodeIndex < 0)
            {
                Debug.LogWarning($"[KHR_character] Joint target '{driver.Target.name}' is not part of the export; skipping its {path} channel.");
                return;
            }

            float[] times = driver.Sampler.Times;
            AccessorId outputAcc;

            if (driver.Channel == TrsChannel.Rotation)
            {
                // Reconstruct absolute local rotations (inverse of the import bake: DeltaQuat = abs * Inverse(frame0)).
                Quaternion[] absVals;
                if (driver.Sampler.SingleKey)
                {
                    absVals = new[] { driver.DeltaQuat[0] };
                }
                else
                {
                    absVals = new Quaternion[driver.DeltaQuat.Length];
                    for (int i = 0; i < absVals.Length; i++)
                        absVals[i] = driver.DeltaQuat[i] * driver.BaseQuat;
                }
                outputAcc = ExportRotationAccessor(exporter, absVals);
            }
            else
            {
                // Reconstruct absolute local translation/scale (inverse of the import bake: DeltaVec = abs - frame0).
                Vector3[] absVals;
                if (driver.Sampler.SingleKey)
                {
                    absVals = new[] { driver.DeltaVec[0] };
                }
                else
                {
                    absVals = new Vector3[driver.DeltaVec.Length];
                    for (int i = 0; i < absVals.Length; i++)
                        absVals[i] = driver.BaseVec + driver.DeltaVec[i];
                }
                outputAcc = ExportVectorAccessor(exporter, absVals, flipHandedness: driver.Channel == TrsChannel.Translation);
            }

            EmitNativeTrsChannel(exporter, gltfRoot, anim, nodeIndex, path,
                exporter.ExportAccessor(times), outputAcc, driver.Sampler.Interp);
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
            int nodeIndex, string path, AccessorId inputAcc, AccessorId outputAcc, Interp interp)
        {
            anim.Samplers.Add(new AnimationSampler
            {
                Input = inputAcc,
                Output = outputAcc,
                Interpolation = MapInterpolation(interp)
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
        // parses (e.g. "pbrMetallicRoughness/baseColorTexture") — populated at bake (G-B); skip+warn if absent.
        private void WriteTextureDriver(GLTFSceneExporter exporter, GLTFRoot gltfRoot, GLTFAnimation anim, TextureDriver driver)
        {
            if (driver.Renderer == null) return;
            if (string.IsNullOrEmpty(driver.GltfTextureSlot))
            {
                Debug.LogWarning("[KHR_character] TextureDriver has no GltfTextureSlot (G-B property-name capture incomplete); skipping its texture channels.");
                return;
            }

            float[] times = driver.Sampler.Times;
            if (times == null || times.Length == 0) return;

            // Resolve the glTF material index for the renderer's material at this submesh slot.
            var materials = driver.Renderer.sharedMaterials;
            if (materials == null || driver.SubmeshSlot < 0 || driver.SubmeshSlot >= materials.Length) return;
            var material = materials[driver.SubmeshSlot];
            if (material == null) return;
            var matId = exporter.GetMaterialId(gltfRoot, material);
            if (matId == null)
            {
                Debug.LogWarning($"[KHR_character] TextureDriver material '{material.name}' is not part of the export; skipping its texture channels.");
                return;
            }

            WriteUvTransformChannels(exporter, gltfRoot, anim, driver, matId.Id, times);
        }

        // UV transform: reconstruct the absolute Unity _ST per key (inverse of the import bake), unpack into glTF
        // scale/offset (the KHR_texture_transform V-flip), and emit two KHR_animation_pointer channels:
        //   /materials/{m}/{slot}/extensions/KHR_texture_transform/{scale,offset}
        private void WriteUvTransformChannels(GLTFSceneExporter exporter, GLTFRoot gltfRoot, GLTFAnimation anim,
            TextureDriver driver, int materialIndex, float[] times)
        {
            int n = times.Length;
            if (driver.StValues == null || driver.StValues.Length < n) return;

            var scale = new Vector2[n];
            var offset = new Vector2[n];
            for (int k = 0; k < n; k++)
            {
                // Reconstruct the absolute Unity _ST per key. SingleKey stores the absolute _ST directly; multi-key
                // stores frame-0-relative deltas re-anchored here on an absolute baseline.
                //
                // FU2 (resolved): the multi-key anchor is the authored frame-0 absolute _ST (Frame0St) when import
                // captured it (HasFrame0St), so this emits st_k = Frame0St + (frame_k - frame0) = the authored
                // absolute per key. Inter-key shape (deltas) AND the absolute baseline are preserved, so a foreign
                // asset whose authored frame 0 differs from the material rest now round-trips exactly on the first
                // cycle (re-import recovers identical deltas, Frame0St, and BaseSt). When Frame0St was NOT captured
                // (hand-authored / synthesized drivers, HasFrame0St == false) we fall back to BaseSt — the prior
                // behavior — so those drivers are unaffected. BaseSt remains the RUNTIME rest anchor regardless:
                // ExpressionController rests at the material _ST and applies deltas additively; it is unchanged
                // here. The exported animation's frame 0 thus equals the authored frame 0, which may differ from the
                // material's static _ST — standard glTF (an animation overrides the static value during playback).
                Vector4 anchor = driver.HasFrame0St ? driver.Frame0St : driver.BaseSt;
                Vector4 st = driver.Sampler.SingleKey ? driver.StValues[k] : anchor + driver.StValues[k];
                // Inverse of KhrCharacterBaker.PackSt: gltfScale = (x, y); gltfOffset = (z, 1 - w - y).
                scale[k] = new Vector2(st.x, st.y);
                offset[k] = new Vector2(st.z, 1f - st.w - st.y);
            }

            string basePath = $"/materials/{materialIndex}/{driver.GltfTextureSlot}/extensions/{ExtTextureTransformExtensionFactory.EXTENSION_NAME}/";
            EmitPointerChannel(exporter, gltfRoot, anim, times, exporter.ExportAccessor(scale),
                basePath + ExtTextureTransformExtensionFactory.SCALE, driver.Sampler.Interp);
            EmitPointerChannel(exporter, gltfRoot, anim, times, exporter.ExportAccessor(offset),
                basePath + ExtTextureTransformExtensionFactory.OFFSET, driver.Sampler.Interp);

            exporter.DeclareExtensionUsage(KHR_animation_pointer.EXTENSION_NAME);
            // Additive (never required): a plain viewer must still load the asset.
            exporter.DeclareExtensionUsage(ExtTextureTransformExtensionFactory.EXTENSION_NAME);
        }

        // Builds a KHR_animation_pointer AnimationSampler + AnimationChannel (target.Path = "pointer") and appends
        // them to `anim` (mirrors WriteMorphDriver). The input (time) accessor gets min/max for free from
        // ExportAccessor(float[]) (ExporterAccessors.cs), satisfying the glTF sampler-input min/max requirement.
        private void EmitPointerChannel(GLTFSceneExporter exporter, GLTFRoot gltfRoot, GLTFAnimation anim,
            float[] times, AccessorId outputAcc, string pointerPath, Interp interp)
        {
            var target = new AnimationChannelTarget { Path = "pointer" };
            target.AddExtension(KHR_animation_pointer.EXTENSION_NAME, new KHR_animation_pointer { path = pointerPath });

            anim.Samplers.Add(new AnimationSampler
            {
                Input = exporter.ExportAccessor(times),
                Output = outputAcc,
                Interpolation = MapInterpolation(interp)
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
            var rigDict = new Dictionary<string, int>();
            foreach (var kv in result.Bones)
            {
                if (kv.Value == null) continue;
                int nodeIdx = exporter.GetTransformIndex(kv.Value);
                if (nodeIdx < 0) continue;
                rigDict[kv.Key] = nodeIdx;
            }

            if (rigDict.Count == 0) return;

            var mapping = new KHR_character_skeleton_mapping
            {
                SkeletalRigMappings = new Dictionary<string, Dictionary<string, int>>
                {
                    [result.SelectedRig ?? "default"] = rigDict
                }
            };
            gltfRoot.AddExtension(KHR_character_skeleton_mapping.EXTENSION_NAME, mapping);
            exporter.DeclareExtensionUsage(KHR_character_skeleton_mapping.EXTENSION_NAME);
        }

        // F7: the reference pose is written as MANUAL native glTF TRS channels (single STEP key at t=0), NOT via
        // AddAnimationData. As with joints, AddAnimationData under UseAnimationPointer would convert these to
        // `pointer` channels (target.Node = null), which KhrCharacterSkeletonBaker.BakeReferencePose (native-TRS
        // only) silently drops. Native channels survive re-import in both export modes.
        private void ExportReferencePose(GLTFSceneExporter exporter, GLTFRoot gltfRoot, ReferencePose pose)
        {
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
                        ExportVectorAccessor(exporter, new[] { pose.LocalPositions[i] }, flipHandedness: true), Interp.Step);
                if (hasRot)
                    EmitNativeTrsChannel(exporter, gltfRoot, anim, nodeIndex, "rotation",
                        exporter.ExportAccessor(new[] { 0f }),
                        ExportRotationAccessor(exporter, new[] { pose.LocalRotations[i] }), Interp.Step);
                if (hasScl)
                    EmitNativeTrsChannel(exporter, gltfRoot, anim, nodeIndex, "scale",
                        exporter.ExportAccessor(new[] { 0f }),
                        ExportVectorAccessor(exporter, new[] { pose.LocalScales[i] }, flipHandedness: false), Interp.Step);
            }

            if (anim.Channels.Count > 0)
            {
                anim.AddExtension(KHR_character_reference_pose.EXTENSION_NAME,
                    new KHR_character_reference_pose { PoseType = pose.PoseType });
                gltfRoot.Animations.Add(anim);
                exporter.DeclareExtensionUsage(KHR_character_reference_pose.EXTENSION_NAME);
            }
        }

        private InterpolationType MapInterpolation(Interp interp)
        {
            return interp switch
            {
                Interp.Step => InterpolationType.STEP,
                Interp.CubicSpline => InterpolationType.CUBICSPLINE,
                _ => InterpolationType.LINEAR
            };
        }
    }
}
