using System;
using System.IO;
using V12.Core.Interfaces;

namespace V12.Pak
{
    /// <summary>
    /// IAssetResolver implementation that reads from a V12PakReader.
    /// Falls back to an optional inner resolver for loose files on disk.
    /// </summary>
    public class V12PakAssetResolver : IAssetResolver
    {
        private readonly V12PakReader _reader;
        private readonly IAssetResolver? _fallback;
        private readonly V12PakOptions _options;

        /// <summary>The pak reader this resolver reads from.</summary>
        public V12PakReader Reader => _reader;

        public V12PakAssetResolver(V12PakReader reader, V12PakOptions? options = null, IAssetResolver? fallback = null)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _options = options ?? V12PakOptions.FullTrust;
            _fallback = fallback;
        }

        public string Resolve(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return uri;

            // Extract the asset path from v12:// URIs
            string? assetKey = ExtractAssetKey(uri);
            if (assetKey != null && _reader.Manifest.Assets.TryGetValue(assetKey, out string? pakPath))
            {
                if (!_options.IsAssetAllowed(pakPath, assetKey))
                    return uri; // Blocked by options

                // Check if the entry is extracted
                string? diskPath = _reader.GetEntryPath(pakPath);
                if (diskPath != null)
                    return diskPath;
            }

            // Fall back to inner resolver (loose files)
            return _fallback?.Resolve(uri) ?? uri;
        }

        public Stream Open(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return Stream.Null;

            // Try pak first
            string? assetKey = ExtractAssetKey(uri);
            if (assetKey != null && _reader.Manifest.Assets.TryGetValue(assetKey, out string? pakPath))
            {
                if (!_options.IsAssetAllowed(pakPath, assetKey))
                    return Stream.Null; // Blocked by options

                Stream? stream = _reader.OpenEntry(pakPath);
                if (stream != null)
                    return stream;
            }

            // Fall back to inner resolver
            if (_fallback != null)
                return _fallback.Open(uri);

            return Stream.Null;
        }

        public void Mount(string mountPoint, string physicalPath)
        {
            // No-op: pak IS the mount. Fallback handles loose file mounts.
            _fallback?.Mount(mountPoint, physicalPath);
        }

        public void Unmount(string mountPoint)
        {
            _fallback?.Unmount(mountPoint);
        }

        /// <summary>
        /// Extracts the asset key from a v12:// URI.
        /// e.g. "v12://MyWorld/textures/crate.png" → "textures/crate.png"
        /// </summary>
        private static string? ExtractAssetKey(string uri)
        {
            if (!uri.StartsWith("v12://", StringComparison.OrdinalIgnoreCase))
                return null;

            string path = uri.Substring(6).TrimStart('/');

            // Skip the mount point name (first segment)
            int slashIndex = path.IndexOf('/');
            if (slashIndex > 0)
                return path.Substring(slashIndex + 1);

            return null;
        }
    }
}
