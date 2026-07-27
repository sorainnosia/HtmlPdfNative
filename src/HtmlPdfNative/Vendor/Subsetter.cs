using System;
using System.Collections.Generic;
using System.IO;

namespace HtmlPdfNative.Subsetter
{
    /// <summary>
    /// Vendored .NET port of the Rust crate <c>subsetter</c> — shrinks an embeddable font to the glyphs
    /// actually used. Focused on the case that dominates our output size: a CID-keyed OpenType/CFF font
    /// (the CJK Notos, several MB each). Uses the well-known "keep glyph numbering, empty the unused
    /// CharStrings" technique — every unused glyph's outline is replaced by a single Type2 <c>endchar</c>,
    /// which collapses the CharStrings INDEX (the bulk of a CJK font) while leaving the charset, FDSelect,
    /// FDArray, subrs and all GID/CID relationships untouched — so the embedding side needs no changes.
    /// The CFF is re-serialized in a canonical section order; the Top/Font DICT offsets we control are
    /// written fixed-width (5-byte) so the layout is deterministic, while every other operator (incl.
    /// real-valued FontMatrix) is re-emitted from its original operand bytes verbatim. Non-CID or
    /// unparseable input returns null (caller keeps the whole font).
    /// </summary>
    public static class Subsetter
    {
        public const string RustCrate = "subsetter";

        /// <summary>Subset a CID-keyed CFF font program to <paramref name="usedGids"/>. Null if it can't.</summary>
        public static byte[]? SubsetCidCff(byte[] cff, ICollection<int> usedGids)
        {
            try { return new CffSubset(cff, usedGids).Build(); }
            catch { return null; }
        }

        /// <summary>Subset a TrueType (glyf-flavoured) sfnt font to <paramref name="usedGids"/> using the same
        /// "keep glyph numbering, empty unused glyphs" technique: the <c>glyf</c> table (the bulk) is rebuilt
        /// keeping only the used glyphs' outlines (composite components pulled in transitively), unused glyphs
        /// collapse to zero-length entries, and <c>loca</c> is rewritten in LONG format to match. All other
        /// tables (cmap/hmtx/GSUB/…) and the GID numbering are preserved, so the CIDFontType2 embedding side
        /// (CIDToGIDMap /Identity, /W keyed by GID) needs no changes. Null if it can't parse / isn't glyf.</summary>
        public static byte[]? SubsetTrueType(byte[] sfnt, ICollection<int> usedGids)
        {
            try { return new TrueTypeSubset(sfnt, usedGids).Build(); }
            catch { return null; }
        }

        private struct Entry
        {
            public int Op;
            public byte[] RawOps;      // original operand bytes (preserves reals verbatim)
            public List<double> Ints;  // decoded operands (for the offsets/sizes we read)
        }

        private sealed class Dict
        {
            public readonly List<Entry> Entries = new List<Entry>();
            private readonly Dictionary<int, List<double>> _map = new Dictionary<int, List<double>>();
            public void Add(Entry e) { Entries.Add(e); _map[e.Op] = e.Ints; }
            public bool ContainsKey(int op) => _map.ContainsKey(op);
            public bool TryGetValue(int op, out List<double> v) => _map.TryGetValue(op, out v!);
        }

        private sealed class CffSubset
        {
            private readonly byte[] _d;
            private readonly HashSet<int> _used;

            public CffSubset(byte[] data, ICollection<int> used)
            {
                _d = data;
                _used = new HashSet<int>(used) { 0 }; // .notdef always kept
            }

            public byte[]? Build()
            {
                byte hdrSize = _d[2];
                int p = hdrSize;
                var name = ReadIndexRaw(ref p);          // Name INDEX
                var top = ReadIndexRaw(ref p);           // Top DICT INDEX (1 entry)
                var strings = ReadIndexRaw(ref p);       // String INDEX
                var gsubr = ReadIndexRaw(ref p);         // Global Subr INDEX
                if (top.Count < 1) return null;

                var topDict = ParseDict(top[0]);
                if (!topDict.ContainsKey(1230)) return null;                 // ROS => must be CID-keyed
                if (!topDict.TryGetValue(17, out var csOp)) return null;     // CharStrings
                if (!topDict.TryGetValue(15, out var chOp)) return null;     // charset
                if (!topDict.TryGetValue(1236, out var fdaOp)) return null;  // FDArray
                if (!topDict.TryGetValue(1237, out var fdsOp)) return null;  // FDSelect

                int csOff = (int)csOp[csOp.Count - 1];
                int charsetOff = (int)chOp[chOp.Count - 1];
                int fdArrayOff = (int)fdaOp[fdaOp.Count - 1];
                int fdSelectOff = (int)fdsOp[fdsOp.Count - 1];
                if (charsetOff <= 2 || fdSelectOff <= 0) return null; // predefined charsets unsupported here

                // CharStrings INDEX -> per-glyph raw charstrings.
                int cp2 = csOff;
                var charStrings = ReadIndexRaw(ref cp2);
                int nGlyphs = charStrings.Count;

                // Subset: unused glyph -> single endchar (0x0E).
                var newCs = new List<byte[]>(nGlyphs);
                for (int g = 0; g < nGlyphs; g++)
                    newCs.Add(_used.Contains(g) ? charStrings[g] : new byte[] { 0x0E });

                // Verbatim slices whose GID relationships are unchanged.
                byte[] charsetBytes = Slice(charsetOff, CharsetLen(charsetOff, nGlyphs));
                byte[] fdSelectBytes = Slice(fdSelectOff, FdSelectLen(fdSelectOff, nGlyphs));

                // FDArray: Font DICTs, each with a Private (op 18 = [size offset]) + optional local subrs.
                int fp = fdArrayOff;
                var fontDicts = ReadIndexRaw(ref fp);
                var fdParsed = new List<Dict>();
                var privBlocks = new List<byte[]>();     // Private DICT (+ local subrs) block per FD
                var privSizes = new List<int>();
                foreach (var fdBytes in fontDicts)
                {
                    var fd = ParseDict(fdBytes);
                    fdParsed.Add(fd);
                    if (!fd.TryGetValue(18, out var priv) || priv.Count < 2) { privBlocks.Add(Array.Empty<byte>()); privSizes.Add(0); continue; }
                    int pSize = (int)priv[0], pOff = (int)priv[1];
                    privSizes.Add(pSize);
                    var pd = ParseDict(Slice(pOff, pSize));
                    if (pd.TryGetValue(19, out var subrsRel))   // local Subrs offset is relative to Private start
                    {
                        int rel = (int)subrsRel[subrsRel.Count - 1];
                        int lp = pOff + rel;
                        int lsEnd = lp; ReadIndexRaw(ref lsEnd);
                        int lsLen = lsEnd - lp;
                        var block = new byte[Math.Max(pSize, rel) + lsLen];
                        Array.Copy(_d, pOff, block, 0, pSize);                 // Private DICT (op 19 value preserved)
                        Array.Copy(_d, lp, block, rel, lsLen);                 // local subrs at same relative offset
                        privBlocks.Add(block);
                    }
                    else privBlocks.Add(Slice(pOff, pSize));
                }

                // ---- assemble in canonical order; controlled offsets are fixed 5-byte ------------------
                byte[] nameIdx = BuildIndex(name);
                byte[] strIdx = BuildIndex(strings);
                byte[] gsubrIdx = BuildIndex(gsubr);
                byte[] csIdx = BuildIndex(newCs);

                // Placeholder builds (sizes are offset-value independent because our offsets are fixed-width).
                byte[] topIdx0 = BuildIndex(new List<byte[]> { EmitTopDict(topDict, 0, 0, 0, 0) });
                var fd0 = new List<byte[]>();
                foreach (var fd in fdParsed) fd0.Add(EmitFontDict(fd, 0, 0));
                byte[] fdArrayIdx0 = BuildIndex(fd0);

                int pos = 4; // header
                pos += nameIdx.Length;
                pos += topIdx0.Length;
                pos += strIdx.Length;
                pos += gsubrIdx.Length;
                int charsetPos = pos; pos += charsetBytes.Length;
                int fdSelectPos = pos; pos += fdSelectBytes.Length;
                int charStringsPos = pos; pos += csIdx.Length;
                int fdArrayPos = pos; pos += fdArrayIdx0.Length;
                var privPos = new int[privBlocks.Count];
                for (int i = 0; i < privBlocks.Count; i++) { privPos[i] = pos; pos += privBlocks[i].Length; }

                // Real builds with computed offsets (same sizes as the placeholders).
                byte[] topIdx = BuildIndex(new List<byte[]> { EmitTopDict(topDict, charStringsPos, charsetPos, fdArrayPos, fdSelectPos) });
                var fdList = new List<byte[]>();
                for (int i = 0; i < fdParsed.Count; i++)
                    fdList.Add(EmitFontDict(fdParsed[i], privSizes[i], privBlocks[i].Length > 0 ? privPos[i] : 0));
                byte[] fdArrayIdx = BuildIndex(fdList);

                using var ms = new MemoryStream();
                ms.WriteByte(_d[0]); ms.WriteByte(_d[1]); ms.WriteByte(4); ms.WriteByte(4); // header: major minor hdrSize offSize
                ms.Write(nameIdx, 0, nameIdx.Length);
                ms.Write(topIdx, 0, topIdx.Length);
                ms.Write(strIdx, 0, strIdx.Length);
                ms.Write(gsubrIdx, 0, gsubrIdx.Length);
                ms.Write(charsetBytes, 0, charsetBytes.Length);
                ms.Write(fdSelectBytes, 0, fdSelectBytes.Length);
                ms.Write(csIdx, 0, csIdx.Length);
                ms.Write(fdArrayIdx, 0, fdArrayIdx.Length);
                foreach (var b in privBlocks) ms.Write(b, 0, b.Length);
                return ms.ToArray();
            }

            // ---- DICT emit ------------------------------------------------------------------------

            private static byte[] EmitTopDict(Dict dict, int csOff, int charsetOff, int fdaOff, int fdsOff)
            {
                using var ms = new MemoryStream();
                foreach (var e in dict.Entries)
                {
                    switch (e.Op)
                    {
                        case 17: WriteOffset(ms, csOff); WriteOp(ms, 17); break;
                        case 15: WriteOffset(ms, charsetOff); WriteOp(ms, 15); break;
                        case 1236: WriteOffset(ms, fdaOff); WriteOp(ms, 1236); break;
                        case 1237: WriteOffset(ms, fdsOff); WriteOp(ms, 1237); break;
                        case 16: break; // Encoding: not used by CID fonts; drop
                        default: ms.Write(e.RawOps, 0, e.RawOps.Length); WriteOp(ms, e.Op); break;
                    }
                }
                return ms.ToArray();
            }

            private static byte[] EmitFontDict(Dict dict, int privSize, int privOff)
            {
                using var ms = new MemoryStream();
                foreach (var e in dict.Entries)
                {
                    if (e.Op == 18) { WriteOffset(ms, privSize); WriteOffset(ms, privOff); WriteOp(ms, 18); }
                    else { ms.Write(e.RawOps, 0, e.RawOps.Length); WriteOp(ms, e.Op); }
                }
                return ms.ToArray();
            }

            private static void WriteOffset(Stream s, int v)
            {
                s.WriteByte(29); s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
            }

            private static void WriteOp(Stream s, int op)
            {
                if (op >= 1200) { s.WriteByte(12); s.WriteByte((byte)(op - 1200)); }
                else s.WriteByte((byte)op);
            }

            // ---- CFF primitives -------------------------------------------------------------------

            private Dict ParseDict(byte[] d)
            {
                var dict = new Dict();
                var operands = new List<double>();
                int p = 0, runStart = 0;
                while (p < d.Length)
                {
                    int b0 = d[p];
                    if (b0 <= 21)
                    {
                        int opPos = p;
                        int op = b0; p++;
                        if (b0 == 12) { op = 1200 + d[p]; p++; }
                        var raw = new byte[opPos - runStart];
                        Array.Copy(d, runStart, raw, 0, raw.Length);
                        dict.Add(new Entry { Op = op, RawOps = raw, Ints = new List<double>(operands) });
                        operands.Clear();
                        runStart = p;
                    }
                    else if (b0 == 28) { operands.Add((short)((d[p + 1] << 8) | d[p + 2])); p += 3; }
                    else if (b0 == 29) { operands.Add((d[p + 1] << 24) | (d[p + 2] << 16) | (d[p + 3] << 8) | d[p + 4]); p += 5; }
                    else if (b0 == 30) { while (++p < d.Length) { int b = d[p]; if ((b & 0x0F) == 0x0F || (b >> 4) == 0x0F) { p++; break; } } operands.Add(0); }
                    else if (b0 >= 32 && b0 <= 246) { operands.Add(b0 - 139); p++; }
                    else if (b0 >= 247 && b0 <= 250) { operands.Add((b0 - 247) * 256 + d[p + 1] + 108); p += 2; }
                    else if (b0 >= 251 && b0 <= 254) { operands.Add(-(b0 - 251) * 256 - d[p + 1] - 108); p += 2; }
                    else p++;
                }
                return dict;
            }

            /// <summary>Read an INDEX at <paramref name="p"/>, returning each entry's raw bytes; advances p past it.</summary>
            private List<byte[]> ReadIndexRaw(ref int p)
            {
                int count = (_d[p] << 8) | _d[p + 1];
                if (count == 0) { p += 2; return new List<byte[]>(); }
                int offSize = _d[p + 2];
                int offBase = p + 3;
                var offs = new int[count + 1];
                for (int i = 0; i <= count; i++)
                {
                    int v = 0;
                    for (int b = 0; b < offSize; b++) v = (v << 8) | _d[offBase + i * offSize + b];
                    offs[i] = v;
                }
                int dataStart = offBase + (count + 1) * offSize - 1;
                var entries = new List<byte[]>(count);
                for (int i = 0; i < count; i++)
                {
                    int len = offs[i + 1] - offs[i];
                    var e = new byte[len];
                    Array.Copy(_d, dataStart + offs[i], e, 0, len);
                    entries.Add(e);
                }
                p = dataStart + offs[count];
                return entries;
            }

            private static byte[] BuildIndex(List<byte[]> entries)
            {
                using var ms = new MemoryStream();
                int count = entries.Count;
                ms.WriteByte((byte)(count >> 8)); ms.WriteByte((byte)count);
                if (count == 0) return ms.ToArray();
                int total = 1; foreach (var e in entries) total += e.Length;
                int offSize = total < 0x100 ? 1 : total < 0x10000 ? 2 : total < 0x1000000 ? 3 : 4;
                ms.WriteByte((byte)offSize);
                int off = 1;
                WriteOffN(ms, off, offSize);
                foreach (var e in entries) { off += e.Length; WriteOffN(ms, off, offSize); }
                foreach (var e in entries) ms.Write(e, 0, e.Length);
                return ms.ToArray();
            }

            private static void WriteOffN(Stream s, int v, int n)
            {
                for (int b = n - 1; b >= 0; b--) s.WriteByte((byte)(v >> (b * 8)));
            }

            private int CharsetLen(int off, int nGlyphs)
            {
                byte fmt = _d[off]; int p = off + 1; int gid = 1;
                if (fmt == 0) return 1 + 2 * (nGlyphs - 1);
                while (gid < nGlyphs)
                {
                    p += 2;                               // first SID/CID
                    int nLeft = fmt == 1 ? _d[p] : ((_d[p] << 8) | _d[p + 1]);
                    p += fmt == 1 ? 1 : 2;
                    gid += nLeft + 1;
                }
                return p - off;
            }

            private int FdSelectLen(int off, int nGlyphs)
            {
                byte fmt = _d[off];
                if (fmt == 0) return 1 + nGlyphs;
                if (fmt == 3) { int nR = (_d[off + 1] << 8) | _d[off + 2]; return 3 + nR * 3 + 2; }
                return 1;
            }

            private byte[] Slice(int off, int len)
            {
                var b = new byte[len];
                Array.Copy(_d, off, b, 0, len);
                return b;
            }
        }

        // ---- TrueType (glyf/loca) subsetter --------------------------------------------------------
        private sealed class TrueTypeSubset
        {
            private readonly byte[] _d;
            private readonly HashSet<int> _used = new HashSet<int>();

            public TrueTypeSubset(byte[] data, ICollection<int> usedGids)
            {
                _d = data;
                foreach (var g in usedGids) _used.Add(g);
                _used.Add(0); // .notdef always kept
            }

            private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);
            private static uint U32(byte[] d, int o) => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
            private static void W16(byte[] d, int o, ushort v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }
            private static void W32(byte[] d, int o, uint v) { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }

            public byte[]? Build()
            {
                ushort numTables = U16(_d, 4);
                var tables = new Dictionary<string, (int off, int len)>();
                int dir = 12;
                for (int i = 0; i < numTables; i++)
                {
                    string tag = System.Text.Encoding.ASCII.GetString(_d, dir, 4);
                    tables[tag] = ((int)U32(_d, dir + 8), (int)U32(_d, dir + 12));
                    dir += 16;
                }
                if (!tables.TryGetValue("glyf", out var glyf) || !tables.TryGetValue("loca", out var loca) ||
                    !tables.TryGetValue("head", out var head) || !tables.TryGetValue("maxp", out var maxp))
                    return null; // not a glyf-flavoured font (likely CFF) — caller keeps whole font

                int numGlyphs = U16(_d, maxp.off + 4);
                short locFormat = (short)U16(_d, head.off + 50);

                // Read the loca offsets (into the glyf table).
                var locaOff = new int[numGlyphs + 1];
                for (int i = 0; i <= numGlyphs; i++)
                    locaOff[i] = locFormat == 0 ? U16(_d, loca.off + i * 2) * 2 : (int)U32(_d, loca.off + i * 4);

                // Transitive closure over composite-glyph components.
                var stack = new List<int>(_used);
                while (stack.Count > 0)
                {
                    int g = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
                    if (g < 0 || g >= numGlyphs) continue;
                    int gs = glyf.off + locaOff[g], ge = glyf.off + locaOff[g + 1];
                    if (ge - gs < 10) continue;
                    short nc = (short)U16(_d, gs);
                    if (nc >= 0) continue;                          // simple glyph
                    int o = gs + 10;                                 // composite components
                    while (o + 4 <= ge)
                    {
                        ushort flags = U16(_d, o); ushort comp = U16(_d, o + 2); o += 4;
                        if (_used.Add(comp)) stack.Add(comp);
                        o += (flags & 0x0001) != 0 ? 4 : 2;          // ARG_1_AND_2_ARE_WORDS
                        if ((flags & 0x0008) != 0) o += 2;           // WE_HAVE_A_SCALE
                        else if ((flags & 0x0040) != 0) o += 4;      // X_AND_Y_SCALE
                        else if ((flags & 0x0080) != 0) o += 8;      // TWO_BY_TWO
                        if ((flags & 0x0020) == 0) break;            // MORE_COMPONENTS
                    }
                }

                // Build new glyf (used glyphs' bytes, others empty) + new LONG loca.
                var newGlyf = new MemoryStream();
                var newLoca = new int[numGlyphs + 1];
                for (int g = 0; g < numGlyphs; g++)
                {
                    newLoca[g] = (int)newGlyf.Length;
                    if (_used.Contains(g))
                    {
                        int gs = glyf.off + locaOff[g], len = locaOff[g + 1] - locaOff[g];
                        if (len > 0) newGlyf.Write(_d, gs, len);
                        while (newGlyf.Length % 2 != 0) newGlyf.WriteByte(0); // 2-byte align
                    }
                }
                newLoca[numGlyphs] = (int)newGlyf.Length;
                byte[] glyfBytes = newGlyf.ToArray();
                var locaBytes = new byte[(numGlyphs + 1) * 4];
                for (int i = 0; i <= numGlyphs; i++) W32(locaBytes, i * 4, (uint)newLoca[i]);

                // head copy with indexToLocFormat=1 (long) and checkSumAdjustment cleared.
                var headBytes = new byte[head.len];
                Array.Copy(_d, head.off, headBytes, 0, head.len);
                W32(headBytes, 8, 0);        // checkSumAdjustment (recomputed after assembly)
                W16(headBytes, 50, 1);       // indexToLocFormat = long

                // Assemble the output sfnt. Keep only the tables a PDF CIDFontType2 embed needs (glyf/loca/
                // head/hhea/hmtx/maxp + hinting cvt/fpgm/prep/gasp + cmap); drop GSUB/GPOS/GDEF/name/post/
                // DSIG/kern/… (shaping/metadata unused once glyphs are placed). glyf/loca/head are replaced.
                var keep = new HashSet<string> { "glyf", "loca", "head", "hhea", "hmtx", "maxp", "cvt ", "fpgm", "prep", "gasp", "cmap" };
                var outTables = new List<(string tag, byte[] data)>();
                foreach (var kv in tables)
                {
                    if (!keep.Contains(kv.Key)) continue;
                    byte[] data = kv.Key switch
                    {
                        "glyf" => glyfBytes,
                        "loca" => locaBytes,
                        "head" => headBytes,
                        _ => Sub(kv.Value.off, kv.Value.len),
                    };
                    outTables.Add((kv.Key, data));
                }
                outTables.Sort((a, b) => string.CompareOrdinal(a.tag, b.tag));

                int n = outTables.Count;
                int headerLen = 12 + n * 16;
                int total = headerLen;
                var pad = new int[n];
                for (int i = 0; i < n; i++) { int len = outTables[i].data.Length; pad[i] = (4 - (len & 3)) & 3; total += len + pad[i]; }

                var outp = new byte[total];
                Array.Copy(_d, 0, outp, 0, 4);   // sfntVersion
                ushort entrySel = 0; while ((1 << (entrySel + 1)) <= n) entrySel++;
                ushort searchRange = (ushort)((1 << entrySel) * 16);
                W16(outp, 4, (ushort)n);
                W16(outp, 6, searchRange);
                W16(outp, 8, entrySel);
                W16(outp, 10, (ushort)(n * 16 - searchRange));

                int off = headerLen; int headOutOff = -1;
                for (int i = 0; i < n; i++)
                {
                    var (tag, data) = outTables[i];
                    Array.Copy(data, 0, outp, off, data.Length);
                    int rec = 12 + i * 16;
                    System.Text.Encoding.ASCII.GetBytes(tag, 0, 4, outp, rec);
                    W32(outp, rec + 4, CheckSum(outp, off, data.Length + pad[i]));
                    W32(outp, rec + 8, (uint)off);
                    W32(outp, rec + 12, (uint)data.Length);
                    if (tag == "head") headOutOff = off;
                    off += data.Length + pad[i];
                }

                // head.checkSumAdjustment = 0xB1B0AFBA - checksum(entire file).
                if (headOutOff >= 0) W32(outp, headOutOff + 8, 0xB1B0AFBA - CheckSum(outp, 0, outp.Length));
                return outp;
            }

            private static uint CheckSum(byte[] d, int off, int len)
            {
                uint sum = 0;
                for (int i = 0; i < len; i += 4)
                {
                    uint w = (uint)(d[off + i] << 24);
                    if (i + 1 < len) w |= (uint)(d[off + i + 1] << 16);
                    if (i + 2 < len) w |= (uint)(d[off + i + 2] << 8);
                    if (i + 3 < len) w |= d[off + i + 3];
                    sum += w;
                }
                return sum;
            }

            private byte[] Sub(int off, int len) { var b = new byte[len]; Array.Copy(_d, off, b, 0, len); return b; }
        }
    }
}
