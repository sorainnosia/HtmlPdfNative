using System;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HtmlPdfNative.Images
{
    /// <summary>A decoded raster image: 8-bit RGB planes plus an optional 8-bit alpha plane.</summary>
    public sealed class DecodedImage
    {
        public int Width;
        public int Height;
        public byte[] Rgb = Array.Empty<byte>();   // Width*Height*3
        public byte[]? Alpha;                      // Width*Height (null if fully opaque)
    }

    /// <summary>
    /// Loads and decodes images referenced by <c>&lt;img src&gt;</c> / <c>url()</c>. Port of the
    /// image-loading side of <c>src/render.rs</c> (Rust used the <c>image</c> crate; .NET uses
    /// SixLabors.ImageSharp). Resolves <c>data:</c> URIs and file paths (relative to
    /// <see cref="BaseDirectory"/>), and caches decoded results by source key.
    /// </summary>
    public static class ImageLoader
    {
        /// <summary>Directory that relative image paths resolve against (set per-conversion). Rust: image base dir.</summary>
        [ThreadStatic] public static string? BaseDirectory;

        private static readonly Dictionary<string, DecodedImage?> Cache = new Dictionary<string, DecodedImage?>();

        public static DecodedImage? Load(string? src)
        {
            if (string.IsNullOrWhiteSpace(src)) return null;
            string key = (BaseDirectory ?? "") + "|" + src;
            if (Cache.TryGetValue(key, out var cached)) return cached;
            DecodedImage? result = null;
            try
            {
                byte[]? bytes = ResolveBytes(src!);
                if (bytes != null && bytes.Length > 0) result = Decode(bytes);
            }
            catch { result = null; }
            Cache[key] = result;
            return result;
        }

        /// <summary>Resolve a <c>url()</c>/<c>src</c> (data: URI or file path vs BaseDirectory) to raw bytes.</summary>
        public static byte[]? ReadBytes(string src) => ResolveBytes(src);

        private static byte[]? ResolveBytes(string src)
        {
            src = src.Trim();
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = src.IndexOf(',');
                if (comma < 0) return null;
                string meta = src.Substring(5, comma - 5);
                string payload = src.Substring(comma + 1);
                if (meta.IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0)
                    return System.Convert.FromBase64String(payload.Trim());
                return System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return AllowNetwork ? FetchUrl(src) : null;
            if (src.StartsWith("//", StringComparison.Ordinal))    // protocol-relative
                return AllowNetwork ? FetchUrl("https:" + src) : null;
            if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                src = new Uri(src).LocalPath;

            string path = Path.IsPathRooted(src) || string.IsNullOrEmpty(BaseDirectory)
                ? src : Path.Combine(BaseDirectory!, src);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        /// <summary>Fetch remote resources (http/https) for <c>@font-face</c> src and &lt;img&gt; src. On by default;
        /// set false to force fully-offline rendering.</summary>
        public static bool AllowNetwork { get; set; } = true;

        private static readonly System.Collections.Generic.Dictionary<string, byte[]?> _urlCache
            = new System.Collections.Generic.Dictionary<string, byte[]?>();
        private static System.Net.Http.HttpClient? _http;

        /// <summary>Download a URL to bytes (cached; 10s timeout; graceful null on any failure).</summary>
        private static byte[]? FetchUrl(string url)
        {
            lock (_urlCache) if (_urlCache.TryGetValue(url, out var cached)) return cached;
            byte[]? result = null;
            try
            {
                if (_http == null)
                {
                    _http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    // A browser-ish UA: some CDNs (incl. Google Fonts) 403 or content-negotiate on the User-Agent.
                    _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) HtmlPdfNative/1.0");
                }
                result = _http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception ex) { System.Console.Error.WriteLine("[NET] fetch failed " + url + ": " + ex.Message); result = null; }
            lock (_urlCache) _urlCache[url] = result;
            return result;
        }

        private static DecodedImage Decode(byte[] bytes)
        {
            using (var img = Image.Load<Rgba32>(bytes))
            {
                int w = img.Width, h = img.Height;
                var rgba = new byte[w * h * 4];
                img.CopyPixelDataTo(rgba);

                var rgb = new byte[w * h * 3];
                byte[]? alpha = null;
                bool hasAlpha = false;
                for (int i = 0; i < w * h; i++)
                {
                    rgb[i * 3] = rgba[i * 4];
                    rgb[i * 3 + 1] = rgba[i * 4 + 1];
                    rgb[i * 3 + 2] = rgba[i * 4 + 2];
                    if (rgba[i * 4 + 3] != 255) hasAlpha = true;
                }
                if (hasAlpha)
                {
                    alpha = new byte[w * h];
                    for (int i = 0; i < w * h; i++) alpha[i] = rgba[i * 4 + 3];
                }
                return new DecodedImage { Width = w, Height = h, Rgb = rgb, Alpha = alpha };
            }
        }
    }
}
