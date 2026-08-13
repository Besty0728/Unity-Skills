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
        private readonly Button _viewReleaseButton;
        private readonly Button _dismissButton;

        private string _lastSnapshot;

        public VersionUpdateBannerController(VisualElement root)
        {
            _banner = root.Q<VisualElement>("version-update-banner");
            _message = root.Q<Label>("version-update-message");
            _viewReleaseButton = root.Q<Button>("version-update-view-btn");
            _dismissButton = root.Q<Button>("version-update-dismiss-btn");

            if (_viewReleaseButton != null)
                _viewReleaseButton.clicked += OpenRelease;
            if (_dismissButton != null)
                _dismissButton.clicked += Dismiss;

            VersionCheckService.StartCheck();
            RefreshLocalization();
        }

        public void UpdateLiveData()
        {
            var release = VersionCheckService.LatestRelease;
            var shouldShow = VersionCheckService.HasUpdate;
            var snapshot = shouldShow
                ? $"{SkillsLogger.Version}|{release?.Version}|show"
                : $"{SkillsLogger.Version}|{release?.Version}|hide";

            if (snapshot == _lastSnapshot) return;
            _lastSnapshot = snapshot;

            _banner?.EnableInClassList("is-hidden", !shouldShow);
            if (shouldShow) RefreshMessage();
        }

        public void RefreshLocalization()
        {
            if (_viewReleaseButton != null)
                _viewReleaseButton.text = SkillsLocalization.Get("version_update_view_release");
            if (_dismissButton != null)
                _dismissButton.tooltip = SkillsLocalization.Get("version_update_dismiss_tip");

            _lastSnapshot = null;
            UpdateLiveData();
        }

        private void RefreshMessage()
        {
            var release = VersionCheckService.LatestRelease;
            if (_message == null || release == null) return;

            _message.text = string.Format(
                SkillsLocalization.Get("version_update_message_fmt"),
                SkillsLogger.Version,
                release.Version);
        }

        private static void OpenRelease()
        {
            var url = VersionCheckService.LatestRelease?.ReleaseUrl;
            if (!string.IsNullOrWhiteSpace(url)) Application.OpenURL(url);
        }

        private void Dismiss()
        {
            VersionCheckService.DismissLatest();
            _lastSnapshot = null;
            UpdateLiveData();
        }
    }
}

// Producer:Betsy
