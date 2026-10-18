using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// P2 runtime control: particle_play / pause / stop / simulate / emit and their batches. They change the live state
    /// of a system (playback, time, particles), which no serialized property holds, so nothing reaches the Undo stack
    /// or the workflow: TracksWorkflow=false is the deliberate boundary of a transient Execute, batches validate every
    /// item and then run item by item without a rollback (each result says what happened), and
    /// /skills/batch?mode=transactional refuses these skills (<see cref="IsPlaybackSkill"/>). Nothing enters Play Mode:
    /// in Edit Mode the skills drive a preview, in Play Mode the running system. A simulation too long for one request
    /// runs as a job that advances a slice per editor tick on the main thread, so /health and /jobs keep answering.
    /// This file compiles without the Particle System module: the skill list, the request shape and the job hooks
    /// name no particle type.
    /// </summary>
    public static partial class ParticleSkills
    {
        internal const string SimulateJobKind = "particle_simulate";
        internal const string DefaultStopBehaviorName = "StopEmittingAndClear";

        /// <summary>The playback skills: runtime state the Undo stack does not record (the transactional batch check asks).</summary>
        internal static bool IsPlaybackSkill(string skill)
        {
            switch (skill)
            {
                case "particle_play":
                case "particle_play_batch":
                case "particle_pause":
                case "particle_pause_batch":
                case "particle_stop":
                case "particle_stop_batch":
                case "particle_simulate":
                case "particle_simulate_batch":
                case "particle_emit":
                case "particle_emit_batch":
                    return true;
                default:
                    return false;
            }
        }

        internal enum PlaybackOp
        {
            Play,
            Pause,
            Stop,
            Simulate,
            Emit
        }

        /// <summary>The parameters of one playback call or batch item, before validation.</summary>
        internal sealed class PlaybackRequest
        {
            public PlaybackOp Op;
            public bool WithChildren;
            public string StopBehavior;
            /// <summary>Null when a batch item leaves it out (the single skill always sends its value).</summary>
            public float? Seconds;
            public bool Restart = true;
            public bool FixedTimeStep = true;
            public int? Count;
            public bool RunAsync;
        }

        /// <summary>A playback batch item: its locator and the fields it adds to the request.</summary>
        internal interface IPlaybackItem
        {
            Locator Locator { get; }
            void Fill(PlaybackRequest request);
        }

        /// <summary>AsyncJobService's hook for <see cref="SimulateJobKind"/> jobs: advances one slice per editor tick.</summary>
        internal static void ProcessSimulateJob(BatchJobRecord job)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            ParticleSkills.ProcessSimulateSlice(job);
#else
            AsyncJobService.FailJob(job.jobId, $"particle_simulate needs Unity's built-in Particle System module ({ModulePackage}); the job cannot run.");
#endif
        }

        /// <summary>AsyncJobService's cancellation hook: the job's systems stay paused where the simulation stopped.</summary>
        internal static void NotifySimulateJobCancelled(BatchJobRecord job)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            ParticleSkills.ForgetSimulateRun(job, "cancelled");
#endif
        }

#if COM_UNITY_MODULES_PARTICLESYSTEM
        // ===================== Product limits (not Unity limits) =====================

        /// <summary>Steps one simulation (one call or batch item) may take: 6 s at the default 0.02 s fixed step.</summary>
        internal const int MaxSimulateSteps = 300;
        /// <summary>Systems one simulation may step (the target plus the children withChildren adds).</summary>
        internal const int MaxSimulateSystems = 10;
        /// <summary>Summed maxParticles of every system one call simulates: the particles left alive afterwards.</summary>
        internal const int MaxSimulateParticles = 100000;
        /// <summary>Summed steps x systems of one simulate call (single or batch).</summary>
        internal const int MaxSimulateSystemSteps = MaxSimulateSteps * MaxSimulateSystems;
        /// <summary>Systems a play / pause / stop item may reach through withChildren.</summary>
        internal const int MaxPlaybackSystems = 50;
        internal const int MaxEmitCount = 10000;
        internal const int MaxEmitTotal = 100000;
        /// <summary>
        /// Upper bound of the work a request simulates inline, in particle-steps (steps x summed maxParticles, an upper
        /// bound of the live particles). Above it the call needs runAsync=true. Calibrated on the probe machine so that
        /// the worst inline run stays near 100 ms of main-thread time.
        /// </summary>
        internal const long MaxInlineSimulateWork = 3000000;
        /// <summary>Main-thread time a simulation job spends per editor tick.</summary>
        internal const double SimulateSliceMilliseconds = 10.0;

        /// <summary>The limits as particle_get_capabilities reports them.</summary>
        internal static Dictionary<string, object> PlaybackLimits() => new Dictionary<string, object>
        {
            ["maxSimulateSteps"] = MaxSimulateSteps,
            ["maxSimulateSystems"] = MaxSimulateSystems,
            ["maxSimulateParticles"] = MaxSimulateParticles,
            ["maxSimulateSystemSteps"] = MaxSimulateSystemSteps,
            ["maxInlineSimulateWork"] = MaxInlineSimulateWork,
            ["maxPlaybackSystems"] = MaxPlaybackSystems,
            ["maxEmitCount"] = MaxEmitCount,
            ["maxEmitTotal"] = MaxEmitTotal,
            ["fixedDeltaTime"] = Time.fixedDeltaTime,
            ["maximumParticleDeltaTime"] = Time.maximumParticleDeltaTime
        };

        /// <summary>The keys each playback batch item accepts; anything else is rejected, not ignored.</summary>
        internal static class PlaybackItemKeys
        {
            public static readonly string[] Play = { "name", "entityId", "instanceId", "path", "withChildren" };
            public static readonly string[] Stop = { "name", "entityId", "instanceId", "path", "withChildren", "stopBehavior" };
            public static readonly string[] Simulate = { "name", "entityId", "instanceId", "path", "seconds", "restart", "withChildren", "fixedTimeStep" };
            public static readonly string[] Emit = { "name", "entityId", "instanceId", "path", "count" };

            public static string[] For(PlaybackOp op)
            {
                switch (op)
                {
                    case PlaybackOp.Stop: return Stop;
                    case PlaybackOp.Simulate: return Simulate;
                    case PlaybackOp.Emit: return Emit;
                    default: return Play;
                }
            }
        }

        /// <summary>A float as the API shows it: invariant culture, the shortest text that reads back as the same float.</summary>
        private static string F(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        internal static string OpName(PlaybackOp op)
        {
            switch (op)
            {
                case PlaybackOp.Play: return "play";
                case PlaybackOp.Pause: return "pause";
                case PlaybackOp.Stop: return "stop";
                case PlaybackOp.Simulate: return "simulate";
                default: return "emit";
            }
        }

        private static PlaybackOp OpOfSkill(string skill)
        {
            var single = skill.EndsWith("_batch", StringComparison.Ordinal) ? skill.Substring(0, skill.Length - "_batch".Length) : skill;
            switch (single)
            {
                case "particle_pause": return PlaybackOp.Pause;
                case "particle_stop": return PlaybackOp.Stop;
                case "particle_simulate": return PlaybackOp.Simulate;
                case "particle_emit": return PlaybackOp.Emit;
                default: return PlaybackOp.Play;
            }
        }

        // ===================== Validation (execution and dryRun share it) =====================

        /// <summary>One validated playback call or batch item: the target, every system it reaches and its budgets.</summary>
        internal sealed class PlaybackItem
        {
            public PlaybackRequest Request;
            public ParticleBinding Target;
            public string Prefix;
            /// <summary>Every system the call drives, the target first, then its children in Hierarchy order.</summary>
            public List<ParticleSystem> Systems = new List<ParticleSystem>();
            public ParticleSystemStopBehavior StopBehavior = ParticleSystemStopBehavior.StopEmittingAndClear;
            public float StepSeconds;
            public int Steps;
            /// <summary>Summed maxParticles of <see cref="Systems"/> (simulate).</summary>
            public long ParticleBudget;
            public List<string> Warnings = new List<string>();
            public long Work => Steps * ParticleBudget;
        }

        /// <summary>
        /// Validates one playback call or batch item against the live target and never changes it: the Editor state, the
        /// scene, the action's parameters, the systems withChildren reaches, this version's refusals (setups whose
        /// playback could run user scripts or change objects) and the per-item budgets.
        /// </summary>
        internal static PlaybackItem PreparePlayback(PlaybackRequest request, ParticleBinding binding, string prefix, Problems problems, List<string> warnings)
        {
            var item = new PlaybackItem { Request = request, Target = binding, Prefix = prefix, Warnings = warnings ?? new List<string>() };
            var targetPath = Codec.Join(prefix, "target");
            var where = GameObjectFinder.GetPath(binding.GameObject);

            if (EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                problems.Add(targetPath, "The Editor is entering or leaving Play Mode; send the call again once it has settled.");
            else if (!IsInOpenScene(binding.GameObject))
                problems.Add(targetPath, $"'{binding.GameObject.name}' is not in an open scene (a prefab asset or a Prefab Mode object); the playback skills drive systems of open scenes.");

            switch (request.Op)
            {
                case PlaybackOp.Stop:
                    PrepareStopBehavior(item, prefix, problems);
                    break;
                case PlaybackOp.Simulate:
                    PrepareSteps(item, prefix, problems);
                    break;
                case PlaybackOp.Emit:
                    var countPath = Codec.Join(prefix, "count");
                    if (!request.Count.HasValue)
                    {
                        // A single call always carries a value; the router's RequiredParams check owns a missing one there.
                        if (!string.IsNullOrEmpty(prefix))
                            problems.Add(countPath, $"{countPath} is required: the number of particles to emit, 1-{MaxEmitCount}.", CodeMissingParam);
                    }
                    else if (request.Count.Value < 1 || request.Count.Value > MaxEmitCount)
                        problems.Add(countPath, $"{countPath} must be 1-{MaxEmitCount} particles per call, got {request.Count.Value}.");
                    break;
            }

            item.Systems = PlaybackSystems(binding.System, request.Op != PlaybackOp.Emit && request.WithChildren);
            int cap = request.Op == PlaybackOp.Simulate ? MaxSimulateSystems : MaxPlaybackSystems;
            if (item.Systems.Count > cap)
            {
                problems.Add(targetPath, $"withChildren reaches {item.Systems.Count} systems from '{where}'; one {OpName(request.Op)} item drives at most {cap}. " +
                    "Target the child systems in separate items, or send withChildren=false.");
            }

            if (request.Op == PlaybackOp.Simulate)
            {
                item.ParticleBudget = item.Systems.Sum(system => (long)system.main.maxParticles);
                if (item.ParticleBudget > MaxSimulateParticles)
                {
                    problems.Add(targetPath, $"The systems reached from '{where}' allow {item.ParticleBudget} particles together (summed main.maxParticles); one simulate call allows at most {MaxSimulateParticles}. " +
                        "Lower maxParticles, simulate fewer systems, or split the call.");
                }
            }

            CheckPlaybackRisks(request.Op, item.Systems, targetPath, problems);

            foreach (var system in item.Systems)
            {
                var busy = SimulateJobOf(system);
                if (busy != null)
                {
                    problems.Add(targetPath, $"'{GameObjectFinder.GetPath(system.gameObject)}' is being simulated by job {busy}; wait for it (GET /jobs/{busy}) or cancel it (job_cancel), then send the call again.");
                    break;
                }
            }

            AddPlaybackWarnings(item);
            return item;
        }

        private static void PrepareStopBehavior(PlaybackItem item, string prefix, Problems problems)
        {
            var text = item.Request.StopBehavior;
            if (string.IsNullOrWhiteSpace(text))
                text = DefaultStopBehaviorName;
            if (Codec.ReadEnum(new JValue(text), Codec.Join(prefix, "stopBehavior"), problems, typeof(ParticleSystemStopBehavior), null, out var value))
                item.StopBehavior = (ParticleSystemStopBehavior)value;
        }

        /// <summary>
        /// The step size and count of a simulation. Fixed steps are Time.fixedDeltaTime and seconds rounds to whole
        /// steps; variable steps simulate the exact seconds, which Unity splits into updates of at most
        /// Time.maximumParticleDeltaTime (the size counted against the step budget).
        /// </summary>
        private static void PrepareSteps(PlaybackItem item, string prefix, Problems problems)
        {
            var request = item.Request;
            var path = Codec.Join(prefix, "seconds");
            item.StepSeconds = request.FixedTimeStep ? Time.fixedDeltaTime : Time.maximumParticleDeltaTime;
            var stepSource = request.FixedTimeStep ? "Time.fixedDeltaTime" : "Time.maximumParticleDeltaTime";
            if (!request.Seconds.HasValue)
            {
                // A single call always carries a value; the router's RequiredParams check owns a missing one there.
                if (!string.IsNullOrEmpty(prefix))
                    problems.Add(path, $"{path} is required: the simulated time in seconds.", CodeMissingParam);
                return;
            }

            float seconds = request.Seconds.Value;
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f)
            {
                problems.Add(path, $"{path} must be a finite number of seconds > 0, got {F(seconds)}.");
                return;
            }
            if (float.IsNaN(item.StepSeconds) || item.StepSeconds <= 0f)
            {
                problems.Add(path, $"{stepSource} is {F(item.StepSeconds)} in this project, so no simulation step can be taken. Set it in Project Settings > Time.");
                return;
            }

            double ratio = (double)seconds / item.StepSeconds;
            double steps = request.FixedTimeStep ? Math.Round(ratio, MidpointRounding.AwayFromZero) : Math.Ceiling(ratio - 1e-6);
            if (steps < 1)
            {
                problems.Add(path, $"{path} = {F(seconds)} s is less than half a fixed step ({F(item.StepSeconds)} s, {stepSource}); send at least {F(item.StepSeconds)} s, or fixedTimeStep=false.");
                return;
            }
            if (steps > MaxSimulateSteps)
            {
                problems.Add(path, $"{path} = {F(seconds)} s is {(long)steps} steps of {F(item.StepSeconds)} s ({stepSource}); one simulation takes at most {MaxSimulateSteps} steps " +
                    $"({F(MaxSimulateSteps * item.StepSeconds)} s). Simulate it in parts: the next call with restart=false continues where the last one stopped.");
                return;
            }
            item.Steps = (int)steps;
        }

        /// <summary>
        /// The systems a call drives: the target, plus with <paramref name="withChildren"/> every ParticleSystem below it,
        /// inactive ones and ones under objects without a system included, in Hierarchy order (what Unity's own
        /// withChildren reaches).
        /// </summary>
        internal static List<ParticleSystem> PlaybackSystems(ParticleSystem target, bool withChildren)
        {
            var systems = new List<ParticleSystem> { target };
            if (!withChildren)
                return systems;
            foreach (var child in target.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (child != target)
                    systems.Add(child);
            }
            return systems;
        }

        /// <summary>
        /// This version refuses a system whose playback could run user scripts or change objects: a stopAction other than
        /// None (Unity disables or destroys the object, or calls OnParticleSystemStopped, when it stops), collision
        /// messages (OnParticleCollision on the scripts of the system and of every collider hit), trigger callbacks
        /// (OnParticleTrigger) and a running script that implements OnParticleUpdateJobScheduled. Sub-emitters of the
        /// systems count, since their particles come from this call. Pausing runs nothing and is never refused; stopping
        /// only triggers the stop action.
        /// </summary>
        private static void CheckPlaybackRisks(PlaybackOp op, List<ParticleSystem> systems, string path, Problems problems)
        {
            if (op == PlaybackOp.Pause)
                return;
            var scope = op == PlaybackOp.Stop ? systems : WithSubEmitters(systems);
            foreach (var system in scope)
            {
                var where = GameObjectFinder.GetPath(system.gameObject);
                var stopAction = system.main.stopAction;
                if (stopAction != ParticleSystemStopAction.None)
                {
                    problems.Add(path, $"'{where}' has main.stopAction {stopAction}: when it stops, Unity {StopActionEffect(stopAction)}. The playback skills refuse such systems in this version. " +
                        "To preview it, set main.stopAction to None with particle_configure (workflow_undo_task restores it), or control it from your own code.");
                }
                if (op == PlaybackOp.Stop)
                    continue;

                var collision = system.collision;
                if (collision.enabled && collision.sendCollisionMessages)
                {
                    problems.Add(path, $"'{where}' sends collision messages (collision.sendCollisionMessages): particles that hit colliders call OnParticleCollision on scripts of the system and of every collider hit. " +
                        "The playback skills refuse it in this version; turn the messages off in the Inspector to preview it.");
                }

                var trigger = system.trigger;
                if (trigger.enabled && (trigger.inside == ParticleSystemOverlapAction.Callback || trigger.outside == ParticleSystemOverlapAction.Callback
                    || trigger.enter == ParticleSystemOverlapAction.Callback || trigger.exit == ParticleSystemOverlapAction.Callback))
                {
                    problems.Add(path, $"'{where}' has trigger callbacks (an inside/outside/enter/exit action set to Callback): Unity calls OnParticleTrigger on its scripts. " +
                        "The playback skills refuse it in this version; set those actions to Ignore or Kill in the Inspector to preview it.");
                }

                foreach (var script in RunningScriptsWith(system.gameObject, "OnParticleUpdateJobScheduled"))
                {
                    problems.Add(path, $"'{where}' has the script {script.GetType().Name} implementing OnParticleUpdateJobScheduled, which Unity calls while the system updates" +
                        (EditorApplication.isPlaying ? "" : " (the script runs in Edit Mode)") + ". The playback skills refuse it in this version.");
                }
            }
        }

        private static string StopActionEffect(ParticleSystemStopAction action)
        {
            switch (action)
            {
                case ParticleSystemStopAction.Disable: return "deactivates its GameObject";
                case ParticleSystemStopAction.Destroy: return "destroys its GameObject";
                case ParticleSystemStopAction.Callback: return "calls OnParticleSystemStopped on its scripts";
                default: return "runs its stop action";
            }
        }

        /// <summary><paramref name="systems"/> plus every sub-emitter system they spawn, transitively (bounded).</summary>
        private static List<ParticleSystem> WithSubEmitters(List<ParticleSystem> systems)
        {
            var result = new List<ParticleSystem>(systems);
            var seen = new HashSet<ParticleSystem>(systems);
            for (int i = 0; i < result.Count && result.Count < 4 * MaxPlaybackSystems; i++)
            {
                var subEmitters = result[i].subEmitters;
                if (!subEmitters.enabled)
                    continue;
                for (int k = 0; k < subEmitters.subEmittersCount; k++)
                {
                    var sub = subEmitters.GetSubEmitterSystem(k);
                    if (sub != null && seen.Add(sub))
                        result.Add(sub);
                }
            }
            return result;
        }

        /// <summary>
        /// The enabled MonoBehaviours of <paramref name="go"/> that implement <paramref name="message"/> and would receive it
        /// now: every one in Play Mode, only those that run in Edit Mode ([ExecuteAlways], [ExecuteInEditMode] or
        /// runInEditMode) otherwise.
        /// </summary>
        private static IEnumerable<MonoBehaviour> RunningScriptsWith(GameObject go, string message)
        {
            foreach (var behaviour in go.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null || !behaviour.isActiveAndEnabled)
                    continue;
                var type = behaviour.GetType();
                if (!EditorApplication.isPlaying && !behaviour.runInEditMode
                    && !type.IsDefined(typeof(ExecuteAlways), true) && !type.IsDefined(typeof(ExecuteInEditMode), true))
                    continue;
                for (var current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
                {
                    if (current.GetMethod(message, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly) != null)
                    {
                        yield return behaviour;
                        break;
                    }
                }
            }
        }

        /// <summary>Notes that are true but do not block: Edit Mode playback semantics and the Editor's own preview.</summary>
        private static void AddPlaybackWarnings(PlaybackItem item)
        {
            if (EditorApplication.isPlaying)
                return;
            var op = item.Request.Op;
            if (op == PlaybackOp.Play)
            {
                AddWarning(item, "In Edit Mode Unity advances a playing system only while the Scene view previews it (a selected system), so time and particleCount stay where they are; " +
                    "particle_simulate moves it by an exact time.");
            }
            if (op == PlaybackOp.Emit && item.Target.System.isStopped)
                AddWarning(item, "The system is stopped: in Edit Mode the new particles stay where they were born until particle_simulate (restart=false) advances them.");

            foreach (var system in item.Systems)
            {
                if (PreviewedBySelection(system))
                {
                    AddWarning(item, $"'{GameObjectFinder.GetPath(system.gameObject)}' belongs to the effect selected in the Editor: the Scene view's Particle Effect preview drives its playback and can override this call. Deselect it for a stable result.");
                    break;
                }
            }
        }

        private static void AddWarning(PlaybackItem item, string warning)
        {
            if (!item.Warnings.Contains(warning))
                item.Warnings.Add(warning);
        }

        /// <summary>Whether a selected system shares <paramref name="system"/>'s effect root (the Particle Effect preview then owns it).</summary>
        private static bool PreviewedBySelection(ParticleSystem system)
        {
            var root = EffectRoot(system);
            foreach (var selected in Selection.gameObjects)
            {
                var selectedSystem = selected != null ? selected.GetComponent<ParticleSystem>() : null;
                if (selectedSystem != null && EffectRoot(selectedSystem) == root)
                    return true;
            }
            return false;
        }

        /// <summary>The top of the chain of ParticleSystem parents, as the Editor's Particle Effect preview groups systems.</summary>
        private static ParticleSystem EffectRoot(ParticleSystem system)
        {
            var transform = system.transform;
            while (transform.parent != null && transform.parent.GetComponent<ParticleSystem>() != null)
                transform = transform.parent;
            return transform.GetComponent<ParticleSystem>();
        }

        /// <summary>
        /// The checks across the items of one call: no system reached by two items (their order would decide the result),
        /// the per-call simulate budgets and the inline work limit, and the emit total. Problems of one item go to its
        /// own list; the call-level ones to <paramref name="callProblems"/>.
        /// </summary>
        internal static void CheckPlaybackTotals(IReadOnlyList<KeyValuePair<Problems, PlaybackItem>> items, bool batch, Problems callProblems)
        {
            var owner = new Dictionary<ParticleSystem, int>();
            for (int i = 0; i < items.Count; i++)
            {
                foreach (var system in items[i].Value.Systems)
                {
                    if (owner.TryGetValue(system, out var first))
                    {
                        items[i].Key.Add(items[i].Value.Prefix, $"{items[i].Value.Prefix} reaches '{GameObjectFinder.GetPath(system.gameObject)}', which {items[first].Value.Prefix} already drives; " +
                            "merge them into one item or send them in separate calls, since their order would decide the result.");
                        break;
                    }
                    owner[system] = i;
                }
            }
            if (items.Count == 0)
                return;

            var op = items[0].Value.Request.Op;
            if (op == PlaybackOp.Simulate)
            {
                long systemSteps = items.Sum(entry => (long)entry.Value.Steps * entry.Value.Systems.Count);
                long particles = items.Sum(entry => entry.Value.ParticleBudget);
                long work = items.Sum(entry => entry.Value.Work);
                if (batch && systemSteps > MaxSimulateSystemSteps)
                    callProblems.Add("items", $"The items simulate {systemSteps} system-steps together (steps x systems); one call takes at most {MaxSimulateSystemSteps}. Split the batch.");
                if (batch && particles > MaxSimulateParticles)
                    callProblems.Add("items", $"The items simulate systems allowing {particles} particles together (summed main.maxParticles); one call allows at most {MaxSimulateParticles}. Split the batch.");
                if (!items[0].Value.Request.RunAsync && work > MaxInlineSimulateWork)
                {
                    callProblems.Add("runAsync", $"This simulation is up to {work} particle-steps (steps x summed maxParticles), above the {MaxInlineSimulateWork} one request runs inline. " +
                        "Send runAsync=true: it runs as a job sliced across editor ticks and returns a jobId to poll (GET /jobs/{id}).");
                }
            }
            else if (op == PlaybackOp.Emit && batch)
            {
                long total = items.Sum(entry => (long)(entry.Value.Request.Count ?? 0));
                if (total > MaxEmitTotal)
                    callProblems.Add("items", $"The items emit {total} particles together; one call emits at most {MaxEmitTotal}. Split the batch.");
            }
        }

        // ===================== Single skills =====================

        /// <summary>particle_play / pause / stop / emit: resolve, validate, run once, read back.</summary>
        internal static object PlaybackCore(string skill, Locator locator, PlaybackRequest request)
        {
            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;

            var problems = new Problems();
            var item = PreparePlayback(request, binding, "", problems, new List<string>());
            var callProblems = new Problems();
            CheckPlaybackTotals(new[] { new KeyValuePair<Problems, PlaybackItem>(problems, item) }, false, callProblems);
            problems.AddRange(callProblems);
            if (problems.Any)
                return ProblemsError(skill, problems);
            return RunPlayback(item);
        }

        /// <summary>Test seam: runs before every play / pause / stop / emit item, so a test can make one fail at run time.</summary>
        internal static Action<PlaybackItem> BeforePlaybackHookForTests;

        /// <summary>Runs one validated play / pause / stop / emit and builds its result from what Unity reports afterwards.</summary>
        internal static Dictionary<string, object> RunPlayback(PlaybackItem item)
        {
            BeforePlaybackHookForTests?.Invoke(item);
            var system = item.Target.System;
            var request = item.Request;
            int before = system.particleCount;
            switch (request.Op)
            {
                case PlaybackOp.Play:
                    system.Play(request.WithChildren);
                    break;
                case PlaybackOp.Pause:
                    system.Pause(request.WithChildren);
                    break;
                case PlaybackOp.Stop:
                    system.Stop(request.WithChildren, item.StopBehavior);
                    break;
                case PlaybackOp.Emit:
                    system.Emit(request.Count ?? 0);
                    break;
            }

            var go = item.Target.GameObject;
            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = go.name,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                ["path"] = GameObjectFinder.GetPath(go),
                ["action"] = OpName(request.Op),
                ["withChildren"] = request.Op != PlaybackOp.Emit && request.WithChildren,
                ["playback"] = LivePlayback(system),
                ["systems"] = SystemStates(item.Systems),
                ["undoable"] = false,
                ["warnings"] = item.Warnings.ToArray()
            };
            if (request.Op == PlaybackOp.Stop)
                result["stopBehavior"] = item.StopBehavior.ToString();
            if (request.Op == PlaybackOp.Emit)
            {
                int after = system.particleCount;
                int emitted = Math.Max(0, after - before);
                result["requested"] = request.Count ?? 0;
                result["emitted"] = emitted;
                result["particleCount"] = after;
                if (emitted < (request.Count ?? 0))
                {
                    var warnings = new List<string>(item.Warnings)
                    {
                        $"Unity emitted {emitted} of {request.Count} particles: a system keeps at most main.maxParticles ({system.main.maxParticles}) alive."
                    };
                    result["warnings"] = warnings.ToArray();
                }
            }
            return result;
        }

        /// <summary>The playback flags, time and particle count Unity reports for one system now.</summary>
        internal static JObject LivePlayback(ParticleSystem system)
        {
            var state = PlaybackState(system);
            state["totalTime"] = system.totalTime;
            return state;
        }

        private static object[] SystemStates(IEnumerable<ParticleSystem> systems) => systems.Select(system => (object)new Dictionary<string, object>
        {
            ["name"] = system.gameObject.name,
            ["entityId"] = UnityObjectIdUtility.GetEntityId(system.gameObject),
            ["path"] = GameObjectFinder.GetPath(system.gameObject),
            ["playback"] = LivePlayback(system)
        }).ToArray();

        private static object[] SystemIdentities(IEnumerable<ParticleSystem> systems) => systems.Select(system => (object)new Dictionary<string, object>
        {
            ["name"] = system.gameObject.name,
            ["entityId"] = UnityObjectIdUtility.GetEntityId(system.gameObject),
            ["path"] = GameObjectFinder.GetPath(system.gameObject)
        }).ToArray();

        // ===================== Simulation =====================

        /// <summary>One simulation in progress, inline or in a job: the steps done and the state it left the target in.</summary>
        internal sealed class SimulateProgress
        {
            public PlaybackItem Item;
            /// <summary>The target's Hierarchy path when the simulation was accepted (still known after it is destroyed).</summary>
            public string Path;
            /// <summary>Whether the result is a batch item's (stamped with its target).</summary>
            public bool InBatch;
            public int Done;
            public bool Started;
            public float LeftAtTime;
            public Dictionary<string, object> Result;
            public bool Finished => Done >= Item.Steps;
        }

        /// <summary>
        /// The simulated seconds of a slice of <paramref name="steps"/> steps. Fixed steps ask for half a step more than
        /// the whole steps: Unity counts fixed steps by subtracting the step size in floating point and drops what is
        /// left, so an exact multiple can lose its last step (0.5 s gave 24 steps of 0.02 s). Variable steps ask for the
        /// exact time; the last slice takes what remains of seconds.
        /// </summary>
        private static float SliceSeconds(SimulateProgress progress, int steps)
        {
            var item = progress.Item;
            if (item.Request.FixedTimeStep)
                return (steps + 0.5f) * item.StepSeconds;
            if (progress.Done + steps >= item.Steps)
                return Math.Max(0f, item.Request.Seconds.Value - progress.Done * item.StepSeconds);
            return steps * item.StepSeconds;
        }

        /// <summary>Simulates up to <paramref name="maxSteps"/> more steps of one item; only its first slice may restart.</summary>
        private static void AdvanceSimulation(SimulateProgress progress, int maxSteps)
        {
            var item = progress.Item;
            int steps = Math.Min(maxSteps, item.Steps - progress.Done);
            if (steps <= 0)
                return;
            var system = item.Target.System;
            system.Simulate(SliceSeconds(progress, steps), item.Request.WithChildren, !progress.Started && item.Request.Restart, item.Request.FixedTimeStep);
            progress.Started = true;
            progress.Done += steps;
            progress.LeftAtTime = system.time;
        }

        /// <summary>The result of a finished simulation, read back from Unity (Simulate leaves the systems paused).</summary>
        internal static Dictionary<string, object> SimulateResult(SimulateProgress progress)
        {
            var item = progress.Item;
            var request = item.Request;
            var system = item.Target.System;
            var go = item.Target.GameObject;
            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["status"] = "completed",
                ["name"] = go.name,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                ["path"] = GameObjectFinder.GetPath(go),
                ["action"] = "simulate",
                ["withChildren"] = request.WithChildren,
                ["seconds"] = request.Seconds ?? 0f,
                ["fixedTimeStep"] = request.FixedTimeStep,
                ["restart"] = request.Restart,
                ["stepSeconds"] = item.StepSeconds,
                ["steps"] = progress.Done,
                ["time"] = system.time,
                ["totalTime"] = system.totalTime,
                ["particleCount"] = system.particleCount,
                ["isPaused"] = system.isPaused,
                ["playback"] = LivePlayback(system),
                ["systems"] = SystemStates(item.Systems),
                ["undoable"] = false,
                ["warnings"] = item.Warnings.ToArray()
            };
            if (!system.isPaused)
                result["note"] = "Unity did not leave the system paused (it may have finished its duration or been inactive); see playback.";
            return result;
        }

        /// <summary>particle_simulate: inline within the work limit, or a sliced job with runAsync.</summary>
        internal static object SimulateCore(Locator locator, PlaybackRequest request)
        {
            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;

            var problems = new Problems();
            var item = PreparePlayback(request, binding, "", problems, new List<string>());
            var callProblems = new Problems();
            CheckPlaybackTotals(new[] { new KeyValuePair<Problems, PlaybackItem>(problems, item) }, false, callProblems);
            problems.AddRange(callProblems);
            if (problems.Any)
                return ProblemsError("particle_simulate", problems);
            if (request.RunAsync)
                return ParticleSkills.StartSimulateJob("particle_simulate", new List<PlaybackItem> { item }, null);

            var progress = new SimulateProgress { Item = item };
            AdvanceSimulation(progress, item.Steps);
            return SimulateResult(progress);
        }

        // ===================== Batches =====================

        /// <summary>Validates one playback batch item through the same path as the single skill.</summary>
        private static void PreparePlaybackItem(IPlaybackItem source, BatchPrep prep, PlaybackOp op, bool runAsync, Dictionary<int, PlaybackItem> plans)
        {
            BindItem(source.Locator, prep, out var binding);
            if (binding == null)
                return;
            var request = new PlaybackRequest { Op = op, RunAsync = runAsync };
            source.Fill(request);
            var item = PreparePlayback(request, binding, Codec.Index("items", prep.Index), prep.Problems, prep.Warnings);
            plans[prep.Index] = item;
        }

        /// <summary>
        /// Validation shared by every playback batch: the items, then the checks across them. Returns a ready error when
        /// the list itself or a call-level budget is invalid; otherwise null, with the per-item verdicts in <paramref name="preps"/>.
        /// </summary>
        private static object ValidatePlaybackBatch<TItem>(string itemsJson, string skill, PlaybackOp op, bool runAsync,
            out List<BatchPrep> preps, out Dictionary<int, PlaybackItem> plans) where TItem : class, IPlaybackItem
        {
            var prepared = new Dictionary<int, PlaybackItem>();
            plans = prepared;
            var rejected = ValidateBatch<TItem>(itemsJson, skill, PlaybackItemKeys.For(op), (item, prep) => PreparePlaybackItem(item, prep, op, runAsync, prepared), out preps);
            if (rejected != null)
                return rejected;

            var valid = preps.Where(prep => prep.Ok && prepared.ContainsKey(prep.Index))
                .Select(prep => new KeyValuePair<Problems, PlaybackItem>(prep.Problems, prepared[prep.Index]))
                .ToList();
            var callProblems = new Problems();
            CheckPlaybackTotals(valid, true, callProblems);
            return callProblems.Any ? ProblemsError(skill, callProblems) : null;
        }

        /// <summary>
        /// The batch flow of play / pause / stop / emit: every item validated first, nothing runs when any item is
        /// invalid, then the items run in order through <see cref="BatchExecutor"/> without atomic (playback cannot be
        /// rolled back): an item that fails at run time is reported failed and the items that ran stay as they are.
        /// </summary>
        internal static object PlaybackBatchCore<TItem>(string items, string skill, PlaybackOp op) where TItem : class, IPlaybackItem
        {
            var rejected = ValidatePlaybackBatch<TItem>(items, skill, op, false, out var preps, out var plans);
            if (rejected != null)
                return rejected;

            bool anyInvalid = preps.Any(prep => !prep.Ok);
            int index = 0;
            return ParticleSkills.PlaybackEnvelope(skill, anyInvalid, null, BatchExecutor.Execute<TItem>(items, item =>
            {
                var prep = preps[index++];
                var refused = RefusedItem(prep, anyInvalid);
                return refused ?? Stamp(RunPlayback(plans[prep.Index]), prep.Target, null);
            }, item => item.Locator.Describe(), atomic: false));
        }

        /// <summary>particle_simulate_batch: validated like the others; inline item by item, or one sliced job for all items.</summary>
        internal static object SimulateBatchCore(string items, bool runAsync)
        {
            const string skill = "particle_simulate_batch";
            var rejected = ValidatePlaybackBatch<BatchParticleSimulateItem>(items, skill, PlaybackOp.Simulate, runAsync, out var preps, out var plans);
            if (rejected != null)
                return rejected;

            bool anyInvalid = preps.Any(prep => !prep.Ok);
            if (runAsync && !anyInvalid)
                return ParticleSkills.StartSimulateJob(skill, preps.Select(prep => plans[prep.Index]).ToList(), items);

            int index = 0;
            return ParticleSkills.PlaybackEnvelope(skill, anyInvalid, "completed", BatchExecutor.Execute<BatchParticleSimulateItem>(items, item =>
            {
                var prep = preps[index++];
                var refused = RefusedItem(prep, anyInvalid);
                if (refused != null)
                    return refused;
                var progress = new SimulateProgress { Item = plans[prep.Index] };
                AdvanceSimulation(progress, progress.Item.Steps);
                return Stamp(SimulateResult(progress), prep.Target, null);
            }, item => item.Locator.Describe(), atomic: false));
        }

        /// <summary>The result of an item that does not run: its own error, or "not run" when another item is invalid.</summary>
        private static Dictionary<string, object> RefusedItem(BatchPrep prep, bool anyInvalid)
        {
            if (prep.Failure != null)
                return Stamp(AsDictionary(prep.Failure), prep.Target, false);
            if (prep.Problems.Any)
                return Stamp(ProblemsError(prep.Target, prep.Problems), prep.Target, false);
            if (!anyInvalid)
                return null;
            return new Dictionary<string, object>
            {
                ["target"] = prep.Target,
                ["success"] = false,
                ["validated"] = true,
                ["executed"] = false,
                ["error"] = "Not run: a playback batch runs only when every item is valid, and another item is not.",
                ["errorCode"] = CodeSemantic
            };
        }

        /// <summary>
        /// BatchExecutor's envelope, completed for a batch nothing can roll back: atomic and undoable are false, and the
        /// error and fixes say which items took effect, so a failed batch is never resent whole (that would play or
        /// emit the successful items again).
        /// </summary>
        internal static object PlaybackEnvelope(string skill, bool anyInvalid, string status, object executed)
        {
            if (!(executed is Dictionary<string, object> envelope))
                return executed;
            envelope["atomic"] = false;
            envelope["undoable"] = false;
            int ran = anyInvalid ? 0 : Convert.ToInt32(envelope["successCount"]);
            envelope["executedCount"] = ran;
            if (status != null && !anyInvalid)
                envelope["status"] = status;
            if (envelope["error"] == null)
                return envelope;

            int failed = Convert.ToInt32(envelope["failCount"]);
            if (anyInvalid)
            {
                envelope["error"] = $"{skill} ran nothing: a playback batch runs only when every item is valid; fix the items that report problems and send the batch again.";
                envelope["suggestedFixes"] = new object[] { Fix("fix_param", null, "Fix every item whose result lists problems or an error, then resend the whole batch; nothing ran.") };
            }
            else
            {
                envelope["error"] = $"{skill}: {failed} item(s) failed while running; the {ran} item(s) that succeeded took effect and are not rolled back.";
                envelope["suggestedFixes"] = new object[] { Fix("fix_param", null, "Resend only the failed items: the others already ran, and resending them would run them again.") };
                envelope["retryStrategy"] = SkillErrorResponse.RetryFixAndRetry;
            }
            return envelope;
        }

        // ===================== Simulation jobs =====================

        /// <summary>
        /// The live simulation jobs. In memory only: a domain reload or an Editor restart ends them (the job then fails
        /// as interrupted; nothing is replayed). A nested class, so the registry's initializer is not a field of the
        /// partial class.
        /// </summary>
        internal static class SimulateJobRegistry
        {
            internal sealed class Run
            {
                public string JobId;
                public string Skill;
                public bool Batch;
                public bool PlayMode;
                public string ItemsJson;
                public readonly List<SimulateProgress> Items = new List<SimulateProgress>();
                public int Current;
                public int TotalSteps;
                public int DoneSteps => Items.Sum(progress => progress.Done);
            }

            internal static readonly Dictionary<string, Run> Runs = new Dictionary<string, Run>(StringComparer.Ordinal);

            /// <summary>Test seam: at most this many steps per editor tick, so a test can watch a job advance slice by slice.</summary>
            internal static int? StepsPerSliceForTests;
        }

        /// <summary>The job that will still simulate <paramref name="system"/>, or null.</summary>
        internal static string SimulateJobOf(ParticleSystem system)
        {
            foreach (var run in SimulateJobRegistry.Runs.Values)
            {
                for (int i = run.Current; i < run.Items.Count; i++)
                {
                    if (run.Items[i].Item.Systems.Contains(system))
                        return run.JobId;
                }
            }
            return null;
        }

        /// <summary>Registers a sliced simulation job for validated items and answers with its handle at once.</summary>
        internal static Dictionary<string, object> StartSimulateJob(string skill, List<PlaybackItem> items, string itemsJson)
        {
            int totalSteps = items.Sum(item => item.Steps);
            var job = AsyncJobService.CreateJob(SimulateJobKind, "queued",
                $"{skill}: {items.Count} simulation(s), {totalSteps} step(s) accepted; they advance on the main thread between requests.", canCancel: true,
                metadata: new Dictionary<string, object>
                {
                    ["skill"] = skill,
                    ["totalSteps"] = totalSteps,
                    ["doneSteps"] = 0,
                    ["targets"] = items.Select(item => GameObjectFinder.GetPath(item.Target.GameObject)).ToArray()
                });
            job.totalItems = items.Count;
            BatchPersistence.UpsertJob(job);

            var run = new SimulateJobRegistry.Run
            {
                JobId = job.jobId,
                Skill = skill,
                Batch = itemsJson != null,
                PlayMode = EditorApplication.isPlaying,
                ItemsJson = itemsJson,
                TotalSteps = totalSteps
            };
            foreach (var item in items)
                run.Items.Add(new SimulateProgress { Item = item, Path = GameObjectFinder.GetPath(item.Target.GameObject), InBatch = run.Batch });
            SimulateJobRegistry.Runs[job.jobId] = run;

            var target = items[0].Target.GameObject;
            var accepted = new Dictionary<string, object>
            {
                ["success"] = true,
                ["status"] = "accepted",
                ["jobId"] = job.jobId,
                ["kind"] = SimulateJobKind,
                ["waitUrl"] = AsyncJobService.BuildWaitUrl(job.jobId),
                ["steps"] = totalSteps,
                ["undoable"] = false,
                ["warnings"] = items.SelectMany(item => item.Warnings).Distinct().ToArray(),
                ["note"] = "Poll GET /jobs/" + job.jobId + " (or job_status); when status is completed its resultData holds the result an inline call returns. job_cancel stops it where it is."
            };
            if (run.Batch)
            {
                accepted["totalItems"] = items.Count;
            }
            else
            {
                accepted["name"] = target.name;
                accepted["entityId"] = UnityObjectIdUtility.GetEntityId(target);
                accepted["instanceId"] = UnityObjectIdUtility.GetObjectId(target);
                accepted["path"] = GameObjectFinder.GetPath(target);
                accepted["systems"] = SystemIdentities(items[0].Systems);
            }
            return accepted;
        }

        /// <summary>
        /// One editor tick of a simulation job: as many steps as fit in <see cref="SimulateSliceMilliseconds"/> (at least one
        /// slice), checking before each slice that nobody changed the target since the last one.
        /// </summary>
        internal static void ProcessSimulateSlice(BatchJobRecord job)
        {
            if (!SimulateJobRegistry.Runs.TryGetValue(job.jobId, out var run))
            {
                var done = job.metadata != null && job.metadata.TryGetValue("doneSteps", out var value) ? value : 0;
                var total = job.metadata != null && job.metadata.TryGetValue("totalSteps", out var all) ? all : "?";
                AsyncJobService.FailJob(job.jobId, $"The simulation was interrupted after {done} of {total} step(s) (a domain reload or an Editor restart ends a simulation job) and is not resumed. " +
                    "Its systems keep the state they had then; read them with particle_get_info.", "interrupted");
                return;
            }

            if (EditorApplication.isPlaying != run.PlayMode || EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
            {
                FailSimulateRun(run, job, $"The Editor entered or left Play Mode while the simulation ran; it stopped after {run.DoneSteps} of {run.TotalSteps} step(s).");
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool sliced = false;
            try
            {
                while (run.Current < run.Items.Count)
                {
                    var progress = run.Items[run.Current];
                    var changed = ChangedSinceLastSlice(progress);
                    if (changed != null)
                    {
                        if (!run.Batch)
                        {
                            FailSimulateRun(run, job, changed);
                            return;
                        }
                        progress.Result = Stamp(Error(CodeSemantic, changed), progress.Path, false);
                        run.Current++;
                        continue;
                    }

                    if (!progress.Finished)
                    {
                        if (sliced && (SimulateJobRegistry.StepsPerSliceForTests.HasValue || watch.Elapsed.TotalMilliseconds >= SimulateSliceMilliseconds))
                            break;
                        AdvanceSimulation(progress, SimulateJobRegistry.StepsPerSliceForTests ?? StepsPerSlice(progress.Item));
                        sliced = true;
                        continue;
                    }

                    progress.Result = progress.InBatch ? Stamp(SimulateResult(progress), progress.Path, null) : SimulateResult(progress);
                    run.Current++;
                }
            }
            catch (Exception ex)
            {
                FailSimulateRun(run, job, $"Unity threw while simulating ({ex.GetType().Name}: {ex.Message}); the simulation stopped after {run.DoneSteps} of {run.TotalSteps} step(s).");
                return;
            }

            if (run.Current >= run.Items.Count)
            {
                CompleteSimulateRun(run, job);
                return;
            }

            job.status = "running";
            job.currentStage = "simulating";
            job.progressStage = "simulating";
            job.processedItems = run.Current;
            job.progress = run.TotalSteps <= 0 ? 0 : (int)Math.Min(99, run.DoneSteps * 100L / run.TotalSteps);
            job.metadata["doneSteps"] = run.DoneSteps;
            job.resultSummary = $"{run.DoneSteps}/{run.TotalSteps} steps simulated.";
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            BatchPersistence.UpsertJob(job);
        }

        /// <summary>
        /// Steps of one Simulate call inside a job slice: enough to amortize the call, few enough that a heavy system
        /// returns to the time check soon (about 50k particle-steps per call).
        /// </summary>
        private static int StepsPerSlice(PlaybackItem item) =>
            (int)Math.Max(1, Math.Min(item.Steps, 50000 / Math.Max(1, item.ParticleBudget)));

        /// <summary>Why a job cannot continue an item: the target is gone, or someone played, stopped or moved it since the last slice.</summary>
        private static string ChangedSinceLastSlice(SimulateProgress progress)
        {
            var system = progress.Item.Target.System;
            if (system == null)
                return $"'{progress.Path}' no longer has its ParticleSystem (destroyed or removed); the simulation stopped after {progress.Done} of {progress.Item.Steps} step(s).";
            if (!progress.Started)
                return null;
            if (!system.isPaused || Math.Abs(system.time - progress.LeftAtTime) > 1e-4f)
            {
                return $"'{GameObjectFinder.GetPath(system.gameObject)}' was changed while the simulation job ran (now {(system.isPlaying ? "playing" : system.isPaused ? "paused" : "stopped")} at time {F(system.time)}; " +
                    $"the job had left it paused at {F(progress.LeftAtTime)}), for example by the Editor's Particle Effect preview or another call; the simulation stopped after {progress.Done} of {progress.Item.Steps} step(s).";
            }
            return null;
        }

        private static void CompleteSimulateRun(SimulateJobRegistry.Run run, BatchJobRecord job)
        {
            SimulateJobRegistry.Runs.Remove(run.JobId);
            Dictionary<string, object> result;
            if (run.Batch)
            {
                int index = 0;
                result = PlaybackEnvelope(run.Skill, false, "completed", BatchExecutor.Execute<BatchParticleSimulateItem>(run.ItemsJson,
                    item => run.Items[index++].Result, item => item.Locator.Describe(), atomic: false)) as Dictionary<string, object>
                    ?? new Dictionary<string, object> { ["error"] = "The batch envelope could not be built." };
            }
            else
            {
                result = run.Items[0].Result;
            }
            result["jobId"] = run.JobId;
            job.processedItems = run.Items.Count;
            job.metadata["doneSteps"] = run.DoneSteps;
            int failed = run.Items.Count(progress => progress.Result != null && progress.Result.TryGetValue("error", out var error) && error != null);
            AsyncJobService.CompleteJob(run.JobId, failed == 0
                ? $"{run.DoneSteps}/{run.TotalSteps} steps simulated; the systems are paused."
                : $"{run.DoneSteps}/{run.TotalSteps} steps simulated; {failed} of {run.Items.Count} item(s) failed (see resultData.results).", result);
        }

        private static void FailSimulateRun(SimulateJobRegistry.Run run, BatchJobRecord job, string message)
        {
            SimulateJobRegistry.Runs.Remove(run.JobId);
            job.metadata["doneSteps"] = run.DoneSteps;
            var resultData = new Dictionary<string, object>
            {
                ["error"] = message,
                ["doneSteps"] = run.DoneSteps,
                ["totalSteps"] = run.TotalSteps,
                ["completedItems"] = run.Items.Take(run.Current).Select(progress => (object)progress.Result).ToArray(),
                ["systems"] = run.Items.Skip(run.Current).Take(1).SelectMany(progress => progress.Item.Systems).Where(system => system != null).Select(system => (object)new Dictionary<string, object>
                {
                    ["path"] = GameObjectFinder.GetPath(system.gameObject),
                    ["playback"] = LivePlayback(system)
                }).ToArray(),
                ["undoable"] = false
            };
            AsyncJobService.FailJob(run.JobId, message, "failed", resultData);
        }

        /// <summary>Drops a cancelled job's run and records where it stopped; its systems stay paused there.</summary>
        internal static void ForgetSimulateRun(BatchJobRecord job, string why)
        {
            if (job == null || !SimulateJobRegistry.Runs.TryGetValue(job.jobId, out var run))
                return;
            SimulateJobRegistry.Runs.Remove(job.jobId);
            job.resultData["doneSteps"] = run.DoneSteps;
            job.resultData["totalSteps"] = run.TotalSteps;
            job.resultData["note"] = $"Simulation {why} after {run.DoneSteps} of {run.TotalSteps} step(s); its systems stay paused where it stopped (particle_stop clears them).";
            job.metadata["doneSteps"] = run.DoneSteps;
        }

        /// <summary>Test cleanup: forgets every run and resets the slice seam.</summary>
        internal static void ResetSimulateJobsForTests()
        {
            SimulateJobRegistry.Runs.Clear();
            SimulateJobRegistry.StepsPerSliceForTests = null;
        }

        // ===================== dryRun =====================

        /// <summary>
        /// The dryRun of the playback skills: the same validation as execution, plus what each reached system would do.
        /// Nothing is played, simulated or emitted.
        /// </summary>
        internal static void PlanPlayback(string skill, JObject args, Func<JObject, string, PlanTarget> resolve, PlanOutcome outcome)
        {
            outcome.Semantic = true;
            var op = OpOfSkill(skill);
            bool batch = skill.EndsWith("_batch", StringComparison.Ordinal);
            var serializer = JsonSerializer.Create(SkillsCommon.JsonSettings);
            bool runAsync = ReadBool(args, "runAsync", false);

            if (!batch)
            {
                PlaybackRequest request;
                try
                {
                    request = PlanRequest(op, args, serializer);
                }
                catch (Exception)
                {
                    return; // A parameter of the wrong type: the router's own type check reports it.
                }
                request.RunAsync = runAsync;
                var plan = PlanPlaybackItem(op, request, args, "", resolve, outcome);
                if (plan != null)
                {
                    var totals = new Problems();
                    var mine = new Problems();
                    CheckPlaybackTotals(new[] { new KeyValuePair<Problems, PlaybackItem>(mine, plan) }, false, totals);
                    totals.AddRange(mine);
                    Report(totals, new List<string>(), outcome);
                    outcome.Extra["playback"] = PlaybackPreview(plan);
                }
                outcome.Steps.Add(new Dictionary<string, object>
                {
                    ["index"] = 1,
                    ["action"] = PlaybackStepText(op, plan),
                    ["target"] = plan != null ? GameObjectFinder.GetPath(plan.Target.GameObject) : "(target)"
                });
                return;
            }

            var problems = new Problems();
            var token = Codec.Normalize(args["items"], "items", problems);
            if (problems.Any)
            {
                foreach (var problem in problems.Items)
                    outcome.Error(problem.Path, problem.Message);
                return;
            }
            if (!(token is JArray items) || items.Count == 0)
                return; // MISSING_PARAM / the router's own items check owns an absent or empty list.
            if (items.Count > MaxBatchItems)
            {
                outcome.Error("items", $"items holds {items.Count} items; one call takes at most {MaxBatchItems}.");
                return;
            }

            var entries = new List<KeyValuePair<Problems, PlaybackItem>>();
            var entryIndexes = new List<int>();
            var itemErrors = new Dictionary<int, List<string>>();
            var previews = new List<Dictionary<string, object>>();
            for (int i = 0; i < items.Count; i++)
            {
                var prefix = Codec.Index("items", i);
                int errorsBefore = outcome.Errors.Count;
                var preview = new Dictionary<string, object> { ["index"] = i, ["target"] = prefix };
                previews.Add(preview);
                if (!(items[i] is JObject item))
                {
                    outcome.Error(prefix, $"{prefix} must be an object, got {Codec.Describe(items[i])}.");
                    itemErrors[i] = new List<string> { $"{prefix} must be an object, got {Codec.Describe(items[i])}." };
                    continue;
                }
                var keyProblems = new Problems();
                Codec.CheckKeys(item, prefix, keyProblems, PlaybackItemKeys.For(op), null);
                Report(keyProblems, new List<string>(), outcome);
                if (!keyProblems.Any)
                {
                    PlaybackRequest request;
                    try
                    {
                        request = PlanRequest(op, item, serializer);
                    }
                    catch (Exception ex)
                    {
                        outcome.Error(prefix, $"{prefix} does not match the item shape: {ex.Message}");
                        request = null;
                    }
                    if (request != null)
                    {
                        request.RunAsync = runAsync;
                        var plan = PlanPlaybackItem(op, request, item, prefix, resolve, outcome);
                        if (plan != null)
                        {
                            var mine = new Problems();
                            entries.Add(new KeyValuePair<Problems, PlaybackItem>(mine, plan));
                            entryIndexes.Add(i);
                            preview["target"] = GameObjectFinder.GetPath(plan.Target.GameObject);
                            preview["systems"] = plan.Systems.Select(system => GameObjectFinder.GetPath(system.gameObject)).ToArray();
                        }
                    }
                }
                itemErrors[i] = outcome.Errors.Skip(errorsBefore).Select(error => error.Value).ToList();
            }

            var callProblems = new Problems();
            CheckPlaybackTotals(entries, true, callProblems);
            for (int k = 0; k < entries.Count; k++)
            {
                foreach (var problem in entries[k].Key.Items)
                {
                    outcome.Error(problem.Path, problem.Message);
                    itemErrors[entryIndexes[k]].Add(problem.Message);
                }
            }
            Report(callProblems, new List<string>(), outcome);

            for (int i = 0; i < previews.Count; i++)
            {
                var errors = itemErrors.TryGetValue(i, out var list) ? list : new List<string>();
                previews[i]["valid"] = errors.Count == 0;
                previews[i]["errors"] = errors.ToArray();
            }
            outcome.Steps.Add(new Dictionary<string, object>
            {
                ["index"] = 1,
                ["action"] = PlaybackStepText(op, null) + " (batch: runs only when every item is valid, then item by item; not undoable)",
                ["target"] = $"{items.Count} items"
            });
            outcome.Extra["batchPreview"] = new Dictionary<string, object>
            {
                ["totalItems"] = items.Count,
                ["atomic"] = false,
                ["runsOnlyWhenAllValid"] = true,
                ["undoable"] = false,
                ["items"] = previews.Cast<object>().ToArray()
            };
            if (op == PlaybackOp.Simulate)
            {
                long work = entries.Sum(entry => entry.Value.Work);
                outcome.Extra["playback"] = new Dictionary<string, object>
                {
                    ["steps"] = entries.Sum(entry => entry.Value.Steps),
                    ["particleBudget"] = entries.Sum(entry => entry.Value.ParticleBudget),
                    ["estimatedWork"] = work,
                    ["maxInlineWork"] = MaxInlineSimulateWork,
                    ["runsAs"] = runAsync ? "job" : "inline"
                };
            }
        }

        /// <summary>A single call's or a batch item's parameters, through the batch item class the execution deserializes.</summary>
        private static PlaybackRequest PlanRequest(PlaybackOp op, JObject args, JsonSerializer serializer)
        {
            var request = new PlaybackRequest { Op = op };
            IPlaybackItem source;
            switch (op)
            {
                case PlaybackOp.Stop: source = args.ToObject<BatchParticleStopItem>(serializer); break;
                case PlaybackOp.Simulate: source = args.ToObject<BatchParticleSimulateItem>(serializer); break;
                case PlaybackOp.Emit: source = args.ToObject<BatchParticleEmitItem>(serializer); break;
                default: source = args.ToObject<BatchParticlePlaybackItem>(serializer); break;
            }
            source.Fill(request);
            return request;
        }

        private static bool ReadBool(JObject args, string key, bool fallback)
        {
            var token = args?[key];
            return token != null && token.Type == JTokenType.Boolean ? token.Value<bool>() : fallback;
        }

        /// <summary>Plans one call or item: resolves the target, validates it as execution would, and previews every reached system.</summary>
        private static PlaybackItem PlanPlaybackItem(PlaybackOp op, PlaybackRequest request, JObject args, string prefix,
            Func<JObject, string, PlanTarget> resolve, PlanOutcome outcome)
        {
            string Field(string name) => string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;
            var target = resolve(args, "");
            if (target.Kind == PlanTargetKind.None)
            {
                if (!string.IsNullOrEmpty(prefix))
                    outcome.Error(Field("target"), $"{prefix} needs a target: one of entityId, name, path or instanceId.");
                return null;
            }
            if (target.Kind == PlanTargetKind.Failed)
            {
                outcome.Error(string.IsNullOrEmpty(prefix) ? "gameObject" : Field("target"), ErrorText(target.Error));
                return null;
            }
            if (target.Kind == PlanTargetKind.Pending)
            {
                outcome.Warn($"{Field("target")} '{target.PendingPath}' will be created by an earlier step; the playback checks that need the live system were skipped.");
                return null;
            }

            var bindError = BindExisting(target.Live, out var binding);
            if (bindError != null)
            {
                outcome.Error(string.IsNullOrEmpty(prefix) ? "gameObject" : Field("target"), ErrorText(bindError));
                return null;
            }

            var problems = new Problems();
            var warnings = new List<string>();
            var item = PreparePlayback(request, binding, prefix, problems, warnings);
            Report(problems, warnings, outcome);
            if (problems.Any)
                return null;

            foreach (var system in item.Systems)
            {
                outcome.Modify.Add(new Dictionary<string, object>
                {
                    ["target"] = GameObjectFinder.GetPath(system.gameObject),
                    ["component"] = "ParticleSystem",
                    ["field"] = "playback",
                    ["before"] = LivePlayback(system),
                    ["after"] = ExpectedPlayback(item, system == item.Target.System),
                    ["undoable"] = false
                });
            }
            return item;
        }

        /// <summary>What a reached system will do, in words (a dryRun never runs it to find out).</summary>
        private static string ExpectedPlayback(PlaybackItem item, bool isTarget)
        {
            var request = item.Request;
            switch (request.Op)
            {
                case PlaybackOp.Play: return "playing";
                case PlaybackOp.Pause: return "paused at its current time";
                case PlaybackOp.Stop:
                    return item.StopBehavior == ParticleSystemStopBehavior.StopEmittingAndClear
                        ? "stopped, every particle removed"
                        : "stopped emitting; live particles finish their lifetime";
                case PlaybackOp.Simulate:
                    return $"{(request.Restart ? "restarted, then " : "")}simulated {item.Steps} step(s) of {F(item.StepSeconds)} s, then paused";
                default:
                    return isTarget ? $"+{request.Count} particle(s), at most main.maxParticles alive" : "unchanged";
            }
        }

        private static Dictionary<string, object> PlaybackPreview(PlaybackItem item)
        {
            var preview = new Dictionary<string, object>
            {
                ["action"] = OpName(item.Request.Op),
                ["withChildren"] = item.Request.Op != PlaybackOp.Emit && item.Request.WithChildren,
                ["systems"] = item.Systems.Select(system => GameObjectFinder.GetPath(system.gameObject)).ToArray(),
                ["undoable"] = false,
                ["tracksWorkflow"] = false
            };
            if (item.Request.Op == PlaybackOp.Simulate)
            {
                preview["steps"] = item.Steps;
                preview["stepSeconds"] = item.StepSeconds;
                preview["particleBudget"] = item.ParticleBudget;
                preview["estimatedWork"] = item.Work;
                preview["maxInlineWork"] = MaxInlineSimulateWork;
                preview["runsAs"] = item.Request.RunAsync ? "job" : "inline";
            }
            return preview;
        }

        private static string PlaybackStepText(PlaybackOp op, PlaybackItem item)
        {
            switch (op)
            {
                case PlaybackOp.Play: return "Play ParticleSystem" + (item != null && item.Request.WithChildren ? " with children" : "");
                case PlaybackOp.Pause: return "Pause ParticleSystem" + (item != null && item.Request.WithChildren ? " with children" : "");
                case PlaybackOp.Stop: return "Stop ParticleSystem" + (item != null ? $" ({item.StopBehavior})" : "");
                case PlaybackOp.Simulate: return "Simulate ParticleSystem" + (item != null ? $" for {item.Steps} step(s)" : "");
                default: return "Emit particles" + (item != null ? $" ({item.Request.Count})" : "");
            }
        }
#endif
    }
}

// Producer:Betsy
