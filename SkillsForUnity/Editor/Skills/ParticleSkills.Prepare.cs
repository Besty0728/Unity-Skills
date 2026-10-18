#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnitySkills
{
    public static partial class ParticleSkills
    {
        internal const int SchemaVersion = 1;

        /// <summary>The top-level keys of each payload (a nested class: static initializers stay out of the partial class).</summary>
        private static class RootKeys
        {
            public static readonly string[] Config = { "schemaVersion", "system", "modules" };
            public static readonly string[] Renderer = { "schemaVersion", "renderer" };
        }

        /// <summary>Whether an empty patch is a rejected no-op (configure) or simply "no initial configuration" (create / add).</summary>
        internal enum ConfigKind
        {
            Configure,
            Initial
        }

        /// <summary>
        /// What the shared Prepare path knows about the target it validates against: a live system, a system about to
        /// be created (registry defaults stand in for live values), or an object an earlier dry-run step will create
        /// (state-dependent checks are skipped).
        /// </summary>
        internal sealed class PrepareTarget
        {
            public ParticleBinding Binding;
            public bool UseDefaults;

            public static PrepareTarget Live(ParticleBinding binding) => new PrepareTarget { Binding = binding };
            public static readonly PrepareTarget NewSystem = new PrepareTarget { UseDefaults = true };
            public static readonly PrepareTarget Unknown = new PrepareTarget();
        }

        // ===================== configJson (system + modules) =====================

        /// <summary>
        /// Parses and validates a configure / create / add configJson into a <see cref="Patch"/>. The single entry both
        /// execution and the dryRun planner call: same parsing, same verdict, and nothing here touches Unity state.
        /// </summary>
        internal static Patch PrepareConfig(JToken payload, string basePath, PrepareTarget target, ConfigKind kind,
            Problems problems, List<string> warnings)
        {
            var patch = new Patch();
            if (payload == null || payload.Type == JTokenType.Null)
            {
                if (kind == ConfigKind.Configure)
                    problems.Add(basePath, $"{basePath} is required: {{\"schemaVersion\": 1, \"modules\": {{...}}}}.", CodeMissingParam);
                return patch;
            }

            var root = Codec.ExpectObject(Codec.Normalize(payload, basePath, problems), basePath, problems,
                "an object {\"schemaVersion\": 1, \"system\"?: {...}, \"modules\"?: {...}}");
            if (root == null || !CheckSchemaRoot(root, basePath, RootKeys.Config, problems))
                return patch;

            if (root["system"] != null)
                ParseModuleFields(Registry.System, root["system"], Codec.Join(basePath, "system"), patch, problems);

            if (root["modules"] != null)
            {
                var modulesPath = Codec.Join(basePath, "modules");
                var modules = Codec.ExpectObject(root["modules"], modulesPath, problems, "an object of module name -> fields");
                if (modules != null)
                {
                    foreach (var property in modules.Properties())
                    {
                        var modulePath = Codec.Join(modulesPath, property.Name);
                        var module = Registry.Module(property.Name);
                        if (module == null)
                        {
                            problems.Add(modulePath, Codec.UnknownKeyMessage(property.Name, modulesPath,
                                Registry.ParticleModules.Select(candidate => candidate.Id)));
                            continue;
                        }
                        if (!module.Configurable)
                        {
                            problems.Add(modulePath, $"Module '{module.Id}' is not configurable in this version: {module.UnavailableReason}");
                            continue;
                        }
                        ParseModuleFields(module, property.Value, modulePath, patch, problems);
                    }
                }
            }

            if (kind == ConfigKind.Configure && patch.IsEmpty && !problems.Any)
                problems.Add(basePath, $"{basePath} changes nothing: add at least one field under \"system\" or \"modules\".");

            if (!problems.Any)
                ValidatePatch(patch, basePath, target, problems, warnings);
            return patch;
        }

        /// <summary>particle_set_renderer's configJson: {"schemaVersion": 1, "renderer": {...}}.</summary>
        internal static Patch PrepareRendererConfig(JToken payload, string basePath, PrepareTarget target, Problems problems, List<string> warnings)
        {
            var patch = new Patch();
            if (payload == null || payload.Type == JTokenType.Null)
            {
                problems.Add(basePath, $"{basePath} is required: {{\"schemaVersion\": 1, \"renderer\": {{...}}}}.", CodeMissingParam);
                return patch;
            }

            var root = Codec.ExpectObject(Codec.Normalize(payload, basePath, problems), basePath, problems,
                "an object {\"schemaVersion\": 1, \"renderer\": {...}}");
            if (root == null || !CheckSchemaRoot(root, basePath, RootKeys.Renderer, problems))
                return patch;

            if (root["renderer"] == null)
                problems.Add(Codec.Join(basePath, "renderer"), $"{Codec.Join(basePath, "renderer")} is required.", CodeMissingParam);
            else
                ParseModuleFields(Registry.Renderer, root["renderer"], Codec.Join(basePath, "renderer"), patch, problems);

            if (patch.IsEmpty && !problems.Any)
                problems.Add(basePath, $"{basePath} changes nothing: add at least one field under \"renderer\".");

            if (!problems.Any)
                ValidatePatch(patch, basePath, target, problems, warnings);
            return patch;
        }

        private static bool CheckSchemaRoot(JObject root, string basePath, string[] allowed, Problems problems)
        {
            if (!Codec.CheckKeys(root, basePath, problems, allowed, new[] { "schemaVersion" }))
                return false;
            var versionPath = Codec.Join(basePath, "schemaVersion");
            if (!Codec.ReadInt(root["schemaVersion"], versionPath, problems, out var version))
                return false;
            if (version != SchemaVersion)
            {
                problems.Add(versionPath, $"Unsupported {versionPath} {version}: this Editor speaks schemaVersion {SchemaVersion} (see particle_get_capabilities).");
                return false;
            }
            return true;
        }

        private static void ParseModuleFields(ModuleAdapter module, JToken token, string path, Patch patch, Problems problems)
        {
            var fields = Codec.ExpectObject(token, path, problems, $"an object of {module.Id} field values");
            if (fields == null)
                return;

            foreach (var property in fields.Properties())
            {
                var fieldPath = Codec.Join(path, property.Name);
                var field = module.Field(property.Name);
                if (field == null)
                {
                    problems.Add(fieldPath, Codec.UnknownKeyMessage(property.Name, path, module.Fields.Select(candidate => candidate.Name)));
                    continue;
                }
                if (!field.Writable)
                {
                    problems.Add(fieldPath, $"{fieldPath} is read-only: {field.ReadOnlyReason}");
                    continue;
                }
                if (Codec.ReadField(field, property.Value, fieldPath, problems, out var value))
                    patch.Writes.Add(new FieldWrite { Field = field, Value = value, Path = fieldPath });
            }

            // Registry order is application order and response order, whatever order the JSON used.
            var order = Registry.Modules;
            patch.Writes.Sort((a, b) =>
            {
                int byModule = order.IndexOf(a.Field.Module).CompareTo(order.IndexOf(b.Field.Module));
                return byModule != 0 ? byModule : a.Field.Module.Fields.IndexOf(a.Field).CompareTo(b.Field.Module.Fields.IndexOf(b.Field));
            });
        }

        /// <summary>
        /// The cross-field rules of a parsed patch: uniform-vs-axis fields of a flag (startSize3D, separateAxes), triplets
        /// that must share one curve mode, each module's own rule, and the disabled-module warning. Effective values
        /// come from the patch, then the live system, then (for a system about to be created) registry defaults.
        /// </summary>
        internal static void ValidatePatch(Patch patch, string basePath, PrepareTarget target, Problems problems, List<string> warnings)
        {
            var touched = patch.Modules.ToList();
            var checks = Registry.Modules
                .Where(module => touched.Contains(module))
                .Select(module => new ModuleCheck
                {
                    Module = module,
                    Patch = patch,
                    Binding = target.Binding,
                    UseDefaults = target.UseDefaults,
                    Problems = problems,
                    Warnings = warnings,
                    BasePath = basePath
                })
                .ToList();

            foreach (var check in checks)
            {
                CheckAxisGroups(check);
                CheckSameModeGroups(check);
                check.Module.Check?.Invoke(check);
            }

            // Last, so a module that also fails a rule is reported by the rule first.
            foreach (var check in checks)
                WarnIfDisabled(check);
        }

        private static void CheckAxisGroups(ModuleCheck check)
        {
            foreach (var group in check.Module.AxisGroups)
            {
                var uniform = group.Uniform.Where(check.Has).ToList();
                var axes = group.Axes.Where(check.Has).ToList();
                if (uniform.Count == 0 && axes.Count == 0)
                    continue;

                var flagRequested = check.Has(group.Flag);
                var flag = check.Effective(group.Flag) as bool?;
                var flagNote = flagRequested ? " in this patch" : "";
                if (flag == true)
                {
                    foreach (var field in uniform)
                        check.Error(field, $"{check.PathOf(field)} applies only while {group.Flag} is false (it is true{flagNote}); send {string.Join("/", group.Axes)} instead, or \"{group.Flag}\": false in the same patch.");
                }
                else if (flag == false)
                {
                    // A group can have no uniform field (rotation: z is always active), then there is nothing else to suggest.
                    var alternative = group.Uniform.Length > 0 ? $", or use {string.Join("/", group.Uniform)}" : "";
                    foreach (var field in axes)
                        check.Error(field, $"{check.PathOf(field)} applies only while {group.Flag} is true (it is false{flagNote}); send \"{group.Flag}\": true in the same patch{alternative}.");
                }
                else if (uniform.Count > 0 && axes.Count > 0)
                {
                    check.Error(axes[0], $"{check.PathOf(axes[0])} and {check.PathOf(uniform[0])} cannot be sent together: {group.Flag} decides which one applies.");
                }
            }
        }

        private static void CheckSameModeGroups(ModuleCheck check)
        {
            foreach (var group in check.Module.SameModeGroups)
            {
                bool flagTurnedOn = group.WhenFlag != null && check.Requested(group.WhenFlag, out var requestedFlag) && requestedFlag is bool on && on;
                if (!group.Fields.Any(check.Has) && !flagTurnedOn)
                    continue;
                if (group.WhenFlag != null && !(check.Effective(group.WhenFlag) is bool active && active))
                    continue;

                var modes = new List<(string field, ParticleSystemCurveMode mode)>();
                foreach (var field in group.Fields)
                {
                    if (check.Effective(field) is ParticleSystem.MinMaxCurve curve)
                        modes.Add((field, curve.mode));
                }
                if (modes.Select(entry => entry.mode).Distinct().Count() <= 1)
                    continue;

                var blame = group.Fields.FirstOrDefault(check.Has) ?? group.WhenFlag;
                check.Error(blame, $"{string.Join(", ", group.Fields)} must share one curve mode (the Inspector edits them as one triplet with a single mode selector): " +
                    string.Join(", ", modes.Select(entry => $"{entry.field} is {entry.mode}")) +
                    ". Send all of them with the same mode in this patch.");
            }
        }

        private static void WarnIfDisabled(ModuleCheck check)
        {
            var enabled = check.Module.EnabledField;
            if (enabled == null || check.Module.Section != SectionModules)
                return;
            if (check.Effective("enabled") is bool on && !on)
                check.Warn($"{check.BasePath}.{check.Module.Path} is disabled: the values were validated and are stored, but have no effect until \"enabled\": true.");
        }

        // ===================== bursts =====================

        /// <summary>
        /// particle_set_bursts' burstsJson: the complete new list (an empty array clears it). At most 8 bursts, Unity's
        /// limit; a burst after the system's duration is kept but warned about.
        /// </summary>
        internal static ParticleSystem.Burst[] PrepareBursts(JToken payload, string basePath, PrepareTarget target, Problems problems, List<string> warnings)
        {
            if (payload == null || payload.Type == JTokenType.Null)
            {
                problems.Add(basePath, $"{basePath} is required: the complete burst list (an empty array [] clears it).", CodeMissingParam);
                return null;
            }

            var token = Codec.Normalize(payload, basePath, problems);
            if (token == null)
                return null;
            if (!(token is JArray array))
            {
                problems.Add(basePath, $"{basePath} must be an array of bursts, got {Codec.Describe(token)}.", CodeTypeMismatch);
                return null;
            }
            if (array.Count > MaxBursts)
            {
                problems.Add(basePath, $"{basePath} holds {array.Count} bursts; Unity keeps at most {MaxBursts}.");
                return null;
            }

            var bursts = new List<ParticleSystem.Burst>();
            bool ok = true;
            for (int i = 0; i < array.Count; i++)
            {
                if (Codec.ReadBurst(array[i], Codec.Index(basePath, i), problems, out var burst))
                    bursts.Add(burst);
                else
                    ok = false;
            }
            if (!ok)
                return null;

            if (target.Binding != null)
            {
                var duration = target.Binding.System.main.duration;
                for (int i = 0; i < bursts.Count; i++)
                {
                    if (bursts[i].time > duration)
                        warnings.Add($"{Codec.Index(basePath, i)}.time {bursts[i].time} is after the system's duration ({duration} s): the burst never fires.");
                }
                if (bursts.Count > 0 && !target.Binding.System.emission.enabled)
                    warnings.Add("modules.emission is disabled: bursts are stored but do not fire until emission is enabled.");
            }
            return bursts.ToArray();
        }

        // ===================== dryRun preview =====================

        /// <summary>One changes.modify entry per field: what it reads now (null when unknown) and what it will hold.</summary>
        internal static List<object> PreviewModify(Patch patch, ParticleBinding binding, string targetPath)
        {
            var entries = new List<object>();
            foreach (var write in patch.Writes)
            {
                entries.Add(new Dictionary<string, object>
                {
                    ["target"] = targetPath,
                    ["component"] = write.Field.Module.TargetsRenderer ? "ParticleSystemRenderer" : "ParticleSystem",
                    ["field"] = write.Field.Path,
                    ["before"] = binding != null ? Codec.WriteField(write.Field, write.Field.Read(binding)) : null,
                    ["after"] = Codec.WriteField(write.Field, write.Value)
                });
            }
            return entries;
        }
    }
}
#endif

// Producer:Betsy
