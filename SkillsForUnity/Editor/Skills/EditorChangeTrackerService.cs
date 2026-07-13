using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnitySkills
{
    /// <summary>
    /// Persistent, bounded editor-change journal used by editor_get_changes. It observes scene
    /// structure, serialized properties and imported files continuously, without a recording
    /// session. Entries live under Library so they survive domain reloads but never enter source
    /// control or the user's Assets folder.
    /// </summary>
    [InitializeOnLoad]
    public static class EditorChangeTrackerService
    {
        internal const int BufferCapacity = 500;
        internal const int DefaultReadLimit = 100;
        internal const int MaxReadLimit = 500;

        private const double PropertyDebounceSeconds = 0.25;
        private const int MaxChangesPerEvent = 200;
        private const int MaxValueChars = 300;
        private const int CompactEveryOverflowEntries = 100;
        private const string RelativeLogPath = "Library/UnitySkills/editor_changes.jsonl";

        private sealed class CatalogEntry
        {
            public int InstanceId;
            public string Name;
            public string Path;
            public string ScenePath;
            public List<string> ComponentTypes;
        }

        private sealed class PendingPropertyChange
        {
            public string Source;
            public JObject Change;
        }

        private static readonly Queue<JObject> Buffer = new Queue<JObject>(BufferCapacity + 1);
        private static readonly Dictionary<int, CatalogEntry> Catalog = new Dictionary<int, CatalogEntry>();
        private static readonly Dictionary<string, PendingPropertyChange> PendingProperties =
            new Dictionary<string, PendingPropertyChange>(StringComparer.Ordinal);

        private static bool _loaded;
        private static bool _propertyFlushScheduled;
        private static double _propertyFlushDue;
        private static long _seq;
        private static int _restDepth;
        private static int _overflowEntriesSinceCompact;

        internal static string OverrideLogPathForTests;

        static EditorChangeTrackerService()
        {
            try
            {
                EnsureLoaded();
                RebuildCatalog();
                ObjectChangeEvents.changesPublished += OnChangesPublished;
                Undo.postprocessModifications += OnPostprocessModifications;
                Undo.undoRedoPerformed += OnUndoRedoPerformed;
                EditorSceneManager.sceneOpened += OnSceneOpened;
                EditorSceneManager.sceneSaved += OnSceneSaved;
                AssemblyReloadEvents.beforeAssemblyReload += FlushPendingProperties;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError("EditorChangeTrackerService init failed: " + ex);
            }
        }

        /// <summary>Marks changes caused by a REST skill so callers can filter them from manual edits.</summary>
        public static void BeginRestExecution() => _restDepth++;

        public static void EndRestExecution()
        {
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                EditorApplication.update -= tick;
                if (_restDepth > 0)
                    _restDepth--;
            };
            EditorApplication.update += tick;
        }

        public static object ReadChanges(long since = 0, string types = null, string source = "all", int limit = DefaultReadLimit)
        {
            EnsureLoaded();
            FlushPendingProperties();

            if (since < 0)
                return new { error = "'since' must be a non-negative cursor." };
            if (limit < 1 || limit > MaxReadLimit)
                return new { error = $"'limit' must be between 1 and {MaxReadLimit}." };

            if (!TryParseTypes(types, out var typeFilter, out var typeError))
                return new { error = typeError };
            if (!TryNormalizeSource(source, out var normalizedSource, out var sourceError))
                return new { error = sourceError };

            var matching = Buffer
                .Where(e => e.Value<long>("seq") > since)
                .Where(e => typeFilter == null || typeFilter.Contains(e.Value<string>("type")))
                .Where(e => normalizedSource == "all" || string.Equals(e.Value<string>("source"), normalizedSource, StringComparison.Ordinal))
                .ToList();

            bool truncated = matching.Count > limit;
            if (truncated)
                matching = matching.Skip(matching.Count - limit).ToList();

            long oldestSeq = Buffer.Count > 0 ? Buffer.Peek().Value<long>("seq") : _seq + 1;
            bool dropped = since + 1 < oldestSeq;

            return new
            {
                success = true,
                hasChanges = matching.Count > 0,
                since,
                cursor = _seq,
                oldestSeq,
                dropped,
                truncated,
                returned = matching.Count,
                retention = BufferCapacity,
                changes = new JArray(matching.Select(e => e.DeepClone()))
            };
        }

        internal static void RecordFileChanges(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var importedPaths = CleanPaths(imported);
            var deletedPaths = CleanPaths(deleted);
            var movedPaths = CleanPaths(moved);
            var movedFromPaths = CleanPaths(movedFrom);
            if (importedPaths.Length == 0 && deletedPaths.Length == 0 && movedPaths.Length == 0 && movedFromPaths.Length == 0)
                return;

            var movePairs = new JArray();
            int pairCount = Math.Min(movedPaths.Length, movedFromPaths.Length);
            for (int i = 0; i < pairCount; i++)
                movePairs.Add(new JObject { ["from"] = movedFromPaths[i], ["to"] = movedPaths[i] });

            Publish("file_changes", CurrentSource(), new JObject
            {
                ["imported"] = JArray.FromObject(importedPaths),
                ["deleted"] = JArray.FromObject(deletedPaths),
                ["moved"] = movePairs,
                ["count"] = importedPaths.Length + deletedPaths.Length + pairCount
            });
        }

        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            try
            {
                var changes = new JArray();
                int omitted = 0;

                for (int i = 0; i < stream.length; i++)
                {
                    JObject change = null;
                    switch (stream.GetEventType(i))
                    {
                        case ObjectChangeKind.CreateGameObjectHierarchy:
                            stream.GetCreateGameObjectHierarchyEvent(i, out var createArgs);
                            change = CaptureCreated(createArgs.instanceId);
                            break;

                        case ObjectChangeKind.DestroyGameObjectHierarchy:
                            stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyArgs);
                            change = CaptureDestroyed(destroyArgs.instanceId);
                            break;

                        case ObjectChangeKind.ChangeGameObjectParent:
                            stream.GetChangeGameObjectParentEvent(i, out var parentArgs);
                            change = CaptureReparented(parentArgs.instanceId, parentArgs.newParentInstanceId);
                            break;

                        case ObjectChangeKind.ChangeGameObjectStructure:
                            stream.GetChangeGameObjectStructureEvent(i, out var structureArgs);
                            change = CaptureStructure(structureArgs.instanceId);
                            break;

                        case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                            stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierarchyArgs);
                            change = CaptureStructure(hierarchyArgs.instanceId);
                            break;

                        case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                            stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var propertyArgs);
                            RefreshCatalogForObject(propertyArgs.instanceId);
                            break;

                        case ObjectChangeKind.ChangeChildrenOrder:
                            stream.GetChangeChildrenOrderEvent(i, out var orderArgs);
                            change = CaptureReordered(orderArgs.instanceId);
                            break;

#if UNITY_6000_0_OR_NEWER
                        case ObjectChangeKind.ChangeRootOrder:
                            stream.GetChangeRootOrderEvent(i, out var rootOrderArgs);
                            change = new JObject
                            {
                                ["kind"] = "root_order_changed",
                                ["scene"] = rootOrderArgs.scene.name,
                                ["scenePath"] = rootOrderArgs.scene.path
                            };
                            break;
#endif
                    }

                    if (change == null)
                        continue;
                    if (changes.Count < MaxChangesPerEvent)
                        changes.Add(change);
                    else
                        omitted++;
                }

                if (changes.Count > 0)
                {
                    Publish("scene_changes", CurrentSource(), new JObject
                    {
                        ["count"] = changes.Count + omitted,
                        ["omitted"] = omitted,
                        ["changes"] = changes
                    });
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Editor change tracker failed to process scene changes: " + ex.Message);
            }
        }

        private static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            try
            {
                string source = CurrentSource();
                foreach (var modification in modifications ?? Array.Empty<UndoPropertyModification>())
                    CapturePropertyModification(modification, source);

                if (PendingProperties.Count > 0)
                    SchedulePropertyFlush();
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Editor change tracker failed to process property changes: " + ex.Message);
            }
            return modifications;
        }

        private static void CapturePropertyModification(UndoPropertyModification modification, string source)
        {
            var current = modification.currentValue;
            if (current?.target == null)
                return;

            string propertyPath = current.propertyPath ?? string.Empty;
            if (propertyPath == "m_RootOrder" || propertyPath.StartsWith("m_LocalEulerAnglesHint", StringComparison.Ordinal))
                return;

            var target = current.target;
            var go = target as GameObject ?? (target as Component)?.gameObject;
            string assetPath = EditorUtility.IsPersistent(target) ? AssetDatabase.GetAssetPath(target) : null;
            if (go == null && string.IsNullOrEmpty(assetPath))
                return;

            string targetKey = go != null
                ? UnityObjectIdUtility.GetEntityId(go) + ":" + target.GetType().FullName
                : assetPath + ":" + target.GetType().FullName;
            string key = source + ":" + targetKey + ":" + propertyPath;

            var change = new JObject
            {
                ["kind"] = "property_changed",
                ["targetType"] = target.GetType().Name,
                ["property"] = propertyPath,
                ["previousValue"] = Truncate(modification.previousValue?.value, MaxValueChars),
                ["value"] = Truncate(current.value, MaxValueChars)
            };

            if (go != null)
            {
                AddGameObjectIdentity(change, go);
            }
            else
            {
                change["assetPath"] = assetPath;
            }

            if (current.objectReference != null)
            {
                var reference = current.objectReference;
                var referenceGo = reference as GameObject ?? (reference as Component)?.gameObject;
                change["objectReference"] = new JObject
                {
                    ["name"] = reference.name,
                    ["type"] = reference.GetType().Name,
                    ["path"] = EditorUtility.IsPersistent(reference)
                        ? AssetDatabase.GetAssetPath(reference)
                        : referenceGo != null ? GameObjectFinder.GetPath(referenceGo) : null
                };
            }

            if (PendingProperties.TryGetValue(key, out var existing))
            {
                existing.Change["value"] = change["value"];
                existing.Change["objectReference"] = change["objectReference"];
            }
            else
            {
                PendingProperties[key] = new PendingPropertyChange { Source = source, Change = change };
            }

            _propertyFlushDue = EditorApplication.timeSinceStartup + PropertyDebounceSeconds;
        }

        private static void SchedulePropertyFlush()
        {
            if (_propertyFlushScheduled)
                return;
            _propertyFlushScheduled = true;
            EditorApplication.update += FlushPropertiesWhenDue;
        }

        private static void FlushPropertiesWhenDue()
        {
            if (EditorApplication.timeSinceStartup < _propertyFlushDue)
                return;
            EditorApplication.update -= FlushPropertiesWhenDue;
            _propertyFlushScheduled = false;
            FlushPendingProperties();
        }

        private static void FlushPendingProperties()
        {
            if (_propertyFlushScheduled)
            {
                EditorApplication.update -= FlushPropertiesWhenDue;
                _propertyFlushScheduled = false;
            }
            if (PendingProperties.Count == 0)
                return;

            foreach (var group in PendingProperties.Values.GroupBy(p => p.Source))
            {
                var changes = new JArray(group.Take(MaxChangesPerEvent).Select(p => p.Change));
                int total = group.Count();
                Publish("scene_changes", group.Key, new JObject
                {
                    ["count"] = total,
                    ["omitted"] = Math.Max(0, total - changes.Count),
                    ["changes"] = changes
                });
            }
            PendingProperties.Clear();
        }

        private static void OnUndoRedoPerformed()
        {
            Publish("undo_redo", CurrentSource(), new JObject
            {
                ["note"] = "Unity does not expose whether this callback was undo or redo; inspect the related scene_changes entries."
            });
            RebuildCatalog();
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            Publish("scene_opened", CurrentSource(), new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["mode"] = mode.ToString()
            });
            RebuildCatalog();
        }

        private static void OnSceneSaved(Scene scene)
        {
            FlushPendingProperties();
            Publish("scene_saved", CurrentSource(), new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path
            });
        }

        private static JObject CaptureCreated(int instanceId)
        {
            var go = ResolveGameObject(instanceId);
            if (go == null || IsIgnored(go))
                return null;
            UpsertCatalog(go);
            CatalogHierarchy(go);

            var change = new JObject { ["kind"] = "gameobject_created" };
            AddGameObjectIdentity(change, go);
            change["components"] = JArray.FromObject(SnapshotComponentTypes(go));
            return change;
        }

        private static JObject CaptureDestroyed(int instanceId)
        {
            if (!Catalog.TryGetValue(instanceId, out var entry))
                return new JObject { ["kind"] = "gameobject_destroyed", ["instanceId"] = instanceId };

            var change = new JObject
            {
                ["kind"] = "gameobject_destroyed",
                ["name"] = entry.Name,
                ["path"] = entry.Path,
                ["scenePath"] = entry.ScenePath,
                ["components"] = JArray.FromObject(entry.ComponentTypes ?? new List<string>())
            };

            string childPrefix = string.IsNullOrEmpty(entry.Path) ? null : entry.Path + "/";
            var removed = Catalog.Where(kv => kv.Key == instanceId ||
                    (childPrefix != null && !string.IsNullOrEmpty(kv.Value.Path) &&
                     kv.Value.Path.StartsWith(childPrefix, StringComparison.Ordinal)))
                .Select(kv => kv.Key).ToArray();
            foreach (int id in removed)
                Catalog.Remove(id);
            return change;
        }

        private static JObject CaptureReparented(int instanceId, int newParentInstanceId)
        {
            Catalog.TryGetValue(instanceId, out var before);
            var go = ResolveGameObject(instanceId);
            if (go == null || IsIgnored(go))
                return null;

            var change = new JObject
            {
                ["kind"] = "gameobject_reparented",
                ["name"] = go.name,
                ["fromPath"] = before?.Path,
                ["toPath"] = GameObjectFinder.GetPath(go),
                ["newParentPath"] = go.transform.parent != null ? GameObjectFinder.GetPath(go.transform.parent.gameObject) : null,
                ["newParentInstanceId"] = newParentInstanceId
            };
            CatalogHierarchy(go);
            return change;
        }

        private static JObject CaptureStructure(int instanceId)
        {
            var go = ResolveGameObject(instanceId);
            if (go == null || IsIgnored(go))
                return null;

            Catalog.TryGetValue(instanceId, out var before);
            var after = SnapshotComponentTypes(go);
            DiffComponentTypes(before?.ComponentTypes, after, out var added, out var removed);
            UpsertCatalog(go);

            var change = new JObject
            {
                ["kind"] = "components_changed",
                ["added"] = JArray.FromObject(added),
                ["removed"] = JArray.FromObject(removed)
            };
            AddGameObjectIdentity(change, go);
            return change;
        }

        private static JObject CaptureReordered(int instanceId)
        {
            var go = ResolveGameObject(instanceId);
            if (go == null || IsIgnored(go))
                return null;
            var change = new JObject { ["kind"] = "children_reordered" };
            AddGameObjectIdentity(change, go);
            return change;
        }

        private static void RefreshCatalogForObject(int instanceId)
        {
            var obj = UnityObjectIdUtility.ObjectIdToObject(instanceId);
            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go != null && !IsIgnored(go))
                CatalogHierarchy(go);
        }

        private static void RebuildCatalog()
        {
            Catalog.Clear();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                    CatalogHierarchy(root);
            }
        }

        private static void CatalogHierarchy(GameObject go)
        {
            if (go == null || IsIgnored(go))
                return;
            UpsertCatalog(go);
            foreach (Transform child in go.transform)
                CatalogHierarchy(child.gameObject);
        }

        private static void UpsertCatalog(GameObject go)
        {
            int id = go.GetInstanceID();
            Catalog[id] = new CatalogEntry
            {
                InstanceId = id,
                Name = go.name,
                Path = GameObjectFinder.GetPath(go),
                ScenePath = go.scene.path,
                ComponentTypes = SnapshotComponentTypes(go)
            };
        }

        private static List<string> SnapshotComponentTypes(GameObject go)
        {
            return go.GetComponents<Component>()
                .Where(component => component != null)
                .Select(component => component.GetType().Name)
                .ToList();
        }

        private static void DiffComponentTypes(List<string> before, List<string> after,
            out string[] added, out string[] removed)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string type in before ?? new List<string>())
            {
                counts.TryGetValue(type, out int count);
                counts[type] = count - 1;
            }
            foreach (string type in after ?? new List<string>())
            {
                counts.TryGetValue(type, out int count);
                counts[type] = count + 1;
            }

            var addedList = new List<string>();
            var removedList = new List<string>();
            foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                for (int i = 0; i < pair.Value; i++)
                    addedList.Add(pair.Key);
                for (int i = 0; i > pair.Value; i--)
                    removedList.Add(pair.Key);
            }
            added = addedList.ToArray();
            removed = removedList.ToArray();
        }

        private static GameObject ResolveGameObject(int instanceId)
        {
            var obj = UnityObjectIdUtility.ObjectIdToObject(instanceId);
            return obj as GameObject ?? (obj as Component)?.gameObject;
        }

        private static bool IsIgnored(GameObject go)
        {
            return go == null || !go.scene.IsValid() ||
                (go.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave)) != 0;
        }

        private static void AddGameObjectIdentity(JObject target, GameObject go)
        {
            target["name"] = go.name;
            target["entityId"] = UnityObjectIdUtility.GetEntityId(go);
            target["instanceId"] = UnityObjectIdUtility.GetObjectId(go);
            target["path"] = GameObjectFinder.GetPath(go);
            target["scenePath"] = go.scene.path;
        }

        private static void Publish(string type, string source, JObject payload)
        {
            EnsureLoaded();
            var entry = new JObject
            {
                ["seq"] = ++_seq,
                ["type"] = type,
                ["source"] = source,
                ["tsUtc"] = DateTime.UtcNow.ToString("o"),
                ["payload"] = payload ?? new JObject()
            };
            Buffer.Enqueue(entry);
            bool overflowed = Buffer.Count > BufferCapacity;
            while (Buffer.Count > BufferCapacity)
                Buffer.Dequeue();
            bool compact = overflowed && ++_overflowEntriesSinceCompact >= CompactEveryOverflowEntries;
            Persist(entry, compact);
            if (compact)
                _overflowEntriesSinceCompact = 0;
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;
            Buffer.Clear();
            _seq = 0;
            _overflowEntriesSinceCompact = 0;

            string path = LogPath;
            if (!File.Exists(path))
                return;

            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    JObject entry;
                    try { entry = JObject.Parse(line); }
                    catch { continue; }
                    long seq = entry.Value<long?>("seq") ?? 0;
                    if (seq <= 0)
                        continue;
                    _seq = Math.Max(_seq, seq);
                    Buffer.Enqueue(entry);
                    while (Buffer.Count > BufferCapacity)
                        Buffer.Dequeue();
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Failed to load editor change journal: " + ex.Message);
            }
        }

        private static void Persist(JObject entry, bool compact)
        {
            try
            {
                string path = LogPath;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                if (compact)
                {
                    File.WriteAllLines(path, Buffer.Select(e => e.ToString(Formatting.None)));
                }
                else
                {
                    File.AppendAllText(path, entry.ToString(Formatting.None) + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Failed to persist editor change journal: " + ex.Message);
            }
        }

        private static string LogPath => !string.IsNullOrEmpty(OverrideLogPathForTests)
            ? OverrideLogPathForTests
            : Path.GetFullPath(Path.Combine(Application.dataPath, "..", RelativeLogPath));

        private static string CurrentSource() => _restDepth > 0 ? "rest" : "editor";

        private static string[] CleanPaths(string[] paths) => (paths ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxChangesPerEvent)
            .ToArray();

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
                return value;
            return value.Substring(0, maxChars);
        }

        private static bool TryParseTypes(string raw, out HashSet<string> types, out string error)
        {
            types = null;
            error = null;
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "all", StringComparison.OrdinalIgnoreCase))
                return true;

            var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["scene"] = new[] { "scene_changes", "scene_opened", "scene_saved" },
                ["file"] = new[] { "file_changes" },
                ["undo"] = new[] { "undo_redo" },
                ["lifecycle"] = new[] { "scene_opened", "scene_saved" },
                ["scene_changes"] = new[] { "scene_changes" },
                ["file_changes"] = new[] { "file_changes" },
                ["undo_redo"] = new[] { "undo_redo" },
                ["scene_opened"] = new[] { "scene_opened" },
                ["scene_saved"] = new[] { "scene_saved" }
            };

            types = new HashSet<string>(StringComparer.Ordinal);
            foreach (string token in raw.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                if (!aliases.TryGetValue(token, out var expanded))
                {
                    error = $"Unknown change type '{token}'. Valid values: all, scene, file, undo, lifecycle.";
                    types = null;
                    return false;
                }
                foreach (string value in expanded)
                    types.Add(value);
            }
            return true;
        }

        private static bool TryNormalizeSource(string raw, out string source, out string error)
        {
            source = string.IsNullOrWhiteSpace(raw) ? "all" : raw.Trim().ToLowerInvariant();
            error = null;
            if (source == "manual")
                source = "editor";
            if (source == "all" || source == "editor" || source == "rest")
                return true;
            error = $"Unknown source '{raw}'. Valid values: all, editor (or manual), rest.";
            return false;
        }

        internal static void ResetForTests(bool deleteFile)
        {
            if (_propertyFlushScheduled)
            {
                EditorApplication.update -= FlushPropertiesWhenDue;
                _propertyFlushScheduled = false;
            }
            PendingProperties.Clear();
            Buffer.Clear();
            _seq = 0;
            _loaded = true;
            _restDepth = 0;
            _overflowEntriesSinceCompact = 0;
            if (deleteFile && File.Exists(LogPath))
                File.Delete(LogPath);
        }

        internal static void ReloadForTests()
        {
            Buffer.Clear();
            _seq = 0;
            _loaded = false;
            _overflowEntriesSinceCompact = 0;
            EnsureLoaded();
        }

        internal static void PublishForTests(string type, string source, JObject payload = null)
        {
            Publish(type, source, payload ?? new JObject());
        }
    }

    internal sealed class EditorChangeAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            EditorChangeTrackerService.RecordFileChanges(
                importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
        }
    }
}

// Producer:Betsy
