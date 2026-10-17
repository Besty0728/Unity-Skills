using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Candidate order and filtering run on injected machine facts, so every platform's rules are checked on any host
    /// and nothing here starts a login shell. The only process launches are a missing executable (fails before
    /// anything runs) and one <c>python -c</c> probe on an interpreter that is already on disk.
    /// </summary>
    [TestFixture]
    public class PythonLocatorTests
    {
        [TestCase(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\python.exe", true)]
        [TestCase(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\PythonSoftwareFoundation.Python.3.11_qbz5n2kfra8p0\python.exe", true)]
        [TestCase("C:/Users/me/AppData/Local/Microsoft/WindowsApps/python3.exe", true)]
        [TestCase(@"C:\Users\me\AppData\Local\Programs\Python\Python311\python.exe", false)]
        [TestCase("/usr/bin/python3", false)]
        [TestCase("", false)]
        public void IsWindowsStoreStub(string path, bool expected)
        {
            Assert.That(PythonLocator.IsWindowsStoreStub(path), Is.EqualTo(expected));
        }

        [Test]
        public void TryParseProbeOutput_ReadsVersionAndExecutable()
        {
            Assert.That(PythonLocator.TryParseProbeOutput("3 11 4\n/opt/homebrew/bin/python3\n", out var version, out var exe), Is.True);
            Assert.That(version, Is.EqualTo(new Version(3, 11, 4)));
            Assert.That(exe, Is.EqualTo("/opt/homebrew/bin/python3"));

            Assert.That(PythonLocator.TryParseProbeOutput("3 9 6\r\nC:\\Python39\\python.exe\r\n", out version, out exe), Is.True);
            Assert.That(version, Is.EqualTo(new Version(3, 9, 6)));
            Assert.That(exe, Is.EqualTo("C:\\Python39\\python.exe"));

            Assert.That(PythonLocator.TryParseProbeOutput("3 12 1", out version, out exe), Is.True);
            Assert.That(exe, Is.Null);
        }

        [TestCase("(2, 7, 18)\n/usr/bin/python")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("Python was not found; run without arguments to install from the Microsoft Store")]
        [TestCase("3 x 1\n/usr/bin/python3")]
        public void TryParseProbeOutput_RejectsAnythingElse(string stdout)
        {
            Assert.That(PythonLocator.TryParseProbeOutput(stdout, out _, out _), Is.False);
        }

        [TestCase(3, 8, false)]
        [TestCase(3, 9, true)]
        [TestCase(3, 13, true)]
        [TestCase(4, 0, true)]
        public void IsSupported_StartsAt39(int major, int minor, bool expected)
        {
            Assert.That(PythonLocator.IsSupported(new Version(major, minor)), Is.EqualTo(expected));
        }

        [TestCase("Python311", 3, 11)]
        [TestCase("Python39-32", 3, 9)]
        [TestCase("python313", 3, 13)]
        public void ParseInstallDirVersion_ReadsTheFolderName(string name, int major, int minor)
        {
            Assert.That(PythonLocator.ParseInstallDirVersion(name), Is.EqualTo(new Version(major, minor)));
        }

        [TestCase("Python3")]
        [TestCase("Scripts")]
        [TestCase(null)]
        public void ParseInstallDirVersion_IgnoresOtherFolders(string name)
        {
            Assert.That(PythonLocator.ParseInstallDirVersion(name), Is.Null);
        }

        [Test]
        public void Override_IsTheOnlyCandidate()
        {
            int lookups = 0;
            var facts = new PythonLocator.Facts
            {
                OverridePath = "  /custom/python3  ",
                FileExists = _ => true,
                ShellLookup = () =>
                {
                    lookups++;
                    return new List<string> { "/usr/local/bin/python3" };
                },
            };

            var candidates = PythonLocator.EnumerateCandidates(facts).ToList();
            Assert.That(candidates.Select(c => c.FileName), Is.EqualTo(new[] { "/custom/python3" }));
            Assert.That(candidates[0].Source, Is.EqualTo(PythonLocator.SourceOverride));
            Assert.That(lookups, Is.Zero);
        }

        [Test]
        public void Windows_TriesTheLauncherFirstAndSkipsStoreStubs()
        {
            const string real = @"C:\Tools\Python310\python.exe";
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                real,
                @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\python.exe",
                @"C:\L\Programs\Python\Python312\python.exe",
                @"C:\L\Programs\Python\Python39\python.exe",
                @"C:\L\Programs\Python\Python27\python.exe",
            };
            var facts = new PythonLocator.Facts
            {
                IsWindows = true,
                LocalAppData = @"C:\L",
                FileExists = existing.Contains,
                ListDirectories = dir => dir == @"C:\L\Programs\Python"
                    ? new[] { dir + @"\Python39", dir + @"\Python27", dir + @"\Python312" }
                    : Array.Empty<string>(),
                ShellLookup = () => new List<string>
                {
                    @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\python.exe",
                    real,
                    "INFO: Could not find files for the given pattern(s).",
                },
            };

            var candidates = PythonLocator.EnumerateCandidates(facts).ToList();
            Assert.That(candidates.Select(c => c.FileName), Is.EqualTo(new[]
            {
                "py", real, @"C:\L\Programs\Python\Python312\python.exe", @"C:\L\Programs\Python\Python39\python.exe",
            }));
            Assert.That(candidates[0].PrefixArgs, Is.EqualTo(new[] { "-3" }));
            Assert.That(candidates[0].Source, Is.EqualTo(PythonLocator.SourcePyLauncher));
        }

        [Test]
        public void Windows_ShellLookupOnlyRunsWhenTheLauncherIsNotEnough()
        {
            int lookups = 0;
            var facts = new PythonLocator.Facts
            {
                IsWindows = true,
                ShellLookup = () =>
                {
                    lookups++;
                    return null;
                },
            };

            using (var candidates = PythonLocator.EnumerateCandidates(facts).GetEnumerator())
            {
                Assert.That(candidates.MoveNext(), Is.True);
                Assert.That(candidates.Current.FileName, Is.EqualTo("py"));
                Assert.That(lookups, Is.Zero);
                Assert.That(candidates.MoveNext(), Is.False);
                Assert.That(lookups, Is.EqualTo(1));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Mac_SystemPythonNeedsTheDeveloperTools(bool developerToolsHavePython)
        {
            var facts = new PythonLocator.Facts
            {
                IsMac = true,
                FileExists = path => path == "/usr/bin/python3" || path == "/opt/homebrew/bin/python3",
                ShellLookup = () => new List<string> { "/usr/bin/python3" },
                MacDeveloperToolsHavePython = () => developerToolsHavePython,
            };

            var names = PythonLocator.EnumerateCandidates(facts).Select(c => c.FileName).ToArray();
            Assert.That(names, developerToolsHavePython
                ? Is.EqualTo(new[] { "/usr/bin/python3", "/opt/homebrew/bin/python3" })
                : Is.EqualTo(new[] { "/opt/homebrew/bin/python3" }));
        }

        [Test]
        public void Unix_SkipsShellNoiseMissingFilesAndDuplicates()
        {
            var facts = new PythonLocator.Facts
            {
                FileExists = path => path == "/home/me/.pyenv/shims/python3" || path == "/usr/local/bin/python3",
                ShellLookup = () => new List<string>
                {
                    "Last login: Fri Oct 16 on ttys001",
                    "/home/me/.pyenv/shims/python3",
                    "/nonexistent/python3",
                    "/usr/local/bin/python3",
                },
            };

            var candidates = PythonLocator.EnumerateCandidates(facts).ToList();
            Assert.That(candidates.Select(c => c.FileName), Is.EqualTo(new[] { "/home/me/.pyenv/shims/python3", "/usr/local/bin/python3" }));
            Assert.That(candidates.Select(c => c.Source), Is.EqualTo(new[] { PythonLocator.SourceShell, PythonLocator.SourceShell }));
        }

        [Test]
        public void Verify_MissingExecutable_FailsWithoutThrowing()
        {
            var missing = Path.Combine(Path.GetTempPath(), "unityskills-no-python-" + Guid.NewGuid().ToString("N"), "python3");
            var result = PythonLocator.Verify(new PythonCandidate(missing, PythonLocator.SourceOverride));

            Assert.That(result.Found, Is.False);
            Assert.That(result.FromOverride, Is.True);
            Assert.That(result.Error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void Verify_AnInstalledInterpreter_ReportsItsRealVersion()
        {
            var facts = PythonLocator.CaptureFacts();
            facts.OverridePath = null;
            facts.ShellLookup = () => null;
            facts.MacDeveloperToolsHavePython = () => File.Exists("/Library/Developer/CommandLineTools/usr/bin/python3");
            var candidate = PythonLocator.EnumerateCandidates(facts).FirstOrDefault();
            if (candidate == null) Assert.Ignore("No interpreter in the usual places on this machine.");

            var result = PythonLocator.Verify(candidate);
            if (!result.Found) Assert.Ignore($"{candidate.FileName} is not a usable Python 3.9+: {result.Error}");

            Assert.That(PythonLocator.IsSupported(new Version(result.Version)), Is.True);
            Assert.That(File.Exists(result.Executable), Is.True, result.Executable);
            Assert.That(Application.platform == RuntimePlatform.WindowsEditor || result.Executable.StartsWith("/"), Is.True);
        }
    }
}

// Producer:Betsy
