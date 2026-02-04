using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnitySkills
{
    [Serializable]
    public class WorkflowHistoryData
    {
        public List<WorkflowTask> tasks = new List<WorkflowTask>();
    }

    [Serializable]
    public class WorkflowTask
    {
        public string id;
        public string tag;
        public string description;
        public long timestamp;
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
    }

}
