using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Compiled with and without the built-in Particle System module: the module gate, the shared locator and the
    /// MISSING_PACKAGE answer. Nothing here references a particle type, so a project that disabled
    /// com.unity.modules.particlesystem still compiles the whole package (the module-dependent files are wrapped in
    /// COM_UNITY_MODULES_PARTICLESYSTEM, the asmdef's version define for that module).
    /// </summary>
    public static partial class ParticleSkills
    {
        internal const string ModulePackage = "com.unity.modules.particlesystem";
        internal const string ContractVersion = "1.0";

        /// <summary>
        /// Test seam: makes every particle skill, the planners and particle_get_capabilities answer exactly as in a project
        /// without the Particle System module. The only way to exercise that path without changing the project's
        /// modules, which a test must never do.
        /// </summary>
        internal static bool SimulateMissingModuleForTests;

        internal static bool ModuleCompiledIn
        {
            get
            {
#if COM_UNITY_MODULES_PARTICLESYSTEM
                return true;
#else
                return false;
#endif
            }
        }

        internal static bool ModuleAvailable => ModuleCompiledIn && !SimulateMissingModuleForTests;

        /// <summary>The answer of every particle skill (except capabilities) when the module is missing.</summary>
        internal static Dictionary<string, object> MissingModuleError(string skill) => new Dictionary<string, object>
        {
            ["error"] = $"{skill} needs Unity's built-in Particle System module ({ModulePackage}), which this project has disabled. " +
                        "Enable it in Package Manager > Built-in > Particle System (or install that package id); nothing was changed.",
            ["errorCode"] = "MISSING_PACKAGE",
            ["retryStrategy"] = SkillErrorResponse.RetryInstallAndRetry,
            ["requiredPackage"] = ModulePackage,
            ["suggestedFixes"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["action"] = "install_package",
                    ["skill"] = "package_install",
                    ["args"] = new Dictionary<string, object> { ["packageId"] = ModulePackage },
                    ["reason"] = "Enabling the built-in module adds it to the project manifest; wait for the domain reload, then retry."
                }
            },
            ["relatedSkills"] = new[] { "package_install", "particle_get_capabilities" }
        };

        /// <summary>The seam check at the top of every entry point: the missing-module answer, or null to proceed.</summary>
        internal static object Gate(string skill) => SimulateMissingModuleForTests ? MissingModuleError(skill) : null;

        /// <summary>particle_get_capabilities without the module: a structured "unavailable" answer rather than an error.</summary>
        internal static Dictionary<string, object> UnavailableCapabilities() => new Dictionary<string, object>
        {
            ["success"] = true,
            ["available"] = false,
            ["schemaVersion"] = 1,
            ["contractVersion"] = ContractVersion,
            ["unityVersion"] = Application.unityVersion,
            ["package"] = ModulePackage,
            ["unavailableReason"] = $"The built-in Particle System module ({ModulePackage}) is disabled in this project, so no particle skill can run. Enable it in Package Manager > Built-in.",
            ["modules"] = new object[0],
            ["valueTypes"] = new Dictionary<string, object>(),
            ["limits"] = new Dictionary<string, object>()
        };

        /// <summary>
        /// Whether Unity adds this component on its own with another one: a ParticleSystem brings its
        /// ParticleSystemRenderer. Workflow restore uses it to restore onto the instance the owner re-created instead
        /// of adding a second one, which Unity refuses. Compared by name, so the check needs no particle types.
        /// </summary>
        internal static bool IsAutoAddedCompanion(System.Type componentType) =>
            componentType != null && componentType.FullName == "UnityEngine.ParticleSystemRenderer";

        /// <summary>
        /// Adds the ParticleSystem and ParticleSystemRenderer of every GameObject already in <paramref name="targets"/>, so
        /// the semantic diff (?diff=1) of a particle write reports the components actually written. A no-op without the module.
        /// </summary>
        internal static void AppendComponentTargets(List<UnityEngine.Object> targets)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            if (targets == null)
                return;
            foreach (var go in targets.OfType<GameObject>().ToList())
            {
                var system = go.GetComponent<ParticleSystem>();
                if (system != null && !targets.Contains(system))
                    targets.Add(system);
                var renderer = go.GetComponent<ParticleSystemRenderer>();
                if (renderer != null && !targets.Contains(renderer))
                    targets.Add(renderer);
            }
#endif
        }

        /// <summary>The name / entityId / instanceId / path locator of a single call or batch item.</summary>
        internal readonly struct Locator
        {
            public readonly string Name;
            public readonly int InstanceId;
            public readonly string Path;
            public readonly string EntityId;

            public Locator(string name, int instanceId, string path, string entityId)
            {
                Name = name;
                InstanceId = instanceId;
                Path = path;
                EntityId = entityId;
            }

            public bool IsEmpty =>
                string.IsNullOrEmpty(Name) && InstanceId == 0 && string.IsNullOrEmpty(Path) && !UnityObjectIdUtility.IsGivenEntityId(EntityId);

            /// <summary>How a batch result names the item: the most precise locator given.</summary>
            public string Describe() =>
                UnityObjectIdUtility.IsGivenEntityId(EntityId) ? "entityId:" + EntityId.Trim()
                : InstanceId != 0 ? "instanceId:" + InstanceId
                : !string.IsNullOrEmpty(Path) ? Path
                : Name ?? "(no target)";
        }
    }
}

// Producer:Betsy
