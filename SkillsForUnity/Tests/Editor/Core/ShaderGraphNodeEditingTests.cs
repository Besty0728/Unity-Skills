using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class ShaderGraphNodeEditingTests
    {
        private const string TestDirectory = "Assets/Temp/ShaderGraphNodeEditingTests";

        private static JObject ToJObject(object result)
        {
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }

        private static JObject GetNode(JObject structure, string nodeId)
        {
            return (structure["nodes"] as JArray)?.Children<JObject>()
                .FirstOrDefault(node => node["nodeId"]?.ToString() == nodeId);
        }

        private static int GetSlotId(JObject node, string direction, int index)
        {
            var slot = (node["slots"] as JArray)?.Children<JObject>()
                .Where(item => item["direction"]?.ToString() == direction)
                .Skip(index)
                .FirstOrDefault();

            Assert.IsNotNull(slot, $"Missing {direction} slot at index {index} on node {node?["nodeId"]}");
            return slot["slotId"].Value<int>();
        }

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(TestDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(TestDirectory);
        }

        [Test]
        public void ShaderGraphListSupportedNodes_ReturnsWhitelistedSubset()
        {
            var templates = ToJObject(ShaderGraphSkills.ShaderGraphListTemplates());
            if (templates["success"]?.Value<bool>() != true)
            {
                Assert.Pass("Current test host does not have Shader Graph installed.");
                return;
            }

            var result = ToJObject(ShaderGraphSkills.ShaderGraphListSupportedNodes());
            Assert.IsTrue(result["success"]?.Value<bool>() ?? false);
            Assert.That(result["count"]?.Value<int>() ?? 0, Is.GreaterThanOrEqualTo(28));

            var vector1 = (result["nodes"] as JArray)?.Children<JObject>()
                .FirstOrDefault(node => node["nodeType"]?.ToString() == "Vector1Node");
            Assert.IsNotNull(vector1);
            Assert.IsNotNull(vector1["slots"]);
            Assert.IsNotNull(vector1["settings"]);
        }

        [Test]
        public void ShaderGraphNodeEditing_WorksOnGraph()
        {
            var templates = ToJObject(ShaderGraphSkills.ShaderGraphListTemplates());
            if (templates["success"]?.Value<bool>() != true)
            {
                Assert.Pass("Current test host does not have Shader Graph installed.");
                return;
            }

            var assetPath = $"{TestDirectory}/GraphNodeEditing.shadergraph";
            RenderPipelineSkillsCommon.EnsureAssetFolderExists(assetPath);

            var created = ToJObject(ShaderGraphSkills.ShaderGraphCreateGraph(assetPath));
            Assert.IsTrue(created["success"]?.Value<bool>() ?? false);

            var added = ToJObject(ShaderGraphSkills.ShaderGraphAddNode(assetPath, "Vector1Node", 10f, 20f, new { value = 0.5f }));
            Assert.IsTrue(added["success"]?.Value<bool>() ?? false);
            var nodeId = added["node"]?["nodeId"]?.ToString();
            Assert.IsNotNull(nodeId);

            var moved = ToJObject(ShaderGraphSkills.ShaderGraphMoveNode(assetPath, nodeId, 64f, 96f));
            Assert.IsTrue(moved["success"]?.Value<bool>() ?? false);

            var structure = ToJObject(ShaderGraphSkills.ShaderGraphGetStructure(assetPath));
            var node = GetNode(structure, nodeId);
            Assert.IsNotNull(node);
            Assert.AreEqual("Vector1Node", node["type"]?.ToString());
            Assert.IsNotNull(node["slots"]);
            Assert.AreEqual(64f, node["position"]?["x"]?.Value<float>() ?? -1f);
            Assert.AreEqual(96f, node["position"]?["y"]?.Value<float>() ?? -1f);

            var removed = ToJObject(ShaderGraphSkills.ShaderGraphRemoveNode(assetPath, nodeId));
            Assert.IsTrue(removed["success"]?.Value<bool>() ?? false);
        }

        [Test]
        public void ShaderGraphNodeEditing_WorksOnSubGraph_AndRejectsInvalidOperations()
        {
            var templates = ToJObject(ShaderGraphSkills.ShaderGraphListTemplates());
            if (templates["success"]?.Value<bool>() != true)
            {
                Assert.Pass("Current test host does not have Shader Graph installed.");
                return;
            }

            var assetPath = $"{TestDirectory}/SubGraphNodeEditing.shadersubgraph";
            RenderPipelineSkillsCommon.EnsureAssetFolderExists(assetPath);

            var created = ToJObject(ShaderGraphSkills.ShaderGraphCreateSubGraph(assetPath));
            Assert.IsTrue(created["success"]?.Value<bool>() ?? false);

            var addProperty = ToJObject(ShaderGraphSkills.ShaderGraphAddProperty(assetPath, "float", "Strength", "_Strength", 1f));
            Assert.IsTrue(addProperty["success"]?.Value<bool>() ?? false);

            var propertyNode = ToJObject(ShaderGraphSkills.ShaderGraphAddNode(
                assetPath,
                "PropertyNode",
                0f,
                0f,
                new { propertyReferenceName = "_Strength" }));
            Assert.IsTrue(propertyNode["success"]?.Value<bool>() ?? false);

            var vector1Node = ToJObject(ShaderGraphSkills.ShaderGraphAddNode(
                assetPath,
                "Vector1Node",
                180f,
                0f,
                new { value = 0.25f }));
            Assert.IsTrue(vector1Node["success"]?.Value<bool>() ?? false);

            var addNode = ToJObject(ShaderGraphSkills.ShaderGraphAddNode(assetPath, "AddNode", 360f, 0f));
            Assert.IsTrue(addNode["success"]?.Value<bool>() ?? false);

            var propertyNodeId = propertyNode["node"]?["nodeId"]?.ToString();
            var vector1NodeId = vector1Node["node"]?["nodeId"]?.ToString();
            var addNodeId = addNode["node"]?["nodeId"]?.ToString();

            var structure = ToJObject(ShaderGraphSkills.ShaderGraphGetStructure(assetPath));
            var propertyNodeStruct = GetNode(structure, propertyNodeId);
            var vector1NodeStruct = GetNode(structure, vector1NodeId);
            var addNodeStruct = GetNode(structure, addNodeId);

            Assert.IsNotNull(propertyNodeStruct);
            Assert.IsNotNull(vector1NodeStruct);
            Assert.IsNotNull(addNodeStruct);
            Assert.That((vector1NodeStruct["slots"] as JArray)?.Children<JObject>().Any(slot => slot["defaultValue"] != null) ?? false, Is.True);

            var propertyOutputSlot = GetSlotId(propertyNodeStruct, "output", 0);
            var vectorInputSlot = GetSlotId(vector1NodeStruct, "input", 0);
            var vectorOutputSlot = GetSlotId(vector1NodeStruct, "output", 0);
            var addInputSlot0 = GetSlotId(addNodeStruct, "input", 0);
            var addInputSlot1 = GetSlotId(addNodeStruct, "input", 1);

            var setDefault = ToJObject(ShaderGraphSkills.ShaderGraphSetNodeDefaults(assetPath, vector1NodeId, vectorInputSlot, 0.75f));
            Assert.IsTrue(setDefault["success"]?.Value<bool>() ?? false);

            var connectA = ToJObject(ShaderGraphSkills.ShaderGraphConnectNodes(assetPath, propertyNodeId, propertyOutputSlot, addNodeId, addInputSlot0));
            Assert.IsTrue(connectA["success"]?.Value<bool>() ?? false);

            var connectB = ToJObject(ShaderGraphSkills.ShaderGraphConnectNodes(assetPath, vector1NodeId, vectorOutputSlot, addNodeId, addInputSlot1));
            Assert.IsTrue(connectB["success"]?.Value<bool>() ?? false);

            var connectedDefault = ToJObject(ShaderGraphSkills.ShaderGraphSetNodeDefaults(assetPath, addNodeId, addInputSlot1, 1f));
            StringAssert.Contains("connected", connectedDefault["error"]?.ToString());

            var disconnect = ToJObject(ShaderGraphSkills.ShaderGraphDisconnectNodes(assetPath, vector1NodeId, vectorOutputSlot, addNodeId, addInputSlot1));
            Assert.IsTrue(disconnect["success"]?.Value<bool>() ?? false);

            var setSettings = ToJObject(ShaderGraphSkills.ShaderGraphSetNodeSettings(assetPath, vector1NodeId, new { value = 1.25f }));
            Assert.IsTrue(setSettings["success"]?.Value<bool>() ?? false);

            var invalidField = ToJObject(ShaderGraphSkills.ShaderGraphSetNodeSettings(assetPath, vector1NodeId, new { invalid = 1f }));
            StringAssert.Contains("Unsupported setting", invalidField["error"]?.ToString());

            var missingProperty = ToJObject(ShaderGraphSkills.ShaderGraphAddNode(
                assetPath,
                "PropertyNode",
                0f,
                180f,
                new { propertyReferenceName = "_Missing" }));
            StringAssert.Contains("was not found", missingProperty["error"]?.ToString());

            var unsupportedNode = ToJObject(ShaderGraphSkills.ShaderGraphAddNode(assetPath, "TimeNode"));
            StringAssert.Contains("Unsupported nodeType", unsupportedNode["error"]?.ToString());

            structure = ToJObject(ShaderGraphSkills.ShaderGraphGetStructure(assetPath));
            vector1NodeStruct = GetNode(structure, vector1NodeId);
            Assert.AreEqual(1.25f, vector1NodeStruct["settings"]?["value"]?.Value<float>() ?? -1f);

            var removed = ToJObject(ShaderGraphSkills.ShaderGraphRemoveNode(assetPath, addNodeId));
            Assert.IsTrue(removed["success"]?.Value<bool>() ?? false);
            Assert.That(removed["node"]?["removedEdgeCount"]?.Value<int>() ?? 0, Is.GreaterThanOrEqualTo(1));
        }
    }
}

// Producer:Betsy
