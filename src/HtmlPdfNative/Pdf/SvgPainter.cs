using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HtmlPdfNative.Css;
using HtmlPdfNative.Fonts;
using HtmlPdfNative.Render;

namespace HtmlPdfNative.Pdf
{
    /// <summary>
    /// Paints an inline &lt;svg&gt; subtree as native PDF vector operators (crisp, resolution-independent).
    /// Supports the common shape set — rect (incl. rounded), circle, ellipse, line, polyline, polygon,
    /// and &lt;path&gt; (M/L/H/V/C/S/Q/T/Z + relatives; arcs approximated) — plus &lt;g&gt; grouping,
    /// per-element transforms (translate/scale/rotate/matrix), presentation attributes
    /// (fill, stroke, stroke-width, via attribute or inline <c>style</c>) and &lt;text&gt;/&lt;tspan&gt;
    /// (base-14 fonts, font-size/family/weight/style, text-anchor). Coordinates are mapped from
    /// the SVG viewBox (y-down) onto the placed box via a single base CTM that also flips to PDF y-up.
    /// A .NET analogue of the vector path taken by the Rust engine's resvg → PDF bridge.
    /// </summary>
    internal static class SvgPainter
    {
        private struct Ctx
        {
            public bool HasFill; public Color Fill;
            public bool HasStroke; public Color Stroke;
            public float StrokeW;
            public float FontSize; public bool Bold; public bool Italic; public string? Family; public int Anchor; // 0 start,1 middle,2 end
            public int Cap; // stroke-linecap: 0 butt, 1 round, 2 square
            public string? Dash; public float DashOffset; // stroke-dasharray / -dashoffset (used for progress-ring arcs)
        }

        // Cascade context for the current SVG (set per Paint): per-element computed styles (class/tag rules the painter
        // can't resolve itself) and the custom-property map (to expand var() used inside inline presentation attributes).
        [ThreadStatic] private static Dictionary<Dom.Node, Style.ComputedStyle>? _styles;
        [ThreadStatic] private static Dictionary<string, string>? _vars;

        [ThreadStatic] private static Dictionary<EmbeddedFont, string>? _embRes;

        /// <summary>Emit the PDF content-stream fragment for one placed SVG (y measured from page top, pt).</summary>
        public static string Paint(SvgDraw d, float pageHeight, Dictionary<FontFace, string> faceRes, Dictionary<EmbeddedFont, string>? embRes = null)
        {
            _embRes = embRes;
            var svg = d.Svg;
            float vbX = 0, vbY = 0, vbW = 0, vbH = 0;
            if (svg.Attributes.TryGetValue("viewBox", out var vb))
            {
                var p = vb.Replace(",", " ").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 4) { Num(p[0], out vbX); Num(p[1], out vbY); Num(p[2], out vbW); Num(p[3], out vbH); }
            }
            if (vbW <= 0 || vbH <= 0) { vbX = 0; vbY = 0; vbW = d.Width / Lib.PxToPt; vbH = d.Height / Lib.PxToPt; }
            if (vbW <= 0) vbW = 1; if (vbH <= 0) vbH = 1;

            float sx = d.Width / vbW, sy = d.Height / vbH;
            // CTM: (vx,vy) in viewBox space (y-down) -> PDF page (y-up). a=sx, d=-sy.
            float a = sx, dd = -sy;
            float e = d.X - vbX * sx;
            float f = (pageHeight - d.Y) + vbY * sy;

            var sb = new StringBuilder();
            sb.Append("q\n");
            sb.Append(F(a)).Append(" 0 0 ").Append(F(dd)).Append(' ').Append(F(e)).Append(' ').Append(F(f)).Append(" cm\n");
            // Clip to the viewBox rectangle.
            sb.Append(F(vbX)).Append(' ').Append(F(vbY)).Append(' ').Append(F(vbW)).Append(' ').Append(F(vbH)).Append(" re W n\n");

            _grads = CollectGradients(svg);       // resolve url(#id) fills to a representative colour
            _styles = d.Styles; _vars = d.Vars;   // cascade context for class/tag-styled children + var() in attrs
            var root = new Ctx { HasFill = true, Fill = Color.Black, HasStroke = false, Stroke = Color.Black, StrokeW = 1f, FontSize = 16f, Anchor = 0 };
            root = Resolve(svg, root);            // root <svg> may carry presentation attrs
            foreach (var child in svg.Children) PaintNode(sb, child, root, faceRes);

            sb.Append("Q\n");
            return sb.ToString();
        }

        private static void PaintNode(StringBuilder sb, Dom.Node n, Ctx parent, Dictionary<FontFace, string> faceRes)
        {
            if (n.IsText || n.Tag == null) return;
            switch (n.Tag)
            {
                case "defs": case "title": case "desc": case "style": case "metadata":
                case "clippath": case "mask": case "symbol": case "lineargradient": case "radialgradient":
                    return;
            }

            var ctx = Resolve(n, parent);
            bool pushed = false;
            if (n.Attributes.TryGetValue("transform", out var tf))
            {
                var m = ParseTransform(tf);
                if (m != null)
                {
                    sb.Append("q ").Append(F(m[0])).Append(' ').Append(F(m[1])).Append(' ').Append(F(m[2])).Append(' ')
                      .Append(F(m[3])).Append(' ').Append(F(m[4])).Append(' ').Append(F(m[5])).Append(" cm\n");
                    pushed = true;
                }
            }

            switch (n.Tag)
            {
                case "g": case "a": case "svg":
                    foreach (var c in n.Children) PaintNode(sb, c, ctx, faceRes);
                    break;
                case "rect": PaintRect(sb, n, ctx); break;
                case "circle": PaintCircle(sb, n, ctx); break;
                case "ellipse": PaintEllipse(sb, n, ctx); break;
                case "line": PaintLine(sb, n, ctx); break;
                case "polyline": PaintPoly(sb, n, ctx, false); break;
                case "polygon": PaintPoly(sb, n, ctx, true); break;
                case "path": PaintPath(sb, n, ctx); break;
                case "text": PaintText(sb, n, ctx, faceRes); break;
                default:
                    foreach (var c in n.Children) PaintNode(sb, c, ctx, faceRes); // unknown container: descend
                    break;
            }

            if (pushed) sb.Append("Q\n");
        }

        // ---- presentation attributes --------------------------------------------------------------

        private static Ctx Resolve(Dom.Node n, Ctx parent)
        {
            var ctx = parent;
            string? fill = Prop(n, "fill"), stroke = Prop(n, "stroke"), sw = Prop(n, "stroke-width");
            if (fill != null)
            {
                if (fill.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) ctx.HasFill = false;
                else { var c = ColorOf(fill); if (c.HasValue) { ctx.HasFill = true; ctx.Fill = c.Value; } }
            }
            if (stroke != null)
            {
                if (stroke.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) ctx.HasStroke = false;
                else { var c = ColorOf(stroke); if (c.HasValue) { ctx.HasStroke = true; ctx.Stroke = c.Value; } }
            }
            if (sw != null && Num(sw.Replace("px", ""), out var w)) ctx.StrokeW = Math.Max(0f, w);
            string? cap = Prop(n, "stroke-linecap");
            if (cap != null) { var s = cap.Trim().ToLowerInvariant(); ctx.Cap = s == "round" ? 1 : s == "square" ? 2 : 0; }
            string? dash = Prop(n, "stroke-dasharray");
            if (dash != null) { var s = dash.Trim().ToLowerInvariant(); ctx.Dash = (s == "none" || s.Length == 0) ? null : dash.Trim(); }
            string? doff = Prop(n, "stroke-dashoffset");
            if (doff != null && Num(doff.Replace("px", ""), out var offv)) ctx.DashOffset = offv;
            ReadFont(n, ref ctx.Family, ref ctx.Bold, ref ctx.Italic);
            string? fsz = Prop(n, "font-size");
            if (fsz != null && Num(fsz.Replace("px", "").Replace("pt", ""), out var fv) && fv > 0) ctx.FontSize = fv;
            string? ta = Prop(n, "text-anchor");
            if (ta != null) { var s = ta.Trim().ToLowerInvariant(); ctx.Anchor = s == "middle" ? 1 : s == "end" ? 2 : 0; }
            return ctx;
        }

        /// <summary>Update inherited font family/weight/style from a node's presentation attrs / inline style.</summary>
        private static void ReadFont(Dom.Node n, ref string? family, ref bool bold, ref bool italic)
        {
            string? ff = Prop(n, "font-family");
            if (ff != null && ff.Trim().Length > 0) family = ff;
            string? fw = Prop(n, "font-weight");
            if (fw != null)
            {
                var s = fw.Trim().ToLowerInvariant();
                if (s == "bold" || s == "bolder" || s == "600" || s == "700" || s == "800" || s == "900") bold = true;
                else if (s == "normal" || s == "lighter" || s == "100" || s == "200" || s == "300" || s == "400" || s == "500") bold = false;
            }
            string? fst = Prop(n, "font-style");
            if (fst != null)
            {
                var s = fst.Trim().ToLowerInvariant();
                if (s == "italic" || s == "oblique") italic = true;
                else if (s == "normal") italic = false;
            }
        }

        // ---- <text> / <tspan> ---------------------------------------------------------------------

        private static void PaintText(StringBuilder sb, Dom.Node n, Ctx ctx, Dictionary<FontFace, string> faceRes)
        {
            // Cursor-based walk: tspans set x/y (absolute) or dx/dy (relative), and text advances the x cursor —
            // supporting inline continuation (multiple runs on a line) and dx/dy kerning.
            float cx = A(n, "x") + A(n, "dx"), cy = A(n, "y") + A(n, "dy");
            bool hasChildTspan = false;
            foreach (var c in n.Children) if (!c.IsText && c.Tag == "tspan") { hasChildTspan = true; break; }
            if (!hasChildTspan) { EmitText(sb, CollectText(n), cx, cy, ctx, faceRes); return; } // simple <text>
            WalkText(sb, n, ctx, faceRes, ref cx, ref cy);
        }

        private static void WalkText(StringBuilder sb, Dom.Node n, Ctx ctx, Dictionary<FontFace, string> faceRes, ref float cx, ref float cy)
        {
            foreach (var c in n.Children)
            {
                if (c.IsText) { cx += EmitText(sb, Collapse(c.Text ?? ""), cx, cy, ctx, faceRes); continue; }
                if (c.Tag != "tspan") continue;
                var tctx = Resolve(c, ctx);
                if (c.Attributes.ContainsKey("x")) cx = A(c, "x");
                if (c.Attributes.ContainsKey("dx")) cx += A(c, "dx");
                if (c.Attributes.ContainsKey("y")) cy = A(c, "y");
                if (c.Attributes.ContainsKey("dy")) cy += A(c, "dy");
                WalkText(sb, c, tctx, faceRes, ref cx, ref cy);   // nested text/tspans continue the cursor
            }
        }

        /// <summary>Emit a text run at (x,y); returns the advance width so callers can continue the x cursor.</summary>
        private static float EmitText(StringBuilder sb, string text, float x, float y, Ctx ctx, Dictionary<FontFace, string> faceRes)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            var col = ctx.HasFill ? ctx.Fill : Color.Black;
            var (r, g, b) = col.Rgb01();

            // Non-WinAnsi text (Arabic, CJK, emoji, symbols) can't use the base-14 fonts — route it to the same
            // embedded Type0 faces the main text engine uses, so it renders instead of showing "???".
            var emb = _embRes != null && FontManager.NeedsEmbedded(text) ? FontManager.ResolveScript(ctx.Bold, text) : null;
            if (emb != null && _embRes!.TryGetValue(emb, out var eres))
            {
                string shaped = IsRtl(text) ? ReorderRtl(Layout.ArabicShaper.Shape(text)) : text;
                emb.MarkUsed(shaped);
                float advE = emb.MeasurePt(shaped, ctx.FontSize);
                float axE = ctx.Anchor == 1 ? x - advE / 2f : ctx.Anchor == 2 ? x - advE : x;
                sb.Append("BT ").Append(F(r)).Append(' ').Append(F(g)).Append(' ').Append(F(b)).Append(" rg /")
                  .Append(eres).Append(' ').Append(F(ctx.FontSize)).Append(" Tf 1 0 0 -1 ")
                  .Append(F(axE)).Append(' ').Append(F(y)).Append(" Tm <").Append(HexGids(emb, shaped)).Append("> Tj ET\n");
                return advE;
            }

            var face = Afm.Resolve(ctx.Family, ctx.Bold, ctx.Italic);
            if (!faceRes.TryGetValue(face, out var res)) return 0f; // font not registered for this page
            float adv = Afm.MeasurePt(face, text, ctx.FontSize);
            float ax = ctx.Anchor == 1 ? x - adv / 2f : ctx.Anchor == 2 ? x - adv : x;
            // We're inside the base CTM (viewBox y-down + flip to PDF y-up, d<0). Text space y is up, so a
            // plain text matrix would render glyphs upside-down; flip locally with d=-1 in Tm to stay upright.
            sb.Append("BT ").Append(F(r)).Append(' ').Append(F(g)).Append(' ').Append(F(b)).Append(" rg /")
              .Append(res).Append(' ').Append(F(ctx.FontSize)).Append(" Tf 1 0 0 -1 ")
              .Append(F(ax)).Append(' ').Append(F(y)).Append(" Tm (")
              .Append(PdfWriter.PdfDocument.EscapeLiteral(text)).Append(") Tj ET\n");
            return adv;
        }

        private static string HexGids(EmbeddedFont emb, string text)
        {
            var sb = new StringBuilder();
            foreach (var cp in EmbeddedFont.Codepoints(text))
                sb.Append(emb.Face.Cid(emb.Face.GlyphId(cp)).ToString("X4", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static bool IsRtl(string s) { foreach (var ch in s) { int c = ch; if ((c >= 0x0590 && c <= 0x06FF) || (c >= 0xFB1D && c <= 0xFEFF)) return true; } return false; }
        private static bool IsRtlChar(char ch) { int c = ch; return (c >= 0x0590 && c <= 0x06FF) || (c >= 0xFB1D && c <= 0xFEFF); }
        private static string ReorderRtl(string s)
        {
            var arr = s.ToCharArray(); Array.Reverse(arr);
            int i = 0, n = arr.Length;
            while (i < n)
            {
                if (IsRtlChar(arr[i]) || char.IsWhiteSpace(arr[i])) { i++; continue; }
                int j = i; while (j < n && !IsRtlChar(arr[j]) && !char.IsWhiteSpace(arr[j])) j++;
                Array.Reverse(arr, i, j - i); i = j;
            }
            return new string(arr);
        }

        /// <summary>Embedded faces used by &lt;text&gt;/&lt;tspan&gt; in an SVG subtree (so PdfGenerator registers them).</summary>
        public static IEnumerable<EmbeddedFont> UsedEmbeddedFonts(Dom.Node svg)
        {
            var acc = new HashSet<EmbeddedFont>();
            void Walk(Dom.Node n, bool bold)
            {
                if (n.Tag == null) { return; }
                switch (n.Tag) { case "defs": case "clippath": case "mask": case "symbol": return; }
                string? fw = Prop(n, "font-weight");
                if (fw != null) { var s = fw.Trim().ToLowerInvariant(); if (s == "bold" || s == "bolder" || (int.TryParse(s, out var wv) && wv >= 600)) bold = true; else if (s == "normal" || s == "lighter") bold = false; }
                if ((n.Tag == "text" || n.Tag == "tspan"))
                {
                    var txt = CollectText(n);
                    if (!string.IsNullOrEmpty(txt) && FontManager.NeedsEmbedded(txt))
                    {
                        var e = FontManager.ResolveScript(bold, txt);
                        // Mark the SHAPED codepoints used NOW — the embedded font is SUBSET by UsedCodepoints before
                        // paint, so unmarked glyphs would be dropped from the subset and render as tofu.
                        if (e != null) { string shaped = IsRtl(txt) ? ReorderRtl(Layout.ArabicShaper.Shape(txt)) : txt; e.MarkUsed(shaped); acc.Add(e); }
                    }
                }
                foreach (var c in n.Children) Walk(c, bold);
            }
            Walk(svg, false);
            return acc;
        }

        /// <summary>Concatenated text of a node's descendants (collapsing runs of whitespace to single spaces).</summary>
        private static string CollectText(Dom.Node n)
        {
            var sb = new StringBuilder();
            Collect(n, sb);
            return Collapse(sb.ToString());
        }

        /// <summary>Collapse runs of whitespace to single spaces (SVG default xml:space).</summary>
        private static string Collapse(string s)
        {
            var outp = new StringBuilder();
            bool sp = false;
            foreach (char ch in s)
            {
                if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r') { sp = true; }
                else { if (sp && outp.Length > 0) outp.Append(' '); sp = false; outp.Append(ch); }
            }
            return outp.ToString();
        }

        private static void Collect(Dom.Node n, StringBuilder sb)
        {
            if (n.IsText) { sb.Append(n.Text); return; }
            foreach (var c in n.Children) Collect(c, sb);
        }

        /// <summary>Base-14 faces used by &lt;text&gt;/&lt;tspan&gt; in an SVG subtree (so they get registered).</summary>
        public static IEnumerable<FontFace> UsedFaces(Dom.Node svg)
        {
            var acc = new HashSet<FontFace>();
            CollectFaces(svg, null, false, false, acc);
            return acc;
        }

        private static void CollectFaces(Dom.Node n, string? family, bool bold, bool italic, HashSet<FontFace> acc)
        {
            if (n.IsText || n.Tag == null) return;
            switch (n.Tag) { case "defs": case "clippath": case "mask": case "symbol": return; }
            ReadFont(n, ref family, ref bold, ref italic);
            if ((n.Tag == "text" || n.Tag == "tspan") && HasText(n)) acc.Add(Afm.Resolve(family, bold, italic));
            foreach (var c in n.Children) CollectFaces(c, family, bold, italic, acc);
        }

        private static bool HasText(Dom.Node n)
        {
            foreach (var c in n.Children) { if (c.IsText && !string.IsNullOrWhiteSpace(c.Text)) return true; if (!c.IsText && HasText(c)) return true; }
            return false;
        }

        /// <summary>Resolve a presentation value with SVG/CSS precedence: inline <c>style</c> &gt; stylesheet rule
        /// (matched by the HTML cascade) &gt; presentation attribute. Any <c>var(--x)</c> is expanded against the
        /// custom-property map in scope. Presentation attributes have the LOWEST priority — so a rule like
        /// <c>.track{fill:none}</c> correctly overrides the default (opaque black) fill.</summary>
        private static string? Prop(Dom.Node n, string name)
        {
            string? v = InlineStyle(n, name) ?? CssCascade(n, name);
            if (v == null && n.Attributes.TryGetValue(name, out var av)) v = av;
            return v == null ? null : ResolveVars(v);
        }

        private static string? InlineStyle(Dom.Node n, string name)
        {
            if (n.Attributes.TryGetValue("style", out var st) && !string.IsNullOrEmpty(st))
                foreach (var decl in st.Split(';'))
                {
                    int c = decl.IndexOf(':');
                    if (c > 0 && decl.Substring(0, c).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                        return decl.Substring(c + 1).Trim();
                }
            return null;
        }

        /// <summary>The value of a CSS presentation property matched onto this element by the document stylesheet.</summary>
        private static string? CssCascade(Dom.Node n, string name)
        {
            if (_styles == null || !_styles.TryGetValue(n, out var cs)) return null;
            switch (name)
            {
                case "fill": return cs.SvgFill;
                case "stroke": return cs.SvgStroke;
                case "stroke-width": return cs.SvgStrokeWidth;
                case "stroke-linecap": return cs.SvgStrokeLinecap;
                case "stroke-dasharray": return cs.SvgStrokeDasharray;
                case "fill-opacity": return cs.SvgFillOpacity;
                case "stroke-opacity": return cs.SvgStrokeOpacity;
                default: return null;
            }
        }

        /// <summary>Expand <c>var(--name[, fallback])</c> against the custom-property map (used for var() inside inline
        /// SVG attributes, which the HTML cascade doesn't touch). Nested/absent vars fall back to the fallback text.</summary>
        private static string ResolveVars(string value)
        {
            if (value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) < 0) return value;
            for (int guard = 0; guard < 8 && value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0; guard++)
            {
                int at = value.IndexOf("var(", StringComparison.OrdinalIgnoreCase);
                int depth = 0, close = -1;
                for (int k = at + 3; k < value.Length; k++) { if (value[k] == '(') depth++; else if (value[k] == ')') { depth--; if (depth == 0) { close = k; break; } } }
                if (close < 0) break;
                string args = value.Substring(at + 4, close - at - 4);
                int comma = -1, dp = 0;
                for (int k = 0; k < args.Length; k++) { if (args[k] == '(') dp++; else if (args[k] == ')') dp--; else if (args[k] == ',' && dp == 0) { comma = k; break; } }
                string varName = (comma < 0 ? args : args.Substring(0, comma)).Trim();
                string fallback = comma < 0 ? "" : args.Substring(comma + 1).Trim();
                string repl = (_vars != null && _vars.TryGetValue(varName, out var vv)) ? vv.Trim() : fallback;
                value = value.Substring(0, at) + repl + value.Substring(close + 1);
            }
            return value;
        }

        [ThreadStatic] private static Dictionary<string, Color>? _grads;   // gradient id → representative (mean-stop) colour

        private static Color? ColorOf(string v)
        {
            v = v.Trim();
            if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return Color.Black;
            if (v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                // Resolve a paint-server reference url(#id) to the gradient's mean stop colour (PDF-in-SVG can't do a
                // per-shape vector gradient here, so approximate with a representative flat fill — far better than grey).
                int h = v.IndexOf('#'), e = v.IndexOf(')');
                if (h >= 0 && e > h) { string id = v.Substring(h + 1, e - h - 1).Trim().Trim('\'', '"'); if (_grads != null && _grads.TryGetValue(id, out var gc)) return gc; }
                return new Color(160, 160, 160); // unknown paint server → neutral grey
            }
            return Values.ParseColor(v);
        }

        /// <summary>Collect &lt;linearGradient&gt;/&lt;radialGradient&gt; ids → mean stop colour for url(#id) fills.</summary>
        private static Dictionary<string, Color> CollectGradients(Dom.Node svg)
        {
            var map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            void Walk(Dom.Node n)
            {
                if ((n.Tag == "lineargradient" || n.Tag == "radialgradient") && n.Attributes.TryGetValue("id", out var id))
                {
                    long r = 0, g = 0, b = 0; int cnt = 0;
                    void Stops(Dom.Node grad) { foreach (var c in grad.Children) { if (c.Tag == "stop") { var sc = Prop(c, "stop-color"); if (sc != null) { var col = Values.ParseColor(sc.Trim()); if (col.HasValue) { r += col.Value.R; g += col.Value.G; b += col.Value.B; cnt++; } } } else Stops(c); } }
                    Stops(n);
                    if (cnt > 0) map[id] = new Color((byte)(r / cnt), (byte)(g / cnt), (byte)(b / cnt));
                }
                foreach (var c in n.Children) Walk(c);
            }
            Walk(svg);
            return map;
        }

        // ---- shapes -------------------------------------------------------------------------------

        private static void PaintRect(StringBuilder sb, Dom.Node n, Ctx ctx)
        {
            float x = A(n, "x"), y = A(n, "y"), w = A(n, "width"), h = A(n, "height");
            if (w <= 0 || h <= 0) return;
            float rx = A(n, "rx", float.NaN), ry = A(n, "ry", float.NaN);
            if (float.IsNaN(rx) && float.IsNaN(ry)) { rx = ry = 0; }
            else if (float.IsNaN(rx)) rx = ry;
            else if (float.IsNaN(ry)) ry = rx;
            rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);

            var p = new StringBuilder();
            if (rx <= 0 || ry <= 0)
            {
                p.Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(w)).Append(' ').Append(F(h)).Append(" re\n");
            }
            else
            {
                float k = 0.5522847498f;
                float ox = rx * k, oy = ry * k;
                float x0 = x, x1 = x + rx, x2 = x + w - rx, x3 = x + w;
                float y0 = y, y1 = y + ry, y2 = y + h - ry, y3 = y + h;
                M(p, x1, y0); L(p, x2, y0);
                C(p, x2 + ox, y0, x3, y1 - oy, x3, y1);
                L(p, x3, y2);
                C(p, x3, y2 + oy, x2 + ox, y3, x2, y3);
                L(p, x1, y3);
                C(p, x1 - ox, y3, x0, y2 + oy, x0, y2);
                L(p, x0, y1);
                C(p, x0, y1 - oy, x1 - ox, y0, x1, y0);
                p.Append("h\n");
            }
            Fill(sb, p.ToString(), ctx);
        }

        private static void PaintCircle(StringBuilder sb, Dom.Node n, Ctx ctx)
        {
            float cx = A(n, "cx"), cy = A(n, "cy"), r = A(n, "r");
            if (r <= 0) return;
            Fill(sb, EllipsePath(cx, cy, r, r), ctx);
        }

        private static void PaintEllipse(StringBuilder sb, Dom.Node n, Ctx ctx)
        {
            float cx = A(n, "cx"), cy = A(n, "cy"), rx = A(n, "rx"), ry = A(n, "ry");
            if (rx <= 0 || ry <= 0) return;
            Fill(sb, EllipsePath(cx, cy, rx, ry), ctx);
        }

        private static string EllipsePath(float cx, float cy, float rx, float ry)
        {
            float k = 0.5522847498f, ox = rx * k, oy = ry * k;
            var p = new StringBuilder();
            M(p, cx + rx, cy);
            C(p, cx + rx, cy + oy, cx + ox, cy + ry, cx, cy + ry);
            C(p, cx - ox, cy + ry, cx - rx, cy + oy, cx - rx, cy);
            C(p, cx - rx, cy - oy, cx - ox, cy - ry, cx, cy - ry);
            C(p, cx + ox, cy - ry, cx + rx, cy - oy, cx + rx, cy);
            p.Append("h\n");
            return p.ToString();
        }

        private static void PaintLine(StringBuilder sb, Dom.Node n, Ctx ctx)
        {
            var p = new StringBuilder();
            M(p, A(n, "x1"), A(n, "y1")); L(p, A(n, "x2"), A(n, "y2"));
            var c = ctx; c.HasFill = false;               // a line can't be filled
            if (!c.HasStroke) { c.HasStroke = true; c.Stroke = Color.Black; }
            Fill(sb, p.ToString(), c);
        }

        private static void PaintPoly(StringBuilder sb, Dom.Node n, Ctx ctx, bool close)
        {
            if (!n.Attributes.TryGetValue("points", out var pts) || string.IsNullOrWhiteSpace(pts)) return;
            var nums = new List<float>();
            foreach (var tok in pts.Replace(",", " ").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                if (Num(tok, out var v)) nums.Add(v);
            if (nums.Count < 4) return;
            var p = new StringBuilder();
            M(p, nums[0], nums[1]);
            for (int i = 2; i + 1 < nums.Count; i += 2) L(p, nums[i], nums[i + 1]);
            if (close) p.Append("h\n");
            var c = ctx;
            if (!close && !c.HasStroke) { c.HasStroke = true; c.Stroke = Color.Black; c.HasFill = false; }
            Fill(sb, p.ToString(), c);
        }

        // ---- <path d="..."> -----------------------------------------------------------------------

        private static void PaintPath(StringBuilder sb, Dom.Node n, Ctx ctx)
        {
            if (!n.Attributes.TryGetValue("d", out var d) || string.IsNullOrWhiteSpace(d)) return;
            string path = BuildPath(d);
            if (path.Length == 0) return;
            Fill(sb, path, ctx);
        }

        private static string BuildPath(string d)
        {
            var toks = Tokenize(d);
            var p = new StringBuilder();
            float cx = 0, cy = 0, startX = 0, startY = 0;      // current + subpath-start
            float pcx = 0, pcy = 0;                            // previous cubic 2nd control (for S)
            float pqx = 0, pqy = 0;                            // previous quad control (for T)
            char cmd = '\0', last = '\0';
            int i = 0;
            float Next() => (i < toks.Count && toks[i] is float) ? (float)toks[i++] : 0f;
            bool More() => i < toks.Count && toks[i] is float;

            while (i < toks.Count)
            {
                if (toks[i] is char ch) { cmd = ch; i++; }
                switch (cmd)
                {
                    case 'M': case 'm':
                    {
                        float x = Next(), y = Next();
                        if (cmd == 'm') { x += cx; y += cy; }
                        cx = x; cy = y; startX = x; startY = y;
                        M(p, x, y);
                        cmd = cmd == 'M' ? 'L' : 'l';           // subsequent pairs are implicit line-to
                        break;
                    }
                    case 'L': case 'l':
                    {
                        float x = Next(), y = Next();
                        if (cmd == 'l') { x += cx; y += cy; }
                        cx = x; cy = y; L(p, x, y);
                        break;
                    }
                    case 'H': case 'h':
                    {
                        float x = Next(); if (cmd == 'h') x += cx; cx = x; L(p, cx, cy);
                        break;
                    }
                    case 'V': case 'v':
                    {
                        float y = Next(); if (cmd == 'v') y += cy; cy = y; L(p, cx, cy);
                        break;
                    }
                    case 'C': case 'c':
                    {
                        float x1 = Next(), y1 = Next(), x2 = Next(), y2 = Next(), x = Next(), y = Next();
                        if (cmd == 'c') { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                        C(p, x1, y1, x2, y2, x, y); pcx = x2; pcy = y2; cx = x; cy = y;
                        break;
                    }
                    case 'S': case 's':
                    {
                        float x2 = Next(), y2 = Next(), x = Next(), y = Next();
                        if (cmd == 's') { x2 += cx; y2 += cy; x += cx; y += cy; }
                        float x1 = (last == 'C' || last == 'c' || last == 'S' || last == 's') ? 2 * cx - pcx : cx;
                        float y1 = (last == 'C' || last == 'c' || last == 'S' || last == 's') ? 2 * cy - pcy : cy;
                        C(p, x1, y1, x2, y2, x, y); pcx = x2; pcy = y2; cx = x; cy = y;
                        break;
                    }
                    case 'Q': case 'q':
                    {
                        float qx = Next(), qy = Next(), x = Next(), y = Next();
                        if (cmd == 'q') { qx += cx; qy += cy; x += cx; y += cy; }
                        Quad(p, cx, cy, qx, qy, x, y); pqx = qx; pqy = qy; cx = x; cy = y;
                        break;
                    }
                    case 'T': case 't':
                    {
                        float x = Next(), y = Next();
                        if (cmd == 't') { x += cx; y += cy; }
                        float qx = (last == 'Q' || last == 'q' || last == 'T' || last == 't') ? 2 * cx - pqx : cx;
                        float qy = (last == 'Q' || last == 'q' || last == 'T' || last == 't') ? 2 * cy - pqy : cy;
                        Quad(p, cx, cy, qx, qy, x, y); pqx = qx; pqy = qy; cx = x; cy = y;
                        break;
                    }
                    case 'A': case 'a':
                    {
                        Next(); Next(); Next(); Next(); Next();  // rx ry rot large-arc sweep (approximated)
                        float x = Next(), y = Next();
                        if (cmd == 'a') { x += cx; y += cy; }
                        cx = x; cy = y; L(p, x, y);              // arc approximated as a straight segment
                        break;
                    }
                    case 'Z': case 'z':
                        p.Append("h\n"); cx = startX; cy = startY;
                        break;
                    default:
                        if (More()) Next(); else i++;            // skip stray token
                        break;
                }
                last = cmd;
                if (cmd == 'Z' || cmd == 'z') cmd = '\0';
            }
            return p.ToString();
        }

        private static List<object> Tokenize(string d)
        {
            var toks = new List<object>();
            int i = 0, n = d.Length;
            while (i < n)
            {
                char c = d[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) { toks.Add(c); i++; }
                else if (c == ',' || c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else
                {
                    int start = i;
                    if (c == '+' || c == '-') i++;
                    bool dot = false, exp = false;
                    while (i < n)
                    {
                        char ch = d[i];
                        if (ch >= '0' && ch <= '9') i++;
                        else if (ch == '.' && !dot && !exp) { dot = true; i++; }
                        else if ((ch == 'e' || ch == 'E') && !exp) { exp = true; i++; if (i < n && (d[i] == '+' || d[i] == '-')) i++; }
                        else break;
                    }
                    if (i > start && Num(d.Substring(start, i - start), out var v)) toks.Add(v);
                    else i = start + 1; // avoid infinite loop on a stray char
                }
            }
            return toks;
        }

        // ---- transform ----------------------------------------------------------------------------

        private static float[]? ParseTransform(string tf)
        {
            float[] m = { 1, 0, 0, 1, 0, 0 };
            bool any = false;
            int i = 0, n = tf.Length;
            while (i < n)
            {
                while (i < n && (tf[i] == ' ' || tf[i] == ',' || tf[i] == '\t' || tf[i] == '\n' || tf[i] == '\r')) i++;
                int nameStart = i;
                while (i < n && tf[i] != '(') i++;
                if (i >= n) break;
                string name = tf.Substring(nameStart, i - nameStart).Trim().ToLowerInvariant();
                int close = tf.IndexOf(')', i);
                if (close < 0) break;
                var args = new List<float>();
                foreach (var t in tf.Substring(i + 1, close - i - 1).Replace(",", " ").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    if (Num(t, out var v)) args.Add(v);
                i = close + 1;

                float[]? op = null;
                switch (name)
                {
                    case "translate": op = new float[] { 1, 0, 0, 1, args.Count > 0 ? args[0] : 0, args.Count > 1 ? args[1] : 0 }; break;
                    case "scale": { float ax = args.Count > 0 ? args[0] : 1; float ay = args.Count > 1 ? args[1] : ax; op = new float[] { ax, 0, 0, ay, 0, 0 }; break; }
                    case "rotate":
                    {
                        double ang = (args.Count > 0 ? args[0] : 0) * Math.PI / 180.0;
                        float cs = (float)Math.Cos(ang), sn = (float)Math.Sin(ang);
                        if (args.Count >= 3)
                        {
                            float ox = args[1], oy = args[2];
                            op = Mul(new float[] { 1, 0, 0, 1, ox, oy }, Mul(new float[] { cs, sn, -sn, cs, 0, 0 }, new float[] { 1, 0, 0, 1, -ox, -oy }));
                        }
                        else op = new float[] { cs, sn, -sn, cs, 0, 0 };
                        break;
                    }
                    case "matrix": if (args.Count >= 6) op = new float[] { args[0], args[1], args[2], args[3], args[4], args[5] }; break;
                    case "skewx": { double t = (args.Count > 0 ? args[0] : 0) * Math.PI / 180.0; op = new float[] { 1, 0, (float)Math.Tan(t), 1, 0, 0 }; break; }
                    case "skewy": { double t = (args.Count > 0 ? args[0] : 0) * Math.PI / 180.0; op = new float[] { 1, (float)Math.Tan(t), 0, 1, 0, 0 }; break; }
                }
                if (op != null) { m = Mul(m, op); any = true; }
            }
            return any ? m : null;
        }

        // Compose two 2x3 affine matrices [a b c d e f] (PDF/SVG order): result = A then applies to B's output... m = A*B.
        private static float[] Mul(float[] A, float[] B) => new float[]
        {
            A[0] * B[0] + A[2] * B[1],
            A[1] * B[0] + A[3] * B[1],
            A[0] * B[2] + A[2] * B[3],
            A[1] * B[2] + A[3] * B[3],
            A[0] * B[4] + A[2] * B[5] + A[4],
            A[1] * B[4] + A[3] * B[5] + A[5],
        };

        // ---- emit helpers -------------------------------------------------------------------------

        private static void Fill(StringBuilder sb, string path, Ctx ctx)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (ctx.HasFill)
            {
                var (r, g, b) = ctx.Fill.Rgb01();
                sb.Append(F(r)).Append(' ').Append(F(g)).Append(' ').Append(F(b)).Append(" rg\n");
            }
            if (ctx.HasStroke)
            {
                var (r, g, b) = ctx.Stroke.Rgb01();
                sb.Append(F(r)).Append(' ').Append(F(g)).Append(' ').Append(F(b)).Append(" RG\n");
                sb.Append(F(ctx.StrokeW)).Append(" w\n");
                sb.Append(ctx.Cap).Append(" J\n");   // line cap: 0 butt / 1 round / 2 square
                // stroke-dasharray/-dashoffset → PDF dash pattern (drives progress-ring / gauge partial arcs)
                if (!string.IsNullOrEmpty(ctx.Dash))
                {
                    var dnums = new List<float>();
                    foreach (var t in ctx.Dash!.Replace(",", " ").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        if (Num(t.Replace("px", ""), out var dv) && dv >= 0) dnums.Add(dv);
                    if (dnums.Count > 0)
                    {
                        sb.Append('[');
                        for (int i = 0; i < dnums.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(F(dnums[i])); }
                        sb.Append("] ").Append(F(ctx.DashOffset)).Append(" d\n");
                    }
                }
                else sb.Append("[] 0 d\n");   // reset any inherited dash so solid strokes stay solid
            }
            sb.Append(path);
            sb.Append(ctx.HasFill && ctx.HasStroke ? "B\n" : ctx.HasFill ? "f\n" : ctx.HasStroke ? "S\n" : "n\n");
        }

        private static void M(StringBuilder p, float x, float y) => p.Append(F(x)).Append(' ').Append(F(y)).Append(" m\n");
        private static void L(StringBuilder p, float x, float y) => p.Append(F(x)).Append(' ').Append(F(y)).Append(" l\n");
        private static void C(StringBuilder p, float x1, float y1, float x2, float y2, float x, float y) =>
            p.Append(F(x1)).Append(' ').Append(F(y1)).Append(' ').Append(F(x2)).Append(' ').Append(F(y2)).Append(' ').Append(F(x)).Append(' ').Append(F(y)).Append(" c\n");

        private static void Quad(StringBuilder p, float cx, float cy, float qx, float qy, float x, float y)
        {
            // quadratic -> cubic
            float c1x = cx + 2f / 3f * (qx - cx), c1y = cy + 2f / 3f * (qy - cy);
            float c2x = x + 2f / 3f * (qx - x), c2y = y + 2f / 3f * (qy - y);
            C(p, c1x, c1y, c2x, c2y, x, y);
        }

        private static float A(Dom.Node n, string attr, float dflt = 0f)
            => n.Attributes.TryGetValue(attr, out var v) && Num(v.Replace("px", ""), out var f) ? f : dflt;

        private static bool Num(string s, out float v)
            => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
