using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class ShaderGraphSkillsTests
    {
        private static JObject ToJObject(object result)
        {
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }

        [Test]
        public void ShaderGraphSkills_AreRegistered()
        {
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_list_templates"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_create_graph"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_create_subgraph"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_get_info"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_get_structure"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_list_supported_nodes"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_add_node"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_remove_node"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_move_node"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_connect_nodes"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_disconnect_nodes"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_set_node_defaults"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_set_node_settings"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_add_property"));
            Assert.IsTrue(SkillRouter.HasSkill("shadergraph_add_keyword"));
        }

        [Test]
        public void ShaderGraphListTemplates_ReturnsTemplatesOrFallback()
        {
            var result = ToJObject(ShaderGraphSkills.ShaderGraphListTemplates());

            if (result["success"]?.Value<bool>() == true)
            {
                Assert.That(result["count"]?.Value<int>() ?? 0, Is.GreaterThanOrEqualTo(0));
                Assert.IsNotNull(result["templates"]);
            }
            else
            {
                StringAssert.Contains("Shader Graph package", result["error"]?.ToString());
            }
        }

        [Test]
        public void ShaderGraphConstrainedEditing_WorksWhenPackageInstalled()
        {
            var templateProbe = ToJObject(ShaderGraphSkills.ShaderGraphListTemplates());
            if (templateProbe["success"]?.Value<bool>() != true)
            {
                Assert.Pass("Current test host does not have Shader Graph installed.");
                return;
            }

            const string assetPath = "Assets/Temp/ShaderGraphSkillTests/SkillTestSubGraph.shadersubgraph";
            try
            {
                AssetDatabase.DeleteAsset(assetPath);

                var created = ToJObject(ShaderGraphSkills.ShaderGraphCreateSubGraph(assetPath));
                Assert.IsTrue(created["success"]?.Value<bool>() ?? false);

                var addedProperty = ToJObject(ShaderGraphSkills.ShaderGraphAddProperty(
                    assetPath,
                    "float",
                    "Amplitude",
                    "_Amplitude",
                    1.5f));
                Assert.IsTrue(addedProperty["success"]?.Value<bool>() ?? false);

                var addedKeyword = ToJObject(ShaderGraphSkills.ShaderGraphAddKeyword(
                    assetPath,
                    "Boolean",
                    "Use Detail",
                    "_USE_DETAIL_ON"));
                Assert.IsTrue(addedKeyword["success"]?.Value<bool>() ?? false);

                var properties = ToJObject(ShaderGraphSkills.ShaderGraphListProperties(assetPath));
                Assert.IsTrue(properties["success"]?.Value<bool>() ?? false);
                Assert.That(properties["count"]?.Value<int>() ?? 0, Is.GreaterThanOrEqualTo(1));

                var keywords = ToJObject(ShaderGraphSkills.ShaderGraphListKeywords(assetPath));
                Assert.IsTrue(keywords["success"]?.Value<bool>() ?? false);
                Assert.That(keywords["count"]?.Value<int>() ?? 0, Is.GreaterThanOrEqualTo(1));

                var info = ToJObject(ShaderGraphSkills.ShaderGraphGetInfo(assetPath));
                Assert.IsTrue(info["success"]?.Value<bool>() ?? false);
                Assert.That(info["kind"]?.ToString(), Is.EqualTo("SubGraph"));
            }
            finally
            {
                var directory = "Assets/Temp/ShaderGraphSkillTests";
                if (AssetDatabase.IsValidFolder(directory))
                    AssetDatabase.DeleteAsset(directory);
            }
        }

        [Test]
        public void ShaderGraphCreateGraph_WorksWithTemplateOrBlankFallback()
        {
            var templateProbe = ToJObject(ShaderGraphSkills.ShaderGraphListTemplates());
            if (templateProbe["success"]?.Value<bool>() != true)
            {
                Assert.Pass("Current test host does not have Shader Graph installed.");
                return;
            }

            const string assetPath = "Assets/Temp/ShaderGraphSkillTests/SkillTestGraph.shadergraph";
            try
            {
                AssetDatabase.DeleteAsset(assetPath);

                var created = ToJObject(ShaderGraphSkills.ShaderGraphCreateGraph(assetPath));
                Assert.IsTrue(created["success"]?.Value<bool>() ?? false);

                var info = ToJObject(ShaderGraphSkills.ShaderGraphGetInfo(assetPath));
                Assert.IsTrue(info["success"]?.Value<bool>() ?? false);
                Assert.That(info["kind"]?.ToString(), Is.EqualTo("Graph"));
            }
            finally
            {
                var directory = "Assets/Temp/ShaderGraphSkillTests";
                if (AssetDatabase.IsValidFolder(directory))
                    AssetDatabase.DeleteAsset(directory);
            }
        }
    }
}

// Producer:Betsy
