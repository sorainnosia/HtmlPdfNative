using System;

namespace HtmlPdfNative.SvgRender
{
    /// <summary>
    /// Vendored .NET port of the Rust crate <c>resvg/usvg</c>. Parses SVG and rasterizes to RGBA via SkiaSharp; injects xmlns/currentColor.
    /// Ported because there is no drop-in .NET NuGet equivalent (or the exact behaviour must match Rust).
    /// </summary>
    public static class SvgRasterizer
    {
        // TODO: port resvg/usvg. See PLAN.md (vendored crates table) for scope + upstream reference.
        public const string RustCrate = "resvg/usvg";
    }
}
