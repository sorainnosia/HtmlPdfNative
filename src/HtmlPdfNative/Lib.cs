namespace HtmlPdfNative
{
    /// <summary>Library-level constants and helpers. Mirrors free functions in <c>src/lib.rs</c>.</summary>
    public static class Lib
    {
        /// <summary>
        /// Chrome's default print margin for documents with no explicit <c>@page</c> margin.
        /// See memory note "chrome-print-parity": ~27.75pt (0.386in).
        /// </summary>
        public const float ChromeDefaultPrintMarginPt = 27.75f;

        /// <summary>CSS px -> PDF pt scale (96dpi CSS px, 72pt/inch).</summary>
        public const float PxToPt = 0.75f;

        /// <summary>Default line-height multiplier for `normal` (Chrome parity). See "chrome-print-parity".</summary>
        public const float NormalLineHeight = 1.15f;

        /// <summary>
        /// The `normal` line height in pt, rounded to a whole CSS pixel the way Chrome snaps the line
        /// box (see memory "chrome-print-parity" 2b). <paramref name="fontSizePt"/> is in points.
        /// </summary>
        public static float NormalLineHeightPt(float fontSizePt)
            => (float)System.Math.Round(fontSizePt / PxToPt * NormalLineHeight) * PxToPt;

        /// <summary>
        /// Apply Chrome's default page margin when the document set no explicit <c>@page</c> margin.
        /// Analogue of Rust <c>apply_default_page_margin</c>.
        /// </summary>
        public static void ApplyDefaultPageMargin(Css.PageConfig config)
        {
            if (config == null || config.MarginExplicit) return;
            config.MarginTop = config.MarginRight = config.MarginBottom = config.MarginLeft = ChromeDefaultPrintMarginPt;
            config.MarginExplicit = true;
        }
    }
}
