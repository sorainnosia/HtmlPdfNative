using System;
using System.IO;

namespace HtmlPdfNative
{
    /// <summary>
    /// Options controlling a single HTML/Markdown -> PDF conversion.
    /// Mirrors the surface of the Rust <c>html_to_pdf_bytes_with_*</c> entry points in
    /// <c>src/lib.rs</c> and the CLI <c>Args</c> in <c>src/main.rs</c>.
    /// </summary>
    public sealed class ConversionOptions
    {
        /// <summary>Page width in points. If null, derived from <see cref="Paper"/>.</summary>
        public float? Width { get; set; }

        /// <summary>Page height in points. If null, derived from <see cref="Paper"/>.</summary>
        public float? Height { get; set; }

        /// <summary>Paper size preset (default A4). Overridden by explicit width/height.</summary>
        public PaperSize Paper { get; set; } = PaperSize.A4;

        /// <summary>
        /// Directory that relative <c>&lt;img src&gt;</c> / <c>url()</c> paths resolve against.
        /// Equivalent to Rust <c>set_image_base_dir</c>. When converting a file this defaults to the
        /// input file's directory.
        /// </summary>
        public string? BaseDirectory { get; set; }

        /// <summary>User (open) password. When set, the PDF is AES-encrypted (see Rust <c>encryption.rs</c>).</summary>
        public string? UserPassword { get; set; }

        /// <summary>Owner (permissions) password. Optional; pairs with <see cref="UserPassword"/>.</summary>
        public string? OwnerPassword { get; set; }

        /// <summary>Optional font cache directory for downloaded web fonts (@font-face).</summary>
        public string? FontCacheDirectory { get; set; }

        /// <summary>Resolve the effective page size in points (paper preset unless width/height override).</summary>
        public PageSize ResolvePageSize()
        {
            var baseSize = PageSize.FromPaper(Paper);
            return new PageSize(Width ?? baseSize.Width, Height ?? baseSize.Height);
        }
    }

    /// <summary>
    /// Public entry point for the engine — the .NET analogue of Rust <c>lib.rs</c>.
    ///
    /// Pipeline (each stage is a ported subsystem; see the corresponding namespace):
    ///   HTML/MD text
    ///     -> <see cref="Dom.Document"/>            (scraper       -> AngleSharp)
    ///     -> <see cref="Css.Stylesheet"/>          (cssparser     -> AngleSharp.Css / our parser)
    ///     -> <see cref="Style.StyleComputer"/>     (selectors cascade)
    ///     -> <see cref="Styled.StyledNode"/>       (styled tree)
    ///     -> <see cref="Layout.LayoutBox"/>        (box/flex/grid/inline layout — the bulk of the port)
    ///     -> <see cref="Render.DisplayList"/>      (paint commands)
    ///     -> <see cref="Pdf.PdfGenerator"/>        (pdf-writer -> our HtmlPdfNative.PdfWriter)
    ///     -> byte[] (PDF)
    /// </summary>
    public static class HtmlToPdf
    {
        /// <summary>
        /// Convert an HTML string (with optional external CSS) to PDF bytes.
        /// Direct analogue of Rust <c>html_to_pdf_bytes_with_options</c>.
        /// </summary>
        public static byte[] Convert(string html, string? css, ConversionOptions? options = null)
        {
            if (html == null) throw new ArgumentNullException(nameof(html));
            options ??= new ConversionOptions();
            var size = options.ResolvePageSize();

            // --- pipeline (skeleton) -------------------------------------------------------------
            Images.ImageLoader.BaseDirectory = options.BaseDirectory;

            var document = Dom.Document.Parse(html);
            MathTex.MathPreprocessor.Process(document.Root); // $…$ / \[…\] → <img data-latex> placeholders
            var (stylesheet, pageConfig) = Css.Stylesheet.Parse(css, document, size.Width / Lib.PxToPt); // media-query width in px
            Lib.ApplyDefaultPageMargin(pageConfig); // Chrome ~27.75pt default when no @page margin
            var styleComputer = new Style.StyleComputer(stylesheet);
            var styledRoot = Styled.StyledNode.Build(document.Root, styleComputer);
            var layoutRoot = Layout.LayoutBox.Build(styledRoot);
            layoutRoot.Layout(new Layout.Rect(0, 0, size.Width, size.Height), pageConfig);
            var displayList = Render.Renderer.Paint(layoutRoot);
            var pdf = new Pdf.PdfGenerator(size, pageConfig);
            if (!string.IsNullOrEmpty(options.UserPassword))
                pdf.SetPasswords(options.UserPassword!, options.OwnerPassword);
            return pdf.Save(displayList);
        }

        /// <summary>
        /// Convert an input file (.html / .htm / .md) to a PDF file. Mirrors the CLI's -i/-c/-o flow.
        /// </summary>
        /// <param name="inputPath">Input HTML/Markdown file (-i).</param>
        /// <param name="cssPath">Optional CSS file (-c).</param>
        /// <param name="outputPath">Output PDF file (-o).</param>
        /// <param name="options">Conversion options (paper size, width/height, encryption…).</param>
        public static void ConvertFile(string inputPath, string? cssPath, string outputPath,
            ConversionOptions? options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            options ??= new ConversionOptions();
            options.BaseDirectory ??= Path.GetDirectoryName(Path.GetFullPath(inputPath));

            string html = ReadInputAsHtml(inputPath);
            string? css = cssPath != null ? File.ReadAllText(cssPath) : null;

            byte[] pdf = Convert(html, css, options);

            // The Rust CLI refuses to overwrite an existing output; keep parity to avoid stale files.
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            File.WriteAllBytes(outputPath, pdf);
        }

        /// <summary>Read an input file, converting Markdown to HTML first (mirrors Rust <c>markdown.rs</c>).</summary>
        private static string ReadInputAsHtml(string inputPath)
        {
            var ext = Path.GetExtension(inputPath).ToLowerInvariant();
            var text = File.ReadAllText(inputPath);
            if (ext == ".md" || ext == ".markdown")
                return Markdown.MarkdownConverter.ToHtml(text);
            return text;
        }
    }
}
