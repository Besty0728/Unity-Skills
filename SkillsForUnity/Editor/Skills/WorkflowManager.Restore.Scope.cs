using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySkills.Internal;

namespace UnitySkills
{
    public static partial class WorkflowManager
    {
        private const string ScopeEntityPrefix = "entity:";
        private const int MaxScopeTargetNames = 10;

        private enum RestoreScopeKind
        {
            Entity,
            Asset,
            Bare
        }

        private sealed class RestoreScopeItem
        {
            public string Text;
            public RestoreScopeKind Kind;
            // The entity id of an entity item.
            public string EntityId;
            // The item without its trailing "/": an asset path, an object name or a hierarchy path.
            public string Value;
            // A trailing "/": the folder and everything under it, or the children of an object.
            public bool Subtree;
        }

        /// <summary>The parsed scope of a partial restore: the items the caller named, and their text joined by "," for the record.</summary>
        private sealed class RestoreScope
        {
            public string Raw;
            public List<RestoreScopeItem> Items = new List<RestoreScopeItem>();
        }

        /// <summary>A scene chain's place in the hierarchy: the path it anchors at, and every entity id it covers.</summary>
        private sealed class ChainAnchor
        {
            public string Path;
            public readonly HashSet<string> EntityIds = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// The objects a restore's Created items remove, with the display each one is listed under. A GameObject covers itself, its
        /// descendants and their components; a Component covers only itself.
        /// </summary>
        private sealed class DoomedObjects
        {
            public readonly HashSet<GameObject> GameObjects = new HashSet<GameObject>();
            public readonly Dictionary<string, string> ByGlobalId = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> ByEntityId = new Dictionary<string, string>(StringComparer.Ordinal);

            public bool IsEmpty => ByGlobalId.Count == 0 && ByEntityId.Count == 0;
        }

        /// <summary>
        /// Parses the scope of a restore: comma-separated items, each trimmed, empty items dropped. Null, blank or all-empty is no scope,
        /// which parses to null. An entity item needs an id.
        /// </summary>
        private static bool TryParseScope(string scope, out RestoreScope parsed, out string error)
        {
            parsed = null;
            error = null;
            if (string.IsNullOrWhiteSpace(scope))
                return true;

            var result = new RestoreScope();
            foreach (string part in scope.Split(','))
            {
                string text = part.Trim();
                if (text.Length == 0)
                    continue;

                var item = new RestoreScopeItem { Text = text };
                if (text.StartsWith(ScopeEntityPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    item.Kind = RestoreScopeKind.Entity;
                    item.EntityId = text.Substring(ScopeEntityPrefix.Length).Trim();
                    if (item.EntityId.Length == 0)
                    {
                        error = $"scope '{text}' has no entity id; write entity:<id>";
                        return false;
                    }
                }
                else
                {
                    bool isAsset = text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                                   text.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
                    item.Kind = isAsset ? RestoreScopeKind.Asset : RestoreScopeKind.Bare;
                    item.Subtree = text.EndsWith("/", StringComparison.Ordinal);
                    item.Value = text.TrimEnd('/');
                }
                result.Items.Add(item);
            }

            if (result.Items.Count == 0)
                return true;
            result.Raw = string.Join(",", result.Items.Select(item => item.Text));
            parsed = result;
            return true;
        }

        /// <summary>
        /// The chains a scope keeps, in their original order: a chain is kept when any item selects it. Every item must select a chain;
        /// an item that selects none fails the scope, and the error names the targets the steps do cover.
        /// </summary>
        private static List<List<RestoreItem>> FilterChainsByScope(List<List<RestoreItem>> chains, RestoreScope scope,
            Dictionary<string, string> gidAliases, out string error)
        {
            error = null;
            var kept = new List<List<RestoreItem>>();
            var matched = new bool[scope.Items.Count];
            var targetNames = new List<string>();
            _restoreScopeAliases = gidAliases;
            try
            {
                foreach (var chain in chains)
                {
                    bool isSceneChain = !IsAssetChain(chain) && !IsSettingChain(chain);
                    var anchor = isSceneChain ? ResolveChainAnchor(chain) : null;
                    AddScopeTargetName(targetNames, ScopeTargetName(chain, anchor));

                    bool selected = false;
                    for (int i = 0; i < scope.Items.Count; i++)
                    {
                        if (!ScopeItemSelects(scope.Items[i], chain, anchor))
                            continue;
                        matched[i] = true;
                        selected = true;
                    }
                    if (selected)
                        kept.Add(chain);
                }
            }
            finally
            {
                _restoreScopeAliases = null;
                _sessionIdFallbackAllowed = true;
            }

            for (int i = 0; i < scope.Items.Count; i++)
            {
                if (matched[i])
                    continue;
                string targets = targetNames.Count == 0 ? "none" : string.Join(", ", targetNames);
                error = $"scope '{scope.Items[i].Text}' matches no target of the restored steps. Targets: {targets}";
                return null;
            }
            return kept;
        }

        private static bool IsAssetChain(List<RestoreItem> chain) => chain.Any(item => !string.IsNullOrEmpty(item.Snapshot.assetPath));

        private static bool IsSettingChain(List<RestoreItem> chain) =>
            chain.Any(item => item.Snapshot.type == SnapshotType.Setting && !string.IsNullOrEmpty(item.Snapshot.settingKey));

        /// <summary>
        /// Whether an item selects a chain. Asset chains match asset items, setting chains bare items (by key), and scene chains entity
        /// items and bare items (through the chain's anchor, which is null for the other kinds).
        /// </summary>
        private static bool ScopeItemSelects(RestoreScopeItem item, List<RestoreItem> chain, ChainAnchor anchor)
        {
            if (IsAssetChain(chain))
                return item.Kind == RestoreScopeKind.Asset && chain.Any(entry => AssetPathSelected(item, entry.Snapshot));
            if (IsSettingChain(chain))
                return item.Kind == RestoreScopeKind.Bare && chain.Any(entry => SettingKeySelected(item, entry.Snapshot.settingKey));
            if (anchor == null)
                return false;
            if (item.Kind == RestoreScopeKind.Entity)
                return anchor.EntityIds.Contains(item.EntityId);
            return item.Kind == RestoreScopeKind.Bare && anchor.Path != null && HierarchyPathSelected(item, anchor.Path);
        }

        private static bool AssetPathSelected(RestoreScopeItem item, ObjectSnapshot snapshot) =>
            PathSelected(item, snapshot.assetPath) || PathSelected(item, snapshot.previousAssetPath);

        private static bool PathSelected(RestoreScopeItem item, string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            return string.Equals(path, item.Value, StringComparison.OrdinalIgnoreCase) ||
                   item.Subtree && path.StartsWith(item.Value + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SettingKeySelected(RestoreScopeItem item, string settingKey)
        {
            if (string.IsNullOrEmpty(settingKey))
                return false;
            return settingKey == item.Text ||
                   item.Text.EndsWith(".", StringComparison.Ordinal) && settingKey.StartsWith(item.Text, StringComparison.Ordinal);
        }

        /// <summary>
        /// A bare item with a "/" names a hierarchy path: the anchor is that path, or with a trailing "/" lies under it. Without one it names
        /// an object: the anchor's last segment, or with a trailing "/" any segment, so an object also covers its children.
        /// </summary>
        private static bool HierarchyPathSelected(RestoreScopeItem item, string anchorPath)
        {
            if (item.Value.IndexOf('/') >= 0)
                return anchorPath == item.Value || item.Subtree && anchorPath.StartsWith(item.Value + "/", StringComparison.Ordinal);

            string[] segments = anchorPath.Split('/');
            return item.Subtree ? Array.IndexOf(segments, item.Value) >= 0 : segments[segments.Length - 1] == item.Value;
        }

        /// <summary>
        /// The anchor of a scene chain, read once per restore: the path of the GameObject its first resolvable snapshot names (a component
        /// anchors at its owner), and the entity ids of every item. Each item resolves in the session it was captured in.
        /// </summary>
        private static ChainAnchor ResolveChainAnchor(List<RestoreItem> chain)
        {
            var anchor = new ChainAnchor();
            foreach (var item in chain)
            {
                var snapshot = item.Snapshot;
                _sessionIdFallbackAllowed = IsCapturedInCurrentSession(item);
                AddAnchorEntityId(anchor, snapshot.entityId);
                AddAnchorEntityId(anchor, snapshot.parentGameObjectEntityId);
                if (anchor.Path != null)
                    continue;

                var obj = TryResolveObject(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId);
                GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go == null && !string.IsNullOrEmpty(snapshot.componentTypeName))
                    go = TryResolveObject(snapshot.parentGameObjectId, snapshot.parentGameObjectEntityId, snapshot.parentGameObjectInstanceId) as GameObject;
                if (go != null)
                {
                    anchor.Path = GameObjectFinder.GetPath(go);
                    AddAnchorEntityId(anchor, UnityObjectIdUtility.GetEntityId(go));
                }
            }

            anchor.Path = anchor.Path ?? DeletedHierarchyPath(chain);
            return anchor;
        }

        private static void AddAnchorEntityId(ChainAnchor anchor, string entityId)
        {
            if (!string.IsNullOrEmpty(entityId))
                anchor.EntityIds.Add(entityId);
        }

        /// <summary>
        /// The path of a chain whose objects no longer resolve: the GameObject its Deleted item recorded, under its live external parent when
        /// that parent exists. Failing that, the first object name the chain holds.
        /// </summary>
        private static string DeletedHierarchyPath(List<RestoreItem> chain)
        {
            var deleted = chain.FirstOrDefault(item => item.Snapshot.type == SnapshotType.Deleted && string.IsNullOrEmpty(item.Snapshot.componentTypeName));
            if (deleted != null)
            {
                _sessionIdFallbackAllowed = IsCapturedInCurrentSession(deleted);
                if (TryHydrate(deleted.Snapshot, out _) && deleted.Snapshot.gameObjectHierarchy.Count > 0)
                {
                    var root = deleted.Snapshot.gameObjectHierarchy[0];
                    var parent = TryResolveObject(root.externalParentGlobalObjectId, root.externalParentEntityId, root.externalParentInstanceId) as GameObject;
                    string parentPath = parent != null ? GameObjectFinder.GetPath(parent) + "/" : string.Empty;
                    return parentPath + root.name;
                }
            }
            return chain.Select(item => item.Snapshot.objectName).FirstOrDefault(name => !string.IsNullOrEmpty(name));
        }

        /// <summary>The name a chain is listed under in the error of an unmatched scope: its setting key, asset path or anchor path.</summary>
        private static string ScopeTargetName(List<RestoreItem> chain, ChainAnchor anchor)
        {
            if (IsSettingChain(chain))
                return chain.Select(item => item.Snapshot.settingKey).FirstOrDefault(key => !string.IsNullOrEmpty(key));
            if (IsAssetChain(chain))
                return chain.Select(item => item.Snapshot.assetPath).FirstOrDefault(path => !string.IsNullOrEmpty(path));
            return anchor?.Path;
        }

        private static void AddScopeTargetName(List<string> names, string name)
        {
            if (!string.IsNullOrEmpty(name) && names.Count < MaxScopeTargetNames && !names.Contains(name))
                names.Add(name);
        }

        /// <summary>
        /// The references a restore would leave dangling, for the preview only (best effort): a Modified scene object outside the restore
        /// that refers to an object the restore deletes. Each holder is judged by its newest snapshot. Null when nothing dangles.
        /// </summary>
        private static List<string> FindDanglingReferences(List<RestoreWork> runs, List<WorkflowTask> steps,
            Dictionary<string, string> gidAliases)
        {
            var restoredKeys = new HashSet<string>(runs.Where(run => run.TargetKey != null).Select(run => run.TargetKey), StringComparer.Ordinal);
            var judgedKeys = new HashSet<string>(StringComparer.Ordinal);
            var dangling = new List<string>();
            long firstSeq = steps.Min(step => step.seq);
            _restoreScopeAliases = gidAliases;
            try
            {
                var doomed = DoomedObjectsOf(runs);
                if (doomed == null)
                    return null;

                foreach (var task in History.tasks.Where(task => task.seq >= firstSeq).OrderByDescending(task => task.seq))
                {
                    for (int i = task.snapshots.Count - 1; i >= 0; i--)
                    {
                        var snapshot = task.snapshots[i];
                        if (snapshot == null || snapshot.type != SnapshotType.Modified || !IsSceneWork(snapshot))
                            continue;

                        string capturedIn = CapturedIn(snapshot, task);
                        _sessionIdFallbackAllowed = string.IsNullOrEmpty(capturedIn) || capturedIn == CurrentIdSession();
                        bool heldInThisSession = _sessionIdFallbackAllowed;
                        var obj = TryResolveObject(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId);
                        var go = obj as GameObject ?? (obj as Component)?.gameObject;
                        if (go == null)
                            continue;

                        var target = SceneTarget(go, obj is Component component ? component.GetType().Name : null);
                        if (!judgedKeys.Add(target.Key) || restoredKeys.Contains(target.Key) || doomed.GameObjects.Contains(go))
                            continue;
                        if (!TryHydrate(snapshot, out _))
                            continue;

                        var references = snapshot.afterObjectReferencesCaptured ? snapshot.afterObjectReferences : snapshot.objectReferences;
                        foreach (var reference in references ?? new List<ObjectReferenceData>())
                        {
                            string doomedName = DoomedNameOf(doomed, reference, heldInThisSession);
                            if (doomedName == null)
                                continue;
                            dangling.Add($"{target.Display}.{reference.propertyPath} -> {doomedName}");
                            if (dangling.Count >= MaxPreviewItems)
                                return dangling;
                        }
                    }
                }
            }
            finally
            {
                _restoreScopeAliases = null;
                _sessionIdFallbackAllowed = true;
            }
            return dangling.Count == 0 ? null : dangling;
        }

        /// <summary>The objects the restore deletes: the Created scene items that resolve now. Null when there are none.</summary>
        private static DoomedObjects DoomedObjectsOf(List<RestoreWork> runs)
        {
            var doomed = new DoomedObjects();
            foreach (var work in runs)
            {
                var snapshot = work.Item.Snapshot;
                if (snapshot.type != SnapshotType.Created || !IsSceneWork(snapshot))
                    continue;
                _sessionIdFallbackAllowed = IsCapturedInCurrentSession(work.Item);
                var obj = TryResolveObject(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId);
                if (obj is GameObject go)
                    AddDoomedGameObject(doomed, go);
                else if (obj is Component component)
                    AddDoomedObject(doomed, component, GameObjectFinder.GetPath(component.gameObject) + "/" + component.GetType().Name);
            }
            return doomed.IsEmpty ? null : doomed;
        }

        private static void AddDoomedGameObject(DoomedObjects doomed, GameObject go)
        {
            AddDoomedObject(doomed, go, GameObjectFinder.GetPath(go));
            foreach (var transform in go.GetComponentsInChildren<Transform>(true))
                doomed.GameObjects.Add(transform.gameObject);
            foreach (var component in go.GetComponentsInChildren<Component>(true))
            {
                string path = GameObjectFinder.GetPath(component.gameObject);
                AddDoomedObject(doomed, component, component is Transform ? path : path + "/" + component.GetType().Name);
            }
        }

        private static void AddDoomedObject(DoomedObjects doomed, UnityEngine.Object obj, string display)
        {
            string globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
            if (!IsUnsavedSceneGlobalObjectId(globalObjectId))
                doomed.ByGlobalId[globalObjectId] = display;
            string entityId = UnityObjectIdUtility.GetEntityId(obj);
            if (!string.IsNullOrEmpty(entityId))
                doomed.ByEntityId[entityId] = display;
        }

        /// <summary>The display of the doomed object a reference points at, or null. An entity id only counts when the holder is from this session.</summary>
        private static string DoomedNameOf(DoomedObjects doomed, ObjectReferenceData reference, bool holderInThisSession)
        {
            if (!string.IsNullOrEmpty(reference.globalObjectId) && doomed.ByGlobalId.TryGetValue(reference.globalObjectId, out string byGlobalId))
                return byGlobalId;
            if (holderInThisSession && !string.IsNullOrEmpty(reference.entityId) && doomed.ByEntityId.TryGetValue(reference.entityId, out string byEntityId))
                return byEntityId;
            return null;
        }

        /// <summary>
        /// Whether a work writes an open scene's .unity file. Such a file is not written while its scene is open: the restore marks the scene
        /// dirty, and saving the scene applies the change.
        /// </summary>
        private static bool TryGetOpenSceneFile(RestoreWork work, out Scene scene)
        {
            scene = default;
            var snapshot = work.Item.Snapshot;
            if (snapshot.type != SnapshotType.Modified)
                return false;
            string path = work.PathOverride ?? snapshot.assetPath;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                return false;
            scene = SceneManager.GetSceneByPath(path);
            return scene.IsValid() && scene.isLoaded;
        }
    }
}

// Producer:Betsy
