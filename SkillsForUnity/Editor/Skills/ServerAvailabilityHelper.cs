using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnitySkills
{
    internal static class ServerAvailabilityHelper
    {
        private static readonly HashSet<string> ScriptDomainExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".asmdef",
            ".asmref",
            ".rsp",
            ".dll"
        };

        public static bool IsCompilationInProgress()
        {
            return EditorApplication.isCompiling || EditorApplication.isUpdating;
        }

        public static bool AffectsScriptDomain(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            string fileName = Path.GetFileName(assetPath);
            if (string.Equals(fileName, "csc.rsp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "mcs.rsp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "gmcs.rsp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "smcs.rsp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return ScriptDomainExtensions.Contains(Path.GetExtension(assetPath));
        }

        public static Dictionary<string, object> CreateTransientUnavailableNotice(string reason, bool alwaysInclude = false, int retryAfterSeconds = 5)
        {
            bool isCompiling = IsCompilationInProgress();
            if (!alwaysInclude && !isCompiling)
                return null;

            reason = NormalizeReason(reason, isCompiling);

            return new Dictionary<string, object>
            {
                ["mayDisconnect"] = true,
                ["isCompiling"] = isCompiling,
                ["reason"] = reason,
                ["suggestion"] = isCompiling
                    ? "Unity is compiling or refreshing assets. The REST server may be briefly unavailable. Please retry shortly."
                    : "This operation may trigger a script-domain reload. The REST server may be briefly unavailable. Please retry shortly.",
                ["retryAfterSeconds"] = Math.Max(1, retryAfterSeconds),
                ["retryStrategy"] = "wait_and_retry"
            };
        }

        private static string NormalizeReason(string reason, bool isCompiling)
        {
            if (string.IsNullOrWhiteSpace(reason) || LooksCorrupted(reason))
            {
                return isCompiling
                    ? "Unity is compiling or refreshing assets after this operation."
                    : "This operation may trigger a script-domain reload or asset refresh.";
            }

            return reason;
        }

        private static bool LooksCorrupted(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            int nonAsciiCount = 0;
            int asciiLetterCount = 0;
            foreach (char ch in text)
            {
                if (ch > 127)
                {
                    nonAsciiCount++;
                }
                else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
                {
                    asciiLetterCount++;
                }
            }

            return nonAsciiCount >= 6 &&
                   asciiLetterCount >= 3 &&
                   nonAsciiCount * 2 >= text.Length;
        }

        public static void AttachTransientUnavailableNotice(IDictionary<string, object> result, string reason, bool alwaysInclude = false, int retryAfterSeconds = 5)
        {
            if (result == null)
                return;

            var notice = CreateTransientUnavailableNotice(reason, alwaysInclude, retryAfterSeconds);
            if (notice != null)
                result["serverAvailability"] = notice;
        }
    }
}

// Producer:Betsy
