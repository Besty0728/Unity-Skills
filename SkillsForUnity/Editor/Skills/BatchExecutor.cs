using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// Generic batch execution framework for UnitySkills.
    /// Eliminates boilerplate from batch skill methods by handling JSON deserialization,
    /// per-item error handling, and result aggregation.
    /// </summary>
    public static class BatchExecutor
    {
        /// <summary>
        /// Execute a batch operation on a JSON array of items.
        /// Handles deserialization, per-item try/catch, and result aggregation.
        /// </summary>
        /// <typeparam name="TItem">The item type to deserialize from JSON</typeparam>
        /// <param name="itemsJson">JSON array string</param>
        /// <param name="processor">Function that processes each item and returns a result object.
        /// On success, return an anonymous object with the desired fields.
        /// On failure, throw an exception or return an object with an "error" field.</param>
        /// <param name="itemIdentifier">Optional function to extract a display name from each item for error reporting</param>
        /// <param name="setup">Optional action to run before processing items (e.g. AssetDatabase.StartAssetEditing)</param>
        /// <param name="teardown">Optional action to run after processing items, even if errors occur (e.g. AssetDatabase.StopAssetEditing)</param>
        /// <returns>Standardized batch result with success, totalItems, successCount, failCount, results</returns>
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
                        // Check if result contains an error field (processor returned error without throwing)
                        bool isError = false;
                        if (result != null)
                        {
                            var resultType = result.GetType();
                            if (resultType.GetProperty("error") != null || resultType.GetField("error") != null)
                                isError = true;
                        }
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
                totalItems = itemList.Count,
                successCount,
                failCount,
                results
            };
        }
    }
}
