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
    /// A pure side-channel observer for the optional semantic diff on write operations (POST /skill/{name}?diff=1).
    ///
    /// Takes an <see cref="EditorJsonUtility"/> snapshot before and after a skill runs, of "the target object(s)
    /// located from args", and reports the leaf fields actually changed by this operation (changed), newly
    /// created objects (added), and destroyed objects (removed) — sparing the AI a follow-up query to confirm.
    ///
    /// Hard constraint: this class is fully exception-isolated throughout — any failure in before-capture /
    /// comparison / added-scan **must not** affect skill execution; it only degrades sceneDiff to {error:...}.
    /// It's an observer, and takes no part in undo / workflow / error branching.
    /// </summary>
    internal static class SkillSceneDiff
    {
        // Cap on the number of before-captured objects: beyond this, only the first N are captured and captureLimited is set.
        private const int MaxCaptureObjects = 20;
        // Cap on changed leaves per object: beyond this, truncated is set.
        private const int MaxChangesPerObject = 50;
        private const int MaxBatchCaptureObjects = 100;

        /// <summary>
        /// A single before-captured object record. Once an object is destroyed, Unity's fake-null makes
        /// obj.name / GetType() throw, so name / typeName / entityId must be pinned down at before-capture time
        /// for the removed report to use. EntityId goes through the <see cref="UnityObjectIdUtility.GetEntityId"/>
        /// compatibility layer (entityId on 6000.4+, falling back to an instanceId string on older versions);
        /// it serves as both the in-process dedup key and the JSON output handle. LegacyInstanceId is only
        /// non-zero on older versions, kept for compatibility with existing instanceId output fields.
        /// </summary>
        internal sealed class ObjectSnapshot
        {
            public UnityEngine.Object Obj;
            public string EntityId;
            public string OwnerEntityId;
            public int LegacyInstanceId;
            public string Name;
            public string TypeName;
            public string BeforeJson;
        }

        internal sealed class DiffCapture
        {
            public readonly List<ObjectSnapshot> Snapshots = new List<ObjectSnapshot>();
            // The entityId set of before-captured objects, used at the added stage to exclude "targets that already existed".
            public readonly HashSet<string> CapturedEntityIds = new HashSet<string>();
            public bool Limited;
            public bool HadTargets;
            public string Error;
        }

        internal sealed class BatchDiffCapture
        {
            public readonly Dictionary<string, ObjectSnapshot> Snapshots = new Dictionary<string, ObjectSnapshot>();
            public readonly Dictionary<string, UnityEngine.Object> AddedObjects = new Dictionary<string, UnityEngine.Object>();
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
                    var id = UnityObjectIdUtility.GetEntityId(obj);
                    if (id == null) continue;
                    if (capture.AddedObjects.ContainsKey(id) || IsPartOfAddedObject(capture, obj) || capture.Snapshots.ContainsKey(id)) continue;
                    if (capture.Snapshots.Count >= MaxBatchCaptureObjects)
                    {
                        capture.Limited = true;
                        break;
                    }
                    capture.Snapshots[id] = new ObjectSnapshot
                    {
                        Obj = obj,
                        EntityId = id,
                        OwnerEntityId = obj is Component component ? UnityObjectIdUtility.GetEntityId(component.gameObject) : null,
                        LegacyInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(obj),
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
                var removedGameObjectIds = new HashSet<string>(capture.Snapshots.Values
                    .Where(snap => snap.Obj == null && snap.TypeName == nameof(GameObject))
                    .Select(snap => snap.EntityId));
                foreach (var snap in capture.Snapshots.Values)
                {
                    if (snap.Obj == null)
                    {
                        if (snap.TypeName != nameof(GameObject) && WasComponentOfRemovedGameObject(capture, snap, removedGameObjectIds))
                            continue;
                        removed.Add(BuildIdentity(snap.Name, snap.TypeName, snap.EntityId, snap.LegacyInstanceId));
                        continue;
                    }
                    string afterJson;
                    try { afterJson = EditorJsonUtility.ToJson(snap.Obj); }
                    catch { continue; }
                    var changes = CompareJson(snap.BeforeJson, afterJson, out var truncated);
                    if (changes.Count == 0) continue;
                    changed.Add(new JObject
                    {
                        ["target"] = BuildIdentity(snap.Obj.name, snap.TypeName, snap.EntityId, snap.LegacyInstanceId),
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
                    var identity = BuildIdentity(obj.name, obj.GetType().Name, pair.Key, UnityObjectIdUtility.GetLegacyInstanceId(obj));
                    identity["path"] = string.IsNullOrEmpty(path) ? null : path;
                    added.Add(identity);
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
            var id = UnityObjectIdUtility.GetEntityId(obj);
            if (id == null) return;
            if (!capture.Snapshots.ContainsKey(id) && !capture.AddedObjects.ContainsKey(id))
                capture.AddedObjects[id] = obj;
        }

        private static bool IsPartOfAddedObject(BatchDiffCapture capture, UnityEngine.Object obj)
        {
            if (obj is Component component && component.gameObject != null)
            {
                var ownerId = UnityObjectIdUtility.GetEntityId(component.gameObject);
                return ownerId != null && capture.AddedObjects.ContainsKey(ownerId);
            }
            return false;
        }

        private static bool WasComponentOfRemovedGameObject(
            BatchDiffCapture capture, ObjectSnapshot componentSnapshot, HashSet<string> removedGameObjectIds)
        {
            if (removedGameObjectIds.Count == 0 || componentSnapshot == null)
                return false;

            // Destroyed Unity components can no longer access gameObject, so ownership was already pinned down
            // in the before-capture snapshot, and remains usable even after Unity turns it into a fake null.
            return !string.IsNullOrEmpty(componentSnapshot.OwnerEntityId) &&
                removedGameObjectIds.Contains(componentSnapshot.OwnerEntityId);
        }

        /// <summary>
        /// Before-invoke capture: reuses SkillRouter's shared target location (<see cref="SkillRouter.CollectTargetsFromArgs"/>),
        /// additionally folding the component instance pointed to by componentType into the capture set (a
        /// component_set_property-style skill modifies exactly this component, which is diff's highest-value
        /// point). For each object, pins down (instanceId, name, typeName, EditorJsonUtility.ToJson).
        /// Capped at <see cref="MaxCaptureObjects"/>; beyond that, Limited is set. Any exception degrades to Error.
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
                    string id = UnityObjectIdUtility.GetEntityId(obj);
                    if (id == null)
                        continue;
                    if (capture.CapturedEntityIds.Contains(id))
                        continue; // Dedupe when the same object is matched by multiple location rules
                    if (capture.Snapshots.Count >= MaxCaptureObjects)
                    {
                        capture.Limited = true;
                        break;
                    }

                    capture.CapturedEntityIds.Add(id);
                    capture.Snapshots.Add(new ObjectSnapshot
                    {
                        Obj = obj,
                        EntityId = id,
                        OwnerEntityId = obj is Component component ? UnityObjectIdUtility.GetEntityId(component.gameObject) : null,
                        LegacyInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(obj),
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
        /// Post-invoke comparison on success: re-ToJson the same set of objects, count destroyed ones as
        /// removed, and diff the rest leaf-by-leaf for changed; then scan the successful result for newly
        /// created objects to get added. Any exception degrades to {error:...}.
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
                    // Unity fake-null: the object was destroyed during execution. Report using the display
                    // fields pinned down at before-capture.
                    if (snap.Obj == null)
                    {
                        removed.Add(BuildIdentity(snap.Name, snap.TypeName, snap.EntityId, snap.LegacyInstanceId));
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
                        ["target"] = BuildIdentity(snap.Obj.name, snap.TypeName, snap.EntityId, snap.LegacyInstanceId),
                        ["changes"] = new JArray(changes),
                        ["truncated"] = truncated,
                    });
                }

                var added = BuildAdded(result, capture.CapturedEntityIds);

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
        /// If args contains componentType and the target GameObject is already in the capture set, also folds
        /// in that type's component instance. Type resolution reuses <see cref="ComponentSkills.FindComponentType"/> (no new type search is written).
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
        /// Deep-collects entityId / instanceId from the successful result's JToken, resolves them back to
        /// objects, and excludes anything already in the before-capture set. entityId takes priority (on Unity
        /// 6000.4+, instanceId in a result is always 0, so entityId is the real identifier); resolution
        /// consistently goes through the <see cref="UnityObjectIdUtility"/> compatibility layer to avoid obsolete APIs.
        /// </summary>
        private static JArray BuildAdded(object result, HashSet<string> capturedEntityIds)
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

                var seen = new HashSet<string>();
                foreach (var eid in entityIds)
                    TryAddNewObject(UnityObjectIdUtility.EntityIdToObject(eid), capturedEntityIds, seen, added);
                foreach (var iid in instanceIds)
                    TryAddNewObject(UnityObjectIdUtility.ObjectIdToObject(iid), capturedEntityIds, seen, added);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[diff] added-scan failed: {ex.Message}");
            }
            return added;
        }

        private static void TryAddNewObject(UnityEngine.Object obj, HashSet<string> capturedEntityIds, HashSet<string> seen, JArray added)
        {
            if (obj == null)
                return;
            string id = UnityObjectIdUtility.GetEntityId(obj);
            if (id == null)
                return;
            if (capturedEntityIds.Contains(id))
                return; // Already in the before-capture set → not newly created this time
            if (!seen.Add(id))
                return; // The same new object is pointed to by multiple id fields; report it only once

            string path = null;
            if (obj is GameObject go)
                path = GameObjectFinder.GetPath(go);
            else
            {
                var assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath))
                    path = assetPath;
            }

            var identity = BuildIdentity(obj.name, obj.GetType().Name, id, UnityObjectIdUtility.GetLegacyInstanceId(obj));
            identity["path"] = path;
            added.Add(identity);
        }

        /// <summary>
        /// Unified object identity fields. Always outputs entityId (the only reliable handle on 6000.4+; an
        /// instanceId string on older versions); older versions additionally attach a non-zero instanceId for
        /// compatibility with existing consumers; on 6000.4+, instanceId is always 0 so it's omitted (see the
        /// "Object location (Unity 6000.4+)" contract in SKILL.md).
        /// </summary>
        private static JObject BuildIdentity(string name, string type, string entityId, int legacyInstanceId)
        {
            var identity = new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["entityId"] = entityId,
            };
            if (legacyInstanceId != 0)
                identity["instanceId"] = legacyInstanceId;
            return identity;
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
        /// Deep-compares two EditorJsonUtility JSON blobs (before and after), outputting the leaf paths that
        /// changed. When array lengths differ, records a single note for the whole array path instead of
        /// aligning element-by-element. Capped at <see cref="MaxChangesPerObject"/>.
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

        // JObject.Parse would coerce a date-looking string into a localized DateTime, corrupting the
        // comparison; DateParseHandling.None keeps it as-is.
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
