using AssetStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AssetStudioXray
{
    // Tarkov asset dumps for the OGSR X-Ray converters (tools\ogf\fbx_hud in the OGSR repo): finds the bundles of a weapon / hands set / item by
    // name through the game's bundle manifest and the SPT database, loads them with their content dependencies and writes the raw dump
    // (XrayExporter.cs). Reads the game files only.
    internal static class Program
    {
        private const string Usage = @"AssetStudioXray <command> [arguments] [options]

commands:
  list <text>                    bundles and item templates whose path / name contains the text
  weapon <name|id> [--preset P]  weapon prefab + the mods of its default preset (or preset P)  -> <out>\weapons\<name>
  hands <name>                   hands bundle(s) (folder or file name) + the player skeleton    -> <out>\hands\<name>
  item <name|id>                 item prefab                                                    -> <out>\items\<name>
  dump <bundle> [<bundle> ...]   bundles by manifest key or a unique part of it                 -> <out>\dump\<name>
  catalog <prefix> [...]         a summary of every bundle group (assets/content/<a>/<b>) under assets/content/<prefix>:
                                 models, textures, controllers, clips with their animated bones -> <out>\catalog\<group>.json
                                 (existing group files are kept, so a run resumes; --clean redoes them)

options:
  --game <dir>       Tarkov install folder (default: environment TARKOV_DIR)
  --out <dir>        output root (default: environment XRAY_DUMP_DIR, else .\xray_dump)
  --deps <mode>      none | content (default: dependencies under assets/content/ without audio and shared animation sets, and the
                     effect bundles under assets/systems/effects/: muzzle flash, smoke, heat haze materials) | all
  --filter <text>    export only root objects whose name contains the text (repeatable)
  --no-textures      skip the png export
  --no-audio         don't load the audio dependencies (sound banks) and skip the wav export
  --shaders          load the game's `shaders` bundle (380 MB) and export the shaders of the exported materials: shaders\<name>.json
                     (properties, passes, render state, constant buffers) and every D3D11 program as .dxbc + disassembled .asm
  --shader-max <n>   with --shaders: write the programs of a shader only when it has at most n (default 256; 0 = all); bigger ones
                     (surface shaders with every lighting variant, 16244 for the weapon shader) get the program list only
  --managed <dir>    game assemblies for MonoBehaviours without a type tree (default: <game>\EscapeFromTarkov_Data\Managed; read with
                     Mono.Cecil, nothing is run)
  --clean            delete the output folder first
  --name <name>      dump: the output folder name (default: the first bundle's file name)
";

        private sealed class Options
        {
            public string Command;
            public List<string> Args = new List<string>();
            public string Game = Environment.GetEnvironmentVariable("TARKOV_DIR");
            public string Out = Environment.GetEnvironmentVariable("XRAY_DUMP_DIR") ?? Path.Combine(".", "xray_dump");
            public string Deps = "content";
            public string Preset;
            public List<string> Filters = new List<string>();
            public bool Textures = true;
            public bool Audio = true;
            public bool Shaders;
            public int ShaderMax = 256;
            public string Managed;
            public bool Clean;
            public string Name;
        }

        private sealed class ConsoleLogger : ILogger
        {
            public void Log(LoggerEvent e, string message, bool ignoreLevel)
            {
                if (e < LoggerEvent.Info && !ignoreLevel)
                    return;
                var w = e >= LoggerEvent.Warning ? Console.Error : Console.Out;
                w.WriteLine(e == LoggerEvent.Info ? message : $"[{e}] {message}");
            }
        }

        public static int Main(string[] argv)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Logger.Default = new ConsoleLogger();
            Options o;
            try
            {
                o = Parse(argv);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine();
                Console.Error.Write(Usage);
                return 2;
            }
            if (o == null)
            {
                Console.Write(Usage);
                return 0;
            }
            try
            {
                return Run(o);
            }
            catch (Exception e) when (e is FileNotFoundException || e is InvalidOperationException)
            {
                Console.Error.WriteLine($"error: {e.Message}");
                return 1;
            }
        }

        private static Options Parse(string[] argv)
        {
            if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
                return null;
            var o = new Options { Command = argv[0].ToLowerInvariant() };
            for (var i = 1; i < argv.Length; i++)
            {
                string Next() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{argv[i]} needs a value");
                switch (argv[i])
                {
                    case "--game": o.Game = Next(); break;
                    case "--out": case "-o": o.Out = Next(); break;
                    case "--deps": o.Deps = Next().ToLowerInvariant(); break;
                    case "--preset": o.Preset = Next(); break;
                    case "--filter": o.Filters.Add(Next()); break;
                    case "--no-textures": o.Textures = false; break;
                    case "--no-audio": o.Audio = false; break;
                    case "--shaders": o.Shaders = true; break;
                    case "--shader-max": o.ShaderMax = int.TryParse(Next(), out var sm) && sm >= 0 ? sm : throw new ArgumentException("--shader-max: a number >= 0"); break;
                    case "--managed": o.Managed = Next(); break;
                    case "--clean": o.Clean = true; break;
                    case "--name": o.Name = Next(); break;
                    default:
                        if (argv[i].StartsWith("--"))
                            throw new ArgumentException($"unknown option {argv[i]}");
                        o.Args.Add(argv[i]);
                        break;
                }
            }
            if (o.Deps is not ("none" or "content" or "all"))
                throw new ArgumentException($"--deps {o.Deps}: none, content or all");
            if (o.Command is not ("list" or "weapon" or "hands" or "item" or "dump" or "catalog"))
                throw new ArgumentException($"unknown command {o.Command}");
            if (o.Args.Count == 0)
                throw new ArgumentException($"{o.Command}: argument missing");
            if (string.IsNullOrEmpty(o.Game))
                throw new ArgumentException("the Tarkov install folder: --game <dir> or environment TARKOV_DIR");
            return o;
        }

        private static int Run(Options o)
        {
            var tk = new Tarkov(o.Game);
            var query = string.Join(" ", o.Args);
            if (o.Command == "list")
                return List(tk, query);
            if (o.Command == "catalog")
                return CatalogRun(tk, o);

            var own = new List<string>();
            var info = new Dictionary<string, object> { ["command"] = o.Command, ["query"] = query, ["game"] = o.Game, ["deps"] = o.Deps };
            string outName;
            switch (o.Command)
            {
                case "weapon":
                case "item":
                {
                    var item = tk.FindItem(query, o.Command == "weapon" ? "assets/content/weapons/" : null, out var cands);
                    if (item == null)
                        return Ambiguous(query, cands.Select(c => $"{c.Name}  ({c.Id}, {c.Prefab})"));
                    info["template"] = new { id = item.Id, name = item.Name, parent = item.Parent, prefab = item.Prefab };
                    own.Add(item.Prefab);
                    outName = item.Name;
                    if (o.Command == "weapon")
                    {
                        var preset = tk.Preset(item, o.Preset, out var presetName, out var available);
                        if (preset == null && o.Preset != null)
                            return Ambiguous(o.Preset, available);
                        if (preset != null)
                        {
                            info["preset"] = new { name = presetName, available, items = preset };
                            own.AddRange(preset.Where(p => p.Bundle != null).Select(p => p.Bundle));
                            Console.WriteLine($"preset {presetName}: {string.Join(", ", preset.Skip(1).Select(p => $"{p.Slot}={p.Name}"))}");
                        }
                        else
                        {
                            Console.WriteLine("no preset found: the weapon prefab only");
                        }
                    }
                    break;
                }
                case "hands":
                {
                    var hands = tk.Bundles.Keys.Where(k => k.StartsWith("assets/content/hands/", StringComparison.OrdinalIgnoreCase)).ToList();
                    string File(string k) => Path.GetFileNameWithoutExtension(k.Substring(k.LastIndexOf('/') + 1));
                    string Folder(string k) => k.Split('/')[3];
                    var byFile = hands.Where(k => File(k).Equals(query, StringComparison.OrdinalIgnoreCase) || File(k).Equals(query + ".skin", StringComparison.OrdinalIgnoreCase)).ToList();
                    var byFolder = hands.Where(k => Folder(k).Equals(query, StringComparison.OrdinalIgnoreCase) || Folder(k).Equals("hands_" + query, StringComparison.OrdinalIgnoreCase)).ToList();
                    var pick = byFile.Count > 0 ? byFile : byFolder;
                    if (pick.Count == 0)
                        return Ambiguous(query, hands.Where(k => k.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
                    own.AddRange(pick);
                    // the skin's bones are paths of the player body skeleton (Root_Joint/.../Base HumanRibcage/...), not part of the hands bundle
                    own.Add("assets/content/characters/character/skeleton.bundle");
                    outName = query;
                    break;
                }
                default:
                {
                    foreach (var a in o.Args)
                    {
                        if (tk.Bundles.ContainsKey(a))
                        {
                            own.Add(a);
                            continue;
                        }
                        var m = tk.Bundles.Keys.Where(k => k.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        if (m.Count != 1)
                            return Ambiguous(a, m);
                        own.Add(m[0]);
                    }
                    outName = o.Name ?? Path.GetFileNameWithoutExtension(own[0].Substring(own[0].LastIndexOf('/') + 1));
                    break;
                }
            }

            var skipped = new List<string>();
            var load = tk.Resolve(own, o.Deps, o.Audio, o.Shaders, skipped);
            // own bundles and the dependencies in their folders (textures, client_assets with the model and clips) are the ones exported;
            // the other dependencies only resolve references (base controllers, shared clips, materials)
            var ownDirs = own.Select(k => k.Substring(0, k.LastIndexOf('/') + 1)).Distinct().ToList();
            var primary = new HashSet<string>(load.Where(k => own.Contains(k, StringComparer.OrdinalIgnoreCase) || ownDirs.Any(d => k.StartsWith(d, StringComparison.OrdinalIgnoreCase))), StringComparer.OrdinalIgnoreCase);
            var missing = load.Where(k => !System.IO.File.Exists(tk.BundlePath(k))).ToList();
            foreach (var m in missing)
                Logger.Warning($"bundle file missing: {m}");
            load = load.Except(missing).ToList();

            var outDir = Path.Combine(o.Out, o.Command switch { "weapon" => "weapons", "item" => "items", _ => o.Command }, Safe(outName));
            if (o.Clean && Directory.Exists(outDir))
                Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            Console.WriteLine($"{own.Count} own bundles, {load.Count} to load ({primary.Count} exported), {skipped.Count} dependencies skipped (--deps {o.Deps})");

            var am = new AssetsManager();
            am.LoadFilesAndFolders(out _, load.Select(tk.BundlePath).ToList());
            if (am.assetsFileList.Count == 0)
            {
                Console.Error.WriteLine("error: nothing loaded");
                return 1;
            }
            var primaryPaths = new HashSet<string>(primary.Select(k => Path.GetFullPath(tk.BundlePath(k))), StringComparer.OrdinalIgnoreCase);
            bool IsPrimary(SerializedFile f) => primaryPaths.Contains(Path.GetFullPath(f.originalPath ?? f.fullName));
            string KeyOf(SerializedFile f) => Path.GetRelativePath(tk.BundleRoot, f.originalPath ?? f.fullName).Replace('\\', '/');

            info["own"] = own;
            info["loaded"] = load;
            info["exported"] = primary.ToList();
            info["skipped"] = skipped;
            info["missing"] = missing;
            info["unity"] = am.assetsFileList[0].version.ToString();
            var managed = o.Managed ?? Path.Combine(o.Game, "EscapeFromTarkov_Data", "Managed");
            var assemblies = new AssemblyLoader();
            if (Directory.Exists(managed))
                assemblies.Load(managed);
            else
                Logger.Warning($"no game assemblies in {managed}: MonoBehaviours without a type tree are skipped");
            var stats = XrayExporter.Run(am, outDir, IsPrimary, KeyOf, o.Filters, o.Textures, o.Audio, o.Shaders, o.ShaderMax, assemblies);
            info["stats"] = stats;
            System.IO.File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }), new UTF8Encoding(false));
            Console.WriteLine($"-> {Path.GetFullPath(outDir)}");
            return 0;
        }

        // one AssetsManager per group: the group's bundles, their dependencies (--deps) and the player skeleton (arm bone names)
        private static int CatalogRun(Tarkov tk, Options o)
        {
            const string skeleton = "assets/content/characters/character/skeleton.bundle";
            // a group per item folder: assets/content/<a>/<b>; usable items (meds, food, tools) one level deeper
            string GroupOf(string k)
            {
                var p = k.Split('/');
                var depth = p.Length > 5 && p[3].Equals("usable_items", StringComparison.OrdinalIgnoreCase) ? 5 : 4;
                return p.Length > depth ? string.Join("/", p.Take(depth)) : k;
            }
            var groups = tk.Bundles.Keys.Where(k => o.Args.Any(a => k.StartsWith("assets/content/" + a.Trim('/'), StringComparison.OrdinalIgnoreCase))).GroupBy(GroupOf, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var dir = Path.Combine(o.Out, "catalog");
            Directory.CreateDirectory(dir);
            Console.WriteLine($"{groups.Count} groups -> {Path.GetFullPath(dir)}");
            int done = 0, failed = 0;
            foreach (var g in groups)
            {
                var file = Path.Combine(dir, Safe(g.Key.Substring("assets/content/".Length).Replace("/", "__")) + ".json");
                if (!o.Clean && System.IO.File.Exists(file))
                    continue;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var own = g.ToList();
                var load = tk.Resolve(own, o.Deps, false, false, new List<string>());
                if (!load.Contains(skeleton, StringComparer.OrdinalIgnoreCase))
                    load.Add(skeleton);
                load = load.Where(k => System.IO.File.Exists(tk.BundlePath(k))).ToList();
                var ownPaths = new HashSet<string>(own.Select(k => Path.GetFullPath(tk.BundlePath(k))), StringComparer.OrdinalIgnoreCase);
                try
                {
                    var am = new AssetsManager();
                    am.LoadFilesAndFolders(out _, load.Select(tk.BundlePath).ToList());
                    var data = Catalog.Group(am, f => ownPaths.Contains(Path.GetFullPath(f.originalPath ?? f.fullName)));
                    var bundles = own.Select(k => new { key = k, size = System.IO.File.Exists(tk.BundlePath(k)) ? new FileInfo(tk.BundlePath(k)).Length : -1 }).ToArray();
                    Catalog.Write(file, new { group = g.Key, bundles, loaded = load.Count, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), data });
                    am.Clear();
                    done++;
                    Console.WriteLine($"[catalog] {g.Key}: {own.Count} own + {load.Count - own.Count} loaded, {sw.Elapsed.TotalSeconds:F1} s");
                }
                catch (Exception e)
                {
                    failed++;
                    Logger.Error($"[catalog] {g.Key}: {e}");
                }
                GC.Collect();
            }
            Console.WriteLine($"{done} groups written, {failed} failed, {groups.Count - done - failed} kept");
            return failed > 0 ? 1 : 0;
        }

        private static int List(Tarkov tk, string text)
        {
            foreach (var k in tk.Bundles.Keys.Where(k => k.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(k => k))
                Console.WriteLine($"bundle  {k}  ({tk.Bundles[k].Dependencies.Length} deps)");
            if (tk.HasDatabase)
            {
                foreach (var i in tk.Items.Values.Where(i => i.Prefab != null && i.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(i => i.Name))
                    Console.WriteLine($"item    {i.Name}  {i.Id}  {i.Prefab}");
            }
            return 0;
        }

        private static int Ambiguous(string query, IEnumerable<string> candidates)
        {
            var c = candidates.ToList();
            Console.Error.WriteLine(c.Count == 0 ? $"error: nothing matches '{query}'" : $"error: '{query}' matches {c.Count}, give one of:");
            foreach (var x in c.Take(40))
                Console.Error.WriteLine("  " + x);
            if (c.Count > 40)
                Console.Error.WriteLine($"  ... {c.Count - 40} more");
            return 1;
        }

        public static string Safe(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "noname", @"[\\/:*?""<>|\s]+", "_");
    }
}
