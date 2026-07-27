using System;
using System.Collections.Generic;
#if HTMLPDF_SKIA
using SkiaSharp;
#endif

namespace HtmlPdfNative.Render
{
    /// <summary>
    /// Software rasterizer (SkiaSharp-backed) that renders a slice of the display list to an RGBA bitmap and applies a
    /// Gaussian blur — the backend for CSS <c>filter: blur()</c> and <c>backdrop-filter: blur()</c>, which cannot be
    /// expressed in PDF vectors. Gated on the <c>HTMLPDF_SKIA</c> compile symbol (set when
    /// <c>HtmlPdfUseExternalPackages=true</c>); when unavailable <see cref="Available"/> is false and the caller
    /// falls back to painting the subtree unblurred.
    /// </summary>
    public static class Rasterizer
    {
        public static bool Available =>
#if HTMLPDF_SKIA
            true;
#else
            false;
#endif

        private const float Scale = 2.0f;   // raster pixels per PDF point (≈2× DPI for acceptable blur quality)

        /// <summary>Render <paramref name="cmds"/> (page-coord DrawCommands, top-origin pt) into the rectangle
        /// [bx,by,bw,bh] padded for the blur, apply a Gaussian blur of <paramref name="blurPt"/>, and return it as a
        /// placed image: (image, x, y, w, h) in pt. Returns null when Skia is unavailable or on any failure.</summary>
        public static (Images.DecodedImage img, float x, float y, float w, float h)? RasterizeAndBlur(
            List<DrawCommand> cmds, float bx, float by, float bw, float bh, float blurPt)
        {
#if HTMLPDF_SKIA
            try
            {
                float pad = Math.Max(1f, 3f * blurPt);              // blur bleeds ~3σ beyond the box
                float ox = bx - pad, oy = by - pad, ow = bw + 2 * pad, oh = bh + 2 * pad;
                int pw = Math.Max(1, (int)Math.Ceiling(ow * Scale));
                int ph = Math.Max(1, (int)Math.Ceiling(oh * Scale));
                if ((long)pw * ph > 24_000_000) return null;         // guard against pathological sizes

                var info = new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using var sharp = new SKBitmap(info);
                using (var canvas = new SKCanvas(sharp))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.Scale(Scale, Scale);
                    canvas.Translate(-ox, -oy);                      // now draw in page-pt coords, top-origin
                    foreach (var c in cmds) Draw(canvas, c);
                }

                using var blurred = new SKBitmap(info);
                using (var canvas2 = new SKCanvas(blurred))
                {
                    canvas2.Clear(SKColors.Transparent);
                    float sigma = blurPt * Scale;
                    using var paint = new SKPaint { IsAntialias = true };
                    if (sigma > 0.05f) paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma);
                    canvas2.DrawBitmap(sharp, 0, 0, paint);
                }

                // Extract straight RGBA -> DecodedImage (RGB + separate alpha).
                var rgba = blurred.Bytes;                            // Rgba8888, unpremultiplied
                var rgb = new byte[pw * ph * 3];
                var alpha = new byte[pw * ph];
                bool anyAlpha = false;
                for (int i = 0, j = 0, k = 0; i < rgba.Length; i += 4, j += 3, k++)
                {
                    rgb[j] = rgba[i]; rgb[j + 1] = rgba[i + 1]; rgb[j + 2] = rgba[i + 2];
                    alpha[k] = rgba[i + 3];
                    if (rgba[i + 3] != 255) anyAlpha = true;
                }
                var img = new Images.DecodedImage { Width = pw, Height = ph, Rgb = rgb, Alpha = anyAlpha ? alpha : null };
                return (img, ox, oy, ow, oh);
            }
            catch { return null; }
#else
            return null;
#endif
        }

        /// <summary>Rasterize <paramref name="backdrop"/> commands, blur, and return the portion within the box
        /// [bx,by,bw,bh] exactly (cropped — for backdrop-filter, the frosted region is clipped to the element box).
        /// Returns (image at bx,by,bw,bh) or null when unavailable.</summary>
        public static Images.DecodedImage? RasterizeBackdrop(
            List<DrawCommand> backdrop, float bx, float by, float bw, float bh, float blurPt)
        {
#if HTMLPDF_SKIA
            try
            {
                int pw = Math.Max(1, (int)Math.Ceiling(bw * Scale));
                int ph = Math.Max(1, (int)Math.Ceiling(bh * Scale));
                if ((long)pw * ph > 24_000_000) return null;
                var info = new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                float sigma = blurPt * Scale;
                using var bmp = new SKBitmap(info);
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.White);           // opaque base: the backdrop composited over page white
                    canvas.Scale(Scale, Scale);
                    canvas.Translate(-bx, -by);
                    using var layer = new SKPaint();
                    if (sigma > 0.05f) layer.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma);
                    canvas.SaveLayer(layer);                // blur the whole backdrop as one layer
                    foreach (var c in backdrop) Draw(canvas, c);
                    canvas.Restore();
                }
                var rgba = bmp.Bytes;
                var rgb = new byte[pw * ph * 3];
                for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3) { rgb[j] = rgba[i]; rgb[j + 1] = rgba[i + 1]; rgb[j + 2] = rgba[i + 2]; }
                return new Images.DecodedImage { Width = pw, Height = ph, Rgb = rgb, Alpha = null };
            }
            catch { return null; }
#else
            return null;
#endif
        }

        /// <summary>Rasterize <paramref name="cmds"/> flat over [bx,by,bw,bh], then projective-warp that bitmap onto
        /// the <paramref name="quad"/> (8 floats: TL,TR,BR,BL corners in page pt) — for real 3D transforms
        /// (rotateX/Y/perspective) that PDF's affine CTM cannot express. Returns (image, bboxX,Y,W,H) or null.</summary>
        public static (Images.DecodedImage img, float x, float y, float w, float h)? RasterizeWarp(
            List<DrawCommand> cmds, float bx, float by, float bw, float bh, float[] quad)
        {
#if HTMLPDF_SKIA
            try
            {
                int sw = Math.Max(1, (int)Math.Ceiling(bw * Scale)), sh = Math.Max(1, (int)Math.Ceiling(bh * Scale));
                if ((long)sw * sh > 24_000_000) return null;
                var sinfo = new SKImageInfo(sw, sh, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using var src = new SKBitmap(sinfo);
                using (var c = new SKCanvas(src)) { c.Clear(SKColors.Transparent); c.Scale(Scale, Scale); c.Translate(-bx, -by); foreach (var cc in cmds) Draw(c, cc); }

                float minx = quad[0], miny = quad[1], maxx = quad[0], maxy = quad[1];
                for (int k = 0; k < 4; k++) { minx = Math.Min(minx, quad[k * 2]); maxx = Math.Max(maxx, quad[k * 2]); miny = Math.Min(miny, quad[k * 2 + 1]); maxy = Math.Max(maxy, quad[k * 2 + 1]); }
                float ow = Math.Max(1f, maxx - minx), oh = Math.Max(1f, maxy - miny);
                int pw = Math.Max(1, (int)Math.Ceiling(ow * Scale)), ph = Math.Max(1, (int)Math.Ceiling(oh * Scale));
                if ((long)pw * ph > 24_000_000) return null;
                var dinfo = new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using var dst = new SKBitmap(dinfo);
                using (var c = new SKCanvas(dst))
                {
                    c.Clear(SKColors.Transparent);
                    c.Scale(Scale, Scale); c.Translate(-minx, -miny);
                    var H = Homography(new float[] { bx, by, bx + bw, by, bx + bw, by + bh, bx, by + bh }, quad);
                    c.Concat(ref H);
                    using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
                    c.DrawBitmap(src, new SKRect(bx, by, bx + bw, by + bh), paint);
                }
                var rgba = dst.Bytes;
                var rgb = new byte[pw * ph * 3]; var alpha = new byte[pw * ph]; bool anyA = false;
                for (int i = 0, j = 0, k = 0; i < rgba.Length; i += 4, j += 3, k++) { rgb[j] = rgba[i]; rgb[j + 1] = rgba[i + 1]; rgb[j + 2] = rgba[i + 2]; alpha[k] = rgba[i + 3]; if (rgba[i + 3] != 255) anyA = true; }
                return (new Images.DecodedImage { Width = pw, Height = ph, Rgb = rgb, Alpha = anyA ? alpha : null }, minx, miny, ow, oh);
            }
            catch { return null; }
#else
            return null;
#endif
        }

#if HTMLPDF_SKIA
        // Homography mapping 4 source points (rect corners) to 4 dest points (projected quad); TL,TR,BR,BL order.
        private static SKMatrix Homography(float[] s, float[] d)
        {
            var A = SquareToQuad(s); var B = SquareToQuad(d);
            var Ai = Invert3(A); var H = Mul3(B, Ai);
            return new SKMatrix { ScaleX = H[0], SkewX = H[1], TransX = H[2], SkewY = H[3], ScaleY = H[4], TransY = H[5], Persp0 = H[6], Persp1 = H[7], Persp2 = H[8] };
        }
        // 3x3 (row-major) mapping the unit square (0,0),(1,0),(1,1),(0,1) to the quad p0..p3.
        private static float[] SquareToQuad(float[] p)
        {
            float x0 = p[0], y0 = p[1], x1 = p[2], y1 = p[3], x2 = p[4], y2 = p[5], x3 = p[6], y3 = p[7];
            float dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
            float dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
            float g, h;
            if (Math.Abs(dx3) < 1e-6 && Math.Abs(dy3) < 1e-6)
                return new float[] { x1 - x0, x3 - x0, x0, y1 - y0, y3 - y0, y0, 0, 0, 1 };
            float den = dx1 * dy2 - dx2 * dy1; if (Math.Abs(den) < 1e-9) den = 1e-9f;
            g = (dx3 * dy2 - dx2 * dy3) / den; h = (dx1 * dy3 - dx3 * dy1) / den;
            return new float[] { x1 - x0 + g * x1, x3 - x0 + h * x3, x0, y1 - y0 + g * y1, y3 - y0 + h * y3, y0, g, h, 1 };
        }
        private static float[] Mul3(float[] a, float[] b)
        {
            var r = new float[9];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }
        private static float[] Invert3(float[] m)
        {
            float a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            float det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g); if (Math.Abs(det) < 1e-12) det = 1e-12f;
            float id = 1f / det;
            return new float[] { (e * i - f * h) * id, (c * h - b * i) * id, (b * f - c * e) * id, (f * g - d * i) * id, (a * i - c * g) * id, (c * d - a * f) * id, (d * h - e * g) * id, (b * g - a * h) * id, (a * e - b * d) * id };
        }

        private static void Draw(SKCanvas canvas, DrawCommand c)
        {
            switch (c)
            {
                case SolidRect r:
                    using (var p = Fill(r.Color)) canvas.DrawRect(r.X, r.Y, r.Width, r.Height, p);
                    break;
                case RoundRect rr:
                    var rect = new SKRect(rr.X, rr.Y, rr.X + rr.Width, rr.Y + rr.Height);
                    float rad = Math.Max(0f, Math.Max(rr.Rtl, Math.Max(rr.Rtr, Math.Max(rr.Rbr, rr.Rbl))));
                    if (rr.Fill.HasValue) using (var p = Fill(rr.Fill.Value)) canvas.DrawRoundRect(rect, rad, rad, p);
                    if (rr.Stroke.HasValue) using (var p = Stroke(rr.Stroke.Value, rr.StrokeW)) canvas.DrawRoundRect(rect, rad, rad, p);
                    break;
                case GradientFill g:
                    DrawGradient(canvas, g);
                    break;
                case ImageDraw im:
                    DrawImage(canvas, im);
                    break;
                case TextRun t when !string.IsNullOrEmpty(t.Text) && !t.Hidden:
                    DrawText(canvas, t);
                    break;
                case OpacityGroup og:
                    using (var p = new SKPaint { Color = new SKColor(0, 0, 0, (byte)Math.Round(og.Alpha * 255)) })
                    {
                        canvas.SaveLayer(p);
                        foreach (var s in og.Sub) Draw(canvas, s);
                        canvas.Restore();
                    }
                    break;
                case ClipGroup cg:
                    canvas.Save();
                    canvas.ClipRect(new SKRect(cg.X, cg.Y, cg.X + cg.Width, cg.Y + cg.Height));
                    foreach (var s in cg.Sub) Draw(canvas, s);
                    canvas.Restore();
                    break;
                case BlendGroup bg:
                    foreach (var s in bg.Sub) Draw(canvas, s);   // blend-in-blur approximated as normal compositing
                    break;
                case TransformGroup tg:
                    foreach (var s in tg.Sub) Draw(canvas, s);   // transforms inside a blurred subtree are rare; drawn untransformed
                    break;
            }
        }

        private static SKPaint Fill(Color c) => new SKPaint { Color = Col(c), IsAntialias = true, Style = SKPaintStyle.Fill };
        private static SKPaint Stroke(Color c, float w) => new SKPaint { Color = Col(c), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.1f, w) };
        private static SKColor Col(Color c) => new SKColor(c.R, c.G, c.B, c.A);

        private static void DrawGradient(SKCanvas canvas, GradientFill g)
        {
            var stops = g.Gradient.Stops;
            if (stops == null || stops.Count == 0) return;
            var colors = new SKColor[stops.Count];
            var pos = new float[stops.Count];
            for (int i = 0; i < stops.Count; i++)
            {
                var sc = stops[i].Color; colors[i] = new SKColor(sc.R, sc.G, sc.B, (byte)Math.Round(sc.A * g.Alpha));
                pos[i] = Math.Min(1f, Math.Max(0f, stops[i].Pos));
            }
            var rect = new SKRect(g.X, g.Y, g.X + g.Width, g.Y + g.Height);
            SKShader shader;
            if (g.Gradient.Radial)
            {
                float cx = g.X + g.Width * g.Gradient.CxFrac, cy = g.Y + g.Height * g.Gradient.CyFrac;
                float rr = Math.Max(g.Width, g.Height) * 0.6f;
                shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), rr, colors, pos, SKShaderTileMode.Clamp);
            }
            else
            {
                double a = (g.Gradient.AngleDeg - 90) * Math.PI / 180.0;   // CSS 0deg = up; Skia x-axis reference
                float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
                var c0 = new SKPoint(g.X + g.Width / 2 - dx * g.Width / 2, g.Y + g.Height / 2 - dy * g.Height / 2);
                var c1 = new SKPoint(g.X + g.Width / 2 + dx * g.Width / 2, g.Y + g.Height / 2 + dy * g.Height / 2);
                shader = SKShader.CreateLinearGradient(c0, c1, colors, pos, SKShaderTileMode.Clamp);
            }
            using (shader)
            using (var p = new SKPaint { Shader = shader, IsAntialias = true }) canvas.DrawRect(rect, p);
        }

        private static void DrawImage(SKCanvas canvas, ImageDraw im)
        {
            var di = im.Image; if (di == null || di.Width <= 0 || di.Height <= 0) return;
            var info = new SKImageInfo(di.Width, di.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var bytes = new byte[di.Width * di.Height * 4];
            for (int i = 0, j = 0, k = 0; k < di.Width * di.Height; k++, i += 3, j += 4)
            {
                bytes[j] = di.Rgb[i]; bytes[j + 1] = di.Rgb[i + 1]; bytes[j + 2] = di.Rgb[i + 2];
                bytes[j + 3] = di.Alpha != null ? di.Alpha[k] : (byte)255;
            }
            using var bmp = new SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
            canvas.DrawBitmap(bmp, new SKRect(im.X, im.Y, im.X + im.Width, im.Y + im.Height), paint);
        }

        private static void DrawText(SKCanvas canvas, TextRun t)
        {
            SKTypeface tf;
            if (t.Emb != null && t.Emb.Face?.Data != null)
                tf = SKTypeface.FromData(SKData.CreateCopy(t.Emb.Face.Data)) ?? SKTypeface.Default;
            else
                tf = SKTypeface.FromFamilyName(Family(t.Face), Weight(t.Face), SKFontStyleWidth.Normal, Slant(t.Face)) ?? SKTypeface.Default;
            using (tf)
            using (var p = new SKPaint { Color = Col(t.Color), IsAntialias = true, Typeface = tf, TextSize = t.FontSizePt })
            {
                if (t.LetterSpacing != 0f)
                {
                    float x = t.X;
                    foreach (var ch in t.Text)
                    {
                        string s = ch.ToString();
                        canvas.DrawText(s, x, t.BaselineY, p);
                        x += p.MeasureText(s) + t.LetterSpacing;
                    }
                }
                else canvas.DrawText(t.Text, t.X, t.BaselineY, p);
            }
        }

        private static string Family(FontFace f) =>
            f == FontFace.TimesRoman || f == FontFace.TimesBold || f == FontFace.TimesItalic || f == FontFace.TimesBoldItalic ? "Times New Roman"
            : f == FontFace.Courier || f == FontFace.CourierBold || f == FontFace.CourierOblique || f == FontFace.CourierBoldOblique ? "Courier New"
            : "Arial";
        private static SKFontStyleWeight Weight(FontFace f) =>
            (f == FontFace.HelveticaBold || f == FontFace.HelveticaBoldOblique || f == FontFace.TimesBold || f == FontFace.TimesBoldItalic || f == FontFace.CourierBold || f == FontFace.CourierBoldOblique)
            ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        private static SKFontStyleSlant Slant(FontFace f) =>
            (f == FontFace.HelveticaOblique || f == FontFace.HelveticaBoldOblique || f == FontFace.TimesItalic || f == FontFace.TimesBoldItalic || f == FontFace.CourierOblique || f == FontFace.CourierBoldOblique)
            ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
#endif
    }
}
