using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Records the outcome of the most recent script compilation, so an AI client can still ask
    /// "did my last script edit compile" after the REST service recovers from the domain reload triggered by a successful compilation.
    ///
    /// Threading model: every CompilationPipeline event is dispatched on the main thread, and the
    /// readers (SkillsHttpServer.ProcessJob, EventChannelService, AsyncJobService's compile jobs) also run on the main thread, so no locking is needed.
    ///
    /// Persistence: the completed result is stored in SessionState -- it survives domain reloads
    /// and is cleared on editor shutdown, which is exactly the lifetime we want. A static field mirrors it; after a reload that field is empty and gets lazily restored on read.
    /// </summary>
    [InitializeOnLoad]
    public static class CompilationResultService
    {
        private const string SessionKey = "UnitySkills_LastCompilationResult";

        // Payload cap: bounds the response body when an unusually large number of errors occurs.
        // The count fields still reflect the true total; the `truncated` flag signals when the arrays were actually cut.
        private const int MaxErrors = 200;
        private const int MaxWarnings = 50;

        // Accumulated intermediate state for the current compilation cycle (main-thread access only).
        private static DateTime _startedUtc;
        private static readonly List<CompileMessageEntry> _errors = new List<CompileMessageEntry>();
        private static readonly List<CompileMessageEntry> _warnings = new List<CompileMessageEntry>();

        // JSON cache of the last completed result; null/empty means not yet loaded or no compilation has finished this session.
        private static string _cachedResultJson;

        /// <summary>Bumped on every compilationStarted: compare it before and after a call to learn whether the call started a compilation.</summary>
        internal static int CompilationStartCount { get; private set; }

        /// <summary>When the current script domain was loaded. A compilation that finished after this is still waiting for its domain reload.</summary>
        internal static long DomainLoadedUtcTicks { get; private set; }

        internal static string LastResultJsonOverrideForTests;

        private static string _outcomeJson;
        private static CompilationOutcome _outcome;

        static CompilationResultService()
        {
            DomainLoadedUtcTicks = DateTime.UtcNow.Ticks;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        /// <summary>
        /// JSON of the last completed compilation result; returns null if no compilation has finished during this editor session. Restored lazily from SessionState after a
        /// domain reload. Other endpoints (e.g. a future event channel) may reuse it too.
        /// </summary>
        public static string GetLastCompilationJson()
        {
            if (string.IsNullOrEmpty(_cachedResultJson))
            {
                var restored = SessionState.GetString(SessionKey, string.Empty);
                if (!string.IsNullOrEmpty(restored))
                    _cachedResultJson = restored;
            }
            return string.IsNullOrEmpty(_cachedResultJson) ? null : _cachedResultJson;
        }

        /// <summary>The last completed compilation, parsed; null when none finished this editor session.</summary>
        internal static CompilationOutcome GetLastOutcome()
        {
            string json = LastResultJsonOverrideForTests ?? GetLastCompilationJson();
            if (string.IsNullOrEmpty(json))
                return null;
            // Compile jobs ask on every editor tick while they wait; parse each stored result once.
            if (json == _outcomeJson)
                return _outcome;

            _outcomeJson = json;
            _outcome = ParseOutcome(json);
            return _outcome;
        }

        private static CompilationOutcome ParseOutcome(string json)
        {
            try
            {
                // DateParseHandling.None keeps finishedAtUtc as the ISO-8601 string it was written as.
                JObject parsed;
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                    parsed = JObject.Load(reader);

                string finishedAt = parsed.Value<string>("finishedAtUtc");
                if (!DateTime.TryParse(finishedAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var finished))
                    return null;

                var outcome = new CompilationOutcome
                {
                    FinishedAtUtc = finishedAt,
                    FinishedAtUtcTicks = finished.Ticks,
                    ErrorCount = parsed.Value<int?>("errorCount") ?? 0,
                    Success = parsed.Value<bool?>("success") ?? true
                };
                if (parsed["errors"] is JArray errors)
                {
                    foreach (var error in errors.OfType<JObject>())
                    {
                        outcome.Errors.Add(new CompilationOutcome.Error
                        {
                            File = error.Value<string>("file"),
                            Line = error.Value<int?>("line") ?? 0,
                            Message = error.Value<string>("message")
                        });
                    }
                }
                return outcome;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static void RecordCompilationStartForTests() => CompilationStartCount++;

        private static void OnCompilationStarted(object context)
        {
            CompilationStartCount++;
            _startedUtc = DateTime.UtcNow;
            _errors.Clear();
            _warnings.Clear();
            SkillsLogger.LogVerbose("Compilation started - capturing result...");
            EventChannelService.Publish("compilation_started", new
            {
                startedAtUtc = _startedUtc.ToString("o"),
            });
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
                return;

            string assembly = Path.GetFileNameWithoutExtension(assemblyPath);
            foreach (var m in messages)
            {
                if (m.type == CompilerMessageType.Error)
                    _errors.Add(new CompileMessageEntry(m, assembly));
                else if (m.type == CompilerMessageType.Warning)
                    _warnings.Add(new CompileMessageEntry(m, assembly));
                // CompilerMessageType.Info is deliberately ignored.
            }
        }

        private static void OnCompilationFinished(object context)
        {
            long durationMs = _startedUtc == default(DateTime)
                ? 0L
                : Math.Max(0L, (long)(DateTime.UtcNow - _startedUtc).TotalMilliseconds);

            // The serialization below is a synchronous read that happens before these two lists are cleared
            // for the next compilation cycle, so handing out the live lists (or a truncated view of them) is safe.
            var errors = _errors.Count > MaxErrors ? _errors.GetRange(0, MaxErrors) : _errors;
            var warnings = _warnings.Count > MaxWarnings ? _warnings.GetRange(0, MaxWarnings) : _warnings;

            var result = new
            {
                finishedAtUtc = DateTime.UtcNow.ToString("o"),
                durationMs,
                success = _errors.Count == 0,
                errorCount = _errors.Count,
                warningCount = _warnings.Count,
                errors,
                warnings,
                truncated = _errors.Count > MaxErrors || _warnings.Count > MaxWarnings
            };

            _cachedResultJson = JsonConvert.SerializeObject(result, SkillsCommon.JsonSettings);
            SessionState.SetString(SessionKey, _cachedResultJson);

            SkillsLogger.LogVerbose(
                $"Compilation finished - success={result.success}, errors={result.errorCount}, " +
                $"warnings={result.warningCount}, {durationMs}ms");

            // Trimmed event payload: the first few errors are enough for the agent to get
            // file:line; the full list is left to GET /compile/status.
            var firstErrors = new List<object>(Math.Min(5, _errors.Count));
            for (int i = 0; i < _errors.Count && i < 5; i++)
                firstErrors.Add(new { _errors[i].file, _errors[i].line, _errors[i].message });

            EventChannelService.Publish("compilation_finished", new
            {
                success = result.success,
                errorCount = result.errorCount,
                warningCount = result.warningCount,
                durationMs,
                firstErrors,
            });
        }

        /// <summary>A single compiler diagnostic, flattened into a transport-friendly shape.</summary>
        private sealed class CompileMessageEntry
        {
            public string file;
            public int line;
            public int column;
            public string message;
            public string assembly;

            public CompileMessageEntry(CompilerMessage m, string assembly)
            {
                file = m.file;
                line = m.line;
                column = m.column;
                message = m.message;
                this.assembly = assembly;
            }
        }

        internal sealed class CompilationOutcome
        {
            internal sealed class Error
            {
                public string File;
                public int Line;
                public string Message;
            }

            public string FinishedAtUtc;
            public long FinishedAtUtcTicks;
            public int ErrorCount;
            public bool Success;
            public readonly List<Error> Errors = new List<Error>();

            public bool HasErrors => ErrorCount > 0 || !Success;
        }
    }
}

// Producer:Betsy
