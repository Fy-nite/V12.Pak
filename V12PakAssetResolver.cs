using V12.Core.Interfaces;

namespace V12.Pak
{
    /// <summary>
    /// <see cref="IAssetResolver"/> that serves assets from a <see cref="V12PakReader"/>.
    ///
    /// <c>v12://</c> URIs resolve through the manifest's asset map first
    /// (virtual key → archive path → extracted temp path), then through world
    /// mount points. Because the reader materializes the archive to disk,
    /// <see cref="Resolve"/> returns real readable file paths, so the engine's
    /// path-based consumers (script components, texture loaders) work unchanged.
    /// </summary>
    public sealed class V12PakAssetResolver : IAssetResolver
    {
        private readonly V12PakReader _reader;
        private readonly Dictionary<string, string> _assetMap;
        private readonly Dictionary<string, string> _mounts = new(StringComparer.OrdinalIgnoreCase);

        public V12PakAssetResolver(V12PakReader reader, IReadOnlyDictionary<string, string> assets)
        {
            _reader = reader;
            _assetMap = new Dictionary<string, string>(assets, StringComparer.OrdinalIgnoreCase);
        }

        public void Mount(string mountPoint, string physicalPath)
            => _mounts[mountPoint] = physicalPath;

        public void Unmount(string mountPoint)
            => _mounts.Remove(mountPoint);

        /// <summary>
        /// Resolver consulted when this pak has no match. Set to the previously
        /// active resolver when a pak is loaded, so a pak can read assets from the
        /// dependency paks loaded before it.
        /// </summary>
        public IAssetResolver? Fallback { get; set; }

        /// <summary>
        /// Resolve a <c>v12://</c> URI to a physical file path. Returns the URI
        /// unchanged when nothing matches.
        /// </summary>
        public string Resolve(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return uri;

            string key = StripScheme(uri);
            if (_assetMap.TryGetValue(key, out var archivePath))
            {
                var path = _reader.ResolveEntryPath(archivePath);
                if (path != null) return path;
            }

            int slash = key.IndexOf('/');
            if (slash > 0 && _mounts.TryGetValue(key[..slash], out var baseDir))
            {
                var candidate = Path.Combine(baseDir, key[(slash + 1)..]);
                if (File.Exists(candidate)) return candidate;
            }

            return Fallback?.Resolve(uri) ?? uri;
        }

        /// <summary>
        /// Open a <c>v12://</c> URI or archive path as a stream. Returns null
        /// when the entry does not exist (the interface contract is loosely
        /// honoured by <see cref="V12AssetResolver"/> the same way).
        /// </summary>
        public Stream Open(string uri)
        {
            string resolved = Resolve(uri);
            if (resolved != uri && File.Exists(resolved))
                return File.OpenRead(resolved);

            string key = StripScheme(uri);
            return _reader.OpenEntry(key) ?? _reader.OpenEntry(uri)!;
        }

        private static string StripScheme(string uri)
        {
            if (uri.StartsWith("v12://", StringComparison.OrdinalIgnoreCase))
                return uri.Substring(6).TrimStart('/');
            return uri;
        }
    }
}
