using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Issues and consumes one-shot confirmation tokens for high-risk skills
    /// (RiskLevel="high" or Operation includes Delete).
    ///
    /// Flow:
    ///   1. Caller invokes a high-risk skill without "_confirm" parameter
    ///   2. Server returns CONFIRMATION_REQUIRED + a fresh token + dry-run preview
    ///   3. Caller re-invokes with the same args + "_confirm": &lt;token&gt;
    ///   4. Server consumes the token and executes
    ///
    /// Tokens are bound to (skillName, argsHash) so an issued token cannot be replayed
    /// against a modified payload. TTL defaults to 5 minutes.
    /// Disabled by default — toggled via UnitySkillsWindow's Server tab.
    /// </summary>
    public static class ConfirmationTokenService
    {
        private const string PrefKeyRequire = "UnitySkills_RequireConfirmation";
        private const int DefaultTtlSeconds = 300;
        private const int MaxLiveTokens = 256;

        private sealed class Entry
        {
            public string Token;
            public string SkillName;
            public string ArgsHash;
            public DateTime ExpiresAtUtc;
        }

        private static readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>
        /// Global toggle. Default false — most users want unattended automation.
        /// When false, the service is a no-op and skills run without confirmation.
        /// </summary>
        public static bool RequireConfirmation
        {
            get => EditorPrefs.GetBool(PrefKeyRequire, false);
            set => EditorPrefs.SetBool(PrefKeyRequire, value);
        }

        public static int Ttl => DefaultTtlSeconds;

        /// <summary>
        /// A skill is considered high-risk if RiskLevel="high" or its Operation includes Delete.
        /// Internal because <see cref="SkillRouter.SkillInfo"/> is internal.
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
        /// Issue a fresh token bound to (skillName, argsHash). Token is one-shot.
        /// </summary>
        public static (string token, int ttlSeconds) IssueToken(string skillName, string argsJson)
        {
            CleanupExpired();
            EnforceCapacity();

            var token = GenerateToken();
            var entry = new Entry
            {
                Token = token,
                SkillName = skillName ?? string.Empty,
                ArgsHash = HashArgs(argsJson),
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(DefaultTtlSeconds),
            };
            _entries[token] = entry;
            return (token, DefaultTtlSeconds);
        }

        /// <summary>
        /// Try to consume a token. Returns false if missing, expired, or bound to a
        /// different (skillName, args) pair. Successfully consumed tokens are removed.
        /// </summary>
        public static bool TryConsume(string token, string skillName, string argsJson)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            if (!_entries.TryRemove(token, out var entry))
                return false;

            if (DateTime.UtcNow > entry.ExpiresAtUtc)
                return false;

            if (!string.Equals(entry.SkillName, skillName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(entry.ArgsHash, HashArgs(argsJson), StringComparison.Ordinal))
                return false;

            return true;
        }

        public static int CleanupExpired()
        {
            int removed = 0;
            var nowUtc = DateTime.UtcNow;
            foreach (var kv in _entries)
            {
                if (nowUtc > kv.Value.ExpiresAtUtc && _entries.TryRemove(kv.Key, out _))
                    removed++;
            }
            return removed;
        }

        private static void EnforceCapacity()
        {
            // Cheap guard against runaway memory if a client churns tokens without consuming.
            if (_entries.Count < MaxLiveTokens) return;
            // Remove arbitrary entries until back under cap. Order is unspecified but bounded.
            foreach (var key in _entries.Keys)
            {
                if (_entries.Count < MaxLiveTokens) break;
                _entries.TryRemove(key, out _);
            }
        }

        private static string GenerateToken()
        {
            // 16 bytes -> 22 chars base64url, plenty unique for a 5-minute window.
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string HashArgs(string argsJson)
        {
            // Normalize whitespace so trivial reformatting doesn't invalidate the token.
            // We don't reorder keys — clients are expected to send the same shape both times.
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
