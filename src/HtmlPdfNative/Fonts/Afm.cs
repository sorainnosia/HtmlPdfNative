using System;
using HtmlPdfNative.Render;

namespace HtmlPdfNative.Fonts
{
    /// <summary>
    /// Adobe Font Metrics (AFM) glyph advance widths (per 1000-unit em) for the base-14 standard PDF
    /// fonts. Used to measure text for line wrapping / alignment. Analogue of the Rust
    /// <c>get_helvetica_width</c> / <c>get_times_width</c> width tables in <c>src/font.rs</c>.
    /// Only ASCII 32..126 is tabulated precisely; other WinAnsi codes fall back to an average.
    /// </summary>
    public static class Afm
    {
        // Widths for printable ASCII 32..126 (index = code - 32).
        private static readonly short[] Helvetica =
        {
            278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278, // 32..47
            556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556, // 48..63
            1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778, // 64..79
            667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,  // 80..95
            333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,  // 96..111
            556,556,333,500,278,556,500,722,500,500,500,334,260,334,584       // 112..126
        };

        private static readonly short[] HelveticaBold =
        {
            278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,
            556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,
            975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,
            667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,
            333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,
            611,611,389,556,333,611,556,778,556,556,500,389,280,389,584
        };

        private static readonly short[] TimesRoman =
        {
            250,333,408,500,500,833,778,180,333,333,500,564,250,333,250,278,
            500,500,500,500,500,500,500,500,500,500,278,278,564,564,564,444,
            921,722,667,667,722,611,556,722,722,333,389,722,611,889,722,722,
            556,722,667,556,611,722,722,944,722,722,611,333,278,333,469,500,
            333,444,500,444,500,444,333,500,500,278,278,500,278,778,500,500,
            500,500,333,389,278,500,500,722,500,500,444,480,200,480,541
        };

        private static readonly short[] TimesBold =
        {
            250,333,555,500,500,1000,833,278,333,333,500,570,250,333,250,278,
            500,500,500,500,500,500,500,500,500,500,333,333,570,570,570,500,
            930,722,667,722,722,667,611,778,778,389,500,778,667,944,722,778,
            611,778,722,556,667,722,722,1000,722,722,667,333,278,333,581,500,
            333,500,556,444,556,444,333,500,556,278,333,556,278,833,556,500,
            556,556,444,389,333,556,500,722,500,500,444,394,220,394,520
        };

        /// <summary>Advance width in 1000-unit em for a single char in the given face.</summary>
        public static int Width1000(FontFace face, char c)
        {
            short[] table = TableFor(face, out bool monospace);
            if (monospace) return 600; // Courier
            int idx = c - 32;
            if (idx >= 0 && idx < table.Length) return table[idx];
            if (c == ' ') return table.Length > 0 ? table[0] : 250; // nbsp ~ space
            return 500; // WinAnsi non-ASCII fallback (average)
        }

        /// <summary>Measure a string's advance in points at the given font size.</summary>
        public static float MeasurePt(FontFace face, string text, float fontSizePt)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            long units = 0;
            foreach (char c in text) units += Width1000(face, c);
            return units / 1000f * fontSizePt;
        }

        private static short[] TableFor(FontFace face, out bool monospace)
        {
            monospace = false;
            switch (face)
            {
                case FontFace.Helvetica:
                case FontFace.HelveticaOblique: return Helvetica;
                case FontFace.HelveticaBold:
                case FontFace.HelveticaBoldOblique: return HelveticaBold;
                case FontFace.TimesRoman:
                case FontFace.TimesItalic: return TimesRoman;
                case FontFace.TimesBold:
                case FontFace.TimesBoldItalic: return TimesBold;
                case FontFace.Courier:
                case FontFace.CourierBold:
                case FontFace.CourierOblique:
                case FontFace.CourierBoldOblique: monospace = true; return Array.Empty<short>();
                default: return Helvetica;
            }
        }

        /// <summary>The PDF base font name for a <see cref="FontFace"/>.</summary>
        public static string PdfName(FontFace face)
        {
            switch (face)
            {
                case FontFace.Helvetica: return "Helvetica";
                case FontFace.HelveticaBold: return "Helvetica-Bold";
                case FontFace.HelveticaOblique: return "Helvetica-Oblique";
                case FontFace.HelveticaBoldOblique: return "Helvetica-BoldOblique";
                case FontFace.TimesRoman: return "Times-Roman";
                case FontFace.TimesBold: return "Times-Bold";
                case FontFace.TimesItalic: return "Times-Italic";
                case FontFace.TimesBoldItalic: return "Times-BoldItalic";
                case FontFace.Courier: return "Courier";
                case FontFace.CourierBold: return "Courier-Bold";
                case FontFace.CourierOblique: return "Courier-Oblique";
                case FontFace.CourierBoldOblique: return "Courier-BoldOblique";
                default: return "Helvetica";
            }
        }

        /// <summary>Pick a base-14 face from a CSS font-family + bold/italic flags.</summary>
        // Base-14 metric substitutes for common CSS family names, resolved in font-family priority order.
        private static readonly string[] MonoNames =
            { "courier", "mono", "consol", "menlo", "monaco", "code", "jetbrains", "cascadia", "inconsolata", "roboto mono", "sf mono" };
        private static readonly string[] SansNames =
            { "arial", "helvetica", "verdana", "tahoma", "segoe", "roboto", "calibri", "candara", "gill", "lato", "open sans",
              "ubuntu", "system-ui", "apple-system", "noto sans", "franklin", "trebuchet", "sans-serif", "sans" };
        private static readonly string[] SerifNames =
            { "times", "georgia", "garamond", "palatino", "cambria", "book antiqua", "minion", "baskerville", "playfair",
              "merriweather", "pt serif", "noto serif", "cardo", "constantia", "didot", "century", "serif" };

        public static FontFace Resolve(string? family, bool bold, bool italic)
        {
            foreach (var raw in (family ?? "").Split(','))
            {
                var f = raw.Trim().Trim('"', '\'').Trim().ToLowerInvariant();
                if (f.Length == 0) continue;
                // Order matters: "sans-serif" contains "serif", so test mono then sans then serif.
                if (Matches(f, MonoNames))
                    return bold ? (italic ? FontFace.CourierBoldOblique : FontFace.CourierBold) : (italic ? FontFace.CourierOblique : FontFace.Courier);
                if (Matches(f, SansNames))
                    return bold ? (italic ? FontFace.HelveticaBoldOblique : FontFace.HelveticaBold) : (italic ? FontFace.HelveticaOblique : FontFace.Helvetica);
                if (Matches(f, SerifNames))
                    return bold ? (italic ? FontFace.TimesBoldItalic : FontFace.TimesBold) : (italic ? FontFace.TimesItalic : FontFace.TimesRoman);
                // Unknown named font: fall through to the next family in the list.
            }
            return bold ? (italic ? FontFace.HelveticaBoldOblique : FontFace.HelveticaBold) : (italic ? FontFace.HelveticaOblique : FontFace.Helvetica);
        }

        private static bool Matches(string f, string[] names)
        {
            foreach (var n in names) if (f.Contains(n)) return true;
            return false;
        }

        // Georgia is a serif that we substitute with the bundled OFL "Gelasio" (metric-compatible),
        // embedded rather than mapped to base-14 Times — matching the Rust engine (font.rs). Only true
        // when Georgia/Gelasio is the FIRST resolvable family (a preceding mono/sans/other family wins).
        private static readonly string[] GeorgiaNames = { "georgia", "gelasio" };
        public static bool IsGeorgia(string? family)
        {
            foreach (var raw in (family ?? "").Split(','))
            {
                var f = raw.Trim().Trim('"', '\'').Trim().ToLowerInvariant();
                if (f.Length == 0) continue;
                // Same priority as Resolve: mono, sans, then serif — so an earlier family claims the slot.
                if (Matches(f, MonoNames)) return false;
                if (Matches(f, SansNames)) return false;
                if (Matches(f, GeorgiaNames)) return true;
                if (Matches(f, SerifNames)) return false;
                // Unknown named font: fall through to the next family in the list.
            }
            return false;
        }
    }
}
