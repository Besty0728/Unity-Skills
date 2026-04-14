using System;
using System.Collections.Generic;

namespace UnitySkills
{
    [Serializable]
    internal class BatchTargetQuery
    {
        public string name;
        public string path;
        public int instanceId;
        public string tag;
        public string layer;
        public bool? active;
        public string componentType;
        public string sceneName;
        public string parentPath;
        public string prefabSource;
        public bool includeInactive;
        public int limit = 500;
    }

    [Serializable]
    internal class BatchPreviewItem
    {
        public string action;
        public string targetName;
        public string targetPath;
        public int instanceId;
        public string sceneName;
        public string componentType;
        public string propertyName;
        public string currentValue;
        public string nextValue;
        public string currentLayer;
        public string nextLayer;
        public string currentMaterialPath;
        public string nextMaterialPath;
        public int missingCount;
        public string reason;
        public string note;
        public bool recursive;
        public bool willChange;
        public bool valid = true;
        public string skipReason;
    }

    [Serializable]
    internal class BatchPreviewEnvelope
    {
        public string confirmToken;
        public string kind;
        public long createdAt;
        public long expiresAt;
        public string riskLevel;
        public string summary;
        public bool rollbackAvailable;
        public bool mayCreateJob;
        public BatchTargetQuery query;
        public Dictionary<string, object> operation = new Dictionary<string, object>();
        public List<BatchPreviewItem> items = new List<BatchPreviewItem>();
        public int targetCount;
        public int executableCount;
        public int skipCount;
    }

    [Serializable]
    internal class BatchFailureGroup
    {
        public string reason;
        public int count;
    }

    [Serializable]
    internal class BatchReportTotals
    {
        public int total;
        public int success;
        public int failed;
        public int skipped;
    }

    [Serializable]
    internal class BatchReportItemRecord
    {
        public string targetName;
        public string targetPath;
        public int instanceId;
        public string action;
        public string status;
        public string before;
        public string after;
        public string reason;
        public string note;
        public int chunkIndex;
    }

    [Serializable]
    internal class BatchReportRecord
    {
        public string reportId;
        public string kind;
        public string status;
        public string summary;
        public long createdAt;
        public string workflowId;
        public string jobId;
        public bool rollbackAvailable;
        public BatchReportTotals totals = new BatchReportTotals();
        public List<BatchReportItemRecord> items = new List<BatchReportItemRecord>();
        public List<BatchFailureGroup> failureGroups = new List<BatchFailureGroup>();
    }

    [Serializable]
    internal class BatchJobLogEntry
    {
        public long timestamp;
        public string level;
        public string stage;
        public string message;
        public string code;
    }

    [Serializable]
    internal class BatchJobRecord
    {
        public string jobId;
        public string kind;
        public string status;
        public int progress;
        public string currentStage;
        public long startedAt;
        public long updatedAt;
        public List<string> warnings = new List<string>();
        public string resultSummary;
        public string relatedWorkflowId;
        public string reportId;
        public bool canCancel = true;
        public int chunkSize = 100;
        public int processedItems;
        public int totalItems;
        public string error;
        public BatchPreviewEnvelope preview;
        public Dictionary<string, object> metadata = new Dictionary<string, object>();
        public Dictionary<string, object> resultData = new Dictionary<string, object>();
        public List<BatchReportItemRecord> items = new List<BatchReportItemRecord>();
        public List<BatchJobLogEntry> logs = new List<BatchJobLogEntry>();
    }

    [Serializable]
    internal class BatchStorageState
    {
        public List<BatchPreviewEnvelope> previews = new List<BatchPreviewEnvelope>();
        public List<BatchReportRecord> reports = new List<BatchReportRecord>();
        public List<BatchJobRecord> jobs = new List<BatchJobRecord>();
    }
}
