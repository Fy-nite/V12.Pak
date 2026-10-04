# V12.Pak

A `.v12pak` packaging and loading library for V12 games. A pak is a single **tar.gz**
archive (built with `System.Formats.Tar`, no external packages) that bundles worlds,
assets, and optional C# DLL gamepaks into one distributable file.

The V12 engine has no dependency on this library — reference `V12.Pak` from your
application (or the sample host) when you want pak support.

```csharp
using V12.Pak;

var loader = root.LoadPak("game.v12pak", V12PakOptions.FullTrust);
// ... keep `loader` alive while the pak's worlds/assets are in use ...
loader.Dispose(); // unloads (removes the extracted temp dir); loaded worlds remain
```

## Archive layout

```
game.v12pak
├── manifest.json              # index: worlds, asset map, DLL list
├── worlds/{WorldName}/
│   ├── world.xml              # WorldML scene definition
│   └── templates/*.xml        # XML element templates (optional)
├── assets/**                  # meshes, textures, audio, fonts, scripts, ...
└── paks/*.dll                 # C# DLL gamepaks (optional)
```

`manifest.json`:

```json
{
  "version": 1,
  "engine": "v12",
  "description": "My game pak",
  "author": "Me",
  "worlds": [
    { "name": "MyWorld", "entry": "worlds/MyWorld/world.xml", "templatesDir": "worlds/MyWorld/templates" }
  ],
  "assets": {
    "textures/crate.png": "assets/textures/crate.png"
  },
  "paks": ["paks/mygame.dll"]
}
```

Asset-map keys are `v12://`-resolvable virtual paths; values are paths inside the archive.

## Loading

`root.LoadPak(...)` is the one-call entry point (an extension on `GameRoot`):

```csharp
// Full trust: load worlds, assets, and DLL gamepaks
var loader = root.LoadPak("official_content.v12pak",
    new V12PakOptions { LoadDlls = true });

// Restricted: no DLLs, allowed asset types only
var loader = root.LoadPak("user_world.v12pak", new V12PakOptions
{
    LoadDlls = false,
    AllowedAssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".glb", ".gltf", ".obj", ".png", ".jpg"
    }
});
```

`LoadPak` opens the archive, mounts a `V12PakAssetResolver` on the active `AssetResolver`,
loads each declared world (parsing `world.xml` + templates into `GameRoot.Worlds`), and —
when enabled — loads bundled DLL gamepaks. Keep the returned `V12PakLoader` alive while the
pak is in use; `Dispose()` removes the extracted temp directory.

Loading is also available without a `GameRoot` via the `V12PakLoader` class directly:

```csharp
var loader = new V12PakLoader();
loader.Load("game.v12pak", V12PakOptions.FullTrust, loadWorlds: true, loadDlls: true);
foreach (var r in loader.Results) Console.WriteLine($"{r.Type}: {r.Message}");
```

## Building

From a directory mirroring the archive layout, `PackFromDirectory` scans for worlds
(`**/world.xml`), asset folders (`meshes/`, `textures/`, `scripts/`, `audio/`, `fonts/`),
and DLLs (`paks/*.dll`), and generates the manifest:

```csharp
var writer = new V12PakWriter("game.v12pak");
var manifest = writer.PackFromDirectory("./mygame_build");
writer.Finalize(manifest);

// or, on GameRoot:
root.BuildPak("./mygame_build", "game.v12pak");
```

For full control, build the manifest explicitly:

```csharp
var manifest = V12PakManifestBuilder.Create()
    .WithDescription("My game pak")
    .WithAuthor("Me")
    .AddWorld("Forest", "worlds/forest/world.xml", "worlds/forest/templates")
    .AddAsset("textures/crate.png", "assets/textures/crate.png")
    .AddAssetsFromDirectory("textures", "./textures")
    .AddPak("paks/MyGameplay.dll")
    .Build();

// Build + write in one step:
V12PakManifestBuilder.Create()
    .AddWorld("Forest", "worlds/forest/world.xml")
    .BuildAndWriteFromDirectory("game.v12pak", "./mygame_build");
```

## Low-level reader / writer

```csharp
using var reader = new V12PakReader("game.v12pak");
var manifest = reader.Manifest;
if (reader.HasEntry("assets/textures/crate.png"))
    using var png = reader.OpenEntry("assets/textures/crate.png");
string? path = reader.GetEntryPath("assets/meshes/hero.glb"); // real temp path for path-based loaders
foreach (var entry in reader.GetAllEntries()) Console.WriteLine(entry);

using var writer = new V12PakWriter("output.v12pak");
writer.AddFile("assets/textures/crate.png", pngBytes);
writer.AddFileFromDisk("assets/meshes/hero.glb", "./hero.glb");
writer.Finalize(manifest);
```

The reader materializes the archive to a temp directory on construction (gzip tar isn't
seekable and the engine's path-based APIs need real files); `Dispose()` removes it.

## Assets

`V12PakAssetResolver` implements `IAssetResolver`. Given a `v12://` URI it strips the mount
point, looks the asset key up in the manifest, applies `V12PakOptions` filtering, and returns
the extracted file path (or an open stream). An optional fallback resolver serves loose files
on disk.

## Options & security

| Scenario | `LoadDlls` | `AllowedAssetExtensions` |
|---|---|---|
| `V12PakOptions.FullTrust` | `true` | (all) |
| `V12PakOptions.Restricted` | `false` | `.xml .glb .gltf .obj .fbx .png .jpg .jpeg .bmp .tga .lua .json` |
| `V12PakOptions.WorldsOnly` | `false` | `.xml` |

`LoadDlls` is `false` by default because DLL gamepaks execute arbitrary code. Entry names are
sanitized on extraction — path-traversal (`..`) and rooted names are rejected. Additional
`BlockedAssetKeys` can be supplied to deny specific assets after the extension check.

## API

| Type | Purpose |
|---|---|
| `GameRootPakExtensions` | `root.LoadPak(...)`, `root.BuildPak(...)` |
| `V12PakLoader` | Open + load a pak; `Results`, `Dispose()` |
| `V12PakOptions` | `LoadDlls`, `AllowedAssetExtensions`, `BlockedAssetKeys`, `AllowWorldLoading`; `FullTrust`/`Restricted`/`WorldsOnly` presets |
| `V12PakReader` | Read/extract a pak; `Manifest`, `OpenEntry`, `HasEntry`, `GetEntryPath`, `GetAllEntries` |
| `V12PakWriter` | Write a pak; `AddFile`/`AddFileFromDisk`/`AddDirectory`/`PackFromDirectory`/`Finalize` |
| `V12PakManifest` / `WorldEntry` | Manifest model (`ToJson`/`FromJson`) |
| `V12PakManifestBuilder` | Fluent manifest construction |
| `V12PakAssetResolver` | `IAssetResolver` serving `v12://` assets from a pak |

## Building this repo

`V12.Pak.csproj` targets `net10.0` and references the V12 engine project (expected as a
sibling `..\V12-engine\V12.csproj`). Package id `Finite.V12.Pak`.
