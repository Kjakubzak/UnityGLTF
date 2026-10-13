using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Builds a small binary glTF in memory and imports it through <see cref="GLTFSceneImporter"/> with the character
    /// import plugin enabled, so tests exercise the complete import path.
    /// </summary>
    internal sealed class CharacterGlbBuilder
    {
        private readonly List<byte> _binary = new List<byte>();
        private readonly JArray _bufferViews = new JArray();
        private readonly JArray _accessors = new JArray();

        public CharacterGlbBuilder()
        {
            Json = new JObject
            {
                { "asset", new JObject { { "version", "2.0" } } },
                { "scene", 0 },
                { "scenes", new JArray(new JObject { { "nodes", new JArray(0) } }) },
            };
        }

        /// <summary>The glTF JSON; buffers, bufferViews and accessors are added by <see cref="ToGlb"/>.</summary>
        public JObject Json { get; }

        /// <summary>
        /// A character whose expression entries each list the one translation channel of node 0 with
        /// <c>KHR_character_expression_joint</c>. Translation is additive, so the entries may share it.
        /// </summary>
        public static CharacterGlbBuilder TranslationCharacter(params string[] names)
        {
            var glb = new CharacterGlbBuilder();
            int input = glb.AddFloatAccessor("SCALAR", 2, new[] { 0f, 1f }, true);
            int output = glb.AddFloatAccessor("VEC3", 2, new[] { 0f, 0f, 0f, 1f, 0f, 0f });
            glb.Json["extensionsUsed"] = new JArray(
                "KHR_character", "KHR_character_expression", "KHR_character_expression_joint");
            glb.Json["nodes"] = new JArray(new JObject { { "name", "body" } });
            glb.Json["animations"] = new JArray(new JObject
            {
                { "samplers", new JArray(new JObject { { "input", input }, { "output", output } }) },
                {
                    "channels", new JArray(new JObject
                    {
                        { "sampler", 0 },
                        { "target", new JObject { { "node", 0 }, { "path", "translation" } } },
                    })
                },
            });
            var expressions = new JArray();
            foreach (var name in names)
                expressions.Add(new JObject
                {
                    { "name", name },
                    { "animation", 0 },
                    {
                        "extensions", new JObject
                        {
                            { "KHR_character_expression_joint", new JObject { { "channels", new JArray(0) } } },
                        }
                    },
                });
            glb.Json["extensions"] = new JObject
            {
                { "KHR_character", new JObject() },
                { "KHR_character_expression", new JObject { { "expressions", expressions } } },
            };
            return glb;
        }

        /// <summary>The JSON object of an expression entry.</summary>
        public JObject Expression(int index)
            => (JObject)Json["extensions"]["KHR_character_expression"]["expressions"][index];

        /// <summary>Adds a FLOAT accessor; a SCALAR input accessor gets min and max.</summary>
        public int AddFloatAccessor(string type, int count, float[] values, bool withMinMax = false)
        {
            var data = new List<byte>();
            foreach (float value in values) data.AddRange(BitConverter.GetBytes(value));
            var accessor = new JObject
            {
                { "bufferView", AddBufferView(data.ToArray()) },
                { "componentType", 5126 },
                { "count", count },
                { "type", type },
            };
            if (withMinMax)
            {
                float min = float.MaxValue, max = float.MinValue;
                foreach (float value in values)
                {
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                }
                accessor["min"] = new JArray(min);
                accessor["max"] = new JArray(max);
            }
            _accessors.Add(accessor);
            return _accessors.Count - 1;
        }

        /// <summary>Adds an integer accessor from little-endian component bytes.</summary>
        public int AddIntegerAccessor(string type, int count, int componentType, bool normalized, byte[] data)
        {
            var accessor = new JObject
            {
                { "bufferView", AddBufferView(data) },
                { "componentType", componentType },
                { "count", count },
                { "type", type },
            };
            if (normalized) accessor["normalized"] = true;
            _accessors.Add(accessor);
            return _accessors.Count - 1;
        }

        /// <summary>Adds a bufferView holding <paramref name="data"/>, aligned to four bytes.</summary>
        public int AddBufferView(byte[] data)
        {
            while (_binary.Count % 4 != 0) _binary.Add(0);
            _bufferViews.Add(new JObject
            {
                { "buffer", 0 },
                { "byteOffset", _binary.Count },
                { "byteLength", data.Length },
            });
            _binary.AddRange(data);
            return _bufferViews.Count - 1;
        }

        public byte[] ToGlb()
        {
            Json["buffers"] = new JArray(new JObject { { "byteLength", _binary.Count } });
            Json["bufferViews"] = _bufferViews;
            Json["accessors"] = _accessors;

            var json = Encoding.UTF8.GetBytes(Json.ToString(Formatting.None));
            int jsonLength = (json.Length + 3) & ~3;
            int binaryLength = (_binary.Count + 3) & ~3;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546C67u);
                writer.Write(2u);
                writer.Write((uint)(12 + 8 + jsonLength + 8 + binaryLength));
                writer.Write((uint)jsonLength);
                writer.Write(0x4E4F534Au);
                writer.Write(json);
                for (int index = json.Length; index < jsonLength; index++) writer.Write((byte)' ');
                writer.Write((uint)binaryLength);
                writer.Write(0x004E4942u);
                writer.Write(_binary.ToArray());
                for (int index = _binary.Count; index < binaryLength; index++) writer.Write((byte)0);
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>Imports the asset and passes the imported scene root to <paramref name="onLoaded"/>.</summary>
        public IEnumerator Import(Action<KhrCharacterImportPlugin> configure, Action<GameObject> onLoaded)
            => Import(ToGlb(), configure, onLoaded);

        /// <summary>Imports a binary glTF, such as an exported asset.</summary>
        public static IEnumerator ImportGlb(
            byte[] glb,
            Action<KhrCharacterImportPlugin> configure,
            Action<GameObject> onLoaded)
            => Import(glb, configure, onLoaded);

        /// <summary>Imports a complete glTF JSON document whose buffers are data URIs.</summary>
        public static IEnumerator ImportJson(
            string json,
            Action<KhrCharacterImportPlugin> configure,
            Action<GameObject> onLoaded)
            => Import(Encoding.UTF8.GetBytes(json), configure, onLoaded);

        private static IEnumerator Import(
            byte[] asset,
            Action<KhrCharacterImportPlugin> configure,
            Action<GameObject> onLoaded)
        {
            var settings = GLTFSettings.GetDefaultSettings();
            foreach (var plugin in settings.ImportPlugins)
            {
                if (!(plugin is KhrCharacterImportPlugin character)) continue;
                character.Enabled = true;
                configure?.Invoke(character);
            }
            var options = new ImportOptions
            {
                AnimationMethod = AnimationMethod.None,
                ThrowOnLowMemory = false,
                ImportContext = new GLTFImportContext(settings),
            };
            var stream = new MemoryStream(asset);
            var importer = new GLTFSceneImporter(stream, options);
            var load = importer.LoadSceneAsync();
            while (!load.IsCompleted) yield return null;
            Assert.IsFalse(load.IsFaulted, load.Exception?.Flatten().ToString());
            var scene = importer.LastLoadedScene;
            importer.Dispose();
            stream.Dispose();
            onLoaded(scene);
        }
    }
}
