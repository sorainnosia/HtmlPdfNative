using System;
using System.Collections.Generic;
using System.Globalization;
using HtmlPdfNative.Css;
using HtmlPdfNative.Fonts;
using HtmlPdfNative.Render;

namespace HtmlPdfNative.Style
{
    /// <summary>One border edge: width in pt + color. (style is treated as solid in this slice.)</summary>
    public struct BorderEdge { public float Width; public Color Color; public string? Style; } // Style: solid|dashed|dotted|double

    /// <summary>Resolved style for one element. Subset of the Rust <c>ComputedStyle</c>.</summary>
    public sealed class ComputedStyle
    {
        public BorderEdge BorderTop, BorderRight, BorderBottom, BorderLeft; // not inherited
        public BorderEdge Outline;            // not inherited (drawn outside the border box, no layout effect)
        public float OutlineOffset;           // gap between the border edge and the outline, pt

        public float FontSizePt = 12f;      // inherited
        public bool Bold;                    // inherited
        public bool Italic;                  // inherited
        public string FontFamily = "sans-serif"; // inherited
        public Color Color = Color.Black;    // inherited
        public string TextAlign = "start";   // inherited (start/end resolve per Direction)
        public string TextAlignLast = "auto"; // inherited — alignment of the last/only line (auto|left|right|center|justify|start|end)
        public string Direction = "ltr";     // inherited (ltr|rtl)
        public float? LineHeightPt;          // inherited (null = normal)
        public float? LineHeightMul;         // inherited: UNITLESS line-height multiplier — resolved against EACH element's OWN font size (a fixed LineHeightPt would wrongly freeze a big-font child's line box)
        public string? ListStyleType;        // inherited (null = UA default: ul→disc, ol→decimal)
        public string ListStylePosition = "outside"; // inherited (outside|inside)
        public string? ListStyleImage;       // inherited (raster url() bullet; overrides the type marker)
        public string[]? Quotes;              // inherited — [open, close, ...] pairs for open-quote/close-quote
        public string? TextDecorationLine;   // inherited-for-paint (underline|line-through|overline combos)
        public Color? TextDecorationColor;   // inherited-for-paint (null = use text color)
        public string TextDecorationStyle = "solid"; // solid|double|dashed|dotted|wavy
        public float TextStrokeWidth;         // -webkit-text-stroke width in pt (inherited); 0 = none
        public Color? TextStrokeColor;        // -webkit-text-stroke colour (inherited)
        public Color? AccentColor;            // accent-color: tint for checked checkbox/radio + progress/meter fill (inherited)
        public float TextDecorationThickness = -1f;  // pt; -1 = auto (inherited-for-paint)
        public float TextUnderlineOffset = 0f;       // pt extra gap below the baseline (inherited)
        public float LetterSpacing;          // inherited, pt (added after each glyph)
        public float WordSpacing;            // inherited, pt (added at each inter-word gap)
        public string TextTransform = "none"; // inherited (none|uppercase|lowercase|capitalize)
        public string WhiteSpace = "normal";  // inherited (normal|nowrap|pre|pre-wrap|pre-line)
        public string WritingMode = "horizontal-tb"; // inherited (horizontal-tb|vertical-rl|vertical-lr)
        public string TextWrap = "wrap";       // inherited (wrap|nowrap|balance|pretty|stable)
        public float TabSize = 8f;             // inherited — a tab advances this many space-widths (CSS default 8)
        public string Visibility = "visible";  // inherited (visible|hidden|collapse)
        public string OverflowWrap = "normal"; // inherited (normal|break-word|anywhere)
        public string WordBreak = "normal";    // inherited (normal|break-all|keep-all|break-word)
        public string VerticalAlign = "baseline"; // NOT inherited, but propagated to a run's own text nodes
        public float? AtomicBaselineDrop;      // pt an atomic inline (e.g. LaTeX-math img) hangs below the text baseline
        public float TextIndent;               // inherited, pt (first-line indent)
        public float TextIndentPct;            // inherited, fraction of the containing block width (resolved at layout)

        public string Display = "inline";    // not inherited
        public Color? BackgroundColor;       // not inherited
        public LinearGradient? BackgroundGradient; // not inherited
        public string? BackgroundImageUrl;   // not inherited (raster url())
        public struct BgLayer { public Css.LinearGradient? Grad; public string? Url; public string? Size, Position, Repeat, Blend; }
        public List<BgLayer>? BackgroundLayers; // multiple background layers (first = topmost); null = single-layer path
        public string BackgroundSize = "auto";
        public string BackgroundPosition = "0% 0%";
        public string BackgroundRepeat = "repeat"; // repeat | repeat-x | repeat-y | no-repeat (space/round ~ repeat)
        public string ObjectFit = "fill";     // fill | contain | cover | none | scale-down (replaced elements)
        public string ObjectPosition = "50% 50%";
        public float MarginTop, MarginRight, MarginBottom, MarginLeft;
        public bool MarginLeftAuto, MarginRightAuto;   // margin:auto → absorb free space (centering)
        public float PadTop, PadRight, PadBottom, PadLeft;
        public string BoxSizing = "content-box"; // content-box | border-box (not inherited)
        public string Overflow = "visible";   // visible | hidden | clip | scroll | auto (clips when not visible)
        public string BackgroundClip = "border-box"; // border-box | padding-box | content-box (background paint area)
        public string? BackgroundOrigin;      // border-box | padding-box | content-box (bg-image positioning area; null = default)
        public float AspectRatio = 0f;         // width/height (0 = none); derives height from width when height is auto
        public string? MixBlendMode;           // multiply/screen/overlay/… → PDF /BM (null = normal)
        public bool Isolate;                   // isolation:isolate → wrap subtree in an isolated transparency group
        public string? BackdropFilter;         // backdrop-filter (blur(...)) → frosted-glass raster of the backdrop
        public string? ClipPath;               // clip-path shape (circle/ellipse/inset/polygon), resolved at render
        public string? BorderImageSource;      // border-image url() (9-slice)
        public float[] BorderImageSlice = { 1f, 1f, 1f, 1f }; // T R B L slice (source px or fraction if pct)
        public bool BorderImageSlicePct;       // slice values are % of the source dimensions
        public bool BorderImageFill;           // 'fill' keyword: also paint the middle region
        public string BorderImageRepeatH = "stretch", BorderImageRepeatV = "stretch"; // stretch|repeat|round|space
        public int ColumnCount;               // multi-column: >0 = fixed count (0 = none/auto)
        public float? ColumnWidth;            // ideal column width (pt)
        public float ColumnGapPt = 16f;       // gutter between columns (default ~1em/16px)
        public BorderEdge ColumnRule;         // vertical rule drawn in each column gap
        public string TextOverflow = "clip";  // clip | ellipsis
        public int LineClamp;                  // -webkit-line-clamp / line-clamp: max lines (0 = none)
        public float? Width;         // absolute width in pt (interpreted per BoxSizing)
        public float? MinWidth, MaxWidth, MinHeight, MaxHeight;   // pt constraints (per BoxSizing)
        public float? MinWidthPct, MaxWidthPct;                   // % constraints (of containing block width)
        public float? WidthPercent;  // % width, resolved at layout time (border-box fill)
        public string? WidthCalc;    // calc()/min()/max()/clamp() width containing a %, resolved at layout (needs the CB width)
        public float? HeightPercent; // % height, resolved against the containing block's definite height
        public string? WidthSizing;  // "max-content" | "min-content" | "fit-content" (content-based width)
        public float? Height;
        public float[]? BorderRadius; // 4 corner radii TL,TR,BR,BL (pt, or a 0..1 fraction where RadiusPct[i])
        public bool[]? RadiusPct;     // per-corner: true = BorderRadius[i] is a fraction of the box (resolved at layout)

        public struct Shadow { public float Dx, Dy, Blur, Spread; public Color Color; public bool Inset; }
        public List<Shadow>? BoxShadows; // comma-separated list (first is painted on top)
        public List<Shadow>? TextShadows; // inherited: list of dx dy blur color behind text
        public Shadow? BoxShadow => BoxShadows != null && BoxShadows.Count > 0 ? BoxShadows[0] : (Shadow?)null;
        public Shadow? TextShadow => TextShadows != null && TextShadows.Count > 0 ? TextShadows[0] : (Shadow?)null;
        public float Opacity = 1f;    // element opacity (0..1), not inherited as a value but multiplies down
        public string? Filter;        // CSS filter (grayscale/sepia/invert/brightness/contrast/saturate/opacity), images only
        public Dictionary<string, string>? Vars;  // CSS custom properties (--name → value), inherited
        // SVG presentation properties resolved from the CSS cascade (var() already substituted). Consumed by
        // SvgPainter for inline <svg> children styled by class/tag rules (which the painter can't cascade itself).
        public string? SvgFill, SvgStroke, SvgStrokeWidth, SvgStrokeLinecap, SvgStrokeDasharray, SvgFillOpacity, SvgStrokeOpacity;
        public List<(string name, int val)>? CounterReset;      // counter-reset (name [value])+
        public List<(string name, int val)>? CounterSet;        // counter-set (name [value])+ (no new scope)
        public List<(string name, int delta)>? CounterIncrement; // counter-increment (name [delta])+

        // Flexbox (not inherited).
        public string FlexDirection = "row";
        public string FlexWrap = "nowrap";    // nowrap | wrap | wrap-reverse
        public string JustifyContent = "flex-start";
        public string AlignItems = "stretch";
        public string AlignContent = "stretch"; // cross-axis distribution of wrapped flex lines
        public string AlignSelf = "auto";     // per-item override of the container's align-items
        public string JustifyItems = "stretch"; // grid: inline-axis (horizontal) alignment of items in their cell
        public string JustifySelf = "auto";   // per-item override of justify-items
        public float Gap;        // column-gap
        public float RowGap;
        public float FlexGrow;
        public float FlexShrink = 1f;
        public string FlexBasis = "auto";

        // Grid (not inherited).
        public string? GridTemplateColumns;
        public int GridColumnSpan = 1;
        public int GridRowSpan = 1;
        public int? GridColStart;             // explicit 1-based column line (grid-column: N / …), null = auto
        public int? GridRowStart;             // explicit 1-based row line
        public string? GridColStartName, GridColEndName, GridRowStartName, GridRowEndName; // named lines (resolved in LayoutGrid)
        public string? GridTemplateRows;

        public string BorderCollapse = "separate"; // separate|collapse (tables)
        public string CaptionSide = "top";    // top|bottom (inherited)
        public string EmptyCells = "show";    // show|hide — empty cells' bg/border in the separated model (inherited)
        public float BorderSpacingH = 0f, BorderSpacingV = 0f; // inherited (tables, separate model), pt

        // Fragmentation (not inherited). "auto" (no-op) | "always"/"page" (force a page break) | "avoid".
        public string BreakBefore = "auto";
        public string BreakAfter = "auto";
        public string BreakInside = "auto";   // "avoid" → keep the box on one page if it fits

        // Positioning (not inherited).
        public string Position = "static";           // static|relative|absolute|fixed
        public float? Top, Right, Bottom, Left;      // offsets in pt (null = auto)
        public float? TopPct, RightPct, BottomPct, LeftPct; // % offsets, resolved against the containing block at layout
        public int ZIndex;
        public bool HasZIndex;       // z-index explicitly set to an integer (not auto)
        public bool IsPositioned => Position != "static";
        public bool IsOutOfFlow => Position == "absolute" || Position == "fixed";

        // 2D transform (not inherited). Matrix is the CSS [a b c d e f]; origin as a fraction of the border box.
        public float[]? TransformMatrix;
        public float TxOriginXFrac = 0.5f, TxOriginYFrac = 0.5f;
        public float[]? Transform3D;   // full 4×4 (row-major) when the transform has rotateX/Y/perspective/matrix3d
        public float PerspectivePt;    // `perspective` property on this element (applies to its 3D children), 0 = none

        // Floats (not inherited).
        public string Float = "none";   // none|left|right
        public string Clear = "none";   // none|left|right|both

        /// <summary>Effective line height in pt (Chrome round-to-px normal, per "chrome-print-parity").</summary>
        public float EffectiveLineHeightPt => LineHeightMul.HasValue ? LineHeightMul.Value * FontSizePt : (LineHeightPt ?? Lib.NormalLineHeightPt(FontSizePt));

        public FontFace Face => Afm.Resolve(FontFamily, Bold, Italic);
    }

    /// <summary>
    /// Computes the cascade: UA defaults + matched author rules (specificity + <c>!important</c>) +
    /// inline <c>style</c>, then inheritance. Subset of <c>src/style.rs</c>.
    /// </summary>
    public sealed class StyleComputer
    {
        private readonly Stylesheet _sheet;
        public StyleComputer(Stylesheet sheet) { _sheet = sheet ?? throw new ArgumentNullException(nameof(sheet)); }

        public ComputedStyle Compute(Dom.Node node, ComputedStyle? parent)
        {
            var cs = new ComputedStyle();
            // Inherit first.
            if (parent != null)
            {
                CopyInherited(cs, parent);
            }
            cs.Display = DefaultDisplay(node.Tag);

            // Collect (rank, prop, value) from UA defaults, author rules, inline style.
            var entries = new List<(long rank, int order, string prop, string val)>();
            int order = 0;
            foreach (var d in UaDefaults(node.Tag))
                entries.Add((0, order++, d.Property, d.Value));
            foreach (var rule in _sheet.Rules)
            {
                int best = -1;
                foreach (var sel in rule.Selectors)
                    if (sel.Pseudo == null && sel.Matches(node)) best = Math.Max(best, sel.Specificity); // pseudo rules handled separately
                if (best < 0) continue;
                foreach (var d in rule.Declarations)
                {
                    long rank = d.Important ? 1_000_000_000L + best : best + 1; // author > UA(0)
                    int ord = order++;
                    entries.Add((rank, ord, d.Property, d.Value));
                    // The `background` shorthand resets background-image. A colour-only shorthand → background-image:none,
                    // emitted as a synthetic longhand so it competes at the SHORTHAND's specificity with any explicit
                    // `background-image` longhand (e.g. striped `tr:nth-child(odd) td{background:#f9fafb}` (0,2,2) must
                    // beat `td:nth-child(2){background-image:linear-gradient(...)}` (0,2,1) → no gradient on odd rows).
                    if (string.Equals(d.Property, "background", StringComparison.OrdinalIgnoreCase)
                        && d.Value.IndexOf("gradient", StringComparison.OrdinalIgnoreCase) < 0
                        && d.Value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) < 0)
                        entries.Add((rank, ord, "background-image", "none"));
                }
            }
            if (node.InlineStyle != null)
                foreach (var d in Stylesheet.ParseDeclarations(node.InlineStyle))
                {
                    long rank = d.Important ? 2_000_000_000L : 100_000; // inline beats selectors
                    int ord = order++;
                    entries.Add((rank, ord, d.Property, d.Value));
                    if (string.Equals(d.Property, "background", StringComparison.OrdinalIgnoreCase)
                        && d.Value.IndexOf("gradient", StringComparison.OrdinalIgnoreCase) < 0
                        && d.Value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) < 0)
                        entries.Add((rank, ord, "background-image", "none"));
                }

            entries.Sort((a, b) => a.rank != b.rank ? a.rank.CompareTo(b.rank) : a.order.CompareTo(b.order));

            var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in entries) raw[e.prop] = e.val; // later (higher rank/order) wins

            // CSS custom properties: inherit parent's, override with this element's --* declarations, then
            // substitute var(...) in every ordinary property value.
            var vars = parent?.Vars != null ? new Dictionary<string, string>(parent.Vars) : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in raw) if (kv.Key.StartsWith("--", StringComparison.Ordinal)) vars[kv.Key] = kv.Value;
            cs.Vars = vars.Count > 0 ? vars : null;
            if (vars.Count > 0)
                foreach (var k in new List<string>(raw.Keys))
                    if (!k.StartsWith("--", StringComparison.Ordinal) && raw[k].IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
                        raw[k] = ResolveVars(raw[k], vars, 0);

            // Legacy HTML cellspacing attribute (px) maps to border-spacing when CSS doesn't set it.
            if (node.Attributes != null && node.Attributes.TryGetValue("cellspacing", out var csv) && !raw.ContainsKey("border-spacing"))
                raw["border-spacing"] = csv.Trim().TrimEnd('p', 'x') + "px";
            // HTML dir attribute → direction (CSS wins).
            if (node.Attributes != null && node.Attributes.TryGetValue("dir", out var dattr) && !raw.ContainsKey("direction"))
            { var dv = dattr.Trim().ToLowerInvariant(); if (dv == "rtl" || dv == "ltr") raw["direction"] = dv; }

            Apply(cs, raw, parent);
            return cs;
        }

        /// <summary>Substitute <c>var(--name[, fallback])</c> occurrences in a value against the vars map.</summary>
        private static string ResolveVars(string value, Dictionary<string, string> vars, int depth)
        {
            if (depth > 16) return value;
            int v = value.IndexOf("var(", StringComparison.OrdinalIgnoreCase);
            if (v < 0) return value;
            var sb = new System.Text.StringBuilder();
            int i = 0;
            while (i < value.Length)
            {
                int at = value.IndexOf("var(", i, StringComparison.OrdinalIgnoreCase);
                if (at < 0) { sb.Append(value.Substring(i)); break; }
                sb.Append(value.Substring(i, at - i));
                int depthP = 0, close = -1;
                for (int k = at + 3; k < value.Length; k++) { if (value[k] == '(') depthP++; else if (value[k] == ')') { depthP--; if (depthP == 0) { close = k; break; } } }
                if (close < 0) { sb.Append(value.Substring(at)); break; }
                string args = value.Substring(at + 4, close - (at + 4));
                // split into name , fallback at the first top-level comma
                int comma = -1, dp = 0;
                for (int k = 0; k < args.Length; k++) { if (args[k] == '(') dp++; else if (args[k] == ')') dp--; else if (args[k] == ',' && dp == 0) { comma = k; break; } }
                string name = (comma < 0 ? args : args.Substring(0, comma)).Trim();
                string? fallback = comma < 0 ? null : args.Substring(comma + 1).Trim();
                string sub = vars.TryGetValue(name, out var vv) ? vv : (fallback ?? "");
                sb.Append(ResolveVars(sub, vars, depth + 1)); // resolve nested var() in the substituted text
                i = close + 1;
            }
            return sb.ToString();
        }

        /// <summary>Copy all inherited properties from a parent computed style.</summary>
        private static void CopyInherited(ComputedStyle cs, ComputedStyle parent)
        {
            cs.FontSizePt = parent.FontSizePt; cs.Bold = parent.Bold; cs.Italic = parent.Italic;
            cs.FontFamily = parent.FontFamily; cs.Color = parent.Color; cs.TextAlign = parent.TextAlign; cs.TextAlignLast = parent.TextAlignLast; cs.Direction = parent.Direction;
            cs.TextShadows = parent.TextShadows; // inherited
            cs.LineHeightPt = parent.LineHeightPt; cs.LineHeightMul = parent.LineHeightMul; cs.ListStyleType = parent.ListStyleType; cs.ListStylePosition = parent.ListStylePosition; cs.ListStyleImage = parent.ListStyleImage; cs.Quotes = parent.Quotes;
            cs.TextDecorationLine = parent.TextDecorationLine; cs.TextDecorationColor = parent.TextDecorationColor; cs.TextDecorationStyle = parent.TextDecorationStyle;
            cs.TextDecorationThickness = parent.TextDecorationThickness; cs.TextUnderlineOffset = parent.TextUnderlineOffset;
            cs.TextStrokeWidth = parent.TextStrokeWidth; cs.TextStrokeColor = parent.TextStrokeColor;
            cs.AccentColor = parent.AccentColor;
            cs.LetterSpacing = parent.LetterSpacing; cs.WordSpacing = parent.WordSpacing;
            cs.TextTransform = parent.TextTransform; cs.WhiteSpace = parent.WhiteSpace; cs.Visibility = parent.Visibility; cs.TabSize = parent.TabSize; cs.TextWrap = parent.TextWrap;
            cs.WritingMode = parent.WritingMode;
            cs.OverflowWrap = parent.OverflowWrap; cs.WordBreak = parent.WordBreak;
            cs.TextIndent = parent.TextIndent; cs.TextIndentPct = parent.TextIndentPct;
            cs.BorderCollapse = parent.BorderCollapse; cs.BorderSpacingH = parent.BorderSpacingH; cs.BorderSpacingV = parent.BorderSpacingV; cs.CaptionSide = parent.CaptionSide; cs.EmptyCells = parent.EmptyCells;
        }

        /// <summary>Compute a pseudo-element's style (::first-letter/::first-line etc.) with no content
        /// requirement; null when no rule targets that pseudo on the node.</summary>
        public ComputedStyle? ComputePseudoStyle(Dom.Node node, ComputedStyle elementStyle, string which)
        {
            var entries = new List<(long rank, int order, string prop, string val)>();
            int order = 0;
            foreach (var rule in _sheet.Rules)
            {
                int best = -1;
                foreach (var sel in rule.Selectors)
                    if (sel.Pseudo == which && sel.Matches(node)) best = Math.Max(best, sel.Specificity);
                if (best < 0) continue;
                foreach (var d in rule.Declarations)
                    entries.Add((d.Important ? 1_000_000_000L + best : best + 1, order++, d.Property, d.Value));
            }
            if (entries.Count == 0) return null;
            entries.Sort((a, b) => a.rank != b.rank ? a.rank.CompareTo(b.rank) : a.order.CompareTo(b.order));
            var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in entries) raw[e.prop] = e.val;
            if (elementStyle.Vars != null)
                foreach (var k in new List<string>(raw.Keys))
                    if (raw[k].IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0) raw[k] = ResolveVars(raw[k], elementStyle.Vars, 0);
            var cs = new ComputedStyle();
            CopyInherited(cs, elementStyle);
            cs.Display = "inline";
            Apply(cs, raw, elementStyle);
            return cs;
        }

        /// <summary>Compute a ::before / ::after pseudo-element's style + raw <c>content</c> value for
        /// <paramref name="node"/>, or null when no matching rule sets a paintable <c>content</c>.</summary>
        public (ComputedStyle style, string content)? ComputePseudo(Dom.Node node, ComputedStyle elementStyle, string which)
        {
            var entries = new List<(long rank, int order, string prop, string val)>();
            int order = 0;
            foreach (var rule in _sheet.Rules)
            {
                int best = -1;
                foreach (var sel in rule.Selectors)
                    if (sel.Pseudo == which && sel.Matches(node)) best = Math.Max(best, sel.Specificity);
                if (best < 0) continue;
                foreach (var d in rule.Declarations)
                    entries.Add((d.Important ? 1_000_000_000L + best : best + 1, order++, d.Property, d.Value));
            }
            if (entries.Count == 0) return null;
            entries.Sort((a, b) => a.rank != b.rank ? a.rank.CompareTo(b.rank) : a.order.CompareTo(b.order));
            var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in entries) raw[e.prop] = e.val;
            if (elementStyle.Vars != null)
                foreach (var k in new List<string>(raw.Keys))
                    if (raw[k].IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0) raw[k] = ResolveVars(raw[k], elementStyle.Vars, 0);
            if (!raw.TryGetValue("content", out var content)) return null;
            var t = content.Trim();
            if (t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("normal", StringComparison.OrdinalIgnoreCase)) return null;

            var cs = new ComputedStyle();
            CopyInherited(cs, elementStyle);  // pseudo inherits from its originating element
            cs.Display = "inline";
            Apply(cs, raw, elementStyle);
            return (cs, content);
        }

        private static void Apply(ComputedStyle cs, Dictionary<string, string> raw, ComputedStyle? parent)
        {
            float parentFont = parent?.FontSizePt ?? 12f;
            // Resolve direction early so logical properties remap to the correct physical side.
            string dirEarly = raw.TryGetValue("direction", out var de) ? de.Trim().ToLowerInvariant() : (parent?.Direction ?? "ltr");
            MapLogicalProperties(raw, dirEarly == "rtl");

            if (raw.TryGetValue("font-size", out var fs))
                cs.FontSizePt = Values.LengthPt(fs, parentFont, parentFont) ?? cs.FontSizePt;
            if (raw.TryGetValue("font-family", out var ff)) cs.FontFamily = ff;
            if (raw.TryGetValue("font-weight", out var fw))
                cs.Bold = fw == "bold" || fw == "bolder" || (int.TryParse(fw, out var w) && w >= 600);
            if (raw.TryGetValue("font-style", out var fst)) cs.Italic = fst == "italic" || fst == "oblique";
            if (raw.TryGetValue("color", out var col)) { var c = Values.ParseColor(col); if (c != null) cs.Color = c.Value; }
            // SVG presentation properties (stored raw for SvgPainter; var() already substituted by the cascade).
            if (raw.TryGetValue("fill", out var svf)) cs.SvgFill = svf.Trim();
            if (raw.TryGetValue("stroke", out var svs)) cs.SvgStroke = svs.Trim();
            if (raw.TryGetValue("stroke-width", out var svw)) cs.SvgStrokeWidth = svw.Trim();
            if (raw.TryGetValue("stroke-linecap", out var svlc)) cs.SvgStrokeLinecap = svlc.Trim();
            if (raw.TryGetValue("stroke-dasharray", out var svda)) cs.SvgStrokeDasharray = svda.Trim();
            if (raw.TryGetValue("fill-opacity", out var svfo)) cs.SvgFillOpacity = svfo.Trim();
            if (raw.TryGetValue("stroke-opacity", out var svso)) cs.SvgStrokeOpacity = svso.Trim();
            if (raw.TryGetValue("text-align", out var ta)) cs.TextAlign = ta.ToLowerInvariant();
            if (raw.TryGetValue("text-align-last", out var tal)) cs.TextAlignLast = tal.Trim().ToLowerInvariant();
            if (raw.TryGetValue("direction", out var dir)) cs.Direction = dir.Trim().ToLowerInvariant();
            if (raw.TryGetValue("text-shadow", out var tsh) && tsh.Trim().ToLowerInvariant() != "none") cs.TextShadows = ParseShadowList(tsh, cs.FontSizePt);
            if (raw.TryGetValue("display", out var disp))
            {
                cs.Display = disp.ToLowerInvariant();
                if (cs.Display == "inline-flex") cs.Display = "flex";
                // CSS table display (on non-<table> elements) → approximate with flex so content isn't dropped:
                // `table`/`table-row` become a flex row of cells; a `table-cell` is a block flex item.
                else if (cs.Display == "table" || cs.Display == "inline-table" || cs.Display == "table-row") cs.Display = "flex";
                else if (cs.Display == "table-cell") cs.Display = "block";
                else if (cs.Display == "table-row-group" || cs.Display == "table-header-group" || cs.Display == "table-footer-group" || cs.Display == "table-caption") cs.Display = "block";
                else if (cs.Display == "table-column" || cs.Display == "table-column-group") cs.Display = "none";
            }
            if (raw.TryGetValue("height", out var hh))
            {
                var hht = hh.Trim();
                if (hht.EndsWith("%") && float.TryParse(hht.Substring(0, hht.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var hpct)) { cs.HeightPercent = hpct; cs.Height = null; }
                else cs.Height = Values.LengthPt(hh, cs.FontSizePt);
            }
            if (raw.TryGetValue("flex-direction", out var fd)) cs.FlexDirection = fd.ToLowerInvariant();
            if (raw.TryGetValue("flex-wrap", out var fwr)) cs.FlexWrap = fwr.Trim().ToLowerInvariant();
            if (raw.TryGetValue("flex-flow", out var fflow))
            {
                foreach (var t in fflow.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (t == "row" || t == "row-reverse" || t == "column" || t == "column-reverse") cs.FlexDirection = t;
                    else if (t == "nowrap" || t == "wrap" || t == "wrap-reverse") cs.FlexWrap = t;
                }
            }
            if (raw.TryGetValue("justify-content", out var jc)) cs.JustifyContent = jc.ToLowerInvariant();
            if (raw.TryGetValue("align-items", out var ai)) cs.AlignItems = ai.ToLowerInvariant();
            if (raw.TryGetValue("align-content", out var ac)) cs.AlignContent = ac.ToLowerInvariant();
            if (raw.TryGetValue("justify-items", out var ji)) cs.JustifyItems = ji.ToLowerInvariant();
            if (raw.TryGetValue("justify-self", out var js)) cs.JustifySelf = js.ToLowerInvariant();
            // place-* shorthands: "<align> <justify>" (a single value applies to both axes).
            if (raw.TryGetValue("place-items", out var pi)) { var t = pi.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); if (t.Length > 0) { cs.AlignItems = t[0]; cs.JustifyItems = t.Length > 1 ? t[1] : t[0]; } }
            if (raw.TryGetValue("place-content", out var pc)) { var t = pc.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); if (t.Length > 0) { cs.AlignContent = t[0]; cs.JustifyContent = t.Length > 1 ? t[1] : t[0]; } }
            if (raw.TryGetValue("place-self", out var plsf)) { var t = plsf.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); if (t.Length > 0) { cs.AlignSelf = t[0]; cs.JustifySelf = t.Length > 1 ? t[1] : t[0]; } }
            if (raw.TryGetValue("align-self", out var asf)) cs.AlignSelf = asf.Trim().ToLowerInvariant();
            if (raw.TryGetValue("box-sizing", out var bxs)) cs.BoxSizing = bxs.Trim().ToLowerInvariant();
            // overflow (+ -x/-y): any non-visible value clips in print (scroll/auto have no scrollbars).
            foreach (var prop in new[] { "overflow", "overflow-x", "overflow-y" })
                if (raw.TryGetValue(prop, out var ov)) { var o = ov.Trim().ToLowerInvariant().Split(' ')[0]; if (o != "visible") cs.Overflow = o; }
            if (raw.TryGetValue("text-overflow", out var txo)) cs.TextOverflow = txo.Trim().ToLowerInvariant();
            if ((raw.TryGetValue("-webkit-line-clamp", out var lcv) || raw.TryGetValue("line-clamp", out lcv)) && int.TryParse(lcv.Trim(), out var lc)) cs.LineClamp = Math.Max(0, lc);
            if (raw.TryGetValue("column-count", out var ccv) && int.TryParse(ccv.Trim(), out var ccn)) cs.ColumnCount = Math.Max(0, ccn);
            if (raw.TryGetValue("column-width", out var cwv)) cs.ColumnWidth = Values.LengthPt(cwv, cs.FontSizePt);
            if (raw.TryGetValue("column-rule", out var crl)) cs.ColumnRule = ParseBorder(crl, cs.FontSizePt, cs.Color);
            if (raw.TryGetValue("column-rule-width", out var crw)) cs.ColumnRule.Width = Values.LengthPt(crw, cs.FontSizePt) ?? cs.ColumnRule.Width;
            if (raw.TryGetValue("column-rule-color", out var crc)) { var c = Values.ParseColor(crc); if (c != null) cs.ColumnRule.Color = c.Value; }
            if (raw.TryGetValue("column-rule-style", out var crs)) { var st = crs.Trim().ToLowerInvariant(); if (st == "none" || st == "hidden") cs.ColumnRule.Width = 0; else { cs.ColumnRule.Style = st; if (cs.ColumnRule.Width == 0) cs.ColumnRule.Width = 1f * Lib.PxToPt; } }
            if (raw.TryGetValue("column-gap", out var cgv2) && cgv2.Trim() != "normal") cs.ColumnGapPt = Values.LengthPt(cgv2, cs.FontSizePt) ?? cs.ColumnGapPt;
            if (raw.TryGetValue("columns", out var colsv))   // shorthand: <width> and/or <count>
                foreach (var t in colsv.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(t, out var cn)) cs.ColumnCount = Math.Max(0, cn);
                    else if (Values.LengthPt(t, cs.FontSizePt) is float cwf) cs.ColumnWidth = cwf;
            if (raw.TryGetValue("gap", out var gp) || raw.TryGetValue("grid-gap", out gp))
            {
                var gt = gp.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                float rg = Values.LengthPt(gt[0], cs.FontSizePt) ?? 0f;
                cs.RowGap = rg;
                cs.Gap = gt.Length > 1 ? (Values.LengthPt(gt[1], cs.FontSizePt) ?? rg) : rg;
            }
            if (raw.TryGetValue("column-gap", out var cgp) || raw.TryGetValue("grid-column-gap", out cgp)) cs.Gap = Values.LengthPt(cgp, cs.FontSizePt) ?? cs.Gap;
            if (raw.TryGetValue("row-gap", out var rgp) || raw.TryGetValue("grid-row-gap", out rgp)) cs.RowGap = Values.LengthPt(rgp, cs.FontSizePt) ?? cs.RowGap;
            if (raw.TryGetValue("grid-template-columns", out var gtc)) cs.GridTemplateColumns = gtc;
            if (raw.TryGetValue("grid-template-rows", out var gtr)) cs.GridTemplateRows = gtr;
            if (raw.TryGetValue("grid-column", out var gc)) { ParseGridLine(gc, out cs.GridColStart, out cs.GridColumnSpan, out cs.GridColStartName, out cs.GridColEndName); }
            if (raw.TryGetValue("grid-column-start", out var gcs)) { if (int.TryParse(gcs.Trim(), out var gcsv)) cs.GridColStart = gcsv; else if (!IsGridKeyword(gcs)) cs.GridColStartName = gcs.Trim(); }
            if (raw.TryGetValue("grid-row", out var gr)) { ParseGridLine(gr, out cs.GridRowStart, out cs.GridRowSpan, out cs.GridRowStartName, out cs.GridRowEndName); }
            if (raw.TryGetValue("grid-row-start", out var grs)) { if (int.TryParse(grs.Trim(), out var grsv)) cs.GridRowStart = grsv; else if (!IsGridKeyword(grs)) cs.GridRowStartName = grs.Trim(); }
            if (raw.TryGetValue("flex-grow", out var fgr) && float.TryParse(fgr, NumberStyles.Float, CultureInfo.InvariantCulture, out var gv)) cs.FlexGrow = gv;
            if (raw.TryGetValue("flex-shrink", out var fsh) && float.TryParse(fsh, NumberStyles.Float, CultureInfo.InvariantCulture, out var sv)) cs.FlexShrink = sv;
            if (raw.TryGetValue("flex-basis", out var fbs)) cs.FlexBasis = fbs.ToLowerInvariant();
            if (raw.TryGetValue("flex", out var flx)) ParseFlex(cs, flx);
            if (raw.TryGetValue("background-image", out var bgimg) && bgimg.Trim().ToLowerInvariant() != "none")
            {
                if (!TryParseBgLayers(bgimg, cs))
                {
                    if (bgimg.IndexOf("gradient", StringComparison.OrdinalIgnoreCase) >= 0) cs.BackgroundGradient = Gradients.Parse(bgimg);
                    else if (bgimg.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0) cs.BackgroundImageUrl = ExtractUrl(bgimg);
                }
            }
            if (raw.TryGetValue("background", out var bgsh))
            {
                // A trailing colour segment ("linear-gradient(...), #fff") sets background-color in every case.
                { var segL = Gradients.SplitTopLevel(bgsh, ','); var tc = Values.ParseColor(segL[segL.Count - 1]); if (tc != null) cs.BackgroundColor = tc; }
                if (TryParseBgLayers(bgsh, cs)) { }
                else if (bgsh.IndexOf("gradient", StringComparison.OrdinalIgnoreCase) >= 0) cs.BackgroundGradient = Gradients.Parse(bgsh);
                else if (bgsh.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    cs.BackgroundImageUrl = ExtractUrl(bgsh);
                    var lo = bgsh.ToLowerInvariant();
                    if (lo.Contains("cover")) cs.BackgroundSize = "cover"; else if (lo.Contains("contain")) cs.BackgroundSize = "contain";
                    foreach (var kw in new[] { "center", "top", "bottom", "left", "right" }) if (lo.Contains(kw)) { cs.BackgroundPosition = ExtractPosition(lo); break; }
                    if (lo.Contains("no-repeat")) cs.BackgroundRepeat = "no-repeat";
                    else if (lo.Contains("repeat-x")) cs.BackgroundRepeat = "repeat-x";
                    else if (lo.Contains("repeat-y")) cs.BackgroundRepeat = "repeat-y";
                    else if (lo.Contains("repeat")) cs.BackgroundRepeat = "repeat";
                }
                else { var c = Values.ParseColor(bgsh); if (c != null) cs.BackgroundColor = c; }
            }
            if (raw.TryGetValue("background-color", out var bgc)) { var c = Values.ParseColor(bgc); if (c != null) cs.BackgroundColor = c; }
            if (raw.TryGetValue("background-clip", out var bgclp)) cs.BackgroundClip = bgclp.Trim().ToLowerInvariant();
            if (raw.TryGetValue("background-origin", out var bgor)) cs.BackgroundOrigin = bgor.Trim().ToLowerInvariant();
            if (raw.TryGetValue("clip-path", out var clpp) && clpp.Trim().ToLowerInvariant() != "none") cs.ClipPath = clpp.Trim();
            if (raw.TryGetValue("mix-blend-mode", out var mbm)) { var m = mbm.Trim().ToLowerInvariant(); if (m != "normal") cs.MixBlendMode = m; }
            if (raw.TryGetValue("isolation", out var iso) && iso.Trim().ToLowerInvariant() == "isolate") cs.Isolate = true;
            foreach (var k in new[] { "backdrop-filter", "-webkit-backdrop-filter" })
                if (raw.TryGetValue(k, out var bdf) && bdf.Trim().ToLowerInvariant() != "none") cs.BackdropFilter = bdf;
            // border-image (source + slice; width/outset/repeat use border-width + stretch defaults).
            if (raw.TryGetValue("border-image-source", out var bis) && bis.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0) cs.BorderImageSource = ExtractUrl(bis);
            if (raw.TryGetValue("border-image-slice", out var bisl)) ParseBorderImageSlice(bisl, cs);
            if (raw.TryGetValue("border-image-repeat", out var birp)) ParseBorderImageRepeat(birp, cs);
            if (raw.TryGetValue("border-image", out var bimg))
            {
                if (bimg.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0) cs.BorderImageSource = ExtractUrl(bimg);
                var parts = bimg.Split('/');
                var slicePart = System.Text.RegularExpressions.Regex.Replace(parts[0], @"url\([^)]*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                if (slicePart.Length > 0) ParseBorderImageSlice(slicePart, cs);
                // the repeat keyword is the trailing token(s) after the last '/' (or after slice if no '/').
                var tail = parts[parts.Length - 1];
                if (tail.IndexOf("repeat", StringComparison.OrdinalIgnoreCase) >= 0 || tail.IndexOf("round", StringComparison.OrdinalIgnoreCase) >= 0 || tail.IndexOf("space", StringComparison.OrdinalIgnoreCase) >= 0 || tail.IndexOf("stretch", StringComparison.OrdinalIgnoreCase) >= 0)
                    ParseBorderImageRepeat(tail, cs);
            }
            if (raw.TryGetValue("aspect-ratio", out var arv))
            {
                var ap = arv.Trim().ToLowerInvariant().Split('/');
                if (ap.Length == 2 && float.TryParse(ap[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var aw) && float.TryParse(ap[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ah) && ah > 0) cs.AspectRatio = aw / ah;
                else if (float.TryParse(ap[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ar) && ar > 0) cs.AspectRatio = ar;
            }
            if (raw.TryGetValue("background-size", out var bsz)) { cs.BackgroundSize = bsz.Trim().ToLowerInvariant(); DistributeBgLonghand(cs, bsz, 0); }
            if (raw.TryGetValue("background-position", out var bps)) { cs.BackgroundPosition = bps.Trim().ToLowerInvariant(); DistributeBgLonghand(cs, bps, 1); }
            if (raw.TryGetValue("background-repeat", out var brp)) { cs.BackgroundRepeat = brp.Trim().ToLowerInvariant(); DistributeBgLonghand(cs, brp, 2); }
            if (raw.TryGetValue("background-blend-mode", out var bbm) && bbm.Trim().ToLowerInvariant() != "normal")
            {
                // A single image/gradient + a bg-colour also blends → promote it to a 1-element layers list.
                if (cs.BackgroundLayers == null)
                {
                    var bl = new ComputedStyle.BgLayer();
                    if (cs.BackgroundGradient != null && !cs.BackgroundGradient.Conic) { bl.Grad = cs.BackgroundGradient; cs.BackgroundGradient = null; }
                    else if (cs.BackgroundImageUrl != null) { bl.Url = cs.BackgroundImageUrl; cs.BackgroundImageUrl = null; }
                    if (bl.Grad != null || bl.Url != null) cs.BackgroundLayers = new List<ComputedStyle.BgLayer> { bl };
                }
                DistributeBgLonghand(cs, bbm, 3);
            }
            if (raw.TryGetValue("width", out var wd))
            {
                var t = wd.Trim(); var tl = t.ToLowerInvariant();
                if (t.EndsWith("%", StringComparison.Ordinal) && float.TryParse(t.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var wp))
                { cs.WidthPercent = wp; cs.Width = null; }
                else if (tl == "max-content" || tl == "min-content" || tl == "fit-content") cs.WidthSizing = tl;
                // A calc()/min()/max()/clamp() with a % needs the containing-block width — defer to layout.
                else if ((tl.StartsWith("calc(") || tl.StartsWith("min(") || tl.StartsWith("max(") || tl.StartsWith("clamp(")) && tl.Contains("%"))
                { cs.WidthCalc = t; cs.Width = null; }
                else cs.Width = Values.LengthPt(wd, cs.FontSizePt);
            }
            // min/max sizing constraints (px/em/pt, or % of the containing block for width).
            ParseSizeConstraint(raw, "min-width", cs.FontSizePt, ref cs.MinWidth, ref cs.MinWidthPct);
            ParseSizeConstraint(raw, "max-width", cs.FontSizePt, ref cs.MaxWidth, ref cs.MaxWidthPct);
            if (raw.TryGetValue("min-height", out var mnh)) cs.MinHeight = Values.LengthPt(mnh, cs.FontSizePt);
            if (raw.TryGetValue("max-height", out var mxh)) cs.MaxHeight = Values.LengthPt(mxh, cs.FontSizePt);

            if (raw.TryGetValue("line-height", out var lh))
            {
                if (float.TryParse(lh, NumberStyles.Float, CultureInfo.InvariantCulture, out var mult) && !lh.Contains("px") && !lh.Contains("pt") && !lh.Contains("%"))
                    { cs.LineHeightMul = mult; cs.LineHeightPt = null; } // unitless: inherit the multiplier, resolve per element's own font
                else if (lh != "normal")
                    { cs.LineHeightPt = Values.LengthPt(lh, cs.FontSizePt, cs.FontSizePt); cs.LineHeightMul = null; }
                else { cs.LineHeightPt = null; cs.LineHeightMul = null; }
            }

            Edge(raw, "margin", (t, r, b, l) => { cs.MarginTop = t; cs.MarginRight = r; cs.MarginBottom = b; cs.MarginLeft = l; }, cs.FontSizePt);
            if (raw.TryGetValue("margin-top", out var mt)) cs.MarginTop = Values.LengthPt(mt, cs.FontSizePt) ?? cs.MarginTop;
            if (raw.TryGetValue("margin-right", out var mr)) cs.MarginRight = Values.LengthPt(mr, cs.FontSizePt) ?? cs.MarginRight;
            if (raw.TryGetValue("margin-bottom", out var mb)) cs.MarginBottom = Values.LengthPt(mb, cs.FontSizePt) ?? cs.MarginBottom;
            if (raw.TryGetValue("margin-left", out var ml)) cs.MarginLeft = Values.LengthPt(ml, cs.FontSizePt) ?? cs.MarginLeft;
            // margin:auto detection (horizontal centering).
            if (raw.TryGetValue("margin", out var mall))
            {
                var mp = mall.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (mp.Length == 1) cs.MarginLeftAuto = cs.MarginRightAuto = mp[0] == "auto";
                else if (mp.Length == 2 || mp.Length == 3) cs.MarginLeftAuto = cs.MarginRightAuto = mp[1] == "auto";
                else if (mp.Length >= 4) { cs.MarginRightAuto = mp[1] == "auto"; cs.MarginLeftAuto = mp[3] == "auto"; }
            }
            if (raw.TryGetValue("margin-left", out var mla)) cs.MarginLeftAuto = mla.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
            if (raw.TryGetValue("margin-right", out var mra)) cs.MarginRightAuto = mra.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

            if (raw.TryGetValue("position", out var posv)) cs.Position = posv.Trim().ToLowerInvariant();
            // inset shorthand → top/right/bottom/left (1-4 values, CSS box order); `auto` leaves that side null.
            if (raw.TryGetValue("inset", out var insv))
            {
                var ip = insv.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // Assign one side, honouring `auto`, a percentage (→ *Pct, resolved against the containing block at
                // layout), or a length. `inset:22%` is common for concentric rings (sunburst / dial hole).
                void Side(string s, Action<float?> setLen, Action<float?> setPct)
                {
                    setPct(null); setLen(null);
                    if (s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return;
                    if (IsPct(s, out var pc)) setPct(pc);
                    else setLen(Values.LengthPt(s, cs.FontSizePt));
                }
                string T = ip.Length > 0 ? ip[0] : "auto";
                string R = ip.Length >= 2 ? ip[1] : T;
                string B = ip.Length >= 3 ? ip[2] : T;
                string L = ip.Length >= 4 ? ip[3] : (ip.Length >= 2 ? ip[1] : T);
                Side(T, v => cs.Top = v, v => cs.TopPct = v);
                Side(R, v => cs.Right = v, v => cs.RightPct = v);
                Side(B, v => cs.Bottom = v, v => cs.BottomPct = v);
                Side(L, v => cs.Left = v, v => cs.LeftPct = v);
            }
            if (raw.TryGetValue("top", out var tpv)) { if (IsPct(tpv, out var pvt)) { cs.TopPct = pvt; cs.Top = null; } else cs.Top = Values.LengthPt(tpv, cs.FontSizePt); }
            if (raw.TryGetValue("right", out var rgv)) { if (IsPct(rgv, out var pvr)) { cs.RightPct = pvr; cs.Right = null; } else cs.Right = Values.LengthPt(rgv, cs.FontSizePt); }
            if (raw.TryGetValue("bottom", out var btv)) { if (IsPct(btv, out var pvb)) { cs.BottomPct = pvb; cs.Bottom = null; } else cs.Bottom = Values.LengthPt(btv, cs.FontSizePt); }
            if (raw.TryGetValue("left", out var lfv)) { if (IsPct(lfv, out var pvl)) { cs.LeftPct = pvl; cs.Left = null; } else cs.Left = Values.LengthPt(lfv, cs.FontSizePt); }
            if (raw.TryGetValue("z-index", out var zv) && int.TryParse(zv.Trim(), out var zi)) { cs.ZIndex = zi; cs.HasZIndex = true; }
            if (raw.TryGetValue("border-radius", out var brd)) cs.BorderRadius = ParseBorderRadius(brd, cs.FontSizePt, out cs.RadiusPct);
            {
                float[] R() => cs.BorderRadius ??= new float[4];
                if (raw.TryGetValue("border-top-left-radius", out var r0)) R()[0] = Values.LengthPt(r0, cs.FontSizePt) ?? 0f;
                if (raw.TryGetValue("border-top-right-radius", out var r1)) R()[1] = Values.LengthPt(r1, cs.FontSizePt) ?? 0f;
                if (raw.TryGetValue("border-bottom-right-radius", out var r2)) R()[2] = Values.LengthPt(r2, cs.FontSizePt) ?? 0f;
                if (raw.TryGetValue("border-bottom-left-radius", out var r3)) R()[3] = Values.LengthPt(r3, cs.FontSizePt) ?? 0f;
            }
            if (raw.TryGetValue("box-shadow", out var bsh) && bsh.Trim().ToLowerInvariant() != "none") cs.BoxShadows = ParseShadowList(bsh, cs.FontSizePt);
            if (raw.TryGetValue("opacity", out var opv) && float.TryParse(opv.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ovv)) cs.Opacity = Math.Max(0f, Math.Min(1f, ovv));
            if (raw.TryGetValue("border-collapse", out var bcl)) cs.BorderCollapse = bcl.Trim().ToLowerInvariant();
            if (raw.TryGetValue("caption-side", out var cap)) cs.CaptionSide = cap.Trim().ToLowerInvariant();
            if (raw.TryGetValue("empty-cells", out var ec)) cs.EmptyCells = ec.Trim().ToLowerInvariant();
            // page-break-* (CSS2) and break-* (CSS3) — first token; "left"/"right"/"page" all force a break here.
            if (raw.TryGetValue("break-before", out var kb)) cs.BreakBefore = kb.Trim().ToLowerInvariant().Split(' ')[0];
            if (raw.TryGetValue("page-break-before", out var pkb)) cs.BreakBefore = pkb.Trim().ToLowerInvariant().Split(' ')[0];
            if (raw.TryGetValue("break-after", out var ka)) cs.BreakAfter = ka.Trim().ToLowerInvariant().Split(' ')[0];
            if (raw.TryGetValue("page-break-after", out var pka)) cs.BreakAfter = pka.Trim().ToLowerInvariant().Split(' ')[0];
            if (raw.TryGetValue("break-inside", out var ki)) cs.BreakInside = ki.Trim().ToLowerInvariant().Split(' ')[0];
            if (raw.TryGetValue("page-break-inside", out var pki)) cs.BreakInside = pki.Trim().ToLowerInvariant().Split(' ')[0];
            // text-decoration shorthand / longhands. Setting `none` clears the propagated decoration.
            if (raw.TryGetValue("text-decoration", out var td) || raw.TryGetValue("text-decoration-line", out td))
            {
                var lo = td.ToLowerInvariant();
                var lines = new List<string>();
                foreach (var k in new[] { "underline", "line-through", "overline" }) if (lo.Contains(k)) lines.Add(k);
                cs.TextDecorationLine = lines.Count > 0 ? string.Join(" ", lines) : null; // none / initial → null
                foreach (var tok in lo.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    if (tok == "solid" || tok == "double" || tok == "dashed" || tok == "dotted" || tok == "wavy") cs.TextDecorationStyle = tok;
                    else if (tok != "underline" && tok != "line-through" && tok != "overline" && tok != "none")
                    { var c = Values.ParseColor(tok); if (c != null) cs.TextDecorationColor = c; }
            }
            if (raw.TryGetValue("text-decoration-color", out var tdc)) { var c = Values.ParseColor(tdc); if (c != null) cs.TextDecorationColor = c; }
            if (raw.TryGetValue("text-decoration-style", out var tds)) cs.TextDecorationStyle = tds.Trim().ToLowerInvariant();
            // -webkit-text-stroke[-width/-color] (also text-stroke): stroke glyph outlines.
            foreach (var k in new[] { "-webkit-text-stroke", "text-stroke" })
                if (raw.TryGetValue(k, out var tst))
                {
                    foreach (var tok in tst.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var c = Values.ParseColor(tok); if (c != null) { cs.TextStrokeColor = c; continue; }
                        var w = Values.LengthPt(tok, cs.FontSizePt); if (w.HasValue) cs.TextStrokeWidth = w.Value;
                    }
                }
            foreach (var k in new[] { "-webkit-text-stroke-width", "text-stroke-width" })
                if (raw.TryGetValue(k, out var tsw)) cs.TextStrokeWidth = Values.LengthPt(tsw, cs.FontSizePt) ?? cs.TextStrokeWidth;
            foreach (var k in new[] { "-webkit-text-stroke-color", "text-stroke-color" })
                if (raw.TryGetValue(k, out var tsc)) { var c = Values.ParseColor(tsc); if (c != null) cs.TextStrokeColor = c; }
            if (raw.TryGetValue("accent-color", out var acc) && acc.Trim() != "auto") { var c = Values.ParseColor(acc); if (c != null) cs.AccentColor = c; }
            if (raw.TryGetValue("text-decoration-thickness", out var tdt) && tdt.Trim() != "auto" && tdt.Trim() != "from-font") cs.TextDecorationThickness = Values.LengthPt(tdt, cs.FontSizePt) ?? -1f;
            if (raw.TryGetValue("text-underline-offset", out var tuo) && tuo.Trim() != "auto") cs.TextUnderlineOffset = Values.LengthPt(tuo, cs.FontSizePt) ?? 0f;
            if (raw.TryGetValue("letter-spacing", out var lsp)) cs.LetterSpacing = lsp.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase) ? 0f : (Values.LengthPt(lsp, cs.FontSizePt) ?? 0f);
            if (raw.TryGetValue("word-spacing", out var wsp)) cs.WordSpacing = wsp.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase) ? 0f : (Values.LengthPt(wsp, cs.FontSizePt) ?? 0f);
            if (raw.TryGetValue("text-transform", out var tt)) cs.TextTransform = tt.Trim().ToLowerInvariant();
            if (raw.TryGetValue("white-space", out var wspc)) cs.WhiteSpace = wspc.Trim().ToLowerInvariant();
            if (raw.TryGetValue("writing-mode", out var wm)) { var v = wm.Trim().ToLowerInvariant(); if (v == "vertical-rl" || v == "vertical-lr" || v == "horizontal-tb") cs.WritingMode = v; else if (v == "tb-rl") cs.WritingMode = "vertical-rl"; else if (v == "tb-lr") cs.WritingMode = "vertical-lr"; }
            if (raw.TryGetValue("text-wrap", out var twr)) cs.TextWrap = twr.Trim().ToLowerInvariant();
            if (raw.TryGetValue("text-wrap-mode", out var twm)) cs.TextWrap = twm.Trim().ToLowerInvariant();
            if (raw.TryGetValue("tab-size", out var tsz))
            {
                // Unitless → number of space-widths; a length → convert to space-widths (~ em/2 per space).
                var t = tsz.Trim();
                if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tn)) cs.TabSize = Math.Max(0f, tn);
                else { var lp = Values.LengthPt(t, cs.FontSizePt); if (lp.HasValue) cs.TabSize = Math.Max(0f, lp.Value / Math.Max(1f, cs.FontSizePt * 0.25f)); }
            }
            if (raw.TryGetValue("visibility", out var vis)) cs.Visibility = vis.Trim().ToLowerInvariant();
            if (raw.TryGetValue("filter", out var fltr) && fltr.Trim().ToLowerInvariant() != "none") cs.Filter = fltr.Trim();
            if (raw.TryGetValue("counter-reset", out var cr)) cs.CounterReset = ParseCounterList(cr, 0);
            if (raw.TryGetValue("counter-set", out var cst)) cs.CounterSet = ParseCounterList(cst, 0);
            if (raw.TryGetValue("counter-increment", out var ci)) cs.CounterIncrement = ParseCounterList(ci, 1);
            if (raw.TryGetValue("object-fit", out var of)) cs.ObjectFit = of.Trim().ToLowerInvariant();
            if (raw.TryGetValue("object-position", out var op)) cs.ObjectPosition = op.Trim().ToLowerInvariant();
            if (raw.TryGetValue("overflow-wrap", out var ow)) cs.OverflowWrap = ow.Trim().ToLowerInvariant();
            else if (raw.TryGetValue("word-wrap", out var ww2)) cs.OverflowWrap = ww2.Trim().ToLowerInvariant(); // legacy alias
            if (raw.TryGetValue("word-break", out var wb)) cs.WordBreak = wb.Trim().ToLowerInvariant();
            if (raw.TryGetValue("vertical-align", out var va)) cs.VerticalAlign = va.Trim().ToLowerInvariant();
            if (raw.TryGetValue("text-indent", out var ti))
            {
                var t = ti.Trim();
                if (t.EndsWith("%", StringComparison.Ordinal) && float.TryParse(t.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var tp)) { cs.TextIndentPct = tp / 100f; cs.TextIndent = 0f; }
                else { cs.TextIndent = Values.LengthPt(ti, cs.FontSizePt) ?? 0f; cs.TextIndentPct = 0f; }
            }
            if (raw.TryGetValue("list-style-type", out var lst)) cs.ListStyleType = lst.Trim().ToLowerInvariant();
            if (raw.TryGetValue("list-style-position", out var lspos)) cs.ListStylePosition = lspos.Trim().ToLowerInvariant();
            if (raw.TryGetValue("list-style-image", out var lsi))
                cs.ListStyleImage = lsi.IndexOf("none", StringComparison.OrdinalIgnoreCase) >= 0 ? null : ExtractUrl(lsi);
            if (raw.TryGetValue("quotes", out var qv))
            {
                var qt = qv.Trim();
                if (qt.Equals("none", StringComparison.OrdinalIgnoreCase)) cs.Quotes = new string[0];
                else if (!qt.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    // A list of quoted strings: "«" "»" '“' '”' … — pair them in declaration order.
                    var qs = new List<string>();
                    for (int qi = 0; qi < qt.Length; qi++)
                    {
                        char q = qt[qi];
                        if (q == '"' || q == '\'') { int e = qt.IndexOf(q, qi + 1); if (e < 0) break; qs.Add(UnescapeCss(qt.Substring(qi + 1, e - qi - 1))); qi = e; }
                    }
                    if (qs.Count >= 2) cs.Quotes = qs.ToArray();
                }
            }
            if (raw.TryGetValue("list-style", out var lss))
            {
                // shorthand: pick the recognised type keyword + inside/outside + a url() image.
                if (lss.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0) cs.ListStyleImage = ExtractUrl(lss);
                foreach (var t in lss.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (IsListType(t)) cs.ListStyleType = t;
                    else if (t == "inside" || t == "outside") cs.ListStylePosition = t;
                }
                if (lss.IndexOf("none", StringComparison.OrdinalIgnoreCase) >= 0 && cs.ListStyleType == null) cs.ListStyleType = "none";
            }
            if (raw.TryGetValue("border-spacing", out var bsp))
            {
                var ps = bsp.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (ps.Length >= 1) { cs.BorderSpacingH = Values.LengthPt(ps[0], cs.FontSizePt) ?? 0f; cs.BorderSpacingV = cs.BorderSpacingH; }
                if (ps.Length >= 2) cs.BorderSpacingV = Values.LengthPt(ps[1], cs.FontSizePt) ?? cs.BorderSpacingV;
            }
            if (raw.TryGetValue("float", out var flt)) cs.Float = flt.Trim().ToLowerInvariant();
            if (raw.TryGetValue("clear", out var clr)) cs.Clear = clr.Trim().ToLowerInvariant();
            if (raw.TryGetValue("transform", out var tf) && tf.Trim().ToLowerInvariant() != "none")
            {
                cs.TransformMatrix = ParseTransformMatrix(tf, cs.FontSizePt);   // 2D subset (affine fallback)
                var lo = tf.ToLowerInvariant();
                if (lo.Contains("rotatex(") || lo.Contains("rotatey(") || lo.Contains("perspective(") || lo.Contains("matrix3d("))
                    cs.Transform3D = Parse3DMatrix(tf, cs.FontSizePt);
            }
            if (raw.TryGetValue("transform-origin", out var to)) ParseTransformOrigin(to, cs);
            if (raw.TryGetValue("perspective", out var pv) && pv.Trim().ToLowerInvariant() != "none") cs.PerspectivePt = Values.LengthPt(pv, cs.FontSizePt) ?? 0f;

            ApplyBorders(cs, raw);

            Edge(raw, "padding", (t, r, b, l) => { cs.PadTop = t; cs.PadRight = r; cs.PadBottom = b; cs.PadLeft = l; }, cs.FontSizePt);
            if (raw.TryGetValue("padding-top", out var pt)) cs.PadTop = Values.LengthPt(pt, cs.FontSizePt) ?? cs.PadTop;
            if (raw.TryGetValue("padding-right", out var pr)) cs.PadRight = Values.LengthPt(pr, cs.FontSizePt) ?? cs.PadRight;
            if (raw.TryGetValue("padding-bottom", out var pb)) cs.PadBottom = Values.LengthPt(pb, cs.FontSizePt) ?? cs.PadBottom;
            if (raw.TryGetValue("padding-left", out var pl)) cs.PadLeft = Values.LengthPt(pl, cs.FontSizePt) ?? cs.PadLeft;
        }

        /// <summary>Parse a CSS <c>transform</c> list into one composed matrix [a b c d e f] (lengths→pt).</summary>
        private static float[]? ParseTransformMatrix(string value, float emPt)
        {
            float[] m = { 1, 0, 0, 1, 0, 0 };
            bool any = false;
            int i = 0, n = value.Length;
            while (i < n)
            {
                while (i < n && (value[i] == ' ' || value[i] == ',')) i++;
                int ns = i;
                while (i < n && value[i] != '(') i++;
                if (i >= n) break;
                string name = value.Substring(ns, i - ns).Trim().ToLowerInvariant();
                int close = value.IndexOf(')', i);
                if (close < 0) break;
                var argsRaw = value.Substring(i + 1, close - i - 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                i = close + 1;

                float LenPt(string s) => Values.LengthPt(s, emPt) ?? 0f;
                float Deg(string s) { s = s.Trim().ToLowerInvariant(); if (s.EndsWith("rad")) return float.Parse(s.Substring(0, s.Length - 3), CultureInfo.InvariantCulture) * 180f / (float)Math.PI; return float.TryParse(s.Replace("deg", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0f; }
                float Num(string s) => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

                float[]? op = null;
                switch (name)
                {
                    case "translate": op = new float[] { 1, 0, 0, 1, LenPt(argsRaw[0]), argsRaw.Length > 1 ? LenPt(argsRaw[1]) : 0 }; break;
                    case "translatex": op = new float[] { 1, 0, 0, 1, LenPt(argsRaw[0]), 0 }; break;
                    case "translatey": op = new float[] { 1, 0, 0, 1, 0, LenPt(argsRaw[0]) }; break;
                    case "scale": { float sx = Num(argsRaw[0]); float sy = argsRaw.Length > 1 ? Num(argsRaw[1]) : sx; op = new float[] { sx, 0, 0, sy, 0, 0 }; break; }
                    case "scalex": op = new float[] { Num(argsRaw[0]), 0, 0, 1, 0, 0 }; break;
                    case "scaley": op = new float[] { 1, 0, 0, Num(argsRaw[0]), 0, 0 }; break;
                    case "rotate": { double a = Deg(argsRaw[0]) * Math.PI / 180.0; float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a); op = new float[] { cs, sn, -sn, cs, 0, 0 }; break; }
                    case "rotatez": { double a = Deg(argsRaw[0]) * Math.PI / 180.0; float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a); op = new float[] { cs, sn, -sn, cs, 0, 0 }; break; }
                    case "skewx": op = new float[] { 1, 0, (float)Math.Tan(Deg(argsRaw[0]) * Math.PI / 180.0), 1, 0, 0 }; break;
                    case "skewy": op = new float[] { 1, (float)Math.Tan(Deg(argsRaw[0]) * Math.PI / 180.0), 0, 1, 0, 0 }; break;
                    case "skew": op = new float[] { 1, argsRaw.Length > 1 ? (float)Math.Tan(Deg(argsRaw[1]) * Math.PI / 180.0) : 0f, (float)Math.Tan(Deg(argsRaw[0]) * Math.PI / 180.0), 1, 0, 0 }; break;
                    case "translate3d": op = new float[] { 1, 0, 0, 1, LenPt(argsRaw[0]), argsRaw.Length > 1 ? LenPt(argsRaw[1]) : 0 }; break; // drop z
                    case "scale3d": { float sx = Num(argsRaw[0]); float sy = argsRaw.Length > 1 ? Num(argsRaw[1]) : sx; op = new float[] { sx, 0, 0, sy, 0, 0 }; break; }
                    case "rotate3d": // only the Z-axis component maps to a 2D rotation
                        if (argsRaw.Length >= 4 && Math.Abs(Num(argsRaw[2])) > Math.Abs(Num(argsRaw[0])) && Math.Abs(Num(argsRaw[2])) > Math.Abs(Num(argsRaw[1])))
                        { double a = Deg(argsRaw[3]) * Math.PI / 180.0; float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a); op = new float[] { cs, sn, -sn, cs, 0, 0 }; }
                        break;
                    case "matrix": if (argsRaw.Length >= 6) op = new float[] { Num(argsRaw[0]), Num(argsRaw[1]), Num(argsRaw[2]), Num(argsRaw[3]), LenPt(argsRaw[4]), LenPt(argsRaw[5]) }; break;
                    case "matrix3d": if (argsRaw.Length >= 16) op = new float[] { Num(argsRaw[0]), Num(argsRaw[1]), Num(argsRaw[4]), Num(argsRaw[5]), LenPt(argsRaw[12]), LenPt(argsRaw[13]) }; break; // 2D subset
                }
                if (op != null) { m = MatMul(m, op); any = true; }
            }
            return any ? m : null;
        }

        /// <summary>Parse a CSS transform list into a 4×4 (row-major) matrix, including rotateX/Y/Z, perspective(),
        /// translate3d, scale3d and matrix3d — for elements that need a real projective (non-affine) warp.</summary>
        private static float[]? Parse3DMatrix(string value, float emPt)
        {
            float[] m = Ident4();
            bool any = false;
            int i = 0, n = value.Length;
            while (i < n)
            {
                while (i < n && (value[i] == ' ' || value[i] == ',')) i++;
                int ns = i; while (i < n && value[i] != '(') i++;
                if (i >= n) break;
                string name = value.Substring(ns, i - ns).Trim().ToLowerInvariant();
                int close = value.IndexOf(')', i); if (close < 0) break;
                var a = value.Substring(i + 1, close - i - 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                i = close + 1;
                float Len(string s) => Values.LengthPt(s, emPt) ?? 0f;
                float Num(string s) => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
                double Rad(string s) { s = s.Trim().ToLowerInvariant(); if (s.EndsWith("rad")) return Num(s.Substring(0, s.Length - 3)); return Num(s.Replace("deg", "")) * Math.PI / 180.0; }
                float[]? op = null;
                switch (name)
                {
                    case "rotatex": { double r = Rad(a[0]); float c = (float)Math.Cos(r), sn = (float)Math.Sin(r); op = new float[] { 1, 0, 0, 0, 0, c, -sn, 0, 0, sn, c, 0, 0, 0, 0, 1 }; break; }
                    case "rotatey": { double r = Rad(a[0]); float c = (float)Math.Cos(r), sn = (float)Math.Sin(r); op = new float[] { c, 0, sn, 0, 0, 1, 0, 0, -sn, 0, c, 0, 0, 0, 0, 1 }; break; }
                    case "rotate": case "rotatez": { double r = Rad(a[0]); float c = (float)Math.Cos(r), sn = (float)Math.Sin(r); op = new float[] { c, -sn, 0, 0, sn, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }; break; }
                    case "rotate3d": { double r = Rad(a[3]); float ax = Num(a[0]), ay = Num(a[1]), az = Num(a[2]); float len = (float)Math.Sqrt(ax * ax + ay * ay + az * az); if (len < 1e-6) break; ax /= len; ay /= len; az /= len; float c = (float)Math.Cos(r), sn = (float)Math.Sin(r), t = 1 - c; op = new float[] { t * ax * ax + c, t * ax * ay - sn * az, t * ax * az + sn * ay, 0, t * ax * ay + sn * az, t * ay * ay + c, t * ay * az - sn * ax, 0, t * ax * az - sn * ay, t * ay * az + sn * ax, t * az * az + c, 0, 0, 0, 0, 1 }; break; }
                    case "perspective": { float d = Len(a[0]); if (d <= 0) break; op = Ident4(); op[14] = -1f / d; break; }
                    case "translate": op = Ident4(); op[3] = Len(a[0]); op[7] = a.Length > 1 ? Len(a[1]) : 0; break;
                    case "translate3d": op = Ident4(); op[3] = Len(a[0]); op[7] = a.Length > 1 ? Len(a[1]) : 0; op[11] = a.Length > 2 ? Len(a[2]) : 0; break;
                    case "translatex": op = Ident4(); op[3] = Len(a[0]); break;
                    case "translatey": op = Ident4(); op[7] = Len(a[0]); break;
                    case "translatez": op = Ident4(); op[11] = Len(a[0]); break;
                    case "scale": op = Ident4(); op[0] = Num(a[0]); op[5] = a.Length > 1 ? Num(a[1]) : Num(a[0]); break;
                    case "scale3d": op = Ident4(); op[0] = Num(a[0]); op[5] = a.Length > 1 ? Num(a[1]) : 1; op[10] = a.Length > 2 ? Num(a[2]) : 1; break;
                    case "matrix": if (a.Length >= 6) { op = Ident4(); op[0] = Num(a[0]); op[4] = Num(a[1]); op[1] = Num(a[2]); op[5] = Num(a[3]); op[3] = Len(a[4]); op[7] = Len(a[5]); } break;
                    case "matrix3d":
                        if (a.Length >= 16) { op = new float[16]; for (int col = 0; col < 4; col++) for (int row = 0; row < 4; row++) op[row * 4 + col] = col < 3 || row < 3 ? Num(a[col * 4 + row]) : Num(a[col * 4 + row]); }
                        break;
                }
                if (op != null) { m = MatMul4(m, op); any = true; }
            }
            return any ? m : null;
        }

        internal static float[] Ident4() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        internal static float[] MatMul4(float[] a, float[] b)
        {
            var r = new float[16];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) { float s = 0; for (int k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j]; r[i * 4 + j] = s; }
            return r;
        }

        /// <summary>Compose 2×3 affine matrices [a b c d e f]: apply B then A (A∘B).</summary>
        internal static float[] MatMul(float[] a, float[] b) => new float[]
        {
            a[0]*b[0] + a[2]*b[1],
            a[1]*b[0] + a[3]*b[1],
            a[0]*b[2] + a[2]*b[3],
            a[1]*b[2] + a[3]*b[3],
            a[0]*b[4] + a[2]*b[5] + a[4],
            a[1]*b[4] + a[3]*b[5] + a[5],
        };

        /// <summary>Parse a <c>box-shadow</c> value (first shadow only): [inset] dx dy [blur [spread]] [color].</summary>
        /// <summary>Parse a counter-reset / counter-increment value: <c>name [int]</c> pairs.</summary>
        private static List<(string, int)>? ParseCounterList(string value, int dflt)
        {
            var v = value.Trim();
            if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            var toks = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<(string, int)>();
            for (int i = 0; i < toks.Length; i++)
            {
                string name = toks[i];
                int num = dflt;
                if (i + 1 < toks.Length && int.TryParse(toks[i + 1], out var nv)) { num = nv; i++; }
                list.Add((name, num));
            }
            return list.Count > 0 ? list : null;
        }

        /// <summary>Parse a <c>grid-column</c>/<c>grid-row</c> value into a 1-based start line (null=auto) + span.
        /// Handles <c>N</c>, <c>N / M</c>, <c>N / span S</c>, and <c>span S</c>.</summary>
        private static void ParseGridLine(string value, out int? start, out int span) { ParseGridLine(value, out start, out span, out _, out _); }

        private static void ParseGridLine(string value, out int? start, out int span, out string? startName, out string? endName)
        {
            start = null; span = 1; startName = null; endName = null;
            var v = value.Trim().ToLowerInvariant();
            var parts = v.Split('/');
            string a = parts[0].Trim();
            string? b = parts.Length > 1 ? parts[1].Trim() : null;
            var sa = System.Text.RegularExpressions.Regex.Match(a, @"span\s+(\d+)");
            if (sa.Success) { span = Math.Max(1, int.Parse(sa.Groups[1].Value)); return; }
            if (int.TryParse(a, out var startLine)) start = startLine;
            else if (a.Length > 0) startName = a;                    // named start line (resolved in LayoutGrid)
            if (b != null)
            {
                var sb = System.Text.RegularExpressions.Regex.Match(b, @"span\s+(\d+)");
                if (sb.Success) span = Math.Max(1, int.Parse(sb.Groups[1].Value));
                else if (int.TryParse(b, out var endLine)) { if (start.HasValue && endLine > start.Value) span = endLine - start.Value; }
                else if (b.Length > 0) endName = b;                  // named end line
            }
        }

        /// <summary>Decode CSS escapes in a string: <c>\XXXXXX</c> (1-6 hex → codepoint) or <c>\c</c>.</summary>
        private static string UnescapeCss(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                int h = i + 1, he = h;
                while (he < s.Length && he - h < 6 && Uri.IsHexDigit(s[he])) he++;
                if (he > h) { sb.Append(char.ConvertFromUtf32(Convert.ToInt32(s.Substring(h, he - h), 16))); i = he - 1; if (i + 1 < s.Length && (s[i + 1] == ' ' || s[i + 1] == '\t')) i++; }
                else { sb.Append(s[i + 1]); i++; }
            }
            return sb.ToString();
        }

        /// <summary>Parse border-image-slice (1-4 numbers, optional %, optional 'fill') into T R B L.</summary>
        private static void ParseBorderImageSlice(string value, ComputedStyle cs)
        {
            var lo = value.Trim().ToLowerInvariant();
            if (lo.Contains("fill")) cs.BorderImageFill = true;
            // keep only numeric / percent tokens (ignore 'fill' and any repeat keywords that share the shorthand).
            var nums = new List<string>();
            foreach (var s in lo.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (float.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) nums.Add(s);
            var toks = nums.ToArray();
            if (toks.Length == 0) return;
            cs.BorderImageSlicePct = toks[0].EndsWith("%");
            float V(string s) => float.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
            var v = new float[toks.Length]; for (int i = 0; i < toks.Length; i++) v[i] = V(toks[i]);
            // CSS box order T R B L (1→all, 2→TB/LR, 3→T/LR/B, 4→TRBL).
            cs.BorderImageSlice = toks.Length == 1 ? new[] { v[0], v[0], v[0], v[0] }
                : toks.Length == 2 ? new[] { v[0], v[1], v[0], v[1] }
                : toks.Length == 3 ? new[] { v[0], v[1], v[2], v[1] }
                : new[] { v[0], v[1], v[2], v[3] };
        }

        /// <summary>Map CSS logical properties to their physical equivalents (assuming horizontal-tb + LTR) so the
        /// existing physical-property parsing handles them. An explicit physical longhand already present wins.</summary>
        private static void MapLogicalProperties(Dictionary<string, string> raw, bool rtl)
        {
            string iStart = rtl ? "right" : "left", iEnd = rtl ? "left" : "right"; // inline-start/end physical side
            void Map(string logical, string physical) { if (raw.TryGetValue(logical, out var v) && !raw.ContainsKey(physical)) raw[physical] = v; }
            void Pair(string logical, string pStart, string pEnd)
            {
                if (!raw.TryGetValue(logical, out var v)) return;
                var t = v.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) return;
                if (!raw.ContainsKey(pStart)) raw[pStart] = t[0];
                if (!raw.ContainsKey(pEnd)) raw[pEnd] = t.Length > 1 ? t[1] : t[0];
            }
            foreach (var (log, phys) in new[] { ("margin", "margin"), ("padding", "padding") })
            {
                Map($"{log}-block-start", $"{phys}-top"); Map($"{log}-block-end", $"{phys}-bottom");
                Map($"{log}-inline-start", $"{phys}-{iStart}"); Map($"{log}-inline-end", $"{phys}-{iEnd}");
                Pair($"{log}-block", $"{phys}-top", $"{phys}-bottom"); Pair($"{log}-inline", $"{phys}-{iStart}", $"{phys}-{iEnd}");
            }
            Map("inset-block-start", "top"); Map("inset-block-end", "bottom");
            Map("inset-inline-start", iStart); Map("inset-inline-end", iEnd);
            Pair("inset-block", "top", "bottom"); Pair("inset-inline", iStart, iEnd);
            Map("block-size", "height"); Map("inline-size", "width");
            Map("min-block-size", "min-height"); Map("min-inline-size", "min-width");
            Map("max-block-size", "max-height"); Map("max-inline-size", "max-width");
            foreach (var side in new[] { ("block-start", "top"), ("block-end", "bottom"), ("inline-start", iStart), ("inline-end", iEnd) })
            {
                Map($"border-{side.Item1}", $"border-{side.Item2}");
                Map($"border-{side.Item1}-width", $"border-{side.Item2}-width");
                Map($"border-{side.Item1}-style", $"border-{side.Item2}-style");
                Map($"border-{side.Item1}-color", $"border-{side.Item2}-color");
            }
        }

        /// <summary>Distribute a comma-list background longhand (size=0/position=1/repeat=2) across the bg layers
        /// (CSS matches lists by index; a shorter list repeats its last value).</summary>
        private static void DistributeBgLonghand(ComputedStyle cs, string value, int which)
        {
            if (cs.BackgroundLayers == null) return;
            var segs = Gradients.SplitTopLevel(value, ',');
            var layers = cs.BackgroundLayers;
            for (int i = 0; i < layers.Count; i++)
            {
                var v = segs[Math.Min(i, segs.Count - 1)].Trim().ToLowerInvariant();
                var L = layers[i];
                if (which == 0) L.Size = v; else if (which == 1) L.Position = v; else if (which == 2) L.Repeat = v; else L.Blend = v;
                layers[i] = L;
            }
        }

        /// <summary>If a background value has MULTIPLE comma-separated image/gradient layers, populate
        /// BackgroundLayers (first listed = topmost) and return true. Single-layer values return false.</summary>
        private static bool TryParseBgLayers(string value, ComputedStyle cs)
        {
            var segs = Gradients.SplitTopLevel(value, ',');
            // Count segments that actually carry an image/gradient.
            var layers = new List<ComputedStyle.BgLayer>();
            foreach (var seg in segs)
            {
                var t = seg.Trim();
                if (t.IndexOf("gradient", StringComparison.OrdinalIgnoreCase) >= 0) { var g = Gradients.Parse(t); if (g != null) layers.Add(new ComputedStyle.BgLayer { Grad = g }); }
                else if (t.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0) { var u = ExtractUrl(t); if (u != null) layers.Add(new ComputedStyle.BgLayer { Url = u }); }
            }
            if (layers.Count < 2) return false;   // single (or none) → use the existing single-layer fields
            cs.BackgroundLayers = layers;
            return true;
        }

        private static void ParseBorderImageRepeat(string value, ComputedStyle cs)
        {
            var t = value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            bool Ok(string s) => s == "stretch" || s == "repeat" || s == "round" || s == "space";
            var vals = new List<string>(); foreach (var s in t) if (Ok(s)) vals.Add(s);
            if (vals.Count == 0) return;
            cs.BorderImageRepeatH = vals[0];
            cs.BorderImageRepeatV = vals.Count > 1 ? vals[1] : vals[0];
        }

        private static bool IsGridKeyword(string v)
        {
            var t = v.Trim().ToLowerInvariant();
            return t == "auto" || t == "none" || t.StartsWith("span");
        }

        private static void ParseSizeConstraint(Dictionary<string, string> raw, string prop, float em, ref float? pt, ref float? pct)
        {
            if (!raw.TryGetValue(prop, out var v)) return;
            var t = v.Trim();
            if (t == "none") return; // max-*: none = no limit
            if (t.EndsWith("%", StringComparison.Ordinal) && float.TryParse(t.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) pct = p / 100f;
            else pt = Values.LengthPt(t, em);
        }

        private static bool IsListType(string t)
        {
            switch (t)
            {
                case "disc": case "circle": case "square": case "none":
                case "decimal": case "decimal-leading-zero":
                case "lower-alpha": case "upper-alpha": case "lower-latin": case "upper-latin":
                case "lower-roman": case "upper-roman": case "lower-greek":
                case "armenian": case "upper-armenian": case "lower-armenian": case "georgian": return true;
                default: return false;
            }
        }

        /// <summary>Parse a comma-separated shadow list (box-shadow / text-shadow) — all layers, first on top.</summary>
        private static List<ComputedStyle.Shadow>? ParseShadowList(string value, float emPt)
        {
            var list = new List<ComputedStyle.Shadow>();
            foreach (var seg in Gradients.SplitTopLevel(value, ','))
            {
                var sh = ParseShadow(seg, emPt);
                if (sh.HasValue) list.Add(sh.Value);
            }
            return list.Count > 0 ? list : null;
        }

        private static ComputedStyle.Shadow? ParseShadow(string seg, float emPt)
        {
            var toks = Gradients.SplitTopLevel(seg.Trim(), ' ');
            var lens = new List<float>();
            Color col = new Color(0, 0, 0, 128); // default: semi-transparent black
            bool inset = false;
            foreach (var raw in toks)
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                if (t.Equals("inset", StringComparison.OrdinalIgnoreCase)) { inset = true; continue; }
                var c = Values.ParseColor(t);
                if (c != null) { col = c.Value; continue; }
                var l = Values.LengthPt(t, emPt);
                if (l.HasValue) lens.Add(l.Value);
            }
            if (lens.Count < 2) return null;
            return new ComputedStyle.Shadow
            {
                Dx = lens[0], Dy = lens[1],
                Blur = lens.Count > 2 ? Math.Max(0, lens[2]) : 0f,
                Spread = lens.Count > 3 ? lens[3] : 0f,
                Color = col, Inset = inset,
            };
        }

        /// <summary>Parse the <c>border-radius</c> shorthand (1–4 values; ignores the elliptical "/" part).
        /// A <c>%</c> value is kept as a 0..1 fraction (flagged in <paramref name="pct"/>) and resolved at layout.</summary>
        private static float[]? ParseBorderRadius(string value, float emPt, out bool[]? pct)
        {
            pct = null;
            int slash = value.IndexOf('/');
            if (slash >= 0) value = value.Substring(0, slash); // circular approximation
            var toks = value.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length == 0) return null;
            var vp = new bool[4];
            float V(int i)
            {
                var t = toks[i].Trim();
                if (t.EndsWith("%", StringComparison.Ordinal) && float.TryParse(t.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) { vp[i] = true; return p / 100f; }
                return Values.LengthPt(t, emPt) ?? 0f;
            }
            // Evaluate in token order so vp indices line up, then map to corners.
            float v0 = V(0), v1 = toks.Length > 1 ? V(1) : 0, v2 = toks.Length > 2 ? V(2) : 0, v3 = toks.Length > 3 ? V(3) : 0;
            bool p0 = vp[0], p1 = vp[1], p2 = vp[2], p3 = vp[3];
            float tl, tr, br, bl; bool ptl, ptr, pbr, pbl;
            switch (toks.Length)
            {
                case 1: tl = tr = br = bl = v0; ptl = ptr = pbr = pbl = p0; break;
                case 2: tl = br = v0; tr = bl = v1; ptl = pbr = p0; ptr = pbl = p1; break;
                case 3: tl = v0; tr = bl = v1; br = v2; ptl = p0; ptr = pbl = p1; pbr = p2; break;
                default: tl = v0; tr = v1; br = v2; bl = v3; ptl = p0; ptr = p1; pbr = p2; pbl = p3; break;
            }
            pct = new[] { ptl, ptr, pbr, pbl };
            return new[] { tl, tr, br, bl };
        }

        /// <summary>Pull the path out of a CSS <c>url( ... )</c> token (strips quotes). Null if none.</summary>
        private static string? ExtractUrl(string value)
        {
            int i = value.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int open = i + 4, close = value.IndexOf(')', open);
            if (close < 0) return null;
            return value.Substring(open, close - open).Trim().Trim('"', '\'').Trim();
        }

        /// <summary>Extract the position keywords from a background shorthand (e.g. "center", "top right").</summary>
        private static string ExtractPosition(string lo)
        {
            var kws = new List<string>();
            foreach (var kw in new[] { "left", "right", "top", "bottom", "center" }) if (lo.Contains(kw)) kws.Add(kw);
            return kws.Count > 0 ? string.Join(" ", kws) : "0% 0%";
        }

        private static void ParseTransformOrigin(string value, ComputedStyle cs)
        {
            var toks = value.Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool xSet = false;
            foreach (var t in toks)
            {
                switch (t)
                {
                    case "left": cs.TxOriginXFrac = 0f; xSet = true; break;
                    case "right": cs.TxOriginXFrac = 1f; xSet = true; break;
                    case "top": cs.TxOriginYFrac = 0f; break;
                    case "bottom": cs.TxOriginYFrac = 1f; break;
                    case "center": if (!xSet) { cs.TxOriginXFrac = 0.5f; xSet = true; } else cs.TxOriginYFrac = 0.5f; break;
                    default:
                        if (t.EndsWith("%") && float.TryParse(t.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                        { if (!xSet) { cs.TxOriginXFrac = p / 100f; xSet = true; } else cs.TxOriginYFrac = p / 100f; }
                        break;
                }
            }
        }

        /// <summary>Parse the <c>flex</c> shorthand: [grow] [shrink] [basis]. Handles <c>none</c>, a bare
        /// number (grow), and the common <c>flex:1</c> / <c>flex:0 0 200px</c> forms.</summary>
        private static bool IsPct(string v, out float pct)
        {
            v = v.Trim();
            if (v.EndsWith("%") && float.TryParse(v.Substring(0, v.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out pct)) return true;
            pct = 0f; return false;
        }

        private static void ParseFlex(ComputedStyle cs, string value)
        {
            var v = value.Trim().ToLowerInvariant();
            if (v == "none") { cs.FlexGrow = 0; cs.FlexShrink = 0; cs.FlexBasis = "auto"; return; }
            if (v == "auto") { cs.FlexGrow = 1; cs.FlexShrink = 1; cs.FlexBasis = "auto"; return; }
            // Split on top-level whitespace, keeping calc(…)/min(…)/… intact (they contain spaces).
            var toks = new System.Collections.Generic.List<string>();
            int depth = 0, start = 0;
            for (int k = 0; k < v.Length; k++)
            {
                char c = v[k];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (char.IsWhiteSpace(c) && depth == 0) { if (k > start) toks.Add(v.Substring(start, k - start)); start = k + 1; }
            }
            if (v.Length > start) toks.Add(v.Substring(start));

            bool grewSet = false, shrankSet = false, basisSet = false;
            foreach (var tok in toks)
            {
                // A UNITLESS number is grow then shrink; anything else (unit/%/calc/auto/content) is the flex-basis.
                if (!basisSet && !float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                { cs.FlexBasis = tok; basisSet = true; continue; }
                if (float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                {
                    if (!grewSet) { cs.FlexGrow = num; grewSet = true; }
                    else if (!shrankSet) { cs.FlexShrink = num; shrankSet = true; }
                    else if (!basisSet) { cs.FlexBasis = tok; basisSet = true; }   // 3rd unitless value = basis (e.g. `1 1 0`)
                }
            }
            // `flex: 1` => grow 1, shrink 1, basis 0.
            if (grewSet && !basisSet) cs.FlexBasis = "0";
            if (grewSet && !shrankSet) cs.FlexShrink = 1;
        }

        private static void ApplyBorders(ComputedStyle cs, Dictionary<string, string> raw)
        {
            // Shorthand `border: <width> <style> <color>` applies to all four sides.
            if (raw.TryGetValue("border", out var all))
            {
                var e = ParseBorder(all, cs.FontSizePt, cs.Color);
                cs.BorderTop = cs.BorderRight = cs.BorderBottom = cs.BorderLeft = e;
            }
            // All-sides longhand-group shorthands are applied BEFORE the per-side longhands so a specific
            // side (border-top-color etc.) overrides them (common "set all, override one side" pattern).
            if (raw.TryGetValue("border-color", out var bcAll)) { var c = Values.ParseColor(bcAll); if (c != null) ForEachSide(cs, e => { e.Color = c.Value; return e; }); }
            if (raw.TryGetValue("border-width", out var bwAll)) { var w = Values.LengthPt(bwAll, cs.FontSizePt); if (w != null) ForEachSide(cs, e => { e.Width = w.Value; return e; }); }
            if (raw.TryGetValue("border-style", out var bsAll)) { var st = bsAll.Trim().ToLowerInvariant().Split(' ')[0]; ForEachSide(cs, e => { if (st == "none" || st == "hidden") e.Width = 0; else { e.Style = st; if (e.Width == 0) e.Width = 1f * Lib.PxToPt; } return e; }); }
            foreach (var side in new[] { "top", "right", "bottom", "left" })
            {
                if (raw.TryGetValue("border-" + side, out var v))
                {
                    var e = ParseBorder(v, cs.FontSizePt, cs.Color);
                    SetSide(cs, side, e);
                }
                // longhand width/color overrides
                if (raw.TryGetValue($"border-{side}-width", out var wv))
                {
                    var e = GetSide(cs, side); e.Width = Values.LengthPt(wv, cs.FontSizePt) ?? e.Width; SetSide(cs, side, e);
                }
                if (raw.TryGetValue($"border-{side}-color", out var cv))
                {
                    var e = GetSide(cs, side); var c = Values.ParseColor(cv); if (c != null) e.Color = c.Value; SetSide(cs, side, e);
                }
                if (raw.TryGetValue($"border-{side}-style", out var stv))
                {
                    var e = GetSide(cs, side); var st = stv.Trim().ToLowerInvariant();
                    if (st == "none" || st == "hidden") e.Width = 0; else { e.Style = st; if (e.Width == 0) e.Width = 1f * Lib.PxToPt; }
                    SetSide(cs, side, e);
                }
            }
            // (all-sides border-width/color/style shorthands are applied before the per-side loop above)

            // outline: like a border but drawn outside the box; `outline:none` clears it.
            if (raw.TryGetValue("outline", out var ol)) cs.Outline = ParseBorder(ol, cs.FontSizePt, cs.Color);
            if (raw.TryGetValue("outline-style", out var ols) && (ols.Trim() == "none" || ols.Trim() == "hidden")) cs.Outline = new BorderEdge { Width = 0, Color = cs.Color };
            else if (raw.ContainsKey("outline-style") && cs.Outline.Width == 0) cs.Outline.Width = 1f * Lib.PxToPt; // a style implies medium width
            if (raw.TryGetValue("outline-width", out var olw)) cs.Outline.Width = Values.LengthPt(olw, cs.FontSizePt) ?? cs.Outline.Width;
            if (raw.TryGetValue("outline-color", out var olc)) { var c = Values.ParseColor(olc); if (c != null) cs.Outline.Color = c.Value; }
            if (raw.TryGetValue("outline-offset", out var olo)) cs.OutlineOffset = Values.LengthPt(olo, cs.FontSizePt) ?? 0f;
        }

        private static BorderEdge ParseBorder(string value, float em, Color currentColor)
        {
            var e = new BorderEdge { Width = 0f, Color = currentColor };
            if (value.IndexOf("none", StringComparison.OrdinalIgnoreCase) >= 0) return new BorderEdge { Width = 0, Color = currentColor };
            foreach (var tok in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok == "solid" || tok == "dashed" || tok == "dotted" || tok == "double" || tok == "groove" || tok == "ridge" || tok == "inset" || tok == "outset")
                {
                    if (e.Width == 0) e.Width = 1f * Lib.PxToPt; // default medium ~1px if width not given
                    e.Style = tok;
                    continue;
                }
                var c = Values.ParseColor(tok);
                if (c != null) { e.Color = c.Value; continue; }
                var w = Values.LengthPt(tok, em);
                if (w != null) e.Width = w.Value;
            }
            return e;
        }

        private static BorderEdge GetSide(ComputedStyle cs, string side) =>
            side == "top" ? cs.BorderTop : side == "right" ? cs.BorderRight : side == "bottom" ? cs.BorderBottom : cs.BorderLeft;
        private static void SetSide(ComputedStyle cs, string side, BorderEdge e)
        {
            switch (side) { case "top": cs.BorderTop = e; break; case "right": cs.BorderRight = e; break; case "bottom": cs.BorderBottom = e; break; default: cs.BorderLeft = e; break; }
        }
        private static void ForEachSide(ComputedStyle cs, Func<BorderEdge, BorderEdge> f)
        { cs.BorderTop = f(cs.BorderTop); cs.BorderRight = f(cs.BorderRight); cs.BorderBottom = f(cs.BorderBottom); cs.BorderLeft = f(cs.BorderLeft); }

        private static void Edge(Dictionary<string, string> raw, string prop, Action<float, float, float, float> set, float em)
        {
            if (!raw.TryGetValue(prop, out var v)) return;
            var toks = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            float P(int i) => Values.LengthPt(toks[i], em) ?? 0f;
            switch (toks.Length)
            {
                case 1: { var a = P(0); set(a, a, a, a); break; }
                case 2: { float a = P(0), b = P(1); set(a, b, a, b); break; }
                case 3: { float a = P(0), b = P(1), c = P(2); set(a, b, c, b); break; }
                default: set(P(0), P(1), P(2), P(3)); break;
            }
        }

        private static readonly HashSet<string> BlockTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "html","body","div","p","section","article","header","footer","main","nav","aside","figure",
            "figcaption","blockquote","pre","ul","ol","li","dl","dt","dd","table","tr","hr","form","fieldset",
            "h1","h2","h3","h4","h5","h6"
        };

        private static string DefaultDisplay(string? tag)
        {
            if (tag == null) return "inline";
            if (tag == "none") return "none";
            if (tag == "table") return "table";
            return BlockTags.Contains(tag) ? "block" : "inline";
        }

        private static IEnumerable<Declaration> UaDefaults(string? tag)
        {
            var list = new List<Declaration>();
            void D(string p, string v) => list.Add(new Declaration { Property = p, Value = v });
            switch (tag)
            {
                case "h1": D("font-size", "2em"); D("font-weight", "bold"); D("margin", "0.67em 0"); break;
                case "h2": D("font-size", "1.5em"); D("font-weight", "bold"); D("margin", "0.83em 0"); break;
                case "h3": D("font-size", "1.17em"); D("font-weight", "bold"); D("margin", "1em 0"); break;
                case "h4": D("font-size", "1em"); D("font-weight", "bold"); D("margin", "1.33em 0"); break;
                case "h5": D("font-size", "0.83em"); D("font-weight", "bold"); D("margin", "1.67em 0"); break;
                case "h6": D("font-size", "0.67em"); D("font-weight", "bold"); D("margin", "2.33em 0"); break;
                case "p": D("margin", "1em 0"); break;
                case "b": case "strong": D("font-weight", "bold"); break;
                case "i": case "em": D("font-style", "italic"); break;
                case "a": D("color", "#0000ee"); D("text-decoration", "underline"); break;
                case "sup": D("vertical-align", "super"); D("font-size", "0.83em"); break;
                case "sub": D("vertical-align", "sub"); D("font-size", "0.83em"); break;
                case "u": case "ins": D("text-decoration", "underline"); break;
                case "s": case "del": case "strike": D("text-decoration", "line-through"); break;
                case "ul": case "ol": D("margin", "1em 0"); D("padding-left", "40px"); break;
                case "blockquote": D("margin", "1em 40px"); break;
                case "pre": D("font-family", "monospace"); D("white-space", "pre"); D("display", "block"); D("margin", "1em 0"); break;
                case "code": case "kbd": case "samp": D("font-family", "monospace"); break;
                case "small": D("font-size", "0.83em"); break;
                case "big": D("font-size", "1.17em"); break;
                case "mark": D("background", "#ffff00"); D("color", "#000000"); break;
                case "center": D("display", "block"); D("text-align", "center"); break;
                case "address": case "cite": case "dfn": case "var": D("font-style", "italic"); break;
                case "tt": D("font-family", "monospace"); break;
                case "abbr": break;   // (dotted underline only with [title]; skipped)
                case "hr": D("margin", "8px 0"); break;
                case "li": D("display", "block"); break;
                case "th": D("font-weight", "bold"); D("text-align", "center"); D("padding", "2px"); break;
                case "td": D("padding", "2px"); break;
                // Basic form-control appearance (bordered boxes, static). Checkbox/radio handled below.
                case "input": case "textarea": case "select":
                    D("display", "inline-block"); D("border", "1px solid #767676"); D("padding", "1px 2px");
                    D("background", "#ffffff"); D("font-size", "13.3px"); break;
                case "button":
                    D("display", "inline-block"); D("border", "1px solid #767676"); D("padding", "1px 6px");
                    D("background", "#efefef"); D("text-align", "center"); break;
                case "fieldset": D("border", "2px groove #cfcfcf"); D("margin", "0 2px"); D("padding", "0.35em 0.75em 0.625em"); break;
                case "legend": D("padding", "0 2px"); break;
                case "label": D("display", "inline"); break;
                case "details": case "summary": D("display", "block"); break;
                case "dialog": D("display", "none"); break;  // shown only when [open]; handled in BuildNode
                case "progress": case "meter":
                    D("display", "inline-block"); D("width", "160px"); D("height", "14px");
                    D("background", "#e6e6e6"); D("border", "1px solid #b3b3b3"); break;
            }
            return list;
        }
    }
}
