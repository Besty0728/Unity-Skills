using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>The dedicated-project marker (contract 7.4), written into a throwaway project folder.</summary>
    [TestFixture]
    public class EvaluationMarkerTests
    {
        private string _project;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "unityskills-marker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_project, "ProjectSettings"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_project)) Directory.Delete(_project, true);
        }

        [Test]
        public void Missing_WhenTheFileDoesNotExist()
        {
            Assert.That(EvaluationMarker.Read(_project), Is.EqualTo(EvaluationMarker.MarkerState.Missing));
        }

        [Test]
        public void Write_ProducesTheContractShapeWithoutBom()
        {
            var created = new DateTime(2026, 10, 17, 3, 0, 0, 123, DateTimeKind.Utc);
            EvaluationMarker.Write(_project, created);

            var path = EvaluationMarker.PathFor(_project);
            Assert.That(path, Is.EqualTo(Path.Combine(_project, "ProjectSettings", "UnitySkillsEvaluation.json")));
            var bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Take(3).ToArray(), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));

            var json = EvaluationState.ParseObject(File.ReadAllText(path));
            Assert.That(json.Properties().Select(p => p.Name), Is.EqualTo(new[] { "schema", "kind", "createdAt", "createdWith" }));
            Assert.That(json.Value<int>("schema"), Is.EqualTo(1));
            Assert.That(json.Value<string>("kind"), Is.EqualTo("UnitySkillsEvaluationProject"));
            Assert.That(json.Value<string>("createdAt"), Is.EqualTo("2026-10-17T03:00:00.123Z"));
            Assert.That(json.Value<string>("createdWith"), Is.EqualTo(SkillsLogger.Version));
            Assert.That(EvaluationMarker.Read(_project), Is.EqualTo(EvaluationMarker.MarkerState.Valid));
            Assert.That(Directory.GetFiles(Path.Combine(_project, "ProjectSettings")), Has.Length.EqualTo(1), "No temp file may stay behind.");
        }

        [Test]
        public void Write_ReplacesAnExistingMarker()
        {
            EvaluationMarker.Write(_project, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            EvaluationMarker.Write(_project, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));

            var json = EvaluationState.ParseObject(File.ReadAllText(EvaluationMarker.PathFor(_project)));
            Assert.That(json.Value<string>("createdAt"), Is.EqualTo("2026-02-02T00:00:00.000Z"));
        }

        [TestCase("{\"schema\":1,\"kind\":\"SomethingElse\"}")]
        [TestCase("{\"schema\":2,\"kind\":\"UnitySkillsEvaluationProject\"}")]
        [TestCase("{\"kind\":\"UnitySkillsEvaluationProject\"}")]
        [TestCase("{\"schema\":\"1\",\"kind\":\"UnitySkillsEvaluationProject\"}")]
        [TestCase("not json")]
        [TestCase("[1,2]")]
        public void Invalid_ForAnythingButTheContractShape(string content)
        {
            File.WriteAllText(EvaluationMarker.PathFor(_project), content);
            Assert.That(EvaluationMarker.Read(_project), Is.EqualTo(EvaluationMarker.MarkerState.Invalid));
        }

        [Test]
        public void Valid_AcceptsUnknownExtraFields()
        {
            File.WriteAllText(EvaluationMarker.PathFor(_project),
                "{\"schema\":1,\"kind\":\"UnitySkillsEvaluationProject\",\"createdAt\":\"2026-10-17T03:00:00.000Z\",\"note\":\"x\"}");
            Assert.That(EvaluationMarker.Read(_project), Is.EqualTo(EvaluationMarker.MarkerState.Valid));
        }
    }
}

// Producer:Betsy
