using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnitySkills
{
    /// <summary>
    /// Demonstration-recording ("macro") skills: record manual editor operations and invert
    /// them into a replayable /skills/batch step sequence. All heavy lifting lives in
    /// MacroRecorderService; these wrappers only define the REST contract.
    ///
    /// The macro_save/list/get/delete/run group turns stopped recordings into a per-project
    /// named library (Library/UnitySkillsMacros, one JSON file per macro in the MacroFileStore
    /// schema) where each macro behaves like a single skill: macro_run executes the whole
    /// sequence through the shared batch pipeline in one call.
    /// </summary>
    public static class MacroSkills
    {
        [UnitySkill("macro_record_start", "Start a demonstration-recording (macro) session: manual editor scene operations (and REST-driven ones) are captured for later inversion into a replayable skill sequence via macro_export. note: optional session label. Only one session may be active; the buffer holds up to 1000 records (auto-stops with stoppedReason 'buffer_full'). A domain reload discards an active session (see macro_record_status.interruptedByReload).",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "macro", "record", "demonstration", "teach" },
            Outputs = new[] { "recording", "startedAtUtc", "catalogSize" },
            RiskLevel = "low")]
        public static object MacroRecordStart(string note = null)
        {
            return MacroRecorderService.Start(note);
        }

        [UnitySkill("macro_record_stop", "Stop the active macro recording session and return a summary: {recordCount, durationSec, byKind, stoppedReason}. The stopped session stays available for macro_export until the next macro_record_start.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "macro", "record", "stop", "summary" },
            Outputs = new[] { "recordCount", "durationSec", "byKind", "stoppedReason" },
            RiskLevel = "low")]
        public static object MacroRecordStop()
        {
            return MacroRecorderService.Stop();
        }

        [UnitySkill("macro_record_status", "Get the macro recorder state: {recording, recordCount, startedAtUtc, stoppedReason, hasExportableSession, interruptedByReload}. interruptedByReload=true means the last recording was discarded by a domain reload.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "macro", "record", "status" },
            Outputs = new[] { "recording", "recordCount", "startedAtUtc", "interruptedByReload", "hasExportableSession" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object MacroRecordStatus()
        {
            return MacroRecorderService.Status();
        }

        [UnitySkill("macro_export", "Invert the most recent stopped macro recording into a replayable step sequence: {steps:[{skill,args}...], warnings, replayable}. format: 'batch' (default) — steps form a body directly POSTable to /skills/batch; objects created during the recording are referenced by later steps via {\"$ref\":\"$N.instanceId\"} inter-step references. Prefab instances dragged in during the recording are inverted into prefab_instantiate steps (the prefab asset must exist at replay time). Sibling-order changes are inverted into gameobject_set_sibling_index steps emitted after all other steps (net final order); unambiguous component removals become component_remove steps (a removal is skipped only when another instance of the same type remains on the object). Changes that cannot be inverted (asset edits, prefab apply/revert, ambiguous same-type component removal, ...) are listed in warnings and set replayable=false; objects created and destroyed within the recording are omitted entirely (net effect zero).",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Analyze,
            Tags = new[] { "macro", "export", "replay", "batch", "generalize" },
            Outputs = new[] { "steps", "warnings", "replayable", "stepCount" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object MacroExport(string format = "batch")
        {
            return MacroRecorderService.Export(format);
        }

        // ===== Named macro library (Library/UnitySkillsMacros) =====

        [UnitySkill("macro_save", "Save the most recent stopped macro recording into the macro library under a name (MacroFileStore schema — same file the panel exports). scope: 'project' (default, Library/UnitySkillsMacros — this project only) or 'global' (~/.unity_skills/macros — shared across every Unity project for cross-project reuse). Fails when the name is invalid as a file name, when a macro with that name exists in that scope and overwrite=false, or when there is no stopped session (never recorded / still recording).",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Create,
            Tags = new[] { "macro", "save", "library", "persist", "scope" },
            Outputs = new[] { "name", "scope", "path", "stepCount", "replayable" },
            RiskLevel = "low")]
        public static object MacroSave(string name, bool overwrite = false, string scope = null)
        {
            if (!MacroLibraryStore.TryNormalizeScope(scope, out var normScope, out var scopeError))
                return new { error = scopeError };
            if (!MacroLibraryStore.TryValidateName(name, out var nameError))
                return new { error = nameError };

            if (!MacroRecorderService.TryGetStoppedSessionExport(out var steps, out var warnings,
                    out var replayable, out var sessionError))
                return new { error = sessionError };

            var json = MacroFileStore.BuildJson(steps, warnings, replayable,
                MacroRecorderService.SessionNote,
                MacroRecorderService.SessionStartedUtc,
                Application.unityVersion,
                SceneManager.GetActiveScene().name);

            if (!MacroLibraryStore.TrySave(name, json, overwrite, normScope, out var saveError))
                return new { error = saveError };

            SkillsLogger.Log($"Macro '{name}' saved to the {normScope} library ({steps.Count} step(s), replayable: {replayable})");
            return new
            {
                success = true,
                name,
                scope = normScope,
                path = MacroLibraryStore.PathFor(name, normScope),
                stepCount = steps.Count,
                replayable,
                warningCount = warnings.Count,
                overwritten = overwrite
            };
        }

        [UnitySkill("macro_list", "List macros in the library: {name, scope, stepCount, replayable, recordedAtUtc, note, fileSizeBytes, params:[{name, hasDefault, default}]}. scope: omit to merge both scopes (each entry carries its scope; a project macro and a same-named global one both appear), or pin 'project' / 'global'. params aggregates the $param slots declared inside the macro's steps; entries without a default must be provided to macro_run. An empty library returns an empty array.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "macro", "list", "library", "scope" },
            Outputs = new[] { "count", "macros" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object MacroList(string scope = null)
        {
            List<string> scopes;
            if (string.IsNullOrWhiteSpace(scope))
            {
                scopes = new List<string> { MacroLibraryStore.ScopeProject, MacroLibraryStore.ScopeGlobal };
            }
            else
            {
                if (!MacroLibraryStore.TryNormalizeScope(scope, out var normScope, out var scopeError))
                    return new { error = scopeError };
                scopes = new List<string> { normScope };
            }

            var macros = new List<object>();
            foreach (var s in scopes)
            {
                foreach (var name in MacroLibraryStore.ListNames(s))
                {
                    if (!MacroLibraryStore.TryLoad(name, s, out var file, out var sizeBytes, out var loadError))
                    {
                        macros.Add(new { name, scope = s, error = loadError });
                        continue;
                    }
                    macros.Add(new
                    {
                        name,
                        scope = s,
                        stepCount = file.Steps.Count,
                        replayable = file.Replayable,
                        recordedAtUtc = file.RecordedAtUtc,
                        note = file.Note,
                        fileSizeBytes = sizeBytes,
                        @params = DescribeParams(file)
                    });
                }
            }
            return new { success = true, count = macros.Count, macros };
        }

        [UnitySkill("macro_get", "Get one library macro's full content: steps, params (saved $param defaults), paramDeclarations (aggregated $param slots), warnings and recording metadata. scope: omit to search project first then global (a project macro shadows a same-named global one; the response reports the scope that was hit), or pin 'project' / 'global'. Fails with the list of available names when the macro does not exist.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "macro", "get", "inspect", "library", "scope" },
            Outputs = new[] { "name", "scope", "steps", "params", "warnings", "replayable" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object MacroGet(string name, string scope = null)
        {
            if (!TryLoadOrError(name, scope, out var file, out var sizeBytes, out var hitScope, out var error))
                return error;

            return new
            {
                success = true,
                name,
                scope = hitScope,
                stepCount = file.Steps.Count,
                replayable = file.Replayable,
                recordedAtUtc = file.RecordedAtUtc,
                unityVersion = file.UnityVersion,
                sceneName = file.SceneName,
                note = file.Note,
                fileSizeBytes = sizeBytes,
                @params = file.Params,
                paramDeclarations = DescribeParams(file),
                steps = file.Steps,
                warnings = file.Warnings
            };
        }

        [UnitySkill("macro_delete", "Delete one macro from the library (other macros are untouched). scope: omit to delete from project first then global (only the scope that is hit; a same-named macro in the other scope survives), or pin 'project' / 'global'. Fails with the list of available names when the macro does not exist.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Delete,
            Tags = new[] { "macro", "delete", "library", "scope" },
            Outputs = new[] { "name", "scope", "deleted" },
            RiskLevel = "low")]
        public static object MacroDelete(string name, string scope = null)
        {
            if (!MacroLibraryStore.TryValidateName(name, out var nameError))
                return new { error = nameError };
            if (!TryPinScope(name, scope, out var hitScope, out var scopeErrorResult))
                return scopeErrorResult;
            if (!MacroLibraryStore.TryDelete(name, hitScope, out var deleteError))
                return new { error = deleteError };

            SkillsLogger.Log($"Macro '{name}' deleted from the {hitScope} library");
            return new { success = true, name, scope = hitScope, deleted = true };
        }

        [UnitySkill("macro_run", "Run a saved library macro as ONE skill call: loads its steps and executes them sequentially through the shared /skills/batch pipeline — $param substitution, inter-step $ref resolution, and the full per-step single-skill pipeline (permission gate, undo, audit; authorization interrupts stop the run and return the grant token). params: JSON object overriding the macro's saved $param defaults; bare $param slots that end up with no value fail BEFORE execution with the missing names. continueOnError: record failed steps and keep going instead of fail-fast. Returns the /skills/batch-shaped result {status, executed, failed, results} plus macro metadata; the whole run is one undo group.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "macro", "run", "replay", "batch", "library" },
            Outputs = new[] { "macro", "status", "executed", "failed", "results" },
            TracksWorkflow = true,
            MutatesScene = true,
            RiskLevel = "medium",
            SupportsDryRun = false)]
        public static object MacroRun(string name, string @params = null, bool continueOnError = false, string scope = null)
        {
            if (!TryLoadOrError(name, scope, out var file, out _, out var hitScope, out var error))
                return error;

            if (file.Steps.Count == 0)
                return new { error = $"Macro '{name}' has no steps; nothing to run." };

            // Effective params = saved defaults from the macro file, overridden by the caller's.
            var effectiveParams = (JObject)file.Params.DeepClone();
            if (!string.IsNullOrEmpty(@params))
            {
                JObject overrides;
                try
                {
                    overrides = JObject.Parse(@params);
                }
                catch (JsonException ex)
                {
                    return new { error = $"'params' must be a JSON object: {ex.Message}" };
                }
                foreach (var prop in overrides.Properties())
                    effectiveParams[prop.Name] = prop.Value;
            }

            var missing = MacroLibraryStore.ComputeMissingParams(file.Steps, effectiveParams);
            if (missing.Count > 0)
            {
                return new
                {
                    error = $"Macro '{name}' declares $param slot(s) with no default and no value provided: "
                        + string.Join(", ", missing) + ". Pass them via the 'params' object."
                };
            }

            var result = SkillsHttpServer.ExecuteBatchCore(file.Steps, effectiveParams, continueOnError,
                dryRun: false, transactional: false, agentId: "macro:" + name);
            result["macro"] = name;
            result["scope"] = hitScope;
            result["stepCount"] = file.Steps.Count;
            SkillsLogger.Log($"Macro '{name}' ({hitScope}) ran: {result["executed"]} step(s) executed, {result["failed"]} failed");
            return result;
        }

        /// <summary>
        /// Pins the scope a macro lives in: an explicit scope is normalized and used as-is;
        /// otherwise project is searched first, then global (project shadows global).
        /// On failure, error is the structured skill response.
        /// </summary>
        private static bool TryPinScope(string name, string rawScope, out string scope, out object error)
        {
            error = null;
            if (!string.IsNullOrWhiteSpace(rawScope))
            {
                if (!MacroLibraryStore.TryNormalizeScope(rawScope, out scope, out var scopeError))
                {
                    error = new { error = scopeError };
                    return false;
                }
                if (!MacroLibraryStore.Exists(name, scope))
                {
                    error = new { error = NotFoundMessage(name) };
                    return false;
                }
                return true;
            }
            if (!MacroLibraryStore.TryResolveExistingScope(name, out scope))
            {
                error = new { error = NotFoundMessage(name) };
                return false;
            }
            return true;
        }

        /// <summary>Shared load-or-structured-error for macro_get / macro_run (available names on a miss).</summary>
        private static bool TryLoadOrError(string name, string rawScope, out MacroFile file, out long sizeBytes,
            out string hitScope, out object error)
        {
            file = null;
            sizeBytes = 0;
            hitScope = null;
            if (!MacroLibraryStore.TryValidateName(name, out var nameError))
            {
                error = new { error = nameError };
                return false;
            }
            if (!TryPinScope(name, rawScope, out hitScope, out error))
                return false;
            if (!MacroLibraryStore.TryLoad(name, hitScope, out file, out sizeBytes, out var loadError))
            {
                error = new { error = $"Macro '{name}' ({hitScope}) could not be loaded: {loadError}" };
                return false;
            }
            return true;
        }

        private static string NotFoundMessage(string name)
        {
            var parts = new List<string>();
            foreach (var n in MacroLibraryStore.ListNames(MacroLibraryStore.ScopeProject))
                parts.Add(n + " (project)");
            foreach (var n in MacroLibraryStore.ListNames(MacroLibraryStore.ScopeGlobal))
                parts.Add(n + " (global)");
            return $"No macro named '{name}' exists in either scope. Available: "
                + (parts.Count > 0 ? string.Join(", ", parts) : "(none — both libraries are empty)");
        }

        /// <summary>Aggregated $param view of a macro: node-level defaults merged with the file's saved params.</summary>
        private static List<object> DescribeParams(MacroFile file)
        {
            var list = new List<object>();
            foreach (var decl in SkillsHttpServer.CollectBatchParamDeclarations(file.Steps))
            {
                bool savedDefault = file.Params.TryGetValue(decl.Name, System.StringComparison.Ordinal, out var savedValue);
                list.Add(new
                {
                    name = decl.Name,
                    // The file's params object feeds the run as provided values, so a saved
                    // entry covers a bare slot exactly like an inline default does.
                    hasDefault = decl.HasDefault || savedDefault,
                    @default = savedDefault ? savedValue : decl.DefaultValue
                });
            }
            return list;
        }
    }
}
