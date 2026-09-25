using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Keeps the git self-update layer behind one door: outside Editor/Versioning nothing names LocalGitSync or
    /// GitCliRunner, and Editor/UI reaches LocalSelfUpdateService only through its registered entry members. A source
    /// scan with coverage floors (a wrong root cannot pass by scanning nothing) and canaries proving each rule fires.
    /// </summary>
    [TestFixture]
    public class SelfUpdateBoundaryTests
    {
        private static readonly string[] GitLayerTypes = { "LocalGitSync", "GitCliRunner" };

        /// <summary>What the UI may call on LocalSelfUpdateService, besides naming the LocalUpdate* result types.</summary>
        private static readonly HashSet<string> UiEntryMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "Check", "Start", "ShortSha", "MaxListedEntries",
        };

        private static readonly string[] KnownUiCallers =
        {
            "SelfUpdateFeedback.cs", "SettingsDrawerController.cs", "VersionUpdateBannerController.cs",
        };

        [Test]
        public void GitLayer_IsNamedOnlyInsideVersioning()
        {
            var root = EditorSourceRoot();
            var versioning = Path.Combine(root, "Versioning") + Path.DirectorySeparatorChar;
            var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.StartsWith(versioning, StringComparison.Ordinal))
                .ToList();

            Assert.That(files.Count, Is.GreaterThanOrEqualTo(150), $"Scanned only {files.Count} files under {root}.");
            Assert.That(files.Select(Path.GetFileName), Is.SupersetOf(KnownUiCallers));
            var offenders = files
                .SelectMany(f => GitLayerReferences(File.ReadAllText(f)).Select(type => $"{Path.GetFileName(f)}: {type}"))
                .ToList();
            Assert.That(offenders, Is.Empty, "Only Editor/Versioning may use the git layer; go through LocalSelfUpdateService.");
        }

        [Test]
        public void Ui_CallsTheServiceOnlyThroughItsEntryMembers()
        {
            var ui = Path.Combine(EditorSourceRoot(), "UI");
            var files = Directory.GetFiles(ui, "*.cs", SearchOption.AllDirectories);
            Assert.That(files.Length, Is.GreaterThanOrEqualTo(20), $"Scanned only {files.Length} files under {ui}.");

            var calls = files
                .SelectMany(f => ServiceMembers(File.ReadAllText(f)).Select(member => (file: Path.GetFileName(f), member)))
                .ToList();
            Assert.That(calls.Select(c => c.member), Is.SupersetOf(new[] { "Check", "Start" }), "The scan must see the known entry calls.");
            var unregistered = calls.Where(c => !UiEntryMembers.Contains(c.member)).Select(c => $"{c.file}: {c.member}").ToList();
            Assert.That(unregistered, Is.Empty, "The UI may only use LocalSelfUpdateService's registered entry members.");
        }

        [Test]
        public void Scanner_Canaries()
        {
            Assert.That(GitLayerReferences("var s = LocalGitSync.ShortSha(sha);"), Is.EqualTo(new[] { "LocalGitSync" }));
            Assert.That(GitLayerReferences("if (GitCliRunner.IsWindows) return;"), Is.EqualTo(new[] { "GitCliRunner" }));
            Assert.That(GitLayerReferences("// LocalGitSync in a comment\n/* GitCliRunner too */\nvar t = \"LocalGitSync\"; var u = @\"GitCliRunner\";"), Is.Empty);
            Assert.That(GitLayerReferences("var v = MyLocalGitSyncHelper.Run();"), Is.Empty);
            Assert.That(ServiceMembers("LocalSelfUpdateService.Start(v, cb);\nLocalSelfUpdateService.SwapDirectories(a, b, c);"),
                Is.EqualTo(new[] { "Start", "SwapDirectories" }));
        }

        private static IEnumerable<string> GitLayerReferences(string source)
        {
            var code = StripCommentsAndStrings(source);
            return GitLayerTypes.Where(type => Regex.IsMatch(code, $@"\b{type}\b"));
        }

        private static IEnumerable<string> ServiceMembers(string source)
        {
            return Regex.Matches(StripCommentsAndStrings(source), @"\bLocalSelfUpdateService\s*\.\s*([A-Za-z_]\w*)")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value);
        }

        /// <summary>Blanks comments and string / char literal contents, keeping line structure.</summary>
        private static string StripCommentsAndStrings(string source)
        {
            var sb = new StringBuilder(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    while (i < source.Length && source[i] != '\n') i++;
                    sb.Append('\n');
                }
                else if (c == '/' && next == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    i++;
                    sb.Append(' ');
                }
                else if (c == '@' && next == '"')
                {
                    i += 2;
                    while (i < source.Length && !(source[i] == '"' && (i + 1 >= source.Length || source[i + 1] != '"')))
                        i += source[i] == '"' ? 2 : 1;
                    sb.Append("\"\"");
                }
                else if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < source.Length && source[i] != quote && source[i] != '\n')
                        i += source[i] == '\\' ? 2 : 1;
                    sb.Append(quote).Append(quote);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>The package's Editor folder: the in-project copy first, then the resolved package path.</summary>
        private static string EditorSourceRoot()
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            var inProject = projectRoot == null ? null : Path.Combine(projectRoot.FullName, "SkillsForUnity", "Editor");
            if (inProject != null && Directory.Exists(inProject))
                return inProject;

            var packageInfo = PackageInfo.FindForAssembly(typeof(UnitySkillAttribute).Assembly);
            Assert.That(packageInfo, Is.Not.Null, "Cannot locate the UnitySkills package sources.");
            var inPackage = Path.Combine(packageInfo.resolvedPath, "Editor");
            Assert.That(Directory.Exists(inPackage), Is.True, $"Missing {inPackage}.");
            return inPackage;
        }
    }
}

// Producer:Betsy
