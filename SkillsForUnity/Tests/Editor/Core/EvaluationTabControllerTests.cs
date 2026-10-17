using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The Evaluation tab's markup, its controller on a detached root (no window, no panel, so its tracking tick never
    /// fires) and the localization contract: every runner problem code maps to tab_eval_problem_&lt;code&gt; in all three
    /// languages with the same placeholders.
    /// </summary>
    [TestFixture]
    public class EvaluationTabControllerTests
    {
        private const string TabUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/EvaluationTab.uxml";

        private static readonly string[] NamedNodes =
        {
            "eval-scroll", "eval-title", "eval-activity", "eval-intro",
            "eval-marker-value", "eval-marker-btn", "eval-marker-confirm", "eval-marker-confirm-ok", "eval-marker-confirm-cancel",
            "eval-python-value", "eval-python-detect-btn", "eval-python-override", "eval-python-use-btn", "eval-python-clear-btn",
            "eval-mode-value", "eval-mode-switch-btn", "eval-mode-restore-btn", "eval-mode-note",
            "eval-check-btn", "eval-cleanup-btn", "eval-check-summary", "eval-env-messages",
            "eval-track", "eval-tier", "eval-run-mode", "eval-lang", "eval-host", "eval-model", "eval-effort", "eval-usage-toggle",
            "eval-start-btn", "eval-copy-prompt-btn", "eval-open-prompt-btn", "eval-copied", "eval-start-messages",
            "eval-refresh-btn", "eval-session-line", "eval-task-line", "eval-task-list", "eval-submit-btn", "eval-abort-btn",
            "eval-abort-confirm", "eval-abort-confirm-ok", "eval-progress-messages",
            "eval-report-session", "eval-report-summary", "eval-report-metrics", "eval-report-tasks", "eval-report-btn",
            "eval-open-report-btn", "eval-reveal-report-btn", "eval-report-messages", "eval-history-list",
        };

        // Contract 3.1 and 3.2, plus the codes only the panel raises.
        private static readonly string[] ProblemCodes =
        {
            "PYTHON_TOO_OLD", "PROJECT_NOT_FOUND", "PROJECT_AMBIGUOUS", "PACKAGE_NOT_FOUND", "RUNNER_NOT_FOUND",
            "INSTANCE_NOT_RUNNING", "INSTANCE_MISMATCH", "VERSION_MISMATCH", "EDITOR_BUSY", "PLAY_MODE_ACTIVE",
            "MODE_NOT_BYPASS", "SURFACE_NOT_FULL", "PIPELINE_UNSUPPORTED", "PROJECT_NOT_DEDICATED", "PACKAGE_MISSING",
            "SCENE_DIRTY", "SCENE_UNTITLED", "RESIDUE_FOUND", "PRIVATE_PACK_UNREACHABLE", "PRIVATE_PACK_TLS", "VERIFICATION_PENDING",
            "PACK_PLUGIN_RANGE", "CLEANUP_NEEDS_SCENE", "BAD_ARGUMENTS", "SESSION_ACTIVE", "NO_ACTIVE_SESSION",
            "SESSION_NOT_FOUND", "SESSION_NOT_FINISHED", "ATTEMPT_NOT_FOUND", "ATTEMPT_NOT_ACTIVE", "ATTEMPT_NOT_READY",
            "ANSWER_NOT_FOUND", "RETRY_NOT_APPLICABLE", "STRICT_NEEDS_NEW_SESSION", "TASKS_INVALID", "REST_UNREACHABLE",
            "REST_ERROR", "FIXTURE_FAILED", "TEARDOWN_FAILED", "PACK_INVALID", "STATE_CORRUPT", "SCHEMA_UNSUPPORTED",
            "IO_ERROR", "INTERNAL_ERROR", "LOCK_TIMEOUT", "RUNNER_CRASHED",
            "TELEMETRY_DISABLED", "SKILL_DOCS_OUTDATED", "SKILL_DOCS_MISSING", "SETTINGS_NONSTANDARD", "PACK_MODIFIED",
            "PROJECT_USER", "PACKAGE_ROOT_AMBIGUOUS", "ISOLATION_UNVERIFIED", "RERUN", "USAGE_UNAVAILABLE",
            "LAUNCH_FAILED", "ARGUMENT_UNSAFE", "RESULT_UNREADABLE",
        };

        private SkillsLocalization.Language _savedLanguage;

        [SetUp]
        public void SetUp() => _savedLanguage = SkillsLocalization.Current;

        [TearDown]
        public void TearDown() => SkillsLocalization.Current = _savedLanguage;

        [Test]
        public void Uxml_LoadsWithEveryNamedNode()
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(TabUxmlPath);
            Assert.That(uxml, Is.Not.Null, TabUxmlPath);

            var root = new VisualElement();
            uxml.CloneTree(root);
            var missing = NamedNodes.Where(name => root.Q(name) == null).ToArray();
            Assert.That(missing, Is.Empty, "Nodes the controller queries are missing from the UXML.");
        }

        [Test]
        public void Uxml_DefaultTextIsAsciiAndHidesNothingInline()
        {
            var text = File.ReadAllText(TabUxmlPath);
            Assert.That(text.Where(c => c > 127).ToArray(), Is.Empty, "UXML default text must stay ASCII.");
            StringAssert.DoesNotContain("display: none", text);
            StringAssert.DoesNotContain("display:none", text);
        }

        [Test]
        public void Controller_BuildsOnADetachedRootAndLocalizesIt()
        {
            SkillsLocalization.Current = SkillsLocalization.Language.English;
            var root = new VisualElement();
            var controller = new EvaluationTabController(root, null);
            try
            {
                Assert.That(root.Q<ScrollView>("eval-scroll"), Is.Not.Null);
                Assert.That(root.Q<Label>("eval-title").text, Is.EqualTo(SkillsLocalization.Get("tab_eval_title")));
                Assert.That(root.Q<Button>("eval-start-btn").text, Is.EqualTo(SkillsLocalization.Get("tab_eval_start_btn")));
                Assert.That(root.Q<DropdownField>("eval-track").choices.Count, Is.EqualTo(2));
                Assert.That(root.Q<DropdownField>("eval-tier").choices.Count, Is.EqualTo(3));
                Assert.That(root.Q<DropdownField>("eval-host").choices.Count, Is.EqualTo(5));

                SkillsLocalization.Current = SkillsLocalization.Language.Chinese;
                controller.RefreshLocalization();
                Assert.That(root.Q<Label>("eval-title").text, Is.EqualTo(SkillsLocalization.Get("tab_eval_title")));
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void TabTitle_MatchesTheSessionPromptWording()
        {
            Assert.That(Dictionary("_english")["tab_evaluation"], Is.EqualTo("Evaluation"));
            Assert.That(Dictionary("_chinese")["tab_evaluation"], Is.EqualTo("\u8BC4\u6D4B"));
        }

        [TestCase("_english")]
        [TestCase("_chinese")]
        [TestCase("_russian")]
        public void EveryProblemCode_HasAKey(string dictionaryField)
        {
            var dictionary = Dictionary(dictionaryField);
            var missing = ProblemCodes
                .Select(code => EvaluationTabController.ProblemKeyPrefix + code.ToLowerInvariant())
                .Where(key => !dictionary.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                .ToArray();
            Assert.That(missing, Is.Empty);
        }

        [Test]
        public void EvaluationKeys_KeepTheSamePlaceholdersInEveryLanguage()
        {
            var english = Dictionary("_english");
            var chinese = Dictionary("_chinese");
            var russian = Dictionary("_russian");
            var keys = english.Keys.Where(key => key.StartsWith("tab_eval")).ToArray();
            Assert.That(keys.Length, Is.GreaterThan(150), "The Evaluation keys were not found.");

            var mismatched = keys.Where(key => Placeholders(english[key]) != Placeholders(chinese[key]) ||
                                               Placeholders(english[key]) != Placeholders(russian[key])).ToArray();
            Assert.That(mismatched, Is.Empty);
        }

        [Test]
        public void RussianEvaluationCopy_AvoidsCapitalsMissingFromTheAtlas()
        {
            var russian = Dictionary("_russian");
            var banned = new[] { '\u0426', '\u0427', '\u0428', '\u0429', '\u042A', '\u042C', '\u0401' };
            var offenders = russian.Where(pair => pair.Key.StartsWith("tab_eval") && pair.Value.IndexOfAny(banned) >= 0)
                .Select(pair => pair.Key).ToArray();
            Assert.That(offenders, Is.Empty);
        }

        [Test]
        public void LocalizeProblem_FillsArgsAndBlanksTheMissingOnes()
        {
            SkillsLocalization.Current = SkillsLocalization.Language.English;
            var text = EvaluationTabController.LocalizeProblem(EvaluationProblem.Local("VERSION_MISMATCH", "9.8.7"));

            StringAssert.Contains("9.8.7", text);
            StringAssert.DoesNotContain("{0}", text);
            StringAssert.DoesNotContain("{1}", text);
        }

        [Test]
        public void LocalizeProblem_UnknownCodeFallsBackToTheRunnersFix()
        {
            var problem = new EvaluationProblem { Code = "SOMETHING_NEW", Fix = "Do the new thing." };
            Assert.That(EvaluationTabController.LocalizeProblem(problem), Is.EqualTo("Do the new thing."));
            Assert.That(EvaluationTabController.LocalizeProblem(new EvaluationProblem { Code = "SOMETHING_NEW" }),
                Is.EqualTo("SOMETHING_NEW"));
        }

        [Test]
        public void LocalizeProblem_FollowsThePanelLanguage()
        {
            var problem = EvaluationProblem.Local("EDITOR_BUSY");
            SkillsLocalization.Current = SkillsLocalization.Language.English;
            var english = EvaluationTabController.LocalizeProblem(problem);
            SkillsLocalization.Current = SkillsLocalization.Language.Chinese;
            var chinese = EvaluationTabController.LocalizeProblem(problem);

            Assert.That(english, Is.EqualTo(Dictionary("_english")["tab_eval_problem_editor_busy"]));
            Assert.That(chinese, Is.EqualTo(Dictionary("_chinese")["tab_eval_problem_editor_busy"]));
        }

        private static string Placeholders(string text)
        {
            return string.Join(",", Regex.Matches(text, @"\{\d+\}").Cast<Match>().Select(m => m.Value).Distinct().OrderBy(v => v));
        }

        private static Dictionary<string, string> Dictionary(string fieldName)
        {
            var field = typeof(SkillsLocalization).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, fieldName);
            SkillsLocalization.Get("tab_evaluation");
            return (Dictionary<string, string>)field.GetValue(null);
        }
    }
}

// Producer:Betsy
