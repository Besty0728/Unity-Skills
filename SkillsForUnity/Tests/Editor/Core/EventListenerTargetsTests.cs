using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Tests.Fixtures;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// event_add_listener located its target by name only and refused Object arguments its description promised;
    /// event_get_listeners named a target without saying which of several same-name objects it was (P5, P13). A target
    /// is now located by entityId, instanceId, path or name (an entityId that names nothing is an error, never a
    /// fallback to the name), an Object argument comes from the objectArg* parameters, and every listener reports its
    /// target's ids, scene path and asset path.
    /// </summary>
    [TestFixture]
    public class EventListenerTargetsTests
    {
        private const string TempRoot = "Assets/Temp/EventListenerTargetsTests";
        private const string Emitter = "Emitter";
        private const string Twin = "Twin";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private EventProbe _emitter;
        private EventProbe _left;
        private EventProbe _right;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            _emitter = new GameObject(Emitter).AddComponent<EventProbe>();
            _left = CreateTwin("LeftRoot");
            _right = CreateTwin("RightRoot");
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
            if (AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.DeleteAsset(TempRoot);
        }

        // ---------- targets ----------

        [Test]
        public void AddListener_ByTargetEntityId_BindsThatSameNameObject()
        {
            foreach (var twin in new[] { _left, _right })
            {
                var json = AddListener(targetObjectName: Twin, targetEntityId: Id(twin), methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));

                Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
                var index = json["index"].Value<int>();
                Assert.That(_emitter.onFire.GetPersistentTarget(index), Is.EqualTo(twin));
                Assert.That(json["targetEntityId"]?.ToString(), Is.EqualTo(Id(twin)), "The result names the object it bound.");
                Assert.That(json["targetPath"]?.ToString(), Is.EqualTo(GameObjectFinder.GetPath(twin.gameObject)));
            }
        }

        [Test]
        public void AddListener_ByTargetInstanceId_BindsThatSameNameObject()
        {
            Assume.That(UnityObjectIdUtility.GetObjectId(_right.gameObject), Is.Not.EqualTo(0), "This Unity version has no legacy instance ids.");

            var json = AddListener(targetObjectName: Twin, targetInstanceId: UnityObjectIdUtility.GetObjectId(_right.gameObject),
                methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_right));
        }

        [Test]
        public void AddListener_ByTargetPath_BindsThatSameNameObject()
        {
            var json = AddListener(targetObjectName: Twin, targetPath: "RightRoot/Twin", methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_right));
            Assert.That(json["targetPath"]?.ToString(), Is.EqualTo("RightRoot/Twin"));
        }

        [Test]
        public void AddListener_UnknownTargetEntityId_IsRefusedAndNeverFallsBackToTheName()
        {
            var json = AddListener(targetObjectName: Twin, targetEntityId: "999999999", methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));

            Assert.That(json["error"]?.ToString(), Does.Contain("entityId"), json.ToString(Formatting.None));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("TARGET_NOT_FOUND"));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0), "A stale id must not bind the same-name object.");
        }

        [Test]
        public void AddListener_NoTargetComponent_TargetsTheGameObjectItself()
        {
            var json = AddListener(targetObjectName: Twin, targetEntityId: Id(_left), methodName: "SetActive", argType: "bool", boolArg: true);

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_left.gameObject));
            Assert.That(json["targetType"]?.ToString(), Is.EqualTo("GameObject"));
        }

        // ---------- Object arguments ----------

        [Test]
        public void AddListener_ObjectArgument_ByEntityId_IsStored()
        {
            var json = AddListener(targetObjectName: Twin, targetEntityId: Id(_left), targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object", objectArgEntityId: Id(_right));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_left));
            Assert.That(_emitter.onFire.GetPersistentMethodName(0), Is.EqualTo(nameof(EventProbe.TakeObject)));
            Assert.That(ObjectArgument(0), Is.EqualTo(_right.gameObject));
            Assert.That(json["argType"]?.ToString(), Is.EqualTo("object"));
        }

        [Test]
        public void AddListener_ObjectArgument_ByPath_IsStored()
        {
            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object", objectArgPath: "RightRoot/Twin");

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(ObjectArgument(0), Is.EqualTo(_right.gameObject));
        }

        [Test]
        public void AddListener_ObjectArgument_ByInstanceId_IsStored()
        {
            Assume.That(UnityObjectIdUtility.GetObjectId(_right.gameObject), Is.Not.EqualTo(0), "This Unity version has no legacy instance ids.");

            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object", objectArgInstanceId: UnityObjectIdUtility.GetObjectId(_right.gameObject));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(ObjectArgument(0), Is.EqualTo(_right.gameObject));
        }

        [Test]
        public void AddListener_ObjectArgument_ByAssetPath_IsStored()
        {
            var material = CreateMaterialAsset();

            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeMaterial), argType: "object", objectArgAssetPath: AssetDatabase.GetAssetPath(material), objectArgType: "Material");

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(ObjectArgument(0), Is.EqualTo(material));
        }

        [Test]
        public void AddListener_ObjectArgumentWithoutALocator_NamesTheParametersAndAddsNothing()
        {
            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object");

            Assert.That(json["error"]?.ToString(), Does.Contain("needs the argument").And.Contain("objectArgAssetPath"), json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0));
        }

        [Test]
        public void AddListener_ObjectArgumentOfTheWrongType_FindsNoCompatibleMethod()
        {
            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeMaterial), argType: "object", objectArgPath: "RightRoot/Twin");

            Assert.That(json["error"]?.ToString(), Does.Contain("Compatible object method"), json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0));
        }

        [Test]
        public void AddListener_UnknownObjectArgumentEntityId_IsRefused()
        {
            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object", objectArgEntityId: "999999999", objectArgPath: "RightRoot/Twin");

            Assert.That(json["error"]?.ToString(), Does.Contain("entityId"), json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0), "The path must not stand in for an id that names nothing.");
        }

        // ---------- the wire ----------

        [Test]
        public void AddListener_ThroughTheRouter_AcceptsTheLocatorsAndTheObjectArgument()
        {
            var args = new JObject
            {
                ["name"] = Emitter, ["componentName"] = nameof(EventProbe), ["eventName"] = "onFire",
                ["targetObjectName"] = Twin, ["targetEntityId"] = Id(_right), ["targetComponentName"] = nameof(EventProbe),
                ["methodName"] = nameof(EventProbe.TakeObject), ["argType"] = "object", ["objectArgPath"] = "LeftRoot/Twin"
            };

            var dryRun = JObject.Parse(SkillRouter.DryRun("event_add_listener", args.ToString(Formatting.None)));
            Assert.That(dryRun["valid"]?.Value<bool>(), Is.True, dryRun.ToString(Formatting.None));

            var response = JObject.Parse(SkillRouter.Execute("event_add_listener", args.ToString(Formatting.None)));
            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_right));
            Assert.That(ObjectArgument(0), Is.EqualTo(_left.gameObject));
            Assert.That(response["result"]?["targetEntityId"]?.ToString(), Is.EqualTo(Id(_right)));
        }

        [Test]
        public void AddListener_ThroughTheRouter_AnIdOrAPathAloneIsEnough()
        {
            foreach (var locator in new[] { ("targetEntityId", Id(_right)), ("targetPath", "LeftRoot/Twin") })
            {
                var args = new JObject
                {
                    ["name"] = Emitter, ["componentName"] = nameof(EventProbe), ["eventName"] = "onFire",
                    ["targetComponentName"] = nameof(EventProbe), ["methodName"] = nameof(EventProbe.Ping), [locator.Item1] = locator.Item2
                };

                var dryRun = JObject.Parse(SkillRouter.DryRun("event_add_listener", args.ToString(Formatting.None)));
                Assert.That(dryRun["valid"]?.Value<bool>(), Is.True, $"{locator.Item1}: {dryRun.ToString(Formatting.None)}");

                var response = JObject.Parse(SkillRouter.Execute("event_add_listener", args.ToString(Formatting.None)));
                Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), $"{locator.Item1}: {response.ToString(Formatting.None)}");
            }

            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_right));
            Assert.That(_emitter.onFire.GetPersistentTarget(1), Is.EqualTo(_left));
        }

        [Test]
        public void SetListener_ThroughTheRouter_AnEntityIdAloneIsEnough()
        {
            UnityEventTools.AddPersistentListener(_emitter.onFire, _left.Ping);
            var args = new JObject
            {
                ["name"] = Emitter, ["componentName"] = nameof(EventProbe), ["eventName"] = "onFire", ["index"] = 0,
                ["targetComponentName"] = nameof(EventProbe), ["methodName"] = nameof(EventProbe.Ping), ["targetEntityId"] = Id(_right)
            };

            var response = JObject.Parse(SkillRouter.Execute("event_set_listener", args.ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_right));
        }

        [Test]
        public void AddListener_WithoutAnyTargetLocator_IsRefusedByValidation_SoDryRunPlanAndExecuteAgree()
        {
            var json = new JObject
            {
                ["name"] = Emitter, ["componentName"] = nameof(EventProbe), ["eventName"] = "onFire",
                ["targetComponentName"] = nameof(EventProbe), ["methodName"] = nameof(EventProbe.Ping)
            }.ToString(Formatting.None);

            var dryRun = JObject.Parse(SkillRouter.DryRun("event_add_listener", json));
            var plan = JObject.Parse(SkillRouter.Plan("event_add_listener", json));
            var response = JObject.Parse(SkillRouter.Execute("event_add_listener", json));

            foreach (var preview in new[] { dryRun, plan })
            {
                Assert.That(preview["valid"]?.Value<bool>(), Is.False, preview.ToString(Formatting.None));
                Assert.That(preview["validation"]?["semanticErrors"]?[0]?["error"]?.ToString(),
                    Does.Contain("Provide one of").And.Contain("targetObjectName").And.Contain("targetEntityId")
                        .And.Contain("targetInstanceId").And.Contain("targetPath"));
            }

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), response.ToString(Formatting.None));
            Assert.That(response["details"]?["semanticErrors"]?[0]?["error"]?.ToString(), Does.Contain("Provide one of"));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0));
        }

        [Test]
        public void AddListener_EachSingleLocator_PassesValidation()
        {
            var locators = new System.Collections.Generic.List<(string, JToken)>
            {
                ("targetObjectName", Twin), ("targetEntityId", Id(_left)), ("targetPath", "LeftRoot/Twin")
            };
            if (UnityObjectIdUtility.GetObjectId(_left.gameObject) != 0)
                locators.Add(("targetInstanceId", UnityObjectIdUtility.GetObjectId(_left.gameObject)));

            foreach (var (key, value) in locators)
            {
                var args = new JObject
                {
                    ["name"] = Emitter, ["componentName"] = nameof(EventProbe), ["eventName"] = "onFire",
                    ["targetComponentName"] = nameof(EventProbe), ["methodName"] = nameof(EventProbe.Ping), [key] = value
                };

                var dryRun = JObject.Parse(SkillRouter.DryRun("event_add_listener", args.ToString(Formatting.None)));
                Assert.That(dryRun["valid"]?.Value<bool>(), Is.True, $"{key}: {dryRun.ToString(Formatting.None)}");
            }
        }

        [Test]
        public void SetListener_EachSingleLocator_PassesValidation_AndNoLocatorIsRefused()
        {
            var locators = new System.Collections.Generic.List<(string, JToken)>
            {
                ("targetName", Twin), ("targetEntityId", Id(_left)), ("targetPath", "LeftRoot/Twin")
            };
            if (UnityObjectIdUtility.GetObjectId(_left.gameObject) != 0)
                locators.Add(("targetInstanceId", UnityObjectIdUtility.GetObjectId(_left.gameObject)));

            var baseArgs = new JObject
            {
                ["name"] = Emitter, ["componentName"] = nameof(EventProbe), ["eventName"] = "onFire", ["index"] = 0,
                ["targetComponentName"] = nameof(EventProbe), ["methodName"] = nameof(EventProbe.Ping)
            };
            foreach (var (key, value) in locators)
            {
                var args = (JObject)baseArgs.DeepClone();
                args[key] = value;
                var dryRun = JObject.Parse(SkillRouter.DryRun("event_set_listener", args.ToString(Formatting.None)));
                Assert.That(dryRun["valid"]?.Value<bool>(), Is.True, $"{key}: {dryRun.ToString(Formatting.None)}");
            }

            var none = JObject.Parse(SkillRouter.DryRun("event_set_listener", baseArgs.ToString(Formatting.None)));
            Assert.That(none["valid"]?.Value<bool>(), Is.False, none.ToString(Formatting.None));
            Assert.That(none["validation"]?["semanticErrors"]?[0]?["error"]?.ToString(), Does.Contain("Provide one of").And.Contain("targetName"));
        }

        [Test]
        public void AddListener_DirectCallWithoutALocator_IsMissingParamAndAddsNothing()
        {
            // Batch items and other in-process callers skip validation, so the skill body keeps its own guard.
            var json = AddListener(targetObjectName: null, methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), json.ToString(Formatting.None));
            Assert.That(json["error"]?.ToString(), Does.Contain("targetEntityId").And.Contain("targetPath"));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0));
        }

        [Test]
        public void AddListener_AnEmptyBodyIsStillRefusedByDryRun()
        {
            var dryRun = JObject.Parse(SkillRouter.DryRun("event_add_listener", "{}"));

            Assert.That(dryRun["valid"]?.Value<bool>(), Is.False, dryRun.ToString(Formatting.None));
        }

        [Test]
        public void AddListener_Metadata_DeclaresWhatItReturns()
        {
            Assert.That(SkillRouter.TryGetSkill("event_add_listener", out var info), Is.True);
            Assert.That(info.Outputs, Is.SupersetOf(new[] { "message", "index", "mode", "target", "targetEntityId", "targetPath", "method", "argType" }));
            Assert.That(info.ParameterNames, Is.SupersetOf(new[]
            {
                "targetInstanceId", "targetEntityId", "targetPath",
                "objectArgName", "objectArgInstanceId", "objectArgEntityId", "objectArgPath", "objectArgAssetPath", "objectArgType"
            }));

            var byName = info.Parameters.ToDictionary(p => p.Name);
            foreach (var documented in new[] { "argType", "targetInstanceId", "targetEntityId", "targetPath", "objectArgAssetPath", "objectArgType" })
                Assert.That(byName[documented].GetCustomAttributes(typeof(SkillParamAttribute), false), Is.Not.Empty, $"{documented} needs a [SkillParam] note.");
        }

        [Test]
        public void AddListenerBatch_ItemsCarryIdsPathsAndObjectArguments()
        {
            var items = new JArray
            {
                new JObject { ["targetEntityId"] = Id(_left), ["targetComponentName"] = nameof(EventProbe), ["methodName"] = nameof(EventProbe.Ping) },
                new JObject
                {
                    ["targetPath"] = "RightRoot/Twin", ["targetComponentName"] = nameof(EventProbe), ["methodName"] = nameof(EventProbe.TakeObject),
                    ["argType"] = "object", ["objectArgEntityId"] = Id(_left)
                },
                new JObject { ["targetEntityId"] = "999999999", ["targetComponentName"] = nameof(EventProbe), ["methodName"] = nameof(EventProbe.Ping) }
            };

            var json = ToJson(EventSkills.EventAddListenerBatch(Emitter, 0, null, nameof(EventProbe), "onFire", items.ToString(Formatting.None)));

            Assert.That(json["added"]?.Value<int>(), Is.EqualTo(2), json.ToString(Formatting.None));
            Assert.That(json["failed"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(2));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_left));
            Assert.That(_emitter.onFire.GetPersistentTarget(1), Is.EqualTo(_right));
            Assert.That(ObjectArgument(1), Is.EqualTo(_left.gameObject));
            Assert.That(json["results"][2]["error"]?.ToString(), Does.Contain("entityId"));
        }

        [Test]
        public void SetListener_ByTargetEntityId_RetargetsToThatSameNameObject()
        {
            UnityEventTools.AddPersistentListener(_emitter.onFire, _left.Ping);

            var json = ToJson(EventSkills.EventSetListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire", index: 0,
                targetName: Twin, targetEntityId: Id(_right), targetComponentName: nameof(EventProbe), methodName: nameof(EventProbe.Ping)));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_right));
            Assert.That(json["targetEntityId"]?.ToString(), Is.EqualTo(Id(_right)));
        }

        [Test]
        public void SetListener_UnknownTargetEntityId_LeavesTheListenerAlone()
        {
            UnityEventTools.AddPersistentListener(_emitter.onFire, _left.Ping);

            var json = ToJson(EventSkills.EventSetListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire", index: 0,
                targetName: Twin, targetEntityId: "999999999", targetComponentName: nameof(EventProbe), methodName: nameof(EventProbe.Ping)));

            Assert.That(json["error"]?.ToString(), Does.Contain("entityId"), json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_left));
        }

        [Test]
        public void SetListener_ObjectArgument_ByEntityId_IsStored()
        {
            UnityEventTools.AddPersistentListener(_emitter.onFire, _left.Ping);

            var json = ToJson(EventSkills.EventSetListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire", index: 0,
                targetName: Twin, targetEntityId: Id(_left), targetComponentName: nameof(EventProbe), methodName: nameof(EventProbe.TakeObject),
                argType: "object", objectReferenceEntityId: Id(_right)));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(ObjectArgument(0), Is.EqualTo(_right.gameObject));
        }

        // ---------- event_get_listeners ----------

        [Test]
        public void GetListeners_TellsSameNameTargetsApart()
        {
            UnityEventTools.AddPersistentListener(_emitter.onFire, _left.Ping);
            UnityEventTools.AddPersistentListener(_emitter.onFire, _right.Ping);

            var listeners = Listeners();

            Assert.That(listeners, Has.Count.EqualTo(2));
            Assert.That(listeners[0]["target"]?.ToString(), Is.EqualTo(Twin));
            Assert.That(listeners[1]["target"]?.ToString(), Is.EqualTo(Twin), "The name alone cannot tell them apart.");
            Assert.That(listeners[0]["targetEntityId"]?.ToString(), Is.EqualTo(Id(_left)));
            Assert.That(listeners[1]["targetEntityId"]?.ToString(), Is.EqualTo(Id(_right)));
            Assert.That(listeners[0]["targetPath"]?.ToString(), Is.EqualTo("LeftRoot/Twin"));
            Assert.That(listeners[1]["targetPath"]?.ToString(), Is.EqualTo("RightRoot/Twin"));
            Assert.That(listeners[0]["targetInstanceId"]?.Value<int>(), Is.EqualTo(UnityObjectIdUtility.GetObjectId(_left)));
            Assert.That(listeners[0]["targetAssetPath"].Type, Is.EqualTo(JTokenType.Null), "A scene object has no asset path.");
        }

        [Test]
        public void GetListeners_AnAssetTarget_ReportsItsAssetPathAndNoScenePath()
        {
            EnsureTempFolder();
            var asset = ScriptableObject.CreateInstance<EventProbeAsset>();
            var assetPath = TempRoot + "/Target.asset";
            AssetDatabase.CreateAsset(asset, assetPath);
            UnityEventTools.AddPersistentListener(_emitter.onFire, asset.Hit);

            var listener = Listeners().Single();

            Assert.That(listener["targetAssetPath"]?.ToString(), Is.EqualTo(assetPath));
            Assert.That(listener["targetPath"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(listener["targetEntityId"]?.ToString(), Is.EqualTo(Id(asset)));
            Assert.That(listener["targetType"]?.ToString(), Is.EqualTo(nameof(EventProbeAsset)));
        }

        [Test]
        public void GetListeners_ADestroyedTarget_ReportsNoIdsAndNoPaths()
        {
            UnityEventTools.AddPersistentListener(_emitter.onFire, _left.Ping);
            Object.DestroyImmediate(_left.gameObject);
            GameObjectFinder.InvalidateCache();

            var listener = Listeners().Single();

            Assert.That(listener["target"]?.ToString(), Is.EqualTo("null"));
            Assert.That(listener["targetEntityId"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(listener["targetPath"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(listener["targetAssetPath"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(listener["targetInstanceId"]?.Value<int>(), Is.EqualTo(0));
        }

        [Test]
        public void GetListeners_ThroughTheRouter_KeepsTheNewKeys()
        {
            UnityEventTools.AddPersistentListener(_emitter.onFire, _right.Ping);
            var args = new JObject { ["name"] = Emitter, ["componentName"] = nameof(EventProbe), ["eventName"] = "onFire" };

            var response = JObject.Parse(SkillRouter.Execute("event_get_listeners", args.ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            var listener = response["result"]["listeners"][0];
            Assert.That(listener["targetEntityId"]?.ToString(), Is.EqualTo(Id(_right)));
            Assert.That(listener["targetPath"]?.ToString(), Is.EqualTo("RightRoot/Twin"));
        }

        // ---------- helpers ----------

        private static EventProbe CreateTwin(string parentName)
        {
            var parent = new GameObject(parentName);
            var twin = new GameObject(Twin);
            twin.transform.SetParent(parent.transform, false);
            return twin.AddComponent<EventProbe>();
        }

        private JObject AddListener(string targetObjectName, string methodName, string targetComponentName = null,
            int targetInstanceId = 0, string targetEntityId = null, string targetPath = null,
            string argType = "void", bool boolArg = false,
            string objectArgEntityId = null, string objectArgPath = null, int objectArgInstanceId = 0,
            string objectArgAssetPath = null, string objectArgType = null)
        {
            var result = EventSkills.EventAddListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire",
                targetObjectName: targetObjectName, targetComponentName: targetComponentName, methodName: methodName,
                argType: argType, boolArg: boolArg,
                targetInstanceId: targetInstanceId, targetEntityId: targetEntityId, targetPath: targetPath,
                objectArgEntityId: objectArgEntityId, objectArgPath: objectArgPath, objectArgInstanceId: objectArgInstanceId,
                objectArgAssetPath: objectArgAssetPath, objectArgType: objectArgType);
            return ToJson(result);
        }

        private JArray Listeners()
        {
            var json = ToJson(EventSkills.EventGetListeners(name: Emitter, componentName: nameof(EventProbe), eventName: "onFire"));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            return (JArray)json["listeners"];
        }

        private Object ObjectArgument(int index)
        {
            var property = new SerializedObject(_emitter).FindProperty($"onFire.m_PersistentCalls.m_Calls.Array.data[{index}].m_Arguments.m_ObjectArgument");
            Assert.That(property, Is.Not.Null, "The persistent call has no object argument slot.");
            return property.objectReferenceValue;
        }

        private static Material CreateMaterialAsset()
        {
            EnsureTempFolder();
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Standard") ?? Shader.Find("UI/Default");
            Assert.That(shader, Is.Not.Null, "No built-in shader to build a material from.");
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, TempRoot + "/Argument.mat");
            return material;
        }

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.CreateFolder("Assets/Temp", "EventListenerTargetsTests");
        }

        private static string Id(Object obj) => UnityObjectIdUtility.GetEntityId(obj);

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
