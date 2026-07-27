using System;
using System.IO;
using System.IO.Compression;

namespace HtmlPdfNative.Compression
{
    /// <summary>
    /// Vendored codecs matching the Rust crates <c>miniz_oxide</c> (zlib/Deflate), <c>ruzstd</c>
    /// (zstd decode) and <c>brotli</c> (WOFF2). Deflate + zlib are implemented here with the BCL;
    /// zstd/brotli decode are provided where the runtime/package supports them, else throw with a
    /// clear message pointing at <c>ZstdSharp.Port</c> / a Brotli polyfill.
    ///
    /// Used for: embedded-font decompression (fonts ship zstd/zlib), PDF <c>FlateDecode</c> streams,
    /// and WOFF2 (brotli) web fonts.
    /// </summary>
    public static class Codecs
    {
        // ---- Raw DEFLATE (RFC 1951) — PDF FlateDecode payload / miniz_oxide raw ---------------------

        public static byte[] DeflateRaw(byte[] data, CompressionLevel level = CompressionLevel.Optimal)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, level, leaveOpen: true))
                    ds.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        public static byte[] InflateRaw(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var ds = new DeflateStream(input, CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                ds.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // ---- zlib (RFC 1950 = 2-byte header + DEFLATE + Adler-32) — how the Rust fonts are stored ----

        public static byte[] ZlibCompress(byte[] data, CompressionLevel level = CompressionLevel.Optimal)
        {
            byte[] raw = DeflateRaw(data, level);
            var outBuf = new byte[2 + raw.Length + 4];
            outBuf[0] = 0x78; outBuf[1] = 0x9C;                 // CMF/FLG (default compression)
            Buffer.BlockCopy(raw, 0, outBuf, 2, raw.Length);
            uint adler = Adler32(data);
            int p = 2 + raw.Length;
            outBuf[p] = (byte)(adler >> 24); outBuf[p + 1] = (byte)(adler >> 16);
            outBuf[p + 2] = (byte)(adler >> 8); outBuf[p + 3] = (byte)adler;
            return outBuf;
        }

        public static byte[] ZlibDecompress(byte[] data)
        {
            if (data.Length < 6) throw new InvalidDataException("zlib stream too short");
            // Skip the 2-byte zlib header; ignore the trailing 4-byte Adler-32 (DeflateStream stops at end).
            var raw = new byte[data.Length - 2];
            Buffer.BlockCopy(data, 2, raw, 0, raw.Length);
            return InflateRaw(raw);
        }

        // ---- zstd decode (ruzstd) -------------------------------------------------------------------

        public static byte[] ZstdDecompress(byte[] data)
        {
#if NET8_0_OR_GREATER
            // TODO: prefer ZstdSharp.Port when referenced; placeholder throws until wired.
#endif
            throw new NotSupportedException(
                "zstd decode not wired. Reference ZstdSharp.Port and route here (Rust: ruzstd).");
        }

        // ---- brotli decode (WOFF2) ------------------------------------------------------------------

        public static byte[] BrotliDecompress(byte[] data)
        {
#if NETSTANDARD2_0
            throw new NotSupportedException(
                "Brotli decode requires .NET Core 3.1+/net6.0+ (System.IO.Compression.BrotliStream) " +
                "or a polyfill package on netstandard2.0.");
#else
            using (var input = new MemoryStream(data))
            using (var bs = new BrotliStream(input, CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                bs.CopyTo(ms);
                return ms.ToArray();
            }
#endif
        }

        // ---- Adler-32 (for the zlib trailer) --------------------------------------------------------

        private static uint Adler32(byte[] data)
        {
            const uint MOD = 65521;
            uint a = 1, b = 0;
            foreach (byte t in data)
            {
                a = (a + t) % MOD;
                b = (b + a) % MOD;
            }
            return (b << 16) | a;
        }
    }
}
