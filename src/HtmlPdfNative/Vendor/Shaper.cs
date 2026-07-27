using System;

namespace HtmlPdfNative.Shaping
{
    /// <summary>
    /// Vendored .NET port of the Rust crate <c>rustybuzz</c>. Shapes a run to positioned glyphs; wraps HarfBuzzSharp.
    /// Ported because there is no drop-in .NET NuGet equivalent (or the exact behaviour must match Rust).
    /// </summary>
    public static class Shaper
    {
        // TODO: port rustybuzz. See PLAN.md (vendored crates table) for scope + upstream reference.
        public const string RustCrate = "rustybuzz";
    }
}
