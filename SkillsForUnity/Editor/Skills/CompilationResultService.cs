using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// Records the outcome of the most recent script compilation, so an AI client can still ask
    /// "did my last script edit compile" after the REST service recovers from the domain reload triggered by a successful compilation.
    ///
    /// Threading model: every CompilationPipeline event is dispatched on the main thread, and the
    /// only reader (SkillsHttpServer.ProcessJob) also runs on the main thread, so no locking is needed.
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

        static CompilationResultService()
        {
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

        private static void OnCompilationStarted(object context)
        {
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
    }
}

// Producer:Betsy
