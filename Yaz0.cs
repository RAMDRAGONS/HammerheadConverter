using System;
using System.IO;

namespace HammerheadConverter
{
    /// <summary>
    /// Standalone Yaz0 decompressor for SZS files.
    /// Yaz0 header: "Yaz0" magic (4), decompressed size BE (4), alignment (4), padding (4) = 16 bytes.
    /// </summary>
    public static class Yaz0
    {
        public static byte[] Decompress(byte[] src)
        {
            if (src.Length < 16 || src[0] != 'Y' || src[1] != 'a' || src[2] != 'z' || src[3] != '0')
                throw new InvalidDataException("Not a Yaz0 file");

            uint decompSize = (uint)(src[4] << 24 | src[5] << 16 | src[6] << 8 | src[7]);
            byte[] dst = new byte[decompSize];

            int srcPos = 16;
            int dstPos = 0;

            while (dstPos < decompSize)
            {
                byte codeByte = src[srcPos++];

                for (int i = 0; i < 8 && dstPos < decompSize; i++)
                {
                    if ((codeByte & 0x80) != 0)
                    {
                        // Direct copy
                        dst[dstPos++] = src[srcPos++];
                    }
                    else
                    {
                        // Back-reference
                        byte b1 = src[srcPos++];
                        byte b2 = src[srcPos++];

                        int dist = ((b1 & 0x0F) << 8) | b2;
                        int copyPos = dstPos - dist - 1;

                        int length = b1 >> 4;
                        if (length == 0)
                            length = src[srcPos++] + 0x12;
                        else
                            length += 2;

                        for (int j = 0; j < length && dstPos < decompSize; j++)
                            dst[dstPos++] = dst[copyPos++];
                    }

                    codeByte <<= 1;
                }
            }

            return dst;
        }

        public static byte[] DecompressFile(string path)
        {
            return Decompress(File.ReadAllBytes(path));
        }

        /// <summary>
        /// Compress data with Yaz0.
        /// Uses greedy back-reference search for reasonable compression.
        /// </summary>
        public static byte[] Compress(byte[] src)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Write Yaz0 header
            writer.Write(new byte[] { (byte)'Y', (byte)'a', (byte)'z', (byte)'0' });
            // Decompressed size (big-endian)
            writer.Write((byte)(src.Length >> 24));
            writer.Write((byte)(src.Length >> 16));
            writer.Write((byte)(src.Length >> 8));
            writer.Write((byte)(src.Length));
            writer.Write(0u); // alignment
            writer.Write(0u); // padding

            int srcPos = 0;
            var codeBuf = new List<byte>();
            var dataBuf = new List<byte>();
            int codeBits = 0;
            byte codeByte = 0;

            while (srcPos < src.Length)
            {
                if (codeBits == 8 || (codeBits == 0 && codeBuf.Count == 0))
                {
                    // Flush previous group
                    if (codeBuf.Count > 0 || codeBits == 8)
                    {
                        ms.WriteByte(codeByte);
                        ms.Write(dataBuf.ToArray(), 0, dataBuf.Count);
                        codeBuf.Clear();
                        dataBuf.Clear();
                    }
                    codeByte = 0;
                    codeBits = 0;
                }

                // Search for back-reference
                int bestLen = 1;
                int bestDist = 0;
                int maxSearchBack = Math.Min(srcPos, 0x1000);
                int maxLen = Math.Min(src.Length - srcPos, 0x111);

                if (maxLen >= 3)
                {
                    for (int dist = 1; dist <= maxSearchBack; dist++)
                    {
                        int matchLen = 0;
                        while (matchLen < maxLen && src[srcPos + matchLen] == src[srcPos - dist + matchLen])
                            matchLen++;

                        if (matchLen > bestLen)
                        {
                            bestLen = matchLen;
                            bestDist = dist;
                            if (bestLen == maxLen) break;
                        }
                    }
                }

                if (bestLen >= 3)
                {
                    // Back-reference
                    codeByte <<= 1; // bit = 0
                    int encodedDist = bestDist - 1;

                    if (bestLen < 0x12)
                    {
                        // 2-byte reference
                        dataBuf.Add((byte)(((bestLen - 2) << 4) | (encodedDist >> 8)));
                        dataBuf.Add((byte)(encodedDist & 0xFF));
                    }
                    else
                    {
                        // 3-byte reference
                        dataBuf.Add((byte)(encodedDist >> 8));
                        dataBuf.Add((byte)(encodedDist & 0xFF));
                        dataBuf.Add((byte)(bestLen - 0x12));
                    }
                    srcPos += bestLen;
                }
                else
                {
                    // Literal byte
                    codeByte = (byte)((codeByte << 1) | 1);
                    dataBuf.Add(src[srcPos++]);
                }

                codeBits++;
            }

            // Flush remaining
            if (codeBits > 0)
            {
                codeByte <<= (8 - codeBits);
                ms.WriteByte(codeByte);
                ms.Write(dataBuf.ToArray(), 0, dataBuf.Count);
            }

            return ms.ToArray();
        }

        public static void CompressFile(byte[] data, string outputPath)
        {
            File.WriteAllBytes(outputPath, Compress(data));
        }
    }
}
