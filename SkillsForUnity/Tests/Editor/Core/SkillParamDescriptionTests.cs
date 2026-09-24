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
    /// <see cref="SkillParamAttribute"/> notes: they sit on real skill parameters, and every wire that lists
    /// parameters (schema v1/v2, dryRun) carries the note as <c>description</c> -- and carries no such key otherwise.
    /// </summary>
    [TestFixture]
    public class SkillParamDescriptionTests
    {
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
        }

        [TearDown]
        public void TearDown() => SkillsSurfaceProfile.Current = _savedProfile;

        [Test]
        public void EverySkillParam_SitsOnAParameterOfASkillMethod()
        {
            const BindingFlags everyMethod = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                             BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var misplaced = new List<string>();
            foreach (var type in SafeTypes(typeof(SkillRouter).Assembly))
            {
                foreach (var method in type.GetMethods(everyMethod))
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        var note = parameter.GetCustomAttribute<SkillParamAttribute>();
                        if (note == null)
                            continue;
                        if (method.GetCustomAttribute<UnitySkillAttribute>() == null)
                            misplaced.Add($"{type.Name}.{method.Name}({parameter.Name}): not a [UnitySkill] method");
                        else if (string.IsNullOrWhiteSpace(note.Description))
                            misplaced.Add($"{type.Name}.{method.Name}({parameter.Name}): empty note");
                    }
                }
            }

            Assert.That(misplaced, Is.Empty,
                "A SkillParam note is only ever read from a skill's own parameters; anywhere else it documents nothing:\n" +
                string.Join("\n", misplaced));
        }

        [Test]
        public void SchemaV1AndV2_CarryTheNote_OnlyWhereOneExists()
        {
            var annotated = AnnotatedSkills();
            Assume.That(annotated, Is.Not.Empty, "No skill parameter carries a SkillParam note yet.");

            var names = string.Join(",", annotated.Select(s => s.Name));
            foreach (var wire in new[] { "", "&wire=v2" })
            {
                var schema = JObject.Parse(SkillRouter.GetFilteredSchema("?names=" + names + wire));
                var entries = ((JArray)schema["skills"]).Cast<JObject>().ToDictionary(s => (string)s["name"], StringComparer.Ordinal);

                foreach (var skill in annotated)
                {
                    Assert.That(entries.TryGetValue(skill.Name, out var entry), Is.True, $"{skill.Name} missing from ?names= schema{wire}");
                    AssertNotesMatch(skill, (JArray)entry["parameters"], $"schema{wire}");
                }
            }
        }

        [Test]
        public void DryRunParameters_CarryTheNote()
        {
            var skill = AnnotatedSkills().FirstOrDefault();
            Assume.That(skill, Is.Not.Null, "No skill parameter carries a SkillParam note yet.");

            var dry = JObject.Parse(SkillRouter.DryRun(skill.Name, "{}"));
            Assert.That(dry["status"]?.ToString(), Is.EqualTo("dryRun"), dry.ToString(Formatting.None));

            var parameters = new JArray(((JArray)dry["parameters"]).Where(p => p["synthetic"] == null));
            AssertNotesMatch(skill, parameters, "dryRun");
        }

        // ---------- helpers ----------

        private static SkillRouter.SkillInfo[] AnnotatedSkills() =>
            SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.Parameters.Any(p => p.GetCustomAttribute<SkillParamAttribute>() != null))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToArray();

        private static void AssertNotesMatch(SkillRouter.SkillInfo skill, JArray parameters, string surface)
        {
            var byName = parameters.Cast<JObject>().ToDictionary(p => (string)p["name"], StringComparer.Ordinal);
            foreach (var parameter in skill.Parameters)
            {
                Assert.That(byName.TryGetValue(parameter.Name, out var entry), Is.True, $"{surface}: {skill.Name}.{parameter.Name} is not listed");
                var note = parameter.GetCustomAttribute<SkillParamAttribute>()?.Description;
                if (string.IsNullOrWhiteSpace(note))
                    Assert.That(entry.Property("description"), Is.Null,
                        $"{surface}: {skill.Name}.{parameter.Name} has no note, so it must carry no description key");
                else
                    Assert.That((string)entry["description"], Is.EqualTo(note), $"{surface}: {skill.Name}.{parameter.Name}");
            }
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }
    }
}

// Producer:Betsy
