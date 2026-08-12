using System.Collections.Generic;
using HtmlPdfNative.Style;
using HtmlPdfNative.TtfParser;

namespace HtmlPdfNative.Fonts
{
    /// <summary>
    /// Chooses between the base-14 fonts and an embedded fallback (bundled Noto) for a run of text.
    /// A word is routed to the embedded font when it contains any character the base-14
    /// WinAnsi encoding can't represent (accented Latin-ext, Cyrillic, Greek, Arabic, symbols…).
    /// Loads the bundled fonts from <see cref="Assets"/>. Subset-to-shrink is a later step; for now
    /// the whole face is embedded. Analogue of the font-routing in Rust <c>font.rs</c>.
    /// </summary>
    public static class FontManager
    {
        private static readonly Dictionary<string, EmbeddedFont?> Loaded = new Dictionary<string, EmbeddedFont?>();
        private static readonly object Gate = new object();

        // @font-face registry: key = "family|I" (lowercased family + italic flag), value = the loaded
        // custom faces for that family/style at each declared numeric weight. ResolveCustom picks the
        // nearest weight so e.g. font-weight:800 selects Syne ExtraBold rather than a collapsed "bold".
        private static readonly Dictionary<string, System.Collections.Generic.List<(int weight, EmbeddedFont font)>> CustomFaces
            = new Dictionary<string, System.Collections.Generic.List<(int weight, EmbeddedFont font)>>();

        /// <summary>Reset the @font-face registry (called per conversion so families don't leak across docs).</summary>
        public static void ClearFontFaces() { lock (Gate) CustomFaces.Clear(); }

        /// <summary>Register a @font-face: load the font file at <paramref name="src"/> (data: URI or path) and
        /// key it by family + weight/style. WOFF/WOFF2 are skipped (raw TTF/OTF only).</summary>
        public static void RegisterFontFace(string family, string src, int weight, bool italic)
        {
            if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(src)) return;
            string fam = family.Trim().Trim('"', '\'').ToLowerInvariant();
            string key = fam + "|" + (italic ? "I" : "");
            lock (Gate)
            {
                if (!CustomFaces.TryGetValue(key, out var list)) { list = new System.Collections.Generic.List<(int, EmbeddedFont)>(); CustomFaces[key] = list; }
                foreach (var e in list) if (e.weight == weight) return; // this weight already registered
                EmbeddedFont? result = null;
                try
                {
                    var bytes = Images.ImageLoader.ReadBytes(src);
                    // Unwrap WOFF1 (zlib) / WOFF2 (Brotli) into a plain sfnt first.
                    if (bytes != null && Woff.IsWoff(bytes)) bytes = Woff.TryDecode(bytes);
                    else if (bytes != null && Woff.IsWoff2(bytes)) { var dc = Woff2.TryDecode(bytes); System.Console.Error.WriteLine("[FONT] woff2 -> " + (dc == null ? "null" : dc.Length + "B sfnt=" + (dc.Length > 4 && IsSfnt(dc)))); bytes = dc; }
                    if (bytes != null && bytes.Length > 4 && IsSfnt(bytes))
                        result = new EmbeddedFont(TtfFace.Parse(bytes), "WF_" + fam.Replace(" ", "") + "_" + weight + (italic ? "i" : ""));
                }
                catch (System.Exception ex) { System.Console.Error.WriteLine("[FONT] @font-face load failed " + src + ": " + ex.Message); }
                if (result != null) list.Add((weight, result));
            }
        }

        private static bool IsSfnt(byte[] b)
        {
            // Accept raw TrueType (0x00010000 / 'true') or OpenType/CFF ('OTTO'); reject WOFF/WOFF2 wrappers.
            uint tag = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
            return tag == 0x00010000 || tag == 0x74727565 /*true*/ || tag == 0x4F54544F /*OTTO*/;
        }

        /// <summary>Return a registered @font-face font matching any family in the CSS font-family list, or null.</summary>
        public static EmbeddedFont? ResolveCustom(string? fontFamily, int weight, bool italic)
        {
            if (string.IsNullOrEmpty(fontFamily) || CustomFaces.Count == 0) return null;
            lock (Gate)
            {
                foreach (var raw in fontFamily!.Split(','))
                {
                    string fam = raw.Trim().Trim('"', '\'').ToLowerInvariant();
                    if (fam.Length == 0) continue;
                    // Prefer the requested style (italic), then the upright faces of the same family.
                    foreach (var key in new[] { fam + "|" + (italic ? "I" : ""), fam + "|" })
                        if (CustomFaces.TryGetValue(key, out var list) && list != null && list.Count > 0)
                            return NearestWeight(list, weight);
                }
            }
            return null;
        }

        /// <summary>Pick the face whose numeric weight is closest to the requested weight, breaking ties
        /// toward the heavier face (a mild nod to the CSS font-matching preference for weights >= 400).</summary>
        private static EmbeddedFont NearestWeight(System.Collections.Generic.List<(int weight, EmbeddedFont font)> list, int want)
        {
            var best = list[0];
            foreach (var e in list)
            {
                int d = System.Math.Abs(e.weight - want), bd = System.Math.Abs(best.weight - want);
                if (d < bd || (d == bd && e.weight > best.weight)) best = e;
            }
            return best.font;
        }

        // The CP1252 (WinAnsi) high-range specials that ARE representable by the base-14 fonts.
        private static readonly HashSet<int> Win1252Extra = new HashSet<int>
        {
            0x20AC,0x201A,0x0192,0x201E,0x2026,0x2020,0x2021,0x02C6,0x2030,0x0160,0x2039,0x0152,
            0x017D,0x2018,0x2019,0x201C,0x201D,0x2022,0x2013,0x2014,0x02DC,0x2122,0x0161,0x203A,
            0x0153,0x017E,0x0178,
        };

        public static bool IsWinAnsi(int cp) => cp <= 0xFF || Win1252Extra.Contains(cp);

        /// <summary>True if any character in <paramref name="word"/> needs the embedded fallback.</summary>
        public static bool NeedsEmbedded(string word)
        {
            foreach (var cp in EmbeddedFont.Codepoints(word))
                if (!IsWinAnsi(cp)) return true;
            return false;
        }

        private enum Script { Latin, Sc, Jp, Kr, Arabic, Hebrew, Math, Symbols }

        private static Script ScriptOf(int cp)
        {
            if ((cp >= 0xAC00 && cp <= 0xD7A3) || (cp >= 0x1100 && cp <= 0x11FF) || (cp >= 0x3130 && cp <= 0x318F)) return Script.Kr; // Hangul
            if ((cp >= 0x3040 && cp <= 0x309F) || (cp >= 0x30A0 && cp <= 0x30FF)) return Script.Jp;                                  // Kana
            if ((cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0x3000 && cp <= 0x303F) || (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0xFF00 && cp <= 0xFFEF)) return Script.Sc; // CJK ideographs/punct/fullwidth
            // Arabic: base + supplements + presentation forms A/B (ArabicShaper emits FE70–FEFF joining forms).
            if ((cp >= 0x0600 && cp <= 0x06FF) || (cp >= 0x0750 && cp <= 0x077F) || (cp >= 0x08A0 && cp <= 0x08FF) || (cp >= 0xFB50 && cp <= 0xFDFF) || (cp >= 0xFE70 && cp <= 0xFEFF)) return Script.Arabic;
            if ((cp >= 0x0590 && cp <= 0x05FF) || (cp >= 0xFB1D && cp <= 0xFB4F)) return Script.Hebrew;                              // Hebrew + presentation forms
            if ((cp >= 0x2200 && cp <= 0x22FF) || (cp >= 0x2A00 && cp <= 0x2AFF) || (cp >= 0x27C0 && cp <= 0x27EF) || (cp >= 0x2980 && cp <= 0x29FF)) return Script.Math; // math operators
            if ((cp >= 0x2600 && cp <= 0x27BF) || (cp >= 0x2B00 && cp <= 0x2BFF) || (cp >= 0x2190 && cp <= 0x21FF) ||
                (cp >= 0x25A0 && cp <= 0x25FF) || (cp >= 0x2300 && cp <= 0x23FF) ||
                (cp >= 0x1F000 && cp <= 0x1FAFF) || (cp >= 0x2460 && cp <= 0x24FF)) return Script.Symbols; // misc symbols/dingbats/arrows/geometric/technical/emoji/enclosed
            return Script.Latin;
        }

        /// <summary>The embedded font to use for a word, or null when base-14 suffices.
        /// Picks a CJK Noto face by script, else the Latin Noto fallback.</summary>
        public static EmbeddedFont? ResolveForWord(ComputedStyle style, string word)
        {
            // A matching @font-face wins for ALL of the element's text (even plain ASCII), so it embeds.
            var custom = ResolveCustom(style.FontFamily, style.Weight, style.Italic);
            if (custom != null) return custom;
            return ResolveScript(style.Bold, word);
        }

        /// <summary>Pick the embedded fallback face for a word by SCRIPT (no @font-face lookup) — used by SvgPainter,
        /// which has no ComputedStyle. Returns null when the base-14 WinAnsi fonts suffice.</summary>
        public static EmbeddedFont? ResolveScript(bool bold, string word)
        {
            bool needs = false;
            int firstCp = -1;
            Script script = Script.Latin;
            foreach (var cp in EmbeddedFont.Codepoints(word))
            {
                if (IsWinAnsi(cp)) continue;
                needs = true;
                if (firstCp < 0) firstCp = cp;
                var s = ScriptOf(cp);
                if (s != Script.Latin) { script = s; firstCp = cp; break; }
            }
            if (!needs) return null;
            switch (script)
            {
                // Dedicated scripts the Latin fallback can't cover.
                case Script.Sc: return Load("fonts/NotoSansSC-Regular.otf", "NotoSansSC");
                case Script.Jp: return Load("fonts/NotoSansJP-Regular.otf", "NotoSansJP");
                case Script.Kr: return Load("fonts/NotoSansKR-Regular.otf", "NotoSansKR");
                case Script.Arabic: return Load("fonts/NotoSansArabic-Regular.ttf", "NotoSansArabic") ?? Fallback(bold);
                case Script.Hebrew: return Load("fonts/NotoSansHebrew-Regular.ttf", "NotoSansHebrew") ?? Fallback(bold);
                // Math/Symbols/other: NotoSans covers most of these — only reach for the specialised face when NotoSans
                // actually LACKS the glyph (else arrows/bullets that NotoSans has would tofu in a sparse symbol font).
                // Symbols/arrows/math: NotoSans covers most; when it doesn't, try the specialised faces IN TURN — no
                // single one is complete (e.g. ↑/↓ U+2191/2193 are absent from BOTH NotoSans and NotoSansSymbols2 but
                // present in NotoSansMath), so a chain avoids dropping the glyph.
                case Script.Math: return FirstCovering(bold, firstCp,
                    ("fonts/NotoSansMath-Regular.ttf", "NotoSansMath"), ("fonts/NotoSansSymbols2-Regular.ttf", "NotoSansSymbols2"), ("fonts/NotoSansSC-Regular.otf", "NotoSansSC"));
                case Script.Symbols: return FirstCovering(bold, firstCp,
                    ("fonts/NotoSansSymbols2-Regular.ttf", "NotoSansSymbols2"), ("fonts/NotoSansMath-Regular.ttf", "NotoSansMath"), ("fonts/NotoSansSC-Regular.otf", "NotoSansSC"));
                default: return Fallback(bold);
            }
        }

        /// <summary>Prefer the broad NotoSans fallback; if it doesn't have <paramref name="cp"/>, use the specialised
        /// font when that one does (else keep NotoSans so we don't swap a real glyph for a worse tofu).</summary>
        private static EmbeddedFont? CoverOrSpecial(bool bold, int cp, string assetPath, string baseName)
        {
            var noto = Fallback(bold);
            if (noto != null && noto.Face.GlyphId(cp) != 0) return noto;
            var special = Load(assetPath, baseName);
            if (special != null && special.Face.GlyphId(cp) != 0) return special;
            return noto;
        }

        /// <summary>Prefer NotoSans; else the first specialised face (in order) that actually has <paramref name="cp"/>;
        /// else NotoSans (so we never swap a real glyph for a worse tofu).</summary>
        private static EmbeddedFont? FirstCovering(bool bold, int cp, params (string asset, string name)[] candidates)
        {
            var noto = Fallback(bold);
            if (noto != null && noto.Face.GlyphId(cp) != 0) return noto;
            foreach (var (asset, name) in candidates)
            {
                var f = Load(asset, name);
                if (f != null && f.Face.GlyphId(cp) != 0) return f;
            }
            return noto;
        }

        /// <summary>Bundled Noto Sans fallback (regular/bold), loaded + cached from Assets.</summary>
        public static EmbeddedFont? Fallback(bool bold)
        {
            string file = bold ? "fonts/NotoSans-Bold.ttf" : "fonts/NotoSans-Regular.ttf";
            string baseName = bold ? "NotoSans-Bold" : "NotoSans-Regular";
            return Load(file, baseName);
        }

        public static EmbeddedFont? Load(string assetPath, string baseName)
        {
            lock (Gate)
            {
                if (Loaded.TryGetValue(assetPath, out var f)) return f;
                EmbeddedFont? result = null;
                try
                {
                    var bytes = Assets.ReadAllBytes(assetPath);
                    var face = TtfFace.Parse(bytes);
                    result = new EmbeddedFont(face, baseName);
                }
                catch (System.Exception ex) { System.Console.Error.WriteLine("[FONT] load failed " + assetPath + ": " + ex.GetType().Name + " " + ex.Message); result = null; }
                Loaded[assetPath] = result;
                return result;
            }
        }
    }
}
