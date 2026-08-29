using System;
using System.Collections.Concurrent;
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
        /// Global switch. Defaults to false -- most users want unattended automation.
        /// When false, this service is entirely a no-op and skills execute without confirmation.
        /// </summary>
        public static bool RequireConfirmation
        {
            get => EditorPrefs.GetBool(PrefKeyRequire, false);
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
        /// Attempts to consume a token. Returns false if it doesn't exist, has expired, or its
        /// bound (skillName, args) doesn't match. A successfully consumed token is removed.
        /// </summary>
        public static bool TryConsume(string token, string skillName, string argsJson)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            if (!_entries.TryGetValue(token, out var entry))
                return false;

            // Must validate before deleting. A token that's still valid but simply doesn't match on
            // (skillName, args) -- e.g. the client's JSON differs slightly, or it was replayed against a different skill -- must not be destroyed: the caller still needs it to
            // retry the confirmation flow properly. Deleting first and checking after would let any single mismatch burn a good token.
            if (DateTime.UtcNow > entry.ExpiresAtUtc)
                return false;

            if (!string.Equals(entry.SkillName, skillName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(entry.ArgsHash, HashArgs(argsJson), StringComparison.Ordinal))
                return false;

            // All checks passed; consume atomically. If another thread already consumed it between
            // the TryGetValue above and here, TryRemove returns false, which handles that race correctly.
            return _entries.TryRemove(token, out _);
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
            // Cheap safeguard against unbounded memory growth when clients issue tokens without consuming them.
            if (_entries.Count < MaxLiveTokens) return;
            // Evict arbitrarily until back under the cap: order is unspecified, but bounded in count.
            foreach (var key in _entries.Keys)
            {
                if (_entries.Count < MaxLiveTokens) break;
                _entries.TryRemove(key, out _);
            }
        }

        private static string GenerateToken()
        {
            // 16 bytes -> 22-char base64url, plenty unique for a 5-minute window.
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
