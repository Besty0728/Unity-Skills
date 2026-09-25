using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// DOTween Pro's DOTweenAnimation component skills, driven through SkillRouter against a real
    /// DOTweenAnimation component. Field names (duration/delay/loops/easeType/animationType/endValueV3)
    /// are verified against DOTweenPro/DOTweenAnimation.cs, not guessed.
    /// </summary>
    [TestFixture]
    public class DOTweenSkillsLiveTests : OptionalPackageTestBase
    {
        // Checks IsDOTweenProInstalled only, regardless of whether the base DOTween type is present, so this
        // reports MISSING_PACKAGE whether DOTween is entirely absent or only the Free tier is installed.
        protected override string ProbeSkill => "dotween_pro_list_animations";

        private static void CreateTarget(string name) => new GameObject(name);

        [Test]
        public void AddAnimation_WithDurationDelayLoopsEase_ReadsBackFromComponent()
        {
            CreateTarget("R6DOTweenTarget");

            var result = Ok(Run("dotween_pro_add_animation", new JObject
            {
                ["target"] = "R6DOTweenTarget",
                ["animationType"] = "Move",
                ["endValueV3"] = "1,2,3",
                ["duration"] = 2.5,
                ["delay"] = 0.3,
                ["loops"] = 3,
                ["ease"] = "OutBack",
                ["autoPlay"] = false,
            }));
            Assert.That(result["success"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["animationIndex"]?.Value<int>(), Is.EqualTo(0), result.ToString());

            var comp = ComponentNamed(Find("R6DOTweenTarget"), "DOTweenAnimation");
            Assert.That(comp, Is.Not.Null);
            Assert.That((float)Member(comp, "duration"), Is.EqualTo(2.5f).Within(1e-5f));
            Assert.That((float)Member(comp, "delay"), Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(Member(comp, "loops"), Is.EqualTo(3));
            // The ease field is named "easeType" on this DOTween Pro version, not "ease".
            Assert.That(Member(comp, "easeType").ToString(), Is.EqualTo("OutBack"));
            Assert.That(Member(comp, "animationType").ToString(), Is.EqualTo("Move"));
            Assert.That((Vector3)Member(comp, "endValueV3"), Is.EqualTo(new Vector3(1, 2, 3)));
        }

        [Test]
        public void SetDuration_ReadsBackFromComponentAndResponse()
        {
            CreateTarget("R6DOTweenTarget");
            Ok(Run("dotween_pro_add_animation", new JObject
            {
                ["target"] = "R6DOTweenTarget", ["animationType"] = "Move", ["endValueV3"] = "0,0,0", ["duration"] = 1f,
            }));

            var result = Ok(Run("dotween_pro_set_duration", new JObject { ["target"] = "R6DOTweenTarget", ["duration"] = 4.2 }));
            Assert.That(result["applied"]?.ToObject<string[]>(), Does.Contain("duration"), result.ToString());
            Assert.That(result["duration"]?.Value<float>(), Is.EqualTo(4.2f).Within(1e-4f), result.ToString());

            var comp = ComponentNamed(Find("R6DOTweenTarget"), "DOTweenAnimation");
            Assert.That((float)Member(comp, "duration"), Is.EqualTo(4.2f).Within(1e-4f));
        }

        [Test]
        public void ListAnimations_ReportsAddedAnimation()
        {
            CreateTarget("R6DOTweenTarget");
            Ok(Run("dotween_pro_add_animation", new JObject
            {
                ["target"] = "R6DOTweenTarget", ["animationType"] = "Fade", ["endValueFloat"] = 0.5, ["duration"] = 1f,
            }));

            var list = Ok(Run("dotween_pro_list_animations", new JObject { ["target"] = "R6DOTweenTarget" }));
            Assert.That(list["count"]?.Value<int>(), Is.EqualTo(1), list.ToString());
            var animations = (JArray)list["animations"];
            Assert.That(animations.Count, Is.EqualTo(1));
            var entry = (JObject)animations[0];
            Assert.That(entry["animationType"]?.ToString(), Is.EqualTo("Fade"));
            Assert.That(entry["gameObject"]?.ToString(), Is.EqualTo("R6DOTweenTarget"));
            Assert.That(entry["animationIndex"]?.Value<int>(), Is.EqualTo(0));
        }

        [Test]
        public void CopyAnimation_DuplicatesFieldsWithNoSkippedFields()
        {
            CreateTarget("R6DOTweenSource");
            CreateTarget("R6DOTweenDest");
            Ok(Run("dotween_pro_add_animation", new JObject
            {
                ["target"] = "R6DOTweenSource", ["animationType"] = "Move", ["endValueV3"] = "5,6,7",
                ["duration"] = 3.3, ["ease"] = "InOutQuad",
            }));

            var result = Ok(Run("dotween_pro_copy_animation", new JObject
            {
                ["sourceTarget"] = "R6DOTweenSource", ["destTarget"] = "R6DOTweenDest",
            }));
            Assert.That(result["success"]?.Value<bool>(), Is.True, result.ToString());
            var skipped = result["skippedFields"] as JArray;
            Assert.That(skipped, Is.Not.Null, result.ToString());
            Assert.That(skipped.Count, Is.EqualTo(0), result.ToString());

            var destComp = ComponentNamed(Find("R6DOTweenDest"), "DOTweenAnimation");
            Assert.That((float)Member(destComp, "duration"), Is.EqualTo(3.3f).Within(1e-4f));
            Assert.That(Member(destComp, "easeType").ToString(), Is.EqualTo("InOutQuad"));
            Assert.That((Vector3)Member(destComp, "endValueV3"), Is.EqualTo(new Vector3(5, 6, 7)));
        }

        [Test]
        public void TryResolveGeneratorEase_CaseInsensitiveName_ReturnsCanonicalOutQuad()
        {
            bool ok = DOTweenSkills.TryResolveGeneratorEase("outquad", out var canonical, out var error);
            Assert.That(ok, Is.True);
            Assert.That(canonical, Is.EqualTo("OutQuad"));
            Assert.That(error, Is.Null);
        }

        [Test]
        public void TryResolveGeneratorEase_Blank_DefaultsToOutQuad()
        {
            bool ok = DOTweenSkills.TryResolveGeneratorEase("", out var canonical, out var error);
            Assert.That(ok, Is.True);
            Assert.That(canonical, Is.EqualTo("OutQuad"));
            Assert.That(error, Is.Null);
        }

        [TestCase("Foobar")]
        [TestCase("Quad.OutFlex")]
        public void TryResolveGeneratorEase_UnknownName_ReturnsFalseWithError(string ease)
        {
            bool ok = DOTweenSkills.TryResolveGeneratorEase(ease, out var canonical, out var error);
            Assert.That(ok, Is.False);
            Assert.That(error, Is.Not.Null);
        }
    }
}

// Producer:Betsy
