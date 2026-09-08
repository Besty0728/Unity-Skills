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
        public void IsDenylisted_PrefixEntries_MatchVersionedNames(string name)
        {
            Assert.That(ClientProcessResolver.IsDenylisted(name), Is.True, $"'{name}' should match a denylist prefix.");
        }

        [TestCase("claude")]
        [TestCase("augment")]
        [TestCase("some-brand-new-cli")]
        [TestCase("node")] // interpreters are handled separately, not via the plain denylist
        public void IsDenylisted_AgentAndInterpreterNames_AreNotExcluded(string name)
        {
            Assert.That(ClientProcessResolver.IsDenylisted(name), Is.False, $"'{name}' must not be treated as a denylisted shell/system process.");
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

        // ===== Display-name normalization =====

        [TestCase("claude", "ClaudeCode")]
        [TestCase("claude-code", "ClaudeCode")]
        [TestCase("CLAUDE", "ClaudeCode")] // case-insensitive
        [TestCase("augment", "Augment")]
        [TestCase("q", "AmazonQ")]
        [TestCase("code", "VSCode")]
        public void NormalizeDisplayName_KnownTokens_MapToTheAgentKeywordsValue(string token, string expected)
        {
            Assert.That(ClientProcessResolver.NormalizeDisplayName(token), Is.EqualTo(expected));
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
