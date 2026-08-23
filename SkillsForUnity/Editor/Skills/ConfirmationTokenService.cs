using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// 为高危技能（RiskLevel="high" 或 Operation 含 Delete）签发并消耗一次性确认令牌。
    ///
    /// 流程：
    ///   1. 调用方不带 "_confirm" 参数调用高危技能
    ///   2. 服务端返回 CONFIRMATION_REQUIRED + 新令牌 + dry-run 预览
    ///   3. 调用方用相同参数加上 "_confirm": &lt;token&gt; 重新调用
    ///   4. 服务端消耗该令牌并真正执行
    ///
    /// 令牌绑定到 (skillName, argsHash)，因此签发出的令牌无法配着改动过的载荷重放。
    /// TTL 默认 5 分钟。默认关闭，可在 UnitySkillsWindow 的 Server 页开启。
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
        /// 全局开关。默认 false——多数用户要的是无人值守自动化。
        /// 为 false 时本服务完全空转，技能无需确认即可执行。
        /// </summary>
        public static bool RequireConfirmation
        {
            get => EditorPrefs.GetBool(PrefKeyRequire, false);
            set => EditorPrefs.SetBool(PrefKeyRequire, value);
        }

        public static int Ttl => DefaultTtlSeconds;

        /// <summary>
        /// RiskLevel="high" 或 Operation 含 Delete 即视为高危技能。
        /// 声明为 internal 是因为 <see cref="SkillRouter.SkillInfo"/> 本身是 internal。
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
        /// 签发一个绑定到 (skillName, argsHash) 的新令牌，一次性有效。
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
        /// 尝试消耗令牌。不存在、已过期，或绑定的 (skillName, args) 不匹配时返回 false。
        /// 成功消耗的令牌会被移除。
        /// </summary>
        public static bool TryConsume(string token, string skillName, string argsJson)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            if (!_entries.TryGetValue(token, out var entry))
                return false;

            // 必须先校验再删除。一个仍然有效、只是 (skillName, args) 不匹配的令牌
            // （例如客户端 JSON 略有差异，或重放到了别的技能上）不能被销毁：
            // 调用方还要拿它完整地重试确认流程。先删后判会让任何一次不匹配烧掉好令牌。
            if (DateTime.UtcNow > entry.ExpiresAtUtc)
                return false;

            if (!string.Equals(entry.SkillName, skillName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(entry.ArgsHash, HashArgs(argsJson), StringComparison.Ordinal))
                return false;

            // 全部校验通过，原子消耗。若在 TryGetValue 到此刻之间已被别的线程消耗，
            // TryRemove 会返回 false，正好处理这个竞态。
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
            // 客户端只签不用时防止内存无限增长的廉价兜底。
            if (_entries.Count < MaxLiveTokens) return;
            // 任意剔除直到回到上限以下：顺序不确定，但次数有界。
            foreach (var key in _entries.Keys)
            {
                if (_entries.Count < MaxLiveTokens) break;
                _entries.TryRemove(key, out _);
            }
        }

        private static string GenerateToken()
        {
            // 16 字节 -> 22 字符 base64url，对 5 分钟窗口而言唯一性足够。
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
            // 只规整首尾空白，避免无关的格式差异使令牌失效。
            // 不对键重排序——约定客户端两次发送的结构应当一致。
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
