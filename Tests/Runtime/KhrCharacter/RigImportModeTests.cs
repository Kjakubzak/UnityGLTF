using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace UnityGLTF.KhrCharacter.Tests
{
    /// <summary>
    /// Unit tests for the vendor-neutral Rig import gating
    /// (<see cref="KhrCharacterImportContext.ShouldBuildHumanoid"/>): a humanoid Avatar is flagged for build only
    /// in Humanoid rig mode and only when an actual bone mapping resolved. Generic never builds.
    /// </summary>
    public class RigImportModeTests
    {
        private static SkeletonMappingResult WithBones() => new SkeletonMappingResult
        {
            Bones = new Dictionary<string, Transform> { { "hips", null }, { "head", null } },
        };

        private static SkeletonMappingResult NoBones() => new SkeletonMappingResult
        {
            Bones = new Dictionary<string, Transform>(),
        };

        [Test]
        public void Humanoid_WithBones_Builds()
        {
            Assert.IsTrue(KhrCharacterImportContext.ShouldBuildHumanoid(RigImportMode.Humanoid, WithBones()));
        }

        [Test]
        public void Generic_WithBones_DoesNotBuild()
        {
            Assert.IsFalse(KhrCharacterImportContext.ShouldBuildHumanoid(RigImportMode.Generic, WithBones()));
        }

        [Test]
        public void Humanoid_NoBones_DoesNotBuild()
        {
            Assert.IsFalse(KhrCharacterImportContext.ShouldBuildHumanoid(RigImportMode.Humanoid, NoBones()));
            Assert.IsFalse(KhrCharacterImportContext.ShouldBuildHumanoid(RigImportMode.Humanoid, null));
        }
    }
}
