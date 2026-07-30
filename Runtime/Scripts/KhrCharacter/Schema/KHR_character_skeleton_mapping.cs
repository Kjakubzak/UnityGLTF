using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF root extension <c>KHR_character_skeleton_mapping</c>: maps an arbitrary rig to one or more target
    /// vocabularies. Per the spec: rigName -> { targetJointName -> { node, name? } }, where <c>node</c> is a
    /// glTFid (a 0-based index into the document's <c>nodes</c> array).
    /// </summary>
    public class KHR_character_skeleton_mapping : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_skeleton_mapping";

        public class JointAssociation
        {
            public int Node = -1;
            public string Name;
        }

        // rigName -> (vocabularyJoint -> source node association), kept verbatim.
        public Dictionary<string, Dictionary<string, JointAssociation>> SkeletalRigMappings
            = new Dictionary<string, Dictionary<string, JointAssociation>>();

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
                        {
                            if (jointKv.Value == null) continue;
                            var association = new JObject { { "node", jointKv.Value.Node } };
                            if (jointKv.Value.Name != null) association.Add("name", jointKv.Value.Name);
                            joints.Add(jointKv.Key, association);
                        }
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
                    var map = new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>();
                    foreach (var jointProp in joints.Properties())
                    {
                        // Legacy bare indices are intentionally dropped: the draft now requires an association.
                        if (!(jointProp.Value is JObject association)
                            || association["node"]?.Type != JTokenType.Integer)
                            continue;
                        map[jointProp.Name] = new KHR_character_skeleton_mapping.JointAssociation
                        {
                            Node = association["node"].Value<int>(),
                            Name = association["name"]?.Value<string>(),
                        };
                    }
                    ext.SkeletalRigMappings[rigProp.Name] = map;
                }
            }
            return ext;
        }
    }
}
