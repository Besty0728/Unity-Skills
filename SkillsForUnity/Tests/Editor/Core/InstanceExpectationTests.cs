using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The expected-instance guard (expectInstance / expectProject, as query parameters or X-Expect-* headers).
    /// The listener thread itself cannot be driven from a test, so these pin the three pure pieces it is built from:
    /// reading the expectation, matching it against the serving identity, and the 409 INSTANCE_MISMATCH body.
    /// </summary>
    [TestFixture]
    public class InstanceExpectationTests
    {
        private const string ServingId = "MyGame_1A2B3C4D";
        private const string ServingName = "My Game";
        private const string ServingPath = "/Users/dev/Projects/MyGameRepo";

        // ---------- reading the expectation ----------

        [Test]
        public void NoQueryAndNoHeaders_MeansNoExpectation()
        {
            Assert.That(SkillsHttpServer.ReadInstanceExpectation(null, null, null), Is.Null);
            Assert.That(SkillsHttpServer.ReadInstanceExpectation("?mode=dryRun", null, "  "), Is.Null,
                "Unrelated keys and blank headers must not produce an expectation.");
        }

        [Test]
        public void QueryParameters_AreReadCaseInsensitively()
        {
            var expectation = SkillsHttpServer.ReadInstanceExpectation("?EXPECTINSTANCE=MyGame_1A2B3C4D&expectproject=Other", null, null);

            Assert.That(expectation, Is.Not.Null);
            Assert.That(expectation.Instance, Is.EqualTo("MyGame_1A2B3C4D"));
            Assert.That(expectation.Project, Is.EqualTo("Other"));
        }

        [Test]
        public void Headers_AreRead_AndPercentEncodedValuesDecoded()
        {
            var expectation = SkillsHttpServer.ReadInstanceExpectation(null, " MyGame_1A2B3C4D ", "My%20Game");

            Assert.That(expectation.Instance, Is.EqualTo("MyGame_1A2B3C4D"));
            Assert.That(expectation.Project, Is.EqualTo("My Game"),
                "The server percent-encodes non-ASCII X-Unity-Project values, so a client echoing one back must match.");
        }

        [Test]
        public void QueryValue_WinsOverTheHeaderOfTheSameKind()
        {
            var expectation = SkillsHttpServer.ReadInstanceExpectation("?expectInstance=FromQuery_1", "FromHeader_2", "HeaderProject");

            Assert.That(expectation.Instance, Is.EqualTo("FromQuery_1"));
            Assert.That(expectation.Project, Is.EqualTo("HeaderProject"),
                "A query value only overrides its own kind; the other header still applies.");
        }

        // ---------- matching ----------

        [Test]
        public void InstanceExpectation_MatchesTheExactIdIgnoringCase()
        {
            Assert.That(Expect(instance: "mygame_1a2b3c4d").Matches(ServingId, ServingName, ServingPath), Is.True);
            Assert.That(Expect(instance: "MyGame_FFFFFFFF").Matches(ServingId, ServingName, ServingPath), Is.False,
                "Two projects may share a productName, never an instanceId: only the exact id matches.");
        }

        [Test]
        public void ProjectExpectation_MatchesProductNameOrProjectFolder()
        {
            Assert.That(Expect(project: "my game").Matches(ServingId, ServingName, ServingPath), Is.True, "productName");
            Assert.That(Expect(project: "MYGAMEREPO").Matches(ServingId, ServingName, ServingPath), Is.True, "folder name");
            Assert.That(Expect(project: "MyGameRepo").Matches(ServingId, ServingName, ServingPath + "/"), Is.True,
                "A trailing separator on the project path must not hide the folder name.");
            Assert.That(Expect(project: "Projects").Matches(ServingId, ServingName, ServingPath), Is.False,
                "Only the project folder itself counts, not its parents.");
        }

        [Test]
        public void BothKinds_MustHoldTogether()
        {
            Assert.That(Expect("MyGame_1A2B3C4D", "My Game").Matches(ServingId, ServingName, ServingPath), Is.True);
            Assert.That(Expect("MyGame_1A2B3C4D", "Another").Matches(ServingId, ServingName, ServingPath), Is.False);
        }

        // ---------- the 409 body ----------

        [Test]
        public void MismatchBody_NamesTheServer_TheExpectation_AndTheInstanceToResendTo()
        {
            var others = new List<RegistryService.InstanceInfo>
            {
                Instance("Other_11111111", "Other", "/p/Other", 8091, null),
                Instance("Wanted_22222222", "Wanted", "/p/Wanted", 8092, RegistryService.StatusReloading),
                Instance("Third_33333333", "Third", "/p/Third", 8093, RegistryService.StatusStopped),
            };

            var body = JObject.Parse(SkillsHttpServer.BuildInstanceMismatchResponse(
                Expect(instance: "Wanted_22222222"), ServingId, ServingName, ServingPath, 8090, others));

            Assert.That(body["status"]?.ToString(), Is.EqualTo("error"));
            Assert.That(body["errorCode"]?.ToString(), Is.EqualTo("INSTANCE_MISMATCH"));
            Assert.That(body["retryStrategy"]?.ToString(), Is.EqualTo("fix_and_retry"));
            StringAssert.Contains($"This server is {ServingName} ({ServingId}) at {ServingPath}", body["error"]?.ToString());
            StringAssert.Contains("Wanted_22222222", body["error"]?.ToString());

            var server = body["details"]?["server"];
            Assert.That(server?["instanceId"]?.ToString(), Is.EqualTo(ServingId));
            Assert.That(server?["projectPath"]?.ToString(), Is.EqualTo(ServingPath));
            Assert.That(server?["port"]?.Value<int>(), Is.EqualTo(8090));
            Assert.That(body["details"]?["expected"]?["instanceId"]?.ToString(), Is.EqualTo("Wanted_22222222"));

            var instances = (JArray)body["details"]?["instances"];
            Assert.That(instances, Has.Count.EqualTo(3));
            Assert.That(instances.Select(i => i["status"]?.ToString()),
                Is.EqualTo(new[] { "running", "reloading", "stopped" }),
                "An entry without status is running; the others keep theirs so the caller can wait for a reloading one.");

            var fixes = (JArray)body["suggestedFixes"];
            Assert.That(fixes, Has.Count.EqualTo(1), "Only the instance that satisfies the expectation is suggested.");
            Assert.That(fixes[0]["args"]?["port"]?.Value<int>(), Is.EqualTo(8092));
            Assert.That(fixes[0]["args"]?["instanceId"]?.ToString(), Is.EqualTo("Wanted_22222222"));
            StringAssert.Contains("reloading", fixes[0]["reason"]?.ToString());
        }

        [Test]
        public void MismatchBody_WithNoMatchingInstance_SaysSo()
        {
            var body = JObject.Parse(SkillsHttpServer.BuildInstanceMismatchResponse(
                Expect(project: "Nowhere"), ServingId, ServingName, ServingPath, 8090,
                new List<RegistryService.InstanceInfo> { Instance("Other_11111111", "Other", "/p/Other", 8091, null) }));

            var fixes = (JArray)body["suggestedFixes"];
            Assert.That(fixes, Has.Count.EqualTo(1));
            Assert.That(fixes[0]["action"]?.ToString(), Is.EqualTo("find_target"));
            Assert.That(body["details"]?["expected"]?["project"]?.ToString(), Is.EqualTo("Nowhere"));
        }

        [Test]
        public void MismatchBody_ToleratesAMissingRegistryView()
        {
            var body = JObject.Parse(SkillsHttpServer.BuildInstanceMismatchResponse(
                Expect(instance: "X_1"), ServingId, ServingName, ServingPath, 8090, null));

            Assert.That((JArray)body["details"]?["instances"], Is.Empty);
            Assert.That(body["errorCode"]?.ToString(), Is.EqualTo("INSTANCE_MISMATCH"));
        }

        [Test]
        public void ErrorCodes_AreAppendedWithTheirWireStrings()
        {
            Assert.That(SkillErrorCode.InstanceMismatch.ToWireString(), Is.EqualTo("INSTANCE_MISMATCH"));
            Assert.That(SkillErrorCode.MethodNotAllowed.ToWireString(), Is.EqualTo("METHOD_NOT_ALLOWED"));
            Assert.That(SkillErrorCodeExtensions.TryParseWire("INSTANCE_MISMATCH", out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(SkillErrorCode.InstanceMismatch));
            Assert.That((int)SkillErrorCode.InstanceMismatch, Is.GreaterThan((int)SkillErrorCode.SurfaceExcluded),
                "New values go at the end so existing numeric values stay stable.");
        }

        // ---------- the batch endpoint accepts the keys the listener consumed ----------

        [Test]
        public void BatchEndpoint_AcceptsTheExpectationQueryKeys()
        {
            // The listener checks the expectation before the batch handler runs; the handler must not then reject
            // the same keys as unknown and refuse a request the guard already let through.
            string body = JsonConvert.SerializeObject(new
            {
                steps = new[] { new { skill = "scene_get_info", args = new { } } },
            });

            var (statusCode, responseJson) = ProcessRequest("POST", "/skills/batch",
                "?mode=dryRun&expectInstance=" + ServingId + "&expectProject=Anything", body);

            Assert.That(statusCode, Is.EqualTo(200), responseJson);
            Assert.That(JObject.Parse(responseJson)["mode"]?.ToString(), Is.EqualTo("dryRun"));
        }

        // ---------- helpers ----------

        private static SkillsHttpServer.InstanceExpectation Expect(string instance = null, string project = null) =>
            new SkillsHttpServer.InstanceExpectation { Instance = instance, Project = project };

        private static RegistryService.InstanceInfo Instance(string id, string name, string path, int port, string status) =>
            new RegistryService.InstanceInfo { id = id, name = name, path = path, port = port, status = status };

        /// <summary>Drives the real main-thread handler (SkillsHttpServer.ProcessJob) the same way ReviewFixRouterTests does.</summary>
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
