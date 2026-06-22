using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Authoring container for an expression set, stored as a project asset. It holds scene-INDEPENDENT driver
    /// bindings: the runtime <see cref="CharacterExpressionSet"/> drivers reference live scene objects
    /// (SkinnedMeshRenderer / Transform / Renderer) and carry <c>Quaternion[]</c> rotation curves, which Unity
    /// cannot serialize into a standalone project asset. <see cref="Extract"/> replaces those live references with
    /// stable hierarchy paths + blendshape names (see <see cref="ExpressionBindingSet"/>), so the asset is
    /// self-contained and re-resolvable back onto a character via <see cref="Resolve"/>.
    ///
    /// Scope: this is groundwork for a future glTF exporter — it stores authoring data but does not export
    /// anything by itself. Capturing human-readable texture property names (for export) is deferred to that work;
    /// the binding keeps the stable <c>PropertyId</c>, which is sufficient to re-resolve a runtime driver.
    /// </summary>
    [CreateAssetMenu(fileName = "CharacterExpressionSet", menuName = "UnityGLTF/KHR Character/Expression Set", order = 220)]
    public class CharacterExpressionSetAsset : ScriptableObject
    {
        [SerializeField] private ExpressionBindingSet _bindings = new ExpressionBindingSet();

        /// <summary>The wrapped scene-independent binding set. Editable in the default inspector.</summary>
        public ExpressionBindingSet Bindings
        {
            get => _bindings;
            set => _bindings = value;
        }

        /// <summary>
        /// Convert a baked runtime set (live scene refs + curves) into a scene-independent binding set: each
        /// driver's live target becomes a hierarchy path relative to <paramref name="root"/>, and each morph's
        /// blendshape index becomes its stable name. Curve/base/priority/sampler data and per-expression metadata
        /// (name, blend mode, domains, binary flag, masks) plus the vocabulary mapping sets are carried verbatim.
        /// Drivers whose target is null or not under <paramref name="root"/> (or whose blendshape index cannot be
        /// named) are skipped with a warning. The result is serializable into a project asset.
        /// </summary>
        public static ExpressionBindingSet Extract(CharacterExpressionSet baked, Transform root)
        {
            var result = new ExpressionBindingSet { MappingSets = baked?.MappingSets };
            if (baked?.Expressions == null) return result;

            var expressions = new List<ExpressionBinding>(baked.Expressions.Length);
            foreach (var src in baked.Expressions)
            {
                if (src == null) continue;
                var binding = new ExpressionBinding
                {
                    Name = src.Name,
                    Domains = src.Domains,
                    BlendMode = src.BlendMode,
                    IsBinary = src.IsBinary,
                    Masks = src.Masks,
                };

                // Morph drivers → renderer path + blendshape name.
                if (src.MorphDrivers != null)
                {
                    var morphs = new List<MorphBinding>(src.MorphDrivers.Length);
                    foreach (var d in src.MorphDrivers)
                    {
                        if (d == null) continue;
                        if (d.Smr == null)
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': morph driver has no SkinnedMeshRenderer; skipped.");
                            continue;
                        }
                        if (!TryRelativePath(d.Smr.transform, root, out var path))
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': morph renderer '{d.Smr.name}' is not under the character root; skipped.");
                            continue;
                        }
                        if (!TryBlendShapeName(d.Smr, d.BlendShapeIndex, out var shapeName))
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': blendshape index {d.BlendShapeIndex} on '{d.Smr.name}' could not be named; skipped.");
                            continue;
                        }
                        morphs.Add(new MorphBinding
                        {
                            RendererPath = path,
                            BlendShapeName = shapeName,
                            Sampler = d.Sampler,
                            DeltaValues = d.DeltaValues,
                            BaseValue = d.BaseValue,
                            Priority = d.Priority,
                        });
                    }
                    binding.MorphBindings = morphs.ToArray();
                }

                // Joint drivers → target path (curve data, incl. Quaternion[] rotations, carried verbatim).
                if (src.JointDrivers != null)
                {
                    var joints = new List<JointBinding>(src.JointDrivers.Length);
                    foreach (var d in src.JointDrivers)
                    {
                        if (d == null) continue;
                        if (d.Target == null)
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': joint driver has no target transform; skipped.");
                            continue;
                        }
                        if (!TryRelativePath(d.Target, root, out var path))
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': joint target '{d.Target.name}' is not under the character root; skipped.");
                            continue;
                        }
                        joints.Add(new JointBinding
                        {
                            TargetPath = path,
                            Channel = d.Channel,
                            Sampler = d.Sampler,
                            DeltaVec = d.DeltaVec,
                            DeltaQuat = d.DeltaQuat,
                            BaseVec = d.BaseVec,
                            BaseQuat = d.BaseQuat,
                            Priority = d.Priority,
                        });
                    }
                    binding.JointBindings = joints.ToArray();
                }

                // Texture drivers → renderer path (PropertyId kept as-is; SwapTextures are project assets).
                if (src.TextureDrivers != null)
                {
                    var textures = new List<TextureBinding>(src.TextureDrivers.Length);
                    foreach (var d in src.TextureDrivers)
                    {
                        if (d == null) continue;
                        if (d.Renderer == null)
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': texture driver has no renderer; skipped.");
                            continue;
                        }
                        if (!TryRelativePath(d.Renderer.transform, root, out var path))
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': texture renderer '{d.Renderer.name}' is not under the character root; skipped.");
                            continue;
                        }
                        textures.Add(new TextureBinding
                        {
                            RendererPath = path,
                            SubmeshSlot = d.SubmeshSlot,
                            PropertyId = d.PropertyId,
                            PropertyName = d.PropertyName,
                            GltfTextureSlot = d.GltfTextureSlot,
                            Kind = d.Kind,
                            Sampler = d.Sampler,
                            SwapTextures = d.SwapTextures,
                            StValues = d.StValues,
                            BaseSt = d.BaseSt,
                            Frame0St = d.Frame0St,
                            HasFrame0St = d.HasFrame0St,
                            Priority = d.Priority,
                        });
                    }
                    binding.TextureBindings = textures.ToArray();
                }

                expressions.Add(binding);
            }
            result.Expressions = expressions.ToArray();
            return result;
        }

        /// <summary>
        /// Inverse of <see cref="Extract"/>: rebuild a runtime <see cref="CharacterExpressionSet"/> by resolving
        /// each binding's path under <paramref name="root"/> back to a live SkinnedMeshRenderer / Transform /
        /// Renderer, and each morph's blendshape name back to an index on the resolved mesh. Bindings whose path
        /// or blendshape name does not resolve are dropped with a warning (consistent with the baker's
        /// drop-and-warn stance). Metadata and mapping sets are carried verbatim.
        /// </summary>
        public static CharacterExpressionSet Resolve(ExpressionBindingSet bindings, Transform root)
        {
            var result = new CharacterExpressionSet { MappingSets = bindings?.MappingSets };
            if (bindings?.Expressions == null) return result;

            var expressions = new List<ExpressionTrack>(bindings.Expressions.Length);
            foreach (var src in bindings.Expressions)
            {
                if (src == null) continue;
                var track = new ExpressionTrack
                {
                    Name = src.Name,
                    Domains = src.Domains,
                    BlendMode = src.BlendMode,
                    IsBinary = src.IsBinary,
                    Masks = src.Masks,
                };

                // Morph bindings → live SkinnedMeshRenderer + blendshape index.
                if (src.MorphBindings != null)
                {
                    var morphs = new List<MorphDriver>(src.MorphBindings.Length);
                    foreach (var b in src.MorphBindings)
                    {
                        if (b == null) continue;
                        var t = ResolvePath(root, b.RendererPath);
                        var smr = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                        if (smr == null)
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': morph renderer path '{b.RendererPath}' did not resolve to a SkinnedMeshRenderer; skipped.");
                            continue;
                        }
                        int index = smr.sharedMesh != null ? smr.sharedMesh.GetBlendShapeIndex(b.BlendShapeName) : -1;
                        if (index < 0)
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': blendshape '{b.BlendShapeName}' not found on '{smr.name}'; skipped.");
                            continue;
                        }
                        morphs.Add(new MorphDriver
                        {
                            Smr = smr,
                            BlendShapeIndex = index,
                            Sampler = b.Sampler,
                            DeltaValues = b.DeltaValues,
                            BaseValue = b.BaseValue,
                            Priority = b.Priority,
                        });
                    }
                    track.MorphDrivers = morphs.ToArray();
                }

                // Joint bindings → live Transform target.
                if (src.JointBindings != null)
                {
                    var joints = new List<JointDriver>(src.JointBindings.Length);
                    foreach (var b in src.JointBindings)
                    {
                        if (b == null) continue;
                        var t = ResolvePath(root, b.TargetPath);
                        if (t == null)
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': joint target path '{b.TargetPath}' did not resolve; skipped.");
                            continue;
                        }
                        joints.Add(new JointDriver
                        {
                            Target = t,
                            Channel = b.Channel,
                            Sampler = b.Sampler,
                            DeltaVec = b.DeltaVec,
                            DeltaQuat = b.DeltaQuat,
                            BaseVec = b.BaseVec,
                            BaseQuat = b.BaseQuat,
                            Priority = b.Priority,
                        });
                    }
                    track.JointDrivers = joints.ToArray();
                }

                // Texture bindings → live Renderer.
                if (src.TextureBindings != null)
                {
                    var textures = new List<TextureDriver>(src.TextureBindings.Length);
                    foreach (var b in src.TextureBindings)
                    {
                        if (b == null) continue;
                        var t = ResolvePath(root, b.RendererPath);
                        var renderer = t != null ? t.GetComponent<Renderer>() : null;
                        if (renderer == null)
                        {
                            Debug.LogWarning($"[KHR_character] Expression '{src.Name}': texture renderer path '{b.RendererPath}' did not resolve to a Renderer; skipped.");
                            continue;
                        }
                        textures.Add(new TextureDriver
                        {
                            Renderer = renderer,
                            SubmeshSlot = b.SubmeshSlot,
                            PropertyId = b.PropertyId,
                            PropertyName = b.PropertyName,
                            GltfTextureSlot = b.GltfTextureSlot,
                            Kind = b.Kind,
                            Sampler = b.Sampler,
                            SwapTextures = b.SwapTextures,
                            StValues = b.StValues,
                            BaseSt = b.BaseSt,
                            Frame0St = b.Frame0St,
                            HasFrame0St = b.HasFrame0St,
                            Priority = b.Priority,
                        });
                    }
                    track.TextureDrivers = textures.ToArray();
                }

                expressions.Add(track);
            }
            result.Expressions = expressions.ToArray();
            return result;
        }

        // Hierarchy path of `self` relative to `root` ("" when self == root). Mirrors the importer's private
        // GLTFSceneImporter.RelativePathFrom (SceneImporter/ImporterAnimation.cs) but returns false instead of
        // throwing when `self` is not under `root` (so callers can drop the binding with a warning).
        private static bool TryRelativePath(Transform self, Transform root, out string path)
        {
            var parts = new List<string>();
            for (var current = self; current != null; current = current.parent)
            {
                if (current == root)
                {
                    path = string.Join("/", parts.ToArray());
                    return true;
                }
                parts.Insert(0, current.name);
            }
            path = null;
            return false;
        }

        // Inverse of TryRelativePath: an empty path is the root itself; otherwise Transform.Find walks the path.
        private static Transform ResolvePath(Transform root, string path)
        {
            if (root == null) return null;
            return string.IsNullOrEmpty(path) ? root : root.Find(path);
        }

        // Stable blendshape name for an index on the renderer's shared mesh; false when the mesh/index is invalid.
        private static bool TryBlendShapeName(SkinnedMeshRenderer smr, int index, out string name)
        {
            name = null;
            var mesh = smr.sharedMesh;
            if (mesh == null || index < 0 || index >= mesh.blendShapeCount) return false;
            name = mesh.GetBlendShapeName(index);
            return !string.IsNullOrEmpty(name);
        }
    }
}
