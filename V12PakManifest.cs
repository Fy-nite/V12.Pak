namespace V12.Pak
{
    /// <summary>
    /// Data model for <c>manifest.json</c> inside a <c>.v12pak</c> archive.
    /// </summary>
    public sealed class V12PakManifest
    {
        /// <summary>Manifest format version. Currently 1.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Engine identifier. Always "v12".</summary>
        public string Engine { get; set; } = "v12";

        /// <summary>Worlds contained in this pak. A pak can hold multiple worlds.</summary>
        public List<V12PakWorldEntry> Worlds { get; set; } = new();

        /// <summary>
        /// Virtual path to archive path mapping. Keys are <c>v12://</c> resolvable
        /// paths (e.g. <c>textures/crate.png</c>), values are tar entry paths
        /// (e.g. <c>assets/textures/crate.png</c>).
        /// </summary>
        public Dictionary<string, string> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Archive paths to C# DLL gamepaks, loaded via <see cref="GamePak.GamepackLoader"/>.</summary>
        public List<string> Paks { get; set; } = new();

        /// <summary>
        /// Archive paths to script gamepack entrypoints. <c>.ct</c> Contract
        /// sources are compiled at load time; <c>.orbt</c>/<c>.oil</c>
        /// precompiled ObjektRT modules are loaded as-is. Each entrypoint must
        /// define a <c>Main</c> function; an optional
        /// <c>OnUpdate(float deltaTime)</c> hook is invoked per frame.
        /// </summary>
        public List<string> Scripts { get; set; } = new();

        /// <summary>
        /// Archive paths to C# component sources (<c>.cs</c>) compiled at load
        /// time. Every <c>IComponent</c> type they declare is registered so this
        /// pak's worlds and Contract scripts, and other paks, can attach it.
        /// When empty, every <c>.cs</c> under the archive's <c>scripts/</c>
        /// folder is compiled instead.
        /// </summary>
        public List<string> CSharpScripts { get; set; } = new();

        /// <summary>
        /// Other <c>.v12pak</c> archives this pak depends on. Values are file
        /// names, relative paths or absolute paths; they are resolved against
        /// the pak's own directory first, then
        /// <see cref="V12PakOptions.DependencySearchPaths"/>, then the working
        /// directory.
        /// <para>
        /// Dependencies are loaded before this pak, so their DLLs and compiled
        /// C# component types are available to this pak's worlds, C# sources and
        /// Contract scripts.
        /// </para>
        /// </summary>
        public List<string> Dependencies { get; set; } = new();
    }

    public sealed class V12PakWorldEntry
    {
        /// <summary>Human-readable world name. Used as the <c>v12://</c> mount point.</summary>
        public string Name { get; set; } = "";

        /// <summary>Path inside the tar archive to the world's world.xml.</summary>
        public string Entry { get; set; } = "";

        /// <summary>Path inside the tar archive to the world's templates directory.</summary>
        public string TemplatesDir { get; set; } = "";
    }
}
