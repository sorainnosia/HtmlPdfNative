using System;
using System.IO;
using System.Reflection;

namespace HtmlPdfNative
{
    /// <summary>
    /// Loads bundled assets (fonts, typst_math) either from embedded manifest resources
    /// (when built with <c>-p:HtmlPdfEmbedAssets=true</c>) or from the <c>Assets/</c> folder copied
    /// next to the assembly. Mirrors how Rust bakes fonts into the binary via
    /// <c>compressed_fonts.rs</c> / <c>build.rs</c>.
    /// </summary>
    public static class Assets
    {
        private const string ResourcePrefix = "HtmlPdfNative.Assets.";

        /// <summary>Directory containing the copied Assets tree (when not embedded).</summary>
        public static string AssetsDirectory =>
            Path.Combine(AppContext.BaseDirectory ?? Directory.GetCurrentDirectory(), "Assets");

        /// <summary>
        /// Open an asset by its relative path (e.g. <c>fonts/NotoSans-Regular.ttf</c>).
        /// Checks embedded resources first, then the on-disk Assets folder.
        /// </summary>
        public static Stream Open(string relativePath)
        {
            if (relativePath == null) throw new ArgumentNullException(nameof(relativePath));
            var normalized = relativePath.Replace('\\', '/').TrimStart('/');

            // Embedded resource: LogicalName is "HtmlPdfNative.Assets.<dir/dir/file.ext>"
            var asm = typeof(Assets).Assembly;
            var resName = ResourcePrefix + normalized;
            var stream = asm.GetManifestResourceStream(resName);
            if (stream != null) return stream;

            var onDisk = Path.Combine(AssetsDirectory, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(onDisk)) return File.OpenRead(onDisk);

            throw new FileNotFoundException(
                $"Asset '{relativePath}' not found (neither embedded resource '{resName}' nor '{onDisk}').");
        }

        /// <summary>Read an asset fully into a byte array.</summary>
        public static byte[] ReadAllBytes(string relativePath)
        {
            using (var s = Open(relativePath))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Absolute path to the fonts directory (on-disk mode).</summary>
        public static string FontsDirectory => Path.Combine(AssetsDirectory, "fonts");
    }
}
