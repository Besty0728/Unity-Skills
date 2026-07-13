using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// Captures the result of the most recent script compilation so AI clients can ask
    /// "did my last script edit compile?" once the REST service recovers from the domain
    /// reload that a successful compile triggers.
    ///
    /// Threading: every CompilationPipeline event is dispatched on the main thread, and the
    /// only reader (SkillsHttpServer.ProcessJob) also runs on the main thread — no locking.
    ///
    /// Persistence: the finished result is stored via SessionState, which survives a domain
    /// reload and is cleared when the editor closes — exactly the lifetime we want. A static
    /// field mirrors it; after a reload that field is empty and is lazily restored on read.
    /// </summary>
    [InitializeOnLoad]
    public static class CompilationResultService
    {
        private const string SessionKey = "UnitySkills_LastCompilationResult";

        // Payload caps: keep the response bounded on pathological failures. The counts below
        // stay accurate (true totals); `truncated` flags when an array was actually clipped.
        private const int MaxErrors = 200;
        private const int MaxWarnings = 50;

        // In-flight accumulation for the current compile cycle (main thread only).
        private static DateTime _startedUtc;
        private static readonly List<CompileMessageEntry> _errors = new List<CompileMessageEntry>();
        private static readonly List<CompileMessageEntry> _warnings = new List<CompileMessageEntry>();

        // Cached JSON of the last finished result; null/empty = not loaded or none this session.
        private static string _cachedResultJson;

        static CompilationResultService()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        /// <summary>
        /// JSON of the last finished compilation result, or null if no compilation has
        /// completed in this editor session. Lazily restores from SessionState after a
        /// domain reload. Reusable by other endpoints (e.g. a future event channel).
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
                // CompilerMessageType.Info is intentionally ignored.
            }
        }

        private static void OnCompilationFinished(object context)
        {
            long durationMs = _startedUtc == default(DateTime)
                ? 0L
                : Math.Max(0L, (long)(DateTime.UtcNow - _startedUtc).TotalMilliseconds);

            // Lists are read synchronously by the serializer below, before the next cycle
            // clears them — so handing over the live lists (or a capped view) is safe.
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

            // Compact event payload: the first few errors give an agent the file:line it
            // needs; the full list stays behind GET /compile/status.
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

        /// <summary>One compiler diagnostic, flattened for the wire.</summary>
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
