using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// UnitySkills' generic batch execution framework: unifies JSON deserialization, per-item error
    /// capture, and result aggregation, saving every batch skill from repeating that boilerplate.
    /// </summary>
    public static class BatchExecutor
    {
        // The reflection verdict for a given result type never changes, so cache "does it have an error member"
        // to avoid repeating GetProperty/GetField per item on large batches.
        private static readonly ConcurrentDictionary<Type, bool> _hasErrorMemberCache = new ConcurrentDictionary<Type, bool>();

        private static bool HasErrorMember(Type type)
        {
            return _hasErrorMemberCache.GetOrAdd(type, static t =>
                t.GetProperty("error") != null || t.GetField("error") != null);
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
        /// <returns>Standard batch result: success, totalItems, successCount, failCount, results</returns>
        public static object Execute<TItem>(
            string itemsJson,
            Func<TItem, object> processor,
            Func<TItem, string> itemIdentifier = null,
            Action setup = null,
            Action teardown = null)
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

            var results = new List<object>();
            int successCount = 0;
            int failCount = 0;

            if (setup != null) setup();
            try
            {
                foreach (var item in itemList)
                {
                    try
                    {
                        var result = processor(item);
                        // processor may also return an object with an "error" field instead of throwing; count that as a failure too.
                        bool isError = result != null && HasErrorMember(result.GetType());
                        results.Add(result);
                        if (isError)
                            failCount++;
                        else
                            successCount++;
                    }
                    catch (Exception ex)
                    {
                        string id = itemIdentifier != null ? itemIdentifier(item) : item?.ToString();
                        results.Add(new { target = id, success = false, error = ex.Message });
                        failCount++;
                    }
                }
            }
            finally
            {
                if (teardown != null) teardown();
            }

            return new
            {
                success = failCount == 0,
                error = failCount == 0 ? null : $"Batch completed with {failCount} failed item(s).",
                errorCode = failCount == 0 ? null : "SEMANTIC_INVALID",
                retryStrategy = failCount == 0 ? null : SkillErrorResponse.RetryFixAndRetry,
                suggestedFixes = failCount == 0 ? null : new[]
                {
                    new { action = "fix_param", reason = "Inspect failed item results, correct those inputs, then retry the batch." }
                },
                totalItems = itemList.Count,
                successCount,
                failCount,
                results
            };
        }
    }
}

// Producer:Betsy
