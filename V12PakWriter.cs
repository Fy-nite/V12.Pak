using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace V12.Pak
{
    /// <summary>
    /// Builds a <c>.v12pak</c> tar.gz archive from game content. Writes entries
    /// sequentially; the manifest should be written first (see <see cref="WriteManifest"/>).
    /// </summary>
    public sealed class V12PakWriter : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly FileStream _fileStream;
        private readonly GZipStream _gzipStream;
        private readonly TarWriter _tarWriter;
        private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);

        public V12PakWriter(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is required.", nameof(outputPath));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            _fileStream = File.Create(outputPath);
            _gzipStream = new GZipStream(_fileStream, CompressionLevel.Optimal);
            _tarWriter = new TarWriter(_gzipStream);
        }

        /// <summary>Write a file entry from a byte array.</summary>
        public void AddFile(string archivePath, byte[] data)
        {
            using var ms = new MemoryStream(data, writable: false);
            AddFile(archivePath, ms);
        }

        /// <summary>
        /// Write a file entry from a stream. The stream is copied during this call.
        /// Duplicate entry names throw.
        /// </summary>
        public void AddFile(string archivePath, Stream data)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
                throw new ArgumentException("Archive path is required.", nameof(archivePath));

            string name = NormalizeName(archivePath);
            if (!_written.Add(name))
                throw new InvalidOperationException($"Duplicate v12pak entry: '{archivePath}'");

            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = data
            };
            _tarWriter.WriteEntry(entry);
        }

        /// <summary>Write a file entry by copying a file from disk.</summary>
        public void AddFile(string archivePath, string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Source file not found: {filePath}", filePath);
            using var stream = File.OpenRead(filePath);
            AddFile(archivePath, stream);
        }

        /// <summary>Write a UTF-8 text entry (e.g. world.xml).</summary>
        public void AddText(string archivePath, string text)
            => AddFile(archivePath, Encoding.UTF8.GetBytes(text));

        /// <summary>Serialize and write <c>manifest.json</c>. Call before the other entries.</summary>
        public void WriteManifest(V12PakManifest manifest)
            => AddText("manifest.json", JsonSerializer.Serialize(manifest, JsonOpts));

        public void Dispose()
        {
            _tarWriter?.Dispose();
            _gzipStream?.Dispose();
            _fileStream?.Dispose();
        }

        private static string NormalizeName(string archivePath) => archivePath.Replace('\\', '/').TrimStart('/');
    }
}
