using BundleCompression.Lzma;
using System;
using System.IO;
using System.Text.RegularExpressions;
using K4os.Compression.LZ4;

namespace AssetStudio
{
    public static class BundleDecompressionHelper
    {
        private static readonly string MsgPattern = @"\. ";

        public static long DecompressLzmaStream(Stream compressedStream, Stream decompressedStream, long compressedSize, long decompressedSize, ref string errorMsg)
        {
            var numWrite = -1L;
            try
            {
                numWrite = SevenZipLzma.DecompressStream(compressedStream, decompressedStream, compressedSize, decompressedSize);
            }
            catch (Exception e)
            {
                Logger.Debug(e.ToString());
                errorMsg = $"({Regex.Split(e.Message, MsgPattern, RegexOptions.CultureInvariant)[0]})";
            }
            return numWrite;
        }

        public static int DecompressLz4(ReadOnlySpan<byte> srcBuffer, Span<byte> dstBuffer, ref string errorMsg)
        {
            var numWrite = -1;
            try
            {
                numWrite = LZ4Codec.Decode(srcBuffer, dstBuffer);
            }
            catch (Exception e)
            {
                Logger.Debug(e.ToString());
                errorMsg = $"({Regex.Split(e.Message, MsgPattern, RegexOptions.CultureInvariant)[0]})";
            }
            return numWrite;
        }
    }
}
