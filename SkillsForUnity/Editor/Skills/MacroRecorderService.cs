using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnitySkills
{
    /// <summary>Per-record inversion capability — the panel timeline's three-state verdict.</summary>
    internal enum MacroInvertibility { Ok, Partial, Unsupported }

    /// <summary>
    /// Classification result shared by macro_export and the panel: State plus, for non-Ok
    /// records, a machine key (panel localization lookup) and the full English sentence that
    /// macro_export emits verbatim as its warning — one source of truth for both consumers.
    /// </summary>
    internal sealed class MacroInvertibilityInfo
    {
        public MacroInvertibility State;
        public string ReasonKey;
        public string ReasonEn;

        public static MacroInvertibilityInfo Ok { get; } = new MacroInvertibilityInfo { State = MacroInvertibility.Ok };

        public static MacroInvertibilityInfo Partial(string key, string reason) =>
            new MacroInvertibilityInfo { State = MacroInvertibility.Partial, ReasonKey = key, ReasonEn = reason };

        public static MacroInvertibilityInfo Unsupported(string key, string reason) =>
            new MacroInvertibilityInfo { State = MacroInvertibility.Unsupported, ReasonKey = key, ReasonEn = reason };
    }

    /// <summary>One row of the panel timeline (a projection of a MacroRecord).</summary>
    internal sealed class MacroTimelineItem
    {
        public int Index;
        public string TsUtc;
        public string KindKey;      // create | destroy | reparent | structure | property
        public string SubjectName;
        public string Detail;       // raw data snippet (path / components / property=value)
        public MacroInvertibility State;
        public string ReasonKey;    // localization suffix for the panel; null when Ok
        public string ReasonEn;     // English fallback (same text export emits)
    }

    /// <summary>Session state + incremental timeline items, pulled by the panel every tick.</summary>
    internal sealed class MacroTimelineSnapshot
    {
        public bool Recording;
        public bool HasStoppedSession;
        public string SessionStartedUtc;  // session identity — the panel resets when it changes
        public string StoppedReason;
        public bool InterruptedByReload;
        public bool UndoRedoDetected;
        public int Total;                 // record count of the current/last session
        public int Revision;              // bumped on out-of-append mutation (RemoveRecord) → panel full-rebuild
        public List<MacroTimelineItem> Items;
    }

    /// <summary>Coverage counters for the panel bar (Total includes off-buffer unsupported events).</summary>
    internal sealed class MacroCoverageSummary
    {
        public int Total;
        public int Ok;
        public int Partial;
        public int Unsupported;
    }

    /// <summary>
    /// Demonstration-recording ("macro") session backing the macro_record_* / macro_export
    /// skills: while recording, scene edits (manual ones and REST-driven ones alike — both go
    /// through the same Undo pipeline) are captured from two complementary editor event sources
    /// and later inverted into a step sequence directly POSTable to /skills/batch.
    ///
    /// Event sources (all callbacks arrive on the main thread, as do skill executions):
    /// - ObjectChangeEvents.changesPublished — structural changes (create / destroy / reparent /
    ///   component add-remove). Destroy events arrive AFTER the object died, so an
    ///   instanceId → (name, path, components) catalog is maintained for the whole session,
    ///   seeded from a full snapshot of all loaded scenes at start (otherwise deleting a
    ///   pre-existing object would be uninvertible: its first and only event has no live object).
    /// - Undo.postprocessModifications — exact property-level modifications
    ///   (target + propertyPath + value). Consecutive edits of the same
    ///   (object, component, propertyPath) are merged in place while recording (slider drags
    ///   produce hundreds of intermediate values), so the buffer holds final values in
    ///   first-occurrence order and drags cannot flood it.
    ///
    /// Lifecycle: at most one session; the buffer is capped (auto-stop with stoppedReason
    /// "buffer_full"); a domain reload kills the event subscriptions, so an active session is
    /// discarded and flagged via SessionState for macro_record_status to report.
    /// </summary>
    [InitializeOnLoad]
    public static class MacroRecorderService
    {
        private const int MaxRecords = 1000;
        private const string SessionKeyActive = "UnitySkills_MacroRecordingActive";
        private const string SessionKeyInterrupted = "UnitySkills_MacroRecordingInterrupted";

        internal enum RecordKind { Create, Destroy, Reparent, Structure, Property, Reorder }

        internal sealed class MacroRecord
        {
            public RecordKind Kind;
            public string Source;              // "structure" | "property"
            public string TsUtc;
            public int InstanceId;             // subject GameObject
            public string SubjectName;         // at record time
            public string SubjectPath;         // at record time (Reparent: path BEFORE the move)
            // Reparent
            public int NewParentInstanceId;    // 0 = moved to scene root
            public string NewParentPath;
            // Structure (component-set delta against the catalog snapshot)
            public List<string> AddedComponents;
            public List<string> RemovedComponents;
            // Removed types with other instance(s) of the same type still on the object after
            // the removal — a type name alone cannot address the right instance, so these stay
            // uninverted (the safe subset in RemovedComponents still becomes component_remove).
            public List<string> RemovedComponentsWithResidue;
            // Reorder (net final state; one record per container per session)
            // InstanceId is the parent whose children were reordered, or 0 for a scene-root
            // reorder, in which case SceneHandle locates the scene at export time.
            public int SceneHandle;
            // Property
            public string ComponentTypeName;   // null = property on the GameObject itself (m_Name, ...)
            public string PropertyPath;
            public string ValueString;         // final (merged) plain value
            public string PreviousValueString; // first-occurrence previous value (rename targeting)
            public bool HasObjectReference;
            public int ReferenceInstanceId;    // scene reference (for $ref promotion on export)
            public string ReferencePath;       // scene reference path at record time
            public string ReferenceAssetPath;  // asset reference
            public string ReferenceObjectType;
            public bool ReferenceUnresolved;
            // UI-side classification cache (ClassifyRecordCached); invalidated when a debounce
            // merge rewrites the value. Export always classifies fresh.
            public MacroInvertibilityInfo CachedInvertibility;
        }

        internal sealed class CatalogEntry
        {
            public int InstanceId;
            public string Name;
            public string Path;
            public bool CreatedDuringRecording;
            public bool Alive = true;
            public List<string> ComponentTypes;
            // Set on the root of a prefab instance dragged in during the recording; export
            // inverts its create record into prefab_instantiate instead of gameobject_create.
            public string PrefabAssetPath;
            // For descendants of such a root: back-pointer to the root's instanceId. They have
            // no create step of their own, but once the root's instantiate step has rebuilt the
            // subtree they are addressable by their record-time path.
            public int PrefabInstanceRootId;
        }

        private static bool _recording;
        private static bool _hasStoppedSession;
        // Bumped whenever a stopped session's record list is mutated out of append order
        // (currently only RemoveRecord). The panel watches it to full-rebuild its timeline,
        // since its incremental (SessionStartedUtc, Index) cursor assumes append-only growth.
        private static int _timelineRevision;
        private static string _note;
        private static DateTime _startedUtc;
        private static DateTime _stoppedUtc;
        private static string _stoppedReason;              // "manual" | "buffer_full"
        private static bool _undoRedoDuringRecording;
        private static readonly List<MacroRecord> _records = new List<MacroRecord>();
        private static readonly Dictionary<int, CatalogEntry> _catalog = new Dictionary<int, CatalogEntry>();
        // (instanceId:componentType:propertyPath) → record, for in-place debounce merging.
        private static readonly Dictionary<string, MacroRecord> _propertyIndex = new Dictionary<string, MacroRecord>();
        // Reorder container key ("c:{parentId}" / "r:{sceneHandle}") → record. Reorder events
        // fire in rapid bursts while dragging; one net-final-state record per container is
        // enough because export re-reads the container's order at export time.
        private static readonly Dictionary<string, MacroRecord> _reorderIndex = new Dictionary<string, MacroRecord>();
        // Events we cannot invert (asset edits, prefab apply/revert, ...) — counted for
        // warnings instead of occupying buffer slots.
        private static readonly Dictionary<string, int> _unsupportedCounts = new Dictionary<string, int>();

        // ===== REST source filter ("ignore REST-driven changes" panel toggle) =====
        // SkillRouter.Execute brackets every REST-invoked skill with Begin/EndRestExecution.
        // Undo.postprocessModifications fires synchronously inside that bracket, but
        // ObjectChangeEvents batches per frame and publishes AFTER Execute returned — so the
        // decrement is deferred one editor tick: the marker is still up when the frame-end
        // flush of the executing tick arrives, and is gone before the next tick's manual edits.

        private const string PrefKeyIgnoreRest = "UnitySkills_MacroIgnoreRestChanges";
        private static int _restDepth;

        // 提示性计数（事件级，非记录级）：录制期间被 REST 过滤掉的变化事件数。
        // 让"录了个空"可诊断——调用方看到 restFilteredCount>0 就知道是开关在过滤，
        // 而不是录制器坏了（此坑实际发生过：开关被遗留为 ON，REST 录制静默全空）。
        private static int _restFilteredCount;
        private static bool? _ignoreRestCache;

        internal static bool IgnoreRestChanges
        {
            get
            {
                if (_ignoreRestCache == null)
                    _ignoreRestCache = EditorPrefs.GetBool(PrefKeyIgnoreRest, false);
                return _ignoreRestCache.Value;
            }
            set
            {
                _ignoreRestCache = value;
                EditorPrefs.SetBool(PrefKeyIgnoreRest, value);
            }
        }

        private static bool SkipRestChange => _restDepth > 0 && IgnoreRestChanges;

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


        static MacroRecorderService()
        {
            // Subscriptions die with the old domain: an active recording cannot survive a
            // reload, so discard it and leave a flag for macro_record_status.
            if (SessionState.GetString(SessionKeyActive, "0") == "1")
            {
                SessionState.SetString(SessionKeyActive, "0");
                SessionState.SetString(SessionKeyInterrupted, "1");
                SkillsLogger.LogWarning("Macro recording was interrupted by a domain reload; the session was discarded.");
            }
        }

        public static bool IsRecording => _recording;

        // Session meta of the current/last session — consumed by MacroFileStore's file export.
        internal static string SessionNote => (_recording || _hasStoppedSession) ? _note : null;
        internal static string SessionStartedUtc => (_recording || _hasStoppedSession) ? _startedUtc.ToString("o") : null;

        // ===== Session control (called by MacroSkills on the main thread) =====

        public static object Start(string note)
        {
            if (_recording)
            {
                return new
                {
                    error = "A macro recording is already active. Call macro_record_stop first.",
                    recording = true,
                    startedAtUtc = _startedUtc.ToString("o"),
                    recordCount = _records.Count
                };
            }

            _recording = true;
            _hasStoppedSession = false;
            _note = note;
            _startedUtc = DateTime.UtcNow;
            _stoppedReason = null;
            _undoRedoDuringRecording = false;
            _records.Clear();
            _catalog.Clear();
            _propertyIndex.Clear();
            _reorderIndex.Clear();
            _unsupportedCounts.Clear();
            _restFilteredCount = 0;

            SessionState.SetString(SessionKeyInterrupted, "0");
            SessionState.SetString(SessionKeyActive, "1");

            BuildInitialCatalog();

            ObjectChangeEvents.changesPublished += OnChangesPublished;
            Undo.postprocessModifications += OnPostprocessModifications;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;

            SkillsLogger.Log($"Macro recording started (catalog: {_catalog.Count} objects)"
                + (string.IsNullOrEmpty(note) ? "" : $" — {note}"));

            return new
            {
                success = true,
                recording = true,
                startedAtUtc = _startedUtc.ToString("o"),
                note = _note,
                catalogSize = _catalog.Count,
                maxRecords = MaxRecords
            };
        }

        public static object Stop()
        {
            if (!_recording)
                return new { error = "No macro recording is active. Call macro_record_start first.", recording = false };

            StopInternal("manual");

            return new
            {
                success = true,
                recording = false,
                recordCount = _records.Count,
                durationSec = Math.Round((_stoppedUtc - _startedUtc).TotalSeconds, 2),
                byKind = BuildByKind(),
                stoppedReason = _stoppedReason,
                undoRedoDetected = _undoRedoDuringRecording,
                ignoreRestChanges = IgnoreRestChanges,
                restFilteredCount = _restFilteredCount,
                note = _note
            };
        }

        public static object Status()
        {
            return new
            {
                recording = _recording,
                recordCount = (_recording || _hasStoppedSession) ? _records.Count : 0,
                startedAtUtc = (_recording || _hasStoppedSession) ? _startedUtc.ToString("o") : null,
                note = (_recording || _hasStoppedSession) ? _note : null,
                stoppedReason = _hasStoppedSession ? _stoppedReason : null,
                hasExportableSession = _hasStoppedSession,
                interruptedByReload = SessionState.GetString(SessionKeyInterrupted, "0") == "1",
                undoRedoDetected = (_recording || _hasStoppedSession) && _undoRedoDuringRecording,
                ignoreRestChanges = IgnoreRestChanges,
                restFilteredCount = (_recording || _hasStoppedSession) ? _restFilteredCount : 0,
                maxRecords = MaxRecords
            };
        }

        private static void StopInternal(string reason)
        {
            ObjectChangeEvents.changesPublished -= OnChangesPublished;
            Undo.postprocessModifications -= OnPostprocessModifications;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;

            _recording = false;
            _hasStoppedSession = true;
            _stoppedUtc = DateTime.UtcNow;
            _stoppedReason = reason;
            SessionState.SetString(SessionKeyActive, "0");

            SkillsLogger.Log($"Macro recording stopped ({reason}): {_records.Count} record(s)");
        }

        private static Dictionary<string, int> BuildByKind()
        {
            var byKind = new Dictionary<string, int>();
            foreach (var rec in _records)
            {
                var key = rec.Kind.ToString().ToLowerInvariant();
                byKind.TryGetValue(key, out var n);
                byKind[key] = n + 1;
            }
            foreach (var kv in _unsupportedCounts)
                byKind["unsupported:" + kv.Key] = kv.Value;
            return byKind;
        }

        // ===== Catalog (instanceId → name/path/components, survives object destruction) =====

        private static void BuildInitialCatalog()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                    CatalogHierarchy(root, createdDuringRecording: false);
            }
        }

        private static void CatalogHierarchy(GameObject go, bool createdDuringRecording)
        {
            if (go == null || IsIgnored(go))
                return;

            UpsertCatalogEntry(go, createdDuringRecording);
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                CatalogHierarchy(t.GetChild(i).gameObject, createdDuringRecording);
        }

        private static CatalogEntry UpsertCatalogEntry(GameObject go, bool createdDuringRecording)
        {
            int id = go.GetInstanceID();
            if (!_catalog.TryGetValue(id, out var entry))
            {
                entry = new CatalogEntry { InstanceId = id };
                _catalog[id] = entry;
                entry.CreatedDuringRecording = createdDuringRecording;
            }
            else if (createdDuringRecording)
            {
                // A property/structure event may reach us before the frame-batched create event;
                // the create handler corrects the flag here.
                entry.CreatedDuringRecording = true;
            }
            entry.Alive = true;
            entry.Name = go.name;
            entry.Path = GameObjectFinder.GetPath(go);
            entry.ComponentTypes = SnapshotComponentTypes(go);
            return entry;
        }

        private static List<string> SnapshotComponentTypes(GameObject go)
        {
            var comps = go.GetComponents<Component>();
            var list = new List<string>(comps.Length);
            foreach (var c in comps)
            {
                if (c != null)
                    list.Add(c.GetType().Name);
            }
            return list;
        }

        private static void RefreshPathsRecursive(GameObject go)
        {
            if (go == null)
                return;
            if (_catalog.TryGetValue(go.GetInstanceID(), out var entry))
            {
                entry.Name = go.name;
                entry.Path = GameObjectFinder.GetPath(go);
            }
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                RefreshPathsRecursive(t.GetChild(i).gameObject);
        }

        private static bool IsIgnored(GameObject go)
        {
            // Editor-internal / hidden / non-scene objects never enter the recording.
            if ((go.hideFlags & (HideFlags.DontSave | HideFlags.HideInHierarchy)) != 0)
                return true;
            if (EditorUtility.IsPersistent(go))
                return true;
            return !go.scene.IsValid();
        }

        // ===== Event source 1: ObjectChangeEvents (structural changes) =====

        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (!_recording)
                return;

            try
            {
                for (int i = 0; i < stream.length; i++)
                {
                    if (!_recording)
                        return; // buffer_full auto-stop mid-stream

                    switch (stream.GetEventType(i))
                    {
                        case ObjectChangeKind.CreateGameObjectHierarchy:
                            stream.GetCreateGameObjectHierarchyEvent(i, out var createArgs);
                            HandleCreate(createArgs.instanceId);
                            break;

                        case ObjectChangeKind.DestroyGameObjectHierarchy:
                            stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyArgs);
                            HandleDestroy(destroyArgs.instanceId);
                            break;

                        case ObjectChangeKind.ChangeGameObjectParent:
                            stream.GetChangeGameObjectParentEvent(i, out var parentArgs);
                            HandleReparent(parentArgs.instanceId, parentArgs.newParentInstanceId);
                            break;

                        case ObjectChangeKind.ChangeGameObjectStructure:
                            stream.GetChangeGameObjectStructureEvent(i, out var structArgs);
                            HandleStructure(structArgs.instanceId, hierarchy: false);
                            break;

                        case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                            stream.GetChangeGameObjectStructureHierarchyEvent(i, out var structHierArgs);
                            HandleStructure(structHierArgs.instanceId, hierarchy: true);
                            break;

                        case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                            // Property details come from Undo.postprocessModifications; this
                            // event only keeps the catalog fresh (renames move paths).
                            stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var propArgs);
                            if (UnityObjectIdUtility.ObjectIdToObject(propArgs.instanceId) is GameObject renamed && !IsIgnored(renamed))
                                RefreshPathsRecursive(renamed);
                            break;

                        case ObjectChangeKind.ChangeChildrenOrder:
                            stream.GetChangeChildrenOrderEvent(i, out var childOrderArgs);
                            HandleChildrenReorder(childOrderArgs.instanceId);
                            break;

                        case ObjectChangeKind.ChangeRootOrder:
                            stream.GetChangeRootOrderEvent(i, out var rootOrderArgs);
                            HandleRootReorder(rootOrderArgs.scene);
                            break;

                        case ObjectChangeKind.CreateAssetObject:
                        case ObjectChangeKind.DestroyAssetObject:
                        case ObjectChangeKind.ChangeAssetObjectProperties:
                        case ObjectChangeKind.UpdatePrefabInstances:
                            CountUnsupported(stream.GetEventType(i).ToString());
                            break;

                        // ObjectChangeKind.ChangeScene ("scene dirtied, nothing specific") and
                        // None carry no invertible information — pure noise, skip silently.
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Macro recorder failed to process a change event: {ex.Message}");
            }
        }

        private static void HandleCreate(int instanceId)
        {
            var go = UnityObjectIdUtility.ObjectIdToObject(instanceId) as GameObject;
            if (go == null || IsIgnored(go))
                return;

            // The event only names the hierarchy root; catalog the whole subtree so later
            // events on descendants (and their destruction) resolve.
            // A filtered REST create keeps NO create record, so the object must read as
            // pre-existing — otherwise later manual steps on it would be dropped by export
            // ("created during recording but has no create step").
            bool skipRest = SkipRestChange;
            CatalogHierarchy(go, createdDuringRecording: !skipRest);

            // A dragged-in prefab instance publishes ONE hierarchy-create event for its root
            // (no per-child events). Snapshot the asset path now — the instance may be renamed
            // or unpacked later — so export can invert the whole subtree into a single
            // prefab_instantiate step. Objects merely ADDED under an existing instance are not
            // part of it (GetNearestPrefabInstanceRoot returns null for them) and keep taking
            // the plain gameobject_create path.
            if (!skipRest
                && PrefabUtility.IsPartOfPrefabInstance(go)
                && PrefabUtility.GetNearestPrefabInstanceRoot(go) == go
                && _catalog.TryGetValue(instanceId, out var rootEntry))
            {
                rootEntry.PrefabAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                MarkPrefabDescendants(go.transform, instanceId);
            }

            AddRecord(new MacroRecord
            {
                Kind = RecordKind.Create,
                Source = "structure",
                InstanceId = instanceId,
                SubjectName = go.name,
                SubjectPath = GameObjectFinder.GetPath(go)
            });
        }

        private static void MarkPrefabDescendants(Transform root, int rootInstanceId)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (_catalog.TryGetValue(child.gameObject.GetInstanceID(), out var entry))
                    entry.PrefabInstanceRootId = rootInstanceId;
                MarkPrefabDescendants(child, rootInstanceId);
            }
        }

        private static void HandleDestroy(int instanceId)
        {
            // The object is already dead — everything must come from the catalog. A miss means
            // we never saw the object (hidden/internal), so it was never part of the recording.
            if (!_catalog.TryGetValue(instanceId, out var entry) || !entry.Alive)
                return;

            entry.Alive = false;
            var prefix = entry.Path + "/";
            foreach (var other in _catalog.Values)
            {
                if (other.Alive && other.Path != null && other.Path.StartsWith(prefix, StringComparison.Ordinal))
                    other.Alive = false;
            }

            AddRecord(new MacroRecord
            {
                Kind = RecordKind.Destroy,
                Source = "structure",
                InstanceId = instanceId,
                SubjectName = entry.Name,
                SubjectPath = entry.Path
            });
        }

        private static void HandleReparent(int instanceId, int newParentInstanceId)
        {
            var go = UnityObjectIdUtility.ObjectIdToObject(instanceId) as GameObject;
            if (go == null || IsIgnored(go))
                return;

            // Locate the child by where it lived BEFORE the move (replay-time consistent);
            // the catalog still holds the pre-move path until we refresh below.
            string pathBefore = _catalog.TryGetValue(instanceId, out var entry) ? entry.Path : GameObjectFinder.GetPath(go);

            string newParentPath = null;
            if (newParentInstanceId != 0 && UnityObjectIdUtility.ObjectIdToObject(newParentInstanceId) is GameObject parentGo)
                newParentPath = GameObjectFinder.GetPath(parentGo);

            AddRecord(new MacroRecord
            {
                Kind = RecordKind.Reparent,
                Source = "structure",
                InstanceId = instanceId,
                SubjectName = go.name,
                SubjectPath = pathBefore,
                NewParentInstanceId = newParentInstanceId,
                NewParentPath = newParentPath
            });

            UpsertCatalogEntry(go, createdDuringRecording: false);
            RefreshPathsRecursive(go);
        }

        /// <summary>
        /// Children of parentInstanceId were reordered. One net-final-state record per container
        /// per session: drag-reordering fires bursts of these events, and export re-reads the
        /// container's CURRENT child order anyway (same "invert from current state" contract as
        /// creates), so recording "this container was reordered" once is lossless.
        /// </summary>
        private static void HandleChildrenReorder(int parentInstanceId)
        {
            var parent = UnityObjectIdUtility.ObjectIdToObject(parentInstanceId) as GameObject;
            if (parent == null || IsIgnored(parent))
                return;

            var key = "c:" + parentInstanceId;
            if (_reorderIndex.ContainsKey(key))
                return;

            if (!_catalog.ContainsKey(parentInstanceId))
                UpsertCatalogEntry(parent, createdDuringRecording: false);

            var rec = new MacroRecord
            {
                Kind = RecordKind.Reorder,
                Source = "structure",
                InstanceId = parentInstanceId,
                SubjectName = parent.name,
                SubjectPath = GameObjectFinder.GetPath(parent)
            };
            if (AddRecord(rec))
                _reorderIndex[key] = rec;
        }

        /// <summary>Scene-root sibling order changed; same net-final-state contract, keyed by scene.</summary>
        private static void HandleRootReorder(Scene scene)
        {
            if (!scene.IsValid())
                return;

            var key = "r:" + scene.handle;
            if (_reorderIndex.ContainsKey(key))
                return;

            var rec = new MacroRecord
            {
                Kind = RecordKind.Reorder,
                Source = "structure",
                InstanceId = 0,
                SceneHandle = scene.handle,
                SubjectName = scene.name
            };
            if (AddRecord(rec))
                _reorderIndex[key] = rec;
        }

        private static void HandleStructure(int instanceId, bool hierarchy)
        {
            var go = UnityObjectIdUtility.ObjectIdToObject(instanceId) as GameObject;
            if (go == null || IsIgnored(go))
                return;

            var current = SnapshotComponentTypes(go);
            List<string> added = null, removed = null;

            if (_catalog.TryGetValue(instanceId, out var entry) && entry.ComponentTypes != null)
            {
                DiffComponentSets(entry.ComponentTypes, current, out added, out removed);
            }
            else
            {
                // No baseline (object surfaced mid-recording without a create event) — a diff
                // would be guesswork; report instead of fabricating steps.
                CountUnsupported("StructureChangeWithoutBaseline");
            }

            UpsertCatalogEntry(go, createdDuringRecording: false);
            if (hierarchy)
            {
                // Descendants may have changed too; refresh their snapshots so later diffs
                // stay accurate. Their own changes are not diffed here (unreliable).
                CatalogHierarchy(go, createdDuringRecording: false);
            }

            if ((added == null || added.Count == 0) && (removed == null || removed.Count == 0))
                return;

            // Removal ambiguity must be judged NOW, against the post-removal snapshot: a removed
            // type with surviving same-type instance(s) cannot be addressed by type name alone at
            // replay time (component_remove would hit an arbitrary instance).
            List<string> removedResidue = null;
            if (removed != null)
            {
                foreach (var typeName in removed)
                {
                    if (!current.Contains(typeName))
                        continue;
                    if (removedResidue == null)
                        removedResidue = new List<string>();
                    if (!removedResidue.Contains(typeName))
                        removedResidue.Add(typeName);
                }
            }

            AddRecord(new MacroRecord
            {
                Kind = RecordKind.Structure,
                Source = "structure",
                InstanceId = instanceId,
                SubjectName = go.name,
                SubjectPath = GameObjectFinder.GetPath(go),
                AddedComponents = added,
                RemovedComponents = removed,
                RemovedComponentsWithResidue = removedResidue
            });
        }

        private static void DiffComponentSets(List<string> before, List<string> after, out List<string> added, out List<string> removed)
        {
            var counts = new Dictionary<string, int>();
            foreach (var t in before)
            {
                counts.TryGetValue(t, out var n);
                counts[t] = n - 1;
            }
            foreach (var t in after)
            {
                counts.TryGetValue(t, out var n);
                counts[t] = n + 1;
            }

            added = new List<string>();
            removed = new List<string>();
            foreach (var kv in counts)
            {
                for (int i = 0; i < kv.Value; i++) added.Add(kv.Key);
                for (int i = 0; i < -kv.Value; i++) removed.Add(kv.Key);
            }
        }

        // ===== Event source 2: Undo.postprocessModifications (property changes) =====

        private static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            if (!_recording)
                return modifications;

            try
            {
                foreach (var mod in modifications)
                    CaptureModification(mod);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Macro recorder failed to capture a property modification: {ex.Message}");
            }

            return modifications;
        }

        private static void CaptureModification(UndoPropertyModification mod)
        {
            // Filtered REST change: no record, and no debounce merge either — a REST value
            // must not overwrite the final value of an earlier manual record with the same key.
            if (SkipRestChange)
            {
                _restFilteredCount++;
                return;
            }

            var cur = mod.currentValue;
            if (cur == null || cur.target == null)
                return;

            var target = cur.target;
            if (EditorUtility.IsPersistent(target))
            {
                CountUnsupported("AssetPropertyModification");
                return;
            }

            GameObject go;
            string componentTypeName;
            if (target is GameObject asGo)
            {
                go = asGo;
                componentTypeName = null;
            }
            else if (target is Component asComp)
            {
                go = asComp.gameObject;
                componentTypeName = asComp.GetType().Name;
            }
            else
            {
                CountUnsupported("NonScenePropertyModification:" + target.GetType().Name);
                return;
            }

            if (IsIgnored(go))
                return;

            var propertyPath = cur.propertyPath ?? string.Empty;
            // Editor bookkeeping, not scene state — recording them would only produce noise steps.
            if (propertyPath == "m_RootOrder" || propertyPath.StartsWith("m_LocalEulerAnglesHint", StringComparison.Ordinal))
                return;

            int goId = go.GetInstanceID();
            var key = goId + ":" + (componentTypeName ?? string.Empty) + ":" + propertyPath;
            if (_propertyIndex.TryGetValue(key, out var existing))
            {
                // Debounce: keep first-occurrence order, overwrite with the latest value.
                ApplyModificationValue(existing, cur);
                return;
            }

            // Make sure destroy/reparent later in the session can resolve this object.
            if (!_catalog.ContainsKey(goId))
                UpsertCatalogEntry(go, createdDuringRecording: false);

            var rec = new MacroRecord
            {
                Kind = RecordKind.Property,
                Source = "property",
                InstanceId = goId,
                SubjectName = go.name,
                SubjectPath = GameObjectFinder.GetPath(go),
                ComponentTypeName = componentTypeName,
                PropertyPath = propertyPath,
                PreviousValueString = mod.previousValue != null ? mod.previousValue.value : null
            };
            ApplyModificationValue(rec, cur);

            if (AddRecord(rec))
                _propertyIndex[key] = rec;
        }

        private static void ApplyModificationValue(MacroRecord rec, PropertyModification cur)
        {
            rec.CachedInvertibility = null; // value/reference changed → re-classify on next UI pull
            rec.HasObjectReference = cur.objectReference != null;
            rec.ValueString = null;
            rec.ReferenceInstanceId = 0;
            rec.ReferencePath = null;
            rec.ReferenceAssetPath = null;
            rec.ReferenceObjectType = null;
            rec.ReferenceUnresolved = false;

            if (!rec.HasObjectReference)
            {
                rec.ValueString = cur.value;
                return;
            }

            var reference = cur.objectReference;
            rec.ReferenceObjectType = reference.GetType().Name;
            if (EditorUtility.IsPersistent(reference))
            {
                rec.ReferenceAssetPath = AssetDatabase.GetAssetPath(reference);
                return;
            }

            var refGo = reference as GameObject ?? (reference as Component)?.gameObject;
            if (refGo != null)
            {
                rec.ReferenceInstanceId = refGo.GetInstanceID();
                rec.ReferencePath = GameObjectFinder.GetPath(refGo);
            }
            else
            {
                rec.ReferenceUnresolved = true;
            }
        }

        private static void OnUndoRedoPerformed()
        {
            if (_recording)
                _undoRedoDuringRecording = true;
        }

        // ===== Buffer =====

        private static bool AddRecord(MacroRecord rec)
        {
            // Structural events funnel here; a filtered REST change still updated the catalog
            // in its Handle* (paths/aliveness must stay fresh) but leaves no record.
            if (SkipRestChange)
            {
                _restFilteredCount++;
                return false;
            }
            if (!_recording || _records.Count >= MaxRecords)
                return false;

            rec.TsUtc = DateTime.UtcNow.ToString("o");
            _records.Add(rec);

            if (_records.Count >= MaxRecords)
            {
                StopInternal("buffer_full");
                SkillsLogger.LogWarning($"Macro recording buffer full ({MaxRecords} records) — recording auto-stopped.");
            }
            return true;
        }

        private static void CountUnsupported(string kindKey)
        {
            if (!_recording || SkipRestChange)
                return;
            _unsupportedCounts.TryGetValue(kindKey, out var n);
            _unsupportedCounts[kindKey] = n + 1;
        }

        /// <summary>
        /// Removes one record from a STOPPED session's buffer (panel timeline "delete this
        /// operation"). Only allowed while not recording — mutating the buffer under the live
        /// event stream would race the append path. The debounce indices (_propertyIndex /
        /// _reorderIndex) are only consulted while recording, but any entry still pointing at
        /// the removed record is dropped so the maps never dangle. Returns false when no stopped
        /// session exists or the index is out of range.
        ///
        /// Referential fallout (a removed Create still referenced by a later step, ...) needs no
        /// special handling here: BuildBatchSteps re-derives everything from the surviving
        /// _records each export and already degrades unresolved references to a warning + omit.
        /// The catalog is intentionally left untouched — an object's CreatedDuringRecording flag
        /// must survive so export keeps treating it as recording-created (dropping it would make
        /// export mistake it for a pre-existing object and emit a path step invalid on replay).
        /// </summary>
        internal static bool RemoveRecord(int index)
        {
            if (_recording || !_hasStoppedSession)
                return false;
            if (index < 0 || index >= _records.Count)
                return false;

            var removed = _records[index];
            _records.RemoveAt(index);

            DropIndexEntriesFor(_propertyIndex, removed);
            DropIndexEntriesFor(_reorderIndex, removed);

            _timelineRevision++;
            SkillsLogger.Log($"Macro record removed (index {index}): {removed.Kind} '{removed.SubjectName}'; {_records.Count} record(s) left");
            return true;
        }

        private static void DropIndexEntriesFor(Dictionary<string, MacroRecord> index, MacroRecord removed)
        {
            List<string> stale = null;
            foreach (var kv in index)
            {
                if (ReferenceEquals(kv.Value, removed))
                    (stale ?? (stale = new List<string>())).Add(kv.Key);
            }
            if (stale != null)
                foreach (var key in stale)
                    index.Remove(key);
        }

        // ===== Invertibility classification (single truth shared by export & panel) =====

        /// <summary>
        /// Static per-record inversion verdict: what CAN this record map to, ignoring
        /// export-time global context (ephemeral objects, $ref/locator availability — those are
        /// only decidable while exporting). Export consumes the ReasonEn texts verbatim as its
        /// warnings; the panel timeline shows State live while recording.
        /// </summary>
        internal static MacroInvertibilityInfo ClassifyRecord(MacroRecord rec)
        {
            switch (rec.Kind)
            {
                case RecordKind.Create:
                    return ClassifyCreate(rec);

                case RecordKind.Destroy:
                    if (string.IsNullOrEmpty(rec.SubjectPath) && string.IsNullOrEmpty(rec.SubjectName))
                        return MacroInvertibilityInfo.Unsupported("destroy_no_identity",
                            "A destroyed object could not be identified from the catalog; delete step omitted.");
                    return MacroInvertibilityInfo.Ok;

                case RecordKind.Reparent:
                    // Statically always invertible; failures (new parent destroyed within the
                    // recording, unresolvable locator) depend on export-time global context.
                    return MacroInvertibilityInfo.Ok;

                case RecordKind.Structure:
                {
                    bool hasAdd = rec.AddedComponents != null && rec.AddedComponents.Count > 0;
                    bool hasRemove = rec.RemovedComponents != null && rec.RemovedComponents.Count > 0;
                    if (!hasRemove)
                        return MacroInvertibilityInfo.Ok;

                    // Removal is now inverted into component_remove for the unambiguous subset
                    // (each removed type with no surviving same-type instance on the object).
                    // A residual same-type instance makes that removal unaddressable by type
                    // name — those types stay uninverted.
                    var residue = rec.RemovedComponentsWithResidue;
                    bool hasResidue = residue != null && residue.Count > 0;
                    if (!hasResidue)
                        return MacroInvertibilityInfo.Ok; // add (if any) + full removal both invert

                    bool hasSafeRemoval = false;
                    foreach (var typeName in rec.RemovedComponents)
                    {
                        if (!residue.Contains(typeName))
                        {
                            hasSafeRemoval = true;
                            break;
                        }
                    }

                    var reason = $"Component removal on '{rec.SubjectName}' is ambiguous for {string.Join(", ", residue)} "
                        + "(another instance of the same type remains, so a type name cannot address the removed one); "
                        + "those removals were not inverted.";
                    return (hasAdd || hasSafeRemoval)
                        ? MacroInvertibilityInfo.Partial("component_removal", reason)
                        : MacroInvertibilityInfo.Unsupported("component_removal", reason);
                }

                case RecordKind.Reorder:
                    // Statically always invertible; a container destroyed within the recording is
                    // caught at export time (ephemeral / unresolvable), like reparent.
                    return MacroInvertibilityInfo.Ok;

                case RecordKind.Property:
                    return ClassifyProperty(rec);

                default:
                    return MacroInvertibilityInfo.Ok;
            }
        }

        private static MacroInvertibilityInfo ClassifyCreate(MacroRecord rec)
        {
            var go = UnityObjectIdUtility.ObjectIdToObject(rec.InstanceId) as GameObject;
            if (go == null)
                return MacroInvertibilityInfo.Unsupported("create_unresolvable",
                    $"Create of '{rec.SubjectName}' could not be inverted: object no longer resolvable.");

            if (_catalog.TryGetValue(rec.InstanceId, out var entry) && !string.IsNullOrEmpty(entry.PrefabAssetPath))
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(entry.PrefabAssetPath) == null)
                    return MacroInvertibilityInfo.Unsupported("prefab_missing",
                        $"Prefab asset '{entry.PrefabAssetPath}' (instance '{go.name}') does not exist in this project; "
                        + "the prefab_instantiate step will fail until the asset is restored.");
                return MacroInvertibilityInfo.Ok;
            }

            if (PrefabUtility.IsPartOfPrefabInstance(go) && PrefabUtility.GetNearestPrefabInstanceRoot(go) != go)
                return MacroInvertibilityInfo.Unsupported("prefab_nonroot",
                    $"'{go.name}' is a non-root part of a prefab instance; its create step was omitted.");

            if (go.transform is RectTransform)
                return MacroInvertibilityInfo.Partial("rect_transform",
                    $"'{go.name}' uses a RectTransform; it is recreated as a plain GameObject (RectTransform cannot be added via component_add).");

            return MacroInvertibilityInfo.Ok;
        }

        private static MacroInvertibilityInfo ClassifyProperty(MacroRecord rec)
        {
            if (rec.ComponentTypeName == null)
            {
                if (rec.PropertyPath == "m_Name" || rec.PropertyPath == "m_IsActive")
                    return MacroInvertibilityInfo.Ok;
                return MacroInvertibilityInfo.Unsupported("go_property",
                    $"GameObject-level property '{rec.PropertyPath}' on '{rec.SubjectName}' has no inverse skill in v1; step omitted.");
            }
            if (rec.ReferenceUnresolved)
                return MacroInvertibilityInfo.Unsupported("scene_ref_unresolved",
                    $"Property '{rec.PropertyPath}' on '{rec.SubjectName}' references a scene object that could not be resolved; step omitted.");
            return MacroInvertibilityInfo.Ok;
        }

        private static MacroInvertibilityInfo ClassifyRecordCached(MacroRecord rec)
            => rec.CachedInvertibility ?? (rec.CachedInvertibility = ClassifyRecord(rec));

        // ===== Panel timeline (polled by MacroRecorderWindow on the main thread) =====

        /// <summary>
        /// Incremental timeline pull: items with Index &gt; sinceIndex. Records only append
        /// within a session (debounce merges update earlier records in place without
        /// re-emitting), so (SessionStartedUtc, Index) is a stable incremental cursor.
        /// </summary>
        internal static MacroTimelineSnapshot GetTimelineSnapshot(int sinceIndex)
        {
            bool hasSession = _recording || _hasStoppedSession;
            var snap = new MacroTimelineSnapshot
            {
                Recording = _recording,
                HasStoppedSession = _hasStoppedSession,
                SessionStartedUtc = hasSession ? _startedUtc.ToString("o") : null,
                StoppedReason = _hasStoppedSession ? _stoppedReason : null,
                InterruptedByReload = SessionState.GetString(SessionKeyInterrupted, "0") == "1",
                UndoRedoDetected = hasSession && _undoRedoDuringRecording,
                Total = hasSession ? _records.Count : 0,
                Revision = _timelineRevision,
                Items = new List<MacroTimelineItem>()
            };
            if (!hasSession)
                return snap;

            for (int i = sinceIndex + 1; i < _records.Count; i++)
            {
                var rec = _records[i];
                var inv = ClassifyRecordCached(rec);
                snap.Items.Add(new MacroTimelineItem
                {
                    Index = i,
                    TsUtc = rec.TsUtc,
                    KindKey = rec.Kind.ToString().ToLowerInvariant(),
                    SubjectName = rec.SubjectName,
                    Detail = BuildTimelineDetail(rec),
                    State = inv.State,
                    ReasonKey = inv.ReasonKey,
                    ReasonEn = inv.ReasonEn
                });
            }
            return snap;
        }

        private static string BuildTimelineDetail(MacroRecord rec)
        {
            switch (rec.Kind)
            {
                case RecordKind.Create:
                    if (_catalog.TryGetValue(rec.InstanceId, out var entry) && !string.IsNullOrEmpty(entry.PrefabAssetPath))
                        return "prefab: " + entry.PrefabAssetPath;
                    return rec.SubjectPath;

                case RecordKind.Destroy:
                    return rec.SubjectPath;

                case RecordKind.Reparent:
                    return "→ " + (string.IsNullOrEmpty(rec.NewParentPath) ? "(scene root)" : rec.NewParentPath);

                case RecordKind.Structure:
                {
                    var parts = new List<string>();
                    if (rec.AddedComponents != null)
                        foreach (var t in rec.AddedComponents) parts.Add("+" + t);
                    if (rec.RemovedComponents != null)
                        foreach (var t in rec.RemovedComponents) parts.Add("-" + t);
                    return string.Join(" ", parts);
                }

                case RecordKind.Property:
                {
                    string target = rec.ComponentTypeName ?? "GameObject";
                    string value = rec.HasObjectReference
                        ? (rec.ReferenceAssetPath ?? rec.ReferencePath ?? "(reference)")
                        : rec.ValueString;
                    return $"{target}.{rec.PropertyPath} = {value}";
                }

                case RecordKind.Reorder:
                    return rec.InstanceId != 0
                        ? "children reordered: " + rec.SubjectPath
                        : "scene root order changed";

                default:
                    return rec.SubjectPath;
            }
        }

        /// <summary>
        /// Live coverage counters for the panel bar. Unsupported-kind events that never made it
        /// into the buffer (asset edits, prefab apply/revert, ...) count as unsupported too.
        /// </summary>
        internal static MacroCoverageSummary GetCoverageSummary()
        {
            var sum = new MacroCoverageSummary();
            if (!_recording && !_hasStoppedSession)
                return sum;

            foreach (var rec in _records)
            {
                switch (ClassifyRecordCached(rec).State)
                {
                    case MacroInvertibility.Ok: sum.Ok++; break;
                    case MacroInvertibility.Partial: sum.Partial++; break;
                    default: sum.Unsupported++; break;
                }
            }
            sum.Total = _records.Count;
            foreach (var kv in _unsupportedCounts)
            {
                sum.Unsupported += kv.Value;
                sum.Total += kv.Value;
            }
            return sum;
        }

        // ===== Export: invert records into a /skills/batch step sequence =====

        public static object Export(string format)
        {
            if (_recording)
                return new { error = "Recording is still active. Call macro_record_stop before exporting.", recording = true };
            if (!_hasStoppedSession)
                return new { error = "No stopped macro recording to export. Record with macro_record_start / macro_record_stop first." };

            format = string.IsNullOrEmpty(format) ? "batch" : format.ToLowerInvariant();
            if (format != "batch")
                return new { error = $"Unknown format '{format}'. Supported: batch" };

            var steps = BuildBatchSteps(out var warnings, out var replayable);

            return new
            {
                success = true,
                format = "batch",
                replayable,
                stepCount = steps.Count,
                recordCount = _records.Count,
                steps,
                warnings,
                note = _note,
                startedAtUtc = _startedUtc.ToString("o"),
                stoppedReason = _stoppedReason
            };
        }

        /// <summary>
        /// Panel / file-export entry: same product as macro_export, minus the REST envelope.
        /// Fails (with the same message texts as macro_export) unless a stopped session exists.
        /// </summary>
        internal static bool TryGetStoppedSessionExport(out JArray steps, out List<string> warnings,
            out bool replayable, out string error)
        {
            steps = null;
            warnings = null;
            replayable = false;
            if (_recording)
            {
                error = "Recording is still active. Call macro_record_stop before exporting.";
                return false;
            }
            if (!_hasStoppedSession)
            {
                error = "No stopped macro recording to export. Record with macro_record_start / macro_record_stop first.";
                return false;
            }
            steps = BuildBatchSteps(out warnings, out replayable);
            error = null;
            return true;
        }

        /// <summary>
        /// Shared inversion core behind macro_export and the panel's export/copy actions.
        /// Callers must ensure a stopped session exists.
        /// </summary>
        private static JArray BuildBatchSteps(out List<string> warnings, out bool replayable)
        {
            var steps = new JArray();
            warnings = new List<string>();
            bool hasUnsupported = false;
            // instanceId of an object created during the recording → index of its create step,
            // so later steps can target it via {"$ref":"$N.instanceId"} (batch v2).
            var createStepIndex = new Dictionary<int, int>();

            // Objects created AND destroyed within the recording: net effect zero — omit them
            // and every record that touches them.
            var ephemeral = new HashSet<int>();
            foreach (var e in _catalog.Values)
            {
                if (e.CreatedDuringRecording && !e.Alive)
                    ephemeral.Add(e.InstanceId);
            }

            // Components that a later Structure record will add itself — the create-step
            // expansion must not emit a duplicate component_add for them.
            var structureAdded = new Dictionary<int, HashSet<string>>();
            foreach (var rec in _records)
            {
                if (rec.Kind != RecordKind.Structure || rec.AddedComponents == null)
                    continue;
                if (!structureAdded.TryGetValue(rec.InstanceId, out var set))
                    structureAdded[rec.InstanceId] = set = new HashSet<string>();
                foreach (var t in rec.AddedComponents)
                    set.Add(t);
            }

            int ephemeralSkipped = 0;
            var ephemeralNames = new SortedSet<string>();
            // Reorder records are emitted AFTER every other step (net final state): replayed
            // creates append at the container's end, so sibling indices are only meaningful
            // once all objects exist — and forward $refs are impossible anyway.
            var pendingReorders = new List<MacroRecord>();

            foreach (var rec in _records)
            {
                if (ephemeral.Contains(rec.InstanceId))
                {
                    ephemeralSkipped++;
                    if (!string.IsNullOrEmpty(rec.SubjectName))
                        ephemeralNames.Add(rec.SubjectName);
                    continue;
                }

                switch (rec.Kind)
                {
                    case RecordKind.Create:
                        AppendCreateSteps(rec, steps, createStepIndex, structureAdded, warnings, ref hasUnsupported);
                        break;
                    case RecordKind.Destroy:
                        AppendDestroyStep(rec, steps, warnings, ref hasUnsupported);
                        break;
                    case RecordKind.Reparent:
                        AppendReparentStep(rec, steps, createStepIndex, ephemeral, warnings, ref hasUnsupported);
                        break;
                    case RecordKind.Structure:
                        AppendStructureSteps(rec, steps, createStepIndex, warnings, ref hasUnsupported);
                        break;
                    case RecordKind.Property:
                        AppendPropertyStep(rec, steps, createStepIndex, ephemeral, warnings, ref hasUnsupported);
                        break;
                    case RecordKind.Reorder:
                        pendingReorders.Add(rec);
                        break;
                }
            }

            foreach (var rec in pendingReorders)
                AppendReorderSteps(rec, steps, createStepIndex, warnings, ref hasUnsupported);

            if (ephemeralSkipped > 0)
            {
                warnings.Add($"Omitted {ephemeralSkipped} record(s) for object(s) created and destroyed within the recording "
                    + $"(net effect zero): {string.Join(", ", ephemeralNames)}");
            }
            foreach (var kv in _unsupportedCounts)
            {
                warnings.Add($"Recorded {kv.Value} change(s) of unsupported kind '{kv.Key}' — not invertible in v1.");
                hasUnsupported = true;
            }
            if (_undoRedoDuringRecording)
            {
                warnings.Add("Undo/Redo was performed during the recording; the export reflects the net event stream, "
                    + "which may not replay the exact editing history.");
                hasUnsupported = true;
            }

            replayable = !hasUnsupported;
            return steps;
        }

        private static JObject MakeStep(string skill, JObject args) =>
            new JObject { ["skill"] = skill, ["args"] = args };

        private static JObject MakeRef(int stepIndex) =>
            new JObject { ["$ref"] = "$" + stepIndex + ".instanceId" };

        /// <summary>
        /// Locator args for a subject: objects created during the recording are addressed via
        /// $ref to their create step (replay-time instanceIds differ); pre-existing objects via
        /// the record-time path, which is replay-time consistent because earlier steps replay
        /// the same history that produced that path.
        /// </summary>
        private static bool TryAddSubjectLocator(JObject args, string instanceIdParam, string pathParam,
            int instanceId, string recordTimePath, Dictionary<int, int> createStepIndex, List<string> warnings, string context)
        {
            if (createStepIndex.TryGetValue(instanceId, out var stepIdx))
            {
                args[instanceIdParam] = MakeRef(stepIdx);
                return true;
            }
            if (_catalog.TryGetValue(instanceId, out var entry) && entry.CreatedDuringRecording)
            {
                // Descendant of a prefab instance instantiated during the recording: it has no
                // create step of its own, but the root's prefab_instantiate step rebuilds the
                // whole subtree, so the record-time path is replay-time valid.
                if (entry.PrefabInstanceRootId != 0
                    && createStepIndex.ContainsKey(entry.PrefabInstanceRootId)
                    && !string.IsNullOrEmpty(recordTimePath))
                {
                    args[pathParam] = recordTimePath;
                    return true;
                }
                // Created during the recording but its create step was skipped (create not
                // invertible, root step omitted, ...) — no reliable replay-time identity exists.
                warnings.Add($"{context}: target '{entry.Name}' was created during the recording but has no create step; step omitted.");
                return false;
            }
            if (string.IsNullOrEmpty(recordTimePath))
            {
                warnings.Add($"{context}: no path known for the target; step omitted.");
                return false;
            }
            args[pathParam] = recordTimePath;
            return true;
        }

        private static void AppendCreateSteps(MacroRecord rec, JArray steps, Dictionary<int, int> createStepIndex,
            Dictionary<int, HashSet<string>> structureAdded, List<string> warnings, ref bool hasUnsupported)
        {
            // Static verdict first (ClassifyRecord is the shared truth with the panel); its
            // reason text is the warning. Unsupported creates emit no step — except
            // prefab_missing, whose (user-fixable) step is still emitted below.
            var inv = ClassifyRecord(rec);
            if (inv.State != MacroInvertibility.Ok)
            {
                warnings.Add(inv.ReasonEn);
                hasUnsupported = true;
                if (inv.State == MacroInvertibility.Unsupported && inv.ReasonKey != "prefab_missing")
                    return;
            }

            // Invert from the object's CURRENT state (it is alive, or it would be ephemeral;
            // unresolvable objects were rejected by the classification above).
            var go = UnityObjectIdUtility.ObjectIdToObject(rec.InstanceId) as GameObject;

            // Root of a prefab instance dragged in during the recording → a single
            // prefab_instantiate step rebuilds the whole subtree, components included (no
            // component_add expansion); override edits on top arrive as their own
            // Structure/Property records and replay against the instantiated hierarchy.
            if (_catalog.TryGetValue(rec.InstanceId, out var catEntry) && !string.IsNullOrEmpty(catEntry.PrefabAssetPath))
            {
                AppendPrefabInstantiateStep(rec, go, catEntry.PrefabAssetPath, steps, createStepIndex);
                return;
            }

            string primitive = DetectPrimitive(go);
            var args = new JObject { ["name"] = go.name };
            if (primitive != null)
                args["primitiveType"] = primitive;
            var lp = go.transform.localPosition;
            args["x"] = lp.x;
            args["y"] = lp.y;
            args["z"] = lp.z;

            var parentT = go.transform.parent;
            if (parentT != null)
            {
                int parentId = parentT.gameObject.GetInstanceID();
                if (createStepIndex.TryGetValue(parentId, out var parentStep))
                {
                    args["parentInstanceId"] = MakeRef(parentStep);
                }
                else if (_catalog.TryGetValue(parentId, out var pe) && pe.CreatedDuringRecording)
                {
                    // Parent's create step comes later — reaching this nesting required a
                    // reparent, whose own record restores the relation.
                }
                else
                {
                    args["parentPath"] = GameObjectFinder.GetPath(parentT.gameObject);
                }
            }

            steps.Add(MakeStep("gameobject_create", args));
            createStepIndex[rec.InstanceId] = steps.Count - 1;

            // (A RectTransform subject already emitted its Partial warning above; the object is
            // still recreated as a plain GameObject.)

            // Non-default components present now and NOT covered by a later Structure record
            // (those emit their own component_add) were there at creation (duplicate/paste) —
            // recreate them.
            var defaults = DefaultComponentSet(primitive);
            structureAdded.TryGetValue(rec.InstanceId, out var laterAdded);
            var defaultSeen = new HashSet<string>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                    continue;
                var typeName = c.GetType().Name;
                if (defaults.Contains(typeName) && defaultSeen.Add(typeName))
                    continue; // first occurrence of a default component
                if (laterAdded != null && laterAdded.Contains(typeName))
                    continue;

                var addArgs = new JObject
                {
                    ["instanceId"] = MakeRef(createStepIndex[rec.InstanceId]),
                    ["componentType"] = typeName
                };
                steps.Add(MakeStep("component_add", addArgs));
            }
        }

        /// <summary>
        /// Inverts the create record of a dragged-in prefab instance root into a
        /// prefab_instantiate step (skill signature: prefabPath, x/y/z → localPosition, name,
        /// parentInstanceId/parentPath; outputs include instanceId, so later steps $ref it
        /// exactly like a gameobject_create step). Whether the prefab asset still exists was
        /// already checked by ClassifyCreate (prefab_missing) in the caller.
        /// </summary>
        private static void AppendPrefabInstantiateStep(MacroRecord rec, GameObject go, string assetPath,
            JArray steps, Dictionary<int, int> createStepIndex)
        {
            var args = new JObject
            {
                ["prefabPath"] = assetPath,
                // Name at create time, not export time: later rename records replay on top in
                // recorded order, keeping record-time paths of descendants valid throughout.
                ["name"] = rec.SubjectName
            };
            var lp = go.transform.localPosition;
            args["x"] = lp.x;
            args["y"] = lp.y;
            args["z"] = lp.z;

            var parentT = go.transform.parent;
            if (parentT != null)
            {
                int parentId = parentT.gameObject.GetInstanceID();
                if (createStepIndex.TryGetValue(parentId, out var parentStep))
                {
                    args["parentInstanceId"] = MakeRef(parentStep);
                }
                else if (_catalog.TryGetValue(parentId, out var pe) && pe.CreatedDuringRecording)
                {
                    // Parent's create step comes later — reaching this nesting required a
                    // reparent, whose own record restores the relation.
                }
                else
                {
                    args["parentPath"] = GameObjectFinder.GetPath(parentT.gameObject);
                }
            }

            steps.Add(MakeStep("prefab_instantiate", args));
            createStepIndex[rec.InstanceId] = steps.Count - 1;
        }

        private static void AppendDestroyStep(MacroRecord rec, JArray steps, List<string> warnings, ref bool hasUnsupported)
        {
            var inv = ClassifyRecord(rec); // destroy_no_identity when neither path nor name is known
            if (inv.State == MacroInvertibility.Unsupported)
            {
                warnings.Add(inv.ReasonEn);
                hasUnsupported = true;
                return;
            }

            var args = new JObject();
            if (!string.IsNullOrEmpty(rec.SubjectPath))
                args["path"] = rec.SubjectPath;
            else
                args["name"] = rec.SubjectName;
            steps.Add(MakeStep("gameobject_delete", args));
        }

        private static void AppendReparentStep(MacroRecord rec, JArray steps, Dictionary<int, int> createStepIndex,
            HashSet<int> ephemeral, List<string> warnings, ref bool hasUnsupported)
        {
            if (rec.NewParentInstanceId != 0 && ephemeral.Contains(rec.NewParentInstanceId))
            {
                warnings.Add($"Reparent of '{rec.SubjectName}' targeted an object that was later destroyed within the recording; step omitted.");
                hasUnsupported = true;
                return;
            }

            var args = new JObject();
            if (!TryAddSubjectLocator(args, "childInstanceId", "childPath", rec.InstanceId, rec.SubjectPath,
                    createStepIndex, warnings, $"Reparent of '{rec.SubjectName}'"))
            {
                hasUnsupported = true;
                return;
            }

            if (rec.NewParentInstanceId != 0)
            {
                if (createStepIndex.TryGetValue(rec.NewParentInstanceId, out var parentStep))
                {
                    args["parentInstanceId"] = MakeRef(parentStep);
                }
                else if (!string.IsNullOrEmpty(rec.NewParentPath))
                {
                    args["parentPath"] = rec.NewParentPath;
                }
                else
                {
                    warnings.Add($"Reparent of '{rec.SubjectName}': new parent could not be identified; step omitted.");
                    hasUnsupported = true;
                    return;
                }
            }
            // NewParentInstanceId == 0 → moved to scene root: gameobject_set_parent with no
            // parent args clears the parent.

            steps.Add(MakeStep("gameobject_set_parent", args));
        }

        private static void AppendStructureSteps(MacroRecord rec, JArray steps, Dictionary<int, int> createStepIndex,
            List<string> warnings, ref bool hasUnsupported)
        {
            if (rec.AddedComponents != null)
            {
                foreach (var typeName in rec.AddedComponents)
                {
                    var args = new JObject();
                    if (!TryAddSubjectLocator(args, "instanceId", "path", rec.InstanceId, rec.SubjectPath,
                            createStepIndex, warnings, $"component_add {typeName}"))
                    {
                        hasUnsupported = true;
                        continue;
                    }
                    args["componentType"] = typeName;
                    steps.Add(MakeStep("component_add", args));
                }
            }

            if (rec.RemovedComponents != null && rec.RemovedComponents.Count > 0)
            {
                // Unambiguous removals (no surviving same-type instance) invert into
                // component_remove; ambiguous types only carry the classification warning.
                // Replay caveat: a removed component that others RequireComponent stays
                // guarded by the component_remove skill itself (it fails with "remove
                // dependents first" rather than removing the wrong thing).
                var residue = rec.RemovedComponentsWithResidue;
                foreach (var typeName in rec.RemovedComponents)
                {
                    if (residue != null && residue.Contains(typeName))
                        continue;

                    var args = new JObject();
                    if (!TryAddSubjectLocator(args, "instanceId", "path", rec.InstanceId, rec.SubjectPath,
                            createStepIndex, warnings, $"component_remove {typeName}"))
                    {
                        hasUnsupported = true;
                        continue;
                    }
                    args["componentType"] = typeName;
                    steps.Add(MakeStep("component_remove", args));
                }

                if (residue != null && residue.Count > 0)
                {
                    // component_removal ambiguity — the shared classification carries the
                    // identical warning text for the panel and this export.
                    var inv = ClassifyRecord(rec);
                    warnings.Add(inv.ReasonEn);
                    hasUnsupported = true;
                }
            }
        }

        /// <summary>
        /// Inverts one Reorder record into gameobject_set_sibling_index steps — one per live,
        /// addressable child of the container, at the child's CURRENT sibling index (net final
        /// state, same "invert from current state" contract as creates). Callers emit these
        /// after every other step so all referenced objects already exist at replay time.
        /// A container destroyed within the recording is skipped silently: its children are
        /// gone with it, so their order is moot.
        /// </summary>
        private static void AppendReorderSteps(MacroRecord rec, JArray steps, Dictionary<int, int> createStepIndex,
            List<string> warnings, ref bool hasUnsupported)
        {
            if (rec.InstanceId != 0)
            {
                var parent = UnityObjectIdUtility.ObjectIdToObject(rec.InstanceId) as GameObject;
                if (parent == null)
                    return; // destroyed later in the session — order is moot

                var t = parent.transform;
                for (int i = 0; i < t.childCount; i++)
                {
                    EmitSiblingIndexStep(t.GetChild(i).gameObject, steps, createStepIndex, warnings,
                        ref hasUnsupported, $"Reorder under '{rec.SubjectName}'");
                }
                return;
            }

            var scene = FindSceneByHandle(rec.SceneHandle);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                warnings.Add($"Root order change in scene '{rec.SubjectName}' could not be inverted: the scene is no longer loaded.");
                hasUnsupported = true;
                return;
            }
            foreach (var root in scene.GetRootGameObjects())
                EmitSiblingIndexStep(root, steps, createStepIndex, warnings, ref hasUnsupported, "Scene root reorder");
        }

        private static void EmitSiblingIndexStep(GameObject child, JArray steps, Dictionary<int, int> createStepIndex,
            List<string> warnings, ref bool hasUnsupported, string context)
        {
            // Ignored objects cannot be addressed at replay time; their slots still count in the
            // other children's absolute indices, which gameobject_set_sibling_index clamps into
            // range when such an object is absent on replay.
            if (child == null || IsIgnored(child))
                return;

            var args = new JObject();
            if (!TryAddSubjectLocator(args, "instanceId", "path", child.GetInstanceID(),
                    GameObjectFinder.GetPath(child), createStepIndex, warnings, context))
            {
                hasUnsupported = true;
                return;
            }
            args["index"] = child.transform.GetSiblingIndex();
            steps.Add(MakeStep("gameobject_set_sibling_index", args));
        }

        private static Scene FindSceneByHandle(int handle)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.handle == handle)
                    return scene;
            }
            return default;
        }

        private static void AppendPropertyStep(MacroRecord rec, JArray steps, Dictionary<int, int> createStepIndex,
            HashSet<int> ephemeral, List<string> warnings, ref bool hasUnsupported)
        {
            // GameObject-level properties map to dedicated high-level skills.
            if (rec.ComponentTypeName == null)
            {
                AppendGameObjectPropertyStep(rec, steps, createStepIndex, warnings, ref hasUnsupported);
                return;
            }

            if (rec.ReferenceUnresolved)
            {
                var inv = ClassifyRecord(rec); // scene_ref_unresolved
                warnings.Add(inv.ReasonEn);
                hasUnsupported = true;
                return;
            }
            if (rec.HasObjectReference && rec.ReferenceInstanceId != 0 && ephemeral.Contains(rec.ReferenceInstanceId))
            {
                warnings.Add($"Property '{rec.PropertyPath}' on '{rec.SubjectName}' referenced an object that was later destroyed within the recording; step omitted.");
                hasUnsupported = true;
                return;
            }

            var args = new JObject();
            if (!TryAddSubjectLocator(args, "instanceId", "path", rec.InstanceId, rec.SubjectPath,
                    createStepIndex, warnings, $"Property '{rec.PropertyPath}' on '{rec.SubjectName}'"))
            {
                hasUnsupported = true;
                return;
            }

            args["componentType"] = rec.ComponentTypeName;
            args["propertyPath"] = rec.PropertyPath;

            if (rec.HasObjectReference)
            {
                if (!string.IsNullOrEmpty(rec.ReferenceAssetPath))
                {
                    args["assetPath"] = rec.ReferenceAssetPath;
                }
                else if (createStepIndex.TryGetValue(rec.ReferenceInstanceId, out var refStep))
                {
                    args["referenceInstanceId"] = MakeRef(refStep);
                }
                else
                {
                    args["referencePath"] = rec.ReferencePath;
                }
                if (!string.IsNullOrEmpty(rec.ReferenceObjectType))
                    args["objectType"] = rec.ReferenceObjectType;
            }
            else
            {
                args["value"] = rec.ValueString ?? "null";
            }

            steps.Add(MakeStep("component_set_serialized_property", args));
        }

        private static void AppendGameObjectPropertyStep(MacroRecord rec, JArray steps, Dictionary<int, int> createStepIndex,
            List<string> warnings, ref bool hasUnsupported)
        {
            switch (rec.PropertyPath)
            {
                case "m_Name":
                {
                    var args = new JObject();
                    // SubjectPath was captured AFTER the rename applied; the replay-time path
                    // ends with the previous name, so rebuild it from the parent path.
                    string pathBefore = ReplaceLastPathSegment(rec.SubjectPath, rec.PreviousValueString);
                    if (!TryAddSubjectLocator(args, "instanceId", "path", rec.InstanceId, pathBefore,
                            createStepIndex, warnings, $"Rename to '{rec.ValueString}'"))
                    {
                        hasUnsupported = true;
                        return;
                    }
                    args["newName"] = rec.ValueString;
                    steps.Add(MakeStep("gameobject_rename", args));
                    return;
                }
                case "m_IsActive":
                {
                    var args = new JObject();
                    if (!TryAddSubjectLocator(args, "instanceId", "path", rec.InstanceId, rec.SubjectPath,
                            createStepIndex, warnings, $"SetActive on '{rec.SubjectName}'"))
                    {
                        hasUnsupported = true;
                        return;
                    }
                    args["active"] = rec.ValueString == "1" || string.Equals(rec.ValueString, "true", StringComparison.OrdinalIgnoreCase);
                    steps.Add(MakeStep("gameobject_set_active", args));
                    return;
                }
                default:
                {
                    var inv = ClassifyRecord(rec); // go_property — no inverse skill in v1
                    warnings.Add(inv.ReasonEn);
                    hasUnsupported = true;
                    return;
                }
            }
        }

        private static string ReplaceLastPathSegment(string path, string newSegment)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(newSegment))
                return null;
            int slash = path.LastIndexOf('/');
            return slash < 0 ? newSegment : path.Substring(0, slash + 1) + newSegment;
        }

        /// <summary>
        /// Detects whether the object is (still) shaped like a Unity primitive: its MeshFilter
        /// must hold one of the built-in meshes (Cube/Sphere/Capsule/Cylinder/Plane/Quad) from
        /// the default resources. Returns the PrimitiveType name, or null for "plain object".
        /// </summary>
        private static string DetectPrimitive(GameObject go)
        {
            var meshFilter = go.GetComponent<MeshFilter>();
            var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (mesh == null || go.GetComponent<MeshRenderer>() == null)
                return null;
            if (AssetDatabase.GetAssetPath(mesh) != "Library/unity default resources")
                return null;

            switch (mesh.name)
            {
                case "Cube":
                case "Sphere":
                case "Capsule":
                case "Cylinder":
                case "Plane":
                case "Quad":
                    return mesh.name;
                default:
                    return null;
            }
        }

        private static HashSet<string> DefaultComponentSet(string primitive)
        {
            // RectTransform is exempted everywhere: it replaces Transform and can never be
            // re-added via component_add (a dedicated warning covers the approximation).
            var set = new HashSet<string> { "Transform", "RectTransform" };
            if (primitive == null)
                return set;

            set.Add("MeshFilter");
            set.Add("MeshRenderer");
            switch (primitive)
            {
                case "Cube": set.Add("BoxCollider"); break;
                case "Sphere": set.Add("SphereCollider"); break;
                case "Capsule":
                case "Cylinder": set.Add("CapsuleCollider"); break;
                case "Plane":
                case "Quad": set.Add("MeshCollider"); break;
            }
            return set;
        }
    }
}
