using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AssetStudioXray
{
    // The game side: the bundle manifest (StreamingAssets\Windows\Windows.json: bundle key -> dependencies) and, when present,
    // the SPT database (SPT_Data\Server\database or SPT_Runtime\SPT_Data\database: item templates with their prefab bundles, weapon presets with the default mods).
    // SPT server mods (--mods): each mod folder's bundles.json (manifest: key -> dependencyKeys, files under <mod>\bundles\<key>) is added to
    // the manifest, a mod key replacing the game's or an earlier mod's bundle of the same key as the SPT server does; the mod's db folder
    // (every *.json, the WTT CommonLib form and plain templates) adds item templates and weapon presets.
    internal sealed class Tarkov
    {
        public sealed class Bundle
        {
            public string Key;
            public string[] Dependencies;
            public string File;     // full path; null: the game's StreamingAssets\Windows\<key>
            public string Mod;      // the mod folder name, null for the game's bundles
        }

        public sealed class Item
        {
            public string Id;
            public string Name;
            public string Parent;
            public string Prefab;
            public string Title;    // mods: the English locale name
            public string Mod;
        }

        public sealed class PresetItem
        {
            public string Id;
            public string Tpl;
            public string Name;
            public string ParentId;
            public string Slot;
            public string Bundle;
        }

        private sealed class Mod
        {
            public string Name;
            public string Dir;
        }

        public readonly string GameDir;
        public readonly string BundleRoot;
        public readonly Dictionary<string, Bundle> Bundles = new Dictionary<string, Bundle>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Item> items;
        private Dictionary<string, JsonElement> presets;   // preset id -> preset; the game's first, a mod's preset of the same id replaces it
        private readonly string dbDir;
        private readonly List<Mod> mods = new List<Mod>();
        private readonly Dictionary<string, string> keyByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonDocumentOptions ModJson = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

        // mod files: trailing commas, comments and a UTF-8 BOM are common
        private static JsonDocument ParseModJson(string path)
        {
            var b = File.ReadAllBytes(path);
            var start = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            return JsonDocument.Parse(new ReadOnlyMemory<byte>(b, start, b.Length - start), ModJson);
        }

        public Tarkov(string gameDir, IEnumerable<string> modDirs = null)
        {
            GameDir = gameDir;
            BundleRoot = Path.Combine(gameDir, "EscapeFromTarkov_Data", "StreamingAssets", "Windows");
            var manifest = Path.Combine(BundleRoot, "Windows.json");
            if (!File.Exists(manifest))
                throw new FileNotFoundException($"bundle manifest not found: {manifest} (--game must be the Tarkov install folder)");
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(manifest)))
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var deps = p.Value.TryGetProperty("Dependencies", out var d) ? d.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>();
                    Bundles[p.Name] = new Bundle { Key = p.Name, Dependencies = deps };
                }
            }
            // SPT 3.x: SPT_Data\Server\database; SPT 4.x (server on .NET): SPT_Runtime\SPT_Data\database
            dbDir = new[] { Path.Combine(gameDir, "SPT_Runtime", "SPT_Data", "database"), Path.Combine(gameDir, "SPT_Data", "Server", "database") }.FirstOrDefault(d => File.Exists(Path.Combine(d, "templates", "items.json"))) ?? Path.Combine(gameDir, "SPT_Data", "Server", "database");
            foreach (var d in modDirs ?? Enumerable.Empty<string>())
                AddMods(d);
        }

        // a mod folder (has bundles.json or db) or a folder of mods (SPT user\mods): its subfolders in name order
        private void AddMods(string dir)
        {
            if (!Directory.Exists(dir))
                throw new FileNotFoundException($"mods folder not found: {dir}");
            bool IsMod(string d) => File.Exists(Path.Combine(d, "bundles.json")) || Directory.Exists(Path.Combine(d, "db"));
            var list = IsMod(dir) ? new[] { dir } : Directory.GetDirectories(dir).Where(IsMod).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).ToArray();
            int replacedGame = 0, replacedMod = 0, added = 0;
            foreach (var d in list)
            {
                var mod = new Mod { Name = Path.GetFileName(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar)), Dir = d };
                mods.Add(mod);
                var bj = Path.Combine(d, "bundles.json");
                if (!File.Exists(bj))
                    continue;
                using var doc = ParseModJson(bj);
                if (!doc.RootElement.TryGetProperty("manifest", out var man) || man.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var e in man.EnumerateArray())
                {
                    var key = e.GetProperty("key").GetString();
                    var deps = e.TryGetProperty("dependencyKeys", out var dk) && dk.ValueKind == JsonValueKind.Array ? dk.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>();
                    if (Bundles.TryGetValue(key, out var old))
                    {
                        if (old.Mod == null)
                            replacedGame++;
                        else
                        {
                            replacedMod++;
                            AssetStudio.Logger.Warning($"bundle {key}: mod {mod.Name} replaces mod {old.Mod}");
                        }
                    }
                    else
                    {
                        added++;
                    }
                    var file = Path.GetFullPath(Path.Combine(d, "bundles", key.Replace('/', Path.DirectorySeparatorChar)));
                    Bundles[key] = new Bundle { Key = key, Dependencies = deps, File = file, Mod = mod.Name };
                    keyByFile[file] = key;
                }
            }
            Console.WriteLine($"mods {dir}: {list.Length} mods, {added} bundles added, {replacedGame} game bundles replaced, {replacedMod} replaced by a later mod");
        }

        public string BundlePath(string key) => Bundles.TryGetValue(key, out var b) && b.File != null ? b.File : Path.Combine(BundleRoot, key.Replace('/', Path.DirectorySeparatorChar));

        // the manifest key of a loaded bundle file
        public string KeyOf(string file)
        {
            var full = Path.GetFullPath(file);
            return keyByFile.TryGetValue(full, out var k) ? k : Path.GetRelativePath(BundleRoot, full).Replace('\\', '/');
        }

        public string ModOf(string key) => key != null && Bundles.TryGetValue(key, out var b) ? b.Mod : null;

        public bool HasDatabase => File.Exists(Path.Combine(dbDir, "templates", "items.json"));

        public Dictionary<string, Item> Items
        {
            get
            {
                if (items != null)
                    return items;
                if (!HasDatabase && mods.Count == 0)
                    throw new InvalidOperationException($"no SPT database in {dbDir}: weapon / item lookups by name need it, use 'dump <bundle>' instead");
                items = new Dictionary<string, Item>();
                presets = new Dictionary<string, JsonElement>();
                if (HasDatabase)
                    LoadGameItems();
                LoadModDatabases();
                return items;
            }
        }

        private void LoadGameItems()
        {
            var gl = Path.Combine(dbDir, "globals.json");
            if (File.Exists(gl))
            {
                using var g = JsonDocument.Parse(File.ReadAllBytes(gl));
                foreach (var p in g.RootElement.GetProperty("ItemPresets").EnumerateObject())
                    presets[p.Name] = p.Value.Clone();
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dbDir, "templates", "items.json")));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value;
                items[p.Name] = new Item
                {
                    Id = p.Name,
                    Name = Str(v, "_name") ?? "",
                    Parent = Str(v, "_parent") ?? "",
                    Prefab = v.TryGetProperty("_props", out var props) ? PrefabOf(props) : null,
                };
            }
        }

        private static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string PrefabOf(JsonElement props)
        {
            if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty("Prefab", out var pf) && pf.ValueKind == JsonValueKind.Object)
            {
                var p = Str(pf, "path");
                return string.IsNullOrEmpty(p) ? null : p;
            }
            return null;
        }

        // the mods' item templates and presets, from every *.json under <mod>\db: the WTT CommonLib form
        //   { "<id>": { itemTplToClone, parentId, overrideProperties: { Prefab: { path }, Name, ... }, locales: { en: { name } }, presets: [ ... ] } }
        // plain templates ({ "<id>": { _id, _name, _parent, _props } }) and preset files ({ "<id>": { _type: "Preset", _items, _encyclopedia } })
        private void LoadModDatabases()
        {
            int added = 0, files = 0, failed = 0, modPresets = 0;
            foreach (var mod in mods)
            {
                var db = Path.Combine(mod.Dir, "db");
                if (!Directory.Exists(db))
                    continue;
                foreach (var f in Directory.EnumerateFiles(db, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    JsonDocument doc;
                    try
                    {
                        doc = ParseModJson(f);
                    }
                    catch (JsonException e)
                    {
                        failed++;
                        AssetStudio.Logger.Warning($"mod {mod.Name}: {Path.GetRelativePath(mod.Dir, f)}: {e.Message}");
                        continue;
                    }
                    files++;
                    using (doc)
                    {
                        if (doc.RootElement.ValueKind != JsonValueKind.Object)
                            continue;
                        bool AddPreset(string id, JsonElement x)
                        {
                            if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty("_items", out var xi) || xi.ValueKind != JsonValueKind.Array || xi.GetArrayLength() == 0)
                                return false;
                            presets[Str(x, "_id") ?? id] = x.Clone();
                            modPresets++;
                            return true;
                        }
                        foreach (var p in doc.RootElement.EnumerateObject())
                        {
                            var v = p.Value;
                            if (v.ValueKind != JsonValueKind.Object)
                                continue;
                            // a preset file: { "<id>": preset } or the globals form { "ItemPresets": { "<id>": preset } }
                            if (AddPreset(p.Name, v))
                                continue;
                            if (p.Name == "ItemPresets")
                            {
                                foreach (var x in v.EnumerateObject())
                                    AddPreset(x.Name, x.Value);
                                continue;
                            }
                            Item it = null;
                            if (v.TryGetProperty("overrideProperties", out var ov) || v.TryGetProperty("itemTplToClone", out _))
                            {
                                var clone = Str(v, "itemTplToClone");
                                items.TryGetValue(clone ?? "", out var baseItem);
                                var own = ov.ValueKind == JsonValueKind.Object ? PrefabOf(ov) : null;
                                var title = v.TryGetProperty("locales", out var loc) && loc.ValueKind == JsonValueKind.Object && loc.TryGetProperty("en", out var en) ? Str(en, "name") : null;
                                var prefab = own ?? baseItem?.Prefab;
                                // the name: the template's own Name, else the prefab file of its own bundle, else the English name
                                var name = Str(ov, "Name") ?? (own != null ? Stem(own) : null) ?? title ?? p.Name;
                                it = new Item { Id = p.Name, Name = name, Parent = Str(v, "parentId") ?? baseItem?.Parent ?? "", Prefab = prefab, Title = title, Mod = mod.Name };
                                // the item's presets (WTT: weaponPresets with addWeaponPreset)
                                foreach (var key in new[] { "weaponPresets", "presets" })
                                {
                                    if (v.TryGetProperty(key, out var pr) && pr.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var x in pr.EnumerateArray())
                                            AddPreset(p.Name, x);
                                    }
                                }
                            }
                            else if (v.TryGetProperty("_props", out var props) && v.TryGetProperty("_name", out _))
                            {
                                it = new Item { Id = p.Name, Name = Str(v, "_name") ?? p.Name, Parent = Str(v, "_parent") ?? "", Prefab = PrefabOf(props), Mod = mod.Name };
                            }
                            if (it != null)
                            {
                                items[p.Name] = it;
                                added++;
                            }
                        }
                    }
                }
            }
            if (mods.Count > 0)
                Console.WriteLine($"mod databases: {files} files, {added} item templates, {modPresets} presets{(failed > 0 ? $", {failed} files not parsed" : "")}");
        }

        private static string Stem(string prefab)
        {
            var f = prefab.Substring(prefab.LastIndexOf('/') + 1);
            f = f.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 7) : f;
            return f.EndsWith("_container", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 10) : f;
        }

        // template by id, exact name (or English name of a mod item), or a unique name part; with a prefix filter on the prefab path
        // (weapons: "assets/content/weapons/"; a mod's own bundle keys have any form, so mod items with a weapon template parent pass too)
        public Item FindItem(string query, string prefabPrefix, out List<Item> candidates)
        {
            bool Kind(Item i) => prefabPrefix == null || i.Prefab.StartsWith(prefabPrefix, StringComparison.OrdinalIgnoreCase) || (i.Mod != null && IsWeapon(i));
            bool Named(Item i, Func<string, bool> f) => f(i.Name) || (i.Title != null && f(i.Title));
            var all = Items.Values.Where(i => i.Prefab != null && Kind(i)).ToList();
            candidates = new List<Item>();
            if (Items.TryGetValue(query, out var byId))
                return byId;
            var exact = all.Where(i => Named(i, s => s.Equals(query, StringComparison.OrdinalIgnoreCase))).ToList();
            if (exact.Count == 1)
                return exact[0];
            // mod variants often keep the game template's name (WTT clones with an override Name): the game's template wins, a variant goes by id
            if (exact.Count(i => i.Mod == null) == 1)
                return exact.First(i => i.Mod == null);
            candidates = all.Where(i => Named(i, s => s.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(i => i.Name).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        // the template is under the Weapon node (5422acb9af1c889c16000029) of the template tree
        private bool IsWeapon(Item i)
        {
            for (var n = 0; i != null && n < 16; n++)
            {
                if (i.Id == "5422acb9af1c889c16000029")
                    return true;
                items.TryGetValue(i.Parent ?? "", out i);
            }
            return false;
        }

        // the items of a weapon preset: the default one (_encyclopedia = the weapon) or one named by --preset; the game's (globals.json
        // ItemPresets) and the mods' presets
        public List<PresetItem> Preset(Item weapon, string presetQuery, out string presetName, out List<string> available)
        {
            presetName = null;
            available = new List<string>();
            _ = Items;
            JsonElement? pick = null;
            foreach (var (pid, v) in presets)
            {
                var its = v.GetProperty("_items");
                if (its.GetArrayLength() == 0 || Str(its[0], "_tpl") != weapon.Id)
                    continue;
                var name = Str(v, "_name") ?? pid;
                available.Add(name);
                var isDefault = Str(v, "_encyclopedia") == weapon.Id;
                if (presetQuery == null ? isDefault : (name.Equals(presetQuery, StringComparison.OrdinalIgnoreCase) || pid == presetQuery))
                {
                    pick = v;
                    presetName = name;
                }
            }
            if (pick == null)
                return null;
            var list = new List<PresetItem>();
            foreach (var it in pick.Value.GetProperty("_items").EnumerateArray())
            {
                var tpl = it.GetProperty("_tpl").GetString();
                Items.TryGetValue(tpl, out var t);
                list.Add(new PresetItem
                {
                    Id = it.GetProperty("_id").GetString(),
                    Tpl = tpl,
                    Name = t?.Name,
                    ParentId = it.TryGetProperty("parentId", out var par) ? par.GetString() : null,
                    Slot = it.TryGetProperty("slotId", out var sid) ? sid.GetString() : null,
                    Bundle = t?.Prefab,
                });
            }
            return list;
        }

        // the bundles to load for a set of own bundles: dependencies followed recursively, filtered by the mode
        //   none     only the own bundles
        //   content  + dependencies under assets/content/ except the shared weapon animation sets (hundreds of MB), and the effect
        //            bundles under assets/systems/effects/ (at most 2.5 MB: muzzle flash, smoke and heat haze materials of the weapons)
        //   all      + every dependency except shaders and cubemaps (no meshes or clips there)
        // audio: the audio dependencies too (the weapon's sound banks; XrayExporter writes the AudioClips an own object reaches)
        // shaders: the `shaders` bundle too, in every mode but none (XrayExporter writes the shaders of the exported materials)
        // mods: a mod bundle whose key is not under assets/ (WTT "Mods/<weapon>/...", flat file names) counts as content
        public List<string> Resolve(IEnumerable<string> own, string mode, bool audio, bool shaders, List<string> skipped)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            foreach (var o in own)
            {
                if (seen.Add(o))
                {
                    result.Add(o);
                    queue.Enqueue(o);
                }
            }
            while (queue.Count > 0)
            {
                var k = queue.Dequeue();
                if (!Bundles.TryGetValue(k, out var b))
                    continue;
                foreach (var d in b.Dependencies)
                {
                    if (!seen.Add(d))
                        continue;
                    if (!Wanted(d, mode, audio, shaders))
                    {
                        skipped.Add(d);
                        continue;
                    }
                    result.Add(d);
                    queue.Enqueue(d);
                }
            }
            return result;
        }

        private bool Wanted(string key, string mode, bool audio, bool shaders)
        {
            if (mode == "none")
                return false;
            var k = key.ToLowerInvariant();
            if (!k.StartsWith("assets/") && k is not ("shaders" or "cubemaps") && ModOf(key) != null)
                return true;
            if (k.StartsWith("assets/content/audio/"))
                return audio;
            if (k == "shaders")
                return shaders;
            if (k == "cubemaps")
                return false;
            if (mode == "all")
                return true;
            return (k.StartsWith("assets/content/") && !k.StartsWith("assets/content/weapons/animations/")) || k.StartsWith("assets/systems/effects/");
        }
    }
}
