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
    /// The particle module through the router: dryRun shares the execution verdict and has no side effect, errors carry
    /// item/field paths, duplicate keys in a nested body are refused, the semantic diff names the particle components,
    /// the missing-module seam answers MISSING_PACKAGE everywhere, entityId locators, discovery and metadata.
    /// </summary>
    [TestFixture]
    public class ParticleRouterTests : ParticleTestBase
    {
        private const string ScenePath = AssetRoot + "/ParticleRouterScene.unity";

        private static readonly string[] AllSkills =
        {
            "particle_get_capabilities", "particle_find_all", "particle_get_info", "particle_get_info_batch", "particle_get_module",
            "particle_get_renderer", "particle_get_bursts", "particle_create", "particle_create_batch", "particle_add_system",
            "particle_add_system_batch", "particle_configure", "particle_configure_batch", "particle_set_renderer",
            "particle_set_renderer_batch", "particle_set_bursts", "particle_set_bursts_batch",
            "particle_get_sub_emitters", "particle_set_sub_emitters", "particle_set_sub_emitters_batch", "particle_analyze"
        };

        private static JObject ConfigObject(JObject modules) => JObject.Parse(Config(modules));

        private static string[] SemanticFields(JObject dryRun) =>
            (dryRun["validation"]?["semanticErrors"] as JArray)?.Select(error => error["field"]?.ToString()).ToArray() ?? new string[0];

        [Test]
        public void DryRun_PreviewsBeforeAndAfter_WithoutAnySideEffect()
        {
            var system = MakeSystem("DryFx");
            var scene = system.gameObject.scene;
            EnsureAssetRoot();
            Assert.That(EditorSceneManager.SaveScene(scene, ScenePath), Is.True);
            var before = Json(ParticleSkills.ParticleGetInfo(entityId: Id(system.gameObject), includeModules: true));
            int undoGroup = Undo.GetCurrentGroup();
            int tasks = WorkflowManager.History.tasks.Count;

            var dry = DryRun("particle_configure", new JObject
            {
                ["entityId"] = Id(system.gameObject),
                ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["duration"] = 3 }, ["emission"] = new JObject { ["rateOverTime"] = 25 } })
            });

            Assert.That(dry["valid"]?.Value<bool>(), Is.True, dry.ToString(Formatting.None));
            var modify = (JArray)dry["changes"]["modify"];
            var duration = modify.First(entry => entry["field"]?.ToString() == "modules.main.duration");
            Assert.That(duration["before"]?.Value<float>(), Is.EqualTo(5f));
            Assert.That(duration["after"]?.Value<float>(), Is.EqualTo(3f));
            Assert.That(duration["component"]?.ToString(), Is.EqualTo("ParticleSystem"));

            var after = Json(ParticleSkills.ParticleGetInfo(entityId: Id(system.gameObject), includeModules: true));
            Assert.That(JToken.DeepEquals(before, after), Is.True, "dryRun wrote to the system.");
            Assert.That(scene.isDirty, Is.False, "dryRun dirtied the scene.");
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroup), "dryRun opened an Undo group.");
            Assert.That(WorkflowManager.CurrentTask, Is.Null);
            Assert.That(WorkflowManager.History.tasks.Count, Is.EqualTo(tasks), "dryRun recorded a workflow task.");
        }

        [Test]
        public void DryRun_And_Execute_ReachTheSameVerdict_WithFieldPaths()
        {
            var system = MakeSystem("VerdictFx");
            var args = new JObject
            {
                ["entityId"] = Id(system.gameObject),
                ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["startLifetime"] = JObject.Parse("{\"mode\":\"TwoConstants\",\"min\":2,\"max\":1}") } })
            };
            var dry = DryRun("particle_configure", args);
            Assert.That(dry["valid"]?.Value<bool>(), Is.False);
            Assert.That(SemanticFields(dry), Does.Contain("configJson.modules.main.startLifetime.min"), dry.ToString(Formatting.None));

            var executed = Execute("particle_configure", args);
            Assert.That(executed["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), executed.ToString(Formatting.None));
            Assert.That(system.main.startLifetime.mode, Is.EqualTo(ParticleSystemCurveMode.Constant));
        }

        [Test]
        public void DryRun_Batch_NamesTheItemAndField()
        {
            var a = MakeSystem("DryBatchA");
            var b = MakeSystem("DryBatchB");
            var dry = DryRun("particle_configure_batch", new JObject
            {
                ["items"] = new JArray(
                    new JObject { ["entityId"] = Id(a.gameObject), ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["loop"] = false } }) },
                    new JObject { ["entityId"] = Id(b.gameObject), ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["duration"] = 0 } }) })
            });
            Assert.That(dry["valid"]?.Value<bool>(), Is.False);
            Assert.That(SemanticFields(dry), Does.Contain("items[1].configJson.modules.main.duration"), dry.ToString(Formatting.None));
            Assert.That(a.main.loop, Is.True);
        }

        [Test]
        public void DryRun_OfABatchRun_SeesASystemAnEarlierStepCreates()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                var create = DryRun("particle_create", new JObject { ["name"] = "PlannedFx" });
                Assert.That(create["valid"]?.Value<bool>(), Is.True, create.ToString(Formatting.None));
                Assert.That(create["changes"]["create"][0]["predictedPath"]?.ToString(), Is.EqualTo("PlannedFx"));

                var configure = DryRun("particle_configure", new JObject
                {
                    ["name"] = "PlannedFx",
                    ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["duration"] = 2 } })
                });
                Assert.That(configure["valid"]?.Value<bool>(), Is.True, configure.ToString(Formatting.None));
                Assert.That(configure["validation"]["warnings"]?.Values<string>().Any(warning => warning.Contains("earlier step")), Is.True);
            }
            Assert.That(GameObject.Find("PlannedFx"), Is.Null, "Planning created nothing.");
        }

        [Test]
        public void Execute_RefusesDuplicateKeysInANestedConfigObject()
        {
            var system = MakeSystem("DupFx");
            var body = "{\"entityId\":\"" + Id(system.gameObject) + "\",\"configJson\":{\"schemaVersion\":1,\"modules\":{\"main\":{\"duration\":2,\"duration\":3}}}}";
            var executed = JObject.Parse(SkillRouter.Execute("particle_configure", body));
            Assert.That(executed["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), executed.ToString(Formatting.None));
            StringAssert.Contains("Duplicate key", executed["error"]?.ToString());
            Assert.That(system.main.duration, Is.EqualTo(5f));
        }

        [Test]
        public void Execute_DiffNamesTheParticleSystemAndRenderer()
        {
            var system = MakeSystem("DiffFx");
            var configured = ExecuteWithDiff("particle_configure", new JObject
            {
                ["entityId"] = Id(system.gameObject),
                ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["duration"] = 4 } })
            });
            Assert.That(configured["status"]?.ToString(), Is.EqualTo("success"), configured.ToString(Formatting.None));
            var changedTypes = configured["sceneDiff"]["changed"].Select(entry => entry["target"]["type"]?.ToString()).ToArray();
            Assert.That(changedTypes, Does.Contain("ParticleSystem"), configured["sceneDiff"].ToString(Formatting.None));

            var renderer = ExecuteWithDiff("particle_set_renderer", new JObject
            {
                ["entityId"] = Id(system.gameObject),
                ["configJson"] = JObject.Parse(RendererConfig(new JObject { ["sortingOrder"] = 2 }))
            });
            var rendererTypes = renderer["sceneDiff"]["changed"].Select(entry => entry["target"]["type"]?.ToString()).ToArray();
            Assert.That(rendererTypes, Does.Contain("ParticleSystemRenderer"), renderer["sceneDiff"].ToString(Formatting.None));

            var host = new GameObject("DiffHost");
            var added = ExecuteWithDiff("particle_add_system", new JObject { ["entityId"] = Id(host) });
            var addedTypes = added["sceneDiff"]["added"].Select(entry => entry["type"]?.ToString()).ToArray();
            Assert.That(addedTypes, Is.SupersetOf(new[] { "ParticleSystem", "ParticleSystemRenderer" }), added["sceneDiff"].ToString(Formatting.None));
        }

        [Test]
        public void TransactionalBatch_LaterFailure_RemovesTheCreatedSystemAndRestoresTheConfiguredOne()
        {
            var existing = MakeSystem("TxExisting");
            var steps = new JArray
            {
                new JObject { ["skill"] = "particle_create", ["args"] = new JObject { ["name"] = "TxCreated", ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["duration"] = 2 } }) } },
                new JObject { ["skill"] = "particle_configure", ["args"] = new JObject { ["entityId"] = Id(existing.gameObject), ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["maxParticles"] = 9 } }) } },
                new JObject { ["skill"] = "particle_set_bursts", ["args"] = new JObject { ["entityId"] = Id(existing.gameObject), ["burstsJson"] = JArray.Parse("[{\"time\":0,\"count\":3}]") } },
                new JObject { ["skill"] = "particle_configure", ["args"] = new JObject { ["name"] = "TxDoesNotExist", ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["loop"] = false } }) } },
            };
            var result = SkillsHttpServer.ExecuteBatchCore(steps, new JObject(), continueOnError: false, dryRun: false, transactional: true, agentId: "tests");

            Assert.That(result["rolledBack"]?.Value<bool>(), Is.True, result.ToString(Formatting.None));
            Assert.That(result["results"][0]["status"]?.ToString(), Is.EqualTo("rolled_back"));
            GameObjectFinder.InvalidateCache();
            Assert.That(GameObject.Find("TxCreated"), Is.Null, "The created system is removed.");
            Assert.That(existing.main.maxParticles, Is.EqualTo(1000), "The configured system is restored.");
            Assert.That(existing.emission.burstCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_IsWorkflowTracked_AndUndoable()
        {
            var system = MakeSystem("TrackedFx");
            var executed = Execute("particle_configure", new JObject
            {
                ["entityId"] = Id(system.gameObject),
                ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["maxParticles"] = 77 } })
            });
            Assert.That(executed["status"]?.ToString(), Is.EqualTo("success"), executed.ToString(Formatting.None));
            var task = WorkflowManager.History.tasks.Last();
            Assert.That(task.tag, Is.EqualTo("particle_configure"));
            Assert.That(WorkflowManager.UndoTask(task.id).success, Is.True);
            Assert.That(system.main.maxParticles, Is.EqualTo(1000));
        }

        [Test]
        public void MissingModuleSeam_AnswersMissingPackage_AndChangesNothing()
        {
            var system = MakeSystem("SeamFx");
            ParticleSkills.SimulateMissingModuleForTests = true;

            var direct = Json(ParticleSkills.ParticleConfigure(entityId: Id(system.gameObject), configJson: Config(new JObject { ["main"] = new JObject { ["duration"] = 2 } })));
            AssertError(direct, "MISSING_PACKAGE");
            Assert.That(direct["requiredPackage"]?.ToString(), Is.EqualTo("com.unity.modules.particlesystem"));

            var args = new JObject { ["entityId"] = Id(system.gameObject), ["configJson"] = ConfigObject(new JObject { ["main"] = new JObject { ["duration"] = 2 } }) };
            Assert.That(Execute("particle_configure", args)["errorCode"]?.ToString(), Is.EqualTo("MISSING_PACKAGE"));
            var dry = DryRun("particle_configure", args);
            Assert.That(dry["valid"]?.Value<bool>(), Is.False);
            Assert.That(dry["validation"]["missingPackages"]?.Values<string>(), Does.Contain("com.unity.modules.particlesystem"));
            Assert.That(Execute("particle_get_info", new JObject { ["entityId"] = Id(system.gameObject) })["errorCode"]?.ToString(), Is.EqualTo("MISSING_PACKAGE"));
            Assert.That(Execute("particle_create", new JObject { ["name"] = "Nope" })["errorCode"]?.ToString(), Is.EqualTo("MISSING_PACKAGE"));
            Assert.That(GameObject.Find("Nope"), Is.Null);

            var capabilities = Json(ParticleSkills.ParticleGetCapabilities());
            Assert.That(capabilities["success"]?.Value<bool>(), Is.True);
            Assert.That(capabilities["available"]?.Value<bool>(), Is.False);
            Assert.That(capabilities["unavailableReason"]?.ToString(), Does.Contain("com.unity.modules.particlesystem"));

            foreach (var skill in AllSkills.Where(name => name != "particle_get_capabilities"))
            {
                var method = SkillRouter.GetAllSkillsSnapshotUnfiltered().First(info => info.Name == skill).Method;
                var defaults = method.GetParameters().Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
                var result = Json(method.Invoke(null, defaults));
                Assert.That(result["errorCode"]?.ToString(), Is.EqualTo("MISSING_PACKAGE"), skill);
            }
            Assert.That(system.main.duration, Is.EqualTo(5f));
        }

        [Test]
        public void EntityIdLocators_PickTheExactObject()
        {
            var first = MakeSystem("Twin");
            var second = MakeSystem("Twin");
            var json = Json(ParticleSkills.ParticleConfigure(entityId: Id(second.gameObject), configJson: Config(new JObject { ["main"] = new JObject { ["maxParticles"] = 5 } })));
            AssertSuccess(json);
            Assert.That(second.main.maxParticles, Is.EqualTo(5));
            Assert.That(first.main.maxParticles, Is.EqualTo(1000));
            Assert.That(json["entityId"]?.ToString(), Is.EqualTo(Id(second.gameObject)));

            var stale = MakeSystem("Gone");
            var staleId = Id(stale.gameObject);
            Object.DestroyImmediate(stale.gameObject);
            AssertError(Json(ParticleSkills.ParticleGetInfo(entityId: staleId)), "TARGET_NOT_FOUND");
            AssertError(Json(ParticleSkills.ParticleConfigure(entityId: "0", configJson: Config(new JObject { ["main"] = new JObject { ["loop"] = false } }))), "MISSING_PARAM");

            var viaRouter = Execute("particle_get_bursts", new JObject { ["entityId"] = Id(first.gameObject) });
            Assert.That(viaRouter["status"]?.ToString(), Is.EqualTo("success"), viaRouter.ToString(Formatting.None));
#if !UNITY_6000_4_OR_NEWER
            var byInstanceId = Json(ParticleSkills.ParticleGetInfo(instanceId: first.gameObject.GetInstanceID()));
            Assert.That(byInstanceId["entityId"]?.ToString(), Is.EqualTo(Id(first.gameObject)));
#endif
        }

        [Test]
        public void Recommend_RoutesParticleIntents()
        {
            var savedTelemetry = SkillTelemetryService.Enabled;
            SkillTelemetryService.Enabled = false;
            try
            {
                string[] Top(string intent, int n) => ((JArray)JObject.Parse(SkillRouter.GetRecommendations("?intent=" + intent + "&topN=10"))["results"])
                    .Take(n).Select(result => result["name"]?.ToString()).ToArray();

                Assert.That(Top("create+particle+system", 3), Does.Contain("particle_create"));
                Assert.That(Top("configure+particle+emission+rate", 3), Does.Contain("particle_configure"));
                Assert.That(Top("particle+burst", 5).Any(name => name.StartsWith("particle_") && name.Contains("burst")), Is.True);
                Assert.That(Top(System.Uri.EscapeDataString("创建粒子"), 3).Any(name => name.StartsWith("particle_")), Is.True);
            }
            finally
            {
                SkillTelemetryService.Enabled = savedTelemetry;
            }
        }

        [Test]
        public void Metadata_CategoryModesAndSurfaces()
        {
            foreach (var name in AllSkills)
            {
                Assert.That(SkillRouter.TryGetSkill(name, out var skill), Is.True, name);
                Assert.That(skill.Category, Is.EqualTo(SkillCategory.Particle), name);
                if (skill.ReadOnly)
                {
                    Assert.That(skill.Mode, Is.EqualTo(SkillMode.SemiAuto), name);
                    Assert.That(skill.MutatesScene, Is.False, name);
                }
                else
                {
                    Assert.That(skill.TracksWorkflow && skill.MutatesScene, Is.True, name);
                    Assert.That(skill.MayEnterPlayMode, Is.False, name);
                }
            }
            Assert.That(SkillRouter.GetAllSkillsSnapshotUnfiltered().Count(skill => skill.Category == SkillCategory.Particle && !ParticleSkills.IsPlaybackSkill(skill.Name)), Is.EqualTo(AllSkills.Length),
                "Every configuration particle skill is listed here; the playback skills are covered by ParticleRuntimeTests.");

            var saved = SkillsSurfaceProfile.Current;
            try
            {
                SkillsSurfaceProfile.Current = SurfaceProfileKind.NoSceneAuthoring;
                SkillRouter.TryGetSkill("particle_configure", out var write);
                SkillRouter.TryGetSkill("particle_get_info", out var read);
                Assert.That(SkillsSurfaceProfile.IsExcluded(write), Is.True);
                Assert.That(SkillsSurfaceProfile.IsExcluded(read), Is.False);
            }
            finally
            {
                SkillsSurfaceProfile.Current = saved;
            }
        }
    }
}
#endif

// Producer:Betsy
