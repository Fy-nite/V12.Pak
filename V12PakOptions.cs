using System;
using System.Collections.Generic;

namespace V12.Pak
{
    /// <summary>
    /// Controls what gets loaded from a .v12pak archive.
    /// Use this to restrict untrusted paks (user-generated content, community mods).
    /// </summary>
    public class V12PakOptions
    {
        /// <summary>
        /// Whether to load DLL gamepaks from the pak. Default: false.
        /// When false, any DLLs in the pak's paks/ directory are skipped entirely.
        /// </summary>
        public bool LoadDlls { get; set; } = false;

        /// <summary>
        /// File extensions allowed to be imported as assets.
        /// Null = allow all extensions. Empty set = block all assets.
        /// Checked against the archive path of each asset in the manifest.
        /// Comparison is case-insensitive.
        /// </summary>
        public HashSet<string>? AllowedAssetExtensions { get; set; } = null;

        /// <summary>
        /// Specific asset keys to block (exact match against manifest asset keys).
        /// Checked after the extension whitelist.
        /// </summary>
        public HashSet<string>? BlockedAssetKeys { get; set; } = null;

        /// <summary>
        /// Whether to allow the pak's manifest to declare worlds for loading. Default: true.
        /// Set to false to only import assets without loading any worlds.
        /// </summary>
        public bool AllowWorldLoading { get; set; } = true;

        /// <summary>
        /// Full trust: load everything including DLLs, no extension filtering.
        /// </summary>
        public static V12PakOptions FullTrust => new() { LoadDlls = true };

        /// <summary>
        /// Restricted: no DLLs, common game asset types only.
        /// </summary>
        public static V12PakOptions Restricted => new()
        {
            LoadDlls = false,
            AllowedAssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".xml", ".glb", ".gltf", ".obj", ".fbx",
                ".png", ".jpg", ".jpeg", ".bmp", ".tga",
                ".lua", ".json"
            }
        };

        /// <summary>
        /// Worlds only: load world XML and templates, no other assets or DLLs.
        /// </summary>
        public static V12PakOptions WorldsOnly => new()
        {
            LoadDlls = false,
            AllowedAssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".xml"
            }
        };

        /// <summary>
        /// Check if an asset with the given archive path and key is allowed by these options.
        /// </summary>
        public bool IsAssetAllowed(string archivePath, string assetKey)
        {
            if (AllowedAssetExtensions != null)
            {
                string ext = System.IO.Path.GetExtension(archivePath);
                if (string.IsNullOrEmpty(ext) || !AllowedAssetExtensions.Contains(ext))
                    return false;
            }

            if (BlockedAssetKeys != null && BlockedAssetKeys.Contains(assetKey))
                return false;

            return true;
        }
    }
}
