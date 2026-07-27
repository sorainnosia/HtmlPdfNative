using System.Collections.Generic;
using HtmlPdfNative.TtfParser;

namespace HtmlPdfNative.Fonts
{
    /// <summary>
    /// A TrueType font to be embedded as a PDF Type0/CIDFontType2 (Identity-H). Wraps a parsed
    /// <see cref="TtfFace"/>, measures text via its own metrics, and records the code points used
    /// (for the <c>/W</c> width array and <c>/ToUnicode</c> map). Analogue of the embedded-font side
    /// of Rust <c>font.rs</c>/<c>pdf.rs</c>.
    /// </summary>
    public sealed class EmbeddedFont
    {
        public TtfFace Face { get; }
        /// <summary>PostScript-ish base name written to the PDF (no spaces).</summary>
        public string BaseName { get; }
        public readonly SortedSet<int> UsedCodepoints = new SortedSet<int>();

        public EmbeddedFont(TtfFace face, string baseName) { Face = face; BaseName = baseName; }

        /// <summary>1000-unit-em scale factor for widths/metrics.</summary>
        public float Scale1000 => 1000f / Face.UnitsPerEm;

        /// <summary>True for a CFF/OpenType font ('OTTO') — embedded as CIDFontType0 /FontFile3 instead of Type2.</summary>
        public bool IsOpenType =>
            Face.Data.Length >= 4 && Face.Data[0] == 0x4F && Face.Data[1] == 0x54 && Face.Data[2] == 0x54 && Face.Data[3] == 0x4F;

        public void MarkUsed(string text)
        {
            foreach (var cp in Codepoints(text)) UsedCodepoints.Add(cp);
        }

        /// <summary>Advance width of a string in points at the given font size.</summary>
        public float MeasurePt(string text, float sizePt)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            long units = 0;
            foreach (var cp in Codepoints(text))
                units += Face.Advance(Face.GlyphId(cp));
            return (float)units / Face.UnitsPerEm * sizePt;
        }

        /// <summary>Enumerate Unicode code points (handling surrogate pairs).</summary>
        public static IEnumerable<int> Codepoints(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                { yield return char.ConvertToUtf32(c, s[i + 1]); i++; }
                else yield return c;
            }
        }
    }
}
