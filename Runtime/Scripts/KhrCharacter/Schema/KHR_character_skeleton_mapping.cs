using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF root extension <c>KHR_character_skeleton_mapping</c>: maps an arbitrary rig to one or more target
    /// vocabularies. Per the spec: rigName -> { targetJointName -> sourceNodeName }. The data is parsed as
    /// authored; the key/value direction is resolved when the rig is consumed.
    /// </summary>
    public class KHR_character_skeleton_mapping : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_skeleton_mapping";

        // rigName -> (keyJoint -> valueJoint), kept verbatim.
        public Dictionary<string, Dictionary<string, string>> SkeletalRigMappings
            = new Dictionary<string, Dictionary<string, string>>();

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
                            joints.Add(jointKv.Key, jointKv.Value);
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
                    var map = new Dictionary<string, string>();
                    foreach (var jointProp in joints.Properties())
                        map[jointProp.Name] = (jointProp.Value as JValue)?.Value<string>(); // tolerate non-string values
                    ext.SkeletalRigMappings[rigProp.Name] = map;
                }
            }
            return ext;
        }
    }
}
