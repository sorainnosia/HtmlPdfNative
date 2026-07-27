using System;
using System.Collections.Generic;

namespace HtmlPdfNative.Render
{
    /// <summary>
    /// Walks the laid-out box tree and emits paint commands (backgrounds/shadows/borders/images/svg/text,
    /// in tree order). CSS <c>opacity</c>&lt;1 on a box emits an <see cref="OpacityGroup"/> — TRUE group opacity:
    /// the subtree composites into an isolated transparency-group Form XObject (in the PDF writer) and is drawn once
    /// at the group alpha, so overlapping layers/children don't double-apply the fade.
    /// </summary>
    public static class Renderer
    {
        public static DisplayList Paint(Layout.LayoutBox root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var list = new DisplayList();
            PaintBox(root, list.Commands);
            if (Rasterizer.Available) ResolveBackdropFilters(list.Commands, new List<DrawCommand>());
            return list;
        }

        /// <summary>Replace each <see cref="BackdropBlur"/> marker with a blurred raster of everything painted before it
        /// (accumulated in paint order across the whole tree) clipped to its box. Groups are walked inline; the
        /// approximation ignores ancestor clip/transform bounds, which is fine for the usual full-bleed backdrop.</summary>
        private static void ResolveBackdropFilters(List<DrawCommand> cmds, List<DrawCommand> backdrop)
        {
            for (int i = 0; i < cmds.Count; i++)
            {
                var c = cmds[i];
                switch (c)
                {
                    case BackdropBlur bd:
                        var img = Rasterizer.RasterizeBackdrop(backdrop, bd.X, bd.Y, bd.Width, bd.Height, bd.BlurPt);
                        cmds[i] = img != null
                            ? new ImageDraw { X = bd.X, Y = bd.Y, Width = bd.Width, Height = bd.Height, Image = img }
                            : new SolidRect { X = bd.X, Y = bd.Y, Width = 0f, Height = 0f, Color = new Color(0, 0, 0, 0) }; // no-op
                        if (img != null) backdrop.Add(cmds[i]);
                        break;
                    // Groups: recurse to resolve inner markers; recursion flattens their subs into `backdrop`
                    // (so we do NOT also add the container — that would double-draw).
                    case OpacityGroup og: ResolveBackdropFilters(og.Sub, backdrop); break;
                    case ClipGroup cg: ResolveBackdropFilters(cg.Sub, backdrop); break;
                    case BlendGroup bg: ResolveBackdropFilters(bg.Sub, backdrop); break;
                    case TransformGroup tg: ResolveBackdropFilters(tg.Sub, backdrop); break;
                    default: backdrop.Add(c); break;
                }
            }
        }

        private static void PaintBox(Layout.LayoutBox box, List<DrawCommand> outp, List<Layout.LayoutBox>? posSink = null)
        {
            // opacity:0 paints nothing (the box still occupies layout space).
            if (box.Opacity <= 0.001f) return;
            // opacity<1 with content that can overlap (own layers + children) needs TRUE group opacity: paint the
            // subtree fully opaque, then composite it once at box.Opacity via a transparency-group Form XObject.
            // isolation:isolate also needs the group (Alpha stays 1) so descendant blend modes composite against the
            // group's own backdrop, not the page behind it.
            bool group = box.Opacity < 0.999f || box.Isolate;
            // filter: blur() — rasterize the whole painted subtree and Gaussian-blur it (PDF has no vector blur).
            float blurPt = box.FilterSpec != null ? ParseBlurPt(box.FilterSpec) : 0f;
            bool blur = blurPt > 0.01f && Rasterizer.Available;
            bool buffer = group || blur;
            var dest = buffer ? new List<DrawCommand>() : outp;
            // mix-blend-mode wraps the WHOLE box (incl. any transform) in a /BM group blended against the backdrop.
            string? blend = box.BlendMode != null ? BlendPdfName(box.BlendMode) : null;
            var target = blend != null ? new List<DrawCommand>() : dest;
            // A real 3D transform (rotateX/Y/perspective) is projective — PDF can't express it, so rasterize the
            // subtree and warp the bitmap onto the projected quad. Falls back to the 2D-affine path if it fails.
            if (box.Transform3D != null && Rasterizer.Available)
            {
                var sub = new List<DrawCommand>();
                PaintInner(box, sub, 1f);
                var img = Warp3D(box, sub);
                if (img != null) target.Add(img);
                else if (box.Transform != null) target.Add(new TransformGroup { Matrix = box.Transform, OxGlobal = box.BorderBoxX + box.BorderBoxWidth * box.TxOriginXFrac, OyGlobal = box.BorderBoxY + box.BorderBoxHeight * box.TxOriginYFrac, Sub = sub });
                else target.AddRange(sub);
            }
            // A CSS-transformed box paints its whole subtree into a self-contained group wrapped in a CTM.
            else if (box.Transform != null)
            {
                var sub = new List<DrawCommand>();
                PaintInner(box, sub, 1f);
                target.Add(new TransformGroup
                {
                    Matrix = box.Transform,
                    OxGlobal = box.BorderBoxX + box.BorderBoxWidth * box.TxOriginXFrac,
                    OyGlobal = box.BorderBoxY + box.BorderBoxHeight * box.TxOriginYFrac,
                    Sub = sub,
                });
            }
            else PaintInner(box, target, 1f, posSink);
            if (blend != null) dest.Add(new BlendGroup { Mode = blend, Sub = target });
            if (blur)
            {
                // Blur the painted subtree over the box's border-box; falls back to the sharp commands on failure.
                var res = Rasterizer.RasterizeAndBlur(dest, box.BorderBoxX, box.BorderBoxY, box.BorderBoxWidth, box.BorderBoxHeight, blurPt);
                if (res.HasValue)
                {
                    var r = res.Value;
                    dest = new List<DrawCommand> { new ImageDraw { X = r.x, Y = r.y, Width = r.w, Height = r.h, Image = r.img } };
                }
            }
            if (group) outp.Add(new OpacityGroup { Alpha = box.Opacity, Sub = dest });
            else if (buffer) outp.AddRange(dest);   // blur-only: splice the (blurred) result into the output
        }

        /// <summary>Project the box's 4 border-box corners through its 4×4 transform (about the transform origin,
        /// with perspective divide) and warp the painted subtree onto that quad as a raster image.</summary>
        private static ImageDraw? Warp3D(Layout.LayoutBox box, List<DrawCommand> sub)
        {
            float bx = box.BorderBoxX, by = box.BorderBoxY, bw = box.BorderBoxWidth, bh = box.BorderBoxHeight;
            if (bw <= 0.5f || bh <= 0.5f) return null;
            float ox = bx + bw * box.TxOriginXFrac, oy = by + bh * box.TxOriginYFrac;
            var M = box.Transform3D!;
            var corners = new[] { (bx, by), (bx + bw, by), (bx + bw, by + bh), (bx, by + bh) };
            var quad = new float[8];
            for (int k = 0; k < 4; k++)
            {
                float lx = corners[k].Item1 - ox, ly = corners[k].Item2 - oy;
                float X = M[0] * lx + M[1] * ly + M[3];
                float Y = M[4] * lx + M[5] * ly + M[7];
                float W = M[12] * lx + M[13] * ly + M[15];
                if (Math.Abs(W) < 1e-4f) W = W < 0 ? -1e-4f : 1e-4f;
                quad[k * 2] = ox + X / W;
                quad[k * 2 + 1] = oy + Y / W;
            }
            var res = Rasterizer.RasterizeWarp(sub, bx, by, bw, bh, quad);
            if (res == null) return null;
            var r = res.Value;
            return new ImageDraw { X = r.x, Y = r.y, Width = r.w, Height = r.h, Image = r.img };
        }

        /// <summary>Extract the blur radius (pt) from a CSS <c>filter</c> string's <c>blur(&lt;len&gt;)</c>, else 0.</summary>
        private static float ParseBlurPt(string filter)
        {
            var m = System.Text.RegularExpressions.Regex.Match(filter, @"blur\(\s*([\d.]+)\s*(px|pt|rem|em)?\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || !float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0f;
            string unit = m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : "px";
            switch (unit) { case "pt": return v; case "rem": case "em": return v * 16f * 0.75f; default: return v * 0.75f; } // px→pt
        }

        /// <summary>CSS mix/background-blend-mode → PDF /BM blend-mode name (Normal for unknown).</summary>
        internal static string BlendPdfName(string m)
        {
            switch (m)
            {
                case "multiply": return "Multiply"; case "screen": return "Screen"; case "overlay": return "Overlay";
                case "darken": return "Darken"; case "lighten": return "Lighten"; case "color-dodge": return "ColorDodge";
                case "color-burn": return "ColorBurn"; case "hard-light": return "HardLight"; case "soft-light": return "SoftLight";
                case "difference": return "Difference"; case "exclusion": return "Exclusion"; case "hue": return "Hue";
                case "saturation": return "Saturation"; case "color": return "Color"; case "luminosity": return "Luminosity";
                default: return "Normal";
            }
        }

        /// <summary>True when the box establishes a new CSS stacking context (its positioned descendants don't escape
        /// to an outer context). Root, group-forming boxes (opacity/transform/isolation/blend/filter/clip-path), and
        /// positioned boxes with an explicit z-index.</summary>
        /// <summary>Compare two boxes by DOM pre-order (document order): walk both node→root chains, find the common
        /// ancestor, compare the diverging children's indices. An ancestor sorts before its descendant.</summary>
        private static int CompareDomOrder(Layout.LayoutBox a, Layout.LayoutBox b)
        {
            if (ReferenceEquals(a, b)) return 0;
            var ca = new List<Dom.Node>(); for (var n = a.DomNode; n != null; n = n.Parent) ca.Add(n); ca.Reverse();
            var cb = new List<Dom.Node>(); for (var n = b.DomNode; n != null; n = n.Parent) cb.Add(n); cb.Reverse();
            int i = 0; while (i < ca.Count && i < cb.Count && ReferenceEquals(ca[i], cb[i])) i++;
            if (i >= ca.Count) return -1;   // a is an ancestor of b
            if (i >= cb.Count) return 1;
            var parent = ca[i - 1];
            return parent.Children.IndexOf(ca[i]).CompareTo(parent.Children.IndexOf(cb[i]));
        }

        private static bool EstablishesStackingContext(Layout.LayoutBox b) =>
            b.Opacity < 0.999f || b.Isolate || b.Transform != null || b.Transform3D != null || b.BlendMode != null ||
            b.FilterSpec != null || b.ClipPathSpec != null || (b.IsPositioned && b.HasZIndex);

        private static void PaintInner(Layout.LayoutBox box, List<DrawCommand> realOut, float o, List<Layout.LayoutBox>? posSink = null)
        {
            // A CSS filter with colour-matrix functions on a non-replaced box recolours its whole painted subtree.
            var colorFilter = (box.FilterSpec != null && !box.IsReplaced && HasColorFilter(box.FilterSpec)) ? box.FilterSpec : null;
            // clip-path clips the WHOLE element (bg/borders/content) — redirect all painting into a group we wrap.
            var cpContours = box.ClipPathSpec != null ? ClipPathContours(box) : null;
            var wrapOut = realOut;
            var filterBuf = colorFilter != null ? new List<DrawCommand>() : null;
            if (filterBuf != null) wrapOut = filterBuf;
            var outp = cpContours != null ? new List<DrawCommand>() : wrapOut;
            void Add(DrawCommand c) => outp.Add(Scale(c, o));
            // visibility:hidden — the box's OWN content isn't painted, but children still recurse (a descendant
            // may set visibility:visible). Individual text runs carry their own visibility so a visible inline
            // inside a hidden block still shows.
            // overflow:hidden/clip — descendant CONTENT (images/svg/text/children) is clipped to the padding
            // box; the box's own shadows/backgrounds/borders/markers paint unclipped.
            bool clip = box.ClipsContent;
            var content = clip ? new List<DrawCommand>() : outp;
            void AddC(DrawCommand c) => content.Add(Scale(c, o));

            // backdrop-filter: blur() — a marker (resolved before pagination) that blurs whatever is painted behind
            // this box within its border box, drawn UNDER the box's own background/content.
            if (Rasterizer.Available && box.BackdropFilter != null)
            {
                float bdBlur = ParseBlurPt(box.BackdropFilter);
                if (bdBlur > 0.01f)
                    outp.Add(new BackdropBlur { X = box.BorderBoxX, Y = box.BorderBoxY, Width = box.BorderBoxWidth, Height = box.BorderBoxHeight, BlurPt = bdBlur });
            }
            // background-clip:text (gradient text): the box's fill is clipped to its glyph outlines and painted in the
            // TEXT layer, not as a background rect — so suppress the normal background AND the normal (transparent) runs.
            bool gradText = box.ClipBgToText && box.TextRuns.Count > 0 &&
                (box.BgGradient != null || box.RoundBg != null || box.Background != null
                 || (box.BgImages?.Count > 0) || (box.BgLayers?.Count > 0));
            if (box.Visible)
            {
                if (box.Shadows != null) foreach (var sh in box.Shadows) Add(sh); // box-shadow behind all
                if (!gradText)
                {
                    if (box.BgGradient != null) Add(box.BgGradient);
                    else if (box.RoundBg != null) Add(box.RoundBg);
                    else if (box.Background != null) Add(box.Background);
                    if (box.BgLayers != null) foreach (var bl in box.BgLayers) Add(bl); // multiple bg layers (bottom→top) over the colour
                    if (box.BgImages != null) foreach (var bi in box.BgImages) Add(bi); // background raster tiles (above bg color)
                }
                if (box.InsetShadows != null) foreach (var sh in box.InsetShadows) Add(sh); // inset shadow over bg, under content
                foreach (var deco in box.Decorations) Add(deco);   // borders, rules
                if (box.Polygons != null) foreach (var pg in box.Polygons) Add(pg); // mitered border triangles
                foreach (var m in box.MarkerShapes) Add(m);        // disc/circle list bullets (vector)
                if (box.RoundBorder != null) Add(box.RoundBorder); // rounded border stroke
                foreach (var img in box.Images) AddC(img);         // raster images (clipped)
                foreach (var svg in box.Svgs) content.Add(svg);    // inline vector SVG (opacity n/a)
            }
            if (gradText)
            {
                var tcf = new TextClipFill();
                foreach (var run in box.TextRuns) if (!run.Hidden) tcf.Runs.Add(run);
                if (box.BgGradient != null) tcf.Fill.Add(box.BgGradient);
                else if (box.RoundBg != null) tcf.Fill.Add(box.RoundBg);
                else if (box.Background != null) tcf.Fill.Add(box.Background);
                if (box.BgLayers != null) tcf.Fill.AddRange(box.BgLayers);
                if (box.BgImages != null) tcf.Fill.AddRange(box.BgImages);
                if (tcf.Runs.Count > 0 && tcf.Fill.Count > 0) AddC(tcf);
            }
            else foreach (var run in box.TextRuns) if (!run.Hidden) AddC(run);
            if (box.Visible) foreach (var td in box.TextDecos) AddC(td); // text-decoration lines (over glyphs)

            // CSS painting order: positioned descendants with z-index:auto paint in a LATER layer (step 6) than
            // non-positioned content (steps 3-5), in tree order — even across non-positioned ancestors. Defer them to
            // the nearest stacking context's sink; a box that establishes a stacking context owns (and drains) a sink.
            // A box owns (and drains) the positioned-descendant sink if it's a stacking-context root, OR if it
            // clips content AND is a containing block for abs descendants (position:relative + overflow:hidden):
            // the overflow clip must apply to its positioned descendants, so drain them into the CLIPPED `content`
            // rather than bubbling them to an ancestor sink where they'd escape the clip (e.g. a decorative
            // `::after` circle inside an overflow:hidden card).
            bool scRoot = posSink == null || EstablishesStackingContext(box);
            bool ownSink = scRoot || (clip && box.IsPositioned);
            var sink = ownSink ? new List<Layout.LayoutBox>() : posSink;
            foreach (var child in ZOrdered(box.Children))
            {
                if (sink != null && child.IsPositioned && !child.HasZIndex && !ReferenceEquals(child, box))
                    sink.Add(child);                                  // step 6: paint later, in tree order
                else
                    PaintBox(child, content, sink);                   // non-positioned / explicit-z: paint now (bubbling)
            }
            // drain deferred positioned-auto descendants in DOM tree order (absolute boxes are appended last during
            // layout, so their Children position ≠ their DOM order — an early absolute must paint UNDER a later one).
            if (ownSink && sink!.Count > 0)
            {
                if (sink.Count > 1) sink.Sort(CompareDomOrder);
                foreach (var d in sink) PaintBox(d, content);
            }

            if (clip)
            {
                var (cx, cy, cw, ch, rad) = box.PaddingClip();
                outp.Add(new ClipGroup { X = cx, Y = cy, Width = cw, Height = ch, Rtl = rad[0], Rtr = rad[1], Rbr = rad[2], Rbl = rad[3], Sub = content });
            }
            // Wrap the whole painted output in the clip-path polygon.
            if (cpContours != null)
            {
                float bx = box.BorderBoxX, by = box.BorderBoxY, bw = box.BorderBoxWidth, bh = box.BorderBoxHeight;
                wrapOut.Add(new ClipGroup { X = bx, Y = by, Width = bw, Height = bh, Contours = cpContours, Sub = outp });
            }
            // Recolour the filtered subtree, then emit to the real output.
            if (filterBuf != null)
            {
                foreach (var c in filterBuf) RecolorCmd(c, box.FilterSpec!);
                realOut.AddRange(filterBuf);
            }
        }

        private static bool HasColorFilter(string f)
        {
            var lo = f.ToLowerInvariant();
            return lo.Contains("grayscale") || lo.Contains("sepia") || lo.Contains("invert") || lo.Contains("brightness")
                || lo.Contains("contrast") || lo.Contains("saturate") || lo.Contains("hue-rotate");
        }

        /// <summary>Recursively apply a CSS colour-matrix filter to every colour in a draw command's subtree.</summary>
        private static void RecolorCmd(DrawCommand c, string filter)
        {
            switch (c)
            {
                case SolidRect s: s.Color = FilterColor(s.Color, filter); break;
                case TextRun t: t.Color = FilterColor(t.Color, filter); break;
                case RoundRect rr:
                    rr.Fill = rr.Fill.HasValue ? FilterColor(rr.Fill.Value, filter) : rr.Fill;
                    rr.Stroke = rr.Stroke.HasValue ? FilterColor(rr.Stroke.Value, filter) : rr.Stroke;
                    break;
                // GradientFill stops are shared structs from the style — skip to avoid mutating shared state.
                case TransformGroup tg: foreach (var s in tg.Sub) RecolorCmd(s, filter); break;
                case ClipGroup cg: foreach (var s in cg.Sub) RecolorCmd(s, filter); break;
            }
        }

        /// <summary>Apply CSS filter colour functions (grayscale/sepia/invert/brightness/contrast/saturate/hue-rotate)
        /// to one colour. blur/opacity are ignored here (handled elsewhere).</summary>
        private static Color FilterColor(Color col, string filter)
        {
            float r = col.R / 255f, g = col.G / 255f, b = col.B / 255f;
            foreach (var (name, amt) in ParseFilterList(filter))
            {
                switch (name)
                {
                    case "grayscale": { float l = 0.2126f * r + 0.7152f * g + 0.0722f * b; r += (l - r) * amt; g += (l - g) * amt; b += (l - b) * amt; break; }
                    case "sepia": { float sr = 0.393f * r + 0.769f * g + 0.189f * b, sg = 0.349f * r + 0.686f * g + 0.168f * b, sb = 0.272f * r + 0.534f * g + 0.131f * b; r += (sr - r) * amt; g += (sg - g) * amt; b += (sb - b) * amt; break; }
                    case "invert": { r += (1f - r - r) * amt; g += (1f - g - g) * amt; b += (1f - b - b) * amt; break; }
                    case "brightness": r *= amt; g *= amt; b *= amt; break;
                    case "contrast": r = (r - 0.5f) * amt + 0.5f; g = (g - 0.5f) * amt + 0.5f; b = (b - 0.5f) * amt + 0.5f; break;
                    case "saturate": { float l = 0.2126f * r + 0.7152f * g + 0.0722f * b; r = l + (r - l) * amt; g = l + (g - l) * amt; b = l + (b - l) * amt; break; }
                    case "hue-rotate":
                    {
                        double rad = amt * Math.PI / 180.0; float hc = (float)Math.Cos(rad), hs = (float)Math.Sin(rad);
                        float nr = r * (0.213f + hc * 0.787f - hs * 0.213f) + g * (0.715f - hc * 0.715f - hs * 0.715f) + b * (0.072f - hc * 0.072f + hs * 0.928f);
                        float ng = r * (0.213f - hc * 0.213f + hs * 0.143f) + g * (0.715f + hc * 0.285f + hs * 0.140f) + b * (0.072f - hc * 0.072f - hs * 0.283f);
                        float nb = r * (0.213f - hc * 0.213f - hs * 0.787f) + g * (0.715f - hc * 0.715f + hs * 0.715f) + b * (0.072f + hc * 0.928f + hs * 0.072f);
                        r = nr; g = ng; b = nb; break;
                    }
                }
            }
            byte C(float v) => (byte)(v < 0 ? 0 : v > 1 ? 255 : Math.Round(v * 255f));
            return new Color(C(r), C(g), C(b), col.A);
        }

        /// <summary>Parse filter functions into (name, amount) — % → /100, deg kept as degrees, unitless as-is.</summary>
        private static IEnumerable<(string name, float amt)> ParseFilterList(string filter)
        {
            int i = 0; var lo = filter.ToLowerInvariant();
            while (i < lo.Length)
            {
                int op = lo.IndexOf('(', i); if (op < 0) break;
                int cp = lo.IndexOf(')', op); if (cp < 0) break;
                string name = lo.Substring(i, op - i).Trim(); string arg = lo.Substring(op + 1, cp - op - 1).Trim();
                i = cp + 1;
                if (name.Length == 0) continue;
                float amt;
                if (arg.Length == 0) amt = 1f;
                else if (arg.EndsWith("%") && float.TryParse(arg.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pc)) amt = pc / 100f;
                else if (arg.EndsWith("deg") && float.TryParse(arg.Substring(0, arg.Length - 3).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dg)) amt = dg;
                else if (float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nv)) amt = nv;
                else amt = 1f;
                if (name == "grayscale" || name == "sepia" || name == "invert") amt = Math.Max(0f, Math.Min(1f, amt));
                yield return (name, amt);
            }
        }

        /// <summary>Resolve a CSS <c>clip-path</c> shape (circle/ellipse/inset/polygon) into closed contours in
        /// top-origin page coords, using the box's border box as the reference. Null if unsupported/degenerate.</summary>
        private static List<float[]>? ClipPathContours(Layout.LayoutBox box)
        {
            string spec = box.ClipPathSpec!.Trim();
            float bx = box.BorderBoxX, by = box.BorderBoxY, bw = box.BorderBoxWidth, bh = box.BorderBoxHeight;
            if (bw <= 0 || bh <= 0) return null;
            int op = spec.IndexOf('('); int cp = spec.LastIndexOf(')');
            if (op < 0 || cp < op) return null;
            string fn = spec.Substring(0, op).Trim().ToLowerInvariant();
            string args = spec.Substring(op + 1, cp - op - 1).Trim();

            // Length/percent resolver against an axis length (px→pt handled by treating px as *0.75 like layout).
            float Len(string t, float basis)
            {
                t = t.Trim();
                if (t.EndsWith("%")) return float.TryParse(t.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p / 100f * basis : 0f;
                if (t.EndsWith("px")) t = t.Substring(0, t.Length - 2);
                else if (t.EndsWith("pt")) return float.TryParse(t.Substring(0, t.Length - 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pv) ? pv : 0f;
                return float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v * 0.75f : 0f;
            }

            if (fn == "circle" || fn == "ellipse")
            {
                // split on " at " → radii | center
                string radPart = args, cenPart = "";
                int atI = args.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
                if (atI >= 0) { radPart = args.Substring(0, atI).Trim(); cenPart = args.Substring(atI + 4).Trim(); }
                float cx = bx + bw / 2f, cy = by + bh / 2f;
                if (cenPart.Length > 0)
                {
                    var cs = cenPart.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (cs.Length >= 1) cx = bx + CenterComp(cs[0], bw);
                    if (cs.Length >= 2) cy = by + CenterComp(cs[1], bh);
                }
                float rx, ry;
                var rp = radPart.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fn == "circle")
                {
                    float basis = (float)Math.Sqrt(bw * bw + bh * bh) / 1.41421356f; // % basis per spec
                    rx = ry = rp.Length >= 1 ? Radius(rp[0], basis, cx - bx, cy - by, bw, bh, true) : Radius("closest-side", 0, cx - bx, cy - by, bw, bh, true);
                }
                else
                {
                    rx = rp.Length >= 1 ? Radius(rp[0], bw, cx - bx, cy - by, bw, bh, false) : bw / 2f;
                    ry = rp.Length >= 2 ? Radius(rp[1], bh, cx - bx, cy - by, bw, bh, false) : bh / 2f;
                }
                if (rx <= 0 || ry <= 0) return null;
                const int N = 64; var pts = new float[N * 2];
                for (int i = 0; i < N; i++) { double a = 2 * Math.PI * i / N; pts[i * 2] = cx + rx * (float)Math.Cos(a); pts[i * 2 + 1] = cy + ry * (float)Math.Sin(a); }
                return new List<float[]> { pts };
            }
            if (fn == "inset")
            {
                // inset( T [R B L] [round …] ) — ignore the round radii (approx as a plain rect).
                int roundI = args.IndexOf("round", StringComparison.OrdinalIgnoreCase);
                string edges = roundI >= 0 ? args.Substring(0, roundI).Trim() : args;
                var e = edges.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                float t = e.Length >= 1 ? Len(e[0], bh) : 0f;
                float r = e.Length >= 2 ? Len(e[1], bw) : t;
                float b = e.Length >= 3 ? Len(e[2], bh) : t;
                float l = e.Length >= 4 ? Len(e[3], bw) : r;
                float x0 = bx + l, y0 = by + t, x1 = bx + bw - r, y1 = by + bh - b;
                if (x1 <= x0 || y1 <= y0) return null;
                return new List<float[]> { new[] { x0, y0, x1, y0, x1, y1, x0, y1 } };
            }
            if (fn == "polygon")
            {
                var verts = args.Split(',');
                var pts = new List<float>();
                foreach (var v in verts)
                {
                    var xy = v.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (xy.Length < 2) continue;
                    pts.Add(bx + Len(xy[0], bw));
                    pts.Add(by + Len(xy[1], bh));
                }
                return pts.Count >= 6 ? new List<float[]> { pts.ToArray() } : null;
            }
            return null;

            float CenterComp(string t, float basis)
            {
                switch (t.ToLowerInvariant()) { case "left": case "top": return 0f; case "right": case "bottom": return basis; case "center": return basis / 2f; }
                return Len(t, basis);
            }
            float Radius(string t, float pctBasis, float cxRel, float cyRel, float w, float h, bool circle)
            {
                t = t.Trim().ToLowerInvariant();
                if (t == "closest-side") return circle ? Math.Min(Math.Min(cxRel, w - cxRel), Math.Min(cyRel, h - cyRel)) : 0f;
                if (t == "farthest-side") return circle ? Math.Max(Math.Max(cxRel, w - cxRel), Math.Max(cyRel, h - cyRel)) : 0f;
                return Len(t, pctBasis);
            }
        }

        /// <summary>Children in CSS paint order: positioned+z&lt;0 behind, then everything in tree order, then
        /// positioned+z&gt;0 in front (ascending z). Stable — equal keys keep tree order. Only an explicit
        /// z-index on a positioned box changes ordering, so non-z content is never disturbed.</summary>
        private static IEnumerable<Layout.LayoutBox> ZOrdered(List<Layout.LayoutBox> children)
        {
            bool any = false;
            foreach (var c in children) if (c.IsPositioned && c.HasZIndex && c.ZIndex != 0) { any = true; break; }
            if (!any) return children;
            int Rank(Layout.LayoutBox b) => (b.IsPositioned && b.HasZIndex && b.ZIndex < 0) ? 0
                                          : (b.IsPositioned && b.HasZIndex && b.ZIndex > 0) ? 2 : 1;
            // Stable sort by (rank, z, originalIndex) via indexed OrderBy chain (LINQ OrderBy is stable).
            var idx = new List<int>();
            for (int i = 0; i < children.Count; i++) idx.Add(i);
            idx.Sort((a, b) =>
            {
                int ra = Rank(children[a]), rb = Rank(children[b]);
                if (ra != rb) return ra.CompareTo(rb);
                if (ra != 1 && children[a].ZIndex != children[b].ZIndex) return children[a].ZIndex.CompareTo(children[b].ZIndex);
                return a.CompareTo(b); // stable: preserve tree order
            });
            var outl = new List<Layout.LayoutBox>(children.Count);
            foreach (var i in idx) outl.Add(children[i]);
            return outl;
        }

        /// <summary>Clone a command with its alpha scaled by <paramref name="o"/> (identity when o≈1).</summary>
        private static DrawCommand Scale(DrawCommand c, float o)
        {
            if (o >= 0.999f) return c;
            byte A(Color col) => (byte)Math.Round(col.A * o);
            Color S(Color col) => new Color(col.R, col.G, col.B, A(col));
            switch (c)
            {
                case SolidRect r: return new SolidRect { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Color = S(r.Color) };
                case TextRun t: return new TextRun { X = t.X, BaselineY = t.BaselineY, Text = t.Text, FontSizePt = t.FontSizePt, Face = t.Face, Color = S(t.Color), LetterSpacing = t.LetterSpacing, Emb = t.Emb, Hidden = t.Hidden };
                case RoundRect rr: return new RoundRect { X = rr.X, Y = rr.Y, Width = rr.Width, Height = rr.Height, Rtl = rr.Rtl, Rtr = rr.Rtr, Rbr = rr.Rbr, Rbl = rr.Rbl, Fill = rr.Fill.HasValue ? S(rr.Fill.Value) : (Color?)null, Stroke = rr.Stroke.HasValue ? S(rr.Stroke.Value) : (Color?)null, StrokeW = rr.StrokeW, Clip = rr.Clip, ClipX = rr.ClipX, ClipY = rr.ClipY, ClipW = rr.ClipW, ClipH = rr.ClipH, ClipRtl = rr.ClipRtl, ClipRtr = rr.ClipRtr, ClipRbr = rr.ClipRbr, ClipRbl = rr.ClipRbl };
                case GradientFill g: return new GradientFill { X = g.X, Y = g.Y, Width = g.Width, Height = g.Height, Gradient = g.Gradient, Rtl = g.Rtl, Rtr = g.Rtr, Rbr = g.Rbr, Rbl = g.Rbl, Alpha = g.Alpha * o };
                case ImageDraw im: return new ImageDraw { X = im.X, Y = im.Y, Width = im.Width, Height = im.Height, Image = im.Image, Clip = im.Clip, ClipX = im.ClipX, ClipY = im.ClipY, ClipW = im.ClipW, ClipH = im.ClipH, Rtl = im.Rtl, Rtr = im.Rtr, Rbr = im.Rbr, Rbl = im.Rbl, Alpha = im.Alpha * o };
                default: return c;
            }
        }
    }
}
