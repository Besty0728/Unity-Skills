using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Presents the cached/latest stable release as a compact global notice.
    /// Network and cache ownership stay in <see cref="VersionCheckService"/>.
    /// </summary>
    internal sealed class VersionUpdateBannerController
    {
        private readonly VisualElement _banner;
        private readonly Label _message;
        private readonly Button _updateNowButton;
        private readonly Button _viewReleaseButton;
        private readonly Button _dismissButton;

        private const double CheckPollIntervalSeconds = 60.0;

        private string _lastSnapshot;
        private double _nextCheckAtEditorTime;
        private VersionCheckService.ReleaseInfo _displayedRelease;
        private bool _updating;
        // Transient status shown in place of the release message while a self-update runs. Held as a
        // localization key (plus an optional verbatim argument) so a language switch re-resolves it
        // instead of freezing the row in whichever language was active when the update started.
        private string _statusKey;
        private string _statusArgKey;
        private string _statusArgText;
        // Manifest reads are cheap but the install source cannot change mid-session, so detect lazily once.
        private PackageManagerHelper.SelfInstallKind? _installKind;

        public VersionUpdateBannerController(VisualElement root)
        {
            _banner = root.Q<VisualElement>("version-update-banner");
            _message = root.Q<Label>("version-update-message");
            _updateNowButton = root.Q<Button>("version-update-now-btn");
            _viewReleaseButton = root.Q<Button>("version-update-view-btn");
            _dismissButton = root.Q<Button>("version-update-dismiss-btn");

            if (_updateNowButton != null)
                _updateNowButton.clicked += StartDirectUpdate;
            if (_viewReleaseButton != null)
                _viewReleaseButton.clicked += OpenRelease;
            if (_dismissButton != null)
                _dismissButton.clicked += Dismiss;

            VersionCheckService.StartCheck();
            _nextCheckAtEditorTime = EditorApplication.timeSinceStartup + CheckPollIntervalSeconds;
            RefreshLocalization();
        }

        public void UpdateLiveData()
        {
            PollForReleaseCheck();

            var release = VersionCheckService.LatestRelease;
            var shouldShow = VersionCheckService.HasUpdate;
            var snapshot = shouldShow
                ? $"{SkillsLogger.Version}|{release?.TagName}|{release?.ReleaseUrl}|show"
                : $"{SkillsLogger.Version}|{release?.Version}|hide";

            if (snapshot == _lastSnapshot) return;
            _lastSnapshot = snapshot;

            if (!shouldShow || release == null)
            {
                _displayedRelease = null;
                _statusKey = null;
                _banner?.EnableInClassList("is-hidden", true);
                return;
            }

            // A status left over from an attempt against an older release is stale once a different
            // release takes over the banner.
            if (!_updating && _displayedRelease != null &&
                !string.Equals(_displayedRelease.Version, release.Version, StringComparison.Ordinal))
                _statusKey = null;

            _displayedRelease = release;
            RefreshMessage(release);
            // Local/embedded installs cannot self-update; hide the one-click button there.
            UiVisibility.SetVisible(_updateNowButton,
                !_updating && InstallKind != PackageManagerHelper.SelfInstallKind.Unsupported);
            _banner?.EnableInClassList("is-hidden", false);
        }

        private PackageManagerHelper.SelfInstallKind InstallKind =>
            _installKind ??= PackageManagerHelper.DetectSelfInstallKind();

        public void RefreshLocalization()
        {
            if (_updateNowButton != null)
                _updateNowButton.text = SkillsLocalization.Get("version_update_now");
            if (_viewReleaseButton != null)
                _viewReleaseButton.text = SkillsLocalization.Get("version_update_view_release");
            if (_dismissButton != null)
                _dismissButton.tooltip = SkillsLocalization.Get("version_update_dismiss_tip");

            _lastSnapshot = null;
            UpdateLiveData();
            // UpdateLiveData leaves a transient status untouched, so re-resolve it here.
            ApplyStatus();
        }

        private void PollForReleaseCheck()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now < _nextCheckAtEditorTime) return;

            _nextCheckAtEditorTime = now + CheckPollIntervalSeconds;
            VersionCheckService.StartCheck();
        }

        private void RefreshMessage(VersionCheckService.ReleaseInfo release)
        {
            if (_message == null) return;

            // A self-update in flight owns the message line; don't overwrite its status.
            if (_statusKey != null) return;

            _message.text = string.Format(
                SkillsLocalization.Get("version_update_message_fmt"),
                SkillsLogger.Version,
                release.Version);
        }

        private void SetStatus(string key, string argKey = null, string argText = null)
        {
            _statusKey = key;
            _statusArgKey = argKey;
            _statusArgText = argText;
            ApplyStatus();
        }

        private void ApplyStatus()
        {
            if (_message == null || _statusKey == null) return;

            var arg = _statusArgKey != null ? SkillsLocalization.Get(_statusArgKey) : _statusArgText;
            _message.text = arg == null
                ? SkillsLocalization.Get(_statusKey)
                : SkillsLocalization.Get(_statusKey, arg);
        }

        private void OpenRelease()
        {
            var url = _displayedRelease?.ReleaseUrl;
            if (!string.IsNullOrWhiteSpace(url)) Application.OpenURL(url);
        }

        /// <summary>
        /// One-click self-update straight from the banner. The banner only ever advertises a
        /// stable release, so the update always targets "#v{release.Version}".
        /// </summary>
        private void StartDirectUpdate()
        {
            var release = _displayedRelease;
            if (release == null || _updating) return;

            if (InstallKind == PackageManagerHelper.SelfInstallKind.Unsupported)
            {
                OpenRelease();
                return;
            }

            _updating = true;
            _updateNowButton?.SetEnabled(false);
            _viewReleaseButton?.SetEnabled(false);
            SetStatus("update_check_updating");

            if (InstallKind == PackageManagerHelper.SelfInstallKind.Local)
            {
                // Local (file:/embedded) installs download the repo archive and swap directories
                // instead of asking the Package Manager to rewrite the manifest.
                LocalSelfUpdateService.Start(release.Version, (success, error) =>
                {
                    if (success)
                    {
                        // The package swap triggers a domain reload that tears this banner down.
                        SetStatus("update_check_done");
                        return;
                    }

                    _updating = false;
                    _updateNowButton?.SetEnabled(true);
                    _viewReleaseButton?.SetEnabled(true);
                    if (error == LocalSelfUpdateService.CancelledMessage)
                        SetStatus("update_check_cancelled");
                    else
                        SetStatus("update_check_failed_fmt",
                            argKey: ResolveFailureReasonKey(error),
                            argText: error);
                });
                return;
            }

            PackageManagerHelper.UpdateSelf(
                PackageManagerHelper.SelfInstallKind.Stable, release.Version, (success, error) =>
                {
                    if (success)
                    {
                        // The package swap triggers a domain reload that tears this banner down.
                        SetStatus("update_check_done");
                        return;
                    }

                    _updating = false;
                    _updateNowButton?.SetEnabled(true);
                    _viewReleaseButton?.SetEnabled(true);
                    SetStatus("update_check_failed_fmt",
                        argKey: ResolveFailureReasonKey(error),
                        argText: error);
                });
        }

        /// <summary>
        /// Maps a failure text produced by <see cref="PackageManagerHelper"/> itself to a localization
        /// key; returns null for an upstream Package Manager diagnostic, which is shown verbatim.
        /// </summary>
        private static string ResolveFailureReasonKey(string message)
        {
            if (string.IsNullOrEmpty(message)) return "update_check_reason_unknown";
            if (message == PackageManagerHelper.BusyMessage) return "update_check_reason_busy";
            if (message == PackageManagerHelper.UnknownErrorMessage) return "update_check_reason_unknown";
            if (message == LocalSelfUpdateService.NetworkErrorMessage) return "update_check_reason_network";
            if (message == LocalSelfUpdateService.DiskErrorMessage) return "update_check_reason_disk";
            if (message == LocalSelfUpdateService.InvalidPackageErrorMessage) return "update_check_reason_invalid";
            if (message == LocalSelfUpdateService.PackageRootNotFoundMessage) return "update_check_reason_path";
            return null;
        }

        private void Dismiss()
        {
            var release = _displayedRelease;
            if (release == null) return;

            VersionCheckService.Dismiss(release);
            _displayedRelease = null;
            _lastSnapshot = null;
            UpdateLiveData();
        }
    }
}

// Producer:Betsy
