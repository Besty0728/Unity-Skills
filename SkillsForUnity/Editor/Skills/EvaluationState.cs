using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Read side of the evaluation runner's state directory, Library/UnitySkills/eval (contract 4.1-4.9). The panel
    /// only reads these files: shared-read open, and an IOException or a parse failure is retried 3 times 50 ms apart,
    /// because the runner replaces them atomically while we read (contract 4.2). Identifiers read from one file are
    /// validated before they become path segments of another.
    /// </summary>
    internal static class EvaluationState
    {
        internal const int SupportedSchema = 1;
        private const int ReadRetries = 3;
        private const int ReadRetryDelayMs = 50;

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
