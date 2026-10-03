using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Tells a test that locks a path with chmod and expects the write or read to fail whether that can work here.
    /// Windows has no chmod, and root (the game-ci containers run Unity as root) ignores POSIX mode bits, so on
    /// those hosts the locked write succeeds and the assertion would fail for a reason unrelated to the code under
    /// test. The probe locks a scratch directory once and checks whether this process is really refused.
    /// </summary>
    internal static class PosixPermissionProbe
    {
        private static bool? _canDeny;

        public static void IgnoreUnlessChmodCanDenyAccess()
        {
            if (!CanDenyAccess())
                Assert.Ignore("chmod cannot deny this process access (Windows, or running as root); the locked-path failure cannot be simulated here.");
        }

        public static bool CanDenyAccess()
        {
            if (_canDeny.HasValue) return _canDeny.Value;
            if (Application.platform == RuntimePlatform.WindowsEditor) return (_canDeny = false).Value;

            var dir = Path.Combine(Path.GetTempPath(), "us-permprobe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Chmod("555", dir);
                try
                {
                    File.WriteAllText(Path.Combine(dir, "probe"), "x");
                    _canDeny = false;
                }
                catch (UnauthorizedAccessException) { _canDeny = true; }
                catch (IOException) { _canDeny = true; }
            }
            finally
            {
                Chmod("755", dir);
                Directory.Delete(dir, true);
            }
            return _canDeny.Value;
        }

        private static void Chmod(string mode, string path)
        {
            using (var process = Process.Start(new ProcessStartInfo("chmod", $"{mode} \"{path}\"")
                   { UseShellExecute = false, CreateNoWindow = true }))
            {
                process?.WaitForExit();
            }
        }
    }
}

// Producer:Betsy
