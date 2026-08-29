using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Always-visible "pending approval" banner pinned between the Topbar and the Tab Bar.
    /// Hidden when no pending grants exist; otherwise lists up to <see cref="MaxRows"/>
    /// inline cards with Approve / Deny buttons so the user doesn't need to open the
    /// Settings drawer for a one-tap decision.
    ///
    /// Refresh strategy is intentionally redundant:
    /// 1. Subscribed to <see cref="SkillsModeManager.OnChanged"/> for instant updates.
    /// 2. <c>schedule.Every(1000)</c> polls the live state every second to recover from
    ///    any event-delivery gap (UIElements only repaints focused windows in some 2022+
    ///    builds, so the active polling is the actual fallback that makes this reliable).
    /// 3. After every Rebuild we call <see cref="EditorWindow.Repaint"/> so the window
    ///    redraws even when in the background.
    /// </summary>
    public sealed class PendingApprovalBannerController
    {
        private const int MaxRows = 3;
        private const int PollIntervalMs = 1000;

        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;

        private VisualElement _banner;
        private Label         _title;
        private Button        _settingsBtn;
        private VisualElement _list;

        // snapshot of (count + tokens) of the last Rebuild — used to decide whether to
        // rebuild DOM or just update the expires Label in place.
        private string _lastSnapshot = "";

        public PendingApprovalBannerController(VisualElement root, UnitySkillsWindow window)
        {
            _root = root;
            _window = window;

            _banner      = _root.Q<VisualElement>("pending-banner");
            _title       = _root.Q<Label>("pending-banner-title");
            _settingsBtn = _root.Q<Button>("pending-banner-settings-btn");
            _list        = _root.Q<VisualElement>("pending-banner-list");

            if (_settingsBtn != null)
                _settingsBtn.clicked += () => _window?.OpenSettings();

            SkillsModeManager.OnChanged += OnModeChanged;
            _root.RegisterCallback<DetachFromPanelEvent>(OnRootDetached);

            // Defers the actual mutation to delayCall via EditorUiScheduler.RepeatSafe, to avoid triggering an
            // InvalidOperationException during repaint/generateVisualContent (issue #44).
            EditorUiScheduler.RepeatSafe(_root, PollIntervalMs, Tick);

            RefreshLocalization();
            Tick(); // initial paint
        }

        private void OnRootDetached(DetachFromPanelEvent _)
        {
            SkillsModeManager.OnChanged -= OnModeChanged;
        }

        private void OnModeChanged() => Tick();

        public void RefreshLocalization()
        {
            if (_settingsBtn != null)
                _settingsBtn.text = SkillsLocalization.Get("pending_banner_open_settings");
            // Title is refreshed dynamically with the count inside Tick, no need to set it here.
        }

        private void Tick()
        {
            var pending = SkillsModeManager.PendingGrantRequests;
            // The banner only shows in Approval mode — other modes never have pending requests, but this is
            // one extra belt-and-suspenders check just in case.
            bool show = pending.Count > 0
                        && SkillsModeManager.CurrentMode == SkillsOperatingMode.Approval;

            if (_banner != null)
                _banner.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;

            if (!show)
            {
                if (_list != null) _list.Clear();
                _lastSnapshot = "";
                return;
            }

            var snapshot = ComputeSnapshot(pending);
            if (snapshot != _lastSnapshot)
            {
                _lastSnapshot = snapshot;
                Rebuild(pending);
                _window?.Repaint();
            }
            else
            {
                RefreshExpiresInPlace(pending);
            }
        }

        private void Rebuild(IReadOnlyList<GrantRequest> pending)
        {
            if (_list == null) return;
            _list.Clear();

            if (_title != null)
            {
                _title.text = string.Format(
                    SkillsLocalization.Get("pending_banner_title_fmt"),
                    pending.Count);
            }

            int shown = System.Math.Min(pending.Count, MaxRows);
            for (int i = 0; i < shown; i++)
                _list.Add(BuildCard(pending[i]));

            if (pending.Count > shown)
            {
                var overflow = new Label(string.Format(
                    SkillsLocalization.Get("pending_banner_overflow_fmt"),
                    pending.Count - shown));
                overflow.AddToClassList("pending-banner__overflow");
                _list.Add(overflow);
            }
        }

        private static VisualElement BuildCard(GrantRequest req)
        {
            var card = new VisualElement();
            card.AddToClassList("pending-banner__card");

            var head = new VisualElement();
            head.AddToClassList("pending-banner__card-head");

            var skill = new Label($"{req.SkillName}  ({req.Channel})  #{PermissionUiHelpers.ShortToken(req.Token)}");
            skill.AddToClassList("pending-banner__skill");
            head.Add(skill);

            var expires = new Label(PermissionUiHelpers.FormatCountdown(req.ExpiresAtUtc));
            expires.AddToClassList("pending-banner__expires");
            expires.userData = req.ExpiresAtUtc; // used to refresh the countdown every second
            head.Add(expires);

            card.Add(head);

            if (!string.IsNullOrEmpty(req.ArgsSummary))
            {
                var args = new Label($"args: {req.ArgsSummary}");
                args.AddToClassList("pending-banner__args");
                card.Add(args);
            }

            bool isPanel = req.Channel == "panel";

            // Channel-specific feedback: the panel channel goes through the panel's Approve; the dialog channel's approval goes through the AI chat
            if (isPanel && req.ApprovedByPanel)
            {
                var status = new Label(SkillsLocalization.Get("perm_approved_waiting"));
                status.AddToClassList("pending-banner__args");
                card.Add(status);
            }
            else if (!isPanel)
            {
                var chatHint = new Label(SkillsLocalization.Get("perm_approve_in_chat"));
                chatHint.AddToClassList("pending-banner__args");
                card.Add(chatHint);
            }

            var actions = new VisualElement();
            actions.AddToClassList("pending-banner__actions");

            var approve = new Button(() => SkillsModeManager.Approve(req.Token))
            {
                text = SkillsLocalization.Get("perm_approve"),
            };
            approve.AddToClassList("mini-btn");
            approve.style.marginRight = 4;
            approve.SetEnabled(isPanel && !req.ApprovedByPanel);
            actions.Add(approve);

            var deny = new Button(() => SkillsModeManager.Deny(req.Token))
            {
                text = SkillsLocalization.Get("perm_deny"),
            };
            deny.AddToClassList("mini-btn");
            deny.AddToClassList("danger");
            actions.Add(deny);

            card.Add(actions);
            return card;
        }

        /// <summary>
        /// The snapshot is only used to decide whether to rebuild the DOM; with the same token set, the DOM is kept and only the countdown text is updated.
        /// </summary>
        private static string ComputeSnapshot(IReadOnlyList<GrantRequest> pending)
        {
            if (pending.Count == 0) return "0";
            var sb = new StringBuilder(pending.Count * 24);
            sb.Append(pending.Count).Append(':');
            for (int i = 0; i < pending.Count; i++)
            {
                sb.Append(pending[i].Token).Append(pending[i].ApprovedByPanel ? '+' : '-').Append(',');
            }
            return sb.ToString();
        }

        private void RefreshExpiresInPlace(IReadOnlyList<GrantRequest> _)
        {
            if (_list == null) return;
            _list.Query<Label>(className: "pending-banner__expires").ForEach(label =>
            {
                if (label.userData is System.DateTime expiresUtc)
                    label.text = PermissionUiHelpers.FormatCountdown(expiresUtc);
            });
        }
    }
}

// Producer:Betsy
