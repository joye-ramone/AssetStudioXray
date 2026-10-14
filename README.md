# AssetStudioXray

Command-line dump tool built on the asset reading libraries of aelurum's [AssetStudioMod](https://github.com/aelurum/AssetStudio) (itself a modified version of Perfare's [AssetStudio](https://github.com/Perfare/AssetStudio)). The GUI, the upstream CLI, FBX export, Live2D, shader and sprite export are removed, and so is every reader the tool does not use (web / zip / gzip / brotli containers, split files, UnityWeb / UnityRaw bundles, multi-bundle files, Zstd / Oodle / custom block compression, UnityCN detection, the pre-2021 shader parser, sprites, sprite atlases, fonts, videos, texture arrays, build / player settings, Switch texture deswizzling, image formats other than PNG); what is left is what the tool uses. The upstream history (changelog included) is in [aelurum/AssetStudio](https://github.com/aelurum/AssetStudio).

- **AssetStudioXray** (`AssetStudioXray/`): raw dumps of Unity models and animations for the X-Ray engine converters of the OGSR Engine: hierarchy with local transforms, meshes with skin weights and the true bind poses, materials and textures, Mecanim clips (streamed / dense / constant curves with their bindings and events), animator controllers and override controllers, MonoBehaviours with type trees, physics components (colliders, rigid bodies, joints, physic materials), AudioClips as wav, as JSON + binary files. Built in catalog for Escape from Tarkov (bundle dependencies from the game manifest, item templates and weapon presets from an SPT server database): `weapon`, `hands`, `item`, `dump`, `list`. See [AssetStudioXray/README.md](AssetStudioXray/README.md).
- **AssetStudio** (`AssetStudio/`): reading of UnityFS asset bundles (LZ4, LZMA or uncompressed), serialized files and the Unity classes the dumps use, type trees (from the files or built from the game assemblies). Shaders, fonts, sprites, videos and texture arrays are kept as raw objects (shaders read through their type tree) or by name only.
- **AssetStudioUtility** (`AssetStudioUtility/`): MonoBehaviour type trees from the game assemblies (Mono.Cecil), Texture2D -> png (Kyaru.Texture2DDecoder, ImageSharp), AudioClip -> wav (fmod).
- **AssetStudio.PInvoke** (`AssetStudio.PInvoke/`): native library loading (fmod).

**Neither the repository, nor the tool, nor the author of the tool, nor the author of the modification is affiliated with, sponsored, or authorized by Unity Technologies or its affiliates, or by the developers of any game whose files it reads.** The tool only reads game files; use it on files you are allowed to use.

## Requirements

Windows x64, [.NET Runtime 8.0](https://dotnet.microsoft.com/download/dotnet/8.0). UnityFS bundles (Unity 5.3+); checked on Escape from Tarkov (Unity 2022.3) and its SPT mods.

## Usage

```
AssetStudioXray <command> [arguments] [--game <Tarkov dir>] [--mods <SPT mods dir>] [--out <dir>] [--deps none|content|all] [--filter <text>] [--no-textures] [--clean]
  list <text>                    bundles and item templates whose path / name contains the text
  weapon <name|id> [--preset P]  weapon prefab + the mods of its default preset (or preset P)  -> <out>\weapons\<name>
  hands <name>                   hands bundle(s) (folder or file name) + the player skeleton    -> <out>\hands\<name>
  item <name|id>                 item prefab                                                    -> <out>\items\<name>
  dump <bundle> [<bundle> ...]   bundles by manifest key or a unique part of it                 -> <out>\dump\<name>
```

`--game` / `--out` default to the environment variables `TARKOV_DIR` / `XRAY_DUMP_DIR`. The output format is described in the header of `AssetStudioXray/XrayExporter.cs`.

## Build

.NET 8 SDK; managed only (the texture decoder's native dll comes with its NuGet package, `fmod.dll` is in `AssetStudioXray/Libraries`):

```
dotnet build AssetStudio.sln -c Release
```

Output: `AssetStudioXray/bin/Release/net8.0/AssetStudioXray.exe`.

## Open source libraries used

### Texture2DDecoder (the Kyaru.Texture2DDecoder package)
* [Ishotihadus/mikunyan](https://github.com/Ishotihadus/mikunyan)
* [BinomialLLC/crunch](https://github.com/BinomialLLC/crunch)
* [Unity-Technologies/crunch](https://github.com/Unity-Technologies/crunch/tree/unity)
