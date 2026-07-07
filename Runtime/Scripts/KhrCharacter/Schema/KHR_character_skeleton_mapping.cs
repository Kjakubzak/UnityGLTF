using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF root extension <c>KHR_character_skeleton_mapping</c>: maps an arbitrary rig to one or more target
    /// vocabularies. Per the spec: rigName -> { targetJointName -> sourceNodeIndex }, where the value is a
    /// glTFid (a 0-based index into the document's <c>nodes</c> array).
    /// </summary>
    public class KHR_character_skeleton_mapping : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_skeleton_mapping";

        // rigName -> (vocabularyJoint -> node index), kept verbatim.
        public Dictionary<string, Dictionary<string, int>> SkeletalRigMappings
            = new Dictionary<string, Dictionary<string, int>>();

        public JProperty RawData;

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);

            var rigs = new JObject();
            if (SkeletalRigMappings != null)
            {
                foreach (var rigKv in SkeletalRigMappings)
                {
                    var joints = new JObject();
                    if (rigKv.Value != null)
                        foreach (var jointKv in rigKv.Value)
                            joints.Add(jointKv.Key, jointKv.Value); // int -> JValue
                    rigs.Add(rigKv.Key, joints);
                }
            }
            return new JProperty(EXTENSION_NAME, new JObject { { "skeletalRigMappings", rigs } });
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_skeleton_mapping
        { SkeletalRigMappings = SkeletalRigMappings, RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_character_skeleton_mapping_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_skeleton_mapping.EXTENSION_NAME;
        public KHR_character_skeleton_mapping_Factory() { ExtensionName = EXTENSION_NAME; }

        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_character_skeleton_mapping { RawData = token };
            if (token.Value is JObject obj && obj["skeletalRigMappings"] is JObject rigs)
            {
                foreach (var rigProp in rigs.Properties())
                {
                    if (!(rigProp.Value is JObject joints)) continue;
                    var map = new Dictionary<string, int>();
                    foreach (var jointProp in joints.Properties())
                    {
                        // Values are node indices (glTFid, mirroring KHR_character.rootNode). Legacy name-string
                        // values are intentionally dropped (hard cut): such an entry simply does not resolve to a
                        // bone on import rather than throwing and failing the whole document load.
                        if (jointProp.Value is JValue jv && jv.Type == JTokenType.Integer)
                            map[jointProp.Name] = jv.Value<int>();
                    }
                    ext.SkeletalRigMappings[rigProp.Name] = map;
                }
            }
            return ext;
        }
    }
}
