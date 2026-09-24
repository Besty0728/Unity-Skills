using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class SkillDocumentationConsistencyTests
    {
        private static readonly Regex SkillHeadingRegex =
            new Regex(@"^###\s+`?(?<name>[a-z0-9]+(?:_[a-z0-9]+)+)`?\s*$", RegexOptions.Compiled);

        /// <summary>
        /// A complete code-span in the top-level SKILL.md that looks like a skill name. Requires backticks on both
        /// sides, so a wildcard pattern like `workflow_session_*` doesn't match as a whole and needs no extra exemption.
        /// </summary>
        private static readonly Regex RootDocSkillTokenRegex =
            new Regex(@"`(?<name>[a-z0-9]+(?:_[a-z0-9]+)+)`", RegexOptions.Compiled);

        /// <summary>Module-level public functions in unity_skills.py (zero indentation, not starting with _).</summary>
        private static readonly Regex PythonModuleDefRegex =
            new Regex(@"^def (?<name>[a-z][a-z0-9_]*)\(", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Relative links in skills/SKILL.md pointing to sibling module docs: `(./&lt;module&gt;/SKILL.md)`.</summary>
        private static readonly Regex ModuleLinkRegex =
            new Regex(@"\(\./(?<module>[A-Za-z0-9._-]+)/SKILL\.md\)", RegexOptions.Compiled);

        /// <summary>Any inline markdown link, with the optional title form `[text](target "title")`.</summary>
        private static readonly Regex MarkdownLinkRegex =
            new Regex(@"\[(?<text>[^\]\n]*)\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);

        /// <summary>An ATX heading; the captured text is what the anchor slug is derived from.</summary>
        private static readonly Regex MarkdownHeadingRegex =
            new Regex(@"^#{1,6}\s+(?<text>.*?)\s*$", RegexOptions.Compiled);

        /// <summary>Explicit heading anchor `{#custom-id}`, honoured in addition to the derived slug.</summary>
        private static readonly Regex ExplicitAnchorRegex =
            new Regex(@"\{#(?<id>[A-Za-z0-9._-]+)\}\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Underscore tokens allowed in the top-level SKILL.md that were never skill names to begin with. New
        /// exceptions must be explicitly registered - this list is exactly what stops ghost skill names from leaking.
        /// </summary>
        private static readonly HashSet<string> RootDocNonSkillTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            // errorCode / retryStrategy values
            "fix_and_retry", "find_target_and_retry", "install_and_retry",
            // GET /events event types
            "compilation_started", "compilation_finished", "before_domain_reload", "after_domain_reload",
            "server_restored", "playmode_changed", "console_error", "job_completed", "job_failed",
            // Response field / body wording
            "rolled_back", "module_verb",
        };

        private static readonly HashSet<string> AdvisoryModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "architecture",
            "patterns",
            "performance",
            "asmdef",
            "async",
            "inspector",
            "blueprints",
            "adr",
            "project-scout",
            "scene-contracts",
            "script-roles",
            "scriptdesign",
            "testability",
            "shadergraph-design",
            // The following *-design entries are all pure design guides (0 ### skill endpoint definitions), same
            // as shadergraph-design - uniformly exempt from schema-first (Exact Signatures) validation to avoid false positives.
            "addressables-design",
            "dotween-design",
            "primetween-design",
            "netcode-design",
            "unitask-design",
            "yooasset-design",
            "pico-design",
            "qframework-design",
            "yaml-editing",
            // Added in v2.6.0: manual-* are pure manual-operation guides (0 REST skill endpoints), same as adr, likewise exempted.
            "manual-gameobject",
            "manual-component",
            "manual-material",
            "manual-scene",
            // skills/SKILL.md's index already states unity-cli belongs to the same category as manual-* /
            // *-design: "pure doc modules defining no REST skills." It stayed green while unlisted only because
            // that doc kept a leftover Exact Signatures + /skills/schema paragraph; removing it (valid for a zero-endpoint module) would turn it red.
            "unity-cli"
        };

        /// <summary>
        /// Module doc directories whose name is not simply a <see cref="SkillCategory"/> name. Every entry is
        /// derived from the actual [UnitySkill] declarations, never guessed:
        ///   batch    -> BatchSkills.cs registers into Workflow + Validation; there is no Batch category.
        ///   bookmark -> the bookmark_* skills declared by WorkflowSkills.cs, split into their own doc directory.
        ///   history  -> the history_* skills declared by WorkflowSkills.cs, likewise.
        ///   importer -> one doc covering the four import-settings categories, declared by
        ///               AssetImportSkills.cs / AudioSkills.cs / TextureSkills.cs / ModelSkills.cs.
        /// Debug deliberately has no entry: DiagnoseSkills.cs registers into SkillCategory.Debug, which the
        /// debug/ directory already matches by name - there is no diagnose/ directory to map.
        /// </summary>
        private static readonly Dictionary<string, SkillCategory[]> ModuleCategoryExceptions =
            new Dictionary<string, SkillCategory[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "batch", new[] { SkillCategory.Workflow, SkillCategory.Validation } },
                { "bookmark", new[] { SkillCategory.Workflow } },
                { "history", new[] { SkillCategory.Workflow } },
                {
                    "importer",
                    new[]
                    {
                        SkillCategory.AssetImport, SkillCategory.Audio, SkillCategory.Texture, SkillCategory.Model
                    }
                },
            };

        /// <summary>
        /// REST modules that intentionally define no `### skill_name` sections at all - schema-first modules whose
        /// entry routes to GET /skills/schema instead. Registration is explicit so that a module silently dropping
        /// to zero documented skills (for instance a botched entry/reference split) still fails; see
        /// <see cref="AssertEveryRestModuleDocumentsSomething"/>. An entry here that does document skills is stale
        /// and must be removed, so the list cannot rot into a blanket exemption.
        /// </summary>
        private static readonly HashSet<string> SchemaOnlyModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hybridclr",
            "importer",
            "netcode",
            "probuilder",
            "uitoolkit",
            "xr",
            "yooasset"
        };

        /// <summary>
        /// Skills deliberately documented under more than one module directory. Today this is only the Workflow
        /// module doc re-stating skills that also own a dedicated directory (batch/, bookmark/, history/), which is
        /// routing redundancy rather than drift. Anything not registered here is reported, and a registered name
        /// that is no longer duplicated is reported as stale - the duplicate check is what stops the same skill
        /// from being described two different ways in two files after the entry/reference split.
        /// </summary>
        private static readonly HashSet<string> KnownCrossModuleDuplicates = new HashSet<string>(StringComparer.Ordinal)
        {
            "batch_query_assets",
            "batch_retry_failed",
            "bookmark_delete",
            "bookmark_goto",
            "bookmark_list",
            "bookmark_set",
            "history_get_current",
            "history_redo",
            "history_undo"
        };

        /// <summary>
        /// Relative doc links that are known to be broken and predate the link check, keyed exactly as
        /// <see cref="SkillDocLinks_ShouldResolve"/> reports them. Kept so the new check can land without also
        /// rewriting docs owned by someone else; each entry is a real defect that should be fixed and deleted, and
        /// an entry whose link resolves again is reported as stale.
        /// </summary>
        private static readonly HashSet<string> KnownBrokenDocLinks = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The doc-split pilots and the skills each one owns: an entry SKILL.md with the module's shared rules plus
        /// one reference/&lt;skill_name&gt;.md per skill. Ownership is resolved per pilot rather than by directory
        /// name: gameobject, component and script map to the like-named category, but batch maps to Workflow +
        /// Validation, which also hold the workflow/ and validation/ modules' skills - so the batch module is defined
        /// as what BatchSkills.cs declares.
        /// </summary>
        private static readonly (string Module, Func<CodeSkill, bool> Owns)[] PilotModules =
        {
            ("gameobject", skill => skill.Attribute.Category == SkillCategory.GameObject),
            ("component", skill => skill.Attribute.Category == SkillCategory.Component),
            ("batch", skill => skill.Method.DeclaringType?.Name == "BatchSkills"),
            ("script", skill => skill.Attribute.Category == SkillCategory.Script),
        };

        /// <summary>
        /// Byte budget per pilot module entry, same measurement as <see cref="RootSkillDoc_ShouldStayWithinByteBudget"/>:
        /// LF-normalised UTF-8 over the whole entry file, frontmatter included. The entry is what an agent reads before
        /// every call to the module, so its size is a recurring cost; reference/*.md files are read on demand and are
        /// deliberately not budgeted.
        ///
        /// A value of -1 means "budget not decided yet" and fails the test on purpose: the real numbers come from
        /// the benchmark comparison, and a missing budget must never read as a pass.
        /// </summary>
        private static readonly Dictionary<string, int> PilotEntryByteBudgets = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // round4 benchmark candidate entries plus ~4% headroom.
            { "gameobject", 5120 },
            { "component", 6208 },
            { "batch", 8448 },
            { "script", 5184 }
        };

        private static readonly HashSet<string> ExactSignatureOptionalModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "batch",
            "editor",
            "profiler",
            "scene",
            "timeline",
            "workflow"
        };

        [Test]
        public void SkillDocumentation_ShouldMatchCodeDefinitions()
        {
            var codeSkills = LoadCodeSkills();
            var docSkills = LoadDocumentedSkills();
            var issues = new List<string>();

            AssertSchemaFirstDocumentation(GetDocsRoot(), issues);
            AssertNoDuplicateDefinitions(docSkills, issues);
            AssertEveryRestModuleDocumentsSomething(docSkills, issues);

            foreach (var ghost in docSkills
                         .Where(doc => !codeSkills.ContainsKey(doc.Name))
                         .OrderBy(doc => doc.Name, StringComparer.Ordinal)
                         .ThenBy(doc => doc.RelativePath, StringComparer.Ordinal))
            {
                issues.Add($"幽灵 Skill: {ghost.RelativePath}:{ghost.Line} -> `{ghost.Name}`");
            }

            // Every occurrence is compared, not just one per name: after the entry/reference split the same skill
            // may be described in two files, and checking only the last one read would silently drop the other.
            foreach (var doc in docSkills
                         .OrderBy(x => x.Name, StringComparer.Ordinal)
                         .ThenBy(x => x.RelativePath, StringComparer.Ordinal))
            {
                if (codeSkills.TryGetValue(doc.Name, out var codeSkill))
                {
                    CompareParameters(doc.Name, codeSkill, doc, issues);
                }
            }

            AssertNoIssues(issues, "Skill 文档与 schema-first 约束不一致");
        }

        [Test]
        public void UnitySkillMetadata_ShouldBeComplete()
        {
            var issues = new List<string>();

            foreach (var skill in LoadCodeSkills().Values.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var attr = skill.Attribute;
                var owner = $"{skill.Method.DeclaringType?.Name}.{skill.Method.Name}";

                if (attr.Category == SkillCategory.Uncategorized)
                {
                    issues.Add($"缺少 Category: `{skill.Name}` ({owner})");
                }

                if (attr.Operation == 0)
                {
                    issues.Add($"缺少 Operation: `{skill.Name}` ({owner})");
                }

                if (attr.Tags == null || attr.Tags.Length == 0)
                {
                    issues.Add($"缺少 Tags: `{skill.Name}` ({owner})");
                }

                if (skill.Method.ReturnType != typeof(void) && (attr.Outputs == null || attr.Outputs.Length == 0))
                {
                    issues.Add($"缺少 Outputs: `{skill.Name}` ({owner})");
                }
            }

            AssertNoIssues(issues, "UnitySkill 元数据不完整");
        }

        [Test]
        public void YooAssetSkills_ShouldHaveEnglishAndChineseLocalization()
        {
            var yooAssetSkillNames = LoadCodeSkills()
                .Keys
                .Where(name => name.StartsWith("yooasset_", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.That(yooAssetSkillNames, Is.Not.Empty, "未发现 YooAsset Skill。");

            var english = GetLocalizationDictionary("_english");
            var chinese = GetLocalizationDictionary("_chinese");
            var issues = new List<string>();

            foreach (var skillName in yooAssetSkillNames)
            {
                if (!english.TryGetValue(skillName, out var englishText) || string.IsNullOrWhiteSpace(englishText))
                {
                    issues.Add($"缺少英文翻译: `{skillName}`");
                }

                if (!chinese.TryGetValue(skillName, out var chineseText) || string.IsNullOrWhiteSpace(chineseText))
                {
                    issues.Add($"缺少中文翻译: `{skillName}`");
                }
            }

            AssertNoIssues(issues, "YooAsset Skill 本地化不完整");
        }

        // ============================================================
        // Reference consistency for the top-level unity-skills~/SKILL.md (issue #52)
        //
        // The other tests only traverse skills/*/SKILL.md via GetDocsRoot(), giving zero coverage of the
        // top-level SKILL.md - 25 ghost skill names and one bare helper name lurked undetected, eventually
        // causing the agent to repeatedly call the nonexistent `get_skill_schema` / `health_check`. The following three tests close this blind spot.
        // ============================================================

        [Test]
        public void RootSkillDoc_ShouldNotReferenceUnregisteredSkillNames()
        {
            var registered = LoadCodeSkills().Keys;
            var doc = ReadRootSkillDoc(out var docPath);
            var issues = new List<string>();

            foreach (Match match in RootDocSkillTokenRegex.Matches(doc))
            {
                var token = match.Groups["name"].Value;
                if (registered.Contains(token) || RootDocNonSkillTokens.Contains(token))
                {
                    continue;
                }

                issues.Add($"幽灵 Skill: SKILL.md -> `{token}`（不在已注册 skill 中；" +
                           "若它本就不是 skill 名，登记到 RootDocNonSkillTokens）");
            }

            AssertNoIssues(issues, $"顶层 SKILL.md 引用了未注册的 skill 名: {docPath}");
        }

        [Test]
        public void RootSkillDoc_ShouldQualifyPythonHelperCalls()
        {
            var helpers = LoadPythonHelperNames();
            var doc = ReadRootSkillDoc(out var docPath);

            // Only matches the call form `name(`: the "health" in `GET /health` in the doc is unrelated to the
            // helper of the same name and shouldn't be treated as an unqualified call.
            var pattern = new Regex(
                @"(?<!unity_skills\.)\b(?<name>" +
                string.Join("|", helpers.OrderByDescending(h => h.Length).Select(Regex.Escape)) +
                @")\s*\(",
                RegexOptions.Compiled);

            var issues = pattern.Matches(doc)
                .Cast<Match>()
                .Select(m => m.Groups["name"].Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(name => $"Python helper 缺少 `unity_skills.` 前缀: `{name}()` —— " +
                                $"裸名会被 agent 当作 skill 名 POST 到 /skill/{name}")
                .ToList();

            AssertNoIssues(issues, $"顶层 SKILL.md 的 Python helper 名未限定: {docPath}");
        }

        [Test]
        public void ClientHelperRestEquivalents_ShouldMapRealHelpersThatAreNotSkills()
        {
            var field = typeof(SkillRouter).GetField(
                "k_ClientHelperRestEquivalents", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, "未找到 SkillRouter.k_ClientHelperRestEquivalents");

            var table = field.GetValue(null) as Dictionary<string, string>;
            Assert.That(table, Is.Not.Null, "k_ClientHelperRestEquivalents 类型不是 Dictionary<string, string>");
            Assert.That(table, Is.Not.Empty, "k_ClientHelperRestEquivalents 为空");

            var helpers = LoadPythonHelperNames();
            var registered = LoadCodeSkills().Keys;
            var issues = new List<string>();

            foreach (var entry in table.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!helpers.Contains(entry.Key))
                {
                    issues.Add($"表键不是 unity_skills.py 的模块级 helper: `{entry.Key}`" +
                               "（拼错的键永远命中不了，是静默失效）");
                }

                if (registered.Contains(entry.Key))
                {
                    issues.Add($"表键与已注册 skill 同名: `{entry.Key}`" +
                               "（该名会走正常执行路径，定向纠正永远不触发）");
                }

                if (string.IsNullOrWhiteSpace(entry.Value))
                {
                    issues.Add($"表值为空: `{entry.Key}`");
                }
            }

            AssertNoIssues(issues, "SkillRouter.k_ClientHelperRestEquivalents 与 Python 客户端脱钩");
        }

        // ============================================================
        // Doc-tree reachability and budget (v2.7)
        // ============================================================

        /// <summary>
        /// Module directories and skills/SKILL.md index links must be bidirectionally flush, no exemptions.
        ///
        /// The index is the agent's only entry point for finding module docs: a directory that exists but isn't
        /// registered is effectively invisible, and an index entry pointing at a nonexistent directory sends the
        /// agent to read a 404. Both directions must have an empty set difference - the AdvisoryModules exemption doesn't apply here, since pure design guides must be findable too.
        /// </summary>
        [Test]
        public void SkillsIndexDoc_ShouldLinkEveryModuleDirectory_BothWays()
        {
            var docsRoot = GetDocsRoot();
            var indexPath = Path.Combine(docsRoot, "SKILL.md");
            Assert.That(File.Exists(indexPath), Is.True, $"模块索引不存在: {indexPath}");
            var index = File.ReadAllText(indexPath);

            var directories = Directory.GetDirectories(docsRoot)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.Ordinal);
            Assert.That(directories, Is.Not.Empty, $"{docsRoot} 下没有任何模块目录。");

            var linked = ModuleLinkRegex.Matches(index)
                .Cast<Match>()
                .Select(m => m.Groups["module"].Value)
                .ToHashSet(StringComparer.Ordinal);

            var issues = new List<string>();
            foreach (var missing in directories.Except(linked).OrderBy(x => x, StringComparer.Ordinal))
            {
                issues.Add($"模块目录未登记进索引: {missing}/SKILL.md" +
                           "（agent 只从 skills/SKILL.md 找模块，没登记等于这份文档不存在）");
            }

            foreach (var dangling in linked.Except(directories).OrderBy(x => x, StringComparer.Ordinal))
            {
                issues.Add($"索引指向不存在的模块目录: {dangling}");
            }

            AssertNoIssues(issues, $"模块索引与目录树不齐平: {indexPath}");
        }

        /// <summary>
        /// Every module doc directory must resolve to a verdict: REST (owns at least one SkillCategory) or advisory
        /// (owns none and is registered as documentation-only). Anything else is an error.
        ///
        /// The old heuristic - "a module with no `### skill_name` heading is advisory" - cannot survive the
        /// entry/reference doc split, because a migrated REST entry legitimately has no such heading any more. It was
        /// already unsafe for a different reason: an optional-package module reports zero skills on a machine without
        /// the package, so "no skills found" never proves "advisory". Ownership therefore comes from the
        /// <see cref="SkillCategory"/> enum plus <see cref="ModuleCategoryExceptions"/>, which are both independent of
        /// what happens to be installed.
        /// </summary>
        [Test]
        public void SkillDocModules_ShouldMapToCategoriesOrBeRegisteredAdvisory()
        {
            var docsRoot = GetDocsRoot();
            Assert.That(Directory.Exists(docsRoot), Is.True, $"技能文档目录不存在: {docsRoot}");
            var issues = new List<string>();
            var directories = Directory.GetDirectories(docsRoot)
                .Select(Path.GetFileName)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            Assert.That(directories, Is.Not.Empty, $"{docsRoot} 下没有任何模块目录。");

            var claimed = new HashSet<SkillCategory>();
            foreach (var moduleName in directories)
            {
                var isRest = TryMapModuleToCategories(moduleName, out var categories);
                var isAdvisory = AdvisoryModules.Contains(moduleName);

                if (isRest && isAdvisory)
                {
                    issues.Add($"归属矛盾: {moduleName} 既映射到 SkillCategory " +
                               $"[{string.Join(", ", categories)}]，又登记在 AdvisoryModules 中。" +
                               "Advisory 模块不得拥有任何 Category；二者只能保留其一");
                    continue;
                }

                if (isRest)
                {
                    foreach (var category in categories)
                    {
                        claimed.Add(category);
                    }

                    continue;
                }

                if (!isAdvisory)
                {
                    issues.Add($"模块归属未知: {moduleName} 既不对应任何 SkillCategory，也未登记在 AdvisoryModules 中。" +
                               "若它是新的 REST 模块，补 SkillCategory 或在 ModuleCategoryExceptions 中登记映射；" +
                               "若它是纯指导文档，登记到 AdvisoryModules");
                }
            }

            foreach (var stale in AdvisoryModules
                         .Except(directories, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                issues.Add($"AdvisoryModules 登记已失效: 不存在名为 {stale} 的模块目录");
            }

            foreach (var key in ModuleCategoryExceptions.Keys
                         .Except(directories, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                issues.Add($"ModuleCategoryExceptions 登记已失效: 不存在名为 {key} 的模块目录");
            }

            // A category nobody documents is invisible to the agent: it can only be reached by guessing skill names.
            foreach (var orphan in AllSkillCategories.Except(claimed).OrderBy(x => x.ToString(), StringComparer.Ordinal))
            {
                issues.Add($"SkillCategory {orphan} 没有任何模块目录承载文档。" +
                           "新增 Category 时要么建同名目录，要么在 ModuleCategoryExceptions 中把它挂到现有目录");
            }

            AssertNoIssues(issues, "模块目录与 SkillCategory 归属不齐平");
        }

        /// <summary>
        /// The doc-split pilots (<see cref="PilotModules"/>) must document every skill their module owns, and nothing
        /// else. This is the guard against a migration silently losing skills: moving sections between files is
        /// exactly the kind of edit where a section can be dropped with nothing left to compare against.
        /// </summary>
        [Test]
        public void PilotSkillDocs_ShouldDocumentEverySkillOfTheirModule()
        {
            var codeSkills = LoadCodeSkills();
            var docSkills = LoadDocumentedSkills();
            var issues = new List<string>();

            foreach (var pilot in PilotModules)
            {
                AssertPilotCoverage(pilot.Module, codeSkills.Values.Where(pilot.Owns), docSkills, issues);
            }

            AssertNoIssues(issues, "试点模块的文档技能集合与代码不一致");
        }

        /// <summary>
        /// A pilot keeps each skill's section in reference/&lt;skill_name&gt;.md, one file per skill, so the entry can
        /// route by name alone ("details live in reference/&lt;skill&gt;.md") without a link per row. That addressing
        /// only works if the file name is the skill name and every owned skill has its file: each reference file must
        /// define exactly one `### skill_name` section whose name equals the file name, and no owned skill may lack one
        /// (a section left in the entry or in a topic file is not addressable that way).
        /// </summary>
        [Test]
        public void PilotReferenceFiles_ShouldEachDefineTheSkillTheyAreNamedAfter()
        {
            var codeSkills = LoadCodeSkills();
            var docsRoot = GetDocsRoot();
            var issues = new List<string>();

            foreach (var pilot in PilotModules)
            {
                var moduleDir = Path.Combine(docsRoot, pilot.Module);
                var referenceDir = Path.Combine(moduleDir, "reference");
                if (!Directory.Exists(referenceDir))
                {
                    issues.Add($"{pilot.Module}: 缺少 reference/ 目录（试点模块的逐技能章节放在 reference/<skill_name>.md）");
                    continue;
                }

                var referenceFiles = Directory
                    .GetFiles(referenceDir, "*.md", SearchOption.TopDirectoryOnly)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();

                foreach (var file in referenceFiles)
                {
                    var expected = Path.GetFileNameWithoutExtension(file);
                    var defined = ParseDocumentedSkills(file, pilot.Module, moduleDir);
                    var where = $"{pilot.Module}/reference/{Path.GetFileName(file)}";

                    if (defined.Count != 1)
                    {
                        issues.Add($"{where}: 定义了 {defined.Count} 个 `### skill_name` 段" +
                                   (defined.Count > 0 ? $"（{string.Join(", ", defined.Select(doc => $"`{doc.Name}`"))}）" : string.Empty) +
                                   $"，应恰好一个 `### {expected}`（一个文件一个技能，专题内容并入相关技能的文件）");
                    }
                    else if (!string.Equals(defined[0].Name, expected, StringComparison.Ordinal))
                    {
                        issues.Add($"{where}:{defined[0].Line}: 定义的是 `### {defined[0].Name}`，与文件名不符；" +
                                   $"文件名必须等于技能名（改名为 {defined[0].Name}.md）");
                    }
                }

                var fileNames = referenceFiles
                    .Select(Path.GetFileNameWithoutExtension)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var missing in codeSkills.Values
                             .Where(pilot.Owns)
                             .Select(skill => skill.Name)
                             .Where(name => !fileNames.Contains(name))
                             .OrderBy(name => name, StringComparer.Ordinal))
                {
                    issues.Add($"{pilot.Module}: 代码中的 `{missing}` 没有 reference/{missing}.md");
                }
            }

            AssertNoIssues(issues, "试点模块的 reference/<skill_name>.md 与技能不一一对应");
        }

        /// <summary>
        /// Every relative markdown link in the shipped docs must resolve - both the file and, when present, the
        /// anchor. A broken link costs the agent a wasted read and a guess, which is precisely what the entry /
        /// reference split is supposed to avoid. http(s) links are out of scope (no network in tests).
        /// </summary>
        [Test]
        public void SkillDocLinks_ShouldResolve()
        {
            var packageRoot = GetPackageRoot();
            var anchorCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var issues = new List<string>();
            var reported = new HashSet<string>(StringComparer.Ordinal);
            var checkedLinks = 0;

            foreach (var sourcePath in GetLinkCheckSources())
            {
                var sourceRelative = ToPackageRelativePath(packageRoot, sourcePath);
                var sourceDirectory = Path.GetDirectoryName(sourcePath);
                var text = File.ReadAllText(sourcePath);

                foreach (Match match in MarkdownLinkRegex.Matches(text))
                {
                    var target = match.Groups["target"].Value;
                    if (!IsRelativeDocLink(target))
                    {
                        continue;
                    }

                    checkedLinks++;
                    var hashIndex = target.IndexOf('#');
                    var filePart = hashIndex < 0 ? target : target.Substring(0, hashIndex);
                    var anchor = hashIndex < 0 ? null : target.Substring(hashIndex + 1);
                    var key = $"{sourceRelative} -> {target}";

                    string resolved;
                    try
                    {
                        resolved = filePart.Length == 0
                            ? sourcePath
                            : Path.GetFullPath(Path.Combine(sourceDirectory, filePart));
                    }
                    catch (Exception)
                    {
                        resolved = null;
                    }

                    if (resolved == null || (!File.Exists(resolved) && !Directory.Exists(resolved)))
                    {
                        AddLinkIssue(issues, reported, key, "链接指向的文件不存在");
                        continue;
                    }

                    if (Directory.Exists(resolved) || string.IsNullOrEmpty(anchor))
                    {
                        continue;
                    }

                    if (!anchorCache.TryGetValue(resolved, out var anchors))
                    {
                        anchors = ReadHeadingAnchors(resolved);
                        anchorCache[resolved] = anchors;
                    }

                    if (!anchors.Contains(anchor.ToLowerInvariant()))
                    {
                        AddLinkIssue(issues, reported, key,
                            $"目标文件中没有匹配的标题锚点（该文件共 {anchors.Count} 个锚点）");
                    }
                }
            }

            Assert.That(checkedLinks, Is.GreaterThan(0), "未扫描到任何相对文档链接，链接检查形同虚设。");

            foreach (var stale in KnownBrokenDocLinks.Except(reported).OrderBy(x => x, StringComparer.Ordinal))
            {
                issues.Add($"KnownBrokenDocLinks 登记已失效: `{stale}` 现在能正常解析，删除该条目");
            }

            AssertNoIssues(issues, "文档中的相对链接无法解析");
        }

        /// <summary>
        /// Per-module entry byte budget for the doc-split pilots, measured exactly like
        /// <see cref="RootSkillDoc_ShouldStayWithinByteBudget"/>. See <see cref="PilotEntryByteBudgets"/> for why an
        /// undecided budget fails instead of passing.
        /// </summary>
        [Test]
        public void PilotSkillDoc_ShouldStayWithinByteBudget()
        {
            var docsRoot = GetDocsRoot();
            var issues = new List<string>();

            foreach (var budget in PilotEntryByteBudgets.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var entryPath = Path.Combine(docsRoot, budget.Key, "SKILL.md");
                if (!File.Exists(entryPath))
                {
                    issues.Add($"试点入口不存在: {budget.Key}/SKILL.md ({entryPath})");
                    continue;
                }

                var normalised = File.ReadAllText(entryPath).Replace("\r\n", "\n").Replace("\r", "\n");
                var actual = Encoding.UTF8.GetByteCount(normalised);

                if (budget.Value < 0)
                {
                    issues.Add($"{budget.Key}/SKILL.md 预算尚未确定（登记值 {budget.Value}），当前实测 {actual} 字节。" +
                               "预算数值应在基准对比通过后由 lead 填入 PilotEntryByteBudgets；" +
                               "在此之前本测试故意失败，避免\"没有预算\"被当成\"通过预算\"。" +
                               "do not raise the budget without regression evidence; move content to reference/");
                    continue;
                }

                if (actual > budget.Value)
                {
                    issues.Add($"{budget.Key}/SKILL.md 为 {actual} 字节，超出 {budget.Value} 字节预算 " +
                               $"{actual - budget.Value} 字节。入口在每次调用该模块前都会被读取，体积是重复成本；" +
                               "do not raise the budget without regression evidence; move content to reference/");
                }
            }

            AssertNoIssues(issues, "试点模块入口超出字节预算（口径：LF 归一化 UTF-8，含 frontmatter）");
        }

        /// <summary>
        /// The manual-* docs referenced in the SURFACE_EXCLUDED payload must genuinely exist.
        ///
        /// That path is the entire basis for making the rejection actionable: the agent is told "read this doc,
        /// then walk the user through it manually" - a broken path turns an actionable rejection into a dead end.
        /// <see cref="SkillsSurfaceProfile.ManualDocFor"/> returns a path relative to the package root, checked against disk one by one here.
        /// </summary>
        [Test]
        public void ManualDocsReferencedBySurfaceProfile_ShouldExistOnDisk()
        {
            var packageRoot = GetPackageRoot();
            var issues = new List<string>();

            foreach (SkillCategory category in Enum.GetValues(typeof(SkillCategory)))
            {
                var relativePath = SkillsSurfaceProfile.ManualDocFor(category);
                if (string.IsNullOrEmpty(relativePath))
                {
                    continue;
                }

                var absolutePath = Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absolutePath))
                {
                    issues.Add($"{category} 指向的 manual 文档不存在: {relativePath}" +
                               "（SURFACE_EXCLUDED 会把这条路径交给 agent，失效即拒绝无法执行）");
                }
            }

            AssertNoIssues(issues, "SkillsSurfaceProfile.ManualDocFor 指向了不存在的文档");
        }

        /// <summary>
        /// The byte budget for the top-level SKILL.md. This doc is read into context in full every session, so its
        /// size is a fixed cost every user pays together - the cap exists to force new content down into references/.
        /// Measured on LF-normalised UTF-8 content, not the on-disk file: a Windows checkout with core.autocrlf=true
        /// rewrites every line break to CRLF, which inflated the same document past the budget (issue #59).
        /// </summary>
        [Test]
        public void RootSkillDoc_ShouldStayWithinByteBudget()
        {
            const int budgetBytes = 8192;

            var normalised = ReadRootSkillDoc(out _).Replace("\r\n", "\n").Replace("\r", "\n");
            var actual = Encoding.UTF8.GetByteCount(normalised);

            Assert.That(actual, Is.LessThanOrEqualTo(budgetBytes),
                $"顶层 SKILL.md 为 {actual} 字节，超出 {budgetBytes} 字节预算 {actual - budgetBytes} 字节。" +
                "这份文档每次会话都全量入上下文；要加内容请先把等量内容下沉到 references/ " +
                "（见 references/SKILL_FULL.md 与 references/README.md），不要抬预算。");
        }

        private static void CompareParameters(string skillName, CodeSkill codeSkill, DocSkill docSkill, List<string> issues)
        {
            var codeParams = codeSkill.Parameters;
            var docParams = docSkill.Parameters;
            var isBatchEnvelope =
                skillName.EndsWith("_batch", StringComparison.Ordinal) &&
                codeParams.ContainsKey("items") &&
                docParams.ContainsKey("items");

            foreach (var docParam in docParams.Values.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (isBatchEnvelope && !string.Equals(docParam.Name, "items", StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsLooseParameterShorthand(docParam.Name))
                {
                    continue;
                }

                if (!codeParams.TryGetValue(docParam.Name, out var codeParam))
                {
                    issues.Add($"文档多出参数: `{skillName}.{docParam.Name}` ({docSkill.RelativePath})");
                    continue;
                }

                // Type/Required comparison is enabled for table rows only: bullet and inline-list rows cannot
                // express "optional" (TryParseBulletParameterRow hard-codes Required=true; the inline backtick
                // list hard-codes both Type="" and Required=true), so comparing them here would just report the
                // format's own inability to say "optional", not a real documentation error.
                if (docParam.Source != DocParameterSource.Table)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(docParam.Type) && !TypesMatch(docParam.Type, codeParam.Type))
                {
                    issues.Add($"参数类型不符: `{skillName}.{docParam.Name}` 文档写 `{docParam.Type}`，代码是 `{codeParam.Type}` ({docSkill.RelativePath})");
                }

                if (docParam.Required != codeParam.Required)
                {
                    issues.Add($"参数必填标记不符: `{skillName}.{docParam.Name}` 文档写 Required={docParam.Required}，代码是 Required={codeParam.Required} ({docSkill.RelativePath})");
                }
            }

        }

        /// <summary>All real categories; Uncategorized is the "not set" sentinel and never owns a module.</summary>
        private static readonly SkillCategory[] AllSkillCategories = Enum
            .GetValues(typeof(SkillCategory))
            .Cast<SkillCategory>()
            .Where(category => category != SkillCategory.Uncategorized)
            .ToArray();

        /// <summary>
        /// Maps a module doc directory to the categories it documents: the like-named SkillCategory, or an explicit
        /// entry in <see cref="ModuleCategoryExceptions"/>. Returning false means the directory owns no REST skills.
        /// </summary>
        private static bool TryMapModuleToCategories(string moduleName, out SkillCategory[] categories)
        {
            if (ModuleCategoryExceptions.TryGetValue(moduleName, out categories))
            {
                return true;
            }

            foreach (var category in AllSkillCategories)
            {
                if (string.Equals(category.ToString(), moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    categories = new[] { category };
                    return true;
                }
            }

            categories = Array.Empty<SkillCategory>();
            return false;
        }

        private static bool IsRestModule(string moduleName)
        {
            return TryMapModuleToCategories(moduleName, out _);
        }

        /// <summary>
        /// A skill must be defined once. Twice in one module - whether in one file or split across the entry and its
        /// reference - means two parameter tables that can drift apart with nothing comparing them.
        /// </summary>
        private static void AssertNoDuplicateDefinitions(List<DocSkill> docSkills, List<string> issues)
        {
            var actualCrossModule = new HashSet<string>(StringComparer.Ordinal);

            foreach (var group in docSkills
                         .GroupBy(doc => doc.Name, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var occurrences = group
                    .OrderBy(doc => doc.RelativePath, StringComparer.Ordinal)
                    .ThenBy(doc => doc.Line)
                    .ToList();
                if (occurrences.Count == 1)
                {
                    continue;
                }

                var where = string.Join(", ", occurrences.Select(doc => $"{doc.RelativePath}:{doc.Line}"));

                foreach (var perModule in occurrences
                             .GroupBy(doc => doc.Module, StringComparer.Ordinal)
                             .Where(moduleGroup => moduleGroup.Count() > 1)
                             .OrderBy(moduleGroup => moduleGroup.Key, StringComparer.Ordinal))
                {
                    issues.Add($"模块内重复定义: `{group.Key}` 在 {perModule.Key} 中出现 {perModule.Count()} 次" +
                               $"（{string.Join(", ", perModule.Select(doc => $"{doc.ModuleFile}:{doc.Line}"))}）——" +
                               "一个 skill 只能在模块的一个文件里定义一次");
                }

                var modules = occurrences
                    .Select(doc => doc.Module)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (modules.Count < 2)
                {
                    continue;
                }

                actualCrossModule.Add(group.Key);
                if (!KnownCrossModuleDuplicates.Contains(group.Key))
                {
                    issues.Add($"跨模块重复定义: `{group.Key}`（{where}）——" +
                               "若确属有意的路由冗余，登记到 KnownCrossModuleDuplicates；否则删掉其中一处");
                }
            }

            foreach (var stale in KnownCrossModuleDuplicates
                         .Except(actualCrossModule)
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                issues.Add($"KnownCrossModuleDuplicates 登记已失效: `{stale}` 已不再跨模块重复，删除该条目");
            }
        }

        /// <summary>
        /// Guards against the silent zero comparison: a REST module whose `*.md` files together define no skill at
        /// all is compared against nothing and passes for the wrong reason. Genuinely schema-first modules are
        /// registered in <see cref="SchemaOnlyModules"/>, and that registration is itself checked for staleness.
        /// </summary>
        private static void AssertEveryRestModuleDocumentsSomething(List<DocSkill> docSkills, List<string> issues)
        {
            var documentedModules = docSkills
                .Select(doc => doc.Module)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var moduleDir in Directory.GetDirectories(GetDocsRoot()).OrderBy(x => x, StringComparer.Ordinal))
            {
                var moduleName = Path.GetFileName(moduleDir);
                if (!IsRestModule(moduleName))
                {
                    continue;
                }

                var documentsSkills = documentedModules.Contains(moduleName);
                var registeredSchemaOnly = SchemaOnlyModules.Contains(moduleName);

                if (!documentsSkills && !registeredSchemaOnly)
                {
                    issues.Add($"静默零比对: REST 模块 {moduleName} 的所有 *.md 加起来定义了 0 个 `### skill_name` 段，" +
                               "该模块因此不参与任何一致性比对。若这是有意的 schema-first 模块，登记到 SchemaOnlyModules；" +
                               "若是迁移时丢了内容，把逐技能章节补回模块的 *.md（试点模块为 reference/<skill_name>.md）");
                }
                else if (documentsSkills && registeredSchemaOnly)
                {
                    issues.Add($"SchemaOnlyModules 登记已失效: {moduleName} 现在定义了 `### skill_name` 段，删除该条目");
                }
            }
        }

        private static void AssertPilotCoverage(
            string module, IEnumerable<CodeSkill> ownedSkills, List<DocSkill> docSkills, List<string> issues)
        {
            var code = ownedSkills.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal);
            Assert.That(code, Is.Not.Empty,
                $"试点模块 {module} 在代码中没有匹配到任何 skill —— 归属规则已经失效，" +
                "别让空集合比空集合比出一个通过。");

            var documented = docSkills
                .Where(doc => string.Equals(doc.Module, module, StringComparison.OrdinalIgnoreCase))
                .Select(doc => doc.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var missing in code.Except(documented).OrderBy(x => x, StringComparer.Ordinal))
            {
                issues.Add($"{module}: 代码中有 `{missing}`，但模块的 *.md 里没有对应的 `### {missing}` 段 —— " +
                           $"迁移不得丢技能，把它的章节补进 reference/{missing}.md");
            }

            foreach (var extra in documented.Except(code).OrderBy(x => x, StringComparer.Ordinal))
            {
                issues.Add($"{module}: 文档定义了 `{extra}`，但它不属于该模块");
            }
        }

        /// <summary>Docs whose relative links are checked: the root entry, references/ and every module `*.md`.</summary>
        private static List<string> GetLinkCheckSources()
        {
            var packageRoot = GetPackageRoot();
            var docsRoot = GetDocsRoot();
            var sources = new List<string>();

            var rootDoc = Path.Combine(packageRoot, "SKILL.md");
            if (File.Exists(rootDoc))
            {
                sources.Add(rootDoc);
            }

            var referencesDir = Path.Combine(packageRoot, "references");
            if (Directory.Exists(referencesDir))
            {
                sources.AddRange(Directory
                    .GetFiles(referencesDir, "*.md", SearchOption.TopDirectoryOnly)
                    .OrderBy(x => x, StringComparer.Ordinal));
            }

            var indexDoc = Path.Combine(docsRoot, "SKILL.md");
            if (File.Exists(indexDoc))
            {
                sources.Add(indexDoc);
            }

            if (!Directory.Exists(docsRoot))
            {
                return sources;
            }

            foreach (var moduleDir in Directory.GetDirectories(docsRoot).OrderBy(x => x, StringComparer.Ordinal))
            {
                sources.AddRange(EnumerateModuleDocs(moduleDir));
            }

            return sources;
        }

        /// <summary>
        /// Every markdown file that documents a module: the files directly inside skills/&lt;module&gt;/ plus the
        /// per-skill detail files in its optional reference/ sub-directory (one level, nothing deeper). Both
        /// LoadDocumentedSkills and the link check use this so a skill defined in reference/ can never fall out of
        /// the consistency comparison.
        /// </summary>
        private static IEnumerable<string> EnumerateModuleDocs(string moduleDir)
        {
            var files = Directory.GetFiles(moduleDir, "*.md", SearchOption.TopDirectoryOnly).ToList();
            var referenceDir = Path.Combine(moduleDir, "reference");
            if (Directory.Exists(referenceDir))
            {
                files.AddRange(Directory.GetFiles(referenceDir, "*.md", SearchOption.TopDirectoryOnly));
            }

            return files.OrderBy(x => x, StringComparer.Ordinal);
        }

        /// <summary>
        /// True for targets that are meant to address a document. Anything without a slash, a `.md` suffix or a
        /// leading `#` is not a doc path - `[Camera](Overlay)` inside an ASCII diagram is markdown link syntax but
        /// was never a link to anything.
        /// </summary>
        private static bool IsRelativeDocLink(string target)
        {
            if (string.IsNullOrEmpty(target) ||
                target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (target.StartsWith("#", StringComparison.Ordinal))
            {
                return true;
            }

            var hashIndex = target.IndexOf('#');
            var filePart = hashIndex < 0 ? target : target.Substring(0, hashIndex);
            return filePart.Length == 0 ||
                   filePart.IndexOf('/') >= 0 ||
                   filePart.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddLinkIssue(List<string> issues, HashSet<string> reported, string key, string reason)
        {
            reported.Add(key);
            if (KnownBrokenDocLinks.Contains(key))
            {
                return;
            }

            issues.Add($"链接无法解析: {key} —— {reason}");
        }

        /// <summary>
        /// All anchors a heading in the file can be addressed by: the GitHub-style slug plus, when the heading ends
        /// with an explicit `{#custom-id}`, that id. Repeated slugs get GitHub's `-1`, `-2` ... suffixes.
        /// </summary>
        private static HashSet<string> ReadHeadingAnchors(string path)
        {
            var anchors = new HashSet<string>(StringComparer.Ordinal);
            var slugCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var line in File.ReadAllLines(path))
            {
                var match = MarkdownHeadingRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var text = match.Groups["text"].Value;
                var explicitAnchor = ExplicitAnchorRegex.Match(text);
                if (explicitAnchor.Success)
                {
                    anchors.Add(explicitAnchor.Groups["id"].Value.ToLowerInvariant());
                    text = text.Substring(0, explicitAnchor.Index).TrimEnd();
                }

                var slug = ToGitHubSlug(text);
                if (slug.Length == 0)
                {
                    continue;
                }

                if (slugCounts.TryGetValue(slug, out var seen))
                {
                    slugCounts[slug] = seen + 1;
                    anchors.Add($"{slug}-{seen + 1}");
                }
                else
                {
                    slugCounts[slug] = 0;
                    anchors.Add(slug);
                }
            }

            return anchors;
        }

        /// <summary>Lowercase, spaces to hyphens, everything but letters/digits/`-`/`_` dropped.</summary>
        private static string ToGitHubSlug(string headingText)
        {
            var builder = new StringBuilder();
            foreach (var character in headingText.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_')
                {
                    builder.Append(character);
                }
                else if (character == ' ')
                {
                    builder.Append('-');
                }
            }

            return builder.ToString();
        }

        private static string ToPackageRelativePath(string packageRoot, string path)
        {
            var relative = path.StartsWith(packageRoot, StringComparison.Ordinal)
                ? path.Substring(packageRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/')
                : path;
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }

        private static void AssertSchemaFirstDocumentation(string docsRoot, List<string> issues)
        {
            foreach (var moduleDir in Directory.GetDirectories(docsRoot).OrderBy(x => x, StringComparer.Ordinal))
            {
                var moduleName = Path.GetFileName(moduleDir);
                if (!IsRestModule(moduleName))
                {
                    continue;
                }

                var skillDocPath = Path.Combine(moduleDir, "SKILL.md");
                if (!File.Exists(skillDocPath))
                {
                    continue;
                }

                var content = File.ReadAllText(skillDocPath);
                if (content.IndexOf("## Canonical Signatures", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    issues.Add($"残留 Canonical Signatures: {moduleName}/SKILL.md");
                }

                if (ExactSignatureOptionalModules.Contains(moduleName))
                {
                    continue;
                }

                var hasExactSignatures = content.IndexOf("## Exact Signatures", StringComparison.OrdinalIgnoreCase) >= 0;
                var mentionsSchemaEndpoint = content.IndexOf("/skills/schema", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hasExactSignatures || !mentionsSchemaEndpoint)
                {
                    issues.Add($"缺少 schema-first Exact Signatures 声明: {moduleName}/SKILL.md");
                }
            }
        }

        private static bool IsLooseParameterShorthand(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            return name.IndexOf(',') >= 0 ||
                   name.IndexOf('/') >= 0 ||
                   name.IndexOf(' ') >= 0 ||
                   name.IndexOf('*') >= 0;
        }

        private static string StripParameterShorthand(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;

            var eqIdx = name.IndexOf('=');
            if (eqIdx >= 0)
                name = name.Substring(0, eqIdx);

            if (name.EndsWith("?", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 1);

            return name.Trim();
        }

        private static Dictionary<string, CodeSkill> LoadCodeSkills()
        {
            var result = new Dictionary<string, CodeSkill>(StringComparer.Ordinal);
            var assembly = typeof(UnitySkillAttribute).Assembly;
            // Unfiltered: this reasons about the registry itself, not "what the current surface profile offers".
            var registeredByName = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .ToDictionary(skill => skill.Name, skill => skill, StringComparer.Ordinal);

            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    var attr = method.GetCustomAttribute<UnitySkillAttribute>();
                    if (attr == null || string.IsNullOrWhiteSpace(attr.Name))
                    {
                        continue;
                    }

                    registeredByName.TryGetValue(attr.Name, out var registered);
                    var parameters = method
                        .GetParameters()
                        .Select(p => new CodeParameter
                        {
                            Name = p.Name,
                            Type = NormalizeCodeType(p.ParameterType),
                            Required = IsWireRequired(registered, p)
                        })
                        .ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);

                    result[attr.Name] = new CodeSkill
                    {
                        Name = attr.Name,
                        Method = method,
                        Attribute = attr,
                        Parameters = parameters
                    };
                }
            }

            return result;
        }

        /// <summary>
        /// The same "required" rule <c>SkillRouter.IsParameterRequired</c> applies at the wire boundary: a literal
        /// <c>RequiredParams</c>/<c>RequiresInput</c> entry overrides an otherwise-optional CLR signature (round4
        /// added these to several dozen skills whose parameter keeps a CLR default only so a bad value can still
        /// reach the skill's own validation, e.g. for a friendlier error); short of that, a non-nullable value
        /// type with no default is required. Duplicated here rather than called into SkillRouter (which keeps the
        /// original private) because this is exactly the rule the docs' Required column is being checked against
        /// - the bare `!p.IsOptional` this replaces flagged every one of those round4 additions as a doc/code
        /// mismatch even when the doc correctly said "Required: Yes".
        /// </summary>
        private static bool IsWireRequired(SkillRouter.SkillInfo skill, ParameterInfo p)
        {
            bool NamesParameter(string[] tokens) =>
                tokens != null && tokens.Any(token => string.Equals(token, p.Name, StringComparison.OrdinalIgnoreCase));

            if (skill != null && (NamesParameter(skill.RequiredParams) || NamesParameter(skill.RequiresInput)))
            {
                return true;
            }

            if (p.HasDefaultValue)
            {
                return false;
            }

            return p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) == null;
        }

        /// <summary>
        /// Every `### skill_name` section in every `*.md` of a REST module (<see cref="EnumerateModuleDocs"/>: the files
        /// directly inside the module directory plus its reference/ sub-directory, one level deep), so detail moving out
        /// of the entry SKILL.md keeps its parameter tables, batch item lists and ghost-name checks under the same
        /// scrutiny.
        ///
        /// Occurrences are returned as a list rather than keyed by skill name, because "the same skill documented
        /// twice" is a finding in its own right - keying it away is how a duplicate stays invisible.
        /// </summary>
        private static List<DocSkill> LoadDocumentedSkills()
        {
            var docsRoot = GetDocsRoot();
            Assert.That(Directory.Exists(docsRoot), Is.True, $"技能文档目录不存在: {docsRoot}");

            var result = new List<DocSkill>();

            foreach (var moduleDir in Directory.GetDirectories(docsRoot).OrderBy(x => x, StringComparer.Ordinal))
            {
                var moduleName = Path.GetFileName(moduleDir);
                if (!IsRestModule(moduleName))
                {
                    continue;
                }

                foreach (var docPath in EnumerateModuleDocs(moduleDir))
                {
                    result.AddRange(ParseDocumentedSkills(docPath, moduleName, moduleDir));
                }
            }

            return result;
        }

        private static List<DocSkill> ParseDocumentedSkills(string skillDocPath, string moduleName, string moduleDir)
        {
            var result = new List<DocSkill>();
            var moduleFile = skillDocPath.StartsWith(moduleDir, StringComparison.Ordinal)
                ? skillDocPath.Substring(moduleDir.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace(Path.DirectorySeparatorChar, '/')
                : Path.GetFileName(skillDocPath);
            var lines = File.ReadAllLines(skillDocPath);

            for (var i = 0; i < lines.Length; i++)
            {
                var match = SkillHeadingRegex.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                var skillName = match.Groups["name"].Value;
                var parameters = new Dictionary<string, DocParameter>(StringComparer.Ordinal);
                var parsedParameterBlock = false;

                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].StartsWith("### ", StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (!parsedParameterBlock)
                    {
                        var tableEndIndex = TryParseParameterTable(lines, j, parameters);
                        if (tableEndIndex >= j)
                        {
                            j = tableEndIndex;
                            parsedParameterBlock = true;
                            continue;
                        }

                        var inlineEndIndex = TryParseInlineParameters(lines, j, parameters);
                        if (inlineEndIndex >= j)
                        {
                            j = inlineEndIndex;
                            parsedParameterBlock = true;
                        }
                    }
                }

                result.Add(new DocSkill
                {
                    Name = skillName,
                    Module = moduleName,
                    ModuleFile = moduleFile,
                    FilePath = skillDocPath,
                    Line = i + 1,
                    Parameters = parameters
                });
            }

            return result;
        }

        private static int TryParseParameterTable(string[] lines, int startIndex, Dictionary<string, DocParameter> parameters)
        {
            var line = lines[startIndex].TrimStart();
            if (!line.StartsWith("|", StringComparison.Ordinal))
            {
                return -1;
            }

            var parsedAny = false;
            var endIndex = startIndex;

            for (var i = startIndex; i < lines.Length; i++)
            {
                var current = lines[i].TrimStart();
                if (!current.StartsWith("|", StringComparison.Ordinal))
                {
                    break;
                }

                endIndex = i;
                if (TryParseParameterRow(lines[i], out var parameter))
                {
                    parameters[parameter.Name] = parameter;
                    parsedAny = true;
                }
            }

            return parsedAny ? endIndex : -1;
        }

        private static int TryParseInlineParameters(string[] lines, int startIndex, Dictionary<string, DocParameter> parameters)
        {
            var trimmed = lines[startIndex].Trim();
            if (!trimmed.StartsWith("**Parameters:**", StringComparison.Ordinal))
            {
                return -1;
            }

            var remainder = trimmed.Substring("**Parameters:**".Length).Trim();
            if (remainder.StartsWith("None", StringComparison.OrdinalIgnoreCase))
            {
                return startIndex;
            }

            if (!string.IsNullOrEmpty(remainder))
            {
                foreach (Match match in Regex.Matches(remainder, @"`(?<name>[^`]+)`"))
                {
                    var name = match.Groups["name"].Value.Trim();
                    name = StripParameterShorthand(name);
                    if (!string.IsNullOrEmpty(name))
                    {
                        parameters[name] = new DocParameter { Name = name, Type = string.Empty, Required = true, Source = DocParameterSource.InlineList };
                    }
                }

                return parameters.Count > 0 ? startIndex : -1;
            }

            var parsedAny = false;
            var endIndex = startIndex;
            for (var i = startIndex + 1; i < lines.Length; i++)
            {
                var bullet = lines[i].Trim();
                if (!bullet.StartsWith("-", StringComparison.Ordinal))
                {
                    break;
                }

                endIndex = i;
                if (TryParseBulletParameterRow(bullet, out var parameter))
                {
                    parameters[parameter.Name] = parameter;
                    parsedAny = true;
                }
            }

            return parsedAny ? endIndex : -1;
        }

        private static bool TryParseParameterRow(string line, out DocParameter parameter)
        {
            parameter = null;
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("|", StringComparison.Ordinal) || trimmed.Length < 2)
            {
                return false;
            }

            var cells = trimmed
                .Trim('|')
                .Split('|')
                .Select(x => x.Trim())
                .ToArray();

            if (cells.Length < 3)
            {
                return false;
            }

            var name = StripInlineCode(cells[0]);
            if (string.IsNullOrWhiteSpace(name) || name == "-" || name == "Parameter" || name.StartsWith("---", StringComparison.Ordinal))
            {
                return false;
            }

            parameter = new DocParameter
            {
                Name = name,
                Type = NormalizeDocType(cells[1]),
                Required = NormalizeRequired(cells[2]),
                Source = DocParameterSource.Table
            };
            return true;
        }

        private static bool TryParseBulletParameterRow(string line, out DocParameter parameter)
        {
            parameter = null;
            var match = Regex.Match(line, @"^-\s*`(?<name>[^`]+)`\s*(?:\((?<type>[^)]+)\))?");
            if (!match.Success)
            {
                return false;
            }

            parameter = new DocParameter
            {
                Name = match.Groups["name"].Value.Trim(),
                Type = NormalizeDocType(match.Groups["type"].Value),
                Required = true,
                Source = DocParameterSource.Bullet
            };
            return true;
        }

        private static bool NormalizeRequired(string cell)
        {
            var value = StripInlineCode(cell).Trim();
            return value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Required", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDocType(string raw)
        {
            var value = StripInlineCode(raw).Trim();
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            value = value.Replace(" ", string.Empty);
            value = ReplaceIgnoreCase(value, "integer", "int");
            value = ReplaceIgnoreCase(value, "boolean", "bool");
            value = ReplaceIgnoreCase(value, "number", "float");
            value = ReplaceIgnoreCase(value, "any", "object");
            // "jsonstring": a *_batch skill's `items` documented as "the content is a JSON string" - the code
            // parameter really is `string items` (the skill re-parses it internally), so this is not a distinct
            // type from the wire's point of view. Recognized before "any"/"number" etc. would matter, since
            // "jsonstring" does not contain any of those words as a substring.
            value = ReplaceIgnoreCase(value, "jsonstring", "string");
            if (value.EndsWith("?", StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - 1);
            }

            return value;
        }

        private static string NormalizeCodeType(Type type)
        {
            var nullableType = Nullable.GetUnderlyingType(type);
            if (nullableType != null)
            {
                type = nullableType;
            }

            if (type.IsArray)
            {
                return NormalizeCodeType(type.GetElementType()) + "[]";
            }

            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(long)) return "long";
            if (type == typeof(object)) return "object";
            // Less common CLR value types a skill parameter can still declare (type.Name would otherwise fall
            // through to the raw CLR name - "UInt32" for a `uint renderingLayerMask`, never matching a doc that
            // (correctly) writes the C# keyword alias).
            if (type == typeof(uint)) return "uint";
            if (type == typeof(short)) return "short";
            if (type == typeof(ushort)) return "ushort";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(sbyte)) return "sbyte";
            if (type == typeof(ulong)) return "ulong";
            if (type == typeof(char)) return "char";
            if (type == typeof(decimal)) return "decimal";

            if (type.IsGenericType)
            {
                var genericType = type.GetGenericTypeDefinition();
                if (genericType == typeof(List<>))
                {
                    return NormalizeCodeType(type.GetGenericArguments()[0]) + "[]";
                }
            }

            return type.Name;
        }

        private static bool TypesMatch(string docType, string codeType)
        {
            if (string.Equals(docType, codeType, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(docType, "array", StringComparison.OrdinalIgnoreCase) && codeType.EndsWith("[]", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static string StripInlineCode(string value)
        {
            return value.Replace("`", string.Empty).Trim();
        }

        private static string ReplaceIgnoreCase(string input, string oldValue, string newValue)
        {
            var index = input.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return input;
            }

            return input.Substring(0, index) + newValue + input.Substring(index + oldValue.Length);
        }

        private static string GetDocsRoot()
        {
            var projectDocsRoot = Path.Combine(
                Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("无法解析 Unity 项目根目录。"),
                "SkillsForUnity",
                "unity-skills~",
                "skills");

            if (Directory.Exists(projectDocsRoot))
            {
                return projectDocsRoot;
            }

            var packageInfo = PackageInfo.FindForAssembly(typeof(UnitySkillAttribute).Assembly)
                             ?? PackageInfo.FindForAssembly(typeof(SkillDocumentationConsistencyTests).Assembly);
            if (packageInfo != null)
            {
                var packageDocsRoot = Path.Combine(packageInfo.resolvedPath, "unity-skills~", "skills");
                if (Directory.Exists(packageDocsRoot))
                {
                    return packageDocsRoot;
                }
            }

            return projectDocsRoot;
        }

        /// <summary>The unity-skills~ package root (the parent of GetDocsRoot()).</summary>
        private static string GetPackageRoot()
        {
            return Directory.GetParent(GetDocsRoot())?.FullName
                   ?? throw new InvalidOperationException("无法解析 unity-skills~ 根目录。");
        }

        private static string ReadRootSkillDoc(out string path)
        {
            path = Path.Combine(GetPackageRoot(), "SKILL.md");
            Assert.That(File.Exists(path), Is.True, $"顶层 SKILL.md 不存在: {path}");
            return File.ReadAllText(path);
        }

        private static HashSet<string> LoadPythonHelperNames()
        {
            var scriptPath = Path.Combine(GetPackageRoot(), "scripts", "unity_skills.py");
            Assert.That(File.Exists(scriptPath), Is.True, $"Python 客户端不存在: {scriptPath}");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in PythonModuleDefRegex.Matches(File.ReadAllText(scriptPath)))
            {
                names.Add(match.Groups["name"].Value);
            }

            Assert.That(names, Is.Not.Empty, $"未从 {scriptPath} 解析到任何模块级 helper");
            return names;
        }

        private static Dictionary<string, string> GetLocalizationDictionary(string fieldName)
        {
            var field = typeof(SkillsLocalization).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, $"未找到 SkillsLocalization.{fieldName}");

            var dictionary = field.GetValue(null) as Dictionary<string, string>;
            Assert.That(dictionary, Is.Not.Null, $"SkillsLocalization.{fieldName} 类型不是 Dictionary<string, string>");
            return dictionary;
        }

        private static void AssertNoIssues(List<string> issues, string title)
        {
            if (issues.Count == 0)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine(title);
            foreach (var issue in issues.Take(100))
            {
                builder.AppendLine(issue);
            }

            if (issues.Count > 100)
            {
                builder.AppendLine($"... 还有 {issues.Count - 100} 条");
            }

            Assert.Fail(builder.ToString());
        }

        private sealed class CodeSkill
        {
            public string Name;
            public MethodInfo Method;
            public UnitySkillAttribute Attribute;
            public Dictionary<string, CodeParameter> Parameters;
        }

        private sealed class DocSkill
        {
            public string Name;
            public string Module;
            /// <summary>Path inside the module directory: `SKILL.md` or `reference/&lt;skill_name&gt;.md`.</summary>
            public string ModuleFile;
            public string FilePath;
            public int Line;
            public Dictionary<string, DocParameter> Parameters;

            /// <summary>`&lt;module&gt;/&lt;module file&gt;`, the form used in every issue message.</summary>
            public string RelativePath => $"{Module}/{ModuleFile}";
        }

        private sealed class CodeParameter
        {
            public string Name;
            public string Type;
            public bool Required;
        }

        /// <summary>Which of the three parameter-documentation shapes a <see cref="DocParameter"/> was read from.
        /// Bullet and inline-list rows cannot express "optional" (see DocParameter.Required), so
        /// <see cref="CompareParameters"/> only compares Type/Required for Table rows.</summary>
        private enum DocParameterSource { Table, Bullet, InlineList }

        private sealed class DocParameter
        {
            public string Name;
            public string Type;
            public bool Required;
            public DocParameterSource Source;
        }
    }
}

// Producer:Betsy
