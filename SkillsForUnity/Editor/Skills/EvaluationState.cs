using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// Read side of the evaluation runner's state directory, Library/UnitySkills/eval (contract 4.1-4.9). The panel
    /// only reads these files: shared-read open, and an IOException or a parse failure is retried 3 times 50 ms apart,
    /// because the runner replaces them atomically while we read (contract 4.2). Identifiers read from one file are
    /// validated before they become path segments of another. The one file the panel writes is its own All-tasks
    /// group record (panel_group.json), see the group section below.
    /// </summary>
    internal static class EvaluationState
    {
        internal const int SupportedSchema = 1;
        private const int ReadRetries = 3;
        private const int ReadRetryDelayMs = 50;

        internal static readonly string[] TrackIds = { "ops", "dev" };
        internal static readonly string[] TierIds = { "basic", "standard", "challenge" };

        private static readonly int CurrentPid = GetCurrentPid();
        private static readonly Regex SessionIdPattern = new Regex(@"^s\d{8}T\d{6}[0-9a-f]{6}$");
        private static readonly Regex AttemptIdPattern = new Regex(@"^[0-9a-f]{12}$");

        internal enum ReadStatus { Ok, Missing, Corrupt, Unsupported }

        internal readonly struct ReadResult
        {
            public readonly ReadStatus Status;
            public readonly JObject Value;
            public readonly string Path;

            public ReadResult(ReadStatus status, JObject value, string path)
            {
                Status = status;
                Value = value;
                Path = path;
            }
        }

        internal sealed class CurrentInfo
        {
            public string SessionId;
            public string Status;
            public string ActiveAttemptId;
        }

        internal sealed class TaskEntry
        {
            public string State;
        }

        internal sealed class SessionInfo
        {
            public string SessionId;
            public string Status;
            public string Mode;
            public string Lang;
            public string Track;
            public string Tier;
            public string CreatedAt;
            public string ReportJson;
            public string ReportMd;
            public List<string> Order = new List<string>();
            public Dictionary<string, TaskEntry> Tasks = new Dictionary<string, TaskEntry>(StringComparer.Ordinal);
        }

        internal sealed class AttemptInfo
        {
            public string AttemptId;
            public string TaskId;
            public string Phase;
            public int K;
            public string IssuedAt;
            public int? Minutes;
            public string AnswerPath;
        }

        internal sealed class LockInfo
        {
            public int? Pid;
            public string Command;
            public DateTime ModifiedUtc;
        }

        // ===== Layout (contract 4.1) =====

        internal static string StateDir(string projectRoot) => Path.Combine(projectRoot, "Library", "UnitySkills", "eval");
        internal static string PanelDir(string projectRoot) => Path.Combine(StateDir(projectRoot), "panel");
        internal static string CurrentFile(string projectRoot) => Path.Combine(StateDir(projectRoot), "current.json");
        internal static string LockFile(string projectRoot) => Path.Combine(StateDir(projectRoot), "lock");
        internal static string SessionsDir(string projectRoot) => Path.Combine(StateDir(projectRoot), "sessions");
        internal static string SessionDir(string projectRoot, string sessionId) => Path.Combine(SessionsDir(projectRoot), sessionId);
        internal static string SessionFile(string projectRoot, string sessionId) => Path.Combine(SessionDir(projectRoot, sessionId), "session.json");

        internal static string AttemptStateFile(string projectRoot, string sessionId, string attemptId) =>
            Path.Combine(SessionDir(projectRoot, sessionId), "attempts", attemptId, "state.json");

        internal static string SessionPromptFile(string projectRoot, string sessionId, string lang) =>
            Path.Combine(SessionDir(projectRoot, sessionId), "session-prompt." + lang + ".md");

        internal static string IoSessionPromptFile(string projectRoot, string sessionId) =>
            Path.Combine(projectRoot, "Library", "UnitySkillsEvalIO", sessionId, "session-prompt.md");

        internal static bool IsSessionId(string value) => value != null && SessionIdPattern.IsMatch(value);
        internal static bool IsAttemptId(string value) => value != null && AttemptIdPattern.IsMatch(value);

        // ===== Raw reads =====

        /// <summary>Parses a JSON object, keeping ISO timestamps as the strings they were written as.</summary>
        internal static JObject ParseObject(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text ?? string.Empty)) { DateParseHandling = DateParseHandling.None })
            {
                var token = JToken.ReadFrom(reader);
                if (token is JObject obj) return obj;
                throw new JsonReaderException("The file does not hold a JSON object.");
            }
        }

        /// <summary>
        /// Reads one JSON object with the contract's retry rule. A missing or non-int "schema" is Corrupt when
        /// <paramref name="requireSchema"/>; a schema above 1 is Unsupported either way.
        /// </summary>
        internal static ReadResult Read(string path, bool requireSchema = true)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return new ReadResult(ReadStatus.Missing, null, path);
                    string text;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
                        text = reader.ReadToEnd();
                    var value = ParseObject(text);
                    var schema = value["schema"];
                    if (schema == null || schema.Type != JTokenType.Integer)
                        return new ReadResult(requireSchema ? ReadStatus.Corrupt : ReadStatus.Ok, value, path);
                    return new ReadResult(schema.Value<long>() > SupportedSchema ? ReadStatus.Unsupported : ReadStatus.Ok, value, path);
                }
                catch (FileNotFoundException) { return new ReadResult(ReadStatus.Missing, null, path); }
                catch (DirectoryNotFoundException) { return new ReadResult(ReadStatus.Missing, null, path); }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
                {
                    if (attempt >= ReadRetries) return new ReadResult(ReadStatus.Corrupt, null, path);
                    Thread.Sleep(ReadRetryDelayMs);
                }
            }
        }

        // ===== Typed views =====

        internal static CurrentInfo ReadCurrent(string projectRoot, out ReadResult raw)
        {
            raw = Read(CurrentFile(projectRoot));
            if (raw.Status != ReadStatus.Ok) return null;
            var sessionId = raw.Value.Value<string>("sessionId");
            if (!IsSessionId(sessionId))
            {
                raw = new ReadResult(ReadStatus.Corrupt, raw.Value, raw.Path);
                return null;
            }
            var activeAttempt = raw.Value.Value<string>("activeAttemptId");
            return new CurrentInfo
            {
                SessionId = sessionId,
                Status = raw.Value.Value<string>("status"),
                ActiveAttemptId = IsAttemptId(activeAttempt) ? activeAttempt : null,
            };
        }

        internal static SessionInfo ReadSession(string projectRoot, string sessionId, out ReadResult raw)
        {
            if (!IsSessionId(sessionId))
            {
                raw = new ReadResult(ReadStatus.Missing, null, null);
                return null;
            }
            raw = Read(SessionFile(projectRoot, sessionId));
            if (raw.Status != ReadStatus.Ok) return null;
            var value = raw.Value;
            var selection = value["selection"] as JObject;
            var report = value["report"] as JObject;
            var info = new SessionInfo
            {
                SessionId = sessionId,
                Status = value.Value<string>("status"),
                Mode = value.Value<string>("mode"),
                Lang = value.Value<string>("lang"),
                Track = selection?.Value<string>("track"),
                Tier = selection?.Value<string>("tier"),
                CreatedAt = value.Value<string>("createdAt"),
                ReportJson = report?.Value<string>("json"),
                ReportMd = report?.Value<string>("md"),
            };
            if (value["order"] is JArray order)
            {
                foreach (var item in order)
                    if (item.Type == JTokenType.String) info.Order.Add(item.Value<string>());
            }
            if (value["tasks"] is JObject tasks)
            {
                foreach (var property in tasks.Properties())
                {
                    if (property.Value is JObject task)
                        info.Tasks[property.Name] = new TaskEntry { State = task.Value<string>("state") };
                }
            }
            return info;
        }

        internal static AttemptInfo ReadAttempt(string projectRoot, string sessionId, string attemptId, out ReadResult raw)
        {
            if (!IsSessionId(sessionId) || !IsAttemptId(attemptId))
            {
                raw = new ReadResult(ReadStatus.Missing, null, null);
                return null;
            }
            raw = Read(AttemptStateFile(projectRoot, sessionId, attemptId));
            if (raw.Status != ReadStatus.Ok) return null;
            var value = raw.Value;
            var minutes = value["minutes"];
            return new AttemptInfo
            {
                AttemptId = attemptId,
                TaskId = value.Value<string>("taskId"),
                Phase = value.Value<string>("phase"),
                K = value["k"]?.Type == JTokenType.Integer ? value.Value<int>("k") : 0,
                IssuedAt = value.Value<string>("issuedAt"),
                Minutes = minutes != null && (minutes.Type == JTokenType.Integer || minutes.Type == JTokenType.Float)
                    ? (int?)Math.Round(minutes.Value<double>())
                    : null,
                AnswerPath = (value["answer"] as JObject)?.Value<string>("path"),
            };
        }

        /// <summary>The session's report.json, or null while it has none. Paths in session.report are relative to the session dir.</summary>
        internal static ReadResult ReadReport(string projectRoot, SessionInfo session)
        {
            if (session == null || string.IsNullOrEmpty(session.ReportJson) || Path.IsPathRooted(session.ReportJson) ||
                session.ReportJson.Contains(".."))
                return new ReadResult(ReadStatus.Missing, null, null);
            return Read(Path.Combine(SessionDir(projectRoot, session.SessionId), session.ReportJson));
        }

        internal static string ReportMarkdownPath(string projectRoot, SessionInfo session)
        {
            if (session == null || string.IsNullOrEmpty(session.ReportMd) || Path.IsPathRooted(session.ReportMd) ||
                session.ReportMd.Contains(".."))
                return null;
            return Path.Combine(SessionDir(projectRoot, session.SessionId), session.ReportMd);
        }

        /// <summary>Readable sessions, newest first by id (ids start with their UTC creation time).</summary>
        internal static List<SessionInfo> ListSessions(string projectRoot, int max)
        {
            var ids = new List<string>();
            try
            {
                var dir = SessionsDir(projectRoot);
                if (Directory.Exists(dir))
                {
                    foreach (var path in Directory.GetDirectories(dir))
                    {
                        var name = Path.GetFileName(path);
                        if (IsSessionId(name)) ids.Add(name);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<SessionInfo>();
            }

            ids.Sort(StringComparer.Ordinal);
            ids.Reverse();
            var sessions = new List<SessionInfo>();
            foreach (var id in ids)
            {
                if (sessions.Count >= max) break;
                var session = ReadSession(projectRoot, id, out _);
                if (session != null) sessions.Add(session);
            }
            return sessions;
        }

        /// <summary>The runner lock (contract 2.6), or null when no lock file exists. An unreadable lock has no pid.</summary>
        internal static LockInfo ReadLock(string projectRoot)
        {
            var path = LockFile(projectRoot);
            DateTime modified;
            try
            {
                if (!File.Exists(path)) return null;
                modified = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            var raw = Read(path, requireSchema: false);
            if (raw.Status == ReadStatus.Missing) return null;
            var pid = raw.Value?["pid"];
            return new LockInfo
            {
                Pid = pid != null && pid.Type == JTokenType.Integer ? (int?)pid.Value<int>() : null,
                Command = raw.Value?.Value<string>("cmd"),
                ModifiedUtc = modified,
            };
        }

        // ===== Grades (the runner uses the same thresholds for report.grade) =====

        /// <summary>
        /// The overall grade of pass / (pass + partial + fail), or null when nothing was gradable. Integer comparisons
        /// keep the 0.9, 0.7 and 0.5 boundaries exact.
        /// </summary>
        internal static string GradeFor(int pass, int partial, int fail)
        {
            int gradable = pass + partial + fail;
            if (gradable <= 0) return null;
            if (pass * 10 >= gradable * 9) return "excellent";
            if (pass * 10 >= gradable * 7) return "good";
            if (pass * 10 >= gradable * 5) return "pass";
            return "needs_work";
        }

        /// <summary>Distinct model names the tasks recorded (usage.model.values), in first-seen order.</summary>
        internal static List<string> ModelNames(IEnumerable<JObject> tasks)
        {
            var names = new List<string>();
            if (tasks == null) return names;
            foreach (var task in tasks)
            {
                if (!(task?["usage"]?["model"]?["values"] is JArray values)) continue;
                foreach (var item in values)
                {
                    if (item.Type != JTokenType.String) continue;
                    var name = item.Value<string>();
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
                }
            }
            return names;
        }

        // ===== Task pack (Evaluation~/pack/manifest.json, shipped with the package) =====

        /// <summary>The pack manifest, or null when the package or the file is not there.</summary>
        internal static JObject ReadPackManifest()
        {
            if (!PackageRootLocator.TryGet(out var root)) return null;
            var raw = Read(Path.Combine(root, "Evaluation~", "pack", "manifest.json"));
            return raw.Status == ReadStatus.Ok ? raw.Value : null;
        }

        /// <summary>The task ids a track and tier hold in the manifest, in pack order.</summary>
        internal static List<string> ManifestTaskIds(JObject manifest, string track, string tier)
        {
            var ids = new List<string>();
            if (manifest?["tracks"]?[track]?[tier] is JArray list)
            {
                foreach (var item in list)
                    if (item.Type == JTokenType.String) ids.Add(item.Value<string>());
            }
            return ids;
        }

        // ===== All tasks: a panel group of six sessions the agent runs one after another =====

        internal const string PanelGroupFileName = "panel_group.json";

        /// <summary>Written by the panel for an All-tasks run; the sessions it covers are found by track, tier and id time.</summary>
        internal sealed class GroupRecord
        {
            public DateTime StartedUtc;
            public string Lang;
            public List<GroupCombo> Combos = new List<GroupCombo>();
        }

        internal sealed class GroupCombo
        {
            public string Track;
            public string Tier;
        }

        /// <summary>One task of the group. State comes from its session, Status and Row from its report once written.</summary>
        internal sealed class GroupTask
        {
            public string Track;
            public string Tier;
            public string TaskId;
            public string State;
            public string Status;
            public double? Completion;
            public JObject Row;
        }

        internal sealed class GroupComboProgress
        {
            public GroupCombo Combo;
            public SessionInfo Session;
            public JObject Report;
        }

        internal sealed class GroupProgress
        {
            public readonly List<GroupComboProgress> Combos = new List<GroupComboProgress>();
            public readonly List<GroupTask> Tasks = new List<GroupTask>();

            public bool Complete => Combos.Count > 0 && Combos.TrueForAll(combo => combo.Report != null);

            public int FinishedSessions
            {
                get
                {
                    int count = 0;
                    foreach (var combo in Combos)
                        if (combo.Report != null) count++;
                    return count;
                }
            }

            public int FinishedTasks
            {
                get
                {
                    int count = 0;
                    foreach (var task in Tasks)
                        if (IsFinished(task)) count++;
                    return count;
                }
            }

            private static bool IsFinished(GroupTask task) =>
                task.Status != null || task.State == "done" || task.State == "invalid" || task.State == "cancelled";
        }

        /// <summary>Totals of a finished group, counted over its task rows; Grade follows <see cref="GradeFor"/>.</summary>
        internal sealed class GroupResult
        {
            public int Tasks;
            public int Pass;
            public int Partial;
            public int Fail;
            public int Other;
            public double? MeanCompletion;
            public string Grade;
            public List<string> Models = new List<string>();
        }

        /// <summary>
        /// The All-tasks combos a render pipeline can run, in the order of the scope: a tier qualifies when every task in it
        /// accepts the pipeline (the runner refuses a start otherwise).
        /// </summary>
        internal static List<GroupCombo> AllCombos(JObject manifest, string pipeline)
        {
            var combos = new List<GroupCombo>();
            foreach (var track in TrackIds)
            {
                foreach (var tier in TierIds)
                {
                    var ids = ManifestTaskIds(manifest, track, tier);
                    if (ids.Count == 0 || !ids.TrueForAll(id => PipelineAccepts(manifest, id, pipeline))) continue;
                    combos.Add(new GroupCombo { Track = track, Tier = tier });
                }
            }
            return combos;
        }

        /// <summary>
        /// Whether a task runs on a pipeline (manifest tasks[].requires.pipeline): a name, a list of names, null for any.
        /// An absent value takes URP, as the pack does.
        /// </summary>
        internal static bool PipelineAccepts(JObject manifest, string taskId, string pipeline)
        {
            var requires = manifest?["tasks"]?[taskId]?["requires"] as JObject;
            if (requires == null || !requires.ContainsKey("pipeline")) return pipeline == "URP";
            var value = requires["pipeline"];
            if (value == null || value.Type == JTokenType.Null) return true;
            if (value is JArray names)
            {
                foreach (var name in names)
                {
                    if (name.Type == JTokenType.String && name.Value<string>() == pipeline) return true;
                }
                return false;
            }
            return value.Type == JTokenType.String && value.Value<string>() == pipeline;
        }

        /// <summary>
        /// Agent protocol text, not UI copy: it is English like the command lines it carries, and <c>{0}</c> takes
        /// the command block. Keep it ASCII so the panel can show it in the bundled font.
        /// </summary>
        internal const string GroupPromptTemplate =
            "UnitySkills evaluation for all tracks and tiers. Run the sessions one at a time, in the order below.\n" +
            "For each command: 1) run it exactly as written; 2) it prints a session prompt, so follow that prompt " +
            "until the session's report is written; 3) only then run the next command. If a command reports " +
            "SESSION_ACTIVE, finish that session first. Do not change the commands.\n\n" +
            "Commands:\n{0}";

        /// <summary>The agent-facing prompt: one start command per combo, the same python and runner the panel uses.</summary>
        internal static string BuildGroupPrompt(string python, string runner, string projectRoot, string lang, IList<GroupCombo> combos)
        {
            var lines = new List<string>();
            foreach (var combo in combos)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\" -B \"{1}\" start --track {2} --tier {3} --mode suite --lang {4} --project \"{5}\"",
                    python, runner, combo.Track, combo.Tier, lang, projectRoot));
            }
            return string.Format(CultureInfo.InvariantCulture, GroupPromptTemplate, string.Join("\n", lines));
        }

        internal static string PanelGroupFile(string projectRoot) => Path.Combine(StateDir(projectRoot), PanelGroupFileName);

        /// <summary>The group record, or null when none exists or it cannot be read (a corrupt record is simply dropped).</summary>
        internal static GroupRecord ReadPanelGroup(string projectRoot)
        {
            var raw = Read(PanelGroupFile(projectRoot));
            if (raw.Status != ReadStatus.Ok) return null;
            var started = ParseIso(raw.Value.Value<string>("startedUtc"));
            if (!started.HasValue) return null;

            var record = new GroupRecord { StartedUtc = started.Value, Lang = raw.Value.Value<string>("lang") };
            if (raw.Value["combos"] is JArray combos)
            {
                foreach (var item in combos)
                {
                    var track = (item as JObject)?.Value<string>("track");
                    var tier = (item as JObject)?.Value<string>("tier");
                    if (track != null && tier != null) record.Combos.Add(new GroupCombo { Track = track, Tier = tier });
                }
            }
            return record.Combos.Count > 0 ? record : null;
        }

        internal static void WritePanelGroup(string projectRoot, GroupRecord record)
        {
            var combos = new JArray();
            foreach (var combo in record.Combos) combos.Add(new JObject { ["track"] = combo.Track, ["tier"] = combo.Tier });
            var json = new JObject
            {
                ["schema"] = SupportedSchema,
                ["startedUtc"] = FormatIso(record.StartedUtc),
                ["lang"] = record.Lang,
                ["combos"] = combos,
            };
            LibraryStateFile.WriteAllText(PanelGroupFile(projectRoot), json.ToString(Formatting.Indented));
        }

        /// <summary>Removes the group record; a file that cannot be deleted is left for the next read to ignore or replace.</summary>
        internal static void DeletePanelGroup(string projectRoot)
        {
            try
            {
                var path = PanelGroupFile(projectRoot);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SkillsLogger.LogVerbose($"Could not delete the evaluation panel group record: {ex.Message}");
            }
        }

        /// <summary>
        /// Maps the group onto the sessions. A combo takes the newest session of its track and tier whose id time is
        /// not older than the group's start (to the second, the id's precision). Before a combo has a session, its
        /// tasks come from the manifest. <paramref name="reportOf"/> returns a session's report.json, or null.
        /// </summary>
        internal static GroupProgress MergeGroup(GroupRecord group, IList<SessionInfo> sessions,
            Func<SessionInfo, JObject> reportOf, JObject manifest)
        {
            var progress = new GroupProgress();
            var since = new DateTime(group.StartedUtc.Ticks - group.StartedUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            foreach (var combo in group.Combos)
            {
                SessionInfo session = null;
                foreach (var candidate in sessions)
                {
                    if (!IsSessionId(candidate.SessionId) || candidate.Track != combo.Track || candidate.Tier != combo.Tier) continue;
                    if (SessionStartedUtc(candidate.SessionId) < since) continue;
                    if (session == null || string.CompareOrdinal(candidate.SessionId, session.SessionId) > 0) session = candidate;
                }

                var report = session != null ? reportOf(session) : null;
                progress.Combos.Add(new GroupComboProgress { Combo = combo, Session = session, Report = report });

                var ids = session != null && session.Order.Count > 0
                    ? session.Order
                    : ManifestTaskIds(manifest, combo.Track, combo.Tier);
                var rows = new Dictionary<string, JObject>(StringComparer.Ordinal);
                if (report?["tasks"] is JArray reportTasks)
                {
                    foreach (var row in reportTasks)
                    {
                        if (row is JObject task && task.Value<string>("taskId") is string taskId) rows[taskId] = task;
                    }
                }
                foreach (var id in ids)
                {
                    var task = new GroupTask { Track = combo.Track, Tier = combo.Tier, TaskId = id };
                    if (session != null && session.Tasks.TryGetValue(id, out var entry)) task.State = entry.State;
                    if (rows.TryGetValue(id, out var row))
                    {
                        task.Row = row;
                        task.Status = row.Value<string>("status");
                        task.Completion = NumberOf(row["completion"]);
                    }
                    progress.Tasks.Add(task);
                }
            }
            return progress;
        }

        /// <summary>Counts and grade of a finished group, over its task rows (pass, partial, fail and the rest).</summary>
        internal static GroupResult SummarizeGroup(GroupProgress progress)
        {
            var result = new GroupResult();
            double completionSum = 0;
            int completionCount = 0;
            var rows = new List<JObject>();
            foreach (var task in progress.Tasks)
            {
                result.Tasks++;
                switch (task.Status)
                {
                    case "pass": result.Pass++; break;
                    case "partial": result.Partial++; break;
                    case "fail": result.Fail++; break;
                    case null: break;
                    default: result.Other++; break;
                }
                if (task.Status == "pass" || task.Status == "partial" || task.Status == "fail")
                {
                    if (task.Completion.HasValue)
                    {
                        completionSum += task.Completion.Value;
                        completionCount++;
                    }
                }
                if (task.Row != null) rows.Add(task.Row);
            }
            result.MeanCompletion = completionCount > 0 ? completionSum / completionCount : (double?)null;
            result.Grade = GradeFor(result.Pass, result.Partial, result.Fail);
            result.Models = ModelNames(rows);
            return result;
        }

        private static DateTime SessionStartedUtc(string sessionId) =>
            DateTime.ParseExact(sessionId.Substring(1, 15), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        private static double? NumberOf(JToken token)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return null;
            return token.Value<double>();
        }

        // ===== Timestamps (contract 0: UTC, millisecond precision, Z suffix) =====

        internal static string FormatIso(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        internal static DateTime? ParseIso(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : (DateTime?)null;
        }

        private static int GetCurrentPid()
        {
            using (var process = System.Diagnostics.Process.GetCurrentProcess()) return process.Id;
        }

        /// <summary>Writes UTF-8 without BOM through a temp file and an atomic swap, so readers never see half a file.</summary>
        internal static void WriteTextAtomic(string path, string text)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp-" + CurrentPid + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            File.WriteAllText(tmp, text, SkillsCommon.Utf8NoBom);
            try
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, null);
                        return;
                    }
                    catch (FileNotFoundException)
                    {
                        // The destination vanished between the check and the swap; a plain move still lands the file.
                    }
                }
                File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception) { /* best effort */ }
                throw;
            }
        }
    }
}

// Producer:Betsy
