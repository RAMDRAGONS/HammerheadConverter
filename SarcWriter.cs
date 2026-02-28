using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HammerheadConverter
{
    /// <summary>
    /// Minimal SARC archive writer.
    /// Writes little-endian SARC archives compatible with Nintendo Switch.
    /// SARC structure: Header(0x14) → SFAT(file table) → SFNT(filename table) → Data
    /// </summary>
    public static class SarcWriter
    {
        private const uint HashKey = 0x65;

        /// <summary>
        /// Write a SARC archive from a dictionary of filename → data.
        /// Files are sorted by hash for proper SFAT ordering.
        /// Data alignment defaults to 0x100 (256 bytes) which is standard for Switch.
        /// </summary>
        public static byte[] Write(Dictionary<string, byte[]> files, int dataAlignment = 0x100)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Sort files by hash (SARC requirement)
            var sorted = files
                .Select(f => (Name: f.Key, Data: f.Value, Hash: CalcHash(f.Key)))
                .OrderBy(f => f.Hash)
                .ToList();

            ushort nodeCount = (ushort)sorted.Count;

            // Calculate SFNT section: null-terminated filenames, each padded to 4-byte boundary
            var nameOffsets = new List<uint>();
            int sfntDataSize = 0;
            foreach (var file in sorted)
            {
                nameOffsets.Add((uint)(sfntDataSize / 4)); // stored as /4 in attrs
                int nameLen = Encoding.ASCII.GetByteCount(file.Name) + 1; // +1 for null
                sfntDataSize += Align(nameLen, 4);
            }

            // Header sizes
            int sarcHeaderSize = 0x14;
            int sfatHeaderSize = 0x0C;
            int sfatNodeSize = 0x10 * nodeCount;
            int sfntHeaderSize = 0x08;

            int headersEnd = sarcHeaderSize + sfatHeaderSize + sfatNodeSize + sfntHeaderSize + sfntDataSize;
            int dataOffset = Align(headersEnd, dataAlignment);

            // Calculate data offsets for each file (relative to data section start)
            var dataOffsets = new List<(uint start, uint end)>();
            int currentDataPos = 0;
            foreach (var file in sorted)
            {
                int alignedStart = Align(currentDataPos, GetFileAlignment(file.Name));
                dataOffsets.Add(((uint)alignedStart, (uint)(alignedStart + file.Data.Length)));
                currentDataPos = alignedStart + file.Data.Length;
            }

            uint totalFileSize = (uint)(dataOffset + currentDataPos);

            // === Write SARC Header (0x14) ===
            writer.Write(Encoding.ASCII.GetBytes("SARC"));  // magic
            writer.Write((ushort)0x14);                      // header size
            writer.Write((ushort)0xFEFF);                    // BOM (LE)
            writer.Write(totalFileSize);                      // file size
            writer.Write((uint)dataOffset);                   // data offset
            writer.Write((ushort)0x0100);                    // version
            writer.Write((ushort)0x0000);                    // reserved

            // === Write SFAT Header ===
            writer.Write(Encoding.ASCII.GetBytes("SFAT"));  // magic
            writer.Write((ushort)0x0C);                      // header size
            writer.Write(nodeCount);                          // node count
            writer.Write(HashKey);                            // hash key

            // === Write SFAT Nodes ===
            for (int i = 0; i < sorted.Count; i++)
            {
                writer.Write(sorted[i].Hash);
                // Attrs: bit 24 set = has filename, lower 24 bits = name offset / 4
                uint attrs = 0x01000000 | (nameOffsets[i] & 0x00FFFFFF);
                writer.Write(attrs);
                writer.Write(dataOffsets[i].start);
                writer.Write(dataOffsets[i].end);
            }

            // === Write SFNT Header ===
            writer.Write(Encoding.ASCII.GetBytes("SFNT"));
            writer.Write((ushort)0x08);
            writer.Write((ushort)0x0000);

            // === Write SFNT Data (filenames) ===
            foreach (var file in sorted)
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(file.Name);
                writer.Write(nameBytes);
                // Null terminator + padding to 4-byte boundary
                int padLen = Align(nameBytes.Length + 1, 4) - nameBytes.Length;
                writer.Write(new byte[padLen]);
            }

            // === Pad to data section ===
            int currentPos = (int)ms.Position;
            if (currentPos < dataOffset)
                writer.Write(new byte[dataOffset - currentPos]);

            // === Write file data ===
            int dataPos = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                int alignedStart = (int)dataOffsets[i].start;
                if (dataPos < alignedStart)
                    writer.Write(new byte[alignedStart - dataPos]);
                writer.Write(sorted[i].Data);
                dataPos = alignedStart + sorted[i].Data.Length;
            }

            return ms.ToArray();
        }

        /// <summary>
        /// SARC filename hash (Multiply-hash with key 0x65).
        /// </summary>
        private static uint CalcHash(string name)
        {
            uint hash = 0;
            foreach (char c in name)
                hash = hash * HashKey + c;
            return hash;
        }

        /// <summary>
        /// Determine alignment for a file based on extension.
        /// bfres and bfsha need higher alignment.
        /// </summary>
        private static int GetFileAlignment(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ext switch
            {
                ".bfres" => 0x1000,  // 4KB alignment
                ".bfsha" => 0x1000,
                _ => 0x80            // 128-byte default
            };
        }

        private static int Align(int value, int alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }
    }
}
