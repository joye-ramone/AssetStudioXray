# AssetStudioXray

Command-line only fork of aelurum's [AssetStudioMod](https://github.com/aelurum/AssetStudio) (itself a modified version of Perfare's [AssetStudio](https://github.com/Perfare/AssetStudio)). The GUI is removed; the asset reading libraries and the upstream CLI are kept, plus a new console tool:

- **AssetStudioXray** (`AssetStudioXray/`): raw dumps of Unity models and animations for the X-Ray engine converters of the OGSR Engine: hierarchy with local transforms, meshes with skin weights and the true bind poses, materials and textures, Mecanim clips (streamed / dense / constant curves with their bindings and events), animator controllers and override controllers, MonoBehaviours with type trees, AudioClips as wav, as JSON + binary files. Built in catalog for Escape from Tarkov (bundle dependencies from the game manifest, item templates and weapon presets from an SPT server database): `weapon`, `hands`, `item`, `dump`, `list`. See [AssetStudioXray/README.md](AssetStudioXray/README.md).
- **AssetStudioModCLI** (`AssetStudioCLI/`): the upstream CLI, unchanged except for the `splitObjects` LOD / shadow filters and the export folder structure (see the history).

**Neither the repository, nor the tool, nor the author of the tool, nor the author of the modification is affiliated with, sponsored, or authorized by Unity Technologies or its affiliates, or by the developers of any game whose files it reads.** The tools only read game files; use them on files you are allowed to use.

## AssetStudio Features

- Support version:
  - 3.4 - 2023.2
- Support asset types:
  - **Texture2D**, **Texture2DArray** : convert to png, tga, jpeg, bmp, webp
  - **Sprite** : crop Texture2D to png, tga, jpeg, bmp, webp
  - **AudioClip** : mp3, ogg, wav, m4a, fsb. Support converting FSB file to WAV(PCM)
  - **Font** : ttf, otf
  - **Mesh** : obj
  - **TextAsset**
  - **Shader** (for Unity < 2021)
  - **MovieTexture**
  - **VideoClip**
  - **MonoBehaviour** : json
  - **Animator** : export to FBX file with bound AnimationClip (FBX export: `splitObjects` mode of the CLI)

## AssetStudioMod Features

- CLI version (for Windows, Linux, Mac)
   - `Animator` and `AnimationClip` assets are not supported in the CLI version (AssetStudioXray dumps them raw)
- Support of sprites with alpha mask
- Support of image export in WebP format
- Support of Live2D Cubism model export
   - Ported from aelurum's fork of Perfare's [UnityLive2DExtractor](https://github.com/aelurum/UnityLive2DExtractor)
- Support of swizzled Switch textures
    - Ported from nesrak1's [AssetStudio fork](https://github.com/nesrak1/AssetStudio/tree/switch-tex-deswizzle)
- Detecting bundles with UnityCN encryption
   - Detection only. If you want to open them, please use Razmoth's [Studio](https://github.com/RazTools/Studio)
- Bug fixes (See [CHANGELOG](CHANGELOG.md) for details)

## Requirements

- AssetStudioXray: Windows x64, [.NET Runtime 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
- AssetStudioModCLI
   - net472: [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472)
   - net8 / net9: [.NET Runtime 8.0 / 9.0](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows, Linux, Mac)

## AssetStudioXray Usage

```
AssetStudioXray <command> [arguments] [--game <Tarkov dir>] [--out <dir>] [--deps none|content|all] [--filter <text>] [--no-textures] [--clean]
  list <text>                    bundles and item templates whose path / name contains the text
  weapon <name|id> [--preset P]  weapon prefab + the mods of its default preset (or preset P)  -> <out>\weapons\<name>
  hands <name>                   hands bundle(s) (folder or file name) + the player skeleton    -> <out>\hands\<name>
  item <name|id>                 item prefab                                                    -> <out>\items\<name>
  dump <bundle> [<bundle> ...]   bundles by manifest key or a unique part of it                 -> <out>\dump\<name>
```

`--game` / `--out` default to the environment variables `TARKOV_DIR` / `XRAY_DUMP_DIR`. The output format is described in the header of `AssetStudioXray/XrayExporter.cs`.

## CLI Usage

You can read the CLI readme [here](AssetStudioCLI/ReadMe.md).

### Run

- Command-line: `AssetStudioModCLI <asset folder path>`
- Command-line for Portable versions (.NET 6+): `dotnet AssetStudioModCLI.dll <asset folder path>`

### Basic Samples

- Show a list with a number of assets of each type available for export
```
AssetStudioModCLI <asset folder path> -m info
```
- Export assets of all supported for export types
```
AssetStudioModCLI <asset folder path>
```
- Export assets of specific types
```
AssetStudioModCLI <asset folder path> -t tex2d
```
```
AssetStudioModCLI <asset folder path> -t tex2d,sprite,audio
```
- Export assets grouped by type
```
AssetStudioModCLI <asset folder path> -g type
```
- Export assets to a specified output folder
```
AssetStudioModCLI <asset folder path> -o <output folder path>
```
- Dump assets to a specified output folder
```
AssetStudioModCLI <asset folder path> -m dump -o <output folder path>
```
- Export Live2D Cubism models
```
AssetStudioModCLI <asset folder path> -m live2d
```
> When running in live2d mode you can only specify `-o`, `--log-level`, `--log-output`, `--l2d-motion-mode`, `--l2d-force-bezier`, `--export-asset-list`, `--unity-version` and `--assembly-folder` options.
Any other options will be ignored.
- Export all FBX objects (every model with its own FBX file)
```
AssetStudioModCLI <asset folder path> -m splitObjects
```
> When running in splitObjects mode you can only specify `-o`, `--log-level`, `--log-output`, `--export-asset-list`, `--image-format`, `--filter-by-name` and `--unity-version` options.
Any other options will be ignored.

### Advanced Samples
- Export image assets converted to webp format to a specified output folder
```
AssetStudioModCLI <asset folder path> -o <output folder path> -t sprite,tex2d --image-format webp
```
- Show the number of audio assets that have "voice" in their names
```
AssetStudioModCLI <asset folder path> -m info -t audio --filter-by-name voice
```
- Export audio assets that have "voice" in their names
```
AssetStudioModCLI <asset folder path> -t audio --filter-by-name voice
```
- Export audio assets that have "music" or "voice" in their names
```
AssetStudioModCLI <asset folder path> -t audio --filter-by-name music,voice
```
```
AssetStudioModCLI <asset folder path> -t audio --filter-by-name music --filter-by-name voice
```
- Export audio assets that have "char" in their names **or** containers
```
AssetStudioModCLI <asset folder path> -t audio --filter-by-text char
```
- Export audio assets that have "voice" in their names **and** "char" in their containers
```
AssetStudioModCLI <asset folder path> -t audio --filter-by-name voice --filter-by-container char
```
- Export FBX objects that have "model" or "scene" in their names and set the scale factor to 10
```
AssetStudioModCLI <asset folder path> -m splitObjects --filter-by-name model,scene --fbx-scale-factor 10
```
- Export MonoBehaviour assets that require an assembly folder to read and create a log file
```
AssetStudioModCLI <asset folder path> -t monobehaviour --assembly-folder <assembly folder path> --log-output both
```
- Export assets that require to specify a Unity version
```
AssetStudioModCLI <asset folder path> --unity-version 2017.4.39f1
```
- Load assets of all types and show them
```
AssetStudioModCLI <asset folder path> -m info --load-all
```
- Load assets of all types and dump Material assets
```
AssetStudioModCLI <asset folder path> -m dump -t material --load-all
```

### MonoBehaviour and Il2Cpp

MonoBehaviour assets need the game's assemblies (`--assembly-folder`, such as the `Managed` folder). For Il2Cpp games, first use [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) to generate dummy dlls, then pass the dummy dll folder.

## Build

- Visual Studio 2022 or newer (C++ and .NET desktop workloads), .NET 8 SDK (.NET 9 SDK for the net9 CLI)
- Native libraries first (x64 Release): `Texture2DDecoderNative` (needed by both tools), `AssetStudioFBXNative` (only for the CLI's FBX export; needs the [FBX SDK](https://aps.autodesk.com/developer/overview/fbx-sdk) 2020.3.7, installed to `C:\Program Files\Autodesk\FBX\FBX SDK\2020.3.7`)
- `dotnet build AssetStudioXray\AssetStudioXray.csproj -c Release -p:SolutionDir=<repo>\`
- `dotnet build AssetStudioCLI\AssetStudioCLI.csproj -c Release -f net8.0 -p:SolutionDir=<repo>\`

## Open source libraries used

### Texture2DDecoder
* [Ishotihadus/mikunyan](https://github.com/Ishotihadus/mikunyan)
* [BinomialLLC/crunch](https://github.com/BinomialLLC/crunch)
* [Unity-Technologies/crunch](https://github.com/Unity-Technologies/crunch/tree/unity)
