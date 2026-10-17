using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Tests.Fixtures;
using Object = UnityEngine.Object;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// How event_add_listener / event_set_listener bind what they are pointed at. The first target locator given decides
    /// (entityId, instanceId, path, name) and one that finds nothing is an error, never a hit on a same-name object; a component's
    /// own id binds that exact component, so event_get_listeners -> event_set_listener round-trips even when a GameObject carries
    /// two components of a type; an asset named by its id is bound as it is; an Object argument is located the same way and
    /// the nearest overload takes it; and a call that fails on its input leaves no workflow snapshot behind.
    /// </summary>
    [TestFixture]
    public class EventListenerBindingTests
    {
        private const string TempRoot = "Assets/Temp/EventListenerBindingTests";
        private const string Emitter = "Emitter";
        private const string Twin = "Twin";
        private const string StaleId = "987654321";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private string _workflowRoot;
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

            // Workflow snapshots (and the file-store blobs they write) stay out of the project's real history.
            _workflowRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsEventBindingTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workflowRoot);
            WorkflowManager.OverrideHistoryFilePathForTests = Path.Combine(_workflowRoot, "workflow_history.json");
            WorkflowFileStore.OverrideStoreRootForTests = Path.Combine(_workflowRoot, "workflow_files");
            WorkflowManager.ResetStateForTests();

            _emitter = new GameObject(Emitter).AddComponent<EventProbe>();
            _left = CreateTwin("LeftRoot");
            _right = CreateTwin("RightRoot");
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            WorkflowManager.AbortTask();
            WorkflowManager.ResetStateForTests();
            WorkflowManager.OverrideHistoryFilePathForTests = null;
            WorkflowFileStore.OverrideStoreRootForTests = null;
            try { if (Directory.Exists(_workflowRoot)) Directory.Delete(_workflowRoot, true); } catch { }

            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
            if (AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.DeleteAsset(TempRoot);
        }

        // ---------- target: the first locator given decides ----------

        [Test]
        public void Target_PathThatFindsNothing_IsAnError_NotTheSameNameObject()
        {
            var json = AddListener(targetObjectName: Twin, targetPath: "Nowhere/Twin", methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));

            Assert.That(json["error"]?.ToString(), Does.Contain("Nowhere/Twin"), json.ToString(Formatting.None));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("TARGET_NOT_FOUND"));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0), "The name must not stand in for a path that names nothing.");
        }

        [Test]
        public void Target_StaleInstanceId_IsAnError_EvenWithAMatchingPathAndName()
        {
            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetInstanceId: int.Parse(StaleId),
                methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));

            Assert.That(json["error"]?.ToString(), Does.Contain("instanceId"), json.ToString(Formatting.None));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("TARGET_NOT_FOUND"));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0), "A stale id must not fall back to the path or the name.");
        }

        [Test]
        public void Target_TheFirstLocatorGivenDecides_InTheDocumentedOrder()
        {
            // entityId over instanceId, path and name.
            var byEntityId = AddListener(targetObjectName: Twin, targetPath: "RightRoot/Twin", targetInstanceId: LegacyId(_right),
                targetEntityId: Id(_left), methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));
            Assert.That(byEntityId["success"]?.Value<bool>(), Is.True, byEntityId.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(_left));

            // path over the name.
            var byPath = AddListener(targetObjectName: "LeftRoot", targetPath: "RightRoot/Twin", methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));
            Assert.That(byPath["success"]?.Value<bool>(), Is.True, byPath.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(1), Is.EqualTo(_right));

            if (LegacyId(_right) != 0)
            {
                // instanceId over the path and the name.
                var byInstanceId = AddListener(targetObjectName: "LeftRoot", targetPath: "LeftRoot/Twin", targetInstanceId: LegacyId(_right),
                    methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));
                Assert.That(byInstanceId["success"]?.Value<bool>(), Is.True, byInstanceId.ToString(Formatting.None));
                Assert.That(_emitter.onFire.GetPersistentTarget(2), Is.EqualTo(_right));
            }
        }

        [Test]
        public void Target_NameOfSeveralObjects_PicksTheFirstAndSaysSo()
        {
            var args = ListenerArgs(targetObjectName: Twin, methodName: nameof(EventProbe.Ping), targetComponentName: nameof(EventProbe));

            var response = JObject.Parse(SkillRouter.Execute("event_add_listener", args.ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(NotesOf(response), Has.Some.Contains("matches 2 objects"), response.ToString(Formatting.None));
        }

        [Test]
        public void Target_PartialNameOfSeveralObjects_ListsTheCandidates()
        {
            new GameObject("Room_A");
            new GameObject("Room_B");
            GameObjectFinder.InvalidateCache();

            var json = AddListener(targetObjectName: "Room", methodName: "SetActive", argType: "bool");

            Assert.That(json["error"]?.ToString(), Does.Contain("ambiguous"), json.ToString(Formatting.None));
            Assert.That(json["candidates"], Has.Count.EqualTo(2));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0));
        }

        // ---------- Object argument: the same rules ----------

        [Test]
        public void ObjectArgument_PathThatFindsNothing_IsAnError_NotTheNamedObject()
        {
            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object", objectArgName: Twin, objectArgPath: "Nowhere/Twin");

            Assert.That(json["error"]?.ToString(), Does.Contain("Nowhere/Twin"), json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0), "The name must not stand in for a path that names nothing.");
        }

        [Test]
        public void ObjectArgument_StaleInstanceId_IsAnError_EvenWithAMatchingPath()
        {
            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object", objectArgInstanceId: int.Parse(StaleId), objectArgPath: "RightRoot/Twin");

            Assert.That(json["error"]?.ToString(), Does.Contain("instanceId"), json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0));
        }

        [Test]
        public void ObjectArgument_TheFirstLocatorGivenDecides()
        {
            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object", objectArgEntityId: Id(_left), objectArgPath: "RightRoot/Twin");

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(ObjectArgument(0), Is.EqualTo(_left.gameObject));
        }

        [Test]
        public void ObjectArgument_PartialNameOfSeveralObjects_ListsTheCandidatesLikeTheTargetDoes()
        {
            new GameObject("Room_A");
            new GameObject("Room_B");
            GameObjectFinder.InvalidateCache();

            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object", objectArgName: "Room");

            Assert.That(json["error"]?.ToString(), Does.Contain("ambiguous"), json.ToString(Formatting.None));
            Assert.That(json["candidates"], Has.Count.EqualTo(2));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0));
        }

        [Test]
        public void ObjectArgument_NameOfSeveralObjects_PicksTheFirstAndSaysSo()
        {
            var args = ListenerArgs(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object");
            args["objectArgName"] = Twin;

            var response = JObject.Parse(SkillRouter.Execute("event_add_listener", args.ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(NotesOf(response), Has.Some.Contains("matches 2 objects"), response.ToString(Formatting.None));
        }

        [Test]
        public void SerializedReferenceWriter_StaleInstanceId_IsNoLongerRescuedByTheName()
        {
            var probe = new GameObject("Holder").AddComponent<PrefabAssetProbe>();
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentSetSerializedProperty(
                name: "Holder", componentType: nameof(PrefabAssetProbe), propertyPath: "target",
                referenceName: Twin, referenceInstanceId: int.Parse(StaleId)));

            Assert.That(json["error"]?.ToString(), Does.Contain("instanceId"), json.ToString(Formatting.None));
            Assert.That(probe.target, Is.Null);
        }

        // ---------- a component's own id binds that exact component ----------

        [Test]
        public void Target_ComponentEntityId_BindsThatComponent_WhenTwoShareAGameObject()
        {
            var (first, second) = CreateSpeaker();

            var json = AddListener(targetObjectName: "Speaker", targetEntityId: Id(second), targetComponentName: nameof(AudioSource), methodName: "Play");

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(second));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.Not.EqualTo(first));
            Assert.That(json["targetEntityId"]?.ToString(), Is.EqualTo(Id(second)));
        }

        [Test]
        public void GetListeners_ThenSetListener_RebindsTheSecondOfTwoAudioSources()
        {
            var (first, second) = CreateSpeaker();
            UnityEventTools.AddPersistentListener(_emitter.onFire, second.Play);

            var listener = Listeners().Single();
            Assert.That(listener["targetType"]?.ToString(), Is.EqualTo(nameof(AudioSource)));
            Assert.That(listener["targetEntityId"]?.ToString(), Is.EqualTo(Id(second)));

            var json = ToJson(EventSkills.EventSetListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire", index: 0,
                targetEntityId: (string)listener["targetEntityId"], targetComponentName: (string)listener["targetType"],
                methodName: "Stop"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(second), "The second AudioSource, not the first one GetComponent finds.");
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.Not.EqualTo(first));
            Assert.That(_emitter.onFire.GetPersistentMethodName(0), Is.EqualTo("Stop"));
        }

        [Test]
        public void GetListeners_ThenSetListener_RoundTripsTheLegacyInstanceId()
        {
            var (_, second) = CreateSpeaker();
            if (LegacyId(second) == 0) Assert.Ignore("This Unity version has no legacy instance ids.");
            UnityEventTools.AddPersistentListener(_emitter.onFire, second.Play);

            var listener = Listeners().Single();
            var json = ToJson(EventSkills.EventSetListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire", index: 0,
                targetInstanceId: (int)listener["targetInstanceId"], targetComponentName: (string)listener["targetType"],
                methodName: "Stop"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(second));
        }

        [Test]
        public void Target_GameObjectEntityId_WithAComponentName_BindsTheFirstOfTheType()
        {
            var (first, _) = CreateSpeaker();

            var json = AddListener(targetObjectName: "Speaker", targetEntityId: Id(first.gameObject), targetComponentName: nameof(AudioSource), methodName: "Play");

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(first));
        }

        [Test]
        public void Target_ComponentEntityId_WithoutAComponentName_BindsTheGameObject()
        {
            var (_, second) = CreateSpeaker();

            var json = AddListener(targetObjectName: "Speaker", targetEntityId: Id(second), methodName: "SetActive", argType: "bool", boolArg: true);

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(second.gameObject));
            Assert.That(json["targetType"]?.ToString(), Is.EqualTo("GameObject"));
        }

        [Test]
        public void Target_ComponentEntityId_WithAnotherComponentName_FindsThatComponent()
        {
            var (_, second) = CreateSpeaker();
            var probe = second.gameObject.AddComponent<EventProbe>();

            var json = AddListener(targetObjectName: "Speaker", targetEntityId: Id(second), targetComponentName: nameof(EventProbe), methodName: nameof(EventProbe.Ping));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(probe));
        }

        [Test]
        public void Target_ComponentEntityId_WithABaseTypeName_StillBindsThatExactComponent()
        {
            var (_, second) = CreateSpeaker();

            var json = AddListener(targetObjectName: "Speaker", targetEntityId: Id(second), targetComponentName: "Behaviour", methodName: "set_enabled", argType: "bool", boolArg: true);

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(second));
        }

        [Test]
        public void ObjectArgument_ComponentEntityId_WithItsType_IsThatExactComponent()
        {
            var (first, second) = CreateSpeaker();

            var byId = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeComponent), argType: "object", objectArgEntityId: Id(second), objectArgType: nameof(AudioSource));
            var byPath = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeComponent), argType: "object", objectArgPath: "Speaker", objectArgType: nameof(AudioSource));

            Assert.That(byId["success"]?.Value<bool>(), Is.True, byId.ToString(Formatting.None));
            Assert.That(byPath["success"]?.Value<bool>(), Is.True, byPath.ToString(Formatting.None));
            Assert.That(ObjectArgument(0), Is.EqualTo(second), "A component's own id names that component.");
            Assert.That(ObjectArgument(1), Is.EqualTo(first), "A GameObject locator names the first component of the type.");
        }

        // ---------- an asset named by its id is bound as it is ----------

        [Test]
        public void Target_AssetEntityId_BindsTheAsset_AndReportsItsAssetPath()
        {
            var (asset, assetPath) = CreateProbeAsset();

            var json = AddListener(targetObjectName: null, targetEntityId: Id(asset), methodName: nameof(EventProbeAsset.Hit));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(asset));
            Assert.That(json["targetType"]?.ToString(), Is.EqualTo(nameof(EventProbeAsset)));
            Assert.That(json["targetAssetPath"]?.ToString(), Is.EqualTo(assetPath));
            Assert.That(json["targetPath"].Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void GetListeners_ThenSetListener_RoundTripsAnAssetTarget()
        {
            var (asset, assetPath) = CreateProbeAsset();
            var (otherAsset, _) = CreateProbeAsset("Other");
            UnityEventTools.AddPersistentListener(_emitter.onFire, asset.Hit);

            var listener = Listeners().Single();
            Assert.That(listener["targetAssetPath"]?.ToString(), Is.EqualTo(assetPath));

            var rebound = ToJson(EventSkills.EventSetListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire", index: 0,
                targetEntityId: Id(otherAsset), targetComponentName: (string)listener["targetType"], methodName: nameof(EventProbeAsset.Hit)));

            Assert.That(rebound["success"]?.Value<bool>(), Is.True, rebound.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentTarget(0), Is.EqualTo(otherAsset));
        }

        [Test]
        public void Target_AssetEntityId_WithAnotherTypeName_IsRefused_AndWithItsTypeNameAccepted()
        {
            var (asset, _) = CreateProbeAsset();

            var wrong = AddListener(targetObjectName: null, targetEntityId: Id(asset), targetComponentName: "Rigidbody", methodName: nameof(EventProbeAsset.Hit));
            var asGameObject = AddListener(targetObjectName: null, targetEntityId: Id(asset), targetComponentName: "GameObject", methodName: nameof(EventProbeAsset.Hit));
            var right = AddListener(targetObjectName: null, targetEntityId: Id(asset), targetComponentName: nameof(EventProbeAsset), methodName: nameof(EventProbeAsset.Hit));

            Assert.That(wrong["error"]?.ToString(), Does.Contain(nameof(EventProbeAsset)).And.Contain("Rigidbody"), wrong.ToString(Formatting.None));
            Assert.That(asGameObject["error"], Is.Not.Null, "An asset is not a GameObject.");
            Assert.That(right["success"]?.Value<bool>(), Is.True, right.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(1));
        }

        [Test]
        public void ObjectArgument_AssetEntityId_IsStored()
        {
            var material = CreateMaterialAsset();

            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeMaterial), argType: "object", objectArgEntityId: Id(material));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(ObjectArgument(0), Is.EqualTo(material));
        }

        [Test]
        public void ObjectArgument_AssetEntityId_OfTheWrongType_IsRefused()
        {
            var (asset, _) = CreateProbeAsset();

            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeMaterial), argType: "object", objectArgEntityId: Id(asset), objectArgType: "Material");

            Assert.That(json["error"]?.ToString(), Does.Contain(nameof(EventProbeAsset)).And.Contain("Material"), json.ToString(Formatting.None));
            Assert.That(_emitter.onFire.GetPersistentEventCount(), Is.EqualTo(0));
        }

        // ---------- the nearest overload takes the argument ----------

        [Test]
        public void FindObjectArgumentMethod_ChoosesTheNearestParameterType_NotTheFirstDeclared()
        {
            // Pick(Object) is declared first, so a first-compatible rule would return it for every argument.
            var material = CreateMaterialAsset();

            Assert.That(ParameterOf(EventSkills.FindObjectArgumentMethod(typeof(EventProbe), nameof(EventProbe.Pick), _right.transform)), Is.EqualTo(typeof(Transform)));
            Assert.That(ParameterOf(EventSkills.FindObjectArgumentMethod(typeof(EventProbe), nameof(EventProbe.Pick), _right)), Is.EqualTo(typeof(Component)));
            Assert.That(ParameterOf(EventSkills.FindObjectArgumentMethod(typeof(EventProbe), nameof(EventProbe.Pick), material)), Is.EqualTo(typeof(Object)));
            Assert.That(EventSkills.FindObjectArgumentMethod(typeof(EventProbe), nameof(EventProbe.Pick), null), Is.Not.Null, "No argument: any overload will do.");
            Assert.That(EventSkills.FindObjectArgumentMethod(typeof(EventProbe), nameof(EventProbe.TakeMaterial), _right.transform), Is.Null, "A Transform is no Material.");
        }

        [TestCase("Transform", "Transform")]
        [TestCase("EventProbe", "Component")]
        [TestCase("Material", "Object")]
        public void ObjectArgument_TheListenerCallsTheNearestOverload(string argumentKind, string expectedOverload)
        {
            var args = ListenerArgs(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.Pick), argType: "object");
            args["mode"] = "EditorAndRuntime";
            switch (argumentKind)
            {
                case "Transform":
                    args["objectArgPath"] = "RightRoot/Twin";
                    args["objectArgType"] = "Transform";
                    break;
                case "EventProbe":
                    args["objectArgPath"] = "RightRoot/Twin";
                    args["objectArgType"] = nameof(EventProbe);
                    break;
                default:
                    args["objectArgAssetPath"] = AssetDatabase.GetAssetPath(CreateMaterialAsset());
                    args["objectArgType"] = "Material";
                    break;
            }

            var response = JObject.Parse(SkillRouter.Execute("event_add_listener", args.ToString(Formatting.None)));
            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));

            _emitter.onFire.Invoke();

            Assert.That(_left.lastPick, Is.EqualTo(expectedOverload), "The persistent call resolves the overload from the stored argument type.");
        }

        // ---------- a call that fails on its input records nothing ----------

        [Test]
        public void ABadMethod_LeavesNoSnapshot_InAddAndSet()
        {
            UnityEventTools.AddPersistentListener(_emitter.onFire, _left.Ping);
            WorkflowManager.BeginTask("bad-method", "test");

            var add = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe), methodName: "NoSuchMethod");
            var set = ToJson(EventSkills.EventSetListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire", index: 0,
                targetName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe), methodName: "NoSuchMethod"));

            Assert.That(add["error"]?.ToString(), Does.Contain("NoSuchMethod"), add.ToString(Formatting.None));
            Assert.That(set["error"]?.ToString(), Does.Contain("NoSuchMethod"), set.ToString(Formatting.None));
            Assert.That(WorkflowManager.CurrentTask.snapshots, Is.Empty, "Nothing was changed, so nothing may be snapshotted.");
        }

        [Test]
        public void ABadObjectArgument_LeavesNoSnapshot_InAddAndSet()
        {
            UnityEventTools.AddPersistentListener(_emitter.onFire, _left.Ping);
            WorkflowManager.BeginTask("bad-argument", "test");

            var add = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe),
                methodName: nameof(EventProbe.TakeObject), argType: "object", objectArgPath: "Nowhere");
            var set = ToJson(EventSkills.EventSetListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire", index: 0,
                targetName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe), methodName: nameof(EventProbe.TakeObject),
                argType: "object", objectReferencePath: "Nowhere"));

            Assert.That(add["error"], Is.Not.Null, add.ToString(Formatting.None));
            Assert.That(set["error"], Is.Not.Null, set.ToString(Formatting.None));
            Assert.That(WorkflowManager.CurrentTask.snapshots, Is.Empty);
        }

        [Test]
        public void AnUnsupportedArgType_LeavesNoSnapshot_InAddAndSet()
        {
            UnityEventTools.AddPersistentListener(_emitter.onFire, _left.Ping);
            WorkflowManager.BeginTask("bad-arg-type", "test");

            var add = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe), methodName: nameof(EventProbe.Ping), argType: "vector3");
            var set = ToJson(EventSkills.EventSetListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire", index: 0,
                targetName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe), methodName: nameof(EventProbe.Ping), argType: "vector3"));

            Assert.That(add["error"]?.ToString(), Does.Contain("Unsupported argType: vector3"), add.ToString(Formatting.None));
            Assert.That(set["error"]?.ToString(), Does.Contain("Unsupported argType: vector3"), set.ToString(Formatting.None));
            Assert.That(WorkflowManager.CurrentTask.snapshots, Is.Empty);
        }

        [Test]
        public void ASuccessfulAdd_IsSnapshotted()
        {
            WorkflowManager.BeginTask("good-add", "test");

            var json = AddListener(targetObjectName: Twin, targetPath: "LeftRoot/Twin", targetComponentName: nameof(EventProbe), methodName: nameof(EventProbe.Ping));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(WorkflowManager.CurrentTask.snapshots, Is.Not.Empty, "The control: a call that changes the event is recorded.");
        }

        // ---------- helpers ----------

        private static EventProbe CreateTwin(string parentName)
        {
            var parent = new GameObject(parentName);
            var twin = new GameObject(Twin);
            twin.transform.SetParent(parent.transform, false);
            return twin.AddComponent<EventProbe>();
        }

        // One GameObject carrying two AudioSources: GetComponent finds only the first.
        private static (AudioSource first, AudioSource second) CreateSpeaker()
        {
            var speaker = new GameObject("Speaker");
            var first = speaker.AddComponent<AudioSource>();
            var second = speaker.AddComponent<AudioSource>();
            GameObjectFinder.InvalidateCache();
            return (first, second);
        }

        private static (EventProbeAsset asset, string path) CreateProbeAsset(string name = "Target")
        {
            EnsureTempFolder();
            var asset = ScriptableObject.CreateInstance<EventProbeAsset>();
            var path = $"{TempRoot}/{name}.asset";
            AssetDatabase.CreateAsset(asset, path);
            return (asset, path);
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
                AssetDatabase.CreateFolder("Assets/Temp", "EventListenerBindingTests");
        }

        private static JObject ListenerArgs(string targetObjectName, string methodName, string targetComponentName = null,
            string targetPath = null, string targetEntityId = null, string argType = "void")
        {
            var args = new JObject
            {
                ["name"] = Emitter, ["componentName"] = nameof(EventProbe), ["eventName"] = "onFire",
                ["methodName"] = methodName, ["argType"] = argType
            };
            if (targetObjectName != null) args["targetObjectName"] = targetObjectName;
            if (targetComponentName != null) args["targetComponentName"] = targetComponentName;
            if (targetPath != null) args["targetPath"] = targetPath;
            if (targetEntityId != null) args["targetEntityId"] = targetEntityId;
            return args;
        }

        private JObject AddListener(string targetObjectName, string methodName, string targetComponentName = null,
            int targetInstanceId = 0, string targetEntityId = null, string targetPath = null,
            string argType = "void", bool boolArg = false,
            string objectArgName = null, string objectArgEntityId = null, string objectArgPath = null, int objectArgInstanceId = 0,
            string objectArgAssetPath = null, string objectArgType = null)
        {
            return ToJson(EventSkills.EventAddListener(
                name: Emitter, componentName: nameof(EventProbe), eventName: "onFire",
                targetObjectName: targetObjectName, targetComponentName: targetComponentName, methodName: methodName,
                argType: argType, boolArg: boolArg,
                targetInstanceId: targetInstanceId, targetEntityId: targetEntityId, targetPath: targetPath,
                objectArgName: objectArgName, objectArgInstanceId: objectArgInstanceId, objectArgEntityId: objectArgEntityId,
                objectArgPath: objectArgPath, objectArgAssetPath: objectArgAssetPath, objectArgType: objectArgType));
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

        private static string[] NotesOf(JObject response) =>
            response[SkillRouter.ResolutionNotesKey] is JArray notes ? notes.Select(note => note.ToString()).ToArray() : Array.Empty<string>();

        private static System.Type ParameterOf(System.Reflection.MethodInfo method) => method?.GetParameters()[0].ParameterType;

        private static int LegacyId(Object obj) => UnityObjectIdUtility.GetObjectId(obj);

        private static string Id(Object obj) => UnityObjectIdUtility.GetEntityId(obj);

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
