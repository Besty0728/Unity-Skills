using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class DeepInspectorSkillTests
    {
        private const string TempRoot = "Assets/Temp/DeepInspectorSkillTests";
        private SkillsOperatingMode _savedMode;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EnsureTempFolder();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            GameObjectFinder.InvalidateCache();
            if (AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.DeleteAsset(TempRoot);
                AssetDatabase.Refresh();
            }
            SkillsModeManager.CurrentMode = _savedMode;
        }

        [Test]
        public void UISetRectTransform_CoversAnchorsOffsetsAndLocalTransform()
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            var child = new GameObject("Panel", typeof(RectTransform));
            child.transform.SetParent(canvas.transform, false);

            var result = JObject.FromObject(UISkills.UISetRectTransform(
                name: "Panel",
                anchorMinX: 0, anchorMinY: 0,
                anchorMaxX: 1, anchorMaxY: 1,
                pivotX: 0.25f, pivotY: 0.75f,
                anchoredPosX: 12, anchoredPosY: 34, anchoredPosZ: 2,
                offsetMinX: 5, offsetMinY: 6,
                offsetMaxX: -7, offsetMaxY: -8,
                localRotZ: 15,
                localScaleX: 1.2f, localScaleY: 0.8f));

            Assert.That(result["success"]?.Value<bool>(), Is.True);
            var rect = child.GetComponent<RectTransform>();
            Assert.That(rect.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(rect.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(rect.pivot.x, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(rect.pivot.y, Is.EqualTo(0.75f).Within(0.001f));
            Assert.That(rect.anchoredPosition3D.z, Is.EqualTo(2f).Within(0.001f));
            Assert.That(rect.offsetMin.x, Is.EqualTo(5f).Within(0.001f));
            Assert.That(rect.localEulerAngles.z, Is.EqualTo(15f).Within(0.001f));
            Assert.That(rect.localScale.x, Is.EqualTo(1.2f).Within(0.001f));

            AssertSuccess(UISkills.UISetRectTransform(name: "Panel", width: 320, height: 180));
            Assert.That(rect.rect.width, Is.EqualTo(320f).Within(0.001f));
            Assert.That(rect.rect.height, Is.EqualTo(180f).Within(0.001f));
        }

        [Test]
        public void ComponentSetSerializedProperty_SetsInspectorVisibleFieldsAndReferences()
        {
            var go = new GameObject("Owner");
            var target = new GameObject("Target");
            var component = go.AddComponent<DeepInspectorFixture>();
            var shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null, "No test shader found for material reference assignment.");
            var material = new Material(shader);
            var materialPath = $"{TempRoot}/Fixture.mat";
            AssetDatabase.CreateAsset(material, materialPath);

            AssertSuccess(ComponentSkills.ComponentSetSerializedProperty(name: "Owner", componentType: nameof(DeepInspectorFixture), propertyPath: "privateText", value: "changed"));
            AssertSuccess(ComponentSkills.ComponentSetSerializedProperty(name: "Owner", componentType: nameof(DeepInspectorFixture), propertyPath: "nested.vector", value: "1,2,3"));
            AssertSuccess(ComponentSkills.ComponentSetSerializedProperty(name: "Owner", componentType: nameof(DeepInspectorFixture), propertyPath: "mode", value: "Advanced"));
            AssertSuccess(ComponentSkills.ComponentSetSerializedProperty(name: "Owner", componentType: nameof(DeepInspectorFixture), propertyPath: "references.Array.data[0]", referenceName: "Target", objectType: "GameObject"));
            AssertSuccess(ComponentSkills.ComponentSetSerializedProperty(name: "Owner", componentType: nameof(DeepInspectorFixture), propertyPath: "material", assetPath: materialPath, objectType: "Material"));

            Assert.That(component.PrivateText, Is.EqualTo("changed"));
            Assert.That(component.NestedVector, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(component.Mode, Is.EqualTo(DeepInspectorMode.Advanced));
            Assert.That(component.FirstReference, Is.EqualTo(target));
            Assert.That(component.Material, Is.EqualTo(material));
        }

        [Test]
        public void ComponentCopyExact_CopiesAndVerifiesSerializedFields()
        {
            var source = new GameObject("Source");
            var target = new GameObject("Target");
            var sourceComponent = source.AddComponent<DeepInspectorFixture>();
            sourceComponent.ConfigureForCopy("copied", new Vector3(7, 8, 9), DeepInspectorMode.Advanced);

            var result = JObject.FromObject(ComponentSkills.ComponentCopyExact(
                sourceName: "Source",
                targetName: "Target",
                componentType: nameof(DeepInspectorFixture)));

            Assert.That(result["success"]?.Value<bool>(), Is.True);
            Assert.That(result["verified"]?.Value<bool>(), Is.True);
            Assert.That(result["mismatchCount"]?.Value<int>(), Is.EqualTo(0));

            var copied = target.GetComponent<DeepInspectorFixture>();
            Assert.That(copied.PrivateText, Is.EqualTo("copied"));
            Assert.That(copied.NestedVector, Is.EqualTo(new Vector3(7, 8, 9)));
            Assert.That(copied.Mode, Is.EqualTo(DeepInspectorMode.Advanced));
        }

        [Test]
        public void EventSetListener_ReplacesButtonOnClickPersistentListener()
        {
            var buttonGo = new GameObject("Button", typeof(RectTransform), typeof(Button));
            var receiverGo = new GameObject("Receiver");
            var receiver = receiverGo.AddComponent<DeepInspectorFixture>();
            var button = buttonGo.GetComponent<Button>();
            UnityEventTools.AddPersistentListener(button.onClick, receiver.First);

            var result = JObject.FromObject(EventSkills.EventSetListener(
                name: "Button",
                componentName: "Button",
                eventName: "onClick",
                index: 0,
                targetName: "Receiver",
                targetComponentName: nameof(DeepInspectorFixture),
                methodName: nameof(DeepInspectorFixture.AcceptString),
                argType: "string",
                stringArg: "payload",
                mode: "EditorAndRuntime"));

            Assert.That(result["success"]?.Value<bool>(), Is.True);
            Assert.That(button.onClick.GetPersistentMethodName(0), Is.EqualTo(nameof(DeepInspectorFixture.AcceptString)));
            Assert.That(button.onClick.GetPersistentListenerState(0), Is.EqualTo(UnityEventCallState.EditorAndRuntime));

            var serialized = new SerializedObject(button);
            var arg = serialized.FindProperty("m_OnClick.m_PersistentCalls.m_Calls.Array.data[0].m_Arguments.m_StringArgument");
            Assert.That(arg.stringValue, Is.EqualTo("payload"));
        }

        private static void AssertSuccess(object result)
        {
            var json = JObject.FromObject(result);
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString());
        }

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
            {
                AssetDatabase.CreateFolder("Assets", "Temp");
            }
            if (!AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.CreateFolder("Assets/Temp", "DeepInspectorSkillTests");
            }
            AssetDatabase.Refresh();
        }
    }

    public enum DeepInspectorMode
    {
        Basic,
        Advanced
    }

    [System.Serializable]
    public class DeepInspectorNested
    {
        public Vector3 vector;
    }

    public class DeepInspectorFixture : MonoBehaviour
    {
        public int publicInt = 3;
        [SerializeField] private string privateText = "initial";
        [SerializeField] private DeepInspectorNested nested = new DeepInspectorNested();
        [SerializeField] private List<GameObject> references = new List<GameObject> { null };
        [SerializeField] private Material material;
        [SerializeField] private DeepInspectorMode mode;

        public string PrivateText => privateText;
        public Vector3 NestedVector => nested.vector;
        public GameObject FirstReference => references[0];
        public Material Material => material;
        public DeepInspectorMode Mode => mode;

        public void ConfigureForCopy(string text, Vector3 vector, DeepInspectorMode newMode)
        {
            privateText = text;
            nested.vector = vector;
            mode = newMode;
        }

        public void First() { }
        public void Second() { }
        public void AcceptString(string value) { }
        public void AcceptObject(Object value) { }
    }
}

// Producer:Betsy
