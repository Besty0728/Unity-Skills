using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// SkillMisnomers table integrity, and that SKILL_NOT_FOUND puts the known right skill first for a known wrong name.
    /// </summary>
    [TestFixture]
    public class SkillMisnomersTests
    {
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
        }

        [TearDown]
        public void TearDown() => SkillsSurfaceProfile.Current = _savedProfile;

        [Test]
        public void Keys_AreNotRegisteredSkills()
        {
            var registered = RegisteredSkillNames();
            var clashes = SkillMisnomers.All.Select(entry => entry.Key).Where(registered.Contains).ToArray();

            Assert.That(clashes, Is.Empty, "SkillMisnomers 的键不能是已注册 skill: " + string.Join(", ", clashes));
        }

        [Test]
        public void Values_AreRegisteredSkills()
        {
            var registered = RegisteredSkillNames();
            var unknown = SkillMisnomers.All.Select(entry => entry.Value).Where(value => !registered.Contains(value)).ToArray();

            Assert.That(unknown, Is.Empty, "SkillMisnomers 的值必须是已注册 skill: " + string.Join(", ", unknown));
        }

        [Test]
        public void KnownWrongName_GetsTheRightSkillFirst()
        {
            var body = JObject.Parse(SkillRouter.ResolveSkillNotFound("gameobject_move"));

            Assert.That((string)body["suggestedFixes"][0]["skill"], Is.EqualTo("gameobject_set_transform"));
        }

        [Test]
        public void UnknownName_StillGetsNearestNames()
        {
            var body = JObject.Parse(SkillRouter.ResolveSkillNotFound("gameobject_creat"));

            Assert.That((string)body["suggestedFixes"][0]["reason"], Is.EqualTo("Closest registered skill name"));
        }

        private static HashSet<string> RegisteredSkillNames()
        {
            var skills = (JArray)JObject.Parse(SkillRouter.GetFilteredManifest("?full=1"))["skills"];
            return new HashSet<string>(skills.Select(skill => (string)skill["name"]), StringComparer.OrdinalIgnoreCase);
        }
    }
}

// Producer:Betsy
