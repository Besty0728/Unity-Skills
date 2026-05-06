using System;
using System.Collections.Generic;
using UnityEngine;
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
        public string assetBytesBase64; // Base64 encoded asset file backup (excludes .cs files)

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
        public const int CurrentSchemaVersion = 2;
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
        Modified, // Object state changed
        Created   // Object was newly created in this task
    }

    [Serializable]
    public class ComponentData
    {
        public string typeName;      // Full type name
        public string json;          // Serialized component data
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
