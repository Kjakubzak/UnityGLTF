using System;
using System.Collections.Generic;
using GLTF.Schema;
using GLTF.Schema.KHR_lights_punctual;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityGLTF.Extensions;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Tests generic skeleton-association resolution by glTF node index, preservation of multiple mapping sets,
    /// reference-pose resolution and validation, and the separate Unity Humanoid adapter.
    /// </summary>
    public class SkeletonMappingTests
    {
        private const string Vocab = "https://example.com/skeleton/v1";
        private const string SparseVocab = "https://example.com/skeleton/sparse/v1";
        private const string FullVocab = "https://example.com/skeleton/full/v1";
        private static readonly string[] RecognizedUnityHumanoidRoles =
        {
            "hips", "spine", "chest", "upperChest", "neck", "head", "jaw", "leftEye", "rightEye",
            "leftShoulder", "rightShoulder", "leftUpperArm", "leftLowerArm", "leftHand", "rightUpperArm",
            "rightLowerArm", "rightHand", "leftUpperLeg", "leftLowerLeg", "leftFoot", "leftToes",
            "rightUpperLeg", "rightLowerLeg", "rightFoot", "rightToes",
        };

        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        private GameObject NewGo(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        private static KHR_character_skeleton_mapping.JointAssociation Joint(int node, string name = null)
            => new KHR_character_skeleton_mapping.JointAssociation { Node = node, Name = name };

        private static KHR_character_skeleton_mapping Mapping(Dictionary<string, KHR_character_skeleton_mapping.JointAssociation> rig)
            => new KHR_character_skeleton_mapping
            {
                SkeletalRigMappings = new Dictionary<string, Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>> { { Vocab, rig } }
            };

        private static GLTFRoot RootWithNodes(int count)
        {
            var root = new GLTFRoot { Nodes = new List<Node>() };
            for (int i = 0; i < count; i++) root.Nodes.Add(new Node());
            return root;
        }

        private SkeletonMappingResult BakeRecognizedRoles(string omittedRole = null, string unresolvedRole = null)
        {
            var nodeMap = new Dictionary<int, GameObject>();
            var rig = new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>();
            int nodeIndex = 0;
            foreach (var role in RecognizedUnityHumanoidRoles)
            {
                if (role == omittedRole) continue;
                if (role == unresolvedRole)
                {
                    rig[role] = Joint(9999);
                    continue;
                }

                var bone = NewGo(role);
                nodeMap[nodeIndex] = bone;
                rig[role] = Joint(nodeIndex);
                nodeIndex++;
            }
            return KhrCharacterSkeletonBaker.BakeSkeleton(RootWithNodes(nodeIndex), nodeMap, Mapping(rig));
        }

        [Test]
        public void BakeSkeleton_ResolvesByNodeIndex()
        {
            var hips = NewGo("Hips_node");
            var head = NewGo("Head_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };
            var ext = Mapping(new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>
            {
                { "hips", Joint(0, "Hips_node") },
                { "head", Joint(1, "Head_node") }
            });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.AreSame(head.transform, result.Bones["head"]);
            Assert.IsTrue(result.Report.IsValid, "omitting roles is valid generic skeleton-mapping data");
            Assert.Contains("leftFoot", result.Report.MissingRequiredBones,
                "the separately assessed Unity Humanoid adapter is incomplete");
            Assert.AreEqual(Vocab, result.MappingSets[0].Identifier);
        }

        [Test]
        public void BakeSkeleton_AllRecognizedRequiredRolesResolve_UnityAdapterIsHealthy()
        {
            var result = BakeRecognizedRoles();

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Report.IsValid);
            Assert.IsEmpty(result.Report.MissingRequiredBones);
        }

        [Test]
        public void BakeSkeleton_OmittedRequiredRole_IsValidMappingButDegradedUnityAdapter()
        {
            var result = BakeRecognizedRoles(omittedRole: "leftFoot");

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Report.IsValid,
                "KHR_character_skeleton_mapping does not prescribe required anatomy");
            CollectionAssert.AreEqual(new[] { "leftFoot" }, result.Report.MissingRequiredBones);
            StringAssert.Contains("does not make the skeleton mapping invalid", result.Report.Warnings[0]);
        }

        [Test]
        public void BakeSkeleton_OmittedOptionalRole_DoesNotDegradeUnityAdapter()
        {
            var result = BakeRecognizedRoles(omittedRole: "jaw");

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Report.IsValid);
            Assert.IsEmpty(result.Report.MissingRequiredBones, "jaw is optional to Unity Humanoid");
        }

        [Test]
        public void BakeSkeleton_MultipleSets_PreservesAllAndSelectsLargestForUnityAdapter()
        {
            var hips = NewGo("Hips_node");
            var head = NewGo("Head_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips }, { 1, head } };

            var ext = new KHR_character_skeleton_mapping
            {
                SkeletalRigMappings = new Dictionary<string, Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>>
                {
                    { SparseVocab, new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation> { { "hips", Joint(0) } } },
                    { FullVocab, new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation> { { "hips", Joint(0) }, { "head", Joint(1) } } },
                }
            };

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreEqual(2, result.MappingSets.Length, "generic support preserves every mapping set");
            Assert.AreEqual(1, result.MappingSets[0].Associations.Count);
            Assert.AreEqual(2, result.MappingSets[1].Associations.Count);
            Assert.AreEqual(FullVocab, result.SelectedRig, "largest-set selection is only the Unity adapter policy");
            Assert.AreEqual(2, result.Bones.Count);
            Assert.IsEmpty(result.MappingSets[0].Report.MissingRequiredBones,
                "adapter health is assessed only for the selected mapping set");
            Assert.IsNotEmpty(result.MappingSets[1].Report.MissingRequiredBones);
        }

        [Test]
        public void BakeSkeleton_UnresolvedRequiredRole_SeparatesValidityFromAdapterHealth()
        {
            var hips = NewGo("Hips_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips } };
            var ext = Mapping(new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>
            {
                { "hips", Joint(0) },
                { "leftFoot", Joint(99) }, // no such node index
            });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(RootWithNodes(1), nodeMap, ext);

            Assert.IsNotNull(result);
            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.IsFalse(result.Bones.ContainsKey("leftFoot"));
            Assert.IsFalse(result.MappingSets[0].NodeIndices.ContainsKey("leftFoot"));
            Assert.IsFalse(result.Report.IsValid, "an association whose node index does not exist is invalid");
            Assert.Contains("leftFoot", result.Report.MissingRequiredBones,
                "the optional Unity adapter independently records its required recognized role");
        }

        [Test]
        public void BakeSkeleton_UnresolvedRole_DoesNotApplyUnityHumanoidOptionality()
        {
            var result = BakeRecognizedRoles(unresolvedRole: "jaw");

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Report.MissingRequiredBones.Count);
            Assert.IsFalse(result.Report.IsValid,
                "a dangling association is invalid even when Unity Humanoid treats that role as optional");
            Assert.Greater(result.Report.Warnings.Count, 0);
        }

        [Test]
        public void BakeSkeleton_AssociationToUninstantiatedNodeKeepsItsIndex()
        {
            var hips = NewGo("Hips_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips } };
            var ext = Mapping(new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>
            {
                { "hips", Joint(0) },
                { "head", Joint(2, "Head") },
            });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(RootWithNodes(3), nodeMap, ext);

            var set = result.MappingSets[0];
            Assert.AreEqual(0, set.NodeIndices["hips"]);
            Assert.AreEqual(2, set.NodeIndices["head"], "the association keeps its node index");
            Assert.IsFalse(set.Associations.ContainsKey("head"));
            Assert.IsTrue(set.Report.IsValid, "a node outside the imported scene does not invalidate the mapping");
            Assert.IsNotEmpty(set.Report.Warnings);

            var skeleton = NewGo("char").AddComponent<SkeletonMap>();
            skeleton.Bind(SerializableSkeletonMapping.FromResult(result).ToResult());
            Assert.IsTrue(skeleton.TryGetAssociationNodeIndex(Vocab, "head", out var headIndex));
            Assert.AreEqual(2, headIndex, "node indices survive serialization");
            Assert.IsFalse(skeleton.TryGetAssociation(Vocab, "head", out _));
            Assert.IsTrue(skeleton.TryGetAssociationNodeIndex(Vocab, "hips", out var hipsIndex));
            Assert.AreEqual(0, hipsIndex);
        }

        [Test]
        public void BakeSkeleton_IndependentAssociationNameIsPreserved()
        {
            var hips = NewGo("Hips_node");
            var nodeMap = new Dictionary<int, GameObject> { { 0, hips } };
            var ext = Mapping(new Dictionary<string, KHR_character_skeleton_mapping.JointAssociation>
            {
                { "hips", Joint(0, "WrongName") },
            });

            var result = KhrCharacterSkeletonBaker.BakeSkeleton(new GLTFRoot(), nodeMap, ext);

            Assert.AreSame(hips.transform, result.Bones["hips"]);
            Assert.AreEqual("WrongName", result.MappingSets[0].AssociationNames["hips"]);
            Assert.IsFalse(result.Report.Warnings.Exists(warning => warning.Contains("WrongName")));
        }

        [Test]
        public void BuildHumanoidAvatar_MissingRequiredBones_ReturnsNullAndFallsBack()
        {
            var root = NewGo("char");
            var hips = NewGo("hips");
            hips.transform.SetParent(root.transform, false);
            var head = NewGo("head");
            head.transform.SetParent(root.transform, false);

            var skel = root.AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform> { { "hips", hips.transform }, { "head", head.transform } },
                SelectedRig = "rig",
            });

            var avatar = skel.BuildHumanoidAvatar();
            if (avatar != null) _created.Add(avatar);

            Assert.IsNull(avatar);
            Assert.IsFalse(skel.HumanoidAvailable);
        }

        [Test]
        public void HasDuplicateBoundName_DetectsCollision()
        {
            var a = NewGo("Bone");
            var b = NewGo("Bone"); // same name -> ambiguous binding
            var c = NewGo("Other");
            var transforms = new[] { a.transform, b.transform, c.transform };
            var human = new List<HumanBone> { new HumanBone { boneName = "Bone", humanName = "Hips" } };

            Assert.IsTrue(SkeletonMap.HasDuplicateBoundName(transforms, human, out var dup));
            Assert.AreEqual("Bone", dup);
        }

        [Test]
        public void HasDuplicateBoundName_UniqueNames_False()
        {
            var a = NewGo("Hips");
            var b = NewGo("Head");
            var transforms = new[] { a.transform, b.transform };
            var human = new List<HumanBone>
            {
                new HumanBone { boneName = "Hips", humanName = "Hips" },
                new HumanBone { boneName = "Head", humanName = "Head" },
            };

            Assert.IsFalse(SkeletonMap.HasDuplicateBoundName(transforms, human, out _));
        }

        [Test]
        public void ApplyReferencePose_MovesBonesToBakedPose()
        {
            var bone = NewGo("bone");
            var target = new Vector3(0f, 2f, 0f);
            var rot = Quaternion.Euler(0f, 90f, 0f);

            var skel = NewGo("char").AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform>(),
                ReferencePose = new ReferencePose
                {
                    PoseType = ReferencePoseLabels.TPose,
                    Bones = new[] { bone.transform },
                    LocalPositions = new[] { target },
                    LocalRotations = new[] { rot },
                    LocalScales = new[] { Vector3.one },
                },
            });

            skel.ApplyReferencePose();

            Assert.Less(Vector3.Distance(target, bone.transform.localPosition), 1e-5f);
            Assert.Less(Quaternion.Angle(rot, bone.transform.localRotation), 0.01f);
        }

        [Test]
        public void SkeletonMap_ExposesMappingSetsAndSelectsReferencePoseByAnimationIndex()
        {
            var hips = NewGo("hips").transform;
            var head = NewGo("head").transform;
            var target = new Vector3(0f, 3f, 0f);
            var skel = NewGo("char").AddComponent<SkeletonMap>();
            skel.Bind(new SkeletonMappingResult
            {
                MappingSets = new[]
                {
                    new SkeletonMappingSetResult
                    {
                        Identifier = Vocab,
                        Associations = new Dictionary<string, Transform> { { "hips", hips } },
                    },
                    new SkeletonMappingSetResult
                    {
                        Identifier = FullVocab,
                        Associations = new Dictionary<string, Transform> { { "head", head } },
                    },
                },
                ReferencePoses = new[]
                {
                    new ReferencePose { AnimationIndex = 3, PoseType = ReferencePoseLabels.TPose, Bones = new[] { hips }, LocalPositions = new[] { Vector3.one } },
                    new ReferencePose { AnimationIndex = 7, PoseType = ReferencePoseLabels.TPose, Bones = new[] { hips }, LocalPositions = new[] { target } },
                },
                Bones = new Dictionary<string, Transform> { { "hips", hips } },
                SelectedRig = Vocab,
            });

            Assert.AreEqual(2, skel.RigVocabularies.Count);
            Assert.IsTrue(skel.TryGetAssociation(FullVocab, "head", out var resolved));
            Assert.AreSame(head, resolved);
            Assert.AreEqual(2, skel.ReferencePoses.Count);
            Assert.IsTrue(skel.ApplyReferencePose(7));
            Assert.AreEqual(target, hips.localPosition);
            Assert.IsFalse(skel.ApplyReferencePose(99));
        }

        private sealed class PoseFixture
        {
            private readonly Dictionary<BufferView, byte[]> _data = new Dictionary<BufferView, byte[]>();

            public GLTFRoot Root { get; } = new GLTFRoot
            {
                Accessors = new List<Accessor>(),
                Animations = new List<GLTFAnimation>(),
                BufferViews = new List<BufferView>(),
                Nodes = new List<Node>(),
                ExtensionsUsed = new List<string>
                {
                    KhrCharacterExtensionNames.Character, KhrCharacterExtensionNames.ReferencePose,
                },
            };

            public Dictionary<int, GameObject> Objects { get; } = new Dictionary<int, GameObject>();

            public int AddFloats(GLTFAccessorAttributeType type, uint count, params float[] values)
            {
                var view = new BufferView { ByteLength = (uint)(values.Length * sizeof(float)) };
                var bytes = new byte[values.Length * sizeof(float)];
                Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                Root.BufferViews.Add(view);
                _data.Add(view, bytes);
                Root.Accessors.Add(new Accessor
                {
                    BufferView = new BufferViewId { Id = Root.BufferViews.Count - 1, Root = Root },
                    ComponentType = GLTFComponentType.Float,
                    Count = count,
                    Type = type,
                });
                return Root.Accessors.Count - 1;
            }

            public int AddSparseOnly(GLTFAccessorAttributeType type, params float[] values)
            {
                var indexView = new BufferView { ByteLength = 1 };
                Root.BufferViews.Add(indexView);
                _data.Add(indexView, new byte[] { 0 });
                var valueView = new BufferView { ByteLength = (uint)(values.Length * sizeof(float)) };
                var bytes = new byte[values.Length * sizeof(float)];
                Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                Root.BufferViews.Add(valueView);
                _data.Add(valueView, bytes);
                Root.Accessors.Add(new Accessor
                {
                    ComponentType = GLTFComponentType.Float,
                    Count = 1,
                    Type = type,
                    Sparse = new AccessorSparse
                    {
                        Count = 1,
                        Indices = new AccessorSparseIndices
                        {
                            BufferView = new BufferViewId { Id = Root.BufferViews.Count - 2, Root = Root },
                            ComponentType = GLTFComponentType.UnsignedByte,
                        },
                        Values = new AccessorSparseValues
                        {
                            BufferView = new BufferViewId { Id = Root.BufferViews.Count - 1, Root = Root },
                        },
                    },
                });
                return Root.Accessors.Count - 1;
            }

            public int Translation(float x, float y, float z) => AddFloats(GLTFAccessorAttributeType.VEC3, 1, x, y, z);

            public int Rotation(float x, float y, float z, float w)
                => AddFloats(GLTFAccessorAttributeType.VEC4, 1, x, y, z, w);

            public int AddNode(Node node, GameObject instance)
            {
                Root.Nodes.Add(node);
                int index = Root.Nodes.Count - 1;
                if (instance != null)
                {
                    Objects[index] = instance;
                    node.GetUnityTRSProperties(out var position, out var rotation, out var scale);
                    instance.transform.localPosition = position;
                    instance.transform.localRotation = rotation;
                    instance.transform.localScale = scale;
                }
                return index;
            }

            public void AddPose(string poseType, params (int node, string path, int output)[] channels)
                => AddAnimation(poseType, InterpolationType.STEP, channels);

            public GLTFAnimation AddAnimation(
                string poseType, InterpolationType interpolation, params (int node, string path, int output)[] channels)
            {
                var animation = new GLTFAnimation();
                int input = AddFloats(GLTFAccessorAttributeType.SCALAR, 1, 0f);
                foreach (var (node, path, output) in channels)
                {
                    animation.Samplers.Add(new AnimationSampler
                    {
                        Input = new AccessorId { Id = input, Root = Root },
                        Output = new AccessorId { Id = output, Root = Root },
                        Interpolation = interpolation,
                    });
                    animation.Channels.Add(new AnimationChannel
                    {
                        Sampler = new AnimationSamplerId
                        {
                            Id = animation.Samplers.Count - 1, Root = Root, GLTFAnimation = animation,
                        },
                        Target = new AnimationChannelTarget
                        {
                            Node = new NodeId { Id = node, Root = Root },
                            Path = path,
                        },
                    });
                }
                var extension = new KHR_character_reference_pose { PoseType = poseType };
                animation.AddExtension(KHR_character_reference_pose.EXTENSION_NAME, extension);
                Root.Animations.Add(animation);
                return animation;
            }

            public ReferencePose[] Bake(float translationScale = 1f)
                => KhrCharacterSkeletonBaker.BakeReferencePoses(Root, view => _data[view], Objects, translationScale);
        }

        private static Vector3 UnityPosition(float x, float y, float z) => new Vector3(-x, y, z);

        private static Quaternion UnityRotation(float x, float y, float z, float w) => new Quaternion(x, -y, -z, w);

        private static Node TrsNode(string name, Vector3 gltfTranslation, params int[] children)
        {
            var node = new Node
            {
                Name = name,
                UseTRS = true,
                Translation = new GLTF.Math.Vector3(gltfTranslation.x, gltfTranslation.y, gltfTranslation.z),
            };
            if (children.Length > 0)
            {
                node.Children = new List<NodeId>();
                foreach (int child in children) node.Children.Add(new NodeId { Id = child });
            }
            return node;
        }

        private SkeletonMap BindPoses(GameObject root, ReferencePose[] poses)
        {
            var skeleton = root.AddComponent<SkeletonMap>();
            skeleton.Bind(new SkeletonMappingResult
            {
                Bones = new Dictionary<string, Transform>(),
                ReferencePoses = poses,
                ReferencePose = Array.Find(poses, pose => pose.IsValid),
            });
            return skeleton;
        }

        // Root (0) -> Spine (1) -> Head (3), plus Prop (2) under Root. Spine's translation and Prop are authored and
        // untargeted; the pose targets Spine's rotation and Head's translation.
        private PoseFixture BuildHierarchy(out GameObject scene, out Transform spine, out Transform head, out Transform prop)
        {
            var fixture = new PoseFixture();
            scene = NewGo("scene");
            var rootObject = NewGo("Root");
            rootObject.transform.SetParent(scene.transform, false);
            var spineObject = NewGo("Spine");
            spineObject.transform.SetParent(rootObject.transform, false);
            var propObject = NewGo("Prop");
            propObject.transform.SetParent(rootObject.transform, false);
            var headObject = NewGo("Head");
            headObject.transform.SetParent(spineObject.transform, false);

            fixture.AddNode(TrsNode("Root", new Vector3(0f, 1f, 0f), 1, 2), rootObject);
            var spineNode = TrsNode("Spine", new Vector3(0.1f, 0.5f, 0f), 3);
            spineNode.Rotation = new GLTF.Math.Quaternion(0f, 0f, 0.38268343f, 0.9238795f);
            spineNode.Scale = new GLTF.Math.Vector3(1f, 2f, 1f);
            fixture.AddNode(spineNode, spineObject);
            fixture.AddNode(TrsNode("Prop", new Vector3(1f, 0f, 0f)), propObject);
            fixture.AddNode(TrsNode("Head", new Vector3(0f, 0.25f, 0f)), headObject);
            spine = spineObject.transform;
            head = headObject.transform;
            prop = propObject.transform;
            return fixture;
        }

        [Test]
        public void ReferencePose_ResolvesEveryNodeFromAuthoredValues()
        {
            var fixture = BuildHierarchy(out _, out var spine, out var head, out var prop);
            fixture.AddPose(ReferencePoseLabels.TPose,
                (1, "rotation", fixture.Rotation(0f, 0.70710677f, 0f, 0.70710677f)),
                (3, "translation", fixture.Translation(0f, 0.3f, 0.05f)));
            spine.localPosition = new Vector3(5f, 5f, 5f);
            prop.localRotation = Quaternion.Euler(40f, 0f, 0f);

            var pose = fixture.Bake()[0];

            Assert.IsTrue(pose.IsValid, string.Join(" ", pose.Errors));
            Assert.AreEqual(ReferencePoseLabels.TPose, pose.PoseType, "the explicit imported label remains exact");
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, pose.ResolvedNodeIndices);
            CollectionAssert.AreEqual(new[] { -1, 0, 0, 1 }, pose.ParentIndices);
            Assert.AreSame(prop, pose.ResolvedNodes[2]);
            Assert.Less(Vector3.Distance(UnityPosition(0.1f, 0.5f, 0f), pose.ResolvedLocalPositions[1]), 1e-6f,
                "an untargeted property of a targeted node keeps its authored value, not the live one");
            Assert.Less(Vector3.Distance(new Vector3(1f, 2f, 1f), pose.ResolvedLocalScales[1]), 1e-6f);
            Assert.Less(Quaternion.Angle(UnityRotation(0f, 0.70710677f, 0f, 0.70710677f), pose.ResolvedLocalRotations[1]), 0.01f);
            Assert.Less(Quaternion.Angle(Quaternion.identity, pose.ResolvedLocalRotations[2]), 0.01f,
                "an untargeted node uses its authored transform, never its live one");
            Assert.Less(Vector3.Distance(UnityPosition(0f, 0.3f, 0.05f), pose.ResolvedLocalPositions[3]), 1e-6f);
            CollectionAssert.AreEqual(new[] { spine, head }, pose.Bones, "Bones keep the targeted nodes for export");
            Assert.Less(Vector3.Distance(UnityPosition(0.1f, 0.5f, 0f), pose.LocalPositions[0]), 1e-6f);
        }

        [Test]
        public void ApplyReferencePose_ResetsUntargetedAndPartlyTargetedNodesToAuthoredValues()
        {
            var fixture = BuildHierarchy(out var scene, out var spine, out var head, out var prop);
            fixture.AddPose(ReferencePoseLabels.TPose,
                (1, "rotation", fixture.Rotation(0f, 0.70710677f, 0f, 0.70710677f)),
                (3, "translation", fixture.Translation(0f, 0.3f, 0.05f)));
            var skeleton = BindPoses(scene, fixture.Bake());
            var propPosition = prop.localPosition;
            var spineScale = spine.localScale;

            prop.localPosition = new Vector3(-3f, 2f, 9f);
            prop.localRotation = Quaternion.Euler(10f, 20f, 30f);
            spine.localPosition = new Vector3(0f, -4f, 0f);
            spine.localScale = Vector3.one * 3f;
            head.localPosition = Vector3.zero;
            skeleton.ApplyReferencePose();

            Assert.Less(Vector3.Distance(propPosition, prop.localPosition), 1e-6f);
            Assert.Less(Quaternion.Angle(Quaternion.identity, prop.localRotation), 0.01f);
            Assert.Less(Vector3.Distance(UnityPosition(0.1f, 0.5f, 0f), spine.localPosition), 1e-6f);
            Assert.Less(Vector3.Distance(spineScale, spine.localScale), 1e-6f);
            Assert.Less(Quaternion.Angle(UnityRotation(0f, 0.70710677f, 0f, 0.70710677f), spine.localRotation), 0.01f);
            Assert.Less(Vector3.Distance(UnityPosition(0f, 0.3f, 0.05f), head.localPosition), 1e-6f);

            Assert.IsTrue(skeleton.TryGetGlobalReferencePose(0, 3, out var global));
            Assert.Less(Vector3.Distance(head.position, (Vector3)global.GetColumn(3)), 1e-5f,
                "the global reference pose composes the resolved locals along the glTF parents");
            Assert.IsFalse(skeleton.TryGetGlobalReferencePose(0, 99, out _));
            Assert.IsFalse(skeleton.TryGetGlobalReferencePose(7, 3, out _));
        }

        [Test]
        public void ReferencePose_TranslationsAreInTheImportedSceneUnits()
        {
            const float scaleFactor = 0.01f;
            var fixture = BuildHierarchy(out var scene, out var spine, out var head, out var prop);
            fixture.AddPose(ReferencePoseLabels.TPose,
                (1, "rotation", fixture.Rotation(0f, 0.70710677f, 0f, 0.70710677f)),
                (3, "translation", fixture.Translation(0f, 0.3f, 0.05f)));
            // The editor importer scales every node position after the import plugins have run.
            foreach (var node in scene.GetComponentsInChildren<Transform>()) node.localPosition *= scaleFactor;
            var propPosition = prop.localPosition;
            var skeleton = BindPoses(scene, fixture.Bake(scaleFactor));

            prop.localPosition = new Vector3(-3f, 2f, 9f);
            spine.localPosition = Vector3.zero;
            head.localPosition = Vector3.zero;
            skeleton.ApplyReferencePose();

            Assert.Less(Vector3.Distance(propPosition, prop.localPosition), 1e-6f,
                "an untargeted node returns to its scaled imported position");
            Assert.Less(Vector3.Distance(UnityPosition(0.1f, 0.5f, 0f) * scaleFactor, spine.localPosition), 1e-6f);
            Assert.Less(Vector3.Distance(UnityPosition(0f, 0.3f, 0.05f) * scaleFactor, head.localPosition), 1e-6f,
                "a targeted translation is scaled too");
            Assert.IsTrue(skeleton.TryGetGlobalReferencePose(0, 3, out var global));
            Assert.Less(Vector3.Distance(head.position, (Vector3)global.GetColumn(3)), 1e-6f);
        }

        [Test]
        public void ReferencePose_MatrixNodeUsesItsAuthoredMatrix()
        {
            var fixture = new PoseFixture();
            var scene = NewGo("scene");
            var matrixObject = NewGo("Matrix");
            matrixObject.transform.SetParent(scene.transform, false);
            var boneObject = NewGo("Bone");
            boneObject.transform.SetParent(matrixObject.transform, false);
            var position = new Vector3(2f, 0f, 1f);
            var rotation = Quaternion.Euler(0f, 90f, 0f);
            var scale = new Vector3(1f, 2f, 1f);
            fixture.AddNode(new Node
            {
                Name = "Matrix",
                Matrix = Matrix4x4.TRS(position, rotation, scale).ToGltfMatrix4x4Convert(),
                HasMatrix = true,
                Children = new List<NodeId> { new NodeId { Id = 1 } },
            }, matrixObject);
            fixture.AddNode(TrsNode("Bone", Vector3.zero), boneObject);
            fixture.AddPose(ReferencePoseLabels.APose, (1, "translation", fixture.Translation(0f, 1f, 0f)));
            var skeleton = BindPoses(scene, fixture.Bake());

            matrixObject.transform.localPosition = Vector3.zero;
            matrixObject.transform.localRotation = Quaternion.identity;
            skeleton.ApplyReferencePose();

            Assert.Less(Vector3.Distance(position, matrixObject.transform.localPosition), 1e-5f);
            Assert.Less(Quaternion.Angle(rotation, matrixObject.transform.localRotation), 0.01f);
            Assert.Less(Vector3.Distance(scale, matrixObject.transform.localScale), 1e-5f);
        }

        [Test]
        public void ReferencePose_TargetingAMatrixNodeIsInvalid()
        {
            var fixture = new PoseFixture();
            fixture.AddNode(new Node { Matrix = GLTF.Math.Matrix4x4.Identity, HasMatrix = true }, NewGo("Matrix"));
            fixture.AddPose(ReferencePoseLabels.TPose, (0, "translation", fixture.Translation(0f, 1f, 0f)));

            var pose = fixture.Bake()[0];

            Assert.IsFalse(pose.IsValid);
            StringAssert.Contains("defined by a matrix", string.Join(" ", pose.Errors));
        }

        [Test]
        public void ReferencePose_SparseOnlyOutputDecodes()
        {
            var fixture = new PoseFixture();
            var bone = NewGo("Bone");
            fixture.AddNode(TrsNode("Bone", Vector3.zero), bone);
            fixture.AddPose(ReferencePoseLabels.TPose,
                (0, "translation", fixture.AddSparseOnly(GLTFAccessorAttributeType.VEC3, 0.5f, 1.5f, -2f)));

            var pose = fixture.Bake()[0];

            Assert.IsTrue(pose.IsValid, string.Join(" ", pose.Errors));
            Assert.Less(Vector3.Distance(UnityPosition(0.5f, 1.5f, -2f), pose.ResolvedLocalPositions[0]), 1e-6f);
        }

        [Test]
        public void ReferencePose_InvalidAnimationsAreReportedNotDropped()
        {
            var fixture = new PoseFixture();
            var bone = NewGo("Bone");
            fixture.AddNode(TrsNode("Bone", Vector3.zero), bone);
            fixture.AddPose("non_unit", (0, "rotation", fixture.Rotation(0f, 0f, 0f, 2f)));
            fixture.AddPose("no_channels");
            fixture.AddPose("wrong_type", (0, "translation", fixture.Rotation(0f, 0f, 0f, 1f)));
            fixture.AddPose("wrong_count",
                (0, "scale", fixture.AddFloats(GLTFAccessorAttributeType.VEC3, 2, 1f, 1f, 1f, 2f, 2f, 2f)));
            fixture.AddAnimation("cubic", InterpolationType.CUBICSPLINE, (0, "translation", fixture.Translation(0f, 0f, 0f)));
            fixture.AddPose("weights", (0, "weights", fixture.AddFloats(GLTFAccessorAttributeType.SCALAR, 1, 0f)));

            var poses = fixture.Bake();

            Assert.AreEqual(6, poses.Length, "every tagged animation is listed");
            string[] expected =
            {
                "unit quaternion", "no channels", "FLOAT VEC3", "FLOAT VEC3", "LINEAR or STEP", "ordinary node",
            };
            for (int i = 0; i < poses.Length; i++)
            {
                Assert.AreEqual(i, poses[i].AnimationIndex);
                Assert.IsFalse(poses[i].IsValid, poses[i].PoseType);
                StringAssert.Contains(expected[i], string.Join(" ", poses[i].Errors), poses[i].PoseType);
                Assert.IsFalse(poses[i].HasResolvedValues);
            }

            var skeleton = BindPoses(NewGo("char"), poses);
            Assert.AreEqual(6, skeleton.ReferencePoses.Count);
            Assert.IsFalse(skeleton.ApplyReferencePose(0), "an invalid pose is never applied");
        }

        [Test]
        public void ReferencePose_MissingExtensionsUsedEntryIsReported()
        {
            var fixture = new PoseFixture();
            fixture.Root.ExtensionsUsed.Remove(KhrCharacterExtensionNames.ReferencePose);
            fixture.AddNode(TrsNode("Bone", Vector3.zero), NewGo("Bone"));
            fixture.AddPose(ReferencePoseLabels.TPose, (0, "translation", fixture.Translation(0f, 1f, 0f)));

            var pose = fixture.Bake()[0];

            Assert.IsFalse(pose.IsValid);
            StringAssert.Contains("extensionsUsed does not list KHR_character_reference_pose", string.Join(" ", pose.Errors));
        }

        [Test]
        public void ReferencePose_TwoTaggedAnimationsAreBothExposedWithTheirLabels()
        {
            var fixture = new PoseFixture();
            var scene = NewGo("scene");
            var bone = NewGo("Bone");
            bone.transform.SetParent(scene.transform, false);
            fixture.AddNode(TrsNode("Bone", Vector3.zero), bone);
            fixture.AddPose(ReferencePoseLabels.TPose, (0, "translation", fixture.Translation(0f, 1f, 0f)));
            fixture.AddPose("ACME_relaxed_pose", (0, "translation", fixture.Translation(0f, 2f, 0f)));

            var skeleton = BindPoses(scene, fixture.Bake());

            Assert.AreEqual(2, skeleton.ReferencePoses.Count);
            Assert.AreEqual(ReferencePoseLabels.TPose, skeleton.ReferencePoses[0].PoseType);
            Assert.AreEqual("ACME_relaxed_pose", skeleton.ReferencePoses[1].PoseType);
            Assert.IsTrue(skeleton.ApplyReferencePose(1));
            Assert.Less(Vector3.Distance(new Vector3(0f, 2f, 0f), bone.transform.localPosition), 1e-6f);
            skeleton.ApplyReferencePose();
            Assert.Less(Vector3.Distance(new Vector3(0f, 1f, 0f), bone.transform.localPosition), 1e-6f,
                "the parameterless overload applies the first valid pose");
        }

        [Test]
        public void ApplyReferencePose_KeepsTheForwardAxisTurnOnLightAndCameraNodes()
        {
            var fixture = new PoseFixture();
            var scene = NewGo("scene");
            var lightObject = NewGo("Lamp");
            lightObject.transform.SetParent(scene.transform, false);
            lightObject.AddComponent<Light>();
            var lightNode = TrsNode("Lamp", new Vector3(0f, 2f, 0f));
            lightNode.Rotation = new GLTF.Math.Quaternion(0.25881904f, 0f, 0f, 0.9659258f);
            lightNode.AddExtension(KHR_lights_punctualExtensionFactory.EXTENSION_NAME,
                new KHR_LightsPunctualNodeExtension(0, fixture.Root));
            fixture.AddNode(lightNode, lightObject);
            var bone = NewGo("Bone");
            bone.transform.SetParent(scene.transform, false);
            fixture.AddNode(TrsNode("Bone", Vector3.zero), bone);
            // The importer turns a light node's GameObject 180° about Y.
            var turn = new Quaternion(0f, -1f, 0f, 0f);
            lightObject.transform.localRotation *= turn;
            var imported = lightObject.transform.localRotation;
            fixture.AddPose(ReferencePoseLabels.TPose, (1, "translation", fixture.Translation(0f, 1f, 0f)));
            var poses = fixture.Bake();
            var skeleton = BindPoses(scene, poses);

            Assert.IsTrue(poses[0].ResolvedNodeHasForwardAxisConversion[0]);
            Assert.Less(Quaternion.Angle(UnityRotation(0.25881904f, 0f, 0f, 0.9659258f), poses[0].ResolvedLocalRotations[0]), 0.01f,
                "the resolved rotation is the authored one, without the Unity turn");
            lightObject.transform.localRotation = Quaternion.identity;
            skeleton.ApplyReferencePose();

            Assert.Less(Quaternion.Angle(imported, lightObject.transform.localRotation), 0.01f);
        }

        [Test]
        public void ReferencePoseSchema_OmittedPoseTypeIsMissing()
        {
            var pose = new KHR_character_reference_pose_Factory().Deserialize(
                new GLTFRoot(),
                new JProperty(KHR_character_reference_pose.EXTENSION_NAME, new JObject()))
                as KHR_character_reference_pose;

            Assert.AreEqual("t_pose", ReferencePoseLabels.TPose);
            Assert.IsNull(pose.PoseType, "an imported missing required label is not invented");
            Assert.AreEqual(ReferencePoseLabels.TPose, new KHR_character_reference_pose().PoseType);
        }

        [TestCase("T_POSE")]
        [TestCase("TPose")]
        [TestCase("ACME_relaxed_pose")]
        public void ReferencePoseSchema_NonStandardLabelsAreKeptVerbatim(string label)
        {
            var pose = new KHR_character_reference_pose_Factory().Deserialize(
                new GLTFRoot(),
                new JProperty(KHR_character_reference_pose.EXTENSION_NAME, new JObject { { "poseType", label } }))
                as KHR_character_reference_pose;

            Assert.AreEqual(label, pose.PoseType, "labels are exact and case-sensitive; custom labels are not mapped");
            Assert.AreEqual(label, ((JObject)new KHR_character_reference_pose { PoseType = label }.Serialize().Value)
                ["poseType"].Value<string>());
        }

        [Test]
        public void ReferencePoseSchema_NeverSerializesANullLabel()
        {
            var value = (JObject)new KHR_character_reference_pose { PoseType = null }.Serialize().Value;

            Assert.IsNull(value["poseType"]);
        }
    }
}
