using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// GET /skill/{name}: skills only run on POST, so a registered name answers 405 METHOD_NOT_ALLOWED with the
    /// POST it most likely meant (query arguments moved into a typed JSON body, request-level keys left in the URL).
    /// </summary>
    [TestFixture]
    public class SkillGetRewriteTests
    {
        [Test]
        public void GetOnARegisteredSkill_Is405_WithTheExactPostRewrite()
        {
            var job = ProcessRequest("GET", "/skill/script_list", "?folder=Assets%2FScripts&limit=5&mode=dryRun");
            var body = JObject.Parse(job.ResponseJson);

            Assert.That(job.StatusCode, Is.EqualTo(405), job.ResponseJson);
            Assert.That(job.AllowHeader, Is.EqualTo("POST"));
            Assert.That(body["errorCode"]?.ToString(), Is.EqualTo("METHOD_NOT_ALLOWED"));
            Assert.That(body["retryStrategy"]?.ToString(), Is.EqualTo("fix_and_retry"));
            StringAssert.Contains("POST /skill/script_list", body["error"]?.ToString());

            var converted = (JObject)body["details"]?["body"];
            Assert.That(converted?.ToString(Formatting.None), Is.EqualTo("{\"folder\":\"Assets/Scripts\",\"limit\":5}"),
                "limit is an int parameter, so it must arrive as a JSON number; mode stays a request-level key.");

            string curl = body["details"]?["curl"]?.ToString();
            StringAssert.StartsWith("curl -s -X POST 'http://localhost:", curl);
            StringAssert.Contains("/skill/script_list?mode=dryRun' -H 'Content-Type: application/json'", curl);
            StringAssert.EndsWith("-d '{\"folder\":\"Assets/Scripts\",\"limit\":5}'", curl);

            var fix = body["suggestedFixes"]?[0];
            Assert.That(fix?["skill"]?.ToString(), Is.EqualTo("script_list"));
            Assert.That(JToken.DeepEquals(fix?["args"], converted), Is.True);
            StringAssert.Contains(curl, fix?["reason"]?.ToString());
        }

        [Test]
        public void GetOnAnUnknownSkill_KeepsTheOld404()
        {
            var job = ProcessRequest("GET", "/skill/__no_such_skill_at_all__", "?x=1");

            Assert.That(job.StatusCode, Is.EqualTo(404), job.ResponseJson);
            Assert.That(JObject.Parse(job.ResponseJson)["errorCode"]?.ToString(), Is.EqualTo("NOT_FOUND"));
            Assert.That(job.AllowHeader, Is.Null);
        }

        [Test]
        public void QueryValues_AreTypedByTheDeclaredParameters()
        {
            var body = SkillsHttpServer.ConvertQueryToSkillBody(
                "?NAME=007&count=3&scale=1.5&flag=1&tags=%5B%22a%22%5D&day=Monday&optionalCount=-2&label=hello+world",
                ProbeParameters(), out string requestQuery);

            Assert.That(body["name"]?.Type, Is.EqualTo(JTokenType.String), "A string parameter keeps \"007\" as text.");
            Assert.That(body["name"]?.ToString(), Is.EqualTo("007"));
            Assert.That(body.Property("NAME"), Is.Null, "The body uses the parameter's declared spelling.");
            Assert.That(body["count"]?.Type, Is.EqualTo(JTokenType.Integer));
            Assert.That(body["scale"]?.Value<double>(), Is.EqualTo(1.5));
            Assert.That(body["flag"]?.Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That(body["flag"]?.Value<bool>(), Is.True);
            Assert.That(body["tags"]?.Type, Is.EqualTo(JTokenType.Array));
            Assert.That(body["day"]?.ToString(), Is.EqualTo("Monday"));
            Assert.That(body["optionalCount"]?.Value<long>(), Is.EqualTo(-2));
            Assert.That(body["label"]?.ToString(), Is.EqualTo("hello world"), "'+' decodes to a space.");
            Assert.That(requestQuery, Is.Empty);
        }

        [Test]
        public void UndeclaredKeys_BecomeNumbersOrBooleansWhenTheyParse()
        {
            var body = SkillsHttpServer.ConvertQueryToSkillBody("?whole=2&real=0.25&yes=true&word=abc&bare", ProbeParameters(), out _);

            Assert.That(body["whole"]?.Type, Is.EqualTo(JTokenType.Integer));
            Assert.That(body["real"]?.Type, Is.EqualTo(JTokenType.Float));
            Assert.That(body["yes"]?.Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That(body["word"]?.Type, Is.EqualTo(JTokenType.String));
            Assert.That(body["bare"]?.Value<bool>(), Is.True, "A bare key is a flag.");
        }

        [Test]
        public void RequestLevelKeys_StayInTheUrl()
        {
            var body = SkillsHttpServer.ConvertQueryToSkillBody(
                "?name=a&mode=dryRun&wire=v2&expectInstance=Game_1&diff=1", ProbeParameters(), out string requestQuery);

            Assert.That(requestQuery, Is.EqualTo("?mode=dryRun&wire=v2&expectInstance=Game_1&diff=1"));
            Assert.That(body.Count, Is.EqualTo(1));
            Assert.That(body["name"]?.ToString(), Is.EqualTo("a"));
        }

        [Test]
        public void Curl_QuotesSingleQuotesForTheShell()
        {
            var body = JObject.Parse(SkillsHttpServer.BuildMethodNotAllowedResponse(
                "script_list", "?filter=it's", ProbeParameters(), 8090));

            StringAssert.Contains("'\\''", body["details"]?["curl"]?.ToString(),
                "A single quote inside the single-quoted -d argument must be closed, escaped and reopened.");
            Assert.That(body["details"]?["body"]?["filter"]?.ToString(), Is.EqualTo("it's"));
        }

        // ---------- helpers ----------

        private static void RewriteProbe(string name, int count, float scale, bool flag, string[] tags,
            DayOfWeek day, int? optionalCount, string label, string filter)
        {
        }

        private static ParameterInfo[] ProbeParameters() =>
            typeof(SkillGetRewriteTests).GetMethod(nameof(RewriteProbe), BindingFlags.NonPublic | BindingFlags.Static).GetParameters();

        private sealed class HandledRequest
        {
            public int StatusCode;
            public string ResponseJson;
            public string AllowHeader;
        }

        /// <summary>Drives the real main-thread handler (SkillsHttpServer.ProcessJob), as ReviewFixRouterTests does.</summary>
        private static HandledRequest ProcessRequest(string httpMethod, string path, string query)
        {
            var jobType = typeof(SkillsHttpServer).GetNestedType("RequestJob", BindingFlags.NonPublic);
            Assert.That(jobType, Is.Not.Null, "SkillsHttpServer.RequestJob was renamed.");

            var job = Activator.CreateInstance(jobType, nonPublic: true);
            jobType.GetField("HttpMethod").SetValue(job, httpMethod);
            jobType.GetField("Path").SetValue(job, path);
            jobType.GetField("QueryString").SetValue(job, query);
            jobType.GetField("StatusCode").SetValue(job, 200);

            var processJob = typeof(SkillsHttpServer).GetMethod("ProcessJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(processJob, Is.Not.Null, "SkillsHttpServer.ProcessJob was renamed.");
            processJob.Invoke(null, new[] { job });

            return new HandledRequest
            {
                StatusCode = (int)jobType.GetField("StatusCode").GetValue(job),
                ResponseJson = (string)jobType.GetField("ResponseJson").GetValue(job),
                AllowHeader = (string)jobType.GetField("AllowHeader").GetValue(job),
            };
        }
    }
}

// Producer:Betsy
