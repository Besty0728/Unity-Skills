using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    internal static class RuntimeScenarioService
    {
        internal static Func<bool> PlayingForTests;
        internal static Action<bool> SetPlayingForTests;
        internal static Func<string, string, string> ExecuteForTests;
        internal static Func<int> FrameForTests;
        internal static Func<JObject> LogWindowForTests;
        internal static Func<long> LogSeqForTests;
        internal static Func<bool> BackgroundForTests;
        internal static Action<bool> SetBackgroundForTests;
        private static bool Playing => PlayingForTests?.Invoke() ?? EditorApplication.isPlaying;
        private static bool Background => BackgroundForTests?.Invoke() ?? Application.runInBackground;
        private static void SetPlaying(bool value) { if (SetPlayingForTests != null) SetPlayingForTests(value); else EditorApplication.isPlaying = value; }
        private static void SetBackground(bool value) { if (SetBackgroundForTests != null) SetBackgroundForTests(value); else Application.runInBackground = value; }
        private static int Frame => FrameForTests?.Invoke() ?? Time.frameCount;
        private static string Execute(string skill, string args) => ExecuteForTests?.Invoke(skill, args) ?? SkillRouter.Execute(skill, args);
        private static bool Flag(BatchJobRecord job, string key) => ComponentInvocationService.Bool(job, key);
        private static long Number(BatchJobRecord job, string key) => ComponentInvocationService.Long(job, key);
        private static void Save(BatchJobRecord job) => ComponentInvocationService.Persist(job);
        private static bool Terminal(BatchJobRecord job) => job == null || job.status == "completed" || job.status == "failed" || job.status == "cancelled";
        private static JObject Error(string message) => (JObject)ComponentInvocationService.Error(message);

        internal static object Preview(string steps, string executionMode, int timeoutSeconds)
        {
            try
            {
                var parsed = JArray.Parse(steps ?? "");
                ValidateSteps(parsed);
                if (executionMode != "play" && executionMode != "edit") return Error("executionMode must be play or edit.");
                if (timeoutSeconds < 1 || timeoutSeconds > 600) return Error("timeoutSeconds must be between 1 and 600.");
                if (executionMode == "edit" && Playing) return Error("An edit scenario requires Edit Mode.");
                return new { valid = true, stepCount = parsed.Count, executionMode, undoSupported = false, atomic = false,
                    note = "Each step is separately validated and authorized by SkillRouter at execution; future runtime targets are not resolved by preview." };
            }
            catch (Exception ex) { return Error(ex.Message); }
        }

        internal static object Start(string steps, string executionMode, int timeoutSeconds)
        {
            JArray parsed;
            try { parsed = JArray.Parse(steps ?? ""); ValidateSteps(parsed); }
            catch (Exception ex) { return Error(ex.Message); }
            if (executionMode != "play" && executionMode != "edit") return Error("executionMode must be play or edit.");
            if (timeoutSeconds < 1 || timeoutSeconds > 600) return Error("timeoutSeconds must be between 1 and 600.");
            if (executionMode == "edit" && Playing) return Error("An edit scenario requires Edit Mode.");
            if (ServerAvailabilityHelper.IsCompilationInProgress()) return Error("Wait for compilation and asset updating to finish.");
            if (BatchPersistence.ListJobs(int.MaxValue).Any(j => j.kind == "run_scenario" && !Terminal(j))) return Error("Another scenario is active; wait or cancel it before starting another.");
            bool startedPlay = executionMode == "play" && !Playing;
            var caller = SkillsAuditLog.CaptureRequestContext();
            var job = AsyncJobService.CreateJob("run_scenario", startedPlay ? "entering_play_mode" : "running", "Scenario accepted; arbitrary calls have no Undo or atomicity guarantee.", true,
                new Dictionary<string, object> {
                    ["steps"] = parsed.ToString(Formatting.None), ["executionMode"] = executionMode,
                    ["startedPlayMode"] = startedPlay, ["originalRunInBackground"] = Background,
                    ["deadlineTicks"] = DateTime.UtcNow.AddSeconds(timeoutSeconds).Ticks,
                    ["scenarioStage"] = startedPlay ? "entering_play_mode" : "running", ["stepIndex"] = 0,
                    ["startedUtcTicks"] = DateTime.UtcNow.Ticks, ["startLogSeq"] = LogSeqForTests?.Invoke() ?? ConsoleLogHistoryService.GetCurrentSeq(),
                    ["callerActive"] = caller.Active, ["callerRemotePort"] = caller.RemotePort,
                    ["callerAgentId"] = caller.FallbackAgentId, ["callerExplicit"] = caller.AgentIdIsExplicit
                }, new Dictionary<string, object> { ["steps"] = new JArray(), ["passed"] = false });
            Save(job);
            try
            {
                SetBackground(true);
                if (startedPlay) SetPlaying(true);
            }
            catch (Exception ex) { Finish(job, false, "Failed to enter scenario mode: " + ex.Message); }
            return new { success = true, status = "accepted", jobId = job.jobId, kind = job.kind, waitUrl = AsyncJobService.BuildWaitUrl(job.jobId) };
        }

        internal static void ValidateSteps(JArray steps)
        {
            if (steps.Count < 1 || steps.Count > 100) throw new ArgumentException("steps must contain between 1 and 100 steps.");
            if (!steps.OfType<JObject>().Any(step => step.Value<string>("kind") == "assert" || step.Value<string>("kind") == "wait_condition"))
                throw new ArgumentException("A scenario requires at least one assert or wait_condition verification step.");
            foreach (var token in steps)
            {
                if (!(token is JObject step)) throw new ArgumentException("Each step must be an object.");
                string kind = step.Value<string>("kind");
                if (!new[] { "invoke", "read", "assert", "wait_frames", "wait_seconds", "wait_condition" }.Contains(kind)) throw new ArgumentException("Unknown scenario step kind: " + kind);
                if (kind == "wait_frames" && ((step.Value<int?>("frames") ?? -1) < 0 || step.Value<int>("frames") > 100000)) throw new ArgumentException("frames must be between 0 and 100000.");
                if (kind == "wait_seconds" && ((step.Value<double?>("seconds") ?? -1) < 0 || step.Value<double>("seconds") > 600 || double.IsNaN(step.Value<double>("seconds")))) throw new ArgumentException("seconds must be between 0 and 600.");
                if (kind == "assert" || kind == "wait_condition")
                {
                    if (!step.ContainsKey("expected")) throw new ArgumentException("Assertions require expected (null is allowed).");
                    double tolerance = step.Value<double?>("tolerance") ?? 0;
                    if (tolerance < 0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance)) throw new ArgumentException("tolerance must be finite and nonnegative.");
                }
                if (kind == "invoke" || kind == "read" || kind == "wait_condition" || (kind == "assert" && step["skill"] != null))
                {
                    var skillName = step.Value<string>("skill");
                    if (string.IsNullOrWhiteSpace(skillName)) throw new ArgumentException("This step requires skill.");
                    if (!SkillRouter.TryGetSkill(skillName, out var info)) throw new ArgumentException("Unknown step skill: " + skillName);
                    if (kind == "invoke" && skillName != "component_invoke_method")
                        throw new ArgumentException("invoke steps currently require component_invoke_method; lifecycle-changing skills are not scenario steps.");
                    if (kind != "invoke" && !info.ReadOnly) throw new ArgumentException("Read, assert and wait_condition require a ReadOnly skill.");
                    if (step["args"] != null && !(step["args"] is JObject)) throw new ArgumentException("Step args must be an object.");
                }
            }
        }

        internal static void Process(BatchJobRecord job)
        {
            if (Terminal(job)) return;
            try { ProcessCore(job); }
            catch (Exception ex) { Finish(job, false, ex.Message); }
        }

        private static void ProcessCore(BatchJobRecord job)
        {
            string stage = Convert.ToString(job.metadata["scenarioStage"]);
            if (stage == "cleanup")
            {
                SetBackground(Flag(job, "originalRunInBackground"));
                if (Flag(job, "startedPlayMode") && Playing)
                {
                    SetPlaying(false);
                    if (DateTime.UtcNow.Ticks - Number(job, "cleanupStartedTicks") < TimeSpan.FromSeconds(60).Ticks) return;
                    job.resultData["cleanupError"] = "Play Mode did not exit before cleanup timeout.";
                    job.resultData["error"] = job.resultData["cleanupError"];
                    job.resultData["passed"] = false;
                    job.resultData["executionUncertain"] = true;
                    job.metadata["failureStage"] = "interrupted";
                }
                Complete(job); return;
            }
            if (DateTime.UtcNow.Ticks > Number(job, "deadlineTicks")) { Finish(job, false, "Scenario timeout; steps will not be replayed.", "timeout"); return; }
            if (stage == "entering_play_mode")
            {
                if (!Playing) return;
                job.metadata["scenarioStage"] = "running";
                job.status = "running";
                Save(job); return;
            }
            if ((Convert.ToString(job.metadata["executionMode"]) == "play") != Playing) { Finish(job, false, "Editor mode changed during scenario."); return; }
            SetBackground(true);
            if (Flag(job, "stepExecuting")) { Finish(job, false, "Step interrupted by domain reload; it is not replayed.", "interrupted"); return; }
            var steps = JArray.Parse(Convert.ToString(job.metadata["steps"]));
            int index = (int)Number(job, "stepIndex");
            if (index >= steps.Count) { Finish(job, true, null); return; }
            var step = (JObject)steps[index];
            string kind = step.Value<string>("kind");
            if (kind == "wait_frames" || kind == "wait_seconds")
            {
                if (!Flag(job, "waiting"))
                {
                    job.metadata["waiting"] = true; job.metadata["waitFrame"] = Frame;
                    job.metadata["waitTicks"] = DateTime.UtcNow.Ticks; Save(job);
                }
                bool done = kind == "wait_frames" ? Frame - Number(job, "waitFrame") >= step.Value<int>("frames") :
                    (DateTime.UtcNow.Ticks - Number(job, "waitTicks")) / (double)TimeSpan.TicksPerSecond >= step.Value<double>("seconds");
                if (done) Advance(job, step, JValue.CreateNull(), true);
                return;
            }
            if (job.metadata.TryGetValue("childJobId", out var childId) && childId != null)
            {
                var child = AsyncJobService.Get(Convert.ToString(childId));
                if (child == null) { Finish(job, false, "Child job was lost; it is not replayed.", "interrupted"); return; }
                if (!Terminal(child)) return;
                var result = JObject.FromObject(child.resultData, JsonSerializer.Create(SkillsCommon.JsonSettings));
                if (child.status != "completed")
                {
                    Record(job, step, result, false);
                    string childStage = child.currentStage == "interrupted" || child.currentStage == "timeout" ? child.currentStage : child.status == "cancelled" ? "interrupted" : "failed";
                    Finish(job, false, "Child job " + child.status + ": " + child.resultSummary, childStage); return;
                }
                Advance(job, step, result, true); return;
            }
            JToken actual;
            if (kind == "assert" && step["skill"] == null)
                actual = job.resultData.TryGetValue("lastActual", out var previous) ? JToken.FromObject(previous, JsonSerializer.Create(SkillsCommon.JsonSettings)) : JValue.CreateNull();
            else
            {
                string skillName = step.Value<string>("skill");
                if (!SkillRouter.TryGetSkill(skillName, out var info) || (kind != "invoke" && !info.ReadOnly)) { Finish(job, false, "Step skill metadata changed; re-submit after inspection."); return; }
                // Persist before dispatch so a reload inside arbitrary skill code cannot replay this step.
                job.metadata["stepExecuting"] = true; Save(job);
                using (SkillsAuditLog.BeginRequestContext((int)Number(job, "callerRemotePort"),
                    job.metadata.TryGetValue("callerAgentId", out var callerId) ? Convert.ToString(callerId) : null, Flag(job, "callerExplicit")))
                    actual = JToken.Parse(Execute(skillName, (step["args"] ?? new JObject()).ToString(Formatting.None)));
                job.metadata["stepExecuting"] = false;
                if (actual["error"] != null || actual.Value<string>("status") == "error")
                { Record(job, step, actual, false); Finish(job, false, "Step refused or failed; inspect its actual response."); return; }
                actual = actual["result"] ?? actual["data"] ?? actual;
                string acceptedJob = (actual as JObject)?.Value<string>("jobId");
                if (!string.IsNullOrEmpty(acceptedJob)) { job.metadata["childJobId"] = acceptedJob; Save(job); return; }
            }
            if (kind == "assert" || kind == "wait_condition")
            {
                var selected = string.IsNullOrWhiteSpace(step.Value<string>("select")) ? actual : actual.SelectToken(step.Value<string>("select"));
                bool passed = Matches(selected, step["expected"], step.Value<double?>("tolerance") ?? 0);
                if (!passed && kind == "wait_condition")
                {
                    // Save the last observation for timeout evidence; polling remains subject to Router gates.
                    job.resultData["lastConditionActual"] = selected ?? JValue.CreateNull(); Save(job); return;
                }
                Record(job, step, selected, passed);
                if (!passed) { Finish(job, false, "Assertion failed."); return; }
                Advance(job, step, selected, true, false); return;
            }
            Advance(job, step, actual, true);
        }

        internal static bool Matches(JToken actual, JToken expected, double tolerance)
        {
            if (actual == null) return false;
            bool Numeric(JToken token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);
            if (Numeric(actual) && Numeric(expected))
            {
                if (decimal.TryParse(actual.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out var left) &&
                    decimal.TryParse(expected.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out var right) && tolerance <= (double)decimal.MaxValue)
                {
                    // Decimal preserves distinct Int64 values that round to the same IEEE double.
                    try { return Math.Abs(left - right) <= (decimal)tolerance; } catch (OverflowException) { return false; }
                }
                return Math.Abs(actual.Value<double>() - expected.Value<double>()) <= tolerance;
            }
            if (actual is JArray a && expected is JArray b) return a.Count == b.Count && a.Select((item, i) => Matches(item, b[i], tolerance)).All(x => x);
            if (actual is JObject ao && expected is JObject bo) return ao.Count == bo.Count && bo.Properties().All(p => Matches(ao[p.Name], p.Value, tolerance));
            return JToken.DeepEquals(actual, expected);
        }

        private static void Record(BatchJobRecord job, JObject step, JToken actual, bool passed)
        {
            var records = job.resultData["steps"] as JArray ?? JArray.FromObject(job.resultData["steps"]);
            records.Add(new JObject { ["index"] = Number(job, "stepIndex"), ["kind"] = step["kind"], ["skill"] = step["skill"],
                ["expected"] = step["expected"], ["actual"] = actual ?? JValue.CreateNull(), ["tolerance"] = step["tolerance"] ?? 0, ["passed"] = passed });
            job.resultData["steps"] = records;
        }
        private static void Advance(BatchJobRecord job, JObject step, JToken actual, bool passed, bool record = true)
        {
            if (record) Record(job, step, actual, passed);
            job.resultData["lastActual"] = actual;
            job.metadata["stepIndex"] = Number(job, "stepIndex") + 1;
            job.metadata.Remove("childJobId"); job.metadata.Remove("waiting");
            job.status = "running"; job.currentStage = "step_" + Number(job, "stepIndex"); Save(job);
        }
        private static void Finish(BatchJobRecord job, bool passed, string error, string failureStage = "failed")
        {
            CancelChild(job);
            job.resultData["passed"] = passed;
            if (error != null) job.resultData["error"] = error;
            job.metadata["failureStage"] = failureStage;
            job.resultData["interrupted"] = failureStage == "interrupted";
            job.resultData["executionUncertain"] = failureStage == "interrupted" || failureStage == "timeout";
            job.metadata["scenarioStage"] = "cleanup";
            job.metadata["cleanupStartedTicks"] = DateTime.UtcNow.Ticks;
            Save(job);
            SetBackground(Flag(job, "originalRunInBackground"));
            if (Flag(job, "startedPlayMode"))
            {
                SetPlaying(false);
                if (Playing) return;
            }
            Complete(job);
        }
        private static void CaptureEvidence(BatchJobRecord job)
        {
            bool passed = Convert.ToBoolean(job.resultData["passed"]);
            var compilation = JToken.Parse(CompilationResultService.GetLastCompilationJson() ?? "null");
            job.resultData["compilation"] = compilation;
            var window = LogWindowForTests?.Invoke() ?? JObject.FromObject(ConsoleLogHistoryService.ReadWindow(Number(job, "startLogSeq")), JsonSerializer.Create(SkillsCommon.JsonSettings));
            job.resultData["logWindow"] = window;
            // Incomplete evidence is not a verified pass. Keep assertion evidence so callers can distinguish causes.
            bool complete = (window.Value<bool?>("complete") ?? false) && !(window.Value<bool?>("hasMore") ?? false);
            bool errors = window.Descendants().OfType<JProperty>().Any(p =>
                (p.Name == "type" || p.Name == "logType" || p.Name == "severity") &&
                new[] { "Error", "Exception", "Assert" }.Contains(Convert.ToString(((JValue)p.Value).Value), StringComparer.OrdinalIgnoreCase));
            if (passed && (!complete || errors || ServerAvailabilityHelper.IsCompilationInProgress()))
            {
                job.resultData["passed"] = false;
                job.resultData["error"] = errors ? "Runtime error logs were captured." : !complete ? "Log coverage is incomplete; verification is inconclusive." : "Compilation is still in progress; verification is inconclusive.";
            }
        }
        private static void Complete(BatchJobRecord job)
        {
            CaptureEvidence(job);
            bool passed = Convert.ToBoolean(job.resultData["passed"]);
            if (passed) AsyncJobService.CompleteJob(job.jobId, "Scenario passed.", job.resultData);
            else AsyncJobService.FailJob(job.jobId, Convert.ToString(job.resultData["error"]), Convert.ToString(job.metadata["failureStage"]), job.resultData);
        }
        private static void CancelChild(BatchJobRecord job)
        {
            if (job.metadata.TryGetValue("childJobId", out var childId) && childId != null)
            {
                var child = AsyncJobService.Get(Convert.ToString(childId));
                if (!Terminal(child) && child.canCancel) AsyncJobService.Cancel(child.jobId);
            }
        }
        internal static void NotifyCancelled(BatchJobRecord job)
        {
            if (job == null || job.kind != "run_scenario") return;
            CancelChild(job);
            SetBackground(Flag(job, "originalRunInBackground"));
            if (Flag(job, "startedPlayMode")) SetPlaying(false);
            job.resultData["passed"] = false;
            job.resultData["cancelled"] = true;
            job.resultData["notReplayed"] = true;
            job.resultData["executionUncertain"] = true;
            CaptureEvidence(job);
        }
    }
}

// Producer:Betsy
