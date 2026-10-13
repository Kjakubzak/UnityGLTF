using Newtonsoft.Json.Linq;
using UnityGLTF.KhrCharacter;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Standard <c>KHR_character_reference_pose</c> <c>poseType</c> labels. Labels are exact and case-sensitive:
    /// any other nonempty string, including <c>"TPose"</c> or <c>"T_POSE"</c>, is a custom label kept verbatim.
    /// </summary>
    public static class ReferencePoseLabels
    {
        public const string TPose = "t_pose";
        public const string APose = "a_pose";
        public const string IPose = "i_pose";
    }
}

namespace GLTF.Schema
{
    /// <summary>
    /// glTF animation extension <c>KHR_character_reference_pose</c>: marks an animation as a canonical
    /// reference (retarget) pose. An omitted <c>poseType</c> resolves to <c>t_pose</c>. Retarget-only — NOT the
    /// additive base.
    /// </summary>
    public class KHR_character_reference_pose : IExtension
    {
        public const string EXTENSION_NAME = "KHR_character_reference_pose";

        public string PoseType = ReferencePoseLabels.TPose;
        public JProperty RawData;

        public JProperty Serialize()
        {
            if (RawData != null) return new JProperty(RawData.Name, RawData.Value);
            var obj = new JObject();
            if (PoseType != null) obj["poseType"] = PoseType;
            return new JProperty(EXTENSION_NAME, obj);
        }

        public IExtension Clone(GLTFRoot root) => new KHR_character_reference_pose
        { PoseType = PoseType, RawData = RawData != null ? new JProperty(RawData) : null };
    }

    public class KHR_character_reference_pose_Factory : ExtensionFactory
    {
        public const string EXTENSION_NAME = KHR_character_reference_pose.EXTENSION_NAME;
        public KHR_character_reference_pose_Factory()
        {
            ExtensionName = EXTENSION_NAME;
            RequiresRuntimeSupportForRequiredUse = true;
        }
        public override IExtension Deserialize(GLTFRoot root, JProperty token)
        {
            var ext = new KHR_character_reference_pose { RawData = token };
            if (token.Value is JObject obj)
                ext.PoseType = obj["poseType"]?.Value<string>() ?? ReferencePoseLabels.TPose;
            return ext;
        }
    }
}
