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
        /// <summary>
        /// Test seam: called after every applied field write (and after a burst list write, with null), so a test can
        /// throw mid-apply and prove the transaction leaves nothing behind. Never set outside tests.
        /// </summary>
        internal static Action<FieldWrite> AfterWriteHookForTests;

        /// <summary>
        /// The transaction every particle write runs in: its own Undo group (nested inside the router's or a batch's),
        /// and on any exception a revert of that group plus the workflow snapshots it added, so a failed write never
        /// leaves a partial change. <paramref name="cleanup"/> runs first on failure, for objects not yet on the Undo stack.
        /// </summary>
        internal static Dictionary<string, object> Transaction(string label, Func<Dictionary<string, object>> body, Action cleanup = null)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(label);
            int group = Undo.GetCurrentGroup();
            int snapshotsBefore = WorkflowManager.CurrentTask?.snapshots?.Count ?? 0;
            try
            {
                var result = body();
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(group);
                return result;
            }
            catch (Exception ex)
            {
                try { cleanup?.Invoke(); }
                catch (Exception cleanupError) { SkillsLogger.LogVerbose($"{label}: cleanup after a failed apply threw: {cleanupError.Message}"); }
                // Pending RecordObject state must reach the Undo stack before it can be reverted.
                Undo.FlushUndoRecordObjects();
                Undo.RevertAllDownToGroup(group);
                if (WorkflowManager.IsRecording)
                    WorkflowManager.TruncateCurrentTask(snapshotsBefore);
                GameObjectFinder.InvalidateCache();
                return ApplyFailed(label, ex);
            }
        }

        private static void ApplyWrites(ParticleBinding binding, Patch patch)
        {
            foreach (var write in patch.Writes)
            {
                write.Field.Write(binding, write.Value);
                AfterWriteHookForTests?.Invoke(write);
            }
        }

        /// <summary>A write to a prefab instance must become an instance override, never a change to the prefab asset.</summary>
        private static void RecordPrefabOverrides(Component component)
        {
            if (component != null && PrefabUtility.IsPartOfPrefabInstance(component))
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }

        /// <summary>
        /// Readback of every written field against what was requested. A difference means Unity adjusted the value
        /// (it clamps several fields silently); it is reported, never hidden behind the requested value.
        /// </summary>
        private static List<object> FindAdjustments(ParticleBinding binding, Patch patch, List<JToken> requested, List<string> warnings)
        {
            var adjustments = new List<object>();
            for (int i = 0; i < patch.Writes.Count; i++)
            {
                var write = patch.Writes[i];
                var actual = Codec.WriteField(write.Field, write.Field.Read(binding));
                if (Codec.Equivalent(requested[i], actual))
                    continue;
                adjustments.Add(new Dictionary<string, object>
                {
                    ["path"] = write.Field.Path,
                    ["valueRequested"] = requested[i],
                    ["valueApplied"] = actual
                });
                warnings.Add($"{write.Field.Path}: Unity stored a different value than requested; see adjustments.");
            }
            return adjustments;
        }

        /// <summary>Applies a validated system/modules or renderer patch to a live system and reads the result back from Unity.</summary>
        internal static Dictionary<string, object> WritePatch(ParticleBinding binding, Patch patch, string label, List<string> warnings)
        {
            return Transaction(label, () =>
            {
                var requested = patch.Writes.Select(write => Codec.WriteField(write.Field, write.Value)).ToList();
                if (patch.TouchesSystem)
                {
                    WorkflowManager.SnapshotObject(binding.System);
                    Undo.RecordObject(binding.System, label);
                }
                if (patch.TouchesRenderer)
                {
                    WorkflowManager.SnapshotObject(binding.Renderer);
                    Undo.RecordObject(binding.Renderer, label);
                }

                ApplyWrites(binding, patch);
                Undo.FlushUndoRecordObjects();
                if (patch.TouchesSystem)
                    RecordPrefabOverrides(binding.System);
                if (patch.TouchesRenderer)
                    RecordPrefabOverrides(binding.Renderer);

                var adjustments = FindAdjustments(binding, patch, requested, warnings);
                var go = binding.GameObject;
                var result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = go.name,
                    ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                    ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                    ["path"] = GameObjectFinder.GetPath(go),
                    ["applied"] = patch.Writes.Select(write => write.Field.Path).ToArray(),
                    ["state"] = ReadState(patch, binding),
                    ["warnings"] = warnings.ToArray()
                };
                if (adjustments.Count > 0)
                    result["adjustments"] = adjustments;
                return result;
            });
        }

        /// <summary>Replaces the whole burst list of a live system and reads it back.</summary>
        internal static Dictionary<string, object> WriteBursts(ParticleBinding binding, ParticleSystem.Burst[] bursts, string label, List<string> warnings)
        {
            return Transaction(label, () =>
            {
                var requested = new JArray(bursts.Select(Codec.BurstToJson));
                WorkflowManager.SnapshotObject(binding.System);
                Undo.RecordObject(binding.System, label);
                var emission = binding.System.emission;
                emission.SetBursts(bursts);
                AfterWriteHookForTests?.Invoke(null);
                Undo.FlushUndoRecordObjects();
                RecordPrefabOverrides(binding.System);

                var actual = ReadBursts(binding.System);
                var go = binding.GameObject;
                var result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = go.name,
                    ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                    ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                    ["path"] = GameObjectFinder.GetPath(go),
                    ["count"] = actual.Count,
                    ["bursts"] = actual,
                    ["duration"] = binding.System.main.duration,
                    ["warnings"] = warnings
                };
                if (!Codec.Equivalent(requested, actual))
                {
                    result["adjustments"] = new object[] { new Dictionary<string, object> { ["path"] = "modules.emission.bursts", ["valueRequested"] = requested, ["valueApplied"] = actual } };
                    warnings.Add("modules.emission.bursts: Unity stored a different burst list than requested; see adjustments.");
                }
                return result;
            });
        }

        /// <summary>
        /// What particle_create and particle_add_system set on a new system besides its configJson: playOnAwake false
        /// (nothing starts by itself) unless the patch sets it, and the pipeline's default particle material, since
        /// AddComponent leaves the renderer without one. stopAction is already None on a new system.
        /// </summary>
        private static List<string> ApplyCreationDefaults(ParticleBinding binding, Patch patch)
        {
            var applied = new List<string>();
            var main = Registry.Module("main");
            if (patch.Find(main.Field("playOnAwake")) == null)
            {
                var module = binding.System.main;
                module.playOnAwake = false;
                applied.Add("modules.main.playOnAwake");
            }
            if (binding.Renderer != null && binding.Renderer.sharedMaterial == null)
            {
                var material = DefaultParticleMaterial();
                if (material != null)
                {
                    binding.Renderer.sharedMaterial = material;
                    applied.Add("renderer.material");
                }
            }
            return applied;
        }

        /// <summary>Validated input of one particle_create: where the object goes and what it is configured with.</summary>
        internal sealed class CreatePlan
        {
            public string Name;
            public GameObject Parent;
            public bool WorldSpace;
            public Vector3 Position;
            public Vector3 EulerAngles;
            public Patch Patch;
        }

        /// <summary>Creates the GameObject with its ParticleSystem and renderer, configures it, and registers both with Undo and the workflow as one creation.</summary>
        internal static Dictionary<string, object> CreateSystem(CreatePlan plan, List<string> warnings)
        {
            GameObject created = null;
            bool onUndoStack = false;
            var label = $"Particle: create {plan.Name}";
            return Transaction(label, () =>
            {
                created = new GameObject(plan.Name);
                var transform = created.transform;
                if (plan.Parent != null)
                    transform.SetParent(plan.Parent.transform, false);
                if (plan.WorldSpace)
                {
                    transform.position = plan.Position;
                    transform.eulerAngles = plan.EulerAngles;
                }
                else
                {
                    transform.localPosition = plan.Position;
                    transform.localEulerAngles = plan.EulerAngles;
                }

                var system = created.AddComponent<ParticleSystem>();
                var binding = new ParticleBinding { GameObject = created, System = system, Renderer = created.GetComponent<ParticleSystemRenderer>() };
                var requested = plan.Patch.Writes.Select(write => Codec.WriteField(write.Field, write.Value)).ToList();
                var defaults = ApplyCreationDefaults(binding, plan.Patch);
                ApplyWrites(binding, plan.Patch);

                // Registered after configuration, as light_create does: undo removes the configured object, redo and the
                // workflow snapshot (which captures every component's JSON) bring back exactly this state.
                Undo.RegisterCreatedObjectUndo(created, label);
                onUndoStack = true;
                WorkflowManager.SnapshotObject(created, SnapshotType.Created);
                GameObjectFinder.RegisterCreated(created);

                var adjustments = FindAdjustments(binding, plan.Patch, requested, warnings);
                var parent = transform.parent;
                var result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = created.name,
                    ["entityId"] = UnityObjectIdUtility.GetEntityId(created),
                    ["instanceId"] = UnityObjectIdUtility.GetObjectId(created),
                    ["path"] = GameObjectFinder.GetPath(created),
                    ["parentPath"] = parent != null ? GameObjectFinder.GetPath(parent.gameObject) : null,
                    ["position"] = Codec.Vector3ToJson(transform.position),
                    ["localPosition"] = Codec.Vector3ToJson(transform.localPosition),
                    ["rotation"] = Codec.Vector3ToJson(transform.eulerAngles),
                    ["particleSystem"] = ComponentIdentity(system),
                    ["renderer"] = ComponentIdentity(binding.Renderer),
                    ["material"] = Codec.AssetToJson(binding.Renderer != null ? binding.Renderer.sharedMaterial : null),
                    ["defaultsApplied"] = defaults.ToArray(),
                    ["applied"] = plan.Patch.Writes.Select(write => write.Field.Path).ToArray(),
                    ["state"] = ReadState(plan.Patch, binding),
                    ["playback"] = PlaybackState(system),
                    ["warnings"] = warnings.ToArray()
                };
                if (adjustments.Count > 0)
                    result["adjustments"] = adjustments;
                return result;
            }, cleanup: () =>
            {
                if (created != null && !onUndoStack)
                    UnityEngine.Object.DestroyImmediate(created);
            });
        }

        /// <summary>
        /// Adds a ParticleSystem to an object that has none. Unity adds the ParticleSystemRenderer itself; both are
        /// recorded as created (the renderer as modified when the object already had one), so Undo and workflow
        /// rollback remove exactly what this call added.
        /// </summary>
        internal static Dictionary<string, object> AddSystem(GameObject go, Patch patch, List<string> warnings)
        {
            var label = $"Particle: add system to {go.name}";
            return Transaction(label, () =>
            {
                var existingRenderer = go.GetComponent<ParticleSystemRenderer>();
                if (existingRenderer != null)
                {
                    WorkflowManager.SnapshotObject(existingRenderer);
                    Undo.RecordObject(existingRenderer, label);
                }

                var system = Undo.AddComponent<ParticleSystem>(go);
                if (system == null)
                    throw new InvalidOperationException($"Unity refused to add a ParticleSystem to '{go.name}'.");
                var renderer = go.GetComponent<ParticleSystemRenderer>();
                WorkflowManager.SnapshotCreatedComponent(system);
                if (renderer != null && existingRenderer == null)
                    WorkflowManager.SnapshotCreatedComponent(renderer);

                Undo.RecordObject(system, label);
                if (renderer != null && existingRenderer == null)
                    Undo.RecordObject(renderer, label);

                var binding = new ParticleBinding { GameObject = go, System = system, Renderer = renderer };
                var requested = patch.Writes.Select(write => Codec.WriteField(write.Field, write.Value)).ToList();
                var defaults = ApplyCreationDefaults(binding, patch);
                ApplyWrites(binding, patch);
                Undo.FlushUndoRecordObjects();
                if (existingRenderer != null)
                    RecordPrefabOverrides(existingRenderer);

                var adjustments = FindAdjustments(binding, patch, requested, warnings);
                var result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = go.name,
                    ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                    ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                    ["path"] = GameObjectFinder.GetPath(go),
                    ["particleSystem"] = ComponentIdentity(system),
                    ["renderer"] = ComponentIdentity(renderer),
                    ["rendererCreated"] = renderer != null && existingRenderer == null,
                    ["material"] = Codec.AssetToJson(renderer != null ? renderer.sharedMaterial : null),
                    ["defaultsApplied"] = defaults.ToArray(),
                    ["applied"] = patch.Writes.Select(write => write.Field.Path).ToArray(),
                    ["state"] = ReadState(patch, binding),
                    ["warnings"] = warnings.ToArray()
                };
                if (adjustments.Count > 0)
                    result["adjustments"] = adjustments;
                return result;
            });
        }
    }
}
#endif

// Producer:Betsy
