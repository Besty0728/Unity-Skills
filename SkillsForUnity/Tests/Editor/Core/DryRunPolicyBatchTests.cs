using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The dryRun policy on POST /skills/batch: one token per batch, bound to the body as submitted, so $ref steps (which have
    /// no value during a dryRun) still run; the steps inside are never gated one by one; a refusal carries the whole batch's
    /// dryRun envelope and executes nothing, not even an undo fence.
    /// </summary>
    [TestFixture]
    public class DryRunPolicyBatchTests
    {
        private readonly DryRunPolicyTestHarness _harness = new DryRunPolicyTestHarness();

        [OneTimeSetUp] public void OneTimeSetUp() => _harness.OneTimeSetUp();
        [OneTimeTearDown] public void OneTimeTearDown() => _harness.OneTimeTearDown();
        [SetUp] public void SetUp() => _harness.SetUp(DryRunPolicy.HighRisk);
        [TearDown] public void TearDown() => _harness.TearDown();

        private static JObject Step(string skill, JObject args) => new JObject { ["skill"] = skill, ["args"] = args };

        private static string BatchBody(params JObject[] steps) => new JObject { ["steps"] = new JArray(steps) }.ToString(Formatting.None);

        private static string DeleteBody(string name) =>
            BatchBody(Step("scene_get_info", new JObject()), Step("gameobject_delete", new JObject { ["name"] = name }));

        [Test]
        public void GatedBatch_WithoutToken_CarriesTheDryRunEnvelopeAndRunsNothing()
        {
            DryRunPolicyTestHarness.Spawn("BatchProbe");
            int undoGroup = Undo.GetCurrentGroup();

            var (status, json) = DryRunPolicyTestHarness.Send("POST", "/skills/batch", "?mode=transactional", DeleteBody("BatchProbe"));
            var response = JObject.Parse(json);

            Assert.That(status, Is.EqualTo(200));
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("DRYRUN_REQUIRED"), json);
            Assert.That(response["skill"]?.ToString(), Is.EqualTo("skills_batch"));
            var details = (JObject)response["details"];
            Assert.That(details["reason"]?.ToString(), Is.EqualTo("missingToken"));
            Assert.That(details["gatedSteps"]?.ToObject<int[]>(), Is.EqualTo(new[] { 1 }), "Only the delete step is gated under highRisk.");
            Assert.That(details["dryRun"]?["mode"]?.ToString(), Is.EqualTo("dryRun"));
            Assert.That(details["dryRun"]?["wire"]?.ToString(), Is.EqualTo("v2"));
            Assert.That(details["dryRun"]?["results"], Has.Count.EqualTo(2));
            var token = DryRunPolicyTestHarness.TokenOf(response);
            StringAssert.Contains($"POST /skills/batch?dryRunToken={token}&mode=transactional", response["suggestedFixes"]?[0]?["reason"]?.ToString());
            Assert.That(DryRunPolicyTestHarness.Exists("BatchProbe"), Is.True, "Nothing may run without the token.");
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroup), "A refused transactional batch must not plant an undo fence.");

            var executed = DryRunPolicyTestHarness.Post("/skills/batch", $"?mode=transactional&dryRunToken={token}", DeleteBody("BatchProbe"));
            Assert.That(executed["status"]?.ToString(), Is.EqualTo("completed"), executed.ToString(Formatting.None));
            Assert.That(executed["transactional"]?.Value<bool>(), Is.True);
            Assert.That(DryRunPolicyTestHarness.Exists("BatchProbe"), Is.False, "Steps inside a passed batch are not gated one by one.");
        }

        [Test]
        public void BatchDryRun_IssuesOneTokenForTheWholeBatch_AndRefStepsRunWithIt()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;
            var body = BatchBody(
                Step("gameobject_create", new JObject { ["name"] = "RefProbe" }),
                Step("gameobject_delete", new JObject { ["instanceId"] = new JObject { ["$ref"] = "$0.instanceId" } }));

            var preview = DryRunPolicyTestHarness.Post("/skills/batch", "?mode=dryRun", body);
            var names = preview.Properties().Select(p => p.Name).ToList();
            Assert.That(names.IndexOf("dryRunToken"), Is.EqualTo(names.IndexOf("dryRun") + 1), preview.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("RefProbe"), Is.False);

            var executed = DryRunPolicyTestHarness.Post("/skills/batch", "?dryRunToken=" + DryRunPolicyTestHarness.TokenOf(preview), body);
            Assert.That(executed["status"]?.ToString(), Is.EqualTo("completed"), executed.ToString(Formatting.None));
            Assert.That(executed["executed"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(DryRunPolicyTestHarness.Exists("RefProbe"), Is.False, "The $ref step deleted what the first step created.");
        }

        [Test]
        public void BatchToken_IsBoundToTheBody_AndAMismatchDoesNotBurnIt()
        {
            DryRunPolicyTestHarness.Spawn("BodyA");
            DryRunPolicyTestHarness.Spawn("BodyB");
            var token = DryRunPolicyTestHarness.TokenOf(DryRunPolicyTestHarness.Post("/skills/batch", "?mode=dryRun", DeleteBody("BodyA")));

            var changed = DryRunPolicyTestHarness.Post("/skills/batch", "?dryRunToken=" + token, DeleteBody("BodyB"));
            Assert.That(changed["errorCode"]?.ToString(), Is.EqualTo("DRYRUN_REQUIRED"));
            Assert.That(changed["details"]?["reason"]?.ToString(), Is.EqualTo("argsChanged"));
            Assert.That(DryRunPolicyTestHarness.Exists("BodyB"), Is.True);

            var original = DryRunPolicyTestHarness.Post("/skills/batch", "?dryRunToken=" + token, DeleteBody("BodyA"));
            Assert.That(original["status"]?.ToString(), Is.EqualTo("completed"), original.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("BodyA"), Is.False);
        }

        [Test]
        public void BatchDryRun_CarriesATokenOnlyWhenAStepIsGated()
        {
            DryRunPolicyTestHarness.Spawn("EnvelopeProbe");

            var readOnly = DryRunPolicyTestHarness.Post("/skills/batch", "?mode=dryRun", BatchBody(Step("scene_get_info", new JObject())));
            Assert.That(readOnly.Property("dryRunToken"), Is.Null);

            DryRunPolicyService.OverrideForTests = DryRunPolicy.Off;
            var body = DeleteBody("EnvelopeProbe");
            var (_, offJson) = DryRunPolicyTestHarness.Send("POST", "/skills/batch", "?mode=dryRun", body);
            var steps = (JArray)JObject.Parse(body)["steps"];
            var direct = SkillsHttpServer.ExecuteBatchCore(steps, null, continueOnError: false, dryRun: true, transactional: false, agentId: "tests");
            Assert.That(offJson, Is.EqualTo(JsonConvert.SerializeObject(direct, SkillsCommon.JsonSettings)),
                "With the policy off the batch dryRun envelope is byte-for-byte what it was.");
        }

        [Test]
        public void TokenQueryKey_IsAccepted_EvenWithThePolicyOff()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.Off;

            var (status, json) = DryRunPolicyTestHarness.Send("POST", "/skills/batch", "?dryRunToken=stale",
                BatchBody(Step("scene_get_info", new JObject())));

            Assert.That(status, Is.EqualTo(200), json);
            Assert.That(JObject.Parse(json)["status"]?.ToString(), Is.EqualTo("completed"), json);
        }

        [Test]
        public void BatchWhoseOnlyGatedStepIsModeForbidden_GetsThatAnswerInsteadOfAToken()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;
            DryRunPolicyTestHarness.Spawn("ForbiddenProbe");

            var response = DryRunPolicyTestHarness.Post("/skills/batch", "", DeleteBody("ForbiddenProbe"));

            Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));
            Assert.That(response["results"]?[1]?["error"]?["errorCode"]?.ToString(), Is.EqualTo("MODE_FORBIDDEN"), response.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("ForbiddenProbe"), Is.True);
        }

        [Test]
        public void Audit_RecordsTheBatchRefusalAndThePass()
        {
            DryRunPolicyTestHarness.Spawn("AuditBatchProbe");
            var refused = DryRunPolicyTestHarness.Post("/skills/batch", "", DeleteBody("AuditBatchProbe"));

            var call = DryRunPolicyTestHarness.LastAudit("call", e => e["result"]?.ToString() == "dryRunRequired");
            Assert.That(call?["skill"]?.ToString(), Is.EqualTo("skills_batch"));
            Assert.That(call?["gatedSteps"]?.ToObject<int[]>(), Is.EqualTo(new[] { 1 }));

            DryRunPolicyTestHarness.Post("/skills/batch", "?dryRunToken=" + DryRunPolicyTestHarness.TokenOf(refused), DeleteBody("AuditBatchProbe"));
            var passed = DryRunPolicyTestHarness.LastAudit("dryrun_passed");
            Assert.That(passed?["scope"]?.ToString(), Is.EqualTo("/skills/batch"));
            Assert.That(DryRunPolicyTestHarness.Exists("AuditBatchProbe"), Is.False);
        }
    }
}

// Producer:Betsy
