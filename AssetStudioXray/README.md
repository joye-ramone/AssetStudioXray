# AssetStudioXray

Raw Unity dumps of Escape from Tarkov weapons, hands and items with their animations, for the OGSR Engine X-Ray converters
(`tools\ogf\fbx_hud` in the OGSR repo: `unity_dump.py` reads the output). Uses the `AssetStudio` and `AssetStudioUtility` libraries
of this repo; no FBX. Reads the game files only.

```
dotnet build AssetStudioXray\AssetStudioXray.csproj -c Release
AssetStudioXray <command> [arguments] [--game <Tarkov dir>] [--mods <dir>] [--out <dir>] [--deps none|content|all] [--filter <text>] [--no-textures] [--no-audio] [--shaders [--shader-max N]] [--clean]
  list <text>                    bundles and item templates whose path / name contains the text
  weapon <name|id> [--preset P]  weapon prefab + the mods of its default preset (or preset P)  -> <out>\weapons\<name>
  hands <name>                   hands bundle(s) (folder or file name) + the player skeleton    -> <out>\hands\<name>
  item <name|id>                 item prefab                                                    -> <out>\items\<name>
  dump <bundle> [<bundle> ...]   bundles by manifest key or a unique part of it [--name N]      -> <out>\dump\<name>
  catalog <prefix> [...]         summary of every bundle group under assets/content/<prefix>     -> <out>\catalog\<group>.json
```

- Bundles and their dependencies come from `EscapeFromTarkov_Data\StreamingAssets\Windows\Windows.json`; item templates (prefab bundle)
  and weapon presets (default mods per slot) from the SPT database (`SPT_Data\Server\database` or, SPT 4.x,
  `SPT_Runtime\SPT_Data\database`: `templates\items.json`, `globals.json`).
- `--mods <dir>` (repeatable, or `TARKOV_MODS` with `;` between folders): SPT server mods, a folder of mods (`SPT_Runtime\user\mods`) or
  one mod folder. Each mod's `bundles.json` (`manifest`: `key`, `dependencyKeys`; files under `<mod>\bundles\<key>`) adds its bundles;
  a key the game (or an earlier mod, by folder name) has is replaced, as the SPT server does, so a game weapon dumped with `--mods`
  takes a mod's replacement bundle. Every `db\**\*.json` (UTF-8 BOM, comments and trailing commas allowed) adds item templates (WTT
  CommonLib `overrideProperties` / `itemTplToClone` / `locales`, plain `_props` templates) and presets (`weaponPresets` / `presets` of an
  item, preset files, `ItemPresets`); a mod preset with a game preset's id replaces it. A mod item's name is its override `Name`, else
  its prefab file (without `_container.bundle`), else its English name; lookups match the English name too, and an exact name the game
  and mod variants share picks the game's template (a variant by id). Mod bundle keys outside `assets/` (`Mods/<weapon>/...`, flat
  file names) count as content; a flat-key own bundle exports the flat-key dependencies of its mod. `list` shows the mod of each
  bundle and item; `manifest.json` gets `mods` and `bundle_mods` (loaded bundle -> mod). Without `--mods` the output is unchanged.
- `hands` also finds the mods' first-person sleeves in `fphands` folders.
- `--deps content` (default) loads the dependencies under `assets/content/` except audio and the shared generated animation sets
  (`weapons/animations/*`: the `_simple` / `_spirit` clips, hundreds of MB), and the small effect bundles under `assets/systems/effects/`
  (muzzle flash, smoke, heat haze: the weapons' muzzle effect materials); they resolve references (controllers using clips of other
  weapons, materials).
- Materials: `materials.json` lists the own materials and the ones own renderers (particle systems too) or exported behaviours use, with
  shader, textures (written to `textures\`), floats, ints, colors, keywords, queue and their users.
- `--shaders` also loads the game's `shaders` bundle (380 MB, about 2 s more) and writes the shaders of those materials:
  `shaders\<name>_<path id>.json` (properties, keywords, passes with render state, constant buffer layouts, programs with their keywords)
  and each D3D11 program as `.dxbc` plus its disassembly `.asm` (d3dcompiler_47.dll), headed by the constant buffer layout (`cb0[7].x
  _ShotVals`). Unity 2021+ shaders are read through their type tree (AssetStudio does not parse them). A shader with more than
  `--shader-max` programs (default 256, 0 = no limit) gets the list only: Tarkov's weapon surface shader has 16244 (214 MB).
- `catalog`: one JSON per group (`assets/content/<a>/<b>`, usable items one level deeper) with the group's bundles, models (renderers,
  vertices, bones, materials, textures), textures, animator controllers (states per layer with their clips), override controllers,
  clips (length, loop, events, animated transforms by kind: arms, fingers, camera, IK, item; names resolved through the loaded
  hierarchies, the avatars and the player skeleton) and MonoBehaviour counts per script. Nothing is exported; group files that
  exist are kept, so a stopped run resumes (`--clean` redoes them). `catalog hands weapons` covers every hands skin and first-person set.
- Exported: the objects of the own bundles and of the dependencies in their folders; plus the controllers and every clip the own
  controllers / override controllers use, wherever they live.
- Output format: the header of `XrayExporter.cs`. `manifest.json` lists what was asked, loaded, skipped and exported.
- `--game` / `--out` default to the environment variables `TARKOV_DIR` / `XRAY_DUMP_DIR`.
