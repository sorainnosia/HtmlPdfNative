using System;

namespace HtmlPdfNative.UnicodeBidi
{
    /// <summary>
    /// Vendored .NET port of the Rust crate <c>unicode-bidi</c>. Implements UAX #9 (paragraph levels, reordering, mirroring).
    /// Ported because there is no drop-in .NET NuGet equivalent (or the exact behaviour must match Rust).
    /// </summary>
    public static class BidiInfo
    {
        // TODO: port unicode-bidi. See PLAN.md (vendored crates table) for scope + upstream reference.
        public const string RustCrate = "unicode-bidi";
    }
}
