using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// How local self-update results are presented, shared by the settings drawer and the update banner: the status-line
    /// reason, the mode hint, the dialog that explains a refusal (what was left untouched and how to proceed), and the
    /// confirmation before an archive replaces a folder whose changes cannot be checked.
    /// </summary>
    internal static class SelfUpdateFeedback
    {
        /// <summary>Localization key of the "{0}" in update_check_failed_fmt.</summary>
        internal static string ReasonKey(LocalUpdateOutcome outcome)
        {
            switch (outcome)
            {
                case LocalUpdateOutcome.Busy: return "update_check_reason_busy";
                case LocalUpdateOutcome.PackageRootNotFound: return "update_check_reason_path";
                case LocalUpdateOutcome.NetworkError: return "update_check_reason_network";
                case LocalUpdateOutcome.DiskError: return "update_check_reason_disk";
                case LocalUpdateOutcome.InvalidPackage: return "update_check_reason_invalid";
                case LocalUpdateOutcome.RecheckRequired: return "update_check_reason_recheck";
                case LocalUpdateOutcome.GitMissingForRepository: return "update_check_reason_git_missing";
                case LocalUpdateOutcome.GitTooOld: return "update_check_reason_git_too_old";
                case LocalUpdateOutcome.GitUnsafeRepository: return "update_check_reason_git_unsafe";
                case LocalUpdateOutcome.GitRepositoryLocked: return "update_check_reason_git_locked";
                case LocalUpdateOutcome.GitCommandFailed: return "update_check_reason_git_failed";
                case LocalUpdateOutcome.DirtyWorktree: return "update_check_reason_git_dirty";
                case LocalUpdateOutcome.DetachedHead: return "update_check_reason_git_detached";
                case LocalUpdateOutcome.UnsupportedBranch: return "update_check_reason_git_branch";
                case LocalUpdateOutcome.Diverged: return "update_check_reason_git_diverged";
                case LocalUpdateOutcome.WouldOverwriteLocalFiles: return "update_check_reason_git_collision";
                case LocalUpdateOutcome.MergeInterrupted: return "update_check_reason_git_interrupted";
                case LocalUpdateOutcome.VendoredSubtreeModified: return "update_check_reason_vendored_dirty";
                case LocalUpdateOutcome.PackageDirIsRepository: return "update_check_reason_pkg_is_repo";
                case LocalUpdateOutcome.PackageDirIsLink: return "update_check_reason_pkg_is_link";
                default: return "update_check_reason_unknown";
            }
        }

        /// <summary>Key and verbatim argument of the drawer's update-method line, or a null key to hide it.</summary>
        internal static string ModeHintKey(LocalUpdateProbe probe, out string argument)
        {
            argument = null;
            if (probe == null) return null;
            if (probe.IsOfficialClone)
            {
                argument = probe.HeadLabel ?? probe.Branch ?? LocalSelfUpdateService.ShortSha(probe.HeadSha);
                return "update_mode_git_fmt";
            }
            switch (probe.Track)
            {
                case LocalUpdateTrack.Archive:
                case LocalUpdateTrack.ArchiveInRepository:
                case LocalUpdateTrack.ArchiveUnverified:
                    return "update_mode_archive";
                default:
                    return null;
            }
        }

        /// <summary>Status line after a finished update attempt: key plus the verbatim {0}, if any.</summary>
        internal static string DoneStatusKey(LocalUpdateResult result, out string argument)
        {
            argument = null;
            if (string.IsNullOrEmpty(result?.BackupPath)) return "update_check_done";
            argument = result.BackupPath;
            return "update_check_done_backup_fmt";
        }

        /// <summary>
        /// Explains a refusal the user has to act on (lists the blocking paths where there are any). Other failures stay
        /// on the status line, with the details in the Console. Returns whether a dialog was shown.
        /// </summary>
        internal static bool ShowRefusalDialog(LocalUpdateResult result)
        {
            var body = BuildRefusalBody(result);
            if (body == null) return false;
            EditorUtility.DisplayDialog(SkillsLocalization.Get("update_git_dialog_title"), body, SkillsLocalization.Get("dialog_ok"));
            return true;
        }

        internal static string BuildRefusalBody(LocalUpdateResult result)
        {
            if (result == null) return null;
            var probe = result.Probe ?? new LocalUpdateProbe();
            var repo = probe.RepoRoot ?? probe.MarkerRoot ?? probe.PackageDir;
            var branch = probe.Branch ?? probe.HeadLabel ?? result.TargetLabel;
            switch (result.Outcome)
            {
                case LocalUpdateOutcome.DirtyWorktree:
                    return SkillsLocalization.Get("update_git_dirty_body_fmt", repo, FormatEntries(result.Entries, result.EntryCount));
                case LocalUpdateOutcome.DetachedHead:
                    return SkillsLocalization.Get("update_git_detached_body_fmt", repo, probe.HeadLabel ?? LocalSelfUpdateService.ShortSha(probe.HeadSha));
                case LocalUpdateOutcome.UnsupportedBranch:
                    return SkillsLocalization.Get("update_git_branch_body_fmt", repo, branch);
                case LocalUpdateOutcome.Diverged:
                    return SkillsLocalization.Get("update_git_diverged_body_fmt", repo, branch);
                case LocalUpdateOutcome.WouldOverwriteLocalFiles:
                    return SkillsLocalization.Get("update_git_collision_body_fmt", repo, FormatEntries(result.Entries, result.EntryCount));
                case LocalUpdateOutcome.MergeInterrupted:
                    return SkillsLocalization.Get("update_git_interrupted_body_fmt", repo, branch);
                case LocalUpdateOutcome.GitMissingForRepository:
                    return SkillsLocalization.Get("update_git_missing_body_fmt", probe.MarkerRoot ?? repo);
                case LocalUpdateOutcome.VendoredSubtreeModified:
                {
                    var entries = result.EntryCount > 0 ? result.Entries : probe.Entries;
                    var count = result.EntryCount > 0 ? result.EntryCount : probe.EntryCount;
                    return SkillsLocalization.Get("update_vendored_dirty_body_fmt", probe.PackageDir, FormatEntries(entries, count));
                }
                case LocalUpdateOutcome.PackageDirIsRepository:
                    return SkillsLocalization.Get("update_pkg_is_repo_body_fmt", probe.PackageDir);
                case LocalUpdateOutcome.PackageDirIsLink:
                    return SkillsLocalization.Get("update_pkg_is_link_body_fmt", probe.PackageDir);
                default:
                    return null;
            }
        }

        /// <summary>
        /// A copy inside another repository without a usable git: its uncommitted changes cannot be checked, so replacing
        /// it needs an explicit yes. Esc or closing the dialog counts as no.
        /// </summary>
        internal static bool ConfirmUnverifiedArchive(LocalUpdateProbe probe)
        {
            return EditorUtility.DisplayDialog(
                SkillsLocalization.Get("update_archive_unverified_title"),
                SkillsLocalization.Get("update_archive_unverified_body_fmt", probe?.PackageDir, probe?.MarkerRoot),
                SkillsLocalization.Get("update_archive_unverified_continue"),
                SkillsLocalization.Get("dialog_cancel"));
        }

        /// <summary>At most <see cref="LocalSelfUpdateService.MaxListedEntries"/> lines, then "...and N more" for the rest.</summary>
        internal static string FormatEntries(IReadOnlyList<string> entries, int count)
        {
            var shown = (entries ?? new List<string>()).Take(LocalSelfUpdateService.MaxListedEntries).ToList();
            var lines = shown.Select(entry => "  " + entry).ToList();
            var hidden = count - shown.Count;
            if (hidden > 0) lines.Add(SkillsLocalization.Get("update_git_more_fmt", hidden));
            return string.Join("\n", lines);
        }
    }
}

// Producer:Betsy
