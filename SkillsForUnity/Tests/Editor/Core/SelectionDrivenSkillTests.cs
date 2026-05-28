using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class SelectionDrivenSkillTests
    {
        private const string TempRoot = "Assets/CodexTemp/SelectionDrivenSkillTests";

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Selection.objects = new Object[0];
            GameObjectFinder.InvalidateCache();

            if (!AssetDatabase.IsValidFolder("Assets/CodexTemp"))
                AssetDatabase.CreateFolder("Assets", "CodexTemp");
            if (!AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.CreateFolder("Assets/CodexTemp", "SelectionDrivenSkillTests");
        }

        [TearDown]
        public void TearDown()
        {
            Selection.objects = new Object[0];
            GameObjectFinder.InvalidateCache();
            AssetDatabase.DeleteAsset(TempRoot);
            AssetDatabase.Refresh();
        }

        [Test]
        public void SmartSceneLayout_ExecutesAgainstCurrentSelection()
        {
            var a = CreateObject("LayoutA", new Vector3(0, 0, 0));
            var b = CreateObject("LayoutB", new Vector3(2, 0, 0));
            var c = CreateObject("LayoutC", new Vector3(4, 0, 0));
            Selection.objects = new Object[] { a, b, c };

            var response = Execute("smart_scene_layout", @"{""layoutType"":""Circle"",""spacing"":3}");

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(response["result"]?["success"]?.Value<bool>(), Is.True);
            Assert.That(response["result"]?["count"]?.Value<int>(), Is.EqualTo(3));
        }

        [Test]
        public void SmartAlignToGround_AlignsSelectedObjects()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            var above = CreateObject("AboveGround", new Vector3(0, 5, 0));
            Selection.objects = new Object[] { above };

            var response = Execute("smart_align_to_ground", @"{""maxDistance"":20}");

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(response["result"]?["success"]?.Value<bool>(), Is.True);
            Assert.That(response["result"]?["aligned"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(above.transform.position.y, Is.EqualTo(0f).Within(0.05f));
            Object.DestroyImmediate(ground);
        }

        [Test]
        public void SmartDistribute_SpreadsMiddleObjectsBetweenEndpoints()
        {
            var a = CreateObject("DistA", new Vector3(0, 0, 0));
            var b = CreateObject("DistB", new Vector3(1, 0, 0));
            var c = CreateObject("DistC", new Vector3(2, 0, 0));
            var d = CreateObject("DistD", new Vector3(9, 0, 0));
            Selection.objects = new Object[] { a, b, c, d };

            var response = Execute("smart_distribute", @"{""axis"":""X""}");

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(response["result"]?["success"]?.Value<bool>(), Is.True);
            Assert.That(b.transform.position.x, Is.EqualTo(3f).Within(0.01f));
            Assert.That(c.transform.position.x, Is.EqualTo(6f).Within(0.01f));
        }

        [Test]
        public void SmartSnapToGrid_SnapsSelection()
        {
            var a = CreateObject("GridA", new Vector3(1.3f, 0.2f, 2.7f));
            var b = CreateObject("GridB", new Vector3(-0.6f, 0.9f, 3.4f));
            Selection.objects = new Object[] { a, b };

            var response = Execute("smart_snap_to_grid", @"{""gridSize"":1}");

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(response["result"]?["success"]?.Value<bool>(), Is.True);
            Assert.That(a.transform.position.x, Is.EqualTo(1f).Within(0.001f));
            Assert.That(a.transform.position.z, Is.EqualTo(3f).Within(0.001f));
            Assert.That(b.transform.position.x, Is.EqualTo(-1f).Within(0.001f));
            Assert.That(b.transform.position.y, Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void SmartRandomizeTransform_ModifiesSelection()
        {
            var a = CreateObject("RandomA", new Vector3(0, 0, 0));
            var b = CreateObject("RandomB", new Vector3(1, 0, 0));
            Selection.objects = new Object[] { a, b };
            var before = a.transform.position;

            var response = Execute("smart_randomize_transform", @"{""posRange"":1,""rotRange"":15,""scaleMin"":0.8,""scaleMax"":1.2}");

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(response["result"]?["success"]?.Value<bool>(), Is.True);
            Assert.That(a.transform.position, Is.Not.EqualTo(before));
        }

        [Test]
        public void SmartReplaceObjects_ReplacesSelectedObjectsWithPrefab()
        {
            var sourcePrefabGo = CreateObject("ReplacementPrefabSource", new Vector3(0, 0, 0));
            var prefabPath = Path.Combine(TempRoot, "Replacement.prefab").Replace('\\', '/');
            PrefabUtility.SaveAsPrefabAsset(sourcePrefabGo, prefabPath);
            Object.DestroyImmediate(sourcePrefabGo);
            // Unity 6 import pipeline is genuinely async — SaveAsPrefabAsset queues an
            // import that may not complete before AssetDatabase.LoadAssetAtPath reads
            // the imported asset cache, returning null in the skill body. Force
            // synchronous import so the prefab is visible to the next LoadAssetAtPath.
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);

            var a = CreateObject("ReplaceA", new Vector3(1, 0, 0));
            var b = CreateObject("ReplaceB", new Vector3(2, 0, 0));
            Selection.objects = new Object[] { a, b };

            var response = Execute("smart_replace_objects", $@"{{""prefabPath"":""{prefabPath}""}}");

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(response["result"]?["success"]?.Value<bool>(), Is.True);
            Assert.That(response["result"]?["replaced"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(GameObject.Find("ReplaceA"), Is.Null);
            Assert.That(GameObject.Find("ReplaceB"), Is.Null);
        }

        [Test]
        public void SmartSelectByComponent_SelectsMatchingObjects()
        {
            var lightGo = new GameObject("LightHolder");
            lightGo.AddComponent<Light>();
            var plain = new GameObject("Plain");

            var response = Execute("smart_select_by_component", @"{""componentName"":""Light""}");

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(response["result"]?["success"]?.Value<bool>(), Is.True);
            Assert.That(response["result"]?["selected"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(Selection.objects, Has.Member((Object)lightGo));
            Assert.That(Selection.objects, Has.No.Member((Object)plain));
        }

        [Test]
        public void UIAlignSelected_AlignsRectTransforms()
        {
            var canvas = CreateCanvas();
            var a = CreateUiChild(canvas.transform, "UiA", new Vector2(-100, 0));
            var b = CreateUiChild(canvas.transform, "UiB", new Vector2(50, 20));
            Selection.objects = new Object[] { a, b };

            var response = Execute("ui_align_selected", @"{""alignment"":""Center""}");

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(response["result"]?["success"]?.Value<bool>(), Is.True);
            Assert.That(a.GetComponent<RectTransform>().anchoredPosition.x,
                Is.EqualTo(b.GetComponent<RectTransform>().anchoredPosition.x).Within(0.001f));
        }

        [Test]
        public void UIDistributeSelected_RepositionsRectTransformsEvenly()
        {
            var canvas = CreateCanvas();
            var a = CreateUiChild(canvas.transform, "UiLeft", new Vector2(0, 0));
            var b = CreateUiChild(canvas.transform, "UiMid", new Vector2(10, 0));
            var c = CreateUiChild(canvas.transform, "UiRight", new Vector2(90, 0));
            Selection.objects = new Object[] { a, b, c };

            var response = Execute("ui_distribute_selected", @"{""direction"":""Horizontal""}");

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(response["result"]?["success"]?.Value<bool>(), Is.True);
            Assert.That(b.GetComponent<RectTransform>().anchoredPosition.x, Is.EqualTo(45f).Within(0.01f));
        }

        private static JObject Execute(string skillName, string jsonBody)
        {
            return JObject.Parse(SkillRouter.Execute(skillName, jsonBody));
        }

        private static GameObject CreateObject(string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            return go;
        }

        private static GameObject CreateCanvas()
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            return canvas;
        }

        private static GameObject CreateUiChild(Transform parent, string name, Vector2 anchoredPosition)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(100, 40);
            rect.anchoredPosition = anchoredPosition;
            return go;
        }
    }
}
