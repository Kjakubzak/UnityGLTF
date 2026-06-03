using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
    /// <summary>
    /// glTF animation extension <c>KHR_character_reference_pose</c>: marks an animation as a canonical
    /// reference (retarget) pose. <c>poseType</c> defaults to "TPose". Retarget-only — NOT the additive base.
    /// </summary>
    public class KHR_character_reference_pose : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_reference_pose";

        public string PoseType = "TPose";
        public JProperty RawData;

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);
            return new JProperty(EXTENSION_NAME, new JObject { { "poseType", PoseType } });
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_reference_pose
        { PoseType = PoseType, RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_character_reference_pose_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_reference_pose.EXTENSION_NAME;
        public KHR_character_reference_pose_Factory() { ExtensionName = EXTENSION_NAME; }
        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_character_reference_pose { RawData = token };
            if (token.Value is JObject obj)
                ext.PoseType = obj["poseType"]?.Value<string>() ?? "TPose";
            return ext;
        }
    }
}
