using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AssetStudioXray
{
    // The game side: the bundle manifest (StreamingAssets\Windows\Windows.json: bundle key -> dependencies) and, when present,
    // the SPT database (SPT_Data\Server\database or SPT_Runtime\SPT_Data\database: item templates with their prefab bundles, weapon presets with the default mods).
    internal sealed class Tarkov
    {
        public sealed class Bundle
        {
            public string Key;
            public string[] Dependencies;
        }

        public sealed class Item
        {
            public string Id;
            public string Name;
            public string Parent;
            public string Prefab;
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

        public readonly string GameDir;
        public readonly string BundleRoot;
        public readonly Dictionary<string, Bundle> Bundles = new Dictionary<string, Bundle>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Item> items;
        private JsonElement presets;
        private readonly string dbDir;

        public Tarkov(string gameDir)
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
        }

        public string BundlePath(string key) => Path.Combine(BundleRoot, key.Replace('/', Path.DirectorySeparatorChar));

        public bool HasDatabase => File.Exists(Path.Combine(dbDir, "templates", "items.json"));

        public Dictionary<string, Item> Items
        {
            get
            {
                if (items != null)
                    return items;
                if (!HasDatabase)
                    throw new InvalidOperationException($"no SPT database in {dbDir}: weapon / item lookups by name need it, use 'dump <bundle>' instead");
                items = new Dictionary<string, Item>();
                using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dbDir, "templates", "items.json")));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var v = p.Value;
                    string prefab = null;
                    if (v.TryGetProperty("_props", out var props) && props.TryGetProperty("Prefab", out var pf) && pf.ValueKind == JsonValueKind.Object && pf.TryGetProperty("path", out var pp))
                        prefab = pp.GetString();
                    items[p.Name] = new Item
                    {
                        Id = p.Name,
                        Name = v.TryGetProperty("_name", out var n) ? n.GetString() : "",
                        Parent = v.TryGetProperty("_parent", out var pa) ? pa.GetString() : "",
                        Prefab = string.IsNullOrEmpty(prefab) ? null : prefab,
                    };
                }
                return items;
            }
        }

        // template by id, exact name, or a unique name part; with a prefix filter on the prefab path (weapons: "assets/content/weapons/")
        public Item FindItem(string query, string prefabPrefix, out List<Item> candidates)
        {
            var all = Items.Values.Where(i => i.Prefab != null && (prefabPrefix == null || i.Prefab.StartsWith(prefabPrefix, StringComparison.OrdinalIgnoreCase))).ToList();
            candidates = new List<Item>();
            if (Items.TryGetValue(query, out var byId))
                return byId;
            var exact = all.Where(i => i.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1)
                return exact[0];
            candidates = all.Where(i => i.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(i => i.Name).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        // the items of a weapon preset: the default one (_encyclopedia = the weapon) or one named by --preset
        public List<PresetItem> Preset(Item weapon, string presetQuery, out string presetName, out List<string> available)
        {
            presetName = null;
            available = new List<string>();
            var gl = Path.Combine(dbDir, "globals.json");
            if (!File.Exists(gl))
                return null;
            if (presets.ValueKind == JsonValueKind.Undefined)
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(gl));
                presets = doc.RootElement.GetProperty("ItemPresets").Clone();
            }
            JsonElement? pick = null;
            foreach (var p in presets.EnumerateObject())
            {
                var v = p.Value;
                var its = v.GetProperty("_items");
                if (its.GetArrayLength() == 0 || its[0].GetProperty("_tpl").GetString() != weapon.Id)
                    continue;
                var name = v.TryGetProperty("_name", out var n) ? n.GetString() : p.Name;
                available.Add(name);
                var isDefault = v.TryGetProperty("_encyclopedia", out var enc) && enc.GetString() == weapon.Id;
                if (presetQuery == null ? isDefault : (name.Equals(presetQuery, StringComparison.OrdinalIgnoreCase) || p.Name == presetQuery))
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
                    ParentId = it.TryGetProperty("parentId", out var pid) ? pid.GetString() : null,
                    Slot = it.TryGetProperty("slotId", out var sid) ? sid.GetString() : null,
                    Bundle = t?.Prefab,
                });
            }
            return list;
        }

        // the bundles to load for a set of own bundles: dependencies followed recursively, filtered by the mode
        //   none     only the own bundles
        //   content  + dependencies under assets/content/ except the shared weapon animation sets (hundreds of MB)
        //   all      + every dependency except shaders and cubemaps (no meshes or clips there)
        // audio: the audio dependencies too (the weapon's sound banks; XrayExporter writes the AudioClips an own object reaches)
        public List<string> Resolve(IEnumerable<string> own, string mode, bool audio, List<string> skipped)
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
                    if (!Wanted(d, mode, audio))
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

        private static bool Wanted(string key, string mode, bool audio)
        {
            if (mode == "none")
                return false;
            var k = key.ToLowerInvariant();
            if (k.StartsWith("assets/content/audio/"))
                return audio;
            if (k == "shaders" || k == "cubemaps")
                return false;
            if (mode == "all")
                return true;
            return k.StartsWith("assets/content/") && !k.StartsWith("assets/content/weapons/animations/");
        }
    }
}
