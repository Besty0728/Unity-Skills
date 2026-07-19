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
        public string globalObjectId; // Unity GlobalObjectId string representation
        public string originalJson;   // JSON state captured via EditorJsonUtility
        public string objectName;     // Cached name for display
        public string typeName;       // e.g. "GameObject", "Transform"
        public SnapshotType type = SnapshotType.Modified;
        public string assetPath;      // For assets: path in project (e.g., "Assets/Materials/Red.mat")
        public string assetBytesBase64; // Base64 encoded asset file backup (legacy, kept for old history compatibility)

        // Content-addressed file store hash. For Modified/Deleted asset snapshots.
        public string fileHash;

        // For Moved type: the original asset path before the move.
        public string previousAssetPath;

        // Reserved for future setting snapshots.
        public string settingKey;
        public string settingOldValueJson;

        // For Created type component undo - stores extra info for reliable deletion
        public string componentTypeName;   // Full type name of the component (e.g., "UnityEngine.Rigidbody")
        public string parentGameObjectId;  // GlobalObjectId of the parent GameObject

        // For Created type GameObject redo - stores info for recreation
        public string primitiveType;       // PrimitiveType name (Cube, Sphere, etc.) or empty for empty GameObject

        // Transform data for GameObject recreation
        public float posX, posY, posZ;
        public float rotX, rotY, rotZ, rotW;
        public float scaleX = 1, scaleY = 1, scaleZ = 1;

        // All components data for full GameObject restoration
        public List<ComponentData> components = new List<ComponentData>();
    }
}

namespace UnitySkills
{
    [Serializable]
    public class WorkflowHistoryData
    {
        public const int CurrentSchemaVersion = 3;
        public int schemaVersion = CurrentSchemaVersion;
        public List<WorkflowTask> tasks = new List<WorkflowTask>();
        public List<WorkflowTask> undoneStack = new List<WorkflowTask>(); // Stack of undone tasks for redo

        public void EnsureDefaults()
        {
            if (schemaVersion <= 0)
                schemaVersion = CurrentSchemaVersion;

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
        public string sessionId;  // Groups tasks belonging to the same conversation/session
        public List<ObjectSnapshot> snapshots = new List<ObjectSnapshot>();
        [NonSerialized] private HashSet<string> _snapshotIds;

        public string GetFormattedTime()
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("HH:mm:ss");
        }

        internal void EnsureSnapshotIndex()
        {
            if (_snapshotIds != null)
                return;

            _snapshotIds = new HashSet<string>(StringComparer.Ordinal);
            if (snapshots == null)
            {
                snapshots = new List<ObjectSnapshot>();
                return;
            }

            snapshots.RemoveAll(snapshot => snapshot == null);
            foreach (var snapshot in snapshots)
            {
                if (!string.IsNullOrEmpty(snapshot.globalObjectId))
                    _snapshotIds.Add(snapshot.globalObjectId);
            }
        }

        internal bool TryRegisterSnapshotId(string globalObjectId)
        {
            if (string.IsNullOrEmpty(globalObjectId))
                return false;

            EnsureSnapshotIndex();
            return _snapshotIds.Add(globalObjectId);
        }

        internal bool HasSnapshotId(string globalObjectId)
        {
            if (string.IsNullOrEmpty(globalObjectId))
                return false;

            EnsureSnapshotIndex();
            return _snapshotIds.Contains(globalObjectId);
        }
    }

    public enum SnapshotType
    {
        Modified = 0, // Object state changed
        Created = 1,  // Object was newly created in this task
        Deleted = 2,  // Object was deleted in this task
        Moved = 3,    // Asset was moved in this task
        Setting = 4   // Editor/project setting changed (restored via WorkflowSettingRestorerRegistry)
    }

    [Serializable]
    public class ComponentData
    {
        public string typeName;      // Full type name
        public string json;          // Serialized component data
    }

    /// <summary>
    /// Result of undoing/redoing a single snapshot.
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
    /// Aggregated result of undoing/redoing a workflow task or session.
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
    /// Report produced by trimming workflow history and the content-addressed file store.
    /// </summary>
    [Serializable]
    public class WorkflowTrimReport
    {
        public int removedTasks;
        public int reclaimedFileEntries;
        public long reclaimedBytes;
    }

    /// <summary>
    /// Persistent auto-cleanup configuration for workflow history and file store.
    /// Backed by EditorPrefs under "UnitySkills.Workflow.*".
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
        /// Reset all cleanup settings to their defaults.
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
    /// Information about a session (conversation-level grouping of tasks).
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
