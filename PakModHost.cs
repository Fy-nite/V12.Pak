using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using V12.Core;
using V12.Core.Modules;
using V12.Core.Mods;

namespace V12.Pak
{
    /// <summary>
    /// Mod host for <c>.v12pak</c> mods. A mod is an ordinary pak whose root
    /// contains <c>module.json</c>; everything else (worlds, assets, paks, bin,
    /// scripts) is the normal pak layout, so <c>vcsdk pack</c> builds mods with no
    /// extra command.
    ///
    /// <para>
    /// Loading order per mod: validate the manifest and dependencies, load the pak
    /// through <see cref="V12PakLoader"/> (content, scripts, DLLs), start any
    /// gamepaks the pak contributed, then feed the pak's assemblies to
    /// <see cref="ModuleHost"/> so <c>IV12Module</c> implementations start with the
    /// rest of the modules.
    /// </para>
    /// </summary>
    public sealed class PakModHost : IModHost, IDisposable
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly GameRoot _root;
        private readonly string? _settingsPath;
        private readonly List<ModInfo> _mods = new();
        private readonly List<V12PakLoadResult> _results = new();
        private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);

        public PakModHost(GameRoot root, string? settingsPath = null)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _settingsPath = settingsPath;
            LoadSettings();
        }

        public IReadOnlyList<ModInfo> Mods => _mods;

        public int EnableFromArgs(string[] args, string? baseDirectory = null)
        {
            ArgumentNullException.ThrowIfNull(args);

            if (ModuleArgs.HasFlag(args, "--no-mods"))
            {
                GameRoot.Log.Information("Mods disabled by --no-mods");
                return 0;
            }

            foreach (string id in ModuleArgs.Values(args, "--disable-mod", out _))
                if (!string.IsNullOrWhiteSpace(id))
                    _disabled.Add(id);

            string root = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppContext.BaseDirectory
                : baseDirectory;

            int loaded = 0;

            var directories = ModuleArgs.Values(args, "--mods", out bool modsFlag);
            if (modsFlag && directories.Count == 0)
                directories.Add("mods");

            foreach (string directory in directories)
                loaded += LoadDirectory(ModuleArgs.Resolve(root, directory), args);

            foreach (string pak in ModuleArgs.Values(args, "--mod", out _))
                if (LoadPak(ModuleArgs.Resolve(root, pak), args))
                    loaded++;

            if (loaded == 0 && (modsFlag || ModuleArgs.HasFlag(args, "--mod")))
                GameRoot.Log.Information("Mod flags were passed but no mods loaded");

            return loaded;
        }

        public int LoadDirectory(string directory, string[]? args = null)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                GameRoot.Log.Warning("Mod directory not found: '{Directory}'", directory);
                return 0;
            }

            int loaded = 0;
            foreach (string pak in Directory
                         .EnumerateFiles(directory, "*.v12pak", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (LoadPak(pak, args)) loaded++;
            }

            if (loaded == 0)
                GameRoot.Log.Information("No mod paks loaded from '{Directory}'", directory);

            return loaded;
        }

        public bool LoadPak(string pakPath, string[]? args = null)
        {
            if (string.IsNullOrWhiteSpace(pakPath)) return false;

            string fullPath;
            try { fullPath = Path.GetFullPath(pakPath); }
            catch (Exception ex)
            {
                GameRoot.Log.Warning(ex, "Mod path '{Path}' is not usable", pakPath);
                return false;
            }

            if (!File.Exists(fullPath))
            {
                GameRoot.Log.Warning("Mod pak not found: '{Path}'", fullPath);
                return false;
            }

            ModuleManifest? manifest;
            try
            {
                using var reader = new V12PakReader(fullPath);
                if (!reader.HasEntry(ModuleApi.ManifestFile))
                {
                    // Not every pak is a mod. A plain content pak in the mods
                    // folder is skipped rather than reported as broken.
                    GameRoot.Log.Debug("'{Pak}' has no {Manifest}; not a mod pak",
                        Path.GetFileName(fullPath), ModuleApi.ManifestFile);
                    return false;
                }

                string json = reader.ReadEntryText(ModuleApi.ManifestFile) ?? "";
                manifest = JsonSerializer.Deserialize<ModuleManifest>(json, Json);
            }
            catch (Exception ex)
            {
                GameRoot.Log.Error(ex, "Failed to read the mod manifest from '{Pak}'", fullPath);
                _mods.Add(new ModInfo
                {
                    Id = Path.GetFileNameWithoutExtension(fullPath),
                    PakPath = fullPath,
                    Loaded = false,
                    Error = ex.GetBaseException().Message,
                });
                return false;
            }

            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                _mods.Add(new ModInfo
                {
                    Id = Path.GetFileNameWithoutExtension(fullPath),
                    PakPath = fullPath,
                    Loaded = false,
                    Error = "module.json has no id",
                });
                return false;
            }

            string id = manifest.Id.Trim();

            if (_loaded.Contains(id))
            {
                _mods.Add(Info(manifest, fullPath, loaded: false, enabled: true, error: "already loaded"));
                return false;
            }

            if (_disabled.Contains(id))
            {
                _mods.Add(Info(manifest, fullPath, loaded: false, enabled: false, error: "disabled"));
                GameRoot.Log.Information("Mod '{Id}' is disabled", id);
                return false;
            }

            if (manifest.ApiVersion != ModuleApi.Version)
            {
                _mods.Add(Info(manifest, fullPath, loaded: false, enabled: true,
                    error: $"module.json apiVersion {manifest.ApiVersion} is not supported (expected {ModuleApi.Version})"));
                return false;
            }

            var missing = manifest.Dependencies
                .Where(dependency => _root.Modules.Find(dependency) is null && !_loaded.Contains(dependency))
                .ToList();

            if (missing.Count > 0)
            {
                _mods.Add(Info(manifest, fullPath, loaded: false, enabled: true,
                    error: "missing dependency: " + string.Join(", ", missing)));
                return false;
            }

            try
            {
                int gamepaksBefore = _root.Gamepaks.Gamepaks.Count;

                var result = V12PakLoader.Load(fullPath, new V12PakOptions
                {
                    LoadDlls = true,
                    LoadCSharpScripts = true,
                    LoadContractScripts = true,
                    AllowWorldLoading = true,
                }, _root);

                _results.Add(result);

                if (result.LoadedGamepaks > 0)
                    _root.Gamepaks.InitializeAndStartFrom(gamepaksBefore);

                int modules = 0;
                foreach (var assembly in result.LoadedAssemblies)
                    if (_root.Modules.LoadAssembly(assembly, args)) modules++;

                _loaded.Add(id);
                _mods.Add(Info(manifest, fullPath, loaded: true, enabled: true, error: null));

                GameRoot.Log.Information(
                    "Loaded mod '{Id}'{Version} from '{Pak}': {Worlds} world(s), {Gamepaks} gamepak(s), {Modules} module(s)",
                    id,
                    string.IsNullOrWhiteSpace(manifest.Version) ? "" : " " + manifest.Version,
                    Path.GetFileName(fullPath),
                    result.LoadedWorlds.Count,
                    result.LoadedGamepaks,
                    modules);

                return true;
            }
            catch (Exception ex)
            {
                _mods.Add(Info(manifest, fullPath, loaded: false, enabled: true, error: ex.GetBaseException().Message));
                GameRoot.Log.Error(ex, "Mod '{Id}' failed to load", id);
                return false;
            }
        }

        public bool SetEnabled(string id, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;

            if (enabled) _disabled.Remove(id);
            else _disabled.Add(id);

            SaveSettings();
            return true;
        }

        public string Describe()
        {
            if (_mods.Count == 0)
                return "no mods considered";

            var builder = new StringBuilder();
            foreach (var mod in _mods)
            {
                builder.Append(mod.Loaded ? "  [ok] " : "  [--] ").Append(mod.Id);

                if (!string.IsNullOrWhiteSpace(mod.Version))
                    builder.Append(' ').Append(mod.Version);

                if (!mod.Loaded && !string.IsNullOrWhiteSpace(mod.Error))
                    builder.Append(": ").Append(mod.Error);

                builder.AppendLine();
            }

            return builder.ToString();
        }

        public void Dispose()
        {
            for (int i = _results.Count - 1; i >= 0; i--)
            {
                try { _results[i].Dispose(); }
                catch (Exception ex) { GameRoot.Log.Warning(ex, "A mod pak failed to dispose"); }
            }

            _results.Clear();
        }

        private static ModInfo Info(ModuleManifest manifest, string pakPath, bool loaded, bool enabled, string? error) => new()
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            Author = manifest.Author,
            Description = manifest.Description,
            PakPath = pakPath,
            ApiVersion = manifest.ApiVersion,
            Enabled = enabled,
            Loaded = loaded,
            Error = error,
            Dependencies = manifest.Dependencies.ToArray(),
        };

        // ── settings ──────────────────────────────────────────────────────

        private sealed class ModSettings
        {
            public List<string> Disabled { get; set; } = new();
        }

        private string SettingsPath()
        {
            if (!string.IsNullOrWhiteSpace(_settingsPath))
                return _settingsPath;

            string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "v12");
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();

            return Path.Combine(root, "V12", string.IsNullOrWhiteSpace(name) ? "v12" : name, "mods.json");
        }

        private void LoadSettings()
        {
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return;

                var settings = JsonSerializer.Deserialize<ModSettings>(File.ReadAllText(path), Json);
                if (settings?.Disabled is null) return;

                foreach (string id in settings.Disabled)
                    if (!string.IsNullOrWhiteSpace(id))
                        _disabled.Add(id);
            }
            catch (Exception ex)
            {
                GameRoot.Log.Warning(ex, "Could not read mod settings");
            }
        }

        private void SaveSettings()
        {
            try
            {
                string path = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                File.WriteAllText(path, JsonSerializer.Serialize(new ModSettings
                {
                    Disabled = _disabled.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                GameRoot.Log.Warning(ex, "Could not write mod settings");
            }
        }
    }
}
