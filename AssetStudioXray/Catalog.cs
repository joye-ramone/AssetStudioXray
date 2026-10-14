using AssetStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AssetStudioXray
{
    // Catalog: what a group of bundles holds, without exporting the data (command 'catalog', one <out>\catalog\<group>.json per group).
    //   bundles       own bundle keys and file sizes
    //   models        own root objects with renderers: node count, renderers (mesh, vertices, bones, sub-meshes, materials with their textures)
    //   textures      own Texture2D: name, size, format
    //   controllers   own animator controllers: per layer the states (name, tag, speed, loop, clips), the layer's mask size
    //   overrides     own override controllers: base controller, original -> override clip pairs
    //   clips         own clips: length, sample rate, loop, events (function counts), animated transforms by kind (bone names resolved
    //                 through every loaded hierarchy and avatar: CRC32 of each path suffix) and the resolved node names
    //   behaviours    own MonoBehaviours per script class; audio: own AudioClip count
    internal static class Catalog
    {
        public static object Group(AssetsManager am, Func<SerializedFile, bool> isOwn)
        {
            var all = am.assetsFileList.SelectMany(f => f.Objects).ToList();
            bool Own(AssetStudio.Object o) => isOwn(o.assetsFile);

            // path hash -> node name: every suffix of every transform path in the loaded hierarchies, plus the avatars' path tables
            var names = new Dictionary<uint, string>();
            foreach (var t in all.OfType<Transform>())
            {
                var parts = new List<string>();
                for (var x = t; x != null; x = x.m_Father.TryGet(out var f) ? f : null)
                    parts.Add(x.m_GameObject.TryGet(out var g) ? g.m_Name : "?");
                parts.Reverse();
                for (var i = 0; i < parts.Count; i++)
                    names.TryAdd(Crc(string.Join("/", parts.Skip(i))), parts[^1]);
            }
            foreach (var av in all.OfType<Avatar>())
            {
                foreach (var kv in av.m_TOS ?? Array.Empty<KeyValuePair<uint, string>>())
                {
                    if (!string.IsNullOrEmpty(kv.Value))
                        names.TryAdd(kv.Key, kv.Value.Substring(kv.Value.LastIndexOf('/') + 1));
                }
            }

            var models = all.OfType<GameObject>().Where(g => Own(g) && g.m_Transform != null && !g.m_Transform.m_Father.TryGet(out _)).Select(g => Model(g)).Where(m => m != null).ToList();
            var textures = all.OfType<Texture2D>().Where(Own).OrderBy(t => t.m_Name).Select(t => new { name = t.m_Name, w = t.m_Width, h = t.m_Height, format = t.m_TextureFormat.ToString() }).ToList();
            var controllers = all.OfType<AnimatorController>().Where(Own).OrderBy(c => c.m_Name).Select(Controller).ToList();
            var overrides = all.OfType<AnimatorOverrideController>().Where(Own).OrderBy(o => o.m_Name).Select(o => new
            {
                name = o.m_Name,
                @base = o.m_Controller.TryGet(out var b) ? b.m_Name : null,
                pairs = o.m_Clips.Select(p => new[] { p.m_OriginalClip.TryGet(out var a) ? a.m_Name : null, p.m_OverrideClip.TryGet(out var c) ? c.m_Name : null }).ToArray(),
            }).ToList();
            var clips = all.OfType<AnimationClip>().Where(Own).OrderBy(c => c.m_Name).Select(c => Clip(c, names)).ToList();
            var behaviours = all.OfType<MonoBehaviour>().Where(Own).GroupBy(m => m.m_Script.TryGet(out var s) ? s.m_ClassName : "?").OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
            var audio = all.OfType<AudioClip>().Count(Own);
            return new { objects = all.Count, own_objects = all.Count(Own), models, textures, controllers, overrides, clips, behaviours, audio };
        }

        private static object Model(GameObject root)
        {
            var nodes = 0;
            var renderers = new List<object>();
            void Walk(Transform t)
            {
                nodes++;
                if (t.m_GameObject.TryGet(out var go))
                {
                    Renderer r = go.m_SkinnedMeshRenderer != null ? go.m_SkinnedMeshRenderer : go.m_MeshFilter != null ? go.m_MeshRenderer : null;
                    if (r != null)
                        renderers.Add(Renderer(r, go.m_Name));
                }
                foreach (var c in t.m_Children)
                {
                    if (c.TryGet(out var ct))
                        Walk(ct);
                }
            }
            Walk(root.m_Transform);
            if (renderers.Count == 0 && root.m_Animator == null)
                return null;
            return new { name = root.m_Name, nodes, animator = root.m_Animator != null, renderers };
        }

        private static object Renderer(Renderer r, string node)
        {
            Mesh mesh = null;
            var bones = 0;
            if (r is SkinnedMeshRenderer smr)
            {
                smr.m_Mesh.TryGet(out mesh);
                bones = smr.m_Bones.Length;
            }
            else if (r.m_GameObject.TryGet(out var go) && go.m_MeshFilter != null)
            {
                go.m_MeshFilter.m_Mesh.TryGet(out mesh);
            }
            var materials = r.m_Materials.Select(mp => mp.TryGet(out var mat) ? new
            {
                name = mat.m_Name,
                textures = mat.m_SavedProperties.m_TexEnvs.Where(te => te.Value.m_Texture.TryGet(out _)).ToDictionary(te => te.Key, te => te.Value.m_Texture.TryGet(out var tx) ? tx.m_Name : null),
            } : null).ToArray();
            return new
            {
                node,
                kind = r is SkinnedMeshRenderer ? "skinned" : "mesh",
                mesh = mesh?.m_Name,
                vertices = mesh?.m_VertexCount ?? 0,
                triangles = mesh == null ? 0 : mesh.m_SubMeshes.Sum(s => (long)s.indexCount) / 3,
                submeshes = mesh?.m_SubMeshes.Length ?? 0,
                bones,
                blend_shapes = mesh?.m_Shapes?.channels?.Length ?? 0,
                materials,
            };
        }

        private static object Controller(AnimatorController ctrl)
        {
            var tos = ctrl.m_TOS?.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Value) ?? new Dictionary<uint, string>();
            string Str(uint id) => tos.TryGetValue(id, out var s) ? s : id.ToString();
            var clipNames = ctrl.m_AnimationClips.Select(p => p.TryGet(out var c) ? c.m_Name : null).ToArray();
            var c0 = ctrl.m_Controller;
            var layers = c0.m_LayerArray.Select((l, li) =>
            {
                var sm = l.m_StateMachineIndex < c0.m_StateMachineArray.Length ? c0.m_StateMachineArray[l.m_StateMachineIndex] : null;
                return new
                {
                    index = li,
                    state_machine = l.m_StateMachineIndex,
                    weight = l.m_DefaultWeight,
                    blending = l.m_LayerBlendingMode,
                    mask = l.m_SkeletonMask?.m_Data?.Length ?? 0,
                    states = sm?.m_StateConstantArray.Select(st => new
                    {
                        name = Str(st.m_NameID),
                        tag = st.m_TagID == 0 ? null : Str(st.m_TagID),
                        speed = st.m_Speed,
                        loop = st.m_Loop,
                        clips = st.m_BlendTreeConstantArray.SelectMany(bt => bt.m_NodeArray).Where(n => n.m_ClipID != uint.MaxValue && n.m_ChildIndices.Length == 0).Select(n => n.m_ClipID < clipNames.Length ? clipNames[n.m_ClipID] : $"#{n.m_ClipID}").Distinct().ToArray(),
                        transitions = st.m_TransitionConstantArray.Length,
                    }).ToArray(),
                };
            }).ToArray();
            return new { name = ctrl.m_Name, clips = clipNames.Length, layers };
        }

        private static object Clip(AnimationClip clip, Dictionary<uint, string> names)
        {
            var kinds = new SortedDictionary<string, int>();
            var nodes = new SortedSet<string>(StringComparer.Ordinal);
            var unresolved = 0;
            float length = 0;
            bool loop = false;
            int streamed = 0, dense = 0, constant = 0;
            if (!clip.m_Legacy && clip.m_MuscleClip != null)
            {
                var mc = clip.m_MuscleClip;
                length = mc.m_StopTime - mc.m_StartTime;
                loop = mc.m_LoopTime;
                var c = mc.m_Clip.data;
                streamed = (int)c.m_StreamedClip.curveCount;
                dense = (int)c.m_DenseClip.m_CurveCount;
                constant = c.m_ConstantClip?.data?.Length ?? 0;
                var bc = clip.m_ClipBindingConstant ?? c.ConvertValueArrayToGenericBinding();
                foreach (var path in bc.genericBindings.Where(b => b.typeID == ClassIDType.Transform).Select(b => b.path).Distinct())
                {
                    if (!names.TryGetValue(path, out var n))
                    {
                        unresolved++;
                        continue;
                    }
                    nodes.Add(n);
                    var k = Kind(n);
                    kinds[k] = kinds.TryGetValue(k, out var v) ? v + 1 : 1;
                }
                var other = bc.genericBindings.Count(b => b.typeID != ClassIDType.Transform);
                if (other > 0)
                    kinds["non_transform"] = other;
            }
            if (unresolved > 0)
                kinds["unresolved"] = unresolved;
            var events = clip.m_Events ?? Array.Empty<AnimationEvent>();
            return new
            {
                name = clip.m_Name,
                legacy = clip.m_Legacy,
                length = MathF.Round(length, 4),
                rate = clip.m_SampleRate,
                loop,
                curves = new[] { streamed, dense, constant },
                events = events.Length,
                event_functions = events.GroupBy(e => e.functionName).ToDictionary(g => g.Key, g => g.Count()),
                tracks = kinds,
                nodes,
            };
        }

        // bone kinds of the Tarkov player skeleton (Base Human*), the FP camera and IK helpers; the rest is the item (weapon parts)
        public static string Kind(string n)
        {
            if (n.StartsWith("Base HumanLDigit") || n.StartsWith("Base HumanRDigit"))
                return n[10] == 'L' ? "l_fingers" : "r_fingers";
            if (n is "Base HumanLCollarbone" or "Base HumanLUpperarm" or "Base HumanLPalm" || n.StartsWith("Base HumanLForearm"))
                return "l_arm";
            if (n is "Base HumanRCollarbone" or "Base HumanRUpperarm" or "Base HumanRPalm" || n.StartsWith("Base HumanRForearm"))
                return "r_arm";
            if (n.StartsWith("Base Human"))
                return "body";
            if (n.StartsWith("Camera", StringComparison.OrdinalIgnoreCase))
                return "camera";
            if (n.StartsWith("IK_") || n.StartsWith("Bend_Goal") || n.EndsWith("_anim") || n is "Root_Joint")
                return "ik";
            return "item";
        }

        private static readonly uint[] crcTable = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static uint Crc(string s)
        {
            var c = 0xFFFFFFFFu;
            foreach (var b in Encoding.UTF8.GetBytes(s))
                c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        public static void Write(string path, object o) => File.WriteAllText(path, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
}
