using System.Collections.Generic;
using NUnit.Framework;
using UnitySkills;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Covers the pure-logic pieces of ClientProcessResolver: the denylist-driven parent-chain walk, interpreter
    /// command-line extraction (including scoped npm packages), the cosmetic display-name map's capitalize
    /// fallback, and the TtlCache's expiry/eviction behavior. Nothing here shells out to lsof/ps or touches the
    /// real network/process tables -- every platform-dependent input is a hand-built fake, per agent.md's
    /// requirement that the platform layer be injectable and tested with fake data only.
    ///
    /// TtlCache instances are created fresh per test (never ClientProcessResolver's own shared _portCache/_pidCache),
    /// so these tests can't race with -- or be polluted by -- the real resolver attributing live requests against
    /// the actual running server in this same process.
    /// </summary>
    [TestFixture]
    public class ClientProcessResolverTests
    {
        // ===== Denylist =====

        [TestCase("zsh")]
        [TestCase("bash")]
        [TestCase("sh")]
        [TestCase("curl")]
        [TestCase("wget")]
        [TestCase("Terminal")]
        [TestCase("login")]
        [TestCase("launchd")]
        [TestCase("cmd")]
        [TestCase("powershell")]
        [TestCase("CURL")] // case-insensitive
        public void IsDenylisted_ShellsTerminalsSystemAndCurl_AreExcluded(string name)
        {
            Assert.That(ClientProcessResolver.IsDenylisted(name), Is.True, $"'{name}' should be on the exclusion list.");
        }

        [TestCase("iTermServer-3.5.6")]
        [TestCase("itermserver")]
        [TestCase("tmux")]
        [TestCase("tmux-1.9a")]
        [TestCase("language_server_windows_x64")] // Codeium agent host -- the platform suffix varies, the stem does not
        [TestCase("language_server_macos_arm")]
        [TestCase("language_server_linux_x64")]
        public void IsDenylisted_PrefixEntries_MatchVersionedNames(string name)
        {
            Assert.That(ClientProcessResolver.IsDenylisted(name), Is.True, $"'{name}' should match a denylist prefix.");
        }

        [TestCase("claude")]
        [TestCase("augment")]
        [TestCase("some-brand-new-cli")]
        [TestCase("node")] // interpreters are handled separately, not via the plain denylist
        [TestCase("code")] // deliberately NOT denylisted -- it maps to "VSCode" in DisplayNameMap; an agent running inside VS Code should report as VSCode, not fall through
        public void IsDenylisted_AgentAndInterpreterNames_AreNotExcluded(string name)
        {
            Assert.That(ClientProcessResolver.IsDenylisted(name), Is.False, $"'{name}' must not be treated as a denylisted shell/system process.");
        }

        [TestCase("warp")]
        [TestCase("ghostty")]
        [TestCase("tabby")]
        [TestCase("rio")]
        [TestCase("zellij")]
        [TestCase("konsole")]
        [TestCase("xterm")]
        public void IsDenylisted_ModernTerminalHosts_AreExcluded(string name)
        {
            Assert.That(ClientProcessResolver.IsDenylisted(name), Is.True, $"'{name}' is a terminal host -- normally above the agent in the chain (never reached), but excluding it means a hand-typed curl with no agent in the chain falls back to the header/UA guess instead of misreporting the terminal as the agent.");
        }

        [TestCase("make")]
        [TestCase("cmake")]
        [TestCase("npm")]
        [TestCase("npx")]
        [TestCase("yarn")]
        [TestCase("pnpm")]
        [TestCase("git")]
        [TestCase("ssh")]
        [TestCase("sshd")]
        [TestCase("nohup")]
        [TestCase("direnv")]
        [TestCase("watchexec")]
        [TestCase("just")]
        [TestCase("task")]
        [TestCase("uv")]
        [TestCase("uvx")]
        [TestCase("pipx")]
        [TestCase("language_server_windows_x64")] // Codeium-derived IDEs' agent host (Windsurf AND Antigravity share this binary name) -- skipped so the walk reaches the IDE's own main process
        public void IsDenylisted_BuildToolsAndPackageManagerMiddlemen_AreExcluded(string name)
        {
            Assert.That(ClientProcessResolver.IsDenylisted(name), Is.True, $"'{name}' should be excluded -- these commonly wrap the real agent CLI (e.g. npx @scope/agent-cli).");
        }

        // ===== Process-name normalization =====

        [TestCase("-zsh", "zsh")]                                   // login shells report with a leading dash
        [TestCase("/usr/bin/curl", "curl")]                         // strips a directory path
        [TestCase("node.exe", "node")]                              // strips a Windows-style extension
        [TestCase("python3.11", "python3")]                         // versioned interpreter still normalizes to the bare interpreter name
        [TestCase("claude", "claude")]                              // already bare
        [TestCase(@"C:\Users\betsy\claude.exe", "claude")]          // Windows path + extension together
        public void NormalizeProcessName_StripsDashPathAndExtension(string raw, string expected)
        {
            Assert.That(ClientProcessResolver.NormalizeProcessName(raw), Is.EqualTo(expected));
        }

        // ===== Electron helper-suffix stripping =====

        [TestCase("Code Helper (Plugin)", "Code")]
        [TestCase("Code Helper (Renderer)", "Code")]
        [TestCase("Code Helper (GPU)", "Code")]
        [TestCase("Cursor Helper (Renderer)", "Cursor")]
        [TestCase("Google Chrome Helper", "Google Chrome")]   // no parenthetical role -- still a valid Electron/Chromium helper name
        [TestCase("My Space Helper (Plugin)", "My Space")]    // unmapped app name -- sanitized later by NormalizeDisplayName, not here
        public void StripElectronHelperSuffix_RemovesTheExactTrailingHelperPattern(string raw, string expected)
        {
            Assert.That(ClientProcessResolver.StripElectronHelperSuffix(raw), Is.EqualTo(expected));
        }

        [TestCase("My Agent")]           // no " Helper" suffix at all -- must stay whole, not cut at the first space
        [TestCase("Helper")]             // no leading space before "Helper" to anchor on -- nothing to strip
        [TestCase("MyHelperApp")]        // "Helper" mid-word, no preceding whitespace -- must not match
        [TestCase("Code Helper Utility")] // "Helper" not at the very end -- must not match
        public void StripElectronHelperSuffix_LeavesNonMatchingNamesUntouched(string raw)
        {
            Assert.That(ClientProcessResolver.StripElectronHelperSuffix(raw), Is.EqualTo(raw));
        }

        [Test]
        public void NormalizeProcessName_FullBundlePathWithHelperSuffix_StripsPathAndSuffixTogether()
        {
            // The realistic end-to-end input: argv[0] from a macOS app bundle, suffix and all.
            string raw = "/Applications/Visual Studio Code.app/Contents/MacOS/Code Helper (Plugin)";
            Assert.That(ClientProcessResolver.NormalizeProcessName(raw), Is.EqualTo("Code"));
        }

        // ===== Interpreter recognition =====

        [TestCase("node", true)]
        [TestCase("python3", true)]
        [TestCase("Python3", true)] // case-insensitive
        [TestCase("dotnet", true)]
        [TestCase("claude", false)]
        [TestCase("zsh", false)]
        public void IsInterpreter_RecognizesKnownRuntimesOnly(string name, bool expected)
        {
            Assert.That(ClientProcessResolver.IsInterpreter(name), Is.EqualTo(expected));
        }

        // ===== TokenizeCommandLine =====

        [Test]
        public void TokenizeCommandLine_PlainWhitespaceSeparatedArgs_SplitsOnSpaces()
        {
            var tokens = ClientProcessResolver.TokenizeCommandLine("node /path/to/cli.js --flag value");
            Assert.That(tokens, Is.EqualTo(new List<string> { "node", "/path/to/cli.js", "--flag", "value" }));
        }

        [Test]
        public void TokenizeCommandLine_QuotedTokenWithEmbeddedSpace_StaysOneTokenWithQuotesStripped()
        {
            // Windows PEB CommandLine convention: CreateProcess quotes any space-containing argument, most
            // commonly argv[0] itself (e.g. a "Program Files" install path). A naive whitespace split would
            // cut this at "Program" / "Files\nodejs\node.exe".
            var tokens = ClientProcessResolver.TokenizeCommandLine(@"""C:\Program Files\nodejs\node.exe"" C:\scripts\app.js");
            Assert.That(tokens, Is.EqualTo(new List<string> { @"C:\Program Files\nodejs\node.exe", @"C:\scripts\app.js" }));
        }

        [Test]
        public void TokenizeCommandLine_UnterminatedQuote_TakesTheRestOfTheStringAsOneToken()
        {
            var tokens = ClientProcessResolver.TokenizeCommandLine(@"""C:\Program Files\broken");
            Assert.That(tokens, Is.EqualTo(new List<string> { @"C:\Program Files\broken" }));
        }

        [Test]
        public void TokenizeCommandLine_EmptyString_ReturnsEmptyList()
        {
            Assert.That(ClientProcessResolver.TokenizeCommandLine(""), Is.Empty);
        }

        // ===== Interpreter command-line extraction =====

        [Test]
        public void TryExtractFromArgs_ScopedNodeModulesPackage_ReturnsPackageNameWithoutScope()
        {
            string args = "node /Users/betsy/.nvm/versions/node/v20.11.0/bin/../lib/node_modules/@anthropic-ai/claude-code/cli.js";
            Assert.That(ClientProcessResolver.TryExtractFromArgs(args), Is.EqualTo("claude-code"));
        }

        [Test]
        public void TryExtractFromArgs_UnscopedNodeModulesPackage_ReturnsPackageName()
        {
            string args = "node /usr/local/lib/node_modules/opencode/dist/cli.js --port 8090";
            Assert.That(ClientProcessResolver.TryExtractFromArgs(args), Is.EqualTo("opencode"));
        }

        [Test]
        public void TryExtractFromArgs_GenericEntryFileOutsideNodeModules_FallsBackToParentDirectoryName()
        {
            string args = "node /opt/homebrew/lib/myagent/cli.js";
            Assert.That(ClientProcessResolver.TryExtractFromArgs(args), Is.EqualTo("myagent"));
        }

        [Test]
        public void TryExtractFromArgs_NonGenericFileName_ReturnsTheFileStem()
        {
            string args = "python3 /Users/betsy/tools/kimi.py --flag value";
            Assert.That(ClientProcessResolver.TryExtractFromArgs(args), Is.EqualTo("kimi"));
        }

        [Test]
        public void TryExtractFromArgs_OnlyFlagsNoPathArgument_ReturnsNull()
        {
            Assert.That(ClientProcessResolver.TryExtractFromArgs("node -e \"1+1\""), Is.Null);
        }

        [Test]
        public void TryExtractFromArgs_BareInterpreterNoArguments_ReturnsNull()
        {
            Assert.That(ClientProcessResolver.TryExtractFromArgs("node"), Is.Null);
        }

        [Test]
        public void TryExtractFromArgs_EmptyOrNullArgs_ReturnsNull()
        {
            Assert.That(ClientProcessResolver.TryExtractFromArgs(null), Is.Null);
            Assert.That(ClientProcessResolver.TryExtractFromArgs("  "), Is.Null);
        }

        [Test]
        public void TryExtractFromArgs_WindowsQuotedInterpreterPath_StillFindsTheScriptArgument()
        {
            // argv[0] itself is a Windows-quoted, space-containing path (PEB CommandLine convention). Before
            // TokenizeCommandLine existed, a naive whitespace split would have shredded this before the scan
            // ever got to the real script argument.
            string args = @"""C:\Program Files\nodejs\node.exe"" C:\tools\node_modules\opencode\dist\cli.js";
            Assert.That(ClientProcessResolver.TryExtractFromArgs(args), Is.EqualTo("opencode"));
        }

        // Inline code is program text, not a script path: python -c / node -e|-p|--eval|--print. Anything
        // path-like inside it (a URL, a sys.path entry) must not become the agent's name -- returning null
        // lets the walk climb to the process that launched the interpreter. Observed live before the guard:
        // agents named "Debug_get_errors" and "Scripts".
        [TestCase("python3 -c \"import urllib.request as u; u.urlopen('http://localhost:8090/skill/debug_get_errors')\"")]
        [TestCase("python3 -c \"import sys; sys.path.insert(0,'/Users/betsy/unity-skills~/scripts'); import unity_skills\"")]
        [TestCase("python3 -c \"print(1/2)\"")]
        [TestCase("node -e \"require('/opt/tools/agent.js')\"")]
        [TestCase("node -p \"require('/opt/tools/agent.js').version\"")]
        [TestCase("node --eval \"fetch('http://localhost:8090/health')\"")]
        [TestCase("node --print \"require('/opt/tools/agent.js').version\"")]
        public void TryExtractFromArgs_InlineCodeFlag_ReturnsNullSoTheWalkClimbs(string args)
        {
            Assert.That(ClientProcessResolver.TryExtractFromArgs(args), Is.Null);
        }

        [Test]
        public void TryExtractFromArgs_ScriptPathBeforeInlineFlag_StillReturnsTheScript()
        {
            // Python stops option parsing at the script path; a later "-c" is the script's own argument.
            Assert.That(ClientProcessResolver.TryExtractFromArgs("python3 /Users/betsy/tools/kimi.py -c config"), Is.EqualTo("kimi"));
        }

        [Test]
        public void TryExtractFromArgs_TokenWithWindowsInvalidPathChars_DoesNotThrow()
        {
            // A PEB command line can carry shell redirection text inside a token; Path.GetFileNameWithoutExtension
            // throws for '<' '>' '|' on Windows/Mono, which used to abort the whole synchronous attribution.
            string result = null;
            Assert.DoesNotThrow(() => result = ClientProcessResolver.TryExtractFromArgs(@"python C:\tools\a<b>c|d.py"));
            Assert.That(result, Is.EqualTo("a<b>c|d"));
        }

        [Test]
        public void JoinArgv_ElementWithWhitespace_SurvivesTokenizationAsOneToken()
        {
            // mac/Linux hand over a NUL-split argv; a plain space-join used to shred "My Project" into two
            // tokens and the first fragment ("/Users/betsy/My") won the path scan.
            var argv = new[] { "/usr/bin/python3", "/Users/betsy/My Project/tool.py", "--port", "8090" };
            var joined = ClientProcessResolver.JoinArgv(argv);

            Assert.That(ClientProcessResolver.TokenizeCommandLine(joined), Is.EqualTo(argv));
            Assert.That(ClientProcessResolver.TryExtractFromArgs(joined), Is.EqualTo("tool"));
        }

        [Test]
        public void JoinArgv_NullOrPlainElements_MatchesSpaceJoin()
        {
            Assert.That(ClientProcessResolver.JoinArgv(null), Is.Null);
            Assert.That(ClientProcessResolver.JoinArgv(new[] { "node", "/opt/a/cli.js" }), Is.EqualTo("node /opt/a/cli.js"));
        }

        // ===== Display-name normalization =====

        [TestCase("claude", "ClaudeCode")]
        [TestCase("claude-code", "ClaudeCode")]
        [TestCase("CLAUDE", "ClaudeCode")] // case-insensitive
        [TestCase("augment", "Augment")]
        [TestCase("auggie", "Augment")] // Augment CLI's actual binary name
        [TestCase("agy", "Antigravity")] // Antigravity CLI's actual binary name (confirmed live against a real agy process)
        [TestCase("antigravity", "Antigravity")]
        [TestCase("antigravity ide", "Antigravity")] // the Windows IDE's main exe is literally "Antigravity IDE.exe"
        [TestCase("codex", "Codex")]
        [TestCase("codex-command-runner", "Codex")] // Codex CLI/App run shell commands through this bundled helper
        [TestCase("codex-code-mode-host", "Codex")]
        [TestCase("amazon-q", "AmazonQ")]
        [TestCase("code", "VSCode")]
        [TestCase("gemini", "GeminiCLI")]
        [TestCase("aider", "Aider")]
        [TestCase("amp", "Amp")]
        [TestCase("goose", "Goose")]
        [TestCase("droid", "Droid")]
        [TestCase("qwen", "QwenCode")]
        [TestCase("crush", "Crush")] // charmbracelet/crush, confirmed via GitHub releases
        [TestCase("plandex", "Plandex")]
        [TestCase("pdx", "Plandex")] // Plandex's other real binary name
        [TestCase("cn", "Continue")] // npm @continuedev/cli bin=["cn"] -- not "continue"
        [TestCase("copilot", "CopilotCLI")] // npm @github/copilot bin=["copilot"] -- not "gh copilot"
        [TestCase("cb", "Codebuff")]
        [TestCase("codebuff", "Codebuff")]
        public void NormalizeDisplayName_KnownTokens_MapToTheAgentKeywordsValue(string token, string expected)
        {
            Assert.That(ClientProcessResolver.NormalizeDisplayName(token), Is.EqualTo(expected));
        }

        [Test]
        public void NormalizeDisplayName_LanguageServerBinary_IsNotMappedToAnySpecificIDE()
        {
            // Windsurf and Antigravity ship an identical "language_server_windows_x64" agent-host binary name,
            // so it must NOT appear in DisplayNameMap -- hardcoding it to either product would mislabel the
            // other. It is denylisted instead, letting the chain walk reach the IDE's own main process.
            var name = ClientProcessResolver.NormalizeDisplayName("language_server_windows_x64");
            Assert.That(name, Is.Not.EqualTo("Antigravity"));
            Assert.That(name, Is.Not.EqualTo("Windsurf"));
        }

        [Test]
        public void NormalizeDisplayName_BareQ_IsNotMappedToAmazonQAndFallsBackToCapitalizedQ()
        {
            // "q" is deliberately unmapped: a single-letter token is the highest-risk entry a denylist-driven
            // walk can have (any unrelated process named "q" would get mislabeled as an agent), and the actual
            // amazon-q-developer-cli binary name couldn't be confirmed against a definitive source. Falls back
            // to the plain capitalize-first-letter path instead of risking a wrong match; "amazon-q" (the
            // unambiguous full name) is still mapped separately.
            Assert.That(ClientProcessResolver.NormalizeDisplayName("q"), Is.EqualTo("Q"));
            Assert.That(ClientProcessResolver.NormalizeDisplayName("q"), Is.Not.EqualTo("AmazonQ"));
        }

        [Test]
        public void NormalizeProcessName_ElectronHelper_FeedsTheHostApplicationsDisplayName()
        {
            // End-to-end of the two steps an IDE-hosted agent depends on: Cline, Roo Code and the Copilot /
            // Continue extensions have no CLI binary and only ever surface as a VS Code helper process, so the
            // suffix strip has to survive all the way into the display-name lookup. Without it these would come
            // back as CodeHelperPlugin / CodeHelperRenderer / CodeHelperGPU -- three "agents" for one editor.
            foreach (var role in new[] { "Code Helper (Plugin)", "Code Helper (Renderer)", "Code Helper (GPU)" })
            {
                var normalized = ClientProcessResolver.NormalizeProcessName(role);
                Assert.That(ClientProcessResolver.NormalizeDisplayName(normalized), Is.EqualTo("VSCode"), role);
            }

            var cursor = ClientProcessResolver.NormalizeProcessName("Cursor Helper (Renderer)");
            Assert.That(ClientProcessResolver.NormalizeDisplayName(cursor), Is.EqualTo("Cursor"));

            // Unmapped host: still collapses to one identity, sanitized for the JSONL.
            var unmapped = ClientProcessResolver.NormalizeProcessName("My Space Helper (Plugin)");
            Assert.That(ClientProcessResolver.NormalizeDisplayName(unmapped), Is.EqualTo("MySpace"));
        }

        [Test]
        public void NormalizeDisplayName_UnknownToken_CapitalizesInsteadOfReportingUnknown()
        {
            Assert.That(ClientProcessResolver.NormalizeDisplayName("myagent"), Is.EqualTo("Myagent"));
        }

        [Test]
        public void NormalizeDisplayName_UnknownSingleCharacterToken_Capitalizes()
        {
            Assert.That(ClientProcessResolver.NormalizeDisplayName("x"), Is.EqualTo("X"));
        }

        [Test]
        public void NormalizeDisplayName_NeverReturnsTheLiteralStringUnknown()
        {
            Assert.That(ClientProcessResolver.NormalizeDisplayName("totally-new-cli"), Does.Not.Contain("Unknown"));
        }

        // ===== Sanitize-for-telemetry =====

        [Test]
        public void SanitizeForTelemetry_StripsCharactersOutsideTheSafeSet()
        {
            Assert.That(ClientProcessResolver.SanitizeForTelemetry("agent\"; DROP TABLE\nname"), Is.EqualTo("agentDROPTABLEname"));
        }

        [Test]
        public void SanitizeForTelemetry_KeepsHyphenUnderscoreAndDot()
        {
            Assert.That(ClientProcessResolver.SanitizeForTelemetry("claude-code_v1.2"), Is.EqualTo("claude-code_v1.2"));
        }

        [Test]
        public void SanitizeForTelemetry_TruncatesTo32Characters()
        {
            var input = new string('a', 50);
            var result = ClientProcessResolver.SanitizeForTelemetry(input);
            Assert.That(result.Length, Is.EqualTo(32));
        }

        [Test]
        public void SanitizeForTelemetry_AllUnsafeCharacters_ReturnsNull()
        {
            Assert.That(ClientProcessResolver.SanitizeForTelemetry("貓貓貓"), Is.Null);
            Assert.That(ClientProcessResolver.SanitizeForTelemetry("@@@"), Is.Null);
        }

        [Test]
        public void NormalizeDisplayName_UnmappedTokenWithUnsafeCharacters_SanitizesTheCapitalizedFallback()
        {
            Assert.That(ClientProcessResolver.NormalizeDisplayName("my\"agent"), Is.EqualTo("Myagent"));
        }

        [Test]
        public void NormalizeDisplayName_UnmappedTokenThatSanitizesToNothing_ReturnsNull()
        {
            Assert.That(ClientProcessResolver.NormalizeDisplayName("貓貓貓"), Is.Null);
        }

        // ===== WalkChain =====

        [Test]
        public void WalkChain_LeafPidIsUnitysOwnProcess_ReturnsTheFixedSelfTestIdentityWithoutWalking()
        {
            // Regression guard for the loopback self-test probe (UA "UnitySkills-SelfTest"), which connects from
            // Unity's own process -- without this short-circuit the walk would find "Unity" isn't denylisted and
            // misreport it as the agent.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [4242] = new ClientProcessResolver.ProcessInfo { Ppid = 1, Name = "Unity" },
            };

            var agentId = ClientProcessResolver.WalkChain(4242, table, commandLineFetcher: null, out var visited, selfPid: 4242);

            Assert.That(agentId, Is.EqualTo(ClientProcessResolver.SelfTestAgentId));
            Assert.That(visited, Is.Empty, "must not walk into the table at all once the self-pid check matches.");
        }

        [Test]
        public void WalkChain_LeafPidDoesNotMatchSelfPid_WalksNormally()
        {
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "claude" },
            };

            var agentId = ClientProcessResolver.WalkChain(100, table, commandLineFetcher: null, out _, selfPid: 4242);

            Assert.That(agentId, Is.EqualTo("ClaudeCode"));
        }

        [Test]
        public void WalkChain_ProcessNameWithEmbeddedSpaceFromArgv0_ResolvesWithoutTruncation()
        {
            // Regression guard for the argv[0]-with-spaces bug: platform readers now derive Name straight from
            // argv[0] (never by re-splitting a joined command-line string), so a VS Code-hosted agent helper's
            // real name -- e.g. "Code Helper (Plugin)" for the default macOS install path
            // "/Applications/Visual Studio Code.app/Contents/MacOS/Code Helper (Plugin)" -- arrives intact here.
            // The old bug (naive first-space split on the joined args string) reported this as "Visual". With
            // the Electron helper-suffix strip now folded into NormalizeProcessName, it goes one step further
            // than merely-not-wrong: it collapses all the way to the host app's own mapped identity.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "Code Helper (Plugin)" },
            };

            var agentId = ClientProcessResolver.WalkChain(100, table, commandLineFetcher: null, out _);

            Assert.That(agentId, Is.Not.EqualTo("Visual"), "must not be truncated to the first space-delimited fragment of the joined command line.");
            Assert.That(agentId, Is.EqualTo("VSCode"), "the Electron helper-suffix strip collapses the helper role into the host app, which DisplayNameMap then maps to the canonical name.");
        }

        [Test]
        public void WalkChain_UnmappedElectronHelperProcess_CollapsesToTheSanitizedHostAppName()
        {
            // An unmapped Electron-hosted app's helper process still collapses to one identity (not fragmented
            // per role like " (Renderer)"/" (GPU)"/" (Plugin)", not truncated to the first space-delimited
            // word), sanitized for telemetry the same as any other unmapped capitalize fallback.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "My Space Helper (Plugin)" },
            };

            var agentId = ClientProcessResolver.WalkChain(100, table, commandLineFetcher: null, out _);

            Assert.That(agentId, Is.EqualTo("MySpace"));
        }

        [Test]
        public void WalkChain_UnmappedAppNameWithoutSpaces_StillResolvesCorrectly()
        {
            // /tmp/MyAgentApp -- an unmapped standalone app with no embedded space in argv[0], confirming the
            // argv[0] fix didn't regress the simple (no-space) case.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "/tmp/MyAgentApp" },
            };

            var agentId = ClientProcessResolver.WalkChain(100, table, commandLineFetcher: null, out _);

            Assert.That(agentId, Is.EqualTo("MyAgentApp"));
        }

        [Test]
        public void WalkChain_NonDenylistedNameThatSanitizesToEmpty_KeepsClimbingInsteadOfStopping()
        {
            // A garbage/non-ASCII process name isn't on the denylist, so it would otherwise look like a plausible
            // agent -- but it sanitizes to nothing, so the walk must treat it as inconclusive and keep climbing.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [10] = new ClientProcessResolver.ProcessInfo { Ppid = 20, Name = "貓貓貓" },
                [20] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "claude" },
            };

            var agentId = ClientProcessResolver.WalkChain(10, table, commandLineFetcher: null, out var visited);

            Assert.That(agentId, Is.EqualTo("ClaudeCode"));
            Assert.That(visited, Is.EqualTo(new List<int> { 10, 20 }));
        }

        [Test]
        public void WalkChain_CurlUnderLoginShellUnderClaude_SkipsDenylistedAncestorsAndFindsClaude()
        {
            // 100 curl -> 200 -zsh (login shell) -> 300 claude -> 400 login (root-ish, never reached)
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 200, Name = "curl" },
                [200] = new ClientProcessResolver.ProcessInfo { Ppid = 300, Name = "-zsh" },
                [300] = new ClientProcessResolver.ProcessInfo { Ppid = 400, Name = "claude" },
                [400] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "login" },
            };

            var agentId = ClientProcessResolver.WalkChain(100, table, commandLineFetcher: null, out var visited);

            Assert.That(agentId, Is.EqualTo("ClaudeCode"));
            Assert.That(visited, Is.EqualTo(new List<int> { 100, 200, 300 }));
        }

        [Test]
        public void WalkChain_NodeHostedAgentBehindAShell_ExtractsPackageFromArgsWithoutClimbingFurther()
        {
            // 100 curl -> 200 bash -> 300 node (running a scoped claude-code package) -> 400 launchd (never reached)
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 200, Name = "curl" },
                [200] = new ClientProcessResolver.ProcessInfo { Ppid = 300, Name = "bash" },
                [300] = new ClientProcessResolver.ProcessInfo { Ppid = 400, Name = "node", Args = "node /x/node_modules/@anthropic-ai/claude-code/cli.js" },
                [400] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "launchd" },
            };

            var agentId = ClientProcessResolver.WalkChain(100, table, commandLineFetcher: null, out var visited);

            Assert.That(agentId, Is.EqualTo("ClaudeCode"));
            Assert.That(visited, Is.EqualTo(new List<int> { 100, 200, 300 }), "must stop at the node hop, never reaching launchd.");
        }

        [Test]
        public void WalkChain_InterpreterArgsMissingFromTable_FallsBackToTheLazyCommandLineFetcher()
        {
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "node", Args = null }, // Windows-style: Args unavailable from the batch read
            };

            string Fetcher(int pid) => pid == 100 ? "node /tools/node_modules/opencode/cli.js" : null;

            var agentId = ClientProcessResolver.WalkChain(100, table, Fetcher, out _);

            Assert.That(agentId, Is.EqualTo("OpenCode"));
        }

        [Test]
        public void WalkChain_EntireChainIsDenylisted_ReturnsNullSoCallerKeepsTheUaGuess()
        {
            // curl -> bash -> zsh -> login, all denylisted, chain ends at the root with no real agent found.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [1] = new ClientProcessResolver.ProcessInfo { Ppid = 2, Name = "curl" },
                [2] = new ClientProcessResolver.ProcessInfo { Ppid = 3, Name = "bash" },
                [3] = new ClientProcessResolver.ProcessInfo { Ppid = 4, Name = "zsh" },
                [4] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "login" },
            };

            var agentId = ClientProcessResolver.WalkChain(1, table, commandLineFetcher: null, out _);

            Assert.That(agentId, Is.Null);
        }

        [Test]
        public void WalkChain_InterpreterWithNoExtractableArgs_KeepsClimbingLikeAPlainDenylistHit()
        {
            // node with unextractable args (`-e ...`) must NOT be reported as the agent; the walk keeps climbing to claude.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [10] = new ClientProcessResolver.ProcessInfo { Ppid = 20, Name = "node", Args = "node -e \"1+1\"" },
                [20] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "claude" },
            };

            var agentId = ClientProcessResolver.WalkChain(10, table, commandLineFetcher: null, out var visited);

            Assert.That(agentId, Is.EqualTo("ClaudeCode"));
            Assert.That(visited, Is.EqualTo(new List<int> { 10, 20 }));
        }

        // ===== Script-stem identities are tentative: the tool that launched the interpreter wins =====

        [Test]
        public void WalkChain_UnrecognizedScriptUnderClaudeCode_ReportsClaudeCodeNotTheScript()
        {
            // 100 python3 "spaced tool.py" -> 200 zsh -> 300 node (claude-code) -> 400 launchd.
            // Observed live before the fix: the walk stopped at the script and reported "Spacedtool".
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 200, Name = "python3", Args = "python3 \"/tmp/My Project/spaced tool.py\"" },
                [200] = new ClientProcessResolver.ProcessInfo { Ppid = 300, Name = "zsh" },
                [300] = new ClientProcessResolver.ProcessInfo { Ppid = 400, Name = "node", Args = "node /x/node_modules/@anthropic-ai/claude-code/cli.js" },
                [400] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "launchd" },
            };

            var result = ClientProcessResolver.WalkChainCore(100, table, commandLineFetcher: null);

            Assert.That(result.Confident, Is.EqualTo("ClaudeCode"));
            Assert.That(result.VisitedPids, Is.EqualTo(new List<int> { 100, 200, 300 }));
        }

        [Test]
        public void WalkChain_UnrecognizedScriptUnderVsCodeHelper_ReportsVSCode()
        {
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 200, Name = "python3", Args = "python3 /Users/betsy/tools/my_tool.py" },
                [200] = new ClientProcessResolver.ProcessInfo { Ppid = 300, Name = "zsh" },
                [300] = new ClientProcessResolver.ProcessInfo { Ppid = 400, Name = "Code Helper (Plugin)" },
                [400] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "launchd" },
            };

            Assert.That(ClientProcessResolver.WalkChain(100, table, commandLineFetcher: null, out _), Is.EqualTo("VSCode"));
        }

        [Test]
        public void WalkChain_UnrecognizedScriptWithNoAgentAbove_FallsBackToTheScriptStem()
        {
            // Hand-run script in a terminal: nothing above it but denylisted hops, so the stem is all there is.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 200, Name = "python3", Args = "python3 /Users/betsy/tools/my_tool.py" },
                [200] = new ClientProcessResolver.ProcessInfo { Ppid = 300, Name = "zsh" },
                [300] = new ClientProcessResolver.ProcessInfo { Ppid = 400, Name = "Terminal" },
                [400] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "launchd" },
            };

            var result = ClientProcessResolver.WalkChainCore(100, table, commandLineFetcher: null);

            Assert.That(result.Confident, Is.Null);
            Assert.That(result.Tentative, Is.EqualTo("My_tool"));
            Assert.That(ClientProcessResolver.WalkChain(100, table, commandLineFetcher: null, out _), Is.EqualTo("My_tool"));
        }

        [Test]
        public void WalkChain_NestedUnrecognizedScripts_TheOutermostStemIsTheTentativeIdentity()
        {
            // helper.py spawned by my_agent.py: the launcher is the better guess.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 200, Name = "python3", Args = "python3 /opt/x/helper.py" },
                [200] = new ClientProcessResolver.ProcessInfo { Ppid = 300, Name = "python3", Args = "python3 /opt/x/my_agent.py" },
                [300] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "launchd" },
            };

            Assert.That(ClientProcessResolver.WalkChain(100, table, commandLineFetcher: null, out _), Is.EqualTo("My_agent"));
        }

        [Test]
        public void WalkChainCore_ChainBreaksAtAStaleAncestor_StillReportsTheTentativeIdentity()
        {
            // Windows shape: explorer.exe's ppid is a userinit that exited long ago, so the table never reaches
            // pid 0. The script stem must survive that, or an unmapped script-based agent degrades to the UA guess.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 200, Name = "python3", Args = "python3 C:\\tools\\my_tool.py" },
                [200] = new ClientProcessResolver.ProcessInfo { Ppid = 300, Name = "cmd" },
                [300] = new ClientProcessResolver.ProcessInfo { Ppid = 9999, Name = "explorer" },
            };

            var result = ClientProcessResolver.WalkChainCore(100, table, commandLineFetcher: null);

            Assert.That(result.Confident, Is.Null);
            Assert.That(result.Tentative, Is.EqualTo("My_tool"));
        }

        [Test]
        public void WalkChain_InstalledEntryPointUnderBin_IsConfidentEvenInsideAnIde()
        {
            // npm -g symlink / pip console script: `[node, /opt/homebrew/bin/foo-agent]` under a VS Code terminal.
            // An installed CLI is the agent; the IDE above it is only the host.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 200, Name = "node", Args = "node /opt/homebrew/bin/foo-agent" },
                [200] = new ClientProcessResolver.ProcessInfo { Ppid = 300, Name = "zsh" },
                [300] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "Code Helper (Plugin)" },
            };

            var result = ClientProcessResolver.WalkChainCore(100, table, commandLineFetcher: null);

            Assert.That(result.Confident, Is.EqualTo("Foo-agent"));
            Assert.That(result.VisitedPids, Is.EqualTo(new List<int> { 100 }));
        }

        [TestCase("node /opt/homebrew/bin/foo-agent", true)]
        [TestCase("python3 /Users/betsy/.venv/bin/foo", true)]
        [TestCase("node /x/node_modules/@anthropic-ai/claude-code/cli.js", true)]
        [TestCase("python3 /Users/betsy/tools/my_tool.py", false)]
        [TestCase("python3 /tmp/x.py", false)]
        public void TryExtractFromArgs_IsPackage_OnlyForInstalledEntryPoints(string args, bool expected)
        {
            ClientProcessResolver.TryExtractFromArgs(args, out var isPackage);
            Assert.That(isPackage, Is.EqualTo(expected));
        }

        [Test]
        public void TryExtractFromArgs_DashP_IsAPathArgumentForNonJsRuntimes()
        {
            Assert.That(ClientProcessResolver.TryExtractFromArgs("java -p /opt/agent/mods -m agent/agent.Main"), Is.EqualTo("mods"));
            Assert.That(ClientProcessResolver.TryExtractFromArgs("node -p \"require('/opt/tools/agent.js').version\""), Is.Null);
        }

        [Test]
        public void WalkChain_ScriptStemInTheDisplayMap_IsConfidentAndStopsTheWalk()
        {
            // kimi.py is a known agent -- the walk must not climb past it to a host IDE.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 200, Name = "python3", Args = "python3 /Users/betsy/tools/kimi.py" },
                [200] = new ClientProcessResolver.ProcessInfo { Ppid = 300, Name = "zsh" },
                [300] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "Code Helper (Plugin)" },
            };

            var result = ClientProcessResolver.WalkChainCore(100, table, commandLineFetcher: null);

            Assert.That(result.Confident, Is.EqualTo("KimiCode"));
            Assert.That(result.VisitedPids, Is.EqualTo(new List<int> { 100 }));
        }

        [TestCase("claude-code", "ClaudeCode")]
        [TestCase("Claude", "ClaudeCode")]
        [TestCase("agy", "Antigravity")]
        [TestCase("ClaudeCode", "ClaudeCode")]
        [TestCase("MyScript", "MyScript")] // custom ids are the caller's own
        [TestCase("Python", "Python")]
        public void CanonicalizeExplicitAgentId_FoldsKnownAliasesAndKeepsCustomIds(string header, string expected)
        {
            Assert.That(ClientProcessResolver.CanonicalizeExplicitAgentId(header), Is.EqualTo(expected));
        }

        [Test]
        public void WalkChain_DepthCapExceeded_GivesUpBeforeReachingTheRealAgent()
        {
            // 10 wrapper hops of denylisted shells (well past MaxWalkDepth), with the real agent one hop beyond the cap.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>();
            int hops = ClientProcessResolver.MaxWalkDepth + 2;
            for (int pid = 0; pid < hops; pid++)
                table[pid] = new ClientProcessResolver.ProcessInfo { Ppid = pid + 1, Name = "sh" };
            table[hops] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "claude" };

            var agentId = ClientProcessResolver.WalkChain(0, table, commandLineFetcher: null, out var visited);

            Assert.That(agentId, Is.Null, "the agent sits beyond MaxWalkDepth and must not be found.");
            Assert.That(visited.Count, Is.EqualTo(ClientProcessResolver.MaxWalkDepth));
        }

        [Test]
        public void WalkChain_SelfReferentialPpid_TerminatesWithoutHanging()
        {
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [1] = new ClientProcessResolver.ProcessInfo { Ppid = 1, Name = "sh" }, // points to itself
            };

            var agentId = ClientProcessResolver.WalkChain(1, table, commandLineFetcher: null, out var visited);

            Assert.That(agentId, Is.Null);
            Assert.That(visited, Is.EqualTo(new List<int> { 1 }));
        }

        [Test]
        public void WalkChain_LeafPidMissingFromTable_ReturnsNullImmediately()
        {
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>();
            var agentId = ClientProcessResolver.WalkChain(999, table, commandLineFetcher: null, out var visited);

            Assert.That(agentId, Is.Null);
            Assert.That(visited, Is.Empty);
        }

        [Test]
        public void WalkChain_NullTable_DegradesToNullInsteadOfThrowing()
        {
            Assert.That(ClientProcessResolver.WalkChain(1, null, commandLineFetcher: null, out var visited), Is.Null);
            Assert.That(visited, Is.Empty);
        }

        [Test]
        public void WalkChain_PidCacheHit_ShortCircuitsWithoutInspectingTheName()
        {
            // Ancestor 200 is a previously-resolved pid (per an isolated fake cache, not the real production one);
            // WalkChain must return that cached identity without ever evaluating 200's own (garbage) process name.
            var table = new Dictionary<int, ClientProcessResolver.ProcessInfo>
            {
                [100] = new ClientProcessResolver.ProcessInfo { Ppid = 200, Name = "curl" },
                [200] = new ClientProcessResolver.ProcessInfo { Ppid = 300, Name = "not-a-real-agent-name" },
                [300] = new ClientProcessResolver.ProcessInfo { Ppid = 0, Name = "claude" },
            };

            bool FakeCache(int pid, out string agentId)
            {
                if (pid == 200) { agentId = "PreCachedAgent"; return true; }
                agentId = null;
                return false;
            }

            var agentId = ClientProcessResolver.WalkChain(100, table, commandLineFetcher: null, out var visited, FakeCache);

            Assert.That(agentId, Is.EqualTo("PreCachedAgent"));
            Assert.That(visited, Is.EqualTo(new List<int> { 100, 200 }), "must stop at the cache hit, never reaching 300.");
        }

        // ===== TtlCache =====

        [Test]
        public void TtlCache_PutThenTryGet_RoundTrips()
        {
            var cache = new ClientProcessResolver.TtlCache(cap: 10, ttlSeconds: 60);
            cache.Put(1, "ClaudeCode");

            Assert.That(cache.TryGet(1, out var value), Is.True);
            Assert.That(value, Is.EqualTo("ClaudeCode"));
        }

        [Test]
        public void TtlCache_TryGetOnMissingKey_ReturnsFalse()
        {
            var cache = new ClientProcessResolver.TtlCache(cap: 10, ttlSeconds: 60);
            Assert.That(cache.TryGet(42, out var value), Is.False);
            Assert.That(value, Is.Null);
        }

        [Test]
        public void TtlCache_EntryExpiresAfterTtl_UsingAnInjectedClock()
        {
            long now = 0;
            var cache = new ClientProcessResolver.TtlCache(cap: 10, ttlSeconds: 60) { NowTicks = () => now };

            cache.Put(1, "ClaudeCode");
            Assert.That(cache.TryGet(1, out _), Is.True, "must still be valid immediately after insertion.");

            now += System.TimeSpan.FromSeconds(61).Ticks; // past the 60s TTL
            Assert.That(cache.TryGet(1, out var expired), Is.False, "must have expired.");
            Assert.That(expired, Is.Null);
        }

        [Test]
        public void TtlCache_OverCapacity_EvictsOldestFirst()
        {
            var cache = new ClientProcessResolver.TtlCache(cap: 3, ttlSeconds: 60);
            for (int i = 1; i <= 5; i++)
                cache.Put(i, "agent" + i);

            Assert.That(cache.TryGet(1, out _), Is.False, "the oldest entry must have been evicted first.");
            Assert.That(cache.TryGet(2, out _), Is.False, "the second-oldest entry must also have been evicted.");
            Assert.That(cache.TryGet(4, out var v4), Is.True);
            Assert.That(v4, Is.EqualTo("agent4"));
            Assert.That(cache.TryGet(5, out var v5), Is.True);
            Assert.That(v5, Is.EqualTo("agent5"));
        }

        [Test]
        public void TtlCache_RefreshingAnExistingKey_DoesNotGrowUnboundedOrCountTwice()
        {
            var cache = new ClientProcessResolver.TtlCache(cap: 3, ttlSeconds: 60);
            cache.Put(1, "first");
            cache.Put(1, "second"); // same key -- refresh in place, not a new insertion

            Assert.That(cache.Count, Is.EqualTo(1));
            Assert.That(cache.TryGet(1, out var value), Is.True);
            Assert.That(value, Is.EqualTo("second"));
        }
    }
}

// Producer:Betsy
