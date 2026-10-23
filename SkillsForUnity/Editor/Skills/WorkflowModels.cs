using System;
using System.Collections.Generic;
using System.Linq;
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
        public string entityId;       // Same-session fallback that still works on 6000.4+, where objectInstanceId is always 0
        public string idSession;      // Set only when captured in another editor session than its task's (an inverse appended later)
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
        public string parentGameObjectEntityId;

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
        public string entityId;
        public string transformGlobalObjectId;
        public int transformInstanceId;
        public string transformEntityId;
        public string name;
        public int parentIndex = -1;
        public bool activeSelf;
        public int layer;
        public string tag;
        public int siblingIndex;
        public string externalParentGlobalObjectId;
        public int externalParentInstanceId;
        public string externalParentEntityId;
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
        public long nextSeq = 1; // Next short task id (s<seq>); never reused within one history

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

            AssignMissingSeqs();
        }

        internal long AllocateSeq() => nextSeq++;

        /// <summary>
        /// Tasks recorded before seq existed get one in recording order, so every task has an s&lt;seq&gt; id. A task that
        /// is partially undone sits on both stacks under one id and gets one seq.
        /// </summary>
        private void AssignMissingSeqs()
        {
            var all = tasks.Concat(undoneStack).ToList();
            long max = all.Count == 0 ? 0 : all.Max(task => task.seq);
            if (nextSeq <= max)
                nextSeq = max + 1;

            var assigned = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var task in all.Where(task => task.seq > 0 && task.id != null))
                assigned[task.id] = task.seq;
            foreach (var task in all.Where(task => task.seq <= 0).OrderBy(task => task.timestamp))
            {
                if (task.id != null && assigned.TryGetValue(task.id, out long seq))
                {
                    task.seq = seq;
                    continue;
                }
                task.seq = AllocateSeq();
                if (task.id != null)
                    assigned[task.id] = task.seq;
            }
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
        public long seq;          // Short id, shown as s<seq>; assigned when the task is recorded
        public string agent;      // Caller the task is attributed to when it ends ("unknown" without a REST identity)
        public string group;      // g<seq of its first task> for one agent's run of writes, j<jobId> for an async batch job's chunks
        public string target;     // Main target named by the call's arguments, for the compact list
        public string idSession;  // Editor session the snapshots were captured in; instance/entity ids are only valid there
        public List<ObjectSnapshot> snapshots = new List<ObjectSnapshot>();
        [NonSerialized] private HashSet<string> _snapshotKeys;
        [NonSerialized] private HashSet<string> _entityKeys;
        [NonSerialized] internal bool autoStarted;
        [NonSerialized] internal long startedUnixMs;
        // The connection whose process-resolver answer was still pending when the task ended (0 once settled); the agent
        // is bound late from it, as telemetry does. Never set for an explicit X-Agent-Id.
        [NonSerialized] internal int pendingAgentPort;
        [NonSerialized] internal long pendingAgentSinceMs;

        public string DisplayId => seq > 0 ? "s" + seq : id;

        public string GetFormattedTime()
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("HH:mm:ss");
        }

        internal void EnsureSnapshotIndex()
        {
            if (_snapshotKeys != null)
                return;

            _snapshotKeys = new HashSet<string>(StringComparer.Ordinal);
            _entityKeys = new HashSet<string>(StringComparer.Ordinal);
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
                if (ShouldDeduplicate(snapshot) && !string.IsNullOrEmpty(snapshot.entityId))
                    _entityKeys.Add(GetSnapshotKey(snapshot.entityId, snapshot.type));
            }
        }

        internal void RegisterEntitySnapshot(ObjectSnapshot snapshot)
        {
            if (!ShouldDeduplicate(snapshot) || string.IsNullOrEmpty(snapshot.entityId))
                return;

            EnsureSnapshotIndex();
            _entityKeys.Add(GetSnapshotKey(snapshot.entityId, snapshot.type));
        }

        /// <summary>Whether this task already holds a snapshot of the live object with this entity id, checked before any capture work.</summary>
        internal bool HasEntitySnapshot(string entityId, SnapshotType type)
        {
            if (string.IsNullOrEmpty(entityId))
                return false;

            EnsureSnapshotIndex();
            return _entityKeys.Contains(GetSnapshotKey(entityId, type));
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
            _entityKeys = null;
        }

        internal static bool ShouldDeduplicate(ObjectSnapshot snapshot)
        {
            return snapshot != null && ShouldDeduplicate(snapshot.type);
        }

        internal static bool ShouldDeduplicate(SnapshotType type)
        {
            return type == SnapshotType.Modified ||
                   type == SnapshotType.Created ||
                   type == SnapshotType.Setting;
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
        public string entityId;
        public bool objectReferencesCaptured;
        public List<ObjectReferenceData> objectReferences = new List<ObjectReferenceData>();
    }

    [Serializable]
    public class ObjectReferenceData
    {
        public string propertyPath;
        public string globalObjectId;
        public int objectInstanceId;
        public string entityId;
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
    /// Stored per project under the "UnitySkills.Workflow.*" keys of <see cref="ProjectPrefs"/>
    /// (UserSettings/UnitySkills/settings.json; a project without its own value reads the pre-3.0 global EditorPrefs value).
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
            get => ProjectPrefs.GetBool(KeyEnabled, true);
            set => ProjectPrefs.SetBool(KeyEnabled, value);
        }

        public static int MaxTasks
        {
            get => ProjectPrefs.GetInt(KeyMaxTasks, 200);
            set => ProjectPrefs.SetInt(KeyMaxTasks, value);
        }

        public static int MaxHistoryMB
        {
            get => ProjectPrefs.GetInt(KeyMaxHistoryMB, 32);
            set => ProjectPrefs.SetInt(KeyMaxHistoryMB, value);
        }

        public static int MaxTaskAgeDays
        {
            get => ProjectPrefs.GetInt(KeyMaxTaskAgeDays, 30);
            set => ProjectPrefs.SetInt(KeyMaxTaskAgeDays, value);
        }

        public static int MaxStoreMB
        {
            get => ProjectPrefs.GetInt(KeyMaxStoreMB, 512);
            set => ProjectPrefs.SetInt(KeyMaxStoreMB, value);
        }

        public static int StoreMaxAgeDays
        {
            get => ProjectPrefs.GetInt(KeyStoreMaxAgeDays, 7);
            set => ProjectPrefs.SetInt(KeyStoreMaxAgeDays, value);
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
