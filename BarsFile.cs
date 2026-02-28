using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HammerheadConverter
{
    /// <summary>
    /// Reads and writes Nintendo BARS (Binary Audio Resource) files.
    /// Handles AMTA metadata + FWAV/BWAV audio data per track.
    /// </summary>
    public class BarsFile
    {
        public List<BarsTrack> Tracks { get; set; } = new();

        /// <summary>A single track in a BARS file (AMTA metadata + BWAV audio data).</summary>
        public class BarsTrack
        {
            public string Name { get; set; }
            public byte[] AmtaData { get; set; }
            public byte[] BwavData { get; set; }
        }

        /// <summary>Load and parse a BARS file from raw bytes.</summary>
        public static BarsFile FromBytes(byte[] data)
        {
            var bars = new BarsFile();

            if (data.Length < 16 || Encoding.ASCII.GetString(data, 0, 4) != "BARS")
                throw new InvalidDataException("Not a valid BARS file");

            bool le = data[8] == 0xFF && data[9] == 0xFE;
            int count = ReadInt32(data, 12, le);

            int hashStart = 16;
            int trkStart = hashStart + count * 4;

            // Read offset pairs (AMTA offset, BWAV offset)
            int[] offsets = new int[count * 2];
            for (int i = 0; i < count * 2; i++)
                offsets[i] = ReadInt32(data, trkStart + i * 4, le);

            for (int i = 0; i < count; i++)
            {
                int amtaOffset = offsets[i * 2];
                int bwavOffset = offsets[i * 2 + 1];

                // Read AMTA total length (at offset +8: after magic(4)+BOM(2)+reserved(2))
                int amtaLen = ReadInt32(data, amtaOffset + 8, le);
                byte[] amtaData = new byte[amtaLen];
                Array.Copy(data, amtaOffset, amtaData, 0, amtaLen);

                // Read track name from AMTA STRG section
                string trackName = ReadAmtaTrackName(data, amtaOffset, le);

                // Read BWAV data
                int bwavSize = ReadInt32(data, bwavOffset + 12, le);
                byte[] bwavData = new byte[bwavSize];
                Array.Copy(data, bwavOffset, bwavData, 0, bwavSize);

                bars.Tracks.Add(new BarsTrack
                {
                    Name = trackName,
                    AmtaData = amtaData,
                    BwavData = bwavData,
                });
            }

            return bars;
        }

        /// <summary>Load a BARS file from disk.</summary>
        public static BarsFile FromFile(string path)
        {
            return FromBytes(File.ReadAllBytes(path));
        }

        /// <summary>Build a BARS file from the current tracks and write to disk.</summary>
        public void Save(string path)
        {
            File.WriteAllBytes(path, ToBytes());
        }

        /// <summary>Serialize to BARS byte array (little-endian / Switch format).</summary>
        public byte[] ToBytes()
        {
            // Sort tracks by CRC32 hash
            var sorted = Tracks.OrderBy(t => CalcCrc32(t.Name)).ToList();
            int count = sorted.Count;

            int headerSize = 16;
            int hashTableSize = count * 4;
            int offsetTableSize = count * 8;
            int metaStart = headerSize + hashTableSize + offsetTableSize;

            var payload = new MemoryStream();
            int[] amtaOffsets = new int[count];
            int[] bwavOffsets = new int[count];

            for (int i = 0; i < count; i++)
            {
                var track = sorted[i];

                // AMTA
                amtaOffsets[i] = metaStart + (int)payload.Position;
                byte[] amta = UpdateAmtaName(track.AmtaData, track.Name);
                payload.Write(amta);
                AlignStream(payload, 0x40);

                // BWAV
                bwavOffsets[i] = metaStart + (int)payload.Position;
                payload.Write(track.BwavData);
                AlignStream(payload, 0x40);
            }

            var output = new MemoryStream();
            var w = new BinaryWriter(output);

            // Header
            w.Write(Encoding.ASCII.GetBytes("BARS"));
            w.Write((uint)0); // placeholder for total size
            w.Write((ushort)0xFFFE); // LE BOM  (note: written as bytes 0xFE 0xFF due to LE)
            w.Write((ushort)0); // reserved
            w.Write((uint)count);

            // BOM bytes need fixing — write raw
            output.Position = 8;
            output.WriteByte(0xFF);
            output.WriteByte(0xFE);
            output.Position = output.Length;

            // Hash table
            foreach (var track in sorted)
                w.Write(CalcCrc32(track.Name));

            // Offset table
            for (int i = 0; i < count; i++)
            {
                w.Write((uint)amtaOffsets[i]);
                w.Write((uint)bwavOffsets[i]);
            }

            // Payload
            payload.Position = 0;
            payload.CopyTo(output);

            // Fix total size
            uint totalSize = (uint)output.Length;
            output.Position = 4;
            w.Write(totalSize);

            return output.ToArray();
        }

        /// <summary>Create a minimal silent FWAV (PCM16, 48kHz, mono, 64 samples of silence).</summary>
        public static byte[] CreateSilentBwav()
        {
            const int sampleCount = 64;
            const int sampleRate = 48000;

            // PCM16 silence, aligned to 0x20
            byte[] pcmData = new byte[AlignUp(sampleCount * 2, 0x20)];

            int dataBlockSize = 0x20 + pcmData.Length;

            // INFO body
            var infoBody = new MemoryStream();
            var iw = new BinaryWriter(infoBody);
            iw.Write((byte)2);    // codec = PCM16
            iw.Write((byte)0);    // loop flag
            iw.Write((ushort)0);  // padding
            iw.Write((uint)sampleRate);
            iw.Write((uint)0);    // loop start
            iw.Write((uint)sampleCount); // loop end / sample count
            iw.Write((uint)0);    // reserved
            iw.Write((uint)1);    // channel count
            // Channel info reference
            iw.Write((ushort)0x7100); iw.Write((ushort)0); iw.Write((int)0x18);
            // Channel info
            iw.Write((ushort)0x1F00); iw.Write((ushort)0); iw.Write((int)0);
            iw.Write((ushort)0); iw.Write((ushort)0); iw.Write((int)-1);
            iw.Write((uint)0);    // reserved

            int infoBlockBodySize = (int)infoBody.Length;
            int infoBlockSize = 8 + infoBlockBodySize;
            int infoPad = AlignUp(infoBlockSize, 0x20) - infoBlockSize;

            int totalSize = 0x40 + infoBlockSize + infoPad + dataBlockSize;

            var output = new MemoryStream();
            var w = new BinaryWriter(output);

            // FWAV header (0x40 bytes)
            w.Write(Encoding.ASCII.GetBytes("FWAV"));
            output.WriteByte(0xFF); output.WriteByte(0xFE); // BOM
            w.Write((ushort)0x40);    // header size
            w.Write((uint)0);         // version
            w.Write((uint)totalSize); // file size
            w.Write((ushort)2);       // num blocks
            w.Write((ushort)0);       // reserved
            // INFO ref
            w.Write((ushort)0x7000); w.Write((ushort)0);
            w.Write((int)0x18);
            w.Write((uint)(infoBlockSize + infoPad));
            // DATA ref
            w.Write((ushort)0x7001); w.Write((ushort)0);
            w.Write((int)(0x18 + infoBlockSize + infoPad));
            w.Write((uint)dataBlockSize);
            // Pad header to 0x40
            while (output.Length < 0x40) w.Write((byte)0);

            // INFO block
            w.Write(Encoding.ASCII.GetBytes("INFO"));
            w.Write((uint)infoBlockSize);
            infoBody.Position = 0;
            infoBody.CopyTo(output);
            for (int i = 0; i < infoPad; i++) w.Write((byte)0);

            // DATA block
            w.Write(Encoding.ASCII.GetBytes("DATA"));
            w.Write((uint)dataBlockSize);
            while ((output.Length % 0x20) != 0) w.Write((byte)0);
            w.Write(pcmData);

            return output.ToArray();
        }

        /// <summary>Create a minimal AMTA header for a track with the given name.</summary>
        public static byte[] CreateSilentAmta(string trackName)
        {
            byte[] nameBytes = PadToAlignment(Encoding.ASCII.GetBytes(trackName + "\0"), 4);
            byte[] dataSection = new byte[12]; // minimal DATA

            var body = new MemoryStream();
            var bw = new BinaryWriter(body);

            bw.Write(Encoding.ASCII.GetBytes("DATA"));
            bw.Write((uint)dataSection.Length);
            bw.Write(dataSection);

            bw.Write(Encoding.ASCII.GetBytes("MARK"));
            bw.Write((uint)0);

            bw.Write(Encoding.ASCII.GetBytes("EXT_"));
            bw.Write((uint)0);

            bw.Write(Encoding.ASCII.GetBytes("STRG"));
            bw.Write((uint)nameBytes.Length);
            bw.Write(nameBytes);

            int totalLen = 28 + (int)body.Length;

            var header = new MemoryStream();
            var hw = new BinaryWriter(header);
            hw.Write(Encoding.ASCII.GetBytes("AMTA"));
            header.WriteByte(0xFF); header.WriteByte(0xFE); // BOM
            hw.Write((ushort)0); // reserved
            hw.Write((uint)totalLen);
            // Section offsets (relative to start of AMTA)
            int dataOff = 28;
            int markOff = dataOff + 8 + dataSection.Length;
            int extOff = markOff + 8;
            int strgOff = extOff + 8;
            hw.Write((uint)dataOff);
            hw.Write((uint)markOff);
            hw.Write((uint)extOff);
            hw.Write((uint)strgOff);

            var result = new byte[totalLen];
            Array.Copy(header.ToArray(), 0, result, 0, header.Length);
            Array.Copy(body.ToArray(), 0, result, 28, body.Length);
            return result;
        }

        // ─── Helpers ───

        /// <summary>CRC32 hash as used by BARS for track name ordering.</summary>
        public static uint CalcCrc32(string name)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(name);
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
            }
            return ~crc;
        }

        private static string ReadAmtaTrackName(byte[] data, int amtaOffset, bool le)
        {
            int apos = amtaOffset + 28; // skip AMTA header
            for (int j = 0; j < 4; j++)
            {
                string secMagic = Encoding.ASCII.GetString(data, apos, 4);
                int secLen = ReadInt32(data, apos + 4, le);
                apos += 8;
                if (secMagic == "STRG")
                {
                    int end = Array.IndexOf(data, (byte)0, apos);
                    if (end < 0) end = apos + secLen;
                    return Encoding.ASCII.GetString(data, apos, end - apos);
                }
                apos += secLen;
            }
            return "Unknown";
        }

        private static byte[] UpdateAmtaName(byte[] amtaData, string newName)
        {
            var result = new MemoryStream();
            result.Write(amtaData, 0, 28); // AMTA header

            int apos = 28;
            for (int j = 0; j < 4; j++)
            {
                string secMagic = Encoding.ASCII.GetString(amtaData, apos, 4);
                int secLen = BitConverter.ToInt32(amtaData, apos + 4);

                if (secMagic == "STRG")
                {
                    byte[] nameBytes = PadToAlignment(Encoding.ASCII.GetBytes(newName + "\0"), 4);
                    result.Write(Encoding.ASCII.GetBytes("STRG"));
                    result.Write(BitConverter.GetBytes(nameBytes.Length));
                    result.Write(nameBytes);
                }
                else
                {
                    result.Write(amtaData, apos, 8 + secLen);
                }
                apos += 8 + secLen;
            }

            byte[] final = result.ToArray();
            // Update AMTA total length at offset +8
            BitConverter.TryWriteBytes(new Span<byte>(final, 8, 4), (uint)final.Length);
            return final;
        }

        private static int ReadInt32(byte[] data, int offset, bool le)
        {
            if (le)
                return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
            else
                return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        private static byte[] PadToAlignment(byte[] data, int align)
        {
            int padded = AlignUp(data.Length, align);
            if (padded == data.Length) return data;
            var result = new byte[padded];
            Array.Copy(data, result, data.Length);
            return result;
        }

        private static int AlignUp(int value, int align)
        {
            return ((value - 1) | (align - 1)) + 1;
        }

        private static void AlignStream(MemoryStream stream, int align)
        {
            while (stream.Length % align != 0)
                stream.WriteByte(0);
        }
    }
}
