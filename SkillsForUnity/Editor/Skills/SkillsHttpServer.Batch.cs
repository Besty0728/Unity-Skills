using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillsHttpServer
    {
        // ===== Cross-skill batch execution =====

        private const int MaxBatchSteps = 50;

        // SkillRouter.ResolveWireVersion's value for ?wire=v2 (v1 is 1).
        private const int WireV2 = 2;

        /// <summary>
        /// POST /skills/batch — executes multiple skills sequentially within a single main-thread job,
        /// saving one HTTP round trip and one main-thread wake-up per step.
        ///
        /// Request body: {"steps":[{"skill":"gameobject_create","args":{...}}, ...], "continueOnError":false}
        /// - Each step runs the full SkillRouter.Execute pipeline (permission gate, semantic validation, undo, audit),
        ///   identical to calling POST /skill/{name} individually; each step gets its own undo group, never merged.
        /// - Fails fast by default: the first failing step terminates the batch, remaining steps reported "skipped".
        ///   With continueOnError=true, a failing step is recorded and the batch continues. A grant-related response
        ///   (MODE_RESTRICTED / CONFIRMATION_REQUIRED) always interrupts regardless of continueOnError — these can't
        ///   be skipped; the full response (with the grant token) is returned so the caller can resume after granting.
        /// - Static $param slots: a request-body-level "params":{"name":value,...} object fills placeholder nodes in
        ///   a step's structured args. At any depth, an object whose only key is "$param" (e.g. {"$param":"height"}),
        ///   or shaped exactly {"$param":"name","default":X}, is replaced by params[name] when present, else its
        ///   "default", else that step fails SEMANTIC_INVALID (details.param names the missing slot).
        ///   $param is purely static and order-independent, resolved before $ref, and behaves the same under dryRun
        ///   and execute (a missing slot fails in dry-run too, exposing gaps before replay).
        ///   $param and $ref are mutually orthogonal: a step may use both, but a single node may only be one or the
        ///   other, never both ({"$param":..,"$ref":..} is SEMANTIC_INVALID). Any $ref left after substitution is
        ///   handled by the $ref stage below.
        /// - Cross-step references: at any depth within a step's structured args, an object whose only key is "$ref"
        ///   (e.g. {"$ref":"$0.instanceId"}) is substituted before that step executes. "$N" is the 0-based index of
        ///   an earlier, successful step; after the dot is a Newtonsoft SelectToken path into that step's unwrapped
        ///   result (bare "$0" = whole result, "$1.items[0].path" reaches into arrays).
        ///   An unresolvable reference (malformed / out-of-range / forward reference / referenced step didn't
        ///   succeed / path matches nothing) fails that step with SEMANTIC_INVALID, then falls through to the normal
        ///   fail-fast / continueOnError rules. References inside string-typed args are not resolved — only structured JSON args are scanned.
        /// - ?mode=dryRun validates every step but executes nothing, and never halts, so an agent can preview the
        ///   whole sequence in one call. $ref parameters carry no real value during a dry run: they're stripped from
        ///   the validation body and only get a structural check (index range, ordering, the referenced skill's
        ///   declared Outputs); such steps carry refsValidated and findings in validation.warnings. ?mode=plan isn't supported.
        /// - ?mode=transactional makes the whole batch all-or-nothing: an unknown skill, or a step whose skill
        ///   declares MayTriggerReload, is rejected up front with 400 (a reload clears the undo stack, breaking the
        ///   rollback promise), and continueOnError=true is rejected as self-contradictory. If any step fails —
        ///   including a grant interruption, whose token is still returned — every executed step is rolled back via
        ///   Undo.RevertAllDownToGroup and re-marked status:"rolled_back" (a MutatesAssets step gets
        ///   rollbackReliability:"partial": AssetDatabase disk writes aren't fully covered by the undo stack). The
        ///   response then reports status:"rolled_back" and rolledBack:true. transactional composes freely with $ref.
        /// - Both modes can equally be specified in the body ("mode":"dryRun"/"transactional", "dryRun":true). These
        ///   two keys are parsed independently, query string wins on conflict — see TryApplyBatchBodyMode — the
        ///   response echoes the mode that actually took effect ("mode":"dryRun"|"transactional"|"execute", plus the
        ///   legacy "dryRun" boolean), the only way for the caller to confirm which of the four spellings won.
        /// </summary>
        private static void HandleSkillsBatchRequest(RequestJob job)
        {
            if (RejectIfCompiling(job))
                return;

            var qs = SkillRouter.ParseQueryString(job.QueryString);
            if (RejectUnknownBatchQueryParams(job, qs))
                return;
            if (!TryResolveBatchRequestMode(job, qs, out bool dryRun, out bool transactional))
                return;
            var batchMode = dryRun ? SkillRouter.RequestMode.DryRun : SkillRouter.RequestMode.Execute;
            if (!TryResolveDiff(job, qs, "/skills/batch", batchMode, out bool captureDiff))
                return;

            if (!TryParseBody(job, out var body)) return;

            if (RejectUnknownBatchBodyKeys(job, body))
                return;
            if (!TryApplyBatchBodyMode(job, body, qs, ref dryRun, ref transactional))
                return;
            if (dryRun)
            {
                // The request body might only turn this into a preview at this point — the ?diff= parsed above
                // was based only on the mode in the query string, and a preview has nothing to compare against.
                captureDiff = false;
                // Nor is there anything to fence or roll back. This only happens when the two keys come from different
                // places (?mode=transactional plus body {"dryRun":true}), since one 'mode' value can't request both; if
                // transactional stayed on, ExecuteBatchCore would open an undo fence and roll back to it on the first
                // invalid step — touching the user's undo stack for a request that executed nothing.
                transactional = false;
            }

            if (!(body.TryGetValue("steps", StringComparison.OrdinalIgnoreCase, out var stepsToken) && stepsToken is JArray steps) || steps.Count == 0)
            {
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.MissingParam,
                    "'steps' must be a non-empty array of {skill, args} objects.",
                    details: new
                    {
                        example = new
                        {
                            steps = new object[] { new { skill = "gameobject_create", args = new { name = "Cube" } } },
                            continueOnError = false,
                        },
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            if (steps.Count > MaxBatchSteps)
            {
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.SemanticInvalid,
                    $"Too many steps: {steps.Count} (max {MaxBatchSteps}). Split into multiple /skills/batch calls.",
                    details: new { received = steps.Count, max = MaxBatchSteps },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            bool continueOnError = false;
            if (body.TryGetValue("continueOnError", StringComparison.OrdinalIgnoreCase, out var coeToken)
                && coeToken != null && coeToken.Type != JTokenType.Null
                && !TryReadBatchBool(coeToken, out continueOnError))
            {
                WriteBatchTypeMismatch(job, "continueOnError", coeToken,
                    "Use JSON true/false; the strings \"true\"/\"false\" are accepted too. Until 2.7 any other type was silently read as false, so a batch the caller believed would continue past failures actually stopped at the first one.");
                return;
            }

            // The request-body-level "params" fills $param slots in step args (static, independent of mode).
            JObject batchParams = null;
            if (body.TryGetValue("params", StringComparison.OrdinalIgnoreCase, out var paramsToken) && paramsToken is JObject paramsObj)
                batchParams = paramsObj;

            if (transactional && RejectTransactionalPrecheck(job, steps, continueOnError))
                return;

            // The dryRun policy: one token per batch, bound to the body as submitted ($ref values do not exist during a dryRun,
            // so per-step tokens could never match). Steps inside the batch are not gated individually.
            var gatedSteps = DryRunPolicyService.Current == DryRunPolicy.Off ? null : FindDryRunGatedSteps(steps);
            var batchHash = gatedSteps != null ? DryRunPolicyService.HashBatch(steps, batchParams, continueOnError) : null;
            if (gatedSteps != null && !dryRun &&
                RejectWithoutBatchDryRunToken(job, qs, steps, batchParams, continueOnError, gatedSteps, batchHash))
                return;

            // agentIdRefiner re-checks ClientProcessResolver's cache before every step's telemetry write (see
            // RefineAgentId) -- a whole batch shares one accepted connection, so the same idempotent recheck
            // that /skill/{name} does once is worth repeating per step here: an early step may run before the
            // background resolver has landed a result, while a later one in the same batch won't. remotePort/
            // agentIdIsExplicit are also forwarded so SkillTelemetryService can do its own flush-time
            // late-binding (the real fix -- see SkillTelemetryService.Record) regardless of how this recheck lands.
            var response = ExecuteBatchCore(steps, batchParams, continueOnError, dryRun, transactional, job.AgentId, captureDiff,
                agentIdRefiner: () => { RefineAgentId(job); return job.AgentId; },
                remotePort: job.RemotePort,
                agentIdIsExplicit: job.AgentIdIsExplicit,
                wire: SkillRouter.ResolveWireVersion(job.QueryString));
            if (gatedSteps != null && dryRun)
                response.Property("dryRun")?.AddAfterSelf(new JProperty(DryRunPolicyService.TokenQueryKey,
                    DryRunPolicyService.IssueToken(DryRunPolicyService.BatchScope, batchHash)));
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(response, _jsonSettings);
        }

        /// <summary>Indices of the steps the dryRun policy gates right now (unknown skill names never count); null when none.</summary>
        private static int[] FindDryRunGatedSteps(JArray steps)
        {
            List<int> gated = null;
            for (int i = 0; i < steps.Count; i++)
            {
                var name = GetBatchStepSkillName(steps[i]);
                if (!string.IsNullOrWhiteSpace(name) && SkillRouter.TryGetSkill(name, out var skill) && SkillRouter.DryRunGateApplies(skill))
                    (gated ??= new List<int>()).Add(i);
            }
            return gated?.ToArray();
        }

        /// <summary>
        /// An executing (or transactional) batch with gated steps: consumes a ?dryRunToken= issued for this exact body and returns
        /// false, or writes DRYRUN_REQUIRED carrying the batch's v2 dryRun envelope plus a fresh token and returns true -- nothing
        /// executed, no undo fence opened. Failed steps in the preview do not withhold the token: fail-fast / continueOnError then
        /// behave exactly as without the policy, and the caller has already seen which steps fail.
        /// </summary>
        private static bool RejectWithoutBatchDryRunToken(RequestJob job, Dictionary<string, string> qs, JArray steps,
            JObject batchParams, bool continueOnError, int[] gatedSteps, string batchHash)
        {
            qs.TryGetValue(DryRunPolicyService.TokenQueryKey, out var token);
            var check = DryRunPolicyService.TryConsume(token, DryRunPolicyService.BatchScope, batchHash, out var tokenAgeSec);
            RefineAgentId(job);
            string reason;
            using (SkillsAuditLog.BeginRequestContext(job.RemotePort, job.AgentId, job.AgentIdIsExplicit))
            {
                if (check == OneTimeTokenStore.Check.Ok)
                {
                    SkillsAuditLog.Append("dryrun_passed", new { skill = "skills_batch", scope = DryRunPolicyService.BatchScope, gatedSteps, tokenAgeSec });
                    return false;
                }

                reason = DryRunPolicyService.ReasonFor(check, token);
                SkillsAuditLog.Append("call", new
                {
                    skill = "skills_batch",
                    result = "dryRunRequired",
                    policy = DryRunPolicyService.CurrentWire,
                    reason,
                    tokenIssued = true,
                    gatedSteps,
                });
            }

            var preview = ExecuteBatchCore(steps, batchParams, continueOnError, dryRun: true, transactional: false, agentId: job.AgentId,
                agentIdRefiner: () => { RefineAgentId(job); return job.AgentId; },
                remotePort: job.RemotePort,
                agentIdIsExplicit: job.AgentIdIsExplicit,
                wire: WireV2);
            var freshToken = DryRunPolicyService.IssueToken(DryRunPolicyService.BatchScope, batchHash);
            job.StatusCode = 200;
            job.ResponseJson = DryRunPolicyService.BuildBatchRequiredResponse(reason, freshToken, preview,
                QueryWithout(job.QueryString, DryRunPolicyService.TokenQueryKey), gatedSteps);
            return true;
        }

        /// <summary>
        /// Runs the refiner and returns its value only when it actually produced one, otherwise the
        /// caller's fallback. A plain "??" is not enough here: an empty refinement would satisfy it and
        /// print a blank agent name.
        /// </summary>
        private static string RefinedOr(Func<string> refiner, string fallback)
        {
            if (refiner == null) return fallback;
            var refined = refiner();
            return string.IsNullOrEmpty(refined) ? fallback : refined;
        }

        /// <summary>
        /// The sequential execution core behind POST /skills/batch: $param substitution, cross-step $ref resolution,
        /// then step-by-step runs of the full single-skill pipeline (SkillRouter.Execute — permission gate, undo,
        /// audit), with fail-fast / continueOnError / grant-interruption semantics and optional transactional rollback.
        /// The caller must pass an already-validated, non-empty steps array (transactional mode must also have run
        /// RejectTransactionalPrecheck). Returns the response body as a JObject ({status, executed, failed, results, ...}).
        ///
        /// agentIdRefiner is an optional per-step re-check (see SkillsHttpServer's call site / RefineAgentId): when
        /// provided, its return value is used instead of the fixed agentId for each step's telemetry. Left null by
        /// every existing test call site, which keeps their agentId argument exactly as passed. remotePort/
        /// agentIdIsExplicit are forwarded to every RecordBatchStep call, so SkillTelemetryService.Record can do
        /// its own flush-time late-binding for each step -- the mechanism that actually fixes attribution for
        /// single-digit-ms skills, independent of whether agentIdRefiner's early recheck already landed.
        ///
        /// wire is the resolved ?wire= version (1 or 2). A dry run passes it to every step's SkillRouter.DryRun, so
        /// ?wire=v2 yields the slim v2 step payloads and the envelope carries "wire":"v2" like every other v2 response.
        /// Execution accepts and ignores it: step results are the skills' own return shapes, so nothing is marked v2.
        /// </summary>
        internal static JObject ExecuteBatchCore(JArray steps, JObject batchParams, bool continueOnError,
            bool dryRun, bool transactional, string agentId, bool captureDiff = false, Func<string> agentIdRefiner = null,
            int remotePort = -1, bool agentIdIsExplicit = false, int wire = 1)
        {
            int txStartGroup = -1;
            if (transactional)
            {
                // Plants a fence on the undo timeline for the whole batch. Each step still opens (and collapses)
                // its own undo group inside Execute; on failure, everything above this fence is rolled back at once.
                Undo.IncrementCurrentGroup();
                txStartGroup = Undo.GetCurrentGroup();
            }

            var results = new List<JObject>(steps.Count);
            // Each successful step's already-unwrapped result, for later steps to reference via $ref.
            var stepResults = new JToken[steps.Count];
            int executedCount = 0;
            int failedCount = 0;
            bool halted = false;
            var batchDiff = captureDiff && !dryRun ? SkillSceneDiff.CreateBatchCapture() : null;

            // One scope for the whole batch (not per-step): remotePort/agentIdIsExplicit don't change between
            // steps -- they describe the single accepted connection this batch arrived on. Every SkillRouter.Execute
            // call inside the loop below (and the "call" audit entry its permission gate writes) picks this up.
            // A dry run also opens one pending-objects scope, so a step can name an object that an earlier step of the
            // same preview would create (nothing exists yet, since a dry run creates nothing).
            using (SkillsAuditLog.BeginRequestContext(remotePort, agentId, agentIdIsExplicit))
            using (dryRun ? SkillPlanningService.BeginPendingObjectsScope() : null)
            for (int i = 0; i < steps.Count; i++)
            {
                string stepSkillName = GetBatchStepSkillName(steps[i]);

                if (halted)
                {
                    results.Add(new JObject { ["index"] = i, ["skill"] = stepSkillName, ["status"] = "skipped" });
                    continue;
                }

                var stepSw = System.Diagnostics.Stopwatch.StartNew();
                // Re-checked per step (see ExecuteBatchCore's doc comment): a pure, idempotent cache lookup when
                // agentIdRefiner is supplied, otherwise just the fixed agentId every existing test call site passes.
                // Falls back on an empty refinement as well as a null one, so a blank never reaches the log line.
                string effectiveAgentId = RefinedOr(agentIdRefiner, agentId);

                if (!(steps[i] is JObject step) || string.IsNullOrWhiteSpace(stepSkillName))
                {
                    failedCount++;
                    results.Add(new JObject
                    {
                        ["index"] = i,
                        ["skill"] = stepSkillName,
                        ["status"] = "error",
                        ["error"] = BuildErrorPayload(SkillErrorResponse.Build(
                            SkillErrorCode.MissingParam,
                            $"steps[{i}] must be an object with a non-empty 'skill' field.",
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry)),
                    });
                    if (!continueOnError && !dryRun) halted = true;
                    RecordBatchStep(stepSkillName, effectiveAgentId, dryRun, false, "MISSING_PARAM", stepSw.ElapsedMilliseconds, remotePort, agentIdIsExplicit);
                    continue;
                }

                string argsJson = "{}";
                JToken argsToken = null;
                if (step.TryGetValue("args", StringComparison.OrdinalIgnoreCase, out var rawArgs) &&
                    rawArgs != null && rawArgs.Type != JTokenType.Null)
                {
                    argsToken = rawArgs;
                    argsJson = rawArgs.Type == JTokenType.String
                        ? rawArgs.ToString()
                        : rawArgs.ToString(Formatting.None);
                }

                // ---- Static $param substitution (resolved before $ref) ----
                // Purely static substitution, drawn from the request-body-level "params" object, so the result is identical
                // between dryRun and execute (the real value is present in both). Any $ref left after substitution is handled by the $ref stage below.
                if (argsToken is JContainer)
                {
                    var paramNodes = FindBatchParamNodes(argsToken, out var paramRefConflict);
                    if (paramRefConflict != null || paramNodes.Count > 0)
                    {
                        string paramErrorJson = null;
                        if (paramRefConflict != null)
                        {
                            paramErrorJson = SkillErrorResponse.Build(
                                SkillErrorCode.SemanticInvalid,
                                $"steps[{i}]: an args node may be $param or $ref, not both — {paramRefConflict.ToString(Formatting.None)}",
                                skill: stepSkillName,
                                details: new { node = paramRefConflict.ToString(Formatting.None), reason = "a single node cannot mix $param and $ref" },
                                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                        }
                        else
                        {
                            // Substitute on a deep copy; the original request body is never mutated.
                            var paramClone = argsToken.DeepClone();
                            foreach (var paramNode in FindBatchParamNodes(paramClone, out _))
                            {
                                if (!TryResolveBatchParam(paramNode, batchParams, out var value, out var reason))
                                {
                                    paramErrorJson = SkillErrorResponse.Build(
                                        SkillErrorCode.SemanticInvalid,
                                        $"steps[{i}]: cannot resolve $param '{paramNode.ParamName ?? "(non-string)"}' — {reason}",
                                        skill: stepSkillName,
                                        details: new { param = paramNode.ParamName, reason },
                                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                                    break;
                                }
                                var replacement = (value ?? JValue.CreateNull()).DeepClone();
                                if (ReferenceEquals(paramNode.Node, paramClone)) paramClone = replacement;
                                else paramNode.Node.Replace(replacement);
                            }
                            if (paramErrorJson == null)
                            {
                                // Feed the substituted args into the $ref stage below.
                                argsToken = paramClone;
                                argsJson = paramClone.ToString(Formatting.None);
                            }
                        }

                        if (paramErrorJson != null)
                        {
                            failedCount++;
                            results.Add(new JObject
                            {
                                ["index"] = i,
                                ["skill"] = stepSkillName,
                                ["status"] = "error",
                                ["error"] = BuildErrorPayload(paramErrorJson),
                            });
                            if (!continueOnError && !dryRun) halted = true;
                            RecordBatchStep(stepSkillName, effectiveAgentId, dryRun, false, "SEMANTIC_INVALID", stepSw.ElapsedMilliseconds, remotePort, agentIdIsExplicit);
                            continue;
                        }
                    }
                }

                // ---- Cross-step $ref references ----
                List<BatchRefNode> refNodes = null;          // for dryRun bookkeeping
                HashSet<string> strippedRefParams = null;    // dryRun: params already stripped from the validation body
                bool wholeArgsFromRef = false;               // dryRun: the args root node is itself a $ref
                List<string> refWarnings = null;             // dryRun: structural-check findings
                if (argsToken is JContainer)
                {
                    if (dryRun)
                    {
                        refNodes = FindBatchRefNodes(argsToken);
                        if (refNodes.Count > 0)
                        {
                            refWarnings = new List<string>();
                            foreach (var refNode in refNodes)
                                ValidateBatchRefStructural(refNode.RefString, i, steps, refWarnings);

                            // References carry no real value during a dry run. A parameter holding a $ref is
                            // removed from the validation body — leaving the placeholder object in would just
                            // produce TYPE_MISMATCH noise. The resulting MISSING_PARAM and semantic gaps are
                            // corrected uniformly after DryRun returns (see AdjustDryRunPayloadForRefs).
                            strippedRefParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var refNode in refNodes)
                            {
                                if (refNode.TopLevelParam == null) wholeArgsFromRef = true;
                                else strippedRefParams.Add(refNode.TopLevelParam);
                            }
                            if (wholeArgsFromRef || !(argsToken is JObject argsObj))
                            {
                                argsJson = "{}";
                            }
                            else
                            {
                                var strippedArgs = (JObject)argsObj.DeepClone();
                                foreach (var refNode in refNodes)
                                {
                                    if (refNode.TopLevelParam != null)
                                        strippedArgs.Remove(refNode.TopLevelParam);
                                }
                                argsJson = strippedArgs.ToString(Formatting.None);
                            }
                        }
                    }
                    else
                    {
                        // Resolve on a deep copy using earlier steps' results; the original request body is never mutated.
                        var argsClone = argsToken.DeepClone();
                        var cloneRefs = FindBatchRefNodes(argsClone);
                        if (cloneRefs.Count > 0)
                        {
                            string refErrorJson = null;
                            foreach (var refNode in cloneRefs)
                            {
                                if (!TryResolveBatchRef(refNode.RefString, stepResults, i, steps.Count,
                                        out var resolved, out var reason, out var referencedStep))
                                {
                                    refErrorJson = SkillErrorResponse.Build(
                                        SkillErrorCode.SemanticInvalid,
                                        $"steps[{i}]: cannot resolve $ref '{refNode.RefString ?? "(non-string)"}' — {reason}",
                                        skill: stepSkillName,
                                        details: new { @ref = refNode.RefString, referencedStep, reason },
                                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                                    break;
                                }
                                var replacement = resolved.DeepClone();
                                if (ReferenceEquals(refNode.Node, argsClone)) argsClone = replacement;
                                else refNode.Node.Replace(replacement);
                            }
                            if (refErrorJson != null)
                            {
                                failedCount++;
                                results.Add(new JObject
                                {
                                    ["index"] = i,
                                    ["skill"] = stepSkillName,
                                    ["status"] = "error",
                                    ["error"] = BuildErrorPayload(refErrorJson),
                                });
                                if (!continueOnError) halted = true;
                                RecordBatchStep(stepSkillName, effectiveAgentId, dryRun, false, "SEMANTIC_INVALID", stepSw.ElapsedMilliseconds, remotePort, agentIdIsExplicit);
                                continue;
                            }
                            argsJson = argsClone.ToString(Formatting.None);
                        }
                    }
                }

                string stepJson;
                try
                {
                    if (dryRun)
                    {
                        stepJson = SkillRouter.DryRun(stepSkillName, argsJson, wire);
                    }
                    else
                    {
                        if (batchDiff != null && SkillRouter.TryGetSkill(stepSkillName, out var diffSkill) && !diffSkill.ReadOnly)
                        {
                            try { SkillSceneDiff.CaptureBatchStepBefore(batchDiff, JObject.Parse(argsJson)); }
                            catch { batchDiff.HadWritableSteps = true; }
                        }
                        stepJson = SkillRouter.Execute(stepSkillName, argsJson);
                        // Every step shares the same POST job, so the per-request cache invalidation in ProcessJobQueue doesn't run
                        // between steps — without this line, a step couldn't find an object created earlier in the same batch.
                        // A ReadOnly step is side-effect-free by contract and can't stale the cache, so it's skipped; every other
                        // step — including one whose name doesn't resolve to a known skill — still triggers invalidation.
                        if (!SkillRouter.TryGetSkill(stepSkillName, out var stepSkill) || !stepSkill.ReadOnly)
                            GameObjectFinder.InvalidateCache();
                        // Re-invoked right after Execute rather than reusing effectiveAgentId (computed before the
                        // step ran): same rationale as the single-skill console line -- this is a direct,
                        // un-late-bound Debug.Log, so it needs the resolver's best value at print time.
                        var logAgentValue = agentIdRefiner != null
                            ? RefinedOr(agentIdRefiner, effectiveAgentId)
                            : effectiveAgentId;
                        SkillsLogger.LogAgent(logAgentValue, $"{stepSkillName} (batch {i + 1}/{steps.Count})");
                    }
                }
                catch (Exception ex)
                {
                    stepJson = SkillErrorResponse.Build(
                        SkillErrorCode.Internal,
                        ex.Message,
                        skill: stepSkillName,
                        details: new { type = ex.GetType().Name },
                        retryStrategy: SkillErrorResponse.RetryWaitAndRetry);
                    SkillsLogger.LogWarning($"Batch step {i} '{stepSkillName}' error: {ex.Message}");
                }

                JObject stepPayload;
                try { stepPayload = JObject.Parse(stepJson); }
                catch { stepPayload = new JObject { ["status"] = "error", ["error"] = stepJson }; }

                string stepStatus = stepPayload["status"]?.ToString();

                if (dryRun)
                {
                    // $ref parameters have already been stripped from the validation body — the payload must be corrected before
                    // reading its 'valid' verdict (filter missingParams, downgrade semantic errors, attach refsValidated).
                    if (refNodes != null && refNodes.Count > 0)
                        AdjustDryRunPayloadForRefs(stepPayload, refNodes, strippedRefParams, wholeArgsFromRef, refWarnings);

                    // A DryRun response carries status:"dryRun" and valid:bool; an unknown skill returns status:"error".
                    // A validation failure never halts a dry-run batch.
                    bool stepValid = string.Equals(stepStatus, "dryRun", StringComparison.OrdinalIgnoreCase) &&
                        stepPayload["valid"]?.Type == JTokenType.Boolean && stepPayload["valid"].ToObject<bool>();
                    if (stepValid)
                    {
                        executedCount++;
                        results.Add(new JObject { ["index"] = i, ["skill"] = stepSkillName, ["status"] = "success", ["result"] = stepPayload });
                    }
                    else
                    {
                        failedCount++;
                        results.Add(new JObject { ["index"] = i, ["skill"] = stepSkillName, ["status"] = "error", ["error"] = stepPayload });
                    }
                    RecordBatchStep(stepSkillName, effectiveAgentId, dryRun, stepValid, stepValid ? null : "DRYRUN_INVALID", stepSw.ElapsedMilliseconds, remotePort, agentIdIsExplicit);
                    continue;
                }

                if (string.Equals(stepStatus, "error", StringComparison.OrdinalIgnoreCase))
                {
                    failedCount++;
                    results.Add(new JObject { ["index"] = i, ["skill"] = stepSkillName, ["status"] = "error", ["error"] = stepPayload });

                    // A grant-related response must never be skipped: the caller has to complete the grant/confirmation flow,
                    // so the batch stops here even if continueOnError=true. The full payload above carries the grant token.
                    string errorCode = stepPayload["errorCode"]?.ToString();
                    bool authorizationRequired =
                        string.Equals(errorCode, "MODE_RESTRICTED", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(errorCode, "CONFIRMATION_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(errorCode, "DRYRUN_REQUIRED", StringComparison.OrdinalIgnoreCase);

                    if (authorizationRequired || !continueOnError)
                        halted = true;
                    RecordBatchStep(stepSkillName, effectiveAgentId, dryRun, false, errorCode, stepSw.ElapsedMilliseconds, remotePort, agentIdIsExplicit);
                    continue;
                }

                // status:"success" (or any non-error shape) — unwrap the inner result;
                // the entry-level status field has already expressed success.
                executedCount++;
                var unwrappedResult = stepPayload.TryGetValue("result", out var innerResult) ? innerResult : stepPayload;
                stepResults[i] = unwrappedResult;
                if (batchDiff != null)
                    SkillSceneDiff.TrackBatchStepResult(batchDiff, unwrappedResult);
                results.Add(new JObject
                {
                    ["index"] = i,
                    ["skill"] = stepSkillName,
                    ["status"] = "success",
                    ["result"] = unwrappedResult,
                });
                RecordBatchStep(stepSkillName, effectiveAgentId, dryRun, true, null, stepSw.ElapsedMilliseconds, remotePort, agentIdIsExplicit);
            }

            bool rolledBack = false;
            if (transactional && failedCount > 0)
            {
                // All-or-nothing: any failure (including a grant interruption — the failed step's entry still carries the
                // grant token as-is) rolls back every step executed since the batch fence, leaving no redo entries.
                Undo.RevertAllDownToGroup(txStartGroup);
                GameObjectFinder.InvalidateCache();
                rolledBack = true;

                int revertedSteps = 0;
                foreach (var entry in results)
                {
                    if (!string.Equals(entry["status"]?.ToString(), "success", StringComparison.Ordinal))
                        continue;
                    entry["status"] = "rolled_back";
                    revertedSteps++;
                    // AssetDatabase's disk writes aren't fully covered by the undo stack —
                    // mark this kind of rollback as partial rather than over-promising.
                    string entrySkill = entry["skill"]?.ToString();
                    if (!string.IsNullOrEmpty(entrySkill) &&
                        SkillRouter.TryGetSkill(entrySkill, out var entryInfo) && entryInfo.MutatesAssets)
                    {
                        entry["rollbackReliability"] = "partial";
                    }
                }
                SkillsLogger.Log($"Transactional batch rolled back {revertedSteps} executed step(s) after a failed step (undo group {txStartGroup}).");
            }

            var response = new JObject
            {
                ["status"] = failedCount == 0 ? "completed" : (transactional ? "rolled_back" : "partial"),
                // Echoes back the mode that actually took effect, not whichever single key requested it. ?mode= and
                // ?dryRun=/body "dryRun" are each parsed independently, either can come from the URL or payload (see
                // TryApplyBatchBodyMode), so "which of my four spellings won" can't be derived from the request alone —
                // a caller who thinks it sent a preview must be able to see that it actually got one.
                ["mode"] = dryRun ? "dryRun" : (transactional ? "transactional" : "execute"),
                ["dryRun"] = dryRun,
            };
            if (dryRun && wire == WireV2)
                response["wire"] = "v2";
            if (transactional)
            {
                response["transactional"] = true;
                response["rolledBack"] = rolledBack;
            }
            response["executed"] = executedCount;
            response["failed"] = failedCount;
            response["results"] = new JArray(results);
            if (batchDiff != null)
                response["sceneDiff"] = SkillSceneDiff.BuildBatch(batchDiff);
            return response;
        }

        /// <summary>A $param name aggregated across the whole step sequence (for macro-library introspection).</summary>
        internal sealed class BatchParamDeclaration
        {
            public string Name;
            public bool HasDefault;      // every node referencing this name carries an inline default
            public JToken DefaultValue;  // the first inline default seen (for display only)
        }

        /// <summary>
        /// Aggregates the $param slots declared across the whole step sequence, keyed by name, in first-seen order.
        /// A name only counts as having a default if every node that references it carries an inline "default" —
        /// even a single bare {"$param":"x"} slot makes that value required.
        /// A malformed slot ($param name isn't a string) is skipped here, and reported per-step at execution time.
        /// Consistent with the execution stage, doesn't scan string-typed args.
        /// </summary>
        internal static List<BatchParamDeclaration> CollectBatchParamDeclarations(JArray steps)
        {
            var byName = new Dictionary<string, BatchParamDeclaration>(StringComparer.Ordinal);
            var ordered = new List<BatchParamDeclaration>();
            if (steps == null)
                return ordered;

            foreach (var step in steps)
            {
                if (!(step is JObject stepObj)
                    || !stepObj.TryGetValue("args", StringComparison.OrdinalIgnoreCase, out var args)
                    || !(args is JContainer))
                    continue;

                foreach (var node in FindBatchParamNodes(args, out _))
                {
                    if (node.ParamName == null)
                        continue;
                    if (!byName.TryGetValue(node.ParamName, out var decl))
                    {
                        decl = new BatchParamDeclaration
                        {
                            Name = node.ParamName,
                            HasDefault = node.HasDefault,
                            DefaultValue = node.DefaultValue,
                        };
                        byName[node.ParamName] = decl;
                        ordered.Add(decl);
                    }
                    else if (!node.HasDefault)
                    {
                        decl.HasDefault = false;
                    }
                    else if (decl.DefaultValue == null)
                    {
                        decl.DefaultValue = node.DefaultValue;
                    }
                }
            }
            return ordered;
        }

        private static string GetBatchStepSkillName(JToken stepToken)
        {
            if (stepToken is JObject step &&
                step.TryGetValue("skill", StringComparison.OrdinalIgnoreCase, out var skillToken) &&
                skillToken != null && skillToken.Type != JTokenType.Null)
            {
                return skillToken.ToString();
            }
            return null;
        }

        /// <summary>
        /// Parses ?mode= / ?dryRun= for /skills/batch. Batch accepts dryRun/transactional
        /// (a single-skill request accepts dryRun/plan — see TryResolveRequestMode, which keeps rejecting
        /// 'transactional' so its INVALID_MODE validValues stays accurate).
        /// Any unrecognized value returns false (and writes an error response).
        /// </summary>
        private static bool TryResolveBatchRequestMode(RequestJob job, Dictionary<string, string> qs, out bool dryRun, out bool transactional)
        {
            dryRun = false;
            transactional = false;

            if (qs.TryGetValue("mode", out var modeValue) && !string.IsNullOrWhiteSpace(modeValue))
            {
                if (modeValue.Equals("dryRun", StringComparison.OrdinalIgnoreCase))
                {
                    dryRun = true;
                    return true;
                }
                if (modeValue.Equals("transactional", StringComparison.OrdinalIgnoreCase))
                {
                    transactional = true;
                    return true;
                }

                bool isPlan = modeValue.Equals("plan", StringComparison.OrdinalIgnoreCase);
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.InvalidMode,
                    isPlan
                        ? "Batch supports '?mode=dryRun' (validates every step without executing) and '?mode=transactional' (all-or-nothing with rollback); 'plan' is not available for /skills/batch."
                        : $"Unknown mode '{modeValue}' — request was NOT executed.",
                    skill: "skills_batch",
                    details: new
                    {
                        received = modeValue,
                        validValues = new[] { "dryRun", "transactional" },
                        hint = "Use '?mode=dryRun' to validate without executing, '?mode=transactional' for all-or-nothing execution with rollback, or omit '?mode=' entirely to execute fail-fast.",
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return false;
            }

            return TryReadDryRunFlag(job, qs, "skills_batch", out dryRun);
        }

        private static bool IsKnownBatchParam(string[] allowed, string name)
        {
            foreach (var candidate in allowed)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Rejects an unrecognized query parameter on /skills/batch (400 UNKNOWN_PARAM). Returns true if rejected.
        /// </summary>
        private static bool RejectUnknownBatchQueryParams(RequestJob job, Dictionary<string, string> qs)
        {
            var unknown = new List<object>();
            foreach (var key in qs.Keys)
            {
                if (IsKnownBatchParam(RequestLevelQueryKeys, key))
                    continue;

                var entry = new Dictionary<string, object> { ["parameter"] = key };
                var hint = BatchParamHint(key);
                if (hint != null)
                    entry["hint"] = hint;
                unknown.Add(entry);
            }

            if (unknown.Count == 0)
                return false;

            job.StatusCode = 400;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.UnknownParam,
                "Unknown query parameter(s) on POST /skills/batch — the batch was NOT executed.",
                skill: "skills_batch",
                details: new
                {
                    unknownParams = unknown,
                    allowedParams = RequestLevelQueryKeys,
                    location = "queryString",
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            return true;
        }

        /// <summary>
        /// Rejects an unrecognized top-level request-body key on /skills/batch (400 UNKNOWN_PARAM), consistent
        /// with what CollectUnknownParameters does for single-skill args.
        /// This runs before the 'steps' check, so a typo like "step" is reported as an "unknown key" itself,
        /// rather than as a missing 'steps'. Returns true if the request was rejected.
        /// </summary>
        private static bool RejectUnknownBatchBodyKeys(RequestJob job, JObject body)
        {
            var unknown = new List<object>();
            foreach (var property in body.Properties())
            {
                if (IsKnownBatchParam(BatchBodyParams, property.Name))
                    continue;

                var entry = new Dictionary<string, object> { ["parameter"] = property.Name };
                var hint = BatchParamHint(property.Name);
                if (hint != null)
                    entry["hint"] = hint;
                unknown.Add(entry);
            }

            if (unknown.Count == 0)
                return false;

            job.StatusCode = 400;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.UnknownParam,
                "Unknown top-level field(s) in the /skills/batch body — the batch was NOT executed.",
                skill: "skills_batch",
                details: new
                {
                    unknownParams = unknown,
                    allowedParams = BatchBodyParams,
                    location = "body",
                    hint = "Per-step fields ('skill', 'args') live inside each element of 'steps', not at the top level.",
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            return true;
        }

        /// <summary>
        /// Gives targeted hints for the handful of /skills/batch keys agents actually get wrong: the singular "step",
        /// promoting a step's own fields to the top level, treating transactional as a boolean, and runAsync — this
        /// endpoint never had that parameter, since it runs every step in the same main-thread job.
        /// Returns null when there's nothing specific to say.
        /// </summary>
        private static string BatchParamHint(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "step":
                    return "Did you mean 'steps'? It takes an array of {skill, args} objects.";
                case "skill":
                case "args":
                    return "'skill' and 'args' belong to an element of 'steps', not to the top level: {\"steps\":[{\"skill\":\"...\",\"args\":{...}}]}.";
                case "transactional":
                    return "All-or-nothing execution is a mode, not a flag: use '?mode=transactional' (or body \"mode\":\"transactional\").";
                case "runasync":
                case "async":
                    return "POST /skills/batch is always synchronous — it runs every step in one main-thread job and returns all results. For a long-running background batch use the batch_execute skill's 'runAsync' parameter (POST /skill/batch_execute) and poll job_status / GET /jobs/{id}.";
                case "continueonfailure":
                case "ignoreerrors":
                    return "Did you mean 'continueOnError'?";
                case "diff":
                    return "'diff' is a query parameter, not a body field: POST /skills/batch?diff=1.";
                case "wire":
                    return "'wire' is a query parameter, not a body field: POST /skills/batch?mode=dryRun&wire=v2 returns each step's dryRun in the v2 shape.";
                case "expectinstance":
                case "expectproject":
                    return "The expected instance is a query parameter (?expectInstance=<instanceId> / ?expectProject=<name>) or an X-Expect-Instance / X-Expect-Project header, not a body field.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Reads a boolean that the client may have quoted. JSON true/false is accepted as-is, and the strings
        /// "true"/"false" are also parsed; anything else fails, so the caller can report TYPE_MISMATCH instead of
        /// silently falling back to a default.
        /// </summary>
        private static bool TryReadBatchBool(JToken token, out bool value)
        {
            value = false;
            if (token == null)
                return false;
            if (token.Type == JTokenType.Boolean)
            {
                value = token.ToObject<bool>();
                return true;
            }
            if (token.Type == JTokenType.String)
                return bool.TryParse(token.ToString().Trim(), out value);
            return false;
        }

        private static void WriteBatchTypeMismatch(RequestJob job, string parameter, JToken token, string hint)
        {
            string receivedType = token.Type.ToString().ToLowerInvariant();
            job.StatusCode = 400;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.TypeMismatch,
                $"'{parameter}' must be a boolean — received {receivedType}. The batch was NOT executed.",
                skill: "skills_batch",
                details: new
                {
                    parameter,
                    expectedType = "boolean",
                    receivedType,
                    received = token is JContainer ? token.ToString(Formatting.None) : token.ToString(),
                    hint,
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
        }

        /// <summary>
        /// Applies the request-body-level "mode"/"dryRun" on top of what the query string already resolved.
        ///
        /// <para>"mode" and "dryRun" are two independent keys, each parsed independently, query string wins on
        /// conflict. In the past one "has the query already decided" flag gated both, so a URL's
        /// <c>?mode=transactional</c> would silently drop a body <c>{"dryRun":true}</c>, actually executing a batch
        /// the caller meant to preview — the worst possible failure for a preview, invisible in the response.</para>
        ///
        /// <para>Priority order: for the same key, the URL wins over the payload; within the same slot, "mode"
        /// wins over "dryRun" (on the query-string side, this is what TryResolveBatchRequestMode's early return
        /// already implements). Across the two keys, dryRun is monotonic — any surviving explicit <c>dryRun:true</c>
        /// makes the request a preview, and <c>dryRun:false</c> never cancels <c>mode:"dryRun"</c>, because "don't
        /// decide preview from this key" and "execute for real" aren't the same statement.
        /// Biasing toward preview is the only direction where the worst case is just a wasted call.</para>
        ///
        /// <para>Even a value that loses the priority contest is still validated, so a typo is never swallowed.
        /// Returns false (and writes a 400) on an invalid value or type.</para>
        /// </summary>
        private static bool TryApplyBatchBodyMode(RequestJob job, JObject body, Dictionary<string, string> qs,
            ref bool dryRun, ref bool transactional)
        {
            bool queryOwnsMode = HasQueryValue(qs, "mode");
            bool queryOwnsDryRun = HasQueryValue(qs, "dryRun");
            bool bodyModeApplied = false;

            if (body.TryGetValue("mode", StringComparison.OrdinalIgnoreCase, out var modeToken)
                && modeToken != null && modeToken.Type != JTokenType.Null)
            {
                if (modeToken.Type != JTokenType.String)
                {
                    string receivedType = modeToken.Type.ToString().ToLowerInvariant();
                    job.StatusCode = 400;
                    job.ResponseJson = SkillErrorResponse.Build(
                        SkillErrorCode.TypeMismatch,
                        $"Body 'mode' must be a string — received {receivedType}. The batch was NOT executed.",
                        skill: "skills_batch",
                        details: new
                        {
                            parameter = "mode",
                            expectedType = "string",
                            receivedType,
                            validValues = new[] { "dryRun", "transactional" },
                        },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    return false;
                }

                string modeValue = modeToken.ToString().Trim();
                bool bodyDryRunMode = modeValue.Equals("dryRun", StringComparison.OrdinalIgnoreCase);
                bool bodyTransactional = modeValue.Equals("transactional", StringComparison.OrdinalIgnoreCase);
                if (!bodyDryRunMode && !bodyTransactional)
                {
                    bool isPlan = modeValue.Equals("plan", StringComparison.OrdinalIgnoreCase);
                    job.StatusCode = 400;
                    job.ResponseJson = SkillErrorResponse.Build(
                        SkillErrorCode.InvalidMode,
                        isPlan
                            ? "Batch supports mode 'dryRun' (validates every step without executing) and 'transactional' (all-or-nothing with rollback); 'plan' is not available for /skills/batch."
                            : $"Unknown mode '{modeValue}' — the batch was NOT executed.",
                        skill: "skills_batch",
                        details: new
                        {
                            received = modeValue,
                            validValues = new[] { "dryRun", "transactional" },
                            location = "body",
                            hint = "Set body \"mode\":\"dryRun\" to validate without executing, \"transactional\" for all-or-nothing execution with rollback, or omit it to execute fail-fast.",
                        },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    return false;
                }

                // This key only belongs to the request body when the URL didn't write 'mode'.
                if (!queryOwnsMode)
                {
                    bodyModeApplied = true;
                    transactional = bodyTransactional;
                    dryRun = dryRun || bodyDryRunMode;
                }
            }

            if (body.TryGetValue("dryRun", StringComparison.OrdinalIgnoreCase, out var dryRunToken)
                && dryRunToken != null && dryRunToken.Type != JTokenType.Null)
            {
                if (!TryReadBatchBool(dryRunToken, out bool bodyDryRun))
                {
                    WriteBatchTypeMismatch(job, "dryRun", dryRunToken,
                        "Use JSON true/false (or the strings \"true\"/\"false\") in the body, or '?dryRun=true' / '?mode=dryRun' in the query string.");
                    return false;
                }

                // Skip if a request-body-level 'mode' already spoke for this slot; that's priority within the
                // same slot, not a reason to ignore the URL's own dryRun key.
                if (!queryOwnsDryRun && !bodyModeApplied)
                    dryRun = dryRun || bodyDryRun;
            }

            return true;
        }

        /// <summary>
        /// Whether the query string carries a usable value for this key — the same "present and non-blank" test
        /// the ?mode= / ?dryRun= parsers use, so "?dryRun=" counts as "the caller didn't decide" in both places.
        /// </summary>
        private static bool HasQueryValue(Dictionary<string, string> qs, string key) =>
            qs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

        /// <summary>
        /// A transactional batch relies on the editor's undo stack to promise "all or nothing", so anything that
        /// would break that promise is rejected up front (400 SEMANTIC_INVALID) rather than failing mid-execution:
        /// unknown/malformed steps, a skill that might trigger a domain reload (wipes the undo stack), and
        /// continueOnError=true (a transaction is fail-fast by definition). Returns true if the batch was rejected.
        /// </summary>
        private static bool RejectTransactionalPrecheck(RequestJob job, JArray steps, bool continueOnError)
        {
            if (continueOnError)
            {
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.SemanticInvalid,
                    "'continueOnError=true' conflicts with '?mode=transactional': a transaction is all-or-nothing, so execution can never continue past a failed step. Remove one of the two.",
                    skill: "skills_batch",
                    details: new { mode = "transactional", continueOnError = true },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return true;
            }

            var violations = new List<(int step, string skill, string reason)>();
            for (int i = 0; i < steps.Count; i++)
            {
                string name = GetBatchStepSkillName(steps[i]);
                string reason = null;
                if (string.IsNullOrWhiteSpace(name))
                    reason = "step is not an object with a non-empty 'skill' field";
                else if (!SkillRouter.TryGetSkill(name, out var info))
                    reason = "unknown skill";
                else if (info.MayTriggerReload)
                    reason = "the skill declares MayTriggerReload — a domain reload wipes the editor undo stack, so the transactional rollback promise cannot be kept";

                if (reason != null)
                    violations.Add((i, name, reason));
            }

            if (violations.Count == 0)
                return false;

            var first = violations[0];
            job.StatusCode = 400;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.SemanticInvalid,
                $"Transactional batch rejected before execution: steps[{first.step}] ('{first.skill ?? "?"}') — {first.reason}." +
                (violations.Count > 1 ? $" {violations.Count - 1} more violation(s) listed in details." : string.Empty),
                skill: "skills_batch",
                details: new
                {
                    mode = "transactional",
                    violations = violations.Select(v => new { v.step, v.skill, v.reason }).ToArray(),
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            return true;
        }
    }
}

// Producer:Betsy
