#if COM_UNITY_MODULES_PARTICLESYSTEM
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// particle_get_sub_emitters and particle_set_sub_emitters(+_batch): whole-list replace with readback, every
    /// rejection named by its path with nothing written, cycles judged on the graph the call leaves behind (across a
    /// batch and in a dry run too), warnings instead of silent no-ops, and Undo / workflow restoring the references.
    /// </summary>
    [TestFixture]
    public class ParticleSubEmitterTests : ParticleTestBase
    {
        private const string ScenePath = AssetRoot + "/SubEmitterScene.unity";

        private static JObject Entry(ParticleSystem system, string type, string[] properties = null, double? probability = null)
        {
            var entry = new JObject { ["system"] = new JObject { ["entityId"] = Id(system.gameObject) }, ["type"] = type };
            if (properties != null)
                entry["properties"] = new JArray(properties.Cast<object>().ToArray());
            if (probability.HasValue)
                entry["emitProbability"] = probability.Value;
            return entry;
        }

        private static string List(params JObject[] entries) => new JArray(entries.Cast<object>().ToArray()).ToString(Formatting.None);

        private static ParticleSystem[] Children(ParticleSystem system) =>
            Enumerable.Range(0, system.subEmitters.subEmittersCount).Select(index => system.subEmitters.GetSubEmitterSystem(index)).ToArray();

        private static (ParticleSystem parent, ParticleSystem first, ParticleSystem second) MakeFamily(string name)
        {
            var parent = MakeSystem(name);
            return (parent, MakeSystem(name + "Sparks", parent.transform), MakeSystem(name + "Smoke", parent.transform));
        }

        private static string[] SemanticFields(JObject dryRun) =>
            (dryRun["validation"]?["semanticErrors"] as JArray)?.Select(error => error["field"]?.ToString()).ToArray() ?? new string[0];

        // ---------- read ----------

        [Test]
        public void GetSubEmitters_OfAFreshSystem_IsEmptyAndDisabled()
        {
            var system = MakeSystem("FreshSubFx");
            var json = Json(ParticleSkills.ParticleGetSubEmitters(entityId: Id(system.gameObject)));
            AssertSuccess(json);
            Assert.That(json["enabled"]?.Value<bool>(), Is.False);
            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(0));
            Assert.That((JArray)json["subEmitters"], Is.Empty);
        }

        [Test]
        public void GetSubEmitters_ReportsAMissingReference()
        {
            var (parent, sparks, _) = MakeFamily("Broken");
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: List(Entry(sparks, "Death")))));
            UnityEngine.Object.DestroyImmediate(sparks.gameObject);

            var json = Json(ParticleSkills.ParticleGetSubEmitters(entityId: Id(parent.gameObject)));
            AssertSuccess(json);
            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["subEmitters"][0]["missing"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["subEmitters"][0]["system"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(json["warnings"].Values<string>().Any(warning => warning.Contains("missing reference")), Is.True);

            var resend = Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: json["subEmitters"].ToString(Formatting.None)));
            AssertError(resend, "SEMANTIC_INVALID");
            StringAssert.Contains("missing reference", resend["error"]?.ToString());

            // Unity's API no longer counts the broken entry, but the stored list (the one the Inspector shows) still
            // holds it; a replace has to remove it too.
            Assert.That(parent.subEmitters.subEmittersCount, Is.EqualTo(0));
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: "[]")));
            using (var serialized = new SerializedObject(parent))
                Assert.That(serialized.FindProperty("SubModule.subEmitters").arraySize, Is.EqualTo(0), "The broken entry survived the replace.");
            Assert.That(Json(ParticleSkills.ParticleGetSubEmitters(entityId: Id(parent.gameObject)))["count"]?.Value<int>(), Is.EqualTo(0));
        }

        // ---------- write ----------

        [Test]
        public void SetSubEmitters_ReplacesTheList_ReadsItBack_AndAnEmptyArrayClearsIt()
        {
            var (parent, sparks, smoke) = MakeFamily("Rocket");
            var target = Id(parent.gameObject);
            var smokePath = GameObjectFinder.GetPath(smoke.gameObject);
            var json = Json(ParticleSkills.ParticleSetSubEmitters(entityId: target, subEmittersJson: List(
                Entry(sparks, "Death", new[] { "InheritColor", "InheritSize" }, 0.5),
                new JObject { ["system"] = smokePath, ["type"] = "Birth" })));
            AssertSuccess(json);

            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(Children(parent), Is.EqualTo(new[] { sparks, smoke }));
            var module = parent.subEmitters;
            Assert.That(module.GetSubEmitterType(0), Is.EqualTo(ParticleSystemSubEmitterType.Death));
            Assert.That(module.GetSubEmitterProperties(0), Is.EqualTo(ParticleSystemSubEmitterProperties.InheritColor | ParticleSystemSubEmitterProperties.InheritSize));
            Assert.That(module.GetSubEmitterEmitProbability(0), Is.EqualTo(0.5f).Within(1e-6));
            Assert.That(module.GetSubEmitterType(1), Is.EqualTo(ParticleSystemSubEmitterType.Birth));
            Assert.That(module.GetSubEmitterProperties(1), Is.EqualTo(ParticleSystemSubEmitterProperties.InheritNothing));
            Assert.That(module.GetSubEmitterEmitProbability(1), Is.EqualTo(1f));

            var entries = (JArray)json["subEmitters"];
            Assert.That(entries[0]["system"]["entityId"]?.ToString(), Is.EqualTo(Id(sparks.gameObject)));
            Assert.That(entries[0]["properties"].Values<string>(), Is.EqualTo(new[] { "InheritColor", "InheritSize" }));
            Assert.That(entries[1]["system"]["path"]?.ToString(), Is.EqualTo(smokePath));
            Assert.That(json["adjustments"], Is.Null, json.ToString(Formatting.None));

            // The readback is accepted back unchanged, and InheritEverything is a shorthand for all five flags.
            var read = Json(ParticleSkills.ParticleGetSubEmitters(entityId: target));
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: target, subEmittersJson: read["subEmitters"].ToString(Formatting.None))));
            Assert.That(Children(parent), Is.EqualTo(new[] { sparks, smoke }));
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: target, subEmittersJson: List(Entry(sparks, "Birth", new[] { "InheritEverything" })))));
            Assert.That(parent.subEmitters.GetSubEmitterProperties(0), Is.EqualTo(ParticleSystemSubEmitterProperties.InheritEverything));

            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: target, subEmittersJson: "[]")));
            Assert.That(parent.subEmitters.subEmittersCount, Is.EqualTo(0));
        }

        [Test]
        public void SetSubEmitters_NeverEnablesTheModule_ButSaysSo_AndConfigureEnablesIt()
        {
            var (parent, sparks, _) = MakeFamily("Quiet");
            var json = Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: List(Entry(sparks, "Death"))));
            AssertSuccess(json);
            Assert.That(parent.subEmitters.enabled, Is.False);
            Assert.That(json["warnings"].Values<string>().Any(warning => warning.Contains("modules.subEmitters is disabled")), Is.True, json.ToString(Formatting.None));

            var enabled = Json(ParticleSkills.ParticleConfigure(entityId: Id(parent.gameObject), configJson: Config(new JObject { ["subEmitters"] = new JObject { ["enabled"] = true } })));
            AssertSuccess(enabled);
            Assert.That(parent.subEmitters.enabled, Is.True);
            Assert.That(parent.subEmitters.subEmittersCount, Is.EqualTo(1));

            var module = Json(ParticleSkills.ParticleGetModule(entityId: Id(parent.gameObject), module: "subEmitters"));
            AssertSuccess(module);
            Assert.That(module["fields"]["subEmittersCount"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(module["collections"][0]["readWith"]?.ToString(), Is.EqualTo("particle_get_sub_emitters"));
        }

        [Test]
        public void SetSubEmitters_RejectsInvalidLists_WithTheirPaths_AndChangesNothing()
        {
            var (parent, sparks, smoke) = MakeFamily("Strict");
            var target = Id(parent.gameObject);
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: target, subEmittersJson: List(Entry(sparks, "Death")))));
            var plain = new GameObject("NoSystemHere");

            void Rejected(string list, string code, string path)
            {
                var json = Json(ParticleSkills.ParticleSetSubEmitters(entityId: target, subEmittersJson: list));
                AssertError(json, code);
                var paths = json["problems"].Select(problem => problem["path"]?.ToString()).ToArray();
                Assert.That(paths, Does.Contain(path), json.ToString(Formatting.None));
                Assert.That(Children(parent), Is.EqualTo(new[] { sparks }), "A rejected list changed the system: " + list);
                Assert.That(parent.subEmitters.GetSubEmitterType(0), Is.EqualTo(ParticleSystemSubEmitterType.Death));
            }

            Rejected(List(Entry(parent, "Birth")), "SEMANTIC_INVALID", "subEmittersJson[0].system");
            Rejected(List(Entry(sparks, "Death"), Entry(sparks, "Death")), "SEMANTIC_INVALID", "subEmittersJson[1]");
            Rejected(List(new JObject { ["system"] = new JObject { ["entityId"] = Id(smoke.gameObject) }, ["type"] = "Explode" }), "SEMANTIC_INVALID", "subEmittersJson[0].type");
            Rejected(List(Entry(smoke, "Birth", new[] { "InheritColor", "InheritEverything" })), "SEMANTIC_INVALID", "subEmittersJson[0].properties[1]");
            Rejected(List(Entry(smoke, "Birth", null, 1.5)), "SEMANTIC_INVALID", "subEmittersJson[0].emitProbability");
            Rejected(List(new JObject { ["system"] = new JObject { ["entityId"] = Id(plain) }, ["type"] = "Birth" }), "TARGET_NOT_FOUND", "subEmittersJson[0].system");
            Rejected(List(new JObject { ["system"] = new JObject { ["path"] = "Strict/DoesNotExist" }, ["type"] = "Birth" }), "TARGET_NOT_FOUND", "subEmittersJson[0].system");
            Rejected(List(new JObject { ["system"] = JValue.CreateNull(), ["type"] = "Birth" }), "SEMANTIC_INVALID", "subEmittersJson[0].system");
            Rejected(List(new JObject { ["system"] = new JObject { ["entityId"] = Id(smoke.gameObject) } }), "MISSING_PARAM", "subEmittersJson[0].type");
            Rejected(List(new JObject { ["system"] = new JObject { ["entityId"] = Id(smoke.gameObject) }, ["type"] = "Birth", ["probability"] = 1 }), "SEMANTIC_INVALID", "subEmittersJson[0].probability");
            Rejected(List(new JObject { ["system"] = new JObject { ["entityId"] = 5 }, ["type"] = "Birth" }), "TYPE_MISMATCH", "subEmittersJson[0].system.entityId");
            Rejected("{\"system\":\"x\"}", "TYPE_MISMATCH", "subEmittersJson");
            Rejected(new JArray(Enumerable.Range(0, 17).Select(_ => (object)Entry(smoke, "Birth")).ToArray()).ToString(Formatting.None), "SEMANTIC_INVALID", "subEmittersJson");
            Rejected("[{\"system\":\"Strict/StrictSmoke\",\"type\":\"Birth\",\"type\":\"Death\"}]", "INVALID_JSON", "subEmittersJson[0].type");
            AssertError(Json(ParticleSkills.ParticleSetSubEmitters(entityId: target)), "MISSING_PARAM");
        }

        [Test]
        public void SetSubEmitters_RefusesDirectAndIndirectCycles()
        {
            var a = MakeSystem("CycleA");
            var b = MakeSystem("CycleB", a.transform);
            var c = MakeSystem("CycleC", b.transform);
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(a.gameObject), subEmittersJson: List(Entry(b, "Death")))));
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(b.gameObject), subEmittersJson: List(Entry(c, "Death")))));

            var indirect = Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(c.gameObject), subEmittersJson: List(Entry(a, "Birth"))));
            AssertError(indirect, "SEMANTIC_INVALID");
            StringAssert.Contains("CycleA/CycleB/CycleC -> CycleA -> CycleA/CycleB -> CycleA/CycleB/CycleC", indirect["error"]?.ToString());
            Assert.That(Children(c), Is.Empty);

            var direct = Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(b.gameObject), subEmittersJson: List(Entry(c, "Death"), Entry(a, "Collision"))));
            AssertError(direct, "SEMANTIC_INVALID");
            Assert.That(direct["problems"][0]["path"]?.ToString(), Is.EqualTo("subEmittersJson[1].system"));
            Assert.That(Children(b), Is.EqualTo(new[] { c }));
        }

        [Test]
        public void SetSubEmitters_WarnsForNonChildrenAndTypesTheirModuleCannotFire_AndReparentsNothing()
        {
            var parent = MakeSystem("Warner");
            var outsider = MakeSystem("Outsider");
            var child = MakeSystem("WarnerChild", parent.transform);
            var json = Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: List(
                Entry(outsider, "Birth"), Entry(child, "Collision"), Entry(child, "Trigger"), Entry(child, "Manual"))));
            AssertSuccess(json);

            var warnings = json["warnings"].Values<string>().ToArray();
            Assert.That(warnings.Any(warning => warning.Contains("is not a child of")), Is.True, string.Join("\n", warnings));
            Assert.That(warnings.Any(warning => warning.Contains("Collision sub emitters")), Is.True);
            Assert.That(warnings.Any(warning => warning.Contains("Trigger sub emitters")), Is.True);
            Assert.That(warnings.Any(warning => warning.Contains("TriggerSubEmitter")), Is.True);
            Assert.That(outsider.transform.parent, Is.Null, "Nothing is reparented.");
            Assert.That(Children(parent), Is.EqualTo(new[] { outsider, child, child, child }));
        }

        [Test]
        public void SetSubEmitters_EntityIdPicksTheExactSameNameSystem()
        {
            var parent = MakeSystem("Twins");
            var first = MakeSystem("Twin", parent.transform);
            var second = MakeSystem("Twin", parent.transform);
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: List(Entry(second, "Death")))));
            Assert.That(Children(parent), Is.EqualTo(new[] { second }));
            Assert.That(Children(parent).Single(), Is.Not.SameAs(first));
        }

        [Test]
        public void FailureWhileApplying_RevertsTheListAndLeavesNoWorkflowResidue()
        {
            var (parent, sparks, smoke) = MakeFamily("Fragile");
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: List(Entry(sparks, "Death")))));
            WorkflowManager.BeginTask("particle-sub-failure", "test");
            ParticleSkills.AfterWriteHookForTests = _ => throw new System.InvalidOperationException("injected");
            var json = Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: List(Entry(smoke, "Birth"))));
            ParticleSkills.AfterWriteHookForTests = null;
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("INTERNAL"), json.ToString(Formatting.None));
            Assert.That(Children(parent), Is.EqualTo(new[] { sparks }));
            Assert.That(WorkflowManager.CurrentTask.snapshots, Is.Empty, "The failed write left a workflow snapshot.");
            WorkflowManager.AbortTask();
        }

        // ---------- batch ----------

        [Test]
        public void SetSubEmittersBatch_BuildsAChainInOneCall()
        {
            var rocket = MakeSystem("BatchRocket");
            var explosion = MakeSystem("Explosion", rocket.transform);
            var sparks = MakeSystem("Sparks", explosion.transform);
            var items = new JArray
            {
                new JObject { ["entityId"] = Id(rocket.gameObject), ["subEmittersJson"] = new JArray(Entry(explosion, "Death")) },
                new JObject { ["entityId"] = Id(explosion.gameObject), ["subEmittersJson"] = new JArray(Entry(sparks, "Death")) }
            };
            var json = Json(ParticleSkills.ParticleSetSubEmittersBatch(items.ToString(Formatting.None)));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(Children(rocket), Is.EqualTo(new[] { explosion }));
            Assert.That(Children(explosion), Is.EqualTo(new[] { sparks }));
        }

        [Test]
        public void SetSubEmittersBatch_RefusesACycleOnlyTheWholeBatchCloses_InDryRunToo_AndAppliesNothing()
        {
            var ping = MakeSystem("Ping");
            var pong = MakeSystem("Pong");
            var items = new JArray
            {
                new JObject { ["entityId"] = Id(ping.gameObject), ["subEmittersJson"] = new JArray(Entry(pong, "Death")) },
                new JObject { ["entityId"] = Id(pong.gameObject), ["subEmittersJson"] = new JArray(Entry(ping, "Death")) }
            };

            var dry = DryRun("particle_set_sub_emitters_batch", new JObject { ["items"] = items });
            Assert.That(dry["valid"]?.Value<bool>(), Is.False, dry.ToString(Formatting.None));
            Assert.That(dry.ToString(Formatting.None), Does.Contain("closes a sub-emitter cycle"));

            var json = Json(ParticleSkills.ParticleSetSubEmittersBatch(items.ToString(Formatting.None)));
            Assert.That(json["success"]?.Value<bool>(), Is.False, json.ToString(Formatting.None));
            Assert.That(json.ToString(Formatting.None), Does.Contain("closes a sub-emitter cycle"));
            Assert.That(Children(ping), Is.Empty);
            Assert.That(Children(pong), Is.Empty);

            // Each item alone is fine: the cycle exists only in the graph the whole batch leaves behind.
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(ping.gameObject), subEmittersJson: List(Entry(pong, "Death")))));
        }

        // ---------- dryRun ----------

        [Test]
        public void DryRun_PreviewsTheList_WithoutSideEffects_AndSharesTheVerdict()
        {
            var (parent, sparks, _) = MakeFamily("DrySub");
            int tasks = WorkflowManager.History.tasks.Count;
            int undoGroup = Undo.GetCurrentGroup();
            var dry = DryRun("particle_set_sub_emitters", new JObject
            {
                ["entityId"] = Id(parent.gameObject),
                ["subEmittersJson"] = new JArray(Entry(sparks, "Death"))
            });
            Assert.That(dry["valid"]?.Value<bool>(), Is.True, dry.ToString(Formatting.None));
            var change = dry["changes"]["modify"].First(entry => entry["field"]?.ToString() == "modules.subEmitters.subEmitters");
            Assert.That((JArray)change["before"], Is.Empty);
            Assert.That(change["after"][0]["system"]["entityId"]?.ToString(), Is.EqualTo(Id(sparks.gameObject)));
            Assert.That(parent.subEmitters.subEmittersCount, Is.EqualTo(0), "dryRun wrote the list.");
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroup), "dryRun opened an Undo group.");
            Assert.That(WorkflowManager.History.tasks.Count, Is.EqualTo(tasks), "dryRun recorded a workflow task.");

            var selfArgs = new JObject { ["entityId"] = Id(parent.gameObject), ["subEmittersJson"] = new JArray(Entry(parent, "Birth")) };
            var drySelf = DryRun("particle_set_sub_emitters", selfArgs);
            Assert.That(drySelf["valid"]?.Value<bool>(), Is.False);
            Assert.That(SemanticFields(drySelf), Does.Contain("subEmittersJson[0].system"), drySelf.ToString(Formatting.None));
            Assert.That(Execute("particle_set_sub_emitters", selfArgs)["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
        }

        [Test]
        public void DryRun_OfABatchRun_AcceptsASubEmitterAnEarlierStepCreates()
        {
            var parent = MakeSystem("PendingParent");
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                var create = DryRun("particle_create", new JObject { ["name"] = "PendingSparks", ["parentEntityId"] = Id(parent.gameObject) });
                Assert.That(create["valid"]?.Value<bool>(), Is.True, create.ToString(Formatting.None));
                var set = DryRun("particle_set_sub_emitters", new JObject
                {
                    ["entityId"] = Id(parent.gameObject),
                    ["subEmittersJson"] = new JArray(new JObject { ["system"] = new JObject { ["path"] = "PendingParent/PendingSparks" }, ["type"] = "Death" })
                });
                Assert.That(set["valid"]?.Value<bool>(), Is.True, set.ToString(Formatting.None));
                Assert.That(set["validation"]["warnings"].Values<string>().Any(warning => warning.Contains("earlier step")), Is.True, set.ToString(Formatting.None));
            }
            Assert.That(GameObject.Find("PendingSparks"), Is.Null, "Planning created nothing.");
        }

        // ---------- Undo and workflow ----------

        [Test]
        public void UnityUndoRedo_RestoreTheList()
        {
            var (parent, sparks, smoke) = MakeFamily("UndoSub");
            var target = Id(parent.gameObject);
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: target, subEmittersJson: List(Entry(sparks, "Death")))));
            Undo.IncrementCurrentGroup();
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: target, subEmittersJson: List(Entry(smoke, "Birth"), Entry(sparks, "Collision")))));
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            Assert.That(Children(parent), Is.EqualTo(new[] { sparks }));
            Assert.That(parent.subEmitters.GetSubEmitterType(0), Is.EqualTo(ParticleSystemSubEmitterType.Death));
            Undo.PerformRedo();
            Assert.That(Children(parent), Is.EqualTo(new[] { smoke, sparks }));
        }

        [Test]
        public void WorkflowUndoRedo_RestoreTheListAndItsReferences()
        {
            var (parent, sparks, smoke) = MakeFamily("TaskSub");
            WorkflowManager.BeginTask("particle-sub", "test");
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: List(
                Entry(sparks, "Death", new[] { "InheritColor" }), Entry(smoke, "Birth")))));
            WorkflowManager.EndTask();
            var taskId = WorkflowManager.History.tasks.Last().id;

            var undo = WorkflowManager.UndoTask(taskId);
            Assert.That(undo.success, Is.True, JsonConvert.SerializeObject(undo));
            Assert.That(parent.subEmitters.subEmittersCount, Is.EqualTo(0));

            var redo = WorkflowManager.RedoTask(taskId);
            Assert.That(redo.success, Is.True, JsonConvert.SerializeObject(redo));
            Assert.That(Children(parent), Is.EqualTo(new[] { sparks, smoke }));
            Assert.That(parent.subEmitters.GetSubEmitterProperties(0), Is.EqualTo(ParticleSystemSubEmitterProperties.InheritColor));
        }

        [Test]
        public void WorkflowUndo_AfterReopeningTheScene_ResolvesTheReferencesToTheReopenedObjects()
        {
            var (parent, sparks, smoke) = MakeFamily("SavedSub");
            EnsureAssetRoot();
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: List(Entry(sparks, "Death")))));
            Assert.That(EditorSceneManager.SaveScene(parent.gameObject.scene, ScenePath), Is.True);

            WorkflowManager.BeginTask("particle-sub-persistent", "test");
            AssertSuccess(Json(ParticleSkills.ParticleSetSubEmitters(entityId: Id(parent.gameObject), subEmittersJson: List(Entry(smoke, "Birth")))));
            WorkflowManager.EndTask();
            var taskId = WorkflowManager.History.tasks.Last().id;
            Assert.That(EditorSceneManager.SaveScene(parent.gameObject.scene), Is.True);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            WorkflowManager.ResetStateForTests();
            WorkflowManager.LoadHistory();
            GameObjectFinder.InvalidateCache();
            var reloaded = GameObject.Find("SavedSub").GetComponent<ParticleSystem>();
            var reloadedSparks = GameObject.Find("SavedSub/SavedSubSparks").GetComponent<ParticleSystem>();
            Assert.That(Children(reloaded).Single().name, Is.EqualTo("SavedSubSmoke"));

            var undo = WorkflowManager.UndoTask(taskId);
            Assert.That(undo.success, Is.True, JsonConvert.SerializeObject(undo));
            Assert.That(Children(reloaded), Is.EqualTo(new[] { reloadedSparks }), "The restored reference must name the reopened object.");
            Assert.That(reloaded.subEmitters.GetSubEmitterType(0), Is.EqualTo(ParticleSystemSubEmitterType.Death));
        }
    }
}
#endif

// Producer:Betsy
