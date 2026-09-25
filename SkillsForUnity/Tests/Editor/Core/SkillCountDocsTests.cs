using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The skill counts quoted in AGENTS.md, both READMEs and the root SKILL.md, checked against the live registry
    /// so a stale count goes red here instead of waiting for someone to run /skillcheck. Counting follows /skillcheck
    /// step 4: one per unique skill name (the #if/#else package stubs never coexist in one build), categories by
    /// SkillCategory (Batch and Diagnose fold into Workflow, Validation and Debug), source files as
    /// Editor/Skills/*Skills.cs. Every quoted claim must be found exactly once, so a reworded sentence cannot fall out
    /// of the check silently.
    /// </summary>
    [TestFixture]
    public class SkillCountDocsTests
    {
        private enum Quantity { Skills, Categories, SourceFiles }

        private sealed class Claim
        {
            public string File;
            public Regex Pattern;
            public Quantity[] Groups;
        }

        private static readonly Claim[] Claims =
        {
            Make("AGENTS.md", @"(\d+) \*Skills\.cs / (\d+) SkillCategory / (\d+) skills",
                Quantity.SourceFiles, Quantity.Categories, Quantity.Skills),
            Make("README.md", @"badge/Skills-(\d+)-green", Quantity.Skills),
            Make("README.md", @"\*\*(\d+) REST Skills Toolkit\*\*: (\d+) source files across (\d+) categories",
                Quantity.Skills, Quantity.SourceFiles, Quantity.Categories),
            Make("README.md", @"Skills Category Overview \((\d+)\)", Quantity.Skills),
            Make("README.md", @"\((\d+) \*Skills\.cs files → (\d+) SkillCategory categories, (\d+) Skills\)",
                Quantity.SourceFiles, Quantity.Categories, Quantity.Skills),
            Make("README.md", @"# (\d+) Skills source code", Quantity.Skills),
            Make("README_CN.md", @"badge/Skills-(\d+)-green", Quantity.Skills),
            Make("README_CN.md", @"\*\*(\d+) REST Skills 全能库\*\*：(\d+) 个源文件、(\d+) 个分类",
                Quantity.Skills, Quantity.SourceFiles, Quantity.Categories),
            Make("README_CN.md", @"Skills 分类概要 \((\d+)\)", Quantity.Skills),
            Make("README_CN.md", @"\((\d+) 个 \*Skills\.cs → (\d+) 个 SkillCategory 分类，共 (\d+) Skills\)",
                Quantity.SourceFiles, Quantity.Categories, Quantity.Skills),
            Make("README_CN.md", @"# (\d+) Skills 源码", Quantity.Skills),
            Make("SkillsForUnity/unity-skills~/SKILL.md", @"`(\d+)` REST skills, `(\d+)` source files, `(\d+)` categories",
                Quantity.Skills, Quantity.SourceFiles, Quantity.Categories),
        };

        /// <summary>A category-table row in either README: <c>| **Name** | 12 | ...</c>.</summary>
        private static readonly Regex CategoryRowRegex =
            new Regex(@"^\|\s*\*\*(?<name>[^*|]+)\*\*\s*\|\s*(?<count>\d+)\s*\|", RegexOptions.Compiled | RegexOptions.Multiline);

        [Test]
        public void QuotedSkillCounts_MatchTheRegistry()
        {
            var root = RepoRoot();
            if (root == null)
                Assert.Ignore("The repository root (AGENTS.md, README.md) is not beside the installed package.");

            var actual = RegistryCounts(root);
            var issues = new List<string>();
            foreach (var claim in Claims)
                issues.AddRange(CheckClaim(claim, File.ReadAllText(Path.Combine(root, claim.File)), actual));

            Assert.That(issues, Is.Empty,
                $"Quoted counts disagree with the registry ({actual[Quantity.Skills]} skills, " +
                $"{actual[Quantity.Categories]} categories, {actual[Quantity.SourceFiles]} *Skills.cs files). " +
                "Run /skillcheck step 4:\n" + string.Join("\n", issues));
        }

        [Test]
        public void CategoryTables_MatchTheRegistryPerCategory()
        {
            var root = RepoRoot();
            if (root == null)
                Assert.Ignore("The repository root (README.md) is not beside the installed package.");

            var perCategory = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .GroupBy(s => s.Category.ToString())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var issues = new List<string>();
            foreach (var readme in new[] { "README.md", "README_CN.md" })
                issues.AddRange(CompareCategoryTable(readme, File.ReadAllText(Path.Combine(root, readme)), perCategory));

            Assert.That(issues, Is.Empty, "README category tables disagree with the registry:\n" + string.Join("\n", issues));
        }

        /// <summary>The canary: a wrong number, a missing sentence and a wrong table row must each be reported.</summary>
        [Test]
        public void CountChecks_ReportWrongMissingAndMismatchedClaims()
        {
            var actual = new Dictionary<Quantity, int>
            {
                [Quantity.Skills] = 805, [Quantity.Categories] = 54, [Quantity.SourceFiles] = 56,
            };
            var claim = Make("X.md", @"(\d+) \*Skills\.cs / (\d+) SkillCategory / (\d+) skills",
                Quantity.SourceFiles, Quantity.Categories, Quantity.Skills);

            Assert.That(CheckClaim(claim, "56 *Skills.cs / 54 SkillCategory / 805 skills", actual), Is.Empty);
            Assert.That(CheckClaim(claim, "56 *Skills.cs / 54 SkillCategory / 804 skills", actual), Has.Count.EqualTo(1));
            Assert.That(CheckClaim(claim, "the sentence was reworded", actual), Has.Count.EqualTo(1));

            var registry = new Dictionary<string, int>(StringComparer.Ordinal) { ["UIToolkit"] = 31, ["Sample"] = 8 };
            Assert.That(CompareCategoryTable("T.md", "| **UI Toolkit** | 31 | x |\n| **Sample** | 8 | y |\n", registry), Is.Empty);
            Assert.That(CompareCategoryTable("T.md", "| **UI Toolkit** | 30 | x |\n| **Sample** | 8 | y |\n", registry), Has.Count.EqualTo(1));
            Assert.That(CompareCategoryTable("T.md", "| **Sample** | 8 | y |\n", registry), Has.Count.EqualTo(1),
                "A category missing from the table is reported.");
            Assert.That(CompareCategoryTable("T.md", "no table at all", registry), Is.Not.Empty);
        }

        private static Claim Make(string file, string pattern, params Quantity[] groups) =>
            new Claim { File = file, Pattern = new Regex(pattern, RegexOptions.Compiled), Groups = groups };

        private static List<string> CheckClaim(Claim claim, string text, IReadOnlyDictionary<Quantity, int> actual)
        {
            var issues = new List<string>();
            var matches = claim.Pattern.Matches(text);
            if (matches.Count != 1)
            {
                issues.Add($"{claim.File}: expected exactly one match of /{claim.Pattern}/, found {matches.Count} " +
                           "(if the sentence was reworded, update the pattern in SkillCountDocsTests)");
                return issues;
            }

            for (var i = 0; i < claim.Groups.Length; i++)
            {
                var quoted = int.Parse(matches[0].Groups[i + 1].Value);
                var expected = actual[claim.Groups[i]];
                if (quoted != expected)
                    issues.Add($"{claim.File}: quotes {quoted} {claim.Groups[i]}, the registry has {expected} (\"{matches[0].Value}\")");
            }

            return issues;
        }

        private static List<string> CompareCategoryTable(string file, string text, IReadOnlyDictionary<string, int> perCategory)
        {
            var issues = new List<string>();
            var rows = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match row in CategoryRowRegex.Matches(text))
            {
                // Display names may carry spaces ("UI Toolkit"); the enum name never does.
                var name = row.Groups["name"].Value.Replace(" ", string.Empty).Trim();
                if (rows.ContainsKey(name))
                    issues.Add($"{file}: category {name} listed twice");
                rows[name] = int.Parse(row.Groups["count"].Value);
            }

            if (rows.Count == 0)
            {
                issues.Add($"{file}: no category table rows found");
                return issues;
            }

            foreach (var pair in perCategory.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!rows.TryGetValue(pair.Key, out var quoted))
                    issues.Add($"{file}: category {pair.Key} ({pair.Value} skills) is missing from the table");
                else if (quoted != pair.Value)
                    issues.Add($"{file}: {pair.Key} quotes {quoted}, the registry has {pair.Value}");
            }

            foreach (var extra in rows.Keys.Where(k => !perCategory.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
                issues.Add($"{file}: table row {extra} is not a registered category");

            return issues;
        }

        private static Dictionary<Quantity, int> RegistryCounts(string root)
        {
            var skills = SkillRouter.GetAllSkillsSnapshotUnfiltered();
            var sourceFiles = Directory.GetFiles(Path.Combine(root, "SkillsForUnity", "Editor", "Skills"), "*Skills.cs").Length;
            return new Dictionary<Quantity, int>
            {
                [Quantity.Skills] = skills.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count(),
                [Quantity.Categories] = skills.Select(s => s.Category).Distinct().Count(),
                [Quantity.SourceFiles] = sourceFiles,
            };
        }

        /// <summary>The repository root beside the package (file: or embedded installs), or null for a registry/git install.</summary>
        private static string RepoRoot()
        {
            var package = PackageInfo.FindForAssembly(typeof(UnitySkillAttribute).Assembly);
            var parent = package != null ? Directory.GetParent(package.resolvedPath)?.FullName : null;
            return parent != null && File.Exists(Path.Combine(parent, "AGENTS.md")) && File.Exists(Path.Combine(parent, "README.md"))
                ? parent
                : null;
        }
    }
}

// Producer:Betsy
