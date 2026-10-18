using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// The dryRun / plan side of the particle write skills. SkillPlanningService resolves locators (including objects an
    /// earlier step of a dry-run batch will create) and hands the target here; validation is the very Prepare path the
    /// skills execute, so a dryRun verdict and an execution verdict cannot drift. Planning never touches Unity state.
    /// </summary>
    public static partial class ParticleSkills
    {
        internal enum PlanTargetKind
        {
            None,
            Live,
            Pending,
            Failed
        }

        /// <summary>Where a planner's locator points: nothing given, a live object, an object still to be created, or an error.</summary>
        internal sealed class PlanTarget
        {
            public PlanTargetKind Kind;
            public GameObject Live;
            public string PendingPath;
            public object Error;

            public static readonly PlanTarget None = new PlanTarget { Kind = PlanTargetKind.None };
        }

        /// <summary>Everything a planner concluded, in a shape SkillPlanningService copies into the validation and plan.</summary>
        internal sealed class PlanOutcome
        {
            public readonly List<KeyValuePair<string, string>> Errors = new List<KeyValuePair<string, string>>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> MissingPackages = new List<string>();
            public readonly List<object> Steps = new List<object>();
            public readonly List<object> Create = new List<object>();
            public readonly List<object> Modify = new List<object>();
            public readonly List<object> Delete = new List<object>();
            public readonly Dictionary<string, object> Extra = new Dictionary<string, object>();
            public bool Semantic;

            public void Error(string field, string message) => Errors.Add(new KeyValuePair<string, string>(field, message));

            public void Warn(string message)
            {
                if (!Warnings.Contains(message))
                    Warnings.Add(message);
            }
        }

        /// <summary>
        /// JSON paths of keys repeated inside one object of <paramref name="json"/>, in document order. A streaming scan,
        /// so it reports what a parse would silently collapse; malformed text yields nothing (the router reports it).
        /// </summary>
        internal static List<string> DuplicateKeyPaths(string json)
        {
            var duplicates = new List<string>();
            if (string.IsNullOrEmpty(json))
                return duplicates;
            try
            {
                using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                {
                    var keys = new Stack<HashSet<string>>();
                    while (reader.Read())
                    {
                        switch (reader.TokenType)
                        {
                            case Newtonsoft.Json.JsonToken.StartObject:
                                keys.Push(new HashSet<string>(StringComparer.Ordinal));
                                break;
                            case Newtonsoft.Json.JsonToken.EndObject:
                                if (keys.Count > 0)
                                    keys.Pop();
                                break;
                            case Newtonsoft.Json.JsonToken.PropertyName:
                                if (keys.Count > 0 && !keys.Peek().Add((string)reader.Value) && duplicates.Count < 20)
                                    duplicates.Add(reader.Path);
                                break;
                        }
                    }
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // Malformed bodies are rejected by the router's own parse; nothing to add here.
            }
            return duplicates;
        }

        /// <summary>The write skills with a semantic planner (reads keep the generic dryRun and answer precisely themselves).</summary>
        internal static bool HasPlanner(string skill)
        {
            if (IsPlaybackSkill(skill))
                return true;
            switch (skill)
            {
                case "particle_create":
                case "particle_create_batch":
                case "particle_add_system":
                case "particle_add_system_batch":
                case "particle_configure":
                case "particle_configure_batch":
                case "particle_set_renderer":
                case "particle_set_renderer_batch":
                case "particle_set_bursts":
                case "particle_set_bursts_batch":
                case "particle_set_sub_emitters":
                case "particle_set_sub_emitters_batch":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Plans one particle write. <paramref name="resolve"/> resolves the locator keys of <c>args</c> with the given prefix
        /// ("" for name/instanceId/path/entityId, "parent" for parentName/..., as SkillPlanningService reads them).
        /// </summary>
        internal static PlanOutcome Plan(string skill, JObject args, Func<JObject, string, PlanTarget> resolve)
        {
            var outcome = new PlanOutcome();
            if (!ModuleAvailable)
            {
                outcome.MissingPackages.Add(ModulePackage);
                return outcome;
            }
#if COM_UNITY_MODULES_PARTICLESYSTEM
            PlanAvailable(skill, args ?? new JObject(), resolve, outcome);
#endif
            return outcome;
        }

#if COM_UNITY_MODULES_PARTICLESYSTEM
        private static void PlanAvailable(string skill, JObject args, Func<JObject, string, PlanTarget> resolve, PlanOutcome outcome)
        {
            if (IsPlaybackSkill(skill))
            {
                PlanPlayback(skill, args, resolve, outcome);
                return;
            }
            outcome.Semantic = true;
            bool batch = skill.EndsWith("_batch", StringComparison.Ordinal);
            var single = batch ? skill.Substring(0, skill.Length - "_batch".Length) : skill;
            if (!batch)
            {
                var itemPlan = PlanItem(single, args, "", resolve, outcome);
                outcome.Steps.Add(new Dictionary<string, object>
                {
                    ["index"] = 1,
                    ["action"] = StepAction(single),
                    ["target"] = itemPlan.Target
                });
                return;
            }

            var problems = new Problems();
            var token = Codec.Normalize(args["items"], "items", problems);
            if (problems.Any)
            {
                foreach (var problem in problems.Items)
                    outcome.Error(problem.Path, problem.Message);
                return;
            }
            if (!(token is JArray items) || items.Count == 0)
                return; // MISSING_PARAM / the router's own items check owns an absent or empty list.
            if (items.Count > MaxBatchItems)
            {
                outcome.Error("items", $"items holds {items.Count} items; one call takes at most {MaxBatchItems}.");
                return;
            }

            var itemKeys = single == "particle_create" ? BatchItemKeys.Create
                : single == "particle_set_bursts" ? BatchItemKeys.TargetBursts
                : single == "particle_set_sub_emitters" ? SubEmitterKeys.BatchItem
                : BatchItemKeys.TargetConfig;
            var previews = new List<object>();
            var subEmitterItems = new List<(int index, SubEmitterPlan plan, Dictionary<string, object> preview)>();
            var firstByTarget = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                var prefix = Codec.Index("items", i);
                if (!(items[i] is JObject item))
                {
                    outcome.Error(prefix, $"{prefix} must be an object, got {Codec.Describe(items[i])}.");
                    continue;
                }

                var keyProblems = new Problems();
                Codec.CheckKeys(item, prefix, keyProblems, itemKeys, null);
                foreach (var problem in keyProblems.Items)
                    outcome.Error(problem.Path, problem.Message);

                int errorsBefore = outcome.Errors.Count;
                var itemPlan = keyProblems.Any ? new ItemPlan { Target = prefix } : PlanItem(single, item, prefix, resolve, outcome);
                if (itemPlan.TargetKey != null)
                {
                    if (firstByTarget.TryGetValue(itemPlan.TargetKey, out var first))
                        outcome.Error(prefix, $"{prefix} targets the same object as items[{first}] ({itemPlan.Target}); merge them into one item.");
                    else
                        firstByTarget[itemPlan.TargetKey] = i;
                }

                var itemErrors = outcome.Errors.Skip(errorsBefore).Select(error => error.Value).ToArray();
                var preview = new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = itemPlan.Target,
                    ["valid"] = itemErrors.Length == 0 && !keyProblems.Any,
                    ["errors"] = keyProblems.Items.Select(problem => problem.Message).Concat(itemErrors).ToArray()
                };
                previews.Add(preview);
                if (itemPlan.SubEmitters != null && itemErrors.Length == 0)
                    subEmitterItems.Add((i, itemPlan.SubEmitters, preview));
            }

            // Cycles span items: judge the graph the whole batch leaves behind, as the executing batch does.
            if (subEmitterItems.Count > 0)
            {
                var checks = subEmitterItems.Select(item => (item.plan, problems: new Problems())).ToList();
                CheckSubEmitterCycles(checks);
                for (int i = 0; i < checks.Count; i++)
                {
                    if (!checks[i].problems.Any)
                        continue;
                    foreach (var problem in checks[i].problems.Items)
                        outcome.Error(problem.Path, problem.Message);
                    var preview = subEmitterItems[i].preview;
                    preview["valid"] = false;
                    preview["errors"] = ((string[])preview["errors"]).Concat(checks[i].problems.Items.Select(problem => problem.Message)).ToArray();
                }
            }

            outcome.Steps.Add(new Dictionary<string, object>
            {
                ["index"] = 1,
                ["action"] = StepAction(single) + " (batch, all-or-nothing)",
                ["target"] = $"{items.Count} items"
            });
            outcome.Extra["batchPreview"] = new Dictionary<string, object>
            {
                ["totalItems"] = items.Count,
                ["atomic"] = true,
                ["items"] = previews.ToArray()
            };
        }

        private sealed class ItemPlan
        {
            public string Target;
            public string TargetKey;
            /// <summary>A valid sub-emitter list of this item, for the batch-wide cycle check.</summary>
            public SubEmitterPlan SubEmitters;
        }

        private static string StepAction(string skill)
        {
            switch (skill)
            {
                case "particle_create": return "Create GameObject with ParticleSystem and ParticleSystemRenderer";
                case "particle_add_system": return "Add ParticleSystem (Unity adds its ParticleSystemRenderer)";
                case "particle_configure": return "Configure ParticleSystem modules";
                case "particle_set_renderer": return "Configure ParticleSystemRenderer";
                case "particle_set_sub_emitters": return "Replace ParticleSystem sub emitters";
                default: return "Replace ParticleSystem bursts";
            }
        }

        /// <summary>Plans one call or batch item through the shared Prepare entries, recording problems under their JSON paths.</summary>
        private static ItemPlan PlanItem(string skill, JObject args, string prefix, Func<JObject, string, PlanTarget> resolve, PlanOutcome outcome)
        {
            var plan = new ItemPlan { Target = string.IsNullOrEmpty(prefix) ? "(target)" : prefix };
            var problems = new Problems();
            var warnings = new List<string>();
            string Field(string name) => string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;

            if (skill == "particle_create")
            {
                GameObject parent = null;
                var parentTarget = resolve(args, "parent");
                if (parentTarget.Kind == PlanTargetKind.Failed)
                {
                    outcome.Error(Field("parent"), ErrorText(parentTarget.Error));
                    return plan;
                }
                if (parentTarget.Kind == PlanTargetKind.Pending)
                    outcome.Warn($"{Field("parent")} '{parentTarget.PendingPath}' will be created by an earlier step; checks that need the live object were skipped.");
                if (parentTarget.Kind == PlanTargetKind.Live)
                    parent = parentTarget.Live;

                var name = ReadString(args, "name") ?? DefaultSystemName;
                var create = PrepareCreate(name,
                    new Vector3(ReadFloat(args, "x", 0f), ReadFloat(args, "y", 0f), ReadFloat(args, "z", 0f)),
                    new Vector3(ReadFloat(args, "rotX", DefaultRotX), ReadFloat(args, "rotY", 0f), ReadFloat(args, "rotZ", 0f)),
                    ReadString(args, "space"), parent, args["configJson"], prefix, problems, warnings);
                var parentPath = parent != null ? GameObjectFinder.GetPath(parent) : parentTarget.PendingPath;
                var predictedPath = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;
                plan.Target = predictedPath;
                Report(problems, warnings, outcome);
                if (!problems.Any)
                {
                    outcome.Create.Add(new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["predictedPath"] = predictedPath,
                        ["components"] = new[] { "ParticleSystem", "ParticleSystemRenderer" },
                        ["configFields"] = create.Patch.Writes.Select(write => write.Field.Path).ToArray(),
                        ["defaults"] = new[] { "modules.main.playOnAwake=false (unless configJson sets it)", "renderer.material=the pipeline's default particle material" }
                    });
                }
                return plan;
            }

            var target = resolve(args, "");
            if (target.Kind == PlanTargetKind.None)
            {
                // A single call's missing locator is the generic required-input check's to report; a batch item has no such check.
                if (!string.IsNullOrEmpty(prefix))
                    outcome.Error(Field("target"), $"{prefix} needs a target: one of entityId, name, path or instanceId.");
                return plan;
            }
            if (target.Kind == PlanTargetKind.Failed)
            {
                outcome.Error(string.IsNullOrEmpty(prefix) ? "gameObject" : Field("target"), ErrorText(target.Error));
                return plan;
            }

            GameObject go = target.Live;
            ParticleBinding binding = null;
            if (target.Kind == PlanTargetKind.Pending)
            {
                plan.Target = target.PendingPath;
                outcome.Warn($"{Field("target")} '{target.PendingPath}' will be created by an earlier step; checks that need the live system were skipped.");
            }
            else
            {
                plan.Target = GameObjectFinder.GetPath(go);
                plan.TargetKey = UnityObjectIdUtility.GetEntityId(go);
                if (skill != "particle_add_system")
                {
                    var bindError = BindExisting(go, out binding);
                    if (bindError != null)
                    {
                        outcome.Error(string.IsNullOrEmpty(prefix) ? "gameObject" : Field("target"), ErrorText(bindError));
                        return plan;
                    }
                }
            }

            switch (skill)
            {
                case "particle_add_system":
                {
                    var patch = PrepareAdd(go, args["configJson"], prefix, problems, warnings);
                    Report(problems, warnings, outcome);
                    if (!problems.Any)
                    {
                        outcome.Create.Add(new Dictionary<string, object> { ["target"] = plan.Target, ["component"] = "ParticleSystem" });
                        outcome.Create.Add(new Dictionary<string, object> { ["target"] = plan.Target, ["component"] = "ParticleSystemRenderer", ["note"] = "added by Unity with the ParticleSystem" });
                        outcome.Modify.AddRange(PreviewModify(patch, null, plan.Target));
                    }
                    break;
                }
                case "particle_configure":
                {
                    var patch = PrepareConfigure(binding, args["configJson"], prefix, problems, warnings);
                    Report(problems, warnings, outcome);
                    if (!problems.Any)
                        outcome.Modify.AddRange(PreviewModify(patch, binding, plan.Target));
                    break;
                }
                case "particle_set_renderer":
                {
                    var patch = PrepareRenderer(binding, args["configJson"], prefix, problems, warnings);
                    Report(problems, warnings, outcome);
                    if (!problems.Any)
                        outcome.Modify.AddRange(PreviewModify(patch, binding, plan.Target));
                    break;
                }
                case "particle_set_sub_emitters":
                {
                    // Nested system locators resolve like the target does, pending objects of a /skills/batch dry run included.
                    var subPlan = PrepareSubEmitterList(binding, args["subEmittersJson"], prefix, locator => resolve(locator, ""), problems, warnings);
                    if (!problems.Any && string.IsNullOrEmpty(prefix))
                        CheckSubEmitterCycles(new[] { (subPlan, problems) });
                    Report(problems, warnings, outcome);
                    if (!problems.Any)
                    {
                        plan.SubEmitters = subPlan;
                        outcome.Modify.Add(new Dictionary<string, object>
                        {
                            ["target"] = plan.Target,
                            ["component"] = "ParticleSystem",
                            ["field"] = SubEmittersPath,
                            ["before"] = binding != null ? (JToken)ReadSubEmitters(binding.System) : null,
                            ["after"] = PlannedSubEmittersToJson(subPlan)
                        });
                    }
                    break;
                }
                default:
                {
                    var bursts = PrepareBurstList(binding, args["burstsJson"], prefix, problems, warnings);
                    Report(problems, warnings, outcome);
                    if (!problems.Any)
                    {
                        outcome.Modify.Add(new Dictionary<string, object>
                        {
                            ["target"] = plan.Target,
                            ["component"] = "ParticleSystem",
                            ["field"] = "modules.emission.bursts",
                            ["before"] = binding != null ? (JToken)ReadBursts(binding.System) : null,
                            ["after"] = new JArray(bursts.Select(Codec.BurstToJson))
                        });
                    }
                    break;
                }
            }
            return plan;
        }

        private static void Report(Problems problems, List<string> warnings, PlanOutcome outcome)
        {
            foreach (var problem in problems.Items)
                outcome.Error(problem.Path, problem.Message);
            if (problems.Omitted > 0)
                outcome.Error("", $"(+{problems.Omitted} more problem(s) omitted)");
            foreach (var warning in warnings)
                outcome.Warn(warning);
        }

        private static string ErrorText(object error) =>
            SkillResultHelper.TryGetError(error, out var text) ? text : error?.ToString() ?? "unknown error";

        private static string ReadString(JObject args, string key)
        {
            var token = args[key];
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static float ReadFloat(JObject args, string key, float fallback)
        {
            var token = args[key];
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                return fallback;
            try { return token.Value<float>(); }
            catch (Exception) { return fallback; }
        }
#endif
    }
}

// Producer:Betsy
