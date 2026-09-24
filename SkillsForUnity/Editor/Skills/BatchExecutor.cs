using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// UnitySkills' generic batch execution framework: unifies JSON deserialization, per-item error
    /// capture, and result aggregation, saving every batch skill from repeating that boilerplate.
    /// </summary>
    public static class BatchExecutor
    {
        private const int MaxUnknownFieldWarnings = 20;
        private const int MaxListedItemIndexes = 10;

        // The reflection verdict for a given result type never changes, so cache "does it have an error member"
        // to avoid repeating GetProperty/GetField per item on large batches.
        private static readonly ConcurrentDictionary<Type, bool> _hasErrorMemberCache = new ConcurrentDictionary<Type, bool>();

        // The JSON contract of an item type, resolved once. Null for types whose unknown keys can't be judged:
        // non-object contracts, and [JsonExtensionData] types that capture every key.
        private static readonly ConcurrentDictionary<Type, JsonObjectContract> _itemContractCache =
            new ConcurrentDictionary<Type, JsonObjectContract>();

        private static bool HasErrorMember(Type type)
        {
            return _hasErrorMemberCache.GetOrAdd(type, static t =>
                t.GetProperty("error") != null || t.GetField("error") != null);
        }

        private static bool IsErrorResult(object result)
        {
            if (result == null)
                return false;
            if (result is JObject json)
            {
                var errorToken = json.GetValue("error", StringComparison.OrdinalIgnoreCase);
                return errorToken != null && errorToken.Type != JTokenType.Null;
            }
            if (result is IDictionary<string, object> dictionary)
                return dictionary.TryGetValue("error", out var errorValue) && errorValue != null;
            return HasErrorMember(result.GetType());
        }

        /// <summary>
        /// Executes a batch operation over a JSON array item by item, handling deserialization,
        /// per-item try/catch, and result aggregation.
        /// </summary>
        /// <typeparam name="TItem">The item type deserialized from JSON</typeparam>
        /// <param name="itemsJson">The JSON array string</param>
        /// <param name="processor">Per-item processing function: return an anonymous object with the
        /// needed fields on success; on failure, either throw or return an object with an "error" field.</param>
        /// <param name="itemIdentifier">Optional; extracts a display name from an item for error reporting</param>
        /// <param name="setup">Optional; runs before processing (e.g. AssetDatabase.StartAssetEditing)</param>
        /// <param name="teardown">Optional; always runs after processing, even on error (e.g. AssetDatabase.StopAssetEditing)</param>
        /// <param name="atomic">Only for batches whose every effect is recorded on the Undo stack (scene edits). When any
        /// item fails, every Undo-recorded change of this call is reverted, the items that had succeeded are reported as
        /// <c>success:false, reverted:true</c>, and the envelope carries <c>rolledBack:true</c> + <c>revertedCount</c>.
        /// The outcome is then all-or-nothing whoever invokes the skill; over REST the router reverts a failed write's
        /// Undo group anyway, so without this the results would claim success for work that no longer exists.</param>
        /// <returns>Standard batch result: success, totalItems, successCount, failCount, results; plus
        /// <c>warnings</c> naming item keys that match no field of <typeparamref name="TItem"/> (ignored, not rejected).</returns>
        public static object Execute<TItem>(
            string itemsJson,
            Func<TItem, object> processor,
            Func<TItem, string> itemIdentifier = null,
            Action setup = null,
            Action teardown = null,
            bool atomic = false)
        {
            if (string.IsNullOrEmpty(itemsJson))
                return new { error = "items parameter is required" };

            List<TItem> itemList;
            try
            {
                itemList = JsonConvert.DeserializeObject<List<TItem>>(itemsJson);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to parse items JSON: {ex.Message}" };
            }

            var warnings = DescribeUnknownFields(typeof(TItem), itemsJson);

            int undoGroup = -1;
            int workflowSnapshotsBefore = 0;
            if (atomic)
            {
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                workflowSnapshotsBefore = WorkflowManager.CurrentTask?.snapshots?.Count ?? 0;
            }

            var results = new List<object>();
            var succeededIndexes = new List<int>();
            var failedIndexes = new List<int>();

            if (setup != null) setup();
            try
            {
                for (int index = 0; index < itemList.Count; index++)
                {
                    var item = itemList[index];
                    try
                    {
                        var result = processor(item);
                        // processor may also return an object with an "error" field instead of throwing; count that as a failure too.
                        results.Add(result);
                        if (IsErrorResult(result))
                            failedIndexes.Add(index);
                        else
                            succeededIndexes.Add(index);
                    }
                    catch (Exception ex)
                    {
                        string id = itemIdentifier != null ? itemIdentifier(item) : item?.ToString();
                        results.Add(new { target = id, success = false, error = ex.Message });
                        failedIndexes.Add(index);
                    }
                }
            }
            finally
            {
                if (teardown != null) teardown();
            }

            int failCount = failedIndexes.Count;
            int successCount = succeededIndexes.Count;
            bool rolledBack = atomic && failCount > 0;
            if (rolledBack)
            {
                Undo.FlushUndoRecordObjects();
                Undo.RevertAllDownToGroup(undoGroup);
                if (WorkflowManager.IsRecording)
                    WorkflowManager.TruncateCurrentTask(workflowSnapshotsBefore);
                GameObjectFinder.InvalidateCache();

                var reason = $"Rolled back: the batch is all-or-nothing and items[{FormatIndexes(failedIndexes)}] failed.";
                var serializer = JsonSerializer.Create(SkillsCommon.JsonSettings);
                foreach (var index in succeededIndexes)
                    results[index] = MarkReverted(results[index], reason, serializer);
                successCount = 0;
            }

            var envelope = new Dictionary<string, object>
            {
                ["success"] = failCount == 0,
                ["error"] = failCount == 0 ? null
                    : rolledBack ? $"Batch failed: {failCount} of {itemList.Count} item(s) failed and the batch was rolled back, so nothing was changed."
                    : $"Batch completed with {failCount} failed item(s).",
                ["errorCode"] = failCount == 0 ? null : "SEMANTIC_INVALID",
                ["retryStrategy"] = failCount == 0 ? null : SkillErrorResponse.RetryFixAndRetry,
                ["suggestedFixes"] = failCount == 0 ? null : new[]
                {
                    new
                    {
                        action = "fix_param",
                        reason = rolledBack
                            ? "Correct the failed items listed in results, then resend the whole batch — none of it was applied."
                            : "Inspect failed item results, correct those inputs, then retry the batch."
                    }
                },
                ["totalItems"] = itemList.Count,
                ["successCount"] = successCount,
                ["failCount"] = failCount,
            };
            if (rolledBack)
            {
                envelope["rolledBack"] = true;
                envelope["revertedCount"] = succeededIndexes.Count;
            }
            if (warnings.Count > 0)
                envelope["warnings"] = warnings;
            envelope["results"] = results;
            return envelope;
        }

        private static object MarkReverted(object result, string reason, JsonSerializer serializer)
        {
            JObject json = null;
            try
            {
                if (result != null)
                    json = JToken.FromObject(result, serializer) as JObject;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Batch result could not be re-shaped after rollback: {ex.Message}");
            }

            json = json ?? new JObject();
            json["success"] = false;
            json["reverted"] = true;
            json["revertReason"] = reason;
            return json;
        }

        /// <summary>
        /// Item keys that bind to no field of the item type are silently dropped by the deserializer, so a typo such as
        /// "parentpathh" would otherwise run the item without its parent. Each such key is reported once, with the items
        /// carrying it and the closest real field name. Matching is Newtonsoft's own (exact, then case-insensitive).
        /// </summary>
        private static List<string> DescribeUnknownFields(Type itemType, string itemsJson)
        {
            var warnings = new List<string>();
            try
            {
                var contract = _itemContractCache.GetOrAdd(itemType, ResolveItemContract);
                if (contract == null || !(JToken.Parse(itemsJson) is JArray array))
                    return warnings;

                var unknown = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                var order = new List<string>();
                for (int i = 0; i < array.Count; i++)
                {
                    if (!(array[i] is JObject item))
                        continue;

                    foreach (var property in item.Properties())
                    {
                        var match = contract.Properties.GetClosestMatchProperty(property.Name);
                        if (match != null && !match.Ignored)
                            continue;

                        if (!unknown.TryGetValue(property.Name, out var indexes))
                        {
                            indexes = new List<int>();
                            unknown[property.Name] = indexes;
                            order.Add(property.Name);
                        }
                        indexes.Add(i);
                    }
                }

                if (order.Count == 0)
                    return warnings;

                var known = contract.Properties.Where(p => !p.Ignored).Select(p => p.PropertyName).ToArray();
                foreach (var field in order.Take(MaxUnknownFieldWarnings))
                {
                    var suggestion = SuggestField(field, known);
                    warnings.Add($"items[{FormatIndexes(unknown[field])}]: unknown field '{field}' ignored" +
                                 (suggestion != null ? $" (did you mean '{suggestion}'?)" : string.Empty));
                }
                if (order.Count > MaxUnknownFieldWarnings)
                    warnings.Add($"(+{order.Count - MaxUnknownFieldWarnings} more unknown field name(s) omitted)");
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Batch unknown-field check skipped: {ex.Message}");
            }
            return warnings;
        }

        private static JsonObjectContract ResolveItemContract(Type itemType)
        {
            var contract = JsonSerializer.CreateDefault().ContractResolver.ResolveContract(itemType) as JsonObjectContract;
            return contract == null || contract.ExtensionDataSetter != null ? null : contract;
        }

        private static string SuggestField(string field, string[] known) => SkillsCommon.ClosestMatch(field, known);

        private static string FormatIndexes(List<int> indexes)
        {
            var shown = string.Join(",", indexes.Take(MaxListedItemIndexes));
            return indexes.Count > MaxListedItemIndexes ? $"{shown},...(+{indexes.Count - MaxListedItemIndexes})" : shown;
        }
    }
}

// Producer:Betsy
