using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Every built-in skill that locates a GameObject by id declares entityId itself: on Unity 6000.4+ the legacy
    /// instanceId reads 0, and only a native entityId reaches one of several same-name objects (the router's synthetic
    /// entityId stays for custom skills, and there it can forward only a path that names one object). So a skill taking
    /// instanceId takes entityId, every pInstanceId comes with pEntityId, batch items do the same, a RequiresInput group
    /// that accepts pInstanceId accepts pEntityId, and a description that lists the locators names entityId (the router
    /// stops rewriting it once the skill declares the parameter).
    /// </summary>
    [TestFixture]
    public class EntityIdCoverageGuardTests
    {
        private static readonly Assembly BuiltIn = typeof(SkillRouter).Assembly;

        private static readonly string[] LocatorListsWithoutEntityId =
        {
            "name/instanceId/path", "name, instanceId, or path", "name / instanceId / path"
        };

        [Test]
        public void SkillsTakingAnInstanceId_DeclareItsEntityId()
        {
            var offenders = BuiltInSkills()
                .SelectMany(skill => MissingEntityIds(skill.ParameterNames).Select(missing => $"{skill.Name}: {missing}"))
                .ToList();

            Assert.That(offenders, Is.Empty,
                "Declare the entityId twin as a trailing `string ... = null` parameter and pass it to GameObjectFinder:\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void BatchItemsTakingAnInstanceId_DeclareItsEntityId()
        {
            var offenders = new List<string>();
            foreach (var type in BuiltIn.GetTypes().Where(IsBatchItemType).OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                var members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name)
                    .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(field => field.Name));
                offenders.AddRange(MissingEntityIds(members).Select(missing => $"{type.DeclaringType?.Name}.{type.Name}: {missing}"));
            }

            Assert.That(offenders, Is.Empty, "A batch item locates its object like the single skill does:\n" + string.Join("\n", offenders));
        }

        [Test]
        public void RequiredInputGroups_ThatAcceptAnInstanceId_AcceptItsEntityId()
        {
            var field = typeof(SkillPlanningService).GetField("_requiredInputGroups", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, "SkillPlanningService._requiredInputGroups was renamed.");
            var groups = (Dictionary<string, string[]>)field.GetValue(null);

            var offenders = groups
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .SelectMany(group => MissingEntityIds(group.Value).Select(missing => $"{group.Key}: {missing}"))
                .ToList();

            Assert.That(offenders, Is.Empty,
                "A call naming its target only by the entityId would be refused with 'Provide one of: ...':\n" + string.Join("\n", offenders));
        }

        [Test]
        public void DescriptionsListingTheLocators_NameEntityId()
        {
            var offenders = BuiltInSkills()
                .Where(skill => skill.ParameterNames.Contains("entityId", StringComparer.OrdinalIgnoreCase) &&
                                LocatorListsWithoutEntityId.Any(text => skill.Description.Contains(text)))
                .Select(skill => skill.Name)
                .ToList();

            Assert.That(offenders, Is.Empty,
                "Write name/entityId/instanceId/path: the router rewrites the description only for skills without their own entityId:\n" +
                string.Join("\n", offenders));
        }

        private static IEnumerable<SkillRouter.SkillInfo> BuiltInSkills() =>
            SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(skill => skill.Method?.DeclaringType?.Assembly == BuiltIn)
                .OrderBy(skill => skill.Name, StringComparer.Ordinal);

        private static bool IsBatchItemType(Type type) =>
            type == typeof(ComponentInvocationService.Request) ||
            (type.IsNested && type.DeclaringType.Name.EndsWith("Skills", StringComparison.Ordinal) &&
             type.Name.EndsWith("Item", StringComparison.Ordinal));

        /// <summary>The entityId twins a set of names lacks: entityId for instanceId, pEntityId for pInstanceId.</summary>
        private static IEnumerable<string> MissingEntityIds(IEnumerable<string> names)
        {
            var set = new HashSet<string>(names ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var name in set.OrderBy(name => name, StringComparer.Ordinal))
            {
                if (!name.EndsWith("InstanceId", StringComparison.OrdinalIgnoreCase))
                    continue;
                var prefix = name.Substring(0, name.Length - "InstanceId".Length);
                var twin = prefix.Length == 0 ? "entityId" : prefix + "EntityId";
                if (!set.Contains(twin))
                    yield return $"{name} without {twin}";
            }
        }
    }
}

// Producer:Betsy
