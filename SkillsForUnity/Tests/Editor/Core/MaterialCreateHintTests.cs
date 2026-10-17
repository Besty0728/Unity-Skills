using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// material_create without a savePath warned the caller to "use asset_save", a skill that does not exist (P6), and an
    /// agent that took the hint at its word got SKILL_NOT_FOUND. Every skill the warning names must be a registered one.
    /// </summary>
    [TestFixture]
    public class MaterialCreateHintTests
    {
        private static readonly Regex SkillName = new Regex(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b");

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private Object _created;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
        }

        [TearDown]
        public void TearDown()
        {
            if (_created != null)
                Object.DestroyImmediate(_created);
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        [Test]
        public void InMemoryWarning_NamesOnlySkillsThatExist()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(MaterialSkills.MaterialCreate("HintProbe")));
            _created = UnityObjectIdUtility.EntityIdToObject((string)json["entityId"]);

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var warning = (string)json["warning"];
            Assert.That(warning, Is.Not.Null.And.Not.Empty);
            Assert.That(warning, Does.Not.Contain("asset_save"));

            var named = SkillName.Matches(warning).Cast<Match>().Select(m => m.Value).ToArray();
            Assert.That(named, Is.Not.Empty, "The warning should point at the skill that persists the material.");
            foreach (var skill in named)
                Assert.That(SkillRouter.TryGetSkill(skill, out _), Is.True, $"The warning names '{skill}', which is not a registered skill: {warning}");
        }

        [Test]
        public void TheSkillTheWarningNames_AcceptsASavePath()
        {
            Assert.That(SkillRouter.TryGetSkill("material_create", out var info), Is.True);

            Assert.That(info.ParameterNames, Does.Contain("savePath"));
        }
    }
}

// Producer:Betsy
