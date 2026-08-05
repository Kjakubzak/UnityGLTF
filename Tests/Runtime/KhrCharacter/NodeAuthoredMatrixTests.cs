using System.IO;
using GLTF.Schema;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnityGLTF.KhrCharacter.Tests
{
    public class NodeAuthoredMatrixTests
    {
        private const string IdentityMatrixJson =
            "[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]";

        [Test]
        public void OmittedMatrixStateSurvivesNodeCopyAndSerialization()
        {
            var source = DeserializeNode("{}");
            var copy = new Node(source, new GLTFRoot());

            Assert.IsFalse(source.HasMatrix);
            Assert.IsFalse(copy.HasMatrix);
            Assert.IsTrue(copy.Matrix.Equals(GLTF.Math.Matrix4x4.Identity));
            Assert.IsNull(SerializeNode(copy)["matrix"]);
        }

        [Test]
        public void ExplicitIdentityMatrixStateSurvivesNodeCopyAndSerialization()
        {
            var source = DeserializeNode($"{{\"matrix\":{IdentityMatrixJson}}}");
            var copy = new Node(source, new GLTFRoot());

            Assert.IsTrue(source.HasMatrix);
            Assert.IsTrue(copy.HasMatrix);
            Assert.IsTrue(copy.Matrix.Equals(GLTF.Math.Matrix4x4.Identity));
            Assert.IsNotNull(SerializeNode(source)["matrix"]);
            Assert.IsNotNull(SerializeNode(copy)["matrix"]);
        }

        [Test]
        public void ProgrammaticNonIdentityMatrixSerializesWithoutAuthoredFlag()
        {
            var node = new Node
            {
                Matrix = new GLTF.Math.Matrix4x4(
                    1f, 0f, 0f, 0f,
                    0f, 1f, 0f, 0f,
                    0f, 0f, 1f, 0f,
                    2f, 0f, 0f, 1f),
            };

            Assert.IsFalse(node.HasMatrix);
            Assert.IsNotNull(SerializeNode(node)["matrix"]);
        }

        private static Node DeserializeNode(string json)
        {
            using (var stringReader = new StringReader(json))
            using (var jsonReader = new JsonTextReader(stringReader))
            {
                jsonReader.Read();
                return Node.Deserialize(new GLTFRoot(), jsonReader);
            }
        }

        private static JObject SerializeNode(Node node)
        {
            using (var stringWriter = new StringWriter())
            using (var jsonWriter = new JsonTextWriter(stringWriter))
            {
                node.Serialize(jsonWriter);
                jsonWriter.Flush();
                return JObject.Parse(stringWriter.ToString());
            }
        }
    }
}
