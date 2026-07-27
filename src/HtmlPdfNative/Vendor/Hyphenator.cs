using System;

namespace HtmlPdfNative.Hyphenation
{
    /// <summary>
    /// Vendored .NET port of the Rust crate <c>hyphenation</c>. Knuth-Liang patterns; ships en-US; returns break opportunities.
    /// Ported because there is no drop-in .NET NuGet equivalent (or the exact behaviour must match Rust).
    /// </summary>
    public static class Hyphenator
    {
        // TODO: port hyphenation. See PLAN.md (vendored crates table) for scope + upstream reference.
        public const string RustCrate = "hyphenation";
    }
}
