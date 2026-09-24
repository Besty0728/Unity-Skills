using System;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Issues and consumes one-time confirmation tokens for high-risk skills (RiskLevel="high" or Operation includes Delete).
    ///
    /// Flow:
    ///   1. Caller invokes a high-risk skill without a "_confirm" argument
    ///   2. The server returns CONFIRMATION_REQUIRED + a new token + a dry-run preview
    ///   3. Caller re-invokes with the same arguments plus "_confirm": &lt;token&gt;
    ///   4. The server consumes the token and actually executes
    ///
    /// A token is bound to (skillName, argsHash), so an issued token cannot be replayed against a
    /// modified payload. TTL defaults to 5 minutes. Off by default; can be enabled from the Server tab of UnitySkillsWindow.
    /// </summary>
    public static class ConfirmationTokenService
    {
        private const string PrefKeyRequire = "UnitySkills_RequireConfirmation";
        private const int DefaultTtlSeconds = 300;
        private const int MaxLiveTokens = 256;

        private static readonly OneTimeTokenStore _tokens = new OneTimeTokenStore(DefaultTtlSeconds, MaxLiveTokens);

        /// <summary>Test seam: when set, <see cref="RequireConfirmation"/> reads this instead of the machine-wide pref.</summary>
        internal static bool? RequireConfirmationOverrideForTests;

        /// <summary>
        /// Global switch. Defaults to false -- most users want unattended automation.
        /// When false, this service is entirely a no-op and skills execute without confirmation.
        /// </summary>
        public static bool RequireConfirmation
        {
            get => RequireConfirmationOverrideForTests ?? EditorPrefs.GetBool(PrefKeyRequire, false);
            set => EditorPrefs.SetBool(PrefKeyRequire, value);
        }

        public static int Ttl => DefaultTtlSeconds;

        /// <summary>
        /// A skill counts as high-risk when RiskLevel="high" or its Operation includes Delete.
        /// Declared internal because <see cref="SkillRouter.SkillInfo"/> is itself internal.
        /// </summary>
        internal static bool IsHighRisk(SkillRouter.SkillInfo skill)
        {
            if (skill == null) return false;
            if (string.Equals(skill.RiskLevel, "high", StringComparison.OrdinalIgnoreCase))
                return true;
            if (skill.Operation.HasFlag(SkillOperation.Delete))
                return true;
            return false;
        }

        /// <summary>
        /// Issues a new token bound to (skillName, argsHash), valid for a single use.
        /// </summary>
        public static (string token, int ttlSeconds) IssueToken(string skillName, string argsJson) =>
            (_tokens.Issue(skillName, HashArgs(argsJson)), DefaultTtlSeconds);

        /// <summary>
        /// Attempts to consume a token. Returns false if it doesn't exist, has expired, or its
        /// bound (skillName, args) doesn't match; a mismatch leaves the token usable. A successfully consumed token is removed.
        /// </summary>
        public static bool TryConsume(string token, string skillName, string argsJson) =>
            _tokens.TryConsume(token, skillName, HashArgs(argsJson), out _) == OneTimeTokenStore.Check.Ok;

        public static int CleanupExpired() => _tokens.CleanupExpired();

        private static string HashArgs(string argsJson)
        {
            // Only trim leading/trailing whitespace to avoid unrelated formatting differences invalidating
            // the token. Keys are not reordered -- the client is expected to send the same structure both times.
            var normalized = argsJson ?? string.Empty;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized.Trim()));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}

// Producer:Betsy
