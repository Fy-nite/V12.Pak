using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace V12.Pak
{
    /// <summary>
    /// Opens a <c>.v12pak</c> tar.gz archive, reads its manifest, and provides
    /// access to its entries.
    ///
    /// Gzip-compressed tar is not seekable, so tar entry data streams cannot be
    /// re-read after the reader advances. The reader therefore materializes the
    /// archive to a temp directory on open and indexes entry name → extracted
    /// path. This mirrors the extraction behaviour of the legacy
    /// <see cref="Core.WorldLoader.LoadFromArchive"/>, and gives the engine's
    /// path-based APIs (script components, texture loaders) real files to read.
    /// Call <see cref="Dispose"/> to remove the temp directory.
    /// </summary>
    public sealed class V12PakReader : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly string _pakPath;
        private readonly string _tempDir;
        private readonly Dictionary<string, string> _entryPaths = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The parsed manifest, or an empty manifest when the archive lacks one.</summary>
        public V12PakManifest Manifest { get; }

        /// <summary>Path of the opened .v12pak file.</summary>
        public string PakPath => _pakPath;

        /// <summary>Directory the archive was extracted into. Removed on <see cref="Dispose"/>.</summary>
        public string TempDirectory => _tempDir;

        /// <summary>All entry names in the archive (forward-slash paths).</summary>
        public IReadOnlyCollection<string> EntryNames => _entryPaths.Keys;

        public V12PakReader(string pakPath)
        {
            if (!File.Exists(pakPath))
                throw new FileNotFoundException($"v12pak not found: {pakPath}", pakPath);

            _pakPath = pakPath;
            string pakName = Path.GetFileNameWithoutExtension(pakPath);
            _tempDir = Path.Combine(Path.GetTempPath(), "V12Paks", pakName + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            string? manifestJson = null;
            try
            {
                using var fileStream = File.OpenRead(pakPath);
                using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                using var tarReader = new TarReader(gzipStream);

                TarEntry? entry;
                while ((entry = tarReader.GetNextEntry()) != null)
                {
                    if (entry.EntryType is TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes) continue;
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string? safeName = SanitizeEntryName(entry.Name);
                    if (safeName == null)
                    {
                        Console.WriteLine($"[V12Pak] Skipping unsafe entry name '{entry.Name}' in '{pakPath}'");
                        continue;
                    }

                    string dest = Path.Combine(_tempDir, safeName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using (var source = entry.DataStream)
                    using (var target = File.Create(dest))
                        source.CopyTo(target);

                    _entryPaths[entry.Name] = dest;
                    if (string.Equals(entry.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                        manifestJson = File.ReadAllText(dest);
                }
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"'{pakPath}' is not a valid gzip-compressed tar (v12pak) file: {ex.Message}", ex);
            }

            if (manifestJson == null)
                throw new InvalidDataException($"No manifest.json found in v12pak: {pakPath}");

            Manifest = JsonSerializer.Deserialize<V12PakManifest>(manifestJson, JsonOpts) ?? new V12PakManifest();
        }

        public bool HasEntry(string entryPath) => _entryPaths.ContainsKey(entryPath);

        /// <summary>
        /// Open an entry for reading. The returned stream must be disposed by the caller.
        /// Returns null when the entry does not exist.
        /// </summary>
        public Stream? OpenEntry(string entryPath)
        {
            if (_entryPaths.TryGetValue(entryPath, out var path))
                return File.OpenRead(path);
            return null;
        }

        /// <summary>Physical path of the extracted entry, or null when the entry does not exist.</summary>
        public string? ResolveEntryPath(string entryPath)
            => _entryPaths.TryGetValue(entryPath, out var path) ? path : null;

        /// <summary>Read an entry's full text (UTF-8). Returns null when the entry does not exist.</summary>
        public string? ReadEntryText(string entryPath)
        {
            var path = ResolveEntryPath(entryPath);
            return path == null ? null : File.ReadAllText(path);
        }

        /// <summary>Remove the extracted temp directory. Best effort.</summary>
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, true);
            }
            catch
            {
                // best effort
            }
        }

        /// <summary>
        /// Normalize an entry name into a safe relative path. Rejects path
        /// traversal ("..") and rooted paths. Returns null for unsafe names.
        /// </summary>
        private static string? SanitizeEntryName(string name)
        {
            string normalized = name.Replace('\\', '/').TrimStart('/');
            if (normalized.Length == 0) return null;

            var clean = new List<string>();
            foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue;
                if (segment == "..") return null;
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                clean.Add(segment);
            }
            if (clean.Count == 0) return null;
            return string.Join(Path.DirectorySeparatorChar, clean);
        }
    }
}
