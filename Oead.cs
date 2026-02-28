using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HammerheadConverter
{
    /// <summary>
    /// P/Invoke bindings for liboead_capi.so — wraps oead's Yaz0 and SARC.
    /// </summary>
    public static class Oead
    {
        private const string LibName = "oead_capi";

        // === Yaz0 ===

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr oead_yaz0_decompress(byte[] src, UIntPtr srcSize, out UIntPtr outSize);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr oead_yaz0_compress(byte[] src, UIntPtr srcSize,
            uint dataAlignment, int level, out UIntPtr outSize);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void oead_free(IntPtr ptr);

        // === SARC Reader ===

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr oead_sarc_open(byte[] data, UIntPtr size);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void oead_sarc_close(IntPtr sarc);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ushort oead_sarc_get_num_files(IntPtr sarc);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int oead_sarc_get_file(IntPtr sarc, ushort index,
            out IntPtr outName, out IntPtr outData, out UIntPtr outSize);

        // === SARC Writer ===

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr oead_sarc_writer_new(int le);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void oead_sarc_writer_free(IntPtr writer);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void oead_sarc_writer_set_min_alignment(IntPtr writer, UIntPtr alignment);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void oead_sarc_writer_add_file(IntPtr writer,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] data, UIntPtr size);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr oead_sarc_writer_write(IntPtr writer, out UIntPtr outSize);

        // ====================================================================
        // Public managed API
        // ====================================================================

        /// <summary>
        /// Decompress Yaz0 data (SZS → SARC).
        /// </summary>
        public static byte[] Yaz0Decompress(byte[] src)
        {
            var ptr = oead_yaz0_decompress(src, (UIntPtr)src.Length, out var size);
            if (ptr == IntPtr.Zero)
                throw new InvalidOperationException("oead Yaz0 decompression failed");
            try
            {
                var result = new byte[(int)size];
                Marshal.Copy(ptr, result, 0, result.Length);
                return result;
            }
            finally { oead_free(ptr); }
        }

        /// <summary>
        /// Decompress a Yaz0 file from disk.
        /// </summary>
        public static byte[] Yaz0DecompressFile(string path)
        {
            return Yaz0Decompress(File.ReadAllBytes(path));
        }

        /// <summary>
        /// Compress data with Yaz0 (SARC → SZS). Uses zlib-ng for fast compression.
        /// </summary>
        public static byte[] Yaz0Compress(byte[] src, uint dataAlignment = 0, int level = 7)
        {
            var ptr = oead_yaz0_compress(src, (UIntPtr)src.Length, dataAlignment, level, out var size);
            if (ptr == IntPtr.Zero)
                throw new InvalidOperationException("oead Yaz0 compression failed");
            try
            {
                var result = new byte[(int)size];
                Marshal.Copy(ptr, result, 0, result.Length);
                return result;
            }
            finally { oead_free(ptr); }
        }

        /// <summary>
        /// Read all files from a SARC archive.
        /// </summary>
        public static Dictionary<string, byte[]> SarcRead(byte[] data)
        {
            var handle = oead_sarc_open(data, (UIntPtr)data.Length);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("oead SARC open failed");

            try
            {
                var files = new Dictionary<string, byte[]>();
                ushort count = oead_sarc_get_num_files(handle);

                for (ushort i = 0; i < count; i++)
                {
                    if (oead_sarc_get_file(handle, i, out var namePtr, out var dataPtr, out var size) != 0)
                    {
                        string name = Marshal.PtrToStringUTF8(namePtr);
                        var fileData = new byte[(int)size];
                        Marshal.Copy(dataPtr, fileData, 0, fileData.Length);
                        files[name] = fileData;
                    }
                }

                return files;
            }
            finally { oead_sarc_close(handle); }
        }

        /// <summary>
        /// Write a SARC archive from a dictionary of files. Little-endian (Switch).
        /// </summary>
        public static byte[] SarcWrite(Dictionary<string, byte[]> files)
        {
            var handle = oead_sarc_writer_new(1); // LE for Switch
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("oead SARC writer creation failed");

            // Set minimum alignment to 0x2000 (8KB) — required for Switch bfres GPU data.
            // Without this, GPU buffer data within the bfres won't be page-aligned,
            // causing BufferImpl::Map() to receive NULL handles.
            oead_sarc_writer_set_min_alignment(handle, (UIntPtr)0x2000);

            try
            {
                foreach (var kvp in files)
                    oead_sarc_writer_add_file(handle, kvp.Key, kvp.Value, (UIntPtr)kvp.Value.Length);

                var ptr = oead_sarc_writer_write(handle, out var size);
                if (ptr == IntPtr.Zero)
                    throw new InvalidOperationException("oead SARC write failed");

                try
                {
                    var result = new byte[(int)size];
                    Marshal.Copy(ptr, result, 0, result.Length);
                    return result;
                }
                finally { oead_free(ptr); }
            }
            finally { oead_sarc_writer_free(handle); }
        }
    }
}
