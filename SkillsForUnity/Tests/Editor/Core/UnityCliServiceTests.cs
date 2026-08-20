using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// UnityCliService 配置与 CLI 探测行为测试。
    /// 不依赖本机是否安装 Unity CLI，失败路径通过临时脚本覆盖。
    /// </summary>
    [TestFixture]
    public class UnityCliServiceTests
    {
        private string _configDir;
        private string _configFile;
        private bool _configExisted;
        private byte[] _originalConfigBytes;

        [SetUp]
        public void SetUp()
        {
            _configDir = Path.Combine(Application.dataPath, "../Library/UnitySkills");
            _configFile = Path.Combine(_configDir, "cli_config.json");

            // 备份真实绑定配置，避免测试破坏用户数据。
            _configExisted = File.Exists(_configFile);
            _originalConfigBytes = _configExisted ? File.ReadAllBytes(_configFile) : null;

            ClearConfigCache();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (_configExisted)
                {
                    // 恢复原始配置。
                    Directory.CreateDirectory(_configDir);
                    File.WriteAllBytes(_configFile, _originalConfigBytes);
                }
                else if (File.Exists(_configFile))
                {
                    File.Delete(_configFile);
                }
            }
            catch { /* best effort */ }
            ClearConfigCache();
        }

        private static void ClearConfigCache()
        {
            typeof(UnityCliService)
                .GetField("_cached", BindingFlags.NonPublic | BindingFlags.Static)
                ?.SetValue(null, null);
        }

        private void WriteConfig(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configFile));
            File.WriteAllText(_configFile, json);
            ClearConfigCache();
        }

        private static (bool success, string version, string error) TryGetVersion(string path)
        {
            var method = typeof(UnityCliService).GetMethod(
                "TryGetVersion", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "TryGetVersion method not found");
            object boxed = method.Invoke(null, new object[] { path });
            var type = boxed.GetType();
            bool success = (bool)type.GetField("success").GetValue(boxed);
            string version = (string)type.GetField("version").GetValue(boxed);
            string error = (string)type.GetField("error").GetValue(boxed);
            return (success, version, error);
        }

        [Test]
        public void LoadConfig_WhenFileMissing_ReturnsNullAndIsBoundFalse()
        {
            if (File.Exists(_configFile))
                File.Delete(_configFile);
            ClearConfigCache();

            Assert.That(UnityCliService.LoadConfig(), Is.Null);
            Assert.That(UnityCliService.IsBound, Is.False);
        }

        [Test]
        public void LoadConfig_WhenEnabledFalse_ReturnsConfigButIsBoundFalse()
        {
            WriteConfig(@"{ ""schemaVersion"": 1, ""enabled"": false, ""cliPath"": ""/x/unity"" }");

            var cfg = UnityCliService.LoadConfig();
            Assert.That(cfg, Is.Not.Null);
            Assert.That(cfg.enabled, Is.False);
            Assert.That(UnityCliService.IsBound, Is.False);
        }

        [Test]
        public void LoadConfig_MissingCliRunAndCliBuildKeys_DefaultToFalse()
        {
            WriteConfig(@"{ ""schemaVersion"": 1, ""enabled"": true, ""cliPath"": ""/x/unity"", ""features"": { ""coldStart"": true, ""openArgs"": true, ""cliTest"": true } }");

            var cfg = UnityCliService.LoadConfig();
            Assert.That(cfg, Is.Not.Null);
            Assert.That(cfg.features.cliRun, Is.False, "cliRun missing key must default to false");
            Assert.That(cfg.features.cliBuild, Is.False, "cliBuild missing key must default to false");
        }

        [Test]
        public void TryGetVersion_WhenFileMissing_ReturnsNoConcreteError()
        {
            var (success, version, error) = TryGetVersion(Path.Combine(Path.GetTempPath(), $"unity_cli_missing_{Guid.NewGuid()}"));
            Assert.That(success, Is.False);
            Assert.That(error, Is.Null);
        }

        [Test]
        public void TryGetVersion_RealCli_WhenPresent_ReturnsSuccess()
        {
            string found = FindLocalCli();
            if (string.IsNullOrEmpty(found))
            {
                Assert.Ignore("No local Unity CLI found to verify success path.");
                return;
            }

            var (success, version, error) = TryGetVersion(found);
            Assert.That(success, Is.True, $"Expected {found} --version to succeed; error={error}");
            Assert.That(version, Is.Not.Null.And.Not.Empty);
        }

        private static string FindLocalCli()
        {
            string env = Environment.GetEnvironmentVariable("UNITY_CLI_PATH");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                return env;

            string pathResolved = TryResolveOnPath("unity");
            if (!string.IsNullOrEmpty(pathResolved) && File.Exists(pathResolved))
                return pathResolved;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
#if UNITY_EDITOR_WIN
            string fallback = Path.Combine(home, ".unity", "bin", "unity.exe");
#else
            string fallback = Path.Combine(home, ".unity", "bin", "unity");
#endif
            return File.Exists(fallback) ? fallback : null;
        }

        private static string TryResolveOnPath(string name)
        {
            try
            {
#if UNITY_EDITOR_WIN
                var psi = new ProcessStartInfo("where", name)
#else
                var psi = new ProcessStartInfo("which", name)
#endif
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0)
                    {
                        var first = stdout
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault();
                        return first?.Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        [Test]
        [Platform("Unix,Linux,MacOsX")]
        public void TryGetVersion_GlibcStyleStderr_ReturnsIncompatibleSystem()
        {
            string script = Path.Combine(Path.GetTempPath(), $"unity_cli_glibc_{Guid.NewGuid()}.sh");
            File.WriteAllText(script,
                "#!/bin/sh\n" +
                "echo '/lib/x86_64-linux-gnu/libc.so.6: version `GLIBC_2.34\\' not found' >&2\n" +
                "exit 1\n");
            MakeExecutable(script);

            try
            {
                var (success, _, error) = TryGetVersion(script);
                Assert.That(success, Is.False);
                Assert.That(error, Is.EqualTo(UnityCliService.CliErrorIncompatibleSystem));
            }
            finally
            {
                try { File.Delete(script); } catch { }
            }
        }

        [Test]
        [Platform("Unix,Linux,MacOsX")]
        public void TryGetVersion_GenericFailure_ReturnsLaunchFailed()
        {
            string script = Path.Combine(Path.GetTempPath(), $"unity_cli_fail_{Guid.NewGuid()}.sh");
            File.WriteAllText(script,
                "#!/bin/sh\n" +
                "echo 'unknown failure' >&2\n" +
                "exit 1\n");
            MakeExecutable(script);

            try
            {
                var (success, _, error) = TryGetVersion(script);
                Assert.That(success, Is.False);
                Assert.That(error, Is.EqualTo(UnityCliService.CliErrorLaunchFailed));
            }
            finally
            {
                try { File.Delete(script); } catch { }
            }
        }

        private static void MakeExecutable(string path)
        {
            using (var chmod = Process.Start(new ProcessStartInfo("chmod", "+x " + path)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                chmod.WaitForExit(5000);
            }
        }
    }
}

// Producer:Betsy
