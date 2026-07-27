using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Editor control skills - play mode, selection, tools.
    /// </summary>
    [InitializeOnLoad]
    public static class EditorSkills
    {
        static EditorSkills()
        {
            RecoverStalePlaymodeStepJobs();
        }

        [UnitySkill("editor_play", "Enter play mode. Warning: any unsaved scene changes made during Play mode will be lost when exiting.",
            Category = SkillCategory.Editor, Operation = SkillOperation.Execute,
            Tags = new[] { "play", "runtime", "test" },
            Outputs = new[] { "mode", "jobId" },
            MayEnterPlayMode = true, RiskLevel = "medium", SupportsDryRun = false)]
        public static object EditorPlay()
        {
            if (EditorApplication.isPlaying)
                return new { error = "Already in play mode" };

            var job = AsyncJobService.CreateJob(
                "playmode", "entering_play_mode", "Entering Play Mode.", false);
            EditorApplication.isPlaying = true;
            return new { success = true, mode = "playing", jobId = job.jobId };
        }

        [UnitySkill("editor_play_capture", "Enter Play Mode, observe runtime errors for a fixed duration, optionally capture the Game View, then exit and return a job report.",
            Category = SkillCategory.Editor, Operation = SkillOperation.Execute | SkillOperation.Analyze,
            Tags = new[] { "play", "runtime", "observe", "errors", "screenshot", "test", "job" },
            Outputs = new[] { "jobId", "kind", "durationSeconds", "captureScreenshot" },
            MayEnterPlayMode = true, RiskLevel = "medium", SupportsDryRun = false)]
        public static object EditorPlayCapture(int durationSeconds = 10, bool captureScreenshot = false,
            string screenshotFilename = null, int maxErrors = 50)
        {
            return PlayCaptureService.Start(durationSeconds, captureScreenshot, screenshotFilename, maxErrors);
        }

        [UnitySkill("editor_stop", "Exit play mode. Warning: any scene changes made during Play mode will be lost.",
            Category = SkillCategory.Editor, Operation = SkillOperation.Execute,
            Tags = new[] { "stop", "runtime", "exit" },
            Outputs = new[] { "mode" },
            MayEnterPlayMode = true, SupportsDryRun = false)]
        public static object EditorStop()
        {
            if (!EditorApplication.isPlaying)
                return new { error = "Not in play mode" };

            EditorApplication.isPlaying = false;
            return new { success = true, mode = "stopped" };
        }

        [UnitySkill("editor_pause", "Pause/unpause play mode",
            Category = SkillCategory.Editor, Operation = SkillOperation.Execute,
            Tags = new[] { "pause", "resume", "toggle" },
            Outputs = new[] { "paused" },
            MayEnterPlayMode = true, SupportsDryRun = false)]
        public static object EditorPause()
        {
            EditorApplication.isPaused = !EditorApplication.isPaused;
            return new { success = true, paused = EditorApplication.isPaused };
        }

        private const string PlaymodeStepJobKind = "playmode_step";
        private const int PlaymodeStepTimeoutSeconds = 10;

        [UnitySkill("editor_playmode_step", "Advance Play Mode forward by N frames (1-100, default 1) using EditorApplication.Step; Unity automatically enters paused state as part of stepping. Requires Play Mode to already be active (call editor_play or editor_play_capture first). Runs as an async job because each Step() call is only confirmed on a later Editor tick (verified via Time.frameCount before issuing the next one) rather than synchronously - poll job_status/job_wait with the returned jobId. The completed job's result contains framesCompleted, frameCount and isPaused.",
            Category = SkillCategory.Editor, Operation = SkillOperation.Execute,
            Tags = new[] { "play", "step", "frame", "runtime", "debug", "job" },
            Outputs = new[] { "jobId", "framesRequested", "framesCompleted", "frameCount", "isPaused" },
            RiskLevel = "low", SupportsDryRun = false)]
        public static object EditorPlaymodeStep(int frames = 1)
        {
            if (!EditorApplication.isPlaying)
                return new
                {
                    error = "Not in Play Mode. Frame stepping only works while Play Mode is already active.",
                    hint = "Call editor_play or editor_play_capture first, then retry editor_playmode_step.",
                    suggestedSkills = new[] { "editor_play", "editor_play_capture" }
                };

            var activeJob = BatchPersistence.ListJobs(100).FirstOrDefault(job =>
                job != null && string.Equals(job.kind, PlaymodeStepJobKind, StringComparison.OrdinalIgnoreCase) &&
                job.status != "completed" && job.status != "failed" && job.status != "cancelled");
            if (activeJob != null)
                return new { error = $"Another frame-step job is already active: {activeJob.jobId} ({activeJob.currentStage ?? activeJob.status})." };

            var clampedFrames = Mathf.Clamp(frames, 1, 100);
            var startFrame = Time.frameCount;

            var job = AsyncJobService.CreateJob(
                PlaymodeStepJobKind, "stepping", $"Stepping Play Mode forward by {clampedFrames} frame(s).", true,
                metadata: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["framesRequested"] = clampedFrames,
                    ["framesIssued"] = 0,
                    ["lastFrameCountAtIssue"] = (long)startFrame,
                    ["issuedAtUtcTicks"] = 0L,
                },
                resultData: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["framesRequested"] = clampedFrames,
                    ["framesCompleted"] = 0,
                    ["frameCount"] = startFrame,
                    ["isPaused"] = EditorApplication.isPaused,
                });
            job.status = "running";
            BatchPersistence.UpsertJob(job);

            EditorApplication.CallbackFunction handler = null;
            handler = () => ProcessPlaymodeStepJob(job.jobId, handler);
            EditorApplication.update += handler;

            return new { success = true, status = "accepted", jobId = job.jobId, framesRequested = clampedFrames };
        }

        /// <summary>
        /// Advances one <see cref="PlaymodeStepJobKind"/> job per Editor tick. Each <see cref="EditorApplication.Step"/>
        /// call is confirmed by observing <see cref="Time.frameCount"/> advance past the value recorded when that
        /// step was issued before the next one is issued - back-to-back Step() calls within the same tick are not
        /// guaranteed to land, so this never issues a new one until the previous one is confirmed.
        /// </summary>
        private static void ProcessPlaymodeStepJob(string jobId, EditorApplication.CallbackFunction handler)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null || job.status == "completed" || job.status == "failed" || job.status == "cancelled")
            {
                EditorApplication.update -= handler;
                return;
            }

            if (!EditorApplication.isPlaying)
            {
                AsyncJobService.FailJob(jobId, "Play Mode was exited before frame stepping completed.", "failed_exited_play_mode", job.resultData);
                EditorApplication.update -= handler;
                return;
            }

            var framesRequested = GetMetaInt(job, "framesRequested", 1);
            var framesIssued = GetMetaInt(job, "framesIssued", 0);
            var lastFrameCountAtIssue = GetMetaLong(job, "lastFrameCountAtIssue", Time.frameCount);
            var currentFrame = Time.frameCount;

            if (framesIssued == 0)
            {
                EditorApplication.Step();
                job.metadata["framesIssued"] = 1;
                job.metadata["lastFrameCountAtIssue"] = (long)currentFrame;
                job.metadata["issuedAtUtcTicks"] = DateTime.UtcNow.Ticks;
                BatchPersistence.UpsertJob(job);
                return;
            }

            if (currentFrame <= lastFrameCountAtIssue)
            {
                var issuedAt = GetMetaLong(job, "issuedAtUtcTicks", DateTime.UtcNow.Ticks);
                if (DateTime.UtcNow.Ticks - issuedAt > PlaymodeStepTimeoutSeconds * TimeSpan.TicksPerSecond)
                {
                    AsyncJobService.FailJob(jobId, $"Unity did not advance the frame within {PlaymodeStepTimeoutSeconds}s.", "failed_step_timeout", job.resultData);
                    EditorApplication.update -= handler;
                }
                return;
            }

            // Frame count advanced past the value recorded at issue time - the previous Step() landed.
            if (framesIssued >= framesRequested)
            {
                job.resultData["framesCompleted"] = framesIssued;
                job.resultData["frameCount"] = currentFrame;
                job.resultData["isPaused"] = EditorApplication.isPaused;
                AsyncJobService.CompleteJob(jobId, $"Advanced Play Mode by {framesIssued} frame(s).", job.resultData);
                EditorApplication.update -= handler;
                return;
            }

            EditorApplication.Step();
            job.metadata["framesIssued"] = framesIssued + 1;
            job.metadata["lastFrameCountAtIssue"] = (long)currentFrame;
            job.metadata["issuedAtUtcTicks"] = DateTime.UtcNow.Ticks;
            job.resultData["framesCompleted"] = framesIssued;
            job.resultData["frameCount"] = currentFrame;
            BatchPersistence.UpsertJob(job);
        }

        private static int GetMetaInt(BatchJobRecord job, string key, int fallback)
        {
            if (job?.metadata == null || !job.metadata.TryGetValue(key, out var value) || value == null) return fallback;
            if (value is int i) return i;
            if (value is long l) return (int)l;
            return int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }

        private static long GetMetaLong(BatchJobRecord job, string key, long fallback)
        {
            if (job?.metadata == null || !job.metadata.TryGetValue(key, out var value) || value == null) return fallback;
            if (value is long l) return l;
            if (value is int i) return i;
            return long.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }

        /// <summary>
        /// The step job is advanced by a dynamically-subscribed <see cref="EditorApplication.update"/> handler
        /// (see <see cref="EditorPlaymodeStep"/>), which does not survive a domain reload. Fail any job left
        /// "running" from before a reload so callers polling job_status get a terminal state instead of hanging.
        /// </summary>
        private static void RecoverStalePlaymodeStepJobs()
        {
            try
            {
                foreach (var job in BatchPersistence.ListJobs(100))
                {
                    if (job != null && string.Equals(job.kind, PlaymodeStepJobKind, StringComparison.OrdinalIgnoreCase) &&
                        job.status == "running")
                    {
                        AsyncJobService.FailJob(job.jobId,
                            "Frame stepping was interrupted by a domain reload or Editor restart before it finished.",
                            "failed_interrupted", job.resultData);
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("[UnitySkills] EditorSkills stale playmode_step job recovery failed: " + ex);
            }
        }

        [UnitySkill("editor_playmode_inspect", "Inspect a GameObject's live runtime state: transform (position/rotation/scale), activeSelf/activeInHierarchy, and optionally one component's public fields and properties. Works during Play Mode (including while paused) and also in Edit Mode, where it returns editor-time values - check the isPlaying/isPaused flags in the response to know which. Combine with editor_playmode_step to assert state changes frame by frame.",
            Category = SkillCategory.Editor, Operation = SkillOperation.Query,
            Tags = new[] { "play", "runtime", "inspect", "state", "component", "debug" },
            Outputs = new[] { "gameObject", "transform", "activeSelf", "isPlaying", "isPaused", "component" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EditorPlaymodeInspect(string name = null, int instanceId = 0, string path = null, string componentType = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (findErr != null) return findErr;

            var t = go.transform;
            object componentInfo = string.IsNullOrEmpty(componentType)
                ? null
                : ComponentSkills.ComponentGetProperties(name, instanceId, path, componentType);

            return new
            {
                success = true,
                gameObject = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                path = GameObjectFinder.GetPath(go),
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                transform = new
                {
                    position = new { x = t.position.x, y = t.position.y, z = t.position.z },
                    localPosition = new { x = t.localPosition.x, y = t.localPosition.y, z = t.localPosition.z },
                    rotation = new { x = t.eulerAngles.x, y = t.eulerAngles.y, z = t.eulerAngles.z },
                    localScale = new { x = t.localScale.x, y = t.localScale.y, z = t.localScale.z }
                },
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                component = componentInfo
            };
        }

        [UnitySkill("editor_select", "Select a GameObject",
            Category = SkillCategory.Editor, Operation = SkillOperation.Execute,
            Tags = new[] { "select", "focus", "highlight" },
            Outputs = new[] { "selected" },
            RequiresInput = new[] { "gameObject" })]
        public static object EditorSelect(string name = null, int instanceId = 0, string path = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (findErr != null) return findErr;

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);

            return new { success = true, selected = go.name };
        }

        [UnitySkill("editor_get_selection", "Get currently selected objects",
            Category = SkillCategory.Editor, Operation = SkillOperation.Query,
            Tags = new[] { "selection", "current", "active" },
            Outputs = new[] { "count", "objects", "instanceId" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EditorGetSelection()
        {
            var selected = Selection.gameObjects.Select(go => new
            {
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go)
            }).ToArray();

            return new { count = selected.Length, objects = selected };
        }

        [UnitySkill("editor_undo", "Undo the last action (single step). For multiple undo steps use history_undo(steps=N). For workflow-level undo use workflow_undo_task.",
            Category = SkillCategory.Editor, Operation = SkillOperation.Execute,
            Tags = new[] { "undo", "revert", "history" },
            Outputs = new[] { "message" })]
        public static object EditorUndo()
        {
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
            Undo.PerformUndo();
            Undo.FlushUndoRecordObjects();
            return new { success = true, message = "Undo performed" };
        }

        [UnitySkill("editor_redo", "Redo the last undone action (single step). For multiple redo steps use history_redo(steps=N).",
            Category = SkillCategory.Editor, Operation = SkillOperation.Execute,
            Tags = new[] { "redo", "restore", "history" },
            Outputs = new[] { "message" })]
        public static object EditorRedo()
        {
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
            Undo.PerformRedo();
            Undo.FlushUndoRecordObjects();
            return new { success = true, message = "Redo performed" };
        }

        [UnitySkill("editor_get_state", "Get current editor state",
            Category = SkillCategory.Editor, Operation = SkillOperation.Query,
            Tags = new[] { "state", "status", "info" },
            Outputs = new[] { "isPlaying", "isPaused", "isCompiling", "unityVersion" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EditorGetState()
        {
            return new
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                timeSinceStartup = EditorApplication.timeSinceStartup,
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString()
            };
        }

        [UnitySkill("editor_get_changes", "Read the persistent editor-change journal instead of parsing .unity YAML. Use after the user edited the project while the AI was away, after external file changes, or after asking the user to make manual Editor changes. Returns scene structure/property summaries and imported/deleted/moved asset paths newer than 'since'. Omit since (or pass 0) for retained history, then pass the returned cursor on the next call. types: all/scene/file/undo/lifecycle (comma-separated). source: all/editor/manual/rest.",
            Category = SkillCategory.Editor, Operation = SkillOperation.Query,
            Tags = new[] { "changes", "observe", "journal", "scene", "files", "cursor" },
            Outputs = new[] { "hasChanges", "cursor", "oldestSeq", "dropped", "truncated", "changes" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EditorGetChanges(long since = 0, string types = null, string source = "all", int limit = 100)
        {
            return EditorChangeTrackerService.ReadChanges(since, types, source, limit);
        }

        [UnitySkill("editor_execute_menu", "Execute a Unity menu item",
            Category = SkillCategory.Editor, Operation = SkillOperation.Execute,
            Tags = new[] { "menu", "command", "action" },
            Outputs = new[] { "executed" })]
        public static object EditorExecuteMenu(string menuPath)
        {
            var result = EditorApplication.ExecuteMenuItem(menuPath);
            if (!result)
                return new { error = $"Menu item not found or failed: {menuPath}" };

            return new { success = true, executed = menuPath };
        }

        [UnitySkill("editor_get_tags", "Get all available tags",
            Category = SkillCategory.Editor, Operation = SkillOperation.Query,
            Tags = new[] { "tags", "list", "config" },
            Outputs = new[] { "tags" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EditorGetTags()
        {
            return new { tags = InternalEditorUtility.tags };
        }

        [UnitySkill("editor_get_layers", "Get all available layers",
            Category = SkillCategory.Editor, Operation = SkillOperation.Query,
            Tags = new[] { "layers", "list", "config" },
            Outputs = new[] { "layers" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EditorGetLayers()
        {
            var layers = Enumerable.Range(0, 32)
                .Select(i => new { index = i, name = LayerMask.LayerToName(i) })
                .Where(l => !string.IsNullOrEmpty(l.name))
                .ToArray();

            return new { layers };
        }

        [UnitySkill("editor_get_context", "Get full editor context - selected GameObjects, selected assets, active scene, focused window. Use this to get current selection without searching.",
            Category = SkillCategory.Editor, Operation = SkillOperation.Query,
            Tags = new[] { "context", "selection", "workspace", "overview" },
            Outputs = new[] { "selectedGameObjects", "selectedAssets", "activeScene", "focusedWindow" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EditorGetContext(bool includeComponents = false, bool includeChildren = false)
        {
            // 1. Hierarchy 选中的 GameObjects
            var selectedGameObjects = Selection.gameObjects.Select(go =>
            {
                var info = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["name"] = go.name,
                    ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                    ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                    ["path"] = GameObjectFinder.GetPath(go),
                    ["tag"] = go.tag,
                    ["layer"] = LayerMask.LayerToName(go.layer),
                    ["isActive"] = go.activeSelf
                };

                if (includeComponents)
                {
                    info["components"] = go.GetComponents<Component>()
                        .Where(c => c != null)
                        .Select(c => c.GetType().Name)
                        .ToArray();
                }

                if (includeChildren && go.transform.childCount > 0)
                {
                    var children = new System.Collections.Generic.List<object>();
                    foreach (Transform child in go.transform)
                    {
                        children.Add(new { name = child.name, entityId = UnityObjectIdUtility.GetEntityId(child.gameObject), instanceId = UnityObjectIdUtility.GetObjectId(child.gameObject) });
                    }
                    info["children"] = children;
                }

                return info;
            }).ToArray();

            // 2. Project 窗口选中的资源 (通过 GUID)
            var selectedAssets = Selection.assetGUIDs.Select(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
                return new
                {
                    guid,
                    path,
                    type = assetType?.Name ?? "Unknown",
                    isFolder = AssetDatabase.IsValidFolder(path)
                };
            }).ToArray();

            // 3. 当前活动场景
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            // 4. 焦点窗口
            var focusedWindow = EditorWindow.focusedWindow;

            return new
            {
                success = true,
                selectedGameObjects = new
                {
                    count = selectedGameObjects.Length,
                    objects = selectedGameObjects
                },
                selectedAssets = new
                {
                    count = selectedAssets.Length,
                    assets = selectedAssets
                },
                activeScene = new
                {
                    name = activeScene.name,
                    path = activeScene.path,
                    isDirty = activeScene.isDirty
                },
                focusedWindow = focusedWindow?.GetType().Name ?? "None",
                isPlaying = EditorApplication.isPlaying,
                isCompiling = EditorApplication.isCompiling
            };
        }

    }
}

// Producer:Betsy
