using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace UnitySkills
{
    /// <summary>
    /// Single-use tokens bound to (scope, argsHash) with a fixed TTL and a bounded live count. Each consumer owns its own
    /// instance, so a token issued by one gate can never satisfy another (a dryRun token is never a <c>_confirm</c> token).
    /// </summary>
    internal sealed class OneTimeTokenStore
    {
        internal enum Check { Ok, Unknown, Expired, Mismatch }

        private sealed class Entry
        {
            public string Scope;
            public string ArgsHash;
            public DateTime IssuedAtUtc;
            public DateTime ExpiresAtUtc;
        }

        private readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
        private readonly int _capacity;
        private readonly Func<DateTime> _utcNow;

        internal OneTimeTokenStore(int ttlSeconds, int capacity, Func<DateTime> utcNow = null)
        {
            TtlSeconds = ttlSeconds;
            _capacity = capacity;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal int TtlSeconds { get; }

        internal string Issue(string scope, string argsHash)
        {
            CleanupExpired();
            EnforceCapacity();

            var now = _utcNow();
            var token = GenerateToken();
            _entries[token] = new Entry
            {
                Scope = scope ?? string.Empty,
                ArgsHash = argsHash ?? string.Empty,
                IssuedAtUtc = now,
                ExpiresAtUtc = now.AddSeconds(TtlSeconds),
            };
            return token;
        }

        /// <summary>
        /// Consumes the token when it is live and bound to exactly this (scope, argsHash). Validation happens before removal:
        /// a mismatch (the caller changed its arguments, or replayed the token against another scope) must not burn a token
        /// that is still good for what it was issued for.
        /// </summary>
        internal Check TryConsume(string token, string scope, string argsHash, out int ageSeconds)
        {
            ageSeconds = 0;
            if (string.IsNullOrWhiteSpace(token) || !_entries.TryGetValue(token, out var entry))
                return Check.Unknown;

            var now = _utcNow();
            if (now > entry.ExpiresAtUtc)
                return Check.Expired;

            if (!string.Equals(entry.Scope, scope ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.ArgsHash, argsHash ?? string.Empty, StringComparison.Ordinal))
                return Check.Mismatch;

            // A concurrent consumer may have won between the lookup and here; TryRemove settles the race.
            if (!_entries.TryRemove(token, out _))
                return Check.Unknown;

            ageSeconds = (int)Math.Max(0, (now - entry.IssuedAtUtc).TotalSeconds);
            return Check.Ok;
        }

        internal int CleanupExpired()
        {
            int removed = 0;
            var now = _utcNow();
            foreach (var kv in _entries)
            {
                if (now > kv.Value.ExpiresAtUtc && _entries.TryRemove(kv.Key, out _))
                    removed++;
            }
            return removed;
        }

        internal void Clear() => _entries.Clear();

        private void EnforceCapacity()
        {
            // Guards against unbounded growth when clients request tokens they never use. Eviction order is unspecified,
            // but the count stays bounded.
            if (_entries.Count < _capacity) return;
            foreach (var key in _entries.Keys)
            {
                if (_entries.Count < _capacity) break;
                _entries.TryRemove(key, out _);
            }
        }

        private static string GenerateToken()
        {
            // 16 bytes -> 22-char base64url: URL-safe as a query value, plenty unique for a 5-minute window.
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}

// Producer:Betsy
