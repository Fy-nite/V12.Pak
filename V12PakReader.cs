using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace V12.Pak
{
    /// <summary>
    /// Opens a .v12pak tar.gz archive, reads the manifest, and provides random access
    /// to entries by extracting to a temp directory on construction.
    /// </summary>
    public class V12PakReader : IDisposable
    {
        private readonly string _tempDir;
        private readonly Dictionary<string, string> _entryPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        /// <summary>The parsed manifest from the pak's manifest.json.</summary>
        public V12PakManifest Manifest { get; }

        /// <summary>The original pak file path.</summary>
        public string PakPath { get; }

        /// <summary>Temp directory where entries are extracted. Cleaned up on Dispose.</summary>
        public string TempDirectory => _tempDir;

        /// <summary>
        /// Opens a .v12pak tar.gz archive, extracts all entries to a temp directory,
        /// and reads the manifest.
        /// </summary>
        public V12PakReader(string pakPath)
        {
            if (!File.Exists(pakPath))
                throw new FileNotFoundException($"Pak file not found: {pakPath}");

            PakPath = pakPath;
            _tempDir = Path.Combine(
                Path.GetTempPath(), "V12Pak",
                Path.GetFileNameWithoutExtension(pakPath) + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            ExtractToTempDir(pakPath, _tempDir);
            BuildIndex(_tempDir);

            string manifestPath = Path.Combine(_tempDir, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new InvalidOperationException($"Pak file does not contain manifest.json: {pakPath}");

            byte[] manifestJson = File.ReadAllBytes(manifestPath);
            Manifest = V12PakManifest.FromJson(manifestJson);
        }

        /// <summary>
        /// Opens a stream to the entry at the given archive path (e.g. "assets/textures/crate.png").
        /// Returns null if the entry does not exist.
        /// </summary>
        public Stream? OpenEntry(string archivePath)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(V12PakReader));

            if (_entryPaths.TryGetValue(archivePath, out string? fullPath) && File.Exists(fullPath))
                return File.OpenRead(fullPath);

            return null;
        }

        /// <summary>
        /// Returns true if an entry with the given archive path exists in the pak.
        /// </summary>
        public bool HasEntry(string archivePath)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(V12PakReader));
            return _entryPaths.ContainsKey(archivePath);
        }

        /// <summary>
        /// Returns the full disk path of an extracted entry, or null if not found.
        /// Useful for passing to APIs that need a file path (e.g. Assimp mesh loading).
        /// </summary>
        public string? GetEntryPath(string archivePath)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(V12PakReader));

            if (_entryPaths.TryGetValue(archivePath, out string? fullPath) && File.Exists(fullPath))
                return fullPath;

            return null;
        }

        /// <summary>
        /// Returns all entry archive paths in the pak.
        /// </summary>
        public IEnumerable<string> GetAllEntries()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(V12PakReader));
            return _entryPaths.Keys;
        }

        private static void ExtractToTempDir(string pakPath, string tempDir)
        {
            using var fileStream = File.OpenRead(pakPath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var tarReader = new TarReader(gzipStream);

            TarEntry? entry;
            while ((entry = tarReader.GetNextEntry(copyData: false)) != null)
            {
                if (entry.Name == null) continue;

                // Normalize the entry name
                string entryName = entry.Name.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrEmpty(entryName)) continue;

                string destPath = Path.Combine(tempDir, entryName);

                // Security: prevent path traversal
                string normalizedDest = Path.GetFullPath(destPath);
                string normalizedBase = Path.GetFullPath(tempDir);
                if (!normalizedDest.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                // Ensure parent directory exists
                string? parentDir = Path.GetDirectoryName(destPath);
                if (parentDir != null)
                    Directory.CreateDirectory(parentDir);

                // Extract file
                if (entry.DataStream != null)
                {
                    using var fileOut = File.Create(destPath);
                    entry.DataStream.CopyTo(fileOut);
                }
            }
        }

        private void BuildIndex(string tempDir)
        {
            string normalizedBase = Path.GetFullPath(tempDir);

            foreach (string filePath in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
            {
                string normalizedPath = Path.GetFullPath(filePath);
                if (!normalizedPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Get the relative path from the temp dir, using forward slashes
                string relativePath = filePath.Substring(normalizedBase.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');

                _entryPaths[relativePath] = filePath;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
