using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// UnitySkills 通用批处理执行框架：统一处理 JSON 反序列化、逐项错误捕获与结果汇总，
    /// 免去各批量技能的样板代码。
    /// </summary>
    public static class BatchExecutor
    {
        // 同一结果类型的反射结论不会变化，缓存"是否有 error 成员"，
        // 避免大批量时对每一项重复 GetProperty/GetField。
        private static readonly ConcurrentDictionary<Type, bool> _hasErrorMemberCache = new ConcurrentDictionary<Type, bool>();

        private static bool HasErrorMember(Type type)
        {
            return _hasErrorMemberCache.GetOrAdd(type, static t =>
                t.GetProperty("error") != null || t.GetField("error") != null);
        }

        /// <summary>
        /// 对一个 JSON 数组逐项执行批量操作，负责反序列化、逐项 try/catch 与结果汇总。
        /// </summary>
        /// <typeparam name="TItem">从 JSON 反序列化出的条目类型</typeparam>
        /// <param name="itemsJson">JSON 数组字符串</param>
        /// <param name="processor">逐项处理函数：成功返回带所需字段的匿名对象，
        /// 失败可抛异常或返回带 "error" 字段的对象。</param>
        /// <param name="itemIdentifier">可选，从条目提取用于报错的显示名</param>
        /// <param name="setup">可选，处理前执行（如 AssetDatabase.StartAssetEditing）</param>
        /// <param name="teardown">可选，处理后必定执行，出错也会执行（如 AssetDatabase.StopAssetEditing）</param>
        /// <returns>标准批量结果：success、totalItems、successCount、failCount、results</returns>
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
                        // processor 也可能不抛异常而返回带 error 字段的对象，这里同样计入失败。
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
