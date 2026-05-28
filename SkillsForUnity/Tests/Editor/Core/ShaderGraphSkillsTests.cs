using System.IO;
using System.Linq;
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

        /// <summary>
        /// R3 (issue #1 Define→Develop gate, REQUIRED-POSITIVE smoke test for PR#3):
        /// Pin the minimum acceptance shape for ShaderGraph after the Unity 6.5 +
        /// URP 17.5 compat fixes. If this fails, PR#3 MUST stop and re-gate — the
        /// `AssetDatabase.Refresh()` alignment alone has not closed the underlying
        /// reflection-adapter break, and the bigger tests using `Assert.Pass`-on-
        /// missing-SG would silently hide it.
        ///
        /// Specifically NOT eligible for the "Shader Graph not installed" early-out:
        /// this test runs in URP 17.5 hosts (which always have Shader Graph) and
        /// fails hard if the adapter's create/add path is genuinely broken.
        /// </summary>
        [Test]
        public void ShaderGraphAdapter_BlankGraphPlusSingleNode_RoundTrips_R3PositiveLane()
        {
            const string assetPath = "Assets/Temp/ShaderGraphSkillTests/R3SmokeTest.shadergraph";
            try
            {
                if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                    AssetDatabase.CreateFolder("Assets", "Temp");
                if (!AssetDatabase.IsValidFolder("Assets/Temp/ShaderGraphSkillTests"))
                    AssetDatabase.CreateFolder("Assets/Temp", "ShaderGraphSkillTests");
                AssetDatabase.DeleteAsset(assetPath);

                // (a) Blank-graph create must succeed. CreateGraph already passes on
                // URP 17.5 before any of the PR#3 fixes — it's the baseline.
                var created = ToJObject(ShaderGraphSkills.ShaderGraphCreateGraph(assetPath));
                Assert.IsTrue(created["success"]?.Value<bool>() ?? false,
                    $"R3.a blank graph creation must succeed on URP 17.5. Error: {created["error"]}");

                // (b) Single node add must succeed AND survive a structure read-back.
                // This is the round-trip — if AddNode succeeds but the saved graph
                // can't be re-loaded as a node-bearing structure, the import race
                // hasn't been fully closed.
                var added = ToJObject(ShaderGraphSkills.ShaderGraphAddNode(assetPath, "Vector1Node", 0f, 0f, new { value = 0.5f }));
                Assert.IsTrue(added["success"]?.Value<bool>() ?? false,
                    $"R3.b AddNode(Vector1Node) must succeed on URP 17.5. Error: {added["error"]}");
                var nodeId = added["node"]?["nodeId"]?.ToString();
                Assert.That(nodeId, Is.Not.Null.And.Not.Empty, "R3.b AddNode must return a nodeId");

                var structure = ToJObject(ShaderGraphSkills.ShaderGraphGetStructure(assetPath));
                Assert.IsTrue(structure["success"]?.Value<bool>() ?? false,
                    $"R3.c GetStructure round-trip must succeed. Error: {structure["error"]}");

                var nodes = structure["nodes"] as JArray;
                Assert.That(nodes, Is.Not.Null.And.Not.Empty,
                    "R3.c GetStructure must return at least the node we just added");
                var addedNode = nodes
                    .OfType<JObject>()
                    .FirstOrDefault(n => n["nodeId"]?.ToString() == nodeId);
                Assert.That(addedNode, Is.Not.Null,
                    $"R3.c the node we added (id={nodeId}) must round-trip through GetStructure");
                Assert.That(addedNode["type"]?.ToString(), Is.EqualTo("Vector1Node"),
                    "R3.c the round-tripped node must preserve its type");
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
