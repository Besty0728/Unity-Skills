using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// 写操作可选语义 diff（POST /skill/{name}?diff=1）的纯旁路观察者。
    ///
    /// 在 skill 执行前后对"从 args 定位到的目标对象"做 <see cref="EditorJsonUtility"/> 快照对比，
    /// 报告本次操作实际改动的叶子字段（changed）、新建对象（added）、被销毁对象（removed），
    /// 免去 AI 二次查询确认。
    ///
    /// 硬约束：本类全程异常隔离——前捕获 / 对比 / added 扫描的任何失败都**不得**影响 skill 执行，
    /// 只让 sceneDiff 降级为 {error:...}。它是观察者，不参与 undo / workflow / 错误分支。
    /// </summary>
    internal static class SkillSceneDiff
    {
        // 前捕获对象数上限：超出只捕获前 N 个并置 captureLimited。
        private const int MaxCaptureObjects = 20;
        // 单对象 changed 叶子上限：超出置 truncated。
        private const int MaxChangesPerObject = 50;
        private const int MaxBatchCaptureObjects = 100;

        /// <summary>
        /// 前捕获单对象记录。对象销毁后 Unity 假 null 会让 obj.name / GetType() 抛异常，
        /// 故 name / typeName / instanceId 必须在前捕获时就固化，供 removed 报告使用。
        /// </summary>
        internal sealed class ObjectSnapshot
        {
            public UnityEngine.Object Obj;
            public int InstanceId;
            public int OwnerGameObjectId;
            public string Name;
            public string TypeName;
            public string BeforeJson;
        }

        internal sealed class DiffCapture
        {
            public readonly List<ObjectSnapshot> Snapshots = new List<ObjectSnapshot>();
            // 前捕获对象的 instanceId 集，用于 added 阶段排除"本就存在的目标"。
            public readonly HashSet<int> CapturedInstanceIds = new HashSet<int>();
            public bool Limited;
            public bool HadTargets;
            public string Error;
        }

        internal sealed class BatchDiffCapture
        {
            public readonly Dictionary<int, ObjectSnapshot> Snapshots = new Dictionary<int, ObjectSnapshot>();
            public readonly Dictionary<int, UnityEngine.Object> AddedObjects = new Dictionary<int, UnityEngine.Object>();
            public bool Limited;
            public bool HadWritableSteps;
            public string Error;
        }

        internal static BatchDiffCapture CreateBatchCapture() => new BatchDiffCapture();

        internal static void CaptureBatchStepBefore(BatchDiffCapture capture, JObject args)
        {
            if (capture == null || !string.IsNullOrEmpty(capture.Error)) return;
            capture.HadWritableSteps = true;
            try
            {
                var targets = SkillRouter.CollectTargetsFromArgs(args) ?? new List<UnityEngine.Object>();
                AppendComponentTarget(args, targets);
                foreach (var obj in targets)
                {
                    if (obj == null) continue;
                    var id = obj.GetInstanceID();
                    if (capture.AddedObjects.ContainsKey(id) || IsPartOfAddedObject(capture, obj) || capture.Snapshots.ContainsKey(id)) continue;
                    if (capture.Snapshots.Count >= MaxBatchCaptureObjects)
                    {
                        capture.Limited = true;
                        break;
                    }
                    capture.Snapshots[id] = new ObjectSnapshot
                    {
                        Obj = obj,
                        InstanceId = id,
                        OwnerGameObjectId = obj is Component component ? component.gameObject.GetInstanceID() : 0,
                        Name = obj.name,
                        TypeName = obj.GetType().Name,
                        BeforeJson = EditorJsonUtility.ToJson(obj),
                    };
                }
            }
            catch (Exception ex)
            {
                capture.Error = $"batch capture failed: {ex.Message}";
            }
        }

        internal static void TrackBatchStepResult(BatchDiffCapture capture, object result)
        {
            if (capture == null || !string.IsNullOrEmpty(capture.Error)) return;
            try
            {
                var token = result as JToken ?? JToken.FromObject(result ?? new object(), JsonSerializer.Create(SkillsCommon.JsonSettings));
                var entityIds = new List<string>();
                var instanceIds = new List<int>();
                CollectIds(token, entityIds, instanceIds);
                foreach (var entityId in entityIds)
                    TrackAddedObject(capture, UnityObjectIdUtility.EntityIdToObject(entityId));
                foreach (var instanceId in instanceIds)
                    TrackAddedObject(capture, UnityObjectIdUtility.ObjectIdToObject(instanceId));
            }
            catch { }
        }

        internal static JObject BuildBatch(BatchDiffCapture capture)
        {
            if (capture == null) return new JObject { ["note"] = "no batch diff captured" };
            if (!string.IsNullOrEmpty(capture.Error)) return new JObject { ["error"] = capture.Error };
            if (!capture.HadWritableSteps) return new JObject { ["note"] = "read-only batch, no diff captured" };

            try
            {
                var changed = new JArray();
                var removed = new JArray();
                var removedGameObjectIds = new HashSet<int>(capture.Snapshots.Values
                    .Where(snap => snap.Obj == null && snap.TypeName == nameof(GameObject))
                    .Select(snap => snap.InstanceId));
                foreach (var snap in capture.Snapshots.Values)
                {
                    if (snap.Obj == null)
                    {
                        if (snap.TypeName != nameof(GameObject) && WasComponentOfRemovedGameObject(capture, snap, removedGameObjectIds))
                            continue;
                        removed.Add(new JObject { ["name"] = snap.Name, ["type"] = snap.TypeName, ["instanceId"] = snap.InstanceId });
                        continue;
                    }
                    string afterJson;
                    try { afterJson = EditorJsonUtility.ToJson(snap.Obj); }
                    catch { continue; }
                    var changes = CompareJson(snap.BeforeJson, afterJson, out var truncated);
                    if (changes.Count == 0) continue;
                    changed.Add(new JObject
                    {
                        ["target"] = new JObject { ["name"] = snap.Obj.name, ["type"] = snap.TypeName, ["instanceId"] = snap.InstanceId },
                        ["changes"] = new JArray(changes),
                        ["truncated"] = truncated,
                    });
                }

                var added = new JArray();
                foreach (var pair in capture.AddedObjects)
                {
                    var obj = pair.Value;
                    if (obj == null) continue;
                    string path = obj is GameObject go ? GameObjectFinder.GetPath(go) : AssetDatabase.GetAssetPath(obj);
                    added.Add(new JObject
                    {
                        ["name"] = obj.name, ["type"] = obj.GetType().Name,
                        ["instanceId"] = pair.Key, ["path"] = string.IsNullOrEmpty(path) ? null : path,
                    });
                }
                return new JObject
                {
                    ["changed"] = changed,
                    ["added"] = added,
                    ["removed"] = removed,
                    ["captureLimited"] = capture.Limited,
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["error"] = $"batch diff failed: {ex.Message}" };
            }
        }

        private static void TrackAddedObject(BatchDiffCapture capture, UnityEngine.Object obj)
        {
            if (obj == null) return;
            var id = obj.GetInstanceID();
            if (!capture.Snapshots.ContainsKey(id) && !capture.AddedObjects.ContainsKey(id))
                capture.AddedObjects[id] = obj;
        }

        private static bool IsPartOfAddedObject(BatchDiffCapture capture, UnityEngine.Object obj)
        {
            if (obj is Component component && component.gameObject != null)
                return capture.AddedObjects.ContainsKey(component.gameObject.GetInstanceID());
            return false;
        }

        private static bool WasComponentOfRemovedGameObject(
            BatchDiffCapture capture, ObjectSnapshot componentSnapshot, HashSet<int> removedGameObjectIds)
        {
            if (removedGameObjectIds.Count == 0 || componentSnapshot == null)
                return false;

            // Destroyed Unity components can no longer expose gameObject, so ownership is fixed in
            // the pre-execution snapshot and remains usable after Unity's fake-null transition.
            return componentSnapshot.OwnerGameObjectId != 0 &&
                removedGameObjectIds.Contains(componentSnapshot.OwnerGameObjectId);
        }

        /// <summary>
        /// invoke 前捕获：复用 SkillRouter 的共享目标定位（<see cref="SkillRouter.CollectTargetsFromArgs"/>），
        /// 额外把 componentType 指向的组件实例纳入捕获集（component_set_property 类 skill 改的正是组件，
        /// 是 diff 最高频价值点）。对每个对象固化 (instanceId, name, typeName, EditorJsonUtility.ToJson)。
        /// 上限 <see cref="MaxCaptureObjects"/>，超出置 Limited。任何异常降级为 Error。
        /// </summary>
        public static DiffCapture CaptureBefore(JObject args)
        {
            var capture = new DiffCapture();
            try
            {
                var targets = SkillRouter.CollectTargetsFromArgs(args) ?? new List<UnityEngine.Object>();
                AppendComponentTarget(args, targets);

                foreach (var obj in targets)
                {
                    if (obj == null)
                        continue;
                    int id = obj.GetInstanceID();
                    if (capture.CapturedInstanceIds.Contains(id))
                        continue; // 同一对象被多个定位规则命中时去重
                    if (capture.Snapshots.Count >= MaxCaptureObjects)
                    {
                        capture.Limited = true;
                        break;
                    }

                    capture.CapturedInstanceIds.Add(id);
                    capture.Snapshots.Add(new ObjectSnapshot
                    {
                        Obj = obj,
                        InstanceId = id,
                        OwnerGameObjectId = obj is Component component ? component.gameObject.GetInstanceID() : 0,
                        Name = obj.name,
                        TypeName = obj.GetType().Name,
                        BeforeJson = EditorJsonUtility.ToJson(obj),
                    });
                }

                capture.HadTargets = capture.Snapshots.Count > 0;
            }
            catch (Exception ex)
            {
                capture.Error = $"capture failed: {ex.Message}";
                SkillsLogger.LogVerbose($"[diff] before-capture failed: {ex.Message}");
            }
            return capture;
        }

        /// <summary>
        /// invoke 成功后对比：同集合对象重新 ToJson，销毁的计入 removed，其余逐叶子对比得 changed；
        /// 再从成功 result 里扫描新建对象得 added。任何异常降级为 {error:...}。
        /// </summary>
        public static JObject Build(DiffCapture capture, object result)
        {
            if (capture == null)
                return new JObject { ["note"] = "no diff captured" };
            if (!string.IsNullOrEmpty(capture.Error))
                return new JObject { ["error"] = capture.Error };

            try
            {
                var changed = new JArray();
                var removed = new JArray();

                foreach (var snap in capture.Snapshots)
                {
                    // Unity 假 null：对象在执行期间被销毁。用前捕获固化的展示字段报告。
                    if (snap.Obj == null)
                    {
                        removed.Add(new JObject
                        {
                            ["name"] = snap.Name,
                            ["type"] = snap.TypeName,
                            ["instanceId"] = snap.InstanceId,
                        });
                        continue;
                    }

                    string afterJson;
                    try { afterJson = EditorJsonUtility.ToJson(snap.Obj); }
                    catch { continue; }

                    var changes = CompareJson(snap.BeforeJson, afterJson, out bool truncated);
                    if (changes.Count == 0)
                        continue;

                    changed.Add(new JObject
                    {
                        ["target"] = new JObject
                        {
                            ["name"] = snap.Obj.name,
                            ["type"] = snap.TypeName,
                            ["instanceId"] = snap.InstanceId,
                        },
                        ["changes"] = new JArray(changes),
                        ["truncated"] = truncated,
                    });
                }

                var added = BuildAdded(result, capture.CapturedInstanceIds);

                var diff = new JObject();
                if (!capture.HadTargets)
                    diff["note"] = "no identifiable targets from args";
                diff["changed"] = changed;
                diff["added"] = added;
                diff["removed"] = removed;
                diff["captureLimited"] = capture.Limited;
                return diff;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[diff] compare failed: {ex.Message}");
                return new JObject { ["error"] = $"compare failed: {ex.Message}" };
            }
        }

        /// <summary>
        /// 若 args 含 componentType 且捕获集里已定位到目标 GameObject，把该类型的组件实例也纳入。
        /// 类型解析复用 <see cref="ComponentSkills.FindComponentType"/>（不新写类型搜索）。
        /// </summary>
        private static void AppendComponentTarget(JObject args, List<UnityEngine.Object> targets)
        {
            if (!TryGetString(args, "componentType", out var componentType))
                return;
            var go = targets.OfType<GameObject>().FirstOrDefault();
            if (go == null)
                return;
            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null)
                return;
            var comp = go.GetComponent(type);
            if (comp != null)
                targets.Add(comp);
        }

        /// <summary>
        /// 从成功 result 的 JToken 里深度收集 entityId / instanceId，解回对象，排除前捕获集已有者。
        /// entityId 优先（Unity 6000.4+ result 里 instanceId 恒为 0，entityId 才是真标识），
        /// 解回统一走 <see cref="UnityObjectIdUtility"/> 兼容层规避 obsolete API。
        /// </summary>
        private static JArray BuildAdded(object result, HashSet<int> capturedInstanceIds)
        {
            var added = new JArray();
            try
            {
                JToken token;
                try { token = JToken.FromObject(result ?? new object(), JsonSerializer.Create(SkillsCommon.JsonSettings)); }
                catch { return added; }

                var entityIds = new List<string>();
                var instanceIds = new List<int>();
                CollectIds(token, entityIds, instanceIds);

                var seen = new HashSet<int>();
                foreach (var eid in entityIds)
                    TryAddNewObject(UnityObjectIdUtility.EntityIdToObject(eid), capturedInstanceIds, seen, added);
                foreach (var iid in instanceIds)
                    TryAddNewObject(UnityObjectIdUtility.ObjectIdToObject(iid), capturedInstanceIds, seen, added);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[diff] added-scan failed: {ex.Message}");
            }
            return added;
        }

        private static void TryAddNewObject(UnityEngine.Object obj, HashSet<int> capturedInstanceIds, HashSet<int> seen, JArray added)
        {
            if (obj == null)
                return;
            int id = obj.GetInstanceID();
            if (capturedInstanceIds.Contains(id))
                return; // 前捕获集已有 → 不是本次新建
            if (!seen.Add(id))
                return; // 同一新对象被多个 id 字段指向，只报一次

            string path = null;
            if (obj is GameObject go)
                path = GameObjectFinder.GetPath(go);
            else
            {
                var assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath))
                    path = assetPath;
            }

            added.Add(new JObject
            {
                ["name"] = obj.name,
                ["type"] = obj.GetType().Name,
                ["instanceId"] = id,
                ["path"] = path,
            });
        }

        private static void CollectIds(JToken token, List<string> entityIds, List<int> instanceIds)
        {
            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if (string.Equals(prop.Name, "entityId", StringComparison.OrdinalIgnoreCase))
                    {
                        var s = prop.Value?.ToString();
                        if (!string.IsNullOrWhiteSpace(s))
                            entityIds.Add(s);
                    }
                    else if (string.Equals(prop.Name, "instanceId", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryReadInt(prop.Value, out int iid) && iid != 0)
                            instanceIds.Add(iid);
                    }
                    else
                    {
                        CollectIds(prop.Value, entityIds, instanceIds);
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                    CollectIds(item, entityIds, instanceIds);
            }
        }

        /// <summary>
        /// 深度对比前后两份 EditorJsonUtility JSON，输出改变的叶子路径。
        /// 数组长度不同时整个数组路径记一条 note，不做逐元素对齐。上限 <see cref="MaxChangesPerObject"/>。
        /// </summary>
        private static List<JObject> CompareJson(string beforeJson, string afterJson, out bool truncated)
        {
            var changes = new List<JObject>();
            truncated = false;

            var before = ParseNoDate(beforeJson);
            var after = ParseNoDate(afterJson);
            if (before == null || after == null)
                return changes;

            CompareTokens("", before, after, changes, ref truncated);
            return changes;
        }

        // JObject.Parse 会把看似日期的字符串强转成本地化 DateTime,破坏对比；DateParseHandling.None 保持原样。
        private static JObject ParseNoDate(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                    return JObject.Load(reader);
            }
            catch { return null; }
        }

        private static void CompareTokens(string path, JToken before, JToken after, List<JObject> changes, ref bool truncated)
        {
            if (changes.Count >= MaxChangesPerObject)
            {
                truncated = true;
                return;
            }

            if (before is JObject bo && after is JObject ao)
            {
                var keys = new List<string>();
                foreach (var p in bo.Properties())
                    keys.Add(p.Name);
                foreach (var p in ao.Properties())
                    if (!keys.Contains(p.Name))
                        keys.Add(p.Name);

                foreach (var key in keys)
                {
                    if (changes.Count >= MaxChangesPerObject) { truncated = true; return; }
                    var childPath = string.IsNullOrEmpty(path) ? key : path + "." + key;
                    CompareTokens(childPath, bo[key], ao[key], changes, ref truncated);
                }
                return;
            }

            if (before is JArray ba && after is JArray aa)
            {
                if (ba.Count != aa.Count)
                {
                    changes.Add(new JObject
                    {
                        ["path"] = path,
                        ["note"] = $"array length {ba.Count}→{aa.Count}",
                    });
                    return;
                }
                for (int i = 0; i < ba.Count; i++)
                {
                    if (changes.Count >= MaxChangesPerObject) { truncated = true; return; }
                    CompareTokens($"{path}[{i}]", ba[i], aa[i], changes, ref truncated);
                }
                return;
            }

            if (!JToken.DeepEquals(before, after))
            {
                changes.Add(new JObject
                {
                    ["path"] = path,
                    ["before"] = before ?? (JToken)JValue.CreateNull(),
                    ["after"] = after ?? (JToken)JValue.CreateNull(),
                });
            }
        }

        private static bool TryGetString(JObject obj, string propertyName, out string value)
        {
            value = null;
            if (obj != null &&
                obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out var token) &&
                token != null && token.Type != JTokenType.Null)
            {
                value = token.ToString();
                return !string.IsNullOrWhiteSpace(value);
            }
            return false;
        }

        private static bool TryReadInt(JToken token, out int value)
        {
            value = 0;
            if (token == null)
                return false;
            try
            {
                if (token.Type == JTokenType.Integer)
                {
                    value = token.Value<int>();
                    return true;
                }
                if (token.Type == JTokenType.String && int.TryParse(token.Value<string>(), out var parsed))
                {
                    value = parsed;
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}

// Producer:Betsy
