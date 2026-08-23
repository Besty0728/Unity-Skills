using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// 自动检测 DOTween 与 DOTween Pro 是否安装，并据此维护
    /// DOTWEEN / DOTWEEN_PRO 两个 Scripting Define Symbol。
    ///
    /// 每个编辑器会话只跑一次。用户装上 DOTween 后会自动补上宏并请求重新编译，
    /// 无需任何手动配置 DOTweenSkills 即可用；用户移除 DOTween 后宏会被摘掉，
    /// 保证 UnitySkills.Editor 程序集仍能干净编译。
    /// </summary>
    internal static class DOTweenPresenceDetector
    {
        private const string DOTweenDefine = "DOTWEEN";
        private const string DOTweenProDefine = "DOTWEEN_PRO";

        private const string SessionDoneKey = "UnitySkills.DOTweenPresenceDetector.Done";

        [InitializeOnLoadMethod]
        private static void Synchronize()
        {
            if (SessionState.GetBool(SessionDoneKey, false))
                return;

            // 先置完成标记再干活：否则中途抛异常会让本方法在此后每次域重载时
            // 都重新请求一次编译。
            SessionState.SetBool(SessionDoneKey, true);

            try
            {
                bool hasDOTween = DOTweenReflectionHelper.IsDOTweenInstalled;
                bool hasDOTweenPro = DOTweenReflectionHelper.IsDOTweenProInstalled;

                bool changed = false;
                changed |= EnsureDefineState(DOTweenDefine, hasDOTween);
                changed |= EnsureDefineState(DOTweenProDefine, hasDOTweenPro);

                if (changed)
                {
                    try { CompilationPipeline.RequestScriptCompilation(); }
                    catch { /* editor may refuse during certain lifecycle moments */ }
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError("[UnitySkills] DOTweenPresenceDetector init failed: " + ex);
            }
        }

        private static bool EnsureDefineState(string define, bool shouldBePresent)
        {
            bool anyChange = false;

            foreach (BuildTargetGroup btg in Enum.GetValues(typeof(BuildTargetGroup)))
            {
                if (btg == BuildTargetGroup.Unknown) continue;
                if (IsObsoleteBuildTargetGroup(btg)) continue;

                string currentDefs;
                try { currentDefs = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(btg)) ?? string.Empty; }
                catch { continue; }

                var defList = currentDefs
                    .Split(';')
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                bool currentlyPresent = defList.Contains(define);

                if (shouldBePresent && !currentlyPresent)
                {
                    defList.Add(define);
                    WriteDefines(btg, defList);
                    anyChange = true;
                }
                else if (!shouldBePresent && currentlyPresent)
                {
                    defList.RemoveAll(s => string.Equals(s, define, StringComparison.Ordinal));
                    WriteDefines(btg, defList);
                    anyChange = true;
                }
            }

            if (anyChange)
            {
                SkillsLogger.Log($"[DOTweenPresenceDetector] {(shouldBePresent ? "Added" : "Removed")} scripting define '{define}'.");
            }
            return anyChange;
        }

        private static void WriteDefines(BuildTargetGroup btg, List<string> defs)
        {
            try
            {
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(btg), string.Join(";", defs));
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[DOTweenPresenceDetector] Failed to write defines for {btg}: {ex.Message}");
            }
        }

        private static bool IsObsoleteBuildTargetGroup(BuildTargetGroup btg)
        {
            var member = typeof(BuildTargetGroup)
                .GetMember(btg.ToString(), BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault();
            return member != null && member.IsDefined(typeof(ObsoleteAttribute), inherit: false);
        }
    }
}

// Producer:Betsy
