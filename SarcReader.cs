using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HammerheadConverter
{
    /// <summary>
    /// Minimal SARC archive reader.
    /// SARC structure: Header(0x14) → SFAT(file table) → SFNT(filename table) → Data
    /// </summary>
    public class SarcReader
    {
        public Dictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>();

        public SarcReader(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            // SARC Header (0x14 bytes)
            string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (magic != "SARC")
                throw new InvalidDataException($"Not a SARC archive (magic: {magic})");

            ushort headerSize = reader.ReadUInt16();     // 0x14
            ushort bom = reader.ReadUInt16();            // 0xFEFF = LE, 0xFFFE = BE
            bool bigEndian = bom == 0xFFFE;
            if (bigEndian)
                throw new NotSupportedException("Big-endian SARC not supported");

            uint fileSize = reader.ReadUInt32();
            uint dataOffset = reader.ReadUInt32();
            ushort version = reader.ReadUInt16();
            ushort reserved = reader.ReadUInt16();

            // SFAT Header
            string sfatMagic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (sfatMagic != "SFAT")
                throw new InvalidDataException("Missing SFAT header");

            ushort sfatHeaderSize = reader.ReadUInt16(); // 0x0C
            ushort nodeCount = reader.ReadUInt16();
            uint hashKey = reader.ReadUInt32();

            // SFAT Nodes
            var nodes = new List<(uint hash, uint nameOffset, uint dataStart, uint dataEnd)>();
            for (int i = 0; i < nodeCount; i++)
            {
                uint hash = reader.ReadUInt32();
                uint attrs = reader.ReadUInt32();
                uint nodeDataStart = reader.ReadUInt32();
                uint nodeDataEnd = reader.ReadUInt32();

                // Bit 24 of attrs indicates filename is present
                uint nameOfs = (attrs & 0x00FFFFFF) * 4; // multiply by 4 for actual offset
                nodes.Add((hash, nameOfs, nodeDataStart, nodeDataEnd));
            }

            // SFNT Header
            string sfntMagic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (sfntMagic != "SFNT")
                throw new InvalidDataException("Missing SFNT header");

            ushort sfntHeaderSize = reader.ReadUInt16();
            ushort sfntReserved = reader.ReadUInt16();

            long sfntDataStart = ms.Position;

            // Read each file
            foreach (var node in nodes)
            {
                // Read filename from SFNT
                ms.Position = sfntDataStart + node.nameOffset;
                string name = ReadNullTerminatedString(reader);

                // Read file data
                uint start = dataOffset + node.dataStart;
                uint length = node.dataEnd - node.dataStart;
                ms.Position = start;
                byte[] fileData = reader.ReadBytes((int)length);

                Files[name] = fileData;
            }
        }

        private static string ReadNullTerminatedString(BinaryReader reader)
        {
            var sb = new StringBuilder();
            while (true)
            {
                byte b = reader.ReadByte();
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Get all files matching the given extension (e.g., ".bfsha")
        /// </summary>
        public Dictionary<string, byte[]> GetFilesByExtension(string extension)
        {
            var result = new Dictionary<string, byte[]>();
            foreach (var kvp in Files)
            {
                if (kvp.Key.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    result[kvp.Key] = kvp.Value;
            }
            return result;
        }
    }
}
