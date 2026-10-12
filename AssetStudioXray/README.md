# AssetStudioXray

Raw Unity dumps of Escape from Tarkov weapons, hands and items with their animations, for the OGSR Engine X-Ray converters
(`tools\ogf\fbx_hud` in the OGSR repo: `unity_dump.py` reads the output). Uses the `AssetStudio` and `AssetStudioUtility` libraries
of this repo; no FBX. Reads the game files only.

```
dotnet build AssetStudioXray\AssetStudioXray.csproj -c Release
AssetStudioXray <command> [arguments] [--game <Tarkov dir>] [--out <dir>] [--deps none|content|all] [--filter <text>] [--no-textures] [--no-audio] [--clean]
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
- `--deps content` (default) loads the dependencies under `assets/content/` except audio and the shared generated animation sets
  (`weapons/animations/*`: the `_simple` / `_spirit` clips, hundreds of MB); they resolve references (controllers using clips of other
  weapons, materials).
- `catalog`: one JSON per group (`assets/content/<a>/<b>`, usable items one level deeper) with the group's bundles, models (renderers,
  vertices, bones, materials, textures), textures, animator controllers (states per layer with their clips), override controllers,
  clips (length, loop, events, animated transforms by kind: arms, fingers, camera, IK, item; names resolved through the loaded
  hierarchies, the avatars and the player skeleton) and MonoBehaviour counts per script. Nothing is exported; group files that
  exist are kept, so a stopped run resumes (`--clean` redoes them). `catalog hands weapons` covers every hands skin and first-person set.
- Exported: the objects of the own bundles and of the dependencies in their folders; plus the controllers and every clip the own
  controllers / override controllers use, wherever they live.
- Output format: the header of `XrayExporter.cs`. `manifest.json` lists what was asked, loaded, skipped and exported.
- `--game` / `--out` default to the environment variables `TARKOV_DIR` / `XRAY_DUMP_DIR`.
