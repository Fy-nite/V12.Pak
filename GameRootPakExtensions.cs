using V12.Core;

namespace V12.Pak
{
    /// <summary>
    /// Convenience entry points on <see cref="GameRoot"/> for loading and
    /// building <c>.v12pak</c> archives. V12-engine itself has no dependency on
    /// V12.Pak, so these live here as extension methods.
    /// </summary>
    public static class GameRootPakExtensions
    {
        /// <summary>
        /// Load a .v12pak archive into this game root. Returns a result the
        /// caller must keep alive while the pak's worlds/assets are in use.
        /// </summary>
        public static V12PakLoadResult LoadPak(this GameRoot root, string pakPath, V12PakOptions? options = null)
            => V12PakLoader.Load(pakPath, options, root);

        /// <summary>Build a .v12pak archive from a directory of game content.</summary>
        public static void BuildPak(this GameRoot root, string inputDirectory, string outputPath)
            => V12PakBuilder.Build(inputDirectory, outputPath);
    }
}
