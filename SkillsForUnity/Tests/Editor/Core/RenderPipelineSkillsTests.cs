using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class RenderPipelineSkillsTests
    {
        private static JObject ToJObject(object result)
        {
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }

        [Test]
        public void GraphicsGetOverview_ReturnsSuccess()
        {
            var result = ToJObject(GraphicsSkills.GraphicsGetOverview());

            Assert.IsTrue(result["success"]?.Value<bool>() ?? false);
            Assert.IsNotNull(result["currentQuality"]);
            Assert.IsNotNull(result["shaderStripping"]);
        }

        [Test]
        public void NewRenderingSkills_AreRegistered()
        {
            Assert.IsTrue(SkillRouter.HasSkill("graphics_get_overview"));
            Assert.IsTrue(SkillRouter.HasSkill("volume_profile_create"));
            Assert.IsTrue(SkillRouter.HasSkill("postprocess_list_effects"));
            Assert.IsTrue(SkillRouter.HasSkill("urp_get_info"));
            Assert.IsTrue(SkillRouter.HasSkill("decal_create"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_get_info"));
        }

        [Test]
        public void LegacyProjectQualitySkills_AreRemoved()
        {
            Assert.IsFalse(SkillRouter.HasSkill("project_get_quality_settings"));
            Assert.IsFalse(SkillRouter.HasSkill("project_set_quality_level"));
        }
    }
}

// Producer:Betsy
