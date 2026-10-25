using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// A change a restore would overwrite that is not one of its own steps. <see cref="id"/> is what the caller passes back in
    /// <c>conflicts</c> to let the restore overwrite it.
    /// </summary>
    internal sealed class RestoreConflict
    {
        public string id;
        public string target;
        public string by;
        public string what;
        public string at;
        // Only journal conflicts carry it: the change was logged, not inferred from a snapshot.
        [JsonProperty("confidence", NullValueHandling = NullValueHandling.Ignore)]
        public string confidence;

        [JsonIgnore] internal string TargetKey;
        [JsonIgnore] internal long Seq;
        [JsonIgnore] internal bool IsStep;
    }

    public static partial class WorkflowManager
    {
        private const int MaxListedSkippedItems = 20;
        private const int MaxConflictValueLength = 40;
        private const string AssetKeyPrefix = "asset:";
        private const string MetaSuffix = ".meta";
        private const string RestoreTag = "workflow_restore";
        private static readonly Regex ConflictIdPattern = new Regex("^k[0-9a-f]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private sealed class RestoreTarget
        {
            public string Key;
            public string Display;
        }

        private sealed class ConflictingStep
        {
            public WorkflowTask Task;
            public List<string> Keys;
        }

        private sealed class JournalHit
        {
            public string Key;
            public string What;
            public long Seq;
            public string TsUtc;
        }

        private sealed class RestoreConflictScan
        {
            public List<RestoreConflict> Conflicts = new List<RestoreConflict>();
            public string Coverage = "unknown";
        }

        /// <summary>
        /// Finds what a restore of <paramref name="steps"/> would overwrite: later steps of other agents (or of the caller, for an
        /// undo) that touched its targets, and journal entries after the restore's cursor that are not covered by a step. Each
        /// work gets the key of its target, so the conflicts can be applied per target.
        /// </summary>
        private static RestoreConflictScan ScanRestoreConflicts(List<WorkflowTask> steps, List<RestoreWork> works,
            Dictionary<string, string> gidAliases, long checkpointCursor, string callerAgent)
        {
            var scan = new RestoreConflictScan();
            _restoreScopeAliases = gidAliases;
            try
            {
                var targets = ResolveRestoreTargets(works);
                var stepTasks = FindConflictingSteps(steps, targets, callerAgent);
                foreach (var conflictingStep in stepTasks)
                {
                    foreach (string key in conflictingStep.Keys)
                    {
                        WorkflowTask task = conflictingStep.Task;
                        scan.Conflicts.Add(new RestoreConflict
                        {
                            id = ConflictId(key, "s" + task.seq),
                            target = targets[key],
                            by = NormalizeAgent(task.agent),
                            what = "s" + task.seq + " " + task.tag,
                            at = task.GetFormattedTime(),
                            TargetKey = key,
                            Seq = task.seq,
                            IsStep = true
                        });
                    }
                }

                long since = ConflictSince(steps, checkpointCursor);
                if (since > 0)
                {
                    var entries = EditorChangeTrackerService.ReadSince(since, out scan.Coverage);
                    AddJournalConflicts(scan, entries, targets, steps, stepTasks, callerAgent);
                }
                AddAssetConflicts(scan, targets, steps, stepTasks);
                scan.Conflicts = scan.Conflicts.OrderBy(conflict => conflict.Seq).ToList();
            }
            finally
            {
                _restoreScopeAliases = null;
                _sessionIdFallbackAllowed = true;
            }
            return scan;
        }

        /// <summary>The live target of every work, keyed; a work whose target does not resolve gets no key and takes no part in conflicts.</summary>
        private static Dictionary<string, string> ResolveRestoreTargets(List<RestoreWork> works)
        {
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var work in works)
            {
                RestoreTarget target = RestoreWorkTarget(work);
                work.TargetKey = target?.Key;
                if (target != null)
                    targets[target.Key] = target.Display;
            }
            return targets;
        }

        private static RestoreTarget RestoreWorkTarget(RestoreWork work)
        {
            var snapshot = work.Item.Snapshot;
            if (snapshot.type == SnapshotType.Setting && !string.IsNullOrEmpty(snapshot.settingKey))
                return new RestoreTarget { Key = "setting:" + snapshot.settingKey, Display = snapshot.settingKey };

            string assetPath = work.PathOverride ?? snapshot.assetPath;
            if (!string.IsNullOrEmpty(assetPath))
                return new RestoreTarget { Key = AssetKeyPrefix + assetPath, Display = assetPath };
            return SceneTargetOf(snapshot, CapturedIn(snapshot, work.Item.Step));
        }

        /// <summary>The live GameObject or component a scene snapshot names, or null when it does not resolve.</summary>
        private static RestoreTarget SceneTargetOf(ObjectSnapshot snapshot, string capturedIn)
        {
            _sessionIdFallbackAllowed = string.IsNullOrEmpty(capturedIn) || capturedIn == CurrentIdSession();
            switch (TryResolveObject(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId))
            {
                case GameObject go:
                    return SceneTarget(go, null);
                case Component component:
                    return SceneTarget(component.gameObject, component.GetType().Name);
                default:
                    return null;
            }
        }

        private static RestoreTarget SceneTarget(GameObject go, string componentType)
        {
            string key = SceneTargetKey(UnityObjectIdUtility.GetEntityId(go), componentType);
            string path = GameObjectFinder.GetPath(go);
            return new RestoreTarget { Key = key, Display = IsGameObjectLevel(componentType) ? path : path + "/" + componentType };
        }

        /// <summary>
        /// The key of a scene target: "go:&lt;entityId&gt;" for a GameObject (its Transform included, since the GameObject snapshot
        /// holds the transform), else "comp:&lt;entityId&gt;:&lt;component type&gt;".
        /// </summary>
        internal static string SceneTargetKey(string entityId, string componentType) =>
            IsGameObjectLevel(componentType) ? "go:" + entityId : "comp:" + entityId + ":" + componentType;

        private static bool IsGameObjectLevel(string componentType) =>
            componentType == null || componentType == "Transform" || componentType == "GameObject";

        private static string CapturedIn(ObjectSnapshot snapshot, WorkflowTask task) =>
            string.IsNullOrEmpty(snapshot.idSession) ? task.idSession : snapshot.idSession;

        /// <summary>
        /// The keys a snapshot of another step touches: its setting, its asset path (both paths of a move), or its live scene target.
        /// </summary>
        private static List<string> SnapshotTargetKeys(ObjectSnapshot snapshot, WorkflowTask task)
        {
            var keys = new List<string>();
            if (snapshot.type == SnapshotType.Setting && !string.IsNullOrEmpty(snapshot.settingKey))
            {
                keys.Add("setting:" + snapshot.settingKey);
            }
            else if (!string.IsNullOrEmpty(snapshot.assetPath))
            {
                keys.Add(AssetKeyPrefix + snapshot.assetPath);
                if (snapshot.type == SnapshotType.Moved && !string.IsNullOrEmpty(snapshot.previousAssetPath))
                    keys.Add(AssetKeyPrefix + snapshot.previousAssetPath);
            }
            else
            {
                RestoreTarget target = SceneTargetOf(snapshot, CapturedIn(snapshot, task));
                if (target != null)
                    keys.Add(target.Key);
            }
            return keys;
        }

        /// <summary>
        /// Steps after the earliest step of the restore, not part of it, that touched one of its targets. The caller's own earlier
        /// restores are not conflicts: they only moved the targets back and forth, and the restore takes them back as well.
        /// </summary>
        private static List<ConflictingStep> FindConflictingSteps(List<WorkflowTask> steps, Dictionary<string, string> targets, string callerAgent)
        {
            long earliest = steps.Min(step => step.seq);
            var inRestore = new HashSet<long>(steps.Select(step => step.seq));
            var found = new List<ConflictingStep>();
            foreach (var task in History.tasks.Where(candidate => candidate.seq > earliest && !inRestore.Contains(candidate.seq)).OrderBy(candidate => candidate.seq))
            {
                if (task.tag == RestoreTag && NormalizeAgent(task.agent) == NormalizeAgent(callerAgent))
                    continue;
                var keys = task.snapshots
                    .Where(snapshot => snapshot != null)
                    .SelectMany(snapshot => SnapshotTargetKeys(snapshot, task))
                    .Where(targets.ContainsKey)
                    .Distinct()
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToList();
                if (keys.Count > 0)
                    found.Add(new ConflictingStep { Task = task, Keys = keys });
            }
            return found;
        }

        /// <summary>The cursor the journal is read from: the checkpoint's when known, else the earliest step's start. 0 when neither is known.</summary>
        private static long ConflictSince(List<WorkflowTask> steps, long checkpointCursor)
        {
            if (checkpointCursor > 0)
                return checkpointCursor;
            return steps.Where(step => step.cursorStart > 0).Select(step => step.cursorStart).DefaultIfEmpty(0).Min();
        }

        /// <summary>
        /// Journal entries that changed a target from outside the restore's steps: the editor's (as "user") and other agents'
        /// REST changes. One conflict per target and source; its seq is the oldest entry's, its text describes the newest.
        /// </summary>
        private static void AddJournalConflicts(RestoreConflictScan scan, List<JObject> entries, Dictionary<string, string> targets,
            List<WorkflowTask> steps, List<ConflictingStep> stepTasks, string callerAgent)
        {
            // A step's own journal entries are covered by the step: the restore's steps, the steps reported as conflicts, and the
            // caller's own later restores, which FindConflictingSteps passes over.
            long earliest = steps.Min(step => step.seq);
            var ownRestores = History.tasks.Where(task => task.seq > earliest && task.tag == RestoreTag &&
                                                          NormalizeAgent(task.agent) == NormalizeAgent(callerAgent));
            var windows = steps.Concat(stepTasks.Select(conflicting => conflicting.Task)).Concat(ownRestores)
                .Where(task => task.cursorStart > 0 && task.cursorEnd > 0)
                .Select(task => (Start: task.cursorStart, End: task.cursorEnd))
                .ToList();
            var stepConflictKeys = new HashSet<(string Key, string By)>();
            foreach (var conflictingStep in stepTasks)
            {
                foreach (string key in conflictingStep.Keys)
                    stepConflictKeys.Add((key, NormalizeAgent(conflictingStep.Task.agent)));
            }

            var groups = new Dictionary<(string Key, string By), List<JournalHit>>();
            foreach (var entry in entries)
            {
                if (entry.Value<bool?>("isPlaying") != false)
                    continue;
                long seq = entry.Value<long?>("seq") ?? 0;
                if (windows.Any(window => seq >= window.Start && seq <= window.End))
                    continue;

                string source = entry.Value<string>("source");
                string by;
                if (source == "editor")
                {
                    by = "user";
                }
                else if (source == "rest")
                {
                    // A window two callers shared cannot be told apart; the other caller's tracked steps are step conflicts anyway.
                    string agent = entry.Value<string>("agent");
                    if (agent == EditorChangeTrackerService.AgentMixed)
                        continue;
                    by = NormalizeAgent(agent);
                    if (by == NormalizeAgent(callerAgent))
                        continue;
                }
                else
                {
                    continue;
                }

                foreach (JournalHit hit in JournalHits(entry))
                {
                    if (!targets.ContainsKey(hit.Key) || (source == "rest" && stepConflictKeys.Contains((hit.Key, by))))
                        continue;
                    hit.Seq = seq;
                    hit.TsUtc = entry.Value<string>("tsUtc");
                    if (!groups.TryGetValue((hit.Key, by), out var group))
                        groups.Add((hit.Key, by), group = new List<JournalHit>());
                    group.Add(hit);
                }
            }

            foreach (var group in groups)
            {
                JournalHit oldest = group.Value[0];
                JournalHit newest = group.Value[group.Value.Count - 1];
                string what = group.Value.Count > 1 ? $"{newest.What} (+{group.Value.Count - 1} more)" : newest.What;
                scan.Conflicts.Add(new RestoreConflict
                {
                    id = ConflictId(group.Key.Key, oldest.Seq.ToString(CultureInfo.InvariantCulture)),
                    target = targets[group.Key.Key],
                    by = group.Key.By,
                    what = what,
                    at = FormatLocalClock(newest.TsUtc),
                    confidence = "logged",
                    TargetKey = group.Key.Key,
                    Seq = oldest.Seq
                });
            }
        }

        private static IEnumerable<JournalHit> JournalHits(JObject entry)
        {
            var payload = entry.Value<JObject>("payload");
            if (payload == null)
                yield break;

            string type = entry.Value<string>("type");
            if (type == "scene_changes" && payload["changes"] is JArray changes)
            {
                foreach (var change in changes.OfType<JObject>())
                {
                    string key = JournalChangeKey(change);
                    if (key != null)
                        yield return new JournalHit { Key = key, What = JournalChangeWhat(change) };
                }
            }
            else if (type == "file_changes")
            {
                foreach (string path in PathsOf(payload["imported"]))
                    yield return new JournalHit { Key = JournalAssetKey(path), What = "file imported" };
                foreach (string path in PathsOf(payload["deleted"]))
                    yield return new JournalHit { Key = JournalAssetKey(path), What = "file deleted" };
                foreach (var move in (payload["moved"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    foreach (string path in new[] { move.Value<string>("from"), move.Value<string>("to") }.Where(value => !string.IsNullOrEmpty(value)))
                        yield return new JournalHit { Key = JournalAssetKey(path), What = "file moved" };
                }
            }
        }

        private static string JournalChangeKey(JObject change)
        {
            string assetPath = change.Value<string>("assetPath");
            if (!string.IsNullOrEmpty(assetPath))
                return AssetKeyPrefix + assetPath;

            string entityId = change.Value<string>("entityId");
            if (string.IsNullOrEmpty(entityId))
                return null;
            return change.Value<string>("kind") == "property_changed"
                ? SceneTargetKey(entityId, change.Value<string>("targetType"))
                : SceneTargetKey(entityId, null);
        }

        private static string JournalChangeWhat(JObject change)
        {
            if (change.Value<string>("kind") != "property_changed")
                return change.Value<string>("kind");
            return change.Value<string>("property") + " " + ShortenValue(change.Value<string>("previousValue")) +
                   "→" + ShortenValue(change.Value<string>("value"));
        }

        private static string ShortenValue(string value)
        {
            if (value == null)
                return string.Empty;
            return value.Length > MaxConflictValueLength ? value.Substring(0, MaxConflictValueLength) : value;
        }

        /// <summary>A file's journal path as the asset key: the .meta of an asset is the same asset.</summary>
        private static string JournalAssetKey(string path) =>
            AssetKeyPrefix + (path.EndsWith(MetaSuffix, StringComparison.Ordinal) ? path.Substring(0, path.Length - MetaSuffix.Length) : path);

        private static IEnumerable<string> PathsOf(JToken token) =>
            (token as JArray ?? new JArray()).Select(item => (string)item).Where(path => !string.IsNullOrEmpty(path));

        /// <summary>
        /// Asset targets are checked against the file's fingerprint, the one the newest snapshot took when its task ended. An
        /// unchanged file means the journal's import is the restore's own write or the task's, so its entries are not conflicts.
        /// A changed file alone is not reported: Unity saving an asset a step left dirty changes it too, and P2's state hashes
        /// are what can tell that from an external edit.
        /// </summary>
        private static void AddAssetConflicts(RestoreConflictScan scan, Dictionary<string, string> targets,
            List<WorkflowTask> steps, List<ConflictingStep> stepTasks)
        {
            var tasks = steps.Concat(stepTasks.Select(conflicting => conflicting.Task)).OrderByDescending(task => task.seq).ToList();
            foreach (string key in targets.Keys.Where(candidate => candidate.StartsWith(AssetKeyPrefix, StringComparison.Ordinal)).ToList())
            {
                var newest = NewestSnapshotOf(tasks, key);
                if (newest == null || string.IsNullOrEmpty(newest.assetFingerprint))
                    continue;

                if (newest.assetFingerprint == AssetFingerprint(key.Substring(AssetKeyPrefix.Length)))
                    scan.Conflicts.RemoveAll(conflict => conflict.TargetKey == key && !conflict.IsStep);
            }
        }

        private static ObjectSnapshot NewestSnapshotOf(List<WorkflowTask> tasks, string key)
        {
            foreach (var task in tasks)
            {
                for (int i = task.snapshots.Count - 1; i >= 0; i--)
                {
                    var snapshot = task.snapshots[i];
                    // A move's snapshot carries the file's fingerprint at its destination, so it only speaks for that path.
                    if (snapshot != null && !string.IsNullOrEmpty(snapshot.assetPath) && AssetKeyPrefix + snapshot.assetPath == key)
                        return snapshot;
                }
            }
            return null;
        }

        /// <summary>
        /// The id of a conflict: "k" plus the first six hex digits of SHA1 over the target key and the seed (the journal seq, or "s" and the
        /// step seq). The same change keeps its id between a preview and the restore.
        /// </summary>
        private static string ConflictId(string targetKey, string seed)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(targetKey + ":" + seed));
                return "k" + string.Concat(hash.Take(3).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static string FormatLocalClock(string utcIso) =>
            DateTimeOffset.TryParse(utcIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : string.Empty;

        /// <summary>
        /// The overwrite set of a <c>conflicts</c> argument: null when omitted (conflicts refuse the restore), empty for "keep", else
        /// the listed ids. Any other value is rejected.
        /// </summary>
        private static bool TryParseConflictsArgument(string conflicts, out HashSet<string> overwrite, out string error)
        {
            overwrite = null;
            error = null;
            if (conflicts == null)
                return true;

            var parsed = new HashSet<string>(StringComparer.Ordinal);
            string value = conflicts.Trim();
            if (!string.Equals(value, "keep", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string part in value.Split(','))
                {
                    string id = part.Trim().ToLowerInvariant();
                    if (!ConflictIdPattern.IsMatch(id))
                    {
                        error = $"conflicts must be omitted, \"keep\", or comma-separated conflict ids such as k1a2b3c; got \"{conflicts}\"";
                        return false;
                    }
                    parsed.Add(id);
                }
            }
            overwrite = parsed;
            return true;
        }

        /// <summary>
        /// The works that run under a conflict choice. A work runs when every conflict on its target is in the overwrite set; any other
        /// work is skipped and reported, one entry per target, capped at <see cref="MaxListedSkippedItems"/>.
        /// </summary>
        private static List<RestoreWork> KeepUnconflictedWorks(List<RestoreWork> works, List<RestoreConflict> conflicts,
            HashSet<string> overwrite, RestoreOutcome outcome)
        {
            var run = new List<RestoreWork>();
            foreach (var work in works)
            {
                var blocking = work.TargetKey == null
                    ? null
                    : conflicts.Where(conflict => conflict.TargetKey == work.TargetKey && !overwrite.Contains(conflict.id)).ToList();
                if (blocking == null || blocking.Count == 0)
                {
                    run.Add(work);
                    continue;
                }

                outcome.skipped++;
                string item = $"{blocking[0].target} (kept: {blocking[0].by} change {blocking[0].id})";
                if (outcome.skippedItems.Count < MaxListedSkippedItems && !outcome.skippedItems.Contains(item))
                    outcome.skippedItems.Add(item);
            }
            return run;
        }
    }
}

// Producer:Betsy
