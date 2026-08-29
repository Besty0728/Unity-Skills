using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class UnitySkillsWindowTabTests
    {
        private const string WindowScriptPath = "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.cs";
        private const string WindowUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.uxml";
        private const string WindowUssPath = "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.uss";

        [Test]
        public void MainTabRegistry_PreservesFiveLocalizedEntries()
        {
            var field = typeof(UnitySkillsWindow).GetField("MainTabs", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "MainTabs registry is missing.");

            var entries = ((Array)field.GetValue(null)).Cast<object>().ToArray();
            Assert.That(entries.Length, Is.EqualTo(5));

            var ids = entries.Select(entry => entry.GetType().GetField("Id").GetValue(entry).ToString()).ToArray();
            Assert.That(ids, Is.EqualTo(new[] { "Skills", "AiConfig", "UnityCli", "History", "Analytics" }));

            var localizationKeys = entries
                .Select(entry => (string)entry.GetType().GetField("LocalizationKey").GetValue(entry))
                .ToArray();
            Assert.That(localizationKeys, Is.EqualTo(new[]
            {
                "tab_skills", "tab_ai_config", "tab_unity_cli",
                "tab_history", "tab_analytics"
            }));
        }

        [Test]
        public void MainWindowMarkup_UsesSemanticTabNamesInsideHorizontalScrollView()
        {
            var uxml = File.ReadAllText(WindowUxmlPath);

            StringAssert.Contains("<ui:ScrollView name=\"tab-bar-scroll\"", uxml);
            StringAssert.Contains("mode=\"Horizontal\"", uxml);
            StringAssert.Contains("horizontal-scroller-visibility=\"Hidden\"", uxml);
            StringAssert.Contains("vertical-scroller-visibility=\"Hidden\"", uxml);
            StringAssert.Contains("name=\"tab-scroll-prev-btn\"", uxml);
            StringAssert.Contains("name=\"tab-scroll-next-btn\"", uxml);
            StringAssert.DoesNotContain("tab-btn-0", uxml);
            StringAssert.DoesNotContain("tab-content-0", uxml);

            foreach (var id in new[] { "skills", "ai-config", "unity-cli", "history", "analytics" })
            {
                StringAssert.Contains($"tab-btn-{id}", uxml);
                StringAssert.Contains($"tab-wrap-{id}", uxml);
                StringAssert.Contains($"tab-content-{id}", uxml);
                StringAssert.Contains($"tab-underline-{id}", uxml);
            }
        }

        [Test]
        public void TabLayoutStyles_KeepItemsFixedAndAllowOverflow()
        {
            var uss = File.ReadAllText(WindowUssPath);
            StringAssert.Contains(".tab-bar-scroll", uss);
            StringAssert.Contains(".tab-scroll-arrow-btn", uss);
            StringAssert.Contains(".tab-wrap", uss);
            StringAssert.Contains("flex-shrink: 0;", uss);

            var script = File.ReadAllText(WindowScriptPath);
            StringAssert.Contains("FixedTabWidth", script);
            StringAssert.Contains("EnsureTabVisible", script);
            StringAssert.Contains("ScrollTabBar", script);
            StringAssert.DoesNotContain("_tabContents[0]", script);
            StringAssert.DoesNotContain("_tabContents[1]", script);
            StringAssert.DoesNotContain("_tabContents[2]", script);
            StringAssert.DoesNotContain("_tabContents[3]", script);
        }
    }
}

// Producer:Betsy
