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
        public string globalObjectId; // String representation of the Unity GlobalObjectId
        public int objectInstanceId;  // Fallback identifier within the same session, for objects in a scene that was never saved
        public string originalJson;   // JSON state captured by EditorJsonUtility
        public bool objectReferencesCaptured;
        public List<ObjectReferenceData> objectReferences = new List<ObjectReferenceData>();
        public string objectName;     // Cached display name
        public string typeName;       // e.g. "GameObject", "Transform"
        public SnapshotType type = SnapshotType.Modified;
        public string assetPath;      // For assets: the in-project path (e.g. "Assets/Materials/Red.mat")
        public string assetBytesBase64; // Base64-encoded asset file backup (legacy field, kept for backward compatibility with old history)

        // Hash into the content-addressed file store, used by Modified/Deleted asset snapshots.
        public string fileHash;
        public string metaFileHash;

        // A deleted folder is represented by one root snapshot plus several content-addressed entries.
        public bool isDirectory;
        public bool deleteRecursively;
        public List<WorkflowStoredPath> directoryEntries = new List<WorkflowStoredPath>();

        // For the Moved type: the original asset path before the move.
        public string previousAssetPath;

        // Reserved for future setting-type snapshots.
        public string settingKey;
        public string settingOldValueJson;

        // For undoing a Created-type component: extra info needed for reliable deletion
        public string componentTypeName;   // The component's fully qualified type name (e.g. "UnityEngine.Rigidbody")
        public string parentGameObjectId;  // The parent GameObject's GlobalObjectId
        public int parentGameObjectInstanceId;

        // For redoing a Created-type GameObject: info needed to recreate it
        public string primitiveType;       // The PrimitiveType name (Cube, Sphere, etc.), or an empty string for an empty GameObject

        // Transform data used to recreate the GameObject
        public float posX, posY, posZ;
        public float rotX, rotY, rotZ, rotW;
        public float scaleX = 1, scaleY = 1, scaleZ = 1;

        // Full component data used to fully restore the GameObject
        public List<ComponentData> components = new List<ComponentData>();

        // Flattened hierarchy data for a deleted/recreated scene GameObject.
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
        public List<WorkflowTask> undoneStack = new List<WorkflowTask>(); // Stack of undone tasks, for redo

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
        public string sessionId;  // Groups tasks from the same conversation/session together
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
        Modified = 0, // Object state was modified
        Created = 1,  // Object was newly created in this task
        Deleted = 2,  // Object was deleted in this task
        Moved = 3,    // Asset was moved in this task
        Setting = 4   // Editor/project setting was modified (restored via WorkflowSettingRestorerRegistry)
    }

    [Serializable]
    public class ComponentData
    {
        public string typeName;      // Fully qualified type name
        public string json;          // Serialized component data
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
    /// The result of undoing/redoing a single snapshot.
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
    /// The aggregate result of undoing/redoing a workflow task or session.
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
    /// The report produced after trimming workflow history and the content-addressed file store.
    /// </summary>
    [Serializable]
    public class WorkflowTrimReport
    {
        public int removedTasks;
        public int reclaimedFileEntries;
        public long reclaimedBytes;
    }

    /// <summary>
    /// Persistent auto-cleanup configuration for workflow history and the file store.
    /// Stored under the "UnitySkills.Workflow.*" EditorPrefs keys.
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
        /// Resets all cleanup settings to their default values.
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
    /// Session info (groups tasks by conversation level).
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
