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
    /// archive to a temp directory and indexes entry name → extracted path. That
    /// directory is content-addressed by <see cref="V12PakCache"/>: the same pak
    /// bytes always map to the same folder, so starting the game again reuses the
    /// previous extraction instead of writing a new copy every launch.
    ///
    /// The extraction stays on disk after <see cref="Dispose"/> because it is a
    /// cache shared by every launch (and possibly by other running processes).
    /// Use <see cref="V12PakCache.Prune"/> or <see cref="V12PakCache.Purge"/> to
    /// reclaim space.
    /// </summary>
    public sealed class V12PakReader : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private const string ManifestEntryName = "manifest.json";

        private readonly string _pakPath;
        private readonly Func<Stream> _openStream;
        private string _tempDir = "";
        private readonly Dictionary<string, string> _entryPaths = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The parsed manifest, or an empty manifest when the archive lacks one.</summary>
        public V12PakManifest Manifest { get; private set; } = new();

        /// <summary>
        /// Path of the opened <c>.v12pak</c> file, or the name it was opened
        /// under when it came from a stream (for example
        /// <c>res://MyGame.v12pak</c> on Android).
        /// </summary>
        public string PakPath => _pakPath;

        /// <summary>
        /// Content-addressed directory the archive was extracted into. Shared by
        /// every launch of the same pak and left in place on dispose.
        /// </summary>
        public string TempDirectory => _tempDir;

        /// <summary>True when this reader reused an extraction a previous run had already written.</summary>
        public bool ReusedCache { get; private set; }

        /// <summary>All entry names in the archive (forward-slash paths).</summary>
        public IReadOnlyCollection<string> EntryNames => _entryPaths.Keys;

        /// <summary>
        /// Reads only the manifest, streaming entries until it is found. The
        /// builder writes <c>manifest.json</c> first, so this normally stops
        /// after one entry and never extracts the archive. Use it to inspect a
        /// pak before deciding whether to load it.
        /// </summary>
        public static V12PakManifest? ReadManifestOnly(string pakPath)
            => ReadManifestOnly(() => File.OpenRead(pakPath));

        /// <summary>
        /// Reads only the manifest from a stream-backed pak (an archive embedded
        /// in <c>res://</c> or retrieved some other way). The stream factory is
        /// called once and never extracts the archive.
        /// </summary>
        public static V12PakManifest? ReadManifestOnly(Func<Stream> openStream)
        {
            try
            {
                using var file = openStream();
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);

                while (tar.GetNextEntry() is { } entry)
                {
                    if (!string.Equals(entry.Name, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry.DataStream is null)
                        return null;

                    using var reader = new StreamReader(entry.DataStream, Encoding.UTF8);
                    return JsonSerializer.Deserialize<V12PakManifest>(reader.ReadToEnd(), JsonOpts);
                }
            }
            catch
            {
                // A damaged or foreign archive: callers fall back to defaults.
            }

            return null;
        }

        public V12PakReader(string pakPath)
        {
            if (!File.Exists(pakPath))
                throw new FileNotFoundException($"v12pak not found: {pakPath}", pakPath);

            _pakPath = Path.GetFullPath(pakPath);
            _openStream = () => File.OpenRead(_pakPath);
            Initialize(V12PakCache.GetDirectory(_pakPath, out bool reused), reused);
        }

        /// <summary>
        /// Opens a pak that is not a plain file. The archive is read through
        /// <paramref name="openStream"/> and extracted exactly like a file pak;
        /// <paramref name="pakName"/> is the display name and the cache stem
        /// (for example <c>res://MyGame.v12pak</c>). The factory is called once
        /// for the cache key and once for extraction, so it must open a fresh
        /// seekable stream each time.
        /// </summary>
        public V12PakReader(string pakName, Func<Stream> openStream)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pakName);
            ArgumentNullException.ThrowIfNull(openStream);

            _pakPath = pakName;
            _openStream = openStream;
            Initialize(V12PakCache.GetDirectory(pakName, openStream, out bool reused), reused);
        }

        private void Initialize(string tempDirectory, bool reused)
        {
            _tempDir = tempDirectory;
            ReusedCache = reused;

            string? manifestJson = null;

            if (reused)
            {
                IndexExtractedDirectory();
                manifestJson = ReadManifestText();
            }

            if (manifestJson is null)
            {
                ExtractArchive();
                IndexExtractedDirectory();
                manifestJson = ReadManifestText();
            }

            if (manifestJson is null)
                throw new InvalidDataException($"No manifest.json found in v12pak: {_pakPath}");

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

        /// <summary>
        /// Keeps the content-addressed extraction so the next launch can reuse it.
        /// Nothing is deleted here: see <see cref="V12PakCache"/> for pruning.
        /// </summary>
        public void Dispose()
        {
        }

        // ── extraction ──────────────────────────────────────────────────

        /// <summary>
        /// Extracts into a staging directory and commits it under the
        /// content-hashed name. A crash mid-extract therefore leaves only a
        /// <c>.tmp-*</c> sibling, never a half-valid cache entry.
        /// </summary>
        private void ExtractArchive()
        {
            string staging = V12PakCache.CreateStagingDirectory(_tempDir);

            try
            {
                ExtractInto(staging);

                string? manifestPath = FindFileIgnoreCase(staging, ManifestEntryName);
                if (manifestPath is null)
                    throw new InvalidDataException($"No manifest.json found in v12pak: {_pakPath}");

                File.WriteAllText(
                    Path.Combine(staging, V12PakCache.MarkerFileName),
                    $"{_pakPath}{Environment.NewLine}{DateTime.UtcNow:O}{Environment.NewLine}");

                // Another process may have finished the same content while we
                // extracted; either way the directory now holds a valid copy.
                V12PakCache.Commit(staging, _tempDir);
            }
            catch
            {
                V12PakCache.Discard(staging);
                throw;
            }
        }

        private void ExtractInto(string stagingDirectory)
        {
            try
            {
                using var fileStream = _openStream();
                using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                using var tarReader = new TarReader(gzipStream);

                TarEntry? entry;
                while ((entry = tarReader.GetNextEntry()) != null)
                {
                    if (entry.EntryType is TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes) continue;
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    // PAX/other metadata entries carry no payload; older archives
                    // also contain them, so skip them instead of dereferencing a
                    // null DataStream.
                    if (entry.DataStream == null) continue;

                    string? safeName = SanitizeEntryName(entry.Name);
                    if (safeName == null)
                    {
                        Console.WriteLine($"[V12Pak] Skipping unsafe entry name '{entry.Name}' in '{_pakPath}'");
                        continue;
                    }

                    string dest = Path.Combine(stagingDirectory, safeName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using (var source = entry.DataStream)
                    using (var target = File.Create(dest))
                        source.CopyTo(target);
                }
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"'{_pakPath}' is not a valid gzip-compressed tar (v12pak) file: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Rebuilds the entry index from the extraction directory. Used on cache
        /// hits, where the entries are already on disk and no archive pass is
        /// needed. Keys are forward-slash relative paths, matching archive names.
        /// </summary>
        private void IndexExtractedDirectory()
        {
            _entryPaths.Clear();

            foreach (string file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(_tempDir, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');

                // Cache bookkeeping is not archive content.
                if (string.Equals(relative, V12PakCache.MarkerFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                _entryPaths[relative] = file;
            }
        }

        private string? ReadManifestText()
        {
            string? path = ResolveEntryPath(ManifestEntryName)
                           ?? FindFileIgnoreCase(_tempDir, ManifestEntryName);
            return path == null ? null : File.ReadAllText(path);
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
