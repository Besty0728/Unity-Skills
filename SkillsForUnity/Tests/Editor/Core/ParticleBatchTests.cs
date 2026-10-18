#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The batch contract shared by every particle write batch: every item is validated before anything is written, an
    /// invalid item means nothing is applied, a failure while applying reverts the items already applied, and the
    /// standard BatchExecutor envelope reports it (rolledBack / revertedCount / reverted items).
    /// </summary>
    [TestFixture]
    public class ParticleBatchTests : ParticleTestBase
    {
        private ParticleSystem _first;
        private ParticleSystem _second;

        [SetUp]
        public void CreateSystems()
        {
            _first = MakeSystem("BatchA");
            _second = MakeSystem("BatchB");
        }

        private string Items(params JObject[] items) => new JArray(items).ToString(Formatting.None);

        private JObject Item(ParticleSystem system, JObject modules) =>
            new JObject { ["entityId"] = Id(system.gameObject), ["configJson"] = JObject.Parse(Config(modules)) };

        [Test]
        public void ConfigureBatch_AppliesEveryValidItem()
        {
            var json = Json(ParticleSkills.ParticleConfigureBatch(Items(
                Item(_first, new JObject { ["main"] = new JObject { ["maxParticles"] = 50 } }),
                Item(_second, new JObject { ["emission"] = new JObject { ["rateOverTime"] = 3 } }))));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["successCount"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(_first.main.maxParticles, Is.EqualTo(50));
            Assert.That(_second.emission.rateOverTime.constant, Is.EqualTo(3f));
            Assert.That(json["results"][0]["target"]?.ToString(), Is.EqualTo("BatchA"));
            Assert.That(json["results"][1]["state"]["modules"]["emission"]["rateOverTime"]["constant"]?.Value<float>(), Is.EqualTo(3f));
        }

        [Test]
        public void ConfigureBatch_OneInvalidItem_AppliesNothing_AndNamesThePath()
        {
            var json = Json(ParticleSkills.ParticleConfigureBatch(Items(
                Item(_first, new JObject { ["main"] = new JObject { ["maxParticles"] = 50 } }),
                Item(_second, new JObject { ["main"] = new JObject { ["duration"] = 0 } }))));
            Assert.That(json["success"]?.Value<bool>(), Is.False);
            Assert.That(json["rolledBack"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["results"][0]["reverted"]?.Value<bool>(), Is.True);
            Assert.That(json["results"][1]["problems"][0]["path"]?.ToString(), Is.EqualTo("items[1].configJson.modules.main.duration"));
            Assert.That(_first.main.maxParticles, Is.EqualTo(1000), "The valid item was validated but never applied.");
        }

        [Test]
        public void ConfigureBatch_FailureWhileApplyingTheSecondItem_RevertsTheFirst()
        {
            int writes = 0;
            ParticleSkills.AfterWriteHookForTests = write =>
            {
                if (++writes == 2)
                    throw new InvalidOperationException("injected failure");
            };
            var json = Json(ParticleSkills.ParticleConfigureBatch(Items(
                Item(_first, new JObject { ["main"] = new JObject { ["maxParticles"] = 50 } }),
                Item(_second, new JObject { ["main"] = new JObject { ["maxParticles"] = 60 } }))));
            Assert.That(json["rolledBack"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["revertedCount"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(_first.main.maxParticles, Is.EqualTo(1000), "Item 0 was applied, then rolled back with the batch.");
            Assert.That(_second.main.maxParticles, Is.EqualTo(1000));
        }

        [Test]
        public void Batches_RejectUnknownItemKeys_AndTwoItemsOnOneTarget()
        {
            var typo = new JObject { ["entityId"] = Id(_first.gameObject), ["confgJson"] = JObject.Parse(Config(new JObject { ["main"] = new JObject { ["loop"] = false } })) };
            var json = Json(ParticleSkills.ParticleConfigureBatch(Items(typo)));
            Assert.That(json["success"]?.Value<bool>(), Is.False);
            StringAssert.Contains("did you mean 'configJson'", json["results"][0]["error"]?.ToString());

            var duplicate = Json(ParticleSkills.ParticleConfigureBatch(Items(
                Item(_first, new JObject { ["main"] = new JObject { ["loop"] = false } }),
                Item(_first, new JObject { ["main"] = new JObject { ["prewarm"] = true } }))));
            Assert.That(duplicate["success"]?.Value<bool>(), Is.False);
            StringAssert.Contains("same object as items[0]", duplicate["results"][1]["error"]?.ToString());
            Assert.That(_first.main.loop, Is.True);
        }

        [Test]
        public void Batches_RejectMalformedItemLists()
        {
            AssertError(Json(ParticleSkills.ParticleConfigureBatch("[]")), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleConfigureBatch("{\"entityId\":\"1\"}")), "TYPE_MISMATCH");
            AssertError(Json(ParticleSkills.ParticleConfigureBatch("[1, 2]")), "TYPE_MISMATCH");
            AssertError(Json(ParticleSkills.ParticleConfigureBatch("[{\"name\":\"a\",\"name\":\"b\"}]")), "INVALID_JSON");
            var tooMany = new JArray(Enumerable.Range(0, 101).Select(i => new JObject { ["name"] = "x" + i })).ToString(Formatting.None);
            AssertError(Json(ParticleSkills.ParticleConfigureBatch(tooMany)), "SEMANTIC_INVALID");
        }

        [Test]
        public void CreateBatch_IsAllOrNothing()
        {
            var json = Json(ParticleSkills.ParticleCreateBatch(Items(
                new JObject { ["name"] = "Made1" },
                new JObject { ["name"] = "Made2", ["configJson"] = JObject.Parse(Config(new JObject { ["main"] = new JObject { ["duration"] = -5 } })) })));
            Assert.That(json["success"]?.Value<bool>(), Is.False);
            Assert.That(GameObject.Find("Made1"), Is.Null, "No object is created when any item is invalid.");

            var ok = Json(ParticleSkills.ParticleCreateBatch(Items(new JObject { ["name"] = "Made1" }, new JObject { ["name"] = "Made2", ["x"] = 4 })));
            Assert.That(ok["successCount"]?.Value<int>(), Is.EqualTo(2), ok.ToString(Formatting.None));
            Assert.That(GameObject.Find("Made2").transform.position.x, Is.EqualTo(4f));
        }

        [Test]
        public void AddRendererAndBurstBatches_Apply()
        {
            var host = new GameObject("BatchHost");
            var add = Json(ParticleSkills.ParticleAddSystemBatch(Items(new JObject { ["entityId"] = Id(host) })));
            Assert.That(add["success"]?.Value<bool>(), Is.True, add.ToString(Formatting.None));
            Assert.That(host.GetComponent<ParticleSystem>(), Is.Not.Null);

            var renderer = Json(ParticleSkills.ParticleSetRendererBatch(Items(
                new JObject { ["entityId"] = Id(_first.gameObject), ["configJson"] = JObject.Parse(RendererConfig(new JObject { ["sortingOrder"] = 3 })) },
                new JObject { ["entityId"] = Id(_second.gameObject), ["configJson"] = JObject.Parse(RendererConfig(new JObject { ["sortingOrder"] = 4 })) })));
            Assert.That(renderer["successCount"]?.Value<int>(), Is.EqualTo(2), renderer.ToString(Formatting.None));
            Assert.That(_second.GetComponent<ParticleSystemRenderer>().sortingOrder, Is.EqualTo(4));

            var bursts = Json(ParticleSkills.ParticleSetBurstsBatch(Items(
                new JObject { ["entityId"] = Id(_first.gameObject), ["burstsJson"] = JArray.Parse("[{\"time\":0,\"count\":5}]") },
                new JObject { ["entityId"] = Id(_second.gameObject), ["burstsJson"] = JArray.Parse("[]") })));
            Assert.That(bursts["successCount"]?.Value<int>(), Is.EqualTo(2), bursts.ToString(Formatting.None));
            Assert.That(_first.emission.burstCount, Is.EqualTo(1));
        }

        [Test]
        public void InfoBatch_IsNotAtomic_AndReportsEachItem()
        {
            var json = Json(ParticleSkills.ParticleGetInfoBatch(Items(
                new JObject { ["entityId"] = Id(_first.gameObject) },
                new JObject { ["name"] = "DoesNotExist" })));
            Assert.That(json["successCount"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            Assert.That(json["failCount"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["rolledBack"], Is.Null);
            Assert.That(json["results"][0]["main"]["duration"]?.Value<float>(), Is.EqualTo(5f));
        }
    }
}
#endif

// Producer:Betsy
