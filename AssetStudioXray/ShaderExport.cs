using AssetStudio;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AssetStudioXray
{
    // Materials and shaders (XrayExporter.cs header: materials.json, shaders\).
    //   materials.json: the own materials and the ones own renderers (any renderer class, particle systems too) or exported behaviours
    //   use, with shader, textures (written to textures\), floats, ints, colors, keywords, render queue and who uses them.
    //   shaders\<name>_<path id>.json: the parsed form of each material's shader when its bundle is loaded (--shaders loads the game's
    //   `shaders` bundle): properties, keywords, passes with their render state, constant buffer layouts and programs; every D3D11 program
    //   as shaders\<name>_<path id>\<subshader>_<pass>_<stage>_<n>.dxbc and its disassembly .asm (d3dcompiler_47.dll D3DDisassemble),
    //   the .asm headed by the keywords and the constant buffer layout; a shader with more programs than --shader-max (surface shaders:
    //   16244 for Tarkov's weapon shader, 214 MB) gets the list only. Unity 2021+ shaders are not parsed by AssetStudio: read here through
    //   their type tree.
    internal static partial class XrayExporter
    {
        private static readonly HashSet<ClassIDType> RendererClasses = new HashSet<ClassIDType> { ClassIDType.MeshRenderer, ClassIDType.SkinnedMeshRenderer, ClassIDType.ParticleSystemRenderer, ClassIDType.LineRenderer, ClassIDType.TrailRenderer, ClassIDType.BillboardRenderer };

        private static readonly string[] BlendFactors = { "Zero", "One", "DstColor", "SrcColor", "OneMinusDstColor", "SrcAlpha", "OneMinusSrcColor", "DstAlpha", "OneMinusDstAlpha", "SrcAlphaSaturate", "OneMinusSrcAlpha" };
        private static readonly string[] BlendOps = { "Add", "Subtract", "RevSubtract", "Min", "Max" };
        private static readonly string[] CompareFuncs = { "Disabled", "Never", "Less", "Equal", "LessEqual", "Greater", "NotEqual", "GreaterEqual", "Always" };
        private static readonly string[] CullModes = { "Off", "Front", "Back" };
        private static readonly string[] PropertyTypes = { "Color", "Vector", "Float", "Range", "Texture", "Int" };
        private static readonly string[] Stages = { "progVertex", "progFragment", "progGeometry", "progHull", "progDomain" };

        private static string TextureName(Texture2D t2, Dictionary<Texture2D, string> textures)
        {
            if (!textures.TryGetValue(t2, out var tn))
            {
                tn = Safe(t2.m_Name);
                if (textures.ContainsValue(tn))
                    tn += "_" + t2.m_PathID;
                textures[t2] = tn;
            }
            return tn;
        }

        // the users of the materials: own renderers of every class (game object path), from the type tree (particle system renderers
        // and line / trail renderers are no parsed class)
        private static void CollectRendererMaterials(IEnumerable<AssetStudio.Object> own, RefResolver refs, Dictionary<Material, List<string>> use)
        {
            foreach (var o in own.Where(x => RendererClasses.Contains(x.type)))
            {
                try
                {
                    var t = o.ToType();
                    if (t == null || !t.Contains("m_Materials"))
                        continue;
                    var gref = t["m_GameObject"] as OrderedDictionary;
                    var go = gref == null ? null : refs.Get(o.assetsFile, Convert.ToInt32(gref["m_FileID"]), Convert.ToInt64(gref["m_PathID"])) as GameObject;
                    var user = $"{o.type} {GameObjectPath(go)}";
                    foreach (OrderedDictionary mp in (IList)t["m_Materials"])
                    {
                        if (refs.Get(o.assetsFile, Convert.ToInt32(mp["m_FileID"]), Convert.ToInt64(mp["m_PathID"])) is Material m)
                            AddUse(use, m, user);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[xray] renderer {o.type} {o.m_PathID}: {ex.Message}");
                }
            }
        }

        private static void AddUse(Dictionary<Material, List<string>> use, Material m, string user)
        {
            if (!use.TryGetValue(m, out var l))
                use[m] = l = new List<string>();
            if (user != null && !l.Contains(user))
                l.Add(user);
        }

        private static string GameObjectPath(GameObject go)
        {
            if (go == null)
                return "?";
            var names = new List<string>();
            var t = go.m_Transform;
            if (t == null)
                return go.m_Name;
            while (t != null)
            {
                names.Add(t.m_GameObject.TryGet(out var g) ? g.m_Name : "?");
                t = t.m_Father.TryGet(out var f) ? f : null;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        // materials.json and the shaders they use; returns (materials, shaders written)
        private static (int, int) ExportMaterials(Dictionary<Material, List<string>> use, string outDir, RefResolver refs, Func<SerializedFile, string> bundleKey, Dictionary<Texture2D, string> textures, bool exportShaders, int maxPrograms)
        {
            if (use.Count == 0)
                return (0, 0);
            var shaders = new Dictionary<string, string>();
            var list = new List<object>();
            foreach (var (mat, users) in use.OrderBy(x => x.Key.m_Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key.m_PathID))
            {
                try
                {
                    // shader: the raw object, read through its type tree (AssetsManager does not keep shaders)
                    var sp = mat.m_Shader;
                    var shObj = refs.GetRaw(mat.assetsFile, sp.m_FileID, sp.m_PathID);
                    object shader = null;
                    if (shObj != null)
                    {
                        var key = BehaviourId(shObj);
                        if (!shaders.TryGetValue(key, out var shName))
                        {
                            shName = exportShaders ? ExportShader(shObj, Path.Combine(outDir, "shaders"), bundleKey, maxPrograms) : ShaderName(shObj);
                            shaders[key] = shName;
                        }
                        shader = new { name = shName, id = key, file = exportShaders && shName != null ? "shaders/" + Safe(shName) + "_" + shObj.m_PathID + ".json" : null };
                    }
                    else if (!sp.IsNull)
                    {
                        shader = new { missing = sp.m_PathID, external = sp.m_FileID > 0 && sp.m_FileID - 1 < mat.assetsFile.m_Externals.Count ? mat.assetsFile.m_Externals[sp.m_FileID - 1].fileName : null };
                    }
                    var texs = new Dictionary<string, object>();
                    foreach (var te in mat.m_SavedProperties.m_TexEnvs)
                    {
                        string tn = null;
                        if (te.Value.m_Texture.TryGet(out var tx))
                            tn = tx is Texture2D t2 ? TextureName(t2, textures) : $"{tx.type}:{tx.m_Name}";
                        else if (!te.Value.m_Texture.IsNull)
                            tn = $"missing:{te.Value.m_Texture.m_PathID}";
                        texs[te.Key] = new { texture = tn, scale = new[] { te.Value.m_Scale.X, te.Value.m_Scale.Y }, offset = new[] { te.Value.m_Offset.X, te.Value.m_Offset.Y } };
                    }
                    // keywords and queue only in the type tree (Unity 2021.2+: m_ValidKeywords / m_InvalidKeywords, older m_ShaderKeywords)
                    var tt = mat.ToType();
                    object keywords = null, invalid = null, queue = null, tags = null;
                    if (tt != null)
                    {
                        keywords = tt.Contains("m_ValidKeywords") ? tt["m_ValidKeywords"] : tt.Contains("m_ShaderKeywords") ? tt["m_ShaderKeywords"] : null;
                        invalid = tt.Contains("m_InvalidKeywords") ? tt["m_InvalidKeywords"] : null;
                        queue = tt.Contains("m_CustomRenderQueue") ? tt["m_CustomRenderQueue"] : null;
                        tags = tt.Contains("stringTagMap") ? Plain(tt["stringTagMap"], mat.assetsFile, refs, null) : null;
                    }
                    list.Add(new
                    {
                        name = mat.m_Name,
                        id = BehaviourId(mat),
                        bundle = bundleKey(mat.assetsFile),
                        file = mat.assetsFile.fileName,
                        path_id = mat.m_PathID,
                        shader,
                        textures = texs,
                        floats = mat.m_SavedProperties.m_Floats?.ToDictionary(x => x.Key, x => x.Value),
                        ints = mat.m_SavedProperties.m_Ints?.ToDictionary(x => x.Key, x => x.Value),
                        colors = mat.m_SavedProperties.m_Colors?.ToDictionary(x => x.Key, x => new[] { x.Value.R, x.Value.G, x.Value.B, x.Value.A }),
                        keywords,
                        invalid_keywords = invalid,
                        render_queue = queue,
                        tags,
                        used_by = users,
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error($"[xray] material {mat.m_Name}: {ex}");
                }
            }
            WriteJson(Path.Combine(outDir, "materials.json"), list);
            return (list.Count, exportShaders ? shaders.Values.Count(n => n != null) : 0);
        }

        private static string ShaderName(AssetStudio.Object sh)
        {
            var t = sh.ToType();
            return t?["m_ParsedForm"] is OrderedDictionary pf ? pf["m_Name"] as string : (sh as NamedObject)?.m_Name;
        }

        // key / value pairs of a type tree map, whatever form the reader gave them
        private static IEnumerable<(object, object)> Pairs(object list)
        {
            if (list is not IList l)
                yield break;
            foreach (var e in l)
            {
                if (e is KeyValuePair<object, object> kv)
                    yield return (kv.Key, kv.Value);
                else if (e is DictionaryEntry de)
                    yield return (de.Key, de.Value);
                else if (e is OrderedDictionary od && od.Contains("first"))
                    yield return (od["first"], od["second"]);
            }
        }

        private static IEnumerable<object> Items(object list) => list is IList l ? l.Cast<object>() : Enumerable.Empty<object>();

        private static OrderedDictionary Dict(object o, string key) => o is OrderedDictionary d && d.Contains(key) ? d[key] as OrderedDictionary : null;

        private static object Val(object o, string key) => o is OrderedDictionary d && d.Contains(key) ? d[key] : null;

        // a render state value: {val, name}; a property name in brackets when the state is bound to a material property
        private static object StateValue(OrderedDictionary st, string key, string[] names = null)
        {
            if (Dict(st, key) is not OrderedDictionary v)
                return null;
            var name = v["name"] as string;
            if (!string.IsNullOrEmpty(name) && name != "<noninit>")
                return "[" + name + "]";
            var f = Convert.ToSingle(v["val"]);
            if (names != null && f >= 0 && f < names.Length && f == Math.Floor(f))
                return names[(int)f];
            return f;
        }

        private static List<OrderedDictionary> SubPrograms(OrderedDictionary stage) => Items(stage["m_SubPrograms"]).Concat(Items(Val(stage, "m_PlayerSubPrograms")).SelectMany(x => x is IList l ? l.Cast<object>() : new[] { x })).OfType<OrderedDictionary>().ToList();

        // shaders\<name>_<path id>.json + the programs (only the list when the shader has more than maxPrograms, 0 = no limit: a surface
        // shader with every lighting / shadow variant has thousands); returns the shader name (null when it has no type tree)
        private static string ExportShader(AssetStudio.Object sh, string dir, Func<SerializedFile, string> bundleKey, int maxPrograms)
        {
            var t = sh.ToType();
            if (t == null || t["m_ParsedForm"] is not OrderedDictionary pf)
            {
                Logger.Warning($"[xray] shader {sh.m_PathID}: no type tree, not exported");
                return null;
            }
            var name = pf["m_Name"] as string ?? sh.m_PathID.ToString();
            var total = Items(pf["m_SubShaders"]).OfType<OrderedDictionary>().SelectMany(ss => Items(ss["m_Passes"]).OfType<OrderedDictionary>()).SelectMany(ps => Stages.Select(s => Dict(ps, s)).Where(s => s != null)).Sum(s => SubPrograms(s).Count);
            var writePrograms = maxPrograms <= 0 || total <= maxPrograms;
            var baseName = Safe(name) + "_" + sh.m_PathID;
            var progDir = Path.Combine(dir, baseName);
            Directory.CreateDirectory(dir);
            var keywordNames = Items(pf["m_KeywordNames"]).Select(x => x as string).ToList();

            // the D3D11 blob (Tarkov ships D3D11 only): its decompressed segments and the entry table at the start of segment 0
            var platforms = Items(t["platforms"]).Select(Convert.ToInt32).ToList();
            var pi = platforms.IndexOf(4);
            if (pi < 0)
                pi = 0;
            var entries = new List<(int offset, int length, int segment)>();
            var segments = new List<byte[]>();
            try
            {
                if (platforms.Count > 0)
                {
                    var blob = t["compressedBlob"] is byte[] bb ? bb : Items(t["compressedBlob"]).Select(Convert.ToByte).ToArray();
                    List<long> Seg(string k)
                    {
                        var v = Items(t[k]).ElementAt(pi);
                        return v is IList l ? l.Cast<object>().Select(Convert.ToInt64).ToList() : new List<long> { Convert.ToInt64(v) };
                    }
                    var offs = Seg("offsets");
                    var clen = Seg("compressedLengths");
                    var dlen = Seg("decompressedLengths");
                    for (var s = 0; s < offs.Count; s++)
                    {
                        var dst = new byte[dlen[s]];
                        var n = K4os.Compression.LZ4.LZ4Codec.Decode(blob, (int)offs[s], (int)clen[s], dst, 0, dst.Length);
                        if (n != dst.Length)
                            throw new InvalidDataException($"segment {s}: lz4 gave {n} of {dst.Length} bytes");
                        segments.Add(dst);
                    }
                    // count, then (offset, length) or, Unity 2019.3+, (offset, length, segment) per entry
                    var s0 = segments[0];
                    var count = BitConverter.ToInt32(s0, 0);
                    var stride = 4 + count * 12 <= s0.Length && Enumerable.Range(0, count).All(i => BitConverter.ToInt32(s0, 4 + i * 12 + 8) is var sg && sg >= 0 && sg < segments.Count) ? 3 : 2;
                    for (var i = 0; i < count; i++)
                    {
                        var at = 4 + i * stride * 4;
                        entries.Add((BitConverter.ToInt32(s0, at), BitConverter.ToInt32(s0, at + 4), stride == 3 ? BitConverter.ToInt32(s0, at + 8) : 0));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[xray] shader {name}: blob not read: {ex.Message}");
                entries.Clear();
            }

            byte[] Program(int blobIndex)
            {
                if (blobIndex < 0 || blobIndex >= entries.Count)
                    return null;
                var (off, len, seg) = entries[blobIndex];
                if (seg >= segments.Count || off < 0 || off + len > segments[seg].Length)
                    return null;
                var data = segments[seg];
                // the sub program header (version, type, keywords, ...) precedes the DXBC container
                for (var at = off; at + 32 <= off + len; at++)
                {
                    if (data[at] != 'D' || data[at + 1] != 'X' || data[at + 2] != 'B' || data[at + 3] != 'C')
                        continue;
                    var size = BitConverter.ToInt32(data, at + 24);
                    if (size > 32 && at + size <= off + len)
                        return data.AsSpan(at, size).ToArray();
                }
                return null;
            }

            var programs = 0;
            var subshaders = new List<object>();
            var ssi = 0;
            foreach (var ss in Items(pf["m_SubShaders"]).OfType<OrderedDictionary>())
            {
                var passes = new List<object>();
                var psi = 0;
                foreach (var ps in Items(ss["m_Passes"]).OfType<OrderedDictionary>())
                {
                    var names = new Dictionary<int, string>();
                    foreach (var (k, v) in Pairs(ps["m_NameIndices"]))
                        names.TryAdd(Convert.ToInt32(v), k as string);
                    string N(object idx) => idx == null ? null : names.TryGetValue(Convert.ToInt32(idx), out var s) ? s : "#" + idx;
                    var st = ps["m_State"] as OrderedDictionary;
                    var rt0 = Dict(st, "rtBlend0") ?? Items(Val(st, "rtBlend")).FirstOrDefault() as OrderedDictionary;
                    var state = st == null ? null : new
                    {
                        name = st["m_Name"],
                        blend = rt0 == null ? null : new
                        {
                            src = StateValue(rt0, "srcBlend", BlendFactors),
                            dst = StateValue(rt0, "destBlend", BlendFactors),
                            op = StateValue(rt0, "blendOp", BlendOps),
                            src_alpha = StateValue(rt0, "srcBlendAlpha", BlendFactors),
                            dst_alpha = StateValue(rt0, "destBlendAlpha", BlendFactors),
                            op_alpha = StateValue(rt0, "blendOpAlpha", BlendOps),
                            color_mask = StateValue(rt0, "colMask"),
                        },
                        separate_blend = st.Contains("rtSeparateBlend") ? st["rtSeparateBlend"] : null,
                        z_test = StateValue(st, "zTest", CompareFuncs),
                        z_write = StateValue(st, "zWrite"),
                        cull = StateValue(st, "culling", CullModes),
                        offset_factor = StateValue(st, "offsetFactor"),
                        offset_units = StateValue(st, "offsetUnits"),
                        alpha_to_mask = StateValue(st, "alphaToMask"),
                        stencil_ref = StateValue(st, "stencilRef"),
                        lighting = st.Contains("lighting") ? st["lighting"] : null,
                        tags = Pairs(Val(Dict(st, "m_Tags"), "tags")).ToDictionary(x => x.Item1.ToString(), x => x.Item2),
                    };
                    var stages = new List<object>();
                    var progs = new List<object>();
                    foreach (var stageKey in Stages)
                    {
                        var sp = Dict(ps, stageKey);
                        if (sp == null)
                            continue;
                        var stage = stageKey.Substring(4).ToLowerInvariant();
                        // constant buffers of the stage's common parameters (offsets in bytes; register = offset / 16)
                        var cp = Dict(sp, "m_CommonParameters");
                        var bindings = Items(Val(cp, "m_ConstantBufferBindings")).OfType<OrderedDictionary>().ToDictionary(b => Convert.ToInt32(b["m_NameIndex"]), b => Convert.ToInt32(b["m_Index"]));
                        var cbs = Items(Val(cp, "m_ConstantBuffers")).OfType<OrderedDictionary>().Select(cb => new
                        {
                            name = N(cb["m_NameIndex"]),
                            slot = bindings.TryGetValue(Convert.ToInt32(cb["m_NameIndex"]), out var slot) ? slot : (int?)null,
                            size = cb["m_Size"],
                            vectors = Items(cb["m_VectorParams"]).OfType<OrderedDictionary>().Select(p => new { name = N(p["m_NameIndex"]), offset = Convert.ToInt32(p["m_Index"]), dim = p["m_Dim"], array = p["m_ArraySize"] }).ToArray(),
                            matrices = Items(cb["m_MatrixParams"]).OfType<OrderedDictionary>().Select(p => new { name = N(p["m_NameIndex"]), offset = Convert.ToInt32(p["m_Index"]), rows = p["m_RowCount"], array = p["m_ArraySize"] }).ToArray(),
                        }).ToList();
                        var texParams = Items(Val(cp, "m_TextureParams")).OfType<OrderedDictionary>().Select(p => new { name = N(p["m_NameIndex"]), index = p["m_Index"], sampler = p["m_SamplerIndex"], dim = p.Contains("m_Dim") ? p["m_Dim"] : null }).ToList();
                        var subs = SubPrograms(sp);
                        if (subs.Count == 0 && cbs.Count == 0)
                            continue;
                        stages.Add(new { stage, constant_buffers = cbs, textures = texParams });
                        var n = 0;
                        foreach (var sub in subs)
                        {
                            var bi = Convert.ToInt32(sub["m_BlobIndex"]);
                            var kw = (sub.Contains("m_KeywordIndices") ? Items(sub["m_KeywordIndices"]) : Items(Val(sub, "m_GlobalKeywordIndices")).Concat(Items(Val(sub, "m_LocalKeywordIndices")))).Select(Convert.ToInt32).Select(i => i < keywordNames.Count ? keywordNames[i] : "#" + i).ToArray();
                            var gpu = Convert.ToInt32(sub["m_GpuProgramType"]);
                            var code = writePrograms ? Program(bi) : null;
                            string dxbc = null, asm = null;
                            if (code != null)
                            {
                                Directory.CreateDirectory(progDir);
                                var fn = $"{ssi}_{psi}_{stage}_{n}";
                                File.WriteAllBytes(Path.Combine(progDir, fn + ".dxbc"), code);
                                dxbc = baseName + "/" + fn + ".dxbc";
                                var text = Disassemble(code);
                                if (text != null)
                                {
                                    var head = new StringBuilder();
                                    head.AppendLine($"// {name}, subshader {ssi} pass {psi} {stage}, keywords: {(kw.Length == 0 ? "-" : string.Join(" ", kw))}");
                                    foreach (var cb in cbs)
                                    {
                                        head.AppendLine($"// cb{cb.slot?.ToString() ?? "?"} {cb.name} ({cb.size} bytes)");
                                        foreach (var p in cb.vectors.Select(v => (v.offset, v.name, what: $"vec{v.dim}")).Concat(cb.matrices.Select(m => (m.offset, m.name, what: $"mat{m.rows}"))).OrderBy(x => x.offset))
                                            head.AppendLine($"//   [{p.offset / 16}].{"xyzw"[p.offset % 16 / 4]} {p.name} ({p.what})");
                                    }
                                    foreach (var tp in texParams)
                                        head.AppendLine($"// t{tp.index} s{tp.sampler} {tp.name}");
                                    File.WriteAllText(Path.Combine(progDir, fn + ".asm"), head + text, new UTF8Encoding(false));
                                    asm = baseName + "/" + fn + ".asm";
                                }
                                programs++;
                            }
                            progs.Add(new { stage, blob = bi, gpu_program_type = Enum.IsDefined(typeof(ShaderGpuProgramType), gpu) ? ((ShaderGpuProgramType)gpu).ToString() : gpu.ToString(), keywords = kw, dxbc, asm });
                            n++;
                        }
                    }
                    passes.Add(new { index = psi, name = ps["m_UseName"] as string is { Length: > 0 } un ? un : st?["m_Name"], type = ps["m_Type"], tags = Pairs(Val(Dict(ps, "m_Tags"), "tags")).ToDictionary(x => x.Item1.ToString(), x => x.Item2), state, stages, programs = progs });
                    psi++;
                }
                subshaders.Add(new { index = ssi, lod = ss.Contains("m_LOD") ? ss["m_LOD"] : null, tags = Pairs(Val(Dict(ss, "m_Tags"), "tags")).ToDictionary(x => x.Item1.ToString(), x => x.Item2), passes });
                ssi++;
            }
            var props = Items(Val(Dict(pf, "m_PropInfo"), "m_Props")).OfType<OrderedDictionary>().Select(p =>
            {
                var type = Convert.ToInt32(p["m_Type"]);
                return new
                {
                    name = p["m_Name"],
                    description = p["m_Description"],
                    type = type >= 0 && type < PropertyTypes.Length ? PropertyTypes[type] : type.ToString(),
                    attributes = p.Contains("m_Attributes") ? p["m_Attributes"] : null,
                    flags = p.Contains("m_Flags") ? p["m_Flags"] : null,
                    @default = new[] { p["m_DefValue[0]"], p["m_DefValue[1]"], p["m_DefValue[2]"], p["m_DefValue[3]"] },
                    texture = Dict(p, "m_DefTexture") is OrderedDictionary dt ? new { name = dt["m_DefaultName"], dim = dt["m_TexDim"] } : null,
                };
            }).ToArray();
            WriteJson(Path.Combine(dir, baseName + ".json"), new
            {
                name,
                id = BehaviourId(sh),
                bundle = bundleKey(sh.assetsFile),
                file = sh.assetsFile.fileName,
                path_id = sh.m_PathID,
                platforms,
                program_count = total,
                programs_written = writePrograms,
                properties = props,
                keywords = keywordNames,
                fallback = pf.Contains("m_FallbackName") ? pf["m_FallbackName"] : null,
                subshaders,
            });
            Logger.Info(writePrograms ? $"[xray] shader {name}: {subshaders.Count} subshaders, {programs} programs -> {Path.Combine(dir, baseName)}.json" : $"[xray] shader {name}: {subshaders.Count} subshaders, {total} programs listed, none written (more than --shader-max {maxPrograms}) -> {Path.Combine(dir, baseName)}.json");
            return name;
        }

        // D3DDisassemble from d3dcompiler_47.dll (Windows); null when it is missing
        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        private static extern int D3DDisassemble(byte[] src, UIntPtr size, uint flags, string comments, out IntPtr disassembly);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr BlobPointer(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate UIntPtr BlobSize(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint BlobRelease(IntPtr self);

        private static bool noDisassembler;

        private static string Disassemble(byte[] code)
        {
            if (noDisassembler)
                return null;
            try
            {
                if (D3DDisassemble(code, (UIntPtr)code.Length, 0, null, out var blob) < 0 || blob == IntPtr.Zero)
                    return null;
                // ID3DBlob: IUnknown (0-2), GetBufferPointer (3), GetBufferSize (4)
                var vtbl = Marshal.ReadIntPtr(blob);
                IntPtr Slot(int i) => Marshal.ReadIntPtr(vtbl, i * IntPtr.Size);
                var ptr = Marshal.GetDelegateForFunctionPointer<BlobPointer>(Slot(3))(blob);
                var size = (int)Marshal.GetDelegateForFunctionPointer<BlobSize>(Slot(4))(blob);
                var text = Marshal.PtrToStringAnsi(ptr, size).TrimEnd('\0');
                Marshal.GetDelegateForFunctionPointer<BlobRelease>(Slot(2))(blob);
                return text;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                noDisassembler = true;
                Logger.Warning("[xray] d3dcompiler_47.dll not found: shader programs written as .dxbc only");
                return null;
            }
        }
    }

    // Unity's ShaderGpuProgramType (the names in gpu_program_type)
    internal enum ShaderGpuProgramType
    {
        Unknown = 0,
        GLLegacy = 1,
        GLES31AEP = 2,
        GLES31 = 3,
        GLES3 = 4,
        GLES = 5,
        GLCore32 = 6,
        GLCore41 = 7,
        GLCore43 = 8,
        DX9VertexSM20 = 9,
        DX9VertexSM30 = 10,
        DX9PixelSM20 = 11,
        DX9PixelSM30 = 12,
        DX10Level9Vertex = 13,
        DX10Level9Pixel = 14,
        DX11VertexSM40 = 15,
        DX11VertexSM50 = 16,
        DX11PixelSM40 = 17,
        DX11PixelSM50 = 18,
        DX11GeometrySM40 = 19,
        DX11GeometrySM50 = 20,
        DX11HullSM50 = 21,
        DX11DomainSM50 = 22,
        MetalVS = 23,
        MetalFS = 24,
        SPIRV = 25,
        ConsoleVS = 26,
        ConsoleFS = 27,
        ConsoleHS = 28,
        ConsoleDS = 29,
        ConsoleGS = 30,
        RayTracing = 31,
        PS5NGGC = 32
    }
}
