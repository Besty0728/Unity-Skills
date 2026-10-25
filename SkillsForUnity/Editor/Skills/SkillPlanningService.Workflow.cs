using System;
using System.Collections.Generic;

namespace UnitySkills
{
    internal static partial class SkillPlanningService
    {
        // Planning here runs only for dryRun (plan is set). Execution refuses the same way on its own, so validation skips it.

        private static void AnalyzeWorkflowRestore(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (plan == null)
                return;
            var args = validation.Args;
            string to = GetStringArg(args, "to");
            if (string.IsNullOrEmpty(to))
                return;

            string agents = GetStringArg(args, "agents");
            bool allAgents = string.Equals(agents, "all", StringComparison.OrdinalIgnoreCase);
            if (!allAgents && !string.IsNullOrEmpty(agents) && !string.Equals(agents, "self", StringComparison.OrdinalIgnoreCase))
            {
                AddSemanticError(validation, "agents", $"agents must be 'self' or 'all', got '{agents}'");
                return;
            }

            string caller = WorkflowManager.ResolveCallerAgent();
            if (IsWithdrawn(validation, "to", "workflow_restore", "restoring these steps' snapshots",
                    WorkflowManager.FindStepsForRestore(to, allAgents, caller)))
                return;
            var preview = WorkflowManager.PreviewTo(to, allAgents, caller);
            if (preview.error != null)
            {
                AddSemanticError(validation, "to", preview.error);
                return;
            }
            SetWorkflowPreview(plan, preview, "Restore to " + to, to);
        }

        private static void AnalyzeWorkflowUndoTask(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (plan == null)
                return;
            string taskId = GetStringArg(validation.Args, "taskId");
            if (string.IsNullOrEmpty(taskId))
                return;

            var steps = WorkflowManager.FindStepsForUndo(taskId);
            if (steps.Count == 0)
            {
                AddSemanticError(validation, "taskId", $"Task not found: {taskId}");
                return;
            }
            if (IsWithdrawn(validation, "taskId", "workflow_undo_task", "restoring this task's snapshots", steps))
                return;
            var preview = WorkflowManager.PreviewSteps(steps, taskId, WorkflowManager.ResolveCallerAgent());
            if (preview.error != null)
            {
                AddSemanticError(validation, "taskId", preview.error);
                return;
            }
            SetWorkflowPreview(plan, preview, "Undo " + taskId, taskId);
        }

        private static void AnalyzeWorkflowRedoTask(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (plan == null)
                return;
            var steps = WorkflowManager.FindStepsForRedo(GetStringArg(validation.Args, "taskId"));
            if (steps.Count == 0)
            {
                AddSemanticError(validation, "taskId", "No undone tasks to redo");
                return;
            }
            if (IsWithdrawn(validation, "taskId", "workflow_redo_task", "re-applying this task's snapshots", steps))
                return;

            string to = steps[0].DisplayId;
            var preview = WorkflowManager.PreviewSteps(steps, to, WorkflowManager.ResolveCallerAgent());
            if (preview.error != null)
            {
                AddSemanticError(validation, "taskId", preview.error);
                return;
            }
            SetWorkflowPreview(plan, preview, "Redo " + to, to);
        }

        private static void AnalyzeWorkflowSessionUndo(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (plan == null)
                return;
            string sessionId = GetStringArg(validation.Args, "sessionId");
            if (string.IsNullOrEmpty(sessionId))
            {
                var sessions = WorkflowManager.GetSessions();
                if (sessions.Count == 0)
                {
                    AddSemanticError(validation, "sessionId", "No sessions found in history");
                    return;
                }
                sessionId = sessions[0].sessionId;
            }

            var steps = WorkflowManager.FindStepsForSession(sessionId);
            if (steps.Count == 0)
            {
                AddSemanticError(validation, "sessionId", $"No tasks found for session: {sessionId}");
                return;
            }
            if (IsWithdrawn(validation, "sessionId", "workflow_session_undo", "restoring this session's snapshots", steps))
                return;
            var preview = WorkflowManager.PreviewSteps(steps, "session " + sessionId, WorkflowManager.ResolveCallerAgent());
            if (preview.error != null)
            {
                AddSemanticError(validation, "sessionId", preview.error);
                return;
            }
            SetWorkflowPreview(plan, preview, "Undo session " + sessionId, sessionId);
        }

        /// <summary>A surface profile that withdraws a write the restore would make refuses the dryRun as it refuses the call.</summary>
        private static bool IsWithdrawn(SkillRouter.ParameterValidationResult validation, string field, string skillName, string subject,
            List<WorkflowTask> steps)
        {
            var rejection = WorkflowSkills.SurfaceRejectionForTasks(skillName, subject, steps);
            if (rejection == null)
                return false;
            AddSemanticError(validation, field, ExtractError(rejection));
            return true;
        }

        /// <summary>The preview goes into changes, the whole payload of design section 7.4; steps names the one restore it previews.</summary>
        private static void SetWorkflowPreview(IDictionary<string, object> plan, RestorePreview preview, string action, string target)
        {
            MarkSemantic(plan);
            plan["changes"] = preview;
            plan["steps"] = new object[]
            {
                new Dictionary<string, object> { ["index"] = 1, ["action"] = action, ["target"] = target }
            };
        }
    }
}

// Producer:Betsy
