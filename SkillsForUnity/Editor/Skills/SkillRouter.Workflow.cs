using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillRouter
    {
        /// <summary>
        /// Automatically snapshots target objects from skill parameters, to support generic rollback.
        /// Recognizes common target parameters (name, instanceId, path, materialPath, etc.) and snapshots them.
        /// Target location is delegated to <see cref="CollectTargetsFromArgs"/>,
        /// so the semantic diff's pre-capture reuses exactly the same object set, order, and best-effort semantics.
        /// </summary>
        /// <summary>
        /// Whether the current manually-recorded session (workflow_begin_task) has anything new to persist since the last SaveHistory --
        /// i.e. a different task is now active, or the active task gained new snapshots. Every time it returns true, it advances the saved marker,
        /// so the next call compares against this save point. Best-effort: defaults to saving on any anomaly (a null task),
        /// to guarantee history is never silently dropped.
        /// </summary>
        private static bool ManualSessionIsDirty(WorkflowTask currentTask)
        {
            if (currentTask == null)
                return true; // shouldn't happen while IsRecording; save defensively

            int count = currentTask.snapshots?.Count ?? 0;
            if (currentTask.id == _lastSavedTaskId && count == _lastSavedSnapshotCount)
                return false;

            _lastSavedTaskId = currentTask.id;
            _lastSavedSnapshotCount = count;
            return true;
        }

        private static void TrySnapshotTargetsFromArgs(JObject args)
        {
            try
            {
                foreach (var obj in CollectTargetsFromArgs(args))
                    WorkflowManager.SnapshotObject(obj);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Workflow snapshot failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Locates the UnityEngine.Object a skill parameter points to -- the shared low-level primitive behind both the
        /// automatic workflow snapshot (<see cref="TrySnapshotTargetsFromArgs"/>) and the semantic diff's
        /// pre-capture (<see cref="SkillSceneDiff.CaptureBefore"/>).
        ///
        /// Objects are returned in a fixed order consistent with the historical snapshot sequence, to keep snapshot behavior unchanged:
        /// the target GameObject + its Transform + Renderer.sharedMaterial, then the asset pointed to by materialPath / assetPath,
        /// then child Transforms, and finally each target in items[]
        /// (GameObject + Transform, capped at the first 50). Location is best-effort; a target that can't be resolved is skipped.
        /// The items[] section carries its own try/catch, so a malformed batch never interrupts the rest -- matching the original inline behavior.
        /// </summary>
        internal static List<UnityEngine.Object> CollectTargetsFromArgs(JObject args)
        {
            var targets = new List<UnityEngine.Object>();

            // Tries to locate the target GameObject by common parameter names
            string targetName = null;
            int targetInstanceId = 0;
            string targetPath = null;
            string targetEntityId = null;

            if (args.TryGetValue("name", StringComparison.OrdinalIgnoreCase, out var nameToken))
                targetName = nameToken.ToString();
            if (args.TryGetValue("instanceId", StringComparison.OrdinalIgnoreCase, out var idToken))
                targetInstanceId = idToken.ToObject<int>();
            if (args.TryGetValue("path", StringComparison.OrdinalIgnoreCase, out var pathToken))
                targetPath = pathToken.ToString();
            if (args.TryGetValue(EntityIdParameterName, StringComparison.OrdinalIgnoreCase, out var entityIdToken))
                targetEntityId = entityIdToken.ToString();

            // Snapshot the GameObject once it's identified
            if (!string.IsNullOrEmpty(targetEntityId) || !string.IsNullOrEmpty(targetName) || targetInstanceId != 0 || !string.IsNullOrEmpty(targetPath))
            {
                var (go, _) = GameObjectFinder.FindOrError(targetName, targetInstanceId, targetPath, entityId: targetEntityId);
                if (go != null)
                {
                    targets.Add(go);
                    // Transform is the most commonly modified, snapshot it too
                    targets.Add(go.transform);
                    // If there's a Renderer, snapshot its material
                    var renderer = go.GetComponent<UnityEngine.Renderer>();
                    if (renderer != null && renderer.sharedMaterial != null)
                        targets.Add(renderer.sharedMaterial);
                }
            }

            // Snapshot the material asset when materialPath is given
            if (args.TryGetValue("materialPath", StringComparison.OrdinalIgnoreCase, out var matPathToken))
            {
                var matPath = matPathToken.ToString();
                if (!string.IsNullOrEmpty(matPath))
                {
                    var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(matPath);
                    if (mat != null)
                        targets.Add(mat);
                }
            }

            // Snapshot the asset when assetPath is given
            if (args.TryGetValue("assetPath", StringComparison.OrdinalIgnoreCase, out var assetPathToken))
            {
                var assetPath = assetPathToken.ToString();
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    if (asset != null)
                        targets.Add(asset);
                }
            }

            // Handles child/parent-style operations (snapshot with entityId fallback)
            {
                args.TryGetValue("childName", StringComparison.OrdinalIgnoreCase, out var childNameToken);
                args.TryGetValue("childEntityId", StringComparison.OrdinalIgnoreCase, out var childEntityIdToken);
                args.TryGetValue("childInstanceId", StringComparison.OrdinalIgnoreCase, out var childInstanceIdToken);
                args.TryGetValue("childPath", StringComparison.OrdinalIgnoreCase, out var childPathToken);
                var childEntityId = childEntityIdToken?.ToString();
                var childName = childNameToken?.ToString();
                int.TryParse(childInstanceIdToken?.ToString(), out int childInstanceId);
                var childPath = childPathToken?.ToString();
                if (!string.IsNullOrEmpty(childEntityId) || !string.IsNullOrEmpty(childName) || childInstanceId != 0 || !string.IsNullOrEmpty(childPath))
                {
                    var (childGo, _) = GameObjectFinder.FindOrError(childName, childInstanceId, childPath, entityId: childEntityId);
                    if (childGo != null)
                        targets.Add(childGo.transform);
                }
            }

            // Handles batch entries: snapshot each target in the batch individually
            if (args.TryGetValue("items", StringComparison.OrdinalIgnoreCase, out var itemsToken))
            {
                try
                {
                    var items = itemsToken.ToObject<List<Dictionary<string, object>>>();
                    if (items != null)
                    {
                        foreach (var item in items.Take(50)) // Limit to avoid performance issues
                        {
                            string itemName = item.ContainsKey("name") ? item["name"]?.ToString() : null;
                            int itemId = item.ContainsKey("instanceId") ? Convert.ToInt32(item["instanceId"]) : 0;
                            string itemPath = item.ContainsKey("path") ? item["path"]?.ToString() : null;
                            string itemEntityId = item.ContainsKey(EntityIdParameterName) ? item[EntityIdParameterName]?.ToString() : null;

                            if (!string.IsNullOrEmpty(itemEntityId) || !string.IsNullOrEmpty(itemName) || itemId != 0 || !string.IsNullOrEmpty(itemPath))
                            {
                                var (itemGo, _) = GameObjectFinder.FindOrError(itemName, itemId, itemPath, entityId: itemEntityId);
                                if (itemGo != null)
                                {
                                    targets.Add(itemGo);
                                    targets.Add(itemGo.transform);
                                }
                            }
                        }
                    }
                }
                catch { /* Ignored when batch parsing fails */ }
            }

            return targets;
        }
    }
}

// Producer:Betsy
