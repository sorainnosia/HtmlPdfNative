using System;
using System.Collections.Generic;

namespace HtmlPdfNative.TtfParser
{
    /// <summary>
    /// Minimal read-only TrueType/OpenType (sfnt) face. Vendored .NET port of the Rust crate
    /// <c>ttf-parser</c> (only what PDF embedding needs): units/em, glyph count, the best Unicode
    /// <c>cmap</c> (formats 4 and 12), horizontal advances (<c>hmtx</c>), and vertical metrics/bbox.
    /// </summary>
    public sealed class TtfFace
    {
        public byte[] Data { get; }
        public ushort UnitsPerEm { get; private set; } = 1000;
        public ushort NumGlyphs { get; private set; }
        public short Ascent { get; private set; }
        public short Descent { get; private set; }
        public short XMin, YMin, XMax, YMax;

        private readonly Dictionary<int, ushort> _cmap = new Dictionary<int, ushort>();
        private ushort[] _advances = Array.Empty<ushort>(); // per hMetric; last repeats
        private ushort[]? _gidToCid; // CID-keyed CFF: glyph index -> CID (charset)
        private int _cffOff = -1, _cffLen = 0; // 'CFF ' table location (OpenType/CFF)

        /// <summary>The raw 'CFF ' table bytes (font program of an OpenType/CFF face), or null for TrueType.</summary>
        public byte[]? CffTable()
        {
            if (_cffOff < 0 || _cffLen <= 0 || _cffOff + _cffLen > Data.Length) return null;
            var b = new byte[_cffLen];
            Array.Copy(Data, _cffOff, b, 0, _cffLen);
            return b;
        }

        /// <summary>True for a CID-keyed CFF (needs GID→CID via the charset when embedded as CIDFontType0).</summary>
        public bool IsCidKeyed => _gidToCid != null;

        /// <summary>Map a glyph id to its CID (identity when not CID-keyed).</summary>
        public ushort Cid(ushort gid) => _gidToCid != null && gid < _gidToCid.Length ? _gidToCid[gid] : gid;

        private TtfFace(byte[] data) { Data = data; }

        public static TtfFace Parse(byte[] data)
        {
            var f = new TtfFace(data);
            var tables = new Dictionary<string, (int off, int len)>();
            ushort numTables = U16(data, 4);
            int dir = 12;
            for (int i = 0; i < numTables; i++)
            {
                string tag = System.Text.Encoding.ASCII.GetString(data, dir, 4);
                int off = (int)U32(data, dir + 8);
                int len = (int)U32(data, dir + 12);
                tables[tag] = (off, len);
                dir += 16;
            }

            if (tables.TryGetValue("head", out var head))
            {
                f.UnitsPerEm = U16(data, head.off + 18);
                f.XMin = (short)U16(data, head.off + 36); f.YMin = (short)U16(data, head.off + 38);
                f.XMax = (short)U16(data, head.off + 40); f.YMax = (short)U16(data, head.off + 42);
            }
            if (f.UnitsPerEm == 0) f.UnitsPerEm = 1000;
            if (tables.TryGetValue("maxp", out var maxp)) f.NumGlyphs = U16(data, maxp.off + 4);
            ushort numHMetrics = 0;
            if (tables.TryGetValue("hhea", out var hhea))
            {
                f.Ascent = (short)U16(data, hhea.off + 4);
                f.Descent = (short)U16(data, hhea.off + 6);
                numHMetrics = U16(data, hhea.off + 34);
            }
            if (tables.TryGetValue("hmtx", out var hmtx) && numHMetrics > 0)
            {
                f._advances = new ushort[numHMetrics];
                for (int i = 0; i < numHMetrics; i++) f._advances[i] = U16(data, hmtx.off + i * 4);
            }
            if (tables.TryGetValue("cmap", out var cmap)) f.ParseCmap(cmap.off);
            if (tables.TryGetValue("CFF ", out var cff)) { f._cffOff = cff.off; f._cffLen = cff.len; f.ParseCffCharset(cff.off); }
            return f;
        }

        /// <summary>Glyph id for a Unicode code point (0 = .notdef / missing).</summary>
        public ushort GlyphId(int codepoint) => _cmap.TryGetValue(codepoint, out var g) ? g : (ushort)0;

        /// <summary>Advance width of a glyph, in font units.</summary>
        public ushort Advance(ushort gid)
        {
            if (_advances.Length == 0) return (ushort)(UnitsPerEm / 2);
            return gid < _advances.Length ? _advances[gid] : _advances[_advances.Length - 1];
        }

        private void ParseCmap(int cmapOff)
        {
            ushort num = U16(Data, cmapOff + 2);
            int best = -1, bestScore = -1;
            for (int i = 0; i < num; i++)
            {
                int rec = cmapOff + 4 + i * 8;
                ushort plat = U16(Data, rec), enc = U16(Data, rec + 2);
                int sub = cmapOff + (int)U32(Data, rec + 4);
                ushort fmt = U16(Data, sub);
                int score = 0;
                if (plat == 3 && enc == 10 && fmt == 12) score = 5;      // Windows UCS-4
                else if (plat == 3 && enc == 1 && fmt == 4) score = 4;   // Windows BMP
                else if (plat == 0) score = 3;                            // Unicode
                else if (fmt == 12) score = 2; else if (fmt == 4) score = 1;
                if (score > bestScore) { bestScore = score; best = sub; }
            }
            if (best < 0) return;
            ushort format = U16(Data, best);
            if (format == 4) ParseFormat4(best);
            else if (format == 12) ParseFormat12(best);
        }

        private void ParseFormat4(int off)
        {
            ushort segX2 = U16(Data, off + 6);
            int segCount = segX2 / 2;
            int endO = off + 14;
            int startO = endO + segX2 + 2;      // + reservedPad
            int deltaO = startO + segX2;
            int rangeO = deltaO + segX2;
            for (int s = 0; s < segCount; s++)
            {
                ushort end = U16(Data, endO + s * 2);
                ushort start = U16(Data, startO + s * 2);
                short delta = (short)U16(Data, deltaO + s * 2);
                ushort rangeOff = U16(Data, rangeO + s * 2);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    ushort gid;
                    if (rangeOff == 0) gid = (ushort)((c + delta) & 0xFFFF);
                    else
                    {
                        int gi = rangeO + s * 2 + rangeOff + (c - start) * 2;
                        if (gi + 1 >= Data.Length) continue;
                        ushort g = U16(Data, gi);
                        gid = g == 0 ? (ushort)0 : (ushort)((g + delta) & 0xFFFF);
                    }
                    if (gid != 0) _cmap[c] = gid;
                }
            }
        }

        private void ParseFormat12(int off)
        {
            uint nGroups = U32(Data, off + 12);
            int g = off + 16;
            for (uint i = 0; i < nGroups; i++)
            {
                uint startC = U32(Data, g), endC = U32(Data, g + 4), startG = U32(Data, g + 8);
                for (uint c = startC; c <= endC; c++) _cmap[(int)c] = (ushort)(startG + (c - startC));
                g += 12;
            }
        }

        // ---- CFF charset (GID -> CID for CID-keyed OpenType/CFF) -----------------------------------

        private void ParseCffCharset(int cffOff)
        {
            try
            {
                byte hdrSize = Data[cffOff + 2];
                int p = cffOff + hdrSize;
                p = SkipIndex(p);                    // Name INDEX
                int topStart, topEnd;
                int[] topOffsets = ReadIndex(p, out topStart, out topEnd);
                if (topOffsets.Length < 2) return;
                // First Top DICT entry.
                int dictStart = topStart + topOffsets[0];
                int dictEnd = topStart + topOffsets[1];
                int charsetOff = -1; bool isCid = false;
                ParseTopDict(dictStart, dictEnd, cffOff, ref charsetOff, ref isCid);
                if (!isCid || charsetOff <= 0) return; // only CID-keyed fonts need remapping

                var map = new ushort[NumGlyphs];
                map[0] = 0; // .notdef -> CID 0
                int cp = cffOff + charsetOff;
                byte fmt = Data[cp]; cp++;
                int gid = 1;
                if (fmt == 0)
                {
                    while (gid < NumGlyphs) { map[gid] = U16(Data, cp); cp += 2; gid++; }
                }
                else if (fmt == 1 || fmt == 2)
                {
                    while (gid < NumGlyphs)
                    {
                        ushort first = U16(Data, cp); cp += 2;
                        int nLeft = fmt == 1 ? Data[cp++] : U16(Data, cp);
                        if (fmt == 2) cp += 2;
                        for (int i = 0; i <= nLeft && gid < NumGlyphs; i++) map[gid++] = (ushort)(first + i);
                    }
                }
                _gidToCid = map;
            }
            catch { _gidToCid = null; }
        }

        private void ParseTopDict(int start, int end, int cffOff, ref int charsetOff, ref bool isCid)
        {
            var operands = new List<int>();
            int p = start;
            while (p < end)
            {
                int b0 = Data[p];
                if (b0 <= 21)
                {
                    int op = b0; p++;
                    if (b0 == 12) { op = 1200 + Data[p]; p++; }
                    if (op == 15 && operands.Count > 0) charsetOff = operands[operands.Count - 1]; // charset
                    if (op == 1230) isCid = true;                                                  // ROS => CID-keyed
                    operands.Clear();
                }
                else if (b0 == 28) { operands.Add((short)U16(Data, p + 1)); p += 3; }
                else if (b0 == 29) { operands.Add((int)U32(Data, p + 1)); p += 5; }
                else if (b0 == 30) { p++; while (p < end && (Data[p] & 0x0F) != 0x0F && (Data[p] >> 4) != 0x0F) p++; p++; } // real: skip
                else if (b0 >= 32 && b0 <= 246) { operands.Add(b0 - 139); p++; }
                else if (b0 >= 247 && b0 <= 250) { operands.Add((b0 - 247) * 256 + Data[p + 1] + 108); p += 2; }
                else if (b0 >= 251 && b0 <= 254) { operands.Add(-(b0 - 251) * 256 - Data[p + 1] - 108); p += 2; }
                else p++;
            }
        }

        private int[] ReadIndex(int pos, out int dataStart, out int endPos)
        {
            ushort count = U16(Data, pos);
            if (count == 0) { dataStart = pos + 2; endPos = pos + 2; return Array.Empty<int>(); }
            byte offSize = Data[pos + 2];
            int offBase = pos + 3;
            var offsets = new int[count + 1];
            for (int i = 0; i <= count; i++)
            {
                int v = 0;
                for (int b = 0; b < offSize; b++) v = (v << 8) | Data[offBase + i * offSize + b];
                offsets[i] = v;
            }
            dataStart = offBase + (count + 1) * offSize - 1;
            endPos = dataStart + offsets[count];
            return offsets;
        }

        private int SkipIndex(int pos)
        {
            ReadIndex(pos, out _, out int endPos);
            return endPos;
        }

        private static ushort U16(byte[] d, int i) => (ushort)((d[i] << 8) | d[i + 1]);
        private static uint U32(byte[] d, int i) => (uint)((d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3]);
    }
}
