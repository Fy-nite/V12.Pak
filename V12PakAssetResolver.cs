using V12.Core.Interfaces;

namespace V12.Pak
{
    /// <summary>
    /// <see cref="IAssetResolver"/> that serves assets from a <see cref="V12PakReader"/>.
    ///
    /// <c>v12://</c> URIs resolve through the manifest's asset map
    /// (virtual key → archive path → extracted temp path), then through the same
    /// assets addressed by their archive path, then through world mount points.
    /// The archive-path lookup is what lets a world written for <c>--game</c>
    /// mode keep using <c>v12://Assets/CesiumMan.glb</c> once it ships in a pak.
    /// Only assets that survived <see cref="V12PakOptions"/> filtering are in
    /// either map, so a blocked asset stays unresolved whichever way it is named.
    /// Because the reader materializes the archive to disk, <see cref="Resolve"/>
    /// returns real readable file paths, so the engine's path-based consumers
    /// (script components, texture loaders) work unchanged.
    /// </summary>
    public sealed class V12PakAssetResolver : IAssetResolver
    {
        private readonly V12PakReader _reader;
        private readonly Dictionary<string, string> _assetMap;
        private readonly Dictionary<string, string> _byArchivePath;
        private readonly Dictionary<string, string> _mounts = new(StringComparer.OrdinalIgnoreCase);

        public V12PakAssetResolver(V12PakReader reader, IReadOnlyDictionary<string, string> assets)
        {
            _reader = reader;
            _assetMap = new Dictionary<string, string>(assets, StringComparer.OrdinalIgnoreCase);

            // Same assets, addressed the way a --game world addresses them
            // ("Assets/CesiumMan.glb" rather than the manifest key). The reader's
            // entry index is case-insensitive, so a pak packed from a Windows
            // folder matches a world written with different casing on Linux.
            _byArchivePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string archivePath in assets.Values)
            {
                if (string.IsNullOrWhiteSpace(archivePath)) continue;

                string? extracted = reader.ResolveEntryPath(archivePath);
                if (extracted is not null)
                    _byArchivePath[archivePath] = extracted;
            }
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

            if (_byArchivePath.TryGetValue(key, out var direct))
                return direct;

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
