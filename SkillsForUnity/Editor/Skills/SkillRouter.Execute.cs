using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillRouter
    {
        public static string Execute(string name, string json)
        {
            return Execute(name, json, captureDiff: false);
        }

        /// <summary>
        /// Executes a skill. When <paramref name="captureDiff"/> is true (POST /skill/{name}?diff=1),
        /// captures a semantic scene diff as a pure side-channel observer, attached to a successful response as a top-level "sceneDiff" field --
        /// telling the caller what this operation actually changed. The diff never affects execution: the undo/workflow/error branches are left entirely untouched,
        /// and any diff failure only degrades sceneDiff to {error:...}, without affecting the skill's result.
        /// When captureDiff is false, output is byte-for-byte identical to before.
        /// </summary>
        public static string Execute(string name, string json, bool captureDiff) => Execute(name, json, captureDiff, null);

        /// <summary>
        /// <paramref name="dryRunGate"/> is non-null only for POST /skill/{name}: that is the one caller the dryRun policy
        /// gates. The panel, tests, /skills/batch steps and approval replays pass null and are never gated here.
        /// </summary>
        internal static string Execute(string name, string json, bool captureDiff, DryRunGateContext dryRunGate)
        {
            Initialize();
            if (!_skills.TryGetValue(name, out var skill))
            {
                return ResolveSkillNotFound(name);
            }

            bool autoStartedWorkflow = false;
            // The persistence cost of EndTask() on the auto-workflow path, attached to the success envelope as workflowEndMs.
            // Always null on every other path, to keep output byte-for-byte unchanged.
            long? workflowEndMs = null;
            var wrapWithUndoTransaction = !skill.ReadOnly && !_transactionlessSkills.Contains(name);
            int undoGroup = -1;
            int workflowSnapshotCountBefore = WorkflowManager.CurrentTask?.snapshots?.Count ?? 0;
            // Name-resolution notes the finder records while this call runs (e.g. a substring match); attached to the response.
            List<string> resolutionNotes = null;
            // In the persisted editor change log, attributes the changes this call caused (including the
            // end-of-frame ObjectChangeEvent) to REST.
            EditorChangeTrackerService.BeginRestExecution();
            try
            {
                // Discards notes a previous call left behind, so everything drained below belongs to this call.
                GameObjectFinder.DrainResolutionNotes();
                var validation = ValidateParameters(skill, json);
                resolutionNotes = MergeResolutionNotes(resolutionNotes);

                // A failed validation answers with the full correction report: the first failing bucket still picks the
                // errorCode and message (same order as always), while details carries every bucket and the parameter list,
                // so one response is enough to fix everything. Nothing has been touched yet, so these returns need no unwinding.
                if (validation.UnknownParams.Count > 0)
                {
                    var fixes = BuildUnknownParamFixes(name, validation.UnknownParams);
                    return SkillErrorResponse.Build(
                        SkillErrorCode.UnknownParam,
                        $"Unknown parameters: {string.Join(", ", ExtractValidationParameterNames(validation.UnknownParams))}",
                        skill: name,
                        details: new
                        {
                            unknownParams = validation.UnknownParams.ToArray(),
                            allowedParams = GetEffectiveParameterNames(skill),
                            validation = BuildValidationBlock(validation),
                            parameters = BuildParameterReport(skill)
                        },
                        suggestedFixes: fixes,
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry,
                        extra: ResolutionNotesExtra(resolutionNotes));
                }

                if (validation.MissingParams.Count > 0)
                {
                    return SkillErrorResponse.Build(
                        SkillErrorCode.MissingParam,
                        $"Missing required parameter: {validation.MissingParams[0]}",
                        skill: name,
                        details: new
                        {
                            missingParams = validation.MissingParams.ToArray(),
                            allowedParams = GetEffectiveParameterNames(skill),
                            validation = BuildValidationBlock(validation),
                            parameters = BuildParameterReport(skill)
                        },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry,
                        extra: ResolutionNotesExtra(resolutionNotes));
                }

                if (validation.TypeErrors.Count > 0)
                {
                    var firstTypeError = validation.TypeErrors[0];
                    var message = SkillResultHelper.TryGetMemberValue(firstTypeError, "error", out var errorValue) && errorValue != null
                        ? errorValue.ToString()
                        : "Parameter type mismatch";
                    return SkillErrorResponse.Build(
                        SkillErrorCode.TypeMismatch,
                        message,
                        skill: name,
                        details: new
                        {
                            typeErrors = validation.TypeErrors.ToArray(),
                            allowedParams = GetEffectiveParameterNames(skill),
                            validation = BuildValidationBlock(validation),
                            parameters = BuildParameterReport(skill)
                        },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry,
                        extra: ResolutionNotesExtra(resolutionNotes));
                }

                if (validation.SemanticErrors.Count > 0)
                {
                    return SkillErrorResponse.Build(
                        SkillErrorCode.SemanticInvalid,
                        ExtractValidationMessage(validation.SemanticErrors[0], "Semantic validation failed"),
                        skill: name,
                        details: new
                        {
                            semanticErrors = validation.SemanticErrors.ToArray(),
                            warnings = validation.Warnings.Count > 0 ? validation.Warnings.ToArray() : null,
                            allowedParams = GetEffectiveParameterNames(skill),
                            validation = BuildValidationBlock(validation),
                            parameters = BuildParameterReport(skill)
                        },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry,
                        extra: ResolutionNotesExtra(resolutionNotes));
                }

                // The surface profile gate. Must run *before* the permission gate -- this ordering is itself a contract:
                // the permission tier answers "can this skill run," the profile answers "did the user even put it on the menu."
                // Bypass mode and the allowlist are authorization, so they cannot lift an exclusion -- only the user switching the profile back to full can.
                // If the profile gate ran second, Bypass could hand out a skill the panel marks hidden.
                var surfaceGate = ApplySurfaceGate(skill, name);
                if (surfaceGate != null)
                    return surfaceGate;

                // The declared-package gate: a pure metadata check with no side effects. After the surface gate, so a hidden
                // skill never sends the agent off to install a package for a call it can never make; before the permission
                // gate, so the user is never asked to approve a call that can only fail.
                if (validation.MissingPackages.Count > 0)
                    return BuildMissingPackageResponse(skill, name, validation, resolutionNotes);

                // The dryRun policy gate: after validation and the terminal surface / package verdicts (a token for a call
                // that can never run is waste), before the permission gate (the user is never asked to approve a call that
                // then bounces for a missing token, and a consumed token is not asked for again by the grant replay).
                if (dryRunGate != null)
                {
                    var dryRunRequired = ApplyDryRunPolicyGate(skill, name, json, dryRunGate);
                    if (dryRunRequired != null)
                        return dryRunRequired;
                }

                // The permission tier gate. Placed before the high-risk confirmation gate, so a skill that is both FullAuto and high-risk
                // reports MODE_RESTRICTED first; the ConfirmationToken step only matters once the skill is already allowed to run.
                var modeGate = ApplyModeGate(skill, name, validation);
                if (modeGate != null)
                    return modeGate;

                // Confirmation gate: once ConfirmationTokenService.RequireConfirmation is enabled,
                // a high-risk skill requires an explicit one-time token.
                // Off by default -- enable it in Window > UnitySkills > Server > Settings.
                if (ConfirmationTokenService.RequireConfirmation && ConfirmationTokenService.IsHighRisk(skill))
                {
                    var gateResult = ApplyConfirmationGate(skill, name, json, validation);
                    if (gateResult != null)
                        return gateResult;
                }

                var args = validation.Args;
                var invoke = validation.InvokeArgs;

                // Pre-capture for the semantic diff (?diff=1). A pure side-channel observer, positioned after the permission gates and before invoke;
                // skipped for read-only skills (nothing to diff against). CaptureBefore fully isolates its own exceptions internally.
                SkillSceneDiff.DiffCapture diffCapture = null;
                if (captureDiff && !skill.ReadOnly)
                    diffCapture = SkillSceneDiff.CaptureBefore(args);

                if (wrapWithUndoTransaction)
                {
                    UnityEditor.Undo.IncrementCurrentGroup();
                    UnityEditor.Undo.SetCurrentGroupName($"Skill: {name}");
                    undoGroup = UnityEditor.Undo.GetCurrentGroup();
                }

                // ========== Automatic workflow recording ==========
                if (skill.TracksWorkflow && !WorkflowManager.IsRecording)
                {
                    var desc = $"{name} - {(json?.Length > 80 ? json.Substring(0, 80) + "..." : json ?? "")}";
                    WorkflowManager.BeginTask(name, desc);
                    autoStartedWorkflow = true;
                }

                // Automatically snapshots the target objects *before* the skill executes, to support rollback.
                // A skill that manages its own dedicated snapshot opts out via SkipAutoPresnapshot, to avoid a redundant generic backup.
                if (WorkflowManager.IsRecording && !skill.SkipAutoPresnapshot)
                {
                    TrySnapshotTargetsFromArgs(args);
                }
                // ==============================================

                // verbose control
                bool verbose = true; // Defaults to true when unspecified, for backward compatibility with direct calls
                if (args.TryGetValue("verbose", StringComparison.OrdinalIgnoreCase, out var verboseToken))
                {
                    // TryParseVerboseFlag is shared with ValidateParameters's dryRun-time check
                    // (ValidateReservedBodyParameters), so the two paths accept and reject the same values.
                    if (!TryParseVerboseFlag(verboseToken, out verbose))
                    {
                        // Nothing has been invoked yet at this point; roll back the bookkeeping started above,
                        // consistent with the catch handling below.
                        if (autoStartedWorkflow && WorkflowManager.IsRecording)
                            WorkflowManager.AbortTask();
                        else if (WorkflowManager.IsRecording)
                            WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);
                        if (undoGroup >= 0)
                            UnityEditor.Undo.RevertAllInCurrentGroup();

                        return SkillErrorResponse.Build(
                            SkillErrorCode.TypeMismatch,
                            $"Parameter 'verbose' must be a boolean (true/false), got: {verboseToken.ToString(Formatting.None)}",
                            skill: name,
                            details: new { typeErrors = new object[] { new { parameter = "verbose", expectedType = "boolean", error = $"Cannot convert {verboseToken.Type} to Boolean" } } },
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    }
                    args.Remove("verbose");
                }

                // Pagination control for Summary mode.
                // Skipped if the skill itself declares a parameter with the same name: 'limit' belongs to asset_find/light_find_all/etc. themselves,
                // and must reach them as their own parameter rather than being swallowed by the envelope layer as pagination (which would also wrap small results in a page).
                int? offset = null;
                int? limit = null;

                if (args.TryGetValue("pageOffset", StringComparison.OrdinalIgnoreCase, out var pageOffsetToken))
                {
                    if (!TryReadPagingArg(pageOffsetToken, "pageOffset", 0, out var value, out var error))
                    {
                        UnwindBeforeInvoke(autoStartedWorkflow, workflowSnapshotCountBefore, undoGroup);
                        return SkillErrorResponse.Build(SkillErrorCode.TypeMismatch, error, skill: name,
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    }
                    offset = value;
                    args.Remove("pageOffset");
                }

                if (args.TryGetValue("pageLimit", StringComparison.OrdinalIgnoreCase, out var pageLimitToken))
                {
                    if (!TryReadPagingArg(pageLimitToken, "pageLimit", 1, out var value, out var error))
                    {
                        UnwindBeforeInvoke(autoStartedWorkflow, workflowSnapshotCountBefore, undoGroup);
                        return SkillErrorResponse.Build(SkillErrorCode.TypeMismatch, error, skill: name,
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    }
                    limit = value;
                    args.Remove("pageLimit");
                }

                if (!offset.HasValue && !SkillDeclaresParameter(skill, "offset") &&
                    args.TryGetValue("offset", StringComparison.OrdinalIgnoreCase, out var offsetToken))
                {
                    if (!TryReadPagingArg(offsetToken, "offset", minValue: 0, out var offsetValue, out var offsetError))
                    {
                        // Nothing has been invoked yet at this point; roll back the bookkeeping started above.
                        UnwindBeforeInvoke(autoStartedWorkflow, workflowSnapshotCountBefore, undoGroup);
                        return SkillErrorResponse.Build(
                            SkillErrorCode.TypeMismatch,
                            offsetError,
                            skill: name,
                            details: new { typeErrors = new object[] { new { parameter = "offset", expectedType = "integer", error = offsetError } } },
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    }
                    offset = offsetValue;
                    args.Remove("offset");
                }

                if (!limit.HasValue && !SkillDeclaresParameter(skill, "limit") &&
                    args.TryGetValue("limit", StringComparison.OrdinalIgnoreCase, out var limitToken))
                {
                    if (!TryReadPagingArg(limitToken, "limit", minValue: 1, out var limitValue, out var limitError))
                    {
                        UnwindBeforeInvoke(autoStartedWorkflow, workflowSnapshotCountBefore, undoGroup);
                        return SkillErrorResponse.Build(
                            SkillErrorCode.TypeMismatch,
                            limitError,
                            skill: name,
                            details: new { typeErrors = new object[] { new { parameter = "limit", expectedType = "integer", error = limitError } } },
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    }
                    limit = limitValue;
                    args.Remove("limit");
                }

                // Discard notes from the workflow pre-snapshot / diff pre-capture: they look up the names a create is about to
                // make, which says nothing about what the skill itself resolves (the skill records its own notes again).
                GameObjectFinder.DrainResolutionNotes();
                var result = skill.Method.Invoke(null, invoke);
                // Drained before the success envelope is built: the entityId enrichment there looks objects up by name
                // again, and those lookups describe the response, not what the skill resolved.
                resolutionNotes = MergeResolutionNotes(resolutionNotes);

                if (!skill.ReadOnly)
                    UnityEditor.Undo.FlushUndoRecordObjects();

                if (SkillResultHelper.TryGetErrorContext(result, out var errorContext))
                {
                    if (autoStartedWorkflow && WorkflowManager.IsRecording)
                        WorkflowManager.AbortTask();
                    else if (WorkflowManager.IsRecording)
                        WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);

                    if (undoGroup >= 0)
                        UnityEditor.Undo.RevertAllInCurrentGroup();

                    // Every skill's business error funnels in here. Whatever the skill itself declared takes priority field by field;
                    // the classifier only fills gaps, so the roughly 700 skills that just return { error = "..." } still get an error code
                    // and retry strategy, instead of a uniform SKILL_ERROR + abort. Declaring errorCode also pulls the rest of the fields along,
                    // keeping a partial declaration self-consistent.
                    var classified = errorContext.Code.HasValue
                        ? SkillErrorClassifier.ForCode(errorContext.Code.Value, errorContext.Message)
                        : SkillErrorClassifier.Classify(errorContext.Message);

                    return SkillErrorResponse.Build(
                        errorContext.Code ?? classified.Code,
                        errorContext.Message,
                        skill: name,
                        suggestedFixes: errorContext.SuggestedFixes ?? classified.SuggestedFixes,
                        relatedSkills: errorContext.RelatedSkills ?? classified.RelatedSkills,
                        retryStrategy: errorContext.RetryStrategy ?? classified.RetryStrategy,
                        extra: WithResolutionNotes(errorContext.Extra, resolutionNotes));
                }

                // ========== Automatic workflow wrap-up ==========
                if (autoStartedWorkflow)
                {
                    // On the auto-workflow path, persistence is entirely EndTask's responsibility (it calls SaveHistory internally).
                    // The cost is measured here for observability.
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    WorkflowManager.EndTask();
                    sw.Stop();
                    workflowEndMs = sw.ElapsedMilliseconds;
                }
                else if (WorkflowManager.IsRecording)
                {
                    // Manual session (workflow_begin_task): otherwise every tracked skill would save on every single call.
                    // Skips the save when the current task has had no new snapshots since the last save.
                    if (ManualSessionIsDirty(WorkflowManager.CurrentTask))
                        WorkflowManager.SaveHistory();
                }
                // ========================================

                if (wrapWithUndoTransaction)
                {
                    // Commit the transaction
                    UnityEditor.Undo.CollapseUndoOperations(undoGroup);

                    // A skill invoked over REST never passes through the usual menu/mouse event boundaries that advance Unity's undo stack.
                    // So explicitly move to the next group, so editor_undo/editor_redo act on the change that was just completed.
                    if (!skill.ReadOnly)
                        UnityEditor.Undo.IncrementCurrentGroup();
                }

                // Post-capture and comparison for the semantic diff (?diff=1). Attached to the success envelope as a top-level "sceneDiff";
                // null on the default path, to keep output byte-for-byte unchanged. BuildSceneDiff already isolates its own exceptions --
                // the diff can never break the response, and a skill that reported an error above never reaches this point anyway.
                JToken sceneDiff = BuildSceneDiff(captureDiff, skill, diffCapture, result);

                if (!verbose && result != null)
                {
                    // "Summary Mode" logic with pagination
                    var jsonResult = JToken.FromObject(result);

                    var arr = FindPageArray(jsonResult, out var arrayProperty);
                    // Read once: a single main-thread EditorPrefs-backed property access, reused below as both the
                    // auto-truncation trigger and the default page size, so the two can never disagree with each other.
                    int summaryPageSize = SummaryPageSize;
                    if (arr != null && ((SummaryAutoTruncate && arr.Count > summaryPageSize) || offset.HasValue || limit.HasValue))
                    {
                        int startIndex = offset ?? 0;
                        int pageSize = limit ?? summaryPageSize;

                        // Clamp to a valid range
                        if (startIndex >= arr.Count)
                        {
                            // offset is beyond the array bounds, return an empty page
                            var emptyWrapper = new JObject
                            {
                                ["isTruncated"] = true,
                                ["totalCount"] = arr.Count,
                                ["offset"] = startIndex,
                                ["limit"] = pageSize,
                                ["showing"] = 0,
                                ["items"] = new JArray(),
                                ["hint"] = $"Offset {startIndex} is beyond array bounds (totalCount: {arr.Count}). To see items, pass a lower 'pageOffset' value."
                            };
                            if (arrayProperty != null)
                            {
                                var preserved = (JObject)jsonResult.DeepClone();
                                preserved[arrayProperty] = new JArray();
                                foreach (var property in emptyWrapper.Properties().Where(property => property.Name != "items"))
                                    preserved[property.Name] = property.Value;
                                return SerializeSuccessResponse(preserved, sceneDiff, workflowEndMs, resolutionNotes);
                            }
                            return SerializeSuccessResponse(emptyWrapper, sceneDiff, workflowEndMs, resolutionNotes);
                        }

                        int endIndex = (int)Math.Min((long)startIndex + pageSize, arr.Count);
                        int actualCount = endIndex - startIndex;

                        var paginatedItems = new JArray();
                        for (int i = startIndex; i < endIndex; i++)
                            paginatedItems.Add(arr[i]);

                        bool hasMore = endIndex < arr.Count;
                        int? nextOffset = hasMore ? (int?)endIndex : null;

                        // Return a wrapper object carrying pagination metadata
                        var wrapper = new JObject
                        {
                            ["isTruncated"] = true,
                            ["totalCount"] = arr.Count,
                            ["offset"] = startIndex,
                            ["limit"] = pageSize,
                            ["showing"] = actualCount,
                            ["items"] = paginatedItems
                        };

                        if (hasMore)
                        {
                            wrapper["nextOffset"] = nextOffset;
                            wrapper["hint"] = $"Showing items {startIndex}-{endIndex - 1} of {arr.Count}. To see more, pass 'pageOffset={nextOffset}' (or 'verbose=true' for all items).";
                        }
                        else
                        {
                            wrapper["hint"] = $"Showing items {startIndex}-{endIndex - 1} of {arr.Count} (last page).";
                        }

                        if (arrayProperty != null)
                        {
                            var preserved = (JObject)jsonResult.DeepClone();
                            preserved[arrayProperty] = paginatedItems;
                            foreach (var property in wrapper.Properties().Where(property => property.Name != "items"))
                                preserved[property.Name] = property.Value;
                            return SerializeSuccessResponse(preserved, sceneDiff, workflowEndMs, resolutionNotes);
                        }

                        return SerializeSuccessResponse(wrapper, sceneDiff, workflowEndMs, resolutionNotes);
                    }
                }

                // Full mode (verbose=true, or the result is already small): return as-is
                return SerializeSuccessResponse(result, sceneDiff, workflowEndMs, resolutionNotes);
            }
            catch (TargetInvocationException ex)
            {
                // Clean up an auto-started workflow on error
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.AbortTask();
                else if (WorkflowManager.IsRecording)
                    WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);

                if (undoGroup >= 0)
                {
                    // Roll back the transaction
                    UnityEditor.Undo.RevertAllInCurrentGroup();
                }

                var inner = ex.InnerException ?? ex;
                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"[Transactional Revert] {inner.Message}",
                    skill: name,
                    details: new { exceptionType = inner.GetType().Name },
                    retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                    extra: ResolutionNotesExtra(MergeResolutionNotes(resolutionNotes)));
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                // Malformed request body -- JObject.Parse inside ValidateParameters throws before any change or undo group
                // has been opened. This is a client error, not a server or transaction failure: return
                // InvalidJson + fix_and_retry, so the agent edits the request body instead of spinning on wait_and_retry
                // (the generic catch below would mislabel it as "[Transactional Revert]"). Consistent with DryRun.
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidJson,
                    $"Invalid JSON: {ex.Message}",
                    skill: name,
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }
            catch (Exception ex)
            {
                // Clean up an auto-started workflow on error
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.AbortTask();
                else if (WorkflowManager.IsRecording)
                    WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);

                if (undoGroup >= 0)
                {
                    // Roll back the transaction
                    UnityEditor.Undo.RevertAllInCurrentGroup();
                }

                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"[Transactional Revert] {ex.Message}",
                    skill: name,
                    details: new { exceptionType = ex.GetType().Name },
                    retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                    extra: ResolutionNotesExtra(MergeResolutionNotes(resolutionNotes)));
            }
            finally
            {
                // Whatever is left was recorded after the response was decided (entityId enrichment); never carry it over.
                GameObjectFinder.DrainResolutionNotes();
                EditorChangeTrackerService.EndRestExecution();
            }
        }

        public static string DryRun(string name, string json) => DryRun(name, json, WireV1);

        /// <summary>
        /// <c>?wire=v2</c> keeps every verdict field (valid / validation / impact / authorization / steps / changes) and slims the two echo
        /// blocks: <c>skill</c> becomes name, category, operation, mode, riskLevel, longRunning and a flags array; <c>parameters</c> lists only
        /// what the caller sent plus required parameters still missing. The v1 body is byte-for-byte unchanged.
        /// </summary>
        public static string DryRun(string name, string json, int wire) => DryRun(name, json, wire, issuePolicyToken: false);

        /// <summary>
        /// <paramref name="issuePolicyToken"/> is true only for POST /skill/{name}?mode=dryRun: a valid preview of a call the
        /// dryRun policy would gate then carries "dryRunToken" right after "valid". Every other preview keeps its exact bytes.
        /// </summary>
        internal static string DryRun(string name, string json, int wire, bool issuePolicyToken)
        {
            Initialize();
            if (!_skills.TryGetValue(name, out var skill))
                return ResolveSkillNotFound(name);

            try
            {
                GameObjectFinder.DrainResolutionNotes();
                var validation = ValidateParameters(skill, json);
                var planData = SkillPlanningService.BuildPlanData(skill, validation);
                // Inside a /skills/batch dry run, what this step would create becomes nameable by the steps after it.
                SkillPlanningService.RegisterPendingCreates(validation, planData);
                var resolutionNotes = MergeResolutionNotes(null);
                var dryRunToken = issuePolicyToken && validation.Valid && DryRunGateApplies(skill)
                    ? DryRunPolicyService.IssueToken(skill.Name, DryRunPolicyService.HashSkillArgs(skill, json))
                    : null;
                if (wire == WireV2)
                {
                    var flags = new List<string>();
                    if (skill.ReadOnly) flags.Add("readOnly");
                    if (skill.TracksWorkflow) flags.Add("tracksWorkflow");
                    if (skill.MutatesScene) flags.Add("mutatesScene");
                    if (skill.MutatesAssets) flags.Add("mutatesAssets");
                    if (skill.MayTriggerReload) flags.Add("mayTriggerReload");
                    if (skill.MayEnterPlayMode) flags.Add("mayEnterPlayMode");
                    if (skill.LongRunning) flags.Add("longRunning");
                    var compactParameters = validation.ParameterDetails
                        .Select(p => JObject.FromObject(p))
                        .Where(p => p.Value<bool?>("provided") == true || (p.Value<bool?>("required") == true && p.Value<bool?>("provided") != true))
                        .ToArray();
                    return SerializeWithResolutionNotes(new
                    {
                        status = "dryRun",
                        wire = "v2",
                        valid = validation.Valid,
                        skill = new
                        {
                            name = skill.Name,
                            category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                            operation = FormatOperation(skill.Operation),
                            mode = SkillsModeManager.SkillModeToWire(skill.Mode),
                            riskLevel = skill.RiskLevel,
                            longRunning = skill.LongRunning,
                            flags = flags.ToArray()
                        },
                        parameters = compactParameters,
                        validation = BuildValidationBlock(validation),
                        impact = new
                        {
                            readOnly = skill.ReadOnly,
                            tracksWorkflow = skill.TracksWorkflow,
                            operation = FormatOperation(skill.Operation),
                            mutatesScene = skill.MutatesScene,
                            mutatesAssets = skill.MutatesAssets,
                            mayTriggerReload = skill.MayTriggerReload,
                            mayEnterPlayMode = skill.MayEnterPlayMode,
                            riskLevel = skill.RiskLevel
                        },
                        authorization = BuildAuthorizationPreview(skill),
                        steps = planData?["steps"],
                        changes = planData?["changes"],
                        note = "No execution performed"
                    }, resolutionNotes, dryRunToken);
                }
                return SerializeWithResolutionNotes(new
                {
                    status = "dryRun",
                    valid = validation.Valid,
                    skill = new
                    {
                        name = skill.Name,
                        description = GetEffectiveDescription(skill),
                        category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                        operation = FormatOperation(skill.Operation),
                        tags = skill.Tags,
                        outputs = GetEffectiveOutputs(skill),
                        requiresInput = skill.RequiresInput,
                        readOnly = skill.ReadOnly,
                        tracksWorkflow = skill.TracksWorkflow,
                        mutatesScene = skill.MutatesScene,
                        mutatesAssets = skill.MutatesAssets,
                        mayTriggerReload = skill.MayTriggerReload,
                        mayEnterPlayMode = skill.MayEnterPlayMode,
                        supportsDryRun = skill.SupportsDryRun,
                        // Always output, regardless of value. This flag used to exist only in ?wire=v2's sparse "flags" array,
                        // so the default surface (the v1 payload and this preview) never mentioned that the call about to be made
                        // would block the main thread (and the whole HTTP queue) for seconds. The preview is exactly the place that should say this:
                        // outputting it only when true would make "absent" ambiguous between "fast" and "old version," so both values are emitted.
                        longRunning = skill.LongRunning,
                        riskLevel = skill.RiskLevel,
                        requiresPackages = skill.RequiresPackages,
                        mode = SkillsModeManager.SkillModeToWire(skill.Mode),
                        approvalBehavior = SkillsModeManager.ApprovalBehaviorForSkill(skill)
                    },
                    parameters = validation.ParameterDetails,
                    validation = BuildValidationBlock(validation),
                    impact = new
                    {
                        readOnly = skill.ReadOnly,
                        tracksWorkflow = skill.TracksWorkflow,
                        operation = FormatOperation(skill.Operation),
                        mutatesScene = skill.MutatesScene,
                        mutatesAssets = skill.MutatesAssets,
                        mayTriggerReload = skill.MayTriggerReload,
                        mayEnterPlayMode = skill.MayEnterPlayMode,
                        riskLevel = skill.RiskLevel
                    },
                    authorization = BuildAuthorizationPreview(skill),
                    steps = planData?["steps"],
                    changes = planData?["changes"],
                    note = "No execution performed"
                }, resolutionNotes, dryRunToken);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidJson,
                    $"Invalid JSON: {ex.Message}",
                    skill: name,
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }
            catch (Exception ex)
            {
                // Even valid JSON can still crash plan/semantic validation (e.g. an NRE). Reporting this case as INVALID_JSON
                // would send the agent into repeatedly rewriting a request body that was never the problem; so, following Execute's catch split,
                // the real failure is reported honestly.
                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"Dry-run failed: {ex.Message}",
                    skill: name,
                    details: new { exceptionType = ex.GetType().Name },
                    retryStrategy: SkillErrorResponse.Abort);
            }
        }

        // ========== Correction report and resolution notes ==========

        internal const string ResolutionNotesKey = "resolutionNotes";

        public static string Plan(string name, string json)
        {
            Initialize();
            if (!_skills.TryGetValue(name, out var skill))
                return ResolveSkillNotFound(name);

            try
            {
                GameObjectFinder.DrainResolutionNotes();
                var validation = ValidateParameters(skill, json);
                var plan = SkillPlanningService.BuildPlan(skill, validation);
                SkillPlanningService.RegisterPendingCreates(validation, plan);
                var resolutionNotes = MergeResolutionNotes(null);

                // A plan made for a skill the profile hides is a plan that can never execute, and ?mode=plan used to be the one preview
                // that never said so -- an agent would plan out the whole sequence, hit SURFACE_EXCLUDED on the very first execute,
                // with nothing in the plan having hinted at it. This uses the same block, the same shape
                // as the dry-run branch (BuildAuthorizationPreview returns the SURFACE_EXCLUDED verdict here too), so the caller only ever has to read one contract.
                // Only appended when there's actually something to say: for every skill the profile directly allows, the plan bytes stay unchanged,
                // and the plan output is already the largest of the three preview payloads. The second branch covers the "carried-write" entry points,
                // whose rejection is decided by a payload no preview has access to -- planning for a batch_execute
                // a profile is going to reject is the same trap one level up.
                if (SkillsSurfaceProfile.IsExcluded(skill) ||
                    SkillsSurfaceProfile.CarriedWritePreviewGate(skill.Name) != null)
                    plan["authorization"] = BuildAuthorizationPreview(skill);

                if (resolutionNotes != null && resolutionNotes.Count > 0)
                    plan[ResolutionNotesKey] = resolutionNotes.ToArray();

                return JsonConvert.SerializeObject(plan, _jsonSettings);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidJson,
                    $"Invalid JSON: {ex.Message}",
                    skill: name,
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }
            catch (Exception ex)
            {
                // Even valid JSON can still crash plan/semantic validation (e.g. an NRE). Reporting this case as INVALID_JSON
                // would send the agent into repeatedly rewriting a request body that was never the problem; so, following Execute's catch split,
                // the real failure is reported honestly.
                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"Plan failed: {ex.Message}",
                    skill: name,
                    details: new { exceptionType = ex.GetType().Name },
                    retryStrategy: SkillErrorResponse.Abort);
            }
        }
    }
}

// Producer:Betsy
