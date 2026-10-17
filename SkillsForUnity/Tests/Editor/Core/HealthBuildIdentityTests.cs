using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// /health names the plugin build that answers: packageRoot (where com.besty.unity-skills was resolved when this domain
    /// started serving) and assembliesLoadedAtUtc (when this domain's assemblies were loaded), so a maintainer tool can
    /// refuse to gate on an editor still running another checkout's code. Driven through the real main-thread handler.
    /// </summary>
    [TestFixture]
    public class HealthBuildIdentityTests
    {
        [Test]
        public void Health_NamesThePackageRootTheDomainLoadedFrom()
        {
            var health = GetHealth();

            Assert.That(PackageRootLocator.TryGet(out var expected), Is.True, "The test needs a resolvable package root.");
            Assert.That((string)health["packageRoot"], Is.EqualTo(expected));
            Assert.That(File.Exists(Path.Combine(expected, "package.json")), Is.True, "packageRoot must be the package folder.");
        }

        [Test]
        public void Health_StampsWhenThisDomainWasLoaded()
        {
            var health = GetHealth();
            var stamp = (string)health["assembliesLoadedAtUtc"];

            Assert.That(DateTime.TryParseExact(stamp, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var loadedAt),
                Is.True, "Not ISO-8601 round-trip: " + stamp);
            Assert.That(loadedAt.Kind, Is.EqualTo(DateTimeKind.Utc), stamp);
            Assert.That(loadedAt.Ticks, Is.EqualTo(CompilationResultService.DomainLoadedUtcTicks), "One clock with the compile jobs' domainLoadedAtUtcTicks.");
            Assert.That(loadedAt, Is.LessThanOrEqualTo(DateTime.UtcNow));
            Assert.That((string)GetHealth()["assembliesLoadedAtUtc"], Is.EqualTo(stamp), "Captured once per domain load, not per request.");
        }

        /// <summary>GET /health through SkillsHttpServer.ProcessJob, parsed without turning dates into DateTime values.</summary>
        private static JObject GetHealth()
        {
            var jobType = typeof(SkillsHttpServer).GetNestedType("RequestJob", BindingFlags.NonPublic);
            Assert.That(jobType, Is.Not.Null, "SkillsHttpServer.RequestJob was renamed.");
            var job = Activator.CreateInstance(jobType, nonPublic: true);
            jobType.GetField("HttpMethod").SetValue(job, "GET");
            jobType.GetField("Path").SetValue(job, "/health");
            jobType.GetField("StatusCode").SetValue(job, 200);

            var processJob = typeof(SkillsHttpServer).GetMethod("ProcessJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(processJob, Is.Not.Null, "SkillsHttpServer.ProcessJob was renamed.");
            processJob.Invoke(null, new[] { job });

            Assert.That((int)jobType.GetField("StatusCode").GetValue(job), Is.EqualTo(200));
            var json = (string)jobType.GetField("ResponseJson").GetValue(job);
            return JsonConvert.DeserializeObject<JObject>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
        }
    }
}

// Producer:Betsy
