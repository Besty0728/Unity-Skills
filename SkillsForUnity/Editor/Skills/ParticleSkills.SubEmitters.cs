#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    public static partial class ParticleSkills
    {
        /// <summary>A product limit (Unity has none): keeps one list, its validation and its readback small.</summary>
        internal const int MaxSubEmitters = 16;

        /// <summary>The keys of a sub-emitter entry, of its system locator and of the batch item (a nested class: static initializers stay out of the partial class).</summary>
        internal static class SubEmitterKeys
        {
            public static readonly string[] Entry = { "system", "type", "properties", "emitProbability", "missing" };
            public static readonly string[] EntryRequired = { "system", "type" };
            public static readonly string[] Locator = { "name", "entityId", "instanceId", "path" };
            public static readonly string[] BatchItem = { "name", "entityId", "instanceId", "path", "subEmittersJson" };

            /// <summary>The five inherit flags in Unity's declaration order; readback lists the set ones.</summary>
            public static readonly ParticleSystemSubEmitterProperties[] Flags =
            {
                ParticleSystemSubEmitterProperties.InheritColor,
                ParticleSystemSubEmitterProperties.InheritSize,
                ParticleSystemSubEmitterProperties.InheritRotation,
                ParticleSystemSubEmitterProperties.InheritLifetime,
                ParticleSystemSubEmitterProperties.InheritDuration
            };
        }

        /// <summary>How a locator nested in a payload resolves: live, still to be created by an earlier dry-run step, or an error.</summary>
        internal delegate PlanTarget ReferenceResolver(JObject locator);

        /// <summary>One validated entry; <see cref="System"/> is null while it names an object an earlier dry-run step creates.</summary>
        internal sealed class SubEmitterEntry
        {
            public ParticleSystem System;
            public string PendingPath;
            public string Path;
            public ParticleSystemSubEmitterType Type;
            public ParticleSystemSubEmitterProperties Properties;
            public float EmitProbability = 1f;
        }

        /// <summary>The validated new list of one target. <see cref="Binding"/> is null for a target an earlier dry-run step creates.</summary>
        internal sealed class SubEmitterPlan
        {
            public ParticleBinding Binding;
            public string BasePath;
            public readonly List<SubEmitterEntry> Entries = new List<SubEmitterEntry>();
        }

        /// <summary>The executing skills resolve nested locators against the live scene only.</summary>
        internal static PlanTarget ResolveLiveReference(JObject locator)
        {
            var name = locator.Value<string>("name");
            var path = locator.Value<string>("path");
            var entityId = locator.Value<string>("entityId");
            var instanceId = locator["instanceId"] != null && locator["instanceId"].Type == JTokenType.Integer ? locator.Value<int>("instanceId") : 0;
            var given = new Locator(name, instanceId, path, entityId);
            if (given.IsEmpty)
                return PlanTarget.None;
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path, entityId: entityId);
            return error != null
                ? new PlanTarget { Kind = PlanTargetKind.Failed, Error = error }
                : new PlanTarget { Kind = PlanTargetKind.Live, Live = go };
        }

        // ===================== Prepare (shared by execution and the dryRun planner) =====================

        /// <summary>
        /// particle_set_sub_emitters' subEmittersJson: the complete new list ([] clears it). Validates every entry and
        /// the list as a whole (self references, other scenes, duplicates); cycles need every list of the call and are
        /// checked by <see cref="CheckSubEmitterCycles"/>. Touches no Unity state.
        /// </summary>
        internal static SubEmitterPlan PrepareSubEmitterList(ParticleBinding binding, JToken payload, string prefix,
            ReferenceResolver resolve, Problems problems, List<string> warnings)
        {
            var basePath = Codec.Join(prefix, "subEmittersJson");
            var plan = new SubEmitterPlan { Binding = binding, BasePath = basePath };
            CheckWritable(binding?.GameObject, binding?.System, Codec.Join(prefix, "target"), problems);

            if (payload == null || payload.Type == JTokenType.Null)
            {
                problems.Add(basePath, $"{basePath} is required: the complete sub-emitter list (an empty array [] clears it).", CodeMissingParam);
                return plan;
            }
            var token = Codec.Normalize(payload, basePath, problems);
            if (token == null)
                return plan;
            if (!(token is JArray array))
            {
                problems.Add(basePath, $"{basePath} must be an array of sub-emitter entries {{system, type, properties?, emitProbability?}}, got {Codec.Describe(token)}.", CodeTypeMismatch);
                return plan;
            }
            if (array.Count > MaxSubEmitters)
            {
                problems.Add(basePath, $"{basePath} holds {array.Count} entries; one list takes at most {MaxSubEmitters}.");
                return plan;
            }

            bool ok = true;
            for (int i = 0; i < array.Count; i++)
            {
                if (ReadSubEmitterEntry(array[i], Codec.Index(basePath, i), resolve, problems, out var entry))
                    plan.Entries.Add(entry);
                else
                    ok = false;
            }
            if (!ok)
                return plan;

            var seen = new Dictionary<(ParticleSystem, ParticleSystemSubEmitterType), int>();
            for (int i = 0; i < plan.Entries.Count; i++)
            {
                var entry = plan.Entries[i];
                if (entry.System == null)
                {
                    warnings.Add($"{entry.Path}.system '{entry.PendingPath}' will be created by an earlier step; the checks that need the live system were skipped.");
                    continue;
                }

                var childGo = entry.System.gameObject;
                var childPath = GameObjectFinder.GetPath(childGo);
                if (!IsInOpenScene(childGo))
                {
                    problems.Add(Codec.Join(entry.Path, "system"), $"'{childGo.name}' is not in an open scene (a prefab asset or a Prefab Mode object); a sub emitter must be a system of the parent's scene.");
                    continue;
                }
                if (seen.TryGetValue((entry.System, entry.Type), out var first))
                {
                    problems.Add(entry.Path, $"{entry.Path} repeats {Codec.Index(basePath, first)} ('{childPath}' as {entry.Type}); each system takes one entry per type.");
                    continue;
                }
                seen[(entry.System, entry.Type)] = i;

                if (binding == null)
                    continue;
                var parentGo = binding.GameObject;
                if (entry.System == binding.System)
                {
                    problems.Add(Codec.Join(entry.Path, "system"), $"'{childPath}' is the system itself; a ParticleSystem cannot be its own sub emitter.");
                    continue;
                }
                if (childGo.scene != parentGo.scene)
                {
                    problems.Add(Codec.Join(entry.Path, "system"), $"'{childPath}' is in scene '{childGo.scene.name}', the parent in '{parentGo.scene.name}': Unity cannot save a reference across scenes.");
                    continue;
                }
                if (!childGo.transform.IsChildOf(parentGo.transform))
                {
                    warnings.Add($"{Codec.Join(entry.Path, "system")}: '{childPath}' is not a child of '{GameObjectFinder.GetPath(parentGo)}'. Unity's Inspector expects sub emitters below the system that triggers them; " +
                        "this skill never reparents (use gameobject_set_parent if that was the intent).");
                }
            }

            if (binding != null && !problems.Any)
                WarnSubEmitterUse(binding, plan, warnings);
            return plan;
        }

        /// <summary>The "stored but has no effect" situations of a valid list, as warnings.</summary>
        private static void WarnSubEmitterUse(ParticleBinding binding, SubEmitterPlan plan, List<string> warnings)
        {
            var system = binding.System;
            if (plan.Entries.Count > 0 && !system.subEmitters.enabled)
                warnings.Add("modules.subEmitters is disabled: the list is stored but spawns nothing until \"enabled\": true (particle_configure {\"modules\": {\"subEmitters\": {\"enabled\": true}}}).");
            if (plan.Entries.Any(entry => entry.Type == ParticleSystemSubEmitterType.Collision) && !system.collision.enabled)
                warnings.Add("Collision sub emitters fire only when particles collide, and modules.collision is disabled.");
            if (plan.Entries.Any(entry => entry.Type == ParticleSystemSubEmitterType.Trigger) && !system.trigger.enabled)
                warnings.Add("Trigger sub emitters fire only from the Triggers module, and modules.trigger is disabled.");
            if (plan.Entries.Any(entry => entry.Type == ParticleSystemSubEmitterType.Manual))
                warnings.Add("Manual sub emitters fire only when a script calls ParticleSystem.TriggerSubEmitter.");
        }

        private static bool ReadSubEmitterEntry(JToken token, string path, ReferenceResolver resolve, Problems problems, out SubEmitterEntry entry)
        {
            entry = new SubEmitterEntry { Path = path };
            var obj = Codec.ExpectObject(token, path, problems, "a sub-emitter entry {system, type, properties?, emitProbability?}");
            if (obj == null || !Codec.CheckKeys(obj, path, problems, SubEmitterKeys.Entry, SubEmitterKeys.EntryRequired))
                return false;

            bool ok = true;
            if (Codec.ReadEnum(obj["type"], Codec.Join(path, "type"), problems, typeof(ParticleSystemSubEmitterType), null, out var type))
                entry.Type = (ParticleSystemSubEmitterType)type;
            else
                ok = false;
            if (obj.Property("properties") != null)
            {
                ok &= ReadSubEmitterProperties(obj["properties"], Codec.Join(path, "properties"), problems, out var properties);
                entry.Properties = properties;
            }
            if (obj.Property("emitProbability") != null)
            {
                ok &= Codec.ReadFloat(obj["emitProbability"], Codec.Join(path, "emitProbability"), problems, out var probability, 0, 1);
                entry.EmitProbability = probability;
            }
            if (obj.Property("missing") != null)
                ok &= Codec.ReadBool(obj["missing"], Codec.Join(path, "missing"), problems, out _);
            ok &= ReadSubEmitterSystem(obj["system"], Codec.Join(path, "system"), resolve, problems, entry);
            return ok;
        }

        /// <summary>The referenced system: an object of locator keys (readback's identity object is one) or a path/name string.</summary>
        private static bool ReadSubEmitterSystem(JToken token, string path, ReferenceResolver resolve, Problems problems, SubEmitterEntry entry)
        {
            JObject locator;
            if (token == null || token.Type == JTokenType.Null)
            {
                problems.Add(path, $"{path} is null (a missing reference): name an existing ParticleSystem, or remove the entry.");
                return false;
            }
            if (token.Type == JTokenType.String)
            {
                var text = token.Value<string>().Trim();
                if (text.Length == 0)
                {
                    problems.Add(path, $"{path} is empty: name an existing ParticleSystem by entityId, path or name.");
                    return false;
                }
                locator = text.Contains("/") ? new JObject { ["path"] = text } : new JObject { ["name"] = text };
            }
            else if (token is JObject obj)
            {
                if (!Codec.CheckKeys(obj, path, problems, SubEmitterKeys.Locator, null))
                    return false;
                bool typed = true;
                foreach (var key in new[] { "name", "entityId", "path" })
                {
                    var value = obj[key];
                    if (value != null && value.Type != JTokenType.String && value.Type != JTokenType.Null)
                    {
                        problems.Add(Codec.Join(path, key), $"{Codec.Join(path, key)} must be a string, got {Codec.Describe(value)}.", CodeTypeMismatch);
                        typed = false;
                    }
                }
                if (obj["instanceId"] != null && obj["instanceId"].Type != JTokenType.Integer && obj["instanceId"].Type != JTokenType.Null)
                {
                    problems.Add(Codec.Join(path, "instanceId"), $"{Codec.Join(path, "instanceId")} must be an integer, got {Codec.Describe(obj["instanceId"])}.", CodeTypeMismatch);
                    typed = false;
                }
                if (!typed)
                    return false;
                locator = obj;
            }
            else
            {
                problems.Add(path, $"{path} must be a locator object {{entityId|name|path|instanceId}} or a path/name string, got {Codec.Describe(token)}.", CodeTypeMismatch);
                return false;
            }

            var target = resolve(locator);
            switch (target.Kind)
            {
                case PlanTargetKind.None:
                    problems.Add(path, $"{path} names no object: give one of entityId, name, path or instanceId.", CodeMissingParam);
                    return false;
                case PlanTargetKind.Failed:
                    problems.Add(path, $"{path}: {ErrorText(target.Error)}", CodeTargetNotFound);
                    return false;
                case PlanTargetKind.Pending:
                    entry.PendingPath = target.PendingPath;
                    return true;
            }

            var system = target.Live.GetComponent<ParticleSystem>();
            if (system == null)
            {
                problems.Add(path, $"{path}: '{GameObjectFinder.GetPath(target.Live)}' has no ParticleSystem; a sub emitter must be a particle system (particle_add_system adds one).", CodeTargetNotFound);
                return false;
            }
            entry.System = system;
            return true;
        }

        /// <summary>
        /// The inherit flags as a list of Unity's names: InheritColor, InheritSize, InheritRotation, InheritLifetime,
        /// InheritDuration; [] (or ["InheritNothing"]) inherits nothing, ["InheritEverything"] all five.
        /// </summary>
        internal static bool ReadSubEmitterProperties(JToken token, string path, Problems problems, out ParticleSystemSubEmitterProperties properties)
        {
            properties = ParticleSystemSubEmitterProperties.InheritNothing;
            var names = SubEmitterKeys.Flags.Select(flag => flag.ToString()).ToArray();
            if (!(token is JArray array))
            {
                problems.Add(path, $"{path} must be an array of flag names ({string.Join(", ", names)}; [] inherits nothing), got {Codec.Describe(token)}.", CodeTypeMismatch);
                return false;
            }

            bool ok = true;
            var seen = new HashSet<ParticleSystemSubEmitterProperties>();
            for (int i = 0; i < array.Count; i++)
            {
                var itemPath = Codec.Index(path, i);
                var item = array[i];
                var text = item != null && item.Type == JTokenType.String ? item.Value<string>().Trim() : null;
                if (text != null && array.Count == 1 && string.Equals(text, nameof(ParticleSystemSubEmitterProperties.InheritEverything), StringComparison.OrdinalIgnoreCase))
                {
                    properties = ParticleSystemSubEmitterProperties.InheritEverything;
                    return true;
                }
                if (text != null && array.Count == 1 && string.Equals(text, nameof(ParticleSystemSubEmitterProperties.InheritNothing), StringComparison.OrdinalIgnoreCase))
                    return true;

                var match = text == null ? null : names.FirstOrDefault(name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    var why = text != null && (text.Equals("InheritEverything", StringComparison.OrdinalIgnoreCase) || text.Equals("InheritNothing", StringComparison.OrdinalIgnoreCase))
                        ? $" {text} must be the only entry."
                        : "";
                    problems.Add(itemPath, $"Invalid value {Codec.Describe(item)} for {itemPath}.{why} Valid values: {string.Join(", ", names)} (or the single entry InheritEverything / InheritNothing).",
                        text == null ? CodeTypeMismatch : CodeSemantic);
                    ok = false;
                    continue;
                }
                var flag = (ParticleSystemSubEmitterProperties)Enum.Parse(typeof(ParticleSystemSubEmitterProperties), match);
                if (!seen.Add(flag))
                {
                    problems.Add(itemPath, $"{itemPath} repeats {match}.");
                    ok = false;
                    continue;
                }
                properties |= flag;
            }
            return ok;
        }

        // ===================== Cycles =====================

        /// <summary>
        /// Refuses lists that would make a system its own (indirect) sub emitter. The graph is the one this call leaves
        /// behind: the new lists of every target in <paramref name="plans"/>, the live lists of every other system, so a
        /// batch that builds a chain in one call is judged as a whole. Each cycle is reported once, on the list that closes it.
        /// </summary>
        internal static void CheckSubEmitterCycles(IReadOnlyList<(SubEmitterPlan plan, Problems problems)> plans)
        {
            var overrides = new Dictionary<ParticleSystem, List<ParticleSystem>>();
            foreach (var (plan, _) in plans)
            {
                if (plan.Binding != null)
                    overrides[plan.Binding.System] = plan.Entries.Where(entry => entry.System != null).Select(entry => entry.System).ToList();
            }

            IEnumerable<ParticleSystem> Children(ParticleSystem system) =>
                overrides.TryGetValue(system, out var list) ? list : LiveSubEmitterSystems(system);

            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (plan, problems) in plans)
            {
                if (plan.Binding == null)
                    continue;
                var root = plan.Binding.System;
                for (int i = 0; i < plan.Entries.Count; i++)
                {
                    var start = plan.Entries[i].System;
                    if (start == null || start == root)
                        continue;
                    var cycle = FindPathBack(start, root, Children);
                    if (cycle == null)
                        continue;
                    var names = new[] { root }.Concat(cycle).Select(system => GameObjectFinder.GetPath(system.gameObject)).ToList();
                    var key = string.Join(">", names.Skip(1).OrderBy(name => name, StringComparer.Ordinal));
                    if (!reported.Add(key))
                        continue;
                    problems.Add(Codec.Join(plan.Entries[i].Path, "system"),
                        $"This entry closes a sub-emitter cycle: {string.Join(" -> ", names)}. A system cannot (indirectly) spawn itself; remove one link.");
                }
            }
        }

        /// <summary>The path from <paramref name="start"/> back to <paramref name="target"/> through sub-emitter links, or null.</summary>
        private static List<ParticleSystem> FindPathBack(ParticleSystem start, ParticleSystem target, Func<ParticleSystem, IEnumerable<ParticleSystem>> children)
        {
            var visited = new HashSet<ParticleSystem>();
            var stack = new Stack<(ParticleSystem system, List<ParticleSystem> path)>();
            stack.Push((start, new List<ParticleSystem> { start }));
            while (stack.Count > 0)
            {
                var (system, path) = stack.Pop();
                if (system == target)
                    return path;
                if (!visited.Add(system))
                    continue;
                foreach (var child in children(system))
                {
                    if (child != null && (child == target || !visited.Contains(child)))
                        stack.Push((child, new List<ParticleSystem>(path) { child }));
                }
            }
            return null;
        }

        // ===================== The serialized list =====================

        /// <summary>The serialized list behind SubEmittersModule (the Inspector draws this one).</summary>
        internal const string SubEmitterArrayPath = "SubModule.subEmitters";

        /// <summary>One entry as it is stored; <see cref="System"/> is null for an empty or broken reference.</summary>
        internal struct StoredSubEmitter
        {
            public ParticleSystem System;
            public ParticleSystemSubEmitterType Type;
            public ParticleSystemSubEmitterProperties Properties;
            public float EmitProbability;
        }

        /// <summary>
        /// The stored list, broken entries included. Unity's API skips an entry whose system was destroyed
        /// (subEmittersCount drops it) while the serialized list keeps it and the Inspector shows it as None, so the
        /// readback, the replace and the analysis read the serialized list. Falls back to the API if the layout changes.
        /// </summary>
        internal static List<StoredSubEmitter> ReadStoredSubEmitters(ParticleSystem system)
        {
            var list = new List<StoredSubEmitter>();
            using (var serialized = new SerializedObject(system))
            {
                var array = serialized.FindProperty(SubEmitterArrayPath);
                if (array != null && array.isArray)
                {
                    for (int i = 0; i < array.arraySize; i++)
                    {
                        var element = array.GetArrayElementAtIndex(i);
                        var emitter = element.FindPropertyRelative("emitter");
                        var type = element.FindPropertyRelative("type");
                        var properties = element.FindPropertyRelative("properties");
                        var probability = element.FindPropertyRelative("emitProbability");
                        if (emitter == null || type == null || properties == null || probability == null)
                            return ReadApiSubEmitters(system);
                        list.Add(new StoredSubEmitter
                        {
                            System = emitter.objectReferenceValue as ParticleSystem,
                            Type = (ParticleSystemSubEmitterType)type.intValue,
                            Properties = (ParticleSystemSubEmitterProperties)properties.intValue,
                            EmitProbability = probability.floatValue
                        });
                    }
                    return list;
                }
            }
            return ReadApiSubEmitters(system);
        }

        private static List<StoredSubEmitter> ReadApiSubEmitters(ParticleSystem system)
        {
            var module = system.subEmitters;
            var list = new List<StoredSubEmitter>();
            for (int i = 0; i < module.subEmittersCount; i++)
            {
                list.Add(new StoredSubEmitter
                {
                    System = module.GetSubEmitterSystem(i),
                    Type = module.GetSubEmitterType(i),
                    Properties = module.GetSubEmitterProperties(i),
                    EmitProbability = module.GetSubEmitterEmitProbability(i)
                });
            }
            return list;
        }

        /// <summary>The stored entry count, broken entries included (what the Inspector lists).</summary>
        internal static int StoredSubEmitterCount(ParticleSystem system) => ReadStoredSubEmitters(system).Count;

        /// <summary>Empties the stored list, broken entries included (RemoveSubEmitter cannot reach those). The caller has recorded Undo.</summary>
        private static void ClearStoredSubEmitters(ParticleSystem system)
        {
            using (var serialized = new SerializedObject(system))
            {
                var array = serialized.FindProperty(SubEmitterArrayPath);
                if (array != null && array.isArray && array.arraySize > 0)
                {
                    array.arraySize = 0;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            var module = system.subEmitters;
            for (int i = module.subEmittersCount - 1; i >= 0; i--)
                module.RemoveSubEmitter(i);
        }

        internal static IEnumerable<ParticleSystem> LiveSubEmitterSystems(ParticleSystem system)
        {
            var module = system.subEmitters;
            for (int i = 0; i < module.subEmittersCount; i++)
            {
                var child = module.GetSubEmitterSystem(i);
                if (child != null)
                    yield return child;
            }
        }

        // ===================== Readback and apply =====================

        internal static JToken SubEmitterPropertiesToJson(ParticleSystemSubEmitterProperties properties) =>
            new JArray(SubEmitterKeys.Flags.Where(flag => (properties & flag) != 0).Select(flag => flag.ToString()));

        /// <summary>The identity of a referenced object, in the locator shape that is accepted back as input.</summary>
        internal static JObject ReferenceToJson(GameObject go) => new JObject
        {
            ["name"] = go.name,
            ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
            ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
            ["path"] = GameObjectFinder.GetPath(go)
        };

        internal static JArray ReadSubEmitters(ParticleSystem system)
        {
            var list = new JArray();
            foreach (var stored in ReadStoredSubEmitters(system))
            {
                var entry = new JObject
                {
                    ["system"] = stored.System != null ? (JToken)ReferenceToJson(stored.System.gameObject) : JValue.CreateNull(),
                    ["type"] = stored.Type.ToString(),
                    ["properties"] = SubEmitterPropertiesToJson(stored.Properties),
                    ["emitProbability"] = stored.EmitProbability
                };
                if (stored.System == null)
                    entry["missing"] = true;
                list.Add(entry);
            }
            return list;
        }

        /// <summary>What a list will read back as: dryRun's "after" and the requested side of the adjustment check.</summary>
        internal static JArray PlannedSubEmittersToJson(SubEmitterPlan plan) => new JArray(plan.Entries.Select(entry => new JObject
        {
            ["system"] = entry.System != null ? (JToken)ReferenceToJson(entry.System.gameObject) : new JObject { ["pendingPath"] = entry.PendingPath },
            ["type"] = entry.Type.ToString(),
            ["properties"] = SubEmitterPropertiesToJson(entry.Properties),
            ["emitProbability"] = entry.EmitProbability
        }));

        /// <summary>Notes on a list as it is: missing references and systems outside the parent's hierarchy.</summary>
        private static List<string> SubEmitterNotes(ParticleBinding binding, JArray list)
        {
            var notes = new List<string>();
            var stored = ReadStoredSubEmitters(binding.System);
            for (int i = 0; i < stored.Count; i++)
            {
                var child = stored[i].System;
                if (child == null)
                    notes.Add($"subEmitters[{i}] is a missing reference: it spawns nothing; replace the list with particle_set_sub_emitters.");
                else if (!child.transform.IsChildOf(binding.GameObject.transform))
                    notes.Add($"subEmitters[{i}] '{GameObjectFinder.GetPath(child.gameObject)}' is not a child of this system.");
            }
            if (list.Count > 0 && !binding.System.subEmitters.enabled)
                notes.Add("modules.subEmitters is disabled: the list spawns nothing until it is enabled.");
            return notes;
        }

        internal static object GetSubEmittersCore(Locator locator)
        {
            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;

            var go = binding.GameObject;
            var list = ReadSubEmitters(binding.System);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = go.name,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                ["path"] = GameObjectFinder.GetPath(go),
                ["enabled"] = binding.System.subEmitters.enabled,
                ["count"] = list.Count,
                ["subEmitters"] = list,
                ["warnings"] = SubEmitterNotes(binding, list).ToArray()
            };
        }

        /// <summary>Replaces the whole sub-emitter list of a live system and reads it back.</summary>
        internal static Dictionary<string, object> WriteSubEmitters(SubEmitterPlan plan, string label, List<string> warnings)
        {
            var binding = plan.Binding;
            return Transaction(label, () =>
            {
                var requested = PlannedSubEmittersToJson(plan);
                WorkflowManager.SnapshotObject(binding.System);
                Undo.RecordObject(binding.System, label);
                ClearStoredSubEmitters(binding.System);
                var module = binding.System.subEmitters;
                foreach (var entry in plan.Entries)
                    module.AddSubEmitter(entry.System, entry.Type, entry.Properties, entry.EmitProbability);
                AfterWriteHookForTests?.Invoke(null);
                Undo.FlushUndoRecordObjects();
                RecordPrefabOverrides(binding.System);

                var actual = ReadSubEmitters(binding.System);
                var go = binding.GameObject;
                var result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = go.name,
                    ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                    ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                    ["path"] = GameObjectFinder.GetPath(go),
                    ["enabled"] = binding.System.subEmitters.enabled,
                    ["count"] = actual.Count,
                    ["subEmitters"] = actual,
                    ["warnings"] = warnings
                };
                if (!Codec.Equivalent(requested, actual))
                {
                    result["adjustments"] = new object[] { new Dictionary<string, object> { ["path"] = SubEmittersPath, ["valueRequested"] = requested, ["valueApplied"] = actual } };
                    warnings.Add(SubEmittersPath + ": Unity stored a different list than requested; see adjustments.");
                }
                return result;
            });
        }

        internal const string SubEmittersPath = "modules.subEmitters.subEmitters";

        // ===================== Skills =====================

        internal static object SetSubEmittersCore(Locator locator, string subEmittersJson)
        {
            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;

            var problems = new Problems();
            var warnings = new List<string>();
            var plan = PrepareSubEmitterList(binding, PayloadToken(subEmittersJson), "", ResolveLiveReference, problems, warnings);
            if (!problems.Any)
                CheckSubEmitterCycles(new[] { (plan, problems) });
            if (problems.Any)
                return ProblemsError("particle_set_sub_emitters", problems);
            return ParticleSkills.WriteSubEmitters(plan, $"Particle: set sub emitters of {binding.GameObject.name}", warnings);
        }

        internal static object SetSubEmittersBatchCore(string items)
        {
            var plans = new Dictionary<int, SubEmitterPlan>();
            return ParticleSkills.RunBatch<BatchParticleSubEmittersItem>(items, "particle_set_sub_emitters_batch", SubEmitterKeys.BatchItem, atomic: true,
                prepare: (item, prep) =>
                {
                    BindItem(item.Locator, prep, out var binding);
                    if (binding == null)
                        return;
                    var plan = PrepareSubEmitterList(binding, item.subEmittersJson, Codec.Index("items", prep.Index), ResolveLiveReference, prep.Problems, prep.Warnings);
                    plans[prep.Index] = plan;
                    prep.Run = () => WriteSubEmitters(plan, $"Particle: set sub emitters of {binding.GameObject.name}", prep.Warnings);
                },
                describe: item => item.Locator.Describe(),
                validateAll: preps => CheckSubEmitterCycles(preps
                    .Where(prep => prep.Ok && plans.ContainsKey(prep.Index))
                    .Select(prep => (plans[prep.Index], prep.Problems))
                    .ToList()));
        }
    }
}
#endif

// Producer:Betsy
