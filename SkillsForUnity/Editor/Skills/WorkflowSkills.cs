using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Workflow Skills - Bookmarks, history, undo management.
    /// Designed to help AI agents navigate and manage work sessions.
    /// </summary>
    public static class WorkflowSkills
    {
        // In-memory bookmark storage (persists until domain reload)
        private static Dictionary<string, BookmarkData> _bookmarks = new Dictionary<string, BookmarkData>();

        private class BookmarkData
        {
            public string[] selectedEntityIds;
            public int[] selectedInstanceIds;
            public Vector3? sceneViewPosition;
            public Quaternion? sceneViewRotation;
            public float? sceneViewSize;
            public string note;
            public System.DateTime createdAt;
        }

        [UnitySkill("bookmark_set", "Save current selection and scene view position as a bookmark",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Create,
            Tags = new[] { "bookmark", "selection", "scene-view", "save" },
            Outputs = new[] { "bookmark", "selectedCount", "hasSceneView" },
            Mode = SkillMode.SemiAuto)]
        public static object BookmarkSet(string bookmarkName, string note = null)
        {
            if (string.IsNullOrEmpty(bookmarkName))
                return new { success = false, error = "bookmarkName is required" };

            var bookmark = new BookmarkData
            {
                selectedEntityIds = UnityObjectIdUtility.GetSelectionEntityIds(),
                selectedInstanceIds = UnityObjectIdUtility.GetSelectionObjectIds(),
                note = note,
                createdAt = System.DateTime.Now
            };

            // Try to capture Scene View camera position
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                bookmark.sceneViewPosition = sceneView.pivot;
                bookmark.sceneViewRotation = sceneView.rotation;
                bookmark.sceneViewSize = sceneView.size;
            }

            _bookmarks[bookmarkName] = bookmark;

            return new
            {
                success = true,
                bookmark = bookmarkName,
                selectedCount = (bookmark.selectedEntityIds ?? Array.Empty<string>()).Length,
                hasSceneView = sceneView != null,
                note
            };
        }

        [UnitySkill("bookmark_goto", "Restore selection and scene view from a bookmark",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "bookmark", "selection", "restore", "navigate" },
            Outputs = new[] { "bookmark", "restoredSelection", "note" },
            RequiresInput = new[] { "bookmarkName" },
            Mode = SkillMode.SemiAuto)]
        public static object BookmarkGoto(string bookmarkName)
        {
            if (!_bookmarks.TryGetValue(bookmarkName, out var bookmark))
                return new { success = false, error = $"Bookmark '{bookmarkName}' not found" };

            // Restore selection. Prefer Unity 6000.5-safe EntityIds; legacy instanceIds are kept for older bookmarks.
            var validEntityIds = (bookmark.selectedEntityIds ?? Array.Empty<string>())
                .Where(id => UnityObjectIdUtility.EntityIdToObject(id) != null)
                .ToArray();
            if (validEntityIds.Length > 0 || bookmark.selectedEntityIds != null)
            {
                UnityObjectIdUtility.SetSelectionEntityIds(validEntityIds);
            }
            else
            {
                var validIds = (bookmark.selectedInstanceIds ?? Array.Empty<int>())
                    .Where(id => UnityObjectIdUtility.ObjectIdToObject(id) != null)
                    .ToArray();
                UnityObjectIdUtility.SetSelectionObjectIds(validIds);
            }

            // Restore scene view
            if (bookmark.sceneViewPosition.HasValue)
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView != null)
                {
                    sceneView.pivot = bookmark.sceneViewPosition.Value;
                    if (bookmark.sceneViewRotation.HasValue)
                        sceneView.rotation = bookmark.sceneViewRotation.Value;
                    if (bookmark.sceneViewSize.HasValue)
                        sceneView.size = bookmark.sceneViewSize.Value;
                    sceneView.Repaint();
                }
            }

            return new
            {
                success = true,
                bookmark = bookmarkName,
                restoredSelection = validEntityIds.Length > 0 || bookmark.selectedEntityIds != null
                    ? validEntityIds.Length
                    : (bookmark.selectedInstanceIds ?? Array.Empty<int>()).Count(id => UnityObjectIdUtility.ObjectIdToObject(id) != null),
                note = bookmark.note
            };
        }

        [UnitySkill("bookmark_list", "List all saved bookmarks",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "bookmark", "list", "overview" },
            Outputs = new[] { "count", "bookmarks" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object BookmarkList()
        {
            var list = _bookmarks.Select(kv => new
            {
                name = kv.Key,
                selectedCount = (kv.Value.selectedEntityIds ?? Array.Empty<string>()).Length > 0
                    ? kv.Value.selectedEntityIds.Length
                    : (kv.Value.selectedInstanceIds ?? Array.Empty<int>()).Length,
                hasSceneView = kv.Value.sceneViewPosition.HasValue,
                note = kv.Value.note,
                createdAt = kv.Value.createdAt.ToString("HH:mm:ss")
            }).ToList();

            return new { success = true, count = list.Count, bookmarks = list };
        }

        [UnitySkill("bookmark_delete", "Delete a bookmark",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Delete,
            Tags = new[] { "bookmark", "delete", "remove" },
            Outputs = new[] { "deleted" },
            RequiresInput = new[] { "bookmarkName" })]
        public static object BookmarkDelete(string bookmarkName)
        {
            if (_bookmarks.Remove(bookmarkName))
                return new { success = true, deleted = bookmarkName };
            return new { success = false, error = $"Bookmark '{bookmarkName}' not found" };
        }

        [UnitySkill("history_undo", "Undo the last operation (or multiple steps)",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "undo", "history", "revert" },
            Outputs = new[] { "undoneSteps" })]
        public static object HistoryUndo(int steps = 1)
        {
            if (steps < 1)
                return new { success = false, error = "steps must be >= 1" };
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
            for (int i = 0; i < steps; i++)
            {
                Undo.PerformUndo();
            }
            Undo.FlushUndoRecordObjects();
            return new { success = true, undoneSteps = steps };
        }

        [UnitySkill("history_redo", "Redo the last undone operation (or multiple steps)",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "redo", "history", "restore" },
            Outputs = new[] { "redoneSteps" })]
        public static object HistoryRedo(int steps = 1)
        {
            if (steps < 1)
                return new { success = false, error = "steps must be >= 1" };
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
            for (int i = 0; i < steps; i++)
            {
                Undo.PerformRedo();
            }
            Undo.FlushUndoRecordObjects();
            return new { success = true, redoneSteps = steps };
        }

        [UnitySkill("history_get_current", "Get the name of the current undo group",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "undo", "history", "current", "group" },
            Outputs = new[] { "currentGroup", "groupIndex" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object HistoryGetCurrent()
        {
            return new
            {
                success = true,
                currentGroup = Undo.GetCurrentGroupName(),
                groupIndex = Undo.GetCurrentGroup()
            };
        }

        // --- Persistent Workflow Skills ---

        [UnitySkill("workflow_task_start", "Start a new persistent workflow task to track changes for undo. Call workflow_task_end when done.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "task", "undo", "tracking", "transaction" },
            Outputs = new[] { "taskId", "message" })]
        public static object WorkflowTaskStart(string tag, string description = "")
        {
            var task = WorkflowManager.BeginTask(tag, description);
            return new
            {
                success = true,
                taskId = task.id,
                message = $"Started task: {tag}"
            };
        }

        [UnitySkill("workflow_task_end", "End the current workflow task and save it. Requires an active task (call workflow_task_start first).",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "task", "undo", "tracking", "save" },
            Outputs = new[] { "taskId", "snapshotCount", "message" })]
        public static object WorkflowTaskEnd()
        {
            if (!WorkflowManager.IsRecording)
                return new { success = false, error = "No active task to end" };
            
            var task = WorkflowManager.CurrentTask;
            string id = task.id;
            int count = task.snapshots.Count;
            
            WorkflowManager.EndTask();
            return new
            {
                success = true,
                taskId = id,
                snapshotCount = count,
                message = "Task ended and saved"
            };
        }

        [UnitySkill("workflow_snapshot_object", "Manually snapshot an object's state before modification. Requires an active task (call workflow_task_start first).",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "snapshot", "undo", "object", "state" },
            Outputs = new[] { "objectName", "type" },
            RequiresInput = new[] { "gameObject" })]
        public static object WorkflowSnapshotObject(string name = null, int instanceId = 0, string entityId = null)
        {
            if (!WorkflowManager.IsRecording)
                return new { success = false, error = "No active task. Call workflow_task_start first." };

            UnityEngine.Object target = null;
            if (!string.IsNullOrWhiteSpace(entityId))
                target = UnityObjectIdUtility.EntityIdToObject(entityId);
            else if (instanceId != 0)
                target = UnityObjectIdUtility.ObjectIdToObject(instanceId);
            else if (!string.IsNullOrEmpty(name))
                target = GameObjectFinder.Find(name: name);

            if (target == null)
                return new { success = false, error = $"Object not found: {entityId ?? name ?? instanceId.ToString()}" };

            WorkflowManager.SnapshotObject(target);
            return new { success = true, objectName = target.name, type = target.GetType().Name };
        }

        [UnitySkill("workflow_list", "List persistent workflow history",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "workflow", "history", "list", "task" },
            Outputs = new[] { "count", "history" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object WorkflowList()
        {
            var history = WorkflowManager.History;
            var list = history.tasks.Select(t => new
            {
                id = t.id,
                tag = t.tag,
                description = t.description,
                time = t.GetFormattedTime(),
                changes = t.snapshots.Count
            }).ToList<object>(); // Cast to object list for JSON serializability

            return new { success = true, count = list.Count, history = list };
        }

        [UnitySkill("workflow_undo_task", "Undo changes from a specific task (restore to previous state)",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "undo", "task", "revert", "restore" },
            Outputs = new[] { "success", "taskId", "total", "succeeded", "failed", "details", "error" },
            RequiresInput = new[] { "taskId" })]
        public static object WorkflowUndoTask(string taskId)
        {
            var result = WorkflowManager.UndoTask(taskId);
            return new
            {
                success = result.success,
                taskId = taskId,
                result.total,
                result.succeeded,
                result.failed,
                details = result.details.Select(d => new { d.globalObjectId, d.objectName, d.success, d.error }).ToList(),
                error = result.error
            };
        }

        [UnitySkill("workflow_redo_task", "Redo a previously undone task (restore changes)",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "redo", "task", "restore", "changes" },
            Outputs = new[] { "success", "taskId", "total", "succeeded", "failed", "details", "error" })]
        public static object WorkflowRedoTask(string taskId = null)
        {
            // If no taskId provided, redo the most recent undone task
            if (string.IsNullOrEmpty(taskId))
            {
                var undoneStack = WorkflowManager.GetUndoneStack();
                if (undoneStack.Count == 0)
                    return new { success = false, error = "No undone tasks to redo" };
                taskId = undoneStack[undoneStack.Count - 1].id;
            }

            var result = WorkflowManager.RedoTask(taskId);
            return new
            {
                success = result.success,
                taskId = taskId,
                result.total,
                result.succeeded,
                result.failed,
                details = result.details.Select(d => new { d.globalObjectId, d.objectName, d.success, d.error }).ToList(),
                error = result.error
            };
        }

        [UnitySkill("workflow_undone_list", "List all undone tasks that can be redone",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "undo", "redo", "list", "history" },
            Outputs = new[] { "count", "undoneStack" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object WorkflowUndoneList()
        {
            var undoneStack = WorkflowManager.GetUndoneStack();
            var list = undoneStack.Select(t => new
            {
                id = t.id,
                tag = t.tag,
                description = t.description,
                time = t.GetFormattedTime(),
                changes = t.snapshots.Count
            }).ToList<object>();

            return new { success = true, count = list.Count, undoneStack = list };
        }

        [UnitySkill("workflow_revert_task", "Alias for workflow_undo_task (deprecated, use workflow_undo_task instead)",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "undo", "task", "revert", "deprecated" },
            Outputs = new[] { "taskId" },
            RequiresInput = new[] { "taskId" })]
        public static object WorkflowRevertTask(string taskId)
        {
            return WorkflowUndoTask(taskId);
        }

        [UnitySkill("workflow_snapshot_created", "Record a newly created object for undo tracking. Requires an active task (call workflow_task_start first).",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "snapshot", "undo", "created", "tracking" },
            Outputs = new[] { "objectName", "type" },
            RequiresInput = new[] { "gameObject" })]
        public static object WorkflowSnapshotCreated(string name = null, int instanceId = 0, string entityId = null)
        {
            if (!WorkflowManager.IsRecording)
                return new { success = false, error = "No active task. Call workflow_task_start first." };

            UnityEngine.Object target = null;
            if (!string.IsNullOrWhiteSpace(entityId))
                target = UnityObjectIdUtility.EntityIdToObject(entityId);
            else if (instanceId != 0)
                target = UnityObjectIdUtility.ObjectIdToObject(instanceId);
            else if (!string.IsNullOrEmpty(name))
                target = GameObjectFinder.Find(name: name);

            if (target == null)
                return new { success = false, error = $"Object not found: {entityId ?? name ?? instanceId.ToString()}" };

            if (target is Component comp)
                WorkflowManager.SnapshotCreatedComponent(comp);
            else
                WorkflowManager.SnapshotObject(target, SnapshotType.Created);

            return new { success = true, objectName = target.name, type = target.GetType().Name };
        }

        [UnitySkill("workflow_delete_task", "Delete a task from history (does not revert changes)",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Delete,
            Tags = new[] { "task", "delete", "history", "cleanup" },
            Outputs = new[] { "deletedId" },
            RequiresInput = new[] { "taskId" })]
        public static object WorkflowDeleteTask(string taskId)
        {
            WorkflowManager.DeleteTask(taskId);
            return new { success = true, deletedId = taskId };
        }

        [UnitySkill("workflow_clear_history",
            "Permanently clear ALL workflow history: tasks, the redo (undone) stack, and every backed-up file blob in the content-addressed store. High-risk and irreversible — requires a confirmation token. This ONLY deletes the tracking history; it does NOT undo or revert any change already applied to your project assets, scenes, or settings.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Delete,
            Tags = new[] { "workflow", "history", "clear", "cleanup", "delete" },
            Outputs = new[] { "success", "before", "after", "message" },
            RiskLevel = "high")]
        public static object WorkflowClearHistory()
        {
            var history = WorkflowManager.History;
            var before = new
            {
                tasks = history.tasks.Count,
                undoneStack = history.undoneStack.Count,
                historyFileBytes = WorkflowManager.GetHistoryFileSizeBytes(),
                fileStoreBytes = WorkflowFileStore.GetStoreSizeBytes()
            };

            WorkflowManager.ClearHistory();

            var afterHistory = WorkflowManager.History;
            var after = new
            {
                tasks = afterHistory.tasks.Count,
                undoneStack = afterHistory.undoneStack.Count,
                historyFileBytes = WorkflowManager.GetHistoryFileSizeBytes(),
                fileStoreBytes = WorkflowFileStore.GetStoreSizeBytes()
            };

            return new
            {
                success = true,
                before,
                after,
                message = "Workflow history cleared. This removed tracking data only and did NOT undo any modifications already made to your project assets."
            };
        }

        // --- Session Management (Conversation-Level Undo) ---

        [UnitySkill("workflow_session_start", "Start a new session (conversation-level). All changes will be tracked and can be undone together.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "session", "conversation", "tracking", "start" },
            Outputs = new[] { "sessionId", "message" })]
        public static object WorkflowSessionStart(string tag = null)
        {
            string sessionId = WorkflowManager.BeginSession(tag);
            return new
            {
                success = true,
                sessionId = sessionId,
                message = "Session started. All changes will be tracked for undo."
            };
        }

        [UnitySkill("workflow_session_end", "End the current session and save all tracked changes.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "session", "conversation", "tracking", "end" },
            Outputs = new[] { "sessionId", "message" })]
        public static object WorkflowSessionEnd()
        {
            if (!WorkflowManager.HasActiveSession)
                return new { success = false, error = "No active session to end" };

            string sessionId = WorkflowManager.CurrentSessionId;
            WorkflowManager.EndSession();
            return new
            {
                success = true,
                sessionId = sessionId,
                message = "Session ended and saved"
            };
        }

        [UnitySkill("workflow_session_undo", "Undo all changes made during a specific session (conversation-level undo)",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "session", "undo", "conversation", "revert" },
            Outputs = new[] { "success", "sessionId", "total", "succeeded", "failed", "details", "error", "message" })]
        public static object WorkflowSessionUndo(string sessionId = null)
        {
            // If no sessionId provided, try to get the most recent session
            if (string.IsNullOrEmpty(sessionId))
            {
                var sessions = WorkflowManager.GetSessions();
                if (sessions.Count == 0)
                    return new { success = false, error = "No sessions found in history" };
                sessionId = sessions[0].sessionId;
            }

            var result = WorkflowManager.UndoSession(sessionId);
            return new
            {
                success = result.success,
                sessionId = sessionId,
                result.total,
                result.succeeded,
                result.failed,
                details = result.details.Select(d => new { d.globalObjectId, d.objectName, d.success, d.error }).ToList(),
                error = result.error,
                message = result.success ? "Session changes undone successfully" : "Failed to undo session"
            };
        }

        [UnitySkill("workflow_session_list", "List all recorded sessions (conversation-level history)",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "session", "list", "history", "conversation" },
            Outputs = new[] { "count", "currentSessionId", "sessions" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object WorkflowSessionList()
        {
            var sessions = WorkflowManager.GetSessions();
            return new
            {
                success = true,
                count = sessions.Count,
                currentSessionId = WorkflowManager.CurrentSessionId,
                sessions = sessions.Select(s => new
                {
                    s.sessionId,
                    s.taskCount,
                    s.totalChanges,
                    s.startTime,
                    s.endTime,
                    s.tags
                }).ToList()
            };
        }

        [UnitySkill("workflow_session_status", "Get the current session status",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "session", "status", "current", "recording" },
            Outputs = new[] { "hasActiveSession", "currentSessionId", "isRecording", "currentTaskId" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object WorkflowSessionStatus()
        {
            return new
            {
                success = true,
                hasActiveSession = WorkflowManager.HasActiveSession,
                currentSessionId = WorkflowManager.CurrentSessionId,
                isRecording = WorkflowManager.IsRecording,
                currentTaskId = WorkflowManager.CurrentTask?.id,
                currentTaskTag = WorkflowManager.CurrentTask?.tag,
                currentTaskDescription = WorkflowManager.CurrentTask?.description,
                snapshotCount = WorkflowManager.CurrentTask?.snapshots.Count ?? 0
            };
        }

        [UnitySkill("workflow_plan", "Generate a combined execution plan for multiple skills. Returns aggregated risk, dependencies, and per-step plans.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Analyze,
            Tags = new[] { "workflow", "plan", "preview", "multi-skill", "aggregate" },
            Outputs = new[] { "totalSteps", "totalRisk", "steps", "dependencies", "warnings" },
            RequiresInput = new[] { "skillsJson" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object WorkflowPlan(string skillsJson)
        {
            if (string.IsNullOrWhiteSpace(skillsJson))
                return new { error = "skillsJson is required. Provide a JSON array of {name, params} objects." };

            JArray skillsArray;
            try { skillsArray = JArray.Parse(skillsJson); }
            catch (Exception ex) { return new { error = $"Invalid skillsJson: {ex.Message}" }; }

            if (skillsArray.Count == 0)
                return new { error = "skillsJson array is empty." };

            var steps = new List<object>();
            var allWarnings = new List<string>();
            var dependencies = new List<object>();
            var highestRisk = "low";
            var mayDisconnect = false;
            var createdNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < skillsArray.Count; i++)
            {
                var entry = skillsArray[i] as JObject;
                if (entry == null)
                {
                    allWarnings.Add($"Step {i}: invalid entry (not a JSON object).");
                    continue;
                }

                var skillName = entry["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(skillName))
                {
                    allWarnings.Add($"Step {i}: missing 'name' field.");
                    continue;
                }

                if (!SkillRouter.HasSkill(skillName))
                {
                    allWarnings.Add($"Step {i}: skill '{skillName}' not found.");
                    steps.Add(new Dictionary<string, object>
                    {
                        ["index"] = i,
                        ["skill"] = skillName,
                        ["error"] = "Skill not found"
                    });
                    continue;
                }

                var paramsObj = entry["params"] as JObject ?? new JObject();
                var planJson = SkillRouter.Plan(skillName, paramsObj.ToString());
                var planResult = JObject.Parse(planJson);

                // Extract risk level from plan
                var stepRisk = planResult.SelectToken("skill.riskLevel")?.ToString()
                               ?? planResult.SelectToken("impact.riskLevel")?.ToString()
                               ?? "low";
                highestRisk = MaxRisk(highestRisk, stepRisk);

                // Check serverAvailability
                if (planResult["serverAvailability"] != null)
                    mayDisconnect = true;

                // Detect dependencies: if this step uses a name that a previous step creates
                var targetName = GetTargetFromPlan(planResult);
                if (!string.IsNullOrEmpty(targetName) && createdNames.Contains(targetName))
                {
                    dependencies.Add(new Dictionary<string, object>
                    {
                        ["step"] = i,
                        ["dependsOn"] = FindCreatorStep(steps, targetName),
                        ["reason"] = $"'{skillName}' targets '{targetName}' which is created by an earlier step"
                    });
                }

                // Track created names for dependency detection
                TrackCreatedNames(planResult, createdNames);

                // Collect warnings from individual plan
                if (planResult["validation"] is JObject valJObj)
                {
                    var warnings = valJObj["warnings"] as JArray;
                    if (warnings != null)
                    {
                        foreach (var w in warnings)
                            allWarnings.Add($"Step {i} ({skillName}): {w}");
                    }
                }

                steps.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["skill"] = skillName,
                    ["plan"] = planResult
                });
            }

            var result = new Dictionary<string, object>
            {
                ["status"] = "plan",
                ["totalSteps"] = skillsArray.Count,
                ["totalRisk"] = highestRisk,
                ["estimatedDuration"] = mayDisconnect ? "may_include_reload" : "instant",
                ["serverAvailability"] = mayDisconnect ? "may_disconnect" : "unaffected",
                ["steps"] = steps.ToArray(),
                ["dependencies"] = dependencies.ToArray(),
                ["warnings"] = allWarnings.ToArray()
            };

            return result;
        }

        private static string MaxRisk(string a, string b)
        {
            int Score(string r) => r == "high" ? 3 : r == "medium" ? 2 : 1;
            return Score(a) >= Score(b) ? a : b;
        }

        private static string GetTargetFromPlan(JObject plan)
        {
            var steps = plan["steps"] as JArray;
            if (steps == null || steps.Count == 0) return null;
            return steps[0]?["target"]?.ToString();
        }

        private static int FindCreatorStep(List<object> steps, string name)
        {
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                if (steps[i] is Dictionary<string, object> step &&
                    step.TryGetValue("plan", out var planObj) &&
                    planObj is JObject plan)
                {
                    var changes = plan["changes"] as JObject;
                    if (changes == null) continue;
                    var creates = changes["create"] as JArray;
                    if (creates != null)
                    {
                        foreach (var c in creates)
                        {
                            if (c?["name"]?.ToString()?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                                return (int)(step.TryGetValue("index", out var idx) ? idx : i);
                        }
                    }
                }
            }
            return -1;
        }

        private static void TrackCreatedNames(JObject plan, HashSet<string> names)
        {
            var changes = plan["changes"] as JObject;
            if (changes == null) return;
            var creates = changes["create"] as JArray;
            if (creates == null) return;
            foreach (var c in creates)
            {
                var n = c?["name"]?.ToString();
                if (!string.IsNullOrEmpty(n)) names.Add(n);
            }
        }
    }
}

// Producer:Betsy
