namespace V12.Pak
{
    /// <summary>
    /// Security/loading options controlling what gets imported from a <c>.v12pak</c>.
    /// Defaults to the "user-generated content" trust level: no DLLs, everything else allowed.
    /// </summary>
    public sealed class V12PakOptions
    {
        /// <summary>
        /// Whether to load DLL gamepaks from the pak. Default: false.
        /// When false, any DLLs in the pak's paks/ directory are skipped entirely.
        /// </summary>
        public bool LoadDlls { get; set; } = false;

        /// <summary>
        /// Whether to compile and run Contract (<c>.ct</c>) gamepack scripts from
        /// the pak. Default: false. Contract compilation executes arbitrary code,
        /// so it sits in the same trust tier as <see cref="LoadDlls"/>.
        /// </summary>
        public bool LoadContractScripts { get; set; } = false;

        /// <summary>
        /// Whether to compile and load C# component sources (<c>.cs</c>) from the
        /// pak. Default: false. Like <see cref="LoadContractScripts"/> this runs
        /// arbitrary code, so it sits in the same trust tier as
        /// <see cref="LoadDlls"/>.
        /// </summary>
        public bool LoadCSharpScripts { get; set; } = false;

        /// <summary>
        /// Whether to load the paks listed in the manifest's
        /// <see cref="V12PakManifest.Dependencies"/> before this pak. Default: true.
        /// </summary>
        public bool LoadDependencies { get; set; } = true;

        /// <summary>
        /// Extra directories (or explicit file paths) searched for dependency
        /// paks, after the depending pak's own directory and before the working
        /// directory.
        /// </summary>
        public List<string> DependencySearchPaths { get; set; } = new();

        /// <summary>
        /// File extensions allowed to be imported as assets.
        /// Null = allow all extensions. Empty set = block all assets.
        /// Checked against the archive path of each asset in the manifest.
        /// </summary>
        public HashSet<string>? AllowedAssetExtensions { get; set; } = null;

        /// <summary>
        /// Specific asset paths to block (exact match, case-insensitive, against manifest asset keys).
        /// Checked after the extension whitelist.
        /// </summary>
        public HashSet<string>? BlockedAssetKeys { get; set; } = null;

        /// <summary>
        /// Whether to allow the pak's manifest to declare worlds for loading. Default: true.
        /// Set to false to only import assets without loading any worlds.
        /// </summary>
        public bool AllowWorldLoading { get; set; } = true;
    }
}
