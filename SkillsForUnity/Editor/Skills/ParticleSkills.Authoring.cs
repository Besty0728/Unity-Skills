#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnitySkills
{
    public static partial class ParticleSkills
    {
        // ===================== Shared Prepare entries (execution and dryRun planners call these) =====================

        /// <summary>A configJson argument as a token: JSON text is parsed strictly by the codec (duplicate keys rejected).</summary>
        internal static JToken PayloadToken(string json) => json == null ? null : new JValue(json);

        internal static Patch PrepareConfigure(ParticleBinding binding, JToken config, string prefix, Problems problems, List<string> warnings)
        {
            CheckWritable(binding?.GameObject, binding?.System, Codec.Join(prefix, "target"), problems);
            return PrepareConfig(config, Codec.Join(prefix, "configJson"), binding != null ? PrepareTarget.Live(binding) : PrepareTarget.Unknown,
                ConfigKind.Configure, problems, warnings);
        }

        internal static Patch PrepareRenderer(ParticleBinding binding, JToken config, string prefix, Problems problems, List<string> warnings)
        {
            CheckWritable(binding?.GameObject, binding?.System, Codec.Join(prefix, "target"), problems);
            if (binding != null && binding.Renderer == null)
                problems.Add(Codec.Join(prefix, "target"), $"'{GameObjectFinder.GetPath(binding.GameObject)}' has a ParticleSystem but no ParticleSystemRenderer; add one with component_add (componentType 'ParticleSystemRenderer').", CodeTargetNotFound);
            return PrepareRendererConfig(config, Codec.Join(prefix, "configJson"), binding != null ? PrepareTarget.Live(binding) : PrepareTarget.Unknown, problems, warnings);
        }

        internal static ParticleSystem.Burst[] PrepareBurstList(ParticleBinding binding, JToken bursts, string prefix, Problems problems, List<string> warnings)
        {
            CheckWritable(binding?.GameObject, binding?.System, Codec.Join(prefix, "target"), problems);
            return PrepareBursts(bursts, Codec.Join(prefix, "burstsJson"), binding != null ? PrepareTarget.Live(binding) : PrepareTarget.Unknown, problems, warnings);
        }

        /// <summary>particle_add_system refuses an object that already has a system: an existing configuration is never reset.</summary>
        internal static Patch PrepareAdd(GameObject go, JToken config, string prefix, Problems problems, List<string> warnings)
        {
            var targetPath = Codec.Join(prefix, "target");
            CheckWritable(go, null, targetPath, problems);
            if (go != null && go.GetComponent<ParticleSystem>() != null)
            {
                problems.Add(targetPath, $"'{GameObjectFinder.GetPath(go)}' already has a ParticleSystem; particle_add_system never resets an existing one. Change it with particle_configure.");
            }
            else if (go != null && go.GetComponent<ParticleSystemRenderer>() != null)
            {
                problems.Add(targetPath, $"'{GameObjectFinder.GetPath(go)}' has a ParticleSystemRenderer without a ParticleSystem. Remove it first (component_remove, componentType 'ParticleSystemRenderer'): " +
                    "Unity removes the renderer together with the system, so undoing the add could not give that renderer back.");
            }
            return PrepareConfig(config, Codec.Join(prefix, "configJson"), PrepareTarget.NewSystem, ConfigKind.Initial, problems, warnings);
        }

        internal static CreatePlan PrepareCreate(string name, Vector3 position, Vector3 eulerAngles, string space, GameObject parent,
            JToken config, string prefix, Problems problems, List<string> warnings)
        {
            CheckWritable(null, null, Codec.Join(prefix, "target"), problems);
            if (string.IsNullOrWhiteSpace(name))
                problems.Add(Codec.Join(prefix, "name"), $"{Codec.Join(prefix, "name")} must be a non-empty object name.", CodeMissingParam);

            bool worldSpace = false;
            if (!string.IsNullOrEmpty(space) && !space.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                if (space.Equals("world", StringComparison.OrdinalIgnoreCase))
                    worldSpace = true;
                else
                    problems.Add(Codec.Join(prefix, "space"), $"Invalid value '{space}' for {Codec.Join(prefix, "space")}. Valid values: local, world.");
            }

            if (parent != null && !IsInOpenScene(parent))
                problems.Add(Codec.Join(prefix, "parent"), $"Parent '{parent.name}' is not in an open scene.");

            var patch = PrepareConfig(config, Codec.Join(prefix, "configJson"), PrepareTarget.NewSystem, ConfigKind.Initial, problems, warnings);
            return new CreatePlan
            {
                Name = name,
                Parent = parent,
                WorldSpace = worldSpace,
                Position = position,
                EulerAngles = eulerAngles,
                Patch = patch
            };
        }

        // ===================== Single skills =====================

        internal static object CreateCore(string name, float x, float y, float z, Locator parent, float rotX, float rotY, float rotZ, string space, string configJson)
        {
            GameObject parentGo = null;
            if (!parent.IsEmpty)
            {
                var (found, error) = GameObjectFinder.FindOrError(parent.Name, parent.InstanceId, parent.Path, entityId: parent.EntityId);
                if (error != null)
                    return error;
                parentGo = found;
            }

            var problems = new Problems();
            var warnings = new List<string>();
            var plan = PrepareCreate(name, new Vector3(x, y, z), new Vector3(rotX, rotY, rotZ), space, parentGo, PayloadToken(configJson), "", problems, warnings);
            if (problems.Any)
                return ProblemsError("particle_create", problems);
            return ParticleSkills.CreateSystem(plan, warnings);
        }

        internal static object AddSystemCore(Locator locator, string configJson)
        {
            var (go, error) = FindTarget(locator);
            if (error != null)
                return error;

            var problems = new Problems();
            var warnings = new List<string>();
            var patch = PrepareAdd(go, PayloadToken(configJson), "", problems, warnings);
            if (problems.Any)
                return ProblemsError("particle_add_system", problems);
            return ParticleSkills.AddSystem(go, patch, warnings);
        }

        internal static object ConfigureCore(Locator locator, string configJson)
        {
            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;

            var problems = new Problems();
            var warnings = new List<string>();
            var patch = PrepareConfigure(binding, PayloadToken(configJson), "", problems, warnings);
            if (problems.Any)
                return ProblemsError("particle_configure", problems);
            return ParticleSkills.WritePatch(binding, patch, $"Particle: configure {binding.GameObject.name}", warnings);
        }

        internal static object SetRendererCore(Locator locator, string configJson)
        {
            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;

            var problems = new Problems();
            var warnings = new List<string>();
            var patch = PrepareRenderer(binding, PayloadToken(configJson), "", problems, warnings);
            if (problems.Any)
                return ProblemsError("particle_set_renderer", problems);
            return ParticleSkills.WritePatch(binding, patch, $"Particle: set renderer of {binding.GameObject.name}", warnings);
        }

        internal static object SetBurstsCore(Locator locator, string burstsJson)
        {
            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;

            var problems = new Problems();
            var warnings = new List<string>();
            var bursts = PrepareBurstList(binding, PayloadToken(burstsJson), "", problems, warnings);
            if (problems.Any)
                return ProblemsError("particle_set_bursts", problems);
            return ParticleSkills.WriteBursts(binding, bursts, $"Particle: set bursts of {binding.GameObject.name}", warnings);
        }

        // ===================== Batches =====================

        /// <summary>One batch item after the validation pass: its problems, or the closure that applies it.</summary>
        internal sealed class BatchPrep
        {
            public int Index;
            public string Target;
            /// <summary>The target object's entityId, to refuse two items writing one object (their order would matter).</summary>
            public string TargetKey;
            public readonly Problems Problems = new Problems();
            public readonly List<string> Warnings = new List<string>();
            /// <summary>A ready error result (the finder's TARGET_NOT_FOUND keeps its own code and candidates).</summary>
            public object Failure;
            public Func<Dictionary<string, object>> Run;
            public bool Ok => Failure == null && !Problems.Any;
        }

        /// <summary>The keys each batch item accepts; anything else is rejected, not ignored with a warning.</summary>
        internal static class BatchItemKeys
        {
            public static readonly string[] Info = { "name", "entityId", "instanceId", "path", "includeModules" };
            public static readonly string[] TargetConfig = { "name", "entityId", "instanceId", "path", "configJson" };
            public static readonly string[] TargetBursts = { "name", "entityId", "instanceId", "path", "burstsJson" };
            public static readonly string[] Create =
            {
                "name", "x", "y", "z", "rotX", "rotY", "rotZ", "space",
                "parentName", "parentInstanceId", "parentPath", "parentEntityId", "configJson"
            };
        }

        /// <summary>
        /// The batch flow of every particle batch: parse the items strictly, validate every item through the same
        /// Prepare path the single skill uses, refuse two items on one target, and only then run them through
        /// <see cref="BatchExecutor"/>. With <paramref name="atomic"/>, one invalid item means no item is applied (the
        /// valid ones are reported as reverted, which is what BatchExecutor's all-or-nothing envelope says), and a
        /// failure while applying reverts every item already applied. <paramref name="validateAll"/>, when given, runs
        /// after every item passed its own checks, for rules that span items (sub-emitter cycles across the batch).
        /// </summary>
        internal static object RunBatch<TItem>(string itemsJson, string skill, string[] itemKeys, bool atomic,
            Action<TItem, BatchPrep> prepare, Func<TItem, string> describe, Action<IReadOnlyList<BatchPrep>> validateAll = null) where TItem : class
        {
            var rejected = ValidateBatch(itemsJson, skill, itemKeys, prepare, out var preps);
            if (rejected != null)
                return rejected;

            bool anyInvalid = preps.Any(prep => !prep.Ok);
            int index = 0;
            return BatchExecutor.Execute<TItem>(itemsJson, item =>
            {
                var prep = preps[index++];
                if (prep.Failure != null)
                    return Stamp(AsDictionary(prep.Failure), prep.Target, false);
                if (prep.Problems.Any)
                    return Stamp(ProblemsError(prep.Target, prep.Problems), prep.Target, false);
                if (atomic && anyInvalid)
                {
                    return new Dictionary<string, object>
                    {
                        ["target"] = prep.Target,
                        ["success"] = true,
                        ["validated"] = true,
                        ["applied"] = false
                    };
                }
                return Stamp(prep.Run(), prep.Target, null);
            }, describe, atomic: atomic);
        }

        /// <summary>
        /// The validation half of every particle batch: parses the items strictly, runs <paramref name="prepare"/> on each
        /// one and refuses two items on one target. Returns the error of a structurally invalid list (nothing to report
        /// per item), otherwise null with one <see cref="BatchPrep"/> per item, valid or not.
        /// </summary>
        internal static object ValidateBatch<TItem>(string itemsJson, string skill, string[] itemKeys,
            Action<TItem, BatchPrep> prepare, out List<BatchPrep> preps) where TItem : class
        {
            preps = null;
            if (string.IsNullOrWhiteSpace(itemsJson))
                return Error(CodeMissingParam, "items is required: a JSON array of item objects.", parameter: "items");

            var structural = new Problems();
            var token = Codec.ParseText(itemsJson, "items", structural);
            if (structural.Any)
                return ProblemsError(skill, structural);
            if (!(token is JArray array))
                return Error(CodeTypeMismatch, $"items must be a JSON array of item objects, got {Codec.Describe(token)}.", parameter: "items");
            if (array.Count == 0)
                return Error(CodeSemantic, "items must hold at least one item.", parameter: "items");
            if (array.Count > MaxBatchItems)
                return Error(CodeSemantic, $"items holds {array.Count} items; one call takes at most {MaxBatchItems}. Split the batch.", parameter: "items");

            var serializer = JsonSerializer.Create(SkillsCommon.JsonSettings);
            var prepared = new List<BatchPrep>();
            for (int i = 0; i < array.Count; i++)
            {
                var itemPath = Codec.Index("items", i);
                var prep = new BatchPrep { Index = i, Target = itemPath };
                prepared.Add(prep);
                if (!(array[i] is JObject obj))
                {
                    structural.Add(itemPath, $"{itemPath} must be an object, got {Codec.Describe(array[i])}.", CodeTypeMismatch);
                    continue;
                }

                Codec.CheckKeys(obj, itemPath, prep.Problems, itemKeys, null);
                TItem item;
                try
                {
                    item = obj.ToObject<TItem>(serializer);
                }
                catch (Exception ex)
                {
                    structural.Add(itemPath, $"{itemPath} does not match the item shape: {ex.Message}", CodeTypeMismatch);
                    continue;
                }
                if (!prep.Problems.Any)
                    prepare(item, prep);
            }
            if (structural.Any)
                return ProblemsError(skill, structural);

            var firstByTarget = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var prep in prepared.Where(candidate => candidate.Ok && candidate.TargetKey != null))
            {
                if (firstByTarget.TryGetValue(prep.TargetKey, out var first))
                    prep.Problems.Add(Codec.Index("items", prep.Index), $"items[{prep.Index}] targets the same object as items[{first}] ({prep.Target}); merge them into one item, since their order would decide the result.");
                else
                    firstByTarget[prep.TargetKey] = prep.Index;
            }
            validateAll?.Invoke(preps);

            preps = prepared;
            return null;
        }

        /// <summary>Puts the item's target first in its result; <paramref name="success"/> overrides the result's own flag when given.</summary>
        private static Dictionary<string, object> Stamp(Dictionary<string, object> result, string target, bool? success)
        {
            var stamped = new Dictionary<string, object> { ["target"] = target };
            if (success.HasValue)
                stamped["success"] = success.Value;
            foreach (var pair in result)
            {
                if (pair.Key == "target" || (success.HasValue && pair.Key == "success"))
                    continue;
                stamped[pair.Key] = pair.Value;
            }
            return stamped;
        }

        private static void BindItem(Locator locator, BatchPrep prep, out ParticleBinding binding)
        {
            var error = ResolveSystem(locator, out binding);
            if (error != null)
            {
                prep.Target = locator.Describe();
                prep.Failure = error;
                binding = null;
                return;
            }
            prep.Target = GameObjectFinder.GetPath(binding.GameObject);
            prep.TargetKey = UnityObjectIdUtility.GetEntityId(binding.GameObject);
        }

        internal static object CreateBatchCore(string items) =>
            RunBatch<BatchParticleCreateItem>(items, "particle_create_batch", BatchItemKeys.Create, atomic: true,
                prepare: (item, prep) =>
                {
                    var prefix = Codec.Index("items", prep.Index);
                    prep.Target = item.name ?? prefix;
                    GameObject parent = null;
                    if (!item.ParentLocator.IsEmpty)
                    {
                        var (found, error) = GameObjectFinder.FindOrError(item.parentName, item.parentInstanceId, item.parentPath, entityId: item.parentEntityId);
                        if (error != null)
                        {
                            prep.Failure = error;
                            return;
                        }
                        parent = found;
                    }
                    var plan = PrepareCreate(item.name ?? DefaultSystemName,
                        new Vector3(item.x ?? 0f, item.y ?? 0f, item.z ?? 0f),
                        new Vector3(item.rotX ?? DefaultRotX, item.rotY ?? 0f, item.rotZ ?? 0f),
                        item.space, parent, item.configJson, prefix, prep.Problems, prep.Warnings);
                    prep.Run = () => CreateSystem(plan, prep.Warnings);
                },
                describe: item => item.name ?? DefaultSystemName);

        internal static object AddSystemBatchCore(string items) =>
            RunBatch<BatchParticleConfigItem>(items, "particle_add_system_batch", BatchItemKeys.TargetConfig, atomic: true,
                prepare: (item, prep) =>
                {
                    var (go, error) = FindTarget(item.Locator);
                    if (error != null)
                    {
                        prep.Target = item.Locator.Describe();
                        prep.Failure = error;
                        return;
                    }
                    prep.Target = GameObjectFinder.GetPath(go);
                    prep.TargetKey = UnityObjectIdUtility.GetEntityId(go);
                    var patch = PrepareAdd(go, item.configJson, Codec.Index("items", prep.Index), prep.Problems, prep.Warnings);
                    prep.Run = () => AddSystem(go, patch, prep.Warnings);
                },
                describe: item => item.Locator.Describe());

        internal static object ConfigureBatchCore(string items) =>
            RunBatch<BatchParticleConfigItem>(items, "particle_configure_batch", BatchItemKeys.TargetConfig, atomic: true,
                prepare: (item, prep) =>
                {
                    BindItem(item.Locator, prep, out var binding);
                    if (binding == null)
                        return;
                    var patch = PrepareConfigure(binding, item.configJson, Codec.Index("items", prep.Index), prep.Problems, prep.Warnings);
                    prep.Run = () => WritePatch(binding, patch, $"Particle: configure {binding.GameObject.name}", prep.Warnings);
                },
                describe: item => item.Locator.Describe());

        internal static object SetRendererBatchCore(string items) =>
            RunBatch<BatchParticleConfigItem>(items, "particle_set_renderer_batch", BatchItemKeys.TargetConfig, atomic: true,
                prepare: (item, prep) =>
                {
                    BindItem(item.Locator, prep, out var binding);
                    if (binding == null)
                        return;
                    var patch = PrepareRenderer(binding, item.configJson, Codec.Index("items", prep.Index), prep.Problems, prep.Warnings);
                    prep.Run = () => WritePatch(binding, patch, $"Particle: set renderer of {binding.GameObject.name}", prep.Warnings);
                },
                describe: item => item.Locator.Describe());

        internal static object SetBurstsBatchCore(string items) =>
            RunBatch<BatchParticleBurstsItem>(items, "particle_set_bursts_batch", BatchItemKeys.TargetBursts, atomic: true,
                prepare: (item, prep) =>
                {
                    BindItem(item.Locator, prep, out var binding);
                    if (binding == null)
                        return;
                    var bursts = PrepareBurstList(binding, item.burstsJson, Codec.Index("items", prep.Index), prep.Problems, prep.Warnings);
                    prep.Run = () => WriteBursts(binding, bursts, $"Particle: set bursts of {binding.GameObject.name}", prep.Warnings);
                },
                describe: item => item.Locator.Describe());
    }
}
#endif

// Producer:Betsy
