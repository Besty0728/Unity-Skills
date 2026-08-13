using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.Networking;

namespace UnitySkills
{
    /// <summary>
    /// Checks the latest published stable GitHub Release without coupling network state to the UI.
    /// Successful responses are cached for 24 hours; failed attempts have a one-hour cooldown.
    /// </summary>
    internal static class VersionCheckService
    {
        internal sealed class ReleaseInfo
        {
            public string Version { get; }
            public string ReleaseUrl { get; }

            public ReleaseInfo(string version, string releaseUrl)
            {
                Version = version;
                ReleaseUrl = releaseUrl;
            }
        }

        private const string LatestReleaseApi =
            "https://api.github.com/repos/Besty0728/Unity-Skills/releases/latest";

        private const string PrefCachedVersion = "UnitySkills_UpdateCachedVersion";
        private const string PrefCachedReleaseUrl = "UnitySkills_UpdateCachedReleaseUrl";
        private const string PrefLastSuccessfulCheckUtc = "UnitySkills_UpdateLastSuccessfulCheckUtc";
        private const string PrefLastAttemptUtc = "UnitySkills_UpdateLastAttemptUtc";
        private const string PrefDismissedVersion = "UnitySkills_UpdateDismissedVersion";

        private static readonly TimeSpan SuccessCacheLifetime = TimeSpan.FromHours(24);
        private static readonly TimeSpan FailedAttemptCooldown = TimeSpan.FromHours(1);

        private static UnityWebRequest _activeRequest;
        private static ReleaseInfo _latestRelease;

        static VersionCheckService()
        {
            LoadCachedRelease();
            AssemblyReloadEvents.beforeAssemblyReload += CancelActiveRequest;
            EditorApplication.quitting += CancelActiveRequest;
        }

        internal static ReleaseInfo LatestRelease => _latestRelease;

        internal static bool HasUpdate =>
            ShouldShowUpdate(
                SkillsLogger.Version,
                _latestRelease?.Version,
                EditorPrefs.GetString(PrefDismissedVersion, string.Empty));

        internal static void StartCheck()
        {
            if (_activeRequest != null) return;

            var now = DateTime.UtcNow;
            if (IsRecent(PrefLastSuccessfulCheckUtc, now, SuccessCacheLifetime)) return;
            if (IsRecent(PrefLastAttemptUtc, now, FailedAttemptCooldown)) return;

            WriteUtc(PrefLastAttemptUtc, now);

            UnityWebRequest request = null;
            try
            {
                request = UnityWebRequest.Get(LatestReleaseApi);
                request.timeout = 10;
                request.SetRequestHeader("Accept", "application/vnd.github+json");
                request.SetRequestHeader("X-GitHub-Api-Version", "2022-11-28");
                _activeRequest = request;

                var operation = request.SendWebRequest();
                operation.completed += _ => CompleteRequest(request);
            }
            catch
            {
                if (ReferenceEquals(_activeRequest, request))
                    _activeRequest = null;
                request?.Dispose();
            }
        }

        internal static void DismissLatest()
        {
            if (_latestRelease == null) return;
            EditorPrefs.SetString(PrefDismissedVersion, _latestRelease.Version);
        }

        internal static bool ShouldShowUpdate(
            string currentVersion,
            string latestVersion,
            string dismissedVersion)
        {
            if (!TryCompareVersions(latestVersion, currentVersion, out var comparison) || comparison <= 0)
                return false;

            var normalizedLatest = NormalizeVersion(latestVersion);
            var normalizedDismissed = NormalizeVersion(dismissedVersion);
            return !string.Equals(normalizedLatest, normalizedDismissed, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryCompareVersions(string left, string right, out int comparison)
        {
            comparison = 0;
            if (!TryParseVersion(left, out var leftVersion) ||
                !TryParseVersion(right, out var rightVersion))
                return false;

            comparison = leftVersion.CompareTo(rightVersion);
            return true;
        }

        internal static bool TryCreateReleaseInfo(string json, out ReleaseInfo release)
        {
            release = null;
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                var root = JObject.Parse(json);
                var version = NormalizeVersion(root.Value<string>("tag_name"));
                var releaseUrl = root.Value<string>("html_url");

                if (!TryParseVersion(version, out _) || string.IsNullOrWhiteSpace(releaseUrl))
                    return false;

                release = new ReleaseInfo(version, releaseUrl);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CompleteRequest(UnityWebRequest request)
        {
            if (!ReferenceEquals(_activeRequest, request))
                return;

            try
            {
                if (request.result != UnityWebRequest.Result.Success) return;
                if (!TryCreateReleaseInfo(request.downloadHandler?.text, out var release)) return;

                _latestRelease = release;
                CacheRelease(release, DateTime.UtcNow);
            }
            finally
            {
                _activeRequest = null;
                request.Dispose();
            }
        }

        private static void LoadCachedRelease()
        {
            var version = EditorPrefs.GetString(PrefCachedVersion, string.Empty);
            var releaseUrl = EditorPrefs.GetString(PrefCachedReleaseUrl, string.Empty);
            if (!TryParseVersion(version, out _) || string.IsNullOrWhiteSpace(releaseUrl)) return;

            _latestRelease = new ReleaseInfo(NormalizeVersion(version), releaseUrl);
        }

        private static void CacheRelease(ReleaseInfo release, DateTime checkedAtUtc)
        {
            EditorPrefs.SetString(PrefCachedVersion, release.Version);
            EditorPrefs.SetString(PrefCachedReleaseUrl, release.ReleaseUrl);
            WriteUtc(PrefLastSuccessfulCheckUtc, checkedAtUtc);
        }

        private static bool IsRecent(string key, DateTime nowUtc, TimeSpan lifetime)
        {
            if (!TryReadUtc(key, out var valueUtc) || valueUtc == default) return false;
            var age = nowUtc - valueUtc;
            return age >= TimeSpan.Zero && age < lifetime;
        }

        private static bool TryReadUtc(string key, out DateTime valueUtc)
        {
            valueUtc = default;
            var raw = EditorPrefs.GetString(key, string.Empty);
            if (!long.TryParse(raw, out var ticks)) return false;
            try
            {
                valueUtc = new DateTime(ticks, DateTimeKind.Utc);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteUtc(string key, DateTime valueUtc)
        {
            EditorPrefs.SetString(key, valueUtc.ToUniversalTime().Ticks.ToString());
        }

        private static void CancelActiveRequest()
        {
            var request = _activeRequest;
            _activeRequest = null;
            if (request == null) return;

            try { request.Abort(); }
            catch { }
            request.Dispose();
        }

        private static bool TryParseVersion(string value, out Version version)
        {
            version = null;
            var normalized = NormalizeVersion(value);
            return normalized.Split('.').Length == 3 && Version.TryParse(normalized, out version);
        }

        private static string NormalizeVersion(string value)
        {
            var normalized = value?.Trim() ?? string.Empty;
            return normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? normalized.Substring(1)
                : normalized;
        }
    }
}

// Producer:Betsy
