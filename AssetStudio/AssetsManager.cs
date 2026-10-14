using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace AssetStudio
{
    // Loads UnityFS asset bundles (and loose serialized files) and reads their objects
    public class AssetsManager
    {
        public List<SerializedFile> assetsFileList = new List<SerializedFile>();

        internal Dictionary<string, int> assetsFileIndexCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal ConcurrentDictionary<string, BinaryReader> resourceFileReaders = new ConcurrentDictionary<string, BinaryReader>(StringComparer.OrdinalIgnoreCase);

        private HashSet<string> assetsFileListHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // classes read by name only: the dumps never use their data, objects.tsv lists their names
        private static readonly HashSet<ClassIDType> NameOnly = new HashSet<ClassIDType>
        {
            ClassIDType.Font, ClassIDType.MovieTexture, ClassIDType.PreloadData, ClassIDType.Sprite, ClassIDType.SpriteAtlas, ClassIDType.Texture2DArray, ClassIDType.VideoClip,
        };

        // files, or folders (every file in them)
        public void LoadFiles(IEnumerable<string> paths)
        {
            var files = new List<string>();
            foreach (var path in paths)
            {
                var fullPath = Path.GetFullPath(path);
                if (Directory.Exists(fullPath))
                    files.AddRange(Directory.GetFiles(fullPath, "*.*", SearchOption.AllDirectories));
                else if (File.Exists(fullPath))
                    files.Add(fullPath);
            }
            foreach (var file in files.Distinct())
            {
                if (!LoadFile(new FileReader(file)))
                    break;
            }
            assetsFileListHash.Clear();

            ReadAssets();
            ProcessAssets();
        }

        private bool LoadFile(FileReader reader)
        {
            switch (reader.FileType)
            {
                case FileType.AssetsFile:
                    return LoadAssetsFile(reader, reader.FullPath);
                case FileType.BundleFile:
                    return LoadBundleFile(reader);
                default:
                    Logger.Warning($"Not an asset bundle or assets file: \"{reader.FullPath}\"");
                    reader.Dispose();
                    return true;
            }
        }

        private bool LoadAssetsFile(FileReader reader, string originalPath, UnityVersion assetBundleUnityVer = null)
        {
            if (!assetsFileListHash.Contains(reader.FileName))
            {
                try
                {
                    var assetsFile = new SerializedFile(reader, this);
                    assetsFile.originalPath = originalPath;
                    if (assetBundleUnityVer != null && assetsFile.header.m_Version < SerializedFileFormatVersion.Unknown_7)
                    {
                        assetsFile.version = assetBundleUnityVer;
                    }
                    if (assetsFile.version.IsStripped)
                    {
                        throw new NotSupportedException($"\"{originalPath}\": the Unity version has been stripped");
                    }
                    assetsFileList.Add(assetsFile);
                    assetsFileListHash.Add(assetsFile.fileName);
                }
                catch (NotSupportedException e)
                {
                    Logger.Error(e.Message);
                    resourceFileReaders.TryAdd(reader.FileName, reader);
                    return false;
                }
                catch (Exception e)
                {
                    Logger.Warning($"Failed to read assets file \"{reader.FullPath}\" from {Path.GetFileName(originalPath)}\n{e}");
                    resourceFileReaders.TryAdd(reader.FileName, reader);
                }
            }
            else
            {
                Logger.Info($"Skipping \"{originalPath}\" ({reader.FileName})");
            }
            return true;
        }

        private bool LoadBundleFile(FileReader reader)
        {
            Logger.Info($"Loading \"{reader.FullPath}\"");
            Logger.Debug($"Bundle offset: {reader.Position}");
            var bundleReader = new FileReader(reader.FullPath, new OffsetStream(reader));
            try
            {
                var bundleFile = new BundleFile(bundleReader);
                foreach (var file in bundleFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(bundleReader.FullPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    if (subReader.FileType == FileType.AssetsFile)
                    {
                        if (!LoadAssetsFile(subReader, bundleReader.FullPath, bundleFile.m_Header.unityRevision))
                            return false;
                    }
                    else
                    {
                        resourceFileReaders.TryAdd(file.fileName, subReader);
                    }
                }
                return true;
            }
            catch (NotSupportedException e)
            {
                Logger.Error(e.Message);
                return false;
            }
            catch (Exception e)
            {
                Logger.Warning($"Error while reading bundle file \"{bundleReader.FullPath}\"\n{e}");
                return true;
            }
            finally
            {
                bundleReader.Dispose();
            }
        }

        public void Clear()
        {
            foreach (var assetsFile in assetsFileList)
            {
                assetsFile.Objects.Clear();
                assetsFile.reader.Close();
            }
            assetsFileList.Clear();

            foreach (var resourceFileReader in resourceFileReaders)
            {
                resourceFileReader.Value.Close();
            }
            resourceFileReaders.Clear();

            assetsFileIndexCache.Clear();
        }

        private void ReadAssets()
        {
            Logger.Info("Read assets...");

            var jsonOptions = new JsonSerializerOptions
            {
                Converters = { new JsonConverterHelper.ByteArrayConverter(), new JsonConverterHelper.PPtrConverter(), new JsonConverterHelper.KVPConverter() },
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                PropertyNameCaseInsensitive = true,
                IncludeFields = true,
            };

            foreach (var assetsFile in assetsFileList)
            {
                JsonConverterHelper.AssetsFile = assetsFile;
                foreach (var objectInfo in assetsFile.m_Objects)
                {
                    var objectReader = new ObjectReader(assetsFile.reader, assetsFile, objectInfo);
                    var typeTree = objectReader.serializedType?.m_Type;
                    try
                    {
                        Object obj;
                        switch (objectReader.type)
                        {
                            case ClassIDType.Animation:
                                obj = new Animation(objectReader);
                                break;
                            case ClassIDType.AnimationClip:
                                obj = typeTree != null
                                    ? new AnimationClip(objectReader, TypeTreeHelper.ReadTypeByteArray(typeTree, objectReader), jsonOptions, objectInfo)
                                    : new AnimationClip(objectReader);
                                break;
                            case ClassIDType.Animator:
                                obj = new Animator(objectReader);
                                break;
                            case ClassIDType.AnimatorController:
                                obj = new AnimatorController(objectReader);
                                break;
                            case ClassIDType.AnimatorOverrideController:
                                obj = new AnimatorOverrideController(objectReader);
                                break;
                            case ClassIDType.AssetBundle:
                                obj = new AssetBundle(objectReader);
                                break;
                            case ClassIDType.AudioClip:
                                obj = new AudioClip(objectReader);
                                break;
                            case ClassIDType.Avatar:
                                obj = new Avatar(objectReader);
                                break;
                            case ClassIDType.GameObject:
                                obj = new GameObject(objectReader);
                                break;
                            case ClassIDType.Material:
                                obj = typeTree != null
                                    ? new Material(objectReader, TypeTreeHelper.ReadTypeByteArray(typeTree, objectReader), jsonOptions)
                                    : new Material(objectReader);
                                break;
                            case ClassIDType.Mesh:
                                obj = new Mesh(objectReader);
                                break;
                            case ClassIDType.MeshFilter:
                                obj = new MeshFilter(objectReader);
                                break;
                            case ClassIDType.MeshRenderer:
                                obj = new MeshRenderer(objectReader);
                                break;
                            case ClassIDType.MonoBehaviour:
                                obj = new MonoBehaviour(objectReader);
                                break;
                            case ClassIDType.MonoScript:
                                obj = new MonoScript(objectReader);
                                break;
                            case ClassIDType.RectTransform:
                                obj = new RectTransform(objectReader);
                                break;
                            case ClassIDType.Shader:
                                // not kept: read through their type tree from the material's PPtr when needed (AssetStudioXray ShaderExport)
                                obj = null;
                                break;
                            case ClassIDType.SkinnedMeshRenderer:
                                obj = new SkinnedMeshRenderer(objectReader);
                                break;
                            case ClassIDType.TextAsset:
                                obj = new TextAsset(objectReader);
                                break;
                            case ClassIDType.Texture2D:
                                obj = typeTree != null
                                    ? new Texture2D(objectReader, TypeTreeHelper.ReadTypeByteArray(typeTree, objectReader), jsonOptions)
                                    : new Texture2D(objectReader);
                                break;
                            case ClassIDType.Transform:
                                obj = new Transform(objectReader);
                                break;
                            default:
                                obj = NameOnly.Contains(objectReader.type) ? new NamedObject(objectReader) : new Object(objectReader);
                                break;
                        }
                        if (obj != null)
                        {
                            assetsFile.AddObject(obj);
                        }
                    }
                    catch (Exception e)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("Unable to load object")
                            .AppendLine($"Assets {assetsFile.fileName}")
                            .AppendLine($"Path {assetsFile.originalPath}")
                            .AppendLine($"Type {objectReader.type}")
                            .AppendLine($"PathID {objectInfo.m_PathID}")
                            .Append(e);
                        Logger.Warning(sb.ToString());
                    }
                }
            }
        }

        private void ProcessAssets()
        {
            Logger.Info("Process assets...");

            foreach (var assetsFile in assetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is GameObject m_GameObject)
                    {
                        foreach (var pptr in m_GameObject.m_Components)
                        {
                            if (pptr.TryGet(out var m_Component))
                            {
                                switch (m_Component)
                                {
                                    case Transform m_Transform:
                                        m_GameObject.m_Transform = m_Transform;
                                        break;
                                    case MeshRenderer m_MeshRenderer:
                                        m_GameObject.m_MeshRenderer = m_MeshRenderer;
                                        break;
                                    case MeshFilter m_MeshFilter:
                                        m_GameObject.m_MeshFilter = m_MeshFilter;
                                        break;
                                    case SkinnedMeshRenderer m_SkinnedMeshRenderer:
                                        m_GameObject.m_SkinnedMeshRenderer = m_SkinnedMeshRenderer;
                                        break;
                                    case Animator m_Animator:
                                        m_GameObject.m_Animator = m_Animator;
                                        break;
                                    case Animation m_Animation:
                                        m_GameObject.m_Animation = m_Animation;
                                        break;
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
