using System.Collections.Generic;
using GLTF.Schema;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityGLTF.Plugins;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Phase-Z baker / import-gating edge cases that the original suite left open: the CUBICSPLINE -> LINEAR
    /// interpolation downgrade the baker performs while sampling value blocks (X5), and the F2 import gate that
    /// bakes nothing unless the root carries KHR_character (N1). Both exercise real internals directly (the same
    /// white-box style the rest of the import suite uses) without a full glTF file import.
    /// </summary>
    public class KhrCharacterBakerEdgeTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private SkinnedMeshRenderer MakeSmr(int blendShapeCount)
        {
            var mesh = new Mesh { name = "test" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            var delta = new[] { Vector3.one, Vector3.one, Vector3.one };
            for (int i = 0; i < blendShapeCount; i++)
                mesh.AddBlendShapeFrame("shape" + i, 1f, delta, null, null);
            _created.Add(mesh);

            var go = new GameObject("smr");
            _created.Add(go);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            return smr;
        }

        // ── X5: CUBICSPLINE keyframes are sampled (value block) and the interpolation downgrades to LINEAR ──

        [Test]
        public void Morph_CubicSpline_DowngradesToLinear()
        {
            // X5: the baker has no CUBICSPLINE evaluation; it reads the per-key VALUE block ([inTangent, value,
            // outTangent]) and records the interpolation as LINEAR (KhrCharacterBaker.MapInterp). A re-importer /
            // viewer therefore sees a LINEAR track. (README documents this as a known round-trip caveat.)
            var smr = MakeSmr(1);
            var times = new[] { 0f, 1f };
            // CUBICSPLINE layout: [inTangent, value, outTangent] per key. Values 0.2 and 0.6.
            var values = new[] { 0f, 0.2f, 0f, 0f, 0.6f, 0f };

            var drivers = new List<MorphDriver>();
            KhrCharacterBaker.BuildMorphPointerDriver(smr, 0, times, values, InterpolationType.CUBICSPLINE, drivers);

            Assert.AreEqual(1, drivers.Count);
            Assert.AreEqual(Interp.Linear, drivers[0].Sampler.Interp,
                "CUBICSPLINE is sampled (value block only) and downgraded to LINEAR");
            // The value block survives as frame-0-relative deltas (tangents discarded).
            Assert.That(drivers[0].DeltaValues, Is.EqualTo(new[] { 0f, 0.4f }).Within(1e-5f));
        }

        [Test]
        public void JointRotation_CubicSpline_DowngradesToLinear()
        {
            // X5 (joint path): the same CUBICSPLINE -> LINEAR downgrade applies to joint rotation channels.
            var t = new GameObject("joint").transform;
            _created.Add(t.gameObject);
            var times = new[] { 0f, 1f };
            var q0 = Quaternion.identity;
            var q1 = Quaternion.Euler(0f, 90f, 0f);
            // CUBICSPLINE quaternion layout: [inTangent, value, outTangent] per key; only the value is used.
            var values = new[] { q0, q0, q0, q0, q1, q0 };

            var drivers = new List<JointDriver>();
            KhrCharacterBaker.BuildJointRotationDriver(t, times, values, InterpolationType.CUBICSPLINE, Quaternion.identity, drivers);

            Assert.AreEqual(1, drivers.Count);
            Assert.AreEqual(Interp.Linear, drivers[0].Sampler.Interp, "CUBICSPLINE downgrades to LINEAR");
            Assert.Less(Quaternion.Angle(drivers[0].DeltaQuat[1], q1), 1e-3f,
                "the value block is read as the frame-0-relative rotation delta (tangents discarded)");
        }

        // ── N1: the import gate bakes nothing unless the root carries KHR_character ──────────────────

        [Test]
        public void Import_WithoutRootCharacter_AttachesNothing()
        {
            // N1 (F2): a glTF whose root does NOT declare KHR_character must not be treated as a character -- the
            // importer attaches no hub/controller/skeleton. OnAfterImportScene gates on the KHR_character presence
            // detected in OnAfterImportRoot, so we can drive the public lifecycle directly with a bare root.
            var ctx = new KhrCharacterImportContext((GLTFImportContext)null);
            ctx.OnAfterImportRoot(new GLTFRoot()); // no extensions -> not a character

            var go = new GameObject("plain");
            _created.Add(go);
            ctx.OnAfterImportScene(null, 0, go);

            Assert.IsNull(go.GetComponent<KhrCharacter>(), "no KHR_character -> no hub attached");
            Assert.IsNull(go.GetComponent<ExpressionController>(), "no KHR_character -> no expression controller");
            Assert.IsNull(go.GetComponent<SkeletonMap>(), "no KHR_character -> no skeleton map");
        }

        [Test]
        public void Import_WithRootCharacter_OpensGateAndAttachesHub()
        {
            // N1 (positive): when the root DOES declare KHR_character, the gate opens and the hub is attached.
            // (With a null import context, baking is skipped -- no ExpressionController -- but the gate decision
            // itself is what N1 pins: detection keys on the KHR_character root extension.)
            var root = new GLTFRoot
            {
                Nodes = new List<Node> { new Node { Name = "character" } },
                Extensions = new Dictionary<string, IExtension>
                {
                    { KhrCharacterExtensionNames.Character, new KHR_character { RootNode = 0 } },
                },
            };
            var ctx = new KhrCharacterImportContext((GLTFImportContext)null);
            ctx.OnAfterImportRoot(root);

            var go = new GameObject("character");
            _created.Add(go);
            ctx.OnAfterImportScene(null, 0, go);

            Assert.IsNotNull(go.GetComponent<KhrCharacter>(),
                "the KHR_character root extension opens the gate -> the hub is attached");
        }

        [Test]
        public void InvalidPassiveExpressionDataCannotCreateOptionalController()
        {
            var root = new GLTFRoot
            {
                Accessors = new List<Accessor>
                {
                    new Accessor
                    {
                        ComponentType = GLTFComponentType.Float,
                        Count = 1,
                        Type = GLTFAccessorAttributeType.SCALAR,
                        Min = new List<double> { 0d },
                        Max = new List<double> { 0d },
                    },
                    new Accessor
                    {
                        ComponentType = GLTFComponentType.Float,
                        Count = 1,
                        Type = GLTFAccessorAttributeType.VEC3,
                    },
                },
                Animations = new List<GLTFAnimation>(),
                Nodes = new List<Node> { new Node { Name = "character" } },
            };
            var animation = new GLTFAnimation();
            animation.Samplers.Add(new AnimationSampler
            {
                Input = new AccessorId { Id = 0, Root = root },
                Output = new AccessorId { Id = 1, Root = root },
                Interpolation = InterpolationType.LINEAR,
            });
            animation.Channels.Add(new AnimationChannel
            {
                Sampler = new AnimationSamplerId { Id = 0, Root = root, GLTFAnimation = animation },
                Target = new AnimationChannelTarget
                {
                    Node = new NodeId { Id = 0, Root = root },
                    Path = "translation",
                },
            });
            root.Animations.Add(animation);
            root.Extensions = new Dictionary<string, IExtension>
            {
                { KhrCharacterExtensionNames.Character, new KHR_character { RootNode = 0 } },
                {
                    KhrCharacterExtensionNames.Expression,
                    new KHR_character_expression
                    {
                        Expressions = new List<KHR_character_expression.ExpressionItem>
                        {
                            new KHR_character_expression.ExpressionItem
                            {
                                Expression = "invalid",
                                Animation = 0,
                            },
                        },
                    }
                },
            };

            var settings = ScriptableObject.CreateInstance<GLTFSettings>();
            _created.Add(settings);
#if UNITY_EDITOR
            var importContext = new GLTFImportContext(null, settings);
#else
            var importContext = new GLTFImportContext(settings);
#endif
            var options = new ImportOptions { ImportContext = importContext };
            using (var importer = new GLTFSceneImporter(root, null, options))
            {
                var context = new KhrCharacterImportContext(importContext, createExpressionController: true);
                context.OnAfterImportRoot(root);

                var sceneObject = new GameObject("invalid-character");
                _created.Add(sceneObject);
                context.OnAfterImportNode(root.Nodes[0], 0, sceneObject);
                LogAssert.Expect(
                    LogType.Error,
                    "[KHR_character] Expression response data is invalid: " +
                    "Sampler 0 input must contain at least two keys.");
                context.OnAfterImportScene(null, 0, sceneObject);

                var hub = sceneObject.GetComponent<KhrCharacter>();
                Assert.IsNotNull(hub);
                Assert.IsNull(sceneObject.GetComponent<ExpressionResponseSet>());
                Assert.IsNull(sceneObject.GetComponent<ExpressionController>());
                Assert.IsNull(hub.ExpressionResponses);
                Assert.IsNull(hub.Expressions);
            }
        }
    }
}
