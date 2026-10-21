using System;
using System.Collections.Generic;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>The channel targets a derived expression extension may list.</summary>
    public enum ExpressionChannelClassification
    {
        /// <summary>Node <c>translation</c>, <c>rotation</c> or <c>scale</c>, in core or pointer form.</summary>
        NodeTrs,

        /// <summary>Node morph weights, as a whole array or one element.</summary>
        MorphWeights,

        /// <summary><c>offset</c>, <c>scale</c> or <c>rotation</c> of an authored <c>KHR_texture_transform</c>.</summary>
        TextureTransform,

        /// <summary>Any target; the extension adds no classification rule.</summary>
        Any,
    }

    /// <summary>An extension that may appear in an expression entry's <c>extensions</c> object.</summary>
    public sealed class ExpressionDerivedExtension
    {
        internal ExpressionDerivedExtension(
            string name,
            bool listsChannels,
            bool isSupported,
            ExpressionChannelClassification classification,
            Func<ExpressionResponseTarget, bool> declaresSingleWriter)
        {
            Name = name;
            ListsChannels = listsChannels;
            IsSupported = isSupported;
            Classification = classification;
            DeclaresSingleWriter = declaresSingleWriter;
        }

        public string Name { get; }

        /// <summary>
        /// True when the extension lists channels in a <c>channels</c> array, which makes it a derived expression
        /// extension.
        /// </summary>
        public bool ListsChannels { get; }

        /// <summary>
        /// True when this implementation implements the extension's handling, so channels it lists produce response
        /// records. Channels listed by an unsupported extension produce none.
        /// </summary>
        public bool IsSupported { get; }

        public ExpressionChannelClassification Classification { get; }

        /// <summary>
        /// Legacy host metadata retained for compatibility. It does not change mandatory additive aggregation.
        /// A historical declaration applies to the
        /// whole asset when an entry carries the extension and this implementation supports it.
        /// </summary>
        public Func<ExpressionResponseTarget, bool> DeclaresSingleWriter { get; }
    }

    /// <summary>
    /// The expression-entry extensions an import recognizes. The built-in joint, morphtarget and texture_transform
    /// extensions list channels and are supported; <c>KHR_character_expression_suppression</c> is recognized and
    /// lists no channels. Hosts register vendor extensions, which are unsupported unless registered otherwise. An
    /// entry extension that is not registered is unrecognized and makes listing and conflict validation incomplete.
    /// </summary>
    public sealed class ExpressionDerivedExtensionRegistry
    {
        private readonly Dictionary<string, ExpressionDerivedExtension> _extensions =
            new Dictionary<string, ExpressionDerivedExtension>(StringComparer.Ordinal);

        /// <summary>The registry the import plugin uses.</summary>
        public static ExpressionDerivedExtensionRegistry Default { get; } = new ExpressionDerivedExtensionRegistry();

        public ExpressionDerivedExtensionRegistry()
        {
            AddBuiltIn(KhrCharacterExtensionNames.ExpressionJoint, true, ExpressionChannelClassification.NodeTrs);
            AddBuiltIn(
                KhrCharacterExtensionNames.ExpressionMorphtarget,
                true,
                ExpressionChannelClassification.MorphWeights);
            AddBuiltIn(
                KhrCharacterExtensionNames.ExpressionTextureTransform,
                true,
                ExpressionChannelClassification.TextureTransform);
            AddBuiltIn(KhrCharacterExtensionNames.ExpressionSuppression, false, ExpressionChannelClassification.Any);
        }

        public IReadOnlyCollection<ExpressionDerivedExtension> Extensions => _extensions.Values;

        /// <summary>
        /// Registers or replaces a host or vendor entry extension. The built-in KHR extensions cannot be replaced.
        /// </summary>
        /// <param name="name">The exact extension name.</param>
        /// <param name="listsChannels">Whether the extension lists channels in a <c>channels</c> array.</param>
        /// <param name="classification">The channel targets the extension may list.</param>
        /// <param name="isSupported">Whether this host implements the extension's handling.</param>
        /// <param name="declaresSingleWriter">Legacy host metadata; the normative composer does not enforce cross-entry single-writer ownership.</param>
        public void Register(
            string name,
            bool listsChannels,
            ExpressionChannelClassification classification = ExpressionChannelClassification.Any,
            bool isSupported = false,
            Func<ExpressionResponseTarget, bool> declaresSingleWriter = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("An extension name is required.", nameof(name));
            if (IsBuiltIn(name))
                throw new ArgumentException($"'{name}' is a built-in expression extension.", nameof(name));
            _extensions[name] = new ExpressionDerivedExtension(
                name,
                listsChannels,
                isSupported,
                classification,
                declaresSingleWriter);
        }

        /// <summary>Removes a registered host or vendor extension. Built-in extensions stay registered.</summary>
        public bool Unregister(string name)
        {
            return name != null && !IsBuiltIn(name) && _extensions.Remove(name);
        }

        public bool TryGet(string name, out ExpressionDerivedExtension extension)
        {
            extension = null;
            return name != null && _extensions.TryGetValue(name, out extension);
        }

        /// <summary>True when the entry extension is registered, so it is known whether it lists channels.</summary>
        public bool IsRecognized(string name) => name != null && _extensions.ContainsKey(name);

        private void AddBuiltIn(string name, bool listsChannels, ExpressionChannelClassification classification)
        {
            _extensions.Add(name, new ExpressionDerivedExtension(name, listsChannels, true, classification, null));
        }

        private static bool IsBuiltIn(string name)
        {
            return name == KhrCharacterExtensionNames.ExpressionJoint
                   || name == KhrCharacterExtensionNames.ExpressionMorphtarget
                   || name == KhrCharacterExtensionNames.ExpressionTextureTransform
                   || name == KhrCharacterExtensionNames.ExpressionSuppression;
        }
    }
}
