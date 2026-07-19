using System;
using System.Collections.Generic;
using System.IO;

namespace V12.Pak
{
    /// <summary>
    /// Fluent builder for constructing a <see cref="V12PakManifest"/> manually.
    /// Use this when you want full control over the manifest contents (e.g. from a studio editor)
    /// rather than auto-detecting from a directory structure.
    /// </summary>
    /// <example>
    /// var manifest = V12PakManifestBuilder.Create()
    ///     .WithDescription("My game pak")
    ///     .WithAuthor("Me")
    ///     .AddWorld("Forest", "worlds/forest/world.xml")
    ///     .AddAsset("textures/crate.png", "assets/textures/crate.png")
    ///     .AddPak("paks/MyGameplay.dll")
    ///     .Build();
    /// </example>
    public class V12PakManifestBuilder
    {
        private int _version = 1;
        private string _engine = "v12";
        private string? _description;
        private string? _author;
        private readonly List<WorldEntry> _worlds = new();
        private readonly Dictionary<string, string> _assets = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _paks = new();

        private V12PakManifestBuilder() { }

        /// <summary>Create a new empty manifest builder.</summary>
        public static V12PakManifestBuilder Create() => new();

        /// <summary>Create a builder pre-populated from an existing manifest (for editing).</summary>
        public static V12PakManifestBuilder FromManifest(V12PakManifest manifest)
        {
            var b = new V12PakManifestBuilder();
            b._version = manifest.Version;
            b._engine = manifest.Engine;
            b._description = manifest.Description;
            b._author = manifest.Author;
            b._worlds.AddRange(manifest.Worlds);
            foreach (var kv in manifest.Assets)
                b._assets[kv.Key] = kv.Value;
            b._paks.AddRange(manifest.Paks);
            return b;
        }

        // ── Metadata ──

        /// <summary>Set the manifest format version (default: 1).</summary>
        public V12PakManifestBuilder WithVersion(int version)
        {
            _version = version;
            return this;
        }

        /// <summary>Set the engine identifier (default: "v12").</summary>
        public V12PakManifestBuilder WithEngine(string engine)
        {
            _engine = engine;
            return this;
        }

        /// <summary>Set the pak description.</summary>
        public V12PakManifestBuilder WithDescription(string? description)
        {
            _description = description;
            return this;
        }

        /// <summary>Set the pak author.</summary>
        public V12PakManifestBuilder WithAuthor(string? author)
        {
            _author = author;
            return this;
        }

        // ── Worlds ──

        /// <summary>
        /// Add a world entry.
        /// </summary>
        /// <param name="name">Human-readable world name. Used as the v12:// mount point.</param>
        /// <param name="entry">Path inside the tar archive to world.xml (e.g. "worlds/forest/world.xml").</param>
        /// <param name="templatesDir">Optional path to the templates directory inside the archive.</param>
        public V12PakManifestBuilder AddWorld(string name, string entry, string? templatesDir = null)
        {
            _worlds.Add(new WorldEntry
            {
                Name = name,
                Entry = NormalizePath(entry),
                TemplatesDir = templatesDir != null ? NormalizePath(templatesDir) : null
            });
            return this;
        }

        /// <summary>Remove a world by name.</summary>
        public V12PakManifestBuilder RemoveWorld(string name)
        {
            _worlds.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
            return this;
        }

        /// <summary>Get the list of worlds (for inspection/modification before Build).</summary>
        public List<WorldEntry> Worlds => _worlds;

        // ── Assets ──

        /// <summary>
        /// Add an asset mapping.
        /// </summary>
        /// <param name="assetKey">Virtual path resolvable via v12:// (e.g. "textures/crate.png").</param>
        /// <param name="archivePath">Path inside the tar archive (e.g. "assets/textures/crate.png").</param>
        public V12PakManifestBuilder AddAsset(string assetKey, string archivePath)
        {
            _assets[assetKey] = NormalizePath(archivePath);
            return this;
        }

        /// <summary>
        /// Remove an asset by key.
        /// </summary>
        public V12PakManifestBuilder RemoveAsset(string assetKey)
        {
            _assets.Remove(assetKey);
            return this;
        }

        /// <summary>
        /// Bulk-add all files from a disk directory as assets.
        /// Each file gets an assetKey based on its relative path within the directory,
        /// and an archivePath under the given archive root.
        /// </summary>
        /// <param name="assetType">Asset category (e.g. "textures", "meshes", "audio"). Used as prefix in archive paths.</param>
        /// <param name="diskDir">Physical directory to scan.</param>
        /// <param name="archiveRoot">Root path inside the archive (default: "assets/{assetType}").</param>
        public V12PakManifestBuilder AddAssetsFromDirectory(string assetType, string diskDir, string? archiveRoot = null)
        {
            archiveRoot ??= $"assets/{assetType}";
            string normalizedRoot = Path.GetFullPath(diskDir);

            foreach (string filePath in Directory.EnumerateFiles(diskDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = filePath.Substring(normalizedRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');

                string assetKey = $"{assetType}/{relativePath}";
                string archivePath = $"{archiveRoot}/{relativePath}";
                _assets[assetKey] = NormalizePath(archivePath);
            }

            return this;
        }

        /// <summary>Get the asset dictionary (for inspection/modification before Build).</summary>
        public Dictionary<string, string> Assets => _assets;

        // ── Pak DLLs ──

        /// <summary>
        /// Add a DLL gamepack reference by its archive path.
        /// </summary>
        /// <param name="archivePath">Path inside the tar archive (e.g. "paks/MyGameplay.dll").</param>
        public V12PakManifestBuilder AddPak(string archivePath)
        {
            _paks.Add(NormalizePath(archivePath));
            return this;
        }

        /// <summary>Remove a pak DLL by archive path.</summary>
        public V12PakManifestBuilder RemovePak(string archivePath)
        {
            _paks.RemoveAll(p => string.Equals(p, archivePath, StringComparison.OrdinalIgnoreCase));
            return this;
        }

        /// <summary>Get the pak list (for inspection/modification before Build).</summary>
        public List<string> Paks => _paks;

        // ── Build ──

        /// <summary>
        /// Build and return the final <see cref="V12PakManifest"/>.
        /// The builder can be reused after this call.
        /// </summary>
        public V12PakManifest Build()
        {
            var manifest = new V12PakManifest
            {
                Version = _version,
                Engine = _engine,
                Description = _description,
                Author = _author,
                Worlds = new List<WorldEntry>(_worlds),
                Assets = new Dictionary<string, string>(_assets, StringComparer.OrdinalIgnoreCase),
                Paks = new List<string>(_paks)
            };
            return manifest;
        }

        /// <summary>
        /// Build the manifest and immediately write it to a tar.gz pak
        /// with the given file entries. This is a shortcut for Build + V12PakWriter.Finalize.
        /// </summary>
        /// <param name="outputPath">Output .v12pak file path.</param>
        /// <param name="entries">
        /// Archive path → file data pairs to include in the pak.
        /// The manifest.json is written automatically.
        /// </param>
        public void BuildAndWrite(string outputPath, IEnumerable<(string ArchivePath, byte[] Data)> entries)
        {
            var manifest = Build();
            using var writer = new V12PakWriter(outputPath);
            foreach (var (archivePath, data) in entries)
                writer.AddFile(archivePath, data);
            writer.Finalize(manifest);
        }

        /// <summary>
        /// Build the manifest and write it to a pak, adding all files from a source directory.
        /// </summary>
        /// <param name="outputPath">Output .v12pak file path.</param>
        /// <param name="sourceDir">Directory whose files are packed into the archive.</param>
        /// <param name="archiveRoot">Optional root prefix for all files (default: empty = files go to archive root).</param>
        public void BuildAndWriteFromDirectory(string outputPath, string sourceDir, string? archiveRoot = null)
        {
            var manifest = Build();
            using var writer = new V12PakWriter(outputPath);

            if (archiveRoot != null)
                writer.AddDirectory(archiveRoot, sourceDir);
            else
                writer.AddDirectory("", sourceDir);

            writer.Finalize(manifest);
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }
    }
}
