using System;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    [Flags]
    public enum ArchiveFlags
    {
        CompressionTypeMask = 0x3f,
        BlocksAndDirectoryInfoCombined = 0x40,
        BlocksInfoAtTheEnd = 0x80,
        OldWebPluginCompatibility = 0x100,
        BlockInfoNeedPaddingAtStart = 0x200
    }

    [Flags]
    public enum StorageBlockFlags
    {
        CompressionTypeMask = 0x3f,
        Streamed = 0x40
    }

    public enum CompressionType
    {
        None,
        Lzma,
        Lz4,
        Lz4HC,
        Lzham,
    }

    // UnityFS asset bundles (Unity 5.3+) with LZMA, LZ4 or no compression
    public class BundleFile
    {
        public class Header
        {
            public string signature;
            public uint version;
            public string unityVersion;
            public UnityVersion unityRevision;
            public long size;
            public uint compressedBlocksInfoSize;
            public uint uncompressedBlocksInfoSize;
            public ArchiveFlags flags;
        }

        public class StorageBlock
        {
            public uint compressedSize;
            public uint uncompressedSize;
            public StorageBlockFlags flags;
        }

        public class Node
        {
            public long offset;
            public long size;
            public uint flags;
            public string path;
        }

        public Header m_Header;
        private StorageBlock[] m_BlocksInfo;
        private Node[] m_DirectoryInfo;

        public StreamFile[] fileList;

        public BundleFile(FileReader reader)
        {
            m_Header = new Header();
            m_Header.signature = reader.ReadStringToNull();
            m_Header.version = reader.ReadUInt32();
            m_Header.unityVersion = reader.ReadStringToNull();
            m_Header.unityRevision = new UnityVersion(reader.ReadStringToNull());
            if (m_Header.signature != "UnityFS")
                throw new NotSupportedException($"Unsupported bundle format {m_Header.signature} (UnityFS only)");
            ReadHeader(reader);
            if (m_Header.size > reader.BaseStream.Length)
            {
                Logger.Warning("Bundle size is incorrect.");
            }
            ReadBlocksInfoAndDirectory(reader, m_Header.unityRevision);
            using (var blocksStream = CreateBlocksStream(reader.FullPath))
            {
                ReadBlocks(reader, blocksStream);
                ReadFiles(blocksStream, reader.FullPath);
            }
        }

        private Stream CreateBlocksStream(string path)
        {
            Stream blocksStream;
            var uncompressedSizeSum = m_BlocksInfo.Sum(x => x.uncompressedSize);
            if (uncompressedSizeSum >= int.MaxValue)
            {
                blocksStream = new FileStream(path + ".temp", FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            }
            else
            {
                blocksStream = new MemoryStream((int)uncompressedSizeSum);
            }
            return blocksStream;
        }

        public void ReadFiles(Stream blocksStream, string path)
        {
            fileList = new StreamFile[m_DirectoryInfo.Length];
            for (int i = 0; i < m_DirectoryInfo.Length; i++)
            {
                var node = m_DirectoryInfo[i];
                var file = new StreamFile();
                fileList[i] = file;
                file.path = node.path;
                file.fileName = Path.GetFileName(node.path);
                if (node.size >= int.MaxValue)
                {
                    var extractPath = path + "_unpacked" + Path.DirectorySeparatorChar;
                    Directory.CreateDirectory(extractPath);
                    file.stream = new FileStream(extractPath + file.fileName, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                }
                else
                {
                    file.stream = new MemoryStream((int)node.size);
                }
                blocksStream.Position = node.offset;
                blocksStream.CopyTo(file.stream, node.size);
                file.stream.Position = 0;
            }
        }

        private void ReadHeader(FileReader reader)
        {
            m_Header.size = reader.ReadInt64();
            m_Header.compressedBlocksInfoSize = reader.ReadUInt32();
            m_Header.uncompressedBlocksInfoSize = reader.ReadUInt32();
            m_Header.flags = (ArchiveFlags)reader.ReadUInt32();
        }

        private void ReadBlocksInfoAndDirectory(FileReader reader, UnityVersion unityVer)
        {
            byte[] blocksInfoBytes;

            if (m_Header.version >= 7)
            {
                reader.AlignStream(16);
            }
            else if (unityVer >= (2019, 4) && m_Header.flags != ArchiveFlags.BlocksAndDirectoryInfoCombined)
            {
                //check if we need to align the reader
                //- align to 16 bytes and check if all are 0
                //- if not, reset the reader to the previous position
                var preAlign = reader.Position;
                var alignData = reader.ReadBytes((16 - (int)(preAlign % 16)) % 16);
                if (alignData.Any(x => x != 0))
                {
                    reader.Position = preAlign;
                }
            }

            var compressedSize = (int)m_Header.compressedBlocksInfoSize;
            var uncompressedSize = (int)m_Header.uncompressedBlocksInfoSize;
            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
            {
                var position = reader.Position;
                reader.Position = m_Header.size - compressedSize;
                blocksInfoBytes = reader.ReadBytes(compressedSize);
                reader.Position = position;
            }
            else //0x40 BlocksAndDirectoryInfoCombined
            {
                blocksInfoBytes = reader.ReadBytes(compressedSize);
            }

            var compressionType = (CompressionType)(m_Header.flags & ArchiveFlags.CompressionTypeMask);
            Logger.Debug($"BlockInfo compression: {compressionType}");

            int numWrite;
            var errorMsg = string.Empty;
            MemoryStream blocksInfoUncompressedStream;
            switch (compressionType)
            {
                case CompressionType.None:
                {
                    blocksInfoUncompressedStream = new MemoryStream(blocksInfoBytes);
                    numWrite = compressedSize;
                    break;
                }
                case CompressionType.Lzma:
                {
                    blocksInfoUncompressedStream = new MemoryStream(uncompressedSize);
                    using (var blocksInfoCompressedStream = new MemoryStream(blocksInfoBytes))
                    {
                        numWrite = (int)BundleDecompressionHelper.DecompressLzmaStream(blocksInfoCompressedStream, blocksInfoUncompressedStream, compressedSize, uncompressedSize, ref errorMsg);
                    }
                    blocksInfoUncompressedStream.Position = 0;
                    break;
                }
                case CompressionType.Lz4:
                case CompressionType.Lz4HC:
                {
                    var uncompressedBytes = new byte[uncompressedSize];
                    numWrite = BundleDecompressionHelper.DecompressLz4(blocksInfoBytes, uncompressedBytes, ref errorMsg);
                    blocksInfoUncompressedStream = new MemoryStream(uncompressedBytes);
                    break;
                }
                default:
                    throw new IOException($"Unsupported blockInfo compression type: {compressionType}.\n");
            }

            if (numWrite != uncompressedSize)
            {
                throw new IOException($"{compressionType} blockInfo decompression error. {errorMsg}\nWrite {numWrite} bytes but expected {uncompressedSize} bytes.\nBlockInfo data might be encrypted.\n");
            }

            using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompressedStream))
            {
                var uncompressedDataHash = blocksInfoReader.ReadBytes(16);
                var blocksInfoCount = blocksInfoReader.ReadInt32();
                m_BlocksInfo = new StorageBlock[blocksInfoCount];
                for (int i = 0; i < blocksInfoCount; i++)
                {
                    m_BlocksInfo[i] = new StorageBlock
                    {
                        uncompressedSize = blocksInfoReader.ReadUInt32(),
                        compressedSize = blocksInfoReader.ReadUInt32(),
                        flags = (StorageBlockFlags)blocksInfoReader.ReadUInt16()
                    };
                }

                var nodesCount = blocksInfoReader.ReadInt32();
                m_DirectoryInfo = new Node[nodesCount];
                for (int i = 0; i < nodesCount; i++)
                {
                    m_DirectoryInfo[i] = new Node
                    {
                        offset = blocksInfoReader.ReadInt64(),
                        size = blocksInfoReader.ReadInt64(),
                        flags = blocksInfoReader.ReadUInt32(),
                        path = blocksInfoReader.ReadStringToNull(),
                    };
                }
            }
            if ((m_Header.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
            {
                reader.AlignStream(16);
            }
        }

        private void ReadBlocks(FileReader reader, Stream blocksStream)
        {
            Logger.Debug($"Block compression: {(CompressionType)m_BlocksInfo.Max(x => x.flags)}");

            foreach (var blockInfo in m_BlocksInfo)
            {
                var compressionType = (CompressionType)(blockInfo.flags & StorageBlockFlags.CompressionTypeMask);
                long numWrite;
                var errorMsg = string.Empty;
                switch (compressionType)
                {
                    case CompressionType.None:
                    {
                        reader.BaseStream.CopyTo(blocksStream, blockInfo.compressedSize);
                        numWrite = blockInfo.compressedSize;
                        break;
                    }
                    case CompressionType.Lzma:
                    {
                        numWrite = BundleDecompressionHelper.DecompressLzmaStream(reader.BaseStream, blocksStream, blockInfo.compressedSize, blockInfo.uncompressedSize, ref errorMsg);
                        break;
                    }
                    case CompressionType.Lz4:
                    case CompressionType.Lz4HC:
                    {
                        var compressedSize = (int)blockInfo.compressedSize;
                        var uncompressedSize = (int)blockInfo.uncompressedSize;

                        var compressedBytes = BigArrayPool<byte>.Shared.Rent(compressedSize);
                        var uncompressedBytes = BigArrayPool<byte>.Shared.Rent(uncompressedSize);
                        try
                        {
                            _ = reader.Read(compressedBytes, 0, compressedSize);
                            var compressedSpan = new ReadOnlySpan<byte>(compressedBytes, 0, compressedSize);
                            var uncompressedSpan = new Span<byte>(uncompressedBytes, 0, uncompressedSize);

                            numWrite = BundleDecompressionHelper.DecompressLz4(compressedSpan, uncompressedSpan, ref errorMsg);
                            if (numWrite == uncompressedSize)
                                blocksStream.Write(uncompressedBytes, 0, uncompressedSize);
                        }
                        finally
                        {
                            BigArrayPool<byte>.Shared.Return(compressedBytes, clearArray: true);
                            BigArrayPool<byte>.Shared.Return(uncompressedBytes, clearArray: true);
                        }
                        break;
                    }
                    default:
                        throw new IOException($"Unsupported block compression type: {compressionType}.\n");
                }

                if (numWrite != blockInfo.uncompressedSize)
                {
                    throw new IOException($"{compressionType} block decompression error. {errorMsg}\nWrite {numWrite} bytes but expected {blockInfo.uncompressedSize} bytes.\nBlock data might be encrypted.\n");
                }
            }
            blocksStream.Position = 0;
        }
    }
}
