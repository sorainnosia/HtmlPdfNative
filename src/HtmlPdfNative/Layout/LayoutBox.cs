using System;
using System.Collections.Generic;
using HtmlPdfNative.Css;
using HtmlPdfNative.Fonts;
using HtmlPdfNative.Render;
using HtmlPdfNative.Style;
using HtmlPdfNative.Styled;

namespace HtmlPdfNative.Layout
{
    /// <summary>Axis-aligned rectangle in PDF points (y measured from page top). Port of <c>layout::Rect</c>.</summary>
    public struct Rect
    {
        public float X, Y, Width, Height;
        public Rect(float x, float y, float w, float h) { X = x; Y = y; Width = w; Height = h; }
    }

    /// <summary>
    /// Block + inline + flex layout. Slice of <c>src/layout.rs</c>: block boxes stack with
    /// margin/padding/border/width; inline content wraps into lines (line-height rounded to px per
    /// "chrome-print-parity"), honours text-align/<c>&lt;br&gt;</c>, list markers and <c>&lt;hr&gt;</c>;
    /// and a flex row container (justify-content, align-items, gap, flex-grow/basis). Grid/tables/
    /// floats/abs/transforms come later.
    /// </summary>
    public sealed class LayoutBox
    {
        private readonly StyledNode _node;
        public SolidRect? Background;
        public GradientFill? BgGradient;
        public List<ImageDraw>? BgImages; // background-image tile(s) (one per background-repeat cell)
        public List<DrawCommand>? BgLayers; // multiple background layers (GradientFill/ImageDraw), painted bottom→top
        public RoundRect? RoundBg;       // rounded background fill (border-radius)
        public RoundRect? RoundBorder;   // rounded border stroke (border-radius, uniform border)
        public bool ClipBgToText;        // background-clip:text → clip the fill to this box's text glyphs (gradient text)
        public List<RoundRect>? Shadows; // outset box-shadow layers (painted behind everything)
        public List<RoundRect>? InsetShadows; // inset box-shadow layers (painted over bg, under content, clipped to box)
        public readonly List<SolidRect> Decorations = new List<SolidRect>();
        public List<Polygon>? Polygons; // filled polygons (mitered border edges / CSS triangles)
        public readonly List<RoundRect> MarkerShapes = new List<RoundRect>(); // disc/circle bullet markers (vector)
        public readonly List<SolidRect> TextDecos = new List<SolidRect>(); // text-decoration lines (painted over text)
        public readonly List<ImageDraw> Images = new List<ImageDraw>();
        public readonly List<SvgDraw> Svgs = new List<SvgDraw>();
        public readonly List<TextRun> TextRuns = new List<TextRun>();
        public readonly List<LayoutBox> Children = new List<LayoutBox>();
        public readonly List<StyledNode> PendingAbs = new List<StyledNode>(); // out-of-flow children hosted here
        public float MarginBoxHeight;
        public float BorderBoxHeight;
        public float BorderBoxWidth;
        public float BorderBoxX, BorderBoxY;         // border-box top-left (top-origin), for transform origin
        public float[]? Transform;                    // CSS transform matrix (pt), null = none
        public float TxOriginXFrac = 0.5f, TxOriginYFrac = 0.5f;
        public float[]? Transform3D => _node.Style.Transform3D;   // 4×4 for non-affine (rotateX/Y/perspective) warp
        public float Opacity = 1f;                    // element opacity (multiplies its subtree's alpha)

        // Stacking (z-index) info for paint-order sorting.
        public int ZIndex => _node.Style.ZIndex;
        public bool IsPositioned => _node.Style.IsPositioned;
        public bool HasZIndex => _node.Style.HasZIndex;
        public bool Visible => _node.Style.Visibility == "visible"; // visibility:hidden/collapse → own content not painted
        public bool ClipsContent => _node.Style.Overflow != "visible"; // overflow:hidden/clip/scroll/auto
        public string? ClipPathSpec => _node.Style.ClipPath;            // clip-path shape (null = none)
        public string? BlendMode => _node.Style.MixBlendMode;           // mix-blend-mode → PDF /BM (null = normal)
        public bool Isolate => _node.Style.Isolate;                     // isolation:isolate → isolated transparency group
        public string? BackdropFilter => _node.Style.BackdropFilter;    // backdrop-filter (blur) → frosted-glass raster
        public string? FilterSpec => _node.Style.Filter;                // CSS filter (color-matrix parts applied at render for non-replaced boxes)
        public bool IsReplaced => _node.Node.Tag == "img" || _node.Node.Tag == "svg"; // filter already applied in layout
        public Dom.Node DomNode => _node.Node;   // for CSS painting order (positioned descendants in DOM tree order)
        public float[]? Radii;   // resolved border-box corner radii (set in ApplyBoxDecor), for overflow clipping

        /// <summary>The padding-box rect (+ inner radii) that overflow:hidden clips descendant content to.</summary>
        public (float x, float y, float w, float h, float[] r) PaddingClip()
        {
            var s = _node.Style;
            float bl = s.BorderLeft.Width, brw = s.BorderRight.Width, bt = s.BorderTop.Width, bb = s.BorderBottom.Width;
            float x = BorderBoxX + bl, y = BorderBoxY + bt;
            float w = Math.Max(0, BorderBoxWidth - bl - brw), h = Math.Max(0, BorderBoxHeight - bt - bb);
            var rad = new float[4];
            if (Radii != null)
            {
                rad[0] = Math.Max(0, Radii[0] - Math.Max(bl, bt)); rad[1] = Math.Max(0, Radii[1] - Math.Max(brw, bt));
                rad[2] = Math.Max(0, Radii[2] - Math.Max(brw, bb)); rad[3] = Math.Max(0, Radii[3] - Math.Max(bl, bb));
            }
            return (x, y, w, h, rad);
        }

        /// <summary>Active floats in a block formatting context (absolute top-origin coords).</summary>
        private sealed class FloatCtx
        {
            public readonly List<(float x0, float x1, float yTop, float yBot, bool left)> F = new List<(float, float, float, float, bool)>();

            /// <summary>Left/right content edges available at the vertical band [yT,yB], given active floats.</summary>
            public (float l, float r) Avail(float cl, float cr, float yT, float yB)
            {
                float l = cl, r = cr;
                foreach (var f in F)
                {
                    if (yB <= f.yTop || yT >= f.yBot) continue;
                    if (f.left) l = Math.Max(l, f.x1); else r = Math.Min(r, f.x0);
                }
                return (l, r);
            }
            public float NextBottomBelow(float y)
            {
                float best = float.MaxValue;
                foreach (var f in F) if (f.yBot > y + 0.01f) best = Math.Min(best, f.yBot);
                return best == float.MaxValue ? y : best;
            }
            public float ClearBottom(string side)
            {
                float b = 0f;
                foreach (var f in F)
                    if (side == "both" || (side == "left" && f.left) || (side == "right" && !f.left)) b = Math.Max(b, f.yBot);
                return b;
            }
        }

        private LayoutBox(StyledNode node) { _node = node; }

        public static LayoutBox Build(StyledNode styledRoot) => new LayoutBox(styledRoot);

        // Pagination grid for forced page breaks (page-break-before/after). Set at the start of Layout();
        // 0 disables breaks. Matches the geometric grid PdfGenerator.Paginate slices on (full page height).
        private static float _pageGridH;
        private static float _pageMarginTop;

        /// <summary>Absolute y of the top of the next page (after y), inset by the top margin. Returns y unchanged
        /// when already at the top of the current page (avoids inserting a blank page).</summary>
        private static float NextPageTop(float y)
        {
            if (_pageGridH <= 0f) return y;
            int p = (int)Math.Floor(y / _pageGridH);
            float curTop = p * _pageGridH + _pageMarginTop;
            if (y <= curTop + 1f) return y;               // already at (or above) this page's content top
            return (p + 1) * _pageGridH + _pageMarginTop;
        }

        public void Layout(Rect cb, Css.PageConfig page)
        {
            _pageGridH = cb.Height;
            _pageMarginTop = page.MarginTop;
            float availWidth = cb.Width - page.MarginLeft - page.MarginRight;
            // The root box is the initial containing block: page-anchored absolutes bubble up here.
            LayoutBlock(_node, cb.X + page.MarginLeft, cb.Y + page.MarginTop, availWidth, this, null, posHost: this, floats: new FloatCtx());
        }

        /// <summary>Shift this box and everything under it by (dx, dy).</summary>
        public void Translate(float dx, float dy)
        {
            // Keep the border-box origin in sync so anything reading it AFTER placement (CSS transform origin,
            // overflow/clip-path bounds) uses the final position — an inline-block/atomic box is laid out at (0,0)
            // then translated into its line, so a stale origin would rotate/clip it about the wrong point.
            BorderBoxX += dx; BorderBoxY += dy;
            if (Background != null) { Background.X += dx; Background.Y += dy; }
            if (BgGradient != null) { BgGradient.X += dx; BgGradient.Y += dy; }
            if (BgImages != null) foreach (var bi in BgImages) { bi.X += dx; bi.Y += dy; bi.ClipX += dx; bi.ClipY += dy; }
            if (BgLayers != null) foreach (var bl in BgLayers) TranslateCmd(bl, dx, dy);
            if (RoundBg != null) { RoundBg.X += dx; RoundBg.Y += dy; }
            if (RoundBorder != null) { RoundBorder.X += dx; RoundBorder.Y += dy; }
            if (Shadows != null) foreach (var sh in Shadows) { sh.X += dx; sh.Y += dy; }
            if (InsetShadows != null) foreach (var sh in InsetShadows) { sh.X += dx; sh.Y += dy; sh.ClipX += dx; sh.ClipY += dy; }
            foreach (var d in Decorations) { d.X += dx; d.Y += dy; if (d.Clip) { d.ClipX += dx; d.ClipY += dy; } }
            if (Polygons != null) foreach (var pg in Polygons) { for (int k = 0; k < pg.Points.Length; k += 2) { pg.Points[k] += dx; pg.Points[k + 1] += dy; } }
            foreach (var d in TextDecos) { d.X += dx; d.Y += dy; }
            foreach (var m in MarkerShapes) { m.X += dx; m.Y += dy; }
            foreach (var im in Images) { im.X += dx; im.Y += dy; }
            foreach (var sv in Svgs) { sv.X += dx; sv.Y += dy; }
            foreach (var t in TextRuns) { t.X += dx; t.BaselineY += dy; }
            foreach (var c in Children) c.Translate(dx, dy);
        }

        /// <summary>Shift a background-layer draw command by (dx,dy), recursing into blend/clip groups (a
        /// background-blend-mode layer is wrapped in a BlendGroup — if not recursed it stays at the old position).</summary>
        private static void TranslateCmd(DrawCommand c, float dx, float dy)
        {
            switch (c)
            {
                case GradientFill gf: gf.X += dx; gf.Y += dy; break;
                case ImageDraw im: im.X += dx; im.Y += dy; im.ClipX += dx; im.ClipY += dy; break;
                case SolidRect sr: sr.X += dx; sr.Y += dy; break;
                case RoundRect rr: rr.X += dx; rr.Y += dy; rr.ClipX += dx; rr.ClipY += dy; break;
                case Polygon pg: for (int k = 0; k < pg.Points.Length; k += 2) { pg.Points[k] += dx; pg.Points[k + 1] += dy; } break;
                case BlendGroup bg: foreach (var s in bg.Sub) TranslateCmd(s, dx, dy); break;
                case ClipGroup cg: cg.X += dx; cg.Y += dy; foreach (var s in cg.Sub) TranslateCmd(s, dx, dy); break;
            }
        }

        private static void LayoutBlock(StyledNode node, float x, float y, float availWidth, LayoutBox box,
            string? marker, float? forcedBorderBoxWidth = null, float? forcedBorderBoxHeight = null, LayoutBox? posHost = null,
            FloatCtx? floats = null, bool noBorder = false, bool suppressDecor = false, bool ignoreVertical = false,
            float? availHeight = null)
        {
            if (node.Node.Tag == "img") { LayoutImage(node, x, y, availWidth, box, forcedBorderBoxWidth); return; }
            if (node.Node.Tag == "svg") { LayoutSvg(node, x, y, availWidth, box, forcedBorderBoxWidth); return; }

            var s = node.Style;
            // This box becomes the containing block for its positioned descendants when it is positioned.
            LayoutBox? childHost = s.IsPositioned ? box : posHost;
            float bl = s.BorderLeft.Width, brw = s.BorderRight.Width, bt = s.BorderTop.Width, bb = s.BorderBottom.Width;

            // Vertical writing-mode (vertical-rl/lr): lay the content out HORIZONTALLY at its natural (unwrapped)
            // width, then transpose the box and rotate the whole subtree 90° clockwise (an affine op). Latin glyphs
            // read top-to-bottom sideways (text-orientation:mixed); block-flow rl vs lr differ only for multi-column
            // stacking, which single labels don't hit. An explicit `transform` on the element is not composed here.
            if (!ignoreVertical && (s.WritingMode == "vertical-rl" || s.WritingMode == "vertical-lr"))
            {
                float padW = s.PadLeft + s.PadRight + bl + brw;
                float natW = MeasureMaxContent(node) + padW;
                LayoutBlock(node, x, y, natW, box, marker, natW, null, posHost, floats, noBorder, suppressDecor, ignoreVertical: true);
                float cw = box.BorderBoxWidth, ch = box.BorderBoxHeight;   // horizontal content: cw long, ch thick
                box.BorderBoxWidth = ch; box.BorderBoxHeight = cw;         // transpose: thickness across, length down
                box.MarginBoxHeight = cw + s.MarginTop + s.MarginBottom;   // the vertical text length is the flow height
                box.TxOriginXFrac = 0f; box.TxOriginYFrac = 0f;            // rotate about the box top-left (= content TL)
                box.Transform = new float[] { 0f, 1f, -1f, 0f, ch, 0f };   // 90° CW + translate +ch (new width) in x
                return;
            }

            // Definite height: explicit `height`, or a `height:%` resolved against the containing block's definite
            // content height (availHeight). Its content-box height is what children resolve their own % heights against.
            float? selfDefH = s.Height ?? (s.HeightPercent.HasValue && availHeight.HasValue && availHeight.Value > 0f ? availHeight.Value * s.HeightPercent.Value / 100f : (float?)null);
            // A forced border-box height (an absolute box stretched by top+bottom/inset, or a stretched flex/grid item)
            // IS this box's definite content height — it wins over the parent's availHeight for the children.
            // gridDefiniteH = a TRULY definite content height (forced or explicit) — used ONLY to stretch grid rows
            // (donut ::after centering). Kept SEPARATE from childAvailH so it doesn't change %-height resolution
            // broadly (which re-paginated unrelated docs).
            float? gridDefiniteH = forcedBorderBoxHeight.HasValue ? Math.Max(0f, forcedBorderBoxHeight.Value - bt - bb - s.PadTop - s.PadBottom)
                               : selfDefH.HasValue ? Math.Max(0f, s.BoxSizing == "border-box" ? selfDefH.Value - bt - bb - s.PadTop - s.PadBottom : selfDefH.Value) : (float?)null;
            float? childAvailH = selfDefH.HasValue ? Math.Max(0f, s.BoxSizing == "border-box" ? selfDefH.Value - bt - bb - s.PadTop - s.PadBottom : selfDefH.Value) : (float?)null;
            // A FLEX/GRID item in a definite-height container gets a definite cross size (the line height, passed as
            // availHeight) even without its own `height` — so ITS flex/grid children can resolve their `height:%`
            // (e.g. combo-col → combo-bar). Plain block boxes must NOT do this (block %-height needs a definite parent).
            if (childAvailH == null && availHeight.HasValue && availHeight.Value > 0f && (s.Display == "flex" || s.Display == "grid"))
                childAvailH = Math.Max(0f, availHeight.Value - bt - bb - s.PadTop - s.PadBottom);

            float contentWidth, borderBoxWidth;
            if (forcedBorderBoxWidth.HasValue)
            {
                borderBoxWidth = Math.Max(0, forcedBorderBoxWidth.Value);
                contentWidth = Math.Max(0, borderBoxWidth - s.PadLeft - s.PadRight - bl - brw);
            }
            else if (s.WidthPercent.HasValue)
            {
                // % width behaves as a border-box fraction of the containing block (avoids overflow).
                borderBoxWidth = Math.Max(0, availWidth * s.WidthPercent.Value / 100f);
                contentWidth = Math.Max(0, borderBoxWidth - s.PadLeft - s.PadRight - bl - brw);
            }
            else if (s.WidthCalc != null)
            {
                // calc()/min()/max()/clamp() with a % — the % resolves against the containing block width (availWidth).
                float cw = Math.Max(0f, Css.Values.LengthPt(s.WidthCalc, s.FontSizePt, availWidth) ?? 0f);
                if (s.BoxSizing == "border-box") { borderBoxWidth = Math.Max(s.PadLeft + s.PadRight + bl + brw, cw); contentWidth = Math.Max(0, borderBoxWidth - s.PadLeft - s.PadRight - bl - brw); }
                else { contentWidth = cw; borderBoxWidth = cw + s.PadLeft + s.PadRight + bl + brw; }
            }
            else if (s.Width.HasValue)
            {
                if (s.BoxSizing == "border-box")
                {
                    // A border-box width can't shrink below its own padding+borders (CSS clamps content to ≥0). This
                    // matters for the CSS-triangle trick (`width:0;border:…`), where the borders ARE the box.
                    borderBoxWidth = Math.Max(s.PadLeft + s.PadRight + bl + brw, Math.Max(0, s.Width.Value));
                    contentWidth = Math.Max(0, borderBoxWidth - s.PadLeft - s.PadRight - bl - brw);
                }
                else
                {
                    contentWidth = Math.Max(0, s.Width.Value);
                    borderBoxWidth = contentWidth + s.PadLeft + s.PadRight + bl + brw;
                }
            }
            else if (s.WidthSizing != null)
            {
                // min-content → widest unbreakable piece; max-content/fit-content → content width; all clamped to
                // the available width (avoids page overflow).
                float wExtra0 = s.PadLeft + s.PadRight + bl + brw;
                float avail = Math.Max(0, availWidth - s.MarginLeft - s.MarginRight);
                float measured = s.WidthSizing == "min-content" ? MeasureMinContent(node) : MeasureMaxContent(node);
                borderBoxWidth = Math.Min(avail, measured + wExtra0);
                contentWidth = Math.Max(0, borderBoxWidth - wExtra0);
            }
            else
            {
                borderBoxWidth = Math.Max(0, availWidth - s.MarginLeft - s.MarginRight);
                contentWidth = Math.Max(0, borderBoxWidth - s.PadLeft - s.PadRight - bl - brw);
            }

            // min-width / max-width clamp the border-box width (constraints follow box-sizing / are % of the CB).
            if (!forcedBorderBoxWidth.HasValue)
            {
                float wExtra = s.PadLeft + s.PadRight + bl + brw;
                float ToBB(float v) => s.BoxSizing == "border-box" ? v : v + wExtra;
                float? minBB = s.MinWidth.HasValue ? ToBB(s.MinWidth.Value) : (s.MinWidthPct.HasValue ? availWidth * s.MinWidthPct.Value : (float?)null);
                float? maxBB = s.MaxWidth.HasValue ? ToBB(s.MaxWidth.Value) : (s.MaxWidthPct.HasValue ? availWidth * s.MaxWidthPct.Value : (float?)null);
                if (maxBB.HasValue && borderBoxWidth > maxBB.Value) borderBoxWidth = maxBB.Value;
                if (minBB.HasValue && borderBoxWidth < minBB.Value) borderBoxWidth = minBB.Value;
                contentWidth = Math.Max(0, borderBoxWidth - wExtra);
            }

            // margin:auto — absorb horizontal free space (center / push) when the box has a definite width.
            float mLeft = s.MarginLeft;
            if (!forcedBorderBoxWidth.HasValue && (s.MarginLeftAuto || s.MarginRightAuto))
            {
                float freeSpace = availWidth - borderBoxWidth - s.MarginLeft - s.MarginRight;
                if (freeSpace > 0.01f)
                {
                    if (s.MarginLeftAuto && s.MarginRightAuto) mLeft += freeSpace / 2f;
                    else if (s.MarginLeftAuto) mLeft += freeSpace;   // right-align
                }
            }
            float borderX = x + mLeft;
            float contentX = borderX + bl + s.PadLeft;
            float top = y + s.MarginTop;
            float contentTop = top + bt + s.PadTop;
            // Multi-column: lay the content narrow (at one column's width) into a tall single column,
            // then redistribute its lines/children across N columns after the flow finishes.
            int mcN = 0; float mcColW = 0f, mcGap = s.ColumnGapPt;
            bool multicol = (s.ColumnCount > 0 || s.ColumnWidth.HasValue) && s.Display != "flex" && s.Display != "grid" && s.Display != "table";
            if (multicol)
            {
                float full = contentWidth;
                mcN = s.ColumnCount > 0 ? s.ColumnCount
                    : Math.Max(1, (int)Math.Floor((full + mcGap) / (s.ColumnWidth!.Value + mcGap)));
                mcN = Math.Max(1, mcN);
                mcColW = Math.Max(1f, (full - (mcN - 1) * mcGap) / mcN);
                if (mcN > 1) contentWidth = mcColW; else multicol = false;
            }

            float cursorY = contentTop;

            if (node.Node.Tag == "hr")
            {
                // <hr>: honour height (thickness), and colour from border/background (default grey).
                float hrH = s.Height ?? (s.BorderTop.Width > 0 ? s.BorderTop.Width : 1f);
                Color hrColor = s.BorderTop.Width > 0 && s.BorderTop.Color.A > 0 ? s.BorderTop.Color
                              : (s.BackgroundColor.HasValue && s.BackgroundColor.Value.A > 0 ? s.BackgroundColor.Value : new Color(160, 160, 160));
                box.Decorations.Add(new SolidRect { X = contentX, Y = contentTop, Width = contentWidth, Height = hrH, Color = hrColor });
                cursorY += hrH;
            }
            else if (s.Display == "flex")
            {
                cursorY += (s.FlexDirection == "column" || s.FlexDirection == "column-reverse")
                    ? LayoutFlexColumn(node, box, contentX, contentTop, contentWidth, childHost)
                    : LayoutFlexRow(node, box, contentX, contentTop, contentWidth, childHost, childAvailH);
            }
            else if (s.Display == "table")
            {
                cursorY += LayoutTable(node, box, contentX, contentTop, contentWidth, childHost);
            }
            else if (s.Display == "grid")
            {
                cursorY += LayoutGrid(node, box, contentX, contentTop, contentWidth, childHost, gridDefiniteH);
            }
            else
            {
                // list-style-position:inside → the marker flows inline as the first content of the first line.
                string? markerPrefix = null;
                if (marker != null && s.ListStylePosition == "inside")
                {
                    markerPrefix = (marker == "disc" || marker == "circle" || marker == "square") ? "• " : marker + " ";
                    marker = null; // don't draw it in the margin
                }
                if (marker != null)
                {
                    float fs = s.FontSizePt, lh = s.EffectiveLineHeightPt;
                    float baseline = contentTop + Math.Max(0f, (lh - fs) / 2f) + fs * 0.8f;
                    Color mColor = node.MarkerStyle != null ? node.MarkerStyle.Color : s.Color; // ::marker { color }
                    HtmlPdfNative.Images.DecodedImage? lsImg = s.ListStyleImage != null ? HtmlPdfNative.Images.ImageLoader.Load(s.ListStyleImage) : null;
                    if (lsImg != null)
                    {
                        // list-style-image: an image bullet, vertically centred on the first line, left of content.
                        float iw = lsImg.Width * Lib.PxToPt, ih = lsImg.Height * Lib.PxToPt;
                        float lineTop = contentTop + Math.Max(0f, (lh - fs) / 2f);
                        box.Images.Add(new ImageDraw { X = contentX - 6f - iw, Y = lineTop + (fs - ih) / 2f, Width = iw, Height = ih, Image = lsImg });
                    }
                    else if (marker == "disc" || marker == "circle" || marker == "square")
                    {
                        // Bullet markers as vectors (font-independent): centred on the first line's x-height.
                        float d = fs * 0.35f;
                        float cy = baseline - fs * 0.28f;
                        float mx = contentX - 6f - d;          // left edge, sits left of the content
                        if (marker == "square")
                            box.Decorations.Add(new SolidRect { X = mx, Y = cy - d / 2f, Width = d, Height = d, Color = mColor });
                        else
                            box.MarkerShapes.Add(new RoundRect
                            {
                                X = mx, Y = cy - d / 2f, Width = d, Height = d, Rtl = d / 2f, Rtr = d / 2f, Rbr = d / 2f, Rbl = d / 2f,
                                Fill = marker == "disc" ? mColor : (Color?)null,
                                Stroke = marker == "circle" ? mColor : (Color?)null,
                                StrokeW = marker == "circle" ? Math.Max(0.6f, fs * 0.06f) : 0f,
                            });
                    }
                    else
                    {
                        // Route the marker through the embedded-font fallback so non-Latin markers (e.g. lower-greek) get glyphs.
                        var memb = FontManager.ResolveForWord(s, marker);
                        float mw = memb != null ? memb.MeasurePt(marker, fs) : Afm.MeasurePt(s.Face, marker, fs);
                        memb?.MarkUsed(marker);
                        box.TextRuns.Add(new TextRun { X = contentX - mw - 4f, BaselineY = baseline, Text = marker, FontSizePt = fs, Face = s.Face, Color = mColor, Emb = memb, Hidden = s.Visibility != "visible" });
                    }
                }

                bool ul = node.Node.Tag == "ul", ol = node.Node.Tag == "ol";
                string? olTypeAttr = ol && node.Node.Attributes.TryGetValue("type", out var otv) ? otv.Trim() : null;
                bool olReversed = ol && node.Node.Attributes.ContainsKey("reversed");
                int olLiCount = 0;
                if (olReversed) foreach (var c in node.Children) if (!c.IsText && c.Node.Tag == "li") olLiCount++;
                int liCounter = ol ? AttrIntOr(node, "start", olReversed ? olLiCount : 1) : 1;
                float floatBottom = contentTop;
                var inlineBuf = new List<StyledNode>();
                var firstLetter = node.FirstLetterStyle;   // consumed by the first line only
                var firstLine = node.FirstLineStyle;
                void FlushInline()
                {
                    if (inlineBuf.Count == 0) return;
                    cursorY += LayoutInline(inlineBuf, node.Style, contentX, cursorY, contentWidth, box, floats, childHost, firstLetter, markerPrefix, firstLine, node.Style.LineClamp);
                    firstLetter = null; markerPrefix = null; firstLine = null;
                    inlineBuf.Clear();
                }
                foreach (var child in node.Children)
                {
                    // Out-of-flow (absolute/fixed): removed from flow, laid out in the host's post-pass.
                    if (!child.IsText && child.Style.IsOutOfFlow) { childHost?.PendingAbs.Add(child); continue; }

                    // Floated child: taken out of flow, placed at an edge; later content wraps beside it.
                    if (!child.IsText && floats != null && (child.Style.Float == "left" || child.Style.Float == "right"))
                    {
                        FlushInline();
                        bool left = child.Style.Float == "left";
                        var fbx = new LayoutBox(child);
                        float? fw = child.Style.Width.HasValue || child.Style.WidthPercent.HasValue ? null
                                  : Math.Min(MeasureMaxContent(child), contentWidth);
                        LayoutBlock(child, contentX, cursorY, contentWidth, fbx, null, forcedBorderBoxWidth: fw, posHost: childHost);
                        float bw = fbx.BorderBoxWidth + child.Style.MarginLeft + child.Style.MarginRight;
                        float fh = fbx.MarginBoxHeight;
                        // Find a vertical position where a slot of width bw fits on the chosen side.
                        float fy = cursorY;
                        while (true)
                        {
                            var (aL, aR) = floats.Avail(contentX, contentX + contentWidth, fy, fy + fh);
                            if (aR - aL >= bw - 0.01f) break;
                            float nb = floats.NextBottomBelow(fy);
                            if (nb <= fy) break;
                            fy = nb;
                        }
                        var (bL, bR) = floats.Avail(contentX, contentX + contentWidth, fy, fy + fh);
                        float fx = left ? bL : bR - bw;
                        fbx.Translate(fx - contentX, fy - cursorY);
                        box.Children.Add(fbx);
                        floats.F.Add((fx, fx + bw, fy, fy + fh, left));
                        floatBottom = Math.Max(floatBottom, fy + fh);
                        continue;
                    }

                    if (!child.IsText && IsBlockLevel(child.Style.Display))   // img/svg are inline (atomic) by default; block only when display says so
                    {
                        FlushInline();
                        // clear: drop below the relevant floats before placing this block.
                        if (floats != null && child.Style.Clear != "none")
                            cursorY = Math.Max(cursorY, floats.ClearBottom(child.Style.Clear));
                        // page-break-before: push this block to the top of the next page.
                        string bb4 = child.Style.BreakBefore;
                        if (bb4 == "always" || bb4 == "page" || bb4 == "left" || bb4 == "right") cursorY = NextPageTop(cursorY);
                        string? cm = null;
                        if (child.Node.Tag == "li")
                        {
                            // Resolve list-style-type (CSS wins; else <ol type>; else UA default disc/decimal).
                            string type = child.Style.ListStyleType ?? OlTypeToCss(olTypeAttr) ?? (ol ? "decimal" : "disc");
                            if (type == "none") cm = null;
                            else if (IsOrderedType(type))
                            {
                                int v = AttrIntOr(child, "value", liCounter);
                                liCounter = v;
                                cm = FormatListCounter(v, type) + ".";
                                liCounter += olReversed ? -1 : 1;
                            }
                            else cm = type; // "disc" | "circle" | "square" → drawn as a vector bullet
                        }
                        var cbx = new LayoutBox(child);
                        box.Children.Add(cbx);
                        float childTop = cursorY;
                        LayoutBlock(child, contentX, cursorY, contentWidth, cbx, cm, posHost: childHost, floats: floats, availHeight: childAvailH);
                        // break-inside:avoid — if the box straddles a page boundary (but fits on one page), push it down.
                        if (child.Style.BreakInside == "avoid" && _pageGridH > 0)
                        {
                            float h = cbx.MarginBoxHeight;
                            if (h > 0 && h <= _pageGridH - _pageMarginTop)
                            {
                                int pTop = (int)Math.Floor(childTop / _pageGridH);
                                int pBot = (int)Math.Floor((childTop + h - 0.5f) / _pageGridH);
                                if (pBot > pTop)
                                {
                                    float target = (pTop + 1) * _pageGridH + _pageMarginTop;
                                    cbx.Translate(0, target - childTop);
                                    childTop = target;
                                }
                            }
                        }
                        cursorY = childTop + cbx.MarginBoxHeight;
                        // page-break-after: advance the flow to the top of the next page.
                        string ba4 = child.Style.BreakAfter;
                        if (ba4 == "always" || ba4 == "page" || ba4 == "left" || ba4 == "right") cursorY = NextPageTop(cursorY);
                        // position:relative offsets the box visually without disturbing the flow.
                        if (child.Style.Position == "relative")
                        {
                            float rdx = child.Style.Left ?? (child.Style.Right.HasValue ? -child.Style.Right.Value : 0f);
                            float rdy = child.Style.Top ?? (child.Style.Bottom.HasValue ? -child.Style.Bottom.Value : 0f);
                            if (rdx != 0 || rdy != 0) cbx.Translate(rdx, rdy);
                        }
                    }
                    else inlineBuf.Add(child);
                }
                FlushInline();
                cursorY = Math.Max(cursorY, floatBottom); // contain floats created in this block
            }

            if (multicol) cursorY = contentTop + RedistributeColumns(box, contentX, contentTop, mcColW, mcGap, mcN, cursorY - contentTop, s.ColumnRule);
            float contentHeight = cursorY - contentTop;
            float borderBoxHeight;
            if (forcedBorderBoxHeight.HasValue) borderBoxHeight = forcedBorderBoxHeight.Value;
            else if (selfDefH.HasValue)
            {
                // border-box: Height already includes padding+border (clamped ≥ padding+borders — the CSS-triangle
                // `height:0;border:…` case); content-box: add them.
                float targetBB = s.BoxSizing == "border-box" ? Math.Max(selfDefH.Value, bt + bb + s.PadTop + s.PadBottom) : selfDefH.Value + bt + bb + s.PadTop + s.PadBottom;
                // overflow:hidden/clip makes height a HARD limit (clip); otherwise it acts as min-height.
                borderBoxHeight = s.Overflow != "visible" ? targetBB : Math.Max(bt + s.PadTop + contentHeight + s.PadBottom + bb, targetBB);
            }
            else if (s.AspectRatio > 0f)
            {
                // aspect-ratio: derive the content height from the content width; content taller than that still grows.
                float aspectContentH = contentWidth / s.AspectRatio;
                borderBoxHeight = bt + s.PadTop + Math.Max(contentHeight, aspectContentH) + s.PadBottom + bb;
            }
            else borderBoxHeight = bt + s.PadTop + contentHeight + s.PadBottom + bb;

            // min-height / max-height clamp the border-box height (max-height caps; content may overflow visually).
            if (!forcedBorderBoxHeight.HasValue)
            {
                float hExtra = bt + bb + s.PadTop + s.PadBottom;
                float ToBB(float v) => s.BoxSizing == "border-box" ? v : v + hExtra;
                if (s.MaxHeight.HasValue) borderBoxHeight = Math.Min(borderBoxHeight, ToBB(s.MaxHeight.Value));
                if (s.MinHeight.HasValue) borderBoxHeight = Math.Max(borderBoxHeight, ToBB(s.MinHeight.Value));
            }

            if (!suppressDecor) ApplyBoxDecor(box, s, borderX, top, borderBoxWidth, borderBoxHeight, noBorder);

            box.BorderBoxWidth = borderBoxWidth;
            box.BorderBoxHeight = borderBoxHeight;
            box.BorderBoxX = borderX; box.BorderBoxY = top;
            box.Transform = s.TransformMatrix; box.TxOriginXFrac = s.TxOriginXFrac; box.TxOriginYFrac = s.TxOriginYFrac; box.Opacity = s.Opacity;
            box.MarginBoxHeight = s.MarginTop + borderBoxHeight + s.MarginBottom;

            // Post-pass: lay out this box's out-of-flow descendants now that its padding box is known.
            if (box.PendingAbs.Count > 0)
            {
                var cb = new Rect
                {
                    X = borderX + bl,
                    Y = top + bt,
                    Width = Math.Max(0, borderBoxWidth - bl - brw),
                    Height = Math.Max(0, borderBoxHeight - bt - bb),
                };
                foreach (var absNode in box.PendingAbs)
                {
                    var abx = new LayoutBox(absNode);
                    box.Children.Add(abx);
                    LayoutAbsolute(absNode, cb, abx);
                }
                box.PendingAbs.Clear();
            }
        }

        // ---- out-of-flow: position:absolute / fixed -----------------------------------------------

        private static void LayoutAbsolute(StyledNode node, Rect cb, LayoutBox box)
        {
            var s = node.Style;
            // Resolve %-based offsets against the containing block (left/right → width, top/bottom → height).
            float? left = s.Left ?? (s.LeftPct.HasValue ? cb.Width * s.LeftPct.Value / 100f : (float?)null);
            float? right = s.Right ?? (s.RightPct.HasValue ? cb.Width * s.RightPct.Value / 100f : (float?)null);
            float? topOff = s.Top ?? (s.TopPct.HasValue ? cb.Height * s.TopPct.Value / 100f : (float?)null);
            float? bottomOff = s.Bottom ?? (s.BottomPct.HasValue ? cb.Height * s.BottomPct.Value / 100f : (float?)null);
            float? forcedBW = null;
            if (!s.Width.HasValue && !s.WidthPercent.HasValue)
            {
                if (left.HasValue && right.HasValue)
                    forcedBW = Math.Max(0, cb.Width - left.Value - right.Value - s.MarginLeft - s.MarginRight);
                else
                    forcedBW = Math.Min(MeasureMaxContent(node), cb.Width); // shrink-to-fit
            }

            // top+bottom both set (no explicit height) → stretch the height to fill between them.
            float? forcedBH = null;
            if (!s.Height.HasValue && topOff.HasValue && bottomOff.HasValue)
                forcedBH = Math.Max(0, cb.Height - topOff.Value - bottomOff.Value - s.MarginTop - s.MarginBottom);

            // Provisional margin-edge origin (right/bottom anchors are resolved after sizing).
            float xMargin = left.HasValue ? cb.X + left.Value : cb.X;
            float yMargin = topOff.HasValue ? cb.Y + topOff.Value : cb.Y;

            // Pass the containing block's height so an absolutely-positioned box's own `height:%` resolves
            // (e.g. waterfall bars `height:55%` / data bars). Without it the % is indefinite → 0 height.
            LayoutBlock(node, xMargin, yMargin, cb.Width, box, null, forcedBW, forcedBH, posHost: box, availHeight: cb.Height);

            if (!left.HasValue && right.HasValue)
            {
                float finalBorderX = cb.X + cb.Width - right.Value - s.MarginRight - box.BorderBoxWidth;
                box.Translate(finalBorderX - (xMargin + s.MarginLeft), 0);
            }
            if (!topOff.HasValue && bottomOff.HasValue)
            {
                float finalBorderY = cb.Y + cb.Height - bottomOff.Value - s.MarginBottom - box.BorderBoxHeight;
                box.Translate(0, finalBorderY - (yMargin + s.MarginTop));
            }
            box.MarginBoxHeight = 0; // out of flow: contributes nothing to the parent's height
        }

        // ---- replaced element: <img> --------------------------------------------------------------

        private static void LayoutImage(StyledNode node, float x, float y, float availWidth, LayoutBox box, float? forcedBorderBoxWidth)
        {
            var s = node.Style;
            float bl = s.BorderLeft.Width, brw = s.BorderRight.Width, bt = s.BorderTop.Width, bb = s.BorderBottom.Width;
            float outerAvail = forcedBorderBoxWidth ?? Math.Max(0, availWidth - s.MarginLeft - s.MarginRight);
            float availContent = Math.Max(0, outerAvail - s.PadLeft - s.PadRight - bl - brw);

            // LaTeX-math placeholder (<img data-latex>): render to a bitmap now, using the computed font-size/colour,
            // and use the math metrics (pt) as the intrinsic size. The baseline drop aligns inline math to the text.
            HtmlPdfNative.Images.DecodedImage? dec; float? mathW = null, mathH = null;
            if (node.Node.Attributes.TryGetValue("data-latex", out var latex))
            {
                var mi = HtmlPdfNative.MathTex.MathRenderer.Render(latex, s.FontSizePt, s.Color,
                    node.Node.Attributes.ContainsKey("data-display"));
                if (mi != null) { dec = mi.Img; mathW = mi.WidthPt; mathH = mi.HeightPt + mi.DepthPt; s.AtomicBaselineDrop = mi.DepthPt; }
                else dec = null;
            }
            else
            {
                node.Node.Attributes.TryGetValue("src", out var src);
                dec = HtmlPdfNative.Images.ImageLoader.Load(src);
            }

            // Intrinsic size in pt (image px -> pt), with attribute fallbacks.
            float iw = mathW ?? (dec != null ? dec.Width * Lib.PxToPt : (AttrF(node, "width") ?? 100f) * Lib.PxToPt);
            float ih = mathH ?? (dec != null ? dec.Height * Lib.PxToPt : (AttrF(node, "height") ?? 100f) * Lib.PxToPt);
            if (iw <= 0) iw = 100; if (ih <= 0) ih = 100;

            float? cw = mathW != null ? (float?)null : s.Width;
            if (cw == null && s.WidthPercent.HasValue) cw = availContent * s.WidthPercent.Value / 100f;
            if (cw == null && AttrF(node, "width") is float aw) cw = aw * Lib.PxToPt;
            float? ch = s.Height ?? (AttrF(node, "height") is float ah ? ah * Lib.PxToPt : (float?)null);

            float dispW, dispH;
            if (cw.HasValue && ch.HasValue) { dispW = cw.Value; dispH = ch.Value; }
            else if (cw.HasValue) { dispW = cw.Value; dispH = ih * (cw.Value / iw); }
            else if (ch.HasValue) { dispH = ch.Value; dispW = iw * (ch.Value / ih); }
            else { dispW = iw; dispH = ih; }
            if (availContent > 0 && dispW > availContent) { float sc = availContent / dispW; dispW *= sc; dispH *= sc; }

            // margin:auto centres only a BLOCK-LEVEL replaced element (e.g. `.card-icon{display:block;margin:0 auto}`).
            // For an inline / inline-block svg/img (the SVG default display) auto margins compute to 0 — centring is
            // then the inline formatting context's job (e.g. the parent's text-align:center), so applying margin:auto
            // here as well would double-shift it right (Example17 `.stat-card{text-align:center} .icon{margin:0 auto}`).
            float mLeftS = s.MarginLeft;
            if (!forcedBorderBoxWidth.HasValue && IsBlockLevel(s.Display) && (s.MarginLeftAuto || s.MarginRightAuto))
            {
                float free = availWidth - (dispW + s.PadLeft + s.PadRight + bl + brw) - s.MarginLeft - s.MarginRight;
                if (free > 0.01f) { if (s.MarginLeftAuto && s.MarginRightAuto) mLeftS += free / 2f; else if (s.MarginLeftAuto) mLeftS += free; }
            }
            float borderX = x + mLeftS;
            float contentX = borderX + bl + s.PadLeft;
            float top = y + s.MarginTop;
            float contentTop = top + bt + s.PadTop;

            if (dec != null && s.Filter != null) dec = ApplyImageFilter(dec, s.Filter); // CSS filter (grayscale/…)
            if (dec != null)
            {
                // object-fit: when the box size differs from intrinsic, fit/position the image inside it (clipped).
                if (s.ObjectFit != "fill" && (Math.Abs(dispW - iw) > 0.5f || Math.Abs(dispH - ih) > 0.5f))
                {
                    float sc = s.ObjectFit switch
                    {
                        "contain" => Math.Min(dispW / iw, dispH / ih),
                        "cover" => Math.Max(dispW / iw, dispH / ih),
                        "none" => 1f,
                        "scale-down" => Math.Min(1f, Math.Min(dispW / iw, dispH / ih)),
                        _ => Math.Max(dispW / iw, dispH / ih),
                    };
                    float imgW = iw * sc, imgH = ih * sc;
                    var (fx, fy) = ParseBgPos(s.ObjectPosition);
                    float offX = (dispW - imgW) * fx, offY = (dispH - imgH) * fy;
                    box.Images.Add(new ImageDraw
                    {
                        X = contentX + offX, Y = contentTop + offY, Width = imgW, Height = imgH, Image = dec,
                        Clip = true, ClipX = contentX, ClipY = contentTop, ClipW = dispW, ClipH = dispH,
                    });
                }
                else box.Images.Add(new ImageDraw { X = contentX, Y = contentTop, Width = dispW, Height = dispH, Image = dec });

                // filter: drop-shadow — a blurred colour silhouette of the image, drawn behind it (inserted first).
                var ds = ParseDropShadow(s.Filter);
                if (ds != null && dispW > 0)
                {
                    var (dx, dy, blurPx, scol) = ds.Value;
                    int rpx = (int)Math.Round(Math.Max(0f, blurPx) * dec.Width * Lib.PxToPt / dispW);
                    var shadow = BuildImageDropShadow(dec, scol, Math.Min(60, rpx));
                    box.Images.Insert(0, new ImageDraw { X = contentX + dx * Lib.PxToPt, Y = contentTop + dy * Lib.PxToPt, Width = dispW, Height = dispH, Image = shadow });
                }
            }
            else
                box.Decorations.Add(new SolidRect { X = contentX, Y = contentTop, Width = dispW, Height = dispH, Color = new Color(224, 224, 224) });

            float borderBoxWidth = dispW + s.PadLeft + s.PadRight + bl + brw;
            float borderBoxHeight = bt + s.PadTop + dispH + s.PadBottom + bb;
            ApplyBoxDecor(box, s, borderX, top, borderBoxWidth, borderBoxHeight);
            box.BorderBoxWidth = borderBoxWidth;
            box.BorderBoxHeight = borderBoxHeight;
            box.BorderBoxX = borderX; box.BorderBoxY = top;
            box.Transform = s.TransformMatrix; box.TxOriginXFrac = s.TxOriginXFrac; box.TxOriginYFrac = s.TxOriginYFrac; box.Opacity = s.Opacity;
            box.MarginBoxHeight = s.MarginTop + borderBoxHeight + s.MarginBottom;
        }

        // ---- replaced element: inline <svg> -------------------------------------------------------

        private static void LayoutSvg(StyledNode node, float x, float y, float availWidth, LayoutBox box, float? forcedBorderBoxWidth)
        {
            var s = node.Style;
            float bl = s.BorderLeft.Width, brw = s.BorderRight.Width, bt = s.BorderTop.Width, bb = s.BorderBottom.Width;
            float outerAvail = forcedBorderBoxWidth ?? Math.Max(0, availWidth - s.MarginLeft - s.MarginRight);
            float availContent = Math.Max(0, outerAvail - s.PadLeft - s.PadRight - bl - brw);

            // viewBox gives the intrinsic aspect ratio; width/height attrs give the intrinsic size (px).
            float vbW = 0, vbH = 0;
            if (node.Node.Attributes.TryGetValue("viewBox", out var vb))
            {
                var p = vb.Replace(",", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 4) { float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out vbW); float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out vbH); }
            }
            float? attrW = AttrF(node, "width"); float? attrH = AttrF(node, "height");
            float iw = s.Width ?? (attrW.HasValue ? attrW.Value * Lib.PxToPt : (vbW > 0 ? vbW * Lib.PxToPt : 300f * Lib.PxToPt));
            float ih = s.Height ?? (attrH.HasValue ? attrH.Value * Lib.PxToPt : (vbH > 0 ? vbH * Lib.PxToPt : 150f * Lib.PxToPt));

            float? cw = s.Width ?? (s.WidthPercent.HasValue ? availContent * s.WidthPercent.Value / 100f : (attrW.HasValue ? attrW.Value * Lib.PxToPt : (float?)null));
            float? ch = s.Height ?? (attrH.HasValue ? attrH.Value * Lib.PxToPt : (float?)null);
            float aspect = (vbW > 0 && vbH > 0) ? vbW / vbH : (ih > 0 ? iw / ih : 2f);

            float dispW, dispH;
            if (cw.HasValue && ch.HasValue) { dispW = cw.Value; dispH = ch.Value; }
            else if (cw.HasValue) { dispW = cw.Value; dispH = cw.Value / aspect; }
            else if (ch.HasValue) { dispH = ch.Value; dispW = ch.Value * aspect; }
            else { dispW = iw; dispH = ih; }
            if (availContent > 0 && dispW > availContent) { float sc = availContent / dispW; dispW *= sc; dispH *= sc; }

            // margin:auto centres only a BLOCK-LEVEL replaced element (e.g. `.card-icon{display:block;margin:0 auto}`).
            // For an inline / inline-block svg/img (the SVG default display) auto margins compute to 0 — centring is
            // then the inline formatting context's job (e.g. the parent's text-align:center), so applying margin:auto
            // here as well would double-shift it right (Example17 `.stat-card{text-align:center} .icon{margin:0 auto}`).
            float mLeftS = s.MarginLeft;
            if (!forcedBorderBoxWidth.HasValue && IsBlockLevel(s.Display) && (s.MarginLeftAuto || s.MarginRightAuto))
            {
                float free = availWidth - (dispW + s.PadLeft + s.PadRight + bl + brw) - s.MarginLeft - s.MarginRight;
                if (free > 0.01f) { if (s.MarginLeftAuto && s.MarginRightAuto) mLeftS += free / 2f; else if (s.MarginLeftAuto) mLeftS += free; }
            }
            float borderX = x + mLeftS;
            float contentX = borderX + bl + s.PadLeft;
            float top = y + s.MarginTop;
            float contentTop = top + bt + s.PadTop;

            // Map each SVG descendant DOM node → its cascaded style (fill/stroke from class/tag rules) so the painter
            // can honour stylesheet styling it can't compute itself; pass the custom-property map for var() in attrs.
            var svgStyles = new Dictionary<Dom.Node, ComputedStyle>();
            CollectStyledMap(node, svgStyles);
            box.Svgs.Add(new SvgDraw { X = contentX, Y = contentTop, Width = dispW, Height = dispH, Svg = node.Node, Styles = svgStyles, Vars = s.Vars });

            float borderBoxWidth = dispW + s.PadLeft + s.PadRight + bl + brw;
            float borderBoxHeight = bt + s.PadTop + dispH + s.PadBottom + bb;
            ApplyBoxDecor(box, s, borderX, top, borderBoxWidth, borderBoxHeight);
            box.BorderBoxWidth = borderBoxWidth;
            box.BorderBoxHeight = borderBoxHeight;
            box.BorderBoxX = borderX; box.BorderBoxY = top;
            box.Transform = s.TransformMatrix; box.TxOriginXFrac = s.TxOriginXFrac; box.TxOriginYFrac = s.TxOriginYFrac; box.Opacity = s.Opacity;
            box.MarginBoxHeight = s.MarginTop + borderBoxHeight + s.MarginBottom;
        }

        private static float? AttrF(StyledNode node, string attr)
        {
            if (node.Node.Attributes.TryGetValue(attr, out var v))
            {
                var t = v.Trim().Replace("px", "");
                if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) return f;
            }
            return null;
        }

        /// <summary>Displays that participate in the parent's block flow as block-level boxes.</summary>
        private static bool IsBlockLevel(string display) =>
            display == "block" || display == "flex" || display == "grid" || display == "table" || display == "list-item" || display == "flow-root";

        // ---- flex (row) ---------------------------------------------------------------------------

        // Collect flex items: each in-flow ELEMENT child is a (blockified) item; contiguous non-whitespace TEXT runs
        // are wrapped in an anonymous block flex item (so `display:flex` on a box with bare text still renders + centers it).
        private static void CollectFlexItems(StyledNode node, List<StyledNode> allItems, LayoutBox? posHost)
        {
            StyledNode? anon = null;
            foreach (var c in node.Children)
            {
                if (c.IsText)
                {
                    if (string.IsNullOrWhiteSpace(c.Node.Text)) { anon?.Children.Add(c); continue; }
                    if (anon == null) { anon = MakeAnonFlexItem(node); allItems.Add(anon); }
                    anon.Children.Add(c);
                }
                else if (c.Style.IsOutOfFlow) posHost?.PendingAbs.Add(c);
                else { anon = null; allItems.Add(c); }
            }
        }

        private static StyledNode MakeAnonFlexItem(StyledNode parent)
        {
            var dn = new Dom.Node { Tag = "div", Parent = parent.Node };
            var p = parent.Style;
            var st = new ComputedStyle { Display = "block", FontFamily = p.FontFamily, FontSizePt = p.FontSizePt, Color = p.Color, TextAlign = p.TextAlign, LineHeightPt = p.LineHeightPt, LineHeightMul = p.LineHeightMul, Bold = p.Bold, Italic = p.Italic };
            return StyledNode.CreateAnonymous(dn, st);
        }

        private static float LayoutFlexRow(StyledNode node, LayoutBox box, float x, float top, float width, LayoutBox? posHost = null, float? contentHeight = null)
        {
            var allItems = new List<StyledNode>();
            CollectFlexItems(node, allItems, posHost);
            if (allItems.Count == 0) return 0f;

            float gap = node.Style.Gap;
            bool wrap = node.Style.FlexWrap == "wrap" || node.Style.FlexWrap == "wrap-reverse";
            // Single line: the cross size is the container's definite CONTENT height (border-box minus padding/border —
            // NOT Style.Height, else the padding is double-counted and the container balloons past its set height).
            if (!wrap) return LayoutFlexLine(node, box, allItems, x, top, width, posHost, contentHeight);

            // flex-wrap: partition items into lines by their base main-size.
            float rowGap = node.Style.RowGap;
            var lines = new List<List<StyledNode>>();
            var cur = new List<StyledNode>(); float lineW = 0f;
            foreach (var it in allItems)
            {
                float bw = FlexBaseW(it, width);
                float add = (cur.Count > 0 ? gap : 0f) + bw;
                if (cur.Count > 0 && lineW + add > width + 0.01f) { lines.Add(cur); cur = new List<StyledNode>(); lineW = 0f; }
                cur.Add(it); lineW += (cur.Count > 1 ? gap : 0f) + bw;
            }
            if (cur.Count > 0) lines.Add(cur);

            // Lay each line, recording the child-index range it produced (for align-content repositioning).
            float y = top;
            var ranges = new List<(int start, int end)>();
            for (int li = 0; li < lines.Count; li++)
            {
                if (li > 0) y += rowGap;
                int startIdx = box.Children.Count;
                y += LayoutFlexLine(node, box, lines[li], x, y, width, posHost);
                ranges.Add((startIdx, box.Children.Count));
            }
            float total = y - top;

            // align-content: distribute leftover cross space among lines when the container height is definite.
            float? ch = node.Style.Height;
            string ac = node.Style.AlignContent;
            if (ch.HasValue && ch.Value > total + 0.01f && lines.Count > 0 && ac != "stretch" && ac != "normal")
            {
                float leftover = ch.Value - total;
                float offset0 = 0f, between = 0f;
                switch (ac)
                {
                    case "center": offset0 = leftover / 2f; break;
                    case "flex-end": case "end": offset0 = leftover; break;
                    case "space-between": if (lines.Count > 1) between = leftover / (lines.Count - 1); break;
                    case "space-around": between = leftover / lines.Count; offset0 = between / 2f; break;
                    case "space-evenly": between = leftover / (lines.Count + 1); offset0 = between; break;
                }
                for (int li = 0; li < ranges.Count; li++)
                {
                    float dy = offset0 + li * between;
                    if (dy != 0f) for (int k = ranges[li].start; k < ranges[li].end; k++) box.Children[k].Translate(0, dy);
                }
            }
            return total; // the container's definite height (if any) is applied by LayoutBlock via s.Height
        }

        /// <summary>Base (pre-grow/shrink) main size of a flex item, border-box, for wrapping decisions.</summary>
        private static float FlexBaseW(StyledNode item, float width)
        {
            var s = item.Style;
            float extra = s.PadLeft + s.PadRight + s.BorderLeft.Width + s.BorderRight.Width;
            // border-box width can't shrink below its padding+borders (CSS-triangle `width:0;border:…` items).
            if (s.Width.HasValue) return s.BoxSizing == "border-box" ? Math.Max(s.Width.Value, extra) : s.Width.Value + extra;
            // A percentage width resolves the flex-basis against the container's inner width (else the item falls to
            // max-content and a `width:58%` sibling wrongly steals space — Example28 header, Example30 sidebar).
            if (s.WidthPercent.HasValue) { float pw = width * s.WidthPercent.Value / 100f; return s.BoxSizing == "border-box" ? pw : pw + extra; }
            if (s.FlexBasis != "auto" && s.FlexBasis != "0" && Values.LengthPt(s.FlexBasis, s.FontSizePt, width) is float fb) return s.BoxSizing == "border-box" ? fb : fb + extra;
            if (s.FlexGrow > 0 || s.FlexBasis == "0") return extra;
            // +0.5pt cushion: max-content and the line-breaker measure CJK advances / inter-word spaces with slightly
            // different float accumulation, so an exactly-max-content-wide item can wrap its last ideograph by a
            // fraction of a point. The cushion guarantees a content-sized item actually fits its content.
            return Math.Min(width, MeasureMaxContent(item) + extra + 0.5f);
        }

        /// <summary>Lay out one flex line (a row of items) at (x, top) within <paramref name="width"/>; returns its height.</summary>
        private static float LayoutFlexLine(StyledNode node, LayoutBox box, List<StyledNode> items, float x, float top, float width, LayoutBox? posHost, float? crossHeight = null)
        {
            // row-reverse: reverse the visual order and swap the flex-start/flex-end packing edge.
            bool reverse = node.Style.FlexDirection == "row-reverse";
            if (reverse) { items = new List<StyledNode>(items); items.Reverse(); }
            int n = items.Count;
            float gap = node.Style.Gap;
            float totalGap = gap * (n - 1);

            // Base main sizes (border-box) + flex factors.
            var baseW = new float[n];
            var grow = new float[n];
            var shrink = new float[n];
            float sumBase = 0f, sumGrow = 0f;
            for (int i = 0; i < n; i++)
            {
                baseW[i] = FlexBaseW(items[i], width);
                grow[i] = items[i].Style.FlexGrow; shrink[i] = items[i].Style.FlexShrink;
                sumBase += baseW[i]; sumGrow += items[i].Style.FlexGrow;
            }

            float free = width - sumBase - totalGap;
            var finalW = new float[n];
            bool grewToFill = false;
            if (free > 0 && sumGrow > 0)
            {
                for (int i = 0; i < n; i++) finalW[i] = baseW[i] + free * grow[i] / sumGrow;
                grewToFill = true;
            }
            else if (free < 0)
            {
                float sumShrinkBase = 0f; for (int i = 0; i < n; i++) sumShrinkBase += shrink[i] * baseW[i];
                for (int i = 0; i < n; i++)
                {
                    float min = items[i].Style.PadLeft + items[i].Style.PadRight + items[i].Style.BorderLeft.Width + items[i].Style.BorderRight.Width;
                    finalW[i] = sumShrinkBase > 0 ? Math.Max(min, baseW[i] + free * (shrink[i] * baseW[i]) / sumShrinkBase) : baseW[i];
                }
            }
            else { for (int i = 0; i < n; i++) finalW[i] = baseW[i]; }

            // Main-axis positions via justify-content (only when there is leftover and no grow filled it).
            float used = totalGap; for (int i = 0; i < n; i++) used += finalW[i];
            float leftover = Math.Max(0, width - used);
            float startX = x, betweenExtra = 0f;
            if (!grewToFill && leftover > 0.01f)
            {
                // In row-reverse, flex-start packs at the main-start (right edge) and flex-end at the left.
                string jc = node.Style.JustifyContent;
                if (reverse)
                {
                    if (jc == "flex-end" || jc == "end" || jc == "right") jc = "flex-start";
                    else if (jc == "flex-start" || jc == "start" || jc == "left" || string.IsNullOrEmpty(jc) || jc == "normal") jc = "flex-end";
                }
                switch (jc)
                {
                    case "center": startX = x + leftover / 2f; break;
                    case "flex-end": case "end": case "right": startX = x + leftover; break;
                    case "space-between": if (n > 1) betweenExtra = leftover / (n - 1); break;
                    case "space-around": betweenExtra = leftover / n; startX = x + betweenExtra / 2f; break;
                    case "space-evenly": betweenExtra = leftover / (n + 1); startX = x + betweenExtra; break;
                }
            }

            // Pass 1: lay each item out at its width; track max height.
            var itemBoxes = new LayoutBox[n];
            float cursorX = startX;
            float maxH = 0f;
            for (int i = 0; i < n; i++)
            {
                var ib = new LayoutBox(items[i]);
                itemBoxes[i] = ib;
                // Pass the container's definite content height so a flex item's own `height:%` resolves (e.g. the
                // color-scale/vertical bars) — without it the % is indefinite and the item collapses to 0/min-height.
                LayoutBlock(items[i], cursorX, top, finalW[i], ib, null, forcedBorderBoxWidth: finalW[i], posHost: posHost, availHeight: crossHeight);
                maxH = Math.Max(maxH, ib.BorderBoxHeight);
                cursorX += finalW[i] + gap + betweenExtra;
            }

            // Cross size for alignment = the container's definite height (if larger) else the tallest item.
            float crossH = crossHeight.HasValue && crossHeight.Value > maxH ? crossHeight.Value : maxH;

            // Pass 2: cross-axis alignment (align-items, overridable per item by align-self).
            for (int i = 0; i < n; i++)
            {
                string align = EffectiveAlign(items[i], node.Style.AlignItems);
                if (align == "stretch" && !items[i].Style.Height.HasValue)
                {
                    var ib = new LayoutBox(items[i]);
                    itemBoxes[i] = ib;
                    // recompute x for this item
                    float ix = startX; for (int k = 0; k < i; k++) ix += finalW[k] + gap + betweenExtra;
                    LayoutBlock(items[i], ix, top, finalW[i], ib, null, forcedBorderBoxWidth: finalW[i], forcedBorderBoxHeight: crossH, posHost: posHost);
                }
                else
                {
                    float h = itemBoxes[i].BorderBoxHeight;
                    float dy = align == "center" ? (crossH - h) / 2f : (align == "flex-end" || align == "end") ? (crossH - h) : 0f;
                    if (dy != 0f) itemBoxes[i].Translate(0, dy);
                }
                box.Children.Add(itemBoxes[i]);
            }
            return crossH;
        }

        /// <summary>The effective cross-axis alignment for a flex/grid item: align-self overrides align-items.</summary>
        private static string EffectiveAlign(StyledNode item, string containerAlign) =>
            item.Style.AlignSelf != "auto" && item.Style.AlignSelf.Length > 0 ? item.Style.AlignSelf : containerAlign;

        // ---- multi-column -------------------------------------------------------------------------

        /// <summary>Redistribute a block's already-laid content (text lines + child blocks, flowed tall at one
        /// column's width) into <paramref name="n"/> balanced columns. Returns the tallest column's height.</summary>
        private static float RedistributeColumns(LayoutBox box, float contentX, float contentTop, float colW, float gap, int n, float totalH, BorderEdge rule)
        {
            // Flow items: each text LINE (TextRuns sharing a baseline) and each child block, in top-to-bottom order.
            var items = new List<(float yTop, float h, List<TextRun>? runs, LayoutBox? child)>();
            var byBaseline = new SortedDictionary<float, List<TextRun>>();
            foreach (var t in box.TextRuns) { float k = (float)Math.Round(t.BaselineY, 1); if (!byBaseline.TryGetValue(k, out var l)) byBaseline[k] = l = new List<TextRun>(); l.Add(t); }
            var baselines = new List<float>(byBaseline.Keys);
            for (int i = 0; i < baselines.Count; i++)
            {
                float bl = baselines[i];
                float top = i > 0 ? (baselines[i - 1] + bl) / 2f : bl - (baselines.Count > 1 ? (baselines[1] - bl) : 12f);
                float h = i + 1 < baselines.Count ? baselines[i + 1] - bl : (i > 0 ? bl - baselines[i - 1] : 14f);
                items.Add((bl, h, byBaseline[bl], null));
            }
            foreach (var c in box.Children) items.Add((c.BorderBoxY, c.MarginBoxHeight, null, c));
            items.Sort((a, b) => a.yTop.CompareTo(b.yTop));
            if (items.Count == 0) return totalH;

            float target = totalH / n;
            var colH = new float[n];
            int col = 0;
            foreach (var (yTop, h, runs, child) in items)
            {
                if (col < n - 1 && colH[col] > 0.5f && colH[col] + h > target + 0.01f) col++;
                float dx = col * (colW + gap);
                float dy = (contentTop + colH[col]) - yTop;
                if (runs != null) foreach (var t in runs) { t.X += dx; t.BaselineY += dy; }
                child?.Translate(dx, dy);
                colH[col] += h;
            }
            float maxH = 0f; foreach (var v in colH) maxH = Math.Max(maxH, v);

            // column-rule: a vertical line centred in each gap, spanning the tallest column (styled like a border).
            if (rule.Width > 0 && rule.Color.A > 0)
            {
                string rst = rule.Style ?? "solid";
                for (int c = 1; c < n; c++)
                {
                    float gx = contentX + c * (colW + gap) - gap / 2f - rule.Width / 2f;
                    if (rst == "dashed" || rst == "dotted")
                    {
                        float t = rule.Width, seg = rst == "dotted" ? t : t * 2f, g = rst == "dotted" ? t : t * 1.5f;
                        for (float o = 0; o < maxH - 0.1f; o += seg + g)
                            box.Decorations.Add(new SolidRect { X = gx, Y = contentTop + o, Width = rule.Width, Height = Math.Min(seg, maxH - o), Color = rule.Color });
                    }
                    else if (rst == "double" && rule.Width >= 3f)
                    {
                        float t = rule.Width / 3f;
                        box.Decorations.Add(new SolidRect { X = gx, Y = contentTop, Width = t, Height = maxH, Color = rule.Color });
                        box.Decorations.Add(new SolidRect { X = gx + rule.Width - t, Y = contentTop, Width = t, Height = maxH, Color = rule.Color });
                    }
                    else box.Decorations.Add(new SolidRect { X = gx, Y = contentTop, Width = rule.Width, Height = maxH, Color = rule.Color });
                }
            }
            return maxH;
        }

        // ---- flex (column) ------------------------------------------------------------------------

        private static float LayoutFlexColumn(StyledNode node, LayoutBox box, float x, float top, float width, LayoutBox? posHost)
        {
            var items = new List<StyledNode>();
            CollectFlexItems(node, items, posHost);
            if (items.Count == 0) return 0f;
            // column-reverse: stack items bottom-to-top (reverse the order; they still flow downward from `top`).
            if (node.Style.FlexDirection == "column-reverse") items.Reverse();

            int n = items.Count;
            float mainGap = node.Style.RowGap;   // vertical gap (gap shorthand sets RowGap too)
            float totalGap = mainGap * (n - 1);

            // Cross size (width) per item: stretch → full container width; else explicit/content width (border-box).
            var crossW = new float[n];
            for (int i = 0; i < n; i++)
            {
                var s = items[i].Style;
                float extra = s.PadLeft + s.PadRight + s.BorderLeft.Width + s.BorderRight.Width;
                if (s.Width.HasValue) crossW[i] = s.BoxSizing == "border-box" ? s.Width.Value : s.Width.Value + extra;
                else if (s.WidthPercent.HasValue) crossW[i] = width * s.WidthPercent.Value / 100f;
                else if (EffectiveAlign(items[i], node.Style.AlignItems) == "stretch") crossW[i] = width;
                else crossW[i] = Math.Min(width, MeasureMaxContent(items[i])); // already border-box (padding included)
            }

            // Pass 1: natural main size (height) at the cross width.
            var boxes = new LayoutBox[n];
            var mainH = new float[n];
            float sumH = 0f;
            for (int i = 0; i < n; i++)
            {
                var ib = new LayoutBox(items[i]);
                LayoutBlock(items[i], x, top, crossW[i], ib, null, forcedBorderBoxWidth: crossW[i], posHost: posHost);
                mainH[i] = ib.BorderBoxHeight; sumH += mainH[i]; boxes[i] = ib;
            }

            // Definite container height → distribute vertical free space by flex-grow/shrink (re-lay at forced height).
            var finalH = (float[])mainH.Clone();
            float? containerH = node.Style.Height;
            bool grew = false;
            if (containerH.HasValue)
            {
                float free = containerH.Value - sumH - totalGap;
                float sumGrow = 0f, sumShrink = 0f;
                for (int i = 0; i < n; i++) { sumGrow += items[i].Style.FlexGrow; sumShrink += items[i].Style.FlexShrink * mainH[i]; }
                if (free > 0 && sumGrow > 0)
                {
                    for (int i = 0; i < n; i++) finalH[i] = mainH[i] + free * items[i].Style.FlexGrow / sumGrow;
                    grew = true;
                }
                else if (free < 0 && sumShrink > 0)
                    for (int i = 0; i < n; i++) finalH[i] = Math.Max(0, mainH[i] + free * (items[i].Style.FlexShrink * mainH[i]) / sumShrink);
                for (int i = 0; i < n; i++)
                    if (Math.Abs(finalH[i] - mainH[i]) > 0.5f)
                    {
                        var ib = new LayoutBox(items[i]);
                        LayoutBlock(items[i], x, top, crossW[i], ib, null, forcedBorderBoxWidth: crossW[i], forcedBorderBoxHeight: finalH[i], posHost: posHost);
                        boxes[i] = ib; finalH[i] = ib.BorderBoxHeight;
                    }
            }

            // Main-axis positions (vertical) via justify-content, when the container has a definite height + leftover.
            float usedH = totalGap; for (int i = 0; i < n; i++) usedH += finalH[i];
            float leftover = containerH.HasValue ? Math.Max(0, containerH.Value - usedH) : 0f;
            float startY = top, betweenExtra = 0f;
            if (!grew && leftover > 0.01f)
                switch (node.Style.JustifyContent)
                {
                    case "center": startY = top + leftover / 2f; break;
                    case "flex-end": case "end": startY = top + leftover; break;
                    case "space-between": if (n > 1) betweenExtra = leftover / (n - 1); break;
                    case "space-around": betweenExtra = leftover / n; startY = top + betweenExtra / 2f; break;
                    case "space-evenly": betweenExtra = leftover / (n + 1); startY = top + betweenExtra; break;
                }

            float cy = startY;
            for (int i = 0; i < n; i++)
            {
                float dy = cy - top;                 // move from its laid position (top) to the flex slot
                string ia = EffectiveAlign(items[i], node.Style.AlignItems);
                float dx = ia == "center" ? (width - crossW[i]) / 2f : (ia == "flex-end" || ia == "end") ? (width - crossW[i]) : 0f;
                if (dx != 0f || dy != 0f) boxes[i].Translate(dx, dy);
                box.Children.Add(boxes[i]);
                cy += finalH[i] + mainGap + betweenExtra;
            }
            return containerH ?? (usedH);
        }

        // ---- grid ---------------------------------------------------------------------------------

        private struct Track { public char Kind; public float Val; public float Min; } // 'p'x, 'f'r, '%', 'a'uto; Min = px floor (minmax)

        private static float LayoutGrid(StyledNode node, LayoutBox box, float x, float top, float width, LayoutBox? posHost = null, float? definiteContentH = null)
        {
            var items = new List<StyledNode>();
            CollectFlexItems(node, items, posHost);
            if (items.Count == 0) return 0f;

            float colGap = node.Style.Gap, rowGap = node.Style.RowGap;
            var tracks = ParseTracks(node.Style.GridTemplateColumns, width, colGap);
            if (tracks.Count == 0) tracks.Add(new Track { Kind = 'f', Val = 1 }); // default single 1fr
            int cols = tracks.Count;

            // Auto-flow (row) with an occupancy grid so col-span AND row-span items reserve their cells.
            int n = items.Count;
            var rowOf = new int[n]; var startOf = new int[n]; var spanOf = new int[n]; var rspanOf = new int[n];
            var occupied = new HashSet<long>();
            long Key(int r, int c) => (long)r * 100000 + c;
            bool Free(int r, int c, int rs, int cs) { for (int rr = r; rr < r + rs; rr++) for (int cc = c; cc < c + cs; cc++) if (occupied.Contains(Key(rr, cc))) return false; return true; }
            int curCol = 0, curRow = 0;
            // Named grid lines: [a] 1fr [b] … → name→1-based line, so grid-column: a / b resolves to line numbers.
            var colNames = ParseLineNames(node.Style.GridTemplateColumns);
            var rowNames = ParseLineNames(node.Style.GridTemplateRows);
            for (int i = 0; i < n; i++)
            {
                var st = items[i].Style;
                int cs = Math.Max(1, st.GridColumnSpan);
                int rs = Math.Max(1, st.GridRowSpan);
                // Resolve named start/end lines into an explicit start + span.
                int? gcStart = st.GridColStart;
                if (!gcStart.HasValue && st.GridColStartName != null && colNames.TryGetValue(st.GridColStartName, out var csl)) gcStart = csl;
                if (st.GridColEndName != null && colNames.TryGetValue(st.GridColEndName, out var cel) && gcStart.HasValue && cel > gcStart.Value) cs = cel - gcStart.Value;
                int? grStart = st.GridRowStart;
                if (!grStart.HasValue && st.GridRowStartName != null && rowNames.TryGetValue(st.GridRowStartName, out var rsl)) grStart = rsl;
                if (st.GridRowEndName != null && rowNames.TryGetValue(st.GridRowEndName, out var rel) && grStart.HasValue && rel > grStart.Value) rs = rel - grStart.Value;
                cs = Math.Min(cs, cols);
                int? fixedCol = gcStart.HasValue ? Math.Max(0, Math.Min(cols - cs, gcStart.Value - 1)) : (int?)null;
                int? fixedRow = grStart.HasValue ? Math.Max(0, grStart.Value - 1) : (int?)null;
                int r, c;
                if (fixedCol.HasValue && fixedRow.HasValue) { r = fixedRow.Value; c = fixedCol.Value; }
                else if (fixedCol.HasValue) { c = fixedCol.Value; r = 0; while (!Free(r, c, rs, cs)) r++; }
                else if (fixedRow.HasValue) { r = fixedRow.Value; c = 0; while (c + cs <= cols && !Free(r, c, rs, cs)) c++; if (c + cs > cols) c = 0; }
                else // auto-flow
                {
                    while (true)
                    {
                        if (curCol + cs > cols) { curRow++; curCol = 0; }
                        if (Free(curRow, curCol, rs, cs)) break;
                        curCol++;
                    }
                    r = curRow; c = curCol; curCol += cs;
                }
                rowOf[i] = r; startOf[i] = c; spanOf[i] = cs; rspanOf[i] = rs;
                for (int rr = r; rr < r + rs; rr++) for (int cc = c; cc < c + cs; cc++) occupied.Add(Key(rr, cc));
            }
            int rows = 0; for (int i = 0; i < n; i++) rows = Math.Max(rows, rowOf[i] + rspanOf[i]);

            // Auto/min/max-content column sizes: max border-box max-content over the single-column items in each column.
            var autoContent = new float[cols];
            for (int i = 0; i < n; i++)
                if (spanOf[i] == 1 && startOf[i] < cols)
                {
                    var it = items[i].Style;
                    // MeasureMaxContent already returns the border-box width (padding+border included) — add only the
                    // item's margins, NOT its padding again (double-counting padding stretched a `1fr auto` badge wide).
                    float cw = MeasureMaxContent(items[i]) + it.MarginLeft + it.MarginRight;
                    autoContent[startOf[i]] = Math.Max(autoContent[startOf[i]], cw);
                }
            float[] colW = ResolveTracks(tracks, width, colGap, autoContent);
            // repeat(auto-fit, …): empty (unoccupied) tracks collapse to 0 so the filled items STRETCH to fill the row
            // (unlike auto-fill, which keeps the empty tracks). Items auto-flow from column 0, so occupied = first k.
            if ((node.Style.GridTemplateColumns ?? "").IndexOf("auto-fit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int k = 0; for (int i = 0; i < n; i++) k = Math.Max(k, startOf[i] + spanOf[i]);
                if (k > 0 && k < cols)
                {
                    var sub = tracks.GetRange(0, k);
                    var subAuto = new float[k]; System.Array.Copy(autoContent, subAuto, k);
                    var subW = ResolveTracks(sub, width, colGap, subAuto);
                    for (int c = 0; c < cols; c++) colW[c] = c < k ? subW[c] : 0f;
                }
            }
            float[] colX = new float[cols];
            float acc = x;
            for (int c = 0; c < cols; c++) { colX[c] = acc; acc += colW[c]; if (colW[c] > 0.1f) acc += colGap; }

            float ItemX(int i) => colX[startOf[i]];
            float ItemW(int i)
            {
                float w = 0f; for (int c = startOf[i]; c < startOf[i] + spanOf[i]; c++) w += colW[c];
                return w + (spanOf[i] - 1) * colGap;
            }

            // Pass 1: natural heights (measured at the item's column-span width).
            var probeH = new float[n];
            for (int i = 0; i < n; i++)
            {
                var probe = new LayoutBox(items[i]);
                LayoutBlock(items[i], ItemX(i), top, ItemW(i), probe, null, forcedBorderBoxWidth: ItemW(i));
                probeH[i] = probe.BorderBoxHeight;
            }
            // Row heights: single-row items set their row; row-span items top up their range; explicit
            // grid-template-rows px tracks override.
            var rowH = new float[rows];
            for (int i = 0; i < n; i++) if (rspanOf[i] == 1) rowH[rowOf[i]] = Math.Max(rowH[rowOf[i]], probeH[i]);
            for (int i = 0; i < n; i++) if (rspanOf[i] > 1)
            {
                float have = rowGap * (rspanOf[i] - 1);
                for (int r = rowOf[i]; r < rowOf[i] + rspanOf[i]; r++) have += rowH[r];
                if (probeH[i] > have) { float add = (probeH[i] - have) / rspanOf[i]; for (int r = rowOf[i]; r < rowOf[i] + rspanOf[i]; r++) rowH[r] += add; }
            }
            if (node.Style.GridTemplateRows != null)
            {
                var rtracks = ParseTracks(node.Style.GridTemplateRows);
                // px + % rows are fixed against the container's definite height; fr rows share the leftover.
                float? containerH = node.Style.Height;
                int rt = Math.Min(rows, rtracks.Count);
                float fixedSum = 0f, frSum = 0f;
                for (int r = 0; r < rt; r++)
                {
                    if (rtracks[r].Kind == 'p' && rtracks[r].Val > 0) { rowH[r] = rtracks[r].Val; fixedSum += rowH[r]; }
                    else if (rtracks[r].Kind == '%' && containerH.HasValue) { rowH[r] = containerH.Value * rtracks[r].Val / 100f; fixedSum += rowH[r]; }
                    else if (rtracks[r].Kind == 'f') frSum += rtracks[r].Val;
                    else fixedSum += rowH[r]; // auto/measured row keeps its content height
                }
                if (frSum > 0 && containerH.HasValue)
                {
                    float leftover = Math.Max(0f, containerH.Value - fixedSum - rowGap * (rows - 1));
                    float frUnit = leftover / frSum;
                    for (int r = 0; r < rt; r++) if (rtracks[r].Kind == 'f') rowH[r] = rtracks[r].Val * frUnit;
                }
            }
            // align-content: normal/stretch on a grid with a DEFINITE height distributes the extra space across the
            // AUTO rows (Chrome default) — so a single-row grid (e.g. donut ::after `place-items:center`) fills its
            // height and align-items:center actually centres the item. Skip when packed (start/center/end/space-*).
            string ac = node.Style.AlignContent;
            bool acStretch = string.IsNullOrEmpty(ac) || ac == "normal" || ac == "stretch";
            if (definiteContentH.HasValue && acStretch && rows > 0)
            {
                float curH = 0f; for (int r = 0; r < rows; r++) curH += rowH[r]; curH += rowGap * (rows - 1);
                float extra = definiteContentH.Value - curH;
                if (extra > 0.5f) { float per = extra / rows; for (int r = 0; r < rows; r++) rowH[r] += per; }
            }
            var rowY = new float[rows];
            float yacc = top;
            for (int r = 0; r < rows; r++) { rowY[r] = yacc; yacc += rowH[r] + rowGap; }
            float RowSpanH(int i) { float h = rowGap * (rspanOf[i] - 1); for (int r = rowOf[i]; r < rowOf[i] + rspanOf[i]; r++) h += rowH[r]; return h; }

            // Pass 2: place, stretching to the item's (row-span) height (align-items default stretch; align-self per item).
            var itemBox = new LayoutBox[n];
            for (int i = 0; i < n; i++)
            {
                string align = EffectiveAlign(items[i], node.Style.AlignItems);
                bool stretch = align == "stretch";
                float cellH = RowSpanH(i);
                float cellW = ItemW(i);
                // justify-items / justify-self (inline axis): non-stretch shrinks the item to content and offsets it.
                string jself = items[i].Style.JustifySelf;
                string jalign = jself != "auto" && jself.Length > 0 ? jself : node.Style.JustifyItems;
                bool stretchAlign = jalign == "stretch" || jalign == "normal" || string.IsNullOrEmpty(jalign);
                var js = items[i].Style;
                bool hasW = js.Width.HasValue || js.WidthPercent.HasValue;
                // An item with an EXPLICIT width uses that width (never stretched to the cell) and is placed per its
                // justify alignment; a stretch item with no width fills the cell; else it shrinks to max-content.
                float itemW; float dx = 0f;
                if (hasW)
                {
                    float ew = js.Width ?? js.WidthPercent!.Value / 100f * cellW;
                    if (js.BoxSizing != "border-box") ew += js.PadLeft + js.PadRight + js.BorderLeft.Width + js.BorderRight.Width;
                    itemW = Math.Min(cellW, ew);
                }
                else if (stretchAlign) itemW = cellW;
                else itemW = Math.Min(cellW, MeasureMaxContent(items[i]));
                if (itemW < cellW)
                    dx = jalign == "center" ? (cellW - itemW) / 2f : (jalign == "end" || jalign == "flex-end" || jalign == "right") ? (cellW - itemW) : 0f;
                var ib = new LayoutBox(items[i]);
                float? fh = stretch && !items[i].Style.Height.HasValue ? (float?)cellH : null;
                LayoutBlock(items[i], ItemX(i) + dx, rowY[rowOf[i]], itemW, ib, null, forcedBorderBoxWidth: itemW, forcedBorderBoxHeight: fh, posHost: posHost);
                if (!stretch)
                {
                    float dy = align == "center" ? (cellH - ib.BorderBoxHeight) / 2f
                             : (align == "flex-end" || align == "end") ? (cellH - ib.BorderBoxHeight) : 0f;
                    if (dy != 0f) ib.Translate(0, dy);
                }
                itemBox[i] = ib;
                box.Children.Add(ib);
            }

            float totalH = 0f; for (int r = 0; r < rows; r++) totalH += rowH[r]; totalH += rowGap * (rows - 1);

            // break-inside:avoid (grid): push a whole ROW to the next page when it straddles a page boundary but fits
            // on one page — so `break-inside:avoid` panels aren't split across pages (matches Chrome's page filling).
            if (_pageGridH > 0f && rows > 0)
            {
                float shift = 0f;
                for (int r = 0; r < rows; r++)
                {
                    float rowContentH = rowH[r]; bool anyAvoid = false;
                    for (int i = 0; i < n; i++) if (rowOf[i] == r)
                    {
                        if (rspanOf[i] == 1) rowContentH = Math.Max(rowContentH, itemBox[i].MarginBoxHeight);
                        if (items[i].Style.BreakInside == "avoid") anyAvoid = true;
                    }
                    float effTop = rowY[r] + shift;
                    if (anyAvoid && rowContentH > 0 && rowContentH <= _pageGridH - _pageMarginTop)
                    {
                        int pTop = (int)Math.Floor(effTop / _pageGridH);
                        int pBot = (int)Math.Floor((effTop + rowContentH - 0.5f) / _pageGridH);
                        if (pBot > pTop) shift += (pTop + 1) * _pageGridH + _pageMarginTop - effTop;
                    }
                    if (shift != 0f) for (int i = 0; i < n; i++) if (rowOf[i] == r) itemBox[i].Translate(0, shift);
                }
                totalH += shift;
            }
            return totalH;
        }

        private static List<Track> ParseTracks(string? spec, float availWidth = -1f, float gap = 0f)
        {
            var list = new List<Track>();
            if (string.IsNullOrWhiteSpace(spec)) return list;
            // repeat(auto-fill|auto-fit, <track>): fit as many copies as the container width allows.
            var am = System.Text.RegularExpressions.Regex.Match(spec!, @"repeat\(\s*(auto-fill|auto-fit)\s*,\s*(.*)\)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (am.Success && availWidth > 0f)
            {
                string inner = am.Groups[2].Value.Trim();
                var t = ParseTrack(inner);
                float trackMin = t.Kind == 'p' ? t.Val : t.Min;      // fixed size or minmax() min
                float trackPct = t.Kind == '%' ? availWidth * t.Val / 100f : 0f;
                float unit = Math.Max(trackMin, trackPct);
                int count = unit > 0.5f ? Math.Max(1, (int)Math.Floor((availWidth + gap) / (unit + gap))) : 1;
                spec = string.Join(" ", System.Linq.Enumerable.Repeat(inner, count));
            }
            // Expand repeat(N, ...) — single level.
            spec = System.Text.RegularExpressions.Regex.Replace(spec!, @"repeat\(\s*(\d+)\s*,\s*([^)]*)\)", m =>
            {
                int count = int.Parse(m.Groups[1].Value);
                string inner = m.Groups[2].Value.Trim();
                var parts = new List<string>();
                for (int k = 0; k < count; k++) parts.Add(inner);
                return string.Join(" ", parts);
            });
            foreach (var tok in SplitTracks(spec)) if (!tok.StartsWith("[")) list.Add(ParseTrack(tok)); // skip [line-name] tokens
            return list;
        }

        /// <summary>Map each grid line NAME to its 1-based line index from a template like <c>[a] 1fr [b] 1fr [c]</c>.
        /// Numeric repeat() is expanded first; auto-fill/named-repeat are not resolved here.</summary>
        private static Dictionary<string, int> ParseLineNames(string? spec)
        {
            var map = new Dictionary<string, int>();
            if (string.IsNullOrWhiteSpace(spec)) return map;
            spec = System.Text.RegularExpressions.Regex.Replace(spec!, @"repeat\(\s*(\d+)\s*,\s*([^)]*)\)", m =>
            {
                int count = int.Parse(m.Groups[1].Value); string inner = m.Groups[2].Value.Trim();
                var parts = new List<string>(); for (int k = 0; k < count; k++) parts.Add(inner);
                return string.Join(" ", parts);
            });
            int line = 1;
            foreach (var tok in SplitTracks(spec!))
            {
                if (tok.StartsWith("[") && tok.EndsWith("]"))
                {
                    foreach (var nm in tok.Substring(1, tok.Length - 2).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        if (!map.ContainsKey(nm)) map[nm] = line;   // first occurrence wins (approx)
                }
                else line++;                                        // a track advances to the next line
            }
            return map;
        }

        /// <summary>Split a track list on whitespace, keeping <c>minmax(…,…)</c> and <c>[line names]</c> intact.</summary>
        private static List<string> SplitTracks(string spec)
        {
            var toks = new List<string>();
            var sb = new System.Text.StringBuilder();
            int depth = 0;
            foreach (char c in spec)
            {
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                if (char.IsWhiteSpace(c) && depth == 0) { if (sb.Length > 0) { toks.Add(sb.ToString()); sb.Clear(); } }
                else sb.Append(c);
            }
            if (sb.Length > 0) toks.Add(sb.ToString());
            return toks;
        }

        private static Track ParseTrack(string tok)
        {
            string t = tok.Trim().ToLowerInvariant();
            if (t.StartsWith("minmax(") && t.EndsWith(")"))
            {
                var inner = t.Substring(7, t.Length - 8);
                int comma = inner.IndexOf(',');
                string a = comma >= 0 ? inner.Substring(0, comma).Trim() : inner.Trim();
                string b = comma >= 0 ? inner.Substring(comma + 1).Trim() : "auto";
                float min = a.EndsWith("%") || a == "auto" || a.EndsWith("fr") ? 0f : (Values.LengthPt(a, 12f) ?? 0f); // px min (auto/%/min-content → 0)
                var track = ParseTrack(b);           // the max determines the track's growth behaviour
                track.Min = Math.Max(track.Min, min);
                return track;
            }
            if (t == "auto" || t == "min-content" || t == "max-content" || t == "fit-content") return new Track { Kind = 'a', Val = 0 };
            if (t.EndsWith("fr")) return new Track { Kind = 'f', Val = ParseF(t, "fr") };
            if (t.EndsWith("%")) return new Track { Kind = '%', Val = ParseF(t, "%") };
            return new Track { Kind = 'p', Val = Values.LengthPt(t, 12f) ?? 0f };
        }

        private static float ParseF(string tok, string suffix)
        {
            var num = tok.Substring(0, tok.Length - suffix.Length);
            return float.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
        }

        private static float[] ResolveTracks(List<Track> tracks, float width, float gap, float[]? autoContent = null)
        {
            int n = tracks.Count;
            var w = new float[n];
            float gaps = (n - 1) * gap;
            float fixedSum = 0f, frSum = 0f;
            var isFr = new bool[n];
            var isAuto = new bool[n];
            for (int i = 0; i < n; i++)
            {
                switch (tracks[i].Kind)
                {
                    case 'p': w[i] = Math.Max(tracks[i].Val, tracks[i].Min); fixedSum += w[i]; break;
                    case '%': w[i] = Math.Max(width * tracks[i].Val / 100f, tracks[i].Min); fixedSum += w[i]; break;
                    case 'f': isFr[i] = true; frSum += tracks[i].Val; break;
                    // auto/min-content/max-content: size to the column's content (measured by the caller), floored by min.
                    default: isAuto[i] = true; w[i] = Math.Max(tracks[i].Min, autoContent != null && i < autoContent.Length ? autoContent[i] : 0f); fixedSum += w[i]; break;
                }
            }
            // If auto columns' content overflows the container (no fr to absorb it), shrink them proportionally to fit.
            if (frSum <= 0f)
            {
                float autoSum = 0f; for (int i = 0; i < n; i++) if (isAuto[i]) autoSum += w[i];
                float avail = width - gaps - (fixedSum - autoSum);
                if (autoSum > avail + 0.5f && autoSum > 0f)
                {
                    float k = Math.Max(0f, avail) / autoSum;
                    for (int i = 0; i < n; i++) if (isAuto[i]) w[i] *= k;
                }
            }
            // fr tracks each get max(fr-share, min); a track whose min binds is clamped and its space redistributed.
            while (frSum > 0)
            {
                float unit = Math.Max(0f, width - gaps - fixedSum) / frSum;
                int clamp = -1;
                for (int i = 0; i < n; i++)
                    if (isFr[i] && unit * tracks[i].Val < tracks[i].Min - 0.001f) { clamp = i; break; }
                if (clamp < 0) { for (int i = 0; i < n; i++) if (isFr[i]) w[i] = unit * tracks[i].Val; break; }
                w[clamp] = tracks[clamp].Min; fixedSum += w[clamp]; frSum -= tracks[clamp].Val; isFr[clamp] = false;
            }
            return w;
        }

        // ---- tables -------------------------------------------------------------------------------

        private static float LayoutTable(StyledNode node, LayoutBox box, float x, float top, float width, LayoutBox? posHost = null)
        {
            var rows = new List<StyledNode>();
            var rowIsHead = new List<bool>();               // true for <tr> inside <thead> (repeated on each page)
            var rowSectionBg = new List<Color?>();          // background COLOUR inherited from the enclosing thead/tbody/tfoot
            var rowSectionGrad = new List<Css.LinearGradient?>(); // background GRADIENT (e.g. thead{background:linear-gradient})
            StyledNode? caption = null;
            void Collect(StyledNode n, bool inHead, Color? sectionBg, Css.LinearGradient? sectionGrad)
            {
                foreach (var c in n.Children)
                {
                    if (c.IsText) continue;
                    var tg = c.Node.Tag;
                    // background on a table SECTION isn't inherited (CSS), but it DOES paint behind its rows/cells.
                    if (tg == "thead" || tg == "tbody" || tg == "tfoot")
                    {
                        var cbg = (c.Style.BackgroundColor.HasValue && c.Style.BackgroundColor.Value.A > 0) ? c.Style.BackgroundColor : sectionBg;
                        var cgr = (c.Style.BackgroundGradient != null && !c.Style.BackgroundGradient.Conic) ? c.Style.BackgroundGradient : sectionGrad;
                        Collect(c, inHead || tg == "thead", cbg, cgr);
                    }
                    else if (tg == "tr") { if (c.Style.Visibility != "collapse") { rows.Add(c); rowIsHead.Add(inHead); rowSectionBg.Add(sectionBg); rowSectionGrad.Add(sectionGrad); } } // visibility:collapse removes the row
                    else if (tg == "caption" && caption == null) caption = c;
                }
            }
            Collect(node, false, null, null);
            // Count leading header rows (contiguous <thead> rows at the top) — these repeat across page breaks.
            int headerRows = 0; while (headerRows < rowIsHead.Count && rowIsHead[headerRows]) headerRows++;
            if (rows.Count == 0) return 0f;

            // <caption>: a full-width block above (default) or below the table.
            float captionTopH = 0f;
            LayoutBox? captionBox = null;
            bool captionBottom = caption != null && caption.Style.CaptionSide == "bottom";
            if (caption != null)
            {
                captionBox = new LayoutBox(caption);
                LayoutBlock(caption, x, top, width, captionBox, null, forcedBorderBoxWidth: width, posHost: posHost);
                if (!captionBottom) { captionTopH = captionBox.MarginBoxHeight; top += captionTopH; box.Children.Add(captionBox); }
            }

            static bool IsCell(StyledNode c) => !c.IsText && (c.Node.Tag == "td" || c.Node.Tag == "th");

            int R = rows.Count;
            static int AttrInt(StyledNode n, string a) => n.Node.Attributes.TryGetValue(a, out var v) && int.TryParse(v.Trim(), out var i) && i > 0 ? i : 1;

            // Place cells into a column grid, honouring colspan/rowspan occupancy.
            var placements = new List<(StyledNode Node, int Row, int Col, int Cs, int Rs)>();
            var occupied = new HashSet<long>();
            long Key(int r, int c) => (long)r * 100000 + c;
            int cols = 0;
            for (int r = 0; r < R; r++)
            {
                int cc = 0;
                foreach (var c in rows[r].Children)
                {
                    if (!IsCell(c)) continue;
                    while (occupied.Contains(Key(r, cc))) cc++;
                    int cs = AttrInt(c, "colspan");
                    int rs = Math.Min(AttrInt(c, "rowspan"), R - r);
                    placements.Add((c, r, cc, cs, rs));
                    for (int rr = r; rr < r + rs; rr++) for (int ccc = cc; ccc < cc + cs; ccc++) occupied.Add(Key(rr, ccc));
                    cc += cs;
                    cols = Math.Max(cols, cc);
                }
            }
            if (cols == 0) return 0f;

            // border-spacing (separate model only): gaps around/between cells shrink the column/row area.
            bool collapse = node.Style.BorderCollapse == "collapse";
            float hSp = collapse ? 0f : Math.Max(0f, node.Style.BorderSpacingH);
            float vSp = collapse ? 0f : Math.Max(0f, node.Style.BorderSpacingV);

            // Column max-content widths: single-col cells set the base; spanning cells top up their range.
            var colMax = new float[cols];
            foreach (var p in placements) if (p.Cs == 1 && p.Col < cols) colMax[p.Col] = Math.Max(colMax[p.Col], MeasureMaxContent(p.Node));
            foreach (var p in placements) if (p.Cs > 1)
            {
                float need = MeasureMaxContent(p.Node) - hSp * (p.Cs - 1), have = 0f; // spanned cell also gets the inter-column gaps
                for (int c = p.Col; c < p.Col + p.Cs && c < cols; c++) have += colMax[c];
                if (need > have) { float add = (need - have) / p.Cs; for (int c = p.Col; c < p.Col + p.Cs && c < cols; c++) colMax[c] += add; }
            }
            float sum = 0f; foreach (var m in colMax) sum += m; if (sum <= 0f) sum = 1f;

            // Column MIN-content (widest unbreakable piece) — a column never shrinks below this, so an unbreakable
            // cell (e.g. a nowrap inline-block badge) isn't squeezed narrower than its content when the table overflows.
            var colMin = new float[cols];
            foreach (var p in placements) if (p.Cs == 1 && p.Col < cols) colMin[p.Col] = Math.Max(colMin[p.Col], MeasureMinContent(p.Node));

            float availCols = Math.Max(1f, width - hSp * (cols + 1)); // width left for columns after all h-gaps

            // Explicit per-column widths from a cell's CSS `width` (px/%). Those columns take exactly that width;
            // the remaining space is distributed over the AUTO columns (filling to width, or shrinking to fit).
            var colExplicit = new float[cols];   // 0 = auto
            foreach (var p in placements) if (p.Cs == 1 && p.Col < cols)
            {
                float ew = p.Node.Style.WidthPercent.HasValue ? availCols * p.Node.Style.WidthPercent.Value / 100f
                         : p.Node.Style.Width.HasValue ? p.Node.Style.Width.Value : 0f;
                if (ew > 0f) colExplicit[p.Col] = Math.Max(colExplicit[p.Col], ew);
            }
            float explicitTotal = 0f, autoMaxSum = 0f; int autoCount = 0;
            for (int i = 0; i < cols; i++) { if (colExplicit[i] > 0f) explicitTotal += colExplicit[i]; else { autoCount++; autoMaxSum += colMax[i]; } }
            float autoAvail = Math.Max(0f, availCols - explicitTotal);

            var colW = new float[cols];
            for (int i = 0; i < cols; i++)
            {
                if (colExplicit[i] > 0f) { colW[i] = colExplicit[i]; continue; }
                if (autoMaxSum <= autoAvail) { float extra = autoAvail - autoMaxSum; colW[i] = colMax[i] + (autoMaxSum > 0f ? extra * (colMax[i] / autoMaxSum) : (autoCount > 0 ? extra / autoCount : 0f)); }
                else colW[i] = Math.Max(colMin[i], autoAvail * (colMax[i] / autoMaxSum));   // never below min-content
            }
            var colX = new float[cols]; float ax = x + hSp; for (int c = 0; c < cols; c++) { colX[c] = ax; ax += colW[c] + hSp; }
            float tableWidth = ax - x;   // ACTUAL table width (columns + spacing); row backgrounds use this, not the
                                         // container `width`, so a table narrower than its container doesn't bleed the bg.
            float CellW((StyledNode Node, int Row, int Col, int Cs, int Rs) p)
            { float w = hSp * (p.Cs - 1); for (int c = p.Col; c < p.Col + p.Cs && c < cols; c++) w += colW[c]; return w; }

            // Row heights: natural per cell; single-row cells set the row, rowspan cells top up their range.
            var natH = new float[placements.Count];
            var rowH = new float[R];
            for (int i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                var probe = new LayoutBox(p.Node);
                LayoutBlock(p.Node, colX[p.Col], top, CellW(p), probe, null, forcedBorderBoxWidth: CellW(p));
                natH[i] = probe.BorderBoxHeight;
                if (p.Rs == 1) rowH[p.Row] = Math.Max(rowH[p.Row], natH[i]);
            }
            for (int i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                if (p.Rs <= 1) continue;
                float have = vSp * (p.Rs - 1); for (int r = p.Row; r < p.Row + p.Rs; r++) have += rowH[r]; // spanned cell also gets inter-row gaps
                if (natH[i] > have) { float add = (natH[i] - have) / p.Rs; for (int r = p.Row; r < p.Row + p.Rs; r++) rowH[r] += add; }
            }
            var rowY = new float[R + 1]; rowY[0] = top + vSp; for (int r = 0; r < R; r++) rowY[r + 1] = rowY[r] + rowH[r] + vSp;

            // Repeating <thead>: when the table spans page boundaries, snap each straddling body row to the next
            // page top (leaving room for a header clone) and record the clone positions. Gated so non-paginating or
            // headerless tables are unaffected. (rowspan cells crossing a break are not split — a simple-table case.)
            var headerClonePageTops = new List<float>();
            if (headerRows > 0 && headerRows < R && _pageGridH > 0f)
            {
                float headerH = rowY[headerRows] - rowY[0];   // height of the header block (incl. its trailing v-gap)
                float extra = 0f;
                for (int r = headerRows; r < R; r++)
                {
                    float t = rowY[r] + extra, b = t + rowH[r];
                    int pt = (int)Math.Floor(t / _pageGridH), pb = (int)Math.Floor((b - 0.5f) / _pageGridH);
                    if (pb > pt && rowH[r] + headerH < _pageGridH)   // straddles a boundary and fits with the header on a fresh page
                    {
                        float npTop = (pt + 1) * _pageGridH + _pageMarginTop;
                        extra += (npTop + headerH) - t;
                        headerClonePageTops.Add(npTop);
                    }
                    rowY[r] += extra;
                }
                rowY[R] += extra;
            }

            // Row backgrounds (behind cells): the enclosing <thead>/<tbody>/<tfoot> background paints first, then the
            // row's own background over it.
            for (int r = 0; r < R; r++)
            {
                if (rowSectionBg[r].HasValue && rowSectionBg[r].Value.A > 0)
                    box.Decorations.Add(new SolidRect { X = x, Y = rowY[r], Width = tableWidth, Height = rowH[r], Color = rowSectionBg[r].Value });
                // Section GRADIENT (e.g. `thead{background:linear-gradient(...)}`) — paints behind the header cells.
                if (rowSectionGrad[r] != null)
                    (box.BgLayers ??= new List<DrawCommand>()).Add(new GradientFill { X = x, Y = rowY[r], Width = tableWidth, Height = rowH[r], Gradient = rowSectionGrad[r]!, Alpha = GradientAlpha(rowSectionGrad[r]!) });
                if (rows[r].Style.BackgroundColor.HasValue && rows[r].Style.BackgroundColor.Value.A > 0)
                    box.Decorations.Add(new SolidRect { X = x, Y = rowY[r], Width = tableWidth, Height = rowH[r], Color = rows[r].Style.BackgroundColor.Value });
            }

            // Place cells, spanning their columns and rows.
            for (int i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                float cw = CellW(p), ch = vSp * (p.Rs - 1);
                for (int r = p.Row; r < p.Row + p.Rs; r++) ch += rowH[r];
                var cb = new LayoutBox(p.Node);
                // empty-cells:hide (separated model only) — an empty cell paints no background or border.
                bool hideEmpty = !collapse && p.Node.Style.EmptyCells == "hide" && CellIsEmpty(p.Node);
                LayoutBlock(p.Node, colX[p.Col], rowY[p.Row], cw, cb, null, forcedBorderBoxWidth: cw, forcedBorderBoxHeight: ch, posHost: posHost, noBorder: collapse, suppressDecor: hideEmpty);
                // vertical-align: content is top by default; middle/bottom shift the cell's CONTENT (not its bg/border).
                string va = p.Node.Style.VerticalAlign;
                if (va == "middle" || va == "bottom")
                {
                    float free = ch - CellContentHeight(cb, p.Node.Style);
                    if (free > 0.5f) TranslateCellContent(cb, va == "middle" ? free / 2f : free);
                }
                box.Children.Add(cb);
            }

            // border-collapse: draw merged grid lines ONCE, on top of cell backgrounds. Each cell draws its
            // top+left; the outer right/bottom edges are drawn by cells touching those edges (respects spans).
            // The lines go into an overlay box appended last so they paint above every cell.
            if (collapse)
            {
                var overlay = new LayoutBox(node);
                foreach (var p in placements)
                {
                    var cs = p.Node.Style;
                    float cw = CellW(p), ch = 0f;
                    for (int r = p.Row; r < p.Row + p.Rs; r++) ch += rowH[r];
                    float cx0 = colX[p.Col], cy0 = rowY[p.Row];
                    BorderEdge Edge(BorderEdge e) => e.Width > 0 ? e : (node.Style.BorderTop.Width > 0 ? node.Style.BorderTop : e);
                    var et = Edge(cs.BorderTop); var el = Edge(cs.BorderLeft); var er = Edge(cs.BorderRight); var eb = Edge(cs.BorderBottom);
                    if (et.Width > 0 && et.Color.A > 0) overlay.Decorations.Add(new SolidRect { X = cx0, Y = cy0, Width = cw, Height = et.Width, Color = et.Color });
                    if (el.Width > 0 && el.Color.A > 0) overlay.Decorations.Add(new SolidRect { X = cx0, Y = cy0, Width = el.Width, Height = ch, Color = el.Color });
                    if (p.Col + p.Cs >= cols && er.Width > 0 && er.Color.A > 0) overlay.Decorations.Add(new SolidRect { X = cx0 + cw - er.Width, Y = cy0, Width = er.Width, Height = ch, Color = er.Color });
                    if (p.Row + p.Rs >= R && eb.Width > 0 && eb.Color.A > 0) overlay.Decorations.Add(new SolidRect { X = cx0, Y = cy0 + ch - eb.Width, Width = cw, Height = eb.Width, Color = eb.Color });
                }
                box.Children.Add(overlay);
            }
            // Repeating <thead>: clone the header rows (cells, row backgrounds, and collapse grid lines) at the top
            // of each page the body overflows onto. Header rows keep their original y (rowY[0..headerRows]).
            foreach (float cloneTop in headerClonePageTops)
            {
                float dy = cloneTop - rowY[0];
                for (int r = 0; r < headerRows; r++)
                {
                    if (rowSectionBg[r].HasValue && rowSectionBg[r].Value.A > 0)
                        box.Decorations.Add(new SolidRect { X = x, Y = rowY[r] + dy, Width = tableWidth, Height = rowH[r], Color = rowSectionBg[r].Value });
                    if (rowSectionGrad[r] != null)
                        (box.BgLayers ??= new List<DrawCommand>()).Add(new GradientFill { X = x, Y = rowY[r] + dy, Width = tableWidth, Height = rowH[r], Gradient = rowSectionGrad[r]!, Alpha = GradientAlpha(rowSectionGrad[r]!) });
                    if (rows[r].Style.BackgroundColor.HasValue && rows[r].Style.BackgroundColor.Value.A > 0)
                        box.Decorations.Add(new SolidRect { X = x, Y = rowY[r] + dy, Width = tableWidth, Height = rowH[r], Color = rows[r].Style.BackgroundColor.Value });
                }
                foreach (var p in placements)
                {
                    if (p.Row >= headerRows) continue;
                    float cw = CellW(p), ch = vSp * (p.Rs - 1);
                    for (int r = p.Row; r < p.Row + p.Rs; r++) ch += rowH[r];
                    var cb = new LayoutBox(p.Node);
                    bool hideEmpty = !collapse && p.Node.Style.EmptyCells == "hide" && CellIsEmpty(p.Node);
                    LayoutBlock(p.Node, colX[p.Col], rowY[p.Row] + dy, cw, cb, null, forcedBorderBoxWidth: cw, forcedBorderBoxHeight: ch, posHost: posHost, noBorder: collapse, suppressDecor: hideEmpty);
                    string va = p.Node.Style.VerticalAlign;
                    if (va == "middle" || va == "bottom")
                    {
                        float free = ch - CellContentHeight(cb, p.Node.Style);
                        if (free > 0.5f) TranslateCellContent(cb, va == "middle" ? free / 2f : free);
                    }
                    box.Children.Add(cb);
                }
                if (collapse)
                {
                    var ov = new LayoutBox(node);
                    foreach (var p in placements)
                    {
                        if (p.Row >= headerRows) continue;
                        var cs = p.Node.Style;
                        float cw = CellW(p), ch = 0f; for (int r = p.Row; r < p.Row + p.Rs; r++) ch += rowH[r];
                        float cx0 = colX[p.Col], cy0 = rowY[p.Row] + dy;
                        BorderEdge Edge(BorderEdge e) => e.Width > 0 ? e : (node.Style.BorderTop.Width > 0 ? node.Style.BorderTop : e);
                        var et = Edge(cs.BorderTop); var el = Edge(cs.BorderLeft); var er = Edge(cs.BorderRight); var eb = Edge(cs.BorderBottom);
                        if (et.Width > 0 && et.Color.A > 0) ov.Decorations.Add(new SolidRect { X = cx0, Y = cy0, Width = cw, Height = et.Width, Color = et.Color });
                        if (el.Width > 0 && el.Color.A > 0) ov.Decorations.Add(new SolidRect { X = cx0, Y = cy0, Width = el.Width, Height = ch, Color = el.Color });
                        if (p.Col + p.Cs >= cols && er.Width > 0 && er.Color.A > 0) ov.Decorations.Add(new SolidRect { X = cx0 + cw - er.Width, Y = cy0, Width = er.Width, Height = ch, Color = er.Color });
                        ov.Decorations.Add(new SolidRect { X = cx0, Y = cy0 + ch - eb.Width, Width = cw, Height = Math.Max(eb.Width, et.Width), Color = eb.Width > 0 ? eb.Color : et.Color });
                    }
                    box.Children.Add(ov);
                }
            }

            float bodyH = rowY[R] - top;

            // bottom caption sits below the table body.
            float captionBotH = 0f;
            if (captionBottom && captionBox != null)
            {
                captionBox.Translate(0, bodyH); // was laid at `top`; move below the body
                box.Children.Add(captionBox);
                captionBotH = captionBox.MarginBoxHeight;
            }
            return captionTopH + bodyH + captionBotH;
        }

        // ---- list markers -------------------------------------------------------------------------
        private static int AttrIntOr(StyledNode n, string a, int dflt)
            => n.Node.Attributes.TryGetValue(a, out var v) && int.TryParse(v.Trim(), out var i) ? i : dflt;

        /// <summary>Map the legacy HTML <c>&lt;ol type&gt;</c> attribute to a CSS list-style-type.</summary>
        private static string? OlTypeToCss(string? t) => t switch
        {
            "1" => "decimal", "a" => "lower-alpha", "A" => "upper-alpha", "i" => "lower-roman", "I" => "upper-roman", _ => null,
        };

        /// <summary>Apply CSS text-transform to a whitespace-delimited token (words are already split).</summary>
        private static string ApplyTextTransform(string tok, string transform)
        {
            switch (transform)
            {
                case "uppercase": return tok.ToUpperInvariant();
                case "lowercase": return tok.ToLowerInvariant();
                case "capitalize":
                    // Titlecase the first letter, skipping leading punctuation ("(hello)"→"(Hello)"), but a
                    // word that begins with a digit is left as-is ("3rd"→"3rd"), matching browsers.
                    for (int i = 0; i < tok.Length; i++)
                    {
                        char c = tok[i];
                        if (char.IsDigit(c)) return tok;
                        if (char.IsLetter(c))
                        {
                            if (char.IsUpper(c)) return tok;        // already capitalized
                            var a = tok.ToCharArray(); a[i] = char.ToUpperInvariant(c); return new string(a);
                        }
                    }
                    return tok;
                default: return tok;
            }
        }

        private static bool IsOrderedType(string t)
        {
            switch (t)
            {
                case "decimal": case "decimal-leading-zero":
                case "lower-alpha": case "upper-alpha": case "lower-latin": case "upper-latin":
                case "lower-roman": case "upper-roman": case "lower-greek":
                case "armenian": case "upper-armenian": case "lower-armenian": case "georgian": return true;
                default: return false;
            }
        }

        /// <summary>Armenian numeral (1-9999): ones/tens/hundreds/thousands letters from the given case base.</summary>
        internal static string ArmenianCounter(int n, bool upper)
        {
            if (n <= 0 || n > 9999) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int ob = upper ? 0x0531 : 0x0561, tb = upper ? 0x053A : 0x056A, hb = upper ? 0x0543 : 0x0573, kb = upper ? 0x054C : 0x057C;
            var sb = new System.Text.StringBuilder();
            int th = n / 1000, hu = (n / 100) % 10, te = (n / 10) % 10, on = n % 10;
            if (th > 0) sb.Append((char)(kb + th - 1));
            if (hu > 0) sb.Append((char)(hb + hu - 1));
            if (te > 0) sb.Append((char)(tb + te - 1));
            if (on > 0) sb.Append((char)(ob + on - 1));
            return sb.ToString();
        }

        // Georgian numeral (1-9999) letter tables (ones, tens, hundreds, thousands).
        private static readonly int[] GeoOnes = { 0x10D0, 0x10D1, 0x10D2, 0x10D3, 0x10D4, 0x10D5, 0x10D6, 0x10F1, 0x10D7 };
        private static readonly int[] GeoTens = { 0x10D8, 0x10D9, 0x10DA, 0x10DB, 0x10DC, 0x10F2, 0x10DD, 0x10DE, 0x10DF };
        private static readonly int[] GeoHund = { 0x10E0, 0x10E1, 0x10E2, 0x10F3, 0x10E4, 0x10E5, 0x10E6, 0x10E7, 0x10E8 };
        private static readonly int[] GeoThou = { 0x10E9, 0x10EA, 0x10EB, 0x10EC, 0x10ED, 0x10EE, 0x10F4, 0x10EF, 0x10F0 };
        internal static string GeorgianCounter(int n)
        {
            if (n <= 0 || n > 9999) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sb = new System.Text.StringBuilder();
            int th = n / 1000, hu = (n / 100) % 10, te = (n / 10) % 10, on = n % 10;
            if (th > 0) sb.Append((char)GeoThou[th - 1]);
            if (hu > 0) sb.Append((char)GeoHund[hu - 1]);
            if (te > 0) sb.Append((char)GeoTens[te - 1]);
            if (on > 0) sb.Append((char)GeoOnes[on - 1]);
            return sb.ToString();
        }

        private static string FormatListCounter(int n, string type)
        {
            switch (type)
            {
                case "decimal-leading-zero": return n >= 0 && n < 10 ? "0" + n : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "lower-alpha": case "lower-latin": return Alpha(n, 'a');
                case "upper-alpha": case "upper-latin": return Alpha(n, 'A');
                case "lower-roman": return Roman(n).ToLowerInvariant();
                case "upper-roman": return Roman(n);
                case "lower-greek": return Greek(n);
                case "armenian": case "upper-armenian": return ArmenianCounter(n, true);
                case "lower-armenian": return ArmenianCounter(n, false);
                case "georgian": return GeorgianCounter(n);
                default: return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        /// <summary>CSS lower-greek counter: α β γ … ω (24 letters), then αα αβ … like a bijective base-24.</summary>
        private static string Greek(int n)
        {
            if (n <= 0) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sb = new System.Text.StringBuilder();
            while (n > 0) { n--; sb.Insert(0, (char)(0x03B1 + n % 24)); n /= 24; }
            return sb.ToString();
        }

        private static string Alpha(int n, char baseCh)
        {
            if (n <= 0) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sb = new System.Text.StringBuilder();
            while (n > 0) { n--; sb.Insert(0, (char)(baseCh + n % 26)); n /= 26; }
            return sb.ToString();
        }

        private static string Roman(int n)
        {
            if (n <= 0 || n >= 4000) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int[] vals = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] syms = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < vals.Length; i++) while (n >= vals[i]) { sb.Append(syms[i]); n -= vals[i]; }
            return sb.ToString();
        }

        /// <summary>The occupied content height of a laid cell (border+padding+actual content extent), ignoring
        /// any explicit/forced cell height — used to compute vertical-align free space.</summary>
        private static float CellContentHeight(LayoutBox cb, ComputedStyle s)
        {
            float cellTop = cb.BorderBoxY;
            float contentTop = cellTop + s.BorderTop.Width + s.PadTop;
            float contentBottom = contentTop;
            foreach (var t in cb.TextRuns) contentBottom = Math.Max(contentBottom, t.BaselineY + t.FontSizePt * 0.25f);
            foreach (var c in cb.Children) contentBottom = Math.Max(contentBottom, c.BorderBoxY + c.BorderBoxHeight);
            foreach (var im in cb.Images) contentBottom = Math.Max(contentBottom, im.Y + im.Height);
            return (contentBottom - cellTop) + s.PadBottom + s.BorderBottom.Width;
        }

        /// <summary>Shift a table cell's CONTENT (text/images/children) down by dy for vertical-align, leaving
        /// its own background/border/decorations in place.</summary>
        private static void TranslateCellContent(LayoutBox b, float dy)
        {
            foreach (var t in b.TextRuns) t.BaselineY += dy;
            foreach (var im in b.Images) { im.Y += dy; im.ClipY += dy; }
            foreach (var sv in b.Svgs) sv.Y += dy;
            foreach (var td in b.TextDecos) td.Y += dy;
            foreach (var m in b.MarkerShapes) m.Y += dy;
            foreach (var c in b.Children) c.Translate(0, dy);
        }

        /// <summary>Approximate max-content (one-line) width in pt of a subtree, ignoring wrapping.</summary>
        private static float MeasureMaxContent(StyledNode n)
        {
            if (n.IsText)
            {
                float w = 0f; var words = n.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < words.Length; i++)
                {
                    string t = ApplyTextTransform(words[i], n.Style.TextTransform); // measure the transformed (e.g. UPPERCASE) text
                    // Use the SAME font the layout will (embedded CJK/Arabic/… face), not the base-14 AFM which
                    // treats CJK as half-width — else CJK boxes measure too narrow and their text overflows/wraps.
                    var emb = FontManager.ResolveForWord(n.Style, t);
                    w += (emb != null ? emb.MeasurePt(t, n.Style.FontSizePt) : Afm.MeasurePt(n.Style.Face, t, n.Style.FontSizePt)) + n.Style.LetterSpacing * t.Length;
                    if (i > 0) w += Afm.MeasurePt(n.Style.Face, " ", n.Style.FontSizePt) + n.Style.WordSpacing;
                }
                return w;
            }
            var s = n.Style;
            float extra = s.PadLeft + s.PadRight + s.BorderLeft.Width + s.BorderRight.Width;
            if (s.Width.HasValue) return s.BoxSizing == "border-box" ? s.Width.Value : s.Width.Value + extra;

            // Replaced elements (<img>, <svg>) contribute their intrinsic width to max-content.
            if (n.Node.Tag == "img" || n.Node.Tag == "svg")
                return IntrinsicWidthPt(n) + extra;

            bool hasBlock = false; foreach (var c in n.Children) if (!c.IsText && c.Style.Display == "block") { hasBlock = true; break; }
            float inner = 0f;
            if (hasBlock)
            { foreach (var c in n.Children) inner = Math.Max(inner, MeasureMaxContent(c) + c.Style.MarginLeft + c.Style.MarginRight); }
            else
            {
                // Sum inline children; add the single collapsed space that renders between two of them when whitespace
                // sits at the boundary (e.g. `<strong>…:</strong> 経営企画部`) — else the box measures one space too
                // narrow and a trailing CJK char (which may break between ideographs) wrongly wraps.
                StyledNode? prev = null;
                foreach (var c in n.Children)
                {
                    if (prev != null && (EndsWithWs(prev) || StartsWithWs(c)))
                        inner += Afm.MeasurePt(s.Face, " ", s.FontSizePt) + s.WordSpacing;
                    inner += MeasureMaxContent(c);
                    prev = c;
                }
            }
            return inner + extra;
        }

        private static bool StartsWithWs(StyledNode n)
        {
            if (n.IsText) return n.Text.Length > 0 && char.IsWhiteSpace(n.Text[0]);
            return n.Children.Count > 0 && StartsWithWs(n.Children[0]);
        }
        private static bool EndsWithWs(StyledNode n)
        {
            if (n.IsText) return n.Text.Length > 0 && char.IsWhiteSpace(n.Text[n.Text.Length - 1]);
            return n.Children.Count > 0 && EndsWithWs(n.Children[n.Children.Count - 1]);
        }

        /// <summary>Approximate min-content width in pt: the widest UNBREAKABLE piece (longest word / widest child).</summary>
        private static float MeasureMinContent(StyledNode n)
        {
            if (n.IsText)
            {
                // word-break:break-all makes every character a breakpoint → min-content ≈ widest single glyph.
                if (n.Style.WordBreak == "break-all")
                {
                    float mx = 0f;
                    foreach (var ch in ApplyTextTransform(n.Text, n.Style.TextTransform))
                        if (!char.IsWhiteSpace(ch)) mx = Math.Max(mx, Afm.MeasurePt(n.Style.Face, ch.ToString(), n.Style.FontSizePt) + n.Style.LetterSpacing);
                    return mx;
                }
                float w = 0f;
                foreach (var raw in n.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = ApplyTextTransform(raw, n.Style.TextTransform);
                    var emb = FontManager.ResolveForWord(n.Style, t);
                    // CJK breaks between characters, so its min-content is the widest single ideograph, not the whole run.
                    if (n.Style.WordBreak != "keep-all" && HasCjk(t))
                        foreach (var ch in t) { string cs = ch.ToString(); w = Math.Max(w, (emb != null ? emb.MeasurePt(cs, n.Style.FontSizePt) : Afm.MeasurePt(n.Style.Face, cs, n.Style.FontSizePt)) + n.Style.LetterSpacing); }
                    else
                        w = Math.Max(w, (emb != null ? emb.MeasurePt(t, n.Style.FontSizePt) : Afm.MeasurePt(n.Style.Face, t, n.Style.FontSizePt)) + n.Style.LetterSpacing * t.Length);
                }
                return w;
            }
            var s = n.Style;
            float extra = s.PadLeft + s.PadRight + s.BorderLeft.Width + s.BorderRight.Width;
            if (s.Width.HasValue) return s.BoxSizing == "border-box" ? s.Width.Value : s.Width.Value + extra;
            if (n.Node.Tag == "img" || n.Node.Tag == "svg") return IntrinsicWidthPt(n) + extra;
            float inner = 0f;   // min-content of a container = the largest min-content among its children
            foreach (var c in n.Children) inner = Math.Max(inner, MeasureMinContent(c) + (c.IsText ? 0f : c.Style.MarginLeft + c.Style.MarginRight));
            return inner + extra;
        }

        /// <summary>Build the background-image draw (sized per background-size, placed per background-position,
        /// clipped to the background-clip box (clip*)). Null when there is no <c>background-image: url(...)</c>.</summary>
        private static List<ImageDraw>? BuildBgImage(ComputedStyle s, string? url, float bx, float by, float bw, float bh, float clipX, float clipY, float clipW, float clipH,
            string? sizeO = null, string? posO = null, string? repeatO = null)
        {
            if (url == null || bw <= 0 || bh <= 0) return null;
            var dec = HtmlPdfNative.Images.ImageLoader.Load(url);
            if (dec == null) return null;
            float iw = dec.Width * Lib.PxToPt, ih = dec.Height * Lib.PxToPt;
            if (iw <= 0 || ih <= 0) return null;

            // background-origin insets the POSITIONING box (background-clip already gave the clip* box).
            if (s.BackgroundOrigin == "padding-box" || s.BackgroundOrigin == "content-box")
            {
                float il = s.BorderLeft.Width, it = s.BorderTop.Width, ir = s.BorderRight.Width, ib2 = s.BorderBottom.Width;
                if (s.BackgroundOrigin == "content-box") { il += s.PadLeft; it += s.PadTop; ir += s.PadRight; ib2 += s.PadBottom; }
                bx += il; by += it; bw = Math.Max(0, bw - il - ir); bh = Math.Max(0, bh - it - ib2);
                if (bw <= 0 || bh <= 0) return null;
            }

            float dw, dh;
            string size = sizeO ?? s.BackgroundSize;
            if (size == "cover") { float sc = Math.Max(bw / iw, bh / ih); dw = iw * sc; dh = ih * sc; }
            else if (size == "contain") { float sc = Math.Min(bw / iw, bh / ih); dw = iw * sc; dh = ih * sc; }
            else if (size == "auto" || size.Length == 0) { dw = iw; dh = ih; }
            else
            {
                var parts = size.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                float? pw = SizeComp(parts.Length > 0 ? parts[0] : "auto", bw, s.FontSizePt);
                float? ph = parts.Length > 1 ? SizeComp(parts[1], bh, s.FontSizePt) : null;
                if (pw.HasValue && ph.HasValue) { dw = pw.Value; dh = ph.Value; }
                else if (pw.HasValue) { dw = pw.Value; dh = ih * (pw.Value / iw); }
                else if (ph.HasValue) { dh = ph.Value; dw = iw * (ph.Value / ih); }
                else { dw = iw; dh = ih; }
            }
            if (dw <= 0 || dh <= 0) return null;

            var (fx, fy) = ParseBgPos(posO ?? s.BackgroundPosition);
            float px = (bw - dw) * fx, py = (bh - dh) * fy;   // anchor tile offset within box

            string rep = repeatO ?? s.BackgroundRepeat;
            bool roundMode = rep == "round", spaceMode = rep == "space";
            bool repX = rep == "repeat" || rep == "repeat-x" || roundMode || spaceMode;
            bool repY = rep == "repeat" || rep == "repeat-y" || roundMode || spaceMode;

            // round: rescale the tile so a whole number fit exactly along each repeating axis (no gaps).
            if (roundMode)
            {
                if (dw > 0) { int n = Math.Max(1, (int)Math.Round(bw / dw)); dw = bw / n; }
                if (dh > 0) { int n = Math.Max(1, (int)Math.Round(bh / dh)); dh = bh / n; }
            }

            // Per-axis tile advance (== tile size, except `space` inserts equal gaps between whole tiles).
            float stepX = dw, stepY = dh;
            float startX, startY, endX, endY;
            if (spaceMode)
            {
                // space: keep the tile size, place n = floor(box/tile) whole tiles edge-to-edge with equal gaps.
                int nx = dw > 0 ? (int)Math.Floor(bw / dw + 1e-4) : 1; if (nx < 1) nx = 1;
                int ny = dh > 0 ? (int)Math.Floor(bh / dh + 1e-4) : 1; if (ny < 1) ny = 1;
                stepX = nx >= 2 ? (bw - dw) / (nx - 1) : dw;
                stepY = ny >= 2 ? (bh - dh) / (ny - 1) : dh;
                startX = 0; startY = 0;
                endX = (nx - 1) * stepX + dw * 0.5f;   // include exactly nx tiles
                endY = (ny - 1) * stepY + dh * 0.5f;
            }
            else
            {
                // Tiling: walk from the anchor back to the first tile at/above the box edge, then forward.
                startX = px; startY = py;
                if (repX) while (startX > 0) startX -= dw;
                if (repY) while (startY > 0) startY -= dh;
                endX = repX ? bw : px + dw * 0.5f;   // no-repeat: single column at px
                endY = repY ? bh : py + dh * 0.5f;
            }

            var tiles = new List<ImageDraw>();
            const int MaxTiles = 4000;                 // safety cap for tiny tiles over large areas
            for (float ty = startY; ty < endY && tiles.Count < MaxTiles; ty += stepY)
            {
                for (float tx = startX; tx < endX && tiles.Count < MaxTiles; tx += stepX)
                {
                    tiles.Add(new ImageDraw
                    {
                        X = bx + tx, Y = by + ty, Width = dw, Height = dh, Image = dec,
                        Clip = true, ClipX = clipX, ClipY = clipY, ClipW = clipW, ClipH = clipH,
                    });
                    if (!repX) break;
                }
                if (!repY) break;
            }
            return tiles.Count > 0 ? tiles : null;
        }

        /// <summary>Apply a CSS <c>filter</c> (grayscale/sepia/invert/brightness/contrast/saturate/opacity) to a
        /// CLONE of the decoded image (the original stays cached). blur() is a box blur; drop-shadow is ignored.</summary>
        private static Images.DecodedImage ApplyImageFilter(Images.DecodedImage src, string filter)
        {
            var rgb = (byte[])src.Rgb.Clone();
            byte[]? alpha = src.Alpha != null ? (byte[])src.Alpha.Clone() : null;
            foreach (var (name, amt) in ParseFilters(filter))
            {
                if (name == "opacity")
                {
                    if (alpha == null) { alpha = new byte[src.Width * src.Height]; for (int i = 0; i < alpha.Length; i++) alpha[i] = 255; }
                    for (int i = 0; i < alpha.Length; i++) alpha[i] = Clamp8(alpha[i] * amt);
                    continue;
                }
                if (name == "blur")
                {
                    // amt = blur radius in CSS px ≈ image px; a separable box blur (2 passes ≈ Gaussian-ish).
                    int r = (int)Math.Round(amt * 0.7f);   // CSS blur σ→box radius fudge; keeps it from over-smearing
                    r = Math.Max(0, Math.Min(80, r));
                    if (r >= 1)
                    {
                        BlurPlane(rgb, src.Width, src.Height, 3, 0, r); BlurPlane(rgb, src.Width, src.Height, 3, 1, r); BlurPlane(rgb, src.Width, src.Height, 3, 2, r);
                        if (alpha != null) BlurPlane(alpha, src.Width, src.Height, 1, 0, r);
                    }
                    continue;
                }
                // hue-rotate: precompute the constant rotation coefficients (cos/sin of the angle).
                float hc = 0f, hs = 0f;
                if (name == "hue-rotate") { double rad = amt * Math.PI / 180.0; hc = (float)Math.Cos(rad); hs = (float)Math.Sin(rad); }
                for (int i = 0; i < rgb.Length; i += 3)
                {
                    float r = rgb[i] / 255f, g = rgb[i + 1] / 255f, b = rgb[i + 2] / 255f;
                    switch (name)
                    {
                        case "hue-rotate":
                        {
                            float nr = r * (0.213f + hc * 0.787f - hs * 0.213f) + g * (0.715f - hc * 0.715f - hs * 0.715f) + b * (0.072f - hc * 0.072f + hs * 0.928f);
                            float ng = r * (0.213f - hc * 0.213f + hs * 0.143f) + g * (0.715f + hc * 0.285f + hs * 0.140f) + b * (0.072f - hc * 0.072f - hs * 0.283f);
                            float nb = r * (0.213f - hc * 0.213f - hs * 0.787f) + g * (0.715f - hc * 0.715f + hs * 0.715f) + b * (0.072f + hc * 0.928f + hs * 0.072f);
                            r = nr; g = ng; b = nb; break;
                        }
                        case "grayscale": { float l = 0.2126f * r + 0.7152f * g + 0.0722f * b; r += (l - r) * amt; g += (l - g) * amt; b += (l - b) * amt; break; }
                        case "sepia":
                        {
                            float sr = 0.393f * r + 0.769f * g + 0.189f * b, sg = 0.349f * r + 0.686f * g + 0.168f * b, sb = 0.272f * r + 0.534f * g + 0.131f * b;
                            r += (sr - r) * amt; g += (sg - g) * amt; b += (sb - b) * amt; break;
                        }
                        case "invert": { r += (1f - r - r) * amt; g += (1f - g - g) * amt; b += (1f - b - b) * amt; break; }
                        case "brightness": { r *= amt; g *= amt; b *= amt; break; }
                        case "contrast": { r = (r - 0.5f) * amt + 0.5f; g = (g - 0.5f) * amt + 0.5f; b = (b - 0.5f) * amt + 0.5f; break; }
                        case "saturate": { float l = 0.2126f * r + 0.7152f * g + 0.0722f * b; r = l + (r - l) * amt; g = l + (g - l) * amt; b = l + (b - l) * amt; break; }
                    }
                    rgb[i] = Clamp8(r * 255f); rgb[i + 1] = Clamp8(g * 255f); rgb[i + 2] = Clamp8(b * 255f);
                }
            }
            return new Images.DecodedImage { Width = src.Width, Height = src.Height, Rgb = rgb, Alpha = alpha };
        }

        private static byte Clamp8(float v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : Math.Round(v));
        private static int Clampi(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>Parse the first <c>drop-shadow(dx dy [blur] color)</c> in a filter (offsets/blur in CSS px). Null if none.</summary>
        private static (float dx, float dy, float blur, Color col)? ParseDropShadow(string? filter)
        {
            if (filter == null) return null;
            int i = filter.IndexOf("drop-shadow(", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int open = i + "drop-shadow".Length;
            int depth = 0, j = open;
            for (; j < filter.Length; j++) { if (filter[j] == '(') depth++; else if (filter[j] == ')') { depth--; if (depth == 0) break; } }
            if (j >= filter.Length) return null;
            string args = filter.Substring(open + 1, j - open - 1).Trim();
            var toks = Css.Gradients.SplitTopLevel(args, ' ');
            float dx = 0, dy = 0, blur = 0; Color col = new Color(0, 0, 0, 128); int lenIdx = 0;
            foreach (var t in toks)
            {
                var tk = t.Trim(); if (tk.Length == 0) continue;
                var c = Css.Values.ParseColor(tk);
                if (c != null) { col = c.Value; continue; }
                float v = Css.Values.LengthPt(tk, 12f) is float lp ? lp / Lib.PxToPt : 0f; // back to px
                if (lenIdx == 0) dx = v; else if (lenIdx == 1) dy = v; else if (lenIdx == 2) blur = v;
                lenIdx++;
            }
            return (dx, dy, blur, col);
        }

        /// <summary>Build a drop-shadow silhouette: a same-size image filled with the shadow colour, alpha = the
        /// source alpha (or opaque), blurred by <paramref name="r"/> pixels.</summary>
        private static Images.DecodedImage BuildImageDropShadow(Images.DecodedImage src, Color col, int r)
        {
            int w = src.Width, h = src.Height, n = w * h;
            var rgb = new byte[n * 3];
            for (int i = 0; i < n; i++) { rgb[i * 3] = col.R; rgb[i * 3 + 1] = col.G; rgb[i * 3 + 2] = col.B; }
            var alpha = new byte[n];
            if (src.Alpha != null) Array.Copy(src.Alpha, alpha, n); else for (int i = 0; i < n; i++) alpha[i] = 255;
            if (col.A < 255) for (int i = 0; i < n; i++) alpha[i] = (byte)(alpha[i] * col.A / 255);
            if (r >= 1) BlurPlane(alpha, w, h, 1, 0, r);
            return new Images.DecodedImage { Width = w, Height = h, Rgb = rgb, Alpha = alpha };
        }

        /// <summary>Separable box blur of one interleaved channel (radius r, edges clamped) using a sliding sum.</summary>
        private static void BlurPlane(byte[] buf, int w, int h, int stride, int off, int r)
        {
            if (w <= 1 || h <= 1) return;
            int win = 2 * r + 1;
            var src = new float[w * h];
            for (int i = 0; i < w * h; i++) src[i] = buf[i * stride + off];
            var mid = new float[w * h];
            // horizontal
            for (int y = 0; y < h; y++)
            {
                int row = y * w; float sum = 0f;
                for (int k = -r; k <= r; k++) sum += src[row + Clampi(k, 0, w - 1)];
                for (int x = 0; x < w; x++)
                {
                    mid[row + x] = sum / win;
                    sum += src[row + Clampi(x + r + 1, 0, w - 1)] - src[row + Clampi(x - r, 0, w - 1)];
                }
            }
            // vertical
            for (int x = 0; x < w; x++)
            {
                float sum = 0f;
                for (int k = -r; k <= r; k++) sum += mid[Clampi(k, 0, h - 1) * w + x];
                for (int y = 0; y < h; y++)
                {
                    buf[(y * w + x) * stride + off] = Clamp8(sum / win);
                    sum += mid[Clampi(y + r + 1, 0, h - 1) * w + x] - mid[Clampi(y - r, 0, h - 1) * w + x];
                }
            }
        }

        /// <summary>Parse a CSS filter list into (function, amount) pairs (amount already normalized: % → /100).</summary>
        private static IEnumerable<(string name, float amt)> ParseFilters(string filter)
        {
            int i = 0; var lo = filter.ToLowerInvariant();
            while (i < lo.Length)
            {
                int op = lo.IndexOf('(', i);
                if (op < 0) break;
                int cp = lo.IndexOf(')', op);
                if (cp < 0) break;
                string name = lo.Substring(i, op - i).Trim();
                string arg = lo.Substring(op + 1, cp - op - 1).Trim();
                i = cp + 1;
                if (name.Length == 0) continue;
                float amt;
                if (arg.Length == 0) amt = name == "grayscale" || name == "sepia" || name == "invert" ? 1f : 1f;
                else if (arg.EndsWith("%", StringComparison.Ordinal) && float.TryParse(arg.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pc)) amt = pc / 100f;
                else if (arg.EndsWith("px", StringComparison.Ordinal) && float.TryParse(arg.Substring(0, arg.Length - 2).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pxv)) amt = pxv; // blur(Npx)
                else if (arg.EndsWith("deg", StringComparison.Ordinal) && float.TryParse(arg.Substring(0, arg.Length - 3).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dgv)) amt = dgv; // hue-rotate(Ndeg)
                else if (float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nv)) amt = nv;
                else amt = 1f;
                if (name == "grayscale" || name == "sepia" || name == "invert") amt = Math.Max(0f, Math.Min(1f, amt));
                yield return (name, amt);
            }
        }

        /// <summary>Rasterize a conic-gradient to a <see cref="Images.DecodedImage"/> the size of the box (in px,
        /// capped). Each pixel's angle from the center (clockwise from top, offset by from-angle) selects a
        /// colour by interpolating the angular stops.</summary>
        private static Images.DecodedImage? RasterizeConic(Css.LinearGradient g, float bw, float bh)
        {
            if (g.Stops.Count == 0 || bw <= 0 || bh <= 0) return null;
            int pw = Math.Max(1, Math.Min(700, (int)Math.Round(bw)));
            int ph = Math.Max(1, Math.Min(700, (int)Math.Round(bh)));
            float cx = g.CxFrac * pw, cy = g.CyFrac * ph;
            var rgb = new byte[pw * ph * 3];
            bool hasAlpha = false;
            foreach (var st in g.Stops) if (st.Color.A < 255) { hasAlpha = true; break; }
            byte[]? alpha = hasAlpha ? new byte[pw * ph] : null;
            const float DegPerRad = 180f / (float)Math.PI;
            for (int py = 0; py < ph; py++)
            {
                float dy = (py + 0.5f) - cy;
                for (int px = 0; px < pw; px++)
                {
                    float dx = (px + 0.5f) - cx;
                    float ang = (float)Math.Atan2(dx, -dy) * DegPerRad;   // 0 at top, clockwise
                    float t = ((ang - g.FromAngleDeg) % 360f + 360f) % 360f / 360f;
                    var c = ColorAtT(g.Stops, t);
                    int o = (py * pw + px) * 3;
                    rgb[o] = c.R; rgb[o + 1] = c.G; rgb[o + 2] = c.B;
                    if (alpha != null) alpha[py * pw + px] = c.A;
                }
            }
            return new Images.DecodedImage { Width = pw, Height = ph, Rgb = rgb, Alpha = alpha };
        }

        /// <summary>Rasterize a stack of gradient background LAYERS (top→bottom in CSS order) into one composited RGBA
        /// image — used when 2+ layers carry per-stop alpha, which PDF's stacked luminosity soft-masks composite
        /// incorrectly (each layer's transparent region would reveal the page instead of the layer below). Chrome's
        /// radial-cutout trick (two `transparent…,#fff…` radials cancelling their notches) needs this.</summary>
        private static Images.DecodedImage? RasterizeBgLayers(List<Style.ComputedStyle.BgLayer> layers, float bw, float bh)
        {
            int pw = Math.Max(1, Math.Min(700, (int)Math.Round(bw)));
            int ph = Math.Max(1, Math.Min(700, (int)Math.Round(bh)));
            // Pre-resolve each layer's stops (PosPx → fraction of its own extent) so the pixel loop is cheap.
            var grads = new List<Css.LinearGradient>();
            var stopsList = new List<List<Css.GradientStop>>();
            var geom = new List<(float cx, float cy, float ext, float dx, float dy, float halfLen, bool radial)>();
            foreach (var L in layers)
            {
                var g = L.Grad; if (g == null || g.Conic) continue;
                float cx, cy, ext, dx = 0, dy = 0, half = 0;
                if (g.Radial)
                {
                    cx = g.CxFrac * pw; cy = g.CyFrac * ph;
                    float xF = Math.Max(cx, pw - cx), yF = Math.Max(cy, ph - cy), xC = Math.Min(cx, pw - cx), yC = Math.Min(cy, ph - cy);
                    ext = g.Extent switch { "closest-side" => Math.Min(xC, yC), "farthest-side" => Math.Max(xF, yF), "closest-corner" => (float)Math.Sqrt(xC * xC + yC * yC), _ => (float)Math.Sqrt(xF * xF + yF * yF) };
                }
                else
                {
                    double a = g.AngleDeg * Math.PI / 180.0; dx = (float)Math.Sin(a); dy = (float)-Math.Cos(a);
                    cx = pw / 2f; cy = ph / 2f; half = (float)((Math.Abs(pw * Math.Sin(a)) + Math.Abs(ph * Math.Cos(a))) / 2.0); ext = 1f;
                }
                if (ext <= 0) ext = 1f;
                // resolve stops
                var rs = new List<Css.GradientStop>(g.Stops.Count + 2); float last = 0f;
                float denom = g.Radial ? ext : Math.Max(1f, 2 * half);
                // repeating-*-gradient: TILE the px-positioned stop pattern across the whole extent (grid/hatch fills).
                if (g.Repeating && g.Stops.Count >= 2 && g.Stops[0].PosPx.HasValue && g.Stops[g.Stops.Count - 1].PosPx.HasValue
                    && g.Stops[g.Stops.Count - 1].PosPx!.Value - g.Stops[0].PosPx!.Value > 0.01f)
                {
                    float periodPx = g.Stops[g.Stops.Count - 1].PosPx!.Value - g.Stops[0].PosPx!.Value;
                    bool done = false;
                    for (int k = 0; k < 4000 && !done; k++)
                        foreach (var st in g.Stops)
                        {
                            float pos = ((st.PosPx ?? 0f) + k * periodPx) / denom;
                            if (pos >= 1f) { rs.Add(new Css.GradientStop { Color = st.Color, Pos = 1f }); done = true; break; }
                            pos = Math.Max(last, Math.Max(0f, pos));
                            rs.Add(new Css.GradientStop { Color = st.Color, Pos = pos }); last = pos;
                        }
                }
                else foreach (var st in g.Stops)
                {
                    float pos = st.PosPx.HasValue ? st.PosPx.Value / denom : st.Pos;
                    pos = Math.Max(last, Math.Min(1f, Math.Max(0f, pos))); last = pos;
                    rs.Add(new Css.GradientStop { Color = st.Color, Pos = pos });
                }
                if (rs.Count > 0) { if (rs[0].Pos > 0f) rs.Insert(0, new Css.GradientStop { Color = rs[0].Color, Pos = 0f }); var lz = rs[rs.Count - 1]; if (lz.Pos < 1f) rs.Add(new Css.GradientStop { Color = lz.Color, Pos = 1f }); }
                grads.Add(g); stopsList.Add(rs); geom.Add((cx, cy, ext, dx, dy, half, g.Radial));
            }
            if (grads.Count == 0) return null;
            var rgb = new byte[pw * ph * 3]; var alpha = new byte[pw * ph]; bool anyA = false;
            for (int py = 0; py < ph; py++)
                for (int px = 0; px < pw; px++)
                {
                    float ar = 0, ag = 0, ab = 0, aa = 0;   // straight-alpha accumulator (bottom→top src-over)
                    for (int li = grads.Count - 1; li >= 0; li--)
                    {
                        var gm = geom[li];
                        float t;
                        if (gm.radial) { float ddx = px + 0.5f - gm.cx, ddy = py + 0.5f - gm.cy; t = (float)Math.Sqrt(ddx * ddx + ddy * ddy) / gm.ext; }
                        else { float proj = (px + 0.5f - gm.cx) * gm.dx + (py + 0.5f - gm.cy) * gm.dy; t = (proj + gm.halfLen) / (2 * gm.halfLen); }
                        var c = ColorAtT(stopsList[li], Math.Max(0f, Math.Min(1f, t)));
                        float sa = c.A / 255f;
                        ar = c.R * sa + ar * (1 - sa); ag = c.G * sa + ag * (1 - sa); ab = c.B * sa + ab * (1 - sa); aa = sa + aa * (1 - sa);
                    }
                    int o = (py * pw + px) * 3; int k = py * pw + px;
                    if (aa > 0.001f) { rgb[o] = (byte)Math.Round(ar / aa); rgb[o + 1] = (byte)Math.Round(ag / aa); rgb[o + 2] = (byte)Math.Round(ab / aa); }
                    alpha[k] = (byte)Math.Round(aa * 255); if (alpha[k] != 255) anyA = true;
                }
            return new Images.DecodedImage { Width = pw, Height = ph, Rgb = rgb, Alpha = anyA ? alpha : null };
        }

        /// <summary>Interpolate a colour at fraction t∈[0,1] over ordered gradient stops.</summary>
        private static Render.Color ColorAtT(List<Css.GradientStop> stops, float t)
        {
            if (t <= stops[0].Pos) return stops[0].Color;
            if (t >= stops[stops.Count - 1].Pos) return stops[stops.Count - 1].Color;
            for (int i = 0; i < stops.Count - 1; i++)
            {
                float p0 = stops[i].Pos, p1 = stops[i + 1].Pos;
                if (t >= p0 && t <= p1)
                {
                    float f = p1 > p0 ? (t - p0) / (p1 - p0) : 0f;
                    var a = stops[i].Color; var b = stops[i + 1].Color;
                    // Interpolate in PREMULTIPLIED alpha (CSS gradient rule): otherwise a `transparent` stop
                    // (= transparent BLACK) drags the RGB toward grey through the fade — e.g. transparent→cream
                    // would show a grey ring at the transition instead of fading cream out cleanly.
                    float aa = a.A / 255f, ba = b.A / 255f;
                    float oa = aa + (ba - aa) * f;
                    float pr = a.R * aa + (b.R * ba - a.R * aa) * f;
                    float pg = a.G * aa + (b.G * ba - a.G * aa) * f;
                    float pb = a.B * aa + (b.B * ba - a.B * aa) * f;
                    byte R = oa > 0.0001f ? (byte)Math.Round(pr / oa) : (byte)0;
                    byte G = oa > 0.0001f ? (byte)Math.Round(pg / oa) : (byte)0;
                    byte B = oa > 0.0001f ? (byte)Math.Round(pb / oa) : (byte)0;
                    return new Render.Color(R, G, B, (byte)Math.Round(oa * 255));
                }
            }
            return stops[stops.Count - 1].Color;
        }

        private static float? SizeComp(string tok, float basis, float emPt)
        {
            tok = tok.Trim();
            if (tok == "auto") return null;
            if (tok.EndsWith("%", StringComparison.Ordinal) && float.TryParse(tok.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) return basis * p / 100f;
            return Css.Values.LengthPt(tok, emPt, basis);
        }

        private static (float fx, float fy) ParseBgPos(string pos)
        {
            var toks = pos.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float? fx = null, fy = null;
            foreach (var t in toks)
            {
                switch (t)
                {
                    case "left": fx = 0f; break;
                    case "right": fx = 1f; break;
                    case "top": fy = 0f; break;
                    case "bottom": fy = 1f; break;
                    case "center": if (fx == null) fx = 0.5f; else fy = 0.5f; break;
                    default:
                        if (t.EndsWith("%", StringComparison.Ordinal) && float.TryParse(t.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        { if (fx == null) fx = v / 100f; else fy = v / 100f; }
                        break;
                }
            }
            // A single value leaves the other axis centered (CSS default); nothing at all = top-left.
            if (toks.Length == 1) { fx ??= 0.5f; fy ??= 0.5f; }
            return (fx ?? 0f, fy ?? 0f);
        }

        /// <summary>Walk a styled subtree, mapping each element's DOM node to its computed style (for SvgPainter).</summary>
        private static void CollectStyledMap(StyledNode n, Dictionary<Dom.Node, ComputedStyle> map)
        {
            if (!n.Node.IsText) map[n.Node] = n.Style;
            foreach (var c in n.Children) CollectStyledMap(c, map);
        }

        /// <summary>Intrinsic (natural) width in pt of a replaced element, for max-content sizing.</summary>
        private static float IntrinsicWidthPt(StyledNode n)
        {
            var s = n.Style;
            if (s.Width.HasValue) return s.Width.Value;
            float? attrW = AttrF(n, "width");
            if (attrW.HasValue) return attrW.Value * Lib.PxToPt;
            if (n.Node.Attributes.TryGetValue("data-latex", out var latex))
            {
                var mi = HtmlPdfNative.MathTex.MathRenderer.Render(latex, s.FontSizePt, s.Color, n.Node.Attributes.ContainsKey("data-display"));
                return mi?.WidthPt ?? 100f;
            }
            if (n.Node.Tag == "img")
            {
                n.Node.Attributes.TryGetValue("src", out var src);
                var dec = HtmlPdfNative.Images.ImageLoader.Load(src);
                if (dec != null) return dec.Width * Lib.PxToPt;
                return 100f;
            }
            // <svg>: viewBox width (user units ~ px), else default 300px.
            if (n.Node.Attributes.TryGetValue("viewBox", out var vb))
            {
                var p = vb.Replace(",", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 4 && float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) && w > 0)
                    return w * Lib.PxToPt;
            }
            return 300f * Lib.PxToPt;
        }

        /// <summary>Emit a box's backgrounds + borders, honouring border-radius (rounded fill / clip / stroke).</summary>
        private static void ApplyBoxDecor(LayoutBox box, ComputedStyle s, float bx, float by, float bw, float bh, bool noBorder = false)
        {
            float[]? radii = s.BorderRadius;
            if (radii != null && s.RadiusPct != null)
            {
                // Resolve percentage radii against the box (circular approximation uses the smaller side).
                radii = (float[])radii.Clone();
                float basis = Math.Min(bw, bh);
                for (int i = 0; i < 4; i++) if (s.RadiusPct[i]) radii[i] *= basis;
            }
            float[]? r = ClampRadii(radii, bw, bh);
            bool rounded = r != null;
            box.Radii = r; // remember for overflow:hidden content clipping

            // box-shadow: outset → soft layers behind the box; inset → layers over bg, clipped to the box.
            // Multiple shadows: the FIRST listed paints on top, so accumulate in reverse (first added last).
            if (s.BoxShadows != null)
            {
                for (int si = s.BoxShadows.Count - 1; si >= 0; si--)
                {
                    var sh = s.BoxShadows[si];
                    if (!sh.Inset)
                    {
                        float sx = bx + sh.Dx - sh.Spread, sy = by + sh.Dy - sh.Spread;
                        float sw = bw + 2 * sh.Spread, sbh = bh + 2 * sh.Spread;
                        var sr = new float[4];
                        for (int i = 0; i < 4; i++) sr[i] = Math.Max(0, (r != null ? r[i] : 0f) + sh.Spread);
                        (box.Shadows ??= new List<RoundRect>()).AddRange(BuildShadowLayers(sx, sy, sw, sbh, sr, sh.Blur, sh.Color));
                    }
                    else
                    {
                        (box.InsetShadows ??= new List<RoundRect>()).AddRange(BuildInsetShadowLayers(bx, by, bw, bh, r, sh));
                    }
                }
            }

            // filter: drop-shadow() on a (non-image) element → a soft shadow following the BOX shape (approximation;
            // for boxes with a bg/border this matches, text-only elements would ideally follow the glyph alpha).
            if (s.Filter != null && s.Filter.IndexOf("drop-shadow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var ds = ParseDropShadow(s.Filter);
                if (ds != null)
                {
                    var (ddx, ddy, dblur, dcol) = ds.Value;
                    float sx = bx + ddx * Lib.PxToPt, sy = by + ddy * Lib.PxToPt;
                    var sr = r ?? new float[4];
                    (box.Shadows ??= new List<RoundRect>()).AddRange(BuildShadowLayers(sx, sy, bw, bh, sr, dblur * Lib.PxToPt, dcol));
                }
            }

            // background-clip:text (gradient text) → the Renderer clips the fill to the glyph outlines instead of
            // painting a background rect; flag it here (the fill is still built below and reused as the clip fill).
            box.ClipBgToText = s.BackgroundClip == "text";
            // background-clip: the painted area is the border box (default), padding box, or content box.
            float cl = 0f, ct = 0f, cr = 0f, cbtm = 0f;
            if (s.BackgroundClip == "padding-box" || s.BackgroundClip == "content-box")
            { cl = s.BorderLeft.Width; ct = s.BorderTop.Width; cr = s.BorderRight.Width; cbtm = s.BorderBottom.Width; }
            if (s.BackgroundClip == "content-box")
            { cl += s.PadLeft; ct += s.PadTop; cr += s.PadRight; cbtm += s.PadBottom; }
            float gx = bx + cl, gy = by + ct, gw = Math.Max(0f, bw - cl - cr), gh = Math.Max(0f, bh - ct - cbtm);

            ImageDraw? conic = null;
            if (s.BackgroundGradient != null && s.BackgroundGradient.Conic)
            {
                // PDF has no conic shading — rasterize it to an image XObject clipped to the (clip) box.
                var img = RasterizeConic(s.BackgroundGradient, gw, gh);
                if (img != null)
                {
                    conic = new ImageDraw { X = gx, Y = gy, Width = gw, Height = gh, Image = img, Clip = true, ClipX = gx, ClipY = gy, ClipW = gw, ClipH = gh };
                    if (rounded) { conic.Rtl = r![0]; conic.Rtr = r[1]; conic.Rbr = r[2]; conic.Rbl = r[3]; }
                }
            }
            else if (s.BackgroundGradient != null)
            {
                box.BgGradient = new GradientFill { X = gx, Y = gy, Width = gw, Height = gh, Gradient = s.BackgroundGradient, Alpha = GradientAlpha(s.BackgroundGradient) };
                if (rounded) { box.BgGradient.Rtl = r![0]; box.BgGradient.Rtr = r[1]; box.BgGradient.Rbr = r[2]; box.BgGradient.Rbl = r[3]; }
            }
            else if (s.BackgroundColor.HasValue && s.BackgroundColor.Value.A > 0)
            {
                if (rounded) box.RoundBg = new RoundRect { X = gx, Y = gy, Width = gw, Height = gh, Fill = s.BackgroundColor.Value, Rtl = r![0], Rtr = r[1], Rbr = r[2], Rbl = r[3] };
                else box.Background = new SolidRect { X = gx, Y = gy, Width = gw, Height = gh, Color = s.BackgroundColor.Value };
            }

            box.BgImages = BuildBgImage(s, s.BackgroundImageUrl, bx, by, bw, bh, gx, gy, gw, gh);
            if (box.BgImages != null && rounded)
                foreach (var bi in box.BgImages) { bi.Rtl = r![0]; bi.Rtr = r[1]; bi.Rbr = r[2]; bi.Rbl = r[3]; }
            if (conic != null) { box.BgImages ??= new List<ImageDraw>(); box.BgImages.Insert(0, conic); } // conic paints under any url image

            // Two+ gradient layers with per-stop alpha and no blend/url: rasterize the COMPOSITE to one image (stacked
            // PDF luminosity soft-masks composite wrong — a top layer's transparent notch would reveal the page, not
            // the layer below). This is the radial-cutout trick.
            bool layersRasterized = false;
            if (s.BackgroundLayers != null)
            {
                int gradLayers = 0; bool anyUrl = false, anyVarAlpha = false, anyBlend = false;
                foreach (var L in s.BackgroundLayers)
                {
                    if (L.Url != null) anyUrl = true;
                    if (L.Blend != null && L.Blend != "normal") anyBlend = true;
                    if (L.Grad != null && !L.Grad.Conic) { gradLayers++; foreach (var st in L.Grad.Stops) if (st.Color.A < 255) anyVarAlpha = true; }
                }
                if (gradLayers >= 2 && anyVarAlpha && !anyUrl && !anyBlend)
                {
                    var comp = RasterizeBgLayers(s.BackgroundLayers, gw, gh);
                    if (comp != null)
                    {
                        var idraw = new ImageDraw { X = gx, Y = gy, Width = gw, Height = gh, Image = comp };
                        // Clip the composite raster to the rounded border-radius, else the square image pokes out
                        // past the rounded corners (cream shows outside the border arc → a spurious "circle").
                        if (rounded) { idraw.Rtl = r![0]; idraw.Rtr = r[1]; idraw.Rbr = r[2]; idraw.Rbl = r[3];
                            idraw.Clip = true; idraw.ClipX = gx; idraw.ClipY = gy; idraw.ClipW = gw; idraw.ClipH = gh; }
                        box.BgImages ??= new List<ImageDraw>();
                        box.BgImages.Add(idraw);
                        layersRasterized = true;
                    }
                }
            }

            // Multiple background layers: build each (gradient / image) and paint bottom→top (first CSS layer on top).
            if (s.BackgroundLayers != null && !layersRasterized)
            {
                box.BgLayers = new List<DrawCommand>();
                for (int li = s.BackgroundLayers.Count - 1; li >= 0; li--)   // reverse → first-listed ends up last (top)
                {
                    var layer = s.BackgroundLayers[li];
                    var layerCmds = new List<DrawCommand>();
                    if (layer.Grad != null && !layer.Grad.Conic)
                    {
                        var gf = new GradientFill { X = gx, Y = gy, Width = gw, Height = gh, Gradient = layer.Grad, Alpha = GradientAlpha(layer.Grad) };
                        if (rounded) { gf.Rtl = r![0]; gf.Rtr = r[1]; gf.Rbr = r[2]; gf.Rbl = r[3]; }
                        layerCmds.Add(gf);
                    }
                    else if (layer.Url != null)
                    {
                        var tiles = BuildBgImage(s, layer.Url, bx, by, bw, bh, gx, gy, gw, gh, layer.Size, layer.Position, layer.Repeat);
                        if (tiles != null) { if (rounded) foreach (var bi in tiles) { bi.Rtl = r![0]; bi.Rtr = r[1]; bi.Rbr = r[2]; bi.Rbl = r[3]; } layerCmds.AddRange(tiles); }
                    }
                    // background-blend-mode: blend this layer against the layers/colour below via a /BM group.
                    string bm = layer.Blend != null && layer.Blend != "normal" ? Renderer.BlendPdfName(layer.Blend) : "Normal";
                    if (bm != "Normal" && layerCmds.Count > 0) box.BgLayers.Add(new BlendGroup { Mode = bm, Sub = layerCmds });
                    else box.BgLayers.AddRange(layerCmds);
                }
            }

            // outline: a ring of uniform width drawn OUTSIDE the border box (offset by outline-offset), no layout effect.
            if (s.Outline.Width > 0 && s.Outline.Color.A > 0)
            {
                float ow = s.Outline.Width, gap = s.OutlineOffset;
                float ox = bx - gap - ow, oy = by - gap - ow, obw = bw + 2 * (gap + ow), obh = bh + 2 * (gap + ow);
                var oc = s.Outline.Color;
                string ost = s.Outline.Style ?? "solid";
                void OutEdge(float x, float y, float w, float h, bool horiz)
                {
                    if (ost == "dashed" || ost == "dotted")
                    {
                        float seg = ost == "dotted" ? ow : ow * 2f, gp = ost == "dotted" ? ow : ow * 1.5f, len = horiz ? w : h;
                        for (float o = 0; o < len - 0.1f; o += seg + gp)
                        {
                            float ln = Math.Min(seg, len - o);
                            box.Decorations.Add(horiz ? new SolidRect { X = x + o, Y = y, Width = ln, Height = h, Color = oc } : new SolidRect { X = x, Y = y + o, Width = w, Height = ln, Color = oc });
                        }
                    }
                    else if (ost == "double" && ow >= 3f)
                    {
                        float t = ow / 3f;
                        box.Decorations.Add(new SolidRect { X = x, Y = y, Width = horiz ? w : t, Height = horiz ? t : h, Color = oc });
                        box.Decorations.Add(horiz ? new SolidRect { X = x, Y = y + ow - t, Width = w, Height = t, Color = oc } : new SolidRect { X = x + ow - t, Y = y, Width = t, Height = h, Color = oc });
                    }
                    else box.Decorations.Add(new SolidRect { X = x, Y = y, Width = w, Height = h, Color = oc });
                }
                OutEdge(ox, oy, obw, ow, true);                 // top
                OutEdge(ox, oy + obh - ow, obw, ow, true);      // bottom
                OutEdge(ox, oy, ow, obh, false);                // left
                OutEdge(ox + obw - ow, oy, ow, obh, false);     // right
            }

            // border-image: a 9-slice bitmap replaces the normal border (stretch only; no round/repeat/space).
            if (s.BorderImageSource != null)
            {
                var bimg = HtmlPdfNative.Images.ImageLoader.Load(s.BorderImageSource);
                if (bimg != null && DrawBorderImage(box, s, bx, by, bw, bh, bimg)) return;
            }

            if (noBorder) return; // collapsed table cells draw no own border (the table draws merged grid lines)

            // Uniform border + radius → a single rounded stroke inset half its width (stays inside the border box).
            if (rounded && UniformBorder(s) && s.BorderTop.Width > 0)
            {
                float hw = s.BorderTop.Width / 2f;
                box.RoundBorder = new RoundRect
                {
                    X = bx + hw, Y = by + hw, Width = Math.Max(0, bw - 2 * hw), Height = Math.Max(0, bh - 2 * hw),
                    Stroke = s.BorderTop.Color, StrokeW = s.BorderTop.Width,
                    Rtl = Math.Max(0, r![0] - hw), Rtr = Math.Max(0, r[1] - hw), Rbr = Math.Max(0, r[2] - hw), Rbl = Math.Max(0, r[3] - hw),
                };
            }
            else AddBorders(box, s, bx, by, bw, bh, rounded ? r : null);
        }

        /// <summary>A gradient's uniform stop-alpha as a 0..1 fill opacity (applied via ExtGState); 1 when stops
        /// have varying alpha (per-stop alpha would need a soft-mask shading, not done).</summary>
        private static float GradientAlpha(Css.LinearGradient g)
        {
            if (g.Stops == null || g.Stops.Count == 0) return 1f;
            byte a = g.Stops[0].Color.A;
            foreach (var st in g.Stops) if (st.Color.A != a) return 1f;
            return a / 255f;
        }

        /// <summary>Draw a border-image as a 9-slice: 4 corners (unscaled ratio) + 4 stretched edges, each a
        /// scaled full-image draw clipped to its border region. Returns false if the slice/border is degenerate.</summary>
        private static bool DrawBorderImage(LayoutBox box, ComputedStyle s, float bx, float by, float bw, float bh, Images.DecodedImage dec)
        {
            float sw = dec.Width, sh = dec.Height;
            var sl = s.BorderImageSlice; // T R B L, in source px (or % of source dims)
            float st = s.BorderImageSlicePct ? sl[0] / 100f * sh : sl[0];
            float sr = s.BorderImageSlicePct ? sl[1] / 100f * sw : sl[1];
            float sb = s.BorderImageSlicePct ? sl[2] / 100f * sh : sl[2];
            float slc = s.BorderImageSlicePct ? sl[3] / 100f * sw : sl[3];
            float bt = s.BorderTop.Width, brw = s.BorderRight.Width, bb = s.BorderBottom.Width, bl = s.BorderLeft.Width;
            if ((st <= 0 && sr <= 0 && sb <= 0 && slc <= 0) || (bt <= 0 && brw <= 0 && bb <= 0 && bl <= 0)) return false;
            var list = box.BgImages ??= new List<ImageDraw>();
            // Draw source region (sx,sy,swid,shei px) into target rect (tx,ty,tw,th pt), clipped.
            void Slice(float sx, float sy, float swid, float shei, float tx, float ty, float tw, float th)
            {
                if (swid <= 0 || shei <= 0 || tw <= 0 || th <= 0) return;
                float scaleX = tw / swid, scaleY = th / shei; // pt per source px
                list.Add(new ImageDraw { X = tx - sx * scaleX, Y = ty - sy * scaleY, Width = sw * scaleX, Height = sh * scaleY, Image = dec, Clip = true, ClipX = tx, ClipY = ty, ClipW = tw, ClipH = th });
            }
            // Tile a source edge strip along a target edge per border-image-repeat (stretch/repeat/round[/space≈repeat]).
            void TileEdge(float sx, float sy, float swid, float shei, float tx, float ty, float tw, float th, bool horiz, string mode)
            {
                if (swid <= 0 || shei <= 0 || tw <= 0 || th <= 0) return;
                float along = horiz ? tw : th;                       // target length along the edge
                if (mode == "stretch") { Slice(sx, sy, swid, shei, tx, ty, tw, th); return; }
                float thick = horiz ? th : tw, sThick = horiz ? shei : swid;
                float scale = thick / sThick;                        // thickness match → uniform tile scale
                float tileLen = (horiz ? swid : shei) * scale;
                if (tileLen <= 0.5f) { Slice(sx, sy, swid, shei, tx, ty, tw, th); return; }
                int nTiles = Math.Max(1, mode == "round" ? (int)Math.Round(along / tileLen) : (int)Math.Floor(along / tileLen + 1e-3));
                if (mode == "round") tileLen = along / nTiles;
                float start = mode == "round" ? 0f : (along - nTiles * tileLen) / 2f; // repeat centres the run
                for (int k = 0; k < nTiles; k++)
                {
                    float o = start + k * tileLen;
                    if (horiz) Slice(sx, sy, swid, shei, tx + o, ty, tileLen, th);
                    else Slice(sx, sy, swid, shei, tx, ty + o, tw, tileLen);
                }
            }
            float rx = bx + bw, ry = by + bh;
            string mh = s.BorderImageRepeatH, mv = s.BorderImageRepeatV;
            Slice(0, 0, slc, st, bx, by, bl, bt);                                   // top-left corner
            Slice(sw - sr, 0, sr, st, rx - brw, by, brw, bt);                        // top-right
            Slice(0, sh - sb, slc, sb, bx, ry - bb, bl, bb);                         // bottom-left
            Slice(sw - sr, sh - sb, sr, sb, rx - brw, ry - bb, brw, bb);             // bottom-right
            TileEdge(slc, 0, sw - slc - sr, st, bx + bl, by, bw - bl - brw, bt, true, mh);        // top edge
            TileEdge(slc, sh - sb, sw - slc - sr, sb, bx + bl, ry - bb, bw - bl - brw, bb, true, mh); // bottom edge
            TileEdge(0, st, slc, sh - st - sb, bx, by + bt, bl, bh - bt - bb, false, mv);         // left edge
            TileEdge(sw - sr, st, sr, sh - st - sb, rx - brw, by + bt, brw, bh - bt - bb, false, mv); // right edge
            if (s.BorderImageFill)                                                   // 'fill' → paint the middle too
                Slice(slc, st, sw - slc - sr, sh - st - sb, bx + bl, by + bt, bw - bl - brw, bh - bt - bb);
            return true;
        }

        /// <summary>Build the soft-shadow layers: concentric rounded rects whose per-layer alpha accumulates
        /// toward the centre, approximating a Gaussian edge of half-width ≈ blur.</summary>
        private static List<RoundRect> BuildShadowLayers(float x, float y, float w, float h, float[] radii, float blur, Color color)
        {
            var list = new List<RoundRect>();
            if (w <= 0 || h <= 0) return list;
            if (blur <= 0.5f)
            {
                list.Add(new RoundRect { X = x, Y = y, Width = w, Height = h, Rtl = radii[0], Rtr = radii[1], Rbr = radii[2], Rbl = radii[3], Fill = color });
                return list;
            }
            // More layers for a wider blur → finer bands (~1pt apart) so the falloff reads as a smooth gradient
            // instead of a visibly stepped rounded-corner arc (Chrome renders a true Gaussian).
            int steps = Math.Max(6, Math.Min(24, (int)Math.Round(blur * 1.2f)));
            // A blurred shadow never reaches full opacity even at the box edge — the blur spreads it out. Cap the
            // PEAK accumulated alpha below 1 so an OPAQUE shadow colour (e.g. `#888888`) still fades across the
            // blur into a soft gradient (matching the Rust engine / Chrome) instead of stacking equal fully-opaque
            // layers into a hard, uniform band. Translucent shadows (alpha < cap) are unaffected.
            double peak = Math.Min(color.A / 255.0, 0.9);
            double ca = 1.0 - Math.Pow(1.0 - peak, 1.0 / steps);
            byte la = (byte)Math.Max(1, Math.Min(255, (int)Math.Round(ca * 255)));
            var layerColor = new Color(color.R, color.G, color.B, la);
            for (int i = 0; i < steps; i++)   // largest first (bottom) → smallest last (top)
            {
                // Quadratic spacing (denser near the outer edge) approximates a Gaussian tail, softening the rim.
                float f = (float)(steps - 1 - i) / (steps - 1);
                float g = blur * f * f;
                list.Add(new RoundRect
                {
                    X = x - g, Y = y - g, Width = w + 2 * g, Height = h + 2 * g,
                    Rtl = radii[0] + g, Rtr = radii[1] + g, Rbr = radii[2] + g, Rbl = radii[3] + g,
                    Fill = layerColor,
                });
            }
            return list;
        }

        /// <summary>Build inset box-shadow layers: concentric rounded-rect STROKES clipped to the box interior,
        /// fading from the (offset+spread) edge inward over ≈blur. Offsetting by (dx,dy) darkens the opposite
        /// inner edges (CSS: a positive dx casts the inner shadow on the left).</summary>
        private static List<RoundRect> BuildInsetShadowLayers(float bx, float by, float bw, float bh, float[]? radii, ComputedStyle.Shadow sh)
        {
            var list = new List<RoundRect>();
            if (bw <= 0 || bh <= 0) return list;
            float[] br = radii ?? new float[4];
            float blur = Math.Max(sh.Blur, 0f);
            float spread = Math.Max(sh.Spread, 0f);
            double targetA = sh.Color.A / 255.0;

            void AddRing(float inset, float strokeW, byte alpha)
            {
                float rw = bw - 2 * inset, rh = bh - 2 * inset;
                if (rw <= 0 || rh <= 0) return;
                var col = new Color(sh.Color.R, sh.Color.G, sh.Color.B, alpha);
                list.Add(new RoundRect
                {
                    X = bx + sh.Dx + inset, Y = by + sh.Dy + inset, Width = rw, Height = rh,
                    Rtl = Math.Max(0, br[0] - inset), Rtr = Math.Max(0, br[1] - inset), Rbr = Math.Max(0, br[2] - inset), Rbl = Math.Max(0, br[3] - inset),
                    Stroke = col, StrokeW = strokeW,
                    Clip = true, ClipX = bx, ClipY = by, ClipW = bw, ClipH = bh,        // clip to box interior
                    ClipRtl = br[0], ClipRtr = br[1], ClipRbr = br[2], ClipRbl = br[3],
                });
            }

            // Solid part: a band of width ≈spread hugging the edge (e.g. `inset 0 0 0 4px` = 4px inner border).
            if (spread > 0f)
            {
                float w = spread;
                AddRing(w / 2f, w, (byte)Math.Round(targetA * 255));
            }
            // Soft blur fade inward beyond the solid part (concentric strokes, alpha accumulating).
            if (blur > 0.5f)
            {
                const int steps = 6;
                double ca = 1.0 - Math.Pow(1.0 - targetA, 1.0 / steps);
                byte la = (byte)Math.Max(1, Math.Min(255, (int)Math.Round(ca * 255)));
                float sw = blur;
                for (int i = 0; i < steps; i++)
                {
                    float inset = spread + sw / 2f + blur * i / (steps - 1);
                    AddRing(inset, sw, la);
                }
            }
            else if (spread <= 0f)
            {
                // Hairline inset (no blur, no spread): a thin edge ring.
                AddRing(0.75f, 1.5f, (byte)Math.Round(targetA * 255));
            }
            return list;
        }

        private static float[]? ClampRadii(float[]? r, float bw, float bh)
        {
            if (r == null) return null;
            float max = Math.Min(bw, bh) / 2f;
            bool any = false;
            var c = new float[4];
            for (int i = 0; i < 4; i++) { c[i] = Math.Max(0, Math.Min(r[i], max)); if (c[i] > 0) any = true; }
            return any ? c : null;
        }

        private static bool UniformBorder(ComputedStyle s) =>
            s.BorderTop.Width > 0 &&
            s.BorderTop.Width == s.BorderRight.Width && s.BorderTop.Width == s.BorderBottom.Width && s.BorderTop.Width == s.BorderLeft.Width &&
            s.BorderTop.Color.R == s.BorderRight.Color.R && s.BorderTop.Color.G == s.BorderRight.Color.G && s.BorderTop.Color.B == s.BorderRight.Color.B &&
            s.BorderTop.Color.R == s.BorderBottom.Color.R && s.BorderTop.Color.R == s.BorderLeft.Color.R;

        /// <summary>True when a table cell has no content (only whitespace text, no element/replaced children) —
        /// the trigger for <c>empty-cells:hide</c>.</summary>
        private static bool CellIsEmpty(StyledNode cell)
        {
            foreach (var ch in cell.Children)
            {
                if (ch.IsText) { if (!string.IsNullOrWhiteSpace(ch.Text)) return false; }
                else return false; // any element/replaced child (incl. ::before/::after) makes it non-empty
            }
            return true;
        }

        private static void AddBorders(LayoutBox box, ComputedStyle s, float bx, float by, float bw, float bh, float[]? clipRadii = null)
        {
            int decoStart = box.Decorations.Count;
            // Darken/lighten a border colour for the 3D bevel styles (groove/ridge/inset/outset).
            Color Shade(Color c, float f) => new Color(
                (byte)Math.Max(0, Math.Min(255, c.R * f)), (byte)Math.Max(0, Math.Min(255, c.G * f)),
                (byte)Math.Max(0, Math.Min(255, c.B * f)), c.A);
            // e = edge, (x,y,w,h) = the edge rect, horizontal = top/bottom (segment along X) vs left/right (along Y).
            // topLeft = true for the top/left edges (which take the "light" shade under outset, "dark" under inset).
            void Add(BorderEdge e, float x, float y, float w, float h, bool horizontal, bool topLeft)
            {
                if (e.Width <= 0 || e.Color.A <= 0 || w <= 0 || h <= 0) return;
                string st = e.Style ?? "solid";
                if (st == "dashed" || st == "dotted")
                {
                    float t = e.Width;
                    float seg = st == "dotted" ? t : t * 2f;
                    float gap = st == "dotted" ? t : t * 1.5f;
                    float len = horizontal ? w : h;
                    for (float o = 0; o < len - 0.1f; o += seg + gap)
                    {
                        float ln = Math.Min(seg, len - o);
                        box.Decorations.Add(horizontal
                            ? new SolidRect { X = x + o, Y = y, Width = ln, Height = h, Color = e.Color }
                            : new SolidRect { X = x, Y = y + o, Width = w, Height = ln, Color = e.Color });
                    }
                }
                else if (st == "double" && e.Width >= 3f)
                {
                    float t = e.Width / 3f;
                    box.Decorations.Add(new SolidRect { X = x, Y = y, Width = horizontal ? w : t, Height = horizontal ? t : h, Color = e.Color });
                    box.Decorations.Add(horizontal
                        ? new SolidRect { X = x, Y = y + e.Width - t, Width = w, Height = t, Color = e.Color }
                        : new SolidRect { X = x + e.Width - t, Y = y, Width = t, Height = h, Color = e.Color });
                }
                else if (st == "inset" || st == "outset")
                {
                    // inset: top/left dark, bottom/right light; outset: the reverse.
                    bool dark = (st == "inset") ? topLeft : !topLeft;
                    box.Decorations.Add(new SolidRect { X = x, Y = y, Width = w, Height = h, Color = Shade(e.Color, dark ? 0.5f : 1f) });
                }
                else if (st == "groove" || st == "ridge")
                {
                    // Two halves per edge with opposite shades → a carved (groove) / raised (ridge) bevel.
                    // groove top/left: outer half dark, inner half light; ridge is the mirror.
                    bool outerDark = (st == "groove") ? topLeft : !topLeft;
                    Color outer = Shade(e.Color, outerDark ? 0.5f : 1f);
                    Color inner = Shade(e.Color, outerDark ? 1f : 0.5f);
                    if (horizontal)
                    {
                        float half = h / 2f;
                        // top edge: outer=top strip; bottom edge: outer=bottom strip (away from content).
                        float outerY = topLeft ? y : y + half;
                        float innerY = topLeft ? y + half : y;
                        box.Decorations.Add(new SolidRect { X = x, Y = outerY, Width = w, Height = half, Color = outer });
                        box.Decorations.Add(new SolidRect { X = x, Y = innerY, Width = w, Height = half, Color = inner });
                    }
                    else
                    {
                        float half = w / 2f;
                        float outerX = topLeft ? x : x + half;
                        float innerX = topLeft ? x + half : x;
                        box.Decorations.Add(new SolidRect { X = outerX, Y = y, Width = half, Height = h, Color = outer });
                        box.Decorations.Add(new SolidRect { X = innerX, Y = y, Width = half, Height = h, Color = inner });
                    }
                }
                else box.Decorations.Add(new SolidRect { X = x, Y = y, Width = w, Height = h, Color = e.Color });
            }
            // CSS-triangle case: when the content box collapses (width or height ≈ 0), borders meet at 45° miters and
            // each colored edge is a TRIANGLE/trapezoid, not a rectangle (the classic `width:0;border:…` skyline trick).
            float cw = bw - s.BorderLeft.Width - s.BorderRight.Width, chh = bh - s.BorderTop.Width - s.BorderBottom.Width;
            bool plain = (s.BorderTop.Style ?? "solid") == "solid" && (s.BorderBottom.Style ?? "solid") == "solid"
                      && (s.BorderLeft.Style ?? "solid") == "solid" && (s.BorderRight.Style ?? "solid") == "solid";
            if (plain && (cw <= 0.5f || chh <= 0.5f) && (bw > 0.5f && bh > 0.5f))
            {
                float x0 = bx, x1 = bx + s.BorderLeft.Width, x2 = bx + bw - s.BorderRight.Width, x3 = bx + bw;
                float y0 = by, y1 = by + s.BorderTop.Width, y2 = by + bh - s.BorderBottom.Width, y3 = by + bh;
                void Tri(BorderEdge e, float[] pts) { if (e.Width > 0 && e.Color.A > 0) (box.Polygons ??= new List<Polygon>()).Add(new Polygon { Points = pts, Color = e.Color }); }
                Tri(s.BorderTop, new[] { x0, y0, x3, y0, x2, y1, x1, y1 });      // top trapezoid
                Tri(s.BorderBottom, new[] { x1, y2, x2, y2, x3, y3, x0, y3 });   // bottom trapezoid
                Tri(s.BorderLeft, new[] { x0, y0, x1, y1, x1, y2, x0, y3 });     // left trapezoid
                Tri(s.BorderRight, new[] { x2, y1, x3, y0, x3, y3, x2, y2 });    // right trapezoid
                return;
            }
            Add(s.BorderTop, bx, by, bw, s.BorderTop.Width, true, true);
            Add(s.BorderBottom, bx, by + bh - s.BorderBottom.Width, bw, s.BorderBottom.Width, true, false);
            Add(s.BorderLeft, bx, by, s.BorderLeft.Width, bh, false, true);
            Add(s.BorderRight, bx + bw - s.BorderRight.Width, by, s.BorderRight.Width, bh, false, false);

            // border-radius on a NON-uniform border: clip the (square-cornered) edge rects to the rounded outer
            // border-box so the corners round off — matches the Rust engine / Chrome for e.g. a coloured
            // `border-top:4px` + thin `border:1px` on a `border-radius` card (Example15 stat-card).
            if (clipRadii != null && (clipRadii[0] > 0 || clipRadii[1] > 0 || clipRadii[2] > 0 || clipRadii[3] > 0))
                for (int i = decoStart; i < box.Decorations.Count; i++)
                {
                    var d = box.Decorations[i];
                    d.Clip = true; d.ClipX = bx; d.ClipY = by; d.ClipW = bw; d.ClipH = bh;
                    d.ClipRtl = clipRadii[0]; d.ClipRtr = clipRadii[1]; d.ClipRbr = clipRadii[2]; d.ClipRbl = clipRadii[3];
                }
        }

        // ---- inline layout ------------------------------------------------------------------------

        private struct Word
        {
            public string Text; public ComputedStyle Style; public bool Break; public Fonts.EmbeddedFont? Emb;
            public LayoutBox? Atomic; public float AtomicW, AtomicH; // display:inline-block atomic box
            public ComputedStyle? BgStyle;                            // nearest inline ancestor with a background
            public float SpaceBefore;                                 // leading space-widths (1 = normal gap; preserved count for pre)
            public List<int>? SoftBreaks;                             // U+00AD positions (in Text) where a hyphenated break is allowed
        }

        private static float WordWidth(in Word w)
        {
            if (w.Atomic != null) return w.AtomicW;
            float baseW = w.Emb != null ? w.Emb.MeasurePt(w.Text, w.Style.FontSizePt) : Afm.MeasurePt(w.Style.Face, w.Text, w.Style.FontSizePt);
            // letter-spacing adds after each glyph (incl. the last, before the inter-word gap).
            if (w.Style.LetterSpacing != 0f && w.Text != null) baseW += w.Style.LetterSpacing * w.Text.Length;
            return baseW;
        }

        /// <summary>Baseline shift (pt, positive = UP toward the page top) for a run's vertical-align.
        /// super/sub/middle/top/bottom are relative to the parent's font size; length/% resolve directly.</summary>
        private static float VerticalAlignShift(ComputedStyle s, float refFs)
        {
            switch (s.VerticalAlign)
            {
                case "baseline": return 0f;
                case "super": return 0.34f * refFs;
                case "sub": return -0.20f * refFs;
                case "top": case "text-top": return 0.34f * refFs;
                case "bottom": case "text-bottom": return -0.30f * refFs;
                case "middle": return 0.24f * refFs;
                default:
                    string v = s.VerticalAlign;
                    if (v.EndsWith("%", StringComparison.Ordinal) &&
                        float.TryParse(v.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p))
                        return p / 100f * s.FontSizePt;   // % of the run's line-height ≈ its font size
                    var l = Css.Values.LengthPt(v, s.FontSizePt);
                    return l ?? 0f;
            }
        }

        /// <summary>Number of leading chars of a word that fit within <paramref name="maxW"/> pt (0 if none).</summary>
        private static int SplitToFit(in Word w, float maxW)
        {
            string t = w.Text; if (string.IsNullOrEmpty(t)) return 0;
            float acc = 0f, ls = w.Style.LetterSpacing, fs = w.Style.FontSizePt; int n = 0;
            for (int k = 0; k < t.Length; k++)
            {
                string ch = t[k].ToString();
                float cw = (w.Emb != null ? w.Emb.MeasurePt(ch, fs) : Afm.MeasurePt(w.Style.Face, ch, fs)) + ls;
                if (acc + cw > maxW + 0.01f) break;
                acc += cw; n++;
            }
            return n;
        }

        /// <summary>Remove U+00AD soft hyphens from a token, returning positions (in the clean text) after which
        /// a hyphenated line break is permitted.</summary>
        private static string StripSoftHyphens(string tok, out List<int>? breaks)
        {
            breaks = null;
            var sb = new System.Text.StringBuilder(tok.Length);
            foreach (char c in tok)
            {
                if (c == '­') (breaks ??= new List<int>()).Add(sb.Length);
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Try to break a word at a soft-hyphen position to fit <paramref name="avail"/> pt; returns the
        /// head (with a trailing '-') and the remaining tail word, or null if no break point fits.</summary>
        private static (Word head, Word tail)? SplitAtSoftHyphen(in Word w, float avail)
        {
            if (w.SoftBreaks == null || w.Text == null) return null;
            for (int bi = w.SoftBreaks.Count - 1; bi >= 0; bi--)
            {
                int k = w.SoftBreaks[bi];
                if (k <= 0 || k >= w.Text.Length) continue;
                string headText = w.Text.Substring(0, k) + "-";
                float hw = (w.Emb != null ? w.Emb.MeasurePt(headText, w.Style.FontSizePt) : Afm.MeasurePt(w.Style.Face, headText, w.Style.FontSizePt)) + w.Style.LetterSpacing * headText.Length;
                if (hw <= avail + 0.01f)
                {
                    var head = new Word { Text = headText, Style = w.Style, Emb = FontManager.ResolveForWord(w.Style, headText), BgStyle = w.BgStyle, SpaceBefore = w.SpaceBefore };
                    var tailBreaks = new List<int>();
                    for (int j = bi + 1; j < w.SoftBreaks.Count; j++) tailBreaks.Add(w.SoftBreaks[j] - k);
                    string tailText = w.Text.Substring(k);
                    var tail = new Word { Text = tailText, Style = w.Style, Emb = FontManager.ResolveForWord(w.Style, tailText), BgStyle = w.BgStyle, SpaceBefore = 0f, SoftBreaks = tailBreaks.Count > 0 ? tailBreaks : null };
                    return (head, tail);
                }
            }
            return null;
        }

        /// <summary>Truncate a single overflowing line to fit <paramref name="width"/> and append an ellipsis word.</summary>
        private static List<Word> TruncateWithEllipsis(List<Word> line, float width, ComputedStyle context)
        {
            float ellW = Afm.MeasurePt(context.Face, "…", context.FontSizePt);
            float avail = Math.Max(0, width - ellW);
            var outw = new List<Word>();
            float acc = 0f;
            foreach (var w in line)
            {
                float sw = outw.Count > 0 ? Afm.MeasurePt(w.Style.Face, " ", w.Style.FontSizePt) : 0f;
                float ww = WordWidth(w);
                if (acc + sw + ww <= avail + 0.01f) { outw.Add(w); acc += sw + ww; continue; }
                // Partially truncate this (text) word to fill the remaining space.
                if (w.Atomic == null)
                {
                    float rem = avail - acc - sw;
                    int cut = SplitToFit(w, rem);
                    if (cut >= 1) outw.Add(MakeSub(w, 0, cut));
                }
                break;
            }
            outw.Add(new Word { Text = "…", Style = context, SpaceBefore = 0f, Emb = FontManager.ResolveForWord(context, "…") });
            return outw;
        }

        /// <summary>Clone a word with a substring of its text (re-resolving the embedded font).</summary>
        private static Word MakeSub(in Word w, int start, int end)
        {
            string t = w.Text.Substring(start, end - start);
            return new Word { Text = t, Style = w.Style, Emb = FontManager.ResolveForWord(w.Style, t), BgStyle = w.BgStyle, SpaceBefore = start == 0 ? w.SpaceBefore : 0f };
        }

        private static float LayoutInline(List<StyledNode> inlines, ComputedStyle context,
            float x, float top, float width, LayoutBox box, FloatCtx? floats = null, LayoutBox? posHost = null, ComputedStyle? firstLetter = null, string? markerPrefix = null, ComputedStyle? firstLineStyle = null, int maxLines = 0)
        {
            var outRuns = box.TextRuns;
            var words = new List<Word>();
            var wsCtx = new WsCtx();
            foreach (var inl in inlines) FlattenWords(inl, words, width, posHost, null, wsCtx);
            if (words.Count == 0 && markerPrefix == null) return 0f;

            // list-style-position:inside — prepend the marker as the first inline word (glued, no leading space).
            if (markerPrefix != null)
                words.Insert(0, new Word { Text = markerPrefix, Style = context, Emb = FontManager.ResolveForWord(context, markerPrefix), SpaceBefore = 0f });

            // ::first-letter — split the first char of the first text word into its own styled word.
            if (firstLetter != null)
                for (int wi = 0; wi < words.Count; wi++)
                {
                    var fw = words[wi];
                    if (fw.Atomic != null || string.IsNullOrEmpty(fw.Text)) continue;
                    // include a leading punctuation char with the first letter (CSS behaviour)
                    int li = 0; while (li < fw.Text.Length && !char.IsLetterOrDigit(fw.Text[li])) li++;
                    int take = Math.Min(fw.Text.Length, li + 1);
                    string flText = fw.Text.Substring(0, take), restText = fw.Text.Substring(take);
                    var flEmb = FontManager.ResolveForWord(firstLetter, flText);
                    // A FLOATED ::first-letter (drop cap) is pulled OUT of the line and laid out as a real float, so
                    // the following lines wrap beside it (its own line-box height) — instead of the big glyph merely
                    // inflating the FIRST line (which then fits only one line beside it). Example32.
                    if (firstLetter.Float == "left" || firstLetter.Float == "right")
                    {
                        float glyphW = flEmb != null ? flEmb.MeasurePt(flText, firstLetter.FontSizePt) : Afm.MeasurePt(firstLetter.Face, flText, firstLetter.FontSizePt);
                        float fW = glyphW + firstLetter.PadLeft + firstLetter.PadRight + firstLetter.MarginLeft + firstLetter.MarginRight;
                        // Reserve a WHOLE number of following lines that the drop-cap box spans (like the Rust engine),
                        // so the text wraps beside exactly that many lines then clears — e.g. a 42px/0.8 cap over 12px
                        // body text spans 2 lines.
                        float bodyLh = context.EffectiveLineHeightPt;
                        int nLines = bodyLh > 0.1f ? Math.Max(1, (int)Math.Round(firstLetter.EffectiveLineHeightPt / bodyLh)) : 1;
                        float fH = nLines * bodyLh;
                        bool leftF = firstLetter.Float != "right";
                        float fx0 = leftF ? x : x + width - fW;
                        floats ??= new FloatCtx();
                        floats.F.Add((fx0, fx0 + fW, top, top + fH, leftF));
                        // Sit the cap ON the baseline of the LAST body line it spans (line `nLines`), so its bottom
                        // rests on that line and doesn't hang down into the following line. Body baselines are
                        // lineTop + 0.8·lineHeight (see the line-metrics below).
                        float dcBaseline = top + (nLines - 1) * bodyLh + bodyLh * 0.8f;
                        outRuns.Add(new TextRun { X = fx0 + firstLetter.MarginLeft + firstLetter.PadLeft, BaselineY = dcBaseline,
                            Text = flText, FontSizePt = firstLetter.FontSizePt, Face = firstLetter.Face, Color = firstLetter.Color, Emb = flEmb });
                        if (restText.Length > 0) words[wi] = new Word { Text = restText, Style = fw.Style, Emb = FontManager.ResolveForWord(fw.Style, restText), BgStyle = fw.BgStyle, SpaceBefore = fw.SpaceBefore };
                        else words.RemoveAt(wi);
                        break;
                    }
                    var flWord = new Word { Text = flText, Style = firstLetter, Emb = flEmb, BgStyle = fw.BgStyle, SpaceBefore = fw.SpaceBefore };
                    words[wi] = flWord;
                    if (restText.Length > 0) words.Insert(wi + 1, new Word { Text = restText, Style = fw.Style, Emb = FontManager.ResolveForWord(fw.Style, restText), BgStyle = fw.BgStyle, SpaceBefore = 0f });
                    break;
                }

            bool noWrap = context.WhiteSpace == "nowrap" || context.WhiteSpace == "pre" || context.TextWrap == "nowrap";
            bool preserve = context.WhiteSpace == "pre" || context.WhiteSpace == "pre-wrap";
            bool breakAll = context.WordBreak == "break-all";
            bool breakWord = context.OverflowWrap == "break-word" || context.OverflowWrap == "anywhere" || context.WordBreak == "break-word";

            float y = top;
            int i = 0;
            bool firstLine = true;
            int lineNo = 0;   // for -webkit-line-clamp
            float textIndent = context.TextIndent + context.TextIndentPct * width; // first-line indent (pt)
            // ::first-line font-size: scale the first line's word widths / line-height / glyphs by this ratio (1 = no change).
            float flScale = (firstLineStyle != null && context.FontSizePt > 0.01f) ? firstLineStyle.FontSizePt / context.FontSizePt : 1f;
            while (i < words.Count)
            {
                // Narrow this line's usable band to the space left by any floats overlapping it.
                float lineX = x, lineWidth = width;
                if (floats != null)
                {
                    float lh0 = context.EffectiveLineHeightPt;
                    var (aL, aR) = floats.Avail(x, x + width, y, y + lh0);
                    // If floats leave no usable width, drop past the nearest float bottom and retry.
                    int guard = 0;
                    while (aR - aL < 1f && guard++ < 64)
                    {
                        float nb = floats.NextBottomBelow(y);
                        if (nb <= y) break;
                        y = nb;
                        (aL, aR) = floats.Avail(x, x + width, y, y + lh0);
                    }
                    lineX = aL; lineWidth = Math.Max(1f, aR - aL);
                }

                // text-indent shifts the first line's start and shrinks its usable width.
                float indent = firstLine ? textIndent : 0f;
                if (indent != 0f) { lineX += indent; lineWidth = Math.Max(1f, lineWidth - indent); }

                var line = new List<Word>();
                float lineW = 0f, lineHeight = 0f;
                float maxAscent = 0f, maxDescent = 0f;   // CSS §10.8 line-box height: extent above/below the baseline
                bool forced = false;
                while (i < words.Count)
                {
                    var w = words[i];
                    if (w.Break) { forced = true; i++; break; }
                    float lscale = firstLine ? flScale : 1f;   // ::first-line font-size scale (1 = unchanged)
                    float ww = WordWidth(w) * lscale;
                    float sw = Afm.MeasurePt(w.Style.Face, " ", w.Style.FontSizePt) * lscale;
                    float sp = line.Count == 0 ? (preserve ? w.SpaceBefore * sw : 0f) : w.SpaceBefore * sw + w.Style.WordSpacing;

                    // Soft-hyphen (&shy;): if the word overflows the line, break at a soft-hyphen point (shows '-').
                    if (!noWrap && w.SoftBreaks != null && lineW + sp + ww > lineWidth + 0.01f)
                    {
                        var split = SplitAtSoftHyphen(w, lineWidth - lineW - sp);
                        if (split.HasValue)
                        {
                            var (head, tail) = split.Value;
                            line.Add(head); lineW += sp + WordWidth(head) * lscale;
                            lineHeight = Math.Max(lineHeight, head.Style.EffectiveLineHeightPt * lscale);
                            words[i] = tail;
                            break;
                        }
                    }

                    // Break a long word (overflow-wrap:break-word / word-break:break-all) so it doesn't overflow.
                    if (!noWrap && w.Atomic == null && ww > 0.01f && (breakAll || breakWord) && w.Text.Length > 1)
                    {
                        float avail = lineWidth - lineW - sp;
                        bool overflowsAlone = ww > lineWidth + 0.01f;          // won't fit even on its own line
                        bool overflowsHere = lineW + sp + ww > lineWidth + 0.01f;
                        // break-all: break anywhere to fill the current line. break-word (last resort): only once the
                        // word is alone on its line and STILL overflows (it first soft-wraps to a fresh line).
                        bool doBreak = (breakAll && overflowsHere && avail > 0.5f) || (breakWord && overflowsAlone && line.Count == 0);
                        if (doBreak)
                        {
                            int cut = SplitToFit(w, avail);
                            if (cut < 1 && line.Count == 0) cut = 1; // guarantee progress when the line is empty
                            if (cut >= 1 && cut < w.Text.Length)
                            {
                                var head = MakeSub(w, 0, cut); var tail = MakeSub(w, cut, w.Text.Length);
                                line.Add(head); lineW += sp + WordWidth(head) * lscale;
                                lineHeight = Math.Max(lineHeight, head.Style.EffectiveLineHeightPt * lscale);
                                words[i] = tail;   // remainder wraps to the next line
                                break;
                            }
                        }
                    }

                    if (!noWrap && line.Count > 0 && lineW + sp + ww > lineWidth + 0.01f) break;
                    line.Add(w); lineW += sp + ww;
                    // Accumulate the line box's ascent/descent about the baseline (per each box's vertical-align), so a
                    // tall middle-aligned inline-block GROWS the line instead of overflowing past the border.
                    if (w.Atomic != null)
                    {
                        float H = w.AtomicH, va2 = 0.25f * context.FontSizePt;   // ≈ half the parent x-height
                        string va = w.Style.VerticalAlign;
                        if (va == "middle") { maxAscent = Math.Max(maxAscent, H / 2f + va2); maxDescent = Math.Max(maxDescent, H / 2f - va2); }
                        else if (w.Style.AtomicBaselineDrop.HasValue) { float drop = w.Style.AtomicBaselineDrop.Value; maxAscent = Math.Max(maxAscent, H - drop); maxDescent = Math.Max(maxDescent, drop); } // math: split about the baseline
                        else { maxAscent = Math.Max(maxAscent, H); }   // baseline/top/bottom: bottom≈on baseline for sizing
                        lineHeight = Math.Max(lineHeight, H);
                    }
                    else
                    {
                        float lh = w.Style.EffectiveLineHeightPt * lscale;
                        maxAscent = Math.Max(maxAscent, lh * 0.8f); maxDescent = Math.Max(maxDescent, lh * 0.2f);
                        lineHeight = Math.Max(lineHeight, lh);
                    }
                    i++;
                }
                if (line.Count == 0) { if (forced) { y += context.EffectiveLineHeightPt; continue; } i++; continue; }
                if (lineHeight <= 0) lineHeight = context.EffectiveLineHeightPt;
                // Grow the line so it fully contains every box at its vertical-align position (tall middle inline-blocks).
                if (maxAscent + maxDescent > lineHeight) lineHeight = maxAscent + maxDescent;

                // text-overflow:ellipsis — a nowrap, clipped line that overflows is truncated with "…".
                if (context.TextOverflow == "ellipsis" && context.Overflow != "visible" && noWrap && lineW > lineWidth + 0.01f)
                    line = TruncateWithEllipsis(line, lineWidth, context);
                // -webkit-line-clamp: the last allowed line gets "…" when more content remains after it.
                if (maxLines > 0 && lineNo == maxLines - 1 && i < words.Count)
                    line = TruncateWithEllipsis(line, lineWidth, context);

                float offset = 0f, extraPerGap = 0f;
                string align = context.TextAlign;
                bool rtl = context.Direction == "rtl";
                if (align == "start") align = rtl ? "right" : "left";       // logical → physical
                else if (align == "end") align = rtl ? "left" : "right";
                bool lastLine = forced || i >= words.Count;   // final line of the block / before a forced break
                // The last/only line takes text-align-last (when not auto); a justified block's last line defaults to start.
                string lineAlign = align;
                if (lastLine)
                {
                    string tal = context.TextAlignLast;
                    if (!string.IsNullOrEmpty(tal) && tal != "auto")
                    {
                        if (tal == "start") tal = rtl ? "right" : "left"; else if (tal == "end") tal = rtl ? "left" : "right";
                        lineAlign = tal;
                    }
                    else if (align == "justify") lineAlign = rtl ? "right" : "left"; // default: don't stretch the last line
                }
                bool justifyLine = lineAlign == "justify" && !noWrap && line.Count > 1;
                if (justifyLine)
                {
                    float slack = lineWidth - lineW;
                    if (slack > 0.01f) extraPerGap = slack / (line.Count - 1); // spread leftover across inter-word gaps
                }
                else if (lineAlign == "center") offset = Math.Max(0, lineWidth - lineW) * 0.5f;
                else if (lineAlign == "right") offset = Math.Max(0, lineWidth - lineW);

                float cx = lineX + offset;
                float lineBaseline = y + (maxAscent > 0f ? maxAscent : lineHeight * 0.8f);
                // The line's SHARED baseline is set by the LARGEST font on it — every baseline-aligned run (whatever
                // its size) sits on this one baseline (mixed font sizes must not each get their own).
                float maxLineFs = context.FontSizePt;
                foreach (var lw in line) if (lw.Atomic == null && lw.Style.VerticalAlign == "baseline") maxLineFs = Math.Max(maxLineFs, lw.Style.FontSizePt);
                float sharedBaseline = y + Math.Max(0f, (lineHeight - maxLineFs) / 2f) + maxLineFs * 0.8f;
                var placed = new List<(ComputedStyle? bg, float x0, float x1)>(line.Count);
                var deco = new List<(ComputedStyle? st, float x0, float x1)>(line.Count);
                bool firstInLine = true;
                float plscale = firstLine ? flScale : 1f;   // ::first-line font-size scale for this line's glyphs/advance
                foreach (var w in line)
                {
                    float sw = Afm.MeasurePt(w.Style.Face, " ", w.Style.FontSizePt) * plscale;
                    cx += firstInLine ? (preserve ? w.SpaceBefore * sw : 0f) : w.SpaceBefore * sw + w.Style.WordSpacing + extraPerGap;
                    firstInLine = false;
                    float ww = WordWidth(w) * plscale;
                    if (w.Atomic != null)
                    {
                        // Atomic inline-block vertical-align, relative to the line box (top/bottom) or baseline.
                        string va = w.Style.VerticalAlign;
                        float boxTop;
                        if (va == "top" || va == "text-top") boxTop = y;                               // top edge to line top
                        else if (va == "bottom" || va == "text-bottom") boxTop = y + lineHeight - w.AtomicH; // bottom edge to line bottom
                        else if (va == "middle") boxTop = lineBaseline - 0.25f * context.FontSizePt - w.AtomicH / 2f; // centre at baseline − ½ x-height
                        else if (w.Style.AtomicBaselineDrop.HasValue) boxTop = lineBaseline - w.AtomicH + w.Style.AtomicBaselineDrop.Value; // math: box bottom hangs `drop` below the baseline
                        else boxTop = lineBaseline - w.AtomicH;                                          // baseline: bottom edge on the baseline
                        w.Atomic.Translate(cx, boxTop);
                        box.Children.Add(w.Atomic);
                    }
                    else
                    {
                        float rfs = w.Style.FontSizePt * plscale;   // ::first-line-scaled glyph size
                        // Where baseline text of the container's font sits: super/sub shift RELATIVE TO this, not to
                        // y+maxAscent — with a tall line-height the two diverge, which mis-placed the aligned runs.
                        float textBaseline = y + Math.Max(0f, (lineHeight - context.FontSizePt) / 2f) + context.FontSizePt * 0.8f;
                        float baseY;
                        switch (w.Style.VerticalAlign)
                        {
                            case "baseline": baseY = sharedBaseline; break;   // all baseline runs share ONE baseline
                            case "top": case "text-top": baseY = y + rfs * 0.8f; break;                    // box top → line-box top
                            case "bottom": case "text-bottom": baseY = y + lineHeight - rfs * 0.2f; break; // box bottom → line-box bottom
                            case "middle": baseY = textBaseline - 0.25f * context.FontSizePt + rfs * 0.3f; break; // box middle → baseline − ½ x-height
                            default: baseY = textBaseline - VerticalAlignShift(w.Style, context.FontSizePt); break; // super/sub/length/%
                        }
                        // ::first-line — first line's runs adopt the pseudo's colour/face (+ scaled font-size).
                        var rStyle = (firstLine && firstLineStyle != null) ? firstLineStyle : w.Style;
                        var rColor = rStyle == w.Style ? w.Style.Color : firstLineStyle!.Color;
                        var rFace = rStyle == w.Style ? w.Style.Face : firstLineStyle!.Face;
                        w.Emb?.MarkUsed(w.Text);
                        bool hiddenRun = w.Style.Visibility != "visible";
                        // text-shadow: offset colour copies painted BEHIND the glyphs (first listed on top → reverse).
                        if (w.Style.TextShadows != null && !hiddenRun)
                            for (int si = w.Style.TextShadows.Count - 1; si >= 0; si--)
                            {
                                var ts = w.Style.TextShadows[si];
                                var sc = ts.Blur > 0.5f ? new Color(ts.Color.R, ts.Color.G, ts.Color.B, (byte)(ts.Color.A * 0.6f)) : ts.Color;
                                outRuns.Add(new TextRun { X = cx + ts.Dx, BaselineY = baseY + ts.Dy, Text = w.Text, FontSizePt = rfs, Face = w.Style.Face, Color = sc, LetterSpacing = w.Style.LetterSpacing, Emb = w.Emb });
                            }
                        outRuns.Add(new TextRun { X = cx, BaselineY = baseY, Text = w.Text, FontSizePt = rfs, Face = rFace, Color = rColor, LetterSpacing = w.Style.LetterSpacing, Emb = w.Emb, Hidden = hiddenRun, StrokeWidth = w.Style.TextStrokeWidth, StrokeColor = w.Style.TextStrokeColor ?? rColor });
                    }
                    placed.Add((w.Atomic != null ? null : w.BgStyle, cx, cx + ww));
                    deco.Add((w.Atomic != null ? null : w.Style, cx, cx + ww));
                    cx += ww;
                }
                EmitInlineBackgrounds(box, placed, y, lineHeight);
                EmitTextDecorations(box, deco, y, lineHeight);
                y += lineHeight;
                firstLine = false;
                if (maxLines > 0 && ++lineNo >= maxLines) break;   // -webkit-line-clamp: stop after N lines
            }
            return y - top;
        }

        /// <summary>True when an inline element carries any visible border edge.</summary>
        private static bool HasInlineBorder(ComputedStyle s) =>
            (s.BorderTop.Width > 0 && s.BorderTop.Color.A > 0) || (s.BorderBottom.Width > 0 && s.BorderBottom.Color.A > 0) ||
            (s.BorderLeft.Width > 0 && s.BorderLeft.Color.A > 0) || (s.BorderRight.Width > 0 && s.BorderRight.Color.A > 0);

        /// <summary>Paint per-line background + border rects for inline (non-block) elements that carry a
        /// background or border, grouping consecutive words sharing the same inline element. Painted behind
        /// the line's text (box.Decorations render before TextRuns). Padding/borders are applied at each
        /// line-segment's ends (over-drawn at wrap points vs CSS — a known simplification).</summary>
        private static void EmitInlineBackgrounds(LayoutBox box, List<(ComputedStyle? bg, float x0, float x1)> placed, float y, float lineHeight)
        {
            int i = 0;
            while (i < placed.Count)
            {
                var st = placed[i].bg;
                bool hasBg = st != null && st.BackgroundColor.HasValue && st.BackgroundColor.Value.A > 0;
                bool hasBd = st != null && HasInlineBorder(st);
                if (st == null || (!hasBg && !hasBd)) { i++; continue; }
                int j = i; float x0 = placed[i].x0;
                while (j < placed.Count && ReferenceEquals(placed[j].bg, st)) j++;
                float x1 = placed[j - 1].x1;

                float rx = x0 - st.PadLeft, ry = y - st.PadTop;
                float rw = (x1 - x0) + st.PadLeft + st.PadRight, rh = lineHeight + st.PadTop + st.PadBottom;
                if (hasBg) box.Decorations.Add(new SolidRect { X = rx, Y = ry, Width = rw, Height = rh, Color = st.BackgroundColor.Value });
                if (hasBd)
                {
                    var t = st.BorderTop; var b = st.BorderBottom; var l = st.BorderLeft; var r = st.BorderRight;
                    if (t.Width > 0 && t.Color.A > 0) box.Decorations.Add(new SolidRect { X = rx, Y = ry, Width = rw, Height = t.Width, Color = t.Color });
                    if (b.Width > 0 && b.Color.A > 0) box.Decorations.Add(new SolidRect { X = rx, Y = ry + rh - b.Width, Width = rw, Height = b.Width, Color = b.Color });
                    if (l.Width > 0 && l.Color.A > 0) box.Decorations.Add(new SolidRect { X = rx, Y = ry, Width = l.Width, Height = rh, Color = l.Color });
                    if (r.Width > 0 && r.Color.A > 0) box.Decorations.Add(new SolidRect { X = rx + rw - r.Width, Y = ry, Width = r.Width, Height = rh, Color = r.Color });
                }
                i = j;
            }
        }

        /// <summary>Draw text-decoration lines (underline / line-through / overline) per line, grouping
        /// consecutive words that share the same decoration (line set + colour) so the rule spans the inter-
        /// word spaces too. Painted into TextDecos (over the glyphs, so line-through crosses them).</summary>
        private static void EmitTextDecorations(LayoutBox box, List<(ComputedStyle? st, float x0, float x1)> deco, float y, float lineHeight)
        {
            int i = 0;
            while (i < deco.Count)
            {
                var st = deco[i].st;
                string? line = st?.TextDecorationLine;
                if (st == null || string.IsNullOrEmpty(line)) { i++; continue; }
                Color col = st.TextDecorationColor ?? st.Color;
                int j = i; float x0 = deco[i].x0;
                while (j < deco.Count && deco[j].st != null && deco[j].st!.TextDecorationLine == line &&
                       SameColor(deco[j].st!.TextDecorationColor ?? deco[j].st!.Color, col)) j++;
                float x1 = deco[j - 1].x1;

                float fs = st.FontSizePt;
                float baseline = y + Math.Max(0f, (lineHeight - fs) / 2f) + fs * 0.8f;
                float thick = st.TextDecorationThickness > 0f ? st.TextDecorationThickness : Math.Max(0.5f, fs * 0.06f);
                float uoff = st.TextUnderlineOffset;   // extra gap below baseline (underline only)
                string dstyle = st.TextDecorationStyle;
                void Rule(float yy)
                {
                    if (dstyle == "double")
                    {
                        box.TextDecos.Add(new SolidRect { X = x0, Y = yy - thick, Width = x1 - x0, Height = thick, Color = col });
                        box.TextDecos.Add(new SolidRect { X = x0, Y = yy + thick, Width = x1 - x0, Height = thick, Color = col });
                    }
                    else if (dstyle == "dashed" || dstyle == "dotted")
                    {
                        float seg = dstyle == "dotted" ? thick * 1.5f : fs * 0.28f;
                        float gap = dstyle == "dotted" ? thick * 1.5f : fs * 0.2f;
                        for (float px = x0; px < x1 - 0.1f; px += seg + gap)
                            box.TextDecos.Add(new SolidRect { X = px, Y = yy, Width = Math.Min(seg, x1 - px), Height = thick, Color = col });
                    }
                    else if (dstyle == "wavy")
                    {
                        // No path primitive: approximate a sine wave with small square dabs following a triangle wave.
                        float amp = Math.Max(0.8f, fs * 0.07f);   // peak-to-baseline
                        float wl = Math.Max(2f, fs * 0.5f);       // wavelength
                        float step = Math.Max(0.4f, thick);       // dab spacing
                        for (float px = x0; px < x1 - 0.1f; px += step)
                        {
                            float ph = (px - x0) / wl;             // fractional wave phase
                            float tri = Math.Abs((ph - (float)Math.Floor(ph + 0.5f)) * 4f) - 1f; // triangle -1..1
                            float dy = tri * amp;
                            box.TextDecos.Add(new SolidRect { X = px, Y = yy + dy, Width = Math.Min(step, x1 - px), Height = thick, Color = col });
                        }
                    }
                    else box.TextDecos.Add(new SolidRect { X = x0, Y = yy, Width = x1 - x0, Height = thick, Color = col });
                }
                if (line.Contains("underline")) Rule(baseline + fs * 0.12f + uoff);
                if (line.Contains("line-through")) Rule(baseline - fs * 0.30f);
                if (line.Contains("overline")) Rule(baseline - fs * 0.78f);
                i = j;
            }
        }

        private static bool SameColor(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

        // CJK: characters between which a line may break (ideographs, kana, hangul, fullwidth forms).
        private static bool IsCjkChar(char c)
        {
            int u = c;
            return (u >= 0x4E00 && u <= 0x9FFF) || (u >= 0x3400 && u <= 0x4DBF) ||   // CJK Unified + Ext A
                   (u >= 0x3040 && u <= 0x30FF) ||                                    // Hiragana + Katakana
                   (u >= 0xAC00 && u <= 0xD7AF) ||                                    // Hangul syllables
                   (u >= 0xF900 && u <= 0xFAFF) || (u >= 0xFF00 && u <= 0xFFEF);      // CJK compat + fullwidth forms
        }
        private static bool HasCjk(string s) { foreach (var c in s) if (IsCjkChar(c)) return true; return false; }
        /// <summary>Split a token so each CJK char is its own segment (breakable) and each maximal non-CJK run is one.</summary>
        private static IEnumerable<string> CjkSegments(string tok)
        {
            int i = 0;
            while (i < tok.Length)
            {
                if (IsCjkChar(tok[i])) { yield return tok[i].ToString(); i++; }
                else { int j = i; while (j < tok.Length && !IsCjkChar(tok[j])) j++; yield return tok.Substring(i, j - i); i = j; }
            }
        }

        // Strong RTL scripts: Hebrew, Arabic (+ presentation forms), Syriac, Thaana. (Arabic contextual shaping not done.)
        private static bool IsRtlChar(char c)
        {
            int u = c;
            return (u >= 0x0590 && u <= 0x05FF) || (u >= 0x0600 && u <= 0x07BF) ||  // Hebrew, Arabic/Syriac/Thaana
                   (u >= 0xFB1D && u <= 0xFDFF) || (u >= 0xFE70 && u <= 0xFEFF);      // presentation forms A/B
        }
        private static bool HasRtl(string s) { foreach (var c in s) if (IsRtlChar(c)) return true; return false; }

        /// <summary>Visual reorder of an RTL token: reverse the glyphs, then flip embedded LTR/digit runs back to
        /// logical order (a simplified UAX#9 L2 — good for Hebrew; Arabic lacks joining forms).</summary>
        private static string VisualReorderRtl(string s)
        {
            var arr = s.ToCharArray();
            Array.Reverse(arr);
            // Re-reverse maximal runs of non-RTL, non-space chars (Latin letters, digits) so they read L→R.
            int i = 0, n = arr.Length;
            while (i < n)
            {
                if (IsRtlChar(arr[i]) || char.IsWhiteSpace(arr[i])) { i++; continue; }
                int j = i; while (j < n && !IsRtlChar(arr[j]) && !char.IsWhiteSpace(arr[j])) j++;
                Array.Reverse(arr, i, j - i);
                i = j;
            }
            return new string(arr);
        }

        private sealed class WsCtx { public bool PendingSpace; } // true when a collapsed whitespace sits before the next word

        private static void FlattenWords(StyledNode node, List<Word> words, float availWidth, LayoutBox? posHost, ComputedStyle? inlineBg = null, WsCtx? ws0 = null)
        {
            var wsc = ws0 ?? new WsCtx();
            if (node.IsText)
            {
                string ws = node.Style.WhiteSpace;
                bool preserveNL = ws == "pre" || ws == "pre-wrap" || ws == "pre-line";
                bool preserveSp = ws == "pre" || ws == "pre-wrap";
                if (!preserveNL && !preserveSp)
                {
                    string raw0 = node.Text;
                    if (raw0.Length > 0 && char.IsWhiteSpace(raw0[0])) wsc.PendingSpace = true;   // leading ws → space before first token
                    var toks = raw0.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    // Bidi (Hebrew + Arabic): shape Arabic joining forms (logical order), then reverse to visual RTL.
                    bool rtlText = HasRtl(node.Text);
                    var start = words.Count;
                    bool firstTok = true;
                    foreach (var raw in toks)
                    {
                        // A word carries a leading space only when whitespace actually preceded it (between tokens, or a
                        // pending inter-node space) — a token that abuts the previous inline (e.g. `,` after </b>) glues.
                        float sb = firstTok ? (wsc.PendingSpace ? 1f : 0f) : 1f;
                        firstTok = false; wsc.PendingSpace = false;
                        string tok = ApplyTextTransform(raw, node.Style.TextTransform);
                        if (rtlText) { tok = ArabicShaper.Shape(tok); tok = VisualReorderRtl(tok); }
                        List<int>? soft = null;
                        if (tok.IndexOf('­') >= 0) { tok = StripSoftHyphens(tok, out soft); }
                        // CJK: no inter-character spaces, so a run is one huge unbreakable "word" — split it so lines can
                        // break between ideographs/kana/hangul (each CJK char its own word, no space; Latin runs stay whole).
                        if (!rtlText && node.Style.WordBreak != "keep-all" && HasCjk(tok))
                        {
                            bool firstSeg = true;
                            foreach (var seg in CjkSegments(tok))
                            {
                                words.Add(new Word { Text = seg, Style = node.Style, Emb = FontManager.ResolveForWord(node.Style, seg), BgStyle = inlineBg, SpaceBefore = firstSeg ? sb : 0f });
                                firstSeg = false;
                            }
                            continue;
                        }
                        words.Add(new Word { Text = tok, Style = node.Style, Emb = FontManager.ResolveForWord(node.Style, tok), BgStyle = inlineBg, SpaceBefore = sb, SoftBreaks = soft });
                    }
                    if (raw0.Length > 0 && char.IsWhiteSpace(raw0[raw0.Length - 1])) wsc.PendingSpace = true; // trailing ws → space before next node's word
                    if (rtlText) words.Reverse(start, words.Count - start); // visual RTL word order
                    return;
                }
                TokenizePre(node.Text, node.Style, words, inlineBg, preserveNL, preserveSp);
                wsc.PendingSpace = false;
                return;
            }
            if (node.Node.Tag == "br") { words.Add(new Word { Break = true, Style = node.Style }); wsc.PendingSpace = false; return; }

            // Atomic inline-level box: inline-block, or an inline <img>/<svg>. Laid out now at the origin;
            // positioned into the line during placement.
            if (node.Style.Display == "inline-block" || node.Node.Tag == "img" || node.Node.Tag == "svg")
            {
                var abx = new LayoutBox(node);
                float? fw = node.Style.Width.HasValue || node.Style.WidthPercent.HasValue ? null
                          : Math.Min(MeasureMaxContent(node), availWidth);
                LayoutBlock(node, 0, 0, availWidth, abx, null, forcedBorderBoxWidth: fw, posHost: posHost);
                words.Add(new Word { Style = node.Style, Atomic = abx, AtomicW = abx.BorderBoxWidth, AtomicH = abx.BorderBoxHeight, SpaceBefore = wsc.PendingSpace ? 1f : 0f });
                wsc.PendingSpace = false;
                return;
            }
            // A non-block inline element with its own background OR border decorates its words (per wrapped line).
            bool hasBg = node.Style.BackgroundColor.HasValue && node.Style.BackgroundColor.Value.A > 0;
            var childBg = (hasBg || HasInlineBorder(node.Style)) ? node.Style : inlineBg;
            foreach (var child in node.Children) FlattenWords(child, words, availWidth, posHost, childBg, wsc);
        }

        /// <summary>Tokenize text for white-space pre / pre-wrap / pre-line: newlines become forced breaks
        /// (Break words) and, for pre/pre-wrap, runs of spaces/tabs are preserved as the next word's leading
        /// space count (pre-line collapses runs to a single space). Words carry SpaceBefore in space-widths.</summary>
        private static void TokenizePre(string text, ComputedStyle style, List<Word> words, ComputedStyle? inlineBg, bool preserveNL, bool preserveSp)
        {
            var buf = new System.Text.StringBuilder();
            float pending = 0f;   // accumulated leading spaces (space-widths) for the next word
            bool sawSpace = false;
            void Flush()
            {
                if (buf.Length == 0) return;
                string tok = ApplyTextTransform(buf.ToString(), style.TextTransform);
                words.Add(new Word { Text = tok, Style = style, Emb = FontManager.ResolveForWord(style, tok), BgStyle = inlineBg, SpaceBefore = pending });
                buf.Clear(); pending = 0f; sawSpace = false;
            }
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    Flush();
                    if (preserveNL) { words.Add(new Word { Break = true, Style = style }); pending = 0f; sawSpace = false; }
                    continue;
                }
                if (c == ' ' || c == '\t' || c == '\r')
                {
                    if (buf.Length > 0) Flush();
                    float add = c == '\t' ? Math.Max(0f, style.TabSize) : 1f;
                    if (preserveSp) pending += add; else if (!sawSpace) { pending = 1f; sawSpace = true; }
                    continue;
                }
                buf.Append(c);
            }
            Flush();
        }
    }
}
