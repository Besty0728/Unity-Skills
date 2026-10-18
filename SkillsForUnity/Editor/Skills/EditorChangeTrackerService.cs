using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// The persisted, bounded editor-change log behind editor_get_changes. It continuously observes scene structure,
    /// serialized properties, and imported files, with no need to start a recording session. Entries are stored under Library:
    /// that lets them survive domain reloads while staying out of version control and the user's Assets folder.
    ///
    /// Disk history is a chain of segments: the active <c>editor_changes.jsonl</c> plus rotated <c>editor_changes.1.jsonl</c>
    /// (newest) to <c>editor_changes.10.jsonl</c>. A segment is rotated once it holds <see cref="SegmentMaxEntries"/> entries or
    /// <see cref="SegmentMaxBytes"/> bytes; files are only ever appended to or renamed, never rewritten. The in-memory ring
    /// (<see cref="BufferCapacity"/>) is what <see cref="ReadChanges"/> serves.
    /// </summary>
    [InitializeOnLoad]
    public static class EditorChangeTrackerService
    {
        internal const int BufferCapacity = 500;
        internal const int DefaultReadLimit = 100;
        internal const int MaxReadLimit = 500;

        internal const int SegmentMaxEntries = 2000;
        internal const long SegmentMaxBytes = 8L * 1024 * 1024;
        internal const int MaxRotatedSegments = 10;

        internal const string AgentMixed = "mixed";
        internal const string AgentUnknown = "unknown";

        private const double PropertyDebounceSeconds = 0.25;

        /// <summary>Changes per scene_changes entry, and paths per list of a file_changes entry.</summary>
        internal const int MaxChangesPerEvent = 200;

        /// <summary>
        /// Safety bound of one flush (an ObjectChangeEvents batch, a debounced property flush, an asset postprocess callback).
        /// A flush goes out as consecutive entries of at most <see cref="MaxChangesPerEvent"/>, so nothing is dropped until it
        /// would take more than this many entries: only then does its last entry count the rest in <c>omitted</c>. The bound
        /// keeps one huge operation from pushing everything else out of the in-memory ring and the rotated files.
        /// </summary>
        internal const int MaxEventsPerFlush = 50;

        /// <summary>Changes one flush lists (per list for file_changes) before the rest is only counted.</summary>
        internal const int MaxChangesPerFlush = MaxChangesPerEvent * MaxEventsPerFlush;

        private const int MaxValueChars = 300;
        private const string RelativeLogPath = "Library/UnitySkills/editor_changes.jsonl";

        /// <summary>Same shape as <see cref="ClientProcessResolver.TryGetAgentId"/>, so the attribution rule can be tested with a fake.</summary>
        internal delegate bool AgentLookup(int remotePort, out string agentId);

        private sealed class CatalogEntry
        {
            public string EntityId;
            public int InstanceId;
            public string Name;
            public string Path;
            public string ScenePath;
            public List<string> ComponentTypes;
        }

        private sealed class PendingPropertyChange
        {
            public string Source;
            public string Agent;
            public bool IsPlaying;
            public JObject Change;
        }

        private static readonly Queue<JObject> Buffer = new Queue<JObject>(BufferCapacity + 1);
        private static readonly Dictionary<string, CatalogEntry> Catalog = new Dictionary<string, CatalogEntry>();
        private static readonly Dictionary<string, PendingPropertyChange> PendingProperties =
            new Dictionary<string, PendingPropertyChange>(StringComparer.Ordinal);

        private static bool _loaded;
        private static bool _propertyFlushScheduled;
        private static double _propertyFlushDue;
        private static long _seq;
        private static int _restDepth;
        private static string _restAgent;
        private static int _activeEntries;
        private static long _activeBytes;
        private static bool _rotationFailureLogged;

        internal static string OverrideLogPathForTests;
        internal static int OverrideSegmentMaxEntriesForTests;
        internal static long OverrideSegmentMaxBytesForTests;
        internal static bool? OverrideIsPlayingForTests;

        static EditorChangeTrackerService()
        {
            if (ImportWorkerGate.IsWorkerProcess) return;

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
                EditorApplication.quitting += FlushPendingProperties;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError("EditorChangeTrackerService init failed: " + ex);
            }
        }

        /// <summary>
        /// Marks changes triggered by a REST skill, so callers can distinguish them from manual edits. Also decides which
        /// caller the journal attributes them to: the first request of a depth window sets it, a different caller joining
        /// the same window turns it into <see cref="AgentMixed"/>.
        /// </summary>
        public static void BeginRestExecution()
        {
            string agent = ResolveRestAgent(SkillsAuditLog.CaptureRequestContext(), ClientProcessResolver.TryGetAgentId);
            if (_restDepth == 0)
                _restAgent = agent;
            else if (!string.Equals(_restAgent, agent, StringComparison.Ordinal))
                _restAgent = AgentMixed;
            _restDepth++;
        }

        public static void EndRestExecution()
        {
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                EditorApplication.update -= tick;
                EndRestWindowStep();
            };
            EditorApplication.update += tick;
        }

        private static void EndRestWindowStep()
        {
            if (_restDepth > 0)
                _restDepth--;
            if (_restDepth == 0)
                _restAgent = null;
        }

        /// <summary>
        /// The REST caller's agent id. An explicit X-Agent-Id wins; otherwise the process resolver's answer for the remote
        /// port, then the header/User-Agent guess. No request context (panel, tests) or no usable id gives
        /// <see cref="AgentUnknown"/>.
        /// </summary>
        internal static string ResolveRestAgent(SkillsAuditLog.AmbientContext context, AgentLookup lookup)
        {
            if (!context.Active)
                return AgentUnknown;

            string agent = context.FallbackAgentId;
            if (!context.AgentIdIsExplicit && context.RemotePort > 0 &&
                lookup(context.RemotePort, out var resolved) && !string.IsNullOrEmpty(resolved))
            {
                agent = resolved;
            }
            return string.IsNullOrEmpty(agent) ? AgentUnknown : agent;
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

        /// <summary>
        /// One asset postprocess callback as consecutive file_changes entries listing at most <see cref="MaxChangesPerEvent"/>
        /// paths per list. Each entry's <c>importedTotal</c> / <c>deletedTotal</c> / <c>movedTotal</c> count the paths it
        /// accounts for: the listed ones, plus on the last entry those past <see cref="MaxChangesPerFlush"/>, which only
        /// <c>omitted</c> counts. A split callback numbers its entries with <c>part</c> / <c>parts</c>.
        /// </summary>
        internal static void RecordFileChanges(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var importedPaths = CleanPaths(imported, out int importedTotal);
            var deletedPaths = CleanPaths(deleted, out int deletedTotal);
            var movedPaths = CleanPaths(moved, out int movedToTotal);
            var movedFromPaths = CleanPaths(movedFrom, out int movedFromTotal);
            if (importedPaths.Length == 0 && deletedPaths.Length == 0 && movedPaths.Length == 0 && movedFromPaths.Length == 0)
                return;

            var movePairs = new List<JObject>();
            int pairCount = Math.Min(movedPaths.Length, movedFromPaths.Length);
            for (int i = 0; i < pairCount; i++)
                movePairs.Add(new JObject { ["from"] = movedFromPaths[i], ["to"] = movedPaths[i] });

            int movedTotal = Math.Min(movedToTotal, movedFromTotal);
            int longest = Math.Max(importedPaths.Length, Math.Max(deletedPaths.Length, pairCount));
            int parts = Math.Max(1, (longest + MaxChangesPerEvent - 1) / MaxChangesPerEvent);
            string source = CurrentSource();
            for (int part = 0; part < parts; part++)
            {
                bool last = part == parts - 1;
                var importedChunk = importedPaths.Skip(part * MaxChangesPerEvent).Take(MaxChangesPerEvent).ToArray();
                var deletedChunk = deletedPaths.Skip(part * MaxChangesPerEvent).Take(MaxChangesPerEvent).ToArray();
                var movedChunk = movePairs.Skip(part * MaxChangesPerEvent).Take(MaxChangesPerEvent).ToArray();
                int importedCounted = importedChunk.Length + (last ? importedTotal - importedPaths.Length : 0);
                int deletedCounted = deletedChunk.Length + (last ? deletedTotal - deletedPaths.Length : 0);
                int movedCounted = movedChunk.Length + (last ? movedTotal - pairCount : 0);
                int listed = importedChunk.Length + deletedChunk.Length + movedChunk.Length;
                var payload = new JObject
                {
                    ["imported"] = JArray.FromObject(importedChunk),
                    ["deleted"] = JArray.FromObject(deletedChunk),
                    ["moved"] = new JArray(movedChunk),
                    ["count"] = listed,
                    ["omitted"] = importedCounted + deletedCounted + movedCounted - listed,
                    ["importedTotal"] = importedCounted,
                    ["deletedTotal"] = deletedCounted,
                    ["movedTotal"] = movedCounted
                };
                if (parts > 1)
                {
                    payload["part"] = part + 1;
                    payload["parts"] = parts;
                }
                Publish("file_changes", source, payload);
            }
        }

        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            try
            {
                var changes = new List<JObject>();
                int omitted = 0;

                for (int i = 0; i < stream.length; i++)
                {
                    JObject change = null;
                    switch (stream.GetEventType(i))
                    {
                        case ObjectChangeKind.CreateGameObjectHierarchy:
                            stream.GetCreateGameObjectHierarchyEvent(i, out var createArgs);
#if UNITY_6000_4_OR_NEWER
                            change = CaptureCreated(UnityObjectIdUtility.EntityKey(createArgs.entityId));
#else
                            change = CaptureCreated(UnityObjectIdUtility.EntityKey(createArgs.instanceId));
#endif
                            break;

                        case ObjectChangeKind.DestroyGameObjectHierarchy:
                            stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyArgs);
#if UNITY_6000_4_OR_NEWER
                            change = CaptureDestroyed(UnityObjectIdUtility.EntityKey(destroyArgs.entityId));
#else
                            change = CaptureDestroyed(UnityObjectIdUtility.EntityKey(destroyArgs.instanceId));
#endif
                            break;

                        case ObjectChangeKind.ChangeGameObjectParent:
                            stream.GetChangeGameObjectParentEvent(i, out var parentArgs);
#if UNITY_6000_4_OR_NEWER
                            change = CaptureReparented(UnityObjectIdUtility.EntityKey(parentArgs.entityId), UnityObjectIdUtility.EntityKey(parentArgs.newParentEntityId));
#else
                            change = CaptureReparented(UnityObjectIdUtility.EntityKey(parentArgs.instanceId), UnityObjectIdUtility.EntityKey(parentArgs.newParentInstanceId));
#endif
                            break;

                        case ObjectChangeKind.ChangeGameObjectStructure:
                            stream.GetChangeGameObjectStructureEvent(i, out var structureArgs);
#if UNITY_6000_4_OR_NEWER
                            change = CaptureStructure(UnityObjectIdUtility.EntityKey(structureArgs.entityId));
#else
                            change = CaptureStructure(UnityObjectIdUtility.EntityKey(structureArgs.instanceId));
#endif
                            break;

                        case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                            stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierarchyArgs);
#if UNITY_6000_4_OR_NEWER
                            change = CaptureStructure(UnityObjectIdUtility.EntityKey(hierarchyArgs.entityId));
#else
                            change = CaptureStructure(UnityObjectIdUtility.EntityKey(hierarchyArgs.instanceId));
#endif
                            break;

                        case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                            stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var propertyArgs);
#if UNITY_6000_4_OR_NEWER
                            RefreshCatalogForObject(UnityObjectIdUtility.EntityKey(propertyArgs.entityId));
#else
                            RefreshCatalogForObject(UnityObjectIdUtility.EntityKey(propertyArgs.instanceId));
#endif
                            break;

                        case ObjectChangeKind.ChangeChildrenOrder:
                            stream.GetChangeChildrenOrderEvent(i, out var orderArgs);
#if UNITY_6000_4_OR_NEWER
                            change = CaptureReordered(UnityObjectIdUtility.EntityKey(orderArgs.entityId));
#else
                            change = CaptureReordered(UnityObjectIdUtility.EntityKey(orderArgs.instanceId));
#endif
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
                    if (changes.Count < MaxChangesPerFlush)
                        changes.Add(change);
                    else
                        omitted++;
                }

                if (changes.Count > 0)
                    PublishSceneChanges(changes, omitted, CurrentSource());
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Editor change tracker failed to process scene changes: " + ex.Message);
            }
        }

        internal static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            try
            {
                string source = CurrentSource();
                string agent = CurrentAgent();
                bool isPlaying = IsPlayingNow();
                foreach (var modification in modifications ?? Array.Empty<UndoPropertyModification>())
                    CapturePropertyModification(modification, source, agent, isPlaying);

                if (PendingProperties.Count > 0)
                    SchedulePropertyFlush();
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Editor change tracker failed to process property changes: " + ex.Message);
            }
            return modifications;
        }

        private static void CapturePropertyModification(UndoPropertyModification modification, string source, string agent, bool isPlaying)
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
            // Play state is part of the key: edit-mode and play-mode changes to one property must stay two entries,
            // because play-mode records are exempt from scope checks and the edit-mode one must not hide in them.
            string key = source + ":" + (isPlaying ? "play" : "edit") + ":" + targetKey + ":" + propertyPath;

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
                if (!string.Equals(existing.Agent, agent, StringComparison.Ordinal))
                    existing.Agent = AgentMixed;
            }
            else
            {
                PendingProperties[key] = new PendingPropertyChange
                {
                    Source = source,
                    Agent = agent,
                    IsPlaying = isPlaying,
                    Change = change
                };
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

        internal static void FlushPendingProperties()
        {
            if (_propertyFlushScheduled)
            {
                EditorApplication.update -= FlushPropertiesWhenDue;
                _propertyFlushScheduled = false;
            }
            if (PendingProperties.Count == 0)
                return;

            // The debounce outlives the REST window that caused the change, so attribution is the value captured with each
            // change, not whatever is current now.
            foreach (var group in PendingProperties.Values.GroupBy(p => new { p.Source, p.Agent, p.IsPlaying }))
            {
                var changes = group.Take(MaxChangesPerFlush).Select(p => p.Change).ToList();
                PublishSceneChanges(changes, group.Count() - changes.Count, group.Key.Source, group.Key.Agent,
                    group.Key.IsPlaying);
            }
            PendingProperties.Clear();
        }

        /// <summary>
        /// One flush of scene changes as consecutive scene_changes entries of at most <see cref="MaxChangesPerEvent"/> changes
        /// each. <paramref name="omitted"/> (the changes past <see cref="MaxChangesPerFlush"/>) is counted on the last entry;
        /// a split flush numbers its entries with <c>part</c> / <c>parts</c>.
        /// </summary>
        private static void PublishSceneChanges(IList<JObject> changes, int omitted, string source, string agent = null,
            bool? isPlaying = null)
        {
            int parts = Math.Max(1, (changes.Count + MaxChangesPerEvent - 1) / MaxChangesPerEvent);
            for (int part = 0; part < parts; part++)
            {
                var chunk = new JArray(changes.Skip(part * MaxChangesPerEvent).Take(MaxChangesPerEvent));
                int chunkOmitted = part == parts - 1 ? omitted : 0;
                var payload = new JObject
                {
                    ["count"] = chunk.Count + chunkOmitted,
                    ["omitted"] = chunkOmitted,
                    ["changes"] = chunk
                };
                if (parts > 1)
                {
                    payload["part"] = part + 1;
                    payload["parts"] = parts;
                }
                Publish("scene_changes", source, payload, agent, isPlaying);
            }
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

        private static JObject CaptureCreated(string entityId)
        {
            var go = ResolveGameObject(entityId);
            if (go == null || IsIgnored(go))
                return null;
            UpsertCatalog(go);
            CatalogHierarchy(go);

            var change = new JObject { ["kind"] = "gameobject_created" };
            AddGameObjectIdentity(change, go);
            change["components"] = JArray.FromObject(SnapshotComponentTypes(go));
            return change;
        }

        internal static JObject CaptureDestroyed(string entityId)
        {
            if (!Catalog.TryGetValue(entityId, out var entry))
                return new JObject
                {
                    ["kind"] = "gameobject_destroyed",
                    ["entityId"] = entityId,
                    ["instanceId"] = LegacyInstanceIdFromKey(entityId)
                };

            var change = new JObject
            {
                ["kind"] = "gameobject_destroyed",
                ["name"] = entry.Name,
                ["path"] = entry.Path,
                ["scenePath"] = entry.ScenePath,
                ["components"] = JArray.FromObject(entry.ComponentTypes ?? new List<string>()),
                ["entityId"] = entry.EntityId,
                ["instanceId"] = entry.InstanceId
            };

            string childPrefix = string.IsNullOrEmpty(entry.Path) ? null : entry.Path + "/";
            var removed = Catalog.Where(kv => kv.Key == entityId ||
                    (childPrefix != null && !string.IsNullOrEmpty(kv.Value.Path) &&
                     kv.Value.Path.StartsWith(childPrefix, StringComparison.Ordinal)))
                .Select(kv => kv.Key).ToArray();
            foreach (string id in removed)
                Catalog.Remove(id);
            return change;
        }

        internal static JObject CaptureReparented(string entityId, string newParentEntityId)
        {
            Catalog.TryGetValue(entityId, out var before);
            var go = ResolveGameObject(entityId);
            if (go == null || IsIgnored(go))
                return null;

            var change = new JObject
            {
                ["kind"] = "gameobject_reparented",
                ["name"] = go.name,
                ["fromPath"] = before?.Path,
                ["toPath"] = GameObjectFinder.GetPath(go),
                ["newParentPath"] = go.transform.parent != null ? GameObjectFinder.GetPath(go.transform.parent.gameObject) : null,
                ["newParentEntityId"] = newParentEntityId,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                ["scenePath"] = go.scene.path
            };
            CatalogHierarchy(go);
            return change;
        }

        private static JObject CaptureStructure(string entityId)
        {
            var go = ResolveGameObject(entityId);
            if (go == null || IsIgnored(go))
                return null;

            Catalog.TryGetValue(entityId, out var before);
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

        private static JObject CaptureReordered(string entityId)
        {
            var go = ResolveGameObject(entityId);
            if (go == null || IsIgnored(go))
                return null;
            var change = new JObject { ["kind"] = "children_reordered" };
            AddGameObjectIdentity(change, go);
            return change;
        }

        private static void RefreshCatalogForObject(string entityId)
        {
            var obj = UnityObjectIdUtility.EntityIdToObject(entityId);
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

        internal static void UpsertCatalog(GameObject go)
        {
            string id = UnityObjectIdUtility.GetEntityId(go);
            if (id == null)
                return;
            Catalog[id] = new CatalogEntry
            {
                EntityId = id,
                InstanceId = UnityObjectIdUtility.GetObjectId(go),
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

        private static GameObject ResolveGameObject(string entityId)
        {
            var obj = UnityObjectIdUtility.EntityIdToObject(entityId);
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

        /// <param name="agent">Attribution to store. Null derives it from <paramref name="source"/>: the current REST window's agent for "rest", null for "editor".</param>
        /// <param name="isPlaying">Play state to store. Null reads it now.</param>
        private static void Publish(string type, string source, JObject payload, string agent = null, bool? isPlaying = null)
        {
            EnsureLoaded();
            string entryAgent = agent ??
                (string.Equals(source, "rest", StringComparison.Ordinal) ? (_restAgent ?? AgentUnknown) : null);
            var entry = new JObject
            {
                ["seq"] = ++_seq,
                ["type"] = type,
                ["source"] = source,
                ["agent"] = entryAgent == null ? (JToken)JValue.CreateNull() : new JValue(entryAgent),
                ["isPlaying"] = isPlaying ?? IsPlayingNow(),
                ["tsUtc"] = DateTime.UtcNow.ToString("o"),
                ["payload"] = payload ?? new JObject()
            };
            Buffer.Enqueue(entry);
            while (Buffer.Count > BufferCapacity)
                Buffer.Dequeue();
            Persist(entry);
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;
            Buffer.Clear();
            _seq = 0;
            _activeEntries = 0;
            _activeBytes = 0;

            var ring = new List<JObject>();
            string active = LogPath;
            if (File.Exists(active) && TryReadSegmentTail(active, BufferCapacity, out var activeTail, out int lines, out long activeMaxSeq))
            {
                ring = activeTail;
                _activeEntries = lines;
                _activeBytes = new FileInfo(active).Length;
                _seq = activeMaxSeq;
            }

            // Rotated segments (newest first) top the ring up when the active one holds fewer than BufferCapacity entries, and
            // stand in for the seq counter when the active one is absent or empty, e.g. just after a rotation.
            for (int n = 1; n <= MaxRotatedSegments && (ring.Count < BufferCapacity || _seq == 0); n++)
            {
                string segment = SegmentPath(n);
                if (!File.Exists(segment) || !TryReadSegmentTail(segment, BufferCapacity - ring.Count, out var older, out _, out long segmentMaxSeq))
                    continue;
                _seq = Math.Max(_seq, segmentMaxSeq);
                ring.InsertRange(0, older);
            }

            foreach (var entry in ring)
                Buffer.Enqueue(entry);
        }

        /// <summary>
        /// Reads one segment, keeping only the last <paramref name="keep"/> lines for parsing: a segment can hold thousands of
        /// entries and the ring needs a few hundred. Seq grows with the line order, so the newest line decides the max.
        /// </summary>
        private static bool TryReadSegmentTail(string path, int keep, out List<JObject> entries, out int lineCount, out long maxSeq)
        {
            entries = new List<JObject>();
            lineCount = 0;
            maxSeq = 0;
            try
            {
                var window = new Queue<string>();
                foreach (string line in ReadLinesShared(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    lineCount++;
                    window.Enqueue(line);
                    if (window.Count > keep)
                        window.Dequeue();
                }

                foreach (string line in window)
                {
                    JObject entry;
                    try { entry = JObject.Parse(line); }
                    catch { continue; }
                    long seq = entry.Value<long?>("seq") ?? 0;
                    if (seq <= 0)
                        continue;
                    maxSeq = Math.Max(maxSeq, seq);
                    entries.Add(entry);
                }
                return true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Failed to load editor change journal segment: " + ex.Message);
                return false;
            }
        }

        private static IEnumerable<string> ReadLinesShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, SkillsCommon.Utf8NoBom))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    yield return line;
            }
        }

        /// <summary>One write per entry, so a concurrent reader sees a whole line or none of it.</summary>
        private static void Persist(JObject entry)
        {
            try
            {
                byte[] bytes = SkillsCommon.Utf8NoBom.GetBytes(entry.ToString(Formatting.None) + Environment.NewLine);
                // A kill can cut a write short; the append starts a new line after such a tail, so it costs one row, not two.
                LibraryStateFile.AppendLines(LogPath, bytes);
                _activeEntries++;
                _activeBytes += bytes.Length;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Failed to persist editor change journal: " + ex.Message);
                return;
            }
            RotateIfNeeded();
        }

        private static int EntryCap => OverrideSegmentMaxEntriesForTests > 0 ? OverrideSegmentMaxEntriesForTests : SegmentMaxEntries;

        private static long ByteCap => OverrideSegmentMaxBytesForTests > 0 ? OverrideSegmentMaxBytesForTests : SegmentMaxBytes;

        /// <summary>
        /// Moves the active segment to <c>.1</c> once it is full, shifting older ones up and dropping the oldest. Nothing is
        /// ever overwritten, and any failure (a reader holding a file open on Windows, a read-only folder) just leaves the
        /// segment growing: the next append retries.
        /// </summary>
        private static void RotateIfNeeded()
        {
            if (_activeEntries < EntryCap && _activeBytes < ByteCap)
                return;

            try
            {
                // The lowest free slot is where the shift ends. Normally that is right after the contiguous run of segments;
                // a gap left by an earlier failed shift gets closed first, so a retry never drops a segment that is not due.
                int free = 1;
                while (free <= MaxRotatedSegments && File.Exists(SegmentPath(free)))
                    free++;
                if (free > MaxRotatedSegments)
                {
                    File.Delete(SegmentPath(MaxRotatedSegments));
                    free = MaxRotatedSegments;
                }

                for (int n = free - 1; n >= 1; n--)
                    File.Move(SegmentPath(n), SegmentPath(n + 1));
                File.Move(LogPath, SegmentPath(1));

                _activeEntries = 0;
                _activeBytes = 0;
                _rotationFailureLogged = false;
            }
            catch (Exception ex)
            {
                if (!_rotationFailureLogged)
                {
                    _rotationFailureLogged = true;
                    SkillsLogger.LogWarning("Editor change journal rotation failed; it is retried on the next change: " + ex.Message);
                }
            }
        }

        /// <summary>Rotated segment <paramref name="n"/> (1 = newest) next to the active file: <c>editor_changes.1.jsonl</c>.</summary>
        internal static string SegmentPath(int n)
        {
            string active = LogPath;
            string directory = Path.GetDirectoryName(active) ?? string.Empty;
            return Path.Combine(directory, Path.GetFileNameWithoutExtension(active) + "." + n + Path.GetExtension(active));
        }

        private static string LogPath => !string.IsNullOrEmpty(OverrideLogPathForTests)
            ? OverrideLogPathForTests
            : Path.GetFullPath(Path.Combine(Application.dataPath, "..", RelativeLogPath));

        private static string CurrentSource() => _restDepth > 0 ? "rest" : "editor";

        private static string CurrentAgent() => _restDepth > 0 ? (_restAgent ?? AgentUnknown) : null;

        private static bool IsPlayingNow() => OverrideIsPlayingForTests ?? EditorApplication.isPlaying;

        /// <summary>The raw id from an ObjectChangeEvent key as a legacy instance id; 0 where Unity no longer has one (6000.4+).</summary>
        private static int LegacyInstanceIdFromKey(string entityId)
        {
#if UNITY_6000_4_OR_NEWER
            return 0;
#else
            return int.TryParse(entityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int instanceId) ? instanceId : 0;
#endif
        }

        /// <param name="total">Distinct non-blank paths before the per-flush bound.</param>
        private static string[] CleanPaths(string[] paths, out int total)
        {
            var distinct = (paths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            total = distinct.Length;
            return total <= MaxChangesPerFlush ? distinct : distinct.Take(MaxChangesPerFlush).ToArray();
        }

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
            _restAgent = null;
            _activeEntries = 0;
            _activeBytes = 0;
            _rotationFailureLogged = false;
            if (deleteFile)
                DeleteJournalFilesForTests();
        }

        private static void DeleteJournalFilesForTests()
        {
            if (File.Exists(LogPath))
                File.Delete(LogPath);
            // One past the last rotated slot as well, so a test can assert that slot never appears.
            for (int n = 1; n <= MaxRotatedSegments + 1; n++)
            {
                string segment = SegmentPath(n);
                if (File.Exists(segment))
                    File.Delete(segment);
                else if (Directory.Exists(segment))
                    Directory.Delete(segment, true);
            }
        }

        internal static void ReloadForTests()
        {
            Buffer.Clear();
            _seq = 0;
            _loaded = false;
            EnsureLoaded();
        }

        internal static void PublishForTests(string type, string source, JObject payload = null)
        {
            Publish(type, source, payload ?? new JObject());
        }

        /// <summary>Closes one REST depth step at once, standing in for the next-frame tick <see cref="EndRestExecution"/> schedules.</summary>
        internal static void EndRestExecutionNowForTests() => EndRestWindowStep();
    }

    internal sealed class EditorChangeAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (ImportWorkerGate.IsWorkerProcess) return;

            ScriptDomainImportCapture.Record(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
            EditorChangeTrackerService.RecordFileChanges(
                importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
        }
    }

    /// <summary>
    /// Collects the script-domain paths (see <see cref="ServerAvailabilityHelper.AffectsScriptDomain"/>) the asset
    /// pipeline reports while the scope is open. OnPostprocessAllAssets runs synchronously inside
    /// AssetDatabase.Refresh, so wrapping a refresh in a scope tells whether it touched scripts.
    /// </summary>
    internal sealed class ScriptDomainImportCapture : IDisposable
    {
        private static ScriptDomainImportCapture _current;

        private readonly ScriptDomainImportCapture _outer;
        private readonly List<string> _paths = new List<string>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        private ScriptDomainImportCapture(ScriptDomainImportCapture outer) => _outer = outer;

        /// <summary>Distinct script-domain paths in the order they were reported.</summary>
        public string[] Paths => _paths.ToArray();

        public static ScriptDomainImportCapture Begin() => _current = new ScriptDomainImportCapture(_current);

        internal static void Record(params string[][] pathGroups)
        {
            if (_current == null || pathGroups == null)
                return;

            foreach (var group in pathGroups)
            {
                foreach (string path in group ?? Array.Empty<string>())
                {
                    if (!ServerAvailabilityHelper.AffectsScriptDomain(path))
                        continue;
                    string normalized = path.Replace('\\', '/');
                    for (var capture = _current; capture != null; capture = capture._outer)
                    {
                        if (capture._seen.Add(normalized))
                            capture._paths.Add(normalized);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_current == this)
                _current = _outer;
        }
    }
}

// Producer:Betsy
