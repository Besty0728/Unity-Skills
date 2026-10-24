using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    public static partial class WorkflowManager
    {

        /// <summary>
        /// Registers a snapshot on the current task, deduplicating by globalObjectId.
        /// When upgradeExisting is true, replaces any snapshot already registered under the same id.
        /// </summary>
        internal static ObjectSnapshot AddSnapshot(ObjectSnapshot snap, bool upgradeExisting = false)
        {
            if (_currentTask == null || snap == null)
                return null;

            if (string.IsNullOrEmpty(snap.globalObjectId))
            {
                _currentTask.snapshots.Add(snap);
                _currentTask.RegisterEntitySnapshot(snap);
                return snap;
            }

            _currentTask.EnsureSnapshotIndex();

            // An object of a scene that was never saved has no usable GlobalObjectId (see IsUnsavedSceneGlobalObjectId), so
            // deduplicating by it could drop the snapshot of every such object but the first of the task (a ParticleSystem
            // and its renderer, say). Two snapshots of one object are harmless: undo restores them newest first, so the
            // oldest state still wins.
            bool shouldDeduplicate = WorkflowTask.ShouldDeduplicate(snap) && !IsUnsavedSceneGlobalObjectId(snap.globalObjectId);
            if (shouldDeduplicate && _currentTask.HasSnapshot(snap.globalObjectId, snap.type))
            {
                if (!upgradeExisting)
                    return null;

                _currentTask.snapshots.RemoveAll(s =>
                    !string.IsNullOrEmpty(s.globalObjectId) &&
                    s.globalObjectId == snap.globalObjectId && s.type == snap.type);
                _currentTask.InvalidateSnapshotIndex();
            }

            _currentTask.snapshots.Add(snap);
            if (shouldDeduplicate)
                _currentTask.TryRegisterSnapshot(snap.globalObjectId, snap.type);
            _currentTask.RegisterEntitySnapshot(snap);
            return snap;
        }

        /// <summary>
        /// An id with an all-zero asset GUID: what an object of a never-saved scene gets ("GlobalObjectId_V1-0-0000...-0-0"
        /// on 6000.x, "GlobalObjectId_V1-2-0000...-&lt;localId&gt;-0" on 2022.3). It names no scene, so it is neither
        /// unique nor resolvable by GlobalObjectId. Matched on the text because GlobalObjectId.TryParse rejects the type-0 form.
        /// </summary>
        internal static bool IsUnsavedSceneGlobalObjectId(string globalObjectId)
        {
            const string prefix = "GlobalObjectId_V1-";
            const int guidLength = 32;
            if (globalObjectId == null || !globalObjectId.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            int guidStart = globalObjectId.IndexOf('-', prefix.Length) + 1;
            if (guidStart <= 0 || globalObjectId.Length < guidStart + guidLength)
                return false;
            for (int i = guidStart; i < guidStart + guidLength; i++)
            {
                if (globalObjectId[i] != '0')
                    return false;
            }
            return globalObjectId.Length == guidStart + guidLength || globalObjectId[guidStart + guidLength] == '-';
        }

        /// <summary>
        /// Captures an object's/component's state before it's modified.
        /// Supports both scene objects and project assets (materials, scripts, etc.).
        /// Asset file backups are stored in the content-addressed WorkflowFileStore.
        /// </summary>
        public static void SnapshotObject(UnityEngine.Object obj, SnapshotType type = SnapshotType.Modified)
        {
            if (_currentTask == null || obj == null) return;

            if (type == SnapshotType.Created && obj is GameObject createdGameObject)
            {
                SnapshotCreatedGameObject(createdGameObject);
                return;
            }

            // A repeat snapshot of the same object in one task is dropped anyway (the oldest state wins), so it is
            // detected by entity id before paying for the GlobalObjectId, JSON, file hash and reference walk below.
            string entityId = UnityObjectIdUtility.GetEntityId(obj);
            if (WorkflowTask.ShouldDeduplicate(type) && _currentTask.HasEntitySnapshot(entityId, type))
                return;

            // Cap the number of snapshots per task, to avoid unbounded memory growth
            const int MaxSnapshotsPerTask = 500;
            if (_currentTask.snapshots.Count >= MaxSnapshotsPerTask)
            {
                SkillsLogger.LogVerbose($"Snapshot limit reached ({MaxSnapshotsPerTask}), skipping: {obj.name}");
                return;
            }

            string gid = GetGlobalObjectIdString(obj);

            string json = "";
            string assetPath = "";
            string fileHash = "";
            string metaFileHash = "";

            try
            {
                json = EditorJsonUtility.ToJson(obj);
                assetPath = AssetDatabase.GetAssetPath(obj);

                // Back up the asset file's bytes into the content-addressed store (any extension, including .cs)
                if (!string.IsNullOrEmpty(assetPath))
                {
                    if (WorkflowFileStore.TryGetSafeAssetFullPath(assetPath, out string fullPath) && File.Exists(fullPath))
                    {
                        fileHash = WorkflowFileStore.StoreFile(assetPath, move: false, out string storedMetaHash);
                        metaFileHash = storedMetaHash;
                    }
                }
            }
            catch (Exception ex) { SkillsLogger.LogVerbose($"Snapshot serialization failed for {obj.name}: {ex.Message}"); }

            var objectReferences = CaptureObjectReferences(obj, out bool objectReferencesCaptured);
            AddSnapshot(new ObjectSnapshot
            {
                globalObjectId = gid,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(obj),
                entityId = entityId,
                originalJson = json,
                objectReferencesCaptured = objectReferencesCaptured,
                objectReferences = objectReferences,
                objectName = obj.name,
                typeName = obj.GetType().Name,
                type = type,
                assetPath = assetPath,
                fileHash = fileHash,
                metaFileHash = metaFileHash
            });
        }

        /// <summary>
        /// Registers a newly-created component for undo.
        /// Also stores the parent GameObject and the component type, to make deletion reliable.
        /// </summary>
        public static void SnapshotCreatedComponent(Component comp)
        {
            if (_currentTask == null || comp == null) return;

            string entityId = UnityObjectIdUtility.GetEntityId(comp);
            if (_currentTask.HasEntitySnapshot(entityId, SnapshotType.Created))
                return;

            string gid = GetGlobalObjectIdString(comp);
            string parentGid = GetGlobalObjectIdString(comp.gameObject);

            AddSnapshot(new ObjectSnapshot
            {
                globalObjectId = gid,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                entityId = entityId,
                originalJson = "",  // A newly-created object needs no original state
                objectName = comp.name,
                typeName = comp.GetType().Name,
                type = SnapshotType.Created,
                componentTypeName = comp.GetType().FullName,
                parentGameObjectId = parentGid,
                parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp.gameObject),
                parentGameObjectEntityId = UnityObjectIdUtility.GetEntityId(comp.gameObject)
            });
        }

        /// <summary>
        /// Registers a newly-created asset (material, prefab, ScriptableObject, script, etc.) for undo.
        /// Doesn't store the file content; undoing "create asset" just means deleting that asset file.
        /// </summary>
        public static void SnapshotCreatedAsset(UnityEngine.Object asset)
        {
            if (_currentTask == null || asset == null) return;

            string entityId = UnityObjectIdUtility.GetEntityId(asset);
            if (_currentTask.HasEntitySnapshot(entityId, SnapshotType.Created))
                return;

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return;

            string gid = GetGlobalObjectIdString(asset);

            AddSnapshot(new ObjectSnapshot
            {
                globalObjectId = gid,
                entityId = entityId,
                objectName = asset.name,
                typeName = asset.GetType().Name,
                type = SnapshotType.Created,
                assetPath = assetPath
            });
        }

        /// <summary>
        /// Registers a setting change (e.g. console flags, gravity, quality level) for undo.
        /// This setting must already have a getter/setter registered in <see cref="WorkflowSettingRestorerRegistry"/>.
        /// Undo restores <paramref name="oldValueJson"/>; redo reapplies whatever value was captured at the moment of undo.
        /// </summary>
        /// <param name="settingKey">A stable setting key of the form "module.property" (e.g. "console.pauseOnError").</param>
        /// <param name="oldValueJson">The value before the change, JSON-encoded (captured by the caller).</param>
        /// <param name="description">A human-readable label for display.</param>
        public static ObjectSnapshot SnapshotSetting(string settingKey, string oldValueJson, string description)
        {
            if (_currentTask == null || string.IsNullOrEmpty(settingKey))
                return null;

            // Uses a stable pseudo-id, so multiple changes to the same setting within one task can be
            // deduplicated (undoing the whole task only cares about the old value in the first snapshot).
            return AddSnapshot(new ObjectSnapshot
            {
                globalObjectId = "setting:" + settingKey,
                objectName = description,
                typeName = "Setting",
                type = SnapshotType.Setting,
                settingKey = settingKey,
                settingOldValueJson = oldValueJson
            });
        }

        /// <summary>
        /// Registers a newly-created GameObject for undo/redo.
        /// Stores primitiveType, for reconstruction on redo.
        /// </summary>
        public static void SnapshotCreatedGameObject(GameObject go, string primitiveType = null)
        {
            if (_currentTask == null || go == null) return;

            // The hierarchy capture below is the costliest of all; a repeat registration is caught before it.
            string entityId = UnityObjectIdUtility.GetEntityId(go);
            if (_currentTask.HasEntitySnapshot(entityId, SnapshotType.Created))
                return;

            string gid = GetGlobalObjectIdString(go);

            var t = go.transform;
            var snapshot = new ObjectSnapshot
            {
                globalObjectId = gid,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                entityId = entityId,
                originalJson = EditorJsonUtility.ToJson(go),
                objectName = go.name,
                typeName = "GameObject",
                type = SnapshotType.Created,
                primitiveType = primitiveType ?? "",
                posX = t.position.x, posY = t.position.y, posZ = t.position.z,
                rotX = t.rotation.x, rotY = t.rotation.y, rotZ = t.rotation.z, rotW = t.rotation.w,
                scaleX = t.localScale.x, scaleY = t.localScale.y, scaleZ = t.localScale.z,
                components = new List<ComponentData>(),
                gameObjectHierarchy = CaptureGameObjectHierarchy(go)
            };

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp is Transform) continue;
                try
                {
                    var objectReferences = CaptureObjectReferences(comp, out bool objectReferencesCaptured);
                    snapshot.components.Add(new ComponentData
                    {
                        typeName = comp.GetType().AssemblyQualifiedName,
                        json = EditorJsonUtility.ToJson(comp),
                        globalObjectId = GetGlobalObjectIdString(comp),
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                        entityId = UnityObjectIdUtility.GetEntityId(comp),
                        objectReferencesCaptured = objectReferencesCaptured,
                        objectReferences = objectReferences
                    });
                }
                catch { /* Some components may not be serializable, skip safely */ }
            }

            AddSnapshot(snapshot);
        }

        /// <summary>
        /// Registers an asset move (source -> destination) for undo/redo.
        /// Replaces any existing snapshot under the same global object id.
        /// </summary>
        public static ObjectSnapshot SnapshotAssetMove(string sourcePath, string destinationPath)
        {
            if (_currentTask == null) return null;
            if (Validate.SafePath(sourcePath, "sourcePath") is object ||
                Validate.SafePath(destinationPath, "destinationPath") is object)
            {
                SkillsLogger.LogWarning($"[WorkflowManager] Invalid asset move paths: {sourcePath} -> {destinationPath}");
                return null;
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sourcePath);
            string gid = asset != null ? GetGlobalObjectIdString(asset) : "";
            string objectName = asset != null ? asset.name : Path.GetFileNameWithoutExtension(sourcePath);
            string typeName = asset != null ? asset.GetType().Name : "DefaultAsset";

            var snap = new ObjectSnapshot
            {
                globalObjectId = gid,
                objectName = objectName,
                typeName = typeName,
                type = SnapshotType.Moved,
                assetPath = destinationPath,
                previousAssetPath = sourcePath
            };

            return AddSnapshot(snap, upgradeExisting: true);
        }

        /// <summary>
        /// Registers a newly-created folder for undo.
        /// Folder deletion goes through AssetDatabase.DeleteAsset (empty folders only).
        /// </summary>
        public static ObjectSnapshot SnapshotCreatedFolder(string folderPath)
        {
            if (_currentTask == null) return null;
            if (Validate.SafePath(folderPath, "folderPath") is object)
            {
                SkillsLogger.LogWarning($"[WorkflowManager] Invalid folder path: {folderPath}");
                return null;
            }

            var folderAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folderPath);
            string gid = folderAsset != null ? GetGlobalObjectIdString(folderAsset) : "";

            var snap = new ObjectSnapshot
            {
                globalObjectId = gid,
                objectName = Path.GetFileName(folderPath.TrimEnd('/', '\\')),
                typeName = "DefaultAsset",
                type = SnapshotType.Created,
                assetPath = folderPath
            };

            return AddSnapshot(snap, upgradeExisting: false);
        }

        /// <summary>
        /// Backs up an asset into the content-addressed file store, then deletes it.
        /// Also creates a Deleted snapshot, making the operation undoable.
        /// When deleting a folder, captures every child file's and folder's .meta before deletion begins.
        /// </summary>
        public static bool DeleteAssetToTrash(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || Validate.SafePath(assetPath, "assetPath", isDelete: true) is object)
            {
                SkillsLogger.LogWarning($"[WorkflowManager] Unsafe asset delete path: {assetPath}");
                return false;
            }

            if (!WorkflowFileStore.TryGetSafeAssetFullPath(assetPath, out string fullPath))
                return false;

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                return false;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            string gid = asset != null ? GetGlobalObjectIdString(asset) : "";
            string objectName = asset != null ? asset.name : Path.GetFileNameWithoutExtension(assetPath);
            string typeName = asset != null ? asset.GetType().Name : "DefaultAsset";
            bool isFolder = Directory.Exists(fullPath);

            if (!isFolder)
            {
                string hash = WorkflowFileStore.StoreFile(assetPath, move: false, out string metaHash);
                if (string.IsNullOrEmpty(hash))
                    return false;

                var snapshot = new ObjectSnapshot
                {
                    globalObjectId = gid,
                    objectName = objectName,
                    typeName = typeName,
                    type = SnapshotType.Deleted,
                    assetPath = assetPath,
                    fileHash = hash,
                    metaFileHash = metaHash
                };

                if (!AssetDatabase.DeleteAsset(assetPath))
                    return false;
                AddSnapshot(snapshot);
            }
            else
            {
                var entries = CaptureDirectoryEntries(fullPath);
                if (entries == null)
                    return false;

                var snapshot = new ObjectSnapshot
                {
                    globalObjectId = gid,
                    objectName = objectName,
                    typeName = typeName,
                    type = SnapshotType.Deleted,
                    assetPath = assetPath,
                    isDirectory = true,
                    directoryEntries = entries
                };

                if (!AssetDatabase.DeleteAsset(assetPath))
                    return false;
                AddSnapshot(snapshot);
            }

            AssetDatabase.Refresh();
            return true;
        }

        /// <summary>
        /// Destroys a scene GameObject or Component with Undo, recording a Deleted snapshot while a task is open.
        /// Returns false when the object survives (Unity can refuse, e.g. a component another one still requires),
        /// so the caller reports an error and no snapshot claims a deletion that never happened.
        /// </summary>
        public static bool DeleteSceneObject(UnityEngine.Object obj)
        {
            if (obj == null) return false;

            if (_currentTask == null)
            {
                Undo.DestroyObjectImmediate(obj);
                return obj == null;
            }

            if (obj is GameObject go)
            {
                var snapshot = new ObjectSnapshot
                {
                    globalObjectId = GetGlobalObjectIdString(go),
                    objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                    entityId = UnityObjectIdUtility.GetEntityId(go),
                    objectName = go.name,
                    typeName = "GameObject",
                    type = SnapshotType.Deleted,
                    gameObjectHierarchy = CaptureGameObjectHierarchy(go)
                };
                Undo.DestroyObjectImmediate(go);
                if (go != null)
                    return false;
                AddSnapshot(snapshot);
                return true;
            }

            if (obj is Component component && !(component is Transform))
            {
                var owner = component.gameObject;
                var componentType = component.GetType();
                var objectReferences = CaptureObjectReferences(component, out bool objectReferencesCaptured);
                var snapshot = new ObjectSnapshot
                {
                    globalObjectId = GetGlobalObjectIdString(component),
                    objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(component),
                    entityId = UnityObjectIdUtility.GetEntityId(component),
                    originalJson = EditorJsonUtility.ToJson(component),
                    objectReferencesCaptured = objectReferencesCaptured,
                    objectReferences = objectReferences,
                    objectName = component.name,
                    typeName = component.GetType().Name,
                    type = SnapshotType.Deleted,
                    componentTypeName = component.GetType().AssemblyQualifiedName,
                    parentGameObjectId = GetGlobalObjectIdString(component.gameObject),
                    parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(component.gameObject),
                    parentGameObjectEntityId = UnityObjectIdUtility.GetEntityId(component.gameObject)
                };
                Undo.DestroyObjectImmediate(component);
                if (component != null)
                    return false;
                NotifyComponentTopologyChanged(owner, componentType);
                AddSnapshot(snapshot);
                return true;
            }

            return false;
        }

        private static List<WorkflowStoredPath> CaptureDirectoryEntries(string rootFullPath)
        {
            var entries = new List<WorkflowStoredPath>();
            string normalizedRoot = Path.GetFullPath(rootFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            try
            {
                foreach (string directory in Directory.EnumerateDirectories(normalizedRoot, "*", SearchOption.AllDirectories))
                {
                    string metaPath = directory + ".meta";
                    string metaHash = File.Exists(metaPath)
                        ? WorkflowFileStore.StoreBytes(File.ReadAllBytes(metaPath))
                        : null;
                    if (File.Exists(metaPath) && string.IsNullOrEmpty(metaHash))
                        return null;

                    entries.Add(new WorkflowStoredPath
                    {
                        relativePath = GetRelativePath(normalizedRoot, directory),
                        isDirectory = true,
                        metaFileHash = metaHash
                    });
                }

                foreach (string file in Directory.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string fileHash = WorkflowFileStore.StoreBytes(File.ReadAllBytes(file));
                    string metaPath = file + ".meta";
                    string metaHash = File.Exists(metaPath)
                        ? WorkflowFileStore.StoreBytes(File.ReadAllBytes(metaPath))
                        : null;
                    if (string.IsNullOrEmpty(fileHash) || (File.Exists(metaPath) && string.IsNullOrEmpty(metaHash)))
                        return null;

                    entries.Add(new WorkflowStoredPath
                    {
                        relativePath = GetRelativePath(normalizedRoot, file),
                        fileHash = fileHash,
                        metaFileHash = metaHash
                    });
                }

                string rootMetaPath = normalizedRoot + ".meta";
                string rootMetaHash = File.Exists(rootMetaPath)
                    ? WorkflowFileStore.StoreBytes(File.ReadAllBytes(rootMetaPath))
                    : null;
                if (File.Exists(rootMetaPath) && string.IsNullOrEmpty(rootMetaHash))
                    return null;

                entries.Add(new WorkflowStoredPath
                {
                    relativePath = "",
                    isDirectory = true,
                    metaFileHash = rootMetaHash
                });
                return entries;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[WorkflowManager] Failed to capture directory backup: {ex.Message}");
                return null;
            }
        }

        private static string GetRelativePath(string rootPath, string fullPath)
        {
            var rootUri = new Uri(rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(fullPath)).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Captures transform and component data from a live GameObject into a new ObjectSnapshot,
        /// copying its base fields from the given baseSnapshot.
        /// </summary>
        private static ObjectSnapshot CaptureGameObjectState(GameObject go, ObjectSnapshot baseSnapshot)
        {
            var t = go.transform;
            var result = new ObjectSnapshot
            {
                globalObjectId = baseSnapshot.globalObjectId,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                entityId = UnityObjectIdUtility.GetEntityId(go),
                originalJson = baseSnapshot.originalJson,
                objectReferencesCaptured = baseSnapshot.objectReferencesCaptured,
                objectReferences = baseSnapshot.objectReferences ?? new List<ObjectReferenceData>(),
                objectName = baseSnapshot.objectName,
                typeName = baseSnapshot.typeName,
                type = baseSnapshot.type,
                componentTypeName = baseSnapshot.componentTypeName,
                parentGameObjectId = baseSnapshot.parentGameObjectId,
                parentGameObjectInstanceId = baseSnapshot.parentGameObjectInstanceId,
                assetPath = baseSnapshot.assetPath,
                fileHash = baseSnapshot.fileHash,
                metaFileHash = baseSnapshot.metaFileHash,
                primitiveType = baseSnapshot.primitiveType,
                gameObjectHierarchy = CaptureGameObjectHierarchy(go),
                posX = t.position.x, posY = t.position.y, posZ = t.position.z,
                rotX = t.rotation.x, rotY = t.rotation.y, rotZ = t.rotation.z, rotW = t.rotation.w,
                scaleX = t.localScale.x, scaleY = t.localScale.y, scaleZ = t.localScale.z,
                components = new List<ComponentData>()
            };

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp is Transform) continue;
                try
                {
                    var objectReferences = CaptureObjectReferences(comp, out bool objectReferencesCaptured);
                    result.components.Add(new ComponentData
                    {
                        typeName = comp.GetType().AssemblyQualifiedName,
                        json = EditorJsonUtility.ToJson(comp),
                        globalObjectId = GetGlobalObjectIdString(comp),
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                        entityId = UnityObjectIdUtility.GetEntityId(comp),
                        objectReferencesCaptured = objectReferencesCaptured,
                        objectReferences = objectReferences
                    });
                }
                catch { /* Some components may not be serializable, skip safely */ }
            }

            return result;
        }

        private static List<GameObjectSnapshotData> CaptureGameObjectHierarchy(GameObject go)
        {
            var nodes = new List<GameObjectSnapshotData>();
            CaptureGameObjectNode(go, -1, nodes);
            return nodes;
        }

        private static void CaptureGameObjectNode(GameObject go, int parentIndex,
            List<GameObjectSnapshotData> nodes)
        {
            var transform = go.transform;
            var data = new GameObjectSnapshotData
            {
                globalObjectId = GetGlobalObjectIdString(go),
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                entityId = UnityObjectIdUtility.GetEntityId(go),
                transformGlobalObjectId = GetGlobalObjectIdString(transform),
                transformInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(transform),
                transformEntityId = UnityObjectIdUtility.GetEntityId(transform),
                name = go.name,
                parentIndex = parentIndex,
                activeSelf = go.activeSelf,
                layer = go.layer,
                tag = go.tag,
                siblingIndex = transform.GetSiblingIndex(),
                externalParentGlobalObjectId = parentIndex < 0 && transform.parent != null
                    ? GetGlobalObjectIdString(transform.parent.gameObject)
                    : null,
                externalParentInstanceId = parentIndex < 0 && transform.parent != null
                    ? UnityObjectIdUtility.GetLegacyInstanceId(transform.parent.gameObject)
                    : 0,
                externalParentEntityId = parentIndex < 0 && transform.parent != null
                    ? UnityObjectIdUtility.GetEntityId(transform.parent.gameObject)
                    : null,
                posX = transform.localPosition.x,
                posY = transform.localPosition.y,
                posZ = transform.localPosition.z,
                rotX = transform.localRotation.x,
                rotY = transform.localRotation.y,
                rotZ = transform.localRotation.z,
                rotW = transform.localRotation.w,
                scaleX = transform.localScale.x,
                scaleY = transform.localScale.y,
                scaleZ = transform.localScale.z
            };
            int currentIndex = nodes.Count;
            nodes.Add(data);

            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null || component is Transform) continue;
                try
                {
                    var objectReferences = CaptureObjectReferences(component, out bool objectReferencesCaptured);
                    data.components.Add(new ComponentData
                    {
                        typeName = component.GetType().AssemblyQualifiedName,
                        json = EditorJsonUtility.ToJson(component),
                        globalObjectId = GetGlobalObjectIdString(component),
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(component),
                        entityId = UnityObjectIdUtility.GetEntityId(component),
                        objectReferencesCaptured = objectReferencesCaptured,
                        objectReferences = objectReferences
                    });
                }
                catch { /* unsupported component serialization is non-fatal */ }
            }

            foreach (Transform child in transform)
                CaptureGameObjectNode(child.gameObject, currentIndex, nodes);
        }

        /// <summary>
        /// GlobalObjectId as a string. A persistent asset's id is cached for the session (it is fixed by GUID and file id), so
        /// the material slots every snapshot's reference walk visits cost one lookup each instead of one per snapshot. Scene
        /// objects are always computed: a scene's first save changes the ids of everything in it.
        /// </summary>
        private static string GetGlobalObjectIdString(UnityEngine.Object obj)
        {
            string cacheKey = EditorUtility.IsPersistent(obj) ? UnityObjectIdUtility.GetEntityId(obj) : null;
            if (cacheKey != null && _persistentGidCache.TryGetValue(cacheKey, out string cached))
                return cached;

            GlobalObjectIdComputationsForTests++;
            string gid = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
            if (cacheKey != null)
            {
                if (_persistentGidCache.Count >= MaxPersistentGidCacheEntries)
                    _persistentGidCache.Clear();
                _persistentGidCache[cacheKey] = gid;
            }
            return gid;
        }

        /// <summary>Drops every cached asset GlobalObjectId; called when assets are deleted or moved.</summary>
        internal static void InvalidateGlobalObjectIdCache()
        {
            _persistentGidCache.Clear();
        }

        private static List<ObjectReferenceData> CaptureObjectReferences(UnityEngine.Object obj,
            out bool captureSucceeded)
        {
            captureSucceeded = false;
            var references = new List<ObjectReferenceData>();
            if (obj == null) return references;

            try
            {
                var serializedObject = new SerializedObject(obj);
                var iterator = serializedObject.GetIterator();
                var walkTimer = System.Diagnostics.Stopwatch.StartNew();
                // Managed references form a graph, not a tree: the same instance can be reached via
                // multiple paths, and can even reference itself. Visiting each referenceId only once turns
                // this graph back into a bounded traversal. Revisiting one wouldn't surface any new
                // restorable property path anyway.
                var visitedManagedRefs = new HashSet<long>();
                int visitedNodes = 0;
                bool truncated = false;
                bool enterChildren = true;

                while (iterator.Next(enterChildren))
                {
                    enterChildren = true;

                    if (++visitedNodes > MaxReferenceWalkNodes ||
                        walkTimer.ElapsedMilliseconds > MaxReferenceWalkMilliseconds)
                    {
                        truncated = true;
                        break;
                    }

                    if (iterator.depth >= MaxReferenceWalkDepth)
                    {
                        enterChildren = false;
                        truncated = true;
                    }

                    if (iterator.propertyType == SerializedPropertyType.ManagedReference)
                    {
                        long referenceId = iterator.managedReferenceId;
                        if (referenceId != 0 && !visitedManagedRefs.Add(referenceId))
                        {
                            enterChildren = false;
                            continue;
                        }
                    }

                    if (iterator.propertyType != SerializedPropertyType.ObjectReference ||
                        !IsRestorableObjectReferencePath(iterator.propertyPath))
                        continue;

                    var referencedObject = iterator.objectReferenceValue;
                    references.Add(new ObjectReferenceData
                    {
                        propertyPath = iterator.propertyPath,
                        globalObjectId = referencedObject != null
                            ? GetGlobalObjectIdString(referencedObject)
                            : string.Empty,
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(referencedObject),
                        entityId = UnityObjectIdUtility.GetEntityId(referencedObject)
                    });
                }

                if (truncated)
                {
                    // A partial capture is still useful: undo restores object references by property path,
                    // and the paths already collected each remain valid. Assets additionally carry a
                    // content-addressed file backup, which is their real path to restoration.
                    SkillsLogger.LogVerbose(
                        $"Object reference snapshot for '{obj.name}' ({obj.GetType().Name}) stopped at " +
                        $"{visitedNodes} properties / {walkTimer.ElapsedMilliseconds}ms; captured {references.Count} references.");
                }

                captureSucceeded = true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Object reference snapshot failed: {ex.Message}");
            }

            return references;
        }
    }
}

// Producer:Betsy
