using System;
using System.IO;
using System.IO.Compression;

namespace HtmlPdfNative.Fonts
{
    /// <summary>Decodes a WOFF 1.0 font (a zlib-per-table wrapper around an sfnt) back into a plain
    /// TrueType/OpenType byte stream that <see cref="TtfFace.Parse"/> can consume. WOFF2 (Brotli + a
    /// transformed glyf table) is NOT handled — <see cref="TryDecode"/> returns null for it.</summary>
    public static class Woff
    {
        private const uint SigWoff1 = 0x774F4646; // 'wOFF'
        private const uint SigWoff2 = 0x774F4632; // 'wOF2'

        public static bool IsWoff(byte[] b) => b.Length >= 4 && Be32(b, 0) == SigWoff1;
        public static bool IsWoff2(byte[] b) => b.Length >= 4 && Be32(b, 0) == SigWoff2;

        /// <summary>Return the decoded sfnt bytes, or null if the input is not WOFF1 / is malformed.</summary>
        public static byte[]? TryDecode(byte[] b)
        {
            if (b == null || b.Length < 44 || Be32(b, 0) != SigWoff1) return null;
            uint flavor = Be32(b, 4);
            int numTables = Be16(b, 12);
            if (numTables == 0 || numTables > 4096) return null;

            // Read the WOFF table directory.
            var dir = new (uint tag, uint offset, uint compLen, uint origLen, uint checksum)[numTables];
            int p = 44;
            for (int i = 0; i < numTables; i++)
            {
                if (p + 20 > b.Length) return null;
                dir[i] = (Be32(b, p), Be32(b, p + 4), Be32(b, p + 8), Be32(b, p + 12), Be32(b, p + 16));
                p += 20;
            }

            // Decompress each table's data (stored if compLen == origLen, else zlib).
            var data = new byte[numTables][];
            for (int i = 0; i < numTables; i++)
            {
                var (_, off, comp, orig, _) = dir[i];
                if (off + comp > (uint)b.Length) return null;
                if (comp == orig) { data[i] = new byte[orig]; Array.Copy(b, (int)off, data[i], 0, (int)orig); }
                else data[i] = Inflate(b, (int)off, (int)comp, (int)orig);
                if (data[i] == null) return null;
            }

            // sfnt requires table RECORDS sorted by tag ascending (data order is free; keep them aligned).
            var order = new int[numTables];
            for (int i = 0; i < numTables; i++) order[i] = i;
            Array.Sort(order, (x, y) => dir[x].tag.CompareTo(dir[y].tag));

            int recStart = 12 + numTables * 16;
            int total = recStart;
            var tableOffset = new int[numTables];
            foreach (int i in order) { tableOffset[i] = total; total += Align4((int)dir[i].origLen); }

            var outb = new byte[total];
            // Offset table.
            ushort entrySel = 0; int pow2 = 1; while (pow2 * 2 <= numTables) { pow2 *= 2; entrySel++; }
            ushort searchRange = (ushort)(pow2 * 16);
            Wr32(outb, 0, flavor);
            Wr16(outb, 4, (ushort)numTables);
            Wr16(outb, 6, searchRange);
            Wr16(outb, 8, entrySel);
            Wr16(outb, 10, (ushort)(numTables * 16 - searchRange));
            // Table records (sorted) + data.
            int rp = 12;
            foreach (int i in order)
            {
                Wr32(outb, rp, dir[i].tag);
                Wr32(outb, rp + 4, dir[i].checksum);
                Wr32(outb, rp + 8, (uint)tableOffset[i]);
                Wr32(outb, rp + 12, dir[i].origLen);
                rp += 16;
                Array.Copy(data[i], 0, outb, tableOffset[i], data[i].Length);
            }
            return outb;
        }

        // zlib (RFC1950) = 2-byte header + raw DEFLATE + 4-byte Adler32. DeflateStream handles the raw body,
        // so skip the 2-byte header (works on netstandard2.0, which lacks ZLibStream).
        private static byte[]? Inflate(byte[] src, int off, int len, int origLen)
        {
            try
            {
                using var ms = new MemoryStream(src, off + 2, len - 2);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                var outb = new byte[origLen];
                int read = 0;
                while (read < origLen) { int n = ds.Read(outb, read, origLen - read); if (n <= 0) break; read += n; }
                return read == origLen ? outb : null;
            }
            catch { return null; }
        }

        private static int Align4(int n) => (n + 3) & ~3;
        private static uint Be32(byte[] b, int i) => (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
        private static int Be16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
        private static void Wr32(byte[] b, int i, uint v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }
        private static void Wr16(byte[] b, int i, ushort v) { b[i] = (byte)(v >> 8); b[i + 1] = (byte)v; }
    }
}
