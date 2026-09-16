using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Covers the single-line mechanics of the opt-in "use Unity Skills" guide line written to AI
    /// tools' root instruction files: create/append/migrate semantics, idempotency, exact removal
    /// (including deleting a file that held nothing else), cleanup of pre-release marker-wrapped
    /// wordings, no-BOM output, and the per-tool path table. Everything runs against temp files —
    /// the user's real CLAUDE.md / AGENTS.md are never touched.
    /// </summary>
    [TestFixture]
    public class AgentInstructionServiceTests
    {
        // Pre-release wording, pinned as a literal on purpose: migration must keep working even if
        // the service's private legacy constants are one day removed.
        private const string LegacyMarkedBlock =
            "<!-- unity-skills:begin -->\n" +
            "When working on this Unity project, prefer the \"unity-skills\" skill and follow its SKILL.md for Editor operations.\n" +
            "<!-- unity-skills:end -->";

        private string _tempRoot;
        private string _savedProjectRootOverride;

        [SetUp]
        public void SetUp()
        {
            _savedProjectRootOverride = AgentInstructionService.ProjectRootOverride;
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsAgentInstruction_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            AgentInstructionService.ProjectRootOverride = _tempRoot;
        }

        [TearDown]
        public void TearDown()
        {
            AgentInstructionService.ProjectRootOverride = _savedProjectRootOverride;
            try
            {
                if (Directory.Exists(_tempRoot))
                    Directory.Delete(_tempRoot, true);
            }
            catch (IOException)
            {
                // A failure to clean up the temp directory shouldn't turn the test red.
            }
        }

        private string TempFile(string name) => Path.Combine(_tempRoot, name);

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        // ===== Preference =====

        [Test]
        public void Enabled_WithNoStoredPreference_DefaultsToOff()
        {
            var key = AgentInstructionService.PrefEnabled;
            bool hadValue = EditorPrefs.HasKey(key);
            bool saved = hadValue && EditorPrefs.GetBool(key);
            try
            {
                EditorPrefs.DeleteKey(key);

                Assert.That(AgentInstructionService.Enabled, Is.False, "Guide-line injection is opt-in and ships off by default.");
                StringAssert.StartsWith("UnitySkills_", key);
                StringAssert.Contains(RegistryService.InstanceId, key,
                    "The preference key must carry the instance id so two projects open at once cannot clobber each other.");
            }
            finally
            {
                if (hadValue) EditorPrefs.SetBool(key, saved);
                else EditorPrefs.DeleteKey(key);
            }
        }

        // ===== UpsertBlock =====

        [Test]
        public void UpsertBlock_MissingFile_CreatesFileWithOnlyTheLine()
        {
            var path = TempFile("AGENTS.md");

            Assert.That(AgentInstructionService.UpsertBlock(path), Is.True);

            Assert.That(File.ReadAllText(path), Is.EqualTo(AgentInstructionService.InstructionLine + "\n"));
        }

        [Test]
        public void UpsertBlock_ExistingContent_AppendsAfterBlankLineAndKeepsUserContent()
        {
            var path = TempFile("CLAUDE.md");
            File.WriteAllText(path, "# My Rules\nDo things carefully.\n");

            Assert.That(AgentInstructionService.UpsertBlock(path), Is.True);

            var text = File.ReadAllText(path);
            Assert.That(text, Is.EqualTo(
                "# My Rules\nDo things carefully.\n\n" + AgentInstructionService.InstructionLine + "\n"));
        }

        [Test]
        public void UpsertBlock_CalledTwice_IsIdempotent()
        {
            var path = TempFile("AGENTS.md");
            File.WriteAllText(path, "user content\n");
            AgentInstructionService.UpsertBlock(path);
            var afterFirst = File.ReadAllText(path);

            Assert.That(AgentInstructionService.UpsertBlock(path), Is.False, "Second run must be a no-op.");
            Assert.That(File.ReadAllText(path), Is.EqualTo(afterFirst));
            Assert.That(CountOccurrences(afterFirst, AgentInstructionService.InstructionLine), Is.EqualTo(1));
        }

        [Test]
        public void UpsertBlock_LegacyMarkedBlock_IsReplacedByBareLine()
        {
            var path = TempFile("AGENTS.md");
            File.WriteAllText(path, "head\n" + LegacyMarkedBlock + "\ntail\n");

            Assert.That(AgentInstructionService.UpsertBlock(path), Is.True);

            var text = File.ReadAllText(path);
            Assert.That(text, Is.EqualTo("head\n" + AgentInstructionService.InstructionLine + "\ntail\n"),
                "The dev-era marker block must migrate to the current bare line in place.");
        }

        [Test]
        public void UpsertBlock_DuplicateLines_AreDeduped()
        {
            var path = TempFile("AGENTS.md");
            File.WriteAllText(path,
                AgentInstructionService.InstructionLine + "\nuser\n" + AgentInstructionService.InstructionLine + "\n");

            Assert.That(AgentInstructionService.UpsertBlock(path), Is.True);

            var text = File.ReadAllText(path);
            Assert.That(CountOccurrences(text, AgentInstructionService.InstructionLine), Is.EqualTo(1));
            StringAssert.Contains("user\n", text);
        }

        [Test]
        public void UpsertBlock_CrlfFile_PreservesCarriageReturns()
        {
            var path = TempFile("CLAUDE.md");
            File.WriteAllText(path, "line one\r\nline two\r\n");

            AgentInstructionService.UpsertBlock(path);

            var raw = File.ReadAllText(path);
            StringAssert.Contains("line one\r\nline two\r\n", raw);
            Assert.That(raw.Replace("\r\n", ""), Does.Not.Contain("\n"), "No bare LF may remain in a CRLF file.");
        }

        // ===== RemoveBlock =====

        [Test]
        public void RemoveBlock_FileWithOnlyTheLine_DeletesTheFile()
        {
            var path = TempFile("AGENTS.md");
            AgentInstructionService.UpsertBlock(path);
            Assert.That(File.Exists(path), Is.True);

            Assert.That(AgentInstructionService.RemoveBlock(path), Is.True);
            Assert.That(File.Exists(path), Is.False,
                "A file we created from scratch must disappear again when the feature is turned off.");
        }

        [Test]
        public void RemoveBlock_MixedContent_RemovesOnlyTheLineAndItsSeparator()
        {
            var path = TempFile("CLAUDE.md");
            File.WriteAllText(path, "# My Rules\nDo things carefully.\n");
            AgentInstructionService.UpsertBlock(path);

            Assert.That(AgentInstructionService.RemoveBlock(path), Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo("# My Rules\nDo things carefully.\n"));
        }

        [Test]
        public void RemoveBlock_LegacyMarkedBlock_IsCleanedUp()
        {
            var path = TempFile("AGENTS.md");
            File.WriteAllText(path, LegacyMarkedBlock + "\n");

            Assert.That(AgentInstructionService.RemoveBlock(path), Is.True);
            Assert.That(File.Exists(path), Is.False,
                "A file holding only a dev-era marker block must be deleted entirely.");
        }

        [Test]
        public void RemoveBlock_NoLineOrNoFile_ReturnsFalse()
        {
            var path = TempFile("AGENTS.md");
            Assert.That(AgentInstructionService.RemoveBlock(path), Is.False, "Missing file.");

            File.WriteAllText(path, "plain user content\n");
            Assert.That(AgentInstructionService.RemoveBlock(path), Is.False, "No guide line of ours.");
            Assert.That(File.ReadAllText(path), Is.EqualTo("plain user content\n"));
        }

        [Test]
        public void WrittenFile_HasNoByteOrderMark()
        {
            var path = TempFile("AGENTS.md");
            AgentInstructionService.UpsertBlock(path);

            var bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Length, Is.GreaterThan(3));
            Assert.That(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, Is.False);
        }

        // ===== Path table =====

        [Test]
        public void GetInstructionFilePath_ProjectScope_MapsToolToFileUnderProjectRoot()
        {
            Assert.That(AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentClaudeCode, false),
                Is.EqualTo(Path.Combine(_tempRoot, "CLAUDE.md")));

            foreach (var agentId in new[]
                     {
                         AgentInstructionService.AgentCodex, AgentInstructionService.AgentAntigravity,
                         AgentInstructionService.AgentCursor, AgentInstructionService.AgentOpenCode,
                         AgentInstructionService.AgentKimiCode
                     })
            {
                Assert.That(AgentInstructionService.GetInstructionFilePath(agentId, false),
                    Is.EqualTo(Path.Combine(_tempRoot, "AGENTS.md")), agentId + " project file");
            }
        }

        [Test]
        public void GetInstructionFilePath_GlobalScope_MapsToolToUserHomeFile()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Assert.That(AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentClaudeCode, true),
                Is.EqualTo(Path.Combine(home, ".claude", "CLAUDE.md")));
            Assert.That(AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentAntigravity, true),
                Is.EqualTo(Path.Combine(home, ".gemini", "GEMINI.md")));
            Assert.That(AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentOpenCode, true),
                Is.EqualTo(Path.Combine(home, ".config", "opencode", "AGENTS.md")));
            Assert.That(Path.GetFileName(AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentCodex, true)),
                Is.EqualTo("AGENTS.md"));
            Assert.That(Path.GetFileName(AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentKimiCode, true)),
                Is.EqualTo("AGENTS.md"));

            Assert.That(AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentCursor, true), Is.Null,
                "Cursor has no dependable user-level instruction file.");
            Assert.That(AgentInstructionService.GetInstructionFilePath("Custom", false), Is.Null);
        }

        [Test]
        public void EnumerateTargets_CarryAgentIdAndScope()
        {
            foreach (var target in SkillInstaller.EnumerateTargets())
            {
                Assert.That(target.AgentId, Is.Not.Null.And.Not.Empty, target.DisplayName + " must name its agent");
                Assert.That(Array.IndexOf(AgentInstructionService.KnownAgentIds, target.AgentId), Is.GreaterThanOrEqualTo(0),
                    target.DisplayName + " uses an agent id the instruction service does not know");
                StringAssert.Contains(target.IsGlobal ? "(Global)" : "(Project)", target.DisplayName);
            }
        }
    }
}

// Producer:Betsy
