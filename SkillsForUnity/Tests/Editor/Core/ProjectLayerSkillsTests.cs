using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class ProjectLayerSkillsTests
    {
        private const string TagManagerPath = "ProjectSettings/TagManager.asset";
        private const int LayerCount = 32;
        private const int FirstUserLayerIndex = 8;
        private const string LayerA = "USK_Test_Layer_A";
        private const string LayerB = "USK_Test_Layer_B";
        private const string LayerC = "USK_Test_Layer_C";

        private List<string> _savedLayers;

        [SetUp]
        public void SetUp()
        {
            _savedLayers = ReadLayers(LoadTagManager());
        }

        [TearDown]
        public void TearDown()
        {
            var tagManager = LoadTagManager();
            var layersProp = tagManager.FindProperty("layers");
            for (int i = 0; i < LayerCount; i++)
                layersProp.GetArrayElementAtIndex(i).stringValue = _savedLayers[i];
            tagManager.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        [Test]
        public void AddLayer_UsesFirstEmptyUserSlot()
        {
            var empty = FindEmptyUserSlots();
            if (empty.Count == 0) Assert.Ignore("No empty user layer slot in this project");

            var result = JObject.FromObject(ProjectSkills.ProjectAddLayer(LayerA));

            Assert.AreEqual(true, (bool)result["success"]);
            Assert.AreEqual(empty[0], (int)result["index"]);
            Assert.AreEqual(LayerA, ReadLayers(LoadTagManager())[empty[0]]);
        }

        [Test]
        public void AddLayer_ExplicitFreeIndex_UsesRequestedSlot()
        {
            var empty = FindEmptyUserSlots();
            if (empty.Count == 0) Assert.Ignore("No empty user layer slot in this project");
            int requested = empty[empty.Count - 1];

            var result = JObject.FromObject(ProjectSkills.ProjectAddLayer(LayerA, requested));

            Assert.AreEqual(true, (bool)result["success"]);
            Assert.AreEqual(requested, (int)result["index"]);
            Assert.AreEqual(LayerA, ReadLayers(LoadTagManager())[requested]);
        }

        [Test]
        public void AddLayer_BuiltInIndex_Rejected()
        {
            AssertRejectedAndNotPersisted(ProjectSkills.ProjectAddLayer(LayerA, 3), LayerA);
        }

        [Test]
        public void AddLayer_IndexAboveRange_Rejected()
        {
            AssertRejectedAndNotPersisted(ProjectSkills.ProjectAddLayer(LayerA, 32), LayerA);
        }

        [Test]
        public void AddLayer_DuplicateName_Rejected()
        {
            var empty = FindEmptyUserSlots();
            if (empty.Count == 0) Assert.Ignore("No empty user layer slot in this project");
            ProjectSkills.ProjectAddLayer(LayerA);

            var duplicate = ProjectSkills.ProjectAddLayer(LayerA);

            AssertRejected(duplicate);
            Assert.AreEqual(1, CountOccurrences(ReadLayers(LoadTagManager()), LayerA));
        }

        [Test]
        public void AddLayer_OccupiedSlot_Rejected()
        {
            var empty = FindEmptyUserSlots();
            if (empty.Count == 0) Assert.Ignore("No empty user layer slot in this project");
            int occupied = empty[0];
            ProjectSkills.ProjectAddLayer(LayerA, occupied);

            var result = ProjectSkills.ProjectAddLayer(LayerB, occupied);

            AssertRejected(result);
            Assert.AreEqual(LayerA, ReadLayers(LoadTagManager())[occupied]);
            AssertNotPersisted(LayerB);
        }

        [Test]
        public void LayersRestorer_CaptureThenApply_RemovesAddedLayer()
        {
            var empty = FindEmptyUserSlots();
            if (empty.Count == 0) Assert.Ignore("No empty user layer slot in this project");
            var captured = WorkflowSettingRestorerRegistry.TryGetCurrentValue("project.layers");
            Assert.IsNotNull(captured);

            ProjectSkills.ProjectAddLayer(LayerC);
            Assert.That(ReadLayers(LoadTagManager()), Does.Contain(LayerC));

            Assert.IsTrue(WorkflowSettingRestorerRegistry.TryRestore("project.layers", captured));
            AssertNotPersisted(LayerC);
        }

        private static SerializedObject LoadTagManager()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(TagManagerPath);
            Assert.That(assets, Is.Not.Empty, "TagManager.asset could not be loaded");
            return new SerializedObject(assets[0]);
        }

        private static List<string> ReadLayers(SerializedObject tagManager)
        {
            var layersProp = tagManager.FindProperty("layers");
            var layers = new List<string>();
            for (int i = 0; i < layersProp.arraySize; i++)
                layers.Add(layersProp.GetArrayElementAtIndex(i).stringValue);
            return layers;
        }

        private static List<int> FindEmptyUserSlots()
        {
            var layers = ReadLayers(LoadTagManager());
            var empty = new List<int>();
            for (int i = FirstUserLayerIndex; i < layers.Count; i++)
            {
                if (string.IsNullOrEmpty(layers[i])) empty.Add(i);
            }
            return empty;
        }

        private static int CountOccurrences(List<string> layers, string name)
        {
            int count = 0;
            foreach (var layer in layers)
            {
                if (layer == name) count++;
            }
            return count;
        }

        private static void AssertRejected(object response)
        {
            var result = JObject.FromObject(response);
            Assert.IsNull(result["success"]);
            Assert.AreEqual(SkillErrorCode.SemanticInvalid.ToWireString(), (string)result["errorCode"]);
        }

        private static void AssertRejectedAndNotPersisted(object response, string layerName)
        {
            AssertRejected(response);
            AssertNotPersisted(layerName);
        }

        private static void AssertNotPersisted(string layerName)
        {
            Assert.That(ReadLayers(LoadTagManager()), Does.Not.Contain(layerName));
        }
    }
}

// Producer:Betsy
