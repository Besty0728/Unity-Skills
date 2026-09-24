using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// POST /skills/batch accepts ?wire=: a dry run hands it to every step's SkillRouter.DryRun (v2 step payloads and a
    /// "wire":"v2" envelope), execution accepts and ignores it, and v1 stays byte-for-byte what it was.
    /// </summary>
    [TestFixture]
    public class SkillsBatchWireTests
    {
        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        [Test]
        public void DryRunWithWireV2_ReturnsV2StepPayloads()
        {
            var response = SkillsHttpServer.ExecuteBatchCore(OneReadOnlyStep(), null, continueOnError: false,
                dryRun: true, transactional: false, agentId: "tests", wire: 2);

            Assert.That(response["wire"]?.ToString(), Is.EqualTo("v2"));
            var payload = StepPayload(response);
            Assert.That(payload["wire"]?.ToString(), Is.EqualTo("v2"), payload.ToString(Formatting.None));
            Assert.That(payload["skill"]?["flags"], Is.Not.Null, "The v2 skill echo carries a flags array.");
            Assert.That(payload["skill"]?["description"], Is.Null, "The v2 skill echo drops the description.");
        }

        [Test]
        public void DryRunWithoutWire_IsUnchangedV1()
        {
            var response = SkillsHttpServer.ExecuteBatchCore(OneReadOnlyStep(), null, continueOnError: false,
                dryRun: true, transactional: false, agentId: "tests");

            Assert.That(response.Property("wire"), Is.Null);
            var payload = StepPayload(response);
            Assert.That(payload.Property("wire"), Is.Null);
            Assert.That(payload["skill"]?["description"], Is.Not.Null);
        }

        [Test]
        public void ExecutionWithWireV2_IsAcceptedAndIgnored()
        {
            var response = SkillsHttpServer.ExecuteBatchCore(OneReadOnlyStep(), null, continueOnError: false,
                dryRun: false, transactional: false, agentId: "tests", wire: 2);

            Assert.That(response["mode"]?.ToString(), Is.EqualTo("execute"));
            Assert.That(response.Property("wire"), Is.Null,
                "Step results are the skills' own shapes; nothing in an executed batch is v2-shaped.");
            Assert.That(response["executed"]?.Value<int>(), Is.EqualTo(1), response.ToString(Formatting.None));
        }

        [Test]
        public void BatchEndpoint_AcceptsWireInTheQueryString()
        {
            var (statusCode, responseJson) = ProcessRequest("POST", "/skills/batch", "?mode=dryRun&wire=v2", OneReadOnlyStepBody());

            Assert.That(statusCode, Is.EqualTo(200), "wire used to be rejected as UNKNOWN_PARAM: " + responseJson);
            Assert.That(JObject.Parse(responseJson)["wire"]?.ToString(), Is.EqualTo("v2"));
        }

        [Test]
        public void WireInTheBody_IsRejectedWithAHint()
        {
            string body = JsonConvert.SerializeObject(new
            {
                steps = new[] { new { skill = "scene_get_info", args = new { } } },
                wire = "v2",
            });

            var (statusCode, responseJson) = ProcessRequest("POST", "/skills/batch", "?mode=dryRun", body);
            var response = JObject.Parse(responseJson);

            Assert.That(statusCode, Is.EqualTo(400));
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("UNKNOWN_PARAM"));
            StringAssert.Contains("query parameter", response["details"]?["unknownParams"]?[0]?["hint"]?.ToString());
        }

        // ---------- helpers ----------

        private static JArray OneReadOnlyStep() => new JArray
        {
            new JObject { ["skill"] = "scene_get_info", ["args"] = new JObject() },
        };

        private static string OneReadOnlyStepBody() => new JObject { ["steps"] = OneReadOnlyStep() }.ToString(Formatting.None);

        private static JObject StepPayload(JObject response)
        {
            var step = (JObject)((JArray)response["results"])[0];
            var payload = (step["result"] ?? step["error"]) as JObject;
            Assert.That(payload, Is.Not.Null, step.ToString(Formatting.None));
            return payload;
        }

        /// <summary>Drives the real main-thread handler (SkillsHttpServer.ProcessJob), as ReviewFixRouterTests does.</summary>
        private static (int StatusCode, string ResponseJson) ProcessRequest(string httpMethod, string path, string query, string body)
        {
            var jobType = typeof(SkillsHttpServer).GetNestedType("RequestJob", BindingFlags.NonPublic);
            Assert.That(jobType, Is.Not.Null, "SkillsHttpServer.RequestJob was renamed.");

            var job = Activator.CreateInstance(jobType, nonPublic: true);
            jobType.GetField("HttpMethod").SetValue(job, httpMethod);
            jobType.GetField("Path").SetValue(job, path);
            jobType.GetField("QueryString").SetValue(job, query);
            jobType.GetField("Body").SetValue(job, body);
            jobType.GetField("StatusCode").SetValue(job, 200);

            var processJob = typeof(SkillsHttpServer).GetMethod("ProcessJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(processJob, Is.Not.Null, "SkillsHttpServer.ProcessJob was renamed.");
            processJob.Invoke(null, new[] { job });

            return ((int)jobType.GetField("StatusCode").GetValue(job), (string)jobType.GetField("ResponseJson").GetValue(job));
        }
    }
}

// Producer:Betsy
