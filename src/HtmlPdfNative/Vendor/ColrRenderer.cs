using System;

namespace HtmlPdfNative.EmojiColr
{
    /// <summary>
    /// Vendored .NET port of the Rust crate <c>(emoji_colr.rs)</c>. Rasterizes COLRv1 layered emoji to RGBA.
    /// Ported because there is no drop-in .NET NuGet equivalent (or the exact behaviour must match Rust).
    /// </summary>
    public static class ColrRenderer
    {
        // TODO: port (emoji_colr.rs). See PLAN.md (vendored crates table) for scope + upstream reference.
        public const string RustCrate = "(emoji_colr.rs)";
    }
}
