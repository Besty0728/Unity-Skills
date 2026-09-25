using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillRouter
    {
        // ========== Query string parsing ==========

        /// <summary>
        /// Parses a query string into a case-insensitive key -> value map.
        ///
        /// Two forms used to be dropped outright, even though a caller writes them deliberately:
        /// <list type="bullet">
        /// <item><b>A bare key</b> (<c>?full</c>, <c>?brief</c>) -- the URL idiom for "present means true."
        /// Dropping it would turn <c>GET /skills?full</c> into a silent no-op, returning the 19KB catalog while the caller
        /// waits on the 707KB manifest. It's now collected as the value <c>"1"</c>, the same value <c>?full=1</c> gets,
        /// so both spellings share one cache entry and one ETag.</item>
        /// <item><b>A key with an empty value</b> (<c>?category=</c>) -- collected as an empty string rather than dropped,
        /// so the narrowing-filter guard can reject it alongside the valid word list. Dropping it would turn a half-written filter
        /// condition into "no filter," so a scoped request would get the whole catalog while still looking like it succeeded.</item>
        /// </list>
        /// A pair with no key at all (<c>?=v</c>, or an empty segment in <c>?a&amp;&amp;b</c>) is still skipped -- there's no key to index by.
        /// </summary>
        internal static Dictionary<string, string> ParseQueryString(string qs)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in EnumerateQueryPairs(qs))
                result[key] = value;
            return result;
        }

        /// <summary>
        /// Every value given for <paramref name="key"/> (case-insensitive), in query order and parsed exactly like
        /// <see cref="ParseQueryString"/>, which keeps only the last occurrence -- this is for keys that may repeat.
        /// </summary>
        internal static List<string> ReadQueryValues(string qs, string key)
        {
            var values = new List<string>();
            foreach (var (k, value) in EnumerateQueryPairs(qs))
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    values.Add(value);
            }
            return values;
        }

        private static IEnumerable<(string key, string value)> EnumerateQueryPairs(string qs)
        {
            if (string.IsNullOrEmpty(qs)) yield break;

            var raw = qs.StartsWith("?") ? qs.Substring(1) : qs;
            if (string.IsNullOrEmpty(raw)) yield break;

            foreach (var pair in raw.Split('&'))
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx == 0) continue;

                string key, val;
                if (eqIdx < 0)
                {
                    key = Uri.UnescapeDataString(pair).Trim();
                    val = "1";
                }
                else
                {
                    key = Uri.UnescapeDataString(pair.Substring(0, eqIdx)).Trim();
                    val = Uri.UnescapeDataString(pair.Substring(eqIdx + 1)).Trim();
                }

                if (!string.IsNullOrEmpty(key))
                    yield return (key, val);
            }
        }
    }
}

// Producer:Betsy
