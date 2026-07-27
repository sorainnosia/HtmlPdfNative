using System;

namespace HtmlPdfNative
{
    /// <summary>
    /// Paper size options. Mirrors <c>PaperSize</c> in <c>src/main.rs</c>.
    /// Dimensions are in PDF points (1pt = 1/72 inch).
    /// </summary>
    public enum PaperSize
    {
        /// <summary>A3 — 842 x 1191 pt.</summary>
        A3,
        /// <summary>A4 — 595 x 842 pt (default).</summary>
        A4,
        /// <summary>A5 — 420 x 595 pt.</summary>
        A5,
        /// <summary>US Letter — 612 x 792 pt.</summary>
        Letter,
        /// <summary>US Legal — 612 x 1008 pt.</summary>
        Legal,
    }

    /// <summary>Point dimensions for a <see cref="PaperSize"/>.</summary>
    public readonly struct PageSize
    {
        public float Width { get; }
        public float Height { get; }

        public PageSize(float width, float height)
        {
            Width = width;
            Height = height;
        }

        public static PageSize FromPaper(PaperSize paper)
        {
            switch (paper)
            {
                case PaperSize.A3: return new PageSize(842f, 1191f);
                case PaperSize.A4: return new PageSize(595f, 842f);
                case PaperSize.A5: return new PageSize(420f, 595f);
                case PaperSize.Letter: return new PageSize(612f, 792f);
                case PaperSize.Legal: return new PageSize(612f, 1008f);
                default: throw new ArgumentOutOfRangeException(nameof(paper));
            }
        }
    }
}
