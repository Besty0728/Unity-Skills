using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// 记录最近一次脚本编译的结果，使 AI 客户端在 REST 服务从编译成功引发的域重载中恢复后，
    /// 仍能追问"我上次改的脚本编译过了吗"。
    ///
    /// 线程模型：CompilationPipeline 的所有事件都派发在主线程，唯一的读取方
    /// （SkillsHttpServer.ProcessJob）也在主线程，因此无需加锁。
    ///
    /// 持久化：完成的结果存进 SessionState——它能跨域重载存活、编辑器关闭时清空，
    /// 正是我们要的生命周期。另有静态字段作镜像；重载后该字段为空，读取时惰性恢复。
    /// </summary>
    [InitializeOnLoad]
    public static class CompilationResultService
    {
        private const string SessionKey = "UnitySkills_LastCompilationResult";

        // 载荷上限：异常大量报错时保证响应体有界。计数字段仍是真实总数，
        // 数组真被截断时由 truncated 标记。
        private const int MaxErrors = 200;
        private const int MaxWarnings = 50;

        // 当前编译周期的累积中间态（仅主线程访问）。
        private static DateTime _startedUtc;
        private static readonly List<CompileMessageEntry> _errors = new List<CompileMessageEntry>();
        private static readonly List<CompileMessageEntry> _warnings = new List<CompileMessageEntry>();

        // 上次完成结果的 JSON 缓存；null / 空表示尚未加载或本会话没有编译过。
        private static string _cachedResultJson;

        static CompilationResultService()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        /// <summary>
        /// 上次编译完成结果的 JSON；本编辑器会话内没有编译完成过则返回 null。
        /// 域重载后会从 SessionState 惰性恢复。其他端点（如后续的事件通道）也可复用。
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
                // CompilerMessageType.Info 刻意忽略。
            }
        }

        private static void OnCompilationFinished(object context)
        {
            long durationMs = _startedUtc == default(DateTime)
                ? 0L
                : Math.Max(0L, (long)(DateTime.UtcNow - _startedUtc).TotalMilliseconds);

            // 下面的序列化是同步读取，早于下一个编译周期清空这两个列表，
            // 所以直接交出活列表（或其截断视图）是安全的。
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

            // 精简的事件载荷：头几条错误足以让 agent 拿到 file:line，
            // 完整列表留给 GET /compile/status。
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

        /// <summary>一条编译器诊断信息，已拍平为便于传输的形状。</summary>
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
