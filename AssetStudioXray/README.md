# AssetStudioXray

Raw Unity dumps of Escape from Tarkov weapons, hands and items with their animations, for the OGSR Engine X-Ray converters
(`tools\ogf\fbx_hud` in the OGSR repo: `unity_dump.py` reads the output). Uses the `AssetStudio` and `AssetStudioUtility` libraries
of this repo; no FBX. Reads the game files only.

```
dotnet build AssetStudioXray\AssetStudioXray.csproj -c Release
AssetStudioXray <command> [arguments] [--game <Tarkov dir>] [--out <dir>] [--deps none|content|all] [--filter <text>] [--no-textures] [--clean]
  list <text>                    bundles and item templates whose path / name contains the text
  weapon <name|id> [--preset P]  weapon prefab + the mods of its default preset (or preset P)  -> <out>\weapons\<name>
  hands <name>                   hands bundle(s) (folder or file name) + the player skeleton    -> <out>\hands\<name>
  item <name|id>                 item prefab                                                    -> <out>\items\<name>
  dump <bundle> [<bundle> ...]   bundles by manifest key or a unique part of it                 -> <out>\dump\<name>
```

- Bundles and their dependencies come from `EscapeFromTarkov_Data\StreamingAssets\Windows\Windows.json`; item templates (prefab bundle)
  and weapon presets (default mods per slot) from `SPT_Data\Server\database` (`templates\items.json`, `globals.json`).
- `--deps content` (default) loads the dependencies under `assets/content/` except audio and the shared third-person animation sets
  (`weapons/animations/*`, hundreds of MB); they resolve references (controllers using clips of other weapons, materials).
- Exported: the objects of the own bundles and of the dependencies in their folders; plus the controllers and every clip the own
  controllers / override controllers use, wherever they live.
- Output format: the header of `XrayExporter.cs`. `manifest.json` lists what was asked, loaded, skipped and exported.
- `--game` / `--out` default to the environment variables `TARKOV_DIR` / `XRAY_DUMP_DIR`.
