using System;
using System.IO;

namespace UnitySkills.Internal
{
    /// <summary>
    /// Crash-safe writes for the package's state files under Library/UnitySkills (only the main editor writes them; see
    /// <see cref="ImportWorkerGate"/>). <see cref="Write"/> builds the next document in a sibling temp file and swaps it
    /// in with a rename, so a domain reload, an editor kill, or a reader in another process sees the previous or the
    /// next document, never a prefix of one.
    /// </summary>
    internal static class LibraryStateFile
    {
        internal const string TempSuffix = ".tmp";

        /// <summary>
        /// Replaces <paramref name="path"/> with what <paramref name="writeBody"/> writes, through "path.tmp" and one
        /// rename. With <paramref name="backupPath"/>, the replaced generation is kept there. CLR only, so callable from
        /// any thread; callers serialize the writers of one path. On an exception the previous document is still in place,
        /// or, when the Windows copy fallback failed mid-copy, in <paramref name="backupPath"/>.
        /// </summary>
        internal static void Write(string path, Action<Stream> writeBody, string backupPath = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A state file path is required.", nameof(path));
            if (writeBody == null) throw new ArgumentNullException(nameof(writeBody));

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string tmp = path + TempSuffix;
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    writeBody(stream);
                Swap(tmp, path, backupPath);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>UTF-8 without BOM through <see cref="Write"/>.</summary>
        internal static void WriteAllText(string path, string text, string backupPath = null)
        {
            byte[] bytes = SkillsCommon.Utf8NoBom.GetBytes(text ?? string.Empty);
            Write(path, stream => stream.Write(bytes, 0, bytes.Length), backupPath);
        }

        /// <summary>
        /// Appends already-terminated lines to a line-oriented file. When the file does not end with a newline (a kill
        /// cut an earlier append short), one is written first, so the torn line stays one bad row instead of swallowing
        /// the first record appended after it. CLR only; callers serialize the writers of one path.
        /// </summary>
        internal static void AppendLines(string path, byte[] terminatedLines)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A state file path is required.", nameof(path));
            if (terminatedLines == null || terminatedLines.Length == 0) return;

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // Shared like the journal's former append: readers and segment rotation are never blocked.
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    bool endsWithNewline = stream.ReadByte() == '\n';
                    stream.Seek(0, SeekOrigin.End);
                    if (!endsWithNewline) stream.WriteByte((byte)'\n');
                }
                stream.Write(terminatedLines, 0, terminatedLines.Length);
            }
        }

        private static void Swap(string tmp, string path, string backupPath)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, backupPath);
                    return;
                }
                catch (FileNotFoundException)
                {
                    // The destination vanished after the check; the move below still lands the document.
                }
                catch (IOException) when (File.Exists(tmp))
                {
                    if (!File.Exists(path))
                    {
                        // The swap moved the old generation to the backup name but not the new one in.
                        File.Move(tmp, path);
                        return;
                    }

                    // Windows refuses the swap while another process holds the destination open without
                    // FILE_SHARE_DELETE (an indexer or a virus scanner). Copying over it is not atomic, so the
                    // backup generation is written first and a torn copy stays recoverable from it.
                    if (!string.IsNullOrEmpty(backupPath)) File.Copy(path, backupPath, true);
                    File.Copy(tmp, path, true);
                    TryDelete(tmp);
                    return;
                }
            }
            File.Move(tmp, path);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // Best effort: the next write truncates a leftover temp file anyway.
            }
        }
    }
}

// Producer:Betsy
