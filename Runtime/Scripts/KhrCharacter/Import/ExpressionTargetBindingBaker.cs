using System;
using System.Collections.Generic;
using System.Globalization;
using GLTF.Schema;
using UnityEngine;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Binds each property that an imported expression channel can write to the Unity objects that show it, for
    /// <see cref="ExpressionController"/>. Only channels that produce records are bound; a property whose node or
    /// material was not instantiated is reported and left unbound.
    /// </summary>
    internal static class ExpressionTargetBindingBaker
    {
        public static ExpressionTargetBinding[] Bake(
            GLTFRoot root,
            GLTFSceneImporter importer,
            ExpressionResponseSet responses,
            IReadOnlyDictionary<int, GameObject> nodeIndexToGo,
            MaterialPropertiesRemapper remapper,
            float translationScale)
        {
            var bindings = new List<ExpressionTargetBinding>();
            if (root == null || responses == null || nodeIndexToGo == null) return bindings.ToArray();
            remapper = remapper ?? new DefaultMaterialPropertiesRemapper();
            var bound = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in responses.Entries)
            {
                var channels = entry?.Animation?.Channels;
                if (channels == null) continue;
                foreach (var channel in channels)
                {
                    var target = channel?.Target;
                    if (target?.Identity == null || !channel.EmitsRecords) continue;
                    string property = target.Identity.CanonicalProperty;
                    switch (target.Kind)
                    {
                        case ExpressionPropertyKind.NodeTranslation:
                        case ExpressionPropertyKind.NodeRotation:
                        case ExpressionPropertyKind.NodeScale:
                            if (bound.Add(property))
                                AddNodeBinding(root, nodeIndexToGo, target, property, translationScale, bindings);
                            break;
                        case ExpressionPropertyKind.NodeWeights:
                            if (bound.Add(property)) AddMorphBinding(nodeIndexToGo, target, property, bindings);
                            break;
                        case ExpressionPropertyKind.TextureOffset:
                        case ExpressionPropertyKind.TextureScale:
                        case ExpressionPropertyKind.TextureRotation:
                            string transform = property.Substring(0, property.LastIndexOf('/'));
                            if (bound.Add(transform))
                                AddTextureBindings(root, importer, nodeIndexToGo, remapper, transform, bindings);
                            break;
                    }
                }
            }
            return bindings.ToArray();
        }

        private static void AddNodeBinding(
            GLTFRoot root,
            IReadOnlyDictionary<int, GameObject> nodeIndexToGo,
            ExpressionResponseTarget target,
            string property,
            float translationScale,
            List<ExpressionTargetBinding> bindings)
        {
            if (!TryGetNode(nodeIndexToGo, property, out int nodeIndex, out var nodeObject)) return;
            bindings.Add(new ExpressionTargetBinding
            {
                Property = property,
                Kind = target.Kind,
                AuthoredInitial = ToArray(target.AuthoredInitial),
                Node = nodeObject.transform,
                NodeHasForwardAxisConversion = root.Nodes != null && nodeIndex < root.Nodes.Count &&
                    KhrCharacterImportContext.HasForwardAxisConversion(root.Nodes[nodeIndex], nodeObject),
                TranslationScale = translationScale,
            });
        }

        private static void AddMorphBinding(
            IReadOnlyDictionary<int, GameObject> nodeIndexToGo,
            ExpressionResponseTarget target,
            string property,
            List<ExpressionTargetBinding> bindings)
        {
            if (!TryGetNode(nodeIndexToGo, property, out _, out var nodeObject)) return;
            var renderer = nodeObject.GetComponent<SkinnedMeshRenderer>();
            var mesh = renderer != null ? renderer.sharedMesh : null;
            var authored = ToArray(target.ArrayAuthoredInitial);
            if (mesh == null || authored == null || authored.Length != target.ArrayLength)
            {
                Debug.LogWarning(
                    $"[KHR_character] Expression property '{property}' has no blendshape renderer; the expression " +
                    "controller does not write it.");
                return;
            }
            var frameWeights = new float[authored.Length];
            for (int element = 0; element < frameWeights.Length; element++)
                frameWeights[element] = element < mesh.blendShapeCount && mesh.GetBlendShapeFrameCount(element) > 0
                    ? mesh.GetBlendShapeFrameWeight(element, 0)
                    : 1f;
            bindings.Add(new ExpressionTargetBinding
            {
                Property = property,
                Kind = ExpressionPropertyKind.NodeWeights,
                AuthoredInitial = authored,
                MorphRenderers = new[] { renderer },
                MorphFrameWeights = frameWeights,
            });
        }

        // The offset, scale and rotation of one KHR_texture_transform object are bound together, because Unity keeps
        // offset and scale in one _ST vector; in Standalone mode the controller writes a component without a record at
        // its authored value.
        private static void AddTextureBindings(
            GLTFRoot root,
            GLTFSceneImporter importer,
            IReadOnlyDictionary<int, GameObject> nodeIndexToGo,
            MaterialPropertiesRemapper remapper,
            string transform,
            List<ExpressionTargetBinding> bindings)
        {
            if (!KhrCharacterResponseBaker.TryGetAuthoredTextureTransform(
                    root, transform, out int materialIndex, out string textureInfoPath,
                    out var offset, out var scale, out double rotation, out int textureIndex))
            {
                Debug.LogWarning(
                    $"[KHR_character] Expression texture transform '{transform}' could not be resolved; the expression " +
                    "controller does not write it.");
                return;
            }
            var material = importer?.MaterialCache != null && materialIndex < importer.MaterialCache.Length
                ? importer.MaterialCache[materialIndex]?.UnityMaterial
                : null;
            var slots = KhrCharacterBaker.ResolveRendererSlots(root, nodeIndexToGo, materialIndex);
            string prefix = $"{textureInfoPath}/extensions/{ExtTextureTransformExtensionFactory.EXTENSION_NAME}/";
            string stProperty = null;
            string rotationProperty = null;
            if (material != null)
            {
                if (remapper.GetUnityPropertyName(
                        material, prefix + ExtTextureTransformExtensionFactory.OFFSET, out string stName, out var map, out _) &&
                    map.PropertyType == MaterialPointerPropertyMap.PropertyTypeOption.TextureTransform)
                    stProperty = stName;
                if (remapper.GetUnityPropertyName(
                        material, prefix + ExtTextureTransformExtensionFactory.ROTATION, out string rotationName, out _, out _))
                    rotationProperty = rotationName;
            }
            if (slots.Count == 0 || (stProperty == null && rotationProperty == null))
            {
                Debug.LogWarning(
                    $"[KHR_character] Expression texture transform '{transform}' has no Unity material property to " +
                    "write; the expression controller does not write it.");
                return;
            }

            var renderers = new Renderer[slots.Count];
            var materialSlots = new int[slots.Count];
            for (int index = 0; index < slots.Count; index++)
            {
                renderers[index] = slots[index].Renderer;
                materialSlots[index] = slots[index].Slot;
            }
            bool flipped = textureIndex >= 0 && root.Textures != null && textureIndex < root.Textures.Count &&
                           importer != null && importer.IsTextureFlippedOnImport(root.Textures[textureIndex]);

            void Add(string component, ExpressionPropertyKind kind, double[] authored) =>
                bindings.Add(new ExpressionTargetBinding
                {
                    Property = $"{transform}/{component}",
                    Kind = kind,
                    AuthoredInitial = authored,
                    TextureTransform = transform,
                    TextureRenderers = renderers,
                    TextureMaterialSlots = materialSlots,
                    TextureStProperty = stProperty,
                    TextureRotationProperty = rotationProperty,
                    TextureFlipped = flipped,
                });
            Add(ExtTextureTransformExtensionFactory.OFFSET, ExpressionPropertyKind.TextureOffset, offset);
            Add(ExtTextureTransformExtensionFactory.SCALE, ExpressionPropertyKind.TextureScale, scale);
            Add(ExtTextureTransformExtensionFactory.ROTATION, ExpressionPropertyKind.TextureRotation, new[] { rotation });
        }

        // Canonical node properties are /nodes/{n}/{path}.
        private static bool TryGetNode(
            IReadOnlyDictionary<int, GameObject> nodeIndexToGo,
            string property,
            out int nodeIndex,
            out GameObject nodeObject)
        {
            nodeObject = null;
            nodeIndex = -1;
            var segments = property.Split('/');
            if (segments.Length == 4 && segments[1] == "nodes" &&
                int.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out nodeIndex) &&
                nodeIndexToGo.TryGetValue(nodeIndex, out nodeObject) && nodeObject != null)
                return true;
            Debug.LogWarning(
                $"[KHR_character] Expression property '{property}' targets a node that was not instantiated; the " +
                "expression controller does not write it.");
            return false;
        }

        private static double[] ToArray(IReadOnlyList<double> values)
        {
            if (values == null) return null;
            var array = new double[values.Count];
            for (int index = 0; index < array.Length; index++) array[index] = values[index];
            return array;
        }
    }
}
