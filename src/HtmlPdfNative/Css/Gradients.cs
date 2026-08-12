using System;
using System.Collections.Generic;
using System.Globalization;
using HtmlPdfNative.Render;

namespace HtmlPdfNative.Css
{
    public struct GradientStop { public Color Color; public float Pos; public float? PosPx; } // Pos 0..1; PosPx = absolute length (pt) resolved to a fraction of the gradient extent at render time

    /// <summary>A parsed CSS gradient: linear (gradient-line angle) or radial (shape/extent/center) + stops.</summary>
    public sealed class LinearGradient
    {
        public float AngleDeg = 180f;               // linear: default "to bottom"
        public List<GradientStop> Stops = new List<GradientStop>();
        // Radial extras (Radial=false → linear).
        public bool Radial;
        public bool Circle;                         // else ellipse
        public float CxFrac = 0.5f, CyFrac = 0.5f;  // center as a fraction of the box
        public string Extent = "farthest-corner";   // closest-side|closest-corner|farthest-side|farthest-corner
        // Conic extras (Conic=true → conic-gradient, rasterized to an image).
        public bool Conic;
        public float FromAngleDeg;                  // start angle (clockwise from top)
        // repeating-linear/radial-gradient: the stop pattern tiles across the gradient line (period = last stop px).
        public bool Repeating;
    }

    /// <summary>Parses CSS gradient functions. Subset of the gradient handling in <c>src/css.rs</c>.</summary>
    public static class Gradients
    {
        /// <summary>Extract a balanced <c>name( ... )</c> substring from a value, or null.</summary>
        public static string? ExtractFunction(string value, string name)
        {
            int start = value.IndexOf(name + "(", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            int open = start + name.Length;
            int depth = 0;
            for (int i = open; i < value.Length; i++)
            {
                if (value[i] == '(') depth++;
                else if (value[i] == ')') { depth--; if (depth == 0) return value.Substring(start, i - start + 1); }
            }
            return null;
        }

        /// <summary>Parse any CSS gradient (linear or radial) found in a value.</summary>
        public static LinearGradient? Parse(string value)
        {
            // `repeating-*-gradient(...)` shares the parser of its non-repeating form (the "linear-gradient("/
            // "radial-gradient(" substring is found inside the repeating name); flag it so rendering tiles the stops.
            bool repeating = value.IndexOf("repeating-linear-gradient", StringComparison.OrdinalIgnoreCase) >= 0
                          || value.IndexOf("repeating-radial-gradient", StringComparison.OrdinalIgnoreCase) >= 0;
            LinearGradient? g;
            if (value.IndexOf("conic-gradient", StringComparison.OrdinalIgnoreCase) >= 0) g = ParseConic(value);
            else if (value.IndexOf("radial-gradient", StringComparison.OrdinalIgnoreCase) >= 0) g = ParseRadial(value);
            else g = ParseLinear(value);
            if (g != null && repeating) g.Repeating = true;
            return g;
        }

        /// <summary>Parse conic-gradient([from &lt;angle&gt;] [at &lt;pos&gt;], &lt;color&gt; [&lt;angle|%&gt;], …).</summary>
        public static LinearGradient? ParseConic(string value)
        {
            var fn = ExtractFunction(value, "conic-gradient");
            if (fn == null) return null;
            int lp = fn.IndexOf('(');
            string inner = fn.Substring(lp + 1, fn.Length - lp - 2);
            var parts = SplitTopLevel(inner, ',');
            if (parts.Count == 0) return null;

            var g = new LinearGradient { Conic = true };
            int idx = 0;
            string first = parts[0].Trim().ToLowerInvariant();
            if (first.StartsWith("from", StringComparison.Ordinal) || first.StartsWith("at ", StringComparison.Ordinal))
            {
                int from = first.IndexOf("from ", StringComparison.Ordinal);
                if (from >= 0)
                {
                    var after = first.Substring(from + 5).Trim();
                    var tok = after.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    g.FromAngleDeg = ParseAngleDeg(tok) ?? 0f;
                }
                ParseRadialPrefix(first, g); // reuse the `at <pos>` center parsing
                idx = 1;
            }
            ParseStops(parts, idx, g);
            return g.Stops.Count > 0 ? g : null;
        }

        /// <summary>Parse an angle token to degrees: <c>Ndeg</c>, <c>Nturn</c>, <c>Nrad</c>, <c>Ngrad</c>.</summary>
        private static float? ParseAngleDeg(string t)
        {
            t = t.Trim().ToLowerInvariant();
            if (t.EndsWith("deg", StringComparison.Ordinal) && float.TryParse(t.Substring(0, t.Length - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            if (t.EndsWith("turn", StringComparison.Ordinal) && float.TryParse(t.Substring(0, t.Length - 4), NumberStyles.Float, CultureInfo.InvariantCulture, out var tn)) return tn * 360f;
            if (t.EndsWith("grad", StringComparison.Ordinal) && float.TryParse(t.Substring(0, t.Length - 4), NumberStyles.Float, CultureInfo.InvariantCulture, out var gr)) return gr * 0.9f;
            if (t.EndsWith("rad", StringComparison.Ordinal) && float.TryParse(t.Substring(0, t.Length - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out var rd)) return rd * 180f / (float)Math.PI;
            if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw)) return raw;
            return null;
        }

        public static LinearGradient? ParseLinear(string value)
        {
            var fn = ExtractFunction(value, "linear-gradient");
            if (fn == null) return null;
            int lp = fn.IndexOf('(');
            string inner = fn.Substring(lp + 1, fn.Length - lp - 2); // strip name( ... )
            var parts = SplitTopLevel(inner, ',');
            if (parts.Count == 0) return null;

            var g = new LinearGradient();
            int idx = 0;
            string first = parts[0].Trim();
            if (IsDirection(first, out float angle)) { g.AngleDeg = angle; idx = 1; }
            ParseStops(parts, idx, g);
            return g.Stops.Count > 0 ? g : null;
        }

        public static LinearGradient? ParseRadial(string value)
        {
            var fn = ExtractFunction(value, "radial-gradient");
            if (fn == null) return null;
            int lp = fn.IndexOf('(');
            string inner = fn.Substring(lp + 1, fn.Length - lp - 2);
            var parts = SplitTopLevel(inner, ',');
            if (parts.Count == 0) return null;

            var g = new LinearGradient { Radial = true };
            int idx = 0;
            string first = parts[0].Trim();
            if (IsRadialPrefix(first)) { ParseRadialPrefix(first, g); idx = 1; }
            ParseStops(parts, idx, g);
            return g.Stops.Count > 0 ? g : null;
        }

        /// <summary>Parse the comma-split color-stop parts (from <paramref name="startIdx"/>) into <paramref name="g"/>.</summary>
        private static void ParseStops(List<string> parts, int startIdx, LinearGradient g)
        {
            var raw = new List<(Color c, float? pos, float? px)>();
            for (int i = startIdx; i < parts.Count; i++)
            {
                var stop = parts[i].Trim();
                if (stop.Length == 0) continue;
                // A stop may carry up to TWO trailing positions (`color <p1> <p2>` = a hard band). A position is a
                // trailing %/angle (→ fraction) or a length like `24px` (→ resolved to a fraction of the gradient
                // extent at render time — needed for hard-stop cutouts like `transparent 0 24px, #fff7ed 25px`).
                var posns = new List<(float? frac, float? px)>();
                while (posns.Count < 2)
                {
                    int sp = stop.LastIndexOf(' ');
                    if (sp <= 0) break;
                    string tail = stop.Substring(sp + 1).Trim();
                    float? pp = ParseStopPos(tail);
                    if (pp != null) { posns.Insert(0, (pp.Value, null)); stop = stop.Substring(0, sp).Trim(); }
                    else if (IsLengthToken(tail)) { posns.Insert(0, (null, LengthTokenPt(tail))); stop = stop.Substring(0, sp).Trim(); }
                    else break;
                }
                var col = Values.ParseColor(stop);
                if (col == null) continue;
                if (posns.Count == 0) raw.Add((col.Value, null, null));
                else if (posns.Count == 1) raw.Add((col.Value, posns[0].frac, posns[0].px));
                else { raw.Add((col.Value, posns[0].frac, posns[0].px)); raw.Add((col.Value, posns[1].frac, posns[1].px)); }
                continue;
            }
            if (raw.Count == 0) return;
            if (raw.Count == 1) raw.Add(raw[0]);

            // Fill in missing FRACTIONAL positions: endpoints at 0 and 1, interior evenly spaced. Stops positioned by
            // an absolute length keep PosPx and get a provisional fraction (the renderer overrides it via PosPx).
            int n = raw.Count;
            var positions = new float?[n];
            for (int i = 0; i < n; i++) positions[i] = raw[i].pos;   // px-positioned stops stay null here
            if (positions[0] == null) positions[0] = raw[0].px.HasValue ? 0f : 0f;
            if (positions[n - 1] == null && !raw[n - 1].px.HasValue) positions[n - 1] = 1f;
            for (int i = 0; i < n; i++)
                if (positions[i] == null)
                {
                    int prev = i - 1; while (prev >= 0 && positions[prev] == null) prev--;
                    int next = i + 1; while (next < n && positions[next] == null) next++;
                    float p0 = prev >= 0 ? positions[prev]!.Value : 0f, p1 = next < n ? positions[next]!.Value : 1f;
                    positions[i] = p0 + (p1 - p0) * (i - (prev >= 0 ? prev : 0)) / Math.Max(1, (next < n ? next : n - 1) - (prev >= 0 ? prev : 0));
                }
            float last = 0f;
            for (int i = 0; i < n; i++)
            {
                float p = Math.Max(last, Math.Min(1f, positions[i] ?? (float)i / (n - 1)));
                last = p;
                g.Stops.Add(new GradientStop { Color = raw[i].c, Pos = p, PosPx = raw[i].px });
            }
        }

        /// <summary>Convert a length token (`24px`, `18pt`, unitless `0`) to points; null for units needing context.</summary>
        private static float? LengthTokenPt(string t)
        {
            int e = 0; while (e < t.Length && (char.IsDigit(t[e]) || t[e] == '.' || t[e] == '-' || t[e] == '+')) e++;
            if (e == 0) return null;
            if (!float.TryParse(t.Substring(0, e), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            string unit = t.Substring(e);
            switch (unit)
            {
                case "": case "px": return v * Lib.PxToPt;
                case "pt": return v;
                case "mm": return v * 72f / 25.4f;          // 1mm = 72/25.4 pt
                case "cm": return v * 720f / 25.4f;         // 1cm = 10mm
                case "in": return v * 72f;                  // 1in = 72pt
                case "pc": return v * 12f;                  // 1pc = 12pt
                default: return null;   // em/rem/vw/vh need context → fall back to stripping (no PosPx)
            }
        }

        /// <summary>A gradient-stop position token → fraction 0..1: <c>N%</c>, or a conic angle (deg/turn/…).</summary>
        private static bool IsLengthToken(string t)
        {
            if (t.Length == 0) return false;
            int e = 0; while (e < t.Length && (char.IsDigit(t[e]) || t[e] == '.' || t[e] == '-' || t[e] == '+')) e++;
            if (e == 0) return false;
            string unit = t.Substring(e);
            return unit == "" || unit == "px" || unit == "pt" || unit == "mm" || unit == "cm" || unit == "in" || unit == "pc"
                || unit == "em" || unit == "rem" || unit == "vw" || unit == "vh";
        }

        private static float? ParseStopPos(string tail)
        {
            if (tail.EndsWith("%", StringComparison.Ordinal) && float.TryParse(tail.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return pct / 100f;
            if ((tail.EndsWith("deg", StringComparison.Ordinal) || tail.EndsWith("turn", StringComparison.Ordinal) || tail.EndsWith("rad", StringComparison.Ordinal) || tail.EndsWith("grad", StringComparison.Ordinal)) && ParseAngleDeg(tail) is float ad) return ad / 360f;
            return null;
        }

        private static bool IsRadialPrefix(string t)
        {
            var v = t.ToLowerInvariant();
            return v.Contains("circle") || v.Contains("ellipse") || v.Contains("closest") ||
                   v.Contains("farthest") || v.StartsWith("at ", StringComparison.Ordinal) || v.Contains(" at ");
        }

        private static void ParseRadialPrefix(string t, LinearGradient g)
        {
            var v = t.ToLowerInvariant();
            g.Circle = v.Contains("circle");
            if (v.Contains("closest-side")) g.Extent = "closest-side";
            else if (v.Contains("closest-corner")) g.Extent = "closest-corner";
            else if (v.Contains("farthest-side")) g.Extent = "farthest-side";
            else if (v.Contains("farthest-corner")) g.Extent = "farthest-corner";

            int at = v.IndexOf("at ", StringComparison.Ordinal);
            if (at >= 0)
            {
                var pos = v.Substring(at + 3).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                bool xSet = false, ySet = false;
                foreach (var tok in pos)
                {
                    switch (tok)
                    {
                        case "left": g.CxFrac = 0f; xSet = true; break;
                        case "right": g.CxFrac = 1f; xSet = true; break;
                        case "top": g.CyFrac = 0f; ySet = true; break;
                        case "bottom": g.CyFrac = 1f; ySet = true; break;
                        case "center": if (!xSet) { g.CxFrac = 0.5f; xSet = true; } else { g.CyFrac = 0.5f; ySet = true; } break;
                        default:
                            if (tok.EndsWith("%", StringComparison.Ordinal) && float.TryParse(tok.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                            { if (!xSet) { g.CxFrac = pct / 100f; xSet = true; } else { g.CyFrac = pct / 100f; ySet = true; } }
                            // A length position (px/pt/…): only 0 maps cleanly to a fraction (0). Non-zero lengths need the
                            // box size, which isn't known here, so leave them at the default centre.
                            else { int e = 0; while (e < tok.Length && (char.IsDigit(tok[e]) || tok[e] == '.' || tok[e] == '-')) e++;
                                   if (e > 0 && float.TryParse(tok.Substring(0, e), NumberStyles.Float, CultureInfo.InvariantCulture, out var len) && len == 0f)
                                   { if (!xSet) { g.CxFrac = 0f; xSet = true; } else { g.CyFrac = 0f; ySet = true; } } }
                            break;
                    }
                }
            }
        }

        private static bool IsDirection(string t, out float angle)
        {
            angle = 180f;
            var v = t.ToLowerInvariant();
            if (v.EndsWith("deg", StringComparison.Ordinal) && float.TryParse(v.Substring(0, v.Length - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { angle = d; return true; }
            if (v.StartsWith("to ", StringComparison.Ordinal))
            {
                bool top = v.Contains("top"), bottom = v.Contains("bottom"), left = v.Contains("left"), right = v.Contains("right");
                if (top && right) angle = 45f; else if (bottom && right) angle = 135f;
                else if (bottom && left) angle = 225f; else if (top && left) angle = 315f;
                else if (top) angle = 0f; else if (bottom) angle = 180f; else if (right) angle = 90f; else if (left) angle = 270f;
                return true;
            }
            return false;
        }

        public static List<string> SplitTopLevel(string s, char sep)
        {
            var list = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == sep && depth == 0) { list.Add(s.Substring(start, i - start)); start = i + 1; }
            }
            list.Add(s.Substring(start));
            return list;
        }
    }
}
