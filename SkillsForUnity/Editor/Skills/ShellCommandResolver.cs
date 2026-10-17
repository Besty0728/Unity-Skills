using System;
using System.Collections.Generic;

namespace UnitySkills.Internal
{
    /// <summary>
    /// Resolves a command on the user's PATH through a shell. The editor's GUI process (especially when launched from
    /// the macOS Dock) doesn't inherit the user shell's PATH, so the lookup goes through an interactive login shell to
    /// let the profile take effect; on Windows it goes through cmd.exe (e.g. "where"). Shared by the Unity CLI
    /// detection and the evaluation Python lookup. Blocking: call it off the main thread.
    /// </summary>
    internal static class ShellCommandResolver
    {
        /// <summary>The first output line of <paramref name="cmd"/>, or null on failure.</summary>
        internal static string Resolve(string cmd, bool winWhere)
        {
            var lines = ResolveAll(cmd, winWhere);
            return lines != null && lines.Count > 0 ? lines[0] : null;
        }

        /// <summary>Every non-empty output line of <paramref name="cmd"/> ("where" may print several), or null on failure.</summary>
        internal static List<string> ResolveAll(string cmd, bool winWhere)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                if (winWhere)
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/c " + cmd;
                }
                else
                {
                    string shell = Environment.GetEnvironmentVariable("SHELL");
                    psi.FileName = string.IsNullOrEmpty(shell) ? "/bin/zsh" : shell;
                    // -lic: login + interactive. -l alone doesn't load .zshrc (that's
                    // interactive-only), and Unity CLI and similar tools' PATH injection is
                    // written precisely in .zshrc — verified in practice that -lc alone fails to find it.
                    psi.Arguments = "-lic \"" + cmd + "\"";
                }
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.BeginErrorReadLine();          // Drain stderr, to avoid a deadlock from a full buffer (rc files can be noisy)
                    string stdout = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
                    if (p.ExitCode != 0) return null;
                    var lines = new List<string>();
                    foreach (var line in (stdout ?? "").Trim()
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0) lines.Add(trimmed);
                    }
                    return lines;
                }
            }
            catch { return null; }
        }
    }
}

// Producer:Betsy
