using AssetStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AssetStudioXray
{
    // Raw dump for the OGSR X-Ray converters: Unity data as stored, no FBX, no axis conversion. Exported: the objects of the own bundles
    // (isPrimary); the other loaded bundles only resolve references, except clips / controllers the own controllers use.
    //   manifest.json                       what was asked and loaded (Program.cs)
    //   objects.tsv                         every loaded object: bundle, file, path id, class, name, container
    //   models\<root>\model.json + .bin     hierarchy (local T / R quaternion xyzw / S), renderers, meshes (decoded vertex streams, per sub-mesh
    //                                       indices, skin: 4 bone indices + weights, bind poses, bone name hashes), materials, animators
    //   textures\<name>.png                 textures used by the exported materials (flipped like the normal export: row 0 = top)
    //   clips\<name>.json + .bin            per clip: raw streamed (Hermite coefficients per key), dense and constant curves with their bindings
    //                                       (path hash = CRC32 of the transform path, attribute 1 T, 2 R quaternion, 3 S, 4 Euler degrees), events, timing
    //   controllers\<name>.json             animator controller: layers, states (name, clip(s), speed, loop, tag), transitions, string table
    //   overrides\<name>.json               animator override controller: base controller, original -> override clip pairs
    //   avatars\<name>.json                 path hash -> path table, skeleton parents, poses
    //   behaviours\<class>_<path id>.json   MonoBehaviours: the own ones (state machine behaviours with the game's animation events, sound
    //                                       players, prefab data) and those they reach in own or audio bundles (sound banks); fields from
    //                                       the type tree or the game assemblies; a reference is {"ref": "<class>:<name>", "id": "<class>_<path id>"}
    //   physics.json                        own colliders, rigid bodies, joints and physic materials: type tree values, root and transform
    //                                       path of the game object (Tarkov: a BoxCollider per weapon part on its LOD1 node, used for loot)
    //   materials.json                      own materials and those own renderers (particle systems too) or exported behaviours use (a muzzle
    //                                       manager's jet material): shader, textures, floats, ints, colors, keywords, queue, used_by
    //   shaders\<name>_<path id>.json       the shaders of those materials when loaded (--shaders): properties, keywords, passes with render
    //                                       state, constant buffer layouts, programs; shaders\<name>_<path id>\*.dxbc + .asm up to --shader-max
    //                                       programs per shader (ShaderExport.cs)
    //   meshes\<name>_<path id>.json + .bin own meshes that exported behaviours reference but no exported renderer draws (Tarkov's
    //                                       ScopeReticle.Mesh: a scope's reticle drawn into its optic camera): the model.json mesh layout plus
    //                                       blend shape frames (weight, vertex indices, position deltas), used_by the behaviour ids
    //   audio\<name>.wav                    AudioClips the own objects reach (own bundles and the sound banks)
    //   text\<name>.bytes                   own TextAssets (raw)
    // Binary layout: every array is a little-endian block in the .bin of its json, referenced as {"offset", "count", "type"} (f32, i32, u32).
    // Matrices are row-major (row 0 first), Unity column-vector convention (p' = M p).
    internal static partial class XrayExporter
    {
        // non-finite floats (a material's _CameraFadeParams = Infinity, the EOTech Vudu / S&B PM II scopes) as "Infinity" / "NaN" strings:
        // System.Text.Json throws on them by default and the whole dump stopped
        private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

        private sealed class BinWriter : IDisposable
        {
            private readonly BinaryWriter w;
            public BinWriter(string path) { w = new BinaryWriter(File.Create(path)); }
            public object F(IEnumerable<float> a) { var off = w.BaseStream.Position; var n = 0; foreach (var v in a) { w.Write(v); n++; } return new { offset = off, count = n, type = "f32" }; }
            public object I(IEnumerable<int> a) { var off = w.BaseStream.Position; var n = 0; foreach (var v in a) { w.Write(v); n++; } return new { offset = off, count = n, type = "i32" }; }
            public object U(IEnumerable<uint> a) { var off = w.BaseStream.Position; var n = 0; foreach (var v in a) { w.Write(v); n++; } return new { offset = off, count = n, type = "u32" }; }
            public void Dispose() { w.Dispose(); }
        }

        private static string Safe(string s) => Regex.Replace(s ?? "noname", @"[\\/:*?""<>|\s]+", "_");

        private static void WriteJson(string path, object o) => File.WriteAllText(path, JsonSerializer.Serialize(o, jsonOptions), new UTF8Encoding(false));

        public static object Run(AssetsManager assetsManager, string outDir, Func<SerializedFile, bool> isPrimary, Func<SerializedFile, string> bundleKey, List<string> filters, bool exportTextures, bool exportAudio, bool exportShaders, int maxShaderPrograms, AssemblyLoader assemblies)
        {
            Directory.CreateDirectory(outDir);
            bool Match(string name) => filters.Count == 0 || filters.Any(f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            bool Own(AssetStudio.Object o) => isPrimary(o.assetsFile);

            var all = assetsManager.assetsFileList.SelectMany(f => f.Objects).ToList();
            var containers = new Dictionary<AssetStudio.Object, string>();
            foreach (var bundle in all.OfType<AssetBundle>())
            {
                var preload = bundle.m_PreloadTable;
                foreach (var c in bundle.m_Container)
                {
                    for (var k = c.Value.preloadIndex; k < c.Value.preloadIndex + c.Value.preloadSize && k < preload.Length; k++)
                    {
                        if (preload[k].TryGet(out var obj))
                            containers[obj] = c.Key;
                    }
                }
            }
            using (var sw = new StreamWriter(Path.Combine(outDir, "objects.tsv"), false, new UTF8Encoding(false)))
            {
                sw.WriteLine("bundle\town\tfile\tpath_id\tclass\tname\tcontainer");
                foreach (var o in all)
                {
                    var name = o is NamedObject no ? no.m_Name : o is GameObject go ? go.m_Name : o is Component cp && cp.m_GameObject.TryGet(out var g) ? g.m_Name : "";
                    containers.TryGetValue(o, out var cont);
                    sw.WriteLine($"{bundleKey(o.assetsFile)}\t{(Own(o) ? 1 : 0)}\t{o.assetsFile.fileName}\t{o.m_PathID}\t{o.type}\t{name}\t{cont}");
                }
            }
            Logger.Info($"[xray] objects.tsv: {all.Count} objects");

            void Each<T>(IEnumerable<T> list, string what, Action<T> f) where T : AssetStudio.Object
            {
                foreach (var x in list)
                {
                    try
                    {
                        f(x);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[xray] {what} {(x as NamedObject)?.m_Name ?? x.m_PathID.ToString()}: {ex}");
                    }
                }
            }

            var textures = new Dictionary<Texture2D, string>();
            var roots = all.OfType<GameObject>().Where(g => Own(g) && g.m_Transform != null && !g.m_Transform.m_Father.TryGet(out _) && Match(g.m_Name) && HasRenderable(g.m_Transform)).ToList();
            var modelNames = new HashSet<string>();
            foreach (var root in roots)
            {
                try
                {
                    ExportModel(root, Path.Combine(outDir, "models", Safe(root.m_Name) + "_" + root.m_PathID), textures);
                    modelNames.Add(root.m_Name);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[xray] model {root.m_Name}: {ex}");
                }
            }
            // controllers: own ones and the base controllers of own override controllers; clips: own ones and every clip those controllers use
            var overrides = all.OfType<AnimatorOverrideController>().Where(Own).ToList();
            var ctrls = new HashSet<AnimatorController>(all.OfType<AnimatorController>().Where(Own));
            foreach (var ov in overrides)
            {
                if (ov.m_Controller.TryGet(out var bc) && bc is AnimatorController ac)
                    ctrls.Add(ac);
            }
            var clips = new HashSet<AnimationClip>(all.OfType<AnimationClip>().Where(Own));
            foreach (var c in ctrls)
            {
                foreach (var p in c.m_AnimationClips)
                {
                    if (p.TryGet(out var clip))
                        clips.Add(clip);
                }
            }
            foreach (var ov in overrides)
            {
                foreach (var p in ov.m_Clips)
                {
                    if (p.m_OverrideClip.TryGet(out var clip))
                        clips.Add(clip);
                }
            }
            var clipNames = new HashSet<string>();
            Each(clips.OrderBy(c => c.m_Name), "clip", clip =>
            {
                var name = Safe(clip.m_Name);
                if (!clipNames.Add(name))
                    name += "_" + clip.m_PathID;
                ExportClip(clip, Path.Combine(outDir, "clips"), name);
            });
            var refs = new RefResolver(assetsManager, assemblies);
            Each(ctrls, "controller", c => ExportController(c, Path.Combine(outDir, "controllers"), refs));
            Each(overrides, "override controller", ov => ExportOverride(ov, Path.Combine(outDir, "overrides")));
            var avatars = all.OfType<Avatar>().Where(Own).ToList();
            Each(avatars, "avatar", av => ExportAvatar(av, Path.Combine(outDir, "avatars")));

            // MonoBehaviours: the own ones, then what they reference inside own or audio bundles (breadth first); AudioClips met on the way
            bool Follow(AssetStudio.Object o) => Own(o) || bundleKey(o.assetsFile).StartsWith("assets/content/audio/", StringComparison.OrdinalIgnoreCase);
            var queue = new Queue<MonoBehaviour>(all.OfType<MonoBehaviour>().Where(Own));
            var seenMb = new HashSet<MonoBehaviour>(queue);
            var audio = new HashSet<AudioClip>(all.OfType<AudioClip>().Where(Own));
            var materialUse = new Dictionary<Material, List<string>>();
            foreach (var m in all.OfType<Material>().Where(Own))
                AddUse(materialUse, m, null);
            CollectRendererMaterials(all.Where(Own), refs, materialUse);
            var meshUse = new Dictionary<Mesh, List<string>>();
            var behaviours = 0;
            while (queue.Count > 0 && behaviours < 20000)
            {
                var mb = queue.Dequeue();
                try
                {
                    var reached = new List<AssetStudio.Object>();
                    if (ExportBehaviour(mb, Path.Combine(outDir, "behaviours"), refs, bundleKey, reached))
                        behaviours++;
                    foreach (var r in reached)
                    {
                        if (r is AudioClip ac)
                            audio.Add(ac);
                        else if (r is Material mat)
                            AddUse(materialUse, mat, "behaviour " + BehaviourId(mb));
                        else if (r is Mesh mesh && Own(mesh))
                        {
                            if (!meshUse.TryGetValue(mesh, out var users))
                                meshUse[mesh] = users = new List<string>();
                            users.Add(BehaviourId(mb));
                        }
                        else if (r is MonoBehaviour m && Follow(m) && seenMb.Add(m))
                            queue.Enqueue(m);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[xray] behaviour {mb.m_PathID}: {ex.Message}");
                }
            }
            // the referenced meshes no exported renderer has (a renderer's mesh is in its model.json already)
            var drawn = new HashSet<Mesh>();
            foreach (var o in all.Where(Own))
            {
                if (o is SkinnedMeshRenderer smr && smr.m_Mesh.TryGet(out var sm))
                    drawn.Add(sm);
                else if (o is MeshFilter mf && mf.m_Mesh.TryGet(out var fm))
                    drawn.Add(fm);
            }
            var meshes = 0;
            foreach (var (mesh, users) in meshUse.Where(kv => !drawn.Contains(kv.Key)).OrderBy(kv => kv.Key.m_Name))
            {
                try
                {
                    ExportMesh(mesh, Path.Combine(outDir, "meshes"), users);
                    meshes++;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[xray] mesh {mesh.m_Name}: {ex.Message}");
                }
            }
            var wavs = 0;
            if (exportAudio)
            {
                var names = new HashSet<string>();
                foreach (var ac in audio.OrderBy(a => a.m_Name))
                {
                    var name = Safe(ac.m_Name);
                    if (!names.Add(name))
                        name += "_" + ac.m_PathID;
                    try
                    {
                        if (ExportAudio(ac, Path.Combine(outDir, "audio"), name))
                            wavs++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[xray] audio {ac.m_Name}: {ex.Message}");
                    }
                }
            }
            var (materials, shaders) = ExportMaterials(materialUse, outDir, refs, bundleKey, textures, exportShaders, maxShaderPrograms);
            if (exportTextures)
            {
                var texDir = Path.Combine(outDir, "textures");
                foreach (var (tex, name) in textures)
                {
                    try
                    {
                        Directory.CreateDirectory(texDir);
                        using var ms = tex.ConvertToPng(true);
                        if (ms != null)
                            File.WriteAllBytes(Path.Combine(texDir, name + ".png"), ms.ToArray());
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[xray] texture {name}: {ex.Message}");
                    }
                }
            }
            var colliders = ExportPhysics(all.Where(o => Own(o) && PhysicsClasses.Contains(o.type)).ToList(), Path.Combine(outDir, "physics.json"), refs);
            var texts = all.OfType<TextAsset>().Where(Own).ToList();
            foreach (var ta in texts)
            {
                Directory.CreateDirectory(Path.Combine(outDir, "text"));
                File.WriteAllBytes(Path.Combine(outDir, "text", Safe(ta.m_Name) + ".bytes"), ta.m_Script);
            }
            Logger.Info($"[xray] {modelNames.Count} models, {clips.Count} clips, {ctrls.Count} controllers, {overrides.Count} override controllers, {avatars.Count} avatars, {textures.Count} textures, {materials} materials, {shaders} shaders, {behaviours} behaviours, {meshes} behaviour meshes, {colliders} physics components, {wavs} of {audio.Count} audio clips, {texts.Count} text assets -> {outDir}");
            return new { models = modelNames.ToArray(), clips = clips.Count, controllers = ctrls.Count, overrides = overrides.Count, avatars = avatars.Count, textures = textures.Count, materials, shaders, behaviours, meshes, physics = colliders, audio = wavs, objects = all.Count };
        }

        private static readonly HashSet<ClassIDType> PhysicsClasses = new HashSet<ClassIDType> { ClassIDType.BoxCollider, ClassIDType.SphereCollider, ClassIDType.CapsuleCollider, ClassIDType.MeshCollider, ClassIDType.Rigidbody, ClassIDType.CharacterJoint, ClassIDType.FixedJoint, ClassIDType.HingeJoint, ClassIDType.ConfigurableJoint, ClassIDType.PhysicMaterial };

        // physics.json: colliders, rigid bodies, joints and physic materials of the own bundles as their type tree values, each with its game
        // object's root and transform path (root name / child / ...: the node of models\<root>\model.json); [{class, id, root, path, fields}]
        private static int ExportPhysics(List<AssetStudio.Object> objs, string path, RefResolver refs)
        {
            var list = new List<object>();
            foreach (var o in objs)
            {
                try
                {
                    string root = null, tpath = null;
                    var fields = o.ToType();
                    // colliders are no parsed class here: their game object from the type tree
                    var gref = fields?["m_GameObject"] as System.Collections.Specialized.OrderedDictionary;
                    var go = gref == null ? null : refs.Get(o.assetsFile, Convert.ToInt32(gref["m_FileID"]), Convert.ToInt64(gref["m_PathID"])) as GameObject;
                    if (go?.m_Transform != null)
                    {
                        var names = new List<string>();
                        var t = go.m_Transform;
                        while (t != null)
                        {
                            names.Add(t.m_GameObject.TryGet(out var g) ? g.m_Name : "?");
                            t = t.m_Father.TryGet(out var f) ? f : null;
                        }
                        names.Reverse();
                        root = names[0];
                        tpath = string.Join("/", names);
                    }
                    list.Add(new { @class = o.type.ToString(), id = BehaviourId(o), name = NameOf(o), root, path = tpath, fields = fields == null ? null : Plain(fields, o.assetsFile, refs, null) });
                }
                catch (Exception ex)
                {
                    Logger.Error($"[xray] physics {o.type} {o.m_PathID}: {ex.Message}");
                }
            }
            if (list.Count > 0)
                WriteJson(path, list);
            return list.Count;
        }

        // PPtr -> object across the loaded files; type trees of MonoBehaviours (stored or built from the game assemblies)
        private sealed class RefResolver
        {
            private readonly Dictionary<string, SerializedFile> files;
            private readonly AssemblyLoader assemblies;

            public RefResolver(AssetsManager am, AssemblyLoader assemblies)
            {
                files = new Dictionary<string, SerializedFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in am.assetsFileList)
                    files.TryAdd(f.fileName, f);
                this.assemblies = assemblies;
            }

            private SerializedFile File(SerializedFile from, int fileId)
            {
                if (fileId <= 0)
                    return from;
                return fileId - 1 < from.m_Externals.Count && files.TryGetValue(from.m_Externals[fileId - 1].fileName, out var f) ? f : null;
            }

            public AssetStudio.Object Get(SerializedFile from, int fileId, long pathId)
            {
                if (pathId == 0)
                    return null;
                var f = File(from, fileId);
                return f != null && f.ObjectsDic.TryGetValue(pathId, out var o) ? o : null;
            }

            // also objects AssetsManager does not keep (Shader from Unity 2021 on): a plain Object to read through its type tree
            public AssetStudio.Object GetRaw(SerializedFile from, int fileId, long pathId)
            {
                var o = Get(from, fileId, pathId);
                if (o != null || pathId == 0)
                    return o;
                var f = File(from, fileId);
                var oi = f?.m_Objects.FirstOrDefault(x => x.m_PathID == pathId);
                return oi == null ? null : new AssetStudio.Object(new ObjectReader(f.reader, f, oi));
            }

            public System.Collections.Specialized.OrderedDictionary Fields(MonoBehaviour mb)
            {
                var t = mb.ToType();
                if (t == null && assemblies.Loaded)
                    t = mb.ToType(mb.ConvertToTypeTree(assemblies));
                return t;
            }
        }

        private static string ClassOf(AssetStudio.Object o)
        {
            if (o is MonoBehaviour mb && mb.m_Script.TryGet(out var ms))
                return ms.m_ClassName;
            return o.type.ToString();
        }

        private static string NameOf(AssetStudio.Object o)
        {
            if (o is NamedObject no && !string.IsNullOrEmpty(no.m_Name))
                return no.m_Name;
            if (o is MonoBehaviour mb && !string.IsNullOrEmpty(mb.m_Name))
                return mb.m_Name;
            if (o is GameObject go)
                return go.m_Name;
            if (o is Component c && c.m_GameObject.TryGet(out var g))
                return g.m_Name;
            return "";
        }

        public static string BehaviourId(AssetStudio.Object o) => Safe(ClassOf(o)) + "_" + o.m_PathID;

        // the type tree values as plain JSON values; PPtrs resolved to {"ref", "id"} and collected in reached
        private static object Plain(object v, SerializedFile from, RefResolver refs, List<AssetStudio.Object> reached)
        {
            switch (v)
            {
                case System.Collections.Specialized.OrderedDictionary d:
                {
                    if (d.Count == 2 && d.Contains("m_FileID") && d.Contains("m_PathID"))
                    {
                        var pid = Convert.ToInt64(d["m_PathID"]);
                        if (pid == 0)
                            return null;
                        var o = refs.Get(from, Convert.ToInt32(d["m_FileID"]), pid);
                        if (o == null)
                            return new { missing = pid };
                        reached?.Add(o);
                        return new Dictionary<string, object> { ["ref"] = ClassOf(o) + ":" + NameOf(o), ["id"] = BehaviourId(o) };
                    }
                    var r = new Dictionary<string, object>();
                    foreach (System.Collections.DictionaryEntry e in d)
                        r[e.Key.ToString()] = Plain(e.Value, from, refs, reached);
                    return r;
                }
                case byte[] b:
                    return b.Length <= 256 ? b.Select(x => (int)x).ToArray() : new { bytes = b.Length };
                case string s:
                    return s;
                case System.Collections.IList l:
                {
                    var r = new List<object>();
                    foreach (var x in l)
                        r.Add(Plain(x, from, refs, reached));
                    return r;
                }
                case System.Collections.DictionaryEntry de:
                    return new { key = Plain(de.Key, from, refs, reached), value = Plain(de.Value, from, refs, reached) };
                case KeyValuePair<object, object> kv:
                    return new { key = Plain(kv.Key, from, refs, reached), value = Plain(kv.Value, from, refs, reached) };
                case float f:
                    return float.IsFinite(f) ? f : f.ToString();
                case double dd:
                    return double.IsFinite(dd) ? dd : dd.ToString();
                default:
                    return v;
            }
        }

        private static bool ExportBehaviour(MonoBehaviour mb, string dir, RefResolver refs, Func<SerializedFile, string> bundleKey, List<AssetStudio.Object> reached)
        {
            var t = refs.Fields(mb);
            if (t == null)
                return false;
            Directory.CreateDirectory(dir);
            mb.m_Script.TryGet(out var ms);
            mb.m_GameObject.TryGet(out var go);
            WriteJson(Path.Combine(dir, BehaviourId(mb) + ".json"), new
            {
                @class = ms?.m_ClassName,
                @namespace = ms?.m_Namespace,
                assembly = ms?.m_AssemblyName,
                name = mb.m_Name,
                game_object = go?.m_Name,
                bundle = bundleKey(mb.assetsFile),
                file = mb.assetsFile.fileName,
                path_id = mb.m_PathID,
                fields = Plain(t, mb.assetsFile, refs, reached),
            });
            return true;
        }

        private static bool ExportAudio(AudioClip ac, string dir, string name)
        {
            var data = ac.m_AudioData.GetData();
            if (data == null || data.Length == 0)
                return false;
            Directory.CreateDirectory(dir);
            var conv = new AudioClipConverter(ac);
            if (conv.IsSupport || conv.IsLegacy)
            {
                var log = "";
                var wav = conv.IsLegacy ? conv.RawAudioClipToWav(ref log) : conv.ConvertToWav(data, ref log);
                if (wav == null)
                {
                    Logger.Error($"[xray] audio {ac.m_Name}: fmod conversion failed {log}");
                    return false;
                }
                File.WriteAllBytes(Path.Combine(dir, name + ".wav"), wav);
            }
            else
            {
                File.WriteAllBytes(Path.Combine(dir, name + conv.GetExtensionName()), data);
            }
            return true;
        }

        private static void ExportOverride(AnimatorOverrideController ov, string dir)
        {
            Directory.CreateDirectory(dir);
            ov.m_Controller.TryGet(out var bc);
            WriteJson(Path.Combine(dir, Safe(ov.m_Name) + "_" + ov.m_PathID + ".json"), new
            {
                name = ov.m_Name,
                file = ov.assetsFile.fileName,
                controller = bc?.m_Name,
                clips = ov.m_Clips.Select(c => new { original = c.m_OriginalClip.TryGet(out var a) ? a.m_Name : null, @override = c.m_OverrideClip.TryGet(out var b) ? b.m_Name : null }).ToArray(),
            });
        }

        private static bool HasRenderable(Transform t)
        {
            if (t.m_GameObject.TryGet(out var go) && (go.m_SkinnedMeshRenderer != null || go.m_MeshFilter != null || go.m_Animator != null))
                return true;
            foreach (var c in t.m_Children)
            {
                if (c.TryGet(out var ct) && HasRenderable(ct))
                    return true;
            }
            return false;
        }

        private static void ExportModel(GameObject root, string dir, Dictionary<Texture2D, string> textures)
        {
            Directory.CreateDirectory(dir);
            var nodes = new List<Transform>();
            var index = new Dictionary<Transform, int>();
            var parents = new List<int>();
            var paths = new List<string>();
            void Walk(Transform t, int parent, string path)
            {
                index[t] = nodes.Count;
                nodes.Add(t);
                parents.Add(parent);
                paths.Add(path);
                var self = nodes.Count - 1;
                foreach (var c in t.m_Children)
                {
                    if (c.TryGet(out var ct) && ct.m_GameObject.TryGet(out var cg))
                        Walk(ct, self, path.Length == 0 ? cg.m_Name : path + "/" + cg.m_Name);
                }
            }
            Walk(root.m_Transform, -1, "");

            using var bin = new BinWriter(Path.Combine(dir, "model.bin"));
            var jNodes = new List<object>();
            var renderers = new List<object>();
            var animators = new List<object>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var t = nodes[i];
                t.m_GameObject.TryGet(out var go);
                var comps = new List<string>();
                foreach (var c in go.m_Components)
                {
                    if (c.TryGet(out var co))
                        comps.Add(co.type.ToString());
                }
                jNodes.Add(new
                {
                    name = go.m_Name,
                    parent = parents[i],
                    path = paths[i],
                    t = new[] { t.m_LocalPosition.X, t.m_LocalPosition.Y, t.m_LocalPosition.Z },
                    r = new[] { t.m_LocalRotation.X, t.m_LocalRotation.Y, t.m_LocalRotation.Z, t.m_LocalRotation.W },
                    s = new[] { t.m_LocalScale.X, t.m_LocalScale.Y, t.m_LocalScale.Z },
                    components = comps,
                });
                if (go.m_SkinnedMeshRenderer != null)
                    renderers.Add(ExportRenderer(go.m_SkinnedMeshRenderer, i, index, bin, textures));
                else if (go.m_MeshRenderer != null && go.m_MeshFilter != null)
                    renderers.Add(ExportRenderer(go.m_MeshRenderer, i, index, bin, textures));
                if (go.m_Animator != null)
                {
                    var a = go.m_Animator;
                    a.m_Controller.TryGet(out var ctrl);
                    a.m_Avatar.TryGet(out var avatar);
                    animators.Add(new { node = i, controller = ctrl?.m_Name, avatar = avatar?.m_Name, has_transform_hierarchy = a.m_HasTransformHierarchy });
                }
            }
            WriteJson(Path.Combine(dir, "model.json"), new { name = root.m_Name, file = root.assetsFile.fileName, path_id = root.m_PathID, nodes = jNodes, renderers, animators });
            Logger.Info($"[xray] model {root.m_Name}: {nodes.Count} nodes, {renderers.Count} renderers -> {dir}");
        }

        private static object ExportRenderer(Renderer r, int node, Dictionary<Transform, int> index, BinWriter bin, Dictionary<Texture2D, string> textures)
        {
            Mesh mesh = null;
            var bones = new List<int>();
            if (r is SkinnedMeshRenderer smr)
            {
                smr.m_Mesh.TryGet(out mesh);
                foreach (var b in smr.m_Bones)
                    bones.Add(b.TryGet(out var bt) && index.TryGetValue(bt, out var bi) ? bi : -1);
            }
            else if (r.m_GameObject.TryGet(out var go) && go.m_MeshFilter != null)
            {
                go.m_MeshFilter.m_Mesh.TryGet(out mesh);
            }
            var materials = new List<object>();
            foreach (var mp in r.m_Materials)
            {
                if (!mp.TryGet(out var mat))
                {
                    materials.Add(null);
                    continue;
                }
                var texs = new Dictionary<string, object>();
                foreach (var te in mat.m_SavedProperties.m_TexEnvs)
                {
                    if (te.Value.m_Texture.TryGet(out var tx) && tx is Texture2D t2)
                    {
                        var tn = TextureName(t2, textures);
                        texs[te.Key] = new { texture = tn, scale = new[] { te.Value.m_Scale.X, te.Value.m_Scale.Y }, offset = new[] { te.Value.m_Offset.X, te.Value.m_Offset.Y } };
                    }
                }
                materials.Add(new
                {
                    name = mat.m_Name,
                    textures = texs,
                    floats = mat.m_SavedProperties.m_Floats?.ToDictionary(x => x.Key, x => x.Value),
                    colors = mat.m_SavedProperties.m_Colors?.ToDictionary(x => x.Key, x => new[] { x.Value.R, x.Value.G, x.Value.B, x.Value.A }),
                });
            }
            var jm = mesh != null ? MeshJson(mesh, bin, false) : null;
            return new { node, kind = r is SkinnedMeshRenderer ? "skinned" : "mesh", bones, materials, mesh = jm };
        }

        // a mesh's decoded streams in the .bin; with shapes also the blend shape frames: per channel its frames with the full weight, the
        // vertex indices and the position deltas (x, y, z)
        private static object MeshJson(Mesh mesh, BinWriter bin, bool shapes)
        {
            var subs = new List<object>();
            var first = 0;
            for (var s = 0; s < mesh.m_SubMeshes.Length; s++)
            {
                var sm = mesh.m_SubMeshes[s];
                var n = sm.topology == GfxPrimitiveType.Triangles ? (int)sm.indexCount : (int)sm.indexCount / 4 * 6;
                n = Math.Min(n, mesh.m_Indices.Count - first);
                subs.Add(new { topology = sm.topology.ToString(), indices = bin.U(mesh.m_Indices.Skip(first).Take(n)) });
                first += n;
            }
            var vc = mesh.m_VertexCount;
            object skin = null;
            if (mesh.m_Skin?.Length > 0)
            {
                skin = new
                {
                    indices = bin.I(mesh.m_Skin.SelectMany(x => x.boneIndex)),
                    weights = bin.F(mesh.m_Skin.SelectMany(x => x.weight)),
                };
            }
            var uvs = new Dictionary<string, object>();
            for (var u = 0; u < 8; u++)
            {
                var a = mesh.GetUV(u);
                if (a != null && a.Length > 0)
                    uvs["uv" + u] = new { dim = a.Length / vc, data = bin.F(a) };
            }
            object frames = null;
            var bs = mesh.m_Shapes;
            if (shapes && bs?.channels?.Length > 0)
            {
                frames = bs.channels.Select(c => new
                {
                    name = c.name,
                    frames = Enumerable.Range(c.frameIndex, c.frameCount).Where(fi => fi < bs.shapes.Length).Select(fi =>
                    {
                        var sh = bs.shapes[fi];
                        var vs = bs.vertices.Skip((int)sh.firstVertex).Take((int)sh.vertexCount).ToList();
                        return new
                        {
                            weight = fi < bs.fullWeights?.Length ? bs.fullWeights[fi] : 100f,
                            indices = bin.U(vs.Select(v => v.index)),
                            deltas = bin.F(vs.SelectMany(v => new[] { v.vertex.X, v.vertex.Y, v.vertex.Z })),
                        };
                    }).ToArray(),
                }).ToArray();
            }
            return new
            {
                name = mesh.m_Name,
                vertex_count = vc,
                positions = new { dim = mesh.m_Vertices.Length / vc, data = bin.F(mesh.m_Vertices) },
                normals = mesh.m_Normals?.Length > 0 ? new { dim = mesh.m_Normals.Length / vc, data = bin.F(mesh.m_Normals) } : null,
                tangents = mesh.m_Tangents?.Length > 0 ? new { dim = mesh.m_Tangents.Length / vc, data = bin.F(mesh.m_Tangents) } : null,
                colors = mesh.m_Colors?.Length > 0 ? new { dim = mesh.m_Colors.Length / vc, data = bin.F(mesh.m_Colors) } : null,
                uvs,
                submeshes = subs,
                skin,
                bind_poses = mesh.m_BindPose?.Length > 0 ? bin.F(mesh.m_BindPose.SelectMany(m => new[] { m.M00, m.M10, m.M20, m.M30, m.M01, m.M11, m.M21, m.M31, m.M02, m.M12, m.M22, m.M32, m.M03, m.M13, m.M23, m.M33 })) : null,
                bone_name_hashes = mesh.m_BoneNameHashes,
                blend_shapes = bs?.channels?.Select(c => c.name).ToArray(),
                blend_shape_frames = frames,
            };
        }

        // a mesh no exported renderer draws (a behaviour's): meshes\<name>_<path id>.json + .bin, the model.json mesh layout with the
        // blend shape frames
        private static void ExportMesh(Mesh mesh, string dir, List<string> users)
        {
            Directory.CreateDirectory(dir);
            var name = Safe(mesh.m_Name) + "_" + mesh.m_PathID;
            object jm;
            using (var bin = new BinWriter(Path.Combine(dir, name + ".bin")))
                jm = MeshJson(mesh, bin, true);
            WriteJson(Path.Combine(dir, name + ".json"), new { name = mesh.m_Name, file = mesh.assetsFile.fileName, path_id = mesh.m_PathID, used_by = users.Distinct().ToArray(), mesh = jm });
            Logger.Info($"[xray] mesh {mesh.m_Name}: {mesh.m_VertexCount} vertices, used by {string.Join(", ", users.Distinct())} -> {dir}");
        }

        private static void ExportClip(AnimationClip clip, string dir, string name)
        {
            Directory.CreateDirectory(dir);
            using var bin = new BinWriter(Path.Combine(dir, name + ".bin"));
            object muscle = null;
            if (!clip.m_Legacy && clip.m_MuscleClip != null)
            {
                var mc = clip.m_MuscleClip;
                var c = mc.m_Clip.data;
                var frames = c.m_StreamedClip.ReadData();
                // streamed: per frame the keys (curve index, coefficients a b c d: value(t + dt) = ((a dt + b) dt + c) dt + d)
                var sTimes = new List<float>();
                var sCounts = new List<int>();
                var sIdx = new List<int>();
                var sCoef = new List<float>();
                foreach (var f in frames)
                {
                    sTimes.Add(f.time);
                    sCounts.Add(f.keyList.Length);
                    foreach (var k in f.keyList)
                    {
                        sIdx.Add(k.index);
                        sCoef.AddRange(k.coeff);
                    }
                }
                var bc = clip.m_ClipBindingConstant ?? c.ConvertValueArrayToGenericBinding();
                muscle = new
                {
                    start_time = mc.m_StartTime,
                    stop_time = mc.m_StopTime,
                    loop_time = mc.m_LoopTime,
                    cycle_offset = mc.m_CycleOffset,
                    streamed = new { curve_count = c.m_StreamedClip.curveCount, frame_times = bin.F(sTimes), frame_key_counts = bin.I(sCounts), key_curves = bin.I(sIdx), key_coeffs = bin.F(sCoef) },
                    dense = new { frame_count = c.m_DenseClip.m_FrameCount, curve_count = c.m_DenseClip.m_CurveCount, sample_rate = c.m_DenseClip.m_SampleRate, begin_time = c.m_DenseClip.m_BeginTime, samples = bin.F(c.m_DenseClip.m_SampleArray) },
                    constant = c.m_ConstantClip != null ? bin.F(c.m_ConstantClip.data) : null,
                    bindings = bc.genericBindings.Select(b => new { path = b.path, attribute = b.attribute, type = b.typeID.ToString(), custom_type = b.customType, pptr = b.isPPtrCurve, script = b.script.TryGet(out var so) ? so.Name : null }).ToArray(),
                };
            }
            object legacy = null;
            if (clip.m_Legacy)
            {
                legacy = new
                {
                    rotation = clip.m_RotationCurves?.Select(q => new { path = q.path, times = bin.F(q.curve.m_Curve.Select(k => k.time)), values = bin.F(q.curve.m_Curve.SelectMany(k => new[] { k.value.X, k.value.Y, k.value.Z, k.value.W })) }).ToArray(),
                    position = clip.m_PositionCurves?.Select(q => new { path = q.path, times = bin.F(q.curve.m_Curve.Select(k => k.time)), values = bin.F(q.curve.m_Curve.SelectMany(k => new[] { k.value.X, k.value.Y, k.value.Z })) }).ToArray(),
                    scale = clip.m_ScaleCurves?.Select(q => new { path = q.path, times = bin.F(q.curve.m_Curve.Select(k => k.time)), values = bin.F(q.curve.m_Curve.SelectMany(k => new[] { k.value.X, k.value.Y, k.value.Z })) }).ToArray(),
                    euler = clip.m_EulerCurves?.Select(q => new { path = q.path, times = bin.F(q.curve.m_Curve.Select(k => k.time)), values = bin.F(q.curve.m_Curve.SelectMany(k => new[] { k.value.X, k.value.Y, k.value.Z })) }).ToArray(),
                };
            }
            WriteJson(Path.Combine(dir, name + ".json"), new
            {
                name = clip.m_Name,
                file = clip.assetsFile.fileName,
                path_id = clip.m_PathID,
                type = clip.m_AnimationType.ToString(),
                legacy = clip.m_Legacy,
                sample_rate = clip.m_SampleRate,
                wrap_mode = clip.m_WrapMode,
                events = clip.m_Events?.Select(e => new { time = e.time, function = e.functionName, data = e.data, f = e.floatParameter, i = e.intParameter, obj = e.objectReferenceParameter.TryGet(out var eo) ? eo.Name : null }).ToArray(),
                muscle,
                curves = legacy,
            });
        }

        // state machine behaviours per state (m_StateMachineBehaviourVectorDescription of the type tree): [{state, layer, behaviours: [id]}]
        private static object StateBehaviours(AnimatorController ctrl, RefResolver refs, Func<uint, string> str)
        {
            var t = ctrl.ToType();
            if (t == null || !t.Contains("m_StateMachineBehaviourVectorDescription") || !t.Contains("m_StateMachineBehaviours"))
                return null;
            var desc = (System.Collections.Specialized.OrderedDictionary)t["m_StateMachineBehaviourVectorDescription"];
            var list = (System.Collections.IList)t["m_StateMachineBehaviours"];
            var indices = ((System.Collections.IList)desc["m_StateMachineBehaviourIndices"]).Cast<object>().Select(Convert.ToInt32).ToList();
            var ids = new List<string>();
            foreach (System.Collections.Specialized.OrderedDictionary p in list)
            {
                var o = refs.Get(ctrl.assetsFile, Convert.ToInt32(p["m_FileID"]), Convert.ToInt64(p["m_PathID"]));
                ids.Add(o == null ? null : BehaviourId(o));
            }
            var result = new List<object>();
            foreach (var e in (System.Collections.IList)desc["m_StateMachineBehaviourRanges"])
            {
                System.Collections.Specialized.OrderedDictionary key, range;
                if (e is KeyValuePair<object, object> kv)
                {
                    key = (System.Collections.Specialized.OrderedDictionary)kv.Key;
                    range = (System.Collections.Specialized.OrderedDictionary)kv.Value;
                }
                else if (e is System.Collections.DictionaryEntry de)
                {
                    key = (System.Collections.Specialized.OrderedDictionary)de.Key;
                    range = (System.Collections.Specialized.OrderedDictionary)de.Value;
                }
                else
                {
                    var pr = (System.Collections.Specialized.OrderedDictionary)e;
                    key = (System.Collections.Specialized.OrderedDictionary)pr["first"];
                    range = (System.Collections.Specialized.OrderedDictionary)pr["second"];
                }
                var state = Convert.ToUInt32(key["m_StateID"]);
                var start = Convert.ToInt32(range["m_StartIndex"]);
                var count = Convert.ToInt32(range["m_Count"]);
                result.Add(new
                {
                    state = str(state),
                    state_id = state,
                    layer = Convert.ToInt32(key["m_LayerIndex"]),
                    behaviours = Enumerable.Range(start, count).Select(k => k < indices.Count && indices[k] < ids.Count ? ids[indices[k]] : null).ToArray(),
                });
            }
            return result;
        }

        private static void ExportController(AnimatorController ctrl, string dir, RefResolver refs)
        {
            Directory.CreateDirectory(dir);
            var tos = ctrl.m_TOS?.GroupBy(x => x.Key).ToDictionary(g => g.Key.ToString(), g => g.First().Value);
            string Str(uint id) => tos != null && tos.TryGetValue(id.ToString(), out var s) ? s : id.ToString();
            var clips = ctrl.m_AnimationClips.Select(p => p.TryGet(out var c) ? c.m_Name : null).ToArray();
            var c0 = ctrl.m_Controller;
            var machines = c0.m_StateMachineArray.Select((sm, smi) => new
            {
                index = smi,
                default_state = sm.m_DefaultState,
                states = sm.m_StateConstantArray.Select(st => new
                {
                    name = Str(st.m_NameID),
                    path = Str(st.m_FullPathID),
                    tag = Str(st.m_TagID),
                    speed = st.m_Speed,
                    speed_param = st.m_SpeedParamID == 0 ? null : Str(st.m_SpeedParamID),
                    loop = st.m_Loop,
                    cycle_offset = st.m_CycleOffset,
                    // leaf nodes: m_ClipID indexes m_AnimationClips (m_ClipIndex is the pre-4.5 field, 0 here)
                    clips = st.m_BlendTreeConstantArray.SelectMany(bt => bt.m_NodeArray).Where(n => n.m_ClipID != uint.MaxValue && n.m_ChildIndices.Length == 0).Select(n => n.m_ClipID < clips.Length ? clips[n.m_ClipID] : $"#{n.m_ClipID}").ToArray(),
                    blend = st.m_BlendTreeConstantArray.SelectMany(bt => bt.m_NodeArray).Where(n => n.m_ChildIndices.Length > 0).Select(n => new { type = n.m_BlendType, param = Str(n.m_BlendEventID), param_y = Str(n.m_BlendEventYID), thresholds = n.m_ChildThresholdArray }).ToArray(),
                    // the whole blend trees: node 0 is the root; 1D thresholds and 2D positions live in m_Blend1dData / m_Blend2dData since
                    // Unity 4.1 (m_ChildThresholdArray above is the pre-4.1 field, null here)
                    trees = st.m_BlendTreeConstantArray.Select(bt => bt.m_NodeArray.Select((n, ni) => new
                    {
                        index = ni,
                        type = n.m_BlendType,
                        param = n.m_ChildIndices.Length > 0 ? Str(n.m_BlendEventID) : null,
                        param_y = n.m_ChildIndices.Length > 0 && n.m_BlendType >= 1 && n.m_BlendType <= 3 ? Str(n.m_BlendEventYID) : null,
                        children = n.m_ChildIndices,
                        thresholds = n.m_ChildIndices.Length > 0 && n.m_BlendType == 0 ? n.m_Blend1dData?.m_ChildThresholdArray : null,
                        positions = n.m_ChildIndices.Length > 0 && n.m_BlendType >= 1 && n.m_BlendType <= 3 ? n.m_Blend2dData?.m_ChildPositionArray?.Select(p => new[] { p.X, p.Y }).ToArray() : null,
                        direct = n.m_ChildIndices.Length > 0 && n.m_BlendType == 4 ? n.m_BlendDirectData?.m_ChildBlendEventIDArray?.Select(Str).ToArray() : null,
                        clip = n.m_ChildIndices.Length == 0 && n.m_ClipID != uint.MaxValue ? (n.m_ClipID < clips.Length ? clips[n.m_ClipID] : $"#{n.m_ClipID}") : null,
                        mirror = n.m_Mirror,
                    }).ToArray()).ToArray(),
                    transitions = st.m_TransitionConstantArray.Select(tr => new
                    {
                        to = tr.m_DestinationState,
                        duration = tr.m_TransitionDuration,
                        offset = tr.m_TransitionOffset,
                        exit_time = tr.m_HasExitTime ? tr.m_ExitTime : (float?)null,
                        fixed_duration = tr.m_HasFixedDuration,
                        conditions = tr.m_ConditionConstantArray.Select(cc => new { mode = cc.m_ConditionMode, param = Str(cc.m_EventID), threshold = cc.m_EventThreshold }).ToArray(),
                    }).ToArray(),
                }).ToArray(),
                any_state_transitions = sm.m_AnyStateTransitionConstantArray.Select(tr => new
                {
                    to = tr.m_DestinationState,
                    duration = tr.m_TransitionDuration,
                    conditions = tr.m_ConditionConstantArray.Select(cc => new { mode = cc.m_ConditionMode, param = Str(cc.m_EventID), threshold = cc.m_EventThreshold }).ToArray(),
                }).ToArray(),
            }).ToArray();
            var layers = c0.m_LayerArray.Select(l => new { state_machine = l.m_StateMachineIndex, motion_set = l.m_StateMachineMotionSetIndex, weight = l.m_DefaultWeight, blending = l.m_LayerBlendingMode, mask = l.m_SkeletonMask?.m_Data?.Select(m => new { path = Str(m.m_PathHash), hash = m.m_PathHash, weight = m.m_Weight }).ToArray() }).ToArray();
            object behaviours = null;
            try
            {
                behaviours = StateBehaviours(ctrl, refs, Str);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[xray] controller {ctrl.m_Name}: state behaviours not read: {ex.Message}");
            }
            WriteJson(Path.Combine(dir, Safe(ctrl.m_Name) + "_" + ctrl.m_PathID + ".json"), new { name = ctrl.m_Name, file = ctrl.assetsFile.fileName, clips, layers, machines, state_behaviours = behaviours, strings = tos });
        }

        // path hash -> path table and the avatar skeleton (parents, path hashes, default pose): resolves skinned mesh bone_name_hashes when the bones live in another bundle
        private static void ExportAvatar(Avatar av, string dir)
        {
            Directory.CreateDirectory(dir);
            var a = av.m_Avatar;
            object Pose(SkeletonPose p) => p?.m_X?.Select(x => new { t = new[] { x.t.X, x.t.Y, x.t.Z }, r = new[] { x.q.X, x.q.Y, x.q.Z, x.q.W }, s = new[] { x.s.X, x.s.Y, x.s.Z } }).ToArray();
            var skel = a.m_AvatarSkeleton;
            WriteJson(Path.Combine(dir, Safe(av.m_Name) + "_" + av.m_PathID + ".json"), new
            {
                name = av.m_Name,
                file = av.assetsFile.fileName,
                tos = av.m_TOS?.Select(x => new { hash = x.Key, path = x.Value }).ToArray(),
                skeleton = skel == null ? null : new { parents = skel.m_Node.Select(n => n.m_ParentId).ToArray(), ids = skel.m_ID },
                pose = Pose(a.m_AvatarSkeletonPose),
                default_pose = Pose(a.m_DefaultPose),
            });
        }
    }
}
