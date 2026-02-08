using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnitySkills
{
    [Serializable]
    public class WorkflowHistoryData
    {
        public List<WorkflowTask> tasks = new List<WorkflowTask>();
        public List<WorkflowTask> undoneStack = new List<WorkflowTask>(); // Stack of undone tasks for redo
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

        public string GetFormattedTime()
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("HH:mm:ss");
        }
    }

    public enum SnapshotType
    {
        Modified, // Object state changed
        Created   // Object was newly created in this task
    }

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
