using System;

namespace HtmlPdfNative.VariableFonts
{
    /// <summary>
    /// Vendored .NET port of the Rust crate <c>skrifa</c>. Resolves a named/axis instance and interpolated metrics.
    /// Ported because there is no drop-in .NET NuGet equivalent (or the exact behaviour must match Rust).
    /// </summary>
    public static class VariationSolver
    {
        // TODO: port skrifa. See PLAN.md (vendored crates table) for scope + upstream reference.
        public const string RustCrate = "skrifa";
    }
}
