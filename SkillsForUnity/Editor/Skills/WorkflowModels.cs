using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnitySkills.Internal;

namespace UnitySkills.Internal
{
    [Serializable]
    public class ObjectSnapshot
    {
        public string globalObjectId; // Unity GlobalObjectId 的字符串表示
        public int objectInstanceId;  // 从未保存过的场景中的对象，同会话内的回退标识
        public string originalJson;   // 由 EditorJsonUtility 捕获的 JSON 状态
        public bool objectReferencesCaptured;
        public List<ObjectReferenceData> objectReferences = new List<ObjectReferenceData>();
        public string objectName;     // 缓存的显示名
        public string typeName;       // 如 "GameObject"、"Transform"
        public SnapshotType type = SnapshotType.Modified;
        public string assetPath;      // 资产用：项目内路径（如 "Assets/Materials/Red.mat"）
        public string assetBytesBase64; // Base64 编码的资产文件备份（遗留字段，为兼容旧历史保留）

        // 内容寻址文件库的哈希，用于 Modified/Deleted 资产快照。
        public string fileHash;
        public string metaFileHash;

        // 被删除的文件夹由一条根快照加若干内容寻址条目表示。
        public bool isDirectory;
        public bool deleteRecursively;
        public List<WorkflowStoredPath> directoryEntries = new List<WorkflowStoredPath>();

        // Moved 类型用：移动前的原始资产路径。
        public string previousAssetPath;

        // 预留给后续的设置类快照。
        public string settingKey;
        public string settingOldValueJson;

        // Created 类型的组件撤销用：存放可靠删除所需的额外信息
        public string componentTypeName;   // 组件的完整类型名（如 "UnityEngine.Rigidbody"）
        public string parentGameObjectId;  // 父 GameObject 的 GlobalObjectId
        public int parentGameObjectInstanceId;

        // Created 类型的 GameObject 重做用：存放重建所需信息
        public string primitiveType;       // PrimitiveType 名称（Cube、Sphere 等），空 GameObject 则为空串

        // 重建 GameObject 用的 Transform 数据
        public float posX, posY, posZ;
        public float rotX, rotY, rotZ, rotW;
        public float scaleX = 1, scaleY = 1, scaleZ = 1;

        // 完整还原 GameObject 用的全部组件数据
        public List<ComponentData> components = new List<ComponentData>();

        // 被删除/重建的场景 GameObject 的扁平层级数据。
        public List<GameObjectSnapshotData> gameObjectHierarchy = new List<GameObjectSnapshotData>();
    }

    [Serializable]
    public class WorkflowStoredPath
    {
        public string relativePath;
        public bool isDirectory;
        public string fileHash;
        public string metaFileHash;
    }

    [Serializable]
    public class GameObjectSnapshotData
    {
        public string globalObjectId;
        public int objectInstanceId;
        public string transformGlobalObjectId;
        public int transformInstanceId;
        public string name;
        public int parentIndex = -1;
        public bool activeSelf;
        public int layer;
        public string tag;
        public int siblingIndex;
        public string externalParentGlobalObjectId;
        public int externalParentInstanceId;
        public float posX, posY, posZ;
        public float rotX, rotY, rotZ, rotW;
        public float scaleX = 1, scaleY = 1, scaleZ = 1;
        public List<ComponentData> components = new List<ComponentData>();
    }
}

namespace UnitySkills
{
    [Serializable]
    public class WorkflowHistoryData
    {
        public const int CurrentSchemaVersion = 5;
        public int schemaVersion = CurrentSchemaVersion;
        public List<WorkflowTask> tasks = new List<WorkflowTask>();
        public List<WorkflowTask> undoneStack = new List<WorkflowTask>(); // 已撤销任务栈，供重做使用

        public void EnsureDefaults()
        {
            if (tasks == null) tasks = new List<WorkflowTask>();
            if (undoneStack == null) undoneStack = new List<WorkflowTask>();

            tasks.RemoveAll(task => task == null);
            undoneStack.RemoveAll(task => task == null);

            foreach (var task in tasks)
                task?.EnsureSnapshotIndex();
            foreach (var task in undoneStack)
                task?.EnsureSnapshotIndex();
        }
    }

    [Serializable]
    public class WorkflowTask
    {
        public string id;
        public string tag;
        public string description;
        public long timestamp;
        public string sessionId;  // 把同一次对话/会话的任务归为一组
        public List<ObjectSnapshot> snapshots = new List<ObjectSnapshot>();
        [NonSerialized] private HashSet<string> _snapshotKeys;

        public string GetFormattedTime()
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("HH:mm:ss");
        }

        internal void EnsureSnapshotIndex()
        {
            if (_snapshotKeys != null)
                return;

            _snapshotKeys = new HashSet<string>(StringComparer.Ordinal);
            if (snapshots == null)
            {
                snapshots = new List<ObjectSnapshot>();
                return;
            }

            snapshots.RemoveAll(snapshot => snapshot == null);
            foreach (var snapshot in snapshots)
            {
                if (ShouldDeduplicate(snapshot) && !string.IsNullOrEmpty(snapshot.globalObjectId))
                    _snapshotKeys.Add(GetSnapshotKey(snapshot.globalObjectId, snapshot.type));
            }
        }

        internal bool TryRegisterSnapshot(string globalObjectId, SnapshotType type)
        {
            if (string.IsNullOrEmpty(globalObjectId))
                return false;

            EnsureSnapshotIndex();
            return _snapshotKeys.Add(GetSnapshotKey(globalObjectId, type));
        }

        internal bool HasSnapshot(string globalObjectId, SnapshotType type)
        {
            if (string.IsNullOrEmpty(globalObjectId))
                return false;

            EnsureSnapshotIndex();
            return _snapshotKeys.Contains(GetSnapshotKey(globalObjectId, type));
        }

        internal void InvalidateSnapshotIndex()
        {
            _snapshotKeys = null;
        }

        internal static bool ShouldDeduplicate(ObjectSnapshot snapshot)
        {
            if (snapshot == null) return false;
            return snapshot.type == SnapshotType.Modified ||
                   snapshot.type == SnapshotType.Created ||
                   snapshot.type == SnapshotType.Setting;
        }

        private static string GetSnapshotKey(string globalObjectId, SnapshotType type)
        {
            return ((int)type).ToString() + ":" + globalObjectId;
        }
    }

    public enum SnapshotType
    {
        Modified = 0, // 对象状态被修改
        Created = 1,  // 对象在本任务中新建
        Deleted = 2,  // 对象在本任务中被删除
        Moved = 3,    // 资产在本任务中被移动
        Setting = 4   // 编辑器/项目设置被修改（经 WorkflowSettingRestorerRegistry 还原）
    }

    [Serializable]
    public class ComponentData
    {
        public string typeName;      // 完整类型名
        public string json;          // 序列化后的组件数据
        public string globalObjectId;
        public int objectInstanceId;
        public bool objectReferencesCaptured;
        public List<ObjectReferenceData> objectReferences = new List<ObjectReferenceData>();
    }

    [Serializable]
    public class ObjectReferenceData
    {
        public string propertyPath;
        public string globalObjectId;
        public int objectInstanceId;
    }

    /// <summary>
    /// 单条快照撤销/重做的结果。
    /// </summary>
    [Serializable]
    public class SnapshotUndoResult
    {
        public string globalObjectId;
        public string objectName;
        public bool success;
        public string error;
    }

    /// <summary>
    /// 撤销/重做一个工作流任务或会话的汇总结果。
    /// </summary>
    [Serializable]
    public class TaskUndoResult
    {
        public bool success;
        public int total;
        public int succeeded;
        public int failed;
        public List<SnapshotUndoResult> details = new List<SnapshotUndoResult>();
        public string error;
    }

    /// <summary>
    /// 修剪工作流历史与内容寻址文件库后产生的报告。
    /// </summary>
    [Serializable]
    public class WorkflowTrimReport
    {
        public int removedTasks;
        public int reclaimedFileEntries;
        public long reclaimedBytes;
    }

    /// <summary>
    /// 工作流历史与文件库的持久化自动清理配置。
    /// 存于 EditorPrefs 的 "UnitySkills.Workflow.*" 键下。
    /// </summary>
    public static class WorkflowAutoCleanConfig
    {
        private const string Prefix = "UnitySkills.Workflow.";

        private const string KeyEnabled = Prefix + "Enabled";
        private const string KeyMaxTasks = Prefix + "MaxTasks";
        private const string KeyMaxHistoryMB = Prefix + "MaxHistoryMB";
        private const string KeyMaxTaskAgeDays = Prefix + "MaxTaskAgeDays";
        private const string KeyMaxStoreMB = Prefix + "MaxStoreMB";
        private const string KeyStoreMaxAgeDays = Prefix + "StoreMaxAgeDays";

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(KeyEnabled, true);
            set => EditorPrefs.SetBool(KeyEnabled, value);
        }

        public static int MaxTasks
        {
            get => EditorPrefs.GetInt(KeyMaxTasks, 200);
            set => EditorPrefs.SetInt(KeyMaxTasks, value);
        }

        public static int MaxHistoryMB
        {
            get => EditorPrefs.GetInt(KeyMaxHistoryMB, 32);
            set => EditorPrefs.SetInt(KeyMaxHistoryMB, value);
        }

        public static int MaxTaskAgeDays
        {
            get => EditorPrefs.GetInt(KeyMaxTaskAgeDays, 30);
            set => EditorPrefs.SetInt(KeyMaxTaskAgeDays, value);
        }

        public static int MaxStoreMB
        {
            get => EditorPrefs.GetInt(KeyMaxStoreMB, 512);
            set => EditorPrefs.SetInt(KeyMaxStoreMB, value);
        }

        public static int StoreMaxAgeDays
        {
            get => EditorPrefs.GetInt(KeyStoreMaxAgeDays, 7);
            set => EditorPrefs.SetInt(KeyStoreMaxAgeDays, value);
        }

        /// <summary>
        /// 把所有清理设置恢复为默认值。
        /// </summary>
        public static void ResetToDefaults()
        {
            Enabled = true;
            MaxTasks = 200;
            MaxHistoryMB = 32;
            MaxTaskAgeDays = 30;
            MaxStoreMB = 512;
            StoreMaxAgeDays = 7;
        }
    }

    /// <summary>
    /// 会话信息（把任务按对话层级分组）。
    /// </summary>
    public class SessionInfo
    {
        public string sessionId;
        public int taskCount;
        public int totalChanges;
        public string startTime;
        public string endTime;
        public List<string> tags;
    }
}

// Producer:Betsy
