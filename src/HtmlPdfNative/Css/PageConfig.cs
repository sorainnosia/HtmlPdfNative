namespace HtmlPdfNative.Css
{
    /// <summary>
    /// Parsed <c>@page</c> configuration. Port of <c>css::PageConfig</c> in <c>src/css.rs</c>.
    /// </summary>
    public sealed class PageConfig
    {
        public float? Width { get; set; }
        public float? Height { get; set; }
        public float MarginTop { get; set; }
        public float MarginRight { get; set; }
        public float MarginBottom { get; set; }
        public float MarginLeft { get; set; }

        /// <summary>True once any <c>@page</c> margin (incl. <c>margin:0</c>) is parsed — distinguishes a
        /// deliberate <c>@page{margin:0}</c> from "no margin at all" (see "chrome-print-parity").</summary>
        public bool MarginExplicit { get; set; }

        /// <summary>True when <c>@page{size:…}</c> set an explicit page size (gates print shrink-to-fit).</summary>
        public bool SizeExplicit { get; set; }
    }
}
