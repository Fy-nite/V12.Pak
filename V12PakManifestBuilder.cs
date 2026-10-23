using System;
using System.Collections.Generic;
using System.IO;

namespace V12.Pak
{
    /// <summary>
    /// Fluent builder for constructing a <see cref="V12PakManifest"/> manually.
    /// Use this when you want full control over the manifest contents (e.g. from
    /// a studio editor or a build script) rather than auto-detecting from a
    /// directory structure.
    /// </summary>
    /// <example>
    /// var manifest = V12PakManifestBuilder.Create()
    ///     .AddWorld("Forest", "worlds/forest/world.xml")
    ///     .AddAsset("textures/crate.png", "assets/textures/crate.png")
    ///     .AddPak("paks/MyGameplay.dll")
    ///     .Build();
    /// </example>
    public sealed class V12PakManifestBuilder
    {
        private int _version = 1;
        private string _engine = "v12";
        private readonly List<V12PakWorldEntry> _worlds = new();
        private readonly Dictionary<string, string> _assets = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _paks = new();
        private readonly List<string> _scripts = new();
        private readonly List<string> _csharpScripts = new();
        private readonly List<string> _dependencies = new();

        private V12PakManifestBuilder() { }

        /// <summary>Create a new empty manifest builder.</summary>
        public static V12PakManifestBuilder Create() => new();

        /// <summary>Create a builder pre-populated from an existing manifest (for editing).</summary>
        public static V12PakManifestBuilder FromManifest(V12PakManifest manifest)
        {
            var b = new V12PakManifestBuilder();
            b._version = manifest.Version;
            b._engine = manifest.Engine;
            b._worlds.AddRange(manifest.Worlds);
            foreach (var kv in manifest.Assets)
                b._assets[kv.Key] = kv.Value;
            b._paks.AddRange(manifest.Paks);
            b._scripts.AddRange(manifest.Scripts);
            b._csharpScripts.AddRange(manifest.CSharpScripts);
            b._dependencies.AddRange(manifest.Dependencies);
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

        // ── Worlds ──

        /// <summary>Add a world entry.</summary>
        /// <param name="name">Human-readable world name. Used as the v12:// mount point.</param>
        /// <param name="entry">Path inside the tar archive to world.xml (e.g. "worlds/forest/world.xml").</param>
        /// <param name="templatesDir">Optional path to the templates directory inside the archive.</param>
        public V12PakManifestBuilder AddWorld(string name, string entry, string? templatesDir = null)
        {
            _worlds.Add(new V12PakWorldEntry
            {
                Name = name,
                Entry = NormalizePath(entry),
                TemplatesDir = templatesDir != null ? NormalizePath(templatesDir) : ""
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
        public List<V12PakWorldEntry> Worlds => _worlds;

        // ── Assets ──

        /// <summary>Add an asset mapping.</summary>
        /// <param name="assetKey">Virtual path resolvable via v12:// (e.g. "textures/crate.png").</param>
        /// <param name="archivePath">Path inside the tar archive (e.g. "assets/textures/crate.png").</param>
        public V12PakManifestBuilder AddAsset(string assetKey, string archivePath)
        {
            _assets[assetKey] = NormalizePath(archivePath);
            return this;
        }

        /// <summary>Remove an asset by key.</summary>
        public V12PakManifestBuilder RemoveAsset(string assetKey)
        {
            _assets.Remove(assetKey);
            return this;
        }

        /// <summary>
        /// Bulk-add all files from a disk directory as assets. Each file gets an
        /// asset key based on its relative path within the directory, and an
        /// archive path under the given archive root.
        /// </summary>
        public V12PakManifestBuilder AddAssetsFromDirectory(string assetType, string diskDir, string? archiveRoot = null)
        {
            archiveRoot ??= $"assets/{assetType}";
            string normalizedRoot = Path.GetFullPath(diskDir);

            foreach (string filePath in Directory.EnumerateFiles(diskDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = filePath.Substring(normalizedRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');

                _assets[$"{assetType}/{relativePath}"] = NormalizePath($"{archiveRoot}/{relativePath}");
            }

            return this;
        }

        /// <summary>Get the asset dictionary (for inspection/modification before Build).</summary>
        public Dictionary<string, string> Assets => _assets;

        // ── Pak DLLs ──

        /// <summary>Add a DLL gamepack reference by its archive path.</summary>
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

        // ── Scripts ──

        /// <summary>Add a Contract (<c>.ct</c>) / precompiled ObjektRT gamepack entrypoint.</summary>
        public V12PakManifestBuilder AddScript(string archivePath)
        {
            _scripts.Add(NormalizePath(archivePath));
            return this;
        }

        /// <summary>Add a C# component source (<c>.cs</c>) compiled when the pak loads.</summary>
        public V12PakManifestBuilder AddCSharpScript(string archivePath)
        {
            _csharpScripts.Add(NormalizePath(archivePath));
            return this;
        }

        /// <summary>Add a dependency <c>.v12pak</c>.</summary>
        public V12PakManifestBuilder AddDependency(string dependency)
        {
            _dependencies.Add(NormalizePath(dependency));
            return this;
        }

        // ── Build ──

        /// <summary>Build and return the final <see cref="V12PakManifest"/>. The builder can be reused.</summary>
        public V12PakManifest Build()
        {
            return new V12PakManifest
            {
                Version = _version,
                Engine = _engine,
                Worlds = new List<V12PakWorldEntry>(_worlds),
                Assets = new Dictionary<string, string>(_assets, StringComparer.OrdinalIgnoreCase),
                Paks = new List<string>(_paks),
                Scripts = new List<string>(_scripts),
                CSharpScripts = new List<string>(_csharpScripts),
                Dependencies = new List<string>(_dependencies),
            };
        }

        /// <summary>
        /// Build the manifest and immediately write it to a tar.gz pak with the
        /// given file entries.
        /// </summary>
        public void BuildAndWrite(string outputPath, IEnumerable<(string ArchivePath, byte[] Data)> entries)
        {
            using var writer = new V12PakWriter(outputPath);
            writer.WriteManifest(Build());
            foreach (var (archivePath, data) in entries)
                writer.AddFile(archivePath, data);
        }

        /// <summary>
        /// Build the manifest and write it to a pak, adding all files from a
        /// source directory.
        /// </summary>
        public void BuildAndWriteFromDirectory(string outputPath, string sourceDir, string? archiveRoot = null)
        {
            using var writer = new V12PakWriter(outputPath);
            writer.WriteManifest(Build());

            string normalizedRoot = Path.GetFullPath(sourceDir);
            foreach (string filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = filePath.Substring(normalizedRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');

                string archivePath = string.IsNullOrEmpty(archiveRoot)
                    ? relativePath
                    : NormalizePath(archiveRoot + "/" + relativePath);

                writer.AddFile(archivePath, filePath);
            }
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }
    }
}
