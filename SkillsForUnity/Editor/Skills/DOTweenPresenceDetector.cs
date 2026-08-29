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
    /// Auto-detects whether DOTween and DOTween Pro are installed, and maintains
    /// the DOTWEEN / DOTWEEN_PRO Scripting Define Symbols accordingly.
    ///
    /// Runs once per editor session only. Once the user installs DOTween, the define gets
    /// added automatically and a recompile is requested; DOTweenSkills works with zero manual
    /// configuration. Once the user removes DOTween, the define gets stripped so
    /// the UnitySkills.Editor assembly still compiles cleanly.
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

            // Set the done flag before doing the work: otherwise an exception mid-way would make
            // this method re-request a compile on every subsequent domain reload.
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
