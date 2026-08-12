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

        /// <summary>Fetch an external CSS stylesheet (e.g. a Google Fonts <c>&lt;link&gt;</c>) as text. Uses a plain
        /// User-Agent so Google Fonts' <c>css2</c> endpoint serves <c>format('truetype')</c> (TTF) URLs, keeping the
        /// font path on the simple sfnt codepath. Returns null on failure / when networking is disabled.</summary>
        public static string? FetchStylesheet(string url)
        {
            if (!AllowNetwork || string.IsNullOrWhiteSpace(url)) return null;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                using (var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", "pdfmaker/0.1 (font-downloader)");
                    var resp = EnsureHttp().SendAsync(req).GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode) return null;
                    var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
            }
            catch (Exception ex) { System.Console.Error.WriteLine("[NET] stylesheet fetch failed " + url + ": " + ex.Message); return null; }
        }

        /// <summary>If <paramref name="src"/> resolves to SVG (data:image/svg+xml, .svg file, or any bytes that
        /// contain <c>&lt;svg</c>), parse it into an SVG DOM node so an <c>&lt;img&gt;</c> can be drawn as VECTORS by the
        /// same path as an inline <c>&lt;svg&gt;</c>. Works for base64/URL-encoded data URIs, file, url and relative
        /// paths (the byte resolution is shared with raster images). Returns null for non‑SVG / on any failure.</summary>
        public static Dom.Node? LoadSvgNode(string? src)
        {
            if (string.IsNullOrWhiteSpace(src)) return null;
            try
            {
                var s = src!.Trim();
                // Quick reject unless it's plausibly SVG (svg+xml data URI, a .svg URL/path, or an inline <svg …>).
                bool maybe = s.IndexOf("svg", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!maybe) return null;
                byte[]? bytes = ResolveBytes(s);
                if (bytes == null || bytes.Length == 0) return null;
                string text = System.Text.Encoding.UTF8.GetString(bytes);
                if (text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) < 0) return null;
                return Dom.Document.ParseSvgFragment(text);
            }
            catch { return null; }
        }

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

        /// <summary>Lazily create the shared HttpClient with a browser-ish default UA (some CDNs incl. Google Fonts
        /// 403 or content-negotiate on the User-Agent). Per-request UA overrides via HttpRequestMessage still win.</summary>
        private static System.Net.Http.HttpClient EnsureHttp()
        {
            if (_http == null)
            {
                _http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) HtmlPdfNative/1.0");
            }
            return _http;
        }

        /// <summary>Download a URL to bytes (cached; 10s timeout; graceful null on any failure).</summary>
        private static byte[]? FetchUrl(string url)
        {
            lock (_urlCache) if (_urlCache.TryGetValue(url, out var cached)) return cached;
            byte[]? result = null;
            try
            {
                result = EnsureHttp().GetByteArrayAsync(url).GetAwaiter().GetResult();
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
