using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using V12.Core;
using V12.Core.Interfaces;

namespace V12.Pak
{
    /// <summary>
    /// High-level orchestrator for loading a .v12pak file.
    /// Opens the pak, mounts the asset resolver, loads worlds, and loads DLLs.
    /// </summary>
    public class V12PakLoader : IDisposable
    {
        private V12PakReader? _reader;
        private V12PakAssetResolver? _resolver;
        private readonly List<V12PakLoaderResult> _results = new();
        private bool _disposed;

        /// <summary>The reader created by this loader.</summary>
        public V12PakReader? Reader => _reader;

        /// <summary>The asset resolver created by this loader.</summary>
        public V12PakAssetResolver? Resolver => _resolver;

        /// <summary>Results of loading operations (worlds loaded, DLLs loaded, errors).</summary>
        public IReadOnlyList<V12PakLoaderResult> Results => _results;

        /// <summary>
        /// Loads a .v12pak file. Optionally loads worlds and DLLs.
        /// </summary>
        /// <param name="pakPath">Path to the .v12pak file.</param>
        /// <param name="options">Loading options. Null means full trust.</param>
        /// <param name="loadWorlds">If true, loads worlds from the pak.</param>
        /// <param name="loadDlls">If true, loads DLL plugins from the pak.</param>
        public void Load(string pakPath, V12PakOptions? options = null, bool loadWorlds = true, bool loadDlls = true)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(V12PakLoader));
            if (_reader != null) throw new InvalidOperationException("Already loaded. Dispose first.");

            options ??= V12PakOptions.FullTrust;

            _results.Clear();

            // Open the pak
            try
            {
                _reader = new V12PakReader(pakPath);
                _results.Add(new V12PakLoaderResult
                {
                    Type = V12PakLoaderResultType.Opened,
                    Message = $"Opened pak: {Path.GetFileName(pakPath)} ({_reader.GetAllEntries().Count()} entries)"
                });
            }
            catch (Exception ex)
            {
                _results.Add(new V12PakLoaderResult
                {
                    Type = V12PakLoaderResultType.Error,
                    Message = $"Failed to open pak: {ex.Message}"
                });
                return;
            }

            // Create the asset resolver
            _resolver = new V12PakAssetResolver(_reader, options);

            // Mount the pak's temp directory as a v12:// mount point on the existing AssetResolver
            string mountPoint = Path.GetFileNameWithoutExtension(pakPath);
            var existingResolver = Core.GameRoot.Instance?.Registry?.Get<Core.Interfaces.IAssetResolver>();
            existingResolver?.Mount(mountPoint, _reader.TempDirectory);

            _results.Add(new V12PakLoaderResult
            {
                Type = V12PakLoaderResultType.ResolverMounted,
                Message = $"Mounted pak at v12://{mountPoint}"
            });

            // Load worlds
            if (loadWorlds && options.AllowWorldLoading)
            {
                foreach (var world in _reader.Manifest.Worlds)
                {
                    try
                    {
                        string? worldXmlPath = _reader.GetEntryPath(world.Entry);
                        if (worldXmlPath == null)
                        {
                            _results.Add(new V12PakLoaderResult
                            {
                                Type = V12PakLoaderResultType.Error,
                                Message = $"World entry not found: {world.Entry}"
                            });
                            continue;
                        }

                        // Load the world using WorldLoader
                        Core.WorldLoader.LoadFromArchive(worldXmlPath);

                        _results.Add(new V12PakLoaderResult
                        {
                            Type = V12PakLoaderResultType.WorldLoaded,
                            Message = $"Loaded world: {world.Name}"
                        });
                    }
                    catch (Exception ex)
                    {
                        _results.Add(new V12PakLoaderResult
                        {
                            Type = V12PakLoaderResultType.Error,
                            Message = $"Failed to load world '{world.Name}': {ex.Message}"
                        });
                    }
                }
            }

            // Load DLLs
            if (loadDlls && options.LoadDlls)
            {
                foreach (string pakDllPath in _reader.Manifest.Paks)
                {
                    try
                    {
                        string? diskPath = _reader.GetEntryPath(pakDllPath);
                        if (diskPath == null)
                        {
                            _results.Add(new V12PakLoaderResult
                            {
                                Type = V12PakLoaderResultType.Error,
                                Message = $"DLL entry not found: {pakDllPath}"
                            });
                            continue;
                        }

                        // Load the DLL as a game pack via Assembly.LoadFrom + GamepackLoader
                        var assembly = System.Reflection.Assembly.LoadFrom(diskPath);
                        var loader = new Core.GamePak.GamepackLoader();
                        int loaded = loader.LoadFromAssembly(assembly);
                        loader.InitializeAll();
                        loader.StartAll();
                        if (loaded > 0)
                        {
                            _results.Add(new V12PakLoaderResult
                            {
                                Type = V12PakLoaderResultType.DllLoaded,
                                Message = $"Loaded {loaded} gamepack(s) from: {Path.GetFileName(pakDllPath)}"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _results.Add(new V12PakLoaderResult
                        {
                            Type = V12PakLoaderResultType.Error,
                            Message = $"Failed to load DLL '{pakDllPath}': {ex.Message}"
                        });
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _resolver = null;
            _reader?.Dispose();
            _reader = null;
        }
    }

    /// <summary>Result of a single pak loading operation.</summary>
    public class V12PakLoaderResult
    {
        public V12PakLoaderResultType Type { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public enum V12PakLoaderResultType
    {
        Opened,
        ResolverMounted,
        WorldLoaded,
        DllLoaded,
        Error
    }
}
