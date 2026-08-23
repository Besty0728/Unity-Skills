using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// 设置抽屉里档位下拉框的绑定，以及三语本地化键的完整性（#5 留下的缺口）。
    ///
    /// 抽屉控制器只需要一个含名为 "drawer" 的子元素的根，window 参数只被存下来、构造期不解引用，
    /// 所以能在不开 EditorWindow 的前提下装起来。断言全部落在「选项位置 ↔ 枚举」这层映射上 ——
    /// 显示文本是本地化的，按文本反查会在切语言时碎掉，这也正是生产代码用 <c>_profileOrder</c>
    /// 按 index 反查的原因。
    /// </summary>
    [TestFixture]
    public class SurfaceProfileDrawerUiTests
    {
        /// <summary>档位这一节新增的 9 个键。三语字典各自都必须解析出非空文本。</summary>
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

        // ---------- 本地化 ----------

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
            // 这两条文案是 string.Format 的模板；哪个语言掉了 {0} 就会在面板上少掉模块名/条数，
            // 而不是报错，所以必须显式盯住。
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
            // 旧的布尔开关键留在字典里只会让下一个人以为面板上还有那个开关。
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
            // 控制器按 choices.IndexOf(displayText) 反查 —— 同语言内两个档位显示同一串文本会让
            // 其中一个永远选不中。
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

        // ---------- 下拉框绑定 ----------

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

            // 面板之外改档（EditorPrefs 迁移、测试夹具、未来的 CLI）必须让抽屉跟上，
            // 否则用户看到的档位和实际生效的不是一回事。
            SkillsSurfaceProfile.Current = SurfaceProfileKind.NoSceneAuthoring;

            Assert.That(dropdown.value, Is.EqualTo(dropdown.choices[2]),
                "SkillsSurfaceProfile.OnChanged 之后下拉框应同步到 noSceneAuthoring。");
        }

        /// <summary>
        /// 「选中第 i 项 ⇒ 写入 _profileOrder[i]」这条因果链在这里合上最后一环。
        ///
        /// 回调体是 <c>choices.IndexOf(evt.newValue)</c> 反查 <c>_profileOrder</c>，所以整条链
        /// 拆成三段：choices[i] 是 _profileOrder[i] 的本地化文本
        /// （<see cref="Dropdown_ChoiceOrder_MatchesLocalizedLabelsInProfileOrder"/>）、
        /// _profileOrder[i] 就是第 i 个档位（<see cref="ProfileOrder_MapsChoiceIndexToEnum"/>）、
        /// 以及这里的 IndexOf(choices[i]) == i（没有重复项把反查引到别的档位）。
        ///
        /// 三段之外只剩「ChangeEvent 真的派发到了回调」那一跳，而它需要一个 UI Toolkit panel：
        /// 离屏元素树没有 panel，SendEvent 直接 no-op；批处理 -nographics 下也开不出窗口
        /// （EditorWindow.GetWindow 会记一条 no-graphic-device 的 Error）。那一跳只能靠交互式
        /// 编辑器里手点，这里不留一条在 CI 上永远跳过的空壳测试。
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
                // 从外部改档，走 OnChanged → RefreshSurfaceProfileUi → 重算说明文字。
                // 这条路不依赖事件派发，所以离屏元素树上照样成立。
                SkillsSurfaceProfile.Current = profile;
                Assert.That(hint.text, Is.Not.Null.And.Not.Empty, $"{profile} 档的说明文字为空。");
                texts.Add(hint.text);
            }

            Assert.That(texts.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(texts.Count),
                "三个档位的说明文字应各不相同。");

            // HiddenCategories 交出的是内部 HashSet 的引用；面板只读遍历，绝不能原地改。
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

        // 每个测试造的抽屉都订阅了 SkillsSurfaceProfile.OnChanged。真实场景靠
        // DetachFromPanelEvent 退订，而离屏的元素树没有 panel，SendEvent 不会派发，所以这里
        // 记下控制器，TearDown 时直接调它的退订处理器。不退订会让订阅数随测试数累积，
        // 后续每次改档都要多跑一遍打在废弃 UI 树上的刷新。
        private readonly List<SettingsDrawerController> _builtControllers = new List<SettingsDrawerController>();

        /// <summary>
        /// 装一个最小抽屉：控制器只要求根下有名为 "drawer" 的容器，window 只被存下来不解引用。
        /// </summary>
        private DropdownField BuildDrawerAndFindProfileDropdown(out VisualElement root)
        {
            root = new VisualElement();
            root.Add(new VisualElement { name = "drawer" });
            root.Add(new VisualElement { name = "drawer-mask" });

            // 构造即完成 CloneTree + 缓存引用 + 绑定事件 + 回填当前值。
            var controller = new SettingsDrawerController(root, null);
            _builtControllers.Add(controller);

            var dropdown = root.Q<DropdownField>("surface-profile-dropdown");
            Assert.That(dropdown, Is.Not.Null,
                "抽屉 UXML 里找不到 surface-profile-dropdown —— 名字改了或该行被删了。");
            return dropdown;
        }

        /// <summary>
        /// 调控制器自己的 DetachFromPanelEvent 处理器完成退订。该处理器完全忽略事件参数，
        /// 所以传 null 是安全的 —— 这里要的就是它退订的那两行。
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
