using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace V12.Pak
{
    /// <summary>
    /// Builds a .v12pak tar.gz archive from a directory of game content.
    /// Writes the manifest and all files into the archive.
    /// </summary>
    public class V12PakWriter : IDisposable
    {
        private readonly string _outputPath;
        private readonly List<(string ArchivePath, byte[] Data)> _entries = new();
        private bool _disposed;
        private bool _finalized;

        public V12PakWriter(string outputPath)
        {
            _outputPath = outputPath;

            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>Add a file from a byte array.</summary>
        public void AddFile(string archivePath, byte[] data)
        {
            if (_finalized) throw new InvalidOperationException("Cannot add entries after finalization.");
            _entries.Add((NormalizePath(archivePath), data));
        }

        /// <summary>Add a file from a stream. The stream is fully read into memory.</summary>
        public void AddFile(string archivePath, Stream data)
        {
            if (_finalized) throw new InvalidOperationException("Cannot add entries after finalization.");
            using var ms = new MemoryStream();
            data.CopyTo(ms);
            _entries.Add((NormalizePath(archivePath), ms.ToArray()));
        }

        /// <summary>Add a file from disk.</summary>
        public void AddFileFromDisk(string archivePath, string diskPath)
        {
            if (_finalized) throw new InvalidOperationException("Cannot add entries after finalization.");
            byte[] data = File.ReadAllBytes(diskPath);
            _entries.Add((NormalizePath(archivePath), data));
        }

        /// <summary>
        /// Recursively add all files from a directory.
        /// Each file's archive path is relative to the directory root.
        /// </summary>
        public void AddDirectory(string archiveRoot, string diskDir)
        {
            if (_finalized) throw new InvalidOperationException("Cannot add entries after finalization.");

            string normalizedRoot = Path.GetFullPath(diskDir);

            foreach (string filePath in Directory.EnumerateFiles(diskDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = filePath.Substring(normalizedRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');

                string archivePath = NormalizePath(archiveRoot + "/" + relativePath);
                byte[] data = File.ReadAllBytes(filePath);
                _entries.Add((archivePath, data));
            }
        }

        /// <summary>
        /// Packs a game directory into the pak. Scans for worlds (directories containing world.xml),
        /// asset files (meshes, textures, scripts), and DLLs. Generates the manifest automatically.
        /// </summary>
        public V12PakManifest PackFromDirectory(string gameDir)
        {
            if (_finalized) throw new InvalidOperationException("Cannot pack after finalization.");

            string normalizedRoot = Path.GetFullPath(gameDir);
            var manifest = new V12PakManifest { Version = 1, Engine = "v12" };

            // Scan for worlds (directories containing world.xml)
            foreach (string worldXml in Directory.EnumerateFiles(gameDir, "world.xml", SearchOption.AllDirectories))
            {
                string worldDir = Path.GetDirectoryName(worldXml)!;
                string relativeDir = worldDir.Substring(normalizedRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');

                string worldName = string.IsNullOrEmpty(relativeDir)
                    ? Path.GetFileNameWithoutExtension(_outputPath)
                    : Path.GetFileName(relativeDir);

                string entryPath = string.IsNullOrEmpty(relativeDir)
                    ? "world.xml"
                    : relativeDir + "/world.xml";

                var worldEntry = new WorldEntry
                {
                    Name = worldName,
                    Entry = NormalizePath(entryPath),
                };

                // Check for templates directory
                string templatesDir = Path.Combine(worldDir, "templates");
                if (Directory.Exists(templatesDir))
                {
                    worldEntry.TemplatesDir = NormalizePath(
                        (string.IsNullOrEmpty(relativeDir) ? "" : relativeDir + "/") + "templates");
                }

                manifest.Worlds.Add(worldEntry);

                // Add all files from the world directory
                AddDirectory(string.IsNullOrEmpty(relativeDir) ? "worlds/" + worldName : "worlds/" + relativeDir, worldDir);
            }

            // Scan for asset directories
            string[] assetDirs = { "meshes", "textures", "scripts", "audio", "fonts" };
            foreach (string assetType in assetDirs)
            {
                string assetDir = Path.Combine(gameDir, assetType);
                if (Directory.Exists(assetDir))
                {
                    string pakAssetDir = "assets/" + assetType;
                    AddDirectory(pakAssetDir, assetDir);

                    // Register each asset in the manifest
                    foreach (string filePath in Directory.EnumerateFiles(assetDir, "*", SearchOption.AllDirectories))
                    {
                        string relativePath = filePath.Substring(assetDir.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .Replace('\\', '/');

                        string pakPath = pakAssetDir + "/" + relativePath;
                        string assetKey = assetType + "/" + relativePath;
                        manifest.Assets[assetKey] = NormalizePath(pakPath);
                    }
                }
            }

            // Scan for DLLs in a paks/ directory
            string paksDir = Path.Combine(gameDir, "paks");
            if (Directory.Exists(paksDir))
            {
                foreach (string dll in Directory.EnumerateFiles(paksDir, "*.dll", SearchOption.AllDirectories))
                {
                    string relativePath = dll.Substring(normalizedRoot.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');

                    string pakPath = NormalizePath(relativePath);
                    AddFileFromDisk(pakPath, dll);
                    manifest.Paks.Add(pakPath);
                }
            }

            return manifest;
        }

        /// <summary>
        /// Write the tar.gz archive with all added entries.
        /// If a manifest is provided, it is written as manifest.json first.
        /// </summary>
        public void Finalize(V12PakManifest? manifest = null)
        {
            if (_finalized) throw new InvalidOperationException("Already finalized.");
            _finalized = true;

            using var fileStream = File.Create(_outputPath);
            using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
            using var tarWriter = new TarWriter(gzipStream);

            // Write manifest first
            if (manifest != null)
            {
                byte[] manifestJson = manifest.ToJson();
                var manifestEntry = new PaxTarEntry(TarEntryType.RegularFile, "manifest.json");
                manifestEntry.DataStream = new MemoryStream(manifestJson, writable: false);
                tarWriter.WriteEntry(manifestEntry);
            }

            // Write all entries
            foreach (var (archivePath, data) in _entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, archivePath);
                entry.DataStream = new MemoryStream(data, writable: false);
                tarWriter.WriteEntry(entry);
            }
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (!_finalized)
            {
                // If not explicitly finalized, do a best-effort finalize without manifest
                try { Finalize(null); }
                catch { /* ignore */ }
            }
        }
    }
}
