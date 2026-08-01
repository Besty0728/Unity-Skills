using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Footer controller — version tag + live stats pill (Pending / Done)
    /// + segmented language switch (EN | 中文 | RU).
    /// </summary>
    public class FooterController
    {
        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;

        private Label _versionTag;

        private Label _queueValue;
        private Label _queueLabel;
        private Label _doneValue;
        private Label _doneLabel;

        private Button _langEnBtn;
        private Button _langCnBtn;
        private Button _langRuBtn;

        public FooterController(VisualElement root, UnitySkillsWindow window)
        {
            _root = root;
            _window = window;

            _versionTag = _root.Q<Label>("version-tag");

            _queueValue = _root.Q<Label>("stat-queue-value");
            _queueLabel = _root.Q<Label>("stat-queue-label");
            _doneValue  = _root.Q<Label>("stat-done-value");
            _doneLabel  = _root.Q<Label>("stat-done-label");

            _langEnBtn = _root.Q<Button>("lang-seg-en");
            _langCnBtn = _root.Q<Button>("lang-seg-cn");
            _langRuBtn = _root.Q<Button>("lang-seg-ru");

            BindEvents();
            UpdateLiveData();
        }

        private void BindEvents()
        {
            if (_langEnBtn != null) _langEnBtn.clicked += () => SwitchLanguage(SkillsLocalization.Language.English);
            if (_langCnBtn != null) _langCnBtn.clicked += () => SwitchLanguage(SkillsLocalization.Language.Chinese);
            if (_langRuBtn != null) _langRuBtn.clicked += () => SwitchLanguage(SkillsLocalization.Language.Russian);
        }

        private void SwitchLanguage(SkillsLocalization.Language lang)
        {
            _window.SetLanguage(lang);
            RefreshLocalization();
        }

        public void UpdateLiveData()
        {
            int queue = SkillsHttpServer.QueuedRequests;
            long done = SkillsHttpServer.TotalProcessed;

            if (_queueValue != null)
            {
                _queueValue.text = queue.ToString();
                if (queue > 0) _queueValue.AddToClassList("busy");
                else           _queueValue.RemoveFromClassList("busy");
            }

            if (_doneValue != null) _doneValue.text = done.ToString();
        }

        public void RefreshLocalization()
        {
            if (_versionTag != null)
            {
                _versionTag.text = "v" + SkillsLogger.Version;
            }

            if (_queueLabel != null) _queueLabel.text = SkillsLocalization.Get("footer_queue");
            if (_doneLabel  != null) _doneLabel.text  = SkillsLocalization.Get("footer_done");

            // Segmented active state
            var lang = SkillsLocalization.Current;
            if (_langEnBtn != null)
            {
                if (lang == SkillsLocalization.Language.English) _langEnBtn.AddToClassList("active");
                else _langEnBtn.RemoveFromClassList("active");
            }
            if (_langCnBtn != null)
            {
                if (lang == SkillsLocalization.Language.Chinese) _langCnBtn.AddToClassList("active");
                else _langCnBtn.RemoveFromClassList("active");
            }
            if (_langRuBtn != null)
            {
                if (lang == SkillsLocalization.Language.Russian) _langRuBtn.AddToClassList("active");
                else _langRuBtn.RemoveFromClassList("active");
            }

            var seg = _root.Q<VisualElement>("lang-segment");
            if (seg != null) seg.tooltip = SkillsLocalization.Get("footer_lang_tooltip");
        }
    }
}

// Producer:Betsy
