using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Xml;
using V12.Bindings;
using V12.Core;
using V12.Core.Interfaces;
using V12.WorldML;

namespace V12.Pak
{
    /// <summary>Outcome of loading a <c>.v12pak</c> into a <see cref="GameRoot"/>.</summary>
    public sealed class V12PakLoadResult : IDisposable
    {
        public string PakPath { get; }
        public V12PakManifest Manifest { get; }
        public V12PakReader Reader { get; }
        public V12PakAssetResolver Resolver { get; }
        public List<World> LoadedWorlds { get; } = new();

        /// <summary>Assemblies loaded from this pak (DLL gamepaks and compiled C# sources).</summary>
        public List<Assembly> LoadedAssemblies { get; } = new();

        /// <summary>Results for the paks listed in <see cref="V12PakManifest.Dependencies"/>, loaded before this one.</summary>
        public List<V12PakLoadResult> Dependencies { get; } = new();

        public int LoadedGamepaks { get; set; }

        /// <summary>Number of C# source sets compiled and loaded from this pak.</summary>
        public int LoadedCSharpGamepaks { get; set; }

        /// <summary>Number of component types registered from this pak's compiled C# sources.</summary>
        public int RegisteredCSharpTypes { get; set; }

        /// <summary>Compiler diagnostics from this pak's C# sources (empty when clean).</summary>
        public List<string> CSharpDiagnostics { get; } = new();

        public int SkippedAssets { get; set; }
        public int SkippedGamepaks { get; set; }

        /// <summary>Absolute paths of every dependency pak loaded for this one.</summary>
        public IReadOnlyList<string> LoadedDependencyPaths
            => Dependencies.Select(d => d.PakPath).ToList();

        public V12PakLoadResult(string pakPath, V12PakManifest manifest, V12PakReader reader, V12PakAssetResolver resolver)
        {
            PakPath = pakPath;
            Manifest = manifest;
            Reader = reader;
            Resolver = resolver;
        }

        /// <summary>
        /// Release the reader and any dependency paks. The content-addressed
        /// extraction cache stays on disk so the next launch of the same pak
        /// reuses it (<see cref="V12PakCache"/>). Loaded worlds remain.
        /// </summary>
        public void Dispose()
        {
            foreach (var dependency in Dependencies) dependency.Dispose();
            Dependencies.Clear();
            Reader.Dispose();
        }
    }

    /// <summary>
    /// High-level orchestrator for loading <c>.v12pak</c> archives into a running
    /// game: reads the manifest, loads declared dependency paks first, applies
    /// <see cref="V12PakOptions"/> security filtering, loads bundled DLL gamepaks
    /// and C# component sources, loads declared worlds (world.xml + templates),
    /// registers the pak asset resolver, and optionally compiles Contract
    /// (<c>.ct</c>) script gamepaks.
    /// <para>
    /// Component types (from DLLs or C# sources) are registered before worlds are
    /// parsed, so a pak's own worlds and Contract scripts can reference components
    /// the pak ships, as can paks that depend on it.
    /// </para>
    /// The caller keeps the returned <see cref="V12PakLoadResult"/> alive so the
    /// resolver's backing temp directory persists; dispose it to unload.
    /// </summary>
    public static class V12PakLoader
    {
        private const string LoadedPaksKey = "V12LoadedPaks";

        private static Serilog.ILogger Log => GameRoot.Log;

        /// <summary>
        /// Host hook for loading pak DLLs into the renderer's own assembly load
        /// context (Godot's plugin context). Null keeps Assembly.LoadFrom.
        /// </summary>
        public static Func<string, Assembly>? AssemblyLoader { get; set; }

        /// <summary>Host hook for finding an already-loaded assembly by simple name.</summary>
        public static Func<string, Assembly?>? AssemblyFinder { get; set; }

        public static V12PakLoadResult Load(string pakPath, V12PakOptions? options = null, GameRoot? root = null)
        {
            options ??= new V12PakOptions();
            root ??= GameRoot.Instance
                ?? throw new InvalidOperationException("No active GameRoot. Pass a GameRoot instance to Load().");

            string fullPath = Path.GetFullPath(pakPath);
            return LoadCore(
                fullPath,
                () => new V12PakReader(fullPath),
                options,
                root,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Loads a pak supplied as a stream rather than a file path: an archive
        /// embedded in the host's own package (Godot's <c>res://</c> on Android)
        /// or downloaded by the game. <paramref name="pakName"/> is the identity
        /// used for the dependency chain, logs and cache stem. The factory must
        /// open a fresh seekable stream each time it is called.
        /// </summary>
        public static V12PakLoadResult LoadFromStream(
            string pakName,
            Func<Stream> openStream,
            V12PakOptions? options = null,
            GameRoot? root = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pakName);
            ArgumentNullException.ThrowIfNull(openStream);

            options ??= new V12PakOptions();
            root ??= GameRoot.Instance
                ?? throw new InvalidOperationException("No active GameRoot. Pass a GameRoot instance to LoadFromStream().");

            return LoadCore(
                pakName,
                () => new V12PakReader(pakName, openStream),
                options,
                root,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private static V12PakLoadResult LoadCore(
            string pakPath,
            Func<V12PakReader> createReader,
            V12PakOptions options,
            GameRoot root,
            HashSet<string> chain)
        {
            if (!chain.Add(pakPath))
                throw new InvalidOperationException($"Circular .v12pak dependency detected at '{pakPath}'.");

            V12PakReader? reader = null;
            V12PakLoadResult? result = null;
            V12PakAssetResolver? resolver = null;
            IAssetResolver? previousResolver = null;
            try
            {
                reader = createReader();
                var manifest = reader.Manifest;
                Log.Information("V12Pak '{PakName}': extraction {State} at '{Directory}'",
                    Path.GetFileName(pakPath), reader.ReusedCache ? "cache hit" : "written", reader.TempDirectory);
                Log.Information("V12Pak '{PakName}': {Worlds} world(s), {Assets} asset(s), {Paks} pak DLL(s), {Scripts} script(s), {CSharp} C# source(s), {Deps} dependency paks",
                    Path.GetFileName(pakPath), manifest.Worlds.Count, manifest.Assets.Count, manifest.Paks.Count,
                    manifest.Scripts.Count, manifest.CSharpScripts.Count, manifest.Dependencies.Count);

                var allowedAssets = FilterAssets(manifest, options, out int skippedAssets);
                resolver = new V12PakAssetResolver(reader, allowedAssets);
                result = new V12PakLoadResult(reader.PakPath, manifest, reader, resolver) { SkippedAssets = skippedAssets };

                if (options.LoadDependencies)
                    LoadDependencies(reader, manifest, options, root, result, chain);

                // DLL and C# component types are registered before worlds parse so
                // this pak's own content can reference the components it ships.
                if (options.LoadDlls)
                {
                    LoadBinDependencies(reader);
                    LoadDlls(reader, manifest, result, root);
                }
                else
                    result.SkippedGamepaks += WarnSkippedDlls(pakPath, manifest);

                if (options.LoadCSharpScripts)
                    LoadCSharpScripts(reader, manifest, result, root);
                else
                    result.SkippedGamepaks += WarnSkippedCSharp(pakPath, manifest);

                // Native libraries are resolved lazily by the platform loader, so
                // the probe directories have to exist before any gamepak code runs
                // a P/Invoke.
                RegisterNativeProbeDirectories(reader.TempDirectory);

                // Publish the resolver BEFORE worlds parse. Components attach
                // during parsing and read their assets from the registry in
                // OnAttach (SkeletalManagerComponent imports its model right
                // there), so registering afterwards made a pak world fail with
                // "Model asset 'v12://Assets/...' did not resolve to a file".
                // Dependency assets stay reachable through the fallback chain.
                previousResolver = root.Registry.Get<IAssetResolver>("AssetResolver");
                resolver.Fallback = previousResolver;
                root.Registry.RegisterOrReplace("AssetResolver", resolver);

                if (options.AllowWorldLoading)
                    LoadWorlds(reader, manifest, resolver, result, root);

                if (options.LoadContractScripts)
                    LoadScripts(reader, manifest, result, root);
                else
                    result.SkippedGamepaks += WarnSkippedScripts(pakPath, manifest);

                LoadedPakSet(root).Add(pakPath);
                return result;
            }
            catch
            {
                // The load never completed, so do not leave its resolver active.
                if (resolver is not null &&
                    ReferenceEquals(root.Registry.Get<IAssetResolver>("AssetResolver"), resolver))
                {
                    if (previousResolver is not null)
                        root.Registry.RegisterOrReplace("AssetResolver", previousResolver);
                    else
                        root.Registry.Unregister("AssetResolver");
                }

                if (result != null) result.Dispose();
                else reader?.Dispose();
                throw;
            }
            finally
            {
                chain.Remove(pakPath);
            }
        }

        // ── Dependencies ────────────────────────────────────────────────

        private static void LoadDependencies(
            V12PakReader reader,
            V12PakManifest manifest,
            V12PakOptions options,
            GameRoot root,
            V12PakLoadResult result,
            HashSet<string> chain)
        {
            if (manifest.Dependencies.Count == 0) return;

            var loaded = LoadedPakSet(root);
            string pakName = Path.GetFileName(reader.PakPath);

            foreach (var dependency in manifest.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(dependency)) continue;

                string? resolved = ResolveDependencyPath(dependency, reader, options);
                if (resolved == null)
                {
                    result.SkippedGamepaks++;
                    Log.Warning("V12Pak '{PakName}': dependency '{Dependency}' not found " +
                                "(searched the pak directory, DependencySearchPaths and the working directory)",
                        pakName, dependency);
                    continue;
                }

                string fullPath = Path.GetFullPath(resolved);
                if (chain.Contains(fullPath))
                {
                    result.SkippedGamepaks++;
                    Log.Warning("V12Pak '{PakName}': circular dependency on '{Dependency}', skipping", pakName, dependency);
                    continue;
                }
                if (loaded.Contains(fullPath))
                {
                    Log.Debug("V12Pak '{PakName}': dependency '{Dependency}' is already loaded", pakName, dependency);
                    continue;
                }

                try
                {
                    var dependencyResult = LoadCore(
                        fullPath,
                        () => new V12PakReader(fullPath),
                        options,
                        root,
                        chain);
                    result.Dependencies.Add(dependencyResult);
                    Log.Information("V12Pak '{PakName}': loaded dependency '{Dependency}'", pakName, Path.GetFileName(fullPath));
                }
                catch (Exception ex)
                {
                    result.SkippedGamepaks++;
                    Log.Warning(ex, "V12Pak '{PakName}': dependency '{Dependency}' failed to load", pakName, dependency);
                }
            }
        }

        private static string? ResolveDependencyPath(string dependency, V12PakReader reader, V12PakOptions options)
        {
            // Paks embedded in this archive win: the builder packs
            // dependencies/*.v12pak in so a pak can ship everything it needs.
            foreach (var name in DependencyFileNames(dependency))
            {
                string normalized = name.Replace('\\', '/').TrimStart('/');
                string? embedded = reader.ResolveEntryPath(normalized);
                if (embedded != null && File.Exists(embedded)) return embedded;

                string extracted = Path.Combine(reader.TempDirectory, normalized.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(extracted)) return extracted;
            }

            foreach (var candidate in DependencyCandidates(dependency, reader.PakPath, options))
            {
                if (File.Exists(candidate)) return candidate;

                // Allow a search path that points at the containing directory.
                if (Directory.Exists(candidate))
                {
                    foreach (var name in DependencyFileNames(dependency))
                    {
                        var nested = Path.Combine(candidate, Path.GetFileName(name));
                        if (File.Exists(nested)) return nested;
                    }
                }
            }
            return null;
        }

        private static IEnumerable<string> DependencyCandidates(string dependency, string pakPath, V12PakOptions options)
        {
            // A stream-backed pak has no directory on disk; its embedded
            // dependencies/*.v12pak entries are checked before this fallback.
            string? pakDirectory = File.Exists(pakPath)
                ? Path.GetDirectoryName(Path.GetFullPath(pakPath))
                : null;

            foreach (var name in DependencyFileNames(dependency))
            {
                if (Path.IsPathRooted(name))
                {
                    yield return name;
                    continue;
                }

                if (pakDirectory is not null)
                    yield return Path.Combine(pakDirectory, name);

                foreach (var searchPath in options.DependencySearchPaths)
                {
                    if (string.IsNullOrWhiteSpace(searchPath)) continue;
                    yield return Path.Combine(searchPath, name);
                }

                yield return Path.Combine(Directory.GetCurrentDirectory(), name);
            }
        }

        private static IEnumerable<string> DependencyFileNames(string dependency)
        {
            yield return dependency;
            if (!dependency.EndsWith(".v12pak", StringComparison.OrdinalIgnoreCase))
                yield return dependency + ".v12pak";
        }

        private static HashSet<string> LoadedPakSet(GameRoot root)
        {
            var set = root.Registry.Get<HashSet<string>>(LoadedPaksKey);
            if (set != null) return set;

            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            root.Registry.Register(LoadedPaksKey, set);
            return set;
        }

        // ── Assets ──────────────────────────────────────────────────────

        private static Dictionary<string, string> FilterAssets(V12PakManifest manifest, V12PakOptions options, out int skipped)
        {
            skipped = 0;
            var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, path) in manifest.Assets)
            {
                string ext = Path.GetExtension(path);
                if (options.AllowedAssetExtensions != null && !options.AllowedAssetExtensions.Contains(ext))
                {
                    skipped++;
                    Log.Debug("V12Pak: asset '{Key}' blocked (extension '{Ext}' not allowed)", key, ext);
                    continue;
                }
                if (options.BlockedAssetKeys?.Contains(key) == true)
                {
                    skipped++;
                    Log.Debug("V12Pak: asset '{Key}' blocked (on blocklist)", key);
                    continue;
                }
                allowed[key] = path;
            }
            return allowed;
        }

        // ── Worlds ──────────────────────────────────────────────────────

        private static void LoadWorlds(V12PakReader reader, V12PakManifest manifest, V12PakAssetResolver resolver, V12PakLoadResult result, GameRoot root)
        {
            var templates = new WorldTemplateProvider();
            LoadTemplatesFromPak(reader, templates);

            foreach (var entry in manifest.Worlds)
            {
                if (string.IsNullOrEmpty(entry.Entry))
                {
                    Log.Warning("V12Pak: world '{Name}' has no entry path, skipping", entry.Name);
                    continue;
                }
                if (!reader.HasEntry(entry.Entry))
                {
                    Log.Warning("V12Pak: world '{Name}' entry '{Entry}' not found, skipping", entry.Name, entry.Entry);
                    continue;
                }

                string xml = reader.ReadEntryText(entry.Entry)!;
                var parser = new WorldMLParser { TemplateProvider = templates };
                var worldContents = parser.Parse(xml);

                var world = new World(worldContents.Name ?? entry.Name)
                {
                    MountPoint = entry.Name,
                    ExtractPath = reader.TempDirectory
                };
                world.AddElement(worldContents);

                root.Worlds.Add(world);
                result.LoadedWorlds.Add(world);
                resolver.Mount(entry.Name, reader.TempDirectory);

                Log.Information("V12Pak: loaded world '{World}' from '{Entry}'", world.WorldName, entry.Entry);
            }

            if (templates.TemplateNames.Any())
            {
                root.Registry.RegisterOrReplace("TemplateProvider", templates);
                Log.Information("V12Pak: registered {Count} template(s)", templates.TemplateNames.Count());
            }
        }

        private static void LoadTemplatesFromPak(V12PakReader reader, WorldTemplateProvider templates)
        {
            foreach (var name in reader.EntryNames)
            {
                if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.Contains("/templates/", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("templates/", StringComparison.OrdinalIgnoreCase)) continue;

                var doc = new XmlDocument();
                using (var stream = reader.OpenEntry(name))
                {
                    if (stream == null) continue;
                    doc.Load(stream);
                }
                templates.AddDocument(doc, Path.GetFileNameWithoutExtension(name));
            }
        }

        // ── Shared runtime dependencies (bin/) ──────────────────────────

        private static readonly object BinResolveGate = new();
        private static readonly List<string> BinProbeDirectories = new();
        private static bool BinResolveHooked;

        /// <summary>
        /// Loads the runtime dependencies shipped in the pak's <c>bin/</c> folder
        /// (e.g. V12.Basic.dll) before the gamepak DLLs, so their references bind.
        /// Assemblies already loaded in the process are reused, not loaded twice.
        /// </summary>
        private static void LoadBinDependencies(V12PakReader reader)
        {
            string binDir = Path.Combine(reader.TempDirectory, "bin");
            if (!Directory.Exists(binDir)) return;

            RegisterBinProbeDirectory(binDir);

            var nativeLibraries = new List<string>();
            foreach (var dll in Directory.GetFiles(binDir, "*.dll"))
            {
                string simpleName = Path.GetFileNameWithoutExtension(dll);
                if (FindLoadedAssembly(simpleName) != null) continue;

                // Native libraries can ship in bin/ too; Assembly.LoadFrom would
                // throw BadImageFormat. Leave them to the platform loader.
                if (!IsManagedAssembly(dll))
                {
                    nativeLibraries.Add(Path.GetFileName(dll));
                    Log.Debug("V12Pak: '{Dll}' is native; skipping assembly load", Path.GetFileName(dll));
                    continue;
                }

                try
                {
                    var assembly = LoadAssembly(dll);
                    V12LinkedAssemblies.Add(assembly);
                    Log.Information("V12Pak '{PakName}': loaded bin dependency '{Dll}'",
                        Path.GetFileName(reader.PakPath), Path.GetFileName(dll));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "V12Pak: failed to load bin dependency '{Dll}'", Path.GetFileName(dll));
                }
            }

            // Natives are resolved by the platform loader, not by us. Surface
            // them at Information level: a pak packed for another RID ships a
            // Windows assimp.dll / TracyClient.dll that Linux can never bind, and
            // the only previous trace was a Debug line.
            if (nativeLibraries.Count > 0)
            {
                if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
                {
                    // The mobile linker will not open a library from the
                    // extraction directory, so this is a packaging error, not a
                    // RID mismatch.
                    Log.Warning("V12Pak '{PakName}': {Count} native library(ies) in bin/ cannot be loaded on mobile " +
                                "({Names}); package them inside the app instead",
                        Path.GetFileName(reader.PakPath), nativeLibraries.Count, string.Join(", ", nativeLibraries));
                }
                else
                {
                    Log.Information("V12Pak '{PakName}': {Count} native library(ies) in bin/ left to the platform loader ({Names})",
                        Path.GetFileName(reader.PakPath), nativeLibraries.Count, string.Join(", ", nativeLibraries));
                }
            }
        }

        private static void RegisterBinProbeDirectory(string directory)
        {
            lock (BinResolveGate)
            {
                if (!BinProbeDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase))
                    BinProbeDirectories.Add(directory);

                if (BinResolveHooked) return;
                BinResolveHooked = true;
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromBinDirectories;
            }
        }

        // ── Native dependencies (bin/, paks/, runtimes/<rid>/native) ────

        private static readonly object NativeResolveGate = new();
        private static readonly List<string> NativeProbeDirectoryList = new();
        private static bool NativeResolveHooked;

        [ThreadStatic] private static bool _resolvingNative;

        /// <summary>
        /// Directories probed for native libraries shipped by loaded paks, in
        /// probe order: the pak root folders (<c>bin/</c>, <c>paks/</c>,
        /// <c>scripts/</c>), then the <c>runtimes/&lt;rid&gt;/native</c> folders that
        /// match the running platform.
        /// </summary>
        public static IReadOnlyList<string> NativeProbeDirectories
        {
            get
            {
                lock (NativeResolveGate) return NativeProbeDirectoryList.ToArray();
            }
        }

        /// <summary>
        /// Finds a native library a pak ships, for the current platform. Accepts
        /// the name a <c>DllImport</c> uses (<c>assimp</c>, <c>TracyClient</c>) or
        /// the file name (<c>libassimp.so</c>). Returns false when no pak-provided
        /// match exists.
        /// </summary>
        public static bool TryFindNativeLibrary(string libraryName, out string path)
        {
            path = string.Empty;

            try
            {
                foreach (string candidate in EnumerateNativeCandidates(libraryName))
                {
                    if (!File.Exists(candidate)) continue;

                    path = candidate;
                    return true;
                }
            }
            catch (IOException)
            {
                // A probe directory disappeared mid-scan (another process
                // pruning the cache); the platform loader still gets its turn.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return false;
        }

        /// <summary>
        /// Registers the native probe directories for one extracted pak and hooks
        /// the unmanaged resolver once per process. Managed assemblies already
        /// get an <see cref="AppDomain.AssemblyResolve"/> hook; native libraries
        /// need <see cref="AssemblyLoadContext.ResolvingUnmanagedDll"/>, which is
        /// what makes <c>assimp.dll</c>/<c>libassimp.so</c> in a pak findable on
        /// Linux and macOS.
        /// </summary>
        private static void RegisterNativeProbeDirectories(string extractionRoot)
        {
            // Android and iOS refuse to dlopen a library from app-writable
            // storage, so an extracted bin/ copy is unusable even though the
            // file is there. Native dependencies belong in the host's own
            // package (lib/<abi>/ on Android) instead of the pak.
            if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            {
                Log.Debug(
                    "V12Pak: skipping native probe directories on mobile; " +
                    "native libraries must ship with the host application.");
                return;
            }

            var directories = new List<string>();

            foreach (string folder in new[] { "bin", "paks", "scripts" })
            {
                string directory = Path.Combine(extractionRoot, folder);
                if (!Directory.Exists(directory)) continue;

                directories.Add(directory);
                directories.AddRange(PlatformNativeRidDirectories(directory));
            }

            if (directories.Count == 0) return;

            lock (NativeResolveGate)
            {
                foreach (string directory in directories)
                {
                    if (!NativeProbeDirectoryList.Contains(directory, StringComparer.OrdinalIgnoreCase))
                        NativeProbeDirectoryList.Add(directory);
                }

                if (NativeResolveHooked) return;
                NativeResolveHooked = true;

                AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNativeFromPakDirectories;

                var context = AssemblyLoadContext.GetLoadContext(typeof(V12PakLoader).Assembly);
                if (context is not null && !ReferenceEquals(context, AssemblyLoadContext.Default))
                    context.ResolvingUnmanagedDll += ResolveNativeFromPakDirectories;
            }
        }

        /// <summary>
        /// <c>runtimes/&lt;rid&gt;/native</c> folders under <paramref name="directory"/>
        /// that apply to this process. A portable (`--runtime any`) pak carries
        /// every RID, so the matching one is chosen here rather than at pack time.
        /// </summary>
        private static IEnumerable<string> PlatformNativeRidDirectories(string directory)
        {
            string runtimes = Path.Combine(directory, "runtimes");
            if (!Directory.Exists(runtimes)) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rid in PlatformRuntimeIdentifiers())
            {
                string native = Path.Combine(runtimes, rid, "native");
                if (Directory.Exists(native) && seen.Add(native))
                    yield return native;
            }

            // Odd RIDs (win10-x64, linux-musl-x64 on a glibc host, ...), but
            // never another architecture's native folder.
            string os = CurrentOsSegment();
            string arch = CurrentArchSegment();
            foreach (string candidate in Directory.EnumerateDirectories(runtimes))
            {
                string name = Path.GetFileName(candidate);
                bool isAny = name.Equals("any", StringComparison.OrdinalIgnoreCase);
                if (!isAny && !name.StartsWith(os + "-", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!isAny && arch.Length > 0 && !name.EndsWith("-" + arch, StringComparison.OrdinalIgnoreCase))
                    continue;

                string native = Path.Combine(candidate, "native");
                if (Directory.Exists(native) && seen.Add(native))
                    yield return native;
            }
        }

        private static IEnumerable<string> PlatformRuntimeIdentifiers()
        {
            string rid = RuntimeInformation.RuntimeIdentifier;
            if (!string.IsNullOrWhiteSpace(rid)) yield return rid;

            string os = CurrentOsSegment();
            string arch = CurrentArchSegment();

            if (arch.Length == 0)
            {
                yield return "any";
                yield break;
            }

            yield return $"{os}-{arch}";
            if (os == "linux" && !rid.Contains("musl", StringComparison.OrdinalIgnoreCase))
                yield return $"linux-musl-{arch}";
            yield return "any";
        }

        private static string CurrentOsSegment()
            => OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";

        private static string CurrentArchSegment()
            => RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                _ => string.Empty,
            };

        private static IEnumerable<string> EnumerateNativeCandidates(string libraryName)
        {
            if (string.IsNullOrWhiteSpace(libraryName)) yield break;

            string[] directories;
            lock (NativeResolveGate) directories = NativeProbeDirectoryList.ToArray();

            foreach (string directory in directories)
            {
                foreach (string candidate in CandidateNativePaths(directory, libraryName))
                    yield return candidate;
            }
        }

        private static IEnumerable<string> CandidateNativePaths(string directory, string libraryName)
        {
            if (!Directory.Exists(directory)) yield break;

            if (Path.IsPathRooted(libraryName))
            {
                yield return libraryName;
                yield break;
            }

            // A caller may pass the exact file name ("libassimp.so").
            if (Path.HasExtension(libraryName))
                yield return Path.Combine(directory, libraryName);

            // DllImport names are bare ("assimp"), so cover the platform's
            // decorated forms: assimp.dll, libassimp.so, TracyClient.so (no lib),
            // libassimp.dylib.
            string[] stems = libraryName.StartsWith("lib", StringComparison.Ordinal) && libraryName.Length > 3
                ? new[] { libraryName, libraryName[3..] }
                : new[] { libraryName, "lib" + libraryName };

            foreach (string stem in stems)
            {
                yield return Path.Combine(directory, stem + ".dll");
                yield return Path.Combine(directory, stem + ".so");
                yield return Path.Combine(directory, stem + ".dylib");
            }

            // Versioned sonames (libfoo.so.1).
            foreach (string stem in stems)
            {
                foreach (string file in Directory.EnumerateFiles(directory, stem + ".so.*"))
                    yield return file;
            }
        }

        private static IntPtr ResolveNativeFromPakDirectories(Assembly assembly, string libraryName)
        {
            if (_resolvingNative) return IntPtr.Zero;

            _resolvingNative = true;
            try
            {
                // Try every candidate rather than stopping at the first file that
                // exists: a wrong-architecture copy in bin/ must not shadow the
                // matching runtimes/<rid>/native library.
                foreach (string candidate in EnumerateNativeCandidates(libraryName))
                {
                    if (!File.Exists(candidate)) continue;
                    if (!NativeLibrary.TryLoad(candidate, out IntPtr handle) || handle == IntPtr.Zero)
                        continue;

                    Log.Information("V12Pak: native dependency '{Library}' resolved from pak ('{Path}')",
                        libraryName, candidate);
                    return handle;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "V12Pak: native dependency '{Library}' failed to load from pak", libraryName);
            }
            finally
            {
                _resolvingNative = false;
            }

            return IntPtr.Zero;
        }

        private static Assembly? ResolveFromBinDirectories(object? sender, ResolveEventArgs args)
        {
            string? simpleName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(simpleName)) return null;

            string[] directories;
            lock (BinResolveGate) directories = BinProbeDirectories.ToArray();

            foreach (var directory in directories)
            {
                string candidate = Path.Combine(directory, simpleName + ".dll");
                if (!File.Exists(candidate)) continue;

                return FindLoadedAssembly(simpleName) ?? LoadAssembly(candidate);
            }
            return null;
        }

        private static Assembly LoadAssembly(string path)
            => AssemblyLoader?.Invoke(path) ?? Assembly.LoadFrom(path);

        private static Assembly? FindLoadedAssembly(string simpleName)
            => AssemblyFinder?.Invoke(simpleName)
               ?? AppDomain.CurrentDomain.GetAssemblies()
                   .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));

        private static bool IsManagedAssembly(string path)
        {
            try
            {
                AssemblyName.GetAssemblyName(path);
                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        // ── DLL gamepaks ────────────────────────────────────────────────

        private static void LoadDlls(V12PakReader reader, V12PakManifest manifest, V12PakLoadResult result, GameRoot root)
        {
            string paksDir = Path.Combine(reader.TempDirectory, "paks");
            if (!Directory.Exists(paksDir))
            {
                if (manifest.Paks.Count > 0)
                    Log.Warning("V12Pak: {Count} gamepak DLL(s) declared but no paks/ directory found", manifest.Paks.Count);
                return;
            }

            int before = root.Gamepaks.Gamepaks.Count;
            root.Gamepaks.LoadFromDirectory(paksDir);
            result.LoadedGamepaks = root.Gamepaks.Gamepaks.Count - before;
            Log.Information("V12Pak: loaded {Count} gamepak(s)", result.LoadedGamepaks);

            // Link every pak dll into the script runtimes so the game's .ct/.orbt
            // entrypoints can call the pak's own C# code by CLR name (and
            // <AssemblyRef(Path: ...)> resolves at runtime). LoadFrom reuses the
            // gamepack loader's assembly context; V12LinkedAssemblies de-dups.
            // Runs before LoadScripts, so compile-time linking sees these too.
            var registry = CSharpComponentRegistry.GetOrCreate();
            string sourcePrefix = "pak:" + Path.GetFileName(reader.PakPath) + "/";

            foreach (var dll in Directory.GetFiles(paksDir, "*.dll"))
            {
                try
                {
                    var assembly = LoadAssembly(dll);
                    V12LinkedAssemblies.Add(assembly);
                    result.LoadedAssemblies.Add(assembly);
                    result.RegisteredCSharpTypes += registry.RegisterAssembly(assembly, sourcePrefix + Path.GetFileName(dll));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "V12Pak: failed to link pak dll '{Dll}' for scripts, skipping", Path.GetFileName(dll));
                }
            }
        }

        // ── C# component sources ────────────────────────────────────────

        private static void LoadCSharpScripts(V12PakReader reader, V12PakManifest manifest, V12PakLoadResult result, GameRoot root)
        {
            var sources = new List<string>();
            foreach (var entry in manifest.CSharpScripts)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                var path = reader.ResolveEntryPath(entry);
                if (path == null)
                {
                    result.SkippedGamepaks++;
                    Log.Warning("V12Pak: C# source '{Script}' not found in archive, skipping", entry);
                    continue;
                }
                sources.Add(path);
            }

            // Older manifests do not list C# sources: compile whatever ships under scripts/.
            if (sources.Count == 0)
            {
                string scriptsDir = Path.Combine(reader.TempDirectory, "scripts");
                if (Directory.Exists(scriptsDir))
                    sources.AddRange(Directory.EnumerateFiles(scriptsDir, "*" + CSharpScriptCompiler.Extension, SearchOption.AllDirectories));
            }

            if (sources.Count == 0) return;

            string pakName = Path.GetFileName(reader.PakPath);
            string assemblyName = "V12CSharp_" + Path.GetFileNameWithoutExtension(reader.PakPath);

            var compile = CSharpScriptCompiler.CompileFiles(sources, assemblyName);
            result.CSharpDiagnostics.AddRange(compile.Warnings);
            result.CSharpDiagnostics.AddRange(compile.Errors);
            if (!compile.Success || string.IsNullOrEmpty(compile.AssemblyPath))
            {
                result.SkippedGamepaks++;
                Log.Warning("V12Pak '{PakName}': C# component compilation failed:\n{Errors}", pakName, compile.ErrorText);
                return;
            }

            try
            {
                var context = new CSharpScriptLoadContext(assemblyName);
                var assembly = context.LoadFrom(compile.AssemblyPath);

                var registry = CSharpComponentRegistry.GetOrCreate();
                string sourceKey = "pak:" + assemblyName;
                registry.RemoveSource(sourceKey);
                int types = registry.RegisterAssembly(assembly, sourceKey);

                // Source-compiled gamepaks (IV12Gamepack) follow the same
                // lifecycle as DLL gamepaks: register now, the host starts the
                // new paks after GameRoot.Initialize().
                int gamepaks = root.Gamepaks.LoadFromAssembly(assembly);

                V12LinkedAssemblies.Add(assembly);
                CSharpScriptImages.Add(assembly, compile.Image);
                result.LoadedAssemblies.Add(assembly);
                result.LoadedCSharpGamepaks++;
                result.RegisteredCSharpTypes += types;
                result.LoadedGamepaks += gamepaks;

                Log.Information("V12Pak '{PakName}': compiled {Sources} C# source(s) into {Assembly} ({Types} component type(s), {Gamepaks} gamepak(s))",
                    pakName, sources.Count, assembly.GetName().Name, types, gamepaks);
            }
            catch (Exception ex)
            {
                result.SkippedGamepaks++;
                Log.Warning(ex, "V12Pak '{PakName}': failed to load compiled C# components", pakName);
            }
        }

        // ── Contract / ObjektRT script gamepaks ─────────────────────────

        /// <summary>
        /// Wrap each declared script entrypoint in the gamepack that serves its
        /// format — Contract (<c>.ct</c>) sources via <see cref="ContractGamepack"/>
        /// (compiled on start), precompiled ObjektRT modules
        /// (<c>.orbt</c>/<c>.oil</c>) via <see cref="ObjektRTGamepak"/> (loaded
        /// as-is) — and register it with the engine's
        /// <see cref="GamePak.GamepackLoader"/>. Scripts that fail to load are
        /// skipped with a warning (mirroring DLL loading behaviour). Runs after
        /// DLLs and C# sources are linked, so scripts can reference pak components.
        /// </summary>
        private static void LoadScripts(V12PakReader reader, V12PakManifest manifest, V12PakLoadResult result, GameRoot root)
        {
            foreach (var script in manifest.Scripts)
            {
                string? path = reader.ResolveEntryPath(script);
                if (path == null)
                {
                    result.SkippedGamepaks++;
                    Log.Warning("V12Pak: script '{Script}' not found in archive, skipping", script);
                    continue;
                }

                try
                {
                    var gamepack = CreateGamepack(path, Path.GetFileNameWithoutExtension(script));
                    root.Gamepaks.Add(gamepack);
                    result.LoadedGamepaks++;
                    Log.Information("V12Pak: registered {Kind} gamepak '{Name}' from '{Script}'",
                        gamepack is ObjektRTGamepak ? "ObjektRT" : "Contract", gamepack.Name, script);
                }
                catch (Exception ex)
                {
                    result.SkippedGamepaks++;
                    Log.Warning(ex, "V12Pak: script '{Script}' failed to load, skipping", script);
                }
            }
        }

        /// <summary>Creates the gamepack for one script entrypoint by format:
        /// compiled ObjektRT modules (<c>.orbt</c>/<c>.oil</c>/<c>.oir</c>) vs
        /// Contract <c>.ct</c> sources.</summary>
        private static IV12Gamepack CreateGamepack(string path, string name)
            => ObjektRTModuleLoader.IsCompiledScript(path)
                ? new ObjektRTGamepak(path, name)
                : new ContractGamepack(path, name);

        // ── Skip warnings ───────────────────────────────────────────────

        private static int WarnSkippedDlls(string pakPath, V12PakManifest manifest)
        {
            foreach (var dll in manifest.Paks)
            {
                Log.Warning("V12Pak '{PakName}': skipping gamepak DLL '{Dll}' (LoadDlls is false)",
                    Path.GetFileName(pakPath), dll);
            }
            return manifest.Paks.Count;
        }

        private static int WarnSkippedCSharp(string pakPath, V12PakManifest manifest)
        {
            foreach (var script in manifest.CSharpScripts)
            {
                Log.Warning("V12Pak '{PakName}': skipping C# source '{Script}' (LoadCSharpScripts is false)",
                    Path.GetFileName(pakPath), script);
            }
            return manifest.CSharpScripts.Count;
        }

        private static int WarnSkippedScripts(string pakPath, V12PakManifest manifest)
        {
            foreach (var script in manifest.Scripts)
            {
                Log.Warning("V12Pak '{PakName}': skipping Contract script '{Script}' (LoadContractScripts is false)",
                    Path.GetFileName(pakPath), script);
            }
            return manifest.Scripts.Count;
        }
    }
}
