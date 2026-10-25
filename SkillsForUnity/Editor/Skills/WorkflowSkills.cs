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
    /// Workflow skills -- bookmarks, history, and undo management.
    /// Aimed at helping an AI agent navigate and manage a work session.
    /// </summary>
    public static class WorkflowSkills
    {
        // Bookmarks are kept in memory; a domain reload invalidates them
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
            RequiresInput = new[] { "bookmarkName" })]
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

            // Try to record the Scene View camera position
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
            RequiresInput = new[] { "bookmarkName" })]
        public static object BookmarkGoto(string bookmarkName)
        {
            if (!_bookmarks.TryGetValue(bookmarkName, out var bookmark))
                return new { success = false, error = $"Bookmark '{bookmarkName}' not found" };

            // Restore the selection set. Prefer the Unity 6000.5-safe EntityId; instanceId is kept only for compatibility with older bookmarks.
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

            // Restore the Scene View perspective
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
            RequiresInput = new[] { "bookmarkName" },
            RiskLevel = "low")]
        public static object BookmarkDelete(string bookmarkName)
        {
            if (_bookmarks.Remove(bookmarkName))
                return new { success = true, deleted = bookmarkName };
            return new { success = false, error = $"Bookmark '{bookmarkName}' not found" };
        }

        [UnitySkill("history_undo", "Undo the last operation (or multiple steps)",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "undo", "history", "revert" },
            Outputs = new[] { "undoneSteps" },
            MutatesScene = true)]
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
            Outputs = new[] { "redoneSteps" },
            MutatesScene = true)]
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

        // --- Persistent workflow skills ---

        [UnitySkill("workflow_task_start", "Start a new persistent workflow task to track changes for undo. Call workflow_task_end when done.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "task", "undo", "tracking", "transaction" },
            Outputs = new[] { "taskId", "checkpoint", "message" },
            RequiresInput = new[] { "tag" })]
        public static object WorkflowTaskStart(string tag, string description = "")
        {
            var task = WorkflowManager.BeginTask(tag, description);
            string checkpoint = WorkflowManager.AddCheckpoint(null, WorkflowCheckpoint.KindTask, tag);
            return new
            {
                success = true,
                taskId = task.id,
                checkpoint,
                message = $"Started task: {tag}"
            };
        }

        [UnitySkill("workflow_checkpoint", "Name a point in the undo history. Without 'at' it adds a checkpoint before the caller's next recorded step; with 'at' (a c<n> id from workflow_list) it renames that checkpoint. Undo back to it later with its id or label.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Create,
            Tags = new[] { "checkpoint", "undo", "label" },
            Outputs = new[] { "id", "label" },
            RiskLevel = "low",
            RequiresInput = new[] { "label" })]
        public static object WorkflowCheckpointMark(string label, string at = null)
        {
            if (Validate.Required(label, "label") is object err) return err;
            return string.IsNullOrEmpty(at) ? AddLabelledCheckpoint(label) : RenameCheckpoint(at, label);
        }

        private static object AddLabelledCheckpoint(string label) =>
            new { id = WorkflowManager.AddCheckpoint(null, WorkflowCheckpoint.KindExplicit, label), label };

        private static object RenameCheckpoint(string id, string label)
        {
            if (!WorkflowManager.RenameCheckpoint(id, label))
                return new
                {
                    error = $"Checkpoint not found: {id}. Use a c<n> id from workflow_list.",
                    errorCode = SkillErrorCode.NotFound.ToWireString(),
                    retryStrategy = SkillErrorResponse.RetryFixAndRetry
                };
            return new { id, label };
        }

        [UnitySkill("workflow_restore", "Restore the scene and project assets to a checkpoint. to is a c<n> id, an s<n> step, 'last' (the caller's newest checkpoint) or a checkpoint label; agents is 'self' (default) or 'all'; conflicts: omit to refuse on conflicts (WORKFLOW_CONFLICT lists them), \"keep\" to skip them, or the ids to overwrite. A guard checkpoint is taken first, so the restore is undone with workflow_restore {\"to\":\"<guard>\"}. scope limits the restore to some targets: comma-separated entity:<id>, object names or paths (trailing / adds children), Assets/ paths (trailing / = folder), setting keys (trailing . = prefix).",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "workflow", "restore", "undo", "checkpoint" },
            Outputs = new[] { "to", "guard", "restored", "deleted", "recreated", "failed", "skipped", "coverage", "undo", "compileTriggered" },
            RequiresInput = new[] { "to" },
            RiskLevel = "medium", MutatesScene = true, MutatesAssets = true)]
        public static object WorkflowRestore(string to, string agents = "self", string conflicts = null, string scope = null)
        {
            if (Validate.Required(to, "to") is object err) return err;
            bool allAgents;
            if (string.Equals(agents, "all", StringComparison.OrdinalIgnoreCase)) allAgents = true;
            else if (string.IsNullOrEmpty(agents) || string.Equals(agents, "self", StringComparison.OrdinalIgnoreCase)) allAgents = false;
            else return new { error = $"agents must be 'self' or 'all', got '{agents}'", errorCode = SkillErrorCode.SemanticInvalid.ToWireString() };

            string caller = WorkflowManager.ResolveCallerAgent();
            var withdrawn = SurfaceRejectionForTasks("workflow_restore", "restoring these steps' snapshots",
                WorkflowManager.FindStepsForRestore(to, allAgents, caller));
            if (withdrawn != null)
                return withdrawn;

            var outcome = WorkflowManager.RestoreTo(to, allAgents, caller, conflicts, scope);
            return outcome.error != null ? RestoreError(outcome) : RestoreResponse(outcome, to);
        }

        /// <summary>
        /// The success response of a restore. A compile it triggered is reported as an async job the caller can wait on.
        /// </summary>
        private static Dictionary<string, object> RestoreResponse(RestoreOutcome outcome, string to)
        {
            var result = new Dictionary<string, object>
            {
                ["to"] = to,
                ["guard"] = outcome.guard,
                ["restored"] = outcome.restored,
                ["deleted"] = outcome.deleted,
                ["recreated"] = outcome.recreated,
                ["failed"] = outcome.failed,
                ["skipped"] = outcome.skipped,
                ["coverage"] = outcome.coverage,
                ["undo"] = outcome.guard == null ? null : $"workflow_restore {{\"to\":\"{outcome.guard}\"}}",
                ["compileTriggered"] = outcome.compileTriggered
            };
            if (outcome.merged > 0)
                result["merged"] = outcome.merged;
            if (outcome.failedItems.Count > 0)
                result["failedItems"] = outcome.failedItems;
            if (outcome.skippedItems.Count > 0)
                result["skippedItems"] = outcome.skippedItems;
            if (!outcome.compileTriggered)
                return result;

            var job = AsyncJobService.StartRefreshCompileJob(outcome.scriptChanges, outcome.refreshStartedUtcTicks,
                compilationStartsBefore: outcome.compilationStartsBefore);
            result["status"] = "accepted";
            result["jobId"] = job.jobId;
            result["waitUrl"] = AsyncJobService.BuildWaitUrl(job.jobId);
            ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                result,
                "The restore may trigger a short asset refresh window. The REST server can be briefly unavailable if Unity starts recompiling scripts.",
                alwaysInclude: true);
            return result;
        }

        // The router keeps a skill error's top-level members but not its details object, so a conflict's list and coverage sit at the top.
        private static object RestoreError(RestoreOutcome outcome)
        {
            if (outcome.errorCode == SkillErrorCode.WorkflowConflict)
            {
                return new
                {
                    error = outcome.error,
                    errorCode = outcome.errorCode.ToWireString(),
                    conflicts = outcome.conflicts,
                    coverage = outcome.coverage,
                    suggestedFixes = new[]
                    {
                        new SuggestedFix
                        {
                            action = "ask_user",
                            reason = "Ask the user which conflicting changes to keep. Then retry with conflicts:\"keep\" or with the ids to overwrite."
                        }
                    },
                    retryStrategy = SkillErrorResponse.RetryFixAndRetry
                };
            }
            return new
            {
                error = outcome.error,
                errorCode = outcome.errorCode.ToWireString(),
                recentCheckpoints = outcome.recentCheckpoints,
                retryStrategy = SkillErrorResponse.RetryFixAndRetry
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

        private const int DefaultWorkflowListRows = 30;
        private const int MaxWorkflowListRows = 200;

        [UnitySkill("workflow_list", "List recorded workflow steps and checkpoints newest first as compact rows ('s<seq> time skill target (changes) [@agent] [(undone)]', 'c<n> time \"label\"|auto [@agent]'); pass a row's s<seq> or c<n> id to workflow_undo_task or workflow_checkpoint. Params: limit (default 30, counts all rows), agent ('all', 'self' or an agent id), before (an s<seq> or c<n> id, for the next page), view ('all' default, or 'checkpoints' for one row per checkpoint with the steps it covers), format ('objects' returns the legacy object list).",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "workflow", "history", "list", "task", "checkpoint" },
            Outputs = new[] { "count", "agent", "rows", "more", "history" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object WorkflowList(int limit = 0, string agent = "all", string before = null, string format = "rows", string view = "all")
        {
            bool objects = string.Equals(format, "objects", StringComparison.OrdinalIgnoreCase);
            if (!objects && !string.IsNullOrEmpty(format) && !string.Equals(format, "rows", StringComparison.OrdinalIgnoreCase))
                return InvalidListArgument($"format must be 'rows' or 'objects', got '{format}'");
            bool checkpointsOnly = string.Equals(view, "checkpoints", StringComparison.OrdinalIgnoreCase);
            if (!checkpointsOnly && !string.IsNullOrEmpty(view) && !string.Equals(view, "all", StringComparison.OrdinalIgnoreCase))
                return InvalidListArgument($"view must be 'all' or 'checkpoints', got '{view}'");
            if (limit < 0 || limit > MaxWorkflowListRows)
                return InvalidListArgument($"limit must be between 1 and {MaxWorkflowListRows} (0 = default)");

            long cutoff = long.MaxValue;
            if (!string.IsNullOrEmpty(before) && !TryParseListCursor(before, out cutoff))
                return InvalidListArgument($"before must be an s<seq> or c<n> id from a previous page, got '{before}'");

            string caller = WorkflowManager.ResolveCallerAgent();
            string agentFilter = string.IsNullOrEmpty(agent) || string.Equals(agent, "all", StringComparison.OrdinalIgnoreCase)
                ? null
                : string.Equals(agent, "self", StringComparison.OrdinalIgnoreCase)
                    ? caller
                    : WorkflowManager.NormalizeAgent(ClientProcessResolver.CanonicalizeExplicitAgentId(agent.Trim()));

            var history = WorkflowManager.History;
            WorkflowManager.BindPendingAgents();
            // A redo puts a task back at the end of the list under its old seq, so the list is ordered by seq before paging.
            var tasks = history.tasks
                .Where(t => agentFilter == null || string.Equals(WorkflowManager.NormalizeAgent(t.agent), agentFilter, StringComparison.Ordinal))
                .OrderBy(t => t.seq)
                .ToList();

            if (objects)
            {
                // The pre-3.0 shape, oldest first and unpaged unless a limit is given, for scripts that parse it.
                var page = tasks.Where(t => ListKeyForStep(t.seq) < cutoff).ToList();
                var selected = limit > 0 ? page.Skip(Math.Max(0, page.Count - limit)) : page;
                var list = selected.Select(t => new
                {
                    id = t.id,
                    tag = t.tag,
                    description = t.description,
                    time = t.GetFormattedTime(),
                    changes = t.snapshots.Count,
                    seq = t.DisplayId,
                    agent = t.agent,
                    group = t.group
                }).ToList<object>(); // Converted to an object list for JSON serialization
                return new { success = true, count = list.Count, history = list };
            }

            var checkpoints = history.checkpoints
                .Where(c => agentFilter == null || string.Equals(WorkflowManager.NormalizeAgent(c.agent), agentFilter, StringComparison.Ordinal))
                .OrderBy(c => c.seq)
                .ToList();
            var entries = new List<ListEntry>();
            if (!checkpointsOnly)
                entries.AddRange(tasks.Select(t => new ListEntry(ListKeyForStep(t.seq), t, null)));
            entries.AddRange(checkpoints.Select(c => new ListEntry(ListKeyForCheckpoint(c.seq), null, c)));
            entries = entries.Where(e => e.Key < cutoff).OrderByDescending(e => e.Key).ToList();

            int take = limit > 0 ? limit : DefaultWorkflowListRows;
            var shown = entries.Take(take).ToList();
            var undone = WorkflowManager.UndoneSteps(history.restores);
            var rows = shown.Select(e => e.Checkpoint != null
                ? (checkpointsOnly
                    ? FormatCheckpointSummaryRow(e.Checkpoint, caller, checkpoints, tasks)
                    : FormatCheckpointRow(e.Checkpoint, caller))
                : FormatTaskRow(e.Task, caller, undone.Contains(e.Task.seq))).ToList();

            int total = checkpointsOnly ? checkpoints.Count : tasks.Count;
            if (entries.Count > shown.Count)
                return new { success = true, count = total, agent = caller, rows, more = ListEntryId(shown[shown.Count - 1]) };
            return new { success = true, count = total, agent = caller, rows };
        }

        /// <summary>
        /// One row of the merged list. A step <c>s&lt;seq&gt;</c> sorts above the checkpoint at its own position, which marks
        /// the state before that step; a checkpoint with no step yet at its position (the next seq) sorts above every step.
        /// </summary>
        private sealed class ListEntry
        {
            public readonly long Key;
            public readonly WorkflowTask Task;
            public readonly WorkflowCheckpoint Checkpoint;

            public ListEntry(long key, WorkflowTask task, WorkflowCheckpoint checkpoint)
            {
                Key = key;
                Task = task;
                Checkpoint = checkpoint;
            }
        }

        private static long ListKeyForStep(long seq) => 2 * seq + 1;
        private static long ListKeyForCheckpoint(long position) => 2 * position;
        private static string ListEntryId(ListEntry entry) => entry.Checkpoint != null ? entry.Checkpoint.id : entry.Task.DisplayId;

        /// <summary>Reads a list cursor: <c>s&lt;seq&gt;</c> or a bare seq name a step, <c>c&lt;n&gt;</c> a checkpoint. Rows above it are shown.</summary>
        private static bool TryParseListCursor(string cursor, out long cutoff)
        {
            cutoff = 0;
            if (cursor.Length < 1)
                return false;
            char prefix = char.ToLowerInvariant(cursor[0]);
            bool checkpoint = prefix == 'c';
            string digits = prefix == 's' || checkpoint ? cursor.Substring(1) : cursor;
            if (!long.TryParse(digits, out long seq))
                return false;
            cutoff = checkpoint ? ListKeyForCheckpoint(seq) : ListKeyForStep(seq);
            return true;
        }

        private const int CheckpointLabelRowChars = 40;
        private const int CheckpointSummaryCount = 2;

        private static string FormatCheckpointRow(WorkflowCheckpoint checkpoint, string caller)
        {
            var row = new System.Text.StringBuilder(checkpoint.id).Append(' ').Append(FormatCheckpointTime(checkpoint)).Append(' ');
            row.Append(CheckpointRowLabel(checkpoint));
            AppendAgent(row, checkpoint.agent, caller);
            return row.ToString();
        }

        /// <summary>The <c>view:"checkpoints"</c> row: the checkpoint, then the steps it covers up to the next newer checkpoint.</summary>
        private static string FormatCheckpointSummaryRow(WorkflowCheckpoint checkpoint, string caller,
            IList<WorkflowCheckpoint> checkpoints, IList<WorkflowTask> tasks)
        {
            int index = checkpoints.IndexOf(checkpoint);
            long upper = index + 1 < checkpoints.Count ? checkpoints[index + 1].seq : long.MaxValue;
            var covered = tasks.Where(t => t.seq >= checkpoint.seq && t.seq < upper).ToList();

            var row = new System.Text.StringBuilder(FormatCheckpointRow(checkpoint, caller)).Append(": ");
            row.Append(string.Join(", ", covered.Take(CheckpointSummaryCount).Select(t =>
                string.IsNullOrEmpty(t.target) ? t.tag : t.tag + " " + t.target)));
            row.Append(" (").Append(covered.Count).Append(')');
            return row.ToString();
        }

        private static string CheckpointRowLabel(WorkflowCheckpoint checkpoint)
        {
            if (string.IsNullOrEmpty(checkpoint.label))
                return checkpoint.kind ?? WorkflowCheckpoint.KindAuto;
            string label = checkpoint.label.Replace('\n', ' ').Replace('\r', ' ');
            if (label.Length > CheckpointLabelRowChars)
                label = label.Substring(0, CheckpointLabelRowChars);
            return "\"" + label + "\"";
        }

        private static string FormatCheckpointTime(WorkflowCheckpoint checkpoint) =>
            DateTimeOffset.FromUnixTimeMilliseconds(checkpoint.timestampMs).ToLocalTime().ToString("HH:mm:ss");

        private static object InvalidListArgument(string message) => new
        {
            error = message,
            errorCode = SkillErrorCode.SemanticInvalid.ToWireString()
        };

        private static string FormatTaskRow(WorkflowTask task, string caller, bool undone)
        {
            var row = new System.Text.StringBuilder(task.DisplayId).Append(' ').Append(task.GetFormattedTime()).Append(' ').Append(task.tag);
            if (!string.IsNullOrEmpty(task.target))
                row.Append(' ').Append(task.target);
            row.Append(" (").Append(task.snapshots?.Count ?? 0).Append(')');
            AppendAgent(row, task.agent, caller);
            if (undone)
                row.Append(" (undone)");
            return row.ToString();
        }

        // Rows name the agent only when it is not the caller, so a single-agent history carries no agent column at all.
        private static void AppendAgent(System.Text.StringBuilder row, string agent, string caller)
        {
            if (string.IsNullOrEmpty(agent))
                return;
            agent = WorkflowManager.NormalizeAgent(agent);
            if (!string.Equals(agent, caller, StringComparison.Ordinal))
                row.Append(" @").Append(agent);
        }


        [UnitySkill("workflow_undo_task", "Undo one step or group (s<seq>, a group id, c<n> or a task UUID): its targets go back to their state before it; later steps on other targets stay; conflicts: omit to refuse on conflicts (WORKFLOW_CONFLICT lists them), \"keep\" to skip them, or the ids to overwrite.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "undo", "task", "revert", "restore" },
            Outputs = new[] { "taskId", "to", "guard", "restored", "deleted", "recreated", "failed", "skipped", "coverage", "undo", "compileTriggered" },
            RequiresInput = new[] { "taskId" },
            // Restoring a snapshot rewrites everything that task touched: scene objects, and --
            // via UndoMovedSnapshot / RedoDeletedSnapshot / RestoreModifiedSnapshot -- project assets.
            MutatesScene = true, MutatesAssets = true)]
        public static object WorkflowUndoTask(string taskId, string conflicts = null)
        {
            var steps = WorkflowManager.FindStepsForUndo(taskId);
            var withdrawn = SurfaceRejectionForTasks("workflow_undo_task", "restoring this task's snapshots", steps);
            if (withdrawn != null)
                return withdrawn;
            if (steps.Count == 0)
                return new
                {
                    error = $"Task not found: {taskId}",
                    errorCode = SkillErrorCode.NotFound.ToWireString(),
                    retryStrategy = SkillErrorResponse.RetryFixAndRetry
                };

            var outcome = WorkflowManager.RestoreSteps(steps, taskId, WorkflowManager.ResolveCallerAgent(), conflicts);
            if (outcome.error != null)
                return RestoreError(outcome);
            var result = RestoreResponse(outcome, taskId);
            result["taskId"] = taskId;
            return result;
        }

        [UnitySkill("workflow_redo_task", "Undo a restore (from workflow_restore or workflow_undo_task): only the targets it changed go back, later steps stay; taskId is the restore's s<seq>, its guard c<n>, or a step it reverted; omitted = your newest restore; conflicts: omit to refuse on conflicts (WORKFLOW_CONFLICT lists them), \"keep\" to skip them, or the ids to overwrite.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "redo", "task", "restore", "changes" },
            Outputs = new[] { "to", "guard", "restored", "deleted", "recreated", "failed", "skipped", "coverage", "undo", "compileTriggered" },
            MutatesScene = true, MutatesAssets = true)]
        public static object WorkflowRedoTask(string taskId = null, string conflicts = null)
        {
            var steps = WorkflowManager.FindStepsForRedo(taskId);
            var withdrawn = SurfaceRejectionForTasks("workflow_redo_task", "re-applying this task's snapshots", steps);
            if (withdrawn != null)
                return withdrawn;

            var outcome = WorkflowManager.RedoRestore(taskId, conflicts);
            return outcome.error != null ? RestoreError(outcome) : RestoreResponse(outcome, outcome.to);
        }

        [UnitySkill("workflow_undone_list", "List restores that can still be undone with workflow_redo_task.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "undo", "redo", "list", "history" },
            Outputs = new[] { "count", "restores" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object WorkflowUndoneList()
        {
            var restores = WorkflowManager.GetUndoableRestores().Select(restore => new
            {
                seq = "s" + restore.seq,
                to = restore.to,
                guard = restore.guard,
                agent = restore.agent,
                steps = restore.steps.Count
            }).ToList<object>();

            return new { success = true, count = restores.Count, restores };
        }

        [UnitySkill("workflow_revert_task", "Alias for workflow_undo_task (deprecated, use workflow_undo_task instead)",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "undo", "task", "revert", "deprecated" },
            Outputs = new[] { "taskId" },
            RequiresInput = new[] { "taskId" },
            MutatesScene = true, MutatesAssets = true)]
        public static object WorkflowRevertTask(string taskId)
        {
            // Query using this skill's own name rather than handing it off to the delegate: the agent called workflow_revert_task, but if the rejection message shows
            // workflow_undo_task instead, it reads like "the alias is fine, the target is the problem," which would only bait a pointless retry.
            var withdrawn = SurfaceRejectionForTasks("workflow_revert_task", "restoring this task's snapshots",
                WorkflowManager.History.tasks.Where(t => WorkflowManager.MatchesTaskId(t, taskId)));
            if (withdrawn != null)
                return withdrawn;

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

        [UnitySkill("workflow_delete_task", "Delete a task from history (does not revert changes); taskId is an s<seq> id or UUID.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Delete,
            Tags = new[] { "task", "delete", "history", "cleanup" },
            Outputs = new[] { "deletedId" },
            RequiresInput = new[] { "taskId" },
            RiskLevel = "medium")]
        public static object WorkflowDeleteTask(string taskId)
        {
            WorkflowManager.DeleteTask(taskId);
            return new { success = true, deletedId = taskId };
        }

        [UnitySkill("workflow_clear_history",
            "Permanently clear ALL workflow history: tasks, restore records, and every backed-up file blob in the content-addressed store. High-risk and irreversible — requires a confirmation token. This ONLY deletes the tracking history; it does NOT undo or revert any change already applied to your project assets, scenes, or settings.",
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
                restores = history.restores.Count,
                historyFileBytes = WorkflowManager.GetHistoryFileSizeBytes(),
                fileStoreBytes = WorkflowFileStore.GetStoreSizeBytes()
            };

            WorkflowManager.ClearHistory();

            var afterHistory = WorkflowManager.History;
            var after = new
            {
                tasks = afterHistory.tasks.Count,
                restores = afterHistory.restores.Count,
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

        // --- Session management (conversation-level undo) ---

        [UnitySkill("workflow_session_start", "Start a new session (conversation-level). All changes will be tracked and can be undone together.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "session", "conversation", "tracking", "start" },
            Outputs = new[] { "sessionId", "checkpoint", "message" })]
        public static object WorkflowSessionStart(string tag = null)
        {
            string sessionId = WorkflowManager.BeginSession(tag);
            string checkpoint = WorkflowManager.AddCheckpoint(null, WorkflowCheckpoint.KindSession, string.IsNullOrEmpty(tag) ? "Session" : tag);
            return new
            {
                success = true,
                sessionId = sessionId,
                checkpoint,
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

        [UnitySkill("workflow_session_undo", "Undo all changes made during a specific session (conversation-level undo); conflicts: omit to refuse on conflicts (WORKFLOW_CONFLICT lists them), \"keep\" to skip them, or the ids to overwrite.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "session", "undo", "conversation", "revert" },
            Outputs = new[] { "sessionId", "to", "guard", "restored", "deleted", "recreated", "failed", "skipped", "coverage", "undo", "compileTriggered" },
            MutatesScene = true, MutatesAssets = true)]
        public static object WorkflowSessionUndo(string sessionId = null, string conflicts = null)
        {
            // When sessionId isn't given, take the most recent session
            if (string.IsNullOrEmpty(sessionId))
            {
                var sessions = WorkflowManager.GetSessions();
                if (sessions.Count == 0)
                    return new { success = false, error = "No sessions found in history" };
                sessionId = sessions[0].sessionId;
            }

            var withdrawn = SurfaceRejectionForTasks("workflow_session_undo", "restoring this session's snapshots",
                WorkflowManager.FindStepsForSession(sessionId));
            if (withdrawn != null)
                return withdrawn;

            var outcome = WorkflowManager.RestoreSession(sessionId, conflicts);
            if (outcome.error != null)
                return RestoreError(outcome);
            var result = RestoreResponse(outcome, outcome.to);
            result["sessionId"] = sessionId;
            return result;
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

            // Later steps may target objects an earlier step creates; the planners accept those while this scope is open.
            using (SkillPlanningService.BeginPendingObjectsScope())
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

                // Pull the risk level out of the plan
                var stepRisk = planResult.SelectToken("skill.riskLevel")?.ToString()
                               ?? planResult.SelectToken("impact.riskLevel")?.ToString()
                               ?? "low";
                highestRisk = MaxRisk(highestRisk, stepRisk);

                // Check serverAvailability
                if (planResult["serverAvailability"] != null)
                    mayDisconnect = true;

                // Dependency detection: was the name this step uses created by an earlier step
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

                // Record already-created names, for use by dependency detection
                TrackCreatedNames(planResult, createdNames);

                // Aggregate warnings from each individual step's plan
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

        /// <summary>
        /// Returns a SURFACE_EXCLUDED payload when restoring these tasks would write into a category
        /// the current tier has withdrawn; returns null when every snapshot is allowed, and is always null under the full tier.
        ///
        /// Undo/redo's write action is hidden inside the recorded snapshot rather than in this module's
        /// metadata, so the snapshot itself must be consulted about the tier. Rejection is all-or-nothing rather than per-snapshot: to a user asking for a rollback, a task is
        /// a single operation, and a half-restored task would leave a state neither side asked for.
        ///
        /// An empty task set doesn't count as a rejection -- that id might simply not exist, and
        /// answering "withdrawn" for a nonexistent task would hide a plain typo behind the policy wall.
        /// </summary>
        internal static object SurfaceRejectionForTasks(
            string skillName, string subject, IEnumerable<WorkflowTask> tasks)
        {
            if (SkillsSurfaceProfile.IsFull || tasks == null)
                return null;

            foreach (var task in tasks)
            {
                if (task?.snapshots == null) continue;

                foreach (var snapshot in task.snapshots)
                {
                    if (!TryClassifySnapshot(snapshot, out var category, out var operation)) continue;
                    if (!SkillsSurfaceProfile.WithdrawsWriteIn(category)) continue;

                    return SkillsSurfaceProfile.CarriedWriteRejection(skillName, category, subject, operation);
                }
            }

            return null;
        }

        /// <summary>
        /// Determines which category of write restoring a given snapshot would cause. Returns
        /// false if that restoration isn't withdrawn by any tier -- which is the common case, and exactly why this classification is worth doing.
        ///
        /// An empty <c>assetPath</c> is the criterion for "this originally lived in the scene":
        /// WorkflowManager fills this field via AssetDatabase.GetAssetPath, so scene objects get an empty string while everything else gets a project path. Setting-type snapshots have
        /// neither, so they're classified first -- they restore editor/project settings, not scene content.
        ///
        /// Asset restoration is classified by extension, and only for the few kinds a tier actually withdraws: .unity is the scene itself, .mat belongs to material authoring.
        /// Everything else -- scripts, prefabs, textures, ScriptableObjects -- is deliberately left available: these are exactly the writes the guide tier leaves for the AI, and
        /// withdrawing their undo too would pull the safety net out from under work it still permits.
        /// </summary>
        private static bool TryClassifySnapshot(
            Internal.ObjectSnapshot snapshot, out SkillCategory category, out string operation)
        {
            category = default;
            operation = null;
            if (snapshot == null || snapshot.type == SnapshotType.Setting)
                return false;

            if (string.IsNullOrEmpty(snapshot.assetPath))
            {
                category = SkillCategory.GameObject;
                operation = "scene_object_snapshot";
                return true;
            }

            var extension = System.IO.Path.GetExtension(snapshot.assetPath);
            if (string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase))
            {
                category = SkillCategory.Scene;
                operation = "scene_asset_snapshot";
                return true;
            }

            if (string.Equals(extension, ".mat", StringComparison.OrdinalIgnoreCase))
            {
                category = SkillCategory.Material;
                operation = "material_asset_snapshot";
                return true;
            }

            return false;
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
