using System;
using System.Collections.Generic;
using System.Text;
using HtmlPdfNative.Render;   // Color
#if HTMLPDF_SKIA
using SkiaSharp;
#endif

namespace HtmlPdfNative.MathTex
{
    /// <summary>A rendered LaTeX-math fragment: an RGBA bitmap plus its inline metrics (all in pt).</summary>
    public sealed class MathImage
    {
        public Images.DecodedImage Img = null!;
        /// <summary>Total image width in pt.</summary>
        public float WidthPt;
        /// <summary>Extent from the image top down to the math baseline, in pt.</summary>
        public float HeightPt;
        /// <summary>Extent from the math baseline to the image bottom, in pt (how far below the text baseline it hangs).</summary>
        public float DepthPt;
    }

    /// <summary>
    /// A small self-contained TeX-style math typesetter. Parses a useful subset of LaTeX math
    /// (fractions, roots, super/sub-scripts, Greek, big operators with limits, \left/\right delimiters,
    /// matrices, accents, spacing, functions) into a box tree and rasterises it via SkiaSharp — the .NET
    /// analogue of the Rust engine's <c>latex</c> feature (which shells out to mitex→Typst). Gated on the
    /// <c>HTMLPDF_SKIA</c> compile symbol; <see cref="Render"/> returns null when unavailable and the caller
    /// leaves the raw source in place.
    /// </summary>
    public static class MathRenderer
    {
        public static bool Available =>
#if HTMLPDF_SKIA
            true;
#else
            false;
#endif

        private static readonly Dictionary<string, MathImage?> Cache = new Dictionary<string, MathImage?>();
        private static readonly object Gate = new object();

        /// <summary>Render <paramref name="latex"/> (the content between the math delimiters) at the given font size
        /// and colour. <paramref name="display"/> selects display style (larger operators, stacked limits).</summary>
        public static MathImage? Render(string latex, float fontSizePt, Color color, bool display)
        {
#if HTMLPDF_SKIA
            if (string.IsNullOrWhiteSpace(latex)) return null;
            string key = fontSizePt.ToString("0.00") + "|" + color.R + "," + color.G + "," + color.B + "|" + (display ? "D" : "I") + "|" + latex;
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var c)) return c;
                MathImage? result;
                try { result = RenderImpl(latex, fontSizePt, color, display); }
                catch (Exception ex) { Console.Error.WriteLine("[MATH] render failed: " + ex.Message); result = null; }
                Cache[key] = result;
                return result;
            }
#else
            return null;
#endif
        }

#if HTMLPDF_SKIA
        // ---- font faces -------------------------------------------------------------------------------
        private static SKTypeface? _rm, _it, _bf, _bi, _math, _sans;
        private static SKTypeface Load(ref SKTypeface? slot, string asset)
        {
            if (slot != null) return slot;
            slot = SKTypeface.FromData(SKData.CreateCopy(Assets.ReadAllBytes(asset))) ?? SKTypeface.Default;
            return slot;
        }
        private static SKTypeface Face(bool italic, bool bold) =>
            bold ? (italic ? Load(ref _bi, "fonts/Gelasio-BoldItalic.ttf") : Load(ref _bf, "fonts/Gelasio-Bold.ttf"))
                 : (italic ? Load(ref _it, "fonts/Gelasio-Italic.ttf") : Load(ref _rm, "fonts/Gelasio-Regular.ttf"));
        private static SKTypeface MathFace => Load(ref _math, "fonts/NotoSansMath-Regular.ttf");
        private static SKTypeface SansFace => Load(ref _sans, "fonts/NotoSans-Regular.ttf");

        private static bool Has(SKTypeface tf, int cp) { try { return tf.GetGlyph(cp) != 0; } catch { return true; } }

        private static SKColor _col;

        // ---- box model --------------------------------------------------------------------------------
        // Coordinates: y grows downward, a box's baseline is local y=0; H is the extent above the baseline
        // (>=0), D the extent below (>=0). Paint(canvas, x, baseY) draws the box with its baseline at baseY
        // and its left edge at x.
        private abstract class Box
        {
            public float W, H, D;
            public abstract void Paint(SKCanvas c, float x, float baseY);
        }

        private enum Cls { Ord, Op, Bin, Rel, Open, Close, Punct, Inner }

        private static SKPaint TextPaint(SKTypeface tf, float size) =>
            new SKPaint { Typeface = tf, TextSize = size, IsAntialias = true, Color = _col, IsStroke = false, SubpixelText = true, LcdRenderText = false };

        private sealed class Glyph : Box
        {
            private readonly string _s; private readonly SKTypeface _tf; private readonly float _size;
            public float ItalicCorr;
            public Glyph(string s, SKTypeface tf, float size)
            {
                _s = s; _tf = tf; _size = size;
                using var p = TextPaint(tf, size);
                var r = new SKRect();
                float adv = p.MeasureText(s, ref r);
                W = adv; H = Math.Max(0f, -r.Top); D = Math.Max(0f, r.Bottom);
                ItalicCorr = Math.Max(0f, r.Right - adv);
                if (H <= 0f && D <= 0f) { H = size * 0.5f; } // spaces / zero-extent
            }
            public override void Paint(SKCanvas c, float x, float baseY)
            { using var p = TextPaint(_tf, _size); c.DrawText(_s, x, baseY, p); }
        }

        private sealed class Kern : Box { public Kern(float w) { W = w; } public override void Paint(SKCanvas c, float x, float b) { } }

        private sealed class Row : Box
        {
            private readonly List<(Box b, float dx)> _items = new List<(Box, float)>();
            public void Place(Box b, float dx) { _items.Add((b, dx)); }
            public void Finish(float w) { W = w; float h = 0, d = 0; foreach (var it in _items) { h = Math.Max(h, it.b.H); d = Math.Max(d, it.b.D); } H = h; D = d; }
            public override void Paint(SKCanvas c, float x, float baseY) { foreach (var it in _items) it.b.Paint(c, x + it.dx, baseY); }
        }

        private sealed class Frac : Box
        {
            private readonly Box _n, _d; private readonly float _size; private readonly bool _bar;
            private readonly float _axis, _gap, _rule, _nx, _dx;
            public Frac(Box n, Box d, float size, bool bar)
            {
                _n = n; _d = d; _size = size; _bar = bar;
                _axis = size * 0.26f; _gap = size * 0.14f; _rule = Math.Max(0.6f, size * 0.045f);
                W = Math.Max(n.W, d.W) + size * 0.24f;
                _nx = (W - n.W) / 2f; _dx = (W - d.W) / 2f;
                H = _axis + _gap + _rule / 2f + n.H + n.D;
                D = -_axis + _gap + _rule / 2f + d.H + d.D;
            }
            public override void Paint(SKCanvas c, float x, float baseY)
            {
                float barY = baseY - _axis;
                if (_bar) using (var p = new SKPaint { Color = _col, IsAntialias = true, Style = SKPaintStyle.Fill })
                    c.DrawRect(x + _size * 0.06f, barY - _rule / 2f, W - _size * 0.12f, _rule, p);
                _n.Paint(c, x + _nx, barY - _gap - _rule / 2f - _n.D);
                _d.Paint(c, x + _dx, barY + _gap + _rule / 2f + _d.H);
            }
        }

        private sealed class Script : Box
        {
            private readonly Box _nuc, _sup, _sub; private readonly float _supShift, _subShift, _sx;
            public Script(Box nuc, Box sup, Box sub, float size)
            {
                _nuc = nuc; _sup = sup; _sub = sub;
                float ic = nuc is Glyph g ? g.ItalicCorr : 0f;
                _sx = nuc.W;
                float sw = 0f;
                if (sup != null) { _supShift = Math.Max(size * 0.42f, nuc.H - size * 0.22f); sw = Math.Max(sw, sup.W + ic); }
                if (sub != null) { _subShift = Math.Max(size * 0.16f, nuc.D + size * 0.10f); sw = Math.Max(sw, sub.W); }
                W = nuc.W + sw + size * 0.02f;
                H = nuc.H; D = nuc.D;
                if (sup != null) H = Math.Max(H, _supShift + sup.H);
                if (sub != null) D = Math.Max(D, _subShift + sub.D);
            }
            public override void Paint(SKCanvas c, float x, float baseY)
            {
                _nuc.Paint(c, x, baseY);
                float ic = _nuc is Glyph g ? g.ItalicCorr : 0f;
                if (_sup != null) _sup.Paint(c, x + _sx + ic, baseY - _supShift);
                if (_sub != null) _sub.Paint(c, x + _sx, baseY + _subShift);
            }
        }

        private sealed class Limits : Box
        {
            private readonly Box _op, _over, _under; private readonly float _gap;
            private readonly float _ox, _ux, _px;
            public Limits(Box op, Box over, Box under, float size)
            {
                _op = op; _over = over; _under = under; _gap = size * 0.16f;
                W = op.W; if (over != null) W = Math.Max(W, over.W); if (under != null) W = Math.Max(W, under.W);
                _px = (W - op.W) / 2f;
                H = op.H; D = op.D;
                if (over != null) { _ox = (W - over.W) / 2f; H = op.H + _gap + over.H + over.D; }
                if (under != null) { _ux = (W - under.W) / 2f; D = op.D + _gap + under.H + under.D; }
            }
            public override void Paint(SKCanvas c, float x, float baseY)
            {
                _op.Paint(c, x + _px, baseY);
                if (_over != null) _over.Paint(c, x + _ox, baseY - _op.H - _gap - _over.D);
                if (_under != null) _under.Paint(c, x + _ux, baseY + _op.D + _gap + _under.H);
            }
        }

        private sealed class Sqrt : Box
        {
            private readonly Box _c; private readonly Box _idx; private readonly float _size, _nose, _gap, _rule, _cx;
            public Sqrt(Box content, Box index, float size)
            {
                _c = content; _idx = index; _size = size;
                _nose = size * 0.55f; _gap = size * 0.12f; _rule = Math.Max(0.6f, size * 0.05f);
                _cx = _nose;
                W = _nose + content.W + size * 0.14f;
                H = content.H + _gap + _rule;
                D = content.D;
            }
            public override void Paint(SKCanvas c, float x, float baseY)
            {
                float topY = baseY - _c.H - _gap - _rule / 2f;
                float botY = baseY + _c.D;
                using (var p = new SKPaint { Color = _col, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = _rule, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round })
                using (var path = new SKPath())
                {
                    float midY = topY + (botY - topY) * 0.6f;
                    path.MoveTo(x + _size * 0.02f, midY);
                    path.LineTo(x + _size * 0.18f, (topY + botY) / 2f + (botY - topY) * 0.10f);
                    path.LineTo(x + _size * 0.36f, botY);
                    path.LineTo(x + _nose - _size * 0.02f, topY);
                    path.LineTo(x + W, topY);
                    c.DrawPath(path, p);
                }
                _c.Paint(c, x + _cx, baseY);
                if (_idx != null) _idx.Paint(c, x + _size * 0.06f, topY + _idx.D + _size * 0.05f);
            }
        }

        private sealed class Delim : Box
        {
            private readonly string _l, _r; private readonly Box _c; private readonly float _size, _lw, _rw, _scaleY, _axis;
            public Delim(string left, string right, Box content, float size)
            {
                _l = left; _r = right; _c = content; _size = size; _axis = size * 0.26f;
                float ext = Math.Max(content.H - _axis, content.D + _axis) + size * 0.10f;
                float natural = size * 0.42f;                 // ~half a paren's height at this size
                _scaleY = Math.Max(1f, ext / natural);
                _lw = left.Length > 0 ? MeasureAdv(left, size) : 0f;
                _rw = right.Length > 0 ? MeasureAdv(right, size) : 0f;
                W = _lw + content.W + _rw + size * 0.06f;
                H = Math.Max(content.H, _axis + ext); D = Math.Max(content.D, ext - _axis);
            }
            private void DrawDelim(SKCanvas c, string s, float x, float baseY)
            {
                if (s.Length == 0) return;
                float axisY = baseY - _axis;
                var tf = PickFace(char.ConvertToUtf32(s, 0), false, false);
                c.Save();
                c.Translate(0, axisY); c.Scale(1f, _scaleY); c.Translate(0, -axisY);
                using (var p = TextPaint(tf, _size)) c.DrawText(s, x, baseY, p);
                c.Restore();
            }
            public override void Paint(SKCanvas c, float x, float baseY)
            {
                DrawDelim(c, _l, x, baseY);
                _c.Paint(c, x + _lw + _size * 0.03f, baseY);
                DrawDelim(c, _r, x + _lw + _size * 0.03f + _c.W, baseY);
            }
        }

        private sealed class Accent : Box
        {
            private readonly Box _c; private readonly int _kind; private readonly float _size;
            // kind: 0 hat, 1 tilde, 2 bar, 3 dot, 4 vec, 5 overline(rule), 6 overrightarrow
            public Accent(Box content, int kind, float size)
            {
                _c = content; _kind = kind; _size = size;
                H = content.H + size * 0.22f; D = content.D; W = content.W;
            }
            public override void Paint(SKCanvas c, float x, float baseY)
            {
                _c.Paint(c, x, baseY);
                float topY = baseY - _c.H - _size * 0.06f;
                if (_kind == 5 || _kind == 6)
                {
                    float rule = Math.Max(0.6f, _size * 0.045f);
                    using var p = new SKPaint { Color = _col, IsAntialias = true, Style = SKPaintStyle.Fill };
                    c.DrawRect(x, topY - rule, _c.W, rule, p);
                    if (_kind == 6) using (var sp = new SKPaint { Color = _col, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = rule, StrokeCap = SKStrokeCap.Round })
                    { c.DrawLine(x + _c.W - _size * 0.12f, topY - _size * 0.10f, x + _c.W, topY - rule / 2f, sp); c.DrawLine(x + _c.W - _size * 0.12f, topY + _size * 0.10f - rule, x + _c.W, topY - rule / 2f, sp); }
                }
                else
                {
                    string a = _kind == 0 ? "^" : _kind == 1 ? "~" : _kind == 2 ? "¯" : _kind == 3 ? "˙" : "→";
                    var tf = PickFace(char.ConvertToUtf32(a, 0), false, false);
                    using var p = TextPaint(tf, _size * 0.8f);
                    var r = new SKRect(); float adv = p.MeasureText(a, ref r);
                    c.DrawText(a, x + (_c.W - adv) / 2f, topY, p);
                }
            }
        }

        private sealed class Matrix : Box
        {
            private readonly List<List<Box>> _rows; private readonly float[] _colW; private readonly float[] _rowH, _rowD;
            private readonly float _colGap, _rowGap; private readonly float[] _rowY;
            public Matrix(List<List<Box>> rows, float size)
            {
                _rows = rows; _colGap = size * 0.7f; _rowGap = size * 0.35f;
                int nc = 0; foreach (var r in rows) nc = Math.Max(nc, r.Count);
                _colW = new float[nc]; _rowH = new float[rows.Count]; _rowD = new float[rows.Count]; _rowY = new float[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                    for (int j = 0; j < rows[i].Count; j++)
                    { _colW[j] = Math.Max(_colW[j], rows[i][j].W); _rowH[i] = Math.Max(_rowH[i], rows[i][j].H); _rowD[i] = Math.Max(_rowD[i], rows[i][j].D); }
                float w = 0; for (int j = 0; j < nc; j++) w += _colW[j] + (j > 0 ? _colGap : 0); W = w;
                float total = 0; for (int i = 0; i < rows.Count; i++) { _rowY[i] = total + _rowH[i]; total += _rowH[i] + _rowD[i] + (i < rows.Count - 1 ? _rowGap : 0); }
                float half = total / 2f; float axis = size * 0.26f;
                H = half + axis; D = half - axis;
                for (int i = 0; i < rows.Count; i++) _rowY[i] = _rowY[i] - half - axis;   // relative to baseline
            }
            public override void Paint(SKCanvas c, float x, float baseY)
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    float cx = x;
                    for (int j = 0; j < _rows[i].Count; j++)
                    { var cell = _rows[i][j]; cell.Paint(c, cx + (_colW[j] - cell.W) / 2f, baseY + _rowY[i]); cx += _colW[j] + _colGap; }
                }
            }
        }

        // ---- tokenizer --------------------------------------------------------------------------------
        private static List<string> Tokenize(string s)
        {
            var t = new List<string>(); int i = 0;
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == '\\')
                {
                    if (i + 1 < s.Length && IsLetter(s[i + 1]))
                    { int j = i + 1; while (j < s.Length && IsLetter(s[j])) j++; t.Add(s.Substring(i, j - i)); i = j; }
                    else if (i + 1 < s.Length) { t.Add(s.Substring(i, 2)); i += 2; }
                    else { i++; }
                }
                else if (ch == '{' || ch == '}' || ch == '^' || ch == '_' || ch == '&') { t.Add(ch.ToString()); i++; }
                else if (char.IsWhiteSpace(ch)) { i++; }
                else { t.Add(ch.ToString()); i++; }
            }
            return t;
        }
        private static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        private sealed class TS
        {
            private readonly List<string> _t; public int Pos;
            public TS(List<string> t) { _t = t; }
            public string? Peek() => Pos < _t.Count ? _t[Pos] : null;
            public string? Next() => Pos < _t.Count ? _t[Pos++] : null;
        }

        private struct Env
        {
            public float Size, Root; public bool Upright, Bold, Display;
            public Env Script() { var e = this; e.Size = Math.Max(Root * 0.42f, Size * 0.7f); e.Display = false; return e; }
        }

        private struct Atom { public Box B; public Cls C; public bool BigOp; public bool UnderOver; }

        // ---- parser -----------------------------------------------------------------------------------
        private static Box ParseList(TS ts, Env env, Func<string, bool> stop)
        {
            var atoms = new List<Atom>();
            while (true)
            {
                var tk = ts.Peek();
                if (tk == null || stop(tk)) break;
                if (tk == "^" || tk == "_") { ts.Next(); ParseAtomOrGroup(ts, env.Script()); continue; } // stray script: ignore nucleus-less
                if (tk == "\\displaystyle") { ts.Next(); env.Display = true; continue; }
                if (tk == "\\limits" || tk == "\\nolimits") { ts.Next(); continue; }
                var a = ParseAtom(ts, env);
                if (a.B == null) continue;
                Box sup = null, sub = null;
                while (ts.Peek() == "^" || ts.Peek() == "_")
                {
                    bool isSup = ts.Next() == "^";
                    var sc = ParseAtomOrGroup(ts, env.Script());
                    if (isSup) sup = sc; else sub = sc;
                }
                if (sup != null || sub != null)
                {
                    if (a.BigOp && a.UnderOver && env.Display) a.B = new Limits(a.B, sup, sub, env.Size);
                    else a.B = new Script(a.B, sup, sub, env.Size);
                }
                atoms.Add(a);
            }
            return BuildRow(atoms, env.Size);
        }

        private static Box ParseAtomOrGroup(TS ts, Env env)
        {
            if (ts.Peek() == "{") { ts.Next(); var b = ParseList(ts, env, t => t == "}"); if (ts.Peek() == "}") ts.Next(); return b; }
            var a = ParseAtom(ts, env);
            // allow a script atom itself to carry scripts (e.g. x^{y^z})
            Box sup = null, sub = null;
            while (ts.Peek() == "^" || ts.Peek() == "_")
            { bool isSup = ts.Next() == "^"; var sc = ParseAtomOrGroup(ts, env.Script()); if (isSup) sup = sc; else sub = sc; }
            if (sup != null || sub != null) a.B = new Script(a.B ?? new Kern(0), sup, sub, env.Size);
            return a.B ?? new Kern(0);
        }

        private static Atom ParseAtom(TS ts, Env env)
        {
            string tk = ts.Next() ?? "";
            if (tk == "{") { var b = ParseList(ts, env, t => t == "}"); if (ts.Peek() == "}") ts.Next(); return new Atom { B = b, C = Cls.Ord }; }
            if (tk == "}") return new Atom { B = null, C = Cls.Ord };

            switch (tk)
            {
                case "\\frac": case "\\dfrac": case "\\tfrac":
                {
                    // TeX: numerator/denominator drop to scriptstyle (~0.7×) in TEXTSTYLE (inline `\frac`, `\tfrac`);
                    // displaystyle (`\dfrac`, or `\frac` inside `\[…\]`) keeps them full. Without this, inline fractions
                    // (Example33) render ~1.6× too tall/wide → they grow the line height and wrap the paragraph early.
                    bool compact = tk == "\\tfrac" || (tk == "\\frac" && !env.Display);
                    var fe = compact ? env.Script() : env;
                    var n = ParseAtomOrGroup(ts, fe);
                    var d = ParseAtomOrGroup(ts, fe);
                    return new Atom { B = new Frac(n, d, env.Size, true), C = Cls.Inner };
                }
                case "\\binom": case "\\dbinom": case "\\tbinom":
                {
                    var n = ParseAtomOrGroup(ts, env.Script()); var d = ParseAtomOrGroup(ts, env.Script());
                    return new Atom { B = new Delim("(", ")", new Frac(n, d, env.Size, false), env.Size), C = Cls.Inner };
                }
                case "\\sqrt":
                {
                    Box idx = null;
                    if (ts.Peek() == "[") { ts.Next(); idx = ParseList(ts, env.Script(), t => t == "]"); if (ts.Peek() == "]") ts.Next(); }
                    var content = ParseAtomOrGroup(ts, env);
                    return new Atom { B = new Sqrt(content, idx, env.Size), C = Cls.Ord };
                }
                case "\\left":
                {
                    string ld = ReadDelim(ts);
                    var inner = ParseList(ts, env, t => t == "\\right");
                    if (ts.Peek() == "\\right") ts.Next();
                    string rd = ReadDelim(ts);
                    return new Atom { B = new Delim(ld, rd, inner, env.Size), C = Cls.Inner };
                }
                case "\\begin":
                {
                    string name = ReadBraceName(ts);
                    return new Atom { B = ParseMatrix(ts, env, name), C = Cls.Inner };
                }
                case "\\mathbf": case "\\boldsymbol": case "\\bm":
                { var e = env; e.Bold = true; return new Atom { B = ParseAtomOrGroup(ts, e), C = Cls.Ord }; }
                case "\\mathrm": case "\\mathsf": case "\\mathtt": case "\\operatorname":
                { var e = env; e.Upright = true; return new Atom { B = ParseAtomOrGroup(ts, e), C = Cls.Op }; }
                case "\\mathbb": case "\\mathcal": case "\\mathfrak": case "\\mathscr":
                { var e = env; e.Upright = true; e.Bold = true; return new Atom { B = ParseAtomOrGroup(ts, e), C = Cls.Ord }; }
                case "\\mathit": { var e = env; e.Upright = false; return new Atom { B = ParseAtomOrGroup(ts, e), C = Cls.Ord }; }
                case "\\text": case "\\textrm": case "\\textbf": case "\\mbox":
                { var e = env; e.Upright = true; e.Bold = tk == "\\textbf"; return new Atom { B = ParseText(ts, e), C = Cls.Ord }; }
                case "\\hat": case "\\widehat": return new Atom { B = new Accent(ParseAtomOrGroup(ts, env), 0, env.Size), C = Cls.Ord };
                case "\\tilde": case "\\widetilde": return new Atom { B = new Accent(ParseAtomOrGroup(ts, env), 1, env.Size), C = Cls.Ord };
                case "\\bar": return new Atom { B = new Accent(ParseAtomOrGroup(ts, env), 2, env.Size), C = Cls.Ord };
                case "\\dot": return new Atom { B = new Accent(ParseAtomOrGroup(ts, env), 3, env.Size), C = Cls.Ord };
                case "\\vec": return new Atom { B = new Accent(ParseAtomOrGroup(ts, env), 4, env.Size), C = Cls.Ord };
                case "\\overline": return new Atom { B = new Accent(ParseAtomOrGroup(ts, env), 5, env.Size), C = Cls.Ord };
                case "\\overrightarrow": return new Atom { B = new Accent(ParseAtomOrGroup(ts, env), 6, env.Size), C = Cls.Ord };
                case "\\,": return new Atom { B = new Kern(env.Size * 0.167f), C = Cls.Ord };
                case "\\:": case "\\>": return new Atom { B = new Kern(env.Size * 0.222f), C = Cls.Ord };
                case "\\;": return new Atom { B = new Kern(env.Size * 0.278f), C = Cls.Ord };
                case "\\!": return new Atom { B = new Kern(-env.Size * 0.167f), C = Cls.Ord };
                case "\\ ": case "\\quad": return new Atom { B = new Kern(tk == "\\quad" ? env.Size : env.Size * 0.28f), C = Cls.Ord };
                case "\\qquad": return new Atom { B = new Kern(env.Size * 2f), C = Cls.Ord };
                case "\\\\": case "\\!\\!": return new Atom { B = new Kern(0), C = Cls.Ord };
            }

            // symbol / function / plain char
            if (tk.Length > 1 && tk[0] == '\\')
            {
                string name = tk.Substring(1);
                if (Symbols.TryGetValue(name, out var sym))
                {
                    float sz = env.Size * (sym.big && env.Display ? 1.45f : sym.big ? 1.1f : 1f);
                    var gtf = PickFace(char.ConvertToUtf32(sym.ch, 0), false, env.Bold);
                    return new Atom { B = new Glyph(sym.ch, gtf, sz), C = sym.cls, BigOp = sym.big, UnderOver = sym.underOver };
                }
                if (Functions.Contains(name))
                {
                    bool lim = FunctionLimits.Contains(name);
                    return new Atom { B = new Glyph(name, Face(false, env.Bold), env.Size), C = Cls.Op, BigOp = true, UnderOver = lim };
                }
                // unknown command: render its name upright (graceful degrade)
                return new Atom { B = new Glyph(name, Face(false, env.Bold), env.Size), C = Cls.Ord };
            }

            // single character
            return CharAtom(tk, env);
        }

        private static Atom CharAtom(string ch, Env env)
        {
            if (ch.Length == 0) return new Atom { B = new Kern(0), C = Cls.Ord };
            char c0 = ch[0];
            bool letter = (c0 >= 'a' && c0 <= 'z') || (c0 >= 'A' && c0 <= 'Z');
            Cls cls = Cls.Ord;
            switch (c0)
            {
                case '+': case '*': cls = Cls.Bin; break;
                case '-': ch = "−"; cls = Cls.Bin; break;    // minus sign
                case '/': cls = Cls.Bin; break;
                case '=': cls = Cls.Rel; break;
                case '<': cls = Cls.Rel; break;
                case '>': cls = Cls.Rel; break;
                case '(': case '[': cls = Cls.Open; break;
                case ')': case ']': cls = Cls.Close; break;
                case ',': case ';': cls = Cls.Punct; break;
                case '!': cls = Cls.Close; break;
                case '|': cls = Cls.Ord; break;
            }
            bool italic = letter && !env.Upright;
            var tf = PickFace(char.ConvertToUtf32(ch, 0), italic, env.Bold);
            return new Atom { B = new Glyph(ch, tf, env.Size), C = cls };
        }

        private static Box ParseText(TS ts, Env env)
        {
            // consume a braced group verbatim as upright text (keeps spaces via kerns between tokens)
            if (ts.Peek() != "{") return ParseAtomOrGroup(ts, env);
            ts.Next();
            var sb = new StringBuilder();
            int depth = 1;
            while (true)
            {
                var tk = ts.Next(); if (tk == null) break;
                if (tk == "{") { depth++; sb.Append('{'); continue; }
                if (tk == "}") { depth--; if (depth == 0) break; sb.Append('}'); continue; }
                sb.Append(tk.StartsWith("\\") ? tk.Substring(1) : tk).Append(' ');
            }
            string txt = sb.ToString().Trim();
            var tf = Face(false, env.Bold);
            return new Glyph(txt.Length == 0 ? " " : txt, tf, env.Size);
        }

        private static string ReadDelim(TS ts)
        {
            var tk = ts.Next() ?? ".";
            if (tk == ".") return "";
            if (tk == "\\{" ) return "{";
            if (tk == "\\}") return "}";
            if (tk == "\\|" || tk == "\\Vert") return "‖";
            if (tk == "\\vert") return "|";
            if (tk == "\\langle") return "⟨";
            if (tk == "\\rangle") return "⟩";
            if (tk == "\\lfloor") return "⌊"; if (tk == "\\rfloor") return "⌋";
            if (tk == "\\lceil") return "⌈"; if (tk == "\\rceil") return "⌉";
            if (tk.Length > 1 && tk[0] == '\\' && Symbols.TryGetValue(tk.Substring(1), out var s)) return s.ch;
            return tk;
        }

        private static string ReadBraceName(TS ts)
        {
            var sb = new StringBuilder();
            if (ts.Peek() == "{") { ts.Next(); while (ts.Peek() != null && ts.Peek() != "}") sb.Append(ts.Next()); if (ts.Peek() == "}") ts.Next(); }
            return sb.ToString();
        }

        private static Box ParseMatrix(TS ts, Env env, string envName)
        {
            var rows = new List<List<Box>>();
            var cur = new List<Box>();
            var cellEnv = env; cellEnv.Display = false;
            while (true)
            {
                var tk = ts.Peek();
                if (tk == null || tk == "\\end") break;
                if (tk == "&") { ts.Next(); cur.Add(new Kern(0)); continue; }
                if (tk == "\\\\") { ts.Next(); rows.Add(cur); cur = new List<Box>(); continue; }
                var cell = ParseList(ts, cellEnv, t => t == "&" || t == "\\\\" || t == "\\end");
                cur.Add(cell);
                // if next is & we keep going; the & handler above just advances, so re-add here:
                if (ts.Peek() == "&") { ts.Next(); }
                else if (ts.Peek() == "\\\\") { ts.Next(); rows.Add(cur); cur = new List<Box>(); }
                else break;
            }
            if (cur.Count > 0) rows.Add(cur);
            if (ts.Peek() == "\\end") { ts.Next(); ReadBraceName(ts); }
            Box grid = new Matrix(rows, env.Size);
            switch (envName)
            {
                case "pmatrix": return new Delim("(", ")", grid, env.Size);
                case "bmatrix": return new Delim("[", "]", grid, env.Size);
                case "Bmatrix": return new Delim("{", "}", grid, env.Size);
                case "vmatrix": return new Delim("|", "|", grid, env.Size);
                case "Vmatrix": return new Delim("‖", "‖", grid, env.Size);
                default: return grid;
            }
        }

        private static SKTypeface PickFace(int cp, bool italic, bool bold)
        {
            bool isAsciiLetter = (cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z');
            var prim = Face(italic && isAsciiLetter, bold);
            if (Has(prim, cp)) return prim;
            var m = MathFace; if (Has(m, cp)) return m;
            var s = SansFace; if (Has(s, cp)) return s;
            return prim;
        }

        private static float MeasureAdv(string s, float size)
        { var tf = PickFace(char.ConvertToUtf32(s, 0), false, false); using var p = TextPaint(tf, size); return p.MeasureText(s); }

        private static Box BuildRow(List<Atom> atoms, float size)
        {
            // demote a binary operator to ordinary when it can't be binary (start, or after op/bin/rel/open/punct)
            for (int i = 0; i < atoms.Count; i++)
                if (atoms[i].C == Cls.Bin)
                {
                    Cls? prev = i == 0 ? (Cls?)null : atoms[i - 1].C;
                    if (prev == null || prev == Cls.Bin || prev == Cls.Op || prev == Cls.Rel || prev == Cls.Open || prev == Cls.Punct)
                    { var a = atoms[i]; a.C = Cls.Ord; atoms[i] = a; }
                }
            var row = new Row();
            float x = 0; Cls? p2 = null;
            for (int i = 0; i < atoms.Count; i++)
            {
                if (p2 != null) x += Space(p2.Value, atoms[i].C, size);
                row.Place(atoms[i].B, x); x += atoms[i].B.W;
                p2 = atoms[i].C;
            }
            row.Finish(x);
            return row;
        }

        private static float Space(Cls a, Cls b, float size)
        {
            float thin = size * 0.167f, med = size * 0.222f, thick = size * 0.278f;
            if (a == Cls.Bin || b == Cls.Bin) return med;
            if (a == Cls.Rel || b == Cls.Rel) return thick;
            if (a == Cls.Punct) return thin;
            if ((a == Cls.Op || b == Cls.Op) && !(a == Cls.Open) && !(b == Cls.Close)) return thin;
            if (a == Cls.Inner || b == Cls.Inner) return thin * 0.5f;
            return 0f;
        }

        // ---- render impl ------------------------------------------------------------------------------
        private static MathImage RenderImpl(string latex, float fontSizePt, Color color, bool display)
        {
            _col = new SKColor(color.R, color.G, color.B, color.A);
            var ts = new TS(Tokenize(latex));
            var env = new Env { Size = fontSizePt, Root = fontSizePt, Display = display };
            Box root = ParseList(ts, env, t => false);

            float padX = fontSizePt * 0.08f, padY = fontSizePt * 0.06f;
            float imgWpt = Math.Max(1f, root.W + 2 * padX);
            float abovept = padY + root.H;
            float belowpt = root.D + padY;
            float imgHpt = abovept + belowpt;

            const int scale = 4;
            int pw = Math.Max(1, (int)Math.Ceiling(imgWpt * scale));
            int ph = Math.Max(1, (int)Math.Ceiling(imgHpt * scale));
            if ((long)pw * ph > 30_000_000) throw new Exception("math bitmap too large");

            var info = new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bmp = new SKBitmap(info);
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(SKColors.Transparent);
                c.Scale(scale, scale);
                c.Translate(padX, abovept);   // baseline at y=abovept (device); local baseline 0
                root.Paint(c, 0f, 0f);
            }

            var rgba = bmp.Bytes;
            var rgb = new byte[pw * ph * 3];
            var alpha = new byte[pw * ph];
            bool anyA = false;
            for (int i = 0, j = 0, k = 0; i < rgba.Length; i += 4, j += 3, k++)
            { rgb[j] = rgba[i]; rgb[j + 1] = rgba[i + 1]; rgb[j + 2] = rgba[i + 2]; alpha[k] = rgba[i + 3]; if (rgba[i + 3] != 255) anyA = true; }

            return new MathImage
            {
                Img = new Images.DecodedImage { Width = pw, Height = ph, Rgb = rgb, Alpha = anyA ? alpha : null },
                WidthPt = imgWpt, HeightPt = abovept, DepthPt = belowpt,
            };
        }

        // ---- symbol table -----------------------------------------------------------------------------
        private struct Sym { public string ch; public Cls cls; public bool big; public bool underOver; public Sym(string c, Cls cl, bool b = false, bool uo = false) { ch = c; cls = cl; big = b; underOver = uo; } }

        private static readonly Dictionary<string, Sym> Symbols = BuildSymbols();
        private static Dictionary<string, Sym> BuildSymbols()
        {
            var d = new Dictionary<string, Sym>();
            void O(string n, int cp, Cls c = Cls.Ord) => d[n] = new Sym(char.ConvertFromUtf32(cp), c);
            void Big(string n, int cp, bool uo) => d[n] = new Sym(char.ConvertFromUtf32(cp), Cls.Op, true, uo);
            // lowercase greek
            O("alpha", 0x3B1); O("beta", 0x3B2); O("gamma", 0x3B3); O("delta", 0x3B4); O("epsilon", 0x3F5); O("varepsilon", 0x3B5);
            O("zeta", 0x3B6); O("eta", 0x3B7); O("theta", 0x3B8); O("vartheta", 0x3D1); O("iota", 0x3B9); O("kappa", 0x3BA);
            O("lambda", 0x3BB); O("mu", 0x3BC); O("nu", 0x3BD); O("xi", 0x3BE); O("omicron", 0x3BF); O("pi", 0x3C0); O("varpi", 0x3D6);
            O("rho", 0x3C1); O("varrho", 0x3F1); O("sigma", 0x3C3); O("varsigma", 0x3C2); O("tau", 0x3C4); O("upsilon", 0x3C5);
            O("phi", 0x3D5); O("varphi", 0x3C6); O("chi", 0x3C7); O("psi", 0x3C8); O("omega", 0x3C9);
            // uppercase greek
            O("Gamma", 0x393); O("Delta", 0x394); O("Theta", 0x398); O("Lambda", 0x39B); O("Xi", 0x39E); O("Pi", 0x3A0);
            O("Sigma", 0x3A3); O("Upsilon", 0x3A5); O("Phi", 0x3A6); O("Psi", 0x3A8); O("Omega", 0x3A9);
            // binary operators
            O("times", 0xD7, Cls.Bin); O("div", 0xF7, Cls.Bin); O("pm", 0xB1, Cls.Bin); O("mp", 0x2213, Cls.Bin);
            O("cdot", 0x22C5, Cls.Bin); O("ast", 0x2217, Cls.Bin); O("star", 0x22C6, Cls.Bin); O("circ", 0x2218, Cls.Bin);
            O("bullet", 0x2219, Cls.Bin); O("oplus", 0x2295, Cls.Bin); O("ominus", 0x2296, Cls.Bin); O("otimes", 0x2297, Cls.Bin);
            O("cup", 0x222A, Cls.Bin); O("cap", 0x2229, Cls.Bin); O("setminus", 0x2216, Cls.Bin); O("wedge", 0x2227, Cls.Bin);
            O("vee", 0x2228, Cls.Bin); O("land", 0x2227, Cls.Bin); O("lor", 0x2228, Cls.Bin); O("odot", 0x2299, Cls.Bin);
            // relations
            O("leq", 0x2264, Cls.Rel); O("le", 0x2264, Cls.Rel); O("geq", 0x2265, Cls.Rel); O("ge", 0x2265, Cls.Rel);
            O("neq", 0x2260, Cls.Rel); O("ne", 0x2260, Cls.Rel); O("approx", 0x2248, Cls.Rel); O("equiv", 0x2261, Cls.Rel);
            O("sim", 0x223C, Cls.Rel); O("simeq", 0x2243, Cls.Rel); O("cong", 0x2245, Cls.Rel); O("propto", 0x221D, Cls.Rel);
            O("ll", 0x226A, Cls.Rel); O("gg", 0x226B, Cls.Rel); O("subset", 0x2282, Cls.Rel); O("subseteq", 0x2286, Cls.Rel);
            O("supset", 0x2283, Cls.Rel); O("supseteq", 0x2287, Cls.Rel); O("in", 0x2208, Cls.Rel); O("notin", 0x2209, Cls.Rel);
            O("ni", 0x220B, Cls.Rel); O("mid", 0x2223, Cls.Rel); O("perp", 0x22A5, Cls.Rel); O("parallel", 0x2225, Cls.Rel);
            O("doteq", 0x2250, Cls.Rel); O("prec", 0x227A, Cls.Rel); O("succ", 0x227B, Cls.Rel); O("asymp", 0x224D, Cls.Rel);
            // arrows
            O("to", 0x2192, Cls.Rel); O("rightarrow", 0x2192, Cls.Rel); O("gets", 0x2190, Cls.Rel); O("leftarrow", 0x2190, Cls.Rel);
            O("leftrightarrow", 0x2194, Cls.Rel); O("Rightarrow", 0x21D2, Cls.Rel); O("Leftarrow", 0x21D0, Cls.Rel);
            O("Leftrightarrow", 0x21D4, Cls.Rel); O("mapsto", 0x21A6, Cls.Rel); O("implies", 0x21D2, Cls.Rel); O("iff", 0x21D4, Cls.Rel);
            O("uparrow", 0x2191); O("downarrow", 0x2193); O("longrightarrow", 0x27F6, Cls.Rel); O("longleftarrow", 0x27F5, Cls.Rel);
            // ordinary symbols
            O("infty", 0x221E); O("partial", 0x2202); O("nabla", 0x2207); O("emptyset", 0x2205); O("varnothing", 0x2205);
            O("Re", 0x211C); O("Im", 0x2111); O("aleph", 0x2135); O("hbar", 0x210F); O("ell", 0x2113); O("wp", 0x2118);
            O("forall", 0x2200); O("exists", 0x2203); O("nexists", 0x2204); O("neg", 0xAC); O("angle", 0x2220); O("triangle", 0x25B3);
            O("prime", 0x2032); O("degree", 0xB0); O("top", 0x22A4); O("bot", 0x22A5); O("surd", 0x221A);
            O("cdots", 0x22EF, Cls.Inner); O("ldots", 0x2026, Cls.Inner); O("dots", 0x2026, Cls.Inner); O("dotsc", 0x2026, Cls.Inner);
            O("vdots", 0x22EE, Cls.Inner); O("ddots", 0x22F1, Cls.Inner);
            O("langle", 0x27E8, Cls.Open); O("rangle", 0x27E9, Cls.Close); O("lfloor", 0x230A, Cls.Open); O("rfloor", 0x230B, Cls.Close);
            O("lceil", 0x2308, Cls.Open); O("rceil", 0x2309, Cls.Close); O("vert", 0x7C); O("Vert", 0x2016); O("|", 0x2016);
            O("backslash", 0x5C); O("%", 0x25); O("#", 0x23); O("&", 0x26); O("$", 0x24); O("{", 0x7B); O("}", 0x7D); O("_", 0x5F);
            O("lbrace", 0x7B, Cls.Open); O("rbrace", 0x7D, Cls.Close); O("lbrack", 0x5B, Cls.Open); O("rbrack", 0x5D, Cls.Close);
            O("quad", 0x20); // safety (also handled as kern)
            // big operators
            Big("sum", 0x2211, true); Big("prod", 0x220F, true); Big("coprod", 0x2210, true);
            Big("int", 0x222B, false); Big("iint", 0x222C, false); Big("iiint", 0x222D, false); Big("oint", 0x222E, false);
            Big("bigcup", 0x22C3, true); Big("bigcap", 0x22C2, true); Big("bigvee", 0x22C1, true); Big("bigwedge", 0x22C0, true);
            Big("bigoplus", 0x2A01, true); Big("bigotimes", 0x2A02, true); Big("bigodot", 0x2A00, true); Big("biguplus", 0x2A04, true);
            return d;
        }

        private static readonly HashSet<string> Functions = new HashSet<string>
        {
            "sin","cos","tan","cot","sec","csc","sinh","cosh","tanh","coth","arcsin","arccos","arctan",
            "exp","log","ln","lg","det","dim","gcd","hom","ker","deg","Pr","arg",
            "lim","limsup","liminf","max","min","sup","inf",
        };
        private static readonly HashSet<string> FunctionLimits = new HashSet<string>
        { "lim","limsup","liminf","max","min","sup","inf","gcd","det","Pr","arg" };
#endif
    }
}
