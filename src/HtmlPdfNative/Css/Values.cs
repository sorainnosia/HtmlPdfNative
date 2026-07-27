using System;
using System.Collections.Generic;
using System.Globalization;
using HtmlPdfNative.Render;

namespace HtmlPdfNative.Css
{
    /// <summary>Helpers to interpret CSS value strings (lengths in pt, colors). Subset of <c>src/css.rs</c>.</summary>
    public static class Values
    {
        /// <summary>Parse a CSS length to points. px-&gt;pt uses 0.75; em/rem/% relative to references. Null if unparseable.</summary>
        public static float? LengthPt(string? value, float emPt = 12f, float percentBasePt = 0f)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value!.Trim().ToLowerInvariant();
            if (v == "0") return 0f;
            if (v == "auto" || v == "normal" || v == "none") return null;

            // Math functions: calc() / min() / max() / clamp().
            if (v.StartsWith("calc(", StringComparison.Ordinal) || v.StartsWith("min(", StringComparison.Ordinal) ||
                v.StartsWith("max(", StringComparison.Ordinal) || v.StartsWith("clamp(", StringComparison.Ordinal))
            {
                int pos = 0;
                return ParseSum(v, ref pos, emPt, percentBasePt);
            }

            (string unit, float scale)[] units =
            {
                ("px", Lib.PxToPt), ("pt", 1f), ("rem", emPt), ("em", emPt),
                ("in", 72f), ("cm", 72f / 2.54f), ("mm", 72f / 25.4f), ("pc", 12f),
            };
            foreach (var (unit, scale) in units)
            {
                if (v.EndsWith(unit, StringComparison.Ordinal))
                {
                    var num = v.Substring(0, v.Length - unit.Length).Trim();
                    if (float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                        return f * scale;
                }
            }
            if (v.EndsWith("%", StringComparison.Ordinal))
            {
                var num = v.Substring(0, v.Length - 1).Trim();
                if (float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    return f / 100f * percentBasePt;
            }
            // bare number -> treat as px
            if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
                return raw * Lib.PxToPt;
            return null;
        }

        // ---- calc()/min()/max()/clamp() recursive-descent evaluator (values resolved to pt) --------
        private static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static float? ParseSum(string s, ref int i, float em, float pct)
        {
            var a = ParseProduct(s, ref i, em, pct); if (a == null) return null;
            while (true)
            {
                SkipWs(s, ref i);
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                {
                    char op = s[i]; i++;
                    var b = ParseProduct(s, ref i, em, pct); if (b == null) return null;
                    a = op == '+' ? a + b : a - b;
                }
                else break;
            }
            return a;
        }

        private static float? ParseProduct(string s, ref int i, float em, float pct)
        {
            var a = ParseFactor(s, ref i, em, pct); if (a == null) return null;
            while (true)
            {
                SkipWs(s, ref i);
                if (i < s.Length && (s[i] == '*' || s[i] == '/'))
                {
                    char op = s[i]; i++;
                    var b = ParseFactor(s, ref i, em, pct); if (b == null) return null;
                    a = op == '*' ? a * b : (b == 0 ? a : a / b);
                }
                else break;
            }
            return a;
        }

        private static float? ParseFactor(string s, ref int i, float em, float pct)
        {
            SkipWs(s, ref i); if (i >= s.Length) return null;
            if (s[i] == '(') { i++; var vv = ParseSum(s, ref i, em, pct); SkipWs(s, ref i); if (i < s.Length && s[i] == ')') i++; return vv; }
            if (s[i] == '+') { i++; return ParseFactor(s, ref i, em, pct); }
            if (s[i] == '-') { i++; var vv = ParseFactor(s, ref i, em, pct); return vv == null ? (float?)null : -vv; }
            if (char.IsLetter(s[i]))
            {
                int st = i; while (i < s.Length && char.IsLetter(s[i])) i++;
                string name = s.Substring(st, i - st);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '(')
                {
                    i++;
                    var args = new List<float>();
                    while (true)
                    {
                        var arg = ParseSum(s, ref i, em, pct);
                        if (arg != null) args.Add(arg.Value);
                        SkipWs(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        break;
                    }
                    SkipWs(s, ref i); if (i < s.Length && s[i] == ')') i++;
                    if (args.Count == 0) return null;
                    switch (name)
                    {
                        case "min": { float m = args[0]; foreach (var x in args) if (x < m) m = x; return m; }
                        case "max": { float m = args[0]; foreach (var x in args) if (x > m) m = x; return m; }
                        case "clamp": return args.Count >= 3 ? Math.Max(args[0], Math.Min(args[1], args[2])) : args[args.Count - 1];
                        case "calc": return args[0];
                        default: return null;
                    }
                }
                return null;
            }
            return ParseValueToken(s, ref i, em, pct);
        }

        private static float? ParseValueToken(string s, ref int i, float em, float pct)
        {
            int st = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (i == st) return null;
            string num = s.Substring(st, i - st);
            int us = i;
            while (i < s.Length && (char.IsLetter(s[i]) || s[i] == '%')) i++;
            string unit = s.Substring(us, i - us);
            if (unit.Length == 0)
                return float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : (float?)null; // unitless scalar
            return LengthPt(num + unit, em, pct); // resolve the unit to pt
        }

        /// <summary>Parse a CSS color (#rgb, #rrggbb, rgb(), rgba(), and common names). Null if unparseable.</summary>
        public static Color? ParseColor(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value!.Trim().ToLowerInvariant();

            switch (v)
            {
                case "transparent": return new Color(0, 0, 0, 0);
                case "black": return new Color(0, 0, 0);
                case "white": return new Color(255, 255, 255);
                case "red": return new Color(255, 0, 0);
                case "green": return new Color(0, 128, 0);
                case "blue": return new Color(0, 0, 255);
                case "gray": case "grey": return new Color(128, 128, 128);
                case "silver": return new Color(192, 192, 192);
                case "navy": return new Color(0, 0, 128);
                case "orange": return new Color(255, 165, 0);
                case "purple": return new Color(128, 0, 128);
                case "yellow": return new Color(255, 255, 0);
                case "maroon": return new Color(128, 0, 0);
                case "teal": return new Color(0, 128, 128);
                case "lightgray": case "lightgrey": return new Color(211, 211, 211);
                case "darkgray": case "darkgrey": return new Color(169, 169, 169);
                case "lime": return new Color(0, 255, 0);
                case "aqua": case "cyan": return new Color(0, 255, 255);
                case "magenta": case "fuchsia": return new Color(255, 0, 255);
                case "olive": return new Color(128, 128, 0);
                case "pink": return new Color(255, 192, 203);
                case "brown": return new Color(165, 42, 42);
                case "gold": return new Color(255, 215, 0);
                case "indigo": return new Color(75, 0, 130);
                case "violet": return new Color(238, 130, 238);
                case "crimson": return new Color(220, 20, 60);
                case "coral": return new Color(255, 127, 80);
                case "salmon": return new Color(250, 128, 114);
                case "khaki": return new Color(240, 230, 140);
                case "orchid": return new Color(218, 112, 214);
                case "tan": return new Color(210, 180, 140);
                case "beige": return new Color(245, 245, 220);
                case "ivory": return new Color(255, 255, 240);
                case "lightblue": return new Color(173, 216, 230);
                case "lightgreen": return new Color(144, 238, 144);
                case "darkred": return new Color(139, 0, 0);
                case "darkgreen": return new Color(0, 100, 0);
                case "darkblue": return new Color(0, 0, 139);
                case "steelblue": return new Color(70, 130, 180);
                case "tomato": return new Color(255, 99, 71);
                case "turquoise": return new Color(64, 224, 208);
                case "skyblue": return new Color(135, 206, 235);
                case "royalblue": return new Color(65, 105, 225);
                case "slategray": case "slategrey": return new Color(112, 128, 144);
                case "chocolate": return new Color(210, 105, 30);
                case "hotpink": return new Color(255, 105, 180);
                case "dodgerblue": return new Color(30, 144, 255);
                case "seagreen": return new Color(46, 139, 87);
                case "forestgreen": return new Color(34, 139, 34);
                case "goldenrod": return new Color(218, 165, 32);
                case "firebrick": return new Color(178, 34, 34);
            }

            // color-mix(in <space>, <c1> [p1%], <c2> [p2%]) — sRGB weighted interpolation of two colours.
            if (v.StartsWith("color-mix", StringComparison.Ordinal))
            {
                int lp0 = value.IndexOf('('), rp0 = value.LastIndexOf(')');
                if (lp0 >= 0 && rp0 > lp0)
                {
                    var args = SplitTop(value.Substring(lp0 + 1, rp0 - lp0 - 1));
                    if (args.Count >= 3)
                    {
                        var (ca, pa) = ColorAndPct(args[1]);
                        var (cb, pb) = ColorAndPct(args[2]);
                        if (ca.HasValue && cb.HasValue)
                        {
                            float wa = pa ?? (pb.HasValue ? 100f - pb.Value : 50f);
                            float wb = pb ?? (100f - wa);
                            float sum = wa + wb; if (sum <= 0f) sum = 1f;
                            wa /= sum; wb /= sum;
                            byte Mix(byte x, byte y) => (byte)Math.Round(x * wa + y * wb);
                            return new Color(Mix(ca.Value.R, cb.Value.R), Mix(ca.Value.G, cb.Value.G), Mix(ca.Value.B, cb.Value.B),
                                (byte)Math.Round(ca.Value.A * wa + cb.Value.A * wb));
                        }
                    }
                }
                return null;
            }

            if (v.StartsWith("#", StringComparison.Ordinal))
            {
                string h = v.Substring(1);
                if (h.Length == 3)
                    return new Color(Hx(h[0], h[0]), Hx(h[1], h[1]), Hx(h[2], h[2]));
                if (h.Length == 6)
                    return new Color(Hx(h[0], h[1]), Hx(h[2], h[3]), Hx(h[4], h[5]));
            }

            if (v.StartsWith("rgb", StringComparison.Ordinal))
            {
                int lp = v.IndexOf('('), rp = v.IndexOf(')');
                if (lp >= 0 && rp > lp)
                {
                    var parts = v.Substring(lp + 1, rp - lp - 1).Split(',');
                    if (parts.Length >= 3)
                    {
                        byte r = ByteOf(parts[0]), g = ByteOf(parts[1]), b = ByteOf(parts[2]);
                        byte a = 255;
                        if (parts.Length >= 4 && float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var af))
                            a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(af * 255)));
                        return new Color(r, g, b, a);
                    }
                }
            }

            // Modern colour spaces: hsl()/hwb() (both syntaxes) and the perceptual oklch()/oklab()/lab().
            if (v.StartsWith("hsl", StringComparison.Ordinal)) { var a = ColorArgs(v, out var al); return a.Length >= 3 ? HslToRgb(a[0], Frac(a[1]), Frac(a[2]), al) : (Color?)null; }
            if (v.StartsWith("hwb", StringComparison.Ordinal)) { var a = ColorArgs(v, out var al); return a.Length >= 3 ? HwbToRgb(a[0], Frac(a[1]), Frac(a[2]), al) : (Color?)null; }
            if (v.StartsWith("oklch", StringComparison.Ordinal)) { var a = ColorArgs(v, out var al); if (a.Length < 3) return null; float L = a[0] > 1.5f ? a[0] / 100f : a[0]; double hr = a[2] * Math.PI / 180.0; return OklabToRgb(L, (float)(a[1] * Math.Cos(hr)), (float)(a[1] * Math.Sin(hr)), al); }
            if (v.StartsWith("oklab", StringComparison.Ordinal)) { var a = ColorArgs(v, out var al); if (a.Length < 3) return null; float L = a[0] > 1.5f ? a[0] / 100f : a[0]; return OklabToRgb(L, a[1], a[2], al); }
            if (v.StartsWith("lab", StringComparison.Ordinal)) { var a = ColorArgs(v, out var al); return a.Length >= 3 ? LabToRgb(a[0], a[1], a[2], al) : (Color?)null; }
            return null;
        }

        private static float Frac(float pct) => pct / 100f;

        /// <summary>Numeric args of a colour function (comma OR space separated; `/ alpha` supported). `%` is kept as the
        /// raw number (55% → 55). Sets <paramref name="alpha"/> (0-255).</summary>
        private static float[] ColorArgs(string v, out byte alpha)
        {
            alpha = 255;
            int lp = v.IndexOf('('), rp = v.LastIndexOf(')');
            if (lp < 0 || rp <= lp) return Array.Empty<float>();
            string inside = v.Substring(lp + 1, rp - lp - 1);
            int slash = inside.IndexOf('/');
            if (slash >= 0)
            {
                string at = inside.Substring(slash + 1).Trim();
                float av = at.EndsWith("%", StringComparison.Ordinal) && float.TryParse(at.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var ap) ? ap / 100f
                         : float.TryParse(at, NumberStyles.Float, CultureInfo.InvariantCulture, out var af) ? af : 1f;
                alpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(av * 255)));
                inside = inside.Substring(0, slash);
            }
            var toks = inside.Replace(",", " ").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new System.Collections.Generic.List<float>(toks.Length);
            foreach (var t in toks)
            {
                string s = t.Trim().TrimEnd('%');
                if (s.EndsWith("deg", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 3);
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) list.Add(f);
            }
            // trailing comma-alpha (hsla(...,0.5))
            if (list.Count >= 4 && slash < 0) { alpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round((list[3] <= 1f ? list[3] : list[3] / 100f) * 255))); }
            return list.ToArray();
        }

        private static Color HslToRgb(float h, float s, float l, byte a)
        {
            h = ((h % 360f) + 360f) % 360f / 360f;
            float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            float H(float t) { if (t < 0) t += 1; if (t > 1) t -= 1; if (t < 1f / 6) return p + (q - p) * 6 * t; if (t < 0.5f) return q; if (t < 2f / 3) return p + (q - p) * (2f / 3 - t) * 6; return p; }
            byte B(float x) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(x * 255)));
            return new Color(B(H(h + 1f / 3)), B(H(h)), B(H(h - 1f / 3)), a);
        }

        private static Color HwbToRgb(float h, float w, float bl, byte a)
        {
            if (w + bl >= 1f) { byte g = (byte)Math.Round(w / (w + bl) * 255); return new Color(g, g, g, a); }
            var baseC = HslToRgb(h, 1f, 0.5f, 255);
            byte Mix(byte c) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round((c / 255f * (1 - w - bl) + w) * 255)));
            return new Color(Mix(baseC.R), Mix(baseC.G), Mix(baseC.B), a);
        }

        private static byte LinToSrgb(double c) { c = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055; return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(c * 255))); }

        private static Color OklabToRgb(float L, float a, float b, byte alpha)
        {
            double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
            double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
            double s_ = L - 0.0894841775 * a - 1.2914855480 * b;
            double l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
            double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
            double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
            double bb = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
            return new Color(LinToSrgb(r), LinToSrgb(g), LinToSrgb(bb), alpha);
        }

        private static Color LabToRgb(float L, float A, float B, byte alpha)
        {
            double fy = (L + 16) / 116, fx = fy + A / 500, fz = fy - B / 200;
            double G(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
            double xn = 0.95047, yn = 1.0, zn = 1.08883;
            double x = xn * G(fx), y = yn * G(fy), z = zn * G(fz);
            double r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
            double g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
            double b = 0.0557 * x - 0.2040 * y + 1.0570 * z;
            return new Color(LinToSrgb(r), LinToSrgb(g), LinToSrgb(b), alpha);
        }

        private static byte Hx(char a, char b)
        {
            int hi = Convert.ToInt32(a.ToString(), 16);
            int lo = Convert.ToInt32(b.ToString(), 16);
            return (byte)(hi * 16 + lo);
        }

        /// <summary>Split on top-level commas (respecting nested parentheses) — for color-mix()/gradient argument lists.</summary>
        private static System.Collections.Generic.List<string> SplitTop(string s)
        {
            var outp = new System.Collections.Generic.List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0) { outp.Add(s.Substring(start, i - start).Trim()); start = i + 1; }
            }
            outp.Add(s.Substring(start).Trim());
            return outp;
        }

        /// <summary>Parse a color-mix component: a colour plus an optional trailing percentage (e.g. "#0a0 90%").</summary>
        private static (Color? col, float? pct) ColorAndPct(string s)
        {
            s = s.Trim();
            float? pct = null;
            int sp = s.LastIndexOf(' ');
            if (sp > 0 && s.EndsWith("%", StringComparison.Ordinal) && s.IndexOf('(') < sp)
            {
                if (float.TryParse(s.Substring(sp + 1).TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p))
                { pct = p; s = s.Substring(0, sp).Trim(); }
            }
            else if (s.EndsWith("%", StringComparison.Ordinal) && s.IndexOf('(') < 0)
            {
                // "green 90%" with no space handling above shouldn't happen; leave as-is
            }
            return (ParseColor(s), pct);
        }

        private static byte ByteOf(string s)
        {
            s = s.Trim();
            if (s.EndsWith("%", StringComparison.Ordinal) &&
                float.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(pct / 100f * 255)));
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                return (byte)Math.Max(0, Math.Min(255, i));
            return 0;
        }
    }
}
