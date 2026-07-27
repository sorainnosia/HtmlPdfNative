using System;
using System.Collections.Generic;
using System.IO;
#if NET6_0_OR_GREATER
using System.IO.Compression;
#endif

namespace HtmlPdfNative.Fonts
{
    /// <summary>Decodes a WOFF2 font (Brotli + compact directory) into a plain sfnt, INCLUDING the TrueType
    /// glyf/loca transform reconstruction (triplet-encoded outlines → standard glyf + loca). Brotli needs
    /// net6.0+; on netstandard2.0 this returns null. The hmtx transform is not reconstructed (bails).</summary>
    public static class Woff2
    {
        private static readonly string[] Known =
        {
            "cmap","head","hhea","hmtx","maxp","name","OS/2","post","cvt ","fpgm","glyf","loca","prep","CFF ","VORG","EBDT",
            "EBLC","gasp","hdmx","kern","LTSH","PCLT","VDMX","vhea","vmtx","BASE","GDEF","GPOS","GSUB","EBSC","JSTF","MATH",
            "CBDT","CBLC","COLR","CPAL","SVG ","sbix","acnt","avar","bdat","bloc","bsln","cvar","fdsc","feat","fmtx","fvar",
            "gvar","hsty","just","lcar","mort","morx","opbd","prop","trak","Zapf","Silf","Glat","Gloc","Feat","Sill",
        };

        public static byte[]? TryDecode(byte[] b)
        {
            if (b == null || b.Length < 48 || Be32(b, 0) != 0x774F4632u /*wOF2*/) return null;
#if !NET6_0_OR_GREATER
            return null;
#else
            uint flavor = Be32(b, 4);
            int numTables = Be16(b, 12);
            uint totalCompressed = Be32(b, 20);
            if (numTables == 0 || numTables > 4096) return null;

            int p = 48;
            var tags = new string[numTables];
            var origLen = new uint[numTables];
            var storedLen = new uint[numTables];
            int glyfIdx = -1, locaIdx = -1, hmtxIdx = -1, hheaIdx = -1;
            bool hmtxTransformed = false;
            for (int i = 0; i < numTables; i++)
            {
                if (p >= b.Length) return null;
                byte flags = b[p++];
                int idx = flags & 0x3F, transform = (flags >> 6) & 0x3;
                string tag = idx == 0x3F ? Latin1(b, ref p) : (idx < Known.Length ? Known[idx] : "????");
                uint oLen = ReadBase128(b, ref p);
                bool transformed = ((tag == "glyf" || tag == "loca") && transform == 0) || (tag == "hmtx" && transform != 0);
                uint sLen = transformed ? ReadBase128(b, ref p) : oLen;
                tags[i] = tag; origLen[i] = oLen; storedLen[i] = sLen;
                if (tag == "glyf" && transformed) glyfIdx = i;
                if (tag == "loca" && transformed) locaIdx = i;
                if (tag == "hmtx") { hmtxIdx = i; hmtxTransformed = transformed; }
                if (tag == "hhea") hheaIdx = i;
            }
            if (hmtxTransformed && glyfIdx < 0) return null;   // hmtx transform needs the glyf xMins

            byte[] dec;
            try
            {
                using var ms = new MemoryStream(b, p, (int)Math.Min(totalCompressed, (uint)(b.Length - p)));
                using var bs = new BrotliStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                bs.CopyTo(outMs);
                dec = outMs.ToArray();
            }
            catch { return null; }

            var data = new byte[numTables][];
            int off = 0;
            for (int i = 0; i < numTables; i++)
            {
                int len = (int)storedLen[i];
                if (off + len > dec.Length) return null;
                data[i] = new byte[len];
                Array.Copy(dec, off, data[i], 0, len);
                off += len;
            }

            // Reconstruct the transformed glyf → standard glyf + loca (+ per-glyph xMin for the hmtx transform).
            short[]? xMins = null;
            if (glyfIdx >= 0)
            {
                var rec = ReconstructGlyf(data[glyfIdx]);
                if (rec == null) return null;
                data[glyfIdx] = rec.Value.glyf;
                if (locaIdx >= 0) data[locaIdx] = rec.Value.loca;
                xMins = rec.Value.xMins;
            }

            // Reconstruct the transformed hmtx (advance widths + lsb, deriving omitted lsb from glyf xMin).
            if (hmtxTransformed && hmtxIdx >= 0 && hheaIdx >= 0 && xMins != null)
            {
                int numHMetrics = Be16(data[hheaIdx], 34);
                var rebuilt = ReconstructHmtx(data[hmtxIdx], numHMetrics, xMins.Length, xMins);
                if (rebuilt == null) return null;
                data[hmtxIdx] = rebuilt;
            }
            else if (hmtxTransformed) return null;

            // Reassemble sfnt (records tag-sorted, data 4-aligned, checksums recomputed).
            var order = new int[numTables];
            for (int i = 0; i < numTables; i++) order[i] = i;
            Array.Sort(order, (x, y) => string.CompareOrdinal(tags[x], tags[y]));
            int recStart = 12 + numTables * 16, total = recStart;
            var tableOffset = new int[numTables];
            foreach (int i in order) { tableOffset[i] = total; total += Align4(data[i].Length); }

            var outb = new byte[total];
            ushort entrySel = 0; int pow2 = 1; while (pow2 * 2 <= numTables) { pow2 *= 2; entrySel++; }
            ushort searchRange = (ushort)(pow2 * 16);
            Wr32(outb, 0, flavor); Wr16(outb, 4, (ushort)numTables); Wr16(outb, 6, searchRange);
            Wr16(outb, 8, entrySel); Wr16(outb, 10, (ushort)(numTables * 16 - searchRange));
            int rp = 12;
            foreach (int i in order)
            {
                for (int k = 0; k < 4; k++) outb[rp + k] = (byte)(k < tags[i].Length ? tags[i][k] : ' ');
                Wr32(outb, rp + 4, Checksum(data[i]));
                Wr32(outb, rp + 8, (uint)tableOffset[i]);
                Wr32(outb, rp + 12, (uint)data[i].Length);
                rp += 16;
                Array.Copy(data[i], 0, outb, tableOffset[i], data[i].Length);
            }
            return outb;
#endif
        }

        private static int WithSign(int flag, int baseval) => (flag & 1) != 0 ? baseval : -baseval;

        /// <summary>Reconstruct the standard glyf + loca tables from the WOFF2 transformed-glyf stream.</summary>
        private static (byte[] glyf, byte[] loca, short[] xMins)? ReconstructGlyf(byte[] t)
        {
            try
            {
                int p = 0;
                p += 2;                                   // reserved
                ushort optionFlags = (ushort)Be16(t, p); p += 2;
                int numGlyphs = Be16(t, p); p += 2;
                int indexFormat = Be16(t, p); p += 2;
                uint nContourSize = Be32(t, p); p += 4;
                uint nPointsSize = Be32(t, p); p += 4;
                uint flagSize = Be32(t, p); p += 4;
                uint glyphSize = Be32(t, p); p += 4;
                uint compositeSize = Be32(t, p); p += 4;
                uint bboxSize = Be32(t, p); p += 4;
                uint instrSize = Be32(t, p); p += 4;
                // optionFlags bit0 = an overlapSimpleBitmap is appended at the END (size = ceil(numGlyphs/8), NOT
                // stored in the header) — it only sets the cosmetic OVERLAP_SIMPLE flag, so we ignore it here.

                int nContourPos = p; p += (int)nContourSize;
                int cNPoints = p; p += (int)nPointsSize;
                int cFlag = p; p += (int)flagSize;
                int cGlyph = p; p += (int)glyphSize;
                int cComposite = p; p += (int)compositeSize;
                int bboxPos = p; p += (int)bboxSize;
                int cInstr = p;                           // instruction stream start
                int bboxBitmapSize = (numGlyphs + 7) / 8;
                int cBbox = bboxPos + bboxBitmapSize;

                var glyf = new List<byte>();
                var loca = new int[numGlyphs + 1];
                var xMins = new short[numGlyphs];
                int cNContour = nContourPos;

                for (int g = 0; g < numGlyphs; g++)
                {
                    loca[g] = glyf.Count;
                    short nContours = (short)Be16(t, cNContour); cNContour += 2;
                    bool haveBBox = (t[bboxPos + (g >> 3)] & (0x80 >> (g & 7))) != 0;
                    if (nContours == 0) continue;         // empty glyph
                    var gb = new List<byte>();

                    if (nContours > 0)
                    {
                        var endPts = new int[nContours]; int endPoint = -1;
                        for (int c = 0; c < nContours; c++) { endPoint += Read255UShort(t, ref cNPoints); endPts[c] = endPoint; }
                        int nPoints = endPoint + 1;
                        var xs = new int[nPoints]; var ys = new int[nPoints]; var on = new bool[nPoints];
                        int x = 0, y = 0;
                        for (int i = 0; i < nPoints; i++)
                        {
                            byte fb = t[cFlag++];
                            on[i] = (fb >> 7) == 0;
                            int f = fb & 0x7f, dx, dy;
                            if (f < 10) { dx = 0; dy = WithSign(f, ((f & 14) << 7) + t[cGlyph++]); }
                            else if (f < 20) { dx = WithSign(f, (((f - 10) & 14) << 7) + t[cGlyph++]); dy = 0; }
                            else if (f < 84) { int b0 = f - 20, b1 = t[cGlyph++]; dx = WithSign(f, 1 + (b0 & 0x30) + (b1 >> 4)); dy = WithSign(f >> 1, 1 + ((b0 & 0x0c) << 2) + (b1 & 0x0f)); }
                            else if (f < 120) { int b0 = f - 84, b1 = t[cGlyph++], b2 = t[cGlyph++]; dx = WithSign(f, 1 + ((b0 / 12) << 8) + b1); dy = WithSign(f >> 1, 1 + (((b0 % 12) >> 2) << 8) + b2); }
                            else if (f < 124) { int b1 = t[cGlyph++], b2 = t[cGlyph++], b3 = t[cGlyph++]; dx = WithSign(f, (b1 << 4) + (b2 >> 4)); dy = WithSign(f >> 1, ((b2 & 0x0f) << 8) + b3); }
                            else { int b1 = t[cGlyph++], b2 = t[cGlyph++], b3 = t[cGlyph++], b4 = t[cGlyph++]; dx = WithSign(f, (b1 << 8) + b2); dy = WithSign(f >> 1, (b3 << 8) + b4); }
                            x += dx; y += dy; xs[i] = x; ys[i] = y;
                        }
                        int instrLen = Read255UShort(t, ref cGlyph);
                        int xMin, yMin, xMax, yMax;
                        if (haveBBox) { xMin = (short)Be16(t, cBbox); cBbox += 2; yMin = (short)Be16(t, cBbox); cBbox += 2; xMax = (short)Be16(t, cBbox); cBbox += 2; yMax = (short)Be16(t, cBbox); cBbox += 2; }
                        else { xMin = yMin = int.MaxValue; xMax = yMax = int.MinValue; for (int i = 0; i < nPoints; i++) { xMin = Math.Min(xMin, xs[i]); yMin = Math.Min(yMin, ys[i]); xMax = Math.Max(xMax, xs[i]); yMax = Math.Max(yMax, ys[i]); } if (nPoints == 0) { xMin = yMin = xMax = yMax = 0; } }

                        xMins[g] = (short)xMin;
                        W16(gb, (ushort)nContours);
                        W16(gb, (ushort)(short)xMin); W16(gb, (ushort)(short)yMin); W16(gb, (ushort)(short)xMax); W16(gb, (ushort)(short)yMax);
                        for (int c = 0; c < nContours; c++) W16(gb, (ushort)endPts[c]);
                        W16(gb, (ushort)instrLen);
                        for (int k = 0; k < instrLen; k++) gb.Add(t[cInstr++]);
                        // Emit flags (no repeat) + delta-encoded coordinates.
                        var fl = new List<byte>(); var xb = new List<byte>(); var yb = new List<byte>();
                        int px = 0, py = 0;
                        for (int i = 0; i < nPoints; i++)
                        {
                            int dx = xs[i] - px, dy = ys[i] - py; px = xs[i]; py = ys[i];
                            byte flag = (byte)(on[i] ? 0x01 : 0x00);
                            if (dx == 0) flag |= 0x10; else if (dx >= -255 && dx <= 255) { flag |= 0x02; if (dx > 0) flag |= 0x10; xb.Add((byte)Math.Abs(dx)); } else { xb.Add((byte)((dx >> 8) & 0xff)); xb.Add((byte)(dx & 0xff)); }
                            if (dy == 0) flag |= 0x20; else if (dy >= -255 && dy <= 255) { flag |= 0x04; if (dy > 0) flag |= 0x20; yb.Add((byte)Math.Abs(dy)); } else { yb.Add((byte)((dy >> 8) & 0xff)); yb.Add((byte)(dy & 0xff)); }
                            fl.Add(flag);
                        }
                        gb.AddRange(fl); gb.AddRange(xb); gb.AddRange(yb);
                    }
                    else
                    {
                        // Composite: bbox required; component records copied verbatim from compositeStream.
                        if (!haveBBox) return null;
                        int xMin = (short)Be16(t, cBbox); cBbox += 2; int yMin = (short)Be16(t, cBbox); cBbox += 2; int xMax = (short)Be16(t, cBbox); cBbox += 2; int yMax = (short)Be16(t, cBbox); cBbox += 2;
                        xMins[g] = (short)xMin;
                        W16(gb, unchecked((ushort)(-1)));
                        W16(gb, (ushort)(short)xMin); W16(gb, (ushort)(short)yMin); W16(gb, (ushort)(short)xMax); W16(gb, (ushort)(short)yMax);
                        bool haveInstr = false, more = true;
                        while (more)
                        {
                            int start = cComposite;
                            ushort cflags = (ushort)Be16(t, cComposite); cComposite += 2;
                            cComposite += 2;                                   // glyphIndex
                            cComposite += (cflags & 0x0001) != 0 ? 4 : 2;      // ARG_1_AND_2_ARE_WORDS
                            if ((cflags & 0x0008) != 0) cComposite += 2;       // WE_HAVE_A_SCALE
                            else if ((cflags & 0x0040) != 0) cComposite += 4;  // X_AND_Y_SCALE
                            else if ((cflags & 0x0080) != 0) cComposite += 8;  // 2x2
                            for (int k = start; k < cComposite; k++) gb.Add(t[k]);
                            if ((cflags & 0x0100) != 0) haveInstr = true;      // WE_HAVE_INSTRUCTIONS
                            more = (cflags & 0x0020) != 0;                     // MORE_COMPONENTS
                        }
                        if (haveInstr)
                        {
                            int instrLen = Read255UShort(t, ref cGlyph);
                            W16(gb, (ushort)instrLen);
                            for (int k = 0; k < instrLen; k++) gb.Add(t[cInstr++]);
                        }
                    }
                    if ((gb.Count & 1) != 0) gb.Add(0);   // pad to 2 bytes (loca alignment)
                    glyf.AddRange(gb);
                }
                loca[numGlyphs] = glyf.Count;

                var locaOut = new List<byte>();
                if (indexFormat == 0) foreach (int o in loca) W16(locaOut, (ushort)(o / 2));
                else foreach (int o in loca) W32(locaOut, (uint)o);
                return (glyf.ToArray(), locaOut.ToArray(), xMins);
            }
            catch { return null; }
        }

        /// <summary>Reconstruct a standard hmtx from the WOFF2 transformed form: advance widths + lsb, deriving any
        /// omitted lsb from the glyf xMin (flags bit0 = proportional lsb omitted, bit1 = monospace lsb omitted).</summary>
        private static byte[]? ReconstructHmtx(byte[] t, int numHMetrics, int numGlyphs, short[] xMins)
        {
            if (numHMetrics <= 0 || numHMetrics > numGlyphs) return null;
            int p = 0;
            byte flags = t[p++];
            bool hasProportionalLsb = (flags & 0x01) == 0, hasMonospaceLsb = (flags & 0x02) == 0;
            var adv = new int[numHMetrics];
            for (int i = 0; i < numHMetrics; i++) { adv[i] = Be16(t, p); p += 2; }
            var lsb = new short[numGlyphs];
            if (hasProportionalLsb) for (int i = 0; i < numHMetrics; i++) { lsb[i] = (short)Be16(t, p); p += 2; }
            else for (int i = 0; i < numHMetrics; i++) lsb[i] = xMins[i];
            if (hasMonospaceLsb) for (int i = numHMetrics; i < numGlyphs; i++) { lsb[i] = (short)Be16(t, p); p += 2; }
            else for (int i = numHMetrics; i < numGlyphs; i++) lsb[i] = xMins[i];
            var outb = new byte[numHMetrics * 4 + (numGlyphs - numHMetrics) * 2];
            int o = 0;
            for (int i = 0; i < numHMetrics; i++) { outb[o++] = (byte)(adv[i] >> 8); outb[o++] = (byte)adv[i]; outb[o++] = (byte)(lsb[i] >> 8); outb[o++] = (byte)lsb[i]; }
            for (int i = numHMetrics; i < numGlyphs; i++) { outb[o++] = (byte)(lsb[i] >> 8); outb[o++] = (byte)lsb[i]; }
            return outb;
        }

        // 255UShort (WOFF2 §4.3): 253→next 2 bytes; 254→next byte+506; 255→next byte+253; else the byte.
        private static int Read255UShort(byte[] b, ref int p)
        {
            byte code = b[p++];
            if (code == 253) { int v = (b[p] << 8) | b[p + 1]; p += 2; return v; }
            if (code == 254) return b[p++] + 506;
            if (code == 255) return b[p++] + 253;
            return code;
        }

        private static uint Checksum(byte[] t)
        {
            uint sum = 0; int n = (t.Length + 3) & ~3;
            for (int i = 0; i < n; i += 4) { uint w = 0; for (int k = 0; k < 4; k++) w = (w << 8) | (uint)(i + k < t.Length ? t[i + k] : 0); sum += w; }
            return sum;
        }

        private static uint ReadBase128(byte[] b, ref int p)
        {
            uint v = 0;
            for (int i = 0; i < 5; i++) { byte c = b[p++]; v = (v << 7) | (uint)(c & 0x7F); if ((c & 0x80) == 0) return v; }
            return v;
        }

        private static void W16(List<byte> l, ushort v) { l.Add((byte)(v >> 8)); l.Add((byte)v); }
        private static void W32(List<byte> l, uint v) { l.Add((byte)(v >> 24)); l.Add((byte)(v >> 16)); l.Add((byte)(v >> 8)); l.Add((byte)v); }
        private static int Align4(int n) => (n + 3) & ~3;
        private static uint Be32(byte[] b, int i) => (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
        private static int Be16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
        private static string Latin1(byte[] b, ref int p) { var c = new char[4]; for (int k = 0; k < 4; k++) c[k] = (char)b[p++]; return new string(c); }
        private static void Wr32(byte[] b, int i, uint v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }
        private static void Wr16(byte[] b, int i, ushort v) { b[i] = (byte)(v >> 8); b[i + 1] = (byte)v; }
    }
}
