using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Binding for the profile dropdown in the settings drawer, and completeness of the three-language localization keys (a gap left by #5).
    ///
    /// The drawer controller only requires a root containing a child element named "drawer"; the window parameter is only stored and never dereferenced at construction time,
    /// so it can be built without opening an EditorWindow. All assertions land on the "option position <-> enum" mapping layer --
    /// the displayed text is localized, and looking it up by text would break when switching languages, which is exactly why the production code uses <c>_profileOrder</c>
    /// to look up by index.
    /// </summary>
    [TestFixture]
    public class SurfaceProfileDrawerUiTests
    {
        /// <summary>The 9 keys newly added for the profile section. Each of the three-language dictionaries must resolve non-empty text for all of them.</summary>
        private static readonly string[] SurfaceProfileKeys =
        {
            "surface_profile",
            "surface_profile_tooltip",
            "surface_profile_full",
            "surface_profile_guide",
            "surface_profile_no_scene_authoring",
            "surface_profile_full_hint",
            "surface_profile_guide_hint",
            "surface_profile_no_scene_authoring_hint",
            "surface_profile_hidden_count_fmt",
        };

        private static readonly SurfaceProfileKind[] ExpectedProfileOrder =
        {
            SurfaceProfileKind.Full,
            SurfaceProfileKind.Guide,
            SurfaceProfileKind.NoSceneAuthoring,
        };

        private SurfaceProfileKind _savedProfile;
        private SkillsLocalization.Language _savedLanguage;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedLanguage = SkillsLocalization.Current;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var controller in _builtControllers)
                DetachDrawer(controller);
            _builtControllers.Clear();

            SkillsSurfaceProfile.Current = _savedProfile;
            SkillsLocalization.Current = _savedLanguage;
        }

        // ---------- localization ----------

        [TestCase("_english")]
        [TestCase("_chinese")]
        [TestCase("_russian")]
        public void SurfaceProfileKeys_ResolveInEveryLanguage(string dictionaryFieldName)
        {
            var dictionary = GetLocalizationDictionary(dictionaryFieldName);
            var missing = SurfaceProfileKeys
                .Where(key => !dictionary.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                .ToArray();

            Assert.That(missing, Is.Empty,
                $"{dictionaryFieldName} 缺少档位键: {string.Join(", ", missing)}");
        }

        [Test]
        public void FormattedHints_KeepTheirPlaceholders()
        {
            // These two strings are string.Format templates; if any language drops {0}, the panel will silently lose the
            // module name/count instead of throwing an error, so this must be watched explicitly.
            foreach (var dictionaryFieldName in new[] { "_english", "_chinese", "_russian" })
            {
                var dictionary = GetLocalizationDictionary(dictionaryFieldName);
                Assert.That(dictionary["surface_profile_guide_hint"], Does.Contain("{0}"),
                    $"{dictionaryFieldName}.surface_profile_guide_hint 少了模块列表占位符。");
                Assert.That(dictionary["surface_profile_hidden_count_fmt"], Does.Contain("{0}").And.Contain("{1}"),
                    $"{dictionaryFieldName}.surface_profile_hidden_count_fmt 需要条数与模块数两个占位符。");
            }
        }

        [Test]
        public void RetiredGuideModeKeys_AreGone()
        {
            // Leaving the old boolean toggle keys in the dictionary would only make the next person think that toggle still exists on the panel.
            foreach (var dictionaryFieldName in new[] { "_english", "_chinese", "_russian" })
            {
                var dictionary = GetLocalizationDictionary(dictionaryFieldName);
                Assert.That(dictionary.ContainsKey("guide_mode"), Is.False,
                    $"{dictionaryFieldName} 仍留着弃用的 guide_mode 键。");
                Assert.That(dictionary.ContainsKey("guide_mode_tooltip"), Is.False,
                    $"{dictionaryFieldName} 仍留着弃用的 guide_mode_tooltip 键。");
            }
        }

        [Test]
        public void ProfileOptionLabels_AreDistinctWithinEachLanguage()
        {
            // The controller looks up via choices.IndexOf(displayText) -- if two profiles show the same text within one
            // language, one of them can never be selected.
            foreach (var language in new[] { SkillsLocalization.Language.English,
                                             SkillsLocalization.Language.Chinese,
                                             SkillsLocalization.Language.Russian })
            {
                SkillsLocalization.Current = language;
                var labels = new[]
                {
                    SkillsLocalization.Get("surface_profile_full"),
                    SkillsLocalization.Get("surface_profile_guide"),
                    SkillsLocalization.Get("surface_profile_no_scene_authoring"),
                };

                Assert.That(labels.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(labels.Length),
                    $"{language} 的三个档位选项文本不互异: {string.Join(" | ", labels)}");
            }
        }

        // ---------- dropdown binding ----------

        [Test]
        public void ProfileOrder_MapsChoiceIndexToEnum()
        {
            var order = GetProfileOrder();

            Assert.That(order, Is.EqualTo(ExpectedProfileOrder),
                "_profileOrder 的顺序就是 choices 的顺序，也是 index 反查唯一的依据。");
            Assert.That(order.Length, Is.EqualTo(Enum.GetValues(typeof(SurfaceProfileKind)).Length),
                "有档位没进下拉框 —— 用户就没有办法选到它。");
        }

        [Test]
        public void Dropdown_ChoiceOrder_MatchesLocalizedLabelsInProfileOrder()
        {
            SkillsLocalization.Current = SkillsLocalization.Language.English;
            var dropdown = BuildDrawerAndFindProfileDropdown(out _);

            Assert.That(dropdown.choices.Count, Is.EqualTo(ExpectedProfileOrder.Length));
            var expectedLabels = new[]
            {
                SkillsLocalization.Get("surface_profile_full"),
                SkillsLocalization.Get("surface_profile_guide"),
                SkillsLocalization.Get("surface_profile_no_scene_authoring"),
            };
            Assert.That(dropdown.choices, Is.EqualTo(expectedLabels),
                "choices 必须按 _profileOrder 的顺序填本地化文本。");
        }

        [Test]
        public void Dropdown_InitialValue_ReflectsCurrentProfile()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            var dropdown = BuildDrawerAndFindProfileDropdown(out _);

            Assert.That(dropdown.value, Is.EqualTo(dropdown.choices[1]),
                "构造时就该把当前档位回填进下拉框。");
        }

        [Test]
        public void ExternalProfileChange_SyncsDropdownValue()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            var dropdown = BuildDrawerAndFindProfileDropdown(out _);
            Assert.That(dropdown.value, Is.EqualTo(dropdown.choices[0]));

            // A profile change made outside the panel (EditorPrefs migration, test fixtures, a future CLI) must be picked
            // up by the drawer, otherwise what the user sees does not match what is actually in effect.
            SkillsSurfaceProfile.Current = SurfaceProfileKind.NoSceneAuthoring;

            Assert.That(dropdown.value, Is.EqualTo(dropdown.choices[2]),
                "SkillsSurfaceProfile.OnChanged 之后下拉框应同步到 noSceneAuthoring。");
        }

        /// <summary>
        /// This is where the last link closes in the causal chain "select item i => write _profileOrder[i]".
        ///
        /// The callback body looks up <c>_profileOrder</c> via <c>choices.IndexOf(evt.newValue)</c>, so the whole
        /// chain splits into three parts: choices[i] is the localized text of _profileOrder[i]
        /// (<see cref="Dropdown_ChoiceOrder_MatchesLocalizedLabelsInProfileOrder"/>),
        /// _profileOrder[i] is exactly the i-th profile (<see cref="ProfileOrder_MapsChoiceIndexToEnum"/>),
        /// and here, IndexOf(choices[i]) == i (no duplicate entries route the lookup to a different profile).
        ///
        /// Beyond those three parts, only one hop remains -- "the ChangeEvent actually gets dispatched to the
        /// callback" -- and that needs a UI Toolkit panel: an off-screen element tree has no panel, so SendEvent is
        /// a flat no-op; batch mode with -nographics also can't open a window (EditorWindow.GetWindow logs a
        /// no-graphic-device Error). That hop can only be exercised by clicking through it interactively in the editor; this file leaves no empty-shell test that would always be skipped on CI.
        /// </summary>
        [Test]
        public void ChoiceLookup_ResolvesEachLabelBackToItsProfile()
        {
            var dropdown = BuildDrawerAndFindProfileDropdown(out _);
            var order = GetProfileOrder();

            for (int index = 0; index < order.Length; index++)
            {
                Assert.That(dropdown.choices.IndexOf(dropdown.choices[index]), Is.EqualTo(index),
                    $"choices[{index}] 反查不回自己 —— 选项文本有重复，其中一个档位永远选不中。");
                Assert.That(order[index], Is.EqualTo(ExpectedProfileOrder[index]));
            }
        }

        [Test]
        public void ProfileHint_IsRebuiltOnEveryProfile_WithoutMutatingHiddenSets()
        {
            var guideBefore = SkillsSurfaceProfile.HiddenCategories(SurfaceProfileKind.Guide).ToArray();
            var noSceneBefore = SkillsSurfaceProfile.HiddenCategories(SurfaceProfileKind.NoSceneAuthoring).ToArray();

            BuildDrawerAndFindProfileDropdown(out var root);
            var hint = root.Q<Label>("surface-profile-hint");
            Assert.That(hint, Is.Not.Null, "抽屉里找不到 surface-profile-hint。");

            var texts = new List<string>();
            foreach (var profile in ExpectedProfileOrder)
            {
                // Changing the profile from outside goes through OnChanged -> RefreshSurfaceProfileUi -> recompute the hint text.
                // This path does not depend on event dispatch, so it still holds on an off-screen element tree.
                SkillsSurfaceProfile.Current = profile;
                Assert.That(hint.text, Is.Not.Null.And.Not.Empty, $"{profile} 档的说明文字为空。");
                texts.Add(hint.text);
            }

            Assert.That(texts.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(texts.Count),
                "三个档位的说明文字应各不相同。");

            // HiddenCategories hands out a reference to the internal HashSet; the panel must only read-iterate it and never mutate it in place.
            Assert.That(SkillsSurfaceProfile.HiddenCategories(SurfaceProfileKind.Guide).ToArray(),
                Is.EquivalentTo(guideBefore), "guide 档的隐藏集被面板改动了。");
            Assert.That(SkillsSurfaceProfile.HiddenCategories(SurfaceProfileKind.NoSceneAuthoring).ToArray(),
                Is.EquivalentTo(noSceneBefore), "noSceneAuthoring 档的隐藏集被面板改动了。");
        }

        // ---------- helpers ----------

        private static SurfaceProfileKind[] GetProfileOrder()
        {
            var field = typeof(SettingsDrawerController).GetField(
                "_profileOrder", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, "未找到 SettingsDrawerController._profileOrder。");
            return (SurfaceProfileKind[])field.GetValue(null);
        }

        // Every drawer built by a test subscribes to SkillsSurfaceProfile.OnChanged. In real usage, unsubscribing
        // happens via DetachFromPanelEvent, but an off-screen element tree has no panel, so SendEvent never dispatches;
        // so here we record the controller and call its detach handler directly in TearDown. Failing to unsubscribe
        // would let the subscription count accumulate across tests, running one extra refresh against a discarded UI tree on every subsequent profile change.
        private readonly List<SettingsDrawerController> _builtControllers = new List<SettingsDrawerController>();

        /// <summary>
        /// Builds a minimal drawer: the controller only requires a container named "drawer" under the root; window is only stored, never dereferenced.
        /// </summary>
        private DropdownField BuildDrawerAndFindProfileDropdown(out VisualElement root)
        {
            root = new VisualElement();
            root.Add(new VisualElement { name = "drawer" });
            root.Add(new VisualElement { name = "drawer-mask" });

            // Construction alone completes CloneTree + caching references + binding events + backfilling the current value.
            var controller = new SettingsDrawerController(root, null);
            _builtControllers.Add(controller);

            var dropdown = root.Q<DropdownField>("surface-profile-dropdown");
            Assert.That(dropdown, Is.Not.Null,
                "抽屉 UXML 里找不到 surface-profile-dropdown —— 名字改了或该行被删了。");
            return dropdown;
        }

        /// <summary>
        /// Calls the controller's own DetachFromPanelEvent handler to complete unsubscription. The handler completely
        /// ignores the event argument, so passing null is safe -- what we want here is just those two lines it unsubscribes.
        /// </summary>
        private static void DetachDrawer(SettingsDrawerController controller)
        {
            var handler = typeof(SettingsDrawerController).GetMethod(
                "OnRootDetached", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(handler, Is.Not.Null,
                "未找到 SettingsDrawerController.OnRootDetached —— 退订路径改了，这里要跟着改。");
            handler.Invoke(controller, new object[] { null });
        }

        private static Dictionary<string, string> GetLocalizationDictionary(string fieldName)
        {
            var field = typeof(SkillsLocalization).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, $"未找到 SkillsLocalization.{fieldName}");

            var dictionary = field.GetValue(null) as Dictionary<string, string>;
            Assert.That(dictionary, Is.Not.Null, $"SkillsLocalization.{fieldName} 不是 Dictionary<string, string>");
            return dictionary;
        }
    }
}

// Producer:Betsy
