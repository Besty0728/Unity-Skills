using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The dryRun policy dropdown in the settings drawer: option position maps to the enum (never the localized text), the
    /// choice writes <see cref="DryRunPolicyService.Current"/>, and an outside change shows up in the dropdown and hint.
    /// The policy only goes through the service's test seam. An off-screen tree has no panel, so ChangeEvent dispatch is
    /// not exercised; the callback body is invoked directly instead (same limitation as SurfaceProfileDrawerUiTests).
    /// </summary>
    [TestFixture]
    public class DryRunPolicyDrawerUiTests
    {
        private static readonly DryRunPolicy[] ExpectedOrder = { DryRunPolicy.Off, DryRunPolicy.HighRisk, DryRunPolicy.AllWrites };

        private static readonly string[] ChoiceKeys = { "dryrun_policy_off", "dryrun_policy_high_risk", "dryrun_policy_all_writes" };

        private static readonly string[] HintKeys =
            { "dryrun_policy_off_hint", "dryrun_policy_high_risk_hint", "dryrun_policy_all_writes_hint" };

        private readonly List<SettingsDrawerController> _drawers = new List<SettingsDrawerController>();
        private SkillsLocalization.Language _savedLanguage;
        private DryRunPolicy _realPolicy;

        [SetUp]
        public void SetUp()
        {
            _savedLanguage = SkillsLocalization.Current;
            DryRunPolicyService.OverrideForTests = null;
            _realPolicy = DryRunPolicyService.Current;
            DryRunPolicyService.OverrideForTests = DryRunPolicy.Off;
            SkillsLocalization.Current = SkillsLocalization.Language.English;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var drawer in _drawers) drawer.Dispose();
            _drawers.Clear();
            // Raise OnChanged with the real value before the seam goes away, so the /health snapshot ends up in sync.
            DryRunPolicyService.Current = _realPolicy;
            DryRunPolicyService.OverrideForTests = null;
            SkillsLocalization.Current = _savedLanguage;
        }

        [Test]
        public void PolicyOrder_MapsChoiceIndexToEnum()
        {
            var order = (DryRunPolicy[])typeof(SettingsDrawerController)
                .GetField("_dryRunPolicyOrder", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);

            Assert.That(order, Is.EqualTo(ExpectedOrder));
            Assert.That(order.Length, Is.EqualTo(Enum.GetValues(typeof(DryRunPolicy)).Length),
                "Every policy must be selectable in the panel.");
        }

        [Test]
        public void Dropdown_ChoicesAreLocalizedLabelsInPolicyOrder()
        {
            var dropdown = Dropdown(BuildDrawer());
            Assert.That(dropdown.choices, Is.EqualTo(ChoiceKeys.Select(key => SkillsLocalization.Get(key)).ToList()));
        }

        [Test]
        public void Dropdown_InitialValue_ReflectsCurrentPolicy()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.HighRisk;
            var root = BuildDrawer();

            Assert.That(Dropdown(root).value, Is.EqualTo(Dropdown(root).choices[1]));
            Assert.That(Hint(root).text, Is.EqualTo(SkillsLocalization.Get("dryrun_policy_high_risk_hint")));
        }

        [Test]
        public void ExternalPolicyChange_SyncsDropdownAndHint()
        {
            var root = BuildDrawer();
            Assert.That(Dropdown(root).value, Is.EqualTo(Dropdown(root).choices[0]));

            DryRunPolicyService.Current = DryRunPolicy.AllWrites;

            Assert.That(Dropdown(root).value, Is.EqualTo(Dropdown(root).choices[2]));
            Assert.That(Hint(root).text, Is.EqualTo(SkillsLocalization.Get("dryrun_policy_all_writes_hint")));
        }

        [Test]
        public void ChoosingAnOption_WritesThePolicy()
        {
            var drawer = BuildDrawer(out var root);
            var dropdown = Dropdown(root);

            for (var index = ExpectedOrder.Length - 1; index >= 0; index--)
            {
                Choose(drawer, dropdown.choices[index]);
                Assert.That(DryRunPolicyService.Current, Is.EqualTo(ExpectedOrder[index]));
                Assert.That(dropdown.value, Is.EqualTo(dropdown.choices[index]));
                Assert.That(Hint(root).text, Is.EqualTo(SkillsLocalization.Get(HintKeys[index])));
            }
        }

        [Test]
        public void ChoosingUnknownText_ChangesNothing()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.HighRisk;
            var drawer = BuildDrawer(out _);

            Choose(drawer, "not an option");

            Assert.That(DryRunPolicyService.Current, Is.EqualTo(DryRunPolicy.HighRisk));
        }

        [Test]
        public void LanguageSwitch_RelocalizesChoices_KeepsTheSelection()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;
            var drawer = BuildDrawer(out var root);

            SkillsLocalization.Current = SkillsLocalization.Language.Chinese;
            drawer.RefreshLocalization();

            Assert.That(Dropdown(root).choices[2], Is.EqualTo(SkillsLocalization.Get("dryrun_policy_all_writes")));
            Assert.That(Dropdown(root).value, Is.EqualTo(Dropdown(root).choices[2]));
            Assert.That(DryRunPolicyService.Current, Is.EqualTo(DryRunPolicy.AllWrites), "Relocalizing must not write the policy.");
        }

        [Test]
        public void Dispose_Unsubscribes()
        {
            var drawer = BuildDrawer(out var root);
            drawer.Dispose();
            _drawers.Remove(drawer);

            DryRunPolicyService.Current = DryRunPolicy.AllWrites;

            Assert.That(Dropdown(root).value, Is.EqualTo(Dropdown(root).choices[0]));
        }

        [TestCase(SkillsLocalization.Language.English)]
        [TestCase(SkillsLocalization.Language.Chinese)]
        [TestCase(SkillsLocalization.Language.Russian)]
        public void Strings_ResolveAndLabelsAreDistinct(SkillsLocalization.Language language)
        {
            SkillsLocalization.Current = language;
            foreach (var key in ChoiceKeys.Concat(HintKeys).Append("dryrun_policy_label"))
                Assert.That(SkillsLocalization.TryGet(key, out var text) && !string.IsNullOrWhiteSpace(text), Is.True, $"{language}: {key}");

            var labels = ChoiceKeys.Select(key => SkillsLocalization.Get(key)).ToList();
            Assert.That(labels.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(labels.Count),
                "Duplicate option text would make one policy unselectable.");
        }

        [TestCase(SkillsLocalization.Language.English)]
        [TestCase(SkillsLocalization.Language.Chinese)]
        [TestCase(SkillsLocalization.Language.Russian)]
        public void Hints_NeverClaimTheTokenReplacesConfirmation(SkillsLocalization.Language language)
        {
            // A dryRun token never satisfies _confirm; the panel must not tell the user otherwise.
            SkillsLocalization.Current = language;
            foreach (var key in HintKeys)
            {
                var text = SkillsLocalization.Get(key);
                Assert.That(text, Does.Not.Contain("replaces the confirmation"), key);
                Assert.That(text, Does.Not.Contain("代替二次确认"), key);
                Assert.That(text, Does.Not.Contain("заменяет и подтверждение"), key);
            }
        }

        // ---------- helpers ----------

        private VisualElement BuildDrawer()
        {
            BuildDrawer(out var root);
            return root;
        }

        private SettingsDrawerController BuildDrawer(out VisualElement root)
        {
            root = new VisualElement();
            root.Add(new VisualElement { name = "drawer" });
            var drawer = new SettingsDrawerController(root, null);
            _drawers.Add(drawer);
            Assert.That(Dropdown(root), Is.Not.Null, "SettingsDrawer.uxml has no dryrun-policy-dropdown.");
            return drawer;
        }

        private static DropdownField Dropdown(VisualElement root) => root.Q<DropdownField>("dryrun-policy-dropdown");

        private static Label Hint(VisualElement root) => root.Q<Label>("dryrun-policy-hint");

        private static void Choose(SettingsDrawerController drawer, string choice)
        {
            var method = typeof(SettingsDrawerController).GetMethod("ApplyDryRunPolicyChoice", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "The dropdown callback body moved; update this test.");
            method.Invoke(drawer, new object[] { choice });
        }
    }
}

// Producer:Betsy
