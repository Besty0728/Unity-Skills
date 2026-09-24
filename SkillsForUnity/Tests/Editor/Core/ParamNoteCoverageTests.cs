using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Parameters whose meaning agents used to look up in a whole module doc -- coordinate spaces, units, value
    /// encodings, allowed values, JSON payload shapes -- must answer from the schema itself: each carries a
    /// <see cref="SkillParamAttribute"/> note, emitted as a non-empty <c>description</c> on both wires.
    /// </summary>
    [TestFixture]
    public class ParamNoteCoverageTests
    {
        private static readonly (string Skill, string Parameter)[] Curated =
        {
            // Coordinate space and application order.
            ("gameobject_create", "x"), ("gameobject_create", "y"), ("gameobject_create", "z"),
            ("gameobject_create", "rotX"), ("gameobject_create", "scaleX"), ("gameobject_create", "space"),
            ("gameobject_create", "parentPath"),
            ("gameobject_set_transform", "posX"), ("gameobject_set_transform", "localPosX"),
            ("gameobject_set_transform", "rotX"), ("gameobject_set_transform", "scaleX"),
            ("gameobject_set_transform", "name"), ("gameobject_set_transform", "path"),
            ("light_create", "x"),
            ("camera_set_transform", "posX"),
            // Units, ranges and allowed values.
            ("gameobject_create", "primitiveType"),
            ("light_create", "lightType"), ("light_create", "r"), ("light_create", "shadows"),
            ("light_set_properties", "r"), ("light_set_properties", "intensity"),
            ("light_set_properties", "range"), ("light_set_properties", "spotAngle"), ("light_set_properties", "shadows"),
            ("material_set_color", "r"), ("material_set_color", "intensity"),
            ("physics_raycast", "layerMask"),
            // Value encodings and references.
            ("component_add", "componentType"),
            ("component_set_property", "value"), ("component_set_property", "propertyName"),
            ("component_set_property", "assetPath"), ("component_set_property", "referencePath"),
            ("component_set_serialized_property", "propertyPath"), ("component_set_serialized_property", "value"),
            ("component_set_serialized_property", "objectType"),
            ("batch_preview_set_property", "value"),
            ("material_assign", "materialPath"), ("material_set_color", "path"), ("material_create", "savePath"),
            ("script_create", "scriptName"), ("script_create", "template"), ("script_create", "content"),
            // JSON payload shapes.
            ("gameobject_create_batch", "items"), ("gameobject_set_transform_batch", "items"),
            ("component_set_property_batch", "items"), ("batch_query_gameobjects", "queryJson"),
        };

        // In these classes a JSON payload parameter is a bare string in the schema, so its note is the only place its shape is stated.
        private static readonly Type[] PayloadCoveredTypes =
        {
            typeof(GameObjectSkills), typeof(ComponentSkills), typeof(MaterialSkills),
            typeof(LightSkills), typeof(ScriptSkills), typeof(BatchSkills),
        };

        private static readonly string[] PayloadParameterNames = { "items", "queryJson" };

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
        public void CuratedParameters_ReportADescription_OnBothWires()
        {
            var names = string.Join(",", Curated.Select(c => c.Skill).Distinct(StringComparer.Ordinal));
            var issues = new List<string>();

            foreach (var wire in new[] { "", "&wire=v2" })
            {
                var schema = JObject.Parse(SkillRouter.GetFilteredSchema("?names=" + names + wire));
                var skills = ((JArray)schema["skills"]).Cast<JObject>()
                    .ToDictionary(s => (string)s["name"], StringComparer.Ordinal);

                foreach (var (skill, parameter) in Curated)
                {
                    if (!skills.TryGetValue(skill, out var entry))
                    {
                        issues.Add($"schema{wire}: {skill} is not listed");
                        continue;
                    }

                    var listed = ((JArray)entry["parameters"]).Cast<JObject>()
                        .FirstOrDefault(p => (string)p["name"] == parameter);
                    if (listed == null)
                        issues.Add($"schema{wire}: {skill} has no parameter '{parameter}'");
                    else if (string.IsNullOrWhiteSpace((string)listed["description"]))
                        issues.Add($"schema{wire}: {skill}.{parameter} carries no description");
                }
            }

            Assert.That(issues, Is.Empty,
                "Each listed parameter needs a [SkillParam] note stating its space, unit, encoding or shape; without one the " +
                "agent falls back to reading the whole module doc:\n" + string.Join("\n", issues));
        }

        [Test]
        public void JsonPayloadParameters_OfCoveredModules_CarryANote()
        {
            var payloads = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => PayloadCoveredTypes.Contains(s.Method.DeclaringType))
                .SelectMany(s => s.Parameters
                    .Where(p => PayloadParameterNames.Contains(p.Name, StringComparer.Ordinal))
                    .Select(p => (skill: s.Name, parameter: p)))
                .ToList();
            Assume.That(payloads, Is.Not.Empty, "No JSON payload parameter found in the covered modules.");

            var bare = payloads
                .Where(x => string.IsNullOrWhiteSpace(x.parameter.GetCustomAttribute<SkillParamAttribute>()?.Description))
                .Select(x => $"{x.skill}.{x.parameter.Name}")
                .ToList();

            Assert.That(bare, Is.Empty,
                "The schema types these as plain strings; the note must list the element fields (derive them from the " +
                "item class passed to BatchExecutor.Execute<T>, or BatchTargetQuery for queryJson):\n" + string.Join("\n", bare));
        }
    }
}

// Producer:Betsy
