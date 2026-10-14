using System;
using System.IO;
using SevenZip.Compression.LZMA;

namespace BundleCompression.Lzma
{
    public static class SevenZipLzma
    {
        public static long DecompressStream(Stream compressedStream, Stream decompressedStream, long compressedSize, long decompressedSize)
        {
            var basePosition = compressedStream.Position;
            var decoder = new Decoder();
            var properties = new byte[5];
            if (compressedStream.Read(properties, 0, 5) != 5)
                throw new Exception("input .lzma is too short");
            decoder.SetDecoderProperties(properties);
            decoder.Code(compressedStream, decompressedStream, compressedSize - 5, decompressedSize, null);
            compressedStream.Position = basePosition + compressedSize;
            return decompressedStream.Position;
        }
    }
}
