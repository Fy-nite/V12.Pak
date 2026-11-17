using System.Security.Cryptography;
using System.Text;

namespace V12.Pak
{
    /// <summary>
    /// Content-addressed cache for extracted <c>.v12pak</c> archives.
    ///
    /// A pak materializes to <c>{root}/{pakStem}_{sha256(content)}</c>. Because the
    /// directory name is derived from the archive's bytes, launching the same pak
    /// again reuses the exact directory the previous run wrote, instead of
    /// minting a fresh GUID-named copy (and a fresh several-hundred-MB extraction)
    /// on every start. Rebuilding the pak with different content produces a new
    /// directory; an unchanged rebuild reuses the old one.
    ///
    /// Extraction is staged in a sibling <c>.tmp-*</c> directory and moved into
    /// place once <c>manifest.json</c> has been written, so a crash mid-extract
    /// leaves no half-populated directory under the real name and two processes
    /// racing to open the same pak converge on one directory.
    ///
    /// Directories are never deleted by <see cref="V12PakReader.Dispose"/>: the
    /// cache is shared between runs (and possibly between live processes), so
    /// removal has to be deliberate. Old extractions of a pak are pruned after
    /// <see cref="MaxIdleAge"/> of no use, and <see cref="Purge"/> removes
    /// everything on demand.
    /// </summary>
    public static class V12PakCache
    {
        /// <summary>
        /// Marker written once an extraction finishes cleanly. Its presence (next
        /// to <c>manifest.json</c>) is what makes a directory reusable.
        /// </summary>
        public const string MarkerFileName = ".v12pak-complete";

        private const string FingerprintDirectoryName = ".fingerprints";
        private const string StagingInfix = ".tmp-";
        private const string RootEnvironmentVariable = "V12_PAK_CACHE";
        private const int HashCharacters = 32;
        private const int SampleBytes = 64 * 1024;
        private const string ArchiveManifestName = "manifest.json";

        private static readonly object Gate = new();
        private static readonly Dictionary<string, string> HashMemo = new(StringComparer.OrdinalIgnoreCase);

        private static string? _root;

        /// <summary>
        /// Root directory for extracted paks. Defaults to a per-user folder under
        /// the OS temp directory, or to <c>V12_PAK_CACHE</c> when that environment
        /// variable is set (useful for CI, and for hosts whose <c>/tmp</c> is
        /// mounted <c>noexec</c>). Assign before opening a pak to override.
        /// </summary>
        public static string RootDirectory
        {
            get
            {
                if (_root is not null) return _root;
                lock (Gate) return _root ??= ResolveRoot();
            }
            set => _root = value;
        }

        /// <summary>
        /// How long an unused extraction is kept before pruning. Default: 7 days.
        /// Set to <see cref="TimeSpan.Zero"/> (or negative) to disable pruning.
        /// The directory and its marker are touched on every reuse, so a pak that
        /// is actually played is never pruned.
        /// </summary>
        public static TimeSpan MaxIdleAge { get; set; } = TimeSpan.FromDays(7);

        /// <summary>
        /// Directory for <paramref name="pakPath"/>, creating the cache root when
        /// needed. <paramref name="reused"/> is true when a previous run's
        /// extraction is already complete and can be opened without writing.
        /// </summary>
        public static string GetDirectory(string pakPath, out bool reused)
        {
            string fullPath = Path.GetFullPath(pakPath);
            string stem = Path.GetFileNameWithoutExtension(fullPath);

            // Hash first: the directory name is the content, so a missing or
            // unreadable pak fails here rather than half-populating a cache.
            string hash = HashFile(fullPath);

            string root = RootDirectory;
            Directory.CreateDirectory(root);
            RestrictPermissions(root);

            string directory = Path.Combine(root, stem + "_" + hash);
            reused = IsComplete(directory);
            if (reused)
                Touch(directory);

            PruneStaging(root);
            Prune(stem);
            return directory;
        }

        /// <summary>True when a directory holds a finished extraction.</summary>
        public static bool IsComplete(string directory)
            => File.Exists(Path.Combine(directory, MarkerFileName))
               && FindFileIgnoreCase(directory, ArchiveManifestName) != null;

        /// <summary>
        /// Creates the sibling staging directory an extraction writes into. It is
        /// deliberately outside <paramref name="directory"/> so a partially
        /// written archive never looks like a valid cache entry.
        /// </summary>
        public static string CreateStagingDirectory(string directory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            string staging = directory + StagingInfix + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            return staging;
        }

        /// <summary>
        /// Moves a finished staging directory into place. Returns false when
        /// another process committed the same content first; the caller should
        /// then index the existing directory instead. On failure the staging
        /// directory is removed and the exception rethrown.
        /// </summary>
        public static bool Commit(string stagingDirectory, string directory)
        {
            try
            {
                if (IsComplete(directory))
                {
                    Discard(stagingDirectory);
                    return false;
                }

                if (Directory.Exists(directory))
                    TryDelete(directory);

                Directory.Move(stagingDirectory, directory);
                return true;
            }
            catch (IOException)
            {
                // Lost a race: the other process's extraction is the winner.
                if (IsComplete(directory))
                {
                    Discard(stagingDirectory);
                    return false;
                }

                Discard(stagingDirectory);
                throw;
            }
            catch
            {
                Discard(stagingDirectory);
                throw;
            }
        }

        /// <summary>Best-effort removal of a staging directory.</summary>
        public static void Discard(string directory) => TryDelete(directory);

        /// <summary>
        /// Removes extractions older than <see cref="MaxIdleAge"/>. Pass a pak
        /// stem (file name without extension) to limit the sweep to one game.
        /// Returns the number of directories removed.
        /// </summary>
        public static int Prune(string? pakStem = null)
        {
            if (MaxIdleAge <= TimeSpan.Zero) return 0;
            return PruneCore(RootDirectory, DateTime.UtcNow - MaxIdleAge, pakStem);
        }

        /// <summary>
        /// Removes cached extractions regardless of age. Intended for hosts that
        /// want to reclaim space (an installer/uninstaller, a settings toggle).
        /// </summary>
        public static int Purge(string? pakStem = null)
            => PruneCore(RootDirectory, DateTime.MaxValue, pakStem);

        /// <summary>
        /// SHA-256 (first 128 bits, lowercase hex) of a pak's bytes. Files are
        /// hashed once per process, and the digest is remembered on disk keyed by
        /// a cheap head/tail fingerprint so relaunching a large pak does not
        /// re-read the whole archive just to find its directory.
        /// </summary>
        public static string HashFile(string pakPath)
        {
            string fullPath = Path.GetFullPath(pakPath);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
                throw new FileNotFoundException($"v12pak not found: {fullPath}", fullPath);

            string fingerprint = BuildFingerprint(fullPath, info);

            lock (Gate)
            {
                if (HashMemo.TryGetValue(fullPath, out string? memo)
                    && memo.StartsWith(fingerprint + "|", StringComparison.Ordinal))
                {
                    return memo[(fingerprint.Length + 1)..];
                }
            }

            string? remembered = ReadFingerprint(fullPath, fingerprint);
            string hash = remembered ?? ComputeFullHash(fullPath);

            lock (Gate) HashMemo[fullPath] = fingerprint + "|" + hash;
            if (remembered is null)
                WriteFingerprint(fullPath, fingerprint, hash);

            return hash;
        }

        // ── pruning ─────────────────────────────────────────────────────

        private static int PruneCore(string root, DateTime cutoff, string? pakStem)
        {
            if (!Directory.Exists(root)) return 0;

            int removed = 0;
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(directory);
                if (string.Equals(name, FingerprintDirectoryName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (pakStem is not null && !HasStem(name, pakStem))
                    continue;
                if (LastActivity(directory) > cutoff)
                    continue;
                if (TryDelete(directory))
                    removed++;
            }
            return removed;
        }

        /// <summary>
        /// Staging directories are ours and never shared, so anything left behind
        /// by a crashed process can go after an hour even if the pak itself is
        /// still in active use.
        /// </summary>
        private static void PruneStaging(string root)
        {
            if (!Directory.Exists(root)) return;

            DateTime cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (string directory in Directory.EnumerateDirectories(root, "*" + StagingInfix + "*"))
            {
                if (LastActivity(directory) > cutoff) continue;
                TryDelete(directory);
            }
        }

        private static DateTime LastActivity(string directory)
        {
            DateTime newest = Directory.GetLastWriteTimeUtc(directory);

            string marker = Path.Combine(directory, MarkerFileName);
            if (File.Exists(marker))
            {
                DateTime stamp = File.GetLastWriteTimeUtc(marker);
                if (stamp > newest) newest = stamp;
            }

            return newest;
        }

        private static bool HasStem(string directoryName, string stem)
            => directoryName.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase);

        private static void Touch(string directory)
        {
            try
            {
                string marker = Path.Combine(directory, MarkerFileName);
                if (File.Exists(marker))
                    File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
            }
            catch
            {
                // Best effort: a read-only extraction directory still works.
            }
        }

        private static bool TryDelete(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return false;
                Directory.Delete(directory, true);
                return true;
            }
            catch
            {
                // In use by this or another process (mapped assemblies, an open
                // stream): leave it for the next sweep.
                return false;
            }
        }

        // ── paths ───────────────────────────────────────────────────────

        private static string ResolveRoot()
        {
            string? configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured.Trim());

            // The temp root is shared on Unix and the cache holds loadable
            // assemblies, so give every user their own folder rather than letting
            // one account pre-seed another's cache.
            return Path.Combine(Path.GetTempPath(), "V12Paks", SafeSegment(Environment.UserName));
        }

        private static string SafeSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "user";

            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
                builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
            return builder.ToString();
        }

        private static void RestrictPermissions(string directory)
        {
            if (OperatingSystem.IsWindows()) return;

            try
            {
                File.SetUnixFileMode(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Best effort: an unusual filesystem (FAT, some network mounts)
                // does not need to break pak loading.
            }
        }

        private static string? FindFileIgnoreCase(string directory, string fileName)
        {
            if (!Directory.Exists(directory)) return null;

            string exact = Path.Combine(directory, fileName);
            if (File.Exists(exact)) return exact;

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                    return file;
            }

            return null;
        }

        // ── hashing ─────────────────────────────────────────────────────

        private static string BuildFingerprint(string path, FileInfo info)
        {
            byte[] sample = SampleFile(path, info.Length);
            string digest = Convert.ToHexString(SHA256.HashData(sample)).ToLowerInvariant();
            return $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{digest}";
        }

        /// <summary>
        /// Length header plus the first and last 64 KiB. Cheap enough to run on a
        /// multi-gigabyte pak, and sensitive to anything but a same-length edit
        /// that touches neither end.
        /// </summary>
        private static byte[] SampleFile(string path, long length)
        {
            int headLength = (int)Math.Min(length, SampleBytes);
            long tailStart = Math.Max(headLength, length - SampleBytes);
            int tailLength = (int)(length - tailStart);

            var sample = new byte[8 + headLength + tailLength];
            BitConverter.TryWriteBytes(sample.AsSpan(0, 8), length);

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);

            if (headLength > 0)
                stream.ReadExactly(sample.AsSpan(8, headLength));

            if (tailLength > 0)
            {
                stream.Seek(tailStart, SeekOrigin.Begin);
                stream.ReadExactly(sample.AsSpan(8 + headLength, tailLength));
            }

            return sample;
        }

        private static string ComputeFullHash(string path)
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

            byte[] digest = SHA256.HashData(stream);
            return Convert.ToHexString(digest).ToLowerInvariant()[..HashCharacters];
        }

        private static string FingerprintPath(string fullPath)
        {
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))
                .ToLowerInvariant()[..HashCharacters];
            return Path.Combine(RootDirectory, FingerprintDirectoryName, key + ".fp");
        }

        private static string? ReadFingerprint(string fullPath, string fingerprint)
        {
            try
            {
                string path = FingerprintPath(fullPath);
                if (!File.Exists(path)) return null;

                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 2) return null;
                if (!string.Equals(lines[0], fingerprint, StringComparison.Ordinal)) return null;
                if (lines.Length > 2 && !string.Equals(lines[2], fullPath, StringComparison.Ordinal)) return null;

                return lines[1];
            }
            catch
            {
                return null;
            }
        }

        private static void WriteFingerprint(string fullPath, string fingerprint, string hash)
        {
            try
            {
                string path = FingerprintPath(fullPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path, new[] { fingerprint, hash, fullPath });
            }
            catch
            {
                // Best effort: the next launch re-hashes the pak.
            }
        }
    }
}
