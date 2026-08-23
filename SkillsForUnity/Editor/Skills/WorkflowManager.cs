using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    public static class WorkflowManager
    {
        private static WorkflowHistoryData _history;
        private static WorkflowTask _currentTask;
        private static string _currentSessionId;

        // 历史文件读取失败时置位。此时手上的历史所引用的 blob 少于库中实际持有的，
        // 于是回收"无引用"条目会删掉活备份；宁可泄漏，代价更小。
        // volatile: SkillsHttpServer 的 /health 快路径在 HTTP 线程上读 IsHistoryRecoveryMode，
        // 写入始终发生在主线程（LoadHistory / ClearHistory）。
        private static volatile bool _historyRecoveryMode;

        // 历史文件存放路径（Library 目录持久但只在本机）
        internal static string OverrideHistoryFilePathForTests;
        private static string HistoryFilePath => OverrideHistoryFilePathForTests ??
            Path.Combine(Application.dataPath, "../Library/UnitySkills/workflow_history.json");

        public static WorkflowHistoryData History
        {
            get
            {
                if (_history == null)
                    LoadHistory();
                return _history;
            }
        }

        public static WorkflowTask CurrentTask => _currentTask;
        public static bool IsRecording => _currentTask != null;
        public static string CurrentSessionId => _currentSessionId;
        public static bool HasActiveSession => !string.IsNullOrEmpty(_currentSessionId);

        /// <summary>
        /// 本次会话历史文件加载失败、因而暂停文件库清理时为 true。由 ClearHistory 清除。
        /// </summary>
        public static bool IsHistoryRecoveryMode => _historyRecoveryMode;

        internal static event Action<GameObject, Type> ComponentTopologyChanged;

        private static void NotifyComponentTopologyChanged(GameObject owner, Type componentType)
        {
            if (owner == null || componentType == null) return;
            try { ComponentTopologyChanged?.Invoke(owner, componentType); }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Component topology callback failed for {componentType.Name}: {ex.Message}");
            }
        }

        public static void LoadHistory()
        {
            // 崩溃恢复：主文件缺失但 .tmp 存在时，把 .tmp 提升为主文件
            string tmpPath = HistoryFilePath + ".tmp";
            if (!File.Exists(HistoryFilePath) && File.Exists(tmpPath))
            {
                try { File.Move(tmpPath, HistoryFilePath); }
                catch { /* If promotion fails, fall back to the .bak below */ }
            }

            string backupPath = HistoryFilePath + ".bak";
            _history = null;
            _historyRecoveryMode = false;
            bool recoveredFromBackup = false;

            if (File.Exists(HistoryFilePath))
            {
                if (!TryLoadHistoryFrom(HistoryFilePath, out string mainError))
                {
                    _historyRecoveryMode = true;
                    string quarantined = QuarantineHistoryFile(HistoryFilePath);
                    SkillsLogger.LogError(
                        $"Failed to load workflow history: {mainError}. Kept the unreadable file as " +
                        $"{quarantined ?? "<quarantine failed>"}; file store cleanup is disabled for this session " +
                        "so the backups of the lost tasks are not reclaimed. Clear the history to re-enable it.");
                }
            }

            if (_history == null && File.Exists(backupPath))
            {
                if (TryLoadHistoryFrom(backupPath, out string backupError))
                {
                    // 备份比当前落后一次保存，其后记录的内容仍然是丢的。
                    _historyRecoveryMode = true;
                    recoveredFromBackup = true;
                    SkillsLogger.LogWarning(
                        $"Recovered workflow history from {Path.GetFileName(backupPath)}; tasks recorded after the last save are gone.");
                }
                else
                {
                    _historyRecoveryMode = true;
                    SkillsLogger.LogError($"Workflow history backup is unreadable as well: {backupError}");
                }
            }

            _history ??= new WorkflowHistoryData();
            _history.EnsureDefaults();
            MigrateHistorySchema();
            if (recoveredFromBackup)
                SaveHistory();
            TrimHistoryIfNeeded();
        }

        /// <summary>
        /// 把历史文件解析进 <see cref="_history"/>。失败时 _history 保持 null，
        /// 使调用方能尝试下一个候选文件，失败原因由 <paramref name="error"/> 带出。
        /// </summary>
        private static bool TryLoadHistoryFrom(string path, out string error)
        {
            error = null;
            try
            {
                string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                var data = JsonUtility.FromJson<WorkflowHistoryData>(json);
                if (data == null)
                {
                    error = "file is empty or not a workflow history document";
                    return false;
                }

                _history = data;
                _history.EnsureDefaults();
                SanitizeHistory();
                return true;
            }
            catch (Exception e)
            {
                _history = null;
                error = e.Message;
                return false;
            }
        }

        /// <summary>
        /// 把无法读取的历史文件按带时间戳的名字挪开，使下次保存不会覆盖它。
        /// 返回隔离后的文件名，移动失败则返回 null。
        /// </summary>
        private static string QuarantineHistoryFile(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                string baseName = Path.GetFileNameWithoutExtension(path);
                string quarantinePath = Path.Combine(dir,
                    $"{baseName}.corrupt.{DateTime.Now:yyyyMMddHHmmss}.json");
                if (File.Exists(quarantinePath))
                    File.Delete(quarantinePath);
                File.Move(path, quarantinePath);
                return Path.GetFileName(quarantinePath);
            }
            catch (Exception e)
            {
                SkillsLogger.LogWarning($"Failed to quarantine unreadable workflow history: {e.Message}");
                return null;
            }
        }

        public static void SaveHistory()
        {
            try
            {
                string dir = Path.GetDirectoryName(HistoryFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                _history ??= new WorkflowHistoryData();
                _history.EnsureDefaults();
                string json = JsonUtility.ToJson(_history, true);
                string tmpPath = HistoryFilePath + ".tmp";
                string backupPath = HistoryFilePath + ".bak";
                File.WriteAllText(tmpPath, json, SkillsCommon.Utf8NoBom);
                if (File.Exists(HistoryFilePath))
                {
                    // 被替换掉的文件保留为 .bak：主文件读不出来时 LoadHistory 会回退到它。
                    File.Replace(tmpPath, HistoryFilePath, backupPath);
                }
                else
                {
                    File.Move(tmpPath, HistoryFilePath);
                }
            }
            catch (Exception e)
            {
                SkillsLogger.LogError($"Failed to save workflow history: {e.Message}");
            }
        }

        private static void SanitizeHistory()
        {
            if (_history == null) return;

            SanitizeTaskCollection(_history.tasks, "tasks");
            SanitizeTaskCollection(_history.undoneStack, "undoneStack");
        }

        private static void SanitizeTaskCollection(List<WorkflowTask> tasks, string source)
        {
            if (tasks == null) return;

            foreach (var task in tasks)
            {
                if (task?.snapshots == null) continue;

                foreach (var snapshot in task.snapshots)
                {
                    if (snapshot == null) continue;

                    if (!string.IsNullOrEmpty(snapshot.assetPath))
                    {
                        if (Validate.SafePath(snapshot.assetPath, "assetPath") is object)
                        {
                            SkillsLogger.LogWarning($"WorkflowManager: stripped unsafe assetPath from {source}: {snapshot.assetPath}");
                            snapshot.assetPath = null;
                            snapshot.fileHash = null;
                            snapshot.metaFileHash = null;
                            snapshot.previousAssetPath = null;
                            snapshot.assetBytesBase64 = null;
                            snapshot.directoryEntries?.Clear();
                        }
                    }

                    if (!string.IsNullOrEmpty(snapshot.previousAssetPath) &&
                        Validate.SafePath(snapshot.previousAssetPath, "previousAssetPath") is object)
                    {
                        SkillsLogger.LogWarning($"WorkflowManager: stripped unsafe previousAssetPath from {source}: {snapshot.previousAssetPath}");
                        snapshot.previousAssetPath = null;
                    }
                }
            }
        }

        public static WorkflowTask BeginTask(string tag, string description)
        {
            if (_currentTask != null)
                EndTask(); // 若上一个任务还开着则自动收尾

            _currentTask = new WorkflowTask
            {
                id = Guid.NewGuid().ToString(),
                tag = tag,
                description = description,
                timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                snapshots = new List<ObjectSnapshot>()
            };
            _currentTask.EnsureSnapshotIndex();

            return _currentTask;
        }

        public static void EndTask()
        {
            if (_currentTask == null) return;

            // 只记录至少拍到一条快照的任务。被跟踪的技能若失败或没做任何改动，就没有东西可撤销，
            // 记下来只会留下一条空条目（changes=0），既污染历史又干扰 undo/redo 导航。
            if (_currentTask.snapshots.Count == 0)
            {
                _currentTask = null;
                return;
            }

            if (_history == null) LoadHistory();

            _history.tasks.Add(_currentTask);
            _history.undoneStack.Clear();
            _currentTask = null;

            TrimHistoryIfNeeded();
            SaveHistory();
        }

        public static void AbortTask()
        {
            _currentTask = null;
        }

        internal static void TruncateCurrentTask(int snapshotCount)
        {
            if (_currentTask?.snapshots == null) return;
            snapshotCount = Mathf.Clamp(snapshotCount, 0, _currentTask.snapshots.Count);
            if (_currentTask.snapshots.Count > snapshotCount)
                _currentTask.snapshots.RemoveRange(snapshotCount, _currentTask.snapshots.Count - snapshotCount);
            _currentTask.InvalidateSnapshotIndex();
        }

        /// <summary>
        /// 把快照登记进当前任务，按 globalObjectId 去重。
        /// upgradeExisting 为 true 时，替换同 id 已登记的快照。
        /// </summary>
        internal static ObjectSnapshot AddSnapshot(ObjectSnapshot snap, bool upgradeExisting = false)
        {
            if (_currentTask == null || snap == null)
                return null;

            if (string.IsNullOrEmpty(snap.globalObjectId))
            {
                _currentTask.snapshots.Add(snap);
                return snap;
            }

            _currentTask.EnsureSnapshotIndex();

            bool shouldDeduplicate = WorkflowTask.ShouldDeduplicate(snap);
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
            return snap;
        }

        /// <summary>
        /// 在修改之前捕获对象/组件的状态。
        /// 场景对象与项目资产（材质、脚本等）都支持。
        /// 资产文件备份存入内容寻址的 WorkflowFileStore。
        /// </summary>
        public static void SnapshotObject(UnityEngine.Object obj, SnapshotType type = SnapshotType.Modified)
        {
            if (_currentTask == null || obj == null) return;

            if (type == SnapshotType.Created && obj is GameObject createdGameObject)
            {
                SnapshotCreatedGameObject(createdGameObject);
                return;
            }

            // 限制单任务快照数，避免内存无界增长
            const int MaxSnapshotsPerTask = 500;
            if (_currentTask.snapshots.Count >= MaxSnapshotsPerTask)
            {
                SkillsLogger.LogVerbose($"Snapshot limit reached ({MaxSnapshotsPerTask}), skipping: {obj.name}");
                return;
            }

            string gid = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();

            string json = "";
            string assetPath = "";
            string fileHash = "";
            string metaFileHash = "";

            try
            {
                json = EditorJsonUtility.ToJson(obj);
                assetPath = AssetDatabase.GetAssetPath(obj);

                // 把资产文件字节备份进内容寻址库（所有扩展名，含 .cs）
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
        /// 登记一个新建的组件以便撤销。
        /// 额外存下父 GameObject 与组件类型，以保证删除可靠。
        /// </summary>
        public static void SnapshotCreatedComponent(Component comp)
        {
            if (_currentTask == null || comp == null) return;

            string gid = GlobalObjectId.GetGlobalObjectIdSlow(comp).ToString();
            string parentGid = GlobalObjectId.GetGlobalObjectIdSlow(comp.gameObject).ToString();

            AddSnapshot(new ObjectSnapshot
            {
                globalObjectId = gid,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                originalJson = "",  // 新建对象无需原始状态
                objectName = comp.name,
                typeName = comp.GetType().Name,
                type = SnapshotType.Created,
                componentTypeName = comp.GetType().FullName,
                parentGameObjectId = parentGid,
                parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp.gameObject)
            });
        }

        /// <summary>
        /// 登记一个新建资产（材质、预制体、ScriptableObject、脚本等）以便撤销。
        /// 不存文件内容；撤销"创建资产"就是删除该资产文件。
        /// </summary>
        public static void SnapshotCreatedAsset(UnityEngine.Object asset)
        {
            if (_currentTask == null || asset == null) return;

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return;

            string gid = GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString();

            AddSnapshot(new ObjectSnapshot
            {
                globalObjectId = gid,
                objectName = asset.name,
                typeName = asset.GetType().Name,
                type = SnapshotType.Created,
                assetPath = assetPath
            });
        }

        /// <summary>
        /// 登记一次设置变更（如控制台标志、重力、质量等级）以便撤销。
        /// 该设置必须已在 <see cref="WorkflowSettingRestorerRegistry"/> 注册 getter/setter。
        /// 撤销时还原 <paramref name="oldValueJson"/>；重做时重新应用撤销那一刻捕获的值。
        /// </summary>
        /// <param name="settingKey">稳定的设置键，形如 "module.property"（如 "console.pauseOnError"）。</param>
        /// <param name="oldValueJson">变更前的值，JSON 编码（由调用方捕获）。</param>
        /// <param name="description">用于展示的可读标签。</param>
        public static ObjectSnapshot SnapshotSetting(string settingKey, string oldValueJson, string description)
        {
            if (_currentTask == null || string.IsNullOrEmpty(settingKey))
                return null;

            // 用稳定的伪 id，使同一任务内对同一设置的多次变更能去重
            // （整任务撤销只关心第一条快照里的旧值）。
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
        /// 登记一个新建 GameObject 以便撤销/重做。
        /// 存下 primitiveType，供重做时重建。
        /// </summary>
        public static void SnapshotCreatedGameObject(GameObject go, string primitiveType = null)
        {
            if (_currentTask == null || go == null) return;

            string gid = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();

            var t = go.transform;
            var snapshot = new ObjectSnapshot
            {
                globalObjectId = gid,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
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
                        globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(comp).ToString(),
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                        objectReferencesCaptured = objectReferencesCaptured,
                        objectReferences = objectReferences
                    });
                }
                catch { /* Some components may not be serializable, skip safely */ }
            }

            AddSnapshot(snapshot);
        }

        /// <summary>
        /// 登记一次资产移动（源 -> 目标）以便撤销/重做。
        /// 会替换同一 global object id 上已有的快照。
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
            string gid = asset != null ? GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString() : "";
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
        /// 登记一个新建文件夹以便撤销。
        /// 文件夹删除走 AssetDatabase.DeleteAsset（仅限空文件夹）。
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
            string gid = folderAsset != null ? GlobalObjectId.GetGlobalObjectIdSlow(folderAsset).ToString() : "";

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
        /// 先把资产备份进内容寻址文件库，再删除它。
        /// 同时创建 Deleted 快照，使该操作可撤销。
        /// 删除文件夹时，会在动手删除之前把每个子文件与文件夹的 .meta 全部捕获。
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
            string gid = asset != null ? GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString() : "";
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

        public static bool DeleteSceneObject(UnityEngine.Object obj)
        {
            if (obj == null) return false;

            if (_currentTask == null)
            {
                Undo.DestroyObjectImmediate(obj);
                return true;
            }

            if (obj is GameObject go)
            {
                var snapshot = new ObjectSnapshot
                {
                    globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                    objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                    objectName = go.name,
                    typeName = "GameObject",
                    type = SnapshotType.Deleted,
                    gameObjectHierarchy = CaptureGameObjectHierarchy(go)
                };
                Undo.DestroyObjectImmediate(go);
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
                    globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(component).ToString(),
                    objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(component),
                    originalJson = EditorJsonUtility.ToJson(component),
                    objectReferencesCaptured = objectReferencesCaptured,
                    objectReferences = objectReferences,
                    objectName = component.name,
                    typeName = component.GetType().Name,
                    type = SnapshotType.Deleted,
                    componentTypeName = component.GetType().AssemblyQualifiedName,
                    parentGameObjectId = GlobalObjectId.GetGlobalObjectIdSlow(component.gameObject).ToString(),
                    parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(component.gameObject)
                };
                Undo.DestroyObjectImmediate(component);
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
        /// 撤销指定任务，返回逐快照的详细结果。
        /// 把逆操作存入 undoneStack 以备重做。
        /// </summary>
        public static TaskUndoResult UndoTask(string taskId)
        {
            var task = History.tasks.FirstOrDefault(t => t.id == taskId);
            if (task == null)
            {
                return new TaskUndoResult { error = "Task not found" };
            }

            return TransitionTask(task, _history.tasks, _history.undoneStack, $"Undo Task: {task.tag}");
        }

        /// <summary>
        /// 重做一个先前被撤销的任务，返回逐快照的详细结果。
        /// </summary>
        public static TaskUndoResult RedoTask(string taskId)
        {
            var task = History.undoneStack.FirstOrDefault(t => t.id == taskId);
            if (task == null)
            {
                return new TaskUndoResult { error = "Task not found in undone stack" };
            }

            return TransitionTask(task, _history.undoneStack, _history.tasks, $"Redo Task: {task.tag}");
        }

        private static TaskUndoResult TransitionTask(WorkflowTask sourceTask, List<WorkflowTask> sourceStack,
            List<WorkflowTask> destinationStack, string undoGroupName)
        {
            var result = new TaskUndoResult();
            var destinationTask = destinationStack.FirstOrDefault(t => t.id == sourceTask.id);
            if (destinationTask == null)
            {
                destinationTask = CloneTaskMetadata(sourceTask);
                destinationStack.Add(destinationTask);
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(undoGroupName);
            int undoGroup = Undo.GetCurrentGroup();

            for (int i = sourceTask.snapshots.Count - 1; i >= 0; i--)
            {
                var detail = UndoSnapshot(sourceTask.snapshots[i], destinationTask);
                result.details.Add(detail);
                if (!detail.success)
                {
                    result.failed++;
                    break;
                }

                result.succeeded++;
                sourceTask.snapshots.RemoveAt(i);
                sourceTask.InvalidateSnapshotIndex();
            }

            result.total = result.details.Count;
            result.success = result.failed == 0 && sourceTask.snapshots.Count == 0;
            Undo.CollapseUndoOperations(undoGroup);

            if (sourceTask.snapshots.Count == 0)
                sourceStack.Remove(sourceTask);
            if (destinationTask.snapshots.Count == 0)
                destinationStack.Remove(destinationTask);

            SaveHistory();
            return result;
        }

        private static WorkflowTask CloneTaskMetadata(WorkflowTask task)
        {
            return new WorkflowTask
            {
                id = task.id,
                tag = task.tag,
                description = task.description,
                timestamp = task.timestamp,
                sessionId = task.sessionId,
                snapshots = new List<ObjectSnapshot>()
            };
        }

        /// <summary>
        /// 取可重做的（已撤销）任务列表。
        /// </summary>
        public static List<WorkflowTask> GetUndoneStack()
        {
            return History.undoneStack;
        }

        /// <summary>
        /// 清空撤销栈（撤销之后又产生新改动时调用）。
        /// </summary>
        public static void ClearUndoneStack()
        {
            if (_history != null)
            {
                _history.undoneStack.Clear();
                SaveHistory();
            }
        }

        /// <summary>
        /// UndoTask 的别名（向后兼容）。
        /// </summary>
        public static TaskUndoResult RevertTask(string taskId)
        {
            return UndoTask(taskId);
        }

        public static void DeleteTask(string taskId)
        {
            if (_history == null) LoadHistory();
            var task = _history.tasks.FirstOrDefault(t => t.id == taskId);
            if (task != null)
            {
                _history.tasks.Remove(task);
            }
            else
            {
                // 已被撤销的任务住在 undoneStack 里而不在 tasks 里。"delete" 之后仍把它留在那儿，
                // 会让 workflow_redo_task 的缺省行为（取 undoneStack 最后一项）把调用方以为已经
                // 消失的对象重新复活。
                var undoneTask = _history.undoneStack.FirstOrDefault(t => t.id == taskId);
                if (undoneTask != null)
                    _history.undoneStack.Remove(undoneTask);
            }

            SaveHistory();

            if (_historyRecoveryMode)
                return;

            var referencedHashes = CollectReferencedHashes();
            WorkflowFileStore.CollectGarbage(referencedHashes, out _, out _);
        }

        #region Session Management (Conversation-Level Undo)

        /// <summary>
        /// 开启一个新会话（对话级）。该会话期间创建的所有任务会被归为一组，可整体撤销。
        /// </summary>
        public static string BeginSession(string sessionTag = null)
        {
            if (HasActiveSession)
            {
                EndSession();
            }

            _currentSessionId = Guid.NewGuid().ToString();

            BeginTask(sessionTag ?? "Session", $"Session started at {DateTime.Now:HH:mm:ss}");
            _currentTask.sessionId = _currentSessionId;

            Debug.Log($"{SkillsLogger.PREFIX_WORKFLOW} Session started: <b>{_currentSessionId}</b>");
            return _currentSessionId;
        }

        /// <summary>
        /// 结束当前会话并保存所有已记录的改动。
        /// </summary>
        public static void EndSession()
        {
            if (!HasActiveSession) return;

            if (_currentTask != null)
            {
                _currentTask.sessionId = _currentSessionId;
                EndTask();
            }

            Debug.Log($"{SkillsLogger.PREFIX_WORKFLOW} Session ended: <b>{_currentSessionId}</b>");
            _currentSessionId = null;
        }

        /// <summary>
        /// 撤销指定会话期间的全部改动，返回逐快照的详细结果。
        /// </summary>
        public static TaskUndoResult UndoSession(string sessionId)
        {
            var result = new TaskUndoResult();
            if (string.IsNullOrEmpty(sessionId))
            {
                result.error = "sessionId is required";
                return result;
            }

            var sessionTasks = History.tasks
                .Where(t => t.sessionId == sessionId)
                .OrderByDescending(t => t.timestamp)
                .ToList();

            if (sessionTasks.Count == 0)
            {
                result.error = $"No tasks found for session: {sessionId}";
                return result;
            }

            // 按任务整体、从新到旧撤销。保留任务边界，才能在同一对象在会话中被修改、移动、删除时保持操作顺序。
            foreach (var task in sessionTasks)
            {
                var taskResult = TransitionTask(task, _history.tasks, _history.undoneStack, "Undo Session");
                result.details.AddRange(taskResult.details);
                result.succeeded += taskResult.succeeded;
                result.failed += taskResult.failed;
                if (!taskResult.success)
                    break;
            }

            result.total = result.details.Count;
            result.success = result.failed == 0 && !_history.tasks.Any(t => t.sessionId == sessionId);

            return result;
        }

        /// <summary>
        /// 取历史中的全部会话。
        /// </summary>
        public static List<SessionInfo> GetSessions()
        {
            var sessions = History.tasks
                .Where(t => !string.IsNullOrEmpty(t.sessionId))
                .GroupBy(t => t.sessionId)
                .Select(g => new SessionInfo
                {
                    sessionId = g.Key,
                    taskCount = g.Count(),
                    totalChanges = g.Sum(t => t.snapshots.Count),
                    startTime = DateTimeOffset.FromUnixTimeSeconds(g.Min(t => t.timestamp)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    endTime = DateTimeOffset.FromUnixTimeSeconds(g.Max(t => t.timestamp)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    tags = g.Select(t => t.tag).Distinct().ToList()
                })
                .OrderByDescending(s => s.startTime)
                .ToList();

            return sessions;
        }

        #endregion

        #region Undo/Redo Snapshot Dispatch

        /// <summary>
        /// 撤销单条快照，并把逆操作记入 redoTask。
        /// </summary>
        private static SnapshotUndoResult UndoSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            var result = new SnapshotUndoResult
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName
            };

            int inverseCountBefore = redoTask.snapshots.Count;
            WorkflowFileStore.ClearLastIntegrityError();
            try
            {
                switch (snapshot.type)
                {
                    case SnapshotType.Modified:
                        result.success = UndoModifiedSnapshot(snapshot, redoTask);
                        break;
                    case SnapshotType.Created:
                        result.success = UndoCreatedSnapshot(snapshot, redoTask);
                        break;
                    case SnapshotType.Deleted:
                        result.success = UndoDeletedSnapshot(snapshot, redoTask);
                        break;
                    case SnapshotType.Moved:
                        result.success = UndoMovedSnapshot(snapshot, redoTask);
                        break;
                    case SnapshotType.Setting:
                        result.success = UndoSettingSnapshot(snapshot, redoTask, out string undoSettingError);
                        if (!result.success && !string.IsNullOrEmpty(undoSettingError))
                            result.error = undoSettingError;
                        break;
                    default:
                        result.error = $"Unsupported snapshot type: {snapshot.type}";
                        break;
                }
            }
            catch (Exception ex)
            {
                result.error = ex.Message;
            }

            if (!result.success && string.IsNullOrEmpty(result.error))
                result.error = WorkflowFileStore.LastIntegrityError ?? "Unknown failure";

            if (!result.success && redoTask.snapshots.Count > inverseCountBefore)
            {
                redoTask.snapshots.RemoveRange(inverseCountBefore, redoTask.snapshots.Count - inverseCountBefore);
                redoTask.InvalidateSnapshotIndex();
            }

            return result;
        }

        /// <summary>
        /// 重做单条快照，并把逆操作记入 newTask。
        /// </summary>
        private static SnapshotUndoResult RedoSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
            var result = new SnapshotUndoResult
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName
            };

            WorkflowFileStore.ClearLastIntegrityError();
            try
            {
                switch (snapshot.type)
                {
                    case SnapshotType.Modified:
                        result.success = RedoModifiedSnapshot(snapshot, newTask);
                        break;
                    case SnapshotType.Created:
                        result.success = RedoCreatedSnapshot(snapshot, newTask);
                        break;
                    case SnapshotType.Deleted:
                        result.success = RedoDeletedSnapshot(snapshot, newTask);
                        break;
                    case SnapshotType.Moved:
                        result.success = RedoMovedSnapshot(snapshot, newTask);
                        break;
                    case SnapshotType.Setting:
                        result.success = RedoSettingSnapshot(snapshot, newTask, out string redoSettingError);
                        if (!result.success && !string.IsNullOrEmpty(redoSettingError))
                            result.error = redoSettingError;
                        break;
                    default:
                        result.error = $"Unsupported snapshot type: {snapshot.type}";
                        break;
                }
            }
            catch (Exception ex)
            {
                result.error = ex.Message;
            }

            if (!result.success && string.IsNullOrEmpty(result.error))
                result.error = WorkflowFileStore.LastIntegrityError ?? "Unknown failure";

            return result;
        }

        private static bool UndoModifiedSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            return RestoreModifiedSnapshot(snapshot, redoTask, removeFromStore: false, undoLabel: "Undo Workflow Modification");
        }

        private static bool RedoModifiedSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
            return RestoreModifiedSnapshot(snapshot, newTask, removeFromStore: false, undoLabel: "Redo Workflow Modification");
        }

        private static bool UndoCreatedSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            // 撤销"创建组件"
            if (!string.IsNullOrEmpty(snapshot.componentTypeName) &&
                !string.IsNullOrEmpty(snapshot.parentGameObjectId))
            {
                if (TryResolveObject(snapshot.parentGameObjectId, snapshot.parentGameObjectInstanceId) is GameObject go)
                {
                    var compType = Type.GetType(snapshot.componentTypeName) ??
                                   ComponentSkills.FindComponentType(snapshot.componentTypeName);
                    if (compType != null)
                    {
                        var comp = TryResolveObject(snapshot.globalObjectId, snapshot.objectInstanceId) as Component;
                        if (comp != null && (comp.gameObject != go || !compType.IsInstanceOfType(comp)))
                            comp = null;

                        // 没有对象标识的遗留快照只能退回按类型查找。
                        // 对有标识的快照，宁可失败，也比删掉另一个同类型组件安全。
                        if (comp == null && string.IsNullOrEmpty(snapshot.globalObjectId) && snapshot.objectInstanceId == 0)
                            comp = go.GetComponent(compType);
                        if (comp != null)
                        {
                            var objectReferences = CaptureObjectReferences(comp, out bool objectReferencesCaptured);
                            redoTask.snapshots.Add(new ObjectSnapshot
                            {
                                globalObjectId = snapshot.globalObjectId,
                                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                                originalJson = EditorJsonUtility.ToJson(comp),
                                objectReferencesCaptured = objectReferencesCaptured,
                                objectReferences = objectReferences,
                                objectName = snapshot.objectName,
                                typeName = snapshot.typeName,
                                type = SnapshotType.Deleted,
                                componentTypeName = snapshot.componentTypeName,
                                parentGameObjectId = snapshot.parentGameObjectId,
                                parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go)
                            });
                            Undo.DestroyObjectImmediate(comp);
                            NotifyComponentTopologyChanged(go, compType);
                            return true;
                        }
                    }
                }
                return false;
            }

            // 撤销"创建 GameObject"
            if (snapshot.typeName == "GameObject")
            {
                var obj = TryResolveObject(snapshot.globalObjectId, snapshot.objectInstanceId);
                if (!(obj is GameObject go))
                    return false;

                redoTask.snapshots.Add(CaptureGameObjectState(go, new ObjectSnapshot
                {
                    globalObjectId = snapshot.globalObjectId,
                    objectName = go.name,
                    typeName = "GameObject",
                    type = SnapshotType.Deleted,
                    primitiveType = snapshot.primitiveType,
                    gameObjectHierarchy = CaptureGameObjectHierarchy(go)
                }));
                Undo.DestroyObjectImmediate(go);
                return true;
            }

            // 撤销"创建资产或文件夹"
            if (!string.IsNullOrEmpty(snapshot.assetPath))
            {
                if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string fullPath))
                    return false;

                bool isFolder = Directory.Exists(fullPath);
                if (isFolder)
                {
                    if (Directory.GetFileSystemEntries(fullPath).Length > 0 && !snapshot.deleteRecursively)
                    {
                        SkillsLogger.LogWarning($"[WorkflowManager] Cannot undo created folder, not empty: {snapshot.assetPath}");
                        return false;
                    }

                    return DeleteExistingAssetToInverse(snapshot, redoTask);
                }

                if (File.Exists(fullPath))
                {
                    return DeleteExistingAssetToInverse(snapshot, redoTask);
                }

                return false;
            }

            // 撤销"创建通用对象"
            if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId genericGid))
                return false;

            var genericObj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(genericGid);
            if (genericObj == null) return false;

            if (genericObj is GameObject go2) Undo.DestroyObjectImmediate(go2);
            else if (genericObj is Component comp2) Undo.DestroyObjectImmediate(comp2);
            else Undo.DestroyObjectImmediate(genericObj);

            return true;
        }

        private static bool RedoCreatedSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
            if (!string.IsNullOrEmpty(snapshot.componentTypeName) &&
                !string.IsNullOrEmpty(snapshot.parentGameObjectId))
            {
                if (TryResolveObject(snapshot.parentGameObjectId, snapshot.parentGameObjectInstanceId) is GameObject go)
                {
                    var compType = Type.GetType(snapshot.componentTypeName) ??
                                   ComponentSkills.FindComponentType(snapshot.componentTypeName);
                    if (compType != null)
                    {
                        var comp = Undo.AddComponent(go, compType);
                        if (comp != null && !string.IsNullOrEmpty(snapshot.originalJson))
                        {
                            EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, comp);
                            RestoreObjectReferences(comp, snapshot.objectReferencesCaptured,
                                snapshot.objectReferences, null);
                        }

                        newTask.snapshots.Add(new ObjectSnapshot
                        {
                            globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(comp).ToString(),
                            objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                            originalJson = "",
                            objectName = snapshot.objectName,
                            typeName = snapshot.typeName,
                            type = SnapshotType.Created,
                            componentTypeName = snapshot.componentTypeName,
                            parentGameObjectId = snapshot.parentGameObjectId,
                            parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go)
                        });
                        NotifyComponentTopologyChanged(go, compType);
                        return true;
                    }
                }
                return false;
            }

            if (snapshot.typeName == "GameObject")
            {
                var newGo = snapshot.gameObjectHierarchy?.Count > 0
                    ? RestoreGameObjectHierarchy(snapshot.gameObjectHierarchy)
                    : RecreateGameObject(snapshot);
                if (newGo == null) return false;
                newTask.snapshots.Add(CaptureGameObjectState(newGo, new ObjectSnapshot
                {
                    globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(newGo).ToString(),
                    originalJson = EditorJsonUtility.ToJson(newGo),
                    objectName = newGo.name,
                    typeName = "GameObject",
                    type = SnapshotType.Created,
                    primitiveType = snapshot.primitiveType,
                    gameObjectHierarchy = CaptureGameObjectHierarchy(newGo)
                }));
                return true;
            }

            // 资产/文件夹分支：资产或文件夹的 Created 快照只可能经 UndoDeletedSnapshot 进入重做栈
            // （撤销了一次删除，资产被还原）。反转那次撤销，就是带一份新的内容寻址备份重新删掉该资产/文件夹，
            // 并压入一条正确的 Deleted 逆操作——这正是 UndoCreatedSnapshot 的资产/文件夹分支所做的事。
            // 委派给它可以避开早先那个 bug：被还原的文件（没有 fileHash）被误判成文件夹、未备份即删除，
            // 从而破坏下一次撤销。上面的组件与 GameObject 分支刻意不是自逆的（销毁 vs 重建），
            // 在到达此处之前就已处理完。
            if (!string.IsNullOrEmpty(snapshot.assetPath))
            {
                return UndoCreatedSnapshot(snapshot, newTask);
            }

            return false;
        }

        private static bool DeleteExistingAssetToInverse(ObjectSnapshot snapshot, WorkflowTask inverseTask)
        {
            if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string fullPath))
                return false;

            bool isDirectory = Directory.Exists(fullPath);
            List<WorkflowStoredPath> directoryEntries = null;
            string fileHash = null;
            string metaHash = null;
            if (isDirectory)
            {
                directoryEntries = CaptureDirectoryEntries(fullPath);
                if (directoryEntries == null) return false;
            }
            else
            {
                fileHash = WorkflowFileStore.StoreFile(snapshot.assetPath, move: false, out metaHash);
                if (string.IsNullOrEmpty(fileHash)) return false;
            }

            if (!AssetDatabase.DeleteAsset(snapshot.assetPath))
                return false;

            inverseTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Deleted,
                assetPath = snapshot.assetPath,
                fileHash = fileHash,
                metaFileHash = metaHash,
                isDirectory = isDirectory,
                directoryEntries = directoryEntries ?? new List<WorkflowStoredPath>()
            });
            return true;
        }

        private static bool UndoDeletedSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            if (snapshot.typeName == "GameObject" && snapshot.gameObjectHierarchy?.Count > 0)
            {
                var restored = RestoreGameObjectHierarchy(snapshot.gameObjectHierarchy);
                if (restored == null) return false;
                SnapshotCreatedInverse(restored, redoTask);
                return true;
            }

            if (!string.IsNullOrEmpty(snapshot.componentTypeName) &&
                !string.IsNullOrEmpty(snapshot.parentGameObjectId))
            {
                var parent = TryResolveObject(snapshot.parentGameObjectId,
                    snapshot.parentGameObjectInstanceId) as GameObject;
                var componentType = Type.GetType(snapshot.componentTypeName) ?? ComponentSkills.FindComponentType(snapshot.componentTypeName);
                if (parent == null || componentType == null) return false;

                var restored = Undo.AddComponent(parent, componentType);
                if (restored == null) return false;
                if (!string.IsNullOrEmpty(snapshot.originalJson))
                {
                    EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, restored);
                    RestoreObjectReferences(restored, snapshot.objectReferencesCaptured,
                        snapshot.objectReferences, null);
                }
                redoTask.snapshots.Add(new ObjectSnapshot
                {
                    globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(restored).ToString(),
                    objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(restored),
                    objectName = restored.name,
                    typeName = restored.GetType().Name,
                    type = SnapshotType.Created,
                    componentTypeName = restored.GetType().AssemblyQualifiedName,
                    parentGameObjectId = snapshot.parentGameObjectId,
                    parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(parent)
                });
                NotifyComponentTopologyChanged(parent, componentType);
                return true;
            }

            if (string.IsNullOrEmpty(snapshot.assetPath))
                return false;

            if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string fullPath))
                return false;

            bool isFolder = snapshot.isDirectory ||
                            (string.IsNullOrEmpty(snapshot.fileHash) && snapshot.directoryEntries?.Count == 0);

            if (File.Exists(fullPath) || (isFolder && Directory.Exists(fullPath)))
                return false; // 目标已存在

            if (!isFolder && !string.IsNullOrEmpty(snapshot.fileHash))
            {
                redoTask.snapshots.Add(new ObjectSnapshot
                {
                    globalObjectId = snapshot.globalObjectId,
                    objectName = snapshot.objectName,
                    typeName = snapshot.typeName,
                    type = SnapshotType.Created,
                    assetPath = snapshot.assetPath
                });

                return WorkflowFileStore.RestoreFile(snapshot.fileHash, snapshot.metaFileHash,
                    snapshot.assetPath, removeFromStore: false);
            }

            if (isFolder)
            {
                if (snapshot.directoryEntries != null && snapshot.directoryEntries.Count > 0)
                {
                    if (!RestoreDirectorySnapshot(snapshot, fullPath))
                        return false;
                }
                else
                {
                    string parentPath = Path.GetDirectoryName(snapshot.assetPath).Replace('\\', '/');
                    string folderName = Path.GetFileName(snapshot.assetPath.TrimEnd('/', '\\'));
                    AssetDatabase.CreateFolder(parentPath, folderName);
                }

                redoTask.snapshots.Add(new ObjectSnapshot
                {
                    globalObjectId = snapshot.globalObjectId,
                    objectName = snapshot.objectName,
                    typeName = snapshot.typeName,
                    type = SnapshotType.Created,
                    assetPath = snapshot.assetPath,
                    deleteRecursively = true
                });
                return true;
            }

            return false;
        }

        private static bool RedoDeletedSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
            // 重做是反转撤销，不是重跑原操作。Deleted 快照只可能经 UndoCreatedSnapshot 进入重做栈
            // （撤销一次创建 = 删掉该资产/文件夹并记下一条 Deleted 逆操作）。反转那次撤销就是把资产/文件夹
            // 还原回来，而这恰恰就是 UndoDeletedSnapshot 干的事，所以"重做一条 Deleted 快照"与
            // "撤销一条 Deleted 快照"完全相同。
            return UndoDeletedSnapshot(snapshot, newTask);
        }

        private static bool UndoMovedSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            if (string.IsNullOrEmpty(snapshot.assetPath) || string.IsNullOrEmpty(snapshot.previousAssetPath))
                return false;

            string result = AssetDatabase.MoveAsset(snapshot.assetPath, snapshot.previousAssetPath);
            if (!string.IsNullOrEmpty(result))
                return false;

            redoTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Moved,
                assetPath = snapshot.previousAssetPath,
                previousAssetPath = snapshot.assetPath
            });
            return true;
        }

        private static bool RedoMovedSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
            // Moved 快照是自逆的：撤销它就是把资产移回去，并记下一条两个路径互换的快照。
            // 反转那次撤销还是同一个操作，所以重做就是对重做栈上那条已互换的快照再跑一遍撤销逻辑。
            return UndoMovedSnapshot(snapshot, newTask);
        }

        /// <summary>
        /// 撤销一次设置变更：先把当前（变更后）值捕获进重做快照，
        /// 再经设置还原注册表恢复已记录的旧值。
        /// </summary>
        private static bool UndoSettingSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(snapshot.settingKey))
            {
                error = "Setting snapshot has no settingKey";
                return false;
            }

            if (!WorkflowSettingRestorerRegistry.IsRegistered(snapshot.settingKey))
            {
                error = $"No restorer registered for setting '{snapshot.settingKey}'. " +
                        "The owning skill's registration runs on domain load; re-run the skill or reload if this persists.";
                return false;
            }

            // 捕获当前值（即那次变更设成的值），供重做时重新应用。
            string redoValueJson = WorkflowSettingRestorerRegistry.TryGetCurrentValue(snapshot.settingKey);

            if (!WorkflowSettingRestorerRegistry.TryRestore(snapshot.settingKey, snapshot.settingOldValueJson))
            {
                error = $"Restorer for setting '{snapshot.settingKey}' failed to apply the old value.";
                return false;
            }

            redoTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Setting,
                settingKey = snapshot.settingKey,
                settingOldValueJson = redoValueJson
            });
            return true;
        }

        /// <summary>
        /// 重做一次设置变更：重新应用撤销时捕获的值，
        /// 同时把重做前的值捕获进新快照，使之后的撤销仍然可逆。
        /// </summary>
        private static bool RedoSettingSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(snapshot.settingKey))
            {
                error = "Setting snapshot has no settingKey";
                return false;
            }

            if (!WorkflowSettingRestorerRegistry.IsRegistered(snapshot.settingKey))
            {
                error = $"No restorer registered for setting '{snapshot.settingKey}'. " +
                        "The owning skill's registration runs on domain load; re-run the skill or reload if this persists.";
                return false;
            }

            // 捕获当前值（重做前，即旧值），供随后的撤销还原。
            string undoValueJson = WorkflowSettingRestorerRegistry.TryGetCurrentValue(snapshot.settingKey);

            if (snapshot.settingOldValueJson == null && undoValueJson == null)
            {
                error = $"Setting '{snapshot.settingKey}' has no redo value to re-apply.";
                return false;
            }

            if (!WorkflowSettingRestorerRegistry.TryRestore(snapshot.settingKey, snapshot.settingOldValueJson))
            {
                error = $"Restorer for setting '{snapshot.settingKey}' failed to re-apply the value.";
                return false;
            }

            newTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Setting,
                settingKey = snapshot.settingKey,
                settingOldValueJson = undoValueJson
            });
            return true;
        }

        #endregion

        #region Internal Helpers

        /// <summary>
        /// 从活的 GameObject 上捕获 transform 与组件数据，生成新的 ObjectSnapshot，
        /// 基础字段从传入的 baseSnapshot 复制。
        /// </summary>
        private static ObjectSnapshot CaptureGameObjectState(GameObject go, ObjectSnapshot baseSnapshot)
        {
            var t = go.transform;
            var result = new ObjectSnapshot
            {
                globalObjectId = baseSnapshot.globalObjectId,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
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
                        globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(comp).ToString(),
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
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
                globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                transformGlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(transform).ToString(),
                transformInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(transform),
                name = go.name,
                parentIndex = parentIndex,
                activeSelf = go.activeSelf,
                layer = go.layer,
                tag = go.tag,
                siblingIndex = transform.GetSiblingIndex(),
                externalParentGlobalObjectId = parentIndex < 0 && transform.parent != null
                    ? GlobalObjectId.GetGlobalObjectIdSlow(transform.parent.gameObject).ToString()
                    : null,
                externalParentInstanceId = parentIndex < 0 && transform.parent != null
                    ? UnityObjectIdUtility.GetLegacyInstanceId(transform.parent.gameObject)
                    : 0,
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
                        globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(component).ToString(),
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(component),
                        objectReferencesCaptured = objectReferencesCaptured,
                        objectReferences = objectReferences
                    });
                }
                catch { /* unsupported component serialization is non-fatal */ }
            }

            foreach (Transform child in transform)
                CaptureGameObjectNode(child.gameObject, currentIndex, nodes);
        }

        private static GameObject RestoreGameObjectHierarchy(List<GameObjectSnapshotData> nodes)
        {
            if (nodes == null || nodes.Count == 0) return null;
            var restored = new List<GameObject>(nodes.Count);
            var restoredObjects = new RestoredObjectMap();
            var restoredComponents = new Dictionary<ComponentData, Component>();

            for (int i = 0; i < nodes.Count; i++)
            {
                var data = nodes[i];
                Transform parent = data.parentIndex >= 0 && data.parentIndex < restored.Count
                    ? restored[data.parentIndex].transform
                    : (TryResolveObject(data.externalParentGlobalObjectId, data.externalParentInstanceId) as GameObject)?.transform;

                var go = new GameObject(data.name);
                if (i == 0) Undo.RegisterCreatedObjectUndo(go, "Restore Workflow GameObject");
                go.transform.SetParent(parent, false);
                go.transform.localPosition = new Vector3(data.posX, data.posY, data.posZ);
                go.transform.localRotation = new Quaternion(data.rotX, data.rotY, data.rotZ, data.rotW);
                go.transform.localScale = new Vector3(data.scaleX, data.scaleY, data.scaleZ);
                go.layer = data.layer;
                try { go.tag = data.tag; } catch { }
                go.SetActive(data.activeSelf);
                go.transform.SetSiblingIndex(Mathf.Max(0, data.siblingIndex));
                restored.Add(go);
                restoredObjects.Add(data.globalObjectId, data.objectInstanceId, go);
                restoredObjects.Add(data.transformGlobalObjectId, data.transformInstanceId, go.transform);

                foreach (var componentData in data.components ?? new List<ComponentData>())
                {
                    var componentType = Type.GetType(componentData.typeName) ?? ComponentSkills.FindComponentType(componentData.typeName);
                    if (componentType == null || !typeof(Component).IsAssignableFrom(componentType)) continue;
                    var component = Undo.AddComponent(go, componentType);
                    if (component == null) continue;
                    NotifyComponentTopologyChanged(go, componentType);
                    restoredComponents[componentData] = component;
                    restoredObjects.Add(componentData.globalObjectId,
                        componentData.objectInstanceId, component);
                }
            }

            // 引用可能前向指向第一遍时还不存在的子对象或组件，所以必须等整个层级重建完毕后再反序列化。
            foreach (var pair in restoredComponents)
            {
                var componentData = pair.Key;
                var component = pair.Value;
                if (component == null || string.IsNullOrEmpty(componentData.json)) continue;
                EditorJsonUtility.FromJsonOverwrite(componentData.json, component);
                RestoreObjectReferences(component, componentData.objectReferencesCaptured,
                    componentData.objectReferences, null, restoredObjects);
            }
            return restored[0];
        }

        private static void SnapshotCreatedInverse(GameObject go, WorkflowTask inverseTask)
        {
            inverseTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                originalJson = EditorJsonUtility.ToJson(go),
                objectName = go.name,
                typeName = "GameObject",
                type = SnapshotType.Created,
                gameObjectHierarchy = CaptureGameObjectHierarchy(go)
            });
        }

        private static bool RestoreDirectorySnapshot(ObjectSnapshot snapshot, string rootFullPath)
        {
            try
            {
                Directory.CreateDirectory(rootFullPath);
                foreach (var entry in snapshot.directoryEntries.Where(e => e != null && e.isDirectory)
                             .OrderBy(e => e.relativePath?.Length ?? 0))
                {
                    string path = string.IsNullOrEmpty(entry.relativePath)
                        ? rootFullPath
                        : Path.Combine(rootFullPath, entry.relativePath);
                    Directory.CreateDirectory(path);
                    if (!string.IsNullOrEmpty(entry.metaFileHash) &&
                        !WorkflowFileStore.RestoreBlob(entry.metaFileHash, path + ".meta"))
                        return false;
                }

                foreach (var entry in snapshot.directoryEntries.Where(e => e != null && !e.isDirectory))
                {
                    string path = Path.Combine(rootFullPath, entry.relativePath);
                    if (!WorkflowFileStore.RestoreBlob(entry.fileHash, path))
                        return false;
                    if (!string.IsNullOrEmpty(entry.metaFileHash) &&
                        !WorkflowFileStore.RestoreBlob(entry.metaFileHash, path + ".meta"))
                        return false;
                }

                AssetDatabase.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[WorkflowManager] Failed to restore directory {snapshot.assetPath}: {ex.Message}");
                return false;
            }
        }

        private static UnityEngine.Object TryResolveObject(string globalObjectId, int instanceId)
        {
            if (!string.IsNullOrEmpty(globalObjectId) &&
                GlobalObjectId.TryParse(globalObjectId, out GlobalObjectId gid))
            {
                var persisted = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                if (persisted != null) return persisted;
            }

            return instanceId != 0 ? UnityObjectIdUtility.ObjectIdToObject(instanceId) : null;
        }

        // CaptureObjectReferences 遍历的预算上限。对序列化数据是 [SerializeReference] 图的资产而言，
        // 全深度 SerializedProperty 下钻是无界的：VisualTreeAsset（每个导入的 .uxml）会让
        // iterator.Next(true) 永远追着托管引用走下去，把主线程钉在 100% CPU 上，除了杀编辑器别无出路。
        // 下面的托管引用去重才是真正的环路阻断；节点数/时间上限只是给尚未见过的其他病态结构兜底。
        private const int MaxReferenceWalkNodes = 50000;
        private const int MaxReferenceWalkDepth = 32;
        private const int MaxReferenceWalkMilliseconds = 2000;

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
                // 托管引用构成的是图而不是树：同一实例可由多条路径抵达，也可以引用自身。
                // 每个 referenceId 只访问一次，就把这张图变回一次有限遍历。反正重复访问也提供不了新的可还原属性路径。
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
                            ? GlobalObjectId.GetGlobalObjectIdSlow(referencedObject).ToString()
                            : string.Empty,
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(referencedObject)
                    });
                }

                if (truncated)
                {
                    // 采到一部分仍然有用：撤销是按属性路径还原对象引用的，已收集到的路径各自依然有效。
                    // 资产还额外带有内容寻址的文件备份，那才是它们真正的还原途径。
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

        private static bool IsRestorableObjectReferencePath(string propertyPath)
        {
            switch (propertyPath)
            {
                case "m_Script":
                case "m_GameObject":
                case "m_CorrespondingSourceObject":
                case "m_PrefabInstance":
                case "m_PrefabAsset":
                    return false;
                default:
                    return true;
            }
        }

        private static void RestoreObjectReferences(UnityEngine.Object obj, bool referencesCaptured,
            List<ObjectReferenceData> capturedReferences, List<ObjectReferenceData> legacyReferences,
            RestoredObjectMap restoredObjects = null)
        {
            var references = referencesCaptured ? capturedReferences : legacyReferences;
            if (obj == null || references == null) return;

            var serializedObject = new SerializedObject(obj);
            bool changed = false;
            foreach (var reference in references)
            {
                if (reference == null || string.IsNullOrEmpty(reference.propertyPath) ||
                    !IsRestorableObjectReferencePath(reference.propertyPath))
                    continue;
                var property = serializedObject.FindProperty(reference.propertyPath);
                if (property == null || property.propertyType != SerializedPropertyType.ObjectReference) continue;
                property.objectReferenceValue = restoredObjects?.Resolve(reference.globalObjectId,
                    reference.objectInstanceId) ?? TryResolveObject(reference.globalObjectId,
                        reference.objectInstanceId);
                changed = true;
            }

            if (changed)
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private sealed class RestoredObjectMap
        {
            private readonly Dictionary<string, UnityEngine.Object> _byGlobalId =
                new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            private readonly Dictionary<int, UnityEngine.Object> _byInstanceId =
                new Dictionary<int, UnityEngine.Object>();

            public void Add(string globalObjectId, int instanceId, UnityEngine.Object obj)
            {
                if (obj == null) return;
                if (!string.IsNullOrEmpty(globalObjectId)) _byGlobalId[globalObjectId] = obj;
                if (instanceId != 0) _byInstanceId[instanceId] = obj;
            }

            public UnityEngine.Object Resolve(string globalObjectId, int instanceId)
            {
                if (instanceId != 0 && _byInstanceId.TryGetValue(instanceId, out var byInstance) &&
                    byInstance != null)
                    return byInstance;
                if (!string.IsNullOrEmpty(globalObjectId) &&
                    _byGlobalId.TryGetValue(globalObjectId, out var byGlobal) && byGlobal != null)
                    return byGlobal;
                return null;
            }
        }

        /// <summary>
        /// 把被修改对象的当前状态捕获进 targetTask，然后还原快照数据
        /// （经文件库还原、遗留 base64 或 JSON 覆盖三条途径之一）。
        /// </summary>
        private static bool RestoreModifiedSnapshot(ObjectSnapshot snapshot, WorkflowTask targetTask,
            bool removeFromStore, string undoLabel)
        {
            UnityEngine.Object obj = null;
            obj = TryResolveObject(snapshot.globalObjectId, snapshot.objectInstanceId);

            // 遗留的删除快照被记成了 Modified。此时对象已解析不到，但存下的字节足以还原它并生成正确的逆操作。
            if (obj == null && !string.IsNullOrEmpty(snapshot.assetPath) &&
                (!string.IsNullOrEmpty(snapshot.fileHash) || !string.IsNullOrEmpty(snapshot.assetBytesBase64)))
            {
                if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string missingFullPath) ||
                    File.Exists(missingFullPath))
                    return false;

                bool restored;
                if (!string.IsNullOrEmpty(snapshot.assetBytesBase64))
                {
                    string parentDirectory = Path.GetDirectoryName(missingFullPath);
                    if (!string.IsNullOrEmpty(parentDirectory)) Directory.CreateDirectory(parentDirectory);
                    File.WriteAllBytes(missingFullPath, Convert.FromBase64String(snapshot.assetBytesBase64));
                    AssetDatabase.ImportAsset(snapshot.assetPath, ImportAssetOptions.ForceUpdate);
                    restored = true;
                }
                else
                {
                    restored = WorkflowFileStore.RestoreFile(snapshot.fileHash, snapshot.metaFileHash,
                        snapshot.assetPath, removeFromStore);
                }

                if (!restored) return false;
                targetTask.snapshots.Add(new ObjectSnapshot
                {
                    globalObjectId = snapshot.globalObjectId,
                    objectName = snapshot.objectName,
                    typeName = snapshot.typeName,
                    type = SnapshotType.Created,
                    assetPath = snapshot.assetPath
                });
                return true;
            }

            if (obj == null) return false;

            // 为目标任务捕获当前状态（含文件库备份）
            string currentFileHash = "";
            string currentMetaHash = "";
            if (!string.IsNullOrEmpty(snapshot.assetPath))
            {
                if (WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string currentAssetPath) && File.Exists(currentAssetPath))
                {
                    currentFileHash = WorkflowFileStore.StoreFile(snapshot.assetPath, move: false, out currentMetaHash);
                }
            }

            var objectReferences = CaptureObjectReferences(obj, out bool objectReferencesCaptured);
            if (!snapshot.objectReferencesCaptured && !objectReferencesCaptured)
                return false;
            targetTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(obj),
                originalJson = EditorJsonUtility.ToJson(obj),
                objectReferencesCaptured = objectReferencesCaptured,
                objectReferences = objectReferences,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Modified,
                assetPath = snapshot.assetPath,
                fileHash = currentFileHash,
                metaFileHash = currentMetaHash
            });

            // 存在遗留 base64 备份时优先使用（旧历史数据）
            if (!string.IsNullOrEmpty(snapshot.assetBytesBase64) && !string.IsNullOrEmpty(snapshot.assetPath))
            {
                if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string fullPath))
                {
                    SkillsLogger.LogWarning($"{SkillsLogger.PREFIX_WARNING} Skipping unsafe workflow restore path: {snapshot.assetPath}");
                    return false;
                }

                File.WriteAllBytes(fullPath, Convert.FromBase64String(snapshot.assetBytesBase64));
                AssetDatabase.ImportAsset(snapshot.assetPath);
                return true;
            }

            // 从内容寻址文件库还原
            if (!string.IsNullOrEmpty(snapshot.fileHash) && !string.IsNullOrEmpty(snapshot.assetPath))
            {
                return WorkflowFileStore.RestoreFile(snapshot.fileHash, snapshot.metaFileHash,
                    snapshot.assetPath, removeFromStore);
            }

            // 没有文件备份的场景对象/资产退回 JSON 覆盖
            if (!string.IsNullOrEmpty(snapshot.originalJson))
            {
                Undo.RecordObject(obj, undoLabel);
                var legacyReferences = snapshot.objectReferencesCaptured ? null : objectReferences;
                EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, obj);
                RestoreObjectReferences(obj, snapshot.objectReferencesCaptured,
                    snapshot.objectReferences, legacyReferences);
                EditorUtility.SetDirty(obj);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 依据快照数据（primitiveType、transform、组件）重建 GameObject，
        /// 并把新对象登记进 Unity 的 Undo 系统。
        /// </summary>
        private static GameObject RecreateGameObject(ObjectSnapshot snapshot)
        {
            GameObject newGo;

            if (!string.IsNullOrEmpty(snapshot.primitiveType) &&
                Enum.TryParse<PrimitiveType>(snapshot.primitiveType, out var pt))
            {
                newGo = GameObject.CreatePrimitive(pt);
            }
            else
            {
                newGo = new GameObject();
            }

            newGo.name = snapshot.objectName;

            newGo.transform.position = new Vector3(snapshot.posX, snapshot.posY, snapshot.posZ);
            newGo.transform.rotation = new Quaternion(snapshot.rotX, snapshot.rotY, snapshot.rotZ, snapshot.rotW);
            newGo.transform.localScale = new Vector3(snapshot.scaleX, snapshot.scaleY, snapshot.scaleZ);

            if (snapshot.components != null)
            {
                foreach (var compData in snapshot.components)
                {
                    if (string.IsNullOrEmpty(compData.typeName)) continue;
                    var compType = Type.GetType(compData.typeName);
                    if (compType == null) compType = ComponentSkills.FindComponentType(compData.typeName);
                    if (compType == null) continue;

                    // 组件已存在则跳过（如基元自带的 MeshRenderer）
                    var existing = newGo.GetComponent(compType);
                    if (existing != null)
                    {
                        if (!string.IsNullOrEmpty(compData.json))
                        {
                            EditorJsonUtility.FromJsonOverwrite(compData.json, existing);
                            RestoreObjectReferences(existing, compData.objectReferencesCaptured,
                                compData.objectReferences, null);
                        }
                    }
                    else
                    {
                        var comp = newGo.AddComponent(compType);
                        if (comp != null && !string.IsNullOrEmpty(compData.json))
                        {
                            EditorJsonUtility.FromJsonOverwrite(compData.json, comp);
                            RestoreObjectReferences(comp, compData.objectReferencesCaptured,
                                compData.objectReferences, null);
                        }
                    }
                }
            }

            Undo.RegisterCreatedObjectUndo(newGo, "Redo Create " + snapshot.objectName);

            return newGo;
        }

        /// <summary>
        /// 收集在途任务，以及所有活动任务与已撤销任务所引用的全部文件哈希。
        /// </summary>
        private static HashSet<string> CollectReferencedHashes()
        {
            // 忽略大小写：库条目枚举时统一转大写，所以仅大小写不同的快照哈希也必须算作一条引用。
            var referencedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 正在记录的那个任务还不在 _history 里——历史是懒加载的，且 LoadHistory 与 EndTask 都在
            // 追加它之前就先回收——所以它靠这里来保护。
            AddTaskHashes(_currentTask, referencedHashes);

            if (_history == null) return referencedHashes;

            foreach (var task in _history.tasks.Concat(_history.undoneStack))
            {
                AddTaskHashes(task, referencedHashes);
            }
            return referencedHashes;
        }

        private static void AddTaskHashes(WorkflowTask task, HashSet<string> hashes)
        {
            if (task?.snapshots == null) return;
            foreach (var snapshot in task.snapshots)
            {
                AddSnapshotHashes(snapshot, hashes);
            }
        }

        private static void AddSnapshotHashes(ObjectSnapshot snapshot, HashSet<string> hashes)
        {
            if (snapshot == null || hashes == null) return;
            if (!string.IsNullOrEmpty(snapshot.fileHash)) hashes.Add(snapshot.fileHash);
            if (!string.IsNullOrEmpty(snapshot.metaFileHash)) hashes.Add(snapshot.metaFileHash);
            if (snapshot.directoryEntries == null) return;
            foreach (var entry in snapshot.directoryEntries)
            {
                if (entry == null) continue;
                if (!string.IsNullOrEmpty(entry.fileHash)) hashes.Add(entry.fileHash);
                if (!string.IsNullOrEmpty(entry.metaFileHash)) hashes.Add(entry.metaFileHash);
            }
        }

        #endregion

        #region Auto-Cleanup

        /// <summary>
        /// 按 WorkflowAutoCleanConfig 的设置修剪工作流历史与内容寻址文件库。
        /// 在 EndTask 与 LoadHistory 之后自动调用。
        /// </summary>
        public static WorkflowTrimReport TrimHistoryIfNeeded(bool force = false)
        {
            var report = new WorkflowTrimReport();
            if (_history == null) LoadHistory();
            if (!force && !WorkflowAutoCleanConfig.Enabled)
                return report;

            if (_historyRecoveryMode)
            {
                if (force)
                {
                    SkillsLogger.LogWarning(
                        "Workflow cleanup skipped: the history file failed to load this session, so the set of " +
                        "referenced backups is incomplete. Clear the history to re-enable cleanup.");
                }
                return report;
            }

            var now = DateTimeOffset.Now;
            int maxAgeDays = WorkflowAutoCleanConfig.MaxTaskAgeDays;
            int maxTasks = WorkflowAutoCleanConfig.MaxTasks;
            long maxHistoryBytes = WorkflowAutoCleanConfig.MaxHistoryMB * 1024L * 1024L;

            int beforeCount = _history.tasks.Count + _history.undoneStack.Count;

            // 删除早于 MaxTaskAgeDays 的任务
            if (maxAgeDays > 0)
            {
                long cutoff = now.AddDays(-maxAgeDays).ToUnixTimeSeconds();
                _history.tasks.RemoveAll(t => t?.timestamp < cutoff);
                _history.undoneStack.RemoveAll(t => t?.timestamp < cutoff);
            }

            // 从最旧开始删，直到任务数低于 MaxTasks
            if (maxTasks > 0)
            {
                while (_history.tasks.Count > maxTasks)
                    _history.tasks.RemoveAt(0);
                while (_history.undoneStack.Count > maxTasks)
                    _history.undoneStack.RemoveAt(0);
            }

            // 从最旧开始删，直到估算的序列化大小低于 MaxHistoryMB
            if (maxHistoryBytes > 0)
            {
                long currentBytes = EstimateHistorySizeBytes();
                while (currentBytes > maxHistoryBytes && (_history.tasks.Count > 0 || _history.undoneStack.Count > 0))
                {
                    var oldest = GetOldestTask(out bool fromActive);
                    if (oldest == null) break;
                    currentBytes -= EstimateTaskSizeBytes(oldest);
                    if (fromActive) _history.tasks.RemoveAt(0);
                    else _history.undoneStack.RemoveAt(0);
                }
            }

            int afterCount = _history.tasks.Count + _history.undoneStack.Count;
            report.removedTasks = beforeCount - afterCount;

            // 回收无引用的文件库条目
            var referencedHashes = CollectReferencedHashes();
            long beforeBytes = WorkflowFileStore.GetStoreSizeBytes();
            WorkflowFileStore.CollectGarbage(referencedHashes, out int reclaimedCount, out _);
            report.reclaimedFileEntries = reclaimedCount;

            // 按存放时长与总大小修剪文件库
            int storeMaxAgeDays = WorkflowAutoCleanConfig.StoreMaxAgeDays;
            long maxStoreBytes = WorkflowAutoCleanConfig.MaxStoreMB > 0
                ? WorkflowAutoCleanConfig.MaxStoreMB * 1024L * 1024L
                : 0;
            if (storeMaxAgeDays > 0 || maxStoreBytes > 0)
            {
                DateTime? storeCutoff = storeMaxAgeDays > 0
                    ? now.AddDays(-storeMaxAgeDays).UtcDateTime
                    : (DateTime?)null;
                report.reclaimedFileEntries += WorkflowFileStore.PruneByAgeAndSize(
                    storeCutoff, maxStoreBytes, referencedHashes);
            }

            long afterBytes = WorkflowFileStore.GetStoreSizeBytes();
            report.reclaimedBytes = beforeBytes - afterBytes;

            if (report.removedTasks > 0 || report.reclaimedBytes > 0)
            {
                SkillsLogger.LogWorkflow($"Trimmed {report.removedTasks} tasks, reclaimed {FormatBytes(report.reclaimedBytes)} from file store");
            }

            return report;
        }

        private static long EstimateHistorySizeBytes()
        {
            long total = 0;
            foreach (var task in _history.tasks)
                total += EstimateTaskSizeBytes(task);
            foreach (var task in _history.undoneStack)
                total += EstimateTaskSizeBytes(task);
            return total;
        }

        private static long EstimateTaskSizeBytes(WorkflowTask task)
        {
            if (task?.snapshots == null) return 0;
            long size = 64; // 任务元数据开销
            foreach (var s in task.snapshots)
            {
                if (s == null) continue;
                size += (s.globalObjectId?.Length ?? 0) +
                        (s.originalJson?.Length ?? 0) +
                        (s.objectName?.Length ?? 0) +
                        (s.typeName?.Length ?? 0) +
                        (s.assetPath?.Length ?? 0) +
                        (s.fileHash?.Length ?? 0) +
                        (s.metaFileHash?.Length ?? 0) +
                        (s.previousAssetPath?.Length ?? 0) +
                        (s.assetBytesBase64?.Length ?? 0) +
                        (s.componentTypeName?.Length ?? 0) +
                        (s.parentGameObjectId?.Length ?? 0) +
                        (s.primitiveType?.Length ?? 0) +
                        (s.settingKey?.Length ?? 0) +
                        (s.settingOldValueJson?.Length ?? 0) +
                        64; // 单条快照开销
            }
            return size;
        }

        private static WorkflowTask GetOldestTask(out bool fromActive)
        {
            fromActive = true;
            WorkflowTask oldest = _history.tasks.Count > 0 ? _history.tasks[0] : null;
            WorkflowTask undoneOldest = _history.undoneStack.Count > 0 ? _history.undoneStack[0] : null;

            if (oldest == null)
            {
                oldest = undoneOldest;
                fromActive = false;
            }
            else if (undoneOldest != null && undoneOldest.timestamp < oldest.timestamp)
            {
                oldest = undoneOldest;
                fromActive = false;
            }

            return oldest;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        #endregion

        private static void MigrateHistorySchema()
        {
            if (_history == null)
                return;

            if (_history.schemaVersion >= WorkflowHistoryData.CurrentSchemaVersion)
                return;

            int sourceVersion = _history.schemaVersion;
            var snapshots = _history.tasks.Concat(_history.undoneStack)
                .Where(t => t?.snapshots != null)
                .SelectMany(t => t.snapshots)
                .Where(s => s != null)
                .ToList();

            bool migrationSucceeded = true;
            foreach (var snapshot in snapshots)
            {
                if (!string.IsNullOrEmpty(snapshot.assetBytesBase64))
                {
                    try
                    {
                        string hash = WorkflowFileStore.StoreBytes(Convert.FromBase64String(snapshot.assetBytesBase64));
                        if (string.IsNullOrEmpty(hash) || !WorkflowFileStore.BlobExists(hash))
                        {
                            migrationSucceeded = false;
                            break;
                        }
                        snapshot.fileHash = hash;
                    }
                    catch (Exception ex)
                    {
                        SkillsLogger.LogWarning($"Workflow base64 migration failed for {snapshot.assetPath}: {ex.Message}");
                        migrationSucceeded = false;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(snapshot.fileHash) && string.IsNullOrEmpty(snapshot.metaFileHash))
                    snapshot.metaFileHash = WorkflowFileStore.MigrateLegacyMetaHash(snapshot.fileHash);
            }

            if (!migrationSucceeded)
                return;

            foreach (var snapshot in snapshots)
                snapshot.assetBytesBase64 = null;

            _history.schemaVersion = WorkflowHistoryData.CurrentSchemaVersion;
            SaveHistory();
            SkillsLogger.LogVerbose(
                $"Workflow history schema upgraded: {sourceVersion} -> {WorkflowHistoryData.CurrentSchemaVersion}");
        }

        public static void ClearHistory()
        {
            _history = new WorkflowHistoryData();
            // 仍在记录中的那个任务的 blob 会保留：它本就不属于用户要求清空的那段历史。
            // 其余全部清掉——该技能承诺清空文件库，所以"近期写入"宽限期在此不适用——
            // 这同时也让文件库与历史重新同步，并解除恢复模式。
            WorkflowFileStore.CollectGarbage(CollectReferencedHashes(), out _, out _, includeRecentWrites: true);
            _historyRecoveryMode = false;
            SaveHistory();
        }

        /// <summary>
        /// 返回工作流历史 JSON 文件的磁盘大小（字节）；文件不存在返回 0。
        /// </summary>
        public static long GetHistoryFileSizeBytes()
        {
            try
            {
                return File.Exists(HistoryFilePath) ? new FileInfo(HistoryFilePath).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        internal static void ResetStateForTests()
        {
            _history = null;
            _currentTask = null;
            _currentSessionId = null;
            _historyRecoveryMode = false;
        }
    }
}

// Producer:Betsy
