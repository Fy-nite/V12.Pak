using System;
using System.IO;
using V12.Core;

namespace V12.Pak
{
    /// <summary>
    /// Extension methods on GameRoot for loading and building .v12pak archives.
    /// These keep V12 engine free of pak dependencies (no circular reference).
    /// </summary>
    public static class GameRootPakExtensions
    {
        /// <summary>
        /// Load a .v12pak archive and register its worlds and assets on this GameRoot.
        /// Optionally loads worlds and DLLs from the pak.
        /// </summary>
        public static V12PakLoader LoadPak(this GameRoot root, string pakPath, V12PakOptions? options = null, bool loadWorlds = true, bool loadDlls = true)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (!File.Exists(pakPath))
                throw new FileNotFoundException($"Pak file not found: {pakPath}");

            var loader = new V12PakLoader();
            loader.Load(pakPath, options, loadWorlds, loadDlls);

            // Register the loader so it can be disposed later
            root.Registry?.Register($"PakLoader_{Path.GetFileNameWithoutExtension(pakPath)}", loader);

            return loader;
        }

        /// <summary>
        /// Build a .v12pak archive from a game directory.
        /// </summary>
        public static void BuildPak(this GameRoot root, string gameDir, string outputPath)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (!Directory.Exists(gameDir))
                throw new DirectoryNotFoundException($"Game directory not found: {gameDir}");

            using var writer = new V12PakWriter(outputPath);
            var manifest = writer.PackFromDirectory(gameDir);
            writer.Finalize(manifest);
        }
    }
}
