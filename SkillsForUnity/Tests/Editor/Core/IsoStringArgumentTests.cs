using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// An ISO-8601 string in a request must reach a string parameter as the same text. Newtonsoft's default reader turns it
    /// into a Date token, and reading that back as a string gives culture-formatted text ("11/06/2026 06:07:50"), which
    /// console_get_logs then rejected as a malformed fromUtc. Each test seeds one console record at 06:07:50.5 UTC and asks
    /// for the window around it, so a rewritten bound fails the call instead of only changing a value.
    /// </summary>
    [TestFixture]
    public class IsoStringArgumentTests
    {
        private const string Probe = "iso-arg-probe";
        private const string From = "2026-11-06T06:07:50Z";
        private const string To = "2026-11-06T06:07:51Z";
        // Seven fraction digits: a Date token writes it back as ".5Z", so the text itself shows whether it was rewritten.
        private const string StoredUtc = "2026-11-06T06:07:50.5000000Z";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private IDisposable _history;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            _history = ConsoleLogHistoryService.BeginTestScope(
                Path.Combine(Path.GetTempPath(), "UnitySkillsConsoleLogs_" + Guid.NewGuid().ToString("N") + ".jsonl"));
            ConsoleLogHistoryService.RecordForTests(Probe, "", LogType.Log, new DateTime(2026, 11, 6, 6, 7, 50, 500, DateTimeKind.Utc));
        }

        [TearDown]
        public void TearDown()
        {
            _history?.Dispose();
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        [TestCase("2026-11-06T06:07:50Z")]
        [TestCase("2026-11-06T06:07:50.5000000Z")]
        [TestCase("2026-11-06T06:07:50+00:00")]
        [TestCase("2026-11-06T14:07:50+08:00")]
        public void ValidateParameters_PassesAnIsoStringToAStringParameterUnchanged(string value)
        {
            Assert.That(SkillRouter.TryGetSkill("console_get_logs", out var skill), Is.True);

            var validation = SkillRouter.ValidateParameters(skill, new JObject { ["fromUtc"] = value }.ToString(Formatting.None));

            Assert.That(validation.TypeErrors, Is.Empty);
            int index = Array.FindIndex(skill.Parameters, p => p.Name == "fromUtc");
            Assert.That(validation.InvokeArgs[index], Is.EqualTo(value));
        }

        [Test]
        public void Execute_ConsoleGetLogs_AcceptsIsoUtcBounds()
        {
            var response = ParseExact(SkillRouter.Execute("console_get_logs", WindowArgs().ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            var logs = (JArray)response["result"]["logs"];
            Assert.That(logs.Count, Is.EqualTo(1), response.ToString(Formatting.None));
            Assert.That((string)logs[0]["message"], Is.EqualTo(Probe));
            Assert.That((string)logs[0]["utc"], Is.EqualTo(StoredUtc));
        }

        [Test]
        public void BatchEndpoint_KeepsIsoStringsInStepArgs()
        {
            var body = new JObject
            {
                ["steps"] = new JArray { new JObject { ["skill"] = "console_get_logs", ["args"] = WindowArgs() } },
            }.ToString(Formatting.None);

            var (statusCode, responseJson) = ProcessRequest("POST", "/skills/batch", "", body);

            Assert.That(statusCode, Is.EqualTo(200), responseJson);
            var step = (JObject)((JArray)ParseExact(responseJson)["results"])[0];
            Assert.That(step["status"]?.ToString(), Is.EqualTo("success"), responseJson);
            Assert.That(((JArray)step["result"]["logs"]).Count, Is.EqualTo(1), responseJson);
        }

        [Test]
        public void BatchStepResult_KeepsAnIsoStringAsAString()
        {
            var steps = new JArray { new JObject { ["skill"] = "console_get_logs", ["args"] = WindowArgs() } };

            var response = SkillsHttpServer.ExecuteBatchCore(steps, null, continueOnError: false,
                dryRun: false, transactional: false, agentId: "tests");

            var utc = response["results"][0]["result"]["logs"][0]["utc"];
            Assert.That(utc.Type, Is.EqualTo(JTokenType.String), "A $ref to this value would otherwise carry a Date token.");
            Assert.That((string)utc, Is.EqualTo(StoredUtc));
        }

        [Test]
        public void BatchExecutor_ObjectTypedItemMember_ReceivesAnIsoStringAsAString()
        {
            object received = null;

            BatchExecutor.Execute<ObjectItem>("[{\"value\":\"" + StoredUtc + "\"}]", item =>
            {
                received = item.value;
                return new { success = true };
            });

            Assert.That(received, Is.TypeOf<string>());
            Assert.That(received, Is.EqualTo(StoredUtc));
        }

        // ---------- helpers ----------

        private sealed class ObjectItem
        {
            public object value { get; set; }
        }

        private static JObject WindowArgs() => new JObject
        {
            ["fromUtc"] = From,
            ["toUtc"] = To,
            ["filter"] = Probe,
            ["collapse"] = false,
        };

        /// <summary>Parses without turning ISO strings into dates, so the assertions see the text the server wrote.</summary>
        private static JObject ParseExact(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        /// <summary>Drives the real main-thread handler (SkillsHttpServer.ProcessJob), as SkillsBatchWireTests does.</summary>
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
