using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// Unity Behavior (com.unity.behavior) live skill tests: graph asset creation, blackboard variable
    /// read/write, and agent attachment. No behavior_* skill can add a new blackboard variable --
    /// behavior_blackboard_set only writes the value of one that already exists -- so
    /// <see cref="AddFloatBlackboardVariable"/> adds one directly to the graph's BlackboardAsset.Variables
    /// list, mirroring the Behavior editor's own "+" button. Verified against
    /// Tools/Graph/Asset/{GraphAsset,BlackboardAsset,VariableModel,TypedVariableModel}.cs in the installed
    /// com.unity.behavior 1.0.16 source; everything else goes through SkillRouter and reflection, per the base class.
    /// </summary>
    [TestFixture]
    public class BehaviorSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "behavior_graph_list";

        private static string CreateGraph(string fileName)
        {
            EnsureProbeFolder();
            var result = Ok(Run("behavior_graph_create", new JObject { ["savePath"] = $"{ProbeFolder}/{fileName}.asset" }));
            var path = result["path"]?.ToString();
            Assert.That(path, Is.Not.Null.And.Not.Empty, result.ToString());
            Assert.That(result["hasRuntimeGraph"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["warning"]?.Value<string>(), Is.Null, result.ToString());
            return path;
        }

        /// <summary>
        /// Adds a float blackboard variable directly to the graph's blackboard, bypassing every skill.
        /// GraphAsset.Blackboard is a public field; BlackboardAsset.Variables is a public
        /// List&lt;VariableModel&gt; property whose getter returns the live, mutable list, so adding to it
        /// needs no internal setter. TypedVariableModel&lt;T&gt; is the only concrete, instantiable
        /// VariableModel (the base class is abstract). The value is left at its CLR default (0f) --
        /// the skill under test is what writes the real value, and its own rebuild/save runs right after.
        /// </summary>
        private static void AddFloatBlackboardVariable(string graphAssetPath, string variableName)
        {
            var authoringGraph = AssetDatabase.LoadMainAssetAtPath(graphAssetPath);
            Assert.That(authoringGraph, Is.Not.Null, $"No asset at {graphAssetPath}.");

            var blackboard = Member(authoringGraph, "Blackboard");
            Assert.That(blackboard, Is.Not.Null, "GraphAsset.Blackboard was null after behavior_graph_create.");

            var openVariableModelType = PackageType("Unity.Behavior.GraphFramework.TypedVariableModel`1");
            var floatVariableType = openVariableModelType.MakeGenericType(typeof(float));
            var variable = Activator.CreateInstance(floatVariableType);
            floatVariableType.GetField("Name", BindingFlags.Public | BindingFlags.Instance).SetValue(variable, variableName);

            var variables = (IList)Member(blackboard, "Variables");
            variables.Add(variable);
        }

        [Test]
        public void GraphCreate_BakesRuntimeGraphInProbeFolder()
        {
            var path = CreateGraph("R6Graph");
            Assert.That(path, Does.StartWith(ProbeFolder));
        }

        [Test]
        public void BlackboardSet_OnGraphAsset_WritesValueAndListReadsBackWithNullWarning()
        {
            var path = CreateGraph("R6BlackboardGraph");
            AddFloatBlackboardVariable(path, "R6FloatVar");

            var setResult = Ok(Run("behavior_blackboard_set", new JObject
            {
                ["graphAssetPath"] = path, ["variable"] = "R6FloatVar", ["value"] = 3.5,
            }));
            Assert.That(setResult["target"]?.ToString(), Is.EqualTo("asset"), setResult.ToString());
            Assert.That(setResult["type"]?.ToString(), Is.EqualTo("Single"), setResult.ToString());
            Assert.That(setResult["value"]?.Value<float>(), Is.EqualTo(3.5f).Within(1e-5f));
            Assert.That(setResult["warning"]?.Value<string>(), Is.Null, setResult.ToString());

            var list = Ok(Run("behavior_blackboard_list", new JObject { ["graphAssetPath"] = path }));
            var variable = ((JArray)list["variables"]).FirstOrDefault(v => v["name"]?.ToString() == "R6FloatVar");
            Assert.That(variable, Is.Not.Null, list.ToString());
            Assert.That(variable["value"]?.Value<float>(), Is.EqualTo(3.5f).Within(1e-5f));
        }

        [Test]
        public void AgentAdd_ThenAgentGet_ReadsBoundGraph()
        {
            var path = CreateGraph("R6AgentGraph");
            Ok(Run("gameobject_create", new JObject { ["name"] = "R6BehaviorGo" }));

            var added = Ok(Run("behavior_agent_add", new JObject { ["name"] = "R6BehaviorGo", ["graphAssetPath"] = path }));
            Assert.That(added["componentAdded"]?.Value<bool>(), Is.True, added.ToString());
            Assert.That(added["graphAssetPath"]?.ToString(), Is.EqualTo(path));
            Assert.That(ComponentNamed(Find("R6BehaviorGo"), "BehaviorGraphAgent"), Is.Not.Null);

            var got = Ok(Run("behavior_agent_get", new JObject { ["name"] = "R6BehaviorGo" }));
            Assert.That(got["graphAssetPath"]?.ToString(), Is.EqualTo(path), got.ToString());
        }

        [Test]
        public void GraphList_FilteredByProbeFolder_FindsCreatedGraph()
        {
            var path = CreateGraph("R6ListGraph");

            var listed = Ok(Run("behavior_graph_list", new JObject { ["folder"] = ProbeFolder }));
            Assert.That(listed["count"]?.Value<int>(), Is.GreaterThanOrEqualTo(1), listed.ToString());
            Assert.That(((JArray)listed["graphs"]).Any(g => g["path"]?.ToString() == path), Is.True, listed.ToString());
        }
    }
}

// Producer:Betsy
